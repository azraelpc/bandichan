using System.Drawing;
using System.Runtime.InteropServices;

namespace Bandichan.App;

internal static class TrayIconFactory
{
    public static Icon CreateIdleIcon() => CreateIcon(Color.FromArgb(45, 180, 80));

    public static Icon CreateRecordingIcon() => CreateIcon(Color.FromArgb(220, 40, 40));

    public static Icon CreateProcessingIcon() => CreateIcon(Color.FromArgb(45, 180, 80));

    public static Icon CreateEditingIcon() => CreateIcon(Color.FromArgb(45, 180, 80));

    private static Icon CreateIcon(Color dotColor)
    {
        using var bitmap = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(bitmap);

        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using var ringBrush = new SolidBrush(Color.FromArgb(40, 40, 40));
        graphics.FillEllipse(ringBrush, 1, 1, 14, 14);

        using var dotBrush = new SolidBrush(dotColor);
        graphics.FillEllipse(dotBrush, 3, 3, 10, 10);

        using var borderPen = new Pen(Color.White, 1f);
        graphics.DrawEllipse(borderPen, 2, 2, 11, 11);

        var hIcon = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
