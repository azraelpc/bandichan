namespace Bandichan.Core;

public interface IEncoderSelector
{
    Task<CodecPreference> SelectBestCodecAsync(CancellationToken cancellationToken = default);
}

public interface IRecordingService
{
    bool IsRecording { get; }
    RecordingSession? CurrentSession { get; }
    Task<RecordingSession> StartAsync(RecordingRequest request, CancellationToken cancellationToken = default);
    Task<RecordingSession?> StopAsync(CancellationToken cancellationToken = default);
}
