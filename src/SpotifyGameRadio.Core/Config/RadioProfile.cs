using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SpotifyGameRadio.Core.Config;

public class RadioProfile : INotifyPropertyChanged
{
    private string _name = "Default";
    private string _sourceProcessName = "";
    private string _outputDeviceId = "";
    private float _sourceX = 0.3f;
    private float _sourceY = -0.1f;
    private float _sourceZ = 0.2f;
    private float _highPassHz = 400f;
    private float _lowPassHz = 3400f;
    private float _distortionDrive = 0.2f;
    private float _compressorThresholdDb = -18f;
    private float _compressorRatio = 4f;
    private float _noiseLevel = 0.05f;
    private float _wetDryMix = 1.0f;
    private FreelookHotkey _hotkey = new();
    private float _mouseSensitivity = 0.15f; // degrees per mouse count
    private float _maxYawDegrees = 90f;
    private float _maxPitchDegrees = 60f;
    private float _springBackRatePerSecond = 720f;
    private bool _autoRouteSource = true;
    private string _routeSourceToDeviceId = "";
    private float _volume = 1.0f;
    private float _stereoWidth = 1.0f;
    private bool _freelookAlwaysOn;
    private FreelookHotkey _recenterHotkey = new() { VirtualKeyCode = 0 };
    private FreelookHotkey _vehicleToggleHotkey = new() { VirtualKeyCode = 0 };
    private float _vehicleExitDelaySeconds = 3.0f;
    private bool _autoMuteSource = false;
    private FreelookHotkey _calibrateHotkey = new() { VirtualKeyCode = 0 };
    private float _measuredYawSweepCounts = 0f;
    private float _measuredMaxOffAxisDegrees = 0f;

    public string Name { get => _name; set => SetField(ref _name, value); }
    public string SourceProcessName { get => _sourceProcessName; set => SetField(ref _sourceProcessName, value); }
    public string OutputDeviceId { get => _outputDeviceId; set => SetField(ref _outputDeviceId, value); }

    // Fixed source position in listener-relative meters: +X right, +Y up, +Z forward.
    public float SourceX { get => _sourceX; set => SetField(ref _sourceX, value); }
    public float SourceY { get => _sourceY; set => SetField(ref _sourceY, value); }
    public float SourceZ { get => _sourceZ; set => SetField(ref _sourceZ, value); }

    public float HighPassHz { get => _highPassHz; set => SetField(ref _highPassHz, value); }
    public float LowPassHz { get => _lowPassHz; set => SetField(ref _lowPassHz, value); }
    public float DistortionDrive { get => _distortionDrive; set => SetField(ref _distortionDrive, value); }
    public float CompressorThresholdDb { get => _compressorThresholdDb; set => SetField(ref _compressorThresholdDb, value); }
    public float CompressorRatio { get => _compressorRatio; set => SetField(ref _compressorRatio, value); }
    public float NoiseLevel { get => _noiseLevel; set => SetField(ref _noiseLevel, value); }
    public float WetDryMix { get => _wetDryMix; set => SetField(ref _wetDryMix, value); }

    public FreelookHotkey Hotkey { get => _hotkey; set => SetField(ref _hotkey, value); }
    public float MouseSensitivity { get => _mouseSensitivity; set => SetField(ref _mouseSensitivity, value); }
    public float MaxYawDegrees { get => _maxYawDegrees; set => SetField(ref _maxYawDegrees, value); }
    public float MaxPitchDegrees { get => _maxPitchDegrees; set => SetField(ref _maxPitchDegrees, value); }
    public float SpringBackRatePerSecond { get => _springBackRatePerSecond; set => SetField(ref _springBackRatePerSecond, value); }

    /// When true, MainViewModel routes the source process's audio to
    /// RouteSourceToDeviceId (or an auto-detected virtual cable) on Start and
    /// restores it on Stop, so only the processed radio output is audible.
    public bool AutoRouteSource { get => _autoRouteSource; set => SetField(ref _autoRouteSource, value); }

    /// MMDevice id of the render endpoint to route the source to. Empty means
    /// "auto-detect a virtual cable at Start".
    public string RouteSourceToDeviceId { get => _routeSourceToDeviceId; set => SetField(ref _routeSourceToDeviceId, value); }

    /// Master output attenuation, 0..1. Applied after StereoWidth, before output.
    public float Volume { get => _volume; set => SetField(ref _volume, value); }

    /// Mid/side stereo width. 0 = mono point source, 1 = unchanged, >1 = wider.
    public float StereoWidth { get => _stereoWidth; set => SetField(ref _stereoWidth, value); }

    /// When true, freelook tracks the mouse continuously (no hold-to-look) and
    /// does not spring back to centre.
    public bool FreelookAlwaysOn { get => _freelookAlwaysOn; set => SetField(ref _freelookAlwaysOn, value); }

    /// Global hotkey that fires Recenter without alt-tabbing to the window.
    /// VirtualKeyCode 0 means unbound (never fires).
    public FreelookHotkey RecenterHotkey { get => _recenterHotkey; set => SetField(ref _recenterHotkey, value); }

    /// Global hotkey that toggles the simulated vehicle in/out state (mutes or
    /// unmutes the processed output independent of Volume). VirtualKeyCode 0
    /// means unbound (never fires).
    public FreelookHotkey VehicleToggleHotkey { get => _vehicleToggleHotkey; set => SetField(ref _vehicleToggleHotkey, value); }

    /// Seconds to wait after toggling "out of vehicle" before the output
    /// actually mutes, matching games with a multi-second exit-vehicle
    /// animation. 0 = mute (almost) immediately. Entering back "in" is always
    /// immediate, no delay.
    public float VehicleExitDelaySeconds { get => _vehicleExitDelaySeconds; set => SetField(ref _vehicleExitDelaySeconds, value); }

    /// When true, MainViewModel mutes the source process's own Windows audio
    /// session on Start and unmutes it on Stop, so the raw source is never
    /// audible alongside the processed radio without a manual mixer step.
    /// Defaults to false: muting the source's session ALSO silences that
    /// session's contribution to this app's own loopback capture (confirmed on
    /// both WasapiDeviceLoopbackCapture and WasapiProcessLoopbackCapture) — so
    /// turning this on silences the whole radio, not just the raw source. Only
    /// safe to enable if you don't need this app to actually process that
    /// source's audio, which defeats the app's purpose for its own source. Kept
    /// as an opt-in rather than removed because a future release may route the
    /// mute through a mechanism that doesn't intersect the capture path.
    public bool AutoMuteSource { get => _autoMuteSource; set => SetField(ref _autoMuteSource, value); }

    /// Global tap hotkey used to mark a view limit during freelook
    /// calibration. VirtualKeyCode 0 means unbound.
    public FreelookHotkey CalibrateHotkey { get => _calibrateHotkey; set => SetField(ref _calibrateHotkey, value); }

    /// Mouse counts from centre to a view limit, captured by calibration.
    /// 0 means "never calibrated". When > 0, MainViewModel re-derives
    /// MouseSensitivity from it whenever MaxYawDegrees changes.
    public float MeasuredYawSweepCounts { get => _measuredYawSweepCounts; set => SetField(ref _measuredYawSweepCounts, value); }

    /// Largest angle from "straight ahead" the game's freelook allows,
    /// captured by the calibration "corner" mark. 0 means "no
    /// combined-angle clamp" (per-axis clamps only). FreelookTracker
    /// clamps sqrt(yaw^2 + pitch^2) to this when > 0.
    public float MeasuredMaxOffAxisDegrees { get => _measuredMaxOffAxisDegrees; set => SetField(ref _measuredMaxOffAxisDegrees, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
