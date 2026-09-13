namespace LogiBuddy.Core.Dsp;

public enum BiquadFilterType { HighPass, LowPass }

/// RBJ Audio EQ Cookbook biquad, Direct Form I.
public class BiquadFilter
{
    private readonly BiquadFilterType _type;
    private readonly float _sampleRate;
    private readonly float _q;
    private float _b0, _b1, _b2, _a1, _a2;
    private float _x1, _x2, _y1, _y2;

    public BiquadFilter(BiquadFilterType type, float cutoffHz, float sampleRate, float q = 0.7071f)
    {
        _type = type;
        _sampleRate = sampleRate;
        _q = q;
        SetCutoff(cutoffHz);
    }

    public void SetCutoff(float cutoffHz)
    {
        float w0 = 2f * MathF.PI * cutoffHz / _sampleRate;
        float cosW0 = MathF.Cos(w0);
        float alpha = MathF.Sin(w0) / (2f * _q);

        float b0, b1, b2, a0, a1, a2;
        if (_type == BiquadFilterType.HighPass)
        {
            b0 = (1f + cosW0) / 2f;
            b1 = -(1f + cosW0);
            b2 = (1f + cosW0) / 2f;
        }
        else
        {
            b0 = (1f - cosW0) / 2f;
            b1 = 1f - cosW0;
            b2 = (1f - cosW0) / 2f;
        }
        a0 = 1f + alpha;
        a1 = -2f * cosW0;
        a2 = 1f - alpha;

        _b0 = b0 / a0;
        _b1 = b1 / a0;
        _b2 = b2 / a0;
        _a1 = a1 / a0;
        _a2 = a2 / a0;
    }

    public float Process(float input)
    {
        float output = _b0 * input + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
        _x2 = _x1;
        _x1 = input;
        _y2 = _y1;
        _y1 = output;
        return output;
    }

    public void Reset()
    {
        _x1 = _x2 = _y1 = _y2 = 0f;
    }
}
