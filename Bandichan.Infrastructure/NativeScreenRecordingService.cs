using Bandichan.Core;
using ScreenRecorderLib;

namespace Bandichan.Infrastructure;

public sealed class NativeScreenRecordingService : IRecordingService
{
    private Recorder? _recorder;
    private RecordingSession? _session;
    private TaskCompletionSource<string>? _completion;
    private volatile bool _isRecording;

    public bool IsRecording => _isRecording;

    public RecordingSession? CurrentSession => _session;

    public Task<RecordingSession> StartAsync(RecordingRequest request, CancellationToken cancellationToken = default)
    {
        if (_recorder is not null)
        {
            throw new InvalidOperationException("A recording session is already running.");
        }

        Directory.CreateDirectory(request.OutputDirectory);
        var outputPath = Path.Combine(request.OutputDirectory, $"recording_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");

        var options = BuildOptions(request);
        var recorder = Recorder.CreateRecorder(options);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        recorder.OnRecordingComplete += (_, e) =>
        {
            _isRecording = false;
            completion.TrySetResult(string.IsNullOrWhiteSpace(e.FilePath) ? outputPath : e.FilePath);
        };
        recorder.OnRecordingFailed += (_, e) =>
        {
            _isRecording = false;
            completion.TrySetException(new InvalidOperationException($"Native recorder failed: {e.Error}"));
        };

        recorder.Record(outputPath);

        _isRecording = true;
        _recorder = recorder;
        _completion = completion;
        _session = new RecordingSession(outputPath, new CodecPreference("h264_mf", "aac", "mp4"), request.IncludeSystemAudio);
        return Task.FromResult(_session);
    }

    public async Task<RecordingSession?> StopAsync(CancellationToken cancellationToken = default)
    {
        if (_recorder is null)
        {
            return null;
        }

        var recorder = _recorder;
        var completion = _completion;

        _recorder = null;
        _completion = null;

        try
        {
            recorder.Stop();
        }
        catch
        {
            // Best effort stop.
        }

        if (completion is not null)
        {
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            var finished = await Task.WhenAny(completion.Task, timeoutTask);
            if (finished == completion.Task)
            {
                await completion.Task;
            }
            else
            {
                throw new TimeoutException("Timed out waiting for native recorder to finalize output file.");
            }
        }

        recorder.Dispose();

        var completed = _session;
        _session = null;
        _isRecording = false;

        if (completed is not null)
        {
            var info = new FileInfo(completed.OutputPath);
            if (!info.Exists || info.Length < 1024)
            {
                throw new InvalidOperationException("Native recording did not produce a valid output file.");
            }
        }

        return completed;
    }

    private static RecorderOptions BuildOptions(RecordingRequest request)
    {
        var displaySource = new DisplayRecordingSource(request.Monitor.Id);
        var sources = new List<RecordingSourceBase> { displaySource };

        var audioOptions = new AudioOptions
        {
            IsAudioEnabled = request.IncludeSystemAudio,
            AudioSources = [],
        };

        if (request.IncludeSystemAudio)
        {
            var loopbackSources = Recorder.GetSystemAudioLoopbackDevices();
            var selected = loopbackSources.FirstOrDefault(x =>
                string.Equals(x.DeviceName, request.SystemAudioDevice, StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.FriendlyName, request.SystemAudioDevice, StringComparison.OrdinalIgnoreCase)
                || (x.IsDefaultDevice && string.IsNullOrWhiteSpace(request.SystemAudioDevice)));

            audioOptions.AudioSources.Add(selected ?? LoopbackAudioSource.Default);
        }

        return new RecorderOptions
        {
            SourceOptions = new SourceOptions
            {
                RecordingSources = sources,
            },
            OutputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Video,
                IsVideoFramePreviewEnabled = false,
            },
            AudioOptions = audioOptions,
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Framerate = request.FrameRate,
                IsFixedFramerate = true,
                IsHardwareEncodingEnabled = true,
                IsMp4FastStartEnabled = true,
                Encoder = new H264VideoEncoder
                {
                    BitrateMode = H264BitrateControlMode.CBR,
                    EncoderProfile = H264Profile.Main,
                },
            },
        };
    }
}
