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

    private WasapiProcessLoopbackInterop.IAudioClient? _rawAudioClient;
    private WasapiProcessLoopbackInterop.IAudioCaptureClient? _rawCaptureClient;
    private AutoResetEvent? _eventHandle;
    private Thread? _captureThread;
    private volatile bool _running;
    private System.Timers.Timer? _retryTimer;
    private string? _processName;

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
        try
        {
            _rawAudioClient = ActivateProcessLoopbackAudioClient((uint)process.Id);

            const int sampleRate = 48000, channels = 2, bitsPerSample = 32;
            formatPtr = AllocIeeeFloatWaveFormat(sampleRate, channels, bitsPerSample);

            int hr = _rawAudioClient.Initialize(
                AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                2_000_000, // 200ms buffer, 100ns units
                0,
                formatPtr,
                IntPtr.Zero);
            if (hr != 0) throw new InvalidOperationException($"IAudioClient.Initialize failed, hresult=0x{hr:X}");

            Format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

            _eventHandle = new AutoResetEvent(false);
            _rawAudioClient.SetEventHandle(_eventHandle.SafeWaitHandle.DangerousGetHandle());

            var captureClientGuid = typeof(WasapiProcessLoopbackInterop.IAudioCaptureClient).GUID;
            _rawAudioClient.GetService(ref captureClientGuid, out var serviceObj);
            _rawCaptureClient = (WasapiProcessLoopbackInterop.IAudioCaptureClient)serviceObj;

            _rawAudioClient.Start();
            _running = true;
            _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "SGR-ProcessLoopback" };
            _captureThread.Start();

            StatusChanged?.Invoke(this, AudioCaptureStatus.Capturing);
        }
        catch (Exception)
        {
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
        while (_running)
        {
            _eventHandle!.WaitOne(200);
            if (!_running) break;

            _rawCaptureClient!.GetNextPacketSize(out uint packetFrames);
            while (packetFrames > 0)
            {
                _rawCaptureClient.GetBuffer(out IntPtr dataPtr, out uint framesAvailable, out uint flags, out _, out _);

                int sampleCount = (int)framesAvailable * 2; // stereo
                var samples = new float[sampleCount];
                if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0)
                    Marshal.Copy(dataPtr, samples, 0, sampleCount);

                DataAvailable?.Invoke(this, samples);

                _rawCaptureClient.ReleaseBuffer(framesAvailable);
                _rawCaptureClient.GetNextPacketSize(out packetFrames);
            }
        }
    }

    public void Stop()
    {
        _running = false;
        _retryTimer?.Stop();
        _captureThread?.Join(500);
        _rawAudioClient?.Stop();
        _rawCaptureClient = null;
        _rawAudioClient = null;
        _eventHandle?.Dispose();
        _eventHandle = null;
    }

    public void Dispose() => Stop();
}
