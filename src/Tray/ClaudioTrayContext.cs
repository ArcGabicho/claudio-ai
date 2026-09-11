using System.Globalization;
using System.Text;
using System.Windows.Forms;
using ClaudioAi.Actions;
using ClaudioAi.Audio;
using ClaudioAi.Brain;
using ClaudioAi.Diagnostics;
using ClaudioAi.Speech;

namespace ClaudioAi.Tray;

/// <summary>
/// Claudio residente: vive en la bandeja del sistema, escucha el micrófono en
/// continuo y, cuando oye "Claudio, {orden}", ejecuta la orden y responde por
/// voz. Sin ventana de consola.
/// </summary>
public sealed class ClaudioTrayContext : ApplicationContext
{
    static readonly char[] EdgeTrim = " ,.:;¡!¿?-–—\"'".ToCharArray();

    readonly ClaudioConfig _cfg;
    readonly string[] _wakeTokens;
    readonly Control _marshal = new();          // para volver al hilo de la UI
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _pauseItem;
    readonly ToolStripMenuItem _autostartItem;
    readonly Dictionary<TrayState, Icon> _icons;

    readonly SpeechToText _stt;
    readonly ContinuousListener _listener;
    readonly ClaudeBrain _brain;
    readonly ActionRouter _router;
    readonly Voice _voice;

    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _turnLock = new(1, 1);
    volatile bool _paused;
    volatile bool _disposed;
    volatile bool _awaitingCommand;
    DateTime _awaitDeadline;

    public ClaudioTrayContext(ClaudioConfig cfg)
    {
        _cfg = cfg;
        _ = _marshal.Handle;   // fuerza la creación del handle en este hilo (STA)

        _wakeTokens = new[] { cfg.WakeWord }
            .Concat(cfg.WakeWordVariants ?? [])
            .Select(Normalize)
            .Where(w => w.Length >= 3)
            .Distinct()
            .ToArray();

        _stt = new SpeechToText(cfg);
        _listener = new ContinuousListener(cfg, _stt);
        _brain = new ClaudeBrain(cfg);
        _router = new ActionRouter();
        _voice = new Voice(cfg.TtsEngine, cfg.PiperModel);

        _icons = Enum.GetValues<TrayState>().ToDictionary(s => s, TrayIconFactory.Create);

        _pauseItem = new ToolStripMenuItem("Pausar escucha", null, (_, _) => TogglePause());
        _autostartItem = new ToolStripMenuItem("Iniciar con Windows", null, (_, _) => ToggleAutostart())
        {
            Checked = Autostart.IsEnabled(),
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"Claudio — di «{cfg.WakeWord}, …»") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripMenuItem("Ver registro…", null, (_, _) => OpenLog()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Salir", null, (_, _) => ExitApp()));

        _tray = new NotifyIcon
        {
            Icon = _icons[TrayState.Busy],
            Text = "Claudio — preparando…",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => TogglePause();

        _ = InitAsync();
    }

    async Task InitAsync()
    {
        try
        {
            await _stt.WarmUpAsync(_cts.Token);

            if (!_listener.Available)
            {
                SetState(TrayState.Paused, "Claudio — sin micrófono");
                Notify("Claudio no puede escuchar",
                    "No se detectó ningún micrófono. Conéctalo y da permiso en " +
                    "Configuración → Privacidad → Micrófono, y reinicia Claudio.",
                    ToolTipIcon.Error);
                return;
            }

            _listener.UtteranceReady += OnUtterance;
            _listener.Start();
            SetState(TrayState.Idle, $"Claudio — escuchando «{_cfg.WakeWord}»");
            Log.Info("Claudio listo y escuchando.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Fallo al iniciar Claudio", ex);
            SetState(TrayState.Paused, "Claudio — error al iniciar");
            Notify("Claudio no arrancó", ex.Message, ToolTipIcon.Error);
        }
    }

    void OnUtterance(string text)
    {
        if (_paused || _disposed) return;
        _ = Task.Run(() => RouteUtteranceAsync(text));
    }

    async Task RouteUtteranceAsync(string text)
    {
        try
        {
            // ¿Es la orden que esperábamos tras un "Claudio" dicho suelto?
            if (_awaitingCommand && DateTime.UtcNow <= _awaitDeadline)
            {
                _awaitingCommand = false;
                await RunTurnAsync(text);
                return;
            }
            _awaitingCommand = false;

            if (!TryStripWake(text, out var command))
                return;

            if (!string.IsNullOrWhiteSpace(command))
            {
                await RunTurnAsync(command);
            }
            else
            {
                // Solo dijo "Claudio": esperamos la orden en la siguiente frase.
                SystemChime.Wake(_cfg);
                _awaitingCommand = true;
                _awaitDeadline = DateTime.UtcNow.AddSeconds(AwaitSeconds);
                SetState(TrayState.Listening, "Claudio — dime…");
                Log.Info("Activado; esperando la orden.");
                _ = RevertAwaitAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Error al encaminar una frase", ex);
        }
    }

    int AwaitSeconds => Math.Max(6, _cfg.MaxCommandSeconds);

    async Task RevertAwaitAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(AwaitSeconds + 1));
        if (_awaitingCommand && DateTime.UtcNow > _awaitDeadline)
        {
            _awaitingCommand = false;
            if (!_paused && !_disposed)
                SetState(TrayState.Idle, $"Claudio — escuchando «{_cfg.WakeWord}»");
        }
    }

    async Task RunTurnAsync(string command)
    {
        if (!await _turnLock.WaitAsync(0)) return;   // ya hay un turno en curso

        try
        {
            _listener.Muted = true;                  // no escucharnos a nosotros mismos
            SetState(TrayState.Busy, "Claudio — pensando…");
            Log.Info($"Orden: {command}");

            var action = await _brain.DecideAsync(command, _cts.Token);
            Log.Info($"Acción: {action.Action}" + (action.Target is null ? "" : $" → {action.Target}"));

            var result = await _router.ExecuteAsync(action, _cts.Token);
            var toSay = action.Action == "shell" && !string.IsNullOrWhiteSpace(result)
                ? $"{action.Say} {result}".Trim()
                : action.Say;

            SetState(TrayState.Busy, "Claudio — respondiendo…");
            await _voice.SpeakAsync(toSay, _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Error durante el turno", ex);
            try { await _voice.SpeakAsync("Ha habido un error.", CancellationToken.None); } catch { }
        }
        finally
        {
            await Task.Delay(300);                   // deja pasar el eco de la propia voz
            _listener.Muted = false;
            _turnLock.Release();
            if (!_paused && !_disposed)
                SetState(TrayState.Idle, $"Claudio — escuchando «{_cfg.WakeWord}»");
        }
    }

    bool TryStripWake(string text, out string command)
    {
        command = "";
        var norm = Normalize(text);

        var token = _wakeTokens.FirstOrDefault(t =>
        {
            var i = norm.IndexOf(t, StringComparison.Ordinal);
            return i >= 0 && i <= 8;                 // al principio de la frase
        });
        if (token is null) return false;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cut = 0;
        for (var i = 0; i < words.Length && i < 3; i++)
        {
            if (Normalize(words[i]).Contains(token, StringComparison.Ordinal))
            {
                cut = i + 1;
                break;
            }
        }
        command = string.Join(' ', words.Skip(cut)).Trim().Trim(EdgeTrim).Trim();
        return true;
    }

    /// <summary>Minúsculas, sin acentos y sin signos: para comparar la palabra de activación.</summary>
    static string Normalize(string s)
    {
        var decomposed = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    void TogglePause()
    {
        if (_disposed || !_listener.Available) return;

        _paused = !_paused;
        _awaitingCommand = false;
        if (_paused)
        {
            _listener.Stop();
            _pauseItem.Text = "Reanudar escucha";
            SetState(TrayState.Paused, "Claudio — en pausa");
            Log.Info("Claudio en pausa.");
        }
        else
        {
            _pauseItem.Text = "Pausar escucha";
            _listener.Start();
            SetState(TrayState.Idle, $"Claudio — escuchando «{_cfg.WakeWord}»");
            Log.Info("Claudio reanudado.");
        }
    }

    void ToggleAutostart()
    {
        if (Autostart.IsEnabled()) Autostart.Disable();
        else Autostart.Enable();
        _autostartItem.Checked = Autostart.IsEnabled();
    }

    void OpenLog()
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("No pude abrir el registro", ex);
        }
    }

    void ExitApp()
    {
        Log.Info("Claudio se cierra desde el menú.");
        Dispose(true);
        ExitThread();
    }

    void SetState(TrayState state, string? tooltip = null) => RunOnUi(() =>
    {
        _tray.Icon = _icons[state];
        if (tooltip is not null)
            _tray.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63];   // NotifyIcon.Text: máx 63
    });

    void Notify(string title, string body, ToolTipIcon icon) => RunOnUi(() =>
    {
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = body;
        _tray.BalloonTipIcon = icon;
        _tray.ShowBalloonTip(5000);
    });

    void RunOnUi(Action action)
    {
        if (_disposed) return;
        try
        {
            if (_marshal.InvokeRequired) _marshal.BeginInvoke(action);
            else action();
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            base.Dispose(disposing);
            return;
        }
        _disposed = true;

        if (disposing)
        {
            try { _cts.Cancel(); } catch { }
            try { _listener.Dispose(); } catch { }
            try { _voice.Dispose(); } catch { }
            try { _stt.Dispose(); } catch { }

            _tray.Visible = false;
            _tray.Dispose();
            foreach (var icon in _icons.Values) icon.Dispose();
            _marshal.Dispose();
            _turnLock.Dispose();
            _cts.Dispose();
        }

        base.Dispose(disposing);
    }
}
