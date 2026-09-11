using System.Diagnostics;
using System.Text.Json;

namespace ClaudioAi.Brain;

/// <summary>
/// El "cerebro" del MVP: llama al CLI de Claude Code (<c>claude -p ... --output-format json</c>),
/// que ya va cubierto por la suscripción Claude Pro/Max. Reutiliza el <c>session_id</c>
/// devuelto para que la conversación tenga memoria entre órdenes.
///
/// Más adelante esto se sustituye por el SDK oficial de Anthropic (paquete NuGet
/// <c>Anthropic</c>) con una API key; el resto de la aplicación no cambia.
/// </summary>
public sealed class ClaudeBrain
{
    readonly ClaudioConfig _cfg;
    string? _sessionId;

    public ClaudeBrain(ClaudioConfig cfg) => _cfg = cfg;

    const string SystemPreamble = """
        Eres "Claudio", un asistente de voz para un PC con Windows 11.
        El usuario habla y su voz se transcribe, así que puede haber errores de transcripción; interpreta con sentido común.
        Responde EXCLUSIVAMENTE con un objeto JSON en UNA sola línea, sin markdown ni texto alrededor:
        {"action":"open_app|web_search|shell|say","target":"...","say":"..."}
        - open_app: target = ejecutable o nombre de app de Windows (firefox, chrome, msedge, code, notepad, calc, explorer, spotify, ...). Deduce el nombre del ejecutable de lo que diga el usuario.
        - web_search: target = términos de búsqueda; se abrirán en el navegador.
        - shell: target = UN solo comando de consulta de PowerShell NO destructivo, sin tuberías. Ejemplos válidos: "Get-Date" (fecha/hora), "(Get-Date) - (Get-CimInstance Win32_OperatingSystem).LastBootUpTime" (tiempo encendido), "Get-CimInstance Win32_OperatingSystem" (memoria/sistema), "Get-Volume" (discos), "whoami", "hostname", "systeminfo". Nunca borres, muevas ni modifiques nada; prohibido usar ';', '|', '>', '&', '$(' o varios comandos.
        - say: solo hablar (conversación, preguntas, o cuando no haya una acción clara).
        El campo "say" va siempre en español, natural y breve (máximo ~20 palabras).
        """;

    public async Task<AssistantAction> DecideAsync(string transcript, CancellationToken ct = default)
    {
        var prompt = $"{SystemPreamble}\n\nOrden del usuario: \"{transcript}\"";

        var (ok, raw) = await RunClaudeAsync(prompt, resume: _sessionId is not null, ct);
        if (!ok && _sessionId is not null)                       // sesión caducada -> reintenta limpio
        {
            _sessionId = null;
            (ok, raw) = await RunClaudeAsync(prompt, resume: false, ct);
        }

        return ok
            ? ParseAction(raw)
            : new AssistantAction { Action = "say", Say = "No he podido contactar con el modelo." };
    }

    async Task<(bool ok, string output)> RunClaudeAsync(string prompt, bool resume, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_cfg.ClaudeCommand)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(prompt);
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");
        if (resume && _sessionId is not null)
        {
            psi.ArgumentList.Add("--resume");
            psi.ArgumentList.Add(_sessionId);
        }

        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"no se pudo iniciar '{_cfg.ClaudeCommand}'");

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (proc.ExitCode != 0)
            {
                Console.Error.WriteLine($"[brain] claude salió con {proc.ExitCode}: {stderr.Trim()}");
                return (false, "");
            }

            // Con --output-format json, la salida es un objeto con .result (texto) y .session_id.
            try
            {
                using var doc = JsonDocument.Parse(stdout);
                var root = doc.RootElement;
                if (root.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String)
                    _sessionId = sid.GetString();
                var result = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String
                    ? r.GetString() ?? ""
                    : stdout;
                return (true, result);
            }
            catch (JsonException)
            {
                return (true, stdout); // formato inesperado: que lo intente el parser de acciones
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[brain] {ex.Message}");
            return (false, "");
        }
    }

    static AssistantAction ParseAction(string text)
    {
        if (ExtractJsonObject(text) is { } json)
        {
            try
            {
                var action = JsonSerializer.Deserialize<AssistantAction>(json);
                if (action is not null && !string.IsNullOrWhiteSpace(action.Action))
                    return action;
            }
            catch (JsonException) { /* cae al fallback */ }
        }

        // Si no vino JSON, tratamos todo el texto como algo que decir.
        return new AssistantAction { Action = "say", Say = Collapse(text) };
    }

    static string? ExtractJsonObject(string s)
    {
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        return start >= 0 && end > start ? s[start..(end + 1)] : null;
    }

    static string Collapse(string s)
        => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
