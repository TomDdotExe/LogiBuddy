using System.IO;
using System.Text.Json;

namespace LogiBuddy.Core.Config;

public sealed record RouteRecoveryEntry(int ProcessId, string Console, string Multimedia, string Communications);

public sealed record RouteRecoveryRecord(string SourceProcessName, IReadOnlyList<RouteRecoveryEntry> Routes);

/// Persists what a source process's per-app audio routing was before the app
/// changed it, so a crash between Start and Stop can still be undone on the
/// next launch. One file, %AppData%/LogiBuddy/route-recovery.json.
public sealed class RouteRecoveryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public RouteRecoveryStore(string? directoryOverride = null)
    {
        string dir = directoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LogiBuddy");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "route-recovery.json");
    }

    public bool Exists() => File.Exists(_path);

    public void Write(RouteRecoveryRecord record)
        => File.WriteAllText(_path, JsonSerializer.Serialize(record, JsonOptions));

    public RouteRecoveryRecord? Read()
    {
        if (!File.Exists(_path)) return null;
        // Any failure (malformed JSON, a locked or unreadable file, a race with
        // Delete) must degrade to "nothing to recover" — this runs during app
        // construction, where a throw would take the launch down with it.
        try { return JsonSerializer.Deserialize<RouteRecoveryRecord>(File.ReadAllText(_path)); }
        catch (Exception) { return null; }
    }

    public void Delete()
    {
        // Best-effort: a locked/removed file must not throw out of Stop() or
        // the crash-recovery path.
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (Exception) { /* best effort */ }
    }
}
