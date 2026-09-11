using ClaudioAi.Diagnostics;
using ClaudioAi.Projects;

namespace ClaudioAi.Memory;

/// <summary>
/// Notas persistentes ("Claudio, recuerda que...") que sobreviven a que cierres
/// Claudio o reinicies el PC: un fichero de texto plano en
/// <c>%LOCALAPPDATA%\claudio-ai\memory.md</c>, una nota por línea (editable a
/// mano si quieres), que se inyecta en el prompt del cerebro en cada turno.
/// </summary>
public sealed class MemoryStore
{
    readonly string _path;
    readonly int _maxEntries;
    readonly object _gate = new();

    public MemoryStore(ClaudioConfig cfg)
    {
        _path = Path.Combine(ClaudioConfig.DataDir, "memory.md");
        _maxEntries = Math.Max(10, cfg.MaxMemoryEntries);
    }

    public string FilePath => _path;

    public IReadOnlyList<string> LoadAll()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return [];
            return File.ReadAllLines(_path)
                .Select(l => l.TrimStart('-', ' ').Trim())
                .Where(l => l.Length > 0)
                .ToList();
        }
    }

    /// <summary>Bloque listo para pegar en el prompt del cerebro; cadena vacía si no hay nada guardado.</summary>
    public string ForPrompt()
    {
        var all = LoadAll();
        return all.Count == 0 ? "" : string.Join('\n', all.Select(l => $"- {l}"));
    }

    public void Add(string fact)
    {
        fact = fact.Trim();
        if (fact.Length == 0) return;

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(ClaudioConfig.DataDir);
                var lines = File.Exists(_path) ? File.ReadAllLines(_path).ToList() : [];
                lines.Add($"[{DateTime.Now:yyyy-MM-dd}] {fact}");
                if (lines.Count > _maxEntries)
                    lines = lines.Skip(lines.Count - _maxEntries).ToList();   // se olvida lo más antiguo
                File.WriteAllLines(_path, lines);
            }
            catch (Exception ex)
            {
                Log.Error("No pude guardar en la memoria", ex);
            }
        }
    }

    /// <summary>Borra la nota que mejor encaje con lo dicho. Devuelve el texto borrado, o null si no encontró nada parecido.</summary>
    public string? Forget(string query)
    {
        var normalizedQuery = ProjectResolver.Normalize(query);
        if (normalizedQuery.Length == 0) return null;

        lock (_gate)
        {
            if (!File.Exists(_path)) return null;
            var lines = File.ReadAllLines(_path).ToList();
            if (lines.Count == 0) return null;

            var best = lines
                .Select((line, i) => (line, i, score: ProjectResolver.Similarity(normalizedQuery, ProjectResolver.Normalize(line))))
                .OrderByDescending(t => t.score)
                .First();

            if (best.score < 0.35) return null;

            lines.RemoveAt(best.i);
            try
            {
                File.WriteAllLines(_path, lines);
            }
            catch (Exception ex)
            {
                Log.Error("No pude actualizar la memoria", ex);
            }

            return best.line;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try { File.Delete(_path); } catch { /* da igual */ }
        }
    }
}
