using SpotifyGameRadio.Core.Config;

namespace SpotifyGameRadio.Core.Tracking;

public class FreelookTracker
{
    private readonly IMouseInputSource _inputSource;
    private RadioProfile _profile;
    private float _idleSeconds;
    private bool _outsideView; // not part of RadioProfile; runtime-only, toggled by the inside/outside view feature.

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

    /// Switches between the cockpit's limited look range (clamped to
    /// Profile.MaxYawDegrees, plus the calibrated off-axis cone, pitch always
    /// 0) and a free 360° pan for an outside third-person camera, including
    /// vertical movement clamped to a fixed ±90° (straight up to straight
    /// down, calibrated geometrically rather than per-game) — matching how
    /// most games let the outside/chase camera orbit freely on both axes
    /// while the cockpit view is constrained (and, for pitch, not modelled
    /// at all). Switching back to inside snaps pitch to 0 immediately, since
    /// the cockpit path never touches it again after this. Never persisted;
    /// a new FreelookTracker instance always starts inside (false). Safe to
    /// call from the UI thread.
    public void SetOutsideView(bool outside)
    {
        _outsideView = outside;
        if (!outside) PitchDegrees = 0f;
    }

    /// Zeroes the listener orientation (faces forward again, level). Called
    /// from the UI thread; the audio thread's per-block read of Yaw/Pitch
    /// Degrees is an atomic float read.
    public void Recenter()
    {
        YawDegrees = 0f;
        PitchDegrees = 0f;
    }

    private void OnMouseMoved(int deltaX, int deltaY)
    {
        _idleSeconds = 0f;
        if (!_profile.FreelookAlwaysOn && !_inputSource.IsHotkeyHeld) return;

        if (_outsideView)
        {
            // Free 360° pan on both axes: yaw wraps rather than clamps, so
            // continuing to turn the mouse one way keeps rotating smoothly
            // instead of pinning at a limit. Pitch is tracked at all here
            // (ignored entirely inside the cockpit, see the class doc) and
            // clamped rather than wrapped — flipping through the poles isn't
            // a real camera move. The cockpit-cone off-axis clamp doesn't
            // apply — there is no cockpit FOV limiting an outside chase camera.
            //
            // Sign convention is intentionally inverted from the cockpit's
            // head-turn model (which adds deltaX/subtracts deltaY directly):
            // outside view is an orbit camera — the listener's position moves
            // around a fixed source rather than the listener turning their
            // head in place — and that inverts which way the source appears
            // to sweep for the same pan input. Panning right should sweep
            // the source right (as if walking around it), not swing it left
            // the way turning your own head right would. See
            // SourceRotationTests for the rotation math this feeds.
            YawDegrees = WrapDegrees(YawDegrees - deltaX * _profile.OutsideYawSensitivity);
            PitchDegrees = Clamp(PitchDegrees + deltaY * _profile.PitchSensitivity, 90f);
            return;
        }

        YawDegrees = Clamp(YawDegrees + deltaX * _profile.MouseSensitivity, _profile.MaxYawDegrees);
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
    /// is not held the view is forced to forward (and, outside, level) —
    /// mirroring the game's own snap-back and clearing any per-hold
    /// integration error before the next hold. FreelookAlwaysOn: ease toward
    /// centre/level once the mouse has been still for ~1 s (there is no key
    /// release to reset on).
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
        if (_outsideView) PitchDegrees = SpringTowardZero(PitchDegrees, step);
    }

    private static float Clamp(float value, float max) => Math.Clamp(value, -max, max);

    /// Normalizes an angle to (-180, 180] instead of clamping it, so
    /// repeatedly turning the same way keeps rotating smoothly through the
    /// wrap point rather than pinning at a limit.
    private static float WrapDegrees(float value)
    {
        value %= 360f;
        if (value > 180f) value -= 360f;
        else if (value <= -180f) value += 360f;
        return value;
    }

    private static float SpringTowardZero(float value, float step)
    {
        if (value > 0f) return MathF.Max(0f, value - step);
        if (value < 0f) return MathF.Min(0f, value + step);
        return 0f;
    }
}
