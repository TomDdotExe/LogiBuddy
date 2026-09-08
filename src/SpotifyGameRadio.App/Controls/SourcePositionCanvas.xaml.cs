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

    public static readonly DependencyProperty ListenerYawProperty = DependencyProperty.Register(
        nameof(ListenerYaw), typeof(double), typeof(SourcePositionCanvas),
        new FrameworkPropertyMetadata(0.0, OnListenerChanged));

    public static readonly DependencyProperty ListenerPitchProperty = DependencyProperty.Register(
        nameof(ListenerPitch), typeof(double), typeof(SourcePositionCanvas),
        new FrameworkPropertyMetadata(0.0, OnListenerChanged));

    /// Freelook yaw in degrees; rotates the listener arrow (positive = looking right).
    public double ListenerYaw
    {
        get => (double)GetValue(ListenerYawProperty);
        set => SetValue(ListenerYawProperty, value);
    }

    /// Freelook pitch in degrees; foreshortens the listener arrow as its magnitude grows.
    public double ListenerPitch
    {
        get => (double)GetValue(ListenerPitchProperty);
        set => SetValue(ListenerPitchProperty, value);
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

    private static void OnListenerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((SourcePositionCanvas)d).UpdateListenerArrow();
    }

    private void UpdateListenerArrow()
    {
        ListenerYawRotate.Angle = ListenerYaw;
        // 1.0 facing level, easing to ~0.45 at 90 degrees up or down.
        double pitchFraction = Math.Min(Math.Abs(ListenerPitch), 90.0) / 90.0;
        ListenerPitchScale.ScaleY = 1.0 - 0.55 * pitchFraction;
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
