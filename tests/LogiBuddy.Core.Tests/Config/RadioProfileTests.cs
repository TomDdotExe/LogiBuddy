using LogiBuddy.Core.Config;
using Xunit;

namespace LogiBuddy.Core.Tests.Config;

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
        Assert.Equal(0f, profile.MeasuredMaxOffAxisDegrees);
    }

    [Fact]
    public void AssigningCalibrateHotkey_RaisesPropertyChanged()
    {
        var profile = new RadioProfile();
        var changed = new List<string?>();
        profile.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        profile.CalibrateHotkey = new FreelookHotkey { VirtualKeyCode = 0x4F };
        profile.MeasuredYawSweepCounts = 3200f;
        profile.MeasuredMaxOffAxisDegrees = 55f;

        Assert.Contains(nameof(RadioProfile.CalibrateHotkey), changed);
        Assert.Contains(nameof(RadioProfile.MeasuredYawSweepCounts), changed);
        Assert.Contains(nameof(RadioProfile.MeasuredMaxOffAxisDegrees), changed);
    }

    [Fact]
    public void VehicleReentryFields_HaveExpectedDefaults()
    {
        var profile = new RadioProfile();

        Assert.Equal(3.0f, profile.VehicleEnterDelaySeconds);
    }

    [Fact]
    public void AssigningVehicleReentryFields_RaisesPropertyChanged()
    {
        var profile = new RadioProfile();
        var changed = new List<string?>();
        profile.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        profile.VehicleEnterDelaySeconds = 1.5f;

        Assert.Contains(nameof(RadioProfile.VehicleEnterDelaySeconds), changed);
    }

    [Fact]
    public void OutsideViewFields_HaveExpectedDefaults()
    {
        var profile = new RadioProfile();

        Assert.Equal(0, profile.OutsideViewHotkey.VirtualKeyCode); // unbound
        Assert.Equal(900f, profile.OutsideLowPassHz);
        Assert.Equal(0.6f, profile.OutsideVolume);
        Assert.Equal(0.4f, profile.OutsideStereoWidth);
        Assert.Equal(5.0f, profile.OutsideSourceDistance);
        Assert.Equal(0.15f, profile.OutsideYawSensitivity);
    }

    [Fact]
    public void AssigningOutsideViewFields_RaisesPropertyChanged()
    {
        var profile = new RadioProfile();
        var changed = new List<string?>();
        profile.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        profile.OutsideViewHotkey = new FreelookHotkey { VirtualKeyCode = 0x50 };
        profile.OutsideLowPassHz = 700f;
        profile.OutsideVolume = 0.5f;
        profile.OutsideStereoWidth = 0.3f;
        profile.OutsideSourceDistance = 7f;
        profile.OutsideYawSensitivity = 0.4f;

        Assert.Contains(nameof(RadioProfile.OutsideViewHotkey), changed);
        Assert.Contains(nameof(RadioProfile.OutsideLowPassHz), changed);
        Assert.Contains(nameof(RadioProfile.OutsideVolume), changed);
        Assert.Contains(nameof(RadioProfile.OutsideStereoWidth), changed);
        Assert.Contains(nameof(RadioProfile.OutsideSourceDistance), changed);
        Assert.Contains(nameof(RadioProfile.OutsideYawSensitivity), changed);
    }

    [Fact]
    public void ChatModeHotkey_DefaultsUnbound()
    {
        var profile = new RadioProfile();

        Assert.Equal(0, profile.ChatModeHotkey.VirtualKeyCode); // unbound
    }

    [Fact]
    public void AssigningChatModeHotkey_RaisesPropertyChanged()
    {
        var profile = new RadioProfile();
        var changed = new List<string?>();
        profile.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        profile.ChatModeHotkey = new FreelookHotkey { VirtualKeyCode = 0x54 };

        Assert.Contains(nameof(RadioProfile.ChatModeHotkey), changed);
    }

    [Fact]
    public void OverrideHotkey_DefaultsUnbound()
    {
        var profile = new RadioProfile();

        Assert.Equal(0, profile.OverrideHotkey.VirtualKeyCode); // unbound
    }

    [Fact]
    public void AssigningOverrideHotkey_RaisesPropertyChanged()
    {
        var profile = new RadioProfile();
        var changed = new List<string?>();
        profile.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        profile.OverrideHotkey = new FreelookHotkey { VirtualKeyCode = 0x58 };

        Assert.Contains(nameof(RadioProfile.OverrideHotkey), changed);
    }
}
