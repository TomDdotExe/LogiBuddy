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

    private readonly object _stateLock = new();

    private WasapiProcessLoopbackInterop.IAudioClient? _rawAudioClient;
    private WasapiProcessLoopbackInterop.IAudioCaptureClient? _rawCaptureClient;
    private AutoResetEvent? _eventHandle;
    private Thread? _captureThread;
    private volatile bool _running;
    private System.Timers.Timer? _retryTimer;
    private string? _processName;
    private int _targetProcessId;

    public WaveFormat Format { get; private set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    public event EventHandler<AudioCaptureStatus>? StatusChanged;
    public event EventHandler<float[]>? DataAvailable;

    public void Start(string processName)
    {
        _processName = processName;
        TryStart();

        _retryTimer = new System.Timers.Timer(2000);
        _retryTimer.Elapsed += (_, _) => { if (!_running) TryStart(); };
        _retryTimer.Start();
    }

    private void TryStart()
    {
        var process = Process.GetProcessesByName(_processName).FirstOrDefault();
        if (process is null)
        {
            StatusChanged?.Invoke(this, AudioCaptureStatus.NoSource);
            return;
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
                _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "SGR-ProcessLoopback" };
                _captureThread.Start();
            }
            committed = true;

            StatusChanged?.Invoke(this, AudioCaptureStatus.Capturing);
        }
        catch (Exception)
        {
            if (!committed) eventHandle?.Dispose();
            StatusChanged?.Invoke(this, AudioCaptureStatus.Error);
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

    private void CaptureLoop()
    {
        // Captured once at thread start: assigned by TryStart inside _stateLock
        // immediately before this thread is started, so these are guaranteed
        // non-null and visible here via the Thread.Start() memory barrier.
        var eventHandle = _eventHandle;
        var captureClient = _rawCaptureClient;
        if (eventHandle is null || captureClient is null)
        {
            _running = false;
            StatusChanged?.Invoke(this, AudioCaptureStatus.Error);
            return;
        }

        try
        {
            while (_running)
            {
                eventHandle.WaitOne(200);
                if (!_running) break;

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
        catch (Exception)
        {
            _running = false;
            ReleaseCaptureResources();
            StatusChanged?.Invoke(this, AudioCaptureStatus.Error);
        }
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
    /// _stateLock so it can't interleave with TryStart's field assignment.
    /// Safe to call from Stop() or from the capture thread itself when it
    /// detects the target process has exited or hits an unrecoverable error.
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
            _captureThread = null;
        }
    }

    public void Stop()
    {
        _running = false;
        _retryTimer?.Stop();

        Thread? threadToJoin;
        lock (_stateLock) { threadToJoin = _captureThread; }
        threadToJoin?.Join(500);

        ReleaseCaptureResources();
    }

    public void Dispose() => Stop();
}
