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
    public RewardState Rewards { get; set; } = new();

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

/// <summary>What the rewards features have done, so nothing is claimed, redeemed or announced twice.</summary>
class RewardState
{
    public Dictionary<string, string> CheckedIn { get; set; } = [];      // game → day (UTC+8) confirmed claimed
    public Dictionary<string, string> CheckInNeedsYou { get; set; } = []; // game → day a captcha blocked it
    public Dictionary<string, List<CodeOffer>> Offers { get; set; } = []; // game → codes currently on HoYoLAB
    public HashSet<string> CodesDone { get; set; } = [];                 // "game:uid:CODE" handled for good
    public HashSet<string> CodesNeedYou { get; set; } = [];              // "game:uid:CODE" to redeem on the website
    public HashSet<string> CodesAnnounced { get; set; } = [];            // "game:CODE" already notified
}
