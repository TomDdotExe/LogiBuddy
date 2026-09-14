using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using LogiBuddy.App.Audio;
using LogiBuddy.Core.Audio;
using LogiBuddy.Core.Config;
using LogiBuddy.Core.Dsp;
using LogiBuddy.Core.Pipeline;
using LogiBuddy.Core.Spatial;
using LogiBuddy.Core.Speech;
using LogiBuddy.Core.Tracking;
using LogiBuddy.Core.Updates;

namespace LogiBuddy.App.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly IConfigStore _configStore = new ConfigStore();
    private readonly ISourceAudioRouter _router = new WindowsAppAudioRouter();
    private readonly RouteRecoveryStore _routeRecovery = new();
    private RouteRecoveryRecord? _activeRouteRecovery;
    private RadioPipeline? _pipeline;
    private Win32MouseHook? _mouseHook;
    private System.Windows.Threading.DispatcherTimer? _uiTimer;
    private readonly ISourceSessionMuter _sessionMuter = new NAudioSourceSessionMuter();
    private string? _mutedProcessName;
    private TapHotkeyWatcher? _recenterHotkeyWatcher;
    private TapHotkeyWatcher? _vehicleToggleHotkeyWatcher;
    private TapHotkeyWatcher? _overrideHotkeyWatcher;
    private bool _isInVehicle = true;
    private TapHotkeyWatcher? _outsideViewHotkeyWatcher;
    private bool _isOutsideView;
    /// Bound by SourcePositionCanvas to swap its visual between the inside
    /// drag-to-place layout and the outside orbit-ring layout.
    public bool IsOutsideView
    {
        get => _isOutsideView;
        private set { _isOutsideView = value; OnPropertyChanged(); }
    }
    private bool _isOverrideMuted;
    /// Bound by the Override button's Style DataTrigger and drives
    /// RadioPipeline.SetOverrideMuted via OnOverrideTogglePressed.
    public bool IsOverrideMuted
    {
        get => _isOverrideMuted;
        private set { _isOverrideMuted = value; OnPropertyChanged(); }
    }
    private System.Windows.Threading.DispatcherTimer? _vehicleTransitionTimer;
    // The direction currently being requested — distinct from _isInVehicle,
    // which only flips once a transition actually completes. Toggling twice
    // in quick succession (tap mode) while the first transition is still
    // pending needs to compare against "what did I just ask for", not "what
    // was last confirmed", or the second tap computes the same target as the
    // first instead of reversing it.
    private bool _vehicleWantInVehicle = true;
    private CalibrationSession? _calibrationSession;
    private TapHotkeyWatcher? _calibrateHotkeyWatcher;
    private readonly CalibrationAnnouncer _announcer;
    private readonly VoiceModelStore _voiceModelStore = new();
    private VoiceChatSession? _voiceSession;
    private LazyWhisperSpeechToText? _speechToText;
    private TapHotkeyWatcher? _voiceRecordWatcher;
    private VoicePreviewOverlay? _voiceOverlay;
    private System.Windows.Threading.DispatcherTimer? _voiceOverlayHideTimer;
    private readonly VoiceCuePlayer _voiceCue;

    // Shared by every hotkey watcher (and Win32MouseHook's freelook hold-key
    // poll) below, so suspending this one instance suspends all of them at
    // once — see _chatModeHotkeyWatcher/_chatModeExitWatcher.
    private readonly SuspendableKeyStateSource _keyStateGate = new(new Win32KeyStateSource());
    private readonly TapHotkeyWatcher _chatModeHotkeyWatcher;
    private readonly TapHotkeyWatcher _chatModeExitWatcher;

    private const string UpdateRepoOwner = "TomDdotExe";
    private const string UpdateRepoName = "LogiBuddy";
    private readonly IUpdateChecker _updateChecker = new GitHubUpdateChecker(UpdateRepoOwner, UpdateRepoName);
    private readonly IUpdateDownloader _updateDownloader = new HttpUpdateDownloader();
    private UpdateInfo? _pendingUpdate;

    private static readonly HashSet<string> LiveProfileProperties = new()
    {
        nameof(RadioProfile.HighPassHz), nameof(RadioProfile.LowPassHz),
        nameof(RadioProfile.DistortionDrive), nameof(RadioProfile.CompressorThresholdDb),
        nameof(RadioProfile.CompressorRatio), nameof(RadioProfile.NoiseLevel),
        nameof(RadioProfile.WetDryMix),
        nameof(RadioProfile.SourceX), nameof(RadioProfile.SourceY), nameof(RadioProfile.SourceZ),
        nameof(RadioProfile.MouseSensitivity), nameof(RadioProfile.MaxYawDegrees),
        nameof(RadioProfile.SpringBackRatePerSecond),
        nameof(RadioProfile.Volume), nameof(RadioProfile.StereoWidth),
        nameof(RadioProfile.FreelookAlwaysOn),
        nameof(RadioProfile.OutsideLowPassHz), nameof(RadioProfile.OutsideVolume),
        nameof(RadioProfile.OutsideStereoWidth), nameof(RadioProfile.OutsideSourceDistance),
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

    private string? _selectedProfileName;
    /// Backs the profile dropdown; Load/Delete act on whichever name is
    /// selected here, independent of Profile.Name (the text box for the
    /// currently-loaded profile's own name).
    public string? SelectedProfileName
    {
        get => _selectedProfileName;
        set { _selectedProfileName = value; OnPropertyChanged(); }
    }

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

    // Set by the silent startup update check when a newer release exists;
    // cleared otherwise. Bound to a small inline note next to the status
    // text — never a popup, so a startup check never interrupts.
    private string _updateAvailableMessage = "";
    public string UpdateAvailableMessage
    {
        get => _updateAvailableMessage;
        set { _updateAvailableMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasUpdateAvailable)); }
    }

    public bool HasUpdateAvailable => !string.IsNullOrEmpty(UpdateAvailableMessage);

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

    public RadioProfile Profile { get; private set; } = new();

    /// Whether per-app audio routing is available at all (Windows 11 + a
    /// reachable IAudioPolicyConfig factory). Fixed for the app's lifetime, so
    /// no change notification is needed; the routing UI row binds IsEnabled to it.
    public bool IsRoutingSupported => _router.IsSupported;

    public IReadOnlyList<CaptureDeviceInfo> AvailableCaptureDevices { get; private set; } = Array.Empty<CaptureDeviceInfo>();

    public bool VoiceModelReady => _voiceModelStore.IsDownloaded;

    public bool VoiceModelNotReady => !_voiceModelStore.IsDownloaded;

    private bool _voiceModelDownloading;
    public bool VoiceModelDownloading
    {
        get => _voiceModelDownloading;
        set { _voiceModelDownloading = value; OnPropertyChanged(); }
    }

    private double _voiceModelDownloadProgress;
    public double VoiceModelDownloadProgress
    {
        get => _voiceModelDownloadProgress;
        set { _voiceModelDownloadProgress = value; OnPropertyChanged(); }
    }

    public ICommand DownloadVoiceModelCommand { get; }

    public ICommand RefreshSourcesCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand LoadProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand ResetSourceRoutingCommand { get; }
    public ICommand RecenterCommand { get; }
    public ICommand CalibrateFreelookCommand { get; }
    public ICommand CheckForUpdatesCommand { get; }
    public ICommand OutsideViewToggleCommand { get; }
    public ICommand OverrideToggleCommand { get; }

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
        SaveProfileCommand = new RelayCommand(_ => SaveProfile());
        LoadProfileCommand = new RelayCommand(
            name => LoadProfile((string)name!),
            name => !string.IsNullOrEmpty(name as string));
        DeleteProfileCommand = new RelayCommand(
            name => DeleteProfile((string)name!),
            name => !string.IsNullOrEmpty(name as string));
        ResetSourceRoutingCommand = new RelayCommand(_ => ResetSourceRouting());
        RecenterCommand = new RelayCommand(_ => Recenter());
        CalibrateFreelookCommand = new RelayCommand(
            _ => StartOrAbortCalibration(),
            _ => _pipeline is not null
                 && _mouseHook is not null
                 && Profile.Hotkey.VirtualKeyCode != 0
                 && Profile.CalibrateHotkey.VirtualKeyCode != 0
                 && Profile.CalibrateHotkey.VirtualKeyCode != Profile.Hotkey.VirtualKeyCode);
        DownloadVoiceModelCommand = new RelayCommand(_ => _ = DownloadVoiceModelAsync(), _ => !VoiceModelDownloading);
        CheckForUpdatesCommand = new RelayCommand(_ => _ = CheckForUpdatesAsync(manual: true));
        OutsideViewToggleCommand = new RelayCommand(_ => OnOutsideViewTogglePressed(), _ => _pipeline is not null);
        OverrideToggleCommand = new RelayCommand(_ => OnOverrideTogglePressed(), _ => _pipeline is not null);
        _announcer = new CalibrationAnnouncer(() => Profile.OutputDeviceId);
        _voiceCue = new VoiceCuePlayer(() => Profile.OutputDeviceId);

        RefreshSources();
        Profile.PropertyChanged += OnProfilePropertyChanged;

        // A profile saved on a Windows 11 box would otherwise make every Start
        // fail on a machine where routing isn't available at all.
        if (!_router.IsSupported && Profile.AutoRouteSource) Profile.AutoRouteSource = false;

        _speechToText = new LazyWhisperSpeechToText(() => _voiceModelStore.ModelPath);
        _voiceSession = new VoiceChatSession(
            new LogiBuddy.Core.Audio.MicrophoneCapture(),
            _speechToText,
            () => VocabularyPromptBuilder.ParseTerms(Profile.VoiceCustomVocabulary),
            () => Profile.VoiceMicrophoneDeviceId);
        _voiceSession.StateChanged += OnVoiceStateChanged;
        _voiceSession.PreviewReady += transcript =>
        {
            try
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    Clipboard.SetText(transcript);
                    StatusMessage = "Copied to clipboard — paste it in, or hold Record to redo.";
                });
            }
            catch (Exception) { /* app is shutting down; nothing to copy */ }
        };
        _voiceSession.Failed += reason =>
        {
            try
            {
                Application.Current?.Dispatcher.Invoke(() => StatusMessage = reason);
            }
            catch (Exception) { /* app is shutting down; nothing to report */ }
        };

        _voiceRecordWatcher = new TapHotkeyWatcher(Profile.VoiceRecordHotkey, _keyStateGate);
        _voiceRecordWatcher.Pressed += () =>
        {
            try
            {
                Application.Current?.Dispatcher.Invoke(OnVoiceRecordPressed);
            }
            catch (Exception) { /* app is shutting down; nothing to record */ }
        };
        _voiceRecordWatcher.Released += () =>
        {
            try
            {
                Application.Current?.Dispatcher.Invoke(() => _voiceSession?.EndRecording());
            }
            catch (Exception) { /* app is shutting down; nothing to record */ }
        };

        // Both built on their own raw (never-suspended) key state source —
        // they're the switch, not something the switch should gate. Enter is
        // hardcoded rather than rebindable: it's the one key every chat box
        // already uses to confirm/send, so it always exits chat mode.
        //
        // Chat mode key toggles rather than only setting Suspended=true so a
        // single key rebound both ways still works if it's ever stuck (e.g.
        // Enter's own watcher below didn't fire). The exit watcher below
        // skips entirely when ChatModeHotkey IS Enter (a common real choice —
        // many games open AND send/close chat on the same key): with two
        // independent watchers polling the same physical key, both would
        // detect the same key-down edge and race to set Suspended, one
        // immediately undoing what the other just did.
        _chatModeHotkeyWatcher = new TapHotkeyWatcher(Profile.ChatModeHotkey, new Win32KeyStateSource());
        _chatModeHotkeyWatcher.Pressed += () =>
        {
            _keyStateGate.Suspended = !_keyStateGate.Suspended;
            string message = _keyStateGate.Suspended
                ? "Chat mode: hotkeys suspended until Enter."
                : "Chat mode: hotkeys active.";
            try
            {
                Application.Current?.Dispatcher.Invoke(() => StatusMessage = message);
            }
            catch (Exception) { /* app is shutting down; nothing to report */ }
        };
        _chatModeExitWatcher = new TapHotkeyWatcher(new FreelookHotkey { VirtualKeyCode = 0x0D }, new Win32KeyStateSource());
        _chatModeExitWatcher.Pressed += () =>
        {
            if (Profile.ChatModeHotkey.VirtualKeyCode == 0x0D) return; // handled by the toggle above instead
            if (!_keyStateGate.Suspended) return;
            _keyStateGate.Suspended = false;
            try
            {
                Application.Current?.Dispatcher.Invoke(() => StatusMessage = "Chat mode: hotkeys active.");
            }
            catch (Exception) { /* app is shutting down; nothing to report */ }
        };

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

        // Fire-and-forget: never blocks startup, never surfaces a failure
        // (offline, rate-limited, no releases yet all end up silent here).
        _ = CheckForUpdatesAsync(manual: false);
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
        else if (e.PropertyName == nameof(RadioProfile.OutsideViewHotkey))
        {
            _outsideViewHotkeyWatcher?.SetHotkey(Profile.OutsideViewHotkey);
        }
        else if (e.PropertyName == nameof(RadioProfile.OverrideHotkey))
        {
            _overrideHotkeyWatcher?.SetHotkey(Profile.OverrideHotkey);
        }
        else if (e.PropertyName == nameof(RadioProfile.VoiceRecordHotkey))
        {
            _voiceRecordWatcher?.SetHotkey(Profile.VoiceRecordHotkey);
        }
        else if (e.PropertyName == nameof(RadioProfile.ChatModeHotkey))
        {
            _chatModeHotkeyWatcher.SetHotkey(Profile.ChatModeHotkey);
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

        AvailableCaptureDevices = new[] { new CaptureDeviceInfo("", "(system default)") }
            .Concat(CaptureDeviceEnumerator.ListCaptureDevices())
            .ToList();
        OnPropertyChanged(nameof(AvailableCaptureDevices));
    }

    private async Task DownloadVoiceModelAsync()
    {
        VoiceModelDownloading = true;
        var progress = new Progress<double>(p => VoiceModelDownloadProgress = p);
        try
        {
            await _voiceModelStore.DownloadAsync(progress, CancellationToken.None);
            StatusMessage = "Voice model ready.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Voice model download failed: {ex.Message}";
        }
        finally
        {
            VoiceModelDownloading = false;
            OnPropertyChanged(nameof(VoiceModelReady));
            OnPropertyChanged(nameof(VoiceModelNotReady));
        }
    }

    /// Saves Profile under its current Name — always just a write to that
    /// name's file, never touching any other profile. Editing the Name box
    /// before saving therefore creates a new profile alongside the one it
    /// was loaded from rather than renaming it; use the Delete button next
    /// to the profile list to remove the old one if a rename was intended.
    private void SaveProfile()
    {
        _configStore.Save(Profile);
        OnPropertyChanged(nameof(AvailableProfiles));
    }

    private void DeleteProfile(string name)
    {
        _configStore.Delete(name);
        if (SelectedProfileName == name) SelectedProfileName = null;
        OnPropertyChanged(nameof(AvailableProfiles));
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
                _outsideViewHotkeyWatcher?.SetHotkey(Profile.OutsideViewHotkey);
                _overrideHotkeyWatcher?.SetHotkey(Profile.OverrideHotkey);
            }
            catch (Exception ex) { StatusMessage = $"Couldn't apply loaded profile: {ex.Message}"; }
            // Source process and routing still need a manual Stop/Start.
            RestartRequired = true;
        }

        // The voice hotkey watcher is constructor-scoped (it works without
        // the pipeline running), so it's re-wired unconditionally here rather
        // than inside the _pipeline-is-not-null block above.
        _voiceRecordWatcher?.SetHotkey(Profile.VoiceRecordHotkey);
    }

    /// Checks GitHub for a newer release. On startup (manual: false) any
    /// failure is swallowed — offline, rate-limited, or no releases yet all
    /// just mean no note appears. From the "Check for Updates" button
    /// (manual: true), a failure is reported and a confirmed up-to-date
    /// result gets an explicit acknowledgement instead of silence.
    private async Task CheckForUpdatesAsync(bool manual)
    {
        var currentVersion = GetCurrentVersion();
        try
        {
            var info = await _updateChecker.CheckForUpdateAsync(currentVersion);
            _pendingUpdate = info;
            if (info is not null)
            {
                UpdateAvailableMessage = $"Update available: v{info.Version}";
                if (manual) await TryUpdateAsync(info);
            }
            else
            {
                UpdateAvailableMessage = "";
                if (manual)
                {
                    MessageBox.Show($"You're up to date (v{currentVersion}).", "Check for Updates",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
        catch (Exception ex)
        {
            if (manual)
            {
                MessageBox.Show($"Couldn't check for updates: {ex.Message}", "Check for Updates",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    /// Offers the update: in-app download+install when this is an installed
    /// copy (InstallDetector.IsInstalled) and the release actually published
    /// a *-setup.exe asset, otherwise falls back to the original
    /// open-the-release-page behavior (portable zip installs, or a release
    /// that only published a zip).
    private async Task TryUpdateAsync(UpdateInfo info)
    {
        var notes = string.IsNullOrWhiteSpace(info.ReleaseNotes) ? "(no patch notes provided)" : info.ReleaseNotes;
        bool canAutoUpdate = info.InstallerAssetUrl is not null && InstallDetector.IsInstalled(AppContext.BaseDirectory);

        var result = MessageBox.Show(
            $"Version {info.Version} is available.\n\n{notes}\n\n{(canAutoUpdate ? "Download and install now?" : "Open the release page?")}",
            "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (result != MessageBoxResult.Yes) return;

        if (!canAutoUpdate)
        {
            OpenReleasePage(info.HtmlUrl);
            return;
        }

        await DownloadAndInstallAsync(info);
    }

    private static void OpenReleasePage(string htmlUrl)
    {
        if (string.IsNullOrEmpty(htmlUrl)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(htmlUrl) { UseShellExecute = true });
        }
        catch { /* best effort; nothing sensible to do if the browser won't launch */ }
    }

    /// Downloads the installer, confirms once more, then hands off to it and
    /// exits. The spawned command waits 2s before running the installer so
    /// our own Shutdown() below has time to fully release LogiBuddy.exe's
    /// file lock before Inno tries to overwrite it — installer.iss's [Run]
    /// section (skipifsilent removed) relaunches the app once install
    /// finishes.
    private async Task DownloadAndInstallAsync(UpdateInfo info)
    {
        // A random name per attempt, in a directory this app owns, rather
        // than a fixed name in the shared system temp root — a predictable
        // path is a TOCTOU target (something else could plant or swap a file
        // there in the gap between us placing the verified download and the
        // delayed command below actually running it).
        var updateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LogiBuddy", "Updates");
        Directory.CreateDirectory(updateDir);
        CleanupStaleInstallers(updateDir);
        var installerPath = Path.Combine(updateDir, $"LogiBuddy-update-{Guid.NewGuid():N}.exe");

        StatusMessage = $"Downloading update v{info.Version}...";
        try
        {
            await _updateDownloader.DownloadAsync(info.InstallerAssetUrl!, installerPath, info.InstallerAssetSha256);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't download the update: {ex.Message}";
            MessageBox.Show($"Couldn't download the update: {ex.Message}", "Update Failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        StatusMessage = "";

        var confirm = MessageBox.Show(
            $"Ready to install v{info.Version} — the app will close, update, and reopen. Continue?",
            "Install Update", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c timeout /t 2 /nobreak >nul && \"{installerPath}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't launch the installer: {ex.Message}", "Update Failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Application.Current.Shutdown();
    }

    /// Best-effort cleanup of installers left behind by earlier update
    /// attempts (declined installs, crashes before relaunch, etc.) — each
    /// attempt gets its own GUID-named file, so nothing here is reused
    /// across runs and this is purely tidiness, never load-bearing.
    private static void CleanupStaleInstallers(string updateDir)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(updateDir, "LogiBuddy-update-*.exe"))
                File.Delete(file);
        }
        catch { /* best effort; a leftover file or two is harmless */ }
    }

    /// Reads the version .csproj/package.ps1 stamp onto the assembly
    /// (AssemblyInformationalVersion, set via -p:Version at publish) so the
    /// update check compares against what was actually shipped.
    private static string GetCurrentVersion()
    {
        var raw = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";
        // The SDK appends "+<git-commit-hash>" (SourceRevisionId) to
        // InformationalVersion by default — meaningless noise for a
        // user-facing version string or for comparing against a release tag.
        var plusIndex = raw.IndexOf('+');
        return plusIndex >= 0 ? raw[..plusIndex] : raw;
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

    /// Fires on the vehicle-toggle hotkey's key-down edge. In tap-to-toggle
    /// mode (default) the tap immediately requests the opposite of whatever
    /// direction is currently requested — see _vehicleWantInVehicle — and
    /// RequestVehicleState starts counting down the matching delay right
    /// away; nothing here waits on a key-up. In hold-to-exit mode, a press
    /// requests the opposite of the last CONFIRMED state and, since holding
    /// is required, that request only completes if the hold survives the
    /// full delay — see OnVehicleToggleReleased for the cancel path.
    private void OnVehicleTogglePressed()
    {
        RequestVehicleState(Profile.HoldToExitVehicle ? !_isInVehicle : !_vehicleWantInVehicle);
    }

    /// Fires on the vehicle-toggle hotkey's key-up edge. Ignored in
    /// tap-to-toggle mode — the press already started the (release-proof)
    /// delay timer. In hold-to-exit mode, releasing before that timer has
    /// fired cancels the attempt outright: nothing was muted/unmuted yet, so
    /// there's nothing to roll back, just a status message telling the user
    /// they let go too early. A release after the timer already fired is a
    /// no-op (RequestVehicleState(_isInVehicle) below just re-confirms the
    /// state that's already true).
    private void OnVehicleToggleReleased()
    {
        if (!Profile.HoldToExitVehicle) return;

        bool cancelling = _vehicleTransitionTimer is not null;
        RequestVehicleState(_isInVehicle);
        if (cancelling)
            StatusMessage = _isInVehicle
                ? "Exit cancelled — hold the full duration to exit."
                : "Re-entry cancelled — hold the full duration to get back in.";
    }

    /// Single entry point for both vehicle-toggle modes: requests that the
    /// source end up muted (wantInVehicle: false) or unmuted (true).
    /// Requesting whatever _isInVehicle already is cancels any transition
    /// still counting down (nothing changed yet, so there's nothing to
    /// finish) and otherwise does nothing. Requesting the opposite
    /// (re)starts a fresh countdown of Profile.VehicleExitDelaySeconds or
    /// VehicleEnterDelaySeconds — 0 completes immediately — timed from this
    /// call, not from any later key-up.
    private void RequestVehicleState(bool wantInVehicle)
    {
        _vehicleWantInVehicle = wantInVehicle;
        _vehicleTransitionTimer?.Stop();
        _vehicleTransitionTimer = null;

        if (wantInVehicle == _isInVehicle)
        {
            StatusMessage = _isInVehicle ? "In vehicle" : "Out of vehicle";
            return;
        }

        float delaySeconds = ClampDelaySeconds(wantInVehicle ? Profile.VehicleEnterDelaySeconds : Profile.VehicleExitDelaySeconds);
        if (delaySeconds <= 0f)
        {
            CompleteVehicleTransition(wantInVehicle);
            return;
        }

        StatusMessage = wantInVehicle
            ? $"Entering vehicle — reactivating in {delaySeconds:0.#}s"
            : $"Exiting vehicle — muting in {delaySeconds:0.#}s";
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(delaySeconds)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _vehicleTransitionTimer = null;
            CompleteVehicleTransition(wantInVehicle);
        };
        _vehicleTransitionTimer = timer;
        timer.Start();
    }

    private void CompleteVehicleTransition(bool inVehicle)
    {
        _isInVehicle = inVehicle;
        _pipeline?.SetVehicleMuted(!inVehicle);
        StatusMessage = inVehicle ? "In vehicle" : "Out of vehicle";
    }

    /// Fires on the outside-view hotkey's tap (and the window button, via the
    /// same handler). Swaps the pipeline's tone between the profile's normal
    /// (inside) values and its Outside* values. Ignored while out of the
    /// vehicle (_isInVehicle false): an outside/inside cockpit distinction
    /// doesn't mean anything once you're not even at the vehicle, and
    /// disabling the key there means an accidental press while typing
    /// in-game chat (the same key easily doubling as a chat character)
    /// can't silently flip it.
    private void OnOutsideViewTogglePressed()
    {
        if (!_isInVehicle)
        {
            StatusMessage = "Outside view is only available while in the vehicle.";
            return;
        }

        IsOutsideView = !IsOutsideView;
        _pipeline?.SetOutsideView(_isOutsideView);
        StatusMessage = _isOutsideView ? "Outside view" : "Inside cockpit";
    }

    /// Fires on the override hotkey's tap (and the window button, via the
    /// same handler). Instantly mutes/un-mutes the whole radio, independent
    /// of the Vehicle in/out toggle — no delay, no in-vehicle gate.
    private void OnOverrideTogglePressed()
    {
        IsOverrideMuted = !IsOverrideMuted;
        _pipeline?.SetOverrideMuted(IsOverrideMuted);
        StatusMessage = IsOverrideMuted ? "Radio muted (override)" : "Radio unmuted (override)";
    }

    private void OnVoiceRecordPressed()
    {
        if (!_voiceModelStore.IsDownloaded)
        {
            StatusMessage = "Voice model not downloaded yet — see the Voice Chat card.";
            return;
        }
        _voiceSession?.BeginRecording();
    }

    /// Drives the on-screen preview overlay. Idle hides it immediately.
    /// Recording/Transcribing show a status line with no hint (nothing to
    /// action yet). PreviewReady shows the transcript — already copied to
    /// the clipboard by the PreviewReady handler wired in the constructor —
    /// and starts a timer to auto-hide the overlay after a few seconds so it
    /// doesn't linger over gameplay. Any state change (including a
    /// re-record starting) cancels a pending hide. The hint additionally
    /// flags a low-confidence transcript (Whisper itself was unsure) so the
    /// user knows to double-check before relying on what got copied, rather
    /// than trusting a wrong-looking sentence with no warning at all.
    private void OnVoiceStateChanged(VoiceChatState state)
    {
        try
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                _voiceOverlayHideTimer?.Stop();
                _voiceOverlayHideTimer = null;

                if (state == VoiceChatState.Idle)
                {
                    _voiceOverlay?.Hide();
                    return;
                }

                if (state == VoiceChatState.Recording) _voiceCue.PlayRecordStartBeep();
                else if (state == VoiceChatState.Transcribing) _voiceCue.PlayTranscribeStartBeep();

                _voiceOverlay ??= new VoicePreviewOverlay();
                _voiceOverlay.SetText(state switch
                {
                    VoiceChatState.Recording => "Listening…",
                    VoiceChatState.Transcribing => "Transcribing…",
                    VoiceChatState.PreviewReady => _voiceSession?.Transcript ?? "",
                    _ => "",
                });
                _voiceOverlay.SetHint(state == VoiceChatState.PreviewReady
                    ? _voiceSession?.LastConfidence < VocabularyCorrector.LowConfidenceThreshold
                        ? "Low confidence — double-check before using. Hold Record to redo."
                        : "Copied to clipboard — hold Record to redo."
                    : "");
                _voiceOverlay.Show();

                if (state == VoiceChatState.PreviewReady)
                {
                    var timer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(6)
                    };
                    timer.Tick += (_, _) =>
                    {
                        timer.Stop();
                        _voiceOverlay?.Hide();
                    };
                    _voiceOverlayHideTimer = timer;
                    timer.Start();
                }
            });
        }
        catch (Exception) { /* app is shutting down; nothing to show */ }
    }

    /// Shared NaN/Infinity/negative guard for the two vehicle transition
    /// delays — a slider only ever produces a finite non-negative value, but
    /// a hand-edited profile JSON might not.
    private static float ClampDelaySeconds(float delaySeconds)
    {
        if (float.IsNaN(delaySeconds) || float.IsInfinity(delaySeconds)) return 0f;
        return Math.Clamp(delaySeconds, 0f, 60f);
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
    /// path does not go through it. Runs Cockpit or Outside calibration
    /// depending on which view is currently active (IsOutsideView) — the two
    /// measure different things (a game-specific yaw limit vs a full 360°
    /// spin plus a 180° vertical sweep) and write to different profile
    /// fields, so this decides which one the button/hotkey will run before
    /// the session ever starts, and Completed below assumes that decision
    /// matches whichever result comes back.
    private void BeginCalibration()
    {
        if (_calibrationSession is not null) return;
        if (_pipeline is null || _mouseHook is null) return;
        if (Profile.Hotkey.VirtualKeyCode == 0
            || Profile.CalibrateHotkey.VirtualKeyCode == 0
            || Profile.CalibrateHotkey.VirtualKeyCode == Profile.Hotkey.VirtualKeyCode)
            return;

        Recenter();

        var mode = IsOutsideView ? CalibrationMode.Outside : CalibrationMode.Cockpit;
        var session = new CalibrationSession(_mouseHook);

        session.StepChanged += (step, message) => Application.Current?.Dispatcher.Invoke(() =>
        {
            StatusMessage = message;
            _announcer.Say(StepPhrase(step));
        });
        session.Completed += result => Application.Current?.Dispatcher.Invoke(() =>
        {
            if (mode == CalibrationMode.Outside)
            {
                // A full 360° spin is a fixed geometric constant, unlike the
                // cockpit's game-specific yaw limit — so no MaxYawDegrees (or
                // equivalent) lookup is needed here. Pitch is not calibrated
                // at all — see CalibrationSession's class doc for why.
                float yawSens = 360f / result.SweepCounts;
                Profile.OutsideYawSensitivity = yawSens;

                _announcer.Say("Mark set. Calibration complete.");
                StatusMessage =
                    $"Calibration complete — outside yaw sensitivity {yawSens:0.####}. " +
                    "Click Save Profile to keep it.";
            }
            else
            {
                float sens = Profile.MaxYawDegrees / result.SweepCounts;
                Profile.MouseSensitivity = sens;

                _announcer.Say("Mark set. Calibration complete.");
                StatusMessage =
                    $"Calibration complete — sensitivity {sens:0.####} (using Max yaw = {Profile.MaxYawDegrees:0.#}°). " +
                    "Click Save Profile to keep it.";
            }
            TeardownCalibration();
        });
        session.Ended += reason => Application.Current?.Dispatcher.Invoke(() =>
        {
            StatusMessage = reason;
            _announcer.Say(reason);
            TeardownCalibration();
        });

        _calibrationSession = session;
        session.Start(mode);
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
        CalibrationStep.AwaitRightLimit => "Calibration started. Face forward, then turn right until the view stops, and tap.",
        CalibrationStep.AwaitFullSpin => "Calibration started. Spin all the way around, then tap.",
        _ => "",
    };

    private void Start()
    {
        // Unconditional: hotkeys must work the moment Start is pressed,
        // regardless of whatever chat-mode state a previous session (or a
        // rebind that fired while the new key was still physically held —
        // TapHotkeyWatcher.SetHotkey does this deliberately) left behind.
        _keyStateGate.Suspended = false;

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
        TapHotkeyWatcher? outsideViewHotkeyWatcher = null;
        TapHotkeyWatcher? overrideHotkeyWatcher = null;
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
            mouseHook = new Win32MouseHook(Profile.Hotkey, _keyStateGate);
            recenterHotkeyWatcher = new TapHotkeyWatcher(Profile.RecenterHotkey, _keyStateGate);
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

            vehicleToggleHotkeyWatcher = new TapHotkeyWatcher(Profile.VehicleToggleHotkey, _keyStateGate);
            vehicleToggleHotkeyWatcher.Pressed += () =>
            {
                try
                {
                    Application.Current?.Dispatcher.Invoke(OnVehicleTogglePressed);
                }
                catch (Exception) { /* app is shutting down; nothing to toggle */ }
            };
            vehicleToggleHotkeyWatcher.Released += () =>
            {
                try
                {
                    Application.Current?.Dispatcher.Invoke(OnVehicleToggleReleased);
                }
                catch (Exception) { /* app is shutting down; nothing to toggle */ }
            };

            calibrateHotkeyWatcher = new TapHotkeyWatcher(Profile.CalibrateHotkey, _keyStateGate);
            calibrateHotkeyWatcher.Pressed += () =>
            {
                try
                {
                    Application.Current?.Dispatcher.Invoke(OnCalibrateHotkeyPressed);
                }
                catch (Exception) { /* app is shutting down */ }
            };

            outsideViewHotkeyWatcher = new TapHotkeyWatcher(Profile.OutsideViewHotkey, _keyStateGate);
            outsideViewHotkeyWatcher.Pressed += () =>
            {
                try
                {
                    Application.Current?.Dispatcher.Invoke(OnOutsideViewTogglePressed);
                }
                catch (Exception) { /* app is shutting down; nothing to toggle */ }
            };

            overrideHotkeyWatcher = new TapHotkeyWatcher(Profile.OverrideHotkey, _keyStateGate);
            overrideHotkeyWatcher.Pressed += () =>
            {
                try
                {
                    Application.Current?.Dispatcher.Invoke(OnOverrideTogglePressed);
                }
                catch (Exception) { /* app is shutting down; nothing to toggle */ }
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
            _vehicleWantInVehicle = true;
            _vehicleTransitionTimer?.Stop();
            _vehicleTransitionTimer = null;
            // Outside view starts on by default — most sessions begin outside
            // the vehicle before getting in, so this matches the common case
            // rather than requiring a hotkey press every launch.
            IsOutsideView = true;
            pipeline.SetOutsideView(true);
            IsOverrideMuted = false;
            pipeline.SetOverrideMuted(false);
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
            _outsideViewHotkeyWatcher = outsideViewHotkeyWatcher;
            _overrideHotkeyWatcher = overrideHotkeyWatcher;

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
            outsideViewHotkeyWatcher?.Dispose();
            overrideHotkeyWatcher?.Dispose();
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
        _vehicleTransitionTimer?.Stop();
        _vehicleTransitionTimer = null;
        _recenterHotkeyWatcher?.Dispose();
        _vehicleToggleHotkeyWatcher?.Dispose();
        _calibrateHotkeyWatcher?.Dispose();
        _outsideViewHotkeyWatcher?.Dispose();
        _overrideHotkeyWatcher?.Dispose();
        _recenterHotkeyWatcher = null;
        _vehicleToggleHotkeyWatcher = null;
        _calibrateHotkeyWatcher = null;
        _outsideViewHotkeyWatcher = null;
        _overrideHotkeyWatcher = null;
        IsOutsideView = false;
        IsOverrideMuted = false;
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

    /// Called from MainWindow.OnClosed to release resources that don't
    /// belong to the capture pipeline (already torn down by Stop()).
    public void Cleanup()
    {
        if (_calibrationSession is not null)
        {
            _calibrationSession.Abort();
            TeardownCalibration();
        }
        _announcer.Dispose();
        _voiceCue.Dispose();
        _voiceRecordWatcher?.Dispose();
        _chatModeHotkeyWatcher.Dispose();
        _chatModeExitWatcher.Dispose();
        _voiceOverlayHideTimer?.Stop();
        _voiceSession?.Dispose();
        _speechToText?.Dispose();
        _voiceOverlay?.Close();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// Defers constructing the real WhisperFactory until the model file is
    /// actually present — MainViewModel builds this at startup, before the
    /// user may have downloaded the model yet.
    private sealed class LazyWhisperSpeechToText : ISpeechToText, IDisposable
    {
        private readonly Func<string> _modelPath;
        private WhisperSpeechToText? _inner;

        public LazyWhisperSpeechToText(Func<string> modelPath) => _modelPath = modelPath;

        public async Task<TranscriptionResult> TranscribeAsync(float[] pcm16kMono, IReadOnlyList<string> vocabularyTerms, CancellationToken ct)
        {
            // WhisperFactory.FromPath (inside the WhisperSpeechToText ctor) loads
            // the ~488 MB model file synchronously — moved off the calling
            // (UI) thread so first use doesn't freeze the app. The per-call
            // ProcessAsync inference below is already async via Whisper.net.
            _inner ??= await Task.Run(() => new WhisperSpeechToText(_modelPath()), ct);
            return await _inner.TranscribeAsync(pcm16kMono, vocabularyTerms, ct);
        }

        public void Dispose() => _inner?.Dispose();
    }
}
