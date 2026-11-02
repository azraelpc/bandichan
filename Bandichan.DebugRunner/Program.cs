using Bandichan.Core;
using Bandichan.Infrastructure;
using System.Globalization;
using Forms = System.Windows.Forms;
using Windows.Storage;
using Windows.Media.Editing;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        var options = ParseOptions(args);
        var outputRoot = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputRoot);

        var logPath = Path.Combine(outputRoot, "debug-runner.log");
        using var log = new StreamWriter(logPath, append: true);

        async Task LogAsync(string message)
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            Console.WriteLine(line);
            await log.WriteLineAsync(line);
            await log.FlushAsync();
        }

        await LogAsync("Starting headless debug recorder/export runner.");
        await LogAsync($"OutputRoot={outputRoot} Seconds={options.Seconds} Fps={options.FrameRate} KeepAudio={options.KeepAudio} MaxAttempts={options.MaxAttempts}");

        var monitor = GetPrimaryMonitor();
        await LogAsync($"Monitor selected: {monitor.DisplayName} ({monitor.Width}x{monitor.Height}) id={monitor.Id}");
        if (!string.IsNullOrWhiteSpace(options.SourcePath))
        {
            await LogAsync($"Using source override: {options.SourcePath}");
        }

        var attempt = 0;
        while (options.MaxAttempts <= 0 || attempt < options.MaxAttempts)
        {
            attempt++;
            var attemptDir = Path.Combine(outputRoot, $"attempt_{attempt:000}");
            Directory.CreateDirectory(attemptDir);
            var exportPath = Path.Combine(outputRoot, $"export_attempt_{attempt:000}.mp4");
            string sourcePath;
            double sourceDurationSeconds;

            try
            {
                if (!string.IsNullOrWhiteSpace(options.SourcePath))
                {
                    sourcePath = Path.GetFullPath(options.SourcePath);
                    if (!File.Exists(sourcePath))
                    {
                        await LogAsync($"Attempt {attempt}: source file does not exist: {sourcePath}");
                        return 1;
                    }

                    sourceDurationSeconds = options.Seconds;
                    await LogAsync($"Attempt {attempt}: exporting existing source {sourcePath}");
                    await LogMediaAudioInfoAsync(sourcePath, $"Attempt {attempt}: source", LogAsync);
                }
                else
                {
                    await LogAsync($"Attempt {attempt}: recording {options.Seconds}s...");
                    var recorder = new NativeScreenRecordingService();
                    var request = new RecordingRequest(
                        Monitor: monitor,
                        OutputDirectory: attemptDir,
                        FrameRate: options.FrameRate,
                        IncludeSystemAudio: options.KeepAudio,
                        SystemAudioDevice: null);

                    await recorder.StartAsync(request);
                    await Task.Delay(TimeSpan.FromSeconds(options.Seconds));
                    var session = await recorder.StopAsync();

                    if (session is null || !File.Exists(session.OutputPath))
                    {
                        await LogAsync($"Attempt {attempt}: recording output was not produced.");
                        continue;
                    }

                    var recordedSizeMb = new FileInfo(session.OutputPath).Length / (1024d * 1024d);
                    sourcePath = session.OutputPath;
                    sourceDurationSeconds = options.Seconds;
                    await LogAsync($"Attempt {attempt}: recording created {sourcePath} ({recordedSizeMb:0.00} MB)");
                    await LogMediaAudioInfoAsync(sourcePath, $"Attempt {attempt}: recorded", LogAsync);
                }

                var exporter = new NativeVideoPostProcessor();
                try
                {
                    var sourceFile = await StorageFile.GetFileFromPathAsync(sourcePath);
                    var props = await sourceFile.Properties.GetVideoPropertiesAsync();
                    await LogAsync($"Attempt {attempt}: source props Width={props.Width} Height={props.Height} Duration={props.Duration}");
                }
                catch (Exception metaEx)
                {
                    await LogAsync($"Attempt {attempt}: source props unavailable: {DescribeException(metaEx)}");
                }

                var exportOptions = new ExportOptions(
                    SourcePath: sourcePath,
                    OutputPath: exportPath,
                    FrameRate: options.FrameRate,
                    EnableCrop: options.EnableCrop,
                    CropX: options.CropX,
                    CropY: options.CropY,
                    CropWidth: options.CropWidth > 0 ? options.CropWidth : monitor.Width,
                    CropHeight: options.CropHeight > 0 ? options.CropHeight : monitor.Height,
                    EnableResize: options.EnableResize,
                    ResizeWidth: options.ResizeWidth > 0 ? options.ResizeWidth : monitor.Width,
                    ResizeHeight: options.ResizeHeight > 0 ? options.ResizeHeight : monitor.Height,
                    KeepAudio: options.KeepAudio,
                    Preset: ExportPreset.Balanced,
                    CustomTargetMb: 24,
                    DeletedSegments: Array.Empty<FrameRange>(),
                    SourceDurationSeconds: sourceDurationSeconds);

                await LogAsync($"Attempt {attempt}: options Crop={options.EnableCrop} ({options.CropX},{options.CropY},{options.CropWidth}x{options.CropHeight}) Resize={options.EnableResize} ({options.ResizeWidth}x{options.ResizeHeight}) KeepAudio={options.KeepAudio}");

                var progress = new Progress<ExportProgress>(p =>
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Attempt {attempt} export: {p.Stage} {p.Percent:0}%");
                });

                await LogAsync($"Attempt {attempt}: exporting to {exportPath}");
                var result = await exporter.ExportAsync(exportOptions, progress);
                if (!File.Exists(result.OutputPath))
                {
                    await LogAsync($"Attempt {attempt}: exporter returned success but file not found.");
                    continue;
                }

                var exportedSizeMb = new FileInfo(result.OutputPath).Length / (1024d * 1024d);
                await LogMediaAudioInfoAsync(result.OutputPath, $"Attempt {attempt}: output", LogAsync);
                await LogAsync($"Attempt {attempt}: SUCCESS output={result.OutputPath} size={exportedSizeMb:0.00} MB");
                return 0;
            }
            catch (Exception ex)
            {
                await LogAsync($"Attempt {attempt}: FAILED {DescribeException(ex)}");
            }
        }

        await LogAsync("No successful export after configured attempts.");
        return 1;
    }

    private static DisplayMonitor GetPrimaryMonitor()
    {
        var screen = Forms.Screen.AllScreens.FirstOrDefault(s => s.Primary) ?? Forms.Screen.AllScreens.First();
        var bounds = screen.Bounds;
        return new DisplayMonitor(
            Id: screen.DeviceName,
            DisplayName: screen.DeviceName,
            Left: bounds.Left,
            Top: bounds.Top,
            Width: bounds.Width,
            Height: bounds.Height);
    }

    private static string DescribeException(Exception ex)
    {
        static string One(Exception e) => $"{e.GetType().Name} (0x{e.HResult:X8}): {e.Message}";
        return ex.InnerException is null ? One(ex) : $"{One(ex)} | Inner: {DescribeException(ex.InnerException)}";
    }

    private static async Task LogMediaAudioInfoAsync(string mediaPath, string label, Func<string, Task> log)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(mediaPath);
            var clip = await MediaClip.CreateFromFileAsync(file);
            var trackCount = clip.EmbeddedAudioTracks.Count;
            var selectedIndex = clip.SelectedEmbeddedAudioTrackIndex;
            await log($"{label} audio tracks={trackCount} selectedIndex={selectedIndex}");
        }
        catch (Exception ex)
        {
            await log($"{label} audio info unavailable: {DescribeException(ex)}");
        }
    }

    private static RunnerOptions ParseOptions(string[] args)
    {
        var options = new RunnerOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = arg[2..].ToLowerInvariant();
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            var value = hasValue ? args[++i] : string.Empty;

            switch (key)
            {
                case "seconds":
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                    {
                        options.Seconds = Math.Clamp(seconds, 1, 60);
                    }
                    break;
                case "fps":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fps))
                    {
                        options.FrameRate = Math.Clamp(fps, 10, 60);
                    }
                    break;
                case "keep-audio":
                    options.KeepAudio = true;
                    break;
                case "no-audio":
                    options.KeepAudio = false;
                    break;
                case "out":
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        options.OutputDirectory = value;
                    }
                    break;
                case "max-attempts":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxAttempts))
                    {
                        options.MaxAttempts = maxAttempts;
                    }
                    break;
                case "source":
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        options.SourcePath = value;
                    }
                    break;
                case "crop":
                    options.EnableCrop = true;
                    break;
                case "crop-x":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cx))
                    {
                        options.CropX = cx;
                    }
                    break;
                case "crop-y":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cy))
                    {
                        options.CropY = cy;
                    }
                    break;
                case "crop-w":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cw))
                    {
                        options.CropWidth = cw;
                    }
                    break;
                case "crop-h":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ch))
                    {
                        options.CropHeight = ch;
                    }
                    break;
                case "resize":
                    options.EnableResize = true;
                    break;
                case "resize-w":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rw))
                    {
                        options.ResizeWidth = rw;
                    }
                    break;
                case "resize-h":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rh))
                    {
                        options.ResizeHeight = rh;
                    }
                    break;
            }
        }

        return options;
    }

    private sealed class RunnerOptions
    {
        public double Seconds { get; set; } = 4;
        public int FrameRate { get; set; } = 30;
        public bool KeepAudio { get; set; }
        public string OutputDirectory { get; set; } = ".\\debug-export";
        public int MaxAttempts { get; set; } = 0;
        public string SourcePath { get; set; } = string.Empty;
        public bool EnableCrop { get; set; }
        public int CropX { get; set; }
        public int CropY { get; set; }
        public int CropWidth { get; set; }
        public int CropHeight { get; set; }
        public bool EnableResize { get; set; }
        public int ResizeWidth { get; set; }
        public int ResizeHeight { get; set; }
    }
}
