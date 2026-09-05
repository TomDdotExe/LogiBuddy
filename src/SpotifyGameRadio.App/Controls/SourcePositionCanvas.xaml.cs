using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SpotifyGameRadio.App.Controls;

public partial class SourcePositionCanvas : UserControl
{
    public static readonly DependencyProperty SourceXProperty = DependencyProperty.Register(
        nameof(SourceX), typeof(double), typeof(SourcePositionCanvas),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPositionChanged));

    public static readonly DependencyProperty SourceZProperty = DependencyProperty.Register(
        nameof(SourceZ), typeof(double), typeof(SourcePositionCanvas),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPositionChanged));

    public double SourceX
    {
        get => (double)GetValue(SourceXProperty);
        set => SetValue(SourceXProperty, value);
    }

    public double SourceZ
    {
        get => (double)GetValue(SourceZProperty);
        set => SetValue(SourceZProperty, value);
    }

    private const double MetersPerPixel = 0.02; // 100px = 2 meters from center each way
    private const double CenterPixel = 100;
    private bool _dragging;

    public SourcePositionCanvas()
    {
        InitializeComponent();
    }

    private static void OnPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((SourcePositionCanvas)d).UpdateMarkerFromProperties();
    }

    private void UpdateMarkerFromProperties()
    {
        double pixelX = CenterPixel + SourceX / MetersPerPixel - 7;
        double pixelY = CenterPixel - SourceZ / MetersPerPixel - 7; // +Z (forward) is up on screen
        Canvas.SetLeft(SourceMarker, pixelX);
        Canvas.SetTop(SourceMarker, pixelY);
    }

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        UpdateFromMouse(e.GetPosition(PlacementCanvas));
        PlacementCanvas.CaptureMouse();
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragging = false;
            PlacementCanvas.ReleaseMouseCapture();
            return;
        }
        UpdateFromMouse(e.GetPosition(PlacementCanvas));
    }

    private void UpdateFromMouse(Point point)
    {
        SourceX = (point.X - CenterPixel) * MetersPerPixel;
        SourceZ = (CenterPixel - point.Y) * MetersPerPixel;
    }
}
