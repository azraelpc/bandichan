using Bandichan.Core;

namespace Bandichan.App;

public sealed class AppSettings
{
    public string OutputDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public EncoderSelectionMode EncoderMode { get; set; } = EncoderSelectionMode.AutoPreferEfficiency;

    public int CaptureFrameRate { get; set; } = 30;

    public bool RecordSystemAudio { get; set; } = true;

    public string? SystemAudioDevice { get; set; }

    public bool RunAtStartup { get; set; }
}
