using System.IO;
using System.Text.Json;

namespace LogiBuddy.Core.Config;

public class ConfigStore : IConfigStore
{
    private readonly string _directory;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ConfigStore(string? directoryOverride = null)
    {
        _directory = directoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LogiBuddy", "Profiles");
        Directory.CreateDirectory(_directory);
    }

    private string PathFor(string name) => Path.Combine(_directory, $"{name}.json");

    public IReadOnlyList<string> ListProfiles()
    {
        if (!Directory.Exists(_directory)) return Array.Empty<string>();
        return Directory.GetFiles(_directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList();
    }

    public RadioProfile Load(string name)
    {
        var json = File.ReadAllText(PathFor(name));
        return JsonSerializer.Deserialize<RadioProfile>(json)
            ?? throw new InvalidDataException($"Profile '{name}' is invalid.");
    }

    public void Save(RadioProfile profile)
    {
        Directory.CreateDirectory(_directory);
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(PathFor(profile.Name), json);
    }

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }
}
