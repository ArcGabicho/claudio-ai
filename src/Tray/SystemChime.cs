using System.Media;

namespace ClaudioAi.Tray;

/// <summary>Avisos sonoros del sistema (para no incluir ficheros de audio propios).</summary>
public static class SystemChime
{
    /// <summary>"Ding" al detectar la palabra de activación: ya te escucho.</summary>
    public static void Wake(ClaudioConfig cfg)
    {
        if (!cfg.ChimeOnWake) return;
        try { SystemSounds.Asterisk.Play(); } catch { /* silencio si falla */ }
    }

    /// <summary>Aviso cuando no se entendió nada.</summary>
    public static void NotUnderstood()
    {
        try { SystemSounds.Exclamation.Play(); } catch { /* silencio si falla */ }
    }
}
