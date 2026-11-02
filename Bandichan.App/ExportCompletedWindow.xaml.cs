using System.Windows;

namespace Bandichan.App;

public partial class ExportCompletedWindow : Window
{
    public bool OpenFolder { get; private set; }

    public ExportCompletedWindow(string outputPath, double actualMb)
    {
        InitializeComponent();
        ResultText.Text = $"Output: {outputPath}\nFinal size: {actualMb:0.##} MB";
    }

    private void OpenFolderClick(object sender, RoutedEventArgs e)
    {
        OpenFolder = true;
        DialogResult = true;
    }
}
