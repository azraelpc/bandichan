namespace Bandichan.Core;

public enum ExportPreset
{
    Balanced,
    EightMb,
    Custom,
}

public sealed record FrameRange(int StartFrame, int EndFrame)
{
    public int NormalizedStart => Math.Min(StartFrame, EndFrame);
    public int NormalizedEnd => Math.Max(StartFrame, EndFrame);
}

public sealed record ExportOptions(
    string SourcePath,
    string OutputPath,
    int FrameRate,
    bool EnableCrop,
    int CropX,
    int CropY,
    int CropWidth,
    int CropHeight,
    bool EnableResize,
    int ResizeWidth,
    int ResizeHeight,
    bool KeepAudio,
    ExportPreset Preset,
    double CustomTargetMb,
    IReadOnlyList<FrameRange> DeletedSegments,
    double SourceDurationSeconds = 0);

public sealed record ExportResult(
    string OutputPath,
    double TargetMb,
    double ActualMb,
    int VideoBitrateKbps,
    int AudioBitrateKbps);

public sealed record ExportProgress(
    double Percent,
    string Stage,
    double EstimatedFinalSizeMb);

public interface IVideoPostProcessor
{
    Task<ExportResult> ExportAsync(
        ExportOptions options,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
