using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using ClaudioAi;
using ClaudioAi.Actions;
using ClaudioAi.Audio;
using ClaudioAi.Brain;
using ClaudioAi.Diagnostics;
using ClaudioAi.Speech;
using ClaudioAi.Tray;

static class Program
{
    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int dwProcessId);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetStdHandle(int nStdHandle);
    const int AttachParentProcess = -1;
    const int StdOutputHandle = -11;

    /// <summary>
    /// En modo diagnóstico necesitamos una consola. Si la salida ya está
    /// redirigida (p. ej. <c>| tail</c> o <c>dotnet run</c>), la respetamos;
    /// si no, nos enganchamos a la consola del proceso padre.
    /// </summary>
    static void EnsureConsole()
    {
        var stdout = GetStdHandle(StdOutputHandle);
        if (stdout == IntPtr.Zero || stdout == new IntPtr(-1))
            AttachConsole(AttachParentProcess);
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0)
            return RunDiagnostics(args).GetAwaiter().GetResult();

        // Instancia única: relanzar mientras corre no hace nada.
        using var single = new Mutex(true, @"Local\ClaudioAiVoiceAssistant", out var isNew);
        if (!isNew) return 0;

        var cfg = ClaudioConfig.Load();

        // Primera ejecución: registrar el arranque con Windows si así se pidió.
        if (cfg.StartWithWindows && !Autostart.HasBeenConfigured())
            Autostart.Enable();

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            Application.Run(new ClaudioTrayContext(cfg));
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Claudio terminó por un error no controlado", ex);
            return 1;
        }
    }

    // ─── Diagnósticos (se ejecutan con la consola del proceso padre) ─────────
    static async Task<int> RunDiagnostics(string[] args)
    {
        EnsureConsole();
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* sin consola */ }

        var cfg = ClaudioConfig.Load();

        switch (args[0])
        {
            case "--transcribe" when args.Length > 1:
            {
                using var stt = new SpeechToText(cfg);
                await stt.WarmUpAsync();
                Console.WriteLine(await stt.TranscribeAsync(args[1]));
                return 0;
            }

            case "--record":
            {
                var secs = args.Length > 1 && int.TryParse(args[1], out var s) ? s : cfg.RecordSeconds;
                Console.WriteLine($"Grabando {secs}s…");
                Console.WriteLine("WAV: " + await new AudioRecorder().RecordAsync(secs));
                return 0;
            }

            case "--listen":
            {
                using var stt = new SpeechToText(cfg);
                await stt.WarmUpAsync();
                Console.WriteLine("Habla ahora (fin automático por silencio)…");
                var wav = await new CommandCapture(cfg).CaptureAsync();
                if (wav is null) { Console.WriteLine("(no se oyó nada)"); return 0; }
                try { Console.WriteLine(await stt.TranscribeAsync(wav)); }
                finally { try { File.Delete(wav); } catch { } }
                return 0;
            }

            case "--hear":
            {
                var secs = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 20;
                using var stt = new SpeechToText(cfg);
                await stt.WarmUpAsync();
                using var listener = new ContinuousListener(cfg, stt);
                if (!listener.Available) { Console.WriteLine("(sin micrófono)"); return 1; }
                listener.UtteranceReady += t => Console.WriteLine($"» {t}");
                listener.Start();
                Console.WriteLine($"Escuchando {secs}s. Di «{cfg.WakeWord}, …» o cualquier frase…");
                await Task.Delay(TimeSpan.FromSeconds(secs));
                listener.Stop();
                return 0;
            }

            case "--say" when args.Length > 1:
            {
                using var voice = new Voice(cfg.TtsEngine, cfg.PiperModel);
                Console.WriteLine($"motor de voz: {(voice.Available ? voice.Engine : "ninguno")}");
                await voice.SpeakAsync(args[1]);
                return 0;
            }

            case "--do" when args.Length > 1:
            {
                var action = JsonSerializer.Deserialize<AssistantAction>(args[1])
                             ?? throw new ArgumentException("JSON de acción no válido.");
                Console.WriteLine("resultado: " + await new ActionRouter().ExecuteAsync(action));
                return 0;
            }

            case "--recognizers":
            {
                var installed = System.Speech.Recognition.SpeechRecognitionEngine.InstalledRecognizers();
                if (installed.Count == 0)
                {
                    Console.WriteLine("No hay reconocedores de voz instalados.");
                    return 1;
                }
                foreach (var r in installed)
                    Console.WriteLine($"{r.Name}  [{r.Culture.Name}]  {r.Description}");
                return 0;
            }

            default:
                Console.WriteLine(
                    "Uso: claudio [--transcribe <wav> | --record [seg] | --listen | " +
                    "--hear [seg] | --say <texto> | --do <json> | --recognizers]");
                return 1;
        }
    }
}
