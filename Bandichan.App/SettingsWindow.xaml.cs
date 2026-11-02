using Bandichan.Core;
using Bandichan.Infrastructure;
using ScreenRecorderLib;
using Forms = System.Windows.Forms;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Imaging = System.Drawing.Imaging;

namespace Bandichan.App;

public partial class SettingsWindow : Window
{
    private sealed class AudioDeviceChoice
    {
        public required string Label { get; init; }
        public string? DeviceName { get; init; }
        public bool Disabled { get; init; }
        public override string ToString() => Label;
    }

    public AppSettings Settings { get; }

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();

        Settings = new AppSettings
        {
            OutputDirectory = current.OutputDirectory,
            EncoderMode = current.EncoderMode,
            CaptureFrameRate = current.CaptureFrameRate,
            RecordSystemAudio = current.RecordSystemAudio,
            SystemAudioDevice = current.SystemAudioDevice,
        };

        OutputFolderText.Text = Settings.OutputDirectory;
        EncoderModeCombo.SelectedIndex = Settings.EncoderMode == EncoderSelectionMode.PreferH264Compatibility ? 1 : 0;
        FrameRateCombo.SelectedIndex = Settings.CaptureFrameRate >= 60 ? 1 : 0;
        RecordSystemAudioCheck.IsChecked = Settings.RecordSystemAudio;
        Loaded += OnLoaded;
        RecordSystemAudioCheck.Checked += (_, _) => UpdateAudioControlsState();
        RecordSystemAudioCheck.Unchecked += (_, _) => UpdateAudioControlsState();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await PopulateAudioDevicesAsync();
        UpdateAudioControlsState();
    }

    private async Task PopulateAudioDevicesAsync()
    {
        await Task.Yield();
        AudioDeviceCombo.Items.Clear();
        AudioDeviceCombo.Items.Add(new AudioDeviceChoice
        {
            Label = "Auto loopback (recommended)",
            DeviceName = null,
            Disabled = false,
        });

        var devices = Recorder.GetSystemAudioLoopbackDevices();
        foreach (var device in devices)
        {
            AudioDeviceCombo.Items.Add(new AudioDeviceChoice
            {
                Label = device.FriendlyName,
                DeviceName = device.DeviceName,
                Disabled = false,
            });
        }

        AudioDeviceCombo.SelectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(Settings.SystemAudioDevice))
        {
            for (var i = 0; i < AudioDeviceCombo.Items.Count; i++)
            {
                if (AudioDeviceCombo.Items[i] is AudioDeviceChoice choice
                    && string.Equals(choice.DeviceName, Settings.SystemAudioDevice, StringComparison.OrdinalIgnoreCase))
                {
                    AudioDeviceCombo.SelectedIndex = i;
                    break;
                }
            }
        }
    }

    private void UpdateAudioControlsState()
    {
        var enabled = RecordSystemAudioCheck.IsChecked == true;
        AudioDeviceCombo.IsEnabled = enabled;
    }

    private void BrowseOutputClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Select default output folder",
            InitialDirectory = Directory.Exists(OutputFolderText.Text)
                ? OutputFolderText.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            OutputFolderText.Text = dialog.SelectedPath;
        }
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(OutputFolderText.Text) || !Directory.Exists(OutputFolderText.Text))
        {
            System.Windows.MessageBox.Show("Choose a valid output folder.", "Bandichan", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Settings.OutputDirectory = OutputFolderText.Text;
        Settings.EncoderMode = EncoderModeCombo.SelectedIndex == 1
            ? EncoderSelectionMode.PreferH264Compatibility
            : EncoderSelectionMode.AutoPreferEfficiency;
        Settings.CaptureFrameRate = FrameRateCombo.SelectedIndex == 1 ? 60 : 30;
        Settings.RecordSystemAudio = RecordSystemAudioCheck.IsChecked == true;

        if (AudioDeviceCombo.SelectedItem is AudioDeviceChoice selectedChoice && !selectedChoice.Disabled)
        {
            Settings.SystemAudioDevice = selectedChoice.DeviceName;
        }
        else
        {
            Settings.SystemAudioDevice = null;
        }

        DialogResult = true;
    }

    private async void TestAudioClick(object sender, RoutedEventArgs e)
    {
        if (RecordSystemAudioCheck.IsChecked != true)
        {
            AudioTestStatusText.Text = "Audio test skipped: system audio recording is disabled.";
            return;
        }

        try
        {
            IsEnabled = false;
            AudioTestStatusText.Text = "Audio test: recording a short native loopback sample...";

            string? selectedDevice = null;
            if (AudioDeviceCombo.SelectedItem is AudioDeviceChoice choice && !choice.Disabled)
            {
                selectedDevice = choice.DeviceName;
            }

            var monitor = MonitorDiscovery.GetMonitors().FirstOrDefault();
            if (monitor is null)
            {
                AudioTestStatusText.Text = "Audio test failed: no monitor detected for temporary test capture.";
                return;
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "BandichanAudioTest");
            Directory.CreateDirectory(tempDir);

            var request = new RecordingRequest(
                monitor,
                tempDir,
                30,
                IncludeSystemAudio: true,
                SystemAudioDevice: selectedDevice);

            var testService = new NativeScreenRecordingService();
            var session = await testService.StartAsync(request);
            await Task.Delay(3000);
            await testService.StopAsync();

            if (!File.Exists(session.OutputPath) || new FileInfo(session.OutputPath).Length < 100_000)
            {
                AudioTestStatusText.Text = "Audio test failed: test capture output is too small.";
                return;
            }

            AudioTestStatusText.Text = "Audio test completed. If desktop sound was active during these 3 seconds, loopback is working.";

            try
            {
                File.Delete(session.OutputPath);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
        catch (Exception ex)
        {
            AudioTestStatusText.Text = $"Audio test failed: {ex.Message}";
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void TestVideoClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var monitors = MonitorDiscovery.GetMonitors();
            var monitor = monitors.FirstOrDefault(m => m.Id.Contains("DISPLAY1", StringComparison.OrdinalIgnoreCase))
                ?? monitors.FirstOrDefault();

            if (monitor is null)
            {
                VideoTestStatusText.Text = "Video test failed: no monitors detected.";
                return;
            }

            using var bitmap = new Drawing.Bitmap(monitor.Width, monitor.Height, Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(monitor.Left, monitor.Top, 0, 0, new Drawing.Size(monitor.Width, monitor.Height));
            }

            VideoPreviewImage.Source = ConvertToBitmapImage(bitmap);
            VideoTestStatusText.Text = $"Video test OK: previewing {monitor.DisplayName}.";
        }
        catch (Exception ex)
        {
            VideoTestStatusText.Text = $"Video test failed: {ex.Message}";
        }
    }

    private static BitmapImage ConvertToBitmapImage(Drawing.Bitmap bitmap)
    {
        using var memory = new MemoryStream();
        bitmap.Save(memory, Imaging.ImageFormat.Png);
        memory.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = memory;
        image.EndInit();
        image.Freeze();
        return image;
    }


    private void CancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
