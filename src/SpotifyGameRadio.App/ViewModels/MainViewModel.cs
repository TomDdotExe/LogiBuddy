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
        var mouseHook = new Win32MouseHook(Profile.Hotkey);
        _mouseHook = mouseHook;
        var tracker = new FreelookTracker(mouseHook, Profile);

        ISpatializer fallback = new StereoPanSpatializer();
        ISpatializer primary = fallback;
        if (SteamAudioSpatializer.TryCreate(48000, 1024, out var steamAudio) && steamAudio is not null)
            primary = steamAudio;

        _pipeline = new RadioPipeline(activeCapture, output, primary, fallback, effectChain, tracker);
        _pipeline.ApplyProfile(Profile);
        _pipeline.Warning += (_, message) => Application.Current.Dispatcher.Invoke(() => StatusMessage = message);

        _pipeline.Start();
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
    }

    private void Stop()
    {
        _pipeline?.Stop();
        _mouseHook?.Dispose();
        StatusMessage = "Stopped";
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
