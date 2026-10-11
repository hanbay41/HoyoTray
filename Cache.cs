using System.Text.Json;

namespace HoyoTray;

/// <summary>
/// Last known accounts and notes, saved to %LOCALAPPDATA%\HoyoTray\cache.json so a fresh launch can show
/// numbers immediately (the regen estimate brings them up to date). Contains no credentials.
/// </summary>
class Cache
{
    public Dictionary<string, List<GameAccount>> Accounts { get; set; } = []; // by Game.Key
    public Dictionary<string, Note> Notes { get; set; } = [];                // by Settings.Key
    public DateTime? LastUpdated { get; set; }

    static string FilePath => Path.Combine(Storage.Dir, "cache.json");

    public static Cache Load()
    {
        try { return JsonSerializer.Deserialize<Cache>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Storage.Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { }
    }

    public static void Delete()
    {
        try { File.Delete(FilePath); } catch { }
    }
}
