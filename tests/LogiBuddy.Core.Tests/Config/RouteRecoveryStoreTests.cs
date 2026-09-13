using System.IO;
using LogiBuddy.Core.Config;
using Xunit;

namespace LogiBuddy.Core.Tests.Config;

public class RouteRecoveryStoreTests
{
    private static RouteRecoveryStore CreateStore(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "sgr-recov-" + Path.GetRandomFileName());
        return new RouteRecoveryStore(dir);
    }

    [Fact]
    public void WriteThenRead_RoundTripsRecord()
    {
        var store = CreateStore(out var dir);
        try
        {
            var record = new RouteRecoveryRecord("Spotify", new[]
            {
                new RouteRecoveryEntry(1234, "", "", ""),
                new RouteRecoveryEntry(5678, "dev-a", "dev-a", ""),
            });

            store.Write(record);
            var read = store.Read();

            Assert.NotNull(read);
            Assert.Equal("Spotify", read!.SourceProcessName);
            Assert.Equal(2, read.Routes.Count);
            Assert.Equal(5678, read.Routes[1].ProcessId);
            Assert.Equal("dev-a", read.Routes[1].Multimedia);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Read_WhenNoFile_ReturnsNull()
    {
        var store = CreateStore(out var dir);
        try
        {
            Assert.False(store.Exists());
            Assert.Null(store.Read());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Delete_RemovesTheFile()
    {
        var store = CreateStore(out var dir);
        try
        {
            store.Write(new RouteRecoveryRecord("X", Array.Empty<RouteRecoveryEntry>()));
            Assert.True(store.Exists());

            store.Delete();

            Assert.False(store.Exists());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
