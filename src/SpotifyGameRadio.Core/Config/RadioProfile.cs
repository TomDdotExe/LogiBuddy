namespace SpotifyGameRadio.Core.Config;

public class RadioProfile
{
    public string Name { get; set; } = "Default";
    public string SourceProcessName { get; set; } = "";
    public string OutputDeviceId { get; set; } = "";

    // Fixed source position in listener-relative meters: +X right, +Y up, +Z forward.
    public float SourceX { get; set; } = 0.3f;
    public float SourceY { get; set; } = -0.1f;
    public float SourceZ { get; set; } = 0.2f;

    public float HighPassHz { get; set; } = 400f;
    public float LowPassHz { get; set; } = 3400f;
    public float DistortionDrive { get; set; } = 0.2f;
    public float CompressorThresholdDb { get; set; } = -18f;
    public float CompressorRatio { get; set; } = 4f;
    public float NoiseLevel { get; set; } = 0.05f;
    public float WetDryMix { get; set; } = 1.0f;

    public FreelookHotkey Hotkey { get; set; } = new();
    public float MouseSensitivity { get; set; } = 0.15f; // degrees per mouse count
    public float MaxYawDegrees { get; set; } = 90f;
    public float MaxPitchDegrees { get; set; } = 60f;
    public float SpringBackRatePerSecond { get; set; } = 720f;
}
