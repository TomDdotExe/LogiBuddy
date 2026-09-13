using System.Windows;

namespace LogiBuddy.App;

public partial class VoicePreviewOverlay : Window
{
    public VoicePreviewOverlay()
    {
        InitializeComponent();
        SizeChanged += (_, _) => PositionBottomCenter();
    }

    public void SetText(string text) => TranscriptText.Text = text;

    public void SetHint(string text) => HintText.Text = text;

    private void PositionBottomCenter()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - ActualWidth) / 2;
        Top = workArea.Bottom - ActualHeight - 40;
    }
}
