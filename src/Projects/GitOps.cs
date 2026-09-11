using System.Diagnostics;
using System.Text.RegularExpressions;
using ClaudioAi.Diagnostics;

namespace ClaudioAi.Projects;

/// <summary>Resultado de una operación de git, con el texto para decir en voz alta.</summary>
public sealed record GitResult(bool Ok, string Message, string? Path = null);

/// <summary>
/// Crea proyectos locales y clona repositorios de GitHub, usando solo <c>git</c>
/// (sin el CLI de GitHub). Solo opera sobre la carpeta de Windows configurada: no
/// toca WSL ni nada fuera de esa carpeta.
/// </summary>
public sealed class GitOps
{
    readonly ClaudioConfig _cfg;

    public GitOps(ClaudioConfig cfg) => _cfg = cfg;

    public GitResult CreateProject(string spokenName)
    {
        var root = _cfg.ProjectWindowsRoots.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(root))
            return new GitResult(false, "No tengo ninguna carpeta de proyectos configurada.");

        var slug = Slug(spokenName);
        if (slug.Length == 0) return new GitResult(false, "No entendí el nombre del proyecto.");

        var path = Path.Combine(root, slug);
        if (Directory.Exists(path)) return new GitResult(false, $"Ya existe una carpeta llamada {slug}.");

        try
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "README.md"), $"# {slug}\n");

            var init = RunGit(["init"], path, 10000);
            if (!init.ok) return new GitResult(false, $"Creé la carpeta pero «git init» falló: {Trim(init.output)}", path);

            RunGit(["add", "-A"], path, 10000);
            var commit = RunGit(["commit", "-m", "Commit inicial"], path, 10000);
            if (!commit.ok)
                return new GitResult(true, $"Proyecto {slug} creado, pero no pude hacer el primer commit: {Trim(commit.output)}", path);

            return new GitResult(true, $"Proyecto {slug} creado.", path);
        }
        catch (Exception ex)
        {
            Log.Error("No pude crear el proyecto", ex);
            return new GitResult(false, "No pude crear el proyecto.");
        }
    }

    public GitResult Clone(string spokenReference)
    {
        var root = _cfg.ProjectWindowsRoots.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(root))
            return new GitResult(false, "No tengo ninguna carpeta de proyectos configurada.");

        var parsed = ParseRepoReference(spokenReference, _cfg.GitHubUsername);
        if (parsed is null) return new GitResult(false, "No entendí qué repositorio clonar.");
        var (owner, repo) = parsed.Value;

        var dest = Path.Combine(root, repo);
        if (Directory.Exists(dest)) return new GitResult(false, $"Ya existe una carpeta llamada {repo}.");

        var url = $"https://github.com/{owner}/{repo}.git";
        var (ok, output) = RunGit(["clone", url, dest], null, 90000);
        return ok
            ? new GitResult(true, $"Cloné {owner} barra {repo}.", dest)
            : new GitResult(false, $"No pude clonar {owner} barra {repo}: {Trim(output)}");
    }

    static (bool ok, string output) RunGit(IEnumerable<string> args, string? workingDir, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (!string.IsNullOrWhiteSpace(workingDir)) psi.WorkingDirectory = workingDir;

            using var p = Process.Start(psi)!;
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* da igual */ }
                return (false, "se agotó el tiempo de espera");
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            return p.ExitCode == 0 ? (true, stdout) : (false, string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, "git no está instalado o no está en el PATH");   // caso esperado, sin ensuciar el log
        }
        catch (Exception ex)
        {
            Log.Error("No pude ejecutar git", ex);
            return (false, ex.Message);
        }
    }

    static string Trim(string s) => s.Length <= 140 ? s.Trim() : s[..140].Trim() + "…";

    static string Slug(string spoken)
    {
        var norm = ProjectResolver.Normalize(spoken);
        return string.Join('-', norm.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    static (string owner, string repo)? ParseRepoReference(string raw, string defaultOwner)
    {
        var s = raw.Trim();

        var m = Regex.Match(s, @"github\.com[:/]+([^/\s]+)/([^/\s.]+)", RegexOptions.IgnoreCase);
        if (m.Success) return (m.Groups[1].Value, m.Groups[2].Value);

        var parts = s.Trim('/', ' ').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            >= 2 => (parts[^2], parts[^1]),
            1 when parts[0].Length > 0 => (defaultOwner, parts[0]),
            _ => null,
        };
    }
}
