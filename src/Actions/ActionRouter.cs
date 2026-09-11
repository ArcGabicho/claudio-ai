using System.Diagnostics;
using System.Text.RegularExpressions;
using ClaudioAi.Brain;

namespace ClaudioAi.Actions;

/// <summary>
/// Ejecuta la <see cref="AssistantAction"/> que decidió el cerebro (versión Windows).
/// Devuelve un texto de resultado (para leerlo en voz alta) o cadena vacía.
/// </summary>
public sealed partial class ActionRouter
{
    /// <summary>
    /// Cmdlets/comandos de PowerShell permitidos para la acción <c>shell</c>.
    /// Solo consultas: nada que escriba en disco o toque el sistema.
    /// </summary>
    static readonly HashSet<string> AllowedShell = new(StringComparer.OrdinalIgnoreCase)
    {
        "Get-Date", "Get-CimInstance", "Get-ComputerInfo", "Get-Volume", "Get-PSDrive",
        "Get-Process", "Get-Service", "Get-Uptime", "Get-Host", "Get-Location",
        "Get-TimeZone", "Get-Culture", "Get-NetIPAddress", "Get-NetIPConfiguration",
        "whoami", "hostname", "ipconfig", "systeminfo", "ver", "vol",
    };

    /// <summary>Verbos/expresiones que jamás dejamos pasar, aunque el primer token parezca inocente.</summary>
    [GeneratedRegex(
        @"(?ix)
          \b(Remove|Set|New|Clear|Stop|Start|Restart|Rename|Move|Copy|Add|Out|Write|
             Disable|Enable|Install|Uninstall|Invoke|Format|Suspend|Resume|Block|Send|Export|Import)-
        | \b(rm|del|erase|rd|rmdir|move|copy|xcopy|robocopy|format|shutdown|reg|rundll32|
             takeown|icacls|attrib|schtasks|net|sc|diskpart|cipher|mklink)\b
        | \b(iex|icm|Invoke-Expression|Invoke-Command|Start-Process)\b
        | [>|&;`] | \$\( | \|\|")]
    private static partial Regex ForbiddenShell();

    public async Task<string> ExecuteAsync(AssistantAction action, CancellationToken ct = default) =>
        action.Action switch
        {
            "open_app"   => OpenApp(action.Target),
            "web_search" => WebSearch(action.Target),
            "shell"      => await RunShellAsync(action.Target, ct),
            _            => "",   // "say" y desconocidos: nada que ejecutar
        };

    static string OpenApp(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return "No me has dicho qué abrir.";

        target = target.Trim();
        var parts = target.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var exe = parts[0];
        var args = parts.Length > 1 ? parts[1] : "";

        try
        {
            // ShellExecute resuelve apps registradas (firefox, chrome, code, notepad, calc…),
            // rutas del PATH y "App Paths" del registro, y desacopla el proceso hijo.
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
            };
            if (args.Length > 0) psi.Arguments = args;
            Process.Start(psi);
            return $"Abriendo {exe}.";
        }
        catch (Exception)
        {
            // Segundo intento: dejar que el shell lo interprete (alias, .lnk, rutas raras).
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c start \"\" \"{target}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                return $"Abriendo {exe}.";
            }
            catch (Exception ex)
            {
                return $"No pude abrir {exe}: {ex.Message}";
            }
        }
    }

    static string WebSearch(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "¿Qué quieres que busque?";

        var url = "https://duckduckgo.com/?q=" + Uri.EscapeDataString(query);
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return $"Buscando {query} en el navegador.";
        }
        catch (Exception ex)
        {
            return $"No pude abrir el navegador: {ex.Message}";
        }
    }

    static async Task<string> RunShellAsync(string? cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return "";
        cmd = cmd.Trim();

        if (ForbiddenShell().IsMatch(cmd))
            return "Por seguridad no ejecuto ese comando.";

        var head = cmd.TrimStart('(', ' ', '&')
                      .Split(new[] { ' ', '(', ')', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                      .FirstOrDefault() ?? "";
        if (!AllowedShell.Contains(head))
            return $"Por seguridad no ejecuto «{head}».";

        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(cmd);

            using var proc = Process.Start(psi)!;
            var outText = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return Collapse(outText);
        }
        catch (Exception ex)
        {
            return $"Error al ejecutar: {ex.Message}";
        }
    }

    /// <summary>Deja la salida en una sola línea legible para leerla en voz alta.</summary>
    static string Collapse(string s)
        => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
