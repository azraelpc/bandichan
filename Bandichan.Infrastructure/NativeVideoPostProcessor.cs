using Bandichan.Core;
using Windows.Media.Editing;
using Windows.Media.Effects;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using System.Runtime.InteropServices;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Bandichan.Infrastructure;

public sealed class NativeVideoPostProcessor : IVideoPostProcessor
{
    private const double EightMbPresetHardLimitMb = 10.0;
    private const double EightMbPresetCorrectionTargetMb = 9.6;

    public async Task<ExportResult> ExportAsync(
        ExportOptions options,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(options.SourcePath))
        {
            throw new FileNotFoundException("Source video not found.", options.SourcePath);
        }

        var sourceFile = await StorageFile.GetFileFromPathAsync(options.SourcePath);
        var sourceDuration = options.SourceDurationSeconds > 0
            ? TimeSpan.FromSeconds(options.SourceDurationSeconds)
            : TimeSpan.Zero;

        var sourceWidth = 1920;
        var sourceHeight = 1080;

        await TryReadSourceMetadataAsync(sourceFile, m =>
        {
            if (m.Duration > TimeSpan.Zero)
            {
                sourceDuration = m.Duration;
            }

            if (m.Width > 0)
            {
                sourceWidth = m.Width;
            }

            if (m.Height > 0)
            {
                sourceHeight = m.Height;
            }
        });

        if (sourceDuration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Could not read source video duration.");
        }

        var plan = BuildTransformPlan(options, sourceWidth, sourceHeight);
        var ranges = BuildKeepRanges(sourceDuration, options);
        if (ranges.Count == 0)
        {
            throw new InvalidOperationException("All content was removed by deleted segments.");
        }

        var effectiveDuration = TimeSpan.FromTicks(ranges.Sum(r => (r.End - r.Start).Ticks));
        var (videoBitrateKbps, audioBitrateKbps, targetMb) = CalculateEncodingTargets(options, effectiveDuration);
        var requiresExactEdits = options.EnableCrop || options.EnableResize || options.DeletedSegments.Count > 0;
        var selectedCodec = options.TargetCodec;

        progress?.Report(new ExportProgress(0, "Preparing export...", targetMb));

        // Ensure output file exists in target location with overwrite semantics.
        var outputFile = await CreateOutputFileAsync(options.OutputPath, cancellationToken);

        // For edited exports (crop/resize/cuts), we still enforce edit-preserving behavior,
        // but allow compatible profile retries before failing hard.

        var canUseDirectDebugPath = CanUseDirectDebugPath(options, plan, sourceWidth, sourceHeight);

        // Debug fast-path: when effective edits are no-op, bypass MediaComposition entirely.
        if (canUseDirectDebugPath)
        {
            progress?.Report(new ExportProgress(0, "Debug path: direct transcode (no edits workflow)...", targetMb));
            outputFile = await CreateOutputFileAsync(options.OutputPath, cancellationToken);
            if (await TryDirectTranscodeAsync(sourceFile, outputFile, options.KeepAudio, videoBitrateKbps, audioBitrateKbps, targetMb, selectedCodec, progress, cancellationToken))
            {
                progress?.Report(new ExportProgress(100, "Completed (direct transcode)", targetMb));
                return await CreateExportResultAsync(options, effectiveDuration, targetMb, videoBitrateKbps, audioBitrateKbps, progress, cancellationToken);
            }

            // If direct path fails, continue with composition fallback chain below.
            outputFile = await CreateOutputFileAsync(options.OutputPath, cancellationToken);
        }

        try
        {
            var tunedProfile = BuildTunedProfile(options, plan, videoBitrateKbps, audioBitrateKbps, selectedCodec);
            var tunedComposition = await BuildCompositionAsync(sourceFile, sourceDuration, ranges, options.KeepAudio, plan, applyTransforms: true, cancellationToken);
            await RenderCompositionAsync(tunedComposition, outputFile, tunedProfile, MediaTrimmingPreference.Precise, targetMb, progress, cancellationToken);
        }
        catch (Exception ex)
        {
            if (IsCancellation(ex, cancellationToken))
            {
                throw;
            }

            if (selectedCodec == ExportVideoCodec.Hevc)
            {
                selectedCodec = ExportVideoCodec.H264;
                progress?.Report(new ExportProgress(0, "HEVC unavailable on this system. Falling back to H.264...", targetMb));
            }

            // Some systems/codecs reject advanced profile attributes with MF_E_ATTRIBUTENOTFOUND.
            // Retry with conservative settings and finally disable transforms if needed.
            progress?.Report(new ExportProgress(0, "Retrying export with compatibility profile...", targetMb));
            outputFile = await CreateOutputFileAsync(options.OutputPath, cancellationToken);

            try
            {
                var fallbackProfile = BuildCompatibilityProfile(options, plan, videoBitrateKbps, audioBitrateKbps, selectedCodec);
                var fallbackComposition = await BuildCompositionAsync(sourceFile, sourceDuration, ranges, options.KeepAudio, plan, applyTransforms: true, cancellationToken);
                await RenderCompositionAsync(fallbackComposition, outputFile, fallbackProfile, MediaTrimmingPreference.Precise, targetMb, progress, cancellationToken);
            }
            catch (Exception ex2)
            {
                if (IsCancellation(ex2, cancellationToken))
                {
                    throw;
                }

                progress?.Report(new ExportProgress(0, "Retrying export with default profile (preserving edits)...", targetMb));
                outputFile = await CreateOutputFileAsync(options.OutputPath, cancellationToken);

                try
                {
                    var keepEditsComposition = await BuildCompositionAsync(sourceFile, sourceDuration, ranges, options.KeepAudio, plan, applyTransforms: true, cancellationToken);
                    await RenderCompositionAsync(keepEditsComposition, outputFile, profile: null, MediaTrimmingPreference.Fast, targetMb, progress, cancellationToken);
                    progress?.Report(new ExportProgress(100, "Completed", targetMb));
                    return await CreateExportResultAsync(options, effectiveDuration, targetMb, videoBitrateKbps, audioBitrateKbps, progress, cancellationToken);
                }
                catch (Exception exKeepEdits)
                {
                    if (IsCancellation(exKeepEdits, cancellationToken))
                    {
                        throw;
                    }

                    if (requiresExactEdits)
                    {
                        // Additional matrix for edit-preserving exports: prefer keeping crop/cuts even if resize/audio must be relaxed.
                        var planNoResize = plan with { EnableResize = false };

                        progress?.Report(new ExportProgress(0, "Retrying export preserving crop/cuts (without resize)...", targetMb));
                        outputFile = await CreateOutputFileAsync(options.OutputPath, cancellationToken);

                        try
                        {
                            var cropOnlyComposition = await BuildCompositionAsync(sourceFile, sourceDuration, ranges, options.KeepAudio, planNoResize, applyTransforms: true, cancellationToken);
                            var cropOnlyProfile = BuildCompatibilityProfile(options, planNoResize, videoBitrateKbps, audioBitrateKbps, selectedCodec);
                            await RenderCompositionAsync(cropOnlyComposition, outputFile, cropOnlyProfile, MediaTrimmingPreference.Fast, targetMb, progress, cancellationToken);
                            progress?.Report(new ExportProgress(100, "Completed", targetMb));
                            return await CreateExportResultAsync(options, effectiveDuration, targetMb, videoBitrateKbps, audioBitrateKbps, progress, cancellationToken);
                        }
                        catch (Exception exCropOnly)
                        {
                            if (IsCancellation(exCropOnly, cancellationToken))
                            {
                                throw;
                            }
                        }

                        progress?.Report(new ExportProgress(0, "Retrying export preserving crop/cuts (default profile)...", targetMb));
                        outputFile = await CreateOutputFileAsync(options.OutputPath, cancellationToken);

                        try
                        {
                            var cropDefaultComposition = await BuildCompositionAsync(sourceFile, sourceDuration, ranges, options.KeepAudio, planNoResize, applyTransforms: true, cancellationToken);
                            await RenderCompositionAsync(cropDefaultComposition, outputFile, profile: null, MediaTrimmingPreference.Fast, targetMb, progress, cancellationToken);
                            progress?.Report(new ExportProgress(100, "Completed", targetMb));
                            return await CreateExportResultAsync(options, effectiveDuration, targetMb, videoBitrateKbps, audioBitrateKbps, progress, cancellationToken);
                        }
                        catch (Exception exCropDefault)
                        {
                            if (IsCancellation(exCropDefault, cancellationToken))
                            {
                                throw;
                            }
                        }

                        if (options.KeepAudio)
                        {
                            progress?.Report(new ExportProgress(0, "Retrying export preserving crop/cuts (audio, absolute default)...", targetMb));
                            outputFile = await CreateOutputFileAsync(options.OutputPath, cancellationToken);

                            try
                            {
                                var cropAudioDefaultComposition = await BuildCompositionAsync(sourceFile, sourceDuration, ranges, keepAudio: true, planNoResize, applyTransforms: true, cancellationToken);
                                await RenderCompositionDefaultAsync(cropAudioDefaultComposition, outputFile, targetMb, progress, cancellationToken);
                                progress?.Report(new ExportProgress(100, "Completed", targetMb));
                                return await CreateExportResultAsync(options, effectiveDuration, targetMb, videoBitrateKbps, audioBitrateKbps, progress, cancellationToken);
                            }
                            catch (Exception exCropAudioDefault)
                            {
                                if (IsCancellation(exCropAudioDefault, cancellationToken))
                                {
                                    throw;
                                }
                            }

                            progress?.Report(new ExportProgress(0, "Retrying export preserving crop/cuts (without audio)...", targetMb));
                            outputFile = await CreateOutputFileAsync(options.OutputPath, cancellationToken);

                            try
                            {
                                var cropNoAudioComposition = await BuildCompositionAsync(sourceFile, sourceDuration, ranges, keepAudio: false, planNoResize, applyTransforms: true, cancellationToken);
                                var cropNoAudioProfile = BuildCompatibilityProfile(options with { KeepAudio = false }, planNoResize, videoBitrateKbps, audioBitrateKbps: 0, selectedCodec);
                                await RenderCompositionAsync(cropNoAudioComposition, outputFile, cropNoAudioProfile, MediaTrimmingPreference.Fast, targetMb, progress, cancellationToken);

                                var outputAudioKbps = 0;
                                try
                                {
                                    progress?.Report(new ExportProgress(0, "Reattaching source audio...", targetMb));
                                    await AttachSourceAudioAsync(options.OutputPath, sourceFile, sourceDuration, ranges, targetMb, progress, cancellationToken);
                                    outputAudioKbps = audioBitrateKbps;
                                }
                                catch (Exception exAttach) when (!IsCancellation(exAttach, cancellationToken))
                                {
                                    progress?.Report(new ExportProgress(0, "Audio reattach failed; keeping silent export.", targetMb));
                                }

                                progress?.Report(new ExportProgress(100, "Completed", targetMb));
                                return await CreateExportResultAsync(options, effectiveDuration, targetMb, videoBitrateKbps, outputAudioKbps, progress, cancellationToken);
                            }
                            catch (Exception exCropNoAudio)
                            {
                                if (IsCancellation(exCropNoAudio, cancellationToken))
                                {
                                    throw;
                                }
                            }
                        }

                        throw new InvalidOperationException(
                            "Export failed while preserving edits (crop/resize/cuts). No fallback without edits was applied.",
                            exKeepEdits);
                    }
                }

                progress?.Report(new ExportProgress(0, "Retrying export without crop/resize for compatibility...", targetMb));
                outputFile = await CreateOutputFileAsync(options.OutputPath, cancellationToken);
                try
                {
                    var safeProfile = BuildCompatibilityProfile(options, plan with { EnableCrop = false, EnableResize = false }, videoBitrateKbps, audioBitrateKbps, selectedCodec);
                    var safeComposition = await BuildCompositionAsync(sourceFile, sourceDuration, ranges, options.KeepAudio, plan, applyTransforms: false, cancellationToken);
                    await RenderCompositionAsync(safeComposition, outputFile, safeProfile, MediaTrimmingPreference.Fast, targetMb, progress, cancellationToken);
                }
                catch (Exception ex3)
                {
                    if (IsCancellation(ex3, cancellationToken))
                    {
                        throw;
                    }

                    progress?.Report(new ExportProgress(0, "Retrying export with default profile...", targetMb));
                    outputFile = await CreateOutputFileAsync(options.OutputPath, cancellationToken);
                    var safestComposition = await BuildCompositionAsync(sourceFile, sourceDuration, ranges, options.KeepAudio, plan, applyTransforms: false, cancellationToken);
                    try
                    {
                        await RenderCompositionAsync(safestComposition, outputFile, profile: null, MediaTrimmingPreference.Fast, targetMb, progress, cancellationToken);
                    }
                    catch (Exception ex4)
                    {
                        if (IsCancellation(ex4, cancellationToken))
                        {
                            throw;
                        }

                        progress?.Report(new ExportProgress(0, "Retrying export with absolute default transcode...", targetMb));
                        outputFile = await CreateOutputFileAsync(options.OutputPath, cancellationToken);
                        try
                        {
                            await RenderCompositionDefaultAsync(safestComposition, outputFile, targetMb, progress, cancellationToken);
                        }
                        catch (Exception ex5)
                        {
                            if (IsCancellation(ex5, cancellationToken))
                            {
                                throw;
                            }

                            // Final rescue path for debugging: direct transcode can succeed where composition pipeline fails.
                            progress?.Report(new ExportProgress(0, "Final debug fallback: direct transcode (no edits)...", targetMb));
                            outputFile = await CreateOutputFileAsync(options.OutputPath, cancellationToken);
                            if (await TryDirectTranscodeAsync(sourceFile, outputFile, options.KeepAudio, videoBitrateKbps, audioBitrateKbps, targetMb, selectedCodec, progress, cancellationToken))
                            {
                                progress?.Report(new ExportProgress(100, "Completed (debug direct fallback)", targetMb));
                                return await CreateExportResultAsync(options, effectiveDuration, targetMb, videoBitrateKbps, audioBitrateKbps, progress, cancellationToken);
                            }

                            if (canUseDirectDebugPath)
                            {
                                progress?.Report(new ExportProgress(0, "Final debug fallback: copying source file...", targetMb));
                                await CopySourceAsDebugExportAsync(options.SourcePath, options.OutputPath, cancellationToken);
                                progress?.Report(new ExportProgress(100, "Completed (source copy debug fallback)", targetMb));
                                return await CreateExportResultAsync(options, effectiveDuration, targetMb, videoBitrateKbps, audioBitrateKbps, progress, cancellationToken);
                            }

                            throw new InvalidOperationException(
                                "Native export failed after all compatibility fallbacks, including direct transcoder debug mode.",
                                ex5);
                        }
                    }
                }
            }
        }

        progress?.Report(new ExportProgress(100, "Completed", targetMb));

        if (requiresExactEdits && plan.EnableCrop && options.KeepAudio)
        {
            try
            {
                if (!await HasEmbeddedAudioAsync(options.OutputPath))
                {
                    progress?.Report(new ExportProgress(0, "Attaching audio track...", targetMb));
                    await AttachSourceAudioAsync(options.OutputPath, sourceFile, sourceDuration, ranges, targetMb, progress, cancellationToken);
                    progress?.Report(new ExportProgress(100, "Completed", targetMb));
                }
            }
            catch (Exception exAttach) when (!IsCancellation(exAttach, cancellationToken))
            {
                // Keep successful video export even if audio attach cannot be completed on this machine.
            }
        }

        return await CreateExportResultAsync(options, effectiveDuration, targetMb, videoBitrateKbps, audioBitrateKbps, progress, cancellationToken);
    }

    private static async Task<ExportResult> CreateExportResultAsync(
        ExportOptions options,
        TimeSpan effectiveDuration,
        double targetMb,
        int videoBitrateKbps,
        int audioBitrateKbps,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var actualMb = new FileInfo(options.OutputPath).Length / (1024d * 1024d);

        if (options.Preset == ExportPreset.EightMb && actualMb > EightMbPresetHardLimitMb)
        {
            progress?.Report(new ExportProgress(0, "8MB preset exceeded 10MB. Applying Discord-size correction...", targetMb));
            await EnforceEightMbHardLimitAsync(options.OutputPath, effectiveDuration, options.KeepAudio, progress, cancellationToken);
            actualMb = new FileInfo(options.OutputPath).Length / (1024d * 1024d);
        }

        return new ExportResult(options.OutputPath, targetMb, actualMb, videoBitrateKbps, audioBitrateKbps);
    }

    private static async Task EnforceEightMbHardLimitAsync(
        string outputPath,
        TimeSpan effectiveDuration,
        bool keepAudio,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var durationSeconds = Math.Max(1d, effectiveDuration.TotalSeconds);

        // Up to two corrective passes to stay under 10 MB for Discord uploads.
        foreach (var targetMb in new[] { EightMbPresetCorrectionTargetMb, 8.8 })
        {
            var tempPath = Path.Combine(
                Path.GetDirectoryName(outputPath) ?? string.Empty,
                $"{Path.GetFileNameWithoutExtension(outputPath)}.discord-limit-{Guid.NewGuid():N}{Path.GetExtension(outputPath)}");

            try
            {
                var inputFile = await StorageFile.GetFileFromPathAsync(outputPath);
                var tempOutput = await CreateOutputFileAsync(tempPath, cancellationToken);

                var totalKbps = (int)Math.Floor((targetMb * 1024d * 1024d * 8d) / durationSeconds / 1000d);
                var audioKbps = keepAudio ? (totalKbps <= 224 ? 64 : 96) : 0;
                var videoKbps = Math.Max(300, totalKbps - audioKbps);

                var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
                ForceMp4CodecAac(profile, ExportVideoCodec.H264, keepAudio);
                profile.Video.Bitrate = (uint)Math.Max(300_000, videoKbps * 1000);
                if (!keepAudio)
                {
                    profile.Audio = null;
                }
                else if (profile.Audio is not null)
                {
                    profile.Audio.Bitrate = (uint)Math.Max(64_000, audioKbps * 1000);
                }

                var transcoder = new MediaTranscoder
                {
                    HardwareAccelerationEnabled = false,
                    AlwaysReencode = true,
                };

                var prepared = await transcoder.PrepareFileTranscodeAsync(inputFile, tempOutput, profile).AsTask(cancellationToken);
                if (!prepared.CanTranscode)
                {
                    continue;
                }

                var transcode = prepared.TranscodeAsync();
                transcode.Progress = (_, value) =>
                {
                    progress?.Report(new ExportProgress(value, "Applying Discord-size correction", targetMb));
                };

                await transcode.AsTask(cancellationToken);

                File.Copy(tempPath, outputPath, overwrite: true);
                var correctedMb = new FileInfo(outputPath).Length / (1024d * 1024d);
                if (correctedMb <= EightMbPresetHardLimitMb)
                {
                    return;
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }
        }
    }

    private static async Task<MediaComposition> BuildCompositionAsync(
        StorageFile sourceFile,
        TimeSpan sourceDuration,
        IReadOnlyList<(TimeSpan Start, TimeSpan End)> ranges,
        bool keepAudio,
        TransformPlan plan,
        bool applyTransforms,
        CancellationToken cancellationToken)
    {
        if (applyTransforms && (plan.EnableCrop || plan.EnableResize))
        {
            return await BuildCompositionWithOverlayTransformAsync(sourceFile, sourceDuration, ranges, keepAudio, plan, cancellationToken);
        }

        var composition = new MediaComposition();
        foreach (var range in ranges)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var clip = await MediaClip.CreateFromFileAsync(sourceFile);
            clip.TrimTimeFromStart = range.Start;
            clip.TrimTimeFromEnd = sourceDuration - range.End;
            if (!keepAudio)
            {
                clip.Volume = 0;
            }

            if (applyTransforms)
            {
                var clipPlan = SanitizePlanForClip(clip, plan);
                ApplyTransformEffects(clip, clipPlan);
            }

            composition.Clips.Add(clip);
        }

        return composition;
    }

    private static async Task<MediaComposition> BuildCompositionWithOverlayTransformAsync(
        StorageFile sourceFile,
        TimeSpan sourceDuration,
        IReadOnlyList<(TimeSpan Start, TimeSpan End)> ranges,
        bool keepAudio,
        TransformPlan plan,
        CancellationToken cancellationToken)
    {
        var composition = new MediaComposition();
        var layer = new MediaOverlayLayer();

        var sourceRectX = plan.EnableCrop ? plan.CropX : 0;
        var sourceRectY = plan.EnableCrop ? plan.CropY : 0;
        var sourceRectW = plan.EnableCrop ? plan.CropWidth : plan.SourceWidth;
        var sourceRectH = plan.EnableCrop ? plan.CropHeight : plan.SourceHeight;

        var targetWidth = plan.EnableResize ? plan.ResizeWidth : sourceRectW;
        var targetHeight = plan.EnableResize ? plan.ResizeHeight : sourceRectH;

        targetWidth = Math.Max(16, targetWidth);
        targetHeight = Math.Max(16, targetHeight);

        sourceRectW = Math.Max(16, sourceRectW);
        sourceRectH = Math.Max(16, sourceRectH);

        var sx = targetWidth / (double)sourceRectW;
        var sy = targetHeight / (double)sourceRectH;

        var cursor = TimeSpan.Zero;

        foreach (var range in ranges)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var clipDuration = range.End - range.Start;
            if (clipDuration <= TimeSpan.Zero)
            {
                continue;
            }

            // Base clip carries timeline duration and audio.
            var baseClip = await MediaClip.CreateFromFileAsync(sourceFile);
            baseClip.TrimTimeFromStart = range.Start;
            baseClip.TrimTimeFromEnd = sourceDuration - range.End;
            if (!keepAudio)
            {
                baseClip.Volume = 0;
            }

            composition.Clips.Add(baseClip);

            // Black fill overlay hides uncropped source video area.
            var black = new Windows.UI.Color { A = 255, R = 0, G = 0, B = 0 };
            var blackClip = MediaClip.CreateFromColor(black, clipDuration);
            blackClip.Volume = 0;

            var blackOverlay = new MediaOverlay(blackClip)
            {
                Delay = cursor,
                Position = new Windows.Foundation.Rect(0, 0, targetWidth, targetHeight),
            };
            layer.Overlays.Add(blackOverlay);

                // Transformed visual overlay (muted) renders crop and/or resize region.
            var sourceClip = await MediaClip.CreateFromFileAsync(sourceFile);
            sourceClip.TrimTimeFromStart = range.Start;
            sourceClip.TrimTimeFromEnd = sourceDuration - range.End;
            sourceClip.Volume = 0;

            var overlay = new MediaOverlay(sourceClip)
            {
                Delay = cursor,
                Position = new Windows.Foundation.Rect(
                    -sourceRectX * sx,
                    -sourceRectY * sy,
                    plan.SourceWidth * sx,
                    plan.SourceHeight * sy),
            };

            layer.Overlays.Add(overlay);
            cursor += clipDuration;
        }

        if (layer.Overlays.Count > 0)
        {
            composition.OverlayLayers.Add(layer);
        }

        return composition;
    }

    private static MediaEncodingProfile BuildTunedProfile(ExportOptions options, TransformPlan plan, int videoBitrateKbps, int audioBitrateKbps, ExportVideoCodec codec)
    {
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
        ForceMp4CodecAac(profile, codec, options.KeepAudio);
        profile.Video.Bitrate = (uint)Math.Max(400_000, videoBitrateKbps * 1000);

        if (plan.EnableResize && !plan.EnableCrop)
        {
            profile.Video.Width = (uint)plan.ResizeWidth;
            profile.Video.Height = (uint)plan.ResizeHeight;
        }
        else if (plan.EnableCrop)
        {
            profile.Video.Width = (uint)(plan.EnableResize ? plan.ResizeWidth : plan.CropWidth);
            profile.Video.Height = (uint)(plan.EnableResize ? plan.ResizeHeight : plan.CropHeight);
        }

        if (!options.KeepAudio)
        {
            profile.Audio = null;
        }
        else if (profile.Audio is not null)
        {
            profile.Audio.Bitrate = (uint)Math.Max(64_000, audioBitrateKbps * 1000);
        }

        return profile;
    }

    private static MediaEncodingProfile BuildCompatibilityProfile(ExportOptions options, TransformPlan plan, int videoBitrateKbps, int audioBitrateKbps, ExportVideoCodec codec)
    {
        var quality = plan.EnableResize || plan.EnableCrop
            ? (plan.EnableResize ? PickQuality(plan.ResizeWidth, plan.ResizeHeight) : PickQuality(plan.CropWidth, plan.CropHeight))
            : VideoEncodingQuality.Auto;

        var profile = MediaEncodingProfile.CreateMp4(quality);
        ForceMp4CodecAac(profile, codec, options.KeepAudio);
        profile.Video.Bitrate = (uint)Math.Max(400_000, videoBitrateKbps * 1000);

        if (plan.EnableResize && !plan.EnableCrop)
        {
            profile.Video.Width = (uint)plan.ResizeWidth;
            profile.Video.Height = (uint)plan.ResizeHeight;
        }
        else if (plan.EnableCrop)
        {
            profile.Video.Width = (uint)(plan.EnableResize ? plan.ResizeWidth : plan.CropWidth);
            profile.Video.Height = (uint)(plan.EnableResize ? plan.ResizeHeight : plan.CropHeight);
        }

        if (!options.KeepAudio)
        {
            profile.Audio = null;
        }
        else if (profile.Audio is not null)
        {
            profile.Audio.Bitrate = (uint)Math.Max(64_000, audioBitrateKbps * 1000);
        }

        return profile;
    }

    private static VideoEncodingQuality PickQuality(int width, int height)
    {
        var max = Math.Max(width, height);
        if (max >= 2160)
        {
            return VideoEncodingQuality.Uhd2160p;
        }

        if (max >= 1080)
        {
            return VideoEncodingQuality.HD1080p;
        }

        if (max >= 720)
        {
            return VideoEncodingQuality.HD720p;
        }

        return VideoEncodingQuality.Wvga;
    }

    private static async Task RenderCompositionAsync(
        MediaComposition composition,
        StorageFile outputFile,
        MediaEncodingProfile? profile,
        MediaTrimmingPreference trimmingPreference,
        double targetMb,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await RenderInternalAsync(composition, outputFile, profile, trimmingPreference, targetMb, progress, cancellationToken);
            return;
        }
        catch (Exception ex) when (!IsCancellation(ex, cancellationToken) && (IsStreamStateIssue(ex) || IsInvalidParameterIssue(ex)))
        {
            // Some Media Foundation pipelines fail with stream-state errors until the composition is rehydrated.
            progress?.Report(new ExportProgress(0, "Recovering composition state and retrying...", targetMb));
            MediaComposition recovered;
            try
            {
                recovered = await RehydrateCompositionAsync(composition, cancellationToken);
            }
            catch (Exception rehydrateEx) when (!IsCancellation(rehydrateEx, cancellationToken))
            {
                progress?.Report(new ExportProgress(0, "Composition recovery failed, using software transcoder...", targetMb));
                await RenderViaMediaStreamSourceAsync(composition, outputFile, profile, targetMb, progress, cancellationToken);
                return;
            }

            try
            {
                await RenderInternalAsync(recovered, outputFile, profile, trimmingPreference, targetMb, progress, cancellationToken);
                return;
            }
            catch (Exception ex2) when (!IsCancellation(ex2, cancellationToken))
            {
                progress?.Report(new ExportProgress(0, "Retrying with software transcoder compatibility mode...", targetMb));
                await RenderViaMediaStreamSourceAsync(recovered, outputFile, profile, targetMb, progress, cancellationToken);
            }
        }
    }

    private static async Task RenderViaMediaStreamSourceAsync(
        MediaComposition composition,
        StorageFile outputFile,
        MediaEncodingProfile? profile,
        double targetMb,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var previewWidth = 1280;
        var previewHeight = 720;

        if (profile?.Video is not null && profile.Video.Width > 0 && profile.Video.Height > 0)
        {
            previewWidth = (int)profile.Video.Width;
            previewHeight = (int)profile.Video.Height;
        }
        else if (composition.Clips.Count > 0)
        {
            var clipProps = composition.Clips[0].GetVideoEncodingProperties();
            if (clipProps.Width > 0 && clipProps.Height > 0)
            {
                previewWidth = (int)clipProps.Width;
                previewHeight = (int)clipProps.Height;
            }
        }

        previewWidth = Math.Max(16, previewWidth);
        previewHeight = Math.Max(16, previewHeight);

        var streamSource = composition.GeneratePreviewMediaStreamSource(previewWidth, previewHeight);
        if (streamSource is null)
        {
            throw new InvalidOperationException("Could not create preview stream source for compatibility transcode.");
        }

        var effectiveProfile = profile ?? MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
        ForceMp4CodecAac(effectiveProfile, InferCodecFromProfile(effectiveProfile), keepAudio: effectiveProfile.Audio is not null);

        using IRandomAccessStream outStream = await outputFile.OpenAsync(FileAccessMode.ReadWrite).AsTask(cancellationToken);
        var transcoder = new MediaTranscoder
        {
            HardwareAccelerationEnabled = false,
            AlwaysReencode = true,
        };

        var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(streamSource, outStream, effectiveProfile).AsTask(cancellationToken);
        if (!prepared.CanTranscode)
        {
            throw new InvalidOperationException($"Compatibility transcoder cannot transcode: {prepared.FailureReason}");
        }

        var transcode = prepared.TranscodeAsync();
        transcode.Progress = (_, value) =>
        {
            progress?.Report(new ExportProgress(value, "Exporting (compatibility)", targetMb));
        };

        await transcode.AsTask(cancellationToken);
    }

    private static async Task RenderInternalAsync(
        MediaComposition composition,
        StorageFile outputFile,
        MediaEncodingProfile? profile,
        MediaTrimmingPreference trimmingPreference,
        double targetMb,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var transcode = profile is null
            ? composition.RenderToFileAsync(outputFile, trimmingPreference)
            : composition.RenderToFileAsync(outputFile, trimmingPreference, profile);
        transcode.Progress = (_, value) =>
        {
            progress?.Report(new ExportProgress(value, "Exporting", targetMb));
        };

        var result = await transcode.AsTask(cancellationToken);
        if (result != TranscodeFailureReason.None)
        {
            throw new InvalidOperationException($"Native export failed: {result}");
        }
    }

    private static async Task<MediaComposition> RehydrateCompositionAsync(MediaComposition composition, CancellationToken cancellationToken)
    {
        var tempName = $"bandichan-export-recover-{Guid.NewGuid():N}.cmp";
        var tempFile = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(tempName, CreationCollisionOption.ReplaceExisting).AsTask(cancellationToken);

        try
        {
            await composition.SaveAsync(tempFile).AsTask(cancellationToken);
            return await MediaComposition.LoadAsync(tempFile).AsTask(cancellationToken);
        }
        finally
        {
            try
            {
                await tempFile.DeleteAsync(StorageDeleteOption.PermanentDelete).AsTask(cancellationToken);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private static async Task RenderCompositionDefaultAsync(
        MediaComposition composition,
        StorageFile outputFile,
        double targetMb,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var transcode = composition.RenderToFileAsync(outputFile);
        transcode.Progress = (_, value) =>
        {
            progress?.Report(new ExportProgress(value, "Exporting", targetMb));
        };

        var result = await transcode.AsTask(cancellationToken);
        if (result != TranscodeFailureReason.None)
        {
            throw new InvalidOperationException($"Native export failed: {result}");
        }
    }

    private static async Task<bool> TryDirectTranscodeAsync(
        StorageFile sourceFile,
        StorageFile outputFile,
        bool keepAudio,
        int videoBitrateKbps,
        int audioBitrateKbps,
        double targetMb,
        ExportVideoCodec codec,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var audioModes = keepAudio ? new[] { true, false } : new[] { false };

        foreach (var withAudio in audioModes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
            ForceMp4CodecAac(profile, codec, withAudio);
            profile.Video.Bitrate = (uint)Math.Max(400_000, videoBitrateKbps * 1000);
            if (!withAudio)
            {
                profile.Audio = null;
            }
            else if (profile.Audio is not null)
            {
                profile.Audio.Bitrate = (uint)Math.Max(64_000, audioBitrateKbps * 1000);
            }

            var transcoder = new MediaTranscoder
            {
                HardwareAccelerationEnabled = false,
                AlwaysReencode = true,
            };

            try
            {
                var prep = await transcoder.PrepareFileTranscodeAsync(sourceFile, outputFile, profile).AsTask(cancellationToken);
                if (!prep.CanTranscode)
                {
                    continue;
                }

                var transcode = prep.TranscodeAsync();
                transcode.Progress = (_, value) =>
                {
                    progress?.Report(new ExportProgress(value, withAudio ? "Direct transcoding" : "Direct transcoding (no audio)", targetMb));
                };

                await transcode.AsTask(cancellationToken);
                return true;
            }
            catch (Exception ex) when (!IsCancellation(ex, cancellationToken))
            {
                // Try next audio mode.
            }
        }

        return false;
    }

    private static async Task AttachSourceAudioAsync(
        string videoPath,
        StorageFile sourceFile,
        TimeSpan sourceDuration,
        IReadOnlyList<(TimeSpan Start, TimeSpan End)> ranges,
        double targetMb,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var videoFile = await StorageFile.GetFileFromPathAsync(videoPath);
        var composition = new MediaComposition();

        var videoClip = await MediaClip.CreateFromFileAsync(videoFile);
        videoClip.Volume = 0;
        composition.Clips.Add(videoClip);

        var outputDuration = videoClip.TrimmedDuration > TimeSpan.Zero
            ? videoClip.TrimmedDuration
            : (videoClip.OriginalDuration > TimeSpan.Zero ? videoClip.OriginalDuration : sourceDuration);

        if (outputDuration <= TimeSpan.Zero)
        {
            outputDuration = sourceDuration;
        }

        var hasSingleFullRange = ranges.Count == 1
            && ranges[0].Start <= TimeSpan.FromMilliseconds(1)
            && Math.Abs((ranges[0].End - sourceDuration).TotalMilliseconds) <= 5;

        if (hasSingleFullRange)
        {
            var fullTrack = await BackgroundAudioTrack.CreateFromFileAsync(sourceFile);
            var trimFromEnd = sourceDuration - outputDuration;
            if (trimFromEnd < TimeSpan.Zero)
            {
                trimFromEnd = TimeSpan.Zero;
            }
            fullTrack.TrimTimeFromStart = TimeSpan.Zero;
            fullTrack.TrimTimeFromEnd = trimFromEnd;
            fullTrack.Delay = TimeSpan.Zero;
            composition.BackgroundAudioTracks.Add(fullTrack);
        }

        var cursor = TimeSpan.Zero;
        var remainingOutput = outputDuration;
        foreach (var range in ranges)
        {
            if (hasSingleFullRange)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var start = range.Start < TimeSpan.Zero ? TimeSpan.Zero : range.Start;
            if (start > sourceDuration)
            {
                start = sourceDuration;
            }

            var end = range.End;
            if (end < start)
            {
                end = start;
            }
            if (end > sourceDuration)
            {
                end = sourceDuration;
            }

            var clipDuration = end - start;
            if (clipDuration <= TimeSpan.Zero)
            {
                continue;
            }

            if (remainingOutput <= TimeSpan.Zero)
            {
                break;
            }

            if (clipDuration > remainingOutput)
            {
                clipDuration = remainingOutput;
            }

            var boundedEnd = start + clipDuration;

            var trimFromEnd = sourceDuration - boundedEnd;
            if (trimFromEnd < TimeSpan.Zero)
            {
                trimFromEnd = TimeSpan.Zero;
            }

            var audioTrack = await BackgroundAudioTrack.CreateFromFileAsync(sourceFile);
            audioTrack.TrimTimeFromStart = start;
            audioTrack.TrimTimeFromEnd = trimFromEnd;
            audioTrack.Delay = cursor;
            composition.BackgroundAudioTracks.Add(audioTrack);
            cursor += clipDuration;
            remainingOutput -= clipDuration;
        }

        var tempPath = Path.Combine(Path.GetDirectoryName(videoPath)!, $"{Path.GetFileNameWithoutExtension(videoPath)}.withaudio{Path.GetExtension(videoPath)}");
        var tempOutput = await CreateOutputFileAsync(tempPath, cancellationToken);

        try
        {
            await RenderCompositionDefaultAsync(composition, tempOutput, targetMb, progress, cancellationToken);

            File.Copy(tempPath, videoPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private static async Task<bool> HasEmbeddedAudioAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var clip = await MediaClip.CreateFromFileAsync(file);
        return clip.EmbeddedAudioTracks.Count > 0;
    }

    private static bool CanUseDirectDebugPath(
        ExportOptions options,
        TransformPlan plan,
        int sourceWidth,
        int sourceHeight)
    {
        return options.DeletedSegments.Count == 0
            && !options.EnableCrop
            && !options.EnableResize;
    }

    private static async Task CopySourceAsDebugExportAsync(string sourcePath, string outputPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static bool IsCancellation(Exception ex, CancellationToken cancellationToken)
    {
        return ex is OperationCanceledException || cancellationToken.IsCancellationRequested;
    }

    private static void ForceMp4CodecAac(MediaEncodingProfile profile, ExportVideoCodec codec, bool keepAudio)
    {
        profile.Container.Subtype = "MPEG4";
        profile.Video.Subtype = codec == ExportVideoCodec.Hevc ? "HEVC" : "H264";
        if (keepAudio && profile.Audio is not null)
        {
            profile.Audio.Subtype = "AAC";
        }
    }

    private static ExportVideoCodec InferCodecFromProfile(MediaEncodingProfile profile)
    {
        return string.Equals(profile.Video?.Subtype, "HEVC", StringComparison.OrdinalIgnoreCase)
            ? ExportVideoCodec.Hevc
            : ExportVideoCodec.H264;
    }

    private static async Task TryReadSourceMetadataAsync(StorageFile sourceFile, Action<(TimeSpan Duration, int Width, int Height)> assign)
    {
        try
        {
            var props = await sourceFile.Properties.GetVideoPropertiesAsync();
            var duration = props.Duration;
            var width = (int)Math.Max(0, props.Width);
            var height = (int)Math.Max(0, props.Height);
            assign((duration, width, height));
        }
        catch
        {
            // Keep safe defaults if metadata probing fails on this machine/codec stack.
        }
    }

    private static bool IsAttributeIssue(Exception ex)
    {
        const int MfAttributeNotFound = unchecked((int)0xC00D36E6);
        if (ex.HResult == MfAttributeNotFound)
        {
            return true;
        }

        if (ex is COMException com && com.HResult == MfAttributeNotFound)
        {
            return true;
        }

        var message = ex.Message;
        if (!string.IsNullOrWhiteSpace(message)
            && (message.Contains("0xC00D36E6", StringComparison.OrdinalIgnoreCase)
                || message.Contains("requested attribute was not found", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return ex.InnerException is not null && IsAttributeIssue(ex.InnerException);
    }

    private static bool IsStreamStateIssue(Exception ex)
    {
        const int MfInvalidRequest = unchecked((int)0xC00DA7FC);

        if (ex.HResult == MfInvalidRequest)
        {
            return true;
        }

        if (ex is COMException com && com.HResult == MfInvalidRequest)
        {
            return true;
        }

        var message = ex.Message;
        if (!string.IsNullOrWhiteSpace(message)
            && (message.Contains("0xC00DA7FC", StringComparison.OrdinalIgnoreCase)
                || message.Contains("stream is not in a state", StringComparison.OrdinalIgnoreCase)
                || message.Contains("state to handle the request", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return ex.InnerException is not null && IsStreamStateIssue(ex.InnerException);
    }

    private static bool IsInvalidParameterIssue(Exception ex)
    {
        const int EInvalidArg = unchecked((int)0x80070057);

        if (ex.HResult == EInvalidArg)
        {
            return true;
        }

        if (ex is COMException com && com.HResult == EInvalidArg)
        {
            return true;
        }

        var message = ex.Message;
        if (!string.IsNullOrWhiteSpace(message)
            && (message.Contains("parameter is incorrect", StringComparison.OrdinalIgnoreCase)
                || message.Contains("0x80070057", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return ex.InnerException is not null && IsInvalidParameterIssue(ex.InnerException);
    }

    private static void ApplyTransformEffects(MediaClip clip, TransformPlan plan)
    {
        if (!plan.EnableCrop && !plan.EnableResize)
        {
            return;
        }

        var transform = new VideoTransformEffectDefinition();

        if (plan.EnableCrop)
        {
            transform.CropRectangle = new Windows.Foundation.Rect(
                plan.CropX,
                plan.CropY,
                plan.CropWidth,
                plan.CropHeight);
        }

        if (plan.EnableResize)
        {
            transform.OutputSize = new Windows.Foundation.Size(
                plan.ResizeWidth,
                plan.ResizeHeight);
        }

        clip.VideoEffectDefinitions.Add(transform);
    }

    private sealed record TransformPlan(
        bool EnableCrop,
        int CropX,
        int CropY,
        int CropWidth,
        int CropHeight,
        bool EnableResize,
        int ResizeWidth,
        int ResizeHeight,
        int SourceWidth,
        int SourceHeight);

    private static TransformPlan BuildTransformPlan(ExportOptions options, int sourceWidth, int sourceHeight)
    {
        var enableCrop = options.EnableCrop;
        var cropX = options.CropX;
        var cropY = options.CropY;
        var cropW = options.CropWidth;
        var cropH = options.CropHeight;

        if (enableCrop)
        {
            cropX = Math.Clamp(cropX, 0, Math.Max(0, sourceWidth - 16));
            cropY = Math.Clamp(cropY, 0, Math.Max(0, sourceHeight - 16));
            cropX = ToEven(cropX);
            cropY = ToEven(cropY);

            var maxW = Math.Max(16, sourceWidth - cropX);
            var maxH = Math.Max(16, sourceHeight - cropY);
            cropW = Math.Clamp(cropW, 16, maxW);
            cropH = Math.Clamp(cropH, 16, maxH);

            cropW = ToMultipleOf16(cropW);
            cropH = ToMultipleOf16(cropH);

            if (cropX + cropW > sourceWidth)
            {
                cropX = Math.Max(0, sourceWidth - cropW);
            }

            if (cropY + cropH > sourceHeight)
            {
                cropY = Math.Max(0, sourceHeight - cropH);
            }
        }

        var enableResize = options.EnableResize;
        var resizeW = options.ResizeWidth;
        var resizeH = options.ResizeHeight;

        if (enableResize)
        {
            resizeW = ToEven(Math.Max(16, resizeW));
            resizeH = ToEven(Math.Max(16, resizeH));
        }

        return new TransformPlan(
            enableCrop,
            cropX,
            cropY,
            cropW,
            cropH,
            enableResize,
            resizeW,
            resizeH,
            sourceWidth,
            sourceHeight);
    }

    private static int ToEven(int value)
    {
        var n = Math.Max(2, value);
        return n % 2 == 0 ? n : n - 1;
    }

    private static int ToMultipleOf16(int value)
    {
        var n = Math.Max(16, value);
        return n - (n % 16);
    }

    private static TransformPlan SanitizePlanForClip(MediaClip clip, TransformPlan plan)
    {
        if (!plan.EnableCrop && !plan.EnableResize)
        {
            return plan;
        }

        var encoding = clip.GetVideoEncodingProperties();
        var width = (int)Math.Max(0, encoding.Width);
        var height = (int)Math.Max(0, encoding.Height);

        if (width < 16 || height < 16)
        {
            return plan;
        }

        if (!plan.EnableCrop)
        {
            return plan;
        }

        var x = Math.Clamp(ToEven(plan.CropX), 0, Math.Max(0, width - 16));
        var y = Math.Clamp(ToEven(plan.CropY), 0, Math.Max(0, height - 16));
        var maxW = Math.Max(16, width - x);
        var maxH = Math.Max(16, height - y);
        var w = ToMultipleOf16(Math.Clamp(plan.CropWidth, 16, maxW));
        var h = ToMultipleOf16(Math.Clamp(plan.CropHeight, 16, maxH));

        if (x + w > width)
        {
            x = Math.Max(0, width - w);
            x = ToEven(x);
        }

        if (y + h > height)
        {
            y = Math.Max(0, height - h);
            y = ToEven(y);
        }

        if (w <= 0 || h <= 0)
        {
            return plan with { EnableCrop = false };
        }

        return plan with
        {
            CropX = x,
            CropY = y,
            CropWidth = w,
            CropHeight = h,
        };
    }

    private static async Task<StorageFile> CreateOutputFileAsync(string path, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("Invalid output path.");
        }

        Directory.CreateDirectory(directory);
        var folder = await StorageFolder.GetFolderFromPathAsync(directory);
        return await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting).AsTask(cancellationToken);
    }

    private static List<(TimeSpan Start, TimeSpan End)> BuildKeepRanges(TimeSpan sourceDuration, ExportOptions options)
    {
        if (options.DeletedSegments.Count == 0)
        {
            return [(TimeSpan.Zero, sourceDuration)];
        }

        var deleted = options.DeletedSegments
            .Select(s =>
            {
                var start = TimeSpan.FromSeconds(s.NormalizedStart / (double)Math.Max(1, options.FrameRate));
                var end = TimeSpan.FromSeconds(s.NormalizedEnd / (double)Math.Max(1, options.FrameRate));
                if (end < start)
                {
                    (start, end) = (end, start);
                }

                if (start < TimeSpan.Zero)
                {
                    start = TimeSpan.Zero;
                }

                if (end > sourceDuration)
                {
                    end = sourceDuration;
                }

                return (Start: start, End: end);
            })
            .Where(x => x.End > x.Start)
            .OrderBy(x => x.Start)
            .ToList();

        var keep = new List<(TimeSpan Start, TimeSpan End)>();
        var cursor = TimeSpan.Zero;

        foreach (var cut in deleted)
        {
            if (cut.Start > cursor)
            {
                keep.Add((cursor, cut.Start));
            }

            if (cut.End > cursor)
            {
                cursor = cut.End;
            }
        }

        if (cursor < sourceDuration)
        {
            keep.Add((cursor, sourceDuration));
        }

        return keep;
    }

    private static (int VideoKbps, int AudioKbps, double TargetMb) CalculateEncodingTargets(ExportOptions options, TimeSpan effectiveDuration)
    {
        var durationSeconds = Math.Max(1d, effectiveDuration.TotalSeconds);
        var width = options.EnableResize ? options.ResizeWidth : (options.EnableCrop ? options.CropWidth : 1280);
        var height = options.EnableResize ? options.ResizeHeight : (options.EnableCrop ? options.CropHeight : 720);

        width = Math.Max(16, width);
        height = Math.Max(16, height);

        var pixelFactor = Math.Max(0.2, (width * (double)height) / (1280d * 720d));
        var fpsFactor = Math.Max(0.5, options.FrameRate / 30d);
        var balancedVideo = (int)Math.Round(2800d * Math.Pow(pixelFactor, 0.65) * Math.Sqrt(fpsFactor));
        balancedVideo = Math.Clamp(balancedVideo, 2200, 9000);
        var balancedPlusVideo = (int)Math.Round(balancedVideo * 1.35);
        balancedPlusVideo = Math.Clamp(balancedPlusVideo, 3000, 14000);

        if (options.Preset == ExportPreset.Balanced)
        {
            var audio = options.KeepAudio ? 128 : 0;
            var video = balancedVideo;
            var estimatedMb = ((video + audio) * durationSeconds * 1000d) / 8d / 1024d / 1024d;
            return (video, audio, Math.Max(1, estimatedMb));
        }

        if (options.Preset == ExportPreset.BalancedPlus)
        {
            var audio = options.KeepAudio ? 128 : 0;
            var video = balancedPlusVideo;
            var estimatedMb = ((video + audio) * durationSeconds * 1000d) / 8d / 1024d / 1024d;
            return (video, audio, Math.Max(1, estimatedMb));
        }

        var targetMb = options.Preset switch
        {
            ExportPreset.EightMb => 8.0,
            _ => Math.Max(1.0, options.CustomTargetMb),
        };

        var targetBits = targetMb * 1024d * 1024d * 8d;
        var totalKbps = (int)Math.Floor(targetBits / durationSeconds / 1000d);
        var audioBitrate = options.KeepAudio ? (totalKbps <= 256 ? 64 : 128) : 0;
        var requestedVideoBitrate = Math.Max(300, totalKbps - audioBitrate);

        if (options.Preset == ExportPreset.EightMb)
        {
            return (requestedVideoBitrate, audioBitrate, targetMb);
        }

        // Preserve visual quality for constrained-size presets with a capped floor for size accuracy.
        var qualityFloorVideo = (int)Math.Round(balancedVideo * 0.75);
        qualityFloorVideo = Math.Clamp(qualityFloorVideo, 1500, 10000);
        var floorAppliedVideo = Math.Max(requestedVideoBitrate, qualityFloorVideo);

        var maxTotalKbps = (int)Math.Round(totalKbps * 1.25); // e.g. 8 MB -> about 10 MB max
        var maxVideoKbps = Math.Max(300, maxTotalKbps - audioBitrate);
        var videoBitrate = Math.Min(floorAppliedVideo, maxVideoKbps);

        var effectiveTotalKbps = videoBitrate + audioBitrate;
        var effectiveTargetMb = Math.Max(1, (effectiveTotalKbps * durationSeconds * 1000d) / 8d / 1024d / 1024d);
        return (videoBitrate, audioBitrate, effectiveTargetMb);
    }
}
