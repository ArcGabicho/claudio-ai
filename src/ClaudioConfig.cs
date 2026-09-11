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

    /// <summary>
    /// Modelo Whisper: tiny | base | small | medium | large-v3 | large-v3-turbo.
    /// Con GPU NVIDIA (CUDA 12) disponible, large-v3-turbo va rápido y es mucho más
    /// preciso que base; sin GPU, conviene bajar a small o base para no ir lento.
    /// </summary>
    public string WhisperModel { get; init; } = "large-v3-turbo";

    /// <summary>Segundos de la grabación del diagnóstico <c>--record</c> (el modo normal usa VAD).</summary>
    public int RecordSeconds { get; init; } = 6;

    /// <summary>Ejecutable del CLI de Claude Code que hace de "cerebro".</summary>
    public string ClaudeCommand { get; init; } = "claude";

    /// <summary>Motor de voz: auto | sapi | piper | none.</summary>
    public string TtsEngine { get; init; } = "auto";

    /// <summary>Ruta a un modelo .onnx de Piper (solo si TtsEngine = piper).</summary>
    public string? PiperModel { get; init; }

    /// <summary>Ruta o nombre del ejecutable de Piper (por defecto lo busca como "piper" en el PATH).</summary>
    public string PiperPath { get; init; } = "piper";

    /// <summary>
    /// Factor de velocidad/tono para la voz de Piper (1.0 = sin cambios). Por debajo
    /// de 1 la voz suena más grave y pausada (p. ej. 0.90); por encima, más aguda y
    /// rápida. No afecta a SAPI.
    /// </summary>
    public double PiperSpeedFactor { get; init; } = 1.0;

    /// <summary>
    /// Clave de API de ElevenLabs (solo si TtsEngine = elevenlabs). NUNCA la pongas en
    /// appsettings.json: se lee exclusivamente de la variable de entorno
    /// CLAUDIO_ELEVENLABS_API_KEY para no dejarla en un fichero que se pueda subir a git.
    /// </summary>
    public string? ElevenLabsApiKey { get; init; }

    /// <summary>Id de la voz de ElevenLabs a usar (se elige en tu cuenta de ElevenLabs).</summary>
    public string? ElevenLabsVoiceId { get; init; }

    /// <summary>Modelo de ElevenLabs.</summary>
    public string ElevenLabsModel { get; init; } = "eleven_multilingual_v2";

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

    // ─── Proyectos ("Claudio, abre el proyecto X") ───────────────────────────

    /// <summary>Carpetas de Windows bajo las que se buscan proyectos.</summary>
    public string[] ProjectWindowsRoots { get; init; } =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents", "Proyectos"),
    ];

    /// <summary>Distro de WSL donde también se buscan proyectos; vacío para no usar WSL.</summary>
    public string WslDistro { get; init; } = "archlinux";

    /// <summary>Carpeta dentro de esa distro bajo la que se buscan proyectos ("~" se resuelve al $HOME real).</summary>
    public string WslProjectRoot { get; init; } = "~/Proyectos";

    /// <summary>Usuario de GitHub por defecto cuando se dice "mi repo X" sin indicar de quién es.</summary>
    public string GitHubUsername { get; init; } = "ArcGabicho";

    /// <summary>Notas máximas que guarda la memoria persistente antes de olvidar las más antiguas.</summary>
    public int MaxMemoryEntries { get; init; } = 200;

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
            PiperPath              = Env("CLAUDIO_PIPER_PATH")       ?? cfg.PiperPath,
            PiperSpeedFactor       = EnvDouble("CLAUDIO_PIPER_SPEED") ?? cfg.PiperSpeedFactor,
            ElevenLabsApiKey       = Env("CLAUDIO_ELEVENLABS_API_KEY") ?? cfg.ElevenLabsApiKey,
            ElevenLabsVoiceId      = Env("CLAUDIO_ELEVENLABS_VOICE_ID") ?? cfg.ElevenLabsVoiceId,
            ElevenLabsModel        = Env("CLAUDIO_ELEVENLABS_MODEL") ?? cfg.ElevenLabsModel,
            WakeWord               = Env("CLAUDIO_WAKE_WORD")        ?? cfg.WakeWord,
            ChimeOnWake            = EnvBool("CLAUDIO_CHIME")        ?? cfg.ChimeOnWake,
            SilenceMs              = EnvInt("CLAUDIO_SILENCE_MS")    ?? cfg.SilenceMs,
            SilenceThreshold       = EnvDouble("CLAUDIO_SILENCE_THRESHOLD") ?? cfg.SilenceThreshold,
            MaxCommandSeconds      = EnvInt("CLAUDIO_MAX_COMMAND_SECONDS") ?? cfg.MaxCommandSeconds,
            NoSpeechTimeoutSeconds = EnvInt("CLAUDIO_NO_SPEECH_SECONDS") ?? cfg.NoSpeechTimeoutSeconds,
            StartWithWindows       = EnvBool("CLAUDIO_START_WITH_WINDOWS") ?? cfg.StartWithWindows,
            WslDistro              = Env("CLAUDIO_WSL_DISTRO")       ?? cfg.WslDistro,
            WslProjectRoot         = Env("CLAUDIO_WSL_PROJECT_ROOT") ?? cfg.WslProjectRoot,
            GitHubUsername         = Env("CLAUDIO_GITHUB_USERNAME")  ?? cfg.GitHubUsername,
            MaxMemoryEntries       = EnvInt("CLAUDIO_MAX_MEMORY")    ?? cfg.MaxMemoryEntries,
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
