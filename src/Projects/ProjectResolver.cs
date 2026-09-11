using System.Diagnostics;
using System.Globalization;
using System.Text;
using ClaudioAi.Diagnostics;

namespace ClaudioAi.Projects;

/// <summary>
/// Encuentra proyectos reales bajo las carpetas configuradas (Windows y, si hay
/// una distro configurada, dentro de WSL) y resuelve un nombre dicho por voz
/// contra ellos, para no depender de que Claude adivine rutas. También abre el
/// proyecto encontrado con Visual Studio Code y/o una terminal con Claude Code.
/// </summary>
public sealed class ProjectResolver
{
    readonly ClaudioConfig _cfg;
    string? _wslHomeCache;

    public ProjectResolver(ClaudioConfig cfg) => _cfg = cfg;

    public IReadOnlyList<ProjectRef> ListAll()
    {
        var list = new List<ProjectRef>();

        foreach (var root in _cfg.ProjectWindowsRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root))
                list.Add(new ProjectRef(Path.GetFileName(dir), ProjectKind.Windows, dir));
        }

        if (!string.IsNullOrWhiteSpace(_cfg.WslDistro))
        {
            var root = ResolveWslRoot();
            if (root is not null)
                foreach (var name in ListWslProjectNames(root))
                    list.Add(new ProjectRef(name, ProjectKind.Wsl, $"{root.TrimEnd('/')}/{name}"));
        }

        return list;
    }

    IEnumerable<string> ListWslProjectNames(string root)
    {
        var (ok, output) = RunWsl($"ls -1 -- \"{root}\"");
        return ok ? output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
    }

    string? ResolveWslRoot()
    {
        var configured = _cfg.WslProjectRoot;
        if (!configured.StartsWith('~')) return configured;

        _wslHomeCache ??= RunWsl("printenv HOME") is (true, var home) ? home.Trim() : null;
        return _wslHomeCache is null ? null : _wslHomeCache + configured[1..];
    }

    public bool WslHasClaudeCode() => RunWsl("command -v claude") is (true, var o) && !string.IsNullOrWhiteSpace(o);

    (bool ok, string output) RunWsl(string bashCommand)
    {
        try
        {
            var psi = new ProcessStartInfo("wsl.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(_cfg.WslDistro);
            psi.ArgumentList.Add("--"); psi.ArgumentList.Add("bash");
            psi.ArgumentList.Add("-lc"); psi.ArgumentList.Add(bashCommand);

            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var exited = p.WaitForExit(8000);
            return exited && p.ExitCode == 0 ? (true, stdout) : (false, "");
        }
        catch (Exception ex)
        {
            Log.Error("No pude consultar WSL", ex);
            return (false, "");
        }
    }

    /// <summary>
    /// Busca el/los proyecto(s) que mejor encajan con lo dicho por voz. Vacío si no
    /// hay nada parecido; más de uno si hay ambigüedad real (p. ej. nombres casi
    /// iguales en Windows y en WSL) y hace falta preguntar cuál.
    /// </summary>
    public IReadOnlyList<ProjectRef> Match(string spoken)
    {
        var all = ListAll();
        var target = Normalize(spoken);
        if (all.Count == 0 || target.Length == 0) return [];

        // Coincidencia exacta tras normalizar: gana sin ambigüedad aunque haya nombres parecidos.
        var exact = all.Where(p => Normalize(p.Name) == target).ToList();
        if (exact.Count == 1) return exact;

        var scored = all
            .Select(p => (proj: p, score: Similarity(target, Normalize(p.Name))))
            .OrderByDescending(t => t.score)
            .ToList();

        if (scored.Count == 0 || scored[0].score < 0.45) return [];   // nada se parece lo suficiente

        var best = scored[0].score;
        return scored.Where(t => best - t.score <= 0.12).Select(t => t.proj).Take(3).ToList();
    }

    public static double Similarity(string a, string b)
    {
        if (a == b) return 1.0;
        if (a.Length > 0 && b.Length > 0 && (b.Contains(a, StringComparison.Ordinal) || a.Contains(b, StringComparison.Ordinal)))
            return 0.85;
        var dist = Levenshtein(a, b);
        var maxLen = Math.Max(a.Length, b.Length);
        return maxLen == 0 ? 0 : 1.0 - (double)dist / maxLen;
    }

    static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        return d[a.Length, b.Length];
    }

    /// <summary>Minúsculas, sin acentos y sin signos, para comparar nombres hablados y reales.</summary>
    public static string Normalize(string s)
    {
        var decomposed = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Abre el proyecto con la herramienta pedida ("vscode" | "claude" | "both"). Devuelve un texto para decir en voz alta.</summary>
    public string Open(ProjectRef project, string tool) => tool switch
    {
        "claude" => OpenClaudeCode(project),
        "both"   => OpenVsCode(project) + " " + OpenClaudeCode(project),
        _        => OpenVsCode(project),
    };

    string OpenVsCode(ProjectRef project)
    {
        try
        {
            var args = project.Kind == ProjectKind.Windows
                ? $"\"{project.Path}\""
                : $"--remote wsl+{_cfg.WslDistro} \"{project.Path}\"";
            Process.Start(new ProcessStartInfo("code") { Arguments = args, UseShellExecute = true });
            return $"Abriendo {project.Name} en Visual Studio Code.";
        }
        catch (Exception ex)
        {
            Log.Error("No pude abrir Visual Studio Code", ex);
            return "No pude abrir Visual Studio Code.";
        }
    }

    string OpenClaudeCode(ProjectRef project)
    {
        try
        {
            if (project.Kind == ProjectKind.Windows)
            {
                Process.Start(new ProcessStartInfo("cmd.exe")
                {
                    Arguments = "/k claude",
                    WorkingDirectory = project.Path,
                    UseShellExecute = false,
                });
            }
            else
            {
                if (!WslHasClaudeCode())
                    return $"No encontré el CLI de Claude Code instalado en {_cfg.WslDistro}. Instálalo antes de pedírmelo así.";

                var psi = new ProcessStartInfo("wsl.exe") { UseShellExecute = false };
                psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(_cfg.WslDistro);
                psi.ArgumentList.Add("--cd"); psi.ArgumentList.Add(project.Path);
                psi.ArgumentList.Add("--"); psi.ArgumentList.Add("claude");
                Process.Start(psi);
            }
            return $"Abriendo {project.Name} en una terminal con Claude Code.";
        }
        catch (Exception ex)
        {
            Log.Error("No pude abrir Claude Code", ex);
            return "No pude abrir Claude Code.";
        }
    }
}
