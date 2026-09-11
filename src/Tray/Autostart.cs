using Microsoft.Win32;
using ClaudioAi.Diagnostics;

namespace ClaudioAi.Tray;

/// <summary>
/// Arranque de Claudio con la sesión de Windows, vía la clave <c>Run</c> del
/// usuario actual (no necesita permisos de administrador).
/// </summary>
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Claudio";

    static string ExePath =>
        Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];

    /// <summary>Si el ejecutable actual es realmente <c>claudio.exe</c> y no <c>dotnet run</c>.</summary>
    static bool IsRealExe =>
        !Path.GetFileName(ExePath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch (Exception ex)
        {
            Log.Error("No pude leer el arranque automático", ex);
            return false;
        }
    }

    /// <summary>¿Se ha decidido ya alguna vez (aunque sea para desactivarlo)?</summary>
    public static bool HasBeenConfigured()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    public static void Enable()
    {
        if (!IsRealExe)
        {
            Log.Warn("Arranque automático omitido: se está ejecutando con 'dotnet run'. " +
                     "Compila y ejecuta 'claudio.exe' para registrarlo.");
            return;
        }
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.SetValue(ValueName, $"\"{ExePath}\"");
            Log.Info($"Arranque con Windows activado: {ExePath}");
        }
        catch (Exception ex)
        {
            Log.Error("No pude activar el arranque automático", ex);
        }
    }

    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            Log.Info("Arranque con Windows desactivado.");
        }
        catch (Exception ex)
        {
            Log.Error("No pude desactivar el arranque automático", ex);
        }
    }
}
