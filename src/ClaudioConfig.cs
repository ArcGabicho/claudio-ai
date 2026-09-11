using System.Globalization;
using System.Text.Json;

namespace ClaudioAi;

/// <summary>
/// Configuración de Claudio. Se lee de <c>appsettings.json</c> (junto al ejecutable)
/// y cada valor puede sobreescribirse con una variable de entorno <c>CLAUDIO_*</c>.
/// </summary>
public sealed record ClaudioConfig
{
    /// <summary>Idioma para la transcripción y las respuestas ("es", "en", ...).</summary>
    public string Language { get; init; } = "es";

    /// <summary>Modelo Whisper: tiny | base | small | medium | large.</summary>
    public string WhisperModel { get; init; } = "base";

    /// <summary>Segundos de la grabación del diagnóstico <c>--record</c> (el modo normal usa VAD).</summary>
    public int RecordSeconds { get; init; } = 6;

    /// <summary>Ejecutable del CLI de Claude Code que hace de "cerebro".</summary>
    public string ClaudeCommand { get; init; } = "claude";

    /// <summary>Motor de voz: auto | sapi | piper | none.</summary>
    public string TtsEngine { get; init; } = "auto";

    /// <summary>Ruta a un modelo .onnx de Piper (solo si TtsEngine = piper).</summary>
    public string? PiperModel { get; init; }

    // ─── Palabra de activación ────────────────────────────────────────────────

    /// <summary>Palabra que despierta a Claudio ("Claudio, {orden}").</summary>
    public string WakeWord { get; init; } = "Claudio";

    /// <summary>Variantes que Whisper suele devolver por "Claudio" y que también activan.</summary>
    public string[] WakeWordVariants { get; init; } = ["Claudia", "Cláudio", "Cloudio", "Clodio", "Clau dio"];

    /// <summary>Suena un aviso del sistema al detectar la palabra de activación.</summary>
    public bool ChimeOnWake { get; init; } = true;

    // ─── Captura de la orden (VAD) ───────────────────────────────────────────

    /// <summary>Silencio continuo (ms) que da por terminada la orden.</summary>
    public int SilenceMs { get; init; } = 800;

    /// <summary>Umbral de energía RMS (0..1) por debajo del cual se considera silencio.</summary>
    public double SilenceThreshold { get; init; } = 0.02;

    /// <summary>Tope de duración de una orden, en segundos.</summary>
    public int MaxCommandSeconds { get; init; } = 15;

    /// <summary>Si tras la activación nadie habla en estos segundos, se cancela el turno.</summary>
    public int NoSpeechTimeoutSeconds { get; init; } = 4;

    // ─── Bandeja / arranque ─────────────────────────────────────────────────

    /// <summary>En la primera ejecución, registra a Claudio para arrancar con Windows.</summary>
    public bool StartWithWindows { get; init; } = true;

    /// <summary>~/.local/share/claudio-ai — datos persistentes (modelos descargados, log, etc.).</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "claudio-ai");

    public string ModelsDir => Path.Combine(DataDir, "models");

    public static ClaudioConfig Load()
    {
        var cfg = new ClaudioConfig();

        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<ClaudioConfig>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (loaded is not null) cfg = loaded;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[config] no pude leer appsettings.json: {ex.Message}");
            }
        }

        return cfg with
        {
            Language               = Env("CLAUDIO_LANGUAGE")        ?? cfg.Language,
            WhisperModel           = Env("CLAUDIO_WHISPER_MODEL")    ?? cfg.WhisperModel,
            ClaudeCommand          = Env("CLAUDIO_CLAUDE_CMD")       ?? cfg.ClaudeCommand,
            TtsEngine              = Env("CLAUDIO_TTS")              ?? cfg.TtsEngine,
            PiperModel             = Env("CLAUDIO_PIPER_MODEL")      ?? cfg.PiperModel,
            WakeWord               = Env("CLAUDIO_WAKE_WORD")        ?? cfg.WakeWord,
            ChimeOnWake            = EnvBool("CLAUDIO_CHIME")        ?? cfg.ChimeOnWake,
            SilenceMs              = EnvInt("CLAUDIO_SILENCE_MS")    ?? cfg.SilenceMs,
            SilenceThreshold       = EnvDouble("CLAUDIO_SILENCE_THRESHOLD") ?? cfg.SilenceThreshold,
            MaxCommandSeconds      = EnvInt("CLAUDIO_MAX_COMMAND_SECONDS") ?? cfg.MaxCommandSeconds,
            NoSpeechTimeoutSeconds = EnvInt("CLAUDIO_NO_SPEECH_SECONDS") ?? cfg.NoSpeechTimeoutSeconds,
            StartWithWindows       = EnvBool("CLAUDIO_START_WITH_WINDOWS") ?? cfg.StartWithWindows,
        };
    }

    static string? Env(string key)
        => Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : null;

    static bool? EnvBool(string key)
        => Env(key) is { } v && bool.TryParse(v, out var b) ? b : null;

    static int? EnvInt(string key)
        => Env(key) is { } v && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    static double? EnvDouble(string key)
        => Env(key) is { } v && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}
