namespace Bandichan.Core;

public sealed record RecordingRequest(
    DisplayMonitor Monitor,
    string OutputDirectory,
    int FrameRate,
    bool IncludeSystemAudio,
    string? SystemAudioDevice = null);
