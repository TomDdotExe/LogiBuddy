using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using SpotifyGameRadio.App.Audio;
using SpotifyGameRadio.Core.Audio;
using SpotifyGameRadio.Core.Config;
using SpotifyGameRadio.Core.Dsp;
using SpotifyGameRadio.Core.Pipeline;
using SpotifyGameRadio.Core.Spatial;
using SpotifyGameRadio.Core.Tracking;

namespace SpotifyGameRadio.App.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly IConfigStore _configStore = new ConfigStore();
    private readonly ISourceAudioRouter _router = new WindowsAppAudioRouter();
    private readonly RouteRecoveryStore _routeRecovery = new();
    private RouteRecoveryRecord? _activeRouteRecovery;
    private RadioPipeline? _pipeline;
    private Win32MouseHook? _mouseHook;
    private System.Windows.Threading.DispatcherTimer? _uiTimer;
    private TestTonePlayer? _testTone;
    private readonly ISourceSessionMuter _sessionMuter = new NAudioSourceSessionMuter();
    private string? _mutedProcessName;
    private TapHotkeyWatcher? _recenterHotkeyWatcher;
    private TapHotkeyWatcher? _vehicleToggleHotkeyWatcher;
    private bool _isInVehicle = true;
    private System.Windows.Threading.DispatcherTimer? _vehicleExitTimer;
    private CalibrationSession? _calibrationSession;
    private TapHotkeyWatcher? _calibrateHotkeyWatcher;
    private readonly CalibrationAnnouncer _announcer;

    private static readonly HashSet<string> LiveProfileProperties = new()
    {
        nameof(RadioProfile.HighPassHz), nameof(RadioProfile.LowPassHz),
        nameof(RadioProfile.DistortionDrive), nameof(RadioProfile.CompressorThresholdDb),
        nameof(RadioProfile.CompressorRatio), nameof(RadioProfile.NoiseLevel),
        nameof(RadioProfile.WetDryMix),
        nameof(RadioProfile.SourceX), nameof(RadioProfile.SourceY), nameof(RadioProfile.SourceZ),
        nameof(RadioProfile.MouseSensitivity), nameof(RadioProfile.MaxYawDegrees),
        nameof(RadioProfile.MaxPitchDegrees), nameof(RadioProfile.SpringBackRatePerSecond),
        nameof(RadioProfile.Volume), nameof(RadioProfile.StereoWidth),
        nameof(RadioProfile.FreelookAlwaysOn),
    };

    // Hotkey and OutputDeviceId are applied live (see OnProfilePropertyChanged);
    // these still need a Stop/Start because capture is bound to the source
    // process and routing is set up in Start().
    private static readonly HashSet<string> RestartRequiredProfileProperties = new()
    {
        nameof(RadioProfile.SourceProcessName),
        nameof(RadioProfile.AutoRouteSource), nameof(RadioProfile.RouteSourceToDeviceId),
        nameof(RadioProfile.AutoMuteSource),
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<AudioSourceInfo> AvailableSources { get; private set; } = Array.Empty<AudioSourceInfo>();
    public IReadOnlyList<RenderDeviceInfo> AvailableRenderDevices { get; private set; } = Array.Empty<RenderDeviceInfo>();
    public IReadOnlyList<string> AvailableProfiles => _configStore.ListProfiles();

    private string _statusMessage = "Idle";
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    private int _bufferUnderrunCount;
    public int BufferUnderrunCount
    {
        get => _bufferUnderrunCount;
        set { _bufferUnderrunCount = value; OnPropertyChanged(); }
    }

    private bool _restartRequired;
    public bool RestartRequired
    {
        get => _restartRequired;
        set { _restartRequired = value; OnPropertyChanged(); }
    }

    // Live freelook orientation, polled from the pipeline by _uiTimer while
    // running and bound by SourcePositionCanvas to rotate the listener marker.
    private double _listenerYaw;
    public double ListenerYaw
    {
        get => _listenerYaw;
        set { if (value == _listenerYaw) return; _listenerYaw = value; OnPropertyChanged(); }
    }

    private double _listenerPitch;
    public double ListenerPitch
    {
        get => _listenerPitch;
        set { if (value == _listenerPitch) return; _listenerPitch = value; OnPropertyChanged(); }
    }

    private bool _testToneEnabled;
    /// Plays a soft 440 Hz sine from this process so per-process loopback has a
    /// source to capture without needing Spotify running. Independent of
    /// Start/Stop; select "SpotifyGameRadio.App" in the Source list to route it
    /// through the radio pipeline.
    public bool TestToneEnabled
    {
        get => _testToneEnabled;
        set
        {
            if (_testToneEnabled == value) return;
            _testToneEnabled = value;
            if (value)
            {
                _testTone ??= new TestTonePlayer();
                _testTone.Start();
            }
            else
            {
                _testTone?.Stop();
            }
            OnPropertyChanged();
        }
    }

    public RadioProfile Profile { get; private set; } = new();

    /// Whether per-app audio routing is available at all (Windows 11 + a
    /// reachable IAudioPolicyConfig factory). Fixed for the app's lifetime, so
    /// no change notification is needed; the routing UI row binds IsEnabled to it.
    public bool IsRoutingSupported => _router.IsSupported;

    public ICommand RefreshSourcesCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand LoadProfileCommand { get; }
    public ICommand ResetSourceRoutingCommand { get; }
    public ICommand RecenterCommand { get; }
    public ICommand CalibrateFreelookCommand { get; }

    public MainViewModel()
    {
        RefreshSourcesCommand = new RelayCommand(_ => RefreshSources());
        // Guarded so a second click can't build a whole new capture/output/hook
        // stack over _pipeline/_mouseHook, orphaning the first (unstoppable, and
        // both rendering to the output device at once). RelayCommand raises
        // CanExecuteChanged off CommandManager.RequerySuggested, which WPF fires
        // after UI interactions such as the Start/Stop clicks themselves.
        StartCommand = new RelayCommand(_ => Start(), _ => _pipeline is null);
        StopCommand = new RelayCommand(_ => Stop());
        SaveProfileCommand = new RelayCommand(_ => _configStore.Save(Profile));
        LoadProfileCommand = new RelayCommand(name => LoadProfile((string)name!));
        ResetSourceRoutingCommand = new RelayCommand(_ => ResetSourceRouting());
        RecenterCommand = new RelayCommand(_ => Recenter());
        CalibrateFreelookCommand = new RelayCommand(
            _ => StartOrAbortCalibration(),
            _ => _pipeline is not null
                 && _mouseHook is not null
                 && Profile.Hotkey.VirtualKeyCode != 0
                 && Profile.CalibrateHotkey.VirtualKeyCode != 0
                 && Profile.CalibrateHotkey.VirtualKeyCode != Profile.Hotkey.VirtualKeyCode);
        _announcer = new CalibrationAnnouncer(() => Profile.OutputDeviceId);

        RefreshSources();
        Profile.PropertyChanged += OnProfilePropertyChanged;

        // A profile saved on a Windows 11 box would otherwise make every Start
        // fail on a machine where routing isn't available at all.
        if (!_router.IsSupported && Profile.AutoRouteSource) Profile.AutoRouteSource = false;

        // Crash recovery runs during construction, i.e. while the window is
        // being built — nothing in here may throw, or the app fails to launch.
        try
        {
            if (_routeRecovery.Exists())
            {
                RestoreSourceRouting();
                StatusMessage = "Restored source audio routing from a previous session.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't restore previous source routing: {ex.Message}";
        }
    }

    private void OnProfilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null) return;

        // Keep a calibrated MouseSensitivity in step with MaxYawDegrees,
        // whether running or not. Setting MouseSensitivity re-enters this
        // handler under a different property name (no further recursion) and,
        // when running, is pushed to the pipeline by the branch below.
        if (e.PropertyName == nameof(RadioProfile.MaxYawDegrees)
            && Profile.MeasuredYawSweepCounts > 0f)
        {
            float oldSens = Profile.MouseSensitivity;
            float newSens = Profile.MaxYawDegrees / Profile.MeasuredYawSweepCounts;
            if (oldSens > 0f && Profile.MeasuredMaxOffAxisDegrees > 0f)
                Profile.MeasuredMaxOffAxisDegrees *= newSens / oldSens;
            Profile.MouseSensitivity = newSens;
        }

        if (LiveProfileProperties.Contains(e.PropertyName))
        {
            if (_pipeline is null) return;
            try
            {
                _pipeline.ApplyProfile(Profile);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Couldn't apply change: {ex.Message}";
            }
        }
        else if (e.PropertyName == nameof(RadioProfile.Hotkey))
        {
            // Applies to the running hook immediately; also picked up by the
            // next Start() since it reads Profile.Hotkey when building the hook.
            _mouseHook?.SetHotkey(Profile.Hotkey);
        }
        else if (e.PropertyName == nameof(RadioProfile.RecenterHotkey))
        {
            _recenterHotkeyWatcher?.SetHotkey(Profile.RecenterHotkey);
        }
        else if (e.PropertyName == nameof(RadioProfile.VehicleToggleHotkey))
        {
            _vehicleToggleHotkeyWatcher?.SetHotkey(Profile.VehicleToggleHotkey);
        }
        else if (e.PropertyName == nameof(RadioProfile.CalibrateHotkey))
        {
            _calibrateHotkeyWatcher?.SetHotkey(Profile.CalibrateHotkey);
        }
        else if (e.PropertyName == nameof(RadioProfile.OutputDeviceId) && _pipeline is not null)
        {
            try
            {
                _pipeline.SetOutputDevice(Profile.OutputDeviceId);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Couldn't switch output device: {ex.Message}";
            }
        }
        else if (RestartRequiredProfileProperties.Contains(e.PropertyName) && _pipeline is not null)
        {
            RestartRequired = true;
        }
    }

    private void RefreshSources()
    {
        AvailableSources = AudioSessionEnumerator.ListActiveSources();
        OnPropertyChanged(nameof(AvailableSources));

        // The synthetic empty-id entry is how the user gets back to auto-detect
        // after picking a device: it sets RouteSourceToDeviceId = "", which
        // ResolveRouteDeviceId() treats as "find a virtual cable at Start".
        AvailableRenderDevices = new[] { new RenderDeviceInfo("", "(auto-detect virtual cable)") }
            .Concat(RenderDeviceEnumerator.ListRenderDevices())
            .ToList();
        OnPropertyChanged(nameof(AvailableRenderDevices));
    }

    private void LoadProfile(string name)
    {
        if (_calibrationSession is not null)
        {
            _calibrationSession.Abort();
            TeardownCalibration();
        }
        Profile.PropertyChanged -= OnProfilePropertyChanged;
        Profile = _configStore.Load(name);
        Profile.PropertyChanged += OnProfilePropertyChanged;
        OnPropertyChanged(nameof(Profile));

        if (_pipeline is not null)
        {
            try
            {
                _pipeline.ApplyProfile(Profile);
                _pipeline.SetOutputDevice(Profile.OutputDeviceId);
                _mouseHook?.SetHotkey(Profile.Hotkey);
                _recenterHotkeyWatcher?.SetHotkey(Profile.RecenterHotkey);
                _vehicleToggleHotkeyWatcher?.SetHotkey(Profile.VehicleToggleHotkey);
                _calibrateHotkeyWatcher?.SetHotkey(Profile.CalibrateHotkey);
            }
            catch (Exception ex) { StatusMessage = $"Couldn't apply loaded profile: {ex.Message}"; }
            // Source process and routing still need a manual Stop/Start.
            RestartRequired = true;
        }
    }

    /// Resolves the render-device id to route the source to: the profile's
    /// explicit choice if it still exists, else the first auto-detected virtual
    /// cable, else null.
    private string? ResolveRouteDeviceId()
    {
        var devices = RenderDeviceEnumerator.ListRenderDevices();
        if (!string.IsNullOrEmpty(Profile.RouteSourceToDeviceId) &&
            devices.Any(d => d.Id == Profile.RouteSourceToDeviceId))
            return Profile.RouteSourceToDeviceId;

        return devices.FirstOrDefault(d => RenderDeviceEnumerator.LooksLikeVirtualCable(d.FriendlyName))?.Id;
    }

    /// Applies routing for every pid of the source process. Returns null on
    /// success or a user-facing error string on failure (with any partial
    /// routing already rolled back).
    private string? TryRouteSource()
    {
        if (!_router.IsSupported)
            return "Auto-routing needs Windows 11 — turn it off in settings to continue.";

        var entries = new List<RouteRecoveryEntry>();
        var routed = new List<int>();
        try
        {
            string? deviceId = ResolveRouteDeviceId();
            if (deviceId is null)
                return "No route-target device — pick one or turn off auto-routing.";

            var pids = System.Diagnostics.Process
                .GetProcessesByName(Profile.SourceProcessName)
                .Select(p => p.Id)
                .ToList();
            if (pids.Count == 0)
                return "Start the source app first, then Start.";

            foreach (int pid in pids)
            {
                var r = _router.GetCurrentRoute(pid);
                entries.Add(new RouteRecoveryEntry(pid, r.Console, r.Multimedia, r.Communications));
            }

            var record = new RouteRecoveryRecord(Profile.SourceProcessName, entries);
            _routeRecovery.Write(record); // persist BEFORE changing anything

            foreach (int pid in pids)
            {
                try
                {
                    _router.RouteProcess(pid, deviceId);
                    routed.Add(pid);
                }
                catch (SourceRoutingException ex) when (ex.NoActiveAudio)
                {
                    // This pid has no audio session. Normal for a multi-process
                    // app like Spotify, where only one of several pids owns the
                    // session — skip it and keep going.
                }
                catch (SourceRoutingException ex)
                {
                    RollBackRouting(routed, entries);
                    return $"Couldn't route source audio: {ex.Message} Fix the route device or turn off auto-routing.";
                }
            }

            if (routed.Count == 0)
            {
                RollBackRouting(routed, entries);
                return "The source isn't playing any audio yet — start playback in it, then click Start.";
            }

            // The record lists every pid's prior route, including the skipped
            // session-less ones — restoring those is a harmless no-op.
            _activeRouteRecovery = record;
            return null;
        }
        catch (Exception ex)
        {
            // Backstop: routing runs on the WPF UI thread from the Start
            // command, so no failure here (COM, IO, a process exiting
            // mid-enumeration) may escape as an unhandled exception.
            RollBackRouting(routed, entries);
            return $"Couldn't set up source routing: {ex.Message}";
        }
    }

    /// Puts back the prior route for every pid already routed, drops the
    /// recovery file, and clears the active record. Best-effort throughout: it
    /// runs on failure paths that must still return a message, not throw.
    private void RollBackRouting(List<int> routed, List<RouteRecoveryEntry> entries)
    {
        foreach (int pid in routed)
        {
            var prev = entries.FirstOrDefault(e => e.ProcessId == pid);
            if (prev is null) continue;
            try { _router.RestoreProcess(pid, new AppAudioRoute(prev.Console, prev.Multimedia, prev.Communications)); }
            catch (Exception) { /* best effort */ }
        }
        _routeRecovery.Delete();
        _activeRouteRecovery = null;
    }

    private void RestoreSourceRouting()
    {
        var record = _activeRouteRecovery ?? _routeRecovery.Read();
        if (record is not null)
        {
            foreach (var e in record.Routes)
            {
                try { _router.RestoreProcess(e.ProcessId, new AppAudioRoute(e.Console, e.Multimedia, e.Communications)); }
                catch (Exception) { /* best effort */ }
            }

            // Pids that appeared after the record was written (the source app
            // was killed and relaunched mid-session, or spawned another helper)
            // would otherwise stay pointed at the cable. They had no override of
            // ours to begin with, so clearing is the correct restore for them.
            if (_router.IsSupported)
            {
                var known = record.Routes.Select(r => r.ProcessId).ToHashSet();
                try
                {
                    foreach (var p in System.Diagnostics.Process.GetProcessesByName(record.SourceProcessName))
                    {
                        if (known.Contains(p.Id)) continue;
                        try { _router.RestoreProcess(p.Id, AppAudioRoute.None); }
                        catch (Exception) { /* best effort */ }
                    }
                }
                catch (Exception) { /* best effort */ }
            }
        }
        _routeRecovery.Delete();
        _activeRouteRecovery = null;
    }

    private void ResetSourceRouting()
    {
        try
        {
            var record = _activeRouteRecovery ?? _routeRecovery.Read();
            if (record is not null)
            {
                RestoreSourceRouting();
            }
            else if (_router.IsSupported)
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(Profile.SourceProcessName))
                {
                    try { _router.RestoreProcess(p.Id, AppAudioRoute.None); }
                    catch (Exception) { /* best effort */ }
                }
            }
            StatusMessage = "Source routing reset.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't reset source routing: {ex.Message}";
        }
    }

    private void OnVehicleTogglePressed()
    {
        _isInVehicle = !_isInVehicle;
        _vehicleExitTimer?.Stop();
        _vehicleExitTimer = null;

        if (_isInVehicle)
        {
            _pipeline?.SetVehicleMuted(false);
            StatusMessage = "In vehicle";
        }
        else
        {
            StatusMessage = $"Exiting vehicle — muting in {Profile.VehicleExitDelaySeconds:0.#}s";
            float delaySeconds = Profile.VehicleExitDelaySeconds;
            if (float.IsNaN(delaySeconds) || float.IsInfinity(delaySeconds)) delaySeconds = 0f;
            delaySeconds = Math.Clamp(delaySeconds, 0f, 60f);
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(delaySeconds)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _pipeline?.SetVehicleMuted(true);
                StatusMessage = "Out of vehicle";
            };
            _vehicleExitTimer = timer;
            timer.Start();
        }
    }

    /// Snaps the freelook listener orientation back to forward, both in the
    /// pipeline and the UI marker. Shared by RecenterCommand (the window
    /// button) and the Recenter hotkey.
    private void Recenter()
    {
        _pipeline?.RecenterListener();
        // Snap the marker now rather than waiting for the next ~30 Hz tick.
        ListenerYaw = 0;
        ListenerPitch = 0;
    }

    /// "Calibrate freelook" button: start a calibration session, or abort
    /// one already running (the button doubles as Cancel).
    private void StartOrAbortCalibration()
    {
        if (_calibrationSession is not null)
        {
            _calibrationSession.Abort(); // the Ended handler tears down
            return;
        }
        BeginCalibration();
    }

    /// Calibrate hotkey press (persistent watcher, live while the pipeline
    /// runs): start a session if none is running, otherwise mark the
    /// current step — so the whole flow works from in-game.
    private void OnCalibrateHotkeyPressed()
    {
        if (_calibrationSession is not null) _calibrationSession.Mark();
        else BeginCalibration();
    }

    /// Shared start path for the button and the hotkey. Guards the same
    /// conditions as CalibrateFreelookCommand.CanExecute, since the hotkey
    /// path does not go through it.
    private void BeginCalibration()
    {
        if (_calibrationSession is not null) return;
        if (_pipeline is null || _mouseHook is null) return;
        if (Profile.Hotkey.VirtualKeyCode == 0
            || Profile.CalibrateHotkey.VirtualKeyCode == 0
            || Profile.CalibrateHotkey.VirtualKeyCode == Profile.Hotkey.VirtualKeyCode)
            return;

        Recenter();

        var session = new CalibrationSession(_mouseHook);

        session.StepChanged += (step, message) => Application.Current?.Dispatcher.Invoke(() =>
        {
            StatusMessage = message;
            _announcer.Say(StepPhrase(step));
        });
        session.Completed += result => Application.Current?.Dispatcher.Invoke(() =>
        {
            float sens = Profile.MaxYawDegrees / result.HalfSweepCounts;
            float cornerYawDeg = MathF.Abs(result.CornerXCounts) * sens;
            float cornerPitchDeg = MathF.Abs(result.CornerYCounts) * sens;

            Profile.MouseSensitivity = sens;
            Profile.MeasuredYawSweepCounts = result.HalfSweepCounts;
            if (cornerPitchDeg > 1f) Profile.MaxPitchDegrees = cornerPitchDeg;
            Profile.MeasuredMaxOffAxisDegrees =
                MathF.Sqrt(cornerYawDeg * cornerYawDeg + cornerPitchDeg * cornerPitchDeg);

            _announcer.Say("Corner set. Calibration complete.");
            StatusMessage =
                $"Calibration complete — sensitivity {sens:0.####}, max pitch {Profile.MaxPitchDegrees:0.#}°, " +
                $"off-axis limit {Profile.MeasuredMaxOffAxisDegrees:0.#}°. Click Save Profile to keep it.";
            TeardownCalibration();
        });
        session.Ended += reason => Application.Current?.Dispatcher.Invoke(() =>
        {
            StatusMessage = reason;
            _announcer.Say(reason);
            TeardownCalibration();
        });

        _calibrationSession = session;
        session.Start();
    }

    private void TeardownCalibration()
    {
        _calibrationSession?.Dispose();
        _calibrationSession = null;
    }

    /// Spoken prompt for a mid-calibration step. Terminal steps are
    /// announced by the Completed / Ended handlers instead, so they return
    /// "" here (CalibrationAnnouncer.Say ignores blank text).
    private static string StepPhrase(CalibrationStep step) => step switch
    {
        CalibrationStep.AwaitRightLimit => "Calibration started. Face forward, then look fully right and tap.",
        CalibrationStep.AwaitCorner => "Right limit set. Now look to the far corner and tap.",
        _ => "",
    };

    private void Start()
    {
        // The construction sequence below touches real hardware (audio
        // devices, the global mouse hook) and can fail for reasons outside
        // our control (e.g. no default render device). Guard the whole
        // thing so a failure degrades to a status message instead of
        // crashing the app on the UI thread — see plan's Global Constraints
        // on graceful degradation, including output device loss.
        if (Profile.AutoRouteSource)
        {
            string? routeError = TryRouteSource();
            if (routeError is not null)
            {
                StatusMessage = routeError;
                return;
            }
        }

        Win32MouseHook? mouseHook = null;
        RadioPipeline? pipeline = null;
        TapHotkeyWatcher? recenterHotkeyWatcher = null;
        TapHotkeyWatcher? vehicleToggleHotkeyWatcher = null;
        TapHotkeyWatcher? calibrateHotkeyWatcher = null;
        try
        {
            // Per-process loopback (WasapiProcessLoopbackCapture) requires Windows 10
            // build 19041 (20H1) or later. On older Windows go straight to
            // whole-device loopback (WasapiDeviceLoopbackCapture). On new-enough
            // Windows, wrap process-loopback in FallbackAudioCaptureService: the
            // build number only says the API exists, not that it actually
            // activates on this box (driver quirks, E_NOINTERFACE), and without
            // this the process-loopback worker would just retry forever and keep
            // reporting AudioCaptureStatus.Error. The wrapper swaps to
            // whole-device loopback the first time activation fails before any
            // successful capture.
            IAudioCaptureService innerCapture;
            if (Environment.OSVersion.Version.Build >= 19041)
            {
                var fallbackCapture = new FallbackAudioCaptureService(
                    () => new WasapiProcessLoopbackCapture(),
                    () => new WasapiDeviceLoopbackCapture());
                fallbackCapture.Notice += message =>
                    Application.Current.Dispatcher.Invoke(() => StatusMessage = message);
                innerCapture = fallbackCapture;
            }
            else
            {
                innerCapture = new WasapiDeviceLoopbackCapture();
            }

            // The rest of the pipeline (effect chain, spatializer, output) is
            // hardcoded to 48 kHz. Per-process loopback always delivers that, but
            // WasapiDeviceLoopbackCapture reports the device's real rate — a
            // 44.1 kHz device would otherwise fill the output buffer slower than
            // it drains and stutter. ResamplingCaptureService converts anything
            // non-48 kHz up front (and is a no-op passthrough at 48 kHz).
            IAudioCaptureService activeCapture = new ResamplingCaptureService(innerCapture);

            var output = new WasapiAudioOutput();
            var effectChain = new RadioEffectChain(48000f);
            mouseHook = new Win32MouseHook(Profile.Hotkey);
            recenterHotkeyWatcher = new TapHotkeyWatcher(Profile.RecenterHotkey, new Win32KeyStateSource());
            recenterHotkeyWatcher.Pressed += () =>
            {
                // A Pressed event can still be in flight after Stop()/window-close
                // has begun tearing down the dispatcher (TapHotkeyWatcher.Dispose()
                // doesn't join its poll thread). Invoke can throw during shutdown or
                // if Application.Current is already null — this runs on a background
                // thread with no global unhandled-exception handler, so an uncaught
                // throw here would crash the whole process.
                try
                {
                    Application.Current?.Dispatcher.Invoke(Recenter);
                }
                catch (Exception) { /* app is shutting down; nothing to recenter */ }
            };

            vehicleToggleHotkeyWatcher = new TapHotkeyWatcher(Profile.VehicleToggleHotkey, new Win32KeyStateSource());
            vehicleToggleHotkeyWatcher.Pressed += () =>
            {
                try
                {
                    Application.Current?.Dispatcher.Invoke(OnVehicleTogglePressed);
                }
                catch (Exception) { /* app is shutting down; nothing to toggle */ }
            };

            calibrateHotkeyWatcher = new TapHotkeyWatcher(Profile.CalibrateHotkey, new Win32KeyStateSource());
            calibrateHotkeyWatcher.Pressed += () =>
            {
                try
                {
                    Application.Current?.Dispatcher.Invoke(OnCalibrateHotkeyPressed);
                }
                catch (Exception) { /* app is shutting down */ }
            };
            var tracker = new FreelookTracker(mouseHook, Profile);

            ISpatializer fallback = new StereoPanSpatializer();
            ISpatializer primary = fallback;
            if (SteamAudioSpatializer.TryCreate(48000, 1024, out var steamAudio) && steamAudio is not null)
                primary = steamAudio;

            // frameSize must match the frame size the spatializer was created
            // with above (1024) — the pipeline hands it exactly this many
            // samples per block.
            pipeline = new RadioPipeline(activeCapture, output, primary, fallback, effectChain, tracker, frameSize: 1024);
            pipeline.ApplyProfile(Profile);
            pipeline.Warning += (_, message) => Application.Current.Dispatcher.Invoke(() => StatusMessage = message);

            // Set before Start(): pipeline.Start() can synchronously raise a
            // Warning (e.g. NoSource), which lands in StatusMessage inline
            // because Dispatcher.Invoke runs immediately when already on the
            // UI thread. Assigning "Running" afterwards would erase it.
            StatusMessage = "Running";

            _isInVehicle = true;
            _vehicleExitTimer?.Stop();
            _vehicleExitTimer = null;
            // Skip auto-mute entirely when routing is also active: routing already
            // hides the raw source (by moving its output to a silent device) without
            // touching Mute, so it doesn't collide with this app's own loopback
            // capture the way session-mute does. Running both would also make the
            // mute scan the wrong render endpoint once routing has moved the
            // session's stream off the default device.
            if (Profile.AutoMuteSource && !Profile.AutoRouteSource)
            {
                _sessionMuter.Mute(Profile.SourceProcessName);
                _mutedProcessName = Profile.SourceProcessName;
            }

            pipeline.Start();

            // Only commit to instance state once everything above has
            // succeeded, so a failed Start() never leaves _pipeline/_mouseHook
            // pointing at partially-initialized objects (would break Stop()
            // or a subsequent retry).
            _pipeline = pipeline;
            _mouseHook = mouseHook;
            _recenterHotkeyWatcher = recenterHotkeyWatcher;
            _vehicleToggleHotkeyWatcher = vehicleToggleHotkeyWatcher;
            _calibrateHotkeyWatcher = calibrateHotkeyWatcher;

            // The hook installs on a background thread; give it a moment, then
            // warn if it failed (spec requires freelook-disabled to be visible).
            var hookCheckTimer = new System.Timers.Timer(500) { AutoReset = false };
            hookCheckTimer.Elapsed += (_, _) => Application.Current.Dispatcher.Invoke(() =>
            {
                if (!mouseHook.HookInstalled)
                    StatusMessage = "Freelook hook failed to install — radio will play fixed at center.";
            });
            hookCheckTimer.Start();

            // Reported after "Running" (not before) so this more specific
            // notice is the one the user actually sees, rather than being
            // clobbered by the generic "Running" assignment above within
            // the same synchronous call — same pattern as hookCheckTimer.
            if (primary == fallback)
                StatusMessage = "HRTF unavailable — using simple stereo panning.";

            // One ~30 Hz UI-thread timer drives both the underrun readout and the
            // live freelook orientation marker. DispatcherTimer ticks on the UI
            // thread, so no marshalling is needed. Stopped and nulled in Stop();
            // rebuilt here so a Start->Stop->Start cycle can't leak one.
            _uiTimer?.Stop();
            _uiTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _uiTimer.Tick += (_, _) =>
            {
                if (_pipeline is null) return;
                BufferUnderrunCount = _pipeline.BufferUnderrunCount;
                ListenerYaw = _pipeline.ListenerYawDegrees;
                ListenerPitch = _pipeline.ListenerPitchDegrees;
            };
            _uiTimer.Start();
            RestartRequired = false;
        }
        catch (Exception ex)
        {
            pipeline?.Stop();
            pipeline?.Dispose();
            mouseHook?.Dispose();
            recenterHotkeyWatcher?.Dispose();
            vehicleToggleHotkeyWatcher?.Dispose();
            calibrateHotkeyWatcher?.Dispose();
            _pipeline = null;
            _mouseHook = null;
            _recenterHotkeyWatcher = null;
            _vehicleToggleHotkeyWatcher = null;
            if (_mutedProcessName is not null)
            {
                _sessionMuter.Unmute(_mutedProcessName);
                _mutedProcessName = null;
            }
            if (_activeRouteRecovery is not null) RestoreSourceRouting();
            StatusMessage = $"Failed to start: {ex.Message}";
        }
    }

    /// Also called from MainWindow.OnClosed, so closing the window restores the
    /// source's routing instead of leaving it pointed at the silent cable.
    /// Safe when nothing is running — every member touched is null-guarded.
    public void Stop()
    {
        if (_calibrationSession is not null)
        {
            _calibrationSession.Abort();
            TeardownCalibration();
        }
        _pipeline?.Stop();
        // Dispose after Stop: releases the spatializers' native resources
        // (Steam Audio HRTF context/effect), which would otherwise leak on
        // every Start->Stop->Start cycle.
        _pipeline?.Dispose();
        _mouseHook?.Dispose();
        _uiTimer?.Stop();
        _uiTimer = null;
        _vehicleExitTimer?.Stop();
        _vehicleExitTimer = null;
        _recenterHotkeyWatcher?.Dispose();
        _vehicleToggleHotkeyWatcher?.Dispose();
        _calibrateHotkeyWatcher?.Dispose();
        _recenterHotkeyWatcher = null;
        _vehicleToggleHotkeyWatcher = null;
        _calibrateHotkeyWatcher = null;
        if (_mutedProcessName is not null)
        {
            _sessionMuter.Unmute(_mutedProcessName);
            _mutedProcessName = null;
        }
        // Cleared so StartCommand.CanExecute goes true again and a fresh
        // Start() builds a new stack rather than stacking on a live one.
        _pipeline = null;
        _mouseHook = null;
        RestoreSourceRouting();
        StatusMessage = "Stopped";
        RestartRequired = false;
        // Snap the freelook marker back to centre now that nothing is polling it.
        ListenerYaw = 0;
        ListenerPitch = 0;
    }

    /// Called from MainWindow.OnClosed so the test tone never outlives the window.
    public void Cleanup()
    {
        _testTone?.Dispose();
        _testTone = null;
        if (_calibrationSession is not null)
        {
            _calibrationSession.Abort();
            TeardownCalibration();
        }
        _announcer.Dispose();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
