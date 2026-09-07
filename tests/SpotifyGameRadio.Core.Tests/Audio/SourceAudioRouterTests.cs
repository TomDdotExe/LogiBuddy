using SpotifyGameRadio.Core.Audio;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Audio;

public class SourceAudioRouterTests
{
    private const int E_INVALIDARG = unchecked((int)0x80070057);
    private const int E_FAIL = unchecked((int)0x80004005);

    /// The multi-pid tolerance in MainViewModel.TryRouteSource hinges on this
    /// flag: a session-less pid (normal for a multi-process app like Spotify)
    /// must be skippable, not treated as a routing failure.
    [Fact]
    public void SourceRoutingException_WithEInvalidArg_SetsNoActiveAudio()
    {
        var ex = new SourceRoutingException("x", E_INVALIDARG);
        Assert.True(ex.NoActiveAudio);
        Assert.Equal(E_INVALIDARG, ex.HResult);
    }

    [Fact]
    public void SourceRoutingException_WithOtherHresult_NoActiveAudioFalse()
    {
        var ex = new SourceRoutingException("x", E_FAIL);
        Assert.False(ex.NoActiveAudio);
        Assert.Equal(E_FAIL, ex.HResult);
    }

    /// On an unsupported host, routing must fail as SourceRoutingException (not
    /// a raw COM/interop exception). On a supported host the same contract is
    /// checked from the read side, which is side-effect free.
    [Fact]
    public void WindowsAppAudioRouter_HonoursIsSupportedContract()
    {
        var router = new WindowsAppAudioRouter();

        if (!router.IsSupported)
        {
            Assert.Throws<SourceRoutingException>(() => router.RouteProcess(Environment.ProcessId, ""));
            Assert.Equal(AppAudioRoute.None, router.GetCurrentRoute(Environment.ProcessId));
            return;
        }

        try
        {
            var route = router.GetCurrentRoute(Environment.ProcessId);
            Assert.NotNull(route);
            Assert.NotNull(route.Console);
        }
        catch (SourceRoutingException)
        {
            // Acceptable: the undocumented API can refuse on some hosts. What
            // matters is that it surfaces as SourceRoutingException.
        }
    }

    /// A pid that doesn't exist is the same shape of input as a source process
    /// that exited between enumeration and routing — it must not escape as a
    /// COMException.
    [Fact]
    public void GetCurrentRoute_ForNonexistentProcess_DoesNotLeakRawInteropException()
    {
        var router = new WindowsAppAudioRouter();
        try
        {
            var route = router.GetCurrentRoute(0x7FFFFFF0);
            Assert.NotNull(route);
        }
        catch (SourceRoutingException)
        {
            // Expected shape of failure.
        }
    }
}
