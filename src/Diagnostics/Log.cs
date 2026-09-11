using System.Text;

namespace ClaudioAi.Diagnostics;

/// <summary>
/// Registro sencillo a fichero. En modo bandeja no hay consola, así que todo lo
/// interesante (arranque, activaciones, órdenes, errores) va a
/// <c>%LOCALAPPDATA%\claudio-ai\claudio.log</c>. También escribe por consola,
/// que es inofensivo cuando no la hay (diagnósticos con <c>--</c>).
/// </summary>
public static class Log
{
    static readonly object Gate = new();
    const long MaxBytes = 1_000_000;

    public static string FilePath { get; } = Path.Combine(ClaudioConfig.DataDir, "claudio.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Error(string message, Exception ex) => Write("ERROR", $"{message} :: {ex}");

    static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";

        try
        {
            Directory.CreateDirectory(ClaudioConfig.DataDir);
            lock (Gate)
            {
                Rotate();
                File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { /* el log nunca debe tumbar la app */ }

        try { Console.WriteLine(line); } catch { /* no hay consola */ }
    }

    static void Rotate()
    {
        try
        {
            var fi = new FileInfo(FilePath);
            if (!fi.Exists || fi.Length < MaxBytes) return;
            var bak = FilePath + ".1";
            File.Delete(bak);
            File.Move(FilePath, bak);
        }
        catch { /* si falla la rotación seguimos escribiendo igualmente */ }
    }
}
