using System.Windows;
using Bandichan.Core;

namespace Bandichan.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    public void UpdateStatus(
        AppState appState,
        EncoderSelectionMode encoderMode,
        string? lastCodec,
        string outputFolder)
    {
        AppStateText.Text = appState.ToString();
        EncoderModeText.Text = encoderMode == EncoderSelectionMode.PreferH264Compatibility
            ? "Compatibility (H.264 first)"
            : "Auto (efficiency first)";
        LastCodecText.Text = string.IsNullOrWhiteSpace(lastCodec) ? "-" : lastCodec;
        OutputFolderText.Text = outputFolder;
    }
}