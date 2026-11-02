using Bandichan.Core;
using Forms = System.Windows.Forms;

namespace Bandichan.App;

internal static class MonitorDiscovery
{
    public static IReadOnlyList<DisplayMonitor> GetMonitors()
    {
        var ordered = Forms.Screen.AllScreens
            .OrderByDescending(s => s.Primary)
            .ThenBy(s => s.Bounds.Left)
            .ThenBy(s => s.Bounds.Top)
            .ToList();

        return ordered
            .Select((s, index) => new DisplayMonitor(
                s.DeviceName,
                BuildLabel(index + 1, s),
                s.Bounds.Left,
                s.Bounds.Top,
                s.Bounds.Width,
                s.Bounds.Height))
            .ToList();
    }

    private static string BuildLabel(int index, Forms.Screen screen)
    {
        var role = screen.Primary ? "Primary" : "Secondary";
        var left = screen.Bounds.Left >= 0 ? $"+{screen.Bounds.Left}" : screen.Bounds.Left.ToString();
        var top = screen.Bounds.Top >= 0 ? $"+{screen.Bounds.Top}" : screen.Bounds.Top.ToString();

        return $"Monitor {index} ({role}) - {screen.Bounds.Width}x{screen.Bounds.Height} at ({left}, {top}) - {screen.DeviceName}";
    }
}
