using Bandichan.Core;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace Bandichan.App;

public partial class MonitorPickerWindow : Window
{
    private readonly IReadOnlyList<DisplayMonitor> _monitors;

    private sealed record MonitorOption(DisplayMonitor Monitor, string Label);

    public DisplayMonitor? SelectedMonitor { get; private set; }

    public MonitorPickerWindow(IReadOnlyList<DisplayMonitor> monitors)
    {
        InitializeComponent();
        _monitors = monitors;
        Loaded += MonitorPickerWindowLoaded;
    }

    private void MonitorPickerWindowLoaded(object sender, RoutedEventArgs e)
    {
        PopulateMonitorOptions();
    }

    private void PopulateMonitorOptions()
    {
        var currentScreenDeviceName = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).DeviceName;
        var options = _monitors
            .Select(m =>
            {
                var isCurrent = string.Equals(m.Id, currentScreenDeviceName, StringComparison.OrdinalIgnoreCase);
                var label = isCurrent
                    ? $"{m.DisplayName} - This display (where this window is shown)"
                    : m.DisplayName;
                return new MonitorOption(m, label);
            })
            .ToList();

        MonitorsList.ItemsSource = options;
        MonitorsList.SelectedIndex = options.Count > 0 ? 0 : -1;
    }

    private void ConfirmClick(object sender, RoutedEventArgs e)
    {
        SelectedMonitor = (MonitorsList.SelectedItem as MonitorOption)?.Monitor;
        if (SelectedMonitor is null)
        {
            System.Windows.MessageBox.Show("Select a monitor.", "Bandichan", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }

    private void MonitorsListMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ConfirmClick(sender, new RoutedEventArgs());
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
