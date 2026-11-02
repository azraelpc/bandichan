using Bandichan.Core;
using Bandichan.Infrastructure;
using System.IO;
using Forms = System.Windows.Forms;
using System.Windows;
using System.Threading;
using System.Runtime.InteropServices;

namespace Bandichan.App;

public partial class App : System.Windows.Application
{
	private const string SingleInstanceMutexName = "Global\\Bandichan.App.SingleInstance";
	private static Mutex? _singleInstanceMutex;
	private static bool _ownsSingleInstanceMutex;
	private const string StartupShortcutName = "Bandichan.lnk";
	private MainWindow? _mainWindow;
	private EditorWindow? _editorWindow;
	private Forms.NotifyIcon? _notifyIcon;
	private Forms.ToolStripMenuItem? _toggleRecordingMenuItem;
	private Forms.ToolStripMenuItem? _runWithWindowsMenuItem;
	private bool _isExiting;
	private System.Drawing.Icon? _idleIcon;
	private System.Drawing.Icon? _recordingIcon;
	private System.Drawing.Icon? _processingIcon;
	private System.Drawing.Icon? _editingIcon;
	private AppSettings _settings;
	private IRecordingService _recordingService;
	private AppState _state = AppState.Idle;
	private string? _lastVideoCodec;
	private bool _isRecordingTransitionInProgress;

	public App()
	{
		_settings = SettingsStore.Load();
		if (_settings.CaptureFrameRate <= 0)
		{
			_settings.CaptureFrameRate = 30;
		}
		_recordingService = new NativeScreenRecordingService();
	}

	protected override void OnStartup(StartupEventArgs e)
	{
		if (!TryAcquireSingleInstance())
		{
			Shutdown();
			return;
		}

		base.OnStartup(e);
		ShutdownMode = ShutdownMode.OnExplicitShutdown;
		EnsureRunAtStartupRegistration();

		_mainWindow = new MainWindow();
		_mainWindow.Closing += MainWindowClosing;
		RefreshMainWindowStatus();
		_mainWindow.Hide();

		InitializeTrayIcon();
		UpdateUiForState(AppState.Idle);
		ShowTrayStartupHint();

	}

	protected override void OnExit(ExitEventArgs e)
	{
		_notifyIcon?.Dispose();
		_notifyIcon = null;
		_idleIcon?.Dispose();
		_recordingIcon?.Dispose();
		_processingIcon?.Dispose();
		_editingIcon?.Dispose();
		if (_singleInstanceMutex is not null)
		{
			if (_ownsSingleInstanceMutex)
			{
				_singleInstanceMutex.ReleaseMutex();
				_ownsSingleInstanceMutex = false;
			}
			_singleInstanceMutex.Dispose();
			_singleInstanceMutex = null;
		}
		base.OnExit(e);
	}

	private void InitializeTrayIcon()
	{
		_idleIcon = TrayIconFactory.CreateIdleIcon();
		_recordingIcon = TrayIconFactory.CreateRecordingIcon();
		_processingIcon = TrayIconFactory.CreateProcessingIcon();
		_editingIcon = TrayIconFactory.CreateEditingIcon();

		_toggleRecordingMenuItem = new Forms.ToolStripMenuItem("Start Recording", null, async (_, _) => await ToggleRecordingFromTrayAsync());
		var openMenuItem = new Forms.ToolStripMenuItem("Open", null, (_, _) => ShowMainWindow());
		var settingsMenuItem = new Forms.ToolStripMenuItem("Settings", null, (_, _) => OpenSettings());
		_runWithWindowsMenuItem = new Forms.ToolStripMenuItem("Run with Windows", null, (_, _) => ToggleRunWithWindowsFromTray())
		{
			CheckOnClick = false,
			Checked = _settings.RunAtStartup,
		};
		var exitMenuItem = new Forms.ToolStripMenuItem("Exit", null, (_, _) => ExitFromTray());

		_notifyIcon = new Forms.NotifyIcon
		{
			Icon = _idleIcon,
			Visible = true,
			Text = "Bandichan",
			ContextMenuStrip = new Forms.ContextMenuStrip(),
		};

		_notifyIcon.ContextMenuStrip.Items.Add(_toggleRecordingMenuItem);
		_notifyIcon.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
		_notifyIcon.ContextMenuStrip.Items.Add(openMenuItem);
		_notifyIcon.ContextMenuStrip.Items.Add(settingsMenuItem);
		_notifyIcon.ContextMenuStrip.Items.Add(_runWithWindowsMenuItem);
		_notifyIcon.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
		_notifyIcon.ContextMenuStrip.Items.Add(exitMenuItem);
		_notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
	}

	private static bool TryAcquireSingleInstance()
	{
		try
		{
			_singleInstanceMutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName, createdNew: out var createdNew);
			_ownsSingleInstanceMutex = createdNew;
			if (!createdNew)
			{
				_singleInstanceMutex.Dispose();
				_singleInstanceMutex = null;
			}

			return createdNew;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}

	private void ToggleRunWithWindowsFromTray()
	{
		var desired = !_settings.RunAtStartup;
		try
		{
			ApplyRunAtStartup(desired);
			_settings.RunAtStartup = desired;
			SettingsStore.Save(_settings);
			if (_runWithWindowsMenuItem is not null)
			{
				_runWithWindowsMenuItem.Checked = desired;
			}
		}
		catch (Exception ex)
		{
			System.Windows.MessageBox.Show(
				$"Could not update Run with Windows setting. {ex.Message}",
				"Bandichan",
				MessageBoxButton.OK,
				MessageBoxImage.Error);

			if (_runWithWindowsMenuItem is not null)
			{
				_runWithWindowsMenuItem.Checked = _settings.RunAtStartup;
			}
		}
	}

	private void EnsureRunAtStartupRegistration()
	{
		try
		{
			ApplyRunAtStartup(_settings.RunAtStartup);
		}
		catch
		{
			_settings.RunAtStartup = false;
			SettingsStore.Save(_settings);
		}
	}

	private static void ApplyRunAtStartup(bool enabled)
	{
		var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
		if (string.IsNullOrWhiteSpace(startupFolder))
		{
			throw new InvalidOperationException("Startup folder could not be resolved.");
		}

		Directory.CreateDirectory(startupFolder);
		var shortcutPath = Path.Combine(startupFolder, StartupShortcutName);

		if (!enabled)
		{
			if (File.Exists(shortcutPath))
			{
				File.Delete(shortcutPath);
			}
			return;
		}

		var targetPath = Environment.ProcessPath;
		if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
		{
			targetPath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
		}

		if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
		{
			throw new InvalidOperationException("Application executable path could not be resolved.");
		}

		var shellType = Type.GetTypeFromProgID("WScript.Shell")
			?? throw new InvalidOperationException("WScript.Shell COM type is not available.");

		object? shell = null;
		object? shortcut = null;

		try
		{
			shell = Activator.CreateInstance(shellType)
				?? throw new InvalidOperationException("Failed to create WScript.Shell instance.");

			dynamic shellDynamic = shell;
			shortcut = shellDynamic.CreateShortcut(shortcutPath);
			dynamic shortcutDynamic = shortcut;

			shortcutDynamic.TargetPath = targetPath;
			shortcutDynamic.WorkingDirectory = Path.GetDirectoryName(targetPath);
			shortcutDynamic.Arguments = string.Empty;
			shortcutDynamic.Description = "Bandichan";
			shortcutDynamic.IconLocation = targetPath + ",0";
			shortcutDynamic.Save();
		}
		finally
		{
			if (shortcut is not null && Marshal.IsComObject(shortcut))
			{
				Marshal.FinalReleaseComObject(shortcut);
			}

			if (shell is not null && Marshal.IsComObject(shell))
			{
				Marshal.FinalReleaseComObject(shell);
			}
		}
	}

	private async Task ToggleRecordingFromTrayAsync()
	{
		if (_isRecordingTransitionInProgress)
		{
			return;
		}

		try
		{
			_isRecordingTransitionInProgress = true;
			if (_toggleRecordingMenuItem is not null)
			{
				_toggleRecordingMenuItem.Enabled = false;
			}
			await ToggleRecordingAsync();
		}
		catch (Exception ex)
		{
			System.Windows.MessageBox.Show(
				$"Unexpected error while toggling recording. {ex.Message}",
				"Bandichan",
				MessageBoxButton.OK,
				MessageBoxImage.Error);
			UpdateUiForState(AppState.Idle);
		}
		finally
		{
			if (_toggleRecordingMenuItem is not null)
			{
				_toggleRecordingMenuItem.Enabled = true;
			}
			_isRecordingTransitionInProgress = false;
		}
	}

	private async Task ToggleRecordingAsync()
	{
		if (_state == AppState.Recording && !_recordingService.IsRecording)
		{
			UpdateUiForState(AppState.Idle);
		}

		if (_state is AppState.Recording or AppState.Processing || _recordingService.IsRecording)
		{
			await StopRecordingAsync();
			return;
		}

		if (_state == AppState.Editing && _editorWindow is not null)
		{
			_editorWindow.Close();
		}

		await StartRecordingAsync();
	}

	private async Task StartRecordingAsync()
	{
		_recordingService = new NativeScreenRecordingService();
		RefreshMainWindowStatus();

		var monitors = MonitorDiscovery.GetMonitors();
		if (monitors.Count == 0)
		{
			System.Windows.MessageBox.Show("No monitors were found.", "Bandichan", MessageBoxButton.OK, MessageBoxImage.Warning);
			return;
		}

		DisplayMonitor? selected = null;
		if (monitors.Count == 1)
		{
			selected = monitors[0];
		}
		else
		{
			var picker = new MonitorPickerWindow(monitors);
			var accepted = picker.ShowDialog();
			if (accepted == true)
			{
				selected = picker.SelectedMonitor;
			}
		}

		if (selected is null)
		{
			return;
		}

		try
		{
			var outputDirectory = _settings.OutputDirectory;
			if (!Directory.Exists(outputDirectory))
			{
				Directory.CreateDirectory(outputDirectory);
			}

			var request = new RecordingRequest(
				selected,
				outputDirectory,
				_settings.CaptureFrameRate,
				IncludeSystemAudio: _settings.RecordSystemAudio,
				SystemAudioDevice: _settings.SystemAudioDevice);
			await _recordingService.StartAsync(request);
			_lastVideoCodec = _recordingService.CurrentSession?.Codec.VideoCodec;
			UpdateUiForState(AppState.Recording);
			_mainWindow?.Hide();
		}
		catch (Exception ex)
		{
			System.Windows.MessageBox.Show($"Could not start recording. {ex.Message}", "Bandichan", MessageBoxButton.OK, MessageBoxImage.Error);
			UpdateUiForState(AppState.Idle);
		}
	}

	private async Task StopRecordingAsync()
	{
		try
		{
			UpdateUiForState(AppState.Processing);
			var session = await _recordingService.StopAsync();

			if (session is null)
			{
				UpdateUiForState(AppState.Idle);
				System.Windows.MessageBox.Show(
					"Recording stopped, but no output session was returned.",
					"Bandichan",
					MessageBoxButton.OK,
					MessageBoxImage.Warning);
				return;
			}

			UpdateUiForState(AppState.Editing);
			_editorWindow?.Close();
			_editorWindow = new EditorWindow(session.OutputPath, _settings.EncoderMode);
			_editorWindow.Closed += (_, _) =>
			{
				_editorWindow = null;
				UpdateUiForState(AppState.Idle);
			};
			_editorWindow.Show();
			_editorWindow.Activate();
		}
		catch (Exception ex)
		{
			System.Windows.MessageBox.Show($"Could not stop recording cleanly. {ex.Message}", "Bandichan", MessageBoxButton.OK, MessageBoxImage.Error);
			UpdateUiForState(AppState.Idle);
		}
	}

	private void OpenSettings()
	{
		var window = new SettingsWindow(_settings);
		if (_editorWindow is { IsVisible: true })
		{
			window.Owner = _editorWindow;
		}
		else if (_mainWindow is { IsLoaded: true, IsVisible: true })
		{
			window.Owner = _mainWindow;
		}

		if (window.ShowDialog() == true)
		{
			_settings = window.Settings;
			SettingsStore.Save(_settings);
			RefreshMainWindowStatus();
		}
	}

	private void ShowMainWindow()
	{
		if (_mainWindow is null)
		{
			return;
		}

		_mainWindow.Show();
		_mainWindow.WindowState = WindowState.Normal;
		_mainWindow.Activate();
	}

	private void MainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
	{
		if (_isExiting)
		{
			return;
		}

		e.Cancel = true;
		_mainWindow?.Hide();
	}

	private void ExitFromTray()
	{
		_isExiting = true;
		Shutdown();
	}

	private void UpdateUiForState(AppState state)
	{
		_state = state;
		RefreshMainWindowStatus();

		if (_notifyIcon is null || _toggleRecordingMenuItem is null)
		{
			return;
		}

		switch (_state)
		{
			case AppState.Recording:
			_notifyIcon.Icon = _recordingIcon;
				_notifyIcon.Text = "Bandichan - Recording";
				_toggleRecordingMenuItem.Text = "Stop Recording";
				break;
			case AppState.Processing:
				_notifyIcon.Icon = _processingIcon;
				_notifyIcon.Text = "Bandichan - Processing";
				_toggleRecordingMenuItem.Text = "Stop Recording";
				break;
			case AppState.Editing:
				_notifyIcon.Icon = _editingIcon;
				_notifyIcon.Text = "Bandichan - Editing";
				_toggleRecordingMenuItem.Text = "Start Recording";
				break;
			default:
				_notifyIcon.Icon = _idleIcon;
				_notifyIcon.Text = "Bandichan - Ready";
				_toggleRecordingMenuItem.Text = "Start Recording";
				break;
		}
	}

	private void RefreshMainWindowStatus()
	{
		if (_mainWindow is null)
		{
			return;
		}

		_mainWindow.UpdateStatus(
			appState: _state,
			encoderMode: _settings.EncoderMode,
			lastCodec: _lastVideoCodec,
			outputFolder: _settings.OutputDirectory);
	}

	private void ShowTrayStartupHint()
	{
		if (_notifyIcon is null)
		{
			return;
		}

		_notifyIcon.BalloonTipTitle = "Bandichan is running";
		_notifyIcon.BalloonTipText = "The app is running in the system tray. Right-click the tray icon to start or stop recording.";
		_notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
		_notifyIcon.ShowBalloonTip(5000);
	}
}

