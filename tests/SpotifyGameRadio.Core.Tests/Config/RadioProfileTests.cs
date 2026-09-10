using SpotifyGameRadio.Core.Config;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Config;

public class RadioProfileTests
{
    [Fact]
    public void SettingProperties_RaisesPropertyChangedInOrderWithNames()
    {
        var profile = new RadioProfile();
        var changed = new List<string?>();
        profile.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        profile.HighPassHz = 555f;
        profile.WetDryMix = 0.5f;
        profile.MaxYawDegrees = 42f;

        Assert.Equal(
            new[] { nameof(RadioProfile.HighPassHz), nameof(RadioProfile.WetDryMix), nameof(RadioProfile.MaxYawDegrees) },
            changed);
    }

    [Fact]
    public void SettingProperty_ToItsCurrentValue_RaisesNothing()
    {
        var profile = new RadioProfile { NoiseLevel = 0.1f };
        var raised = false;
        profile.PropertyChanged += (_, _) => raised = true;

        profile.NoiseLevel = 0.1f;

        Assert.False(raised);
    }

    [Fact]
    public void AssigningHotkey_RaisesHotkeyPropertyChanged()
    {
        var profile = new RadioProfile();
        var changed = new List<string?>();
        profile.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        profile.Hotkey = new FreelookHotkey { VirtualKeyCode = 0x20 };

        Assert.Contains(nameof(RadioProfile.Hotkey), changed);
    }

    [Fact]
    public void NewCalibrationFields_HaveExpectedDefaults()
    {
        var profile = new RadioProfile();

        Assert.Equal(0, profile.CalibrateHotkey.VirtualKeyCode); // unbound
        Assert.Equal(0f, profile.MeasuredYawSweepCounts);
    }

    [Fact]
    public void AssigningCalibrateHotkey_RaisesPropertyChanged()
    {
        var profile = new RadioProfile();
        var changed = new List<string?>();
        profile.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        profile.CalibrateHotkey = new FreelookHotkey { VirtualKeyCode = 0x4F };
        profile.MeasuredYawSweepCounts = 3200f;

        Assert.Contains(nameof(RadioProfile.CalibrateHotkey), changed);
        Assert.Contains(nameof(RadioProfile.MeasuredYawSweepCounts), changed);
    }
}
