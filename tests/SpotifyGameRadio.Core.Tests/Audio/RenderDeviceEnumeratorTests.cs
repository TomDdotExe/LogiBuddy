using SpotifyGameRadio.Core.Audio;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Audio;

public class RenderDeviceEnumeratorTests
{
    [Theory]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)")]
    [InlineData("CABLE In 16ch (VB-Audio Virtual Cable)")]
    [InlineData("VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)")]
    [InlineData("Virtual Audio Device")]
    public void LooksLikeVirtualCable_TrueForVirtualDevices(string name)
        => Assert.True(RenderDeviceEnumerator.LooksLikeVirtualCable(name));

    [Theory]
    [InlineData("Speakers (Focusrite USB Audio)")]
    [InlineData("LG ULTRAGEAR (NVIDIA High Definition Audio)")]
    [InlineData("Headphones (Realtek(R) Audio)")]
    [InlineData("")]
    public void LooksLikeVirtualCable_FalseForRealDevices(string name)
        => Assert.False(RenderDeviceEnumerator.LooksLikeVirtualCable(name));
}
