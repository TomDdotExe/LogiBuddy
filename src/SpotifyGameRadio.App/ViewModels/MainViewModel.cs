using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
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
    private RadioPipeline? _pipeline;
    private Win32MouseHook? _mouseHook;
    private System.Timers.Timer? _underrunTimer;

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<AudioSourceInfo> AvailableSources { get; private set; } = Array.Empty<AudioSourceInfo>();
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

    public RadioProfile Profile { get; private set; } = new();

    public ICommand RefreshSourcesCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand LoadProfileCommand { get; }

    public MainViewModel()
    {
        RefreshSourcesCommand = new RelayCommand(_ => RefreshSources());
        StartCommand = new RelayCommand(_ => Start());
        StopCommand = new RelayCommand(_ => Stop());
        SaveProfileCommand = new RelayCommand(_ => _configStore.Save(Profile));
        LoadProfileCommand = new RelayCommand(name => LoadProfile((string)name!));

        RefreshSources();
    }

    private void RefreshSources()
    {
        AvailableSources = AudioSessionEnumerator.ListActiveSources();
        OnPropertyChanged(nameof(AvailableSources));
    }

    private void LoadProfile(string name)
    {
        Profile = _configStore.Load(name);
        OnPropertyChanged(nameof(Profile));
    }

    private void Start()
    {
        // The construction sequence below touches real hardware (audio
        // devices, the global mouse hook) and can fail for reasons outside
        // our control (e.g. no default render device). Guard the whole
        // thing so a failure degrades to a status message instead of
        // crashing the app on the UI thread — see plan's Global Constraints
        // on graceful degradation, including output device loss.
        Win32MouseHook? mouseHook = null;
        RadioPipeline? pipeline = null;
        try
        {
            // Per-process loopback (WasapiProcessLoopbackCapture) requires Windows 10
            // build 19041 (20H1) or later. It has no internal OS-version check of its
            // own, so on older Windows it would just retry forever and report
            // AudioCaptureStatus.Error — select the whole-device loopback fallback
            // (WasapiDeviceLoopbackCapture) here instead, per the plan's Global
            // Constraints ("whole-device loopback fallback for older Windows").
            IAudioCaptureService activeCapture = Environment.OSVersion.Version.Build >= 19041
                ? new WasapiProcessLoopbackCapture()
                : new WasapiDeviceLoopbackCapture();

            var output = new WasapiAudioOutput();
            var effectChain = new RadioEffectChain(48000f);
            mouseHook = new Win32MouseHook(Profile.Hotkey);
            var tracker = new FreelookTracker(mouseHook, Profile);

            ISpatializer fallback = new StereoPanSpatializer();
            ISpatializer primary = fallback;
            if (SteamAudioSpatializer.TryCreate(48000, 1024, out var steamAudio) && steamAudio is not null)
                primary = steamAudio;

            pipeline = new RadioPipeline(activeCapture, output, primary, fallback, effectChain, tracker);
            pipeline.ApplyProfile(Profile);
            pipeline.Warning += (_, message) => Application.Current.Dispatcher.Invoke(() => StatusMessage = message);

            pipeline.Start();

            // Only commit to instance state once everything above has
            // succeeded, so a failed Start() never leaves _pipeline/_mouseHook
            // pointing at partially-initialized objects (would break Stop()
            // or a subsequent retry).
            _pipeline = pipeline;
            _mouseHook = mouseHook;
            StatusMessage = "Running";

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
        }
        catch (Exception ex)
        {
            pipeline?.Stop();
            mouseHook?.Dispose();
            _pipeline = null;
            _mouseHook = null;
            StatusMessage = $"Failed to start: {ex.Message}";
        }
    }

    private void Stop()
    {
        _pipeline?.Stop();
        _mouseHook?.Dispose();
        _underrunTimer?.Stop();
        _underrunTimer?.Dispose();
        _underrunTimer = null;
        StatusMessage = "Stopped";
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
