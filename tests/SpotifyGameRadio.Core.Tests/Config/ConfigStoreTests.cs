using System.IO;
using SpotifyGameRadio.Core.Config;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Config;

public class ConfigStoreTests
{
    private static ConfigStore CreateStore(out string tempDir)
    {
        tempDir = Path.Combine(Path.GetTempPath(), "sgr-test-" + Path.GetRandomFileName());
        return new ConfigStore(tempDir);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        var store = CreateStore(out var dir);
        try
        {
            var profile = new RadioProfile
            {
                Name = "Arma3-Truck",
                SourceProcessName = "Spotify",
                OutputDeviceId = "device-123",
                SourceX = 0.5f,
                SourceY = -0.2f,
                SourceZ = 0.4f,
                HighPassHz = 500f,
                LowPassHz = 3000f,
                DistortionDrive = 0.3f,
                CompressorThresholdDb = -20f,
                CompressorRatio = 6f,
                NoiseLevel = 0.1f,
                WetDryMix = 0.8f,
                Hotkey = new FreelookHotkey { VirtualKeyCode = 0x12 },
                MouseSensitivity = 0.2f,
                MaxYawDegrees = 80f,
                MaxPitchDegrees = 50f,
                SpringBackRatePerSecond = 500f,
                AutoRouteSource = false,
                RouteSourceToDeviceId = "device-cable-1",
                CalibrateHotkey = new FreelookHotkey { VirtualKeyCode = 0x4F },
                MeasuredYawSweepCounts = 3600f,
            };

            store.Save(profile);
            var loaded = store.Load("Arma3-Truck");

            Assert.Equal(profile.SourceProcessName, loaded.SourceProcessName);
            Assert.Equal(profile.SourceX, loaded.SourceX);
            Assert.Equal(profile.HighPassHz, loaded.HighPassHz);
            Assert.Equal(profile.Hotkey.VirtualKeyCode, loaded.Hotkey.VirtualKeyCode);
            Assert.Equal(profile.SpringBackRatePerSecond, loaded.SpringBackRatePerSecond);
            Assert.False(loaded.AutoRouteSource);
            Assert.Equal("device-cable-1", loaded.RouteSourceToDeviceId);
            Assert.Equal(0x4F, loaded.CalibrateHotkey.VirtualKeyCode);
            Assert.Equal(3600f, loaded.MeasuredYawSweepCounts);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ListProfiles_ReturnsAllSavedNames()
    {
        var store = CreateStore(out var dir);
        try
        {
            store.Save(new RadioProfile { Name = "One" });
            store.Save(new RadioProfile { Name = "Two" });

            var names = store.ListProfiles();

            Assert.Contains("One", names);
            Assert.Contains("Two", names);
            Assert.Equal(2, names.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Delete_RemovesProfile()
    {
        var store = CreateStore(out var dir);
        try
        {
            store.Save(new RadioProfile { Name = "Temp" });
            store.Delete("Temp");

            Assert.DoesNotContain("Temp", store.ListProfiles());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_ProfileJsonMissingNewerFields_KeepsDefaults()
    {
        var store = CreateStore(out var dir);
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "Legacy.json"),
                "{ \"Name\": \"Legacy\", \"HighPassHz\": 600 }");

            var loaded = store.Load("Legacy");

            Assert.Equal("Legacy", loaded.Name);
            Assert.Equal(600f, loaded.HighPassHz);
            Assert.Equal(1.0f, loaded.WetDryMix);           // default retained
            Assert.Equal(0x12, loaded.Hotkey.VirtualKeyCode); // default retained
            Assert.True(loaded.AutoRouteSource);            // default retained
            Assert.Equal("", loaded.RouteSourceToDeviceId); // default retained
            Assert.Equal(1.0f, loaded.Volume);             // default retained
            Assert.Equal(1.0f, loaded.StereoWidth);        // default retained
            Assert.False(loaded.FreelookAlwaysOn);          // default retained
            Assert.Equal(0, loaded.RecenterHotkey.VirtualKeyCode);      // default retained (unbound)
            Assert.Equal(0, loaded.VehicleToggleHotkey.VirtualKeyCode); // default retained (unbound)
            Assert.Equal(3.0f, loaded.VehicleExitDelaySeconds);         // default retained
            Assert.False(loaded.AutoMuteSource);                        // default retained
            Assert.Equal(0, loaded.CalibrateHotkey.VirtualKeyCode);     // default retained (unbound)
            Assert.Equal(0f, loaded.MeasuredYawSweepCounts);            // default retained
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
