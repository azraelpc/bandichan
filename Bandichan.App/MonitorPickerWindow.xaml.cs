using Bandichan.Core;
using System.Windows;
using System.Windows.Input;

namespace Bandichan.App;

public partial class MonitorPickerWindow : Window
{
    public DisplayMonitor? SelectedMonitor { get; private set; }

    public MonitorPickerWindow(IReadOnlyList<DisplayMonitor> monitors)
    {
        InitializeComponent();
        MonitorsList.ItemsSource = monitors;
        MonitorsList.SelectedIndex = monitors.Count > 0 ? 0 : -1;
    }

    private void ConfirmClick(object sender, RoutedEventArgs e)
    {
        SelectedMonitor = MonitorsList.SelectedItem as DisplayMonitor;
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
