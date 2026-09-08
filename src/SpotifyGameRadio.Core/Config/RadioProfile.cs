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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
