using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using SpotifyGameRadio.App.ViewModels;

namespace SpotifyGameRadio.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Stop() first: it restores the source app's audio routing and tears
        // down the pipeline/hook/timers. Closing the window without it would
        // leave the source pointed at the silent cable until the next launch's
        // crash recovery.
        (DataContext as MainViewModel)?.Stop();
        (DataContext as MainViewModel)?.Cleanup();
        base.OnClosed(e);
    }
}