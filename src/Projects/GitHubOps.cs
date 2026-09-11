using System.Diagnostics;
using System.Text.RegularExpressions;
using ClaudioAi.Diagnostics;

namespace ClaudioAi.Projects;

/// <summary>Resultado de una operación de git/GitHub, con el texto para decir en voz alta.</summary>
public sealed record GitResult(bool Ok, string Message, string? Path = null);

/// <summary>
/// Crea proyectos locales, clona repositorios y publica un proyecto existente en
/// GitHub (creando el repositorio con <c>gh</c> si hace falta). Solo opera sobre la
/// carpeta de Windows configurada: no toca WSL ni nada fuera de esa carpeta.
/// </summary>
public sealed class GitHubOps
{
    readonly ClaudioConfig _cfg;
    bool? _ghAvailable;
    bool? _ghAuthed;

    public GitHubOps(ClaudioConfig cfg) => _cfg = cfg;

    public bool IsGhAvailable() => _ghAvailable ??= RunGh(["--version"], null, 5000).ok;

    public bool IsGhAuthenticated() => _ghAuthed ??= RunGh(["auth", "status"], null, 8000).ok;

    public bool HasRemote(string path) => RunGit(["remote", "get-url", "origin"], path, 8000).ok;

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

    /// <summary>Crea el commit inicial si hace falta y publica: push si ya hay remoto, o crea el repo con gh si no.</summary>
    public GitResult Publish(string path, string projectName, bool isPrivate)
    {
        try
        {
            if (!Directory.Exists(System.IO.Path.Combine(path, ".git")))
            {
                var init = RunGit(["init"], path, 10000);
                if (!init.ok) return new GitResult(false, $"«git init» falló: {Trim(init.output)}");
            }

            var status = RunGit(["status", "--porcelain"], path, 10000);
            if (status.ok && !string.IsNullOrWhiteSpace(status.output))
            {
                RunGit(["add", "-A"], path, 15000);
                var commit = RunGit(["commit", "-m", "Commit inicial"], path, 15000);
                if (!commit.ok) return new GitResult(false, $"No pude preparar los cambios para subir: {Trim(commit.output)}");
            }

            if (HasRemote(path))
            {
                var (ok, output) = RunGit(["push"], path, 60000);
                return ok
                    ? new GitResult(true, $"Subí los cambios de {projectName} a GitHub.")
                    : new GitResult(false, $"No pude subir los cambios: {Trim(output)}");
            }

            if (!IsGhAvailable())
                return new GitResult(false,
                    "No tengo el CLI de GitHub instalado. Instálalo con winget install GitHub punto cli, y vuelve a pedírmelo.");
            if (!IsGhAuthenticated())
                return new GitResult(false,
                    "El CLI de GitHub no tiene sesión iniciada. Ejecuta gh auth login en una terminal, y vuelve a pedírmelo.");

            var slug = Slug(projectName);
            var create = RunGh(
                ["repo", "create", slug, isPrivate ? "--private" : "--public",
                 "--source", path, "--remote", "origin", "--push"],
                null, 60000);

            return create.ok
                ? new GitResult(true, $"Creé el repositorio {slug} en tu GitHub como {(isPrivate ? "privado" : "público")} y subí el proyecto.")
                : new GitResult(false, $"No pude crear el repositorio: {Trim(create.output)}");
        }
        catch (Exception ex)
        {
            Log.Error("Error al publicar el proyecto", ex);
            return new GitResult(false, "Ha habido un error al publicar el proyecto.");
        }
    }

    (bool ok, string output) RunGit(IEnumerable<string> args, string? workingDir, int timeoutMs) => Run("git", args, workingDir, timeoutMs);
    (bool ok, string output) RunGh(IEnumerable<string> args, string? workingDir, int timeoutMs) => Run("gh", args, workingDir, timeoutMs);

    static (bool ok, string output) Run(string exe, IEnumerable<string> args, string? workingDir, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
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
            return (false, $"{exe} no está instalado o no está en el PATH");   // caso esperado, sin ensuciar el log
        }
        catch (Exception ex)
        {
            Log.Error($"No pude ejecutar {exe}", ex);
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
