using SpotifyGameRadio.Core.Config;

namespace SpotifyGameRadio.Core.Tracking;

public class FreelookTracker
{
    private readonly IMouseInputSource _inputSource;
    private RadioProfile _profile;
    private float _idleSeconds;

    public float YawDegrees { get; private set; }
    public float PitchDegrees { get; private set; }

    public FreelookTracker(IMouseInputSource inputSource, RadioProfile profile)
    {
        _inputSource = inputSource;
        _profile = profile;
        _inputSource.MouseMoved += OnMouseMoved;
    }

    public void ApplyProfile(RadioProfile profile)
    {
        _profile = profile;
    }

    /// Zeroes the listener orientation (faces forward again). Called from the UI
    /// thread; the audio thread's per-block read of YawDegrees/PitchDegrees is
    /// two atomic float reads, so the worst case is one block with one axis
    /// already zeroed and the other not — inaudible.
    public void Recenter()
    {
        YawDegrees = 0f;
        PitchDegrees = 0f;
    }

    private void OnMouseMoved(int deltaX, int deltaY)
    {
        _idleSeconds = 0f;
        if (!_profile.FreelookAlwaysOn && !_inputSource.IsHotkeyHeld) return;

        YawDegrees = Clamp(YawDegrees + deltaX * _profile.MouseSensitivity, _profile.MaxYawDegrees);
        PitchDegrees = Clamp(PitchDegrees + deltaY * _profile.MouseSensitivity, _profile.MaxPitchDegrees);
        ApplyOffAxisClamp();
    }

    /// When the game's freelook limit is a cone rather than a rectangle,
    /// clamp the combined off-forward angle to the calibrated maximum,
    /// scaling both axes down together so the look direction is preserved.
    /// No-op until calibration has measured a value.
    private void ApplyOffAxisClamp()
    {
        float max = _profile.MeasuredMaxOffAxisDegrees;
        if (max <= 0f) return;
        float combined = MathF.Sqrt(YawDegrees * YawDegrees + PitchDegrees * PitchDegrees);
        if (combined <= max || combined == 0f) return;
        float scale = max / combined;
        YawDegrees *= scale;
        PitchDegrees *= scale;
    }

    /// Call once per audio block. Hold-to-look: whenever the freelook key
    /// is not held the view is forced to forward — mirroring the game's
    /// own snap-back and clearing any per-hold integration error before
    /// the next hold. FreelookAlwaysOn: ease toward centre once the mouse
    /// has been still for ~1 s (there is no key release to reset on).
    public void Update(float deltaSeconds)
    {
        if (!_profile.FreelookAlwaysOn)
        {
            if (!_inputSource.IsHotkeyHeld)
            {
                YawDegrees = 0f;
                PitchDegrees = 0f;
            }
            return;
        }

        _idleSeconds += deltaSeconds;
        if (_idleSeconds < 1.0f) return;
        float step = _profile.SpringBackRatePerSecond * deltaSeconds;
        YawDegrees = SpringTowardZero(YawDegrees, step);
        PitchDegrees = SpringTowardZero(PitchDegrees, step);
    }

    private static float Clamp(float value, float max) => Math.Clamp(value, -max, max);

    private static float SpringTowardZero(float value, float step)
    {
        if (value > 0f) return MathF.Max(0f, value - step);
        if (value < 0f) return MathF.Min(0f, value + step);
        return 0f;
    }
}
