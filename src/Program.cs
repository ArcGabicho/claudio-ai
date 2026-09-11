using System.Text;
using ClaudioAi;
using ClaudioAi.Actions;
using ClaudioAi.Audio;
using ClaudioAi.Brain;
using ClaudioAi.Speech;

Console.OutputEncoding = Encoding.UTF8;

var cfg = ClaudioConfig.Load();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var recorder = new AudioRecorder();
using var stt = new SpeechToText(cfg);

// Utilidad de diagnóstico:  claudio --transcribe archivo.wav
if (args is ["--transcribe", var wavArg, ..])
{
    await stt.WarmUpAsync(cts.Token);
    Console.WriteLine(await stt.TranscribeAsync(wavArg, cts.Token));
    return 0;
}

var brain = new ClaudeBrain(cfg);
var router = new ActionRouter();
using var voice = new Voice(cfg.TtsEngine, cfg.PiperModel);

// Diagnóstico de voz:  claudio --say "texto a leer en voz alta"
if (args is ["--say", var phrase, ..])
{
    Console.WriteLine($"motor de voz: {(voice.Available ? voice.Engine : "ninguno")}");
    await voice.SpeakAsync(phrase, cts.Token);
    return 0;
}

// Diagnóstico de micrófono:  claudio --record [segundos]
if (args is ["--record", ..])
{
    var secs = args.Length > 1 && int.TryParse(args[1], out var s) ? s : cfg.RecordSeconds;
    Console.WriteLine($"Grabando {secs}s…");
    var path = await recorder.RecordAsync(secs, cts.Token);
    Console.WriteLine($"WAV: {path}");
    return 0;
}

// Diagnóstico de acciones:  claudio --do '{"action":"shell","target":"Get-Date","say":""}'
if (args is ["--do", var actionJson, ..])
{
    var act = System.Text.Json.JsonSerializer.Deserialize<AssistantAction>(actionJson)
              ?? throw new ArgumentException("JSON de acción no válido.");
    var res = await new ActionRouter().ExecuteAsync(act, cts.Token);
    Console.WriteLine($"resultado: {res}");
    return 0;
}

PrintBanner(cfg, voice);

try
{
    Console.WriteLine("Preparando el modelo de voz…");
    await stt.WarmUpAsync(cts.Token);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[fatal] no pude preparar Whisper: {ex.Message}");
    return 1;
}

Console.WriteLine("Listo.\n");

while (!cts.IsCancellationRequested)
{
    Console.Write("▶  ENTER para hablar · escribe 'q' + ENTER para salir: ");
    var line = Console.ReadLine();
    if (line is null || line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase))
        break;

    try
    {
        Console.WriteLine($"🎙️  Grabando {cfg.RecordSeconds}s… habla ahora.");
        var wav = await recorder.RecordAsync(cfg.RecordSeconds, cts.Token);

        Console.WriteLine("📝  Transcribiendo…");
        var text = await stt.TranscribeAsync(wav, cts.Token);
        TryDelete(wav);

        if (string.IsNullOrWhiteSpace(text))
        {
            await voice.SpeakAsync("No te he oído.", cts.Token);
            continue;
        }

        Console.WriteLine($"🗣️  Tú: {text}");
        Console.WriteLine("🤔  Pensando…");

        var action = await brain.DecideAsync(text, cts.Token);
        Console.WriteLine($"⚙️  {action.Action}{(action.Target is null ? "" : $" → {action.Target}")}");

        var result = await router.ExecuteAsync(action, cts.Token);

        // En open_app / web_search basta con la confirmación hablada; en shell añadimos el resultado.
        var toSay = action.Action == "shell" && !string.IsNullOrWhiteSpace(result)
            ? $"{action.Say} {result}".Trim()
            : action.Say;

        await voice.SpeakAsync(toSay, cts.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[error] {ex.Message}");
    }
}

Console.WriteLine("\n👋 Hasta luego.");
return 0;

static void PrintBanner(ClaudioConfig cfg, Voice voice)
{
    Console.WriteLine("""
        ┌───────────────────────────────┐
        │   CLAUDIO · asistente de voz   │
        └───────────────────────────────┘
        """);
    Console.WriteLine($"  modelo voz : Whisper '{cfg.WhisperModel}'  ({cfg.Language})");
    Console.WriteLine($"  cerebro    : {cfg.ClaudeCommand} (Claude Code CLI)");
    Console.WriteLine($"  voz salida : {(voice.Available ? voice.Engine : "ninguna — solo texto (no se encontró una voz SAPI)")}");
    Console.WriteLine();
}

static void TryDelete(string path)
{
    try { File.Delete(path); } catch { /* no pasa nada */ }
}
