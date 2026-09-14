using LogiBuddy.Core.Updates;
using Xunit;

namespace LogiBuddy.Core.Tests.Updates;

public class InstallDetectorTests
{
    [Fact]
    public void IsInstalled_ReturnsTrue_WhenUninstallerPresent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LogiBuddyInstallDetectorTests_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "unins000.exe"), "");

            Assert.True(InstallDetector.IsInstalled(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void IsInstalled_ReturnsFalse_WhenUninstallerAbsent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LogiBuddyInstallDetectorTests_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            Assert.False(InstallDetector.IsInstalled(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
