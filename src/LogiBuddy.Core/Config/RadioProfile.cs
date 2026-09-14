using System.ComponentModel;
using System.Runtime.CompilerServices;
using LogiBuddy.Core.Speech;

namespace LogiBuddy.Core.Config;

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
    private float _noiseLevel = 0.01f;
    private float _wetDryMix = 1.0f;
    private FreelookHotkey _hotkey = new();
    private float _mouseSensitivity = 0.15f; // degrees per mouse count, yaw
    private float _pitchSensitivity = 0.15f; // degrees per mouse count, pitch
    private float _maxYawDegrees = 90f;
    private float _springBackRatePerSecond = 720f;
    private bool _autoRouteSource = true;
    private string _routeSourceToDeviceId = "";
    private float _volume = 1.0f;
    private float _stereoWidth = 1.0f;
    private bool _freelookAlwaysOn;
    private FreelookHotkey _recenterHotkey = new() { VirtualKeyCode = 0 };
    private FreelookHotkey _vehicleToggleHotkey = new() { VirtualKeyCode = 0 };
    private float _vehicleExitDelaySeconds = 3.0f;
    private bool _holdToExitVehicle = false;
    private float _vehicleEnterDelaySeconds = 3.0f;
    private bool _autoMuteSource = false;
    private FreelookHotkey _calibrateHotkey = new() { VirtualKeyCode = 0 };
    private float _measuredYawSweepCounts = 0f;
    private float _measuredMaxOffAxisDegrees = 0f;
    private FreelookHotkey _voiceRecordHotkey = new() { VirtualKeyCode = 0 };
    private string _voiceCustomVocabulary = VoiceVocabularyDefaults.Starter;
    private string _voiceMicrophoneDeviceId = "";
    private FreelookHotkey _outsideViewHotkey = new() { VirtualKeyCode = 0 };
    private FreelookHotkey _chatModeHotkey = new() { VirtualKeyCode = 0 };
    private float _outsideLowPassHz = 900f;
    private float _outsideVolume = 0.6f;
    private float _outsideStereoWidth = 0.4f;
    private float _outsideSourceDistance = 5.0f;
    private float _outsideYawSensitivity = 0.15f; // degrees per mouse count, calibrated from a full 360° spin

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

    /// Degrees per mouse count for outside-view pitch (unused inside the
    /// cockpit, which never tracks pitch at all). Manual only — no
    /// calibration technique sets this: judging "straight up"/"straight
    /// down" requires visually confirming against the game's own camera,
    /// which many games clamp well short of vertical, so there's no
    /// reliable universal reference to derive it from the way
    /// MouseSensitivity derives from a measured sweep.
    public float PitchSensitivity { get => _pitchSensitivity; set => SetField(ref _pitchSensitivity, value); }
    public float MaxYawDegrees { get => _maxYawDegrees; set => SetField(ref _maxYawDegrees, value); }
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

    /// Seconds between requesting "out of vehicle" and the output actually
    /// muting — matching games with a multi-second exit-vehicle animation. 0
    /// = mute immediately. In tap-to-toggle mode this counts from the tap;
    /// in hold-to-exit mode it counts from the press, and IS the hold
    /// duration — release before it elapses and the exit never happens at
    /// all (see HoldToExitVehicle).
    public float VehicleExitDelaySeconds { get => _vehicleExitDelaySeconds; set => SetField(ref _vehicleExitDelaySeconds, value); }

    /// When true, the vehicle-toggle hotkey requires holding it down for the
    /// full VehicleExitDelaySeconds (to exit) or VehicleEnterDelaySeconds (to
    /// re-enter) — releasing early cancels the attempt outright, nothing
    /// mutes/unmutes. This is what makes an accidental tap harmless: only a
    /// hold that survives the whole delay does anything. When false
    /// (default), the hotkey is tap-to-toggle instead — a single tap starts
    /// the same delay counting down regardless of what you do with the key
    /// afterward.
    public bool HoldToExitVehicle { get => _holdToExitVehicle; set => SetField(ref _holdToExitVehicle, value); }

    /// Seconds between requesting "in vehicle" (re-entry) and the output
    /// actually un-muting — the inverse of VehicleExitDelaySeconds, for
    /// games with an enter-vehicle animation before audio should resume. 0 =
    /// un-mute immediately. In hold-to-exit mode this IS the required hold
    /// duration to re-enter, same as VehicleExitDelaySeconds is for exiting.
    public float VehicleEnterDelaySeconds { get => _vehicleEnterDelaySeconds; set => SetField(ref _vehicleEnterDelaySeconds, value); }

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

    /// Global hotkey held to record a voice-chat message. On release the
    /// recording is transcribed and the result copied to the clipboard
    /// automatically — no separate confirm/discard step. Holding it again
    /// re-records and replaces the clipboard contents. VirtualKeyCode 0
    /// means unbound (never fires).
    public FreelookHotkey VoiceRecordHotkey { get => _voiceRecordHotkey; set => SetField(ref _voiceRecordHotkey, value); }

    /// Free-text list (comma or newline separated) of terms to bias voice
    /// transcription toward, e.g. callsigns and milsim jargon. Turned into a
    /// Whisper prompt by VocabularyPromptBuilder.
    public string VoiceCustomVocabulary { get => _voiceCustomVocabulary; set => SetField(ref _voiceCustomVocabulary, value); }

    /// MMDevice id of the capture (microphone) endpoint to record from.
    /// Empty means "use the default capture device".
    public string VoiceMicrophoneDeviceId { get => _voiceMicrophoneDeviceId; set => SetField(ref _voiceMicrophoneDeviceId, value); }

    /// Global hotkey that toggles between inside-cockpit and outside
    /// third-person sound (RadioPipeline.SetOutsideView). VirtualKeyCode 0
    /// means unbound. Runtime-only state — always starts back "inside" on
    /// the next Start(), same as the vehicle in/out toggle.
    public FreelookHotkey OutsideViewHotkey { get => _outsideViewHotkey; set => SetField(ref _outsideViewHotkey, value); }

    /// Global hotkey that suspends every other LogiBuddy hotkey (and the
    /// freelook hold-key) until Enter is pressed — for typing in an in-game
    /// chat box without a bound letter firing a hotkey by accident.
    /// VirtualKeyCode 0 means unbound (feature off).
    public FreelookHotkey ChatModeHotkey { get => _chatModeHotkey; set => SetField(ref _chatModeHotkey, value); }

    /// Low-pass cutoff used instead of LowPassHz while in outside view — the
    /// main "how muffled" knob, since it's what most distinguishes hearing a
    /// car stereo through the vehicle's body/windows from outside versus
    /// sitting inside it.
    public float OutsideLowPassHz { get => _outsideLowPassHz; set => SetField(ref _outsideLowPassHz, value); }

    /// Master volume used instead of Volume while in outside view.
    public float OutsideVolume { get => _outsideVolume; set => SetField(ref _outsideVolume, value); }

    /// Stereo width used instead of StereoWidth while in outside view —
    /// narrower/more diffuse, as expected from a more distant, indirect
    /// source rather than sitting right next to the speakers.
    public float OutsideStereoWidth { get => _outsideStereoWidth; set => SetField(ref _outsideStereoWidth, value); }

    /// Distance (metres, dead ahead) the source sits at in outside view,
    /// replacing SourceX/Y/Z entirely. Unlike the inside position, this
    /// isn't about placement (the "camera pivot" is definitionally straight
    /// ahead of an orbiting listener) — it's about giving the HRTF/pan a
    /// large enough vector to produce a clear directional sweep as you pan
    /// (too close to 0 and real HRTF rendering's near-field handling
    /// collapses toward centred/mono regardless of pan direction), and it
    /// also drives an explicit loudness falloff in RadioPipeline — neither
    /// spatializer models distance-based volume on its own, only direction,
    /// so without that this slider would otherwise do nothing audible past
    /// the near-field range.
    public float OutsideSourceDistance { get => _outsideSourceDistance; set => SetField(ref _outsideSourceDistance, value); }

    /// Degrees per mouse count for outside-view yaw, calibrated independently
    /// from cockpit MouseSensitivity: the outside camera pans a free 360°
    /// rather than the game's own limited cockpit range, so it's calibrated
    /// from a full-spin sweep (see CalibrationSession's Outside mode) instead
    /// of a sweep to a game-specific limit.
    public float OutsideYawSensitivity { get => _outsideYawSensitivity; set => SetField(ref _outsideYawSensitivity, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
