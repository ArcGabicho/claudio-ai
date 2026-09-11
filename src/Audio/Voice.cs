using System.Diagnostics;
using System.Speech.Synthesis;
using NAudio.Wave;

namespace ClaudioAi.Audio;

/// <summary>
/// Texto a voz para Windows. Por defecto usa SAPI (<c>System.Speech</c>), que
/// siempre está disponible; si hay un <c>piper.exe</c> en el PATH y un modelo
/// <c>.onnx</c> configurado, se puede forzar Piper para una voz de más calidad.
/// Si nada funciona, Claudio sigue vivo y solo escribe la respuesta por consola.
/// </summary>
public sealed class Voice : IDisposable
{
    string _engine;
    readonly string? _piperModel;
    readonly SpeechSynthesizer? _sapi;

    public Voice(string configured, string? piperModel)
    {
        _piperModel = piperModel;
        _engine = configured is "auto" or "" ? Detect() : configured;

        // Aceptamos el nombre antiguo de Linux por compatibilidad con appsettings viejos.
        if (_engine is "espeak-ng" or "spd-say")
            _engine = "sapi";

        if (_engine == "sapi")
        {
            try
            {
                _sapi = new SpeechSynthesizer { Rate = 1, Volume = 100 };
                var spanish = _sapi.GetInstalledVoices()
                    .Where(v => v.Enabled &&
                                v.VoiceInfo.Culture.TwoLetterISOLanguageName.Equals(
                                    "es", StringComparison.OrdinalIgnoreCase))
                    .Select(v => v.VoiceInfo.Name)
                    .FirstOrDefault();
                if (spanish is not null)
                    _sapi.SelectVoice(spanish);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[tts] SAPI no disponible: {ex.Message}");
                _sapi?.Dispose();
                _sapi = null;
                _engine = "none";
            }
        }
    }

    public string Engine => _engine;
    public bool Available => _engine is "sapi" or "piper";

    static string Detect() => Which("piper") ? "piper" : "sapi";

    static bool Which(string bin)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("where", bin)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            p.WaitForExit(2000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task SpeakAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        Console.WriteLine($"\n[36m🔊 Claudio:[0m {text}\n");

        try
        {
            switch (_engine)
            {
                case "sapi":
                    await SpeakSapiAsync(text, ct);
                    break;
                case "piper":
                    await SpeakPiperAsync(text, ct);
                    break;
                // "none": ya se ha impreso arriba, no hay nada más que hacer.
            }
        }
        catch (OperationCanceledException) { /* corte pedido por el usuario */ }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tts] {ex.Message}");
        }
    }

    async Task SpeakSapiAsync(string text, CancellationToken ct)
    {
        if (_sapi is null) return;

        var done = new TaskCompletionSource();
        void OnCompleted(object? s, SpeakCompletedEventArgs e) => done.TrySetResult();

        _sapi.SpeakCompleted += OnCompleted;
        await using var reg = ct.Register(() =>
        {
            try { _sapi.SpeakAsyncCancelAll(); } catch { /* da igual */ }
        });

        try
        {
            _sapi.SpeakAsync(text);
            await done.Task;
        }
        finally
        {
            _sapi.SpeakCompleted -= OnCompleted;
        }
    }

    async Task SpeakPiperAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_piperModel) || !File.Exists(_piperModel))
        {
            Console.Error.WriteLine("[tts] piper necesita 'piperModel' en appsettings.json (ruta a un .onnx).");
            return;
        }

        var wav = Path.Combine(Path.GetTempPath(), $"claudio-tts-{Guid.NewGuid():N}.wav");

        var piper = new ProcessStartInfo("piper")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        piper.ArgumentList.Add("--model"); piper.ArgumentList.Add(_piperModel);
        piper.ArgumentList.Add("--output_file"); piper.ArgumentList.Add(wav);

        using (var p = Process.Start(piper)!)
        {
            await p.StandardInput.WriteAsync(text.AsMemory(), ct);
            p.StandardInput.Close();
            await p.WaitForExitAsync(ct);
        }

        try
        {
            await PlayWavAsync(wav, ct);
        }
        finally
        {
            try { File.Delete(wav); } catch { /* da igual */ }
        }
    }

    static async Task PlayWavAsync(string wav, CancellationToken ct)
    {
        using var reader = new AudioFileReader(wav);
        using var output = new WaveOutEvent();
        output.Init(reader);
        output.Play();

        while (output.PlaybackState == PlaybackState.Playing)
        {
            if (ct.IsCancellationRequested)
            {
                output.Stop();
                break;
            }
            await Task.Delay(100, CancellationToken.None);
        }
    }

    public void Dispose() => _sapi?.Dispose();
}
