using System.IO;
using System.Text.Json;

namespace SpotifyGameRadio.Core.Config;

public sealed record RouteRecoveryEntry(int ProcessId, string Console, string Multimedia, string Communications);

public sealed record RouteRecoveryRecord(string SourceProcessName, IReadOnlyList<RouteRecoveryEntry> Routes);

/// Persists what a source process's per-app audio routing was before the app
/// changed it, so a crash between Start and Stop can still be undone on the
/// next launch. One file, %AppData%/SpotifyGameRadio/route-recovery.json.
public sealed class RouteRecoveryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public RouteRecoveryStore(string? directoryOverride = null)
    {
        string dir = directoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SpotifyGameRadio");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "route-recovery.json");
    }

    public bool Exists() => File.Exists(_path);

    public void Write(RouteRecoveryRecord record)
        => File.WriteAllText(_path, JsonSerializer.Serialize(record, JsonOptions));

    public RouteRecoveryRecord? Read()
    {
        if (!File.Exists(_path)) return null;
        try { return JsonSerializer.Deserialize<RouteRecoveryRecord>(File.ReadAllText(_path)); }
        catch (JsonException) { return null; }
    }

    public void Delete()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
