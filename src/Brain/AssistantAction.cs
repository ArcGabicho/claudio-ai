using System.Text.Json.Serialization;

namespace ClaudioAi.Brain;

/// <summary>
/// Decisión que toma Claudio ante una orden. El "cerebro" (Claude) devuelve
/// exactamente este objeto en JSON.
/// </summary>
public sealed class AssistantAction
{
    /// <summary>open_app | web_search | shell | say</summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = "say";

    /// <summary>
    /// open_app → ejecutable a lanzar · web_search → términos de búsqueda ·
    /// shell → comando de consulta · say → (sin uso)
    /// </summary>
    [JsonPropertyName("target")]
    public string? Target { get; set; }

    /// <summary>Frase corta, en español, para decir en voz alta.</summary>
    [JsonPropertyName("say")]
    public string Say { get; set; } = "";
}
