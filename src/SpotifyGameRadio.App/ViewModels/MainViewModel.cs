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
    private System.Timers.Timer? _underrunTimer;
    private TestTonePlayer? _testTone;

    private static readonly HashSet<string> LiveProfileProperties = new()
    {
        nameof(RadioProfile.HighPassHz), nameof(RadioProfile.LowPassHz),
        nameof(RadioProfile.DistortionDrive), nameof(RadioProfile.CompressorThresholdDb),
        nameof(RadioProfile.CompressorRatio), nameof(RadioProfile.NoiseLevel),
        nameof(RadioProfile.WetDryMix),
        nameof(RadioProfile.SourceX), nameof(RadioProfile.SourceY), nameof(RadioProfile.SourceZ),
        nameof(RadioProfile.MouseSensitivity), nameof(RadioProfile.MaxYawDegrees),
        nameof(RadioProfile.MaxPitchDegrees), nameof(RadioProfile.SpringBackRatePerSecond),
    };

    private static readonly HashSet<string> RestartRequiredProfileProperties = new()
    {
        nameof(RadioProfile.SourceProcessName), nameof(RadioProfile.OutputDeviceId),
        nameof(RadioProfile.Hotkey),
        nameof(RadioProfile.AutoRouteSource), nameof(RadioProfile.RouteSourceToDeviceId),
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
        Profile.PropertyChanged -= OnProfilePropertyChanged;
        Profile = _configStore.Load(name);
        Profile.PropertyChanged += OnProfilePropertyChanged;
        OnPropertyChanged(nameof(Profile));

        if (_pipeline is not null)
        {
            try { _pipeline.ApplyProfile(Profile); }
            catch (Exception ex) { StatusMessage = $"Couldn't apply loaded profile: {ex.Message}"; }
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

            pipeline.Start();

            // Only commit to instance state once everything above has
            // succeeded, so a failed Start() never leaves _pipeline/_mouseHook
            // pointing at partially-initialized objects (would break Stop()
            // or a subsequent retry).
            _pipeline = pipeline;
            _mouseHook = mouseHook;

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

            // Stop/dispose any previous instance first so a Start->Stop->Start
            // cycle can't leak a perpetually-firing timer (AutoReset defaults
            // to true here, unlike the self-limiting hookCheckTimer above) —
            // same idempotent-restart pattern as WasapiAudioOutput.Start().
            _underrunTimer?.Stop();
            _underrunTimer?.Dispose();
            _underrunTimer = new System.Timers.Timer(1000);
            _underrunTimer.Elapsed += (_, _) => Application.Current.Dispatcher.Invoke(() =>
            {
                if (_pipeline is not null) BufferUnderrunCount = _pipeline.BufferUnderrunCount;
            });
            _underrunTimer.Start();
            RestartRequired = false;
        }
        catch (Exception ex)
        {
            pipeline?.Stop();
            pipeline?.Dispose();
            mouseHook?.Dispose();
            _pipeline = null;
            _mouseHook = null;
            if (_activeRouteRecovery is not null) RestoreSourceRouting();
            StatusMessage = $"Failed to start: {ex.Message}";
        }
    }

    /// Also called from MainWindow.OnClosed, so closing the window restores the
    /// source's routing instead of leaving it pointed at the silent cable.
    /// Safe when nothing is running — every member touched is null-guarded.
    public void Stop()
    {
        _pipeline?.Stop();
        // Dispose after Stop: releases the spatializers' native resources
        // (Steam Audio HRTF context/effect), which would otherwise leak on
        // every Start->Stop->Start cycle.
        _pipeline?.Dispose();
        _mouseHook?.Dispose();
        _underrunTimer?.Stop();
        _underrunTimer?.Dispose();
        _underrunTimer = null;
        // Cleared so StartCommand.CanExecute goes true again and a fresh
        // Start() builds a new stack rather than stacking on a live one.
        _pipeline = null;
        _mouseHook = null;
        RestoreSourceRouting();
        StatusMessage = "Stopped";
        RestartRequired = false;
    }

    /// Called from MainWindow.OnClosed so the test tone never outlives the window.
    public void Cleanup()
    {
        _testTone?.Dispose();
        _testTone = null;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
