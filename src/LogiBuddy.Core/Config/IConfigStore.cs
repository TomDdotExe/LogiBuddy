namespace LogiBuddy.Core.Config;

public interface IConfigStore
{
    IReadOnlyList<string> ListProfiles();
    RadioProfile Load(string name);
    void Save(RadioProfile profile);
    void Delete(string name);
}
