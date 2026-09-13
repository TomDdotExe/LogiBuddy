using LogiBuddy.Core.Config;

namespace LogiBuddy.Core.Dsp;

public class RadioEffectChain
{
    private readonly float _sampleRate;
    private readonly BiquadFilter _highPass;
    private readonly BiquadFilter _lowPass;
    private readonly SoftClipDistortion _distortion;
    private readonly Compressor _compressor;
    private readonly NoiseGenerator _noise;

    private float _highPassHz = 400f;
    private float _lowPassHz = 3400f;

    public float HighPassHz
    {
        get => _highPassHz;
        set { _highPassHz = value; _highPass.SetCutoff(value); }
    }

    public float LowPassHz
    {
        get => _lowPassHz;
        set { _lowPassHz = value; _lowPass.SetCutoff(value); }
    }

    public float DistortionDrive
    {
        get => _distortion.Drive;
        set => _distortion.Drive = value;
    }

    public float CompressorThresholdDb
    {
        get => _compressor.ThresholdDb;
        set => _compressor.ThresholdDb = value;
    }

    public float CompressorRatio
    {
        get => _compressor.Ratio;
        set => _compressor.Ratio = value;
    }

    public float NoiseLevel
    {
        get => _noise.Level;
        set => _noise.Level = value;
    }

    public float WetDryMix { get; set; } = 1f;

    public RadioEffectChain(float sampleRate)
    {
        _sampleRate = sampleRate;
        _highPass = new BiquadFilter(BiquadFilterType.HighPass, _highPassHz, sampleRate);
        _lowPass = new BiquadFilter(BiquadFilterType.LowPass, _lowPassHz, sampleRate);
        _distortion = new SoftClipDistortion(drive: 0f);
        _compressor = new Compressor(sampleRate, thresholdDb: -18f, ratio: 4f);
        _noise = new NoiseGenerator();
    }

    public void ApplyProfile(RadioProfile profile)
    {
        HighPassHz = profile.HighPassHz;
        LowPassHz = profile.LowPassHz;
        DistortionDrive = profile.DistortionDrive;
        CompressorThresholdDb = profile.CompressorThresholdDb;
        CompressorRatio = profile.CompressorRatio;
        NoiseLevel = profile.NoiseLevel;
        WetDryMix = profile.WetDryMix;
    }

    public void Process(float[] buffer, int count)
    {
        for (int i = 0; i < count; i++)
        {
            float dry = buffer[i];
            // Noise goes in before the filter/distortion/compressor stage
            // rather than added on top of it afterward: real radio static
            // shares the receiver's own band-limiting and companding, so it
            // takes on the same muffled, narrow-band character as the voice
            // instead of reading as a separate, full-band hiss layered over
            // an already-filtered signal.
            float wet = dry + _noise.NextSample();
            wet = _highPass.Process(wet);
            wet = _lowPass.Process(wet);
            wet = _distortion.Process(wet);
            wet = _compressor.Process(wet);

            buffer[i] = dry * (1f - WetDryMix) + wet * WetDryMix;
        }
    }
}
