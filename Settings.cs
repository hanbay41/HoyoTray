using System.Text.Json;

namespace HoyoTray;

/// <summary>User choices, stored as JSON in %LOCALAPPDATA%\HoyoTray\settings.json.</summary>
class Settings
{
    public HashSet<string> DisabledGames { get; set; } = [];

    /// <summary>"game:uid" → shown. Accounts not listed here fall back to "highest level only".</summary>
    public Dictionary<string, bool> Accounts { get; set; } = [];

    public string Icon { get; set; } = "resin";

    static string FilePath => Path.Combine(Storage.Dir, "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Storage.Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public Settings Clone() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this))!;

    public static string Key(Game g, GameAccount a) => $"{g.Key}:{a.Uid}";

    public bool GameEnabled(Game g) => !DisabledGames.Contains(g.Key);

    public bool AccountShown(Game g, GameAccount a, IReadOnlyList<GameAccount> all) =>
        Accounts.TryGetValue(Key(g, a), out var shown) ? shown : all.Count > 0 && all[0].Uid == a.Uid;
}
