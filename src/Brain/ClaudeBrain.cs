using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClaudioAi.Memory;

namespace ClaudioAi.Brain;

/// <summary>
/// El "cerebro" del MVP: llama al CLI de Claude Code (<c>claude -p ... --output-format json</c>),
/// que ya va cubierto por la suscripción Claude Pro/Max. Reutiliza el <c>session_id</c>
/// devuelto para que la conversación tenga memoria entre órdenes de la misma sesión,
/// y le pega además las notas de <see cref="MemoryStore"/> para que recuerde cosas
/// entre reinicios de Claudio.
///
/// Más adelante esto se sustituye por el SDK oficial de Anthropic (paquete NuGet
/// <c>Anthropic</c>) con una API key; el resto de la aplicación no cambia.
/// </summary>
public sealed class ClaudeBrain
{
    readonly ClaudioConfig _cfg;
    readonly MemoryStore _memory;
    string? _sessionId;

    public ClaudeBrain(ClaudioConfig cfg, MemoryStore memory)
    {
        _cfg = cfg;
        _memory = memory;
    }

    const string SystemPreamble = """
        Eres "Claudio", un asistente de voz para un PC con Windows 11.
        El usuario habla y su voz se transcribe, así que puede haber errores de transcripción; interpreta con sentido común.
        Responde EXCLUSIVAMENTE con un objeto JSON en UNA sola línea, sin markdown ni texto alrededor:
        {"action":"open_app|web_search|web_answer|shell|open_project|new_project|clone_repo|remember|forget|say","target":"...","say":"..."}
        - open_app: para lanzar un PROGRAMA suelto. target = ejecutable o nombre de app de Windows (firefox, chrome, msedge, code, notepad, calc, explorer, spotify, ...). Deduce el nombre del ejecutable de lo que diga el usuario.
        - web_search: para ABRIR una búsqueda en el navegador cuando el usuario quiere navegar él mismo ("busca vuelos a Cusco", "ábreme una búsqueda de..."). target = términos de búsqueda.
        - web_answer: para RESPONDER HABLANDO con información de internet, cuando el usuario pregunta algo y quiere que TÚ le contestes (no que se abra el navegador): "qué es X", "quién es X", "cuánto cuesta X", "qué pasó con X", noticias, precios, resultados, o cualquier cosa que cambie con el tiempo o que no sepas con certeza. target = la pregunta o lo que hay que buscar, tal y como la entendiste. "say":"", Claudio busca y responde por su cuenta.
        - shell: target = UN solo comando de consulta de PowerShell NO destructivo, sin tuberías. Ejemplos válidos: "Get-Date" (fecha/hora), "(Get-Date) - (Get-CimInstance Win32_OperatingSystem).LastBootUpTime" (tiempo encendido), "Get-CimInstance Win32_OperatingSystem" (memoria/sistema), "Get-Volume" (discos), "whoami", "hostname", "systeminfo". Nunca borres, muevas ni modifiques nada; prohibido usar ';', '|', '>', '&', '$(' o varios comandos.
        - open_project: para abrir un PROYECTO, repositorio o carpeta de código (Windows o WSL) YA EXISTENTE, no un programa suelto. target = "[herramienta:]nombre del proyecto" TAL Y COMO LO DIJO el usuario, sin inventar rutas (se compara luego contra los proyectos reales que existen de verdad). "herramienta" es opcional: pon "vscode:" si menciona Visual Studio Code / VS Code / el editor / el código; pon "claude:" si menciona Claude Code / la terminal de Claude; pon "both:" si pide los dos. Si NO menciona ninguna herramienta, deja el target SIN prefijo (solo el nombre) y Claudio preguntará cuál usar. Dos ejemplos: dice "ábreme claudio-ai con Visual Studio Code" → target="vscode:claudio-ai"; dice "abre el proyecto vitalis erp" → target="vitalis erp". Pon "say":"" en esta acción, Claudio genera su propia respuesta.
        - new_project: crea una carpeta de proyecto NUEVA (de momento solo en Windows) con git ya iniciado, para EMPEZAR algo desde cero ("créame un proyecto llamado X", "empieza un proyecto nuevo X"). target = nombre del proyecto tal y como lo dijo el usuario, sin inventarle nada. "say":"".
        - clone_repo: clona un repositorio YA EXISTENTE de GitHub a la carpeta de proyectos de Windows ("clona X", "clona el repo X de fulano", "clona mi repo X"). target = referencia del repositorio, intenta darla en formato "usuario/repo" si puedes deducir el usuario de lo que dijo; si dice "mi repo" o no menciona de quién es, deja solo el nombre del repo (sin usuario) y Claudio usará el usuario configurado; si dice una URL completa de github.com, pon esa URL. "say":"".
        - remember: guarda un dato para recordarlo en el futuro, incluso después de reiniciar ("recuerda que...", "acuérdate de que...", "apunta que..."). target = el dato reescrito como una frase completa que se entienda sola (sin "recuerda que" delante, en tercera persona si hace falta). "say":"", Claudio confirma solo.
        - forget: borra algo que te pidió recordar antes ("olvida que...", "borra lo de..."). target = de qué trataba, para buscarlo entre lo guardado. "say":"".
        - say: solo hablar (conversación, preguntas de cultura general que ya sabes con certeza, o cuando no haya una acción clara). Si abajo hay notas guardadas y la pregunta va sobre alguna de ellas, respóndela usándolas.
        El campo "say" va siempre en español, natural y breve (máximo ~20 palabras).
        """;

    const string WebAnswerPreamble = """
        Eres "Claudio", un asistente de voz. Busca en internet información fiable y
        actual sobre esto, y responde en español EXCLUSIVAMENTE con la respuesta hablada,
        breve (máximo ~60 palabras), sin markdown, sin listas, sin enlaces ni citas: como
        si se la dijeras en voz alta a alguien.
        """;

    public async Task<AssistantAction> DecideAsync(string transcript, CancellationToken ct = default)
    {
        var notes = _memory.ForPrompt();
        var memorySection = notes.Length == 0
            ? ""
            : $"\n\nNotas guardadas de antes (puedes usarlas para responder; no hace falta repetirlas todas):\n{notes}";

        var prompt = $"{SystemPreamble}{memorySection}\n\nOrden del usuario: \"{transcript}\"";

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

    /// <summary>
    /// Pregunta algo que requiere información de internet: lanza una sesión de Claude
    /// aparte (no comparte memoria con <see cref="DecideAsync"/>) con las herramientas
    /// de búsqueda web permitidas sin pedir confirmación, y devuelve la respuesta ya
    /// pensada para decirse en voz alta.
    /// </summary>
    public async Task<string> AnswerFromWebAsync(string question, CancellationToken ct = default)
    {
        var prompt = $"{WebAnswerPreamble}\n\nPregunta: \"{question}\"";
        var (ok, raw) = await RunClaudeAsync(prompt, resume: false, ct, extraArgs: ["--allowedTools", "WebSearch", "WebFetch"]);
        return ok && !string.IsNullOrWhiteSpace(raw)
            ? Collapse(raw)
            : "No he podido buscarlo en internet ahora mismo.";
    }

    async Task<(bool ok, string output)> RunClaudeAsync(
        string prompt, bool resume, CancellationToken ct, IReadOnlyList<string>? extraArgs = null)
    {
        var psi = new ProcessStartInfo(_cfg.ClaudeCommand)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(prompt);
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");
        if (extraArgs is not null)
            foreach (var arg in extraArgs) psi.ArgumentList.Add(arg);
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
