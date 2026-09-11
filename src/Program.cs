using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using ClaudioAi;
using ClaudioAi.Actions;
using ClaudioAi.Audio;
using ClaudioAi.Brain;
using ClaudioAi.Diagnostics;
using ClaudioAi.Memory;
using ClaudioAi.Projects;
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
                using var voice = new Voice(cfg);
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

            case "--projects":
            {
                var resolver = new ProjectResolver(cfg);
                var all = resolver.ListAll();
                if (all.Count == 0) { Console.WriteLine("No encontré ningún proyecto."); return 0; }
                foreach (var p in all) Console.WriteLine($"{p.Name}  [{p.Kind}]  {p.Path}");
                return 0;
            }

            case "--match" when args.Length > 1:
            {
                var matches = new ProjectResolver(cfg).Match(args[1]);
                if (matches.Count == 0) { Console.WriteLine("(sin coincidencias)"); return 0; }
                foreach (var m in matches) Console.WriteLine($"{m.Name}  [{m.Kind}]  {m.Path}");
                return 0;
            }

            case "--new-project" when args.Length > 1:
            {
                var result = new GitOps(cfg).CreateProject(args[1]);
                Console.WriteLine($"{(result.Ok ? "ok" : "error")}: {result.Message}" + (result.Path is null ? "" : $" ({result.Path})"));
                return result.Ok ? 0 : 1;
            }

            case "--clone-repo" when args.Length > 1:
            {
                var result = new GitOps(cfg).Clone(args[1]);
                Console.WriteLine($"{(result.Ok ? "ok" : "error")}: {result.Message}" + (result.Path is null ? "" : $" ({result.Path})"));
                return result.Ok ? 0 : 1;
            }

            case "--decide" when args.Length > 1:
            {
                var brain = new ClaudeBrain(cfg, new MemoryStore(cfg));
                var action = await brain.DecideAsync(args[1]);
                Console.WriteLine($"{action.Action} | target={action.Target} | say={action.Say}");
                return 0;
            }

            case "--web" when args.Length > 1:
            {
                var brain = new ClaudeBrain(cfg, new MemoryStore(cfg));
                Console.WriteLine(await brain.AnswerFromWebAsync(args[1]));
                return 0;
            }

            case "--memory":
            {
                var all = new MemoryStore(cfg).LoadAll();
                if (all.Count == 0) { Console.WriteLine("(vacía)"); return 0; }
                foreach (var line in all) Console.WriteLine($"- {line}");
                return 0;
            }

            case "--remember" when args.Length > 1:
            {
                new MemoryStore(cfg).Add(args[1]);
                Console.WriteLine("guardado.");
                return 0;
            }

            case "--forget" when args.Length > 1:
            {
                var removed = new MemoryStore(cfg).Forget(args[1]);
                Console.WriteLine(removed is null ? "(no encontré nada parecido)" : $"borrado: {removed}");
                return removed is null ? 1 : 0;
            }

            case "--memory-clear":
            {
                new MemoryStore(cfg).Clear();
                Console.WriteLine("memoria borrada.");
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
                    "Uso: claudio [--transcribe <wav> | --record [seg] | --listen | --hear [seg] | " +
                    "--say <texto> | --do <json> | --projects | --match <texto> | --new-project <nombre> | " +
                    "--clone-repo <owner/repo> | --decide <orden> | --web <pregunta> | --memory | " +
                    "--remember <texto> | --forget <texto> | --memory-clear | --recognizers]");
                return 1;
        }
    }
}
