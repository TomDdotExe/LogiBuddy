namespace LogiBuddy.Core.Dsp;

/// Simple feed-forward peak compressor with exponential attack/release envelopes.
public class Compressor
{
    private readonly float _sampleRate;
    private readonly float _attackCoeff;
    private readonly float _releaseCoeff;
    private float _envelope;

    public float ThresholdDb { get; set; }
    public float Ratio { get; set; }

    public Compressor(float sampleRate, float thresholdDb, float ratio, float attackMs = 5f, float releaseMs = 80f)
    {
        _sampleRate = sampleRate;
        ThresholdDb = thresholdDb;
        Ratio = ratio;
        _attackCoeff = MathF.Exp(-1f / (attackMs * 0.001f * _sampleRate));
        _releaseCoeff = MathF.Exp(-1f / (releaseMs * 0.001f * _sampleRate));
    }

    public float Process(float input)
    {
        float rectified = MathF.Abs(input);
        float coeff = rectified > _envelope ? _attackCoeff : _releaseCoeff;
        _envelope = coeff * _envelope + (1f - coeff) * rectified;

        float envelopeDb = 20f * MathF.Log10(MathF.Max(_envelope, 1e-6f));
        float gainReductionDb = 0f;
        if (envelopeDb > ThresholdDb)
            gainReductionDb = (ThresholdDb - envelopeDb) * (1f - 1f / Ratio);

        float gain = MathF.Pow(10f, gainReductionDb / 20f);
        return input * gain;
    }
}
