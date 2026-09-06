using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace SpotifyGameRadio.Core.Audio;

public class WasapiProcessLoopbackCapture : IAudioCaptureService
{
    private const int AUDCLNT_SHAREMODE_SHARED = 0;
    private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    private const int RetryIntervalMs = 2000;

    private readonly object _stateLock = new();

    private WasapiProcessLoopbackInterop.IAudioClient? _rawAudioClient;
    private WasapiProcessLoopbackInterop.IAudioCaptureClient? _rawCaptureClient;
    private AutoResetEvent? _eventHandle;
    private Thread? _worker;
    private volatile bool _running;
    private volatile bool _stopRequested;
    private string? _processName;
    private int _targetProcessId;

    public WaveFormat Format { get; private set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    public event EventHandler<AudioCaptureStatus>? StatusChanged;
    public event EventHandler<float[]>? DataAvailable;

    public void Start(string processName)
    {
        // Re-entrant Start: tear the previous worker down first so we can't
        // orphan a live capture thread / COM client.
        if (_worker is not null) Stop();

        _processName = processName;
        _stopRequested = false;

        // ActivateAudioInterfaceAsync delivers its completion callback on a COM
        // MTA worker thread, and the process-loopback IAudioClient it returns has
        // no proxy/stub, so it can only be QueryInterface'd / used from the MTA
        // apartment it was created in. The WPF Start button calls in on the STA
        // UI thread, so the whole activation + capture lifetime has to live on one
        // dedicated MTA thread — casting the activated interface from the STA
        // thread is what fails with E_NOINTERFACE.
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "SGR-ProcessLoopback" };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    private void WorkerLoop()
    {
        try
        {
            while (!_stopRequested)
            {
                if (TryActivateAndStart())
                    CaptureLoop(); // blocks until Stop() or an unrecoverable error

                if (_stopRequested) break;

                // Back off before retrying (source not started yet, or a transient
                // activation failure), polling _stopRequested so Stop() stays snappy.
                for (int waited = 0; waited < RetryIntervalMs && !_stopRequested; waited += 50)
                    Thread.Sleep(50);
            }
        }
        finally
        {
            ReleaseCaptureResources();
        }
    }

    private bool TryActivateAndStart()
    {
        var process = Process.GetProcessesByName(_processName).FirstOrDefault();
        if (process is null)
        {
            StatusChanged?.Invoke(this, AudioCaptureStatus.NoSource);
            return false;
        }

        IntPtr formatPtr = IntPtr.Zero;
        AutoResetEvent? eventHandle = null;
        bool committed = false;
        try
        {
            var rawAudioClient = ActivateProcessLoopbackAudioClient((uint)process.Id);

            const int sampleRate = 48000, channels = 2, bitsPerSample = 32;
            formatPtr = AllocIeeeFloatWaveFormat(sampleRate, channels, bitsPerSample);

            int hr = rawAudioClient.Initialize(
                AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                2_000_000, // 200ms buffer, 100ns units
                0,
                formatPtr,
                IntPtr.Zero);
            if (hr != 0) throw new InvalidOperationException($"IAudioClient.Initialize failed, hresult=0x{hr:X}");

            eventHandle = new AutoResetEvent(false);
            hr = rawAudioClient.SetEventHandle(eventHandle.SafeWaitHandle.DangerousGetHandle());
            if (hr != 0) throw new InvalidOperationException($"IAudioClient.SetEventHandle failed, hresult=0x{hr:X}");

            var captureClientGuid = typeof(WasapiProcessLoopbackInterop.IAudioCaptureClient).GUID;
            hr = rawAudioClient.GetService(ref captureClientGuid, out var serviceObj);
            if (hr != 0 || serviceObj is null)
                throw new InvalidOperationException($"IAudioClient.GetService failed, hresult=0x{hr:X}");
            var rawCaptureClient = (WasapiProcessLoopbackInterop.IAudioCaptureClient)serviceObj;

            hr = rawAudioClient.Start();
            if (hr != 0) throw new InvalidOperationException($"IAudioClient.Start failed, hresult=0x{hr:X}");

            lock (_stateLock)
            {
                _rawAudioClient = rawAudioClient;
                _rawCaptureClient = rawCaptureClient;
                _eventHandle = eventHandle;
                _targetProcessId = process.Id;
                Format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
                _running = true;
            }
            committed = true;

            DebugLog($"capture started for '{_processName}' (pid={process.Id}, apartment={Thread.CurrentThread.GetApartmentState()})");
            StatusChanged?.Invoke(this, AudioCaptureStatus.Capturing);
            return true;
        }
        catch (Exception ex)
        {
            if (!committed) eventHandle?.Dispose();
            DebugLog($"TryActivateAndStart failed (apartment={Thread.CurrentThread.GetApartmentState()}): {ex}");
            StatusChanged?.Invoke(this, AudioCaptureStatus.Error);
            return false;
        }
        finally
        {
            if (formatPtr != IntPtr.Zero) Marshal.FreeHGlobal(formatPtr);
        }
    }

    private static WasapiProcessLoopbackInterop.IAudioClient ActivateProcessLoopbackAudioClient(uint processId)
    {
        var activationParams = new WasapiProcessLoopbackInterop.AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = WasapiProcessLoopbackInterop.AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
            ProcessLoopbackMode = WasapiProcessLoopbackInterop.PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE,
            TargetProcessId = processId
        };

        IntPtr paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WasapiProcessLoopbackInterop.AUDIOCLIENT_ACTIVATION_PARAMS>());
        IntPtr propvariantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WasapiProcessLoopbackInterop.PROPVARIANT_BLOB>());
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);

            var propvariant = new WasapiProcessLoopbackInterop.PROPVARIANT_BLOB
            {
                vt = WasapiProcessLoopbackInterop.VT_BLOB,
                blobCbSize = (uint)Marshal.SizeOf<WasapiProcessLoopbackInterop.AUDIOCLIENT_ACTIVATION_PARAMS>(),
                blobPBlobData = paramsPtr
            };
            Marshal.StructureToPtr(propvariant, propvariantPtr, false);

            var handler = new ActivationCompletionHandler();
            WasapiProcessLoopbackInterop.ActivateAudioInterfaceAsync(
                WasapiProcessLoopbackInterop.VirtualAudioDeviceProcessLoopback,
                WasapiProcessLoopbackInterop.IID_IAudioClient,
                propvariantPtr,
                handler,
                out _);

            handler.Wait();
            if (handler.ActivateResult != 0 || handler.ActivatedInterface is null)
                throw new InvalidOperationException($"ActivateAudioInterfaceAsync failed, hresult=0x{handler.ActivateResult:X}");

            return (WasapiProcessLoopbackInterop.IAudioClient)handler.ActivatedInterface;
        }
        finally
        {
            Marshal.FreeHGlobal(paramsPtr);
            Marshal.FreeHGlobal(propvariantPtr);
        }
    }

    private static IntPtr AllocIeeeFloatWaveFormat(int sampleRate, int channels, int bitsPerSample)
    {
        IntPtr ptr = Marshal.AllocHGlobal(18);
        Marshal.WriteInt16(ptr, 0, 3); // wFormatTag = WAVE_FORMAT_IEEE_FLOAT
        Marshal.WriteInt16(ptr, 2, (short)channels);
        Marshal.WriteInt32(ptr, 4, sampleRate);
        int blockAlign = channels * bitsPerSample / 8;
        Marshal.WriteInt32(ptr, 8, sampleRate * blockAlign); // nAvgBytesPerSec
        Marshal.WriteInt16(ptr, 12, (short)blockAlign);
        Marshal.WriteInt16(ptr, 14, (short)bitsPerSample);
        Marshal.WriteInt16(ptr, 16, 0); // cbSize
        return ptr;
    }

    /// Runs on the dedicated MTA worker thread, straight after a successful
    /// TryActivateAndStart, so the COM capture client is only ever touched from
    /// the apartment it was activated in. Returns on Stop() or an unrecoverable
    /// error; WorkerLoop decides whether to retry.
    private void CaptureLoop()
    {
        AutoResetEvent? eventHandle;
        WasapiProcessLoopbackInterop.IAudioCaptureClient? captureClient;
        lock (_stateLock)
        {
            eventHandle = _eventHandle;
            captureClient = _rawCaptureClient;
        }
        if (eventHandle is null || captureClient is null)
        {
            _running = false;
            StatusChanged?.Invoke(this, AudioCaptureStatus.Error);
            return;
        }

        try
        {
            while (_running && !_stopRequested)
            {
                eventHandle.WaitOne(200);
                if (!_running || _stopRequested) break;

                if (!IsTargetProcessAlive())
                {
                    _running = false;
                    ReleaseCaptureResources();
                    StatusChanged?.Invoke(this, AudioCaptureStatus.NoSource);
                    return;
                }

                captureClient.GetNextPacketSize(out uint packetFrames);
                while (packetFrames > 0)
                {
                    captureClient.GetBuffer(out IntPtr dataPtr, out uint framesAvailable, out uint flags, out _, out _);

                    int sampleCount = (int)framesAvailable * 2; // stereo
                    var samples = new float[sampleCount];
                    if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0)
                        Marshal.Copy(dataPtr, samples, 0, sampleCount);

                    DataAvailable?.Invoke(this, samples);

                    captureClient.ReleaseBuffer(framesAvailable);
                    captureClient.GetNextPacketSize(out packetFrames);
                }
            }
        }
        catch (Exception ex)
        {
            _running = false;
            ReleaseCaptureResources();
            DebugLog($"CaptureLoop failed (apartment={Thread.CurrentThread.GetApartmentState()}): {ex}");
            StatusChanged?.Invoke(this, AudioCaptureStatus.Error);
        }
    }

    private static void DebugLog(string message)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sgr-capture-debug.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}\n\n");
        }
        catch { /* diagnostic-only, never let logging itself throw */ }
    }

    private bool IsTargetProcessAlive()
    {
        try
        {
            using var process = Process.GetProcessById(_targetProcessId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no process with this id anymore
        }
        catch (Win32Exception)
        {
            return false; // no query rights (process likely gone/replaced)
        }
        catch (InvalidOperationException)
        {
            return false; // process access denied
        }
    }

    /// Best-effort teardown of the native COM/event-handle state, guarded by
    /// _stateLock so it can't interleave with TryActivateAndStart's field
    /// assignment. Safe to call from Stop(), from the worker thread itself when
    /// it detects the target process has exited or hits an unrecoverable error,
    /// and from WorkerLoop's finally on exit.
    private void ReleaseCaptureResources()
    {
        lock (_stateLock)
        {
            try { _rawAudioClient?.Stop(); }
            catch (Exception) { /* best-effort; client may already be in a bad state */ }

            _rawCaptureClient = null;
            _rawAudioClient = null;
            _eventHandle?.Dispose();
            _eventHandle = null;
        }
    }

    public void Stop()
    {
        _stopRequested = true;
        _running = false;

        Thread? worker;
        lock (_stateLock)
        {
            worker = _worker;
            // Wake CaptureLoop's WaitOne right away. Held under _stateLock so this
            // can't race ReleaseCaptureResources disposing the same handle.
            try { _eventHandle?.Set(); }
            catch (ObjectDisposedException) { /* already torn down */ }
        }

        worker?.Join(2000);
        _worker = null;

        ReleaseCaptureResources();
    }

    public void Dispose() => Stop();
}
