namespace LogiBuddy.Core.Spatial;

public interface ISpatializer
{
    /// Fixed position of the source in vehicle/world space, meters: +X right, +Y up, +Z forward.
    void SetSourcePosition(float x, float y, float z);

    /// Current listener (player head) orientation relative to vehicle forward.
    void SetListenerOrientation(float yawDegrees, float pitchDegrees);

    /// Renders count mono input samples into count*2 interleaved stereo output samples.
    void Process(float[] monoInput, int count, float[] stereoOutputInterleaved);
}
