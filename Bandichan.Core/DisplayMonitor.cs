namespace Bandichan.Core;

public sealed record DisplayMonitor(
    string Id,
    string DisplayName,
    int Left,
    int Top,
    int Width,
    int Height)
{
    public override string ToString() => $"{DisplayName} ({Width}x{Height})";
}
