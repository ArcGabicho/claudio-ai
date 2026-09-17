using System.Text;
using System.Text.Json.Nodes;
using ClaudioAi.Diagnostics;

namespace ClaudioAi.Notifications;

public interface IDiscordNotifier
{
    Task SendTaskStartedAsync(string taskId, string taskName, string repo, CancellationToken ct = default);
    Task SendTaskCompletedAsync(string taskId, string taskName, TimeSpan timeSpent, CancellationToken ct = default);
    Task SendTaskFailedAsync(string taskId, string taskName, string errorMessage, CancellationToken ct = default);
    Task SendTaskProgressAsync(string taskId, int progress, TimeSpan? eta = null, CancellationToken ct = default);
    Task SendSystemAlertAsync(string severity, string message, CancellationToken ct = default);
}

/// <summary>
/// Notifica al canal de Discord configurado (vía webhook) los eventos del
/// orquestador: tareas iniciadas, completadas, fallidas, su progreso, y alertas
/// del sistema. Nunca lanza: si el webhook no está configurado o Discord no
/// responde, se registra en el log y quien la llamó sigue su curso con normalidad.
/// </summary>
public sealed class DiscordNotifier : IDiscordNotifier
{
    const int ColorBlue = 0x3498DB;
    const int ColorGreen = 0x2ECC71;
    const int ColorRed = 0xE74C3C;
    const int ColorYellow = 0xF1C40F;
    const int ColorOrange = 0xE67E22;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    readonly string? _webhookUrl;

    public DiscordNotifier(ClaudioConfig cfg) => _webhookUrl = cfg.DiscordWebhookUrl;

    public Task SendTaskStartedAsync(string taskId, string taskName, string repo, CancellationToken ct = default) =>
        SendAsync("🚀 Tarea iniciada", $"**{taskName}**", ColorBlue,
            [("ID", taskId, true), ("Repositorio", repo, true)], ct);

    public Task SendTaskCompletedAsync(string taskId, string taskName, TimeSpan timeSpent, CancellationToken ct = default) =>
        SendAsync("✅ Tarea completada", $"**{taskName}**", ColorGreen,
            [("ID", taskId, true), ("Tiempo", FormatDuration(timeSpent), true)], ct);

    public Task SendTaskFailedAsync(string taskId, string taskName, string errorMessage, CancellationToken ct = default) =>
        SendAsync("❌ Tarea fallida", $"**{taskName}**", ColorRed,
            [("ID", taskId, true), ("Error", Truncate(errorMessage, 1000), false)], ct);

    public Task SendTaskProgressAsync(string taskId, int progress, TimeSpan? eta = null, CancellationToken ct = default) =>
        SendAsync("🔄 Progreso de tarea", $"**{Math.Clamp(progress, 0, 100)}%** completado", ColorYellow,
            [("ID", taskId, true), ("ETA", eta is { } t ? FormatDuration(t) : "—", true)], ct);

    public Task SendSystemAlertAsync(string severity, string message, CancellationToken ct = default) =>
        SendAsync("⚠️ Alerta del sistema", Truncate(message, 2000), ColorOrange,
            [("Severidad", severity, true)], ct);

    async Task SendAsync(
        string title, string description, int color,
        (string name, string value, bool inline)[] fields, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_webhookUrl)) return;

        try
        {
            var embed = new JsonObject
            {
                ["title"] = title,
                ["description"] = description,
                ["color"] = color,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                ["fields"] = new JsonArray(fields.Select(f => (JsonNode)new JsonObject
                {
                    ["name"] = f.name,
                    ["value"] = string.IsNullOrWhiteSpace(f.value) ? "—" : f.value,
                    ["inline"] = f.inline,
                }).ToArray()),
            };
            var payload = new JsonObject { ["embeds"] = new JsonArray(embed) };

            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(_webhookUrl, content, ct);
            if (!response.IsSuccessStatusCode)
                Log.Warn($"[discord] webhook respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[discord] no se pudo notificar ({title}): {ex.Message}");
        }
    }

    static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" :
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" :
        $"{t.Seconds}s";

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
