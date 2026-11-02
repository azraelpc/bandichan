using Bandichan.Core;
using Bandichan.Infrastructure;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPoint = System.Windows.Point;

namespace Bandichan.App;

public partial class EditorWindow : Window
{
    private readonly string _sourcePath;
    private readonly IVideoPostProcessor _postProcessor;
    private readonly List<FrameRange> _deletedSegments = [];
    private readonly DispatcherTimer _playTimer = new();
    private int _frameIndex;
    private int? _markA;
    private int? _markB;
    private int _detectedFrameRate = 30;
    private double _detectedFrameRateExact = 30;
    private int _estimatedFrameCount = 1;
    private int _videoWidth = 1920;
    private int _videoHeight = 1080;
    private double _sourceDurationSeconds;
    private bool _previewReady;
    private bool _suppressSliderEvent;
    private bool _isPlaying;
    private bool _isResizeSyncing;
    private double _resizeAspectRatio = 16d / 9d;
    private bool _isCropDragging;
    private WpfPoint _cropDragStart;
    private WpfPoint _cropDragCurrent;
    private System.Windows.Shapes.Rectangle? _cropSelectionRect;

    public EditorWindow(string sourcePath, EncoderSelectionMode encoderMode)
    {
        _sourcePath = sourcePath;
        _postProcessor = new NativeVideoPostProcessor();
        InitializeComponent();
        SourcePathText.Text = $"Source: {sourcePath}";
        KeyDown += OnEditorKeyDown;
        Loaded += OnLoaded;
        _playTimer.Tick += PlayTimerTick;
        PresetCombo.SelectionChanged += (_, _) =>
        {
            UpdatePresetUiState();
            UpdateEstimatedSizeText();
        };
        CustomTargetMb.TextChanged += (_, _) => UpdateEstimatedSizeText();
        ResizeW.TextChanged += ResizeWTextChanged;
        ResizeH.TextChanged += ResizeHTextChanged;
        CropW.TextChanged += CropAspectSourceChanged;
        CropH.TextChanged += CropAspectSourceChanged;
        UpdateSelectionText();
        UpdatePresetUiState();
        UpdateEstimatedSizeText();
        ExportProgressBar.Value = 0;
        ExportStatusText.Text = "Ready.";
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await InitializePreviewAsync();
    }

    private void PlayPauseClick(object sender, RoutedEventArgs e)
    {
        TogglePlayPause();
    }

    private void TogglePlayPause()
    {
        if (!_previewReady)
        {
            return;
        }

        if (_isPlaying)
        {
            StopPlayback();
            return;
        }

        StartPlayback();
    }

    private void StartPlayback()
    {
        _isPlaying = true;
        PlayPauseButton.Content = "Pause";
        _playTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(10, 1000d / Math.Max(1, _detectedFrameRateExact)));
        PreviewPlayer.Play();
        _playTimer.Start();
    }

    private void StopPlayback()
    {
        _isPlaying = false;
        PlayPauseButton.Content = "Play";
        PreviewPlayer.Pause();
        _playTimer.Stop();
    }

    private void PlayTimerTick(object? sender, EventArgs e)
    {
        if (!_previewReady)
        {
            return;
        }

        var currentFrame = (int)Math.Round(PreviewPlayer.Position.TotalSeconds * Math.Max(1d, _detectedFrameRateExact));
        _frameIndex = Math.Clamp(currentFrame, 0, Math.Max(0, _estimatedFrameCount - 1));

        if (_frameIndex >= Math.Max(0, _estimatedFrameCount - 1))
        {
            StopPlayback();
            return;
        }

        SyncSliderWithCurrentFrame();
        UpdateSelectionText();
        RedrawTimeline();
        RedrawCropOverlay();
    }

    private void PrevFrameClick(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        _frameIndex = Math.Max(0, _frameIndex - 1);
        SyncSliderWithCurrentFrame();
        UpdateSelectionText();
        _ = RenderCurrentFrameAsync();
    }

    private void NextFrameClick(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        _frameIndex = Math.Min(Math.Max(0, _estimatedFrameCount - 1), _frameIndex + 1);
        SyncSliderWithCurrentFrame();
        UpdateSelectionText();
        _ = RenderCurrentFrameAsync();
    }

    private void SetInClick(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        _markA = _frameIndex;
        UpdateSelectionText();
        RedrawTimeline();
    }

    private void SetOutClick(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        _markB = _frameIndex;
        UpdateSelectionText();
        RedrawTimeline();
    }

    private void DeleteSegmentClick(object sender, RoutedEventArgs e)
    {
        if (_markA is null || _markB is null)
        {
            System.Windows.MessageBox.Show("Set A and B before deleting.", "Bandichan", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var range = new FrameRange(_markA.Value, _markB.Value);
        _deletedSegments.Add(range);
        _markA = null;
        _markB = null;
        UpdateSelectionText();
        RedrawTimeline();
        UpdateEstimatedSizeText();
    }

    private void UndoLastCutClick(object sender, RoutedEventArgs e)
    {
        if (_deletedSegments.Count == 0)
        {
            return;
        }

        _deletedSegments.RemoveAt(_deletedSegments.Count - 1);
        UpdateSelectionText();
        RedrawTimeline();
        UpdateEstimatedSizeText();
    }

    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        StopPlayback();

        if (!TryBuildExportOptions(out var options, out var validationError))
        {
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                System.Windows.MessageBox.Show(validationError, "Bandichan", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }

        await ExecuteExportAsync(options);
    }

    private async Task ExecuteExportAsync(ExportOptions options)
    {
        var shouldRestorePreview = _previewReady;
        ReleasePreviewForExport();

        try
        {
            IsEnabled = false;
            ExportProgressBar.Value = 0;
            var estimatedSize = EstimateTargetSizeMb(options);
            ExportStatusText.Text = estimatedSize > 0
                ? $"Preparing export... Estimated final size: {estimatedSize:0.##} MB"
                : "Preparing export...";

            var exportProgress = new Progress<ExportProgress>(p =>
            {
                ExportProgressBar.Value = Math.Clamp(p.Percent, 0, 100);
                ExportStatusText.Text = $"{p.Stage} {p.Percent:0}% | Estimated final size: {p.EstimatedFinalSizeMb:0.##} MB";
            });

            var result = await _postProcessor.ExportAsync(options, exportProgress);
            ExportProgressBar.Value = 100;
            ExportStatusText.Text = $"Export completed. Final size: {result.ActualMb:0.##} MB";

            var completedWindow = new ExportCompletedWindow(result.OutputPath, result.ActualMb)
            {
                Owner = this,
            };
            completedWindow.ShowDialog();

            if (completedWindow.OpenFolder)
            {
                var folder = Path.GetDirectoryName(result.OutputPath);
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = folder,
                        UseShellExecute = true,
                    });
                }
            }

            _markA = null;
            _markB = null;
            UpdateSelectionText();
            RedrawTimeline();

            // Keep editor open so user can export again with different settings.
            ExportStatusText.Text = $"Export completed. Ready for another export. Final size: {result.ActualMb:0.##} MB";
        }
        catch (Exception ex)
        {
            var details = ex.InnerException is null ? ex.Message : $"{ex.Message}\nCause: {ex.InnerException.Message}";
            System.Windows.MessageBox.Show($"Export failed. {details}", "Bandichan", MessageBoxButton.OK, MessageBoxImage.Error);
            ExportStatusText.Text = "Export failed.";
        }
        finally
        {
            IsEnabled = true;
            if (shouldRestorePreview)
            {
                await RestorePreviewAfterExportAsync();
            }
        }
    }

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        Close();
    }

    private void OnEditorKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (IsTextInputFocused())
        {
            return;
        }

        if (e.Key == Key.Left)
        {
            PrevFrameClick(sender, new RoutedEventArgs());
        }
        else if (e.Key == Key.Right)
        {
            NextFrameClick(sender, new RoutedEventArgs());
        }
        else if (e.Key == Key.Space)
        {
            TogglePlayPause();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _markA is not null && _markB is not null)
        {
            DeleteSegmentClick(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void UpdateSelectionText()
    {
        SelectionText.Text = $"Frame: {_frameIndex}/{Math.Max(0, _estimatedFrameCount - 1)} | A: {_markA?.ToString() ?? "-"} | B: {_markB?.ToString() ?? "-"} | Deleted segments: {_deletedSegments.Count}";
    }

    private bool TryBuildExportOptions(out ExportOptions options, out string error)
    {
        options = default!;
        error = string.Empty;

        if (!TryParseInt(CropX.Text, out var cropX) ||
            !TryParseInt(CropY.Text, out var cropY) ||
            !TryParseInt(CropW.Text, out var cropW) ||
            !TryParseInt(CropH.Text, out var cropH) ||
            !TryParseInt(ResizeW.Text, out var resizeW) ||
            !TryParseInt(ResizeH.Text, out var resizeH))
        {
            error = "Crop/resize values must be integers.";
            return false;
        }

        if (cropW <= 0 || cropH <= 0 || resizeW <= 0 || resizeH <= 0)
        {
            error = "Width and height must be greater than zero.";
            return false;
        }

        var saveDialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export video",
            Filter = "MP4 video (*.mp4)|*.mp4",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            FileName = $"{System.IO.Path.GetFileNameWithoutExtension(_sourcePath)}_edited.mp4",
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (saveDialog.ShowDialog(this) != true)
        {
            error = string.Empty;
            return false;
        }

        var preset = PresetCombo.SelectedIndex switch
        {
            0 => ExportPreset.Balanced,
            1 => ExportPreset.EightMb,
            _ => ExportPreset.Custom,
        };

        var customMb = 24d;
        if (!double.TryParse(CustomTargetMb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out customMb))
        {
            if (!double.TryParse(CustomTargetMb.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out customMb))
            {
                customMb = 24d;
            }
        }

        options = new ExportOptions(
            SourcePath: _sourcePath,
            OutputPath: saveDialog.FileName,
            FrameRate: _detectedFrameRate,
            EnableCrop: CropEnabled.IsChecked == true,
            CropX: cropX,
            CropY: cropY,
            CropWidth: cropW,
            CropHeight: cropH,
            EnableResize: ResizeEnabled.IsChecked == true,
            ResizeWidth: resizeW,
            ResizeHeight: resizeH,
            KeepAudio: AudioEnabled.IsChecked == true,
            Preset: preset,
            CustomTargetMb: customMb,
            DeletedSegments: _deletedSegments.ToArray(),
            SourceDurationSeconds: _sourceDurationSeconds > 0
                ? _sourceDurationSeconds
                : _estimatedFrameCount / Math.Max(1d, _detectedFrameRateExact));

        return true;
    }

    private void ReleasePreviewForExport()
    {
        StopPlayback();
        try
        {
            PreviewPlayer.Stop();
            PreviewPlayer.Source = null;
        }
        catch
        {
            // Best effort release.
        }

        _previewReady = false;
    }

    private async Task RestorePreviewAfterExportAsync()
    {
        await Task.Delay(60);
        await InitializePreviewAsync();
    }

    private static bool IsTextInputFocused()
    {
        return Keyboard.FocusedElement is System.Windows.Controls.TextBox;
    }

    private static bool TryParseInt(string input, out int value)
    {
        return int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            || int.TryParse(input, NumberStyles.Integer, CultureInfo.CurrentCulture, out value);
    }

    private async Task InitializePreviewAsync()
    {
        try
        {
            if (!File.Exists(_sourcePath))
            {
                throw new FileNotFoundException("Source video not found.", _sourcePath);
            }

            var openedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            RoutedEventHandler onOpened = null!;
            EventHandler<ExceptionRoutedEventArgs> onFailed = null!;

            onOpened = (_, _) => openedTcs.TrySetResult(true);
            onFailed = (_, e) => openedTcs.TrySetException(new InvalidOperationException(e.ErrorException?.Message ?? "Media open failed."));

            PreviewPlayer.MediaOpened += onOpened;
            PreviewPlayer.MediaFailed += onFailed;

            try
            {
                PreviewPlayer.Source = new Uri(_sourcePath);
                PreviewPlayer.LoadedBehavior = MediaState.Manual;
                PreviewPlayer.UnloadedBehavior = MediaState.Manual;
                PreviewPlayer.Play();

                var completed = await Task.WhenAny(openedTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
                if (completed != openedTcs.Task)
                {
                    throw new InvalidOperationException("Timed out opening video for preview.");
                }

                await openedTcs.Task;

                // Prime decoder so the first still frame is visible without pressing Play.
                await Task.Delay(80);
                PreviewPlayer.Pause();
                PreviewPlayer.Position = TimeSpan.Zero;
            }
            finally
            {
                PreviewPlayer.MediaOpened -= onOpened;
                PreviewPlayer.MediaFailed -= onFailed;
            }

            var duration = PreviewPlayer.NaturalDuration.HasTimeSpan
                ? PreviewPlayer.NaturalDuration.TimeSpan.TotalSeconds
                : 0;
            var videoWidth = PreviewPlayer.NaturalVideoWidth;
            var videoHeight = PreviewPlayer.NaturalVideoHeight;
            var fps = 30d;

            _detectedFrameRateExact = Math.Clamp(fps <= 0 ? 30d : fps, 1d, 120d);
            _detectedFrameRate = Math.Max(1, (int)Math.Round(_detectedFrameRateExact));
            _sourceDurationSeconds = Math.Max(0.1, duration);
            _estimatedFrameCount = Math.Max(1, (int)Math.Ceiling(Math.Max(0.1, duration) * _detectedFrameRateExact));
            _videoWidth = videoWidth > 0 ? videoWidth : _videoWidth;
            _videoHeight = videoHeight > 0 ? videoHeight : _videoHeight;
            _resizeAspectRatio = _videoWidth / (double)Math.Max(1, _videoHeight);

            _suppressSliderEvent = true;
            FrameSlider.Minimum = 0;
            FrameSlider.Maximum = Math.Max(0, _estimatedFrameCount - 1);
            FrameSlider.Value = 0;
            _suppressSliderEvent = false;

            _previewReady = true;
            _frameIndex = 0;
            await RenderCurrentFrameAsync();
            UpdateSelectionText();
            RedrawTimeline();
            RedrawCropOverlay();
            UpdateEstimatedSizeText();
        }
        catch (Exception ex)
        {
            _previewReady = false;
            PreviewInfoText.Text = $"Preview init failed: {ex.Message}";
        }
    }

    private async Task RenderCurrentFrameAsync()
    {
        if (!_previewReady)
        {
            return;
        }

        try
        {
            var timeSeconds = _frameIndex / Math.Max(1d, _detectedFrameRateExact);
            var targetTime = TimeSpan.FromSeconds(timeSeconds);
            PreviewPlayer.Pause();
            PreviewPlayer.Position = targetTime;
            // WPF MediaElement may update decoded frame slightly after setting Position.
            await Task.Delay(35);

            await Dispatcher.InvokeAsync(() =>
            {
                var seconds = _frameIndex / Math.Max(1d, _detectedFrameRateExact);
                PreviewInfoText.Text = $"Frame {_frameIndex} / {Math.Max(0, _estimatedFrameCount - 1)} | t={seconds:0.###}s | fps~{_detectedFrameRate}";
            });

            RedrawTimeline();
            RedrawCropOverlay();
        }
        catch (Exception ex)
        {
            PreviewInfoText.Text = $"Preview render failed: {ex.Message}";
        }

        await Task.CompletedTask;
    }

    private async void FrameSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvent || !_previewReady)
        {
            return;
        }

        StopPlayback();
        _frameIndex = Math.Clamp((int)Math.Round(FrameSlider.Value), 0, Math.Max(0, _estimatedFrameCount - 1));
        UpdateSelectionText();
        RedrawTimeline();
        await RenderCurrentFrameAsync();
    }

    private void SyncSliderWithCurrentFrame()
    {
        _suppressSliderEvent = true;
        FrameSlider.Value = _frameIndex;
        _suppressSliderEvent = false;
    }

    private void TimelineCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RedrawTimeline();
    }

    private void RedrawTimeline()
    {
        if (TimelineCanvas is null)
        {
            return;
        }

        var width = TimelineCanvas.ActualWidth;
        var height = TimelineCanvas.ActualHeight;
        if (width <= 1 || height <= 1)
        {
            return;
        }

        TimelineCanvas.Children.Clear();

        var maxFrame = Math.Max(1, _estimatedFrameCount - 1);

        var background = new System.Windows.Shapes.Rectangle
        {
            Width = width,
            Height = height,
            Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30)),
        };
        TimelineCanvas.Children.Add(background);

        DrawTimelineTicks(width, height, maxFrame);

        foreach (var segment in _deletedSegments)
        {
            var start = Math.Clamp(segment.NormalizedStart, 0, maxFrame);
            var end = Math.Clamp(segment.NormalizedEnd, 0, maxFrame);

            var x1 = start / (double)maxFrame * width;
            var x2 = end / (double)maxFrame * width;
            var segmentRect = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(2, x2 - x1),
                Height = height,
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(180, 200, 40, 40)),
            };
            Canvas.SetLeft(segmentRect, x1);
            Canvas.SetTop(segmentRect, 0);
            TimelineCanvas.Children.Add(segmentRect);
        }

        if (_markA is not null && _markB is not null)
        {
            var selectedStart = Math.Clamp(Math.Min(_markA.Value, _markB.Value), 0, maxFrame);
            var selectedEnd = Math.Clamp(Math.Max(_markA.Value, _markB.Value), 0, maxFrame);
            var sx1 = selectedStart / (double)maxFrame * width;
            var sx2 = selectedEnd / (double)maxFrame * width;
            var selectedRect = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(2, sx2 - sx1),
                Height = height,
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(210, 40, 130, 255)),
            };
            Canvas.SetLeft(selectedRect, sx1);
            Canvas.SetTop(selectedRect, 0);
            TimelineCanvas.Children.Add(selectedRect);
        }

        DrawMarkerLine(_markA, width, height, maxFrame, System.Windows.Media.Color.FromRgb(255, 196, 64));
        DrawMarkerLine(_markB, width, height, maxFrame, System.Windows.Media.Color.FromRgb(255, 196, 64));
        DrawMarkerLine(_frameIndex, width, height, maxFrame, System.Windows.Media.Color.FromRgb(70, 170, 255));
    }

    private void DrawTimelineTicks(double width, double height, int maxFrame)
    {
        if (maxFrame <= 0)
        {
            return;
        }

        var totalSeconds = _estimatedFrameCount / Math.Max(1d, _detectedFrameRateExact);
        if (totalSeconds <= 0)
        {
            return;
        }

        // Draw major ticks every second and minor ticks at subdivisions.
        var secondsStep = totalSeconds > 120 ? 10 : totalSeconds > 60 ? 5 : 1;
        var minorDivisions = 5;

        for (var t = 0d; t <= totalSeconds; t += secondsStep / (double)minorDivisions)
        {
            var isMajor = Math.Abs((t / secondsStep) - Math.Round(t / secondsStep)) < 0.0001;
            var frame = Math.Clamp((int)Math.Round(t * _detectedFrameRateExact), 0, maxFrame);
            var x = frame / (double)maxFrame * width;

            var tick = new System.Windows.Shapes.Line
            {
                X1 = x,
                X2 = x,
                Y1 = isMajor ? 0 : height * 0.55,
                Y2 = height,
                Stroke = new SolidColorBrush(isMajor
                    ? System.Windows.Media.Color.FromArgb(120, 180, 180, 180)
                    : System.Windows.Media.Color.FromArgb(70, 120, 120, 120)),
                StrokeThickness = 1,
            };
            TimelineCanvas.Children.Add(tick);
        }
    }

    private void DrawMarkerLine(int? frame, double width, double height, int maxFrame, System.Windows.Media.Color color)
    {
        if (frame is null)
        {
            return;
        }

        var clampedFrame = Math.Clamp(frame.Value, 0, maxFrame);
        var x = clampedFrame / (double)maxFrame * width;

        var line = new System.Windows.Shapes.Line
        {
            X1 = x,
            X2 = x,
            Y1 = 0,
            Y2 = height,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 2,
        };
        TimelineCanvas.Children.Add(line);
    }

    private void ResizeWTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isResizeSyncing || LockAspectCheck.IsChecked != true)
        {
            return;
        }

        if (!int.TryParse(ResizeW.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) || width <= 0)
        {
            return;
        }

        _isResizeSyncing = true;
        try
        {
            var height = Math.Max(1, (int)Math.Round(width / Math.Max(0.0001, _resizeAspectRatio)));
            ResizeH.Text = height.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _isResizeSyncing = false;
        }
    }

    private void ResizeHTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isResizeSyncing || LockAspectCheck.IsChecked != true)
        {
            return;
        }

        if (!int.TryParse(ResizeH.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) || height <= 0)
        {
            return;
        }

        _isResizeSyncing = true;
        try
        {
            var width = Math.Max(1, (int)Math.Round(height * _resizeAspectRatio));
            ResizeW.Text = width.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _isResizeSyncing = false;
        }
    }

    private void CropAspectSourceChanged(object sender, TextChangedEventArgs e)
    {
        if (int.TryParse(CropW.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cw)
            && int.TryParse(CropH.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ch)
            && cw > 0 && ch > 0)
        {
            _resizeAspectRatio = cw / (double)ch;
        }

        RedrawCropOverlay();
    }

    private void CropOverlaySizeChanged(object sender, SizeChangedEventArgs e)
    {
        RedrawCropOverlay();
    }

    private void CropOverlayMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_previewReady)
        {
            return;
        }

        _isCropDragging = true;
        _cropDragStart = e.GetPosition(CropOverlay);
        _cropDragCurrent = _cropDragStart;
        CropOverlay.CaptureMouse();
        RedrawCropOverlay();
    }

    private void CropOverlayMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (!_isCropDragging)
        {
            return;
        }

        _cropDragCurrent = e.GetPosition(CropOverlay);
        RedrawCropOverlay();
    }

    private void CropOverlayMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isCropDragging)
        {
            return;
        }

        _isCropDragging = false;
        CropOverlay.ReleaseMouseCapture();
        _cropDragCurrent = e.GetPosition(CropOverlay);

        if (!TryMapOverlayRectToVideo(_cropDragStart, _cropDragCurrent, out var cropX, out var cropY, out var cropW, out var cropH))
        {
            RedrawCropOverlay();
            return;
        }

        CropEnabled.IsChecked = true;
        CropX.Text = cropX.ToString(CultureInfo.InvariantCulture);
        CropY.Text = cropY.ToString(CultureInfo.InvariantCulture);
        CropW.Text = cropW.ToString(CultureInfo.InvariantCulture);
        CropH.Text = cropH.ToString(CultureInfo.InvariantCulture);
        _resizeAspectRatio = cropW / (double)Math.Max(1, cropH);

        RedrawCropOverlay();
    }

    private void RedrawCropOverlay()
    {
        if (CropOverlay is null)
        {
            return;
        }

        CropOverlay.Children.Clear();

        Rect overlayRect;
        if (_isCropDragging)
        {
            overlayRect = CreateOverlayRect(_cropDragStart, _cropDragCurrent);
        }
        else if (CropEnabled.IsChecked == true && TryGetOverlayRectFromCropInputs(out var fromInputs))
        {
            overlayRect = fromInputs;
        }
        else
        {
            return;
        }

        _cropSelectionRect = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(1, overlayRect.Width),
            Height = Math.Max(1, overlayRect.Height),
            Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 80, 80)),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(55, 255, 30, 30)),
        };
        Canvas.SetLeft(_cropSelectionRect, overlayRect.Left);
        Canvas.SetTop(_cropSelectionRect, overlayRect.Top);
        CropOverlay.Children.Add(_cropSelectionRect);
    }

    private bool TryMapOverlayRectToVideo(WpfPoint a, WpfPoint b, out int cropX, out int cropY, out int cropW, out int cropH)
    {
        cropX = 0;
        cropY = 0;
        cropW = 0;
        cropH = 0;

        if (!TryGetPreviewTransform(out var offsetX, out var offsetY, out var scale))
        {
            return false;
        }

        var rect = CreateOverlayRect(a, b);
        if (rect.Width < 2 || rect.Height < 2)
        {
            return false;
        }

        var x1 = (rect.Left - offsetX) / scale;
        var y1 = (rect.Top - offsetY) / scale;
        var x2 = (rect.Right - offsetX) / scale;
        var y2 = (rect.Bottom - offsetY) / scale;

        x1 = Math.Clamp(x1, 0, _videoWidth);
        x2 = Math.Clamp(x2, 0, _videoWidth);
        y1 = Math.Clamp(y1, 0, _videoHeight);
        y2 = Math.Clamp(y2, 0, _videoHeight);

        cropX = (int)Math.Round(Math.Min(x1, x2));
        cropY = (int)Math.Round(Math.Min(y1, y2));
        cropW = Math.Max(1, (int)Math.Round(Math.Abs(x2 - x1)));
        cropH = Math.Max(1, (int)Math.Round(Math.Abs(y2 - y1)));

        return true;
    }

    private bool TryGetOverlayRectFromCropInputs(out Rect rect)
    {
        rect = Rect.Empty;

        if (!int.TryParse(CropX.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cropX)
            || !int.TryParse(CropY.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cropY)
            || !int.TryParse(CropW.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cropW)
            || !int.TryParse(CropH.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cropH)
            || cropW <= 0 || cropH <= 0)
        {
            return false;
        }

        if (!TryGetPreviewTransform(out var offsetX, out var offsetY, out var scale))
        {
            return false;
        }

        rect = new Rect(
            offsetX + cropX * scale,
            offsetY + cropY * scale,
            cropW * scale,
            cropH * scale);

        return true;
    }

    private bool TryGetPreviewTransform(out double offsetX, out double offsetY, out double scale)
    {
        offsetX = 0;
        offsetY = 0;
        scale = 1;

        var hostW = CropOverlay.ActualWidth;
        var hostH = CropOverlay.ActualHeight;
        if (hostW <= 1 || hostH <= 1 || _videoWidth <= 0 || _videoHeight <= 0)
        {
            return false;
        }

        var sx = hostW / _videoWidth;
        var sy = hostH / _videoHeight;
        scale = Math.Min(sx, sy);
        var displayedW = _videoWidth * scale;
        var displayedH = _videoHeight * scale;
        offsetX = (hostW - displayedW) / 2d;
        offsetY = (hostH - displayedH) / 2d;
        return true;
    }

    private static Rect CreateOverlayRect(WpfPoint a, WpfPoint b)
    {
        var left = Math.Min(a.X, b.X);
        var top = Math.Min(a.Y, b.Y);
        var right = Math.Max(a.X, b.X);
        var bottom = Math.Max(a.Y, b.Y);
        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private void UpdatePresetUiState()
    {
        var isCustom = PresetCombo.SelectedIndex == 2;
        CustomTargetMb.IsEnabled = isCustom;
    }

    private void UpdateEstimatedSizeText()
    {
        var target = GetSelectedTargetSizeMb();
        EstimatedSizeText.Text = $"Estimated final size: ~{target:0.##} MB";
    }

    private double GetSelectedTargetSizeMb()
    {
        if (PresetCombo.SelectedIndex == 0)
        {
            return EstimateBalancedSizeMb();
        }

        return PresetCombo.SelectedIndex switch
        {
            1 => 8.0,
            _ => ParseCustomTargetMb(),
        };
    }

    private double EstimateBalancedSizeMb()
    {
        var duration = Math.Max(1, _estimatedFrameCount / Math.Max(1d, _detectedFrameRateExact));
        var (width, height) = DetermineOutputResolution();
        var (videoKbps, audioKbps) = EstimateBalancedBitrates(width, height, _detectedFrameRate, AudioEnabled.IsChecked == true);
        var totalKbps = videoKbps + audioKbps;
        return Math.Max(1, (totalKbps * duration * 1000d) / 8d / 1024d / 1024d);
    }

    private (int Width, int Height) DetermineOutputResolution()
    {
        if (ResizeEnabled.IsChecked == true
            && TryParseInt(ResizeW.Text, out var rw)
            && TryParseInt(ResizeH.Text, out var rh)
            && rw > 0 && rh > 0)
        {
            return (rw, rh);
        }

        if (CropEnabled.IsChecked == true
            && TryParseInt(CropW.Text, out var cw)
            && TryParseInt(CropH.Text, out var ch)
            && cw > 0 && ch > 0)
        {
            return (cw, ch);
        }

        return (_videoWidth, _videoHeight);
    }

    private static (int VideoKbps, int AudioKbps) EstimateBalancedBitrates(int width, int height, int frameRate, bool keepAudio)
    {
        var pixelFactor = Math.Max(0.2, (width * (double)height) / (1280d * 720d));
        var fpsFactor = Math.Max(0.5, frameRate / 30d);
        var video = (int)Math.Round(2800d * Math.Pow(pixelFactor, 0.65) * Math.Sqrt(fpsFactor));
        video = Math.Clamp(video, 2200, 9000);

        var audio = keepAudio ? 128 : 0;
        return (video, audio);
    }

    private double ParseCustomTargetMb()
    {
        if (double.TryParse(CustomTargetMb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var customMb) && customMb > 0)
        {
            return customMb;
        }

        if (double.TryParse(CustomTargetMb.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out customMb) && customMb > 0)
        {
            return customMb;
        }

        return 24;
    }

    private static double EstimateTargetSizeMb(ExportOptions options)
    {
        if (options.Preset == ExportPreset.Balanced)
        {
            return 0;
        }

        return options.Preset switch
        {
            ExportPreset.EightMb => 8.0,
            _ => Math.Max(1, options.CustomTargetMb),
        };
    }

}
