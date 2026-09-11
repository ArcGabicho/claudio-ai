using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace ClaudioAi.Tray;

/// <summary>Estado visible de Claudio, representado por el color del icono de la bandeja.</summary>
public enum TrayState
{
    /// <summary>Escuchando la palabra de activación.</summary>
    Idle,
    /// <summary>Grabando la orden del usuario.</summary>
    Listening,
    /// <summary>Transcribiendo, pensando o respondiendo.</summary>
    Busy,
    /// <summary>Escucha en pausa.</summary>
    Paused,
}

/// <summary>
/// Genera al vuelo el icono de la bandeja (círculo de color con una "C"), para no
/// tener que incluir ficheros <c>.ico</c> en el proyecto.
/// </summary>
public static class TrayIconFactory
{
    public static Icon Create(TrayState state)
    {
        var color = state switch
        {
            TrayState.Listening => Color.FromArgb(46, 160, 67),   // verde
            TrayState.Busy      => Color.FromArgb(219, 154, 4),   // ámbar
            TrayState.Paused    => Color.FromArgb(110, 118, 129), // gris
            _                   => Color.FromArgb(47, 129, 247),  // azul
        };

        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            using (var brush = new SolidBrush(color))
                g.FillEllipse(brush, 1, 1, 30, 30);

            using var font = new Font("Segoe UI", 17, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fmt = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("C", font, Brushes.White, new RectangleF(0, 0, 32, 33), fmt);
        }

        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
