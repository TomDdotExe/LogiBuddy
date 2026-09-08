using SpotifyGameRadio.Core.Config;

namespace SpotifyGameRadio.Core.Tracking;

public class FreelookTracker
{
    private readonly IMouseInputSource _inputSource;
    private RadioProfile _profile;

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
        if (!_profile.FreelookAlwaysOn && !_inputSource.IsHotkeyHeld) return;

        YawDegrees = Clamp(YawDegrees + deltaX * _profile.MouseSensitivity, _profile.MaxYawDegrees);
        PitchDegrees = Clamp(PitchDegrees + deltaY * _profile.MouseSensitivity, _profile.MaxPitchDegrees);
    }

    /// Call once per audio block (or on a timer) to ease the view back to
    /// center when the freelook hotkey is not held, mirroring in-game snap-back.
    public void Update(float deltaSeconds)
    {
        if (_profile.FreelookAlwaysOn || _inputSource.IsHotkeyHeld) return;

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
