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

    /// <summary>Segundos que dura cada grabación tras pulsar ENTER (MVP: duración fija).</summary>
    public int RecordSeconds { get; init; } = 6;

    /// <summary>Ejecutable del CLI de Claude Code que hace de "cerebro".</summary>
    public string ClaudeCommand { get; init; } = "claude";

    /// <summary>Motor de voz: auto | piper | espeak-ng | spd-say | none.</summary>
    public string TtsEngine { get; init; } = "auto";

    /// <summary>Ruta a un modelo .onnx de Piper (solo si TtsEngine = piper).</summary>
    public string? PiperModel { get; init; }

    /// <summary>~/.local/share/claudio-ai — datos persistentes (modelos descargados, etc.).</summary>
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
            Language      = Env("CLAUDIO_LANGUAGE")      ?? cfg.Language,
            WhisperModel  = Env("CLAUDIO_WHISPER_MODEL") ?? cfg.WhisperModel,
            ClaudeCommand = Env("CLAUDIO_CLAUDE_CMD")    ?? cfg.ClaudeCommand,
            TtsEngine     = Env("CLAUDIO_TTS")           ?? cfg.TtsEngine,
            PiperModel    = Env("CLAUDIO_PIPER_MODEL")   ?? cfg.PiperModel,
        };
    }

    static string? Env(string key)
        => Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : null;
}
