using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HoyoTray;

class HoyoException(int retcode, string message) : Exception(message)
{
    public int Retcode { get; } = retcode;
    public bool AuthExpired => Retcode is -100 or 10001 or 10103;
    public bool NotPublic => Retcode is 10102;
}

record GameAccount(string Uid, string Region, string Nickname, int Level);

/// <summary>A regenerating stamina value (resin / power / battery) as of FetchedAt.</summary>
record Stamina(int Current, int Max, int RecoverySeconds, int SecondsPerUnit, DateTime FetchedAt)
{
    TimeSpan Remaining => TimeSpan.FromSeconds(
        Math.Max(0, RecoverySeconds - (DateTime.Now - FetchedAt).TotalSeconds));

    // Estimate current value from the regen timer so it stays accurate between polls.
    public int Estimate()
    {
        if (RecoverySeconds <= 0) return Current;
        int missing = (int)Math.Ceiling(Remaining.TotalSeconds / SecondsPerUnit);
        return Math.Max(Current, Max - missing);
    }

    public DateTime FullAt => FetchedAt.AddSeconds(RecoverySeconds);

    public string FullIn()
    {
        var r = Remaining;
        if (r <= TimeSpan.Zero) return "full";
        return r.TotalHours >= 1 ? $"full in {(int)r.TotalHours}h {r.Minutes}m" : $"full in {r.Minutes}m";
    }
}

/// <summary>Stamina plus one secondary daily stat (commissions / daily training / engagement).</summary>
record Note(Stamina Stamina, string Side, bool SideDone);

/// <summary>Everything game-specific lives here; the rest of the app just loops over All.</summary>
record Game(string Key, string Name, string Biz, string StaminaName, string ShortName, Color Accent,
    Func<string, GameAccount, Task<Note>> Fetch)
{
    public static readonly Game Genshin = new("genshin", "Genshin Impact", "hk4e_global",
        "Original Resin", "Resin", Theme.Resin, HoyoApi.GetGenshin);
    public static readonly Game Hsr = new("hsr", "Honkai: Star Rail", "hkrpg_global",
        "Trailblaze Power", "TB Power", Theme.Trailblaze, HoyoApi.GetHsr);
    public static readonly Game Zzz = new("zzz", "Zenless Zone Zero", "nap_global",
        "Battery Charge", "Battery", Theme.Battery, HoyoApi.GetZzz);

    public static readonly Game[] All = [Genshin, Hsr, Zzz];
}

static class HoyoApi
{
    // Overseas (HoYoLAB) DS salt, as used by genshin.py and other community tools.
    const string DsSalt = "6s25p5ox5y14umn1p61aqyyvbvvl3lrt";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    static string Ds()
    {
        long t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var r = new string(Enumerable.Range(0, 6).Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());
        var hash = MD5.HashData(Encoding.UTF8.GetBytes($"salt={DsSalt}&t={t}&r={r}"));
        return $"{t},{r},{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    static async Task<JsonElement> Get(string url, string cookie, bool ds)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Cookie", cookie);
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        req.Headers.TryAddWithoutValidation("Referer", "https://act.hoyolab.com/");
        req.Headers.TryAddWithoutValidation("Origin", "https://act.hoyolab.com");
        req.Headers.TryAddWithoutValidation("x-rpc-app_version", "1.5.0");
        req.Headers.TryAddWithoutValidation("x-rpc-client_type", "5");
        req.Headers.TryAddWithoutValidation("x-rpc-language", "en-us");
        if (ds) req.Headers.TryAddWithoutValidation("DS", Ds());

        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        int retcode = root.GetProperty("retcode").GetInt32();
        if (retcode != 0)
            throw new HoyoException(retcode, root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "");
        return root.GetProperty("data").Clone();
    }

    // Some fields come back as numbers in one game and strings in another.
    static int Int(JsonElement e, string name)
    {
        var v = e.GetProperty(name);
        return v.ValueKind == JsonValueKind.String ? int.Parse(v.GetString()!) : v.GetInt32();
    }

    /// <summary>All accounts for a game_biz (hk4e_global / hkrpg_global / nap_global), highest level first.</summary>
    public static async Task<List<GameAccount>> GetAccounts(string cookie, string gameBiz)
    {
        var data = await Get(
            $"https://api-account-os.hoyolab.com/account/binding/api/getUserGameRolesByCookie?game_biz={gameBiz}",
            cookie, ds: false);
        return data.GetProperty("list").EnumerateArray()
            .Select(a => new GameAccount(
                a.GetProperty("game_uid").GetString()!,
                a.GetProperty("region").GetString()!,
                a.GetProperty("nickname").GetString() ?? "",
                a.GetProperty("level").GetInt32()))
            .OrderByDescending(a => a.Level)
            .ToList();
    }

    public static string RegionName(string region) => region switch
    {
        _ when region.Contains("usa") || region.EndsWith("_us") => "America",
        _ when region.Contains("eur") || region.EndsWith("_eu") => "Europe",
        _ when region.Contains("asia") || region.EndsWith("_jp") => "Asia",
        _ when region.Contains("cht") || region.EndsWith("_sg") => "TW/HK/MO",
        _ => region,
    };

    public static async Task<Note> GetGenshin(string cookie, GameAccount acc)
    {
        var d = await Get(
            $"https://bbs-api-os.hoyolab.com/game_record/genshin/api/dailyNote?server={acc.Region}&role_id={acc.Uid}",
            cookie, ds: true);
        var resin = new Stamina(Int(d, "current_resin"), Int(d, "max_resin"), Int(d, "resin_recovery_time"), 480, DateTime.Now);
        bool claimed = d.GetProperty("is_extra_task_reward_received").GetBoolean();
        return new Note(resin,
            $"Commissions {Int(d, "finished_task_num")}/{Int(d, "total_task_num")}" + (claimed ? " ✓" : ""), claimed);
    }

    public static async Task<Note> GetHsr(string cookie, GameAccount acc)
    {
        var d = await Get(
            $"https://bbs-api-os.hoyolab.com/game_record/hkrpg/api/note?server={acc.Region}&role_id={acc.Uid}",
            cookie, ds: true);
        var power = new Stamina(Int(d, "current_stamina"), Int(d, "max_stamina"), Int(d, "stamina_recover_time"), 360, DateTime.Now);
        int score = Int(d, "current_train_score"), max = Int(d, "max_train_score");
        bool done = score >= max;
        return new Note(power, $"Daily Training {score}/{max}" + (done ? " ✓" : ""), done);
    }

    public static async Task<Note> GetZzz(string cookie, GameAccount acc)
    {
        var d = await Get(
            $"https://sg-act-nap-api.hoyolab.com/event/game_record_zzz/api/zzz/note?server={acc.Region}&role_id={acc.Uid}",
            cookie, ds: true);
        var energy = d.GetProperty("energy");
        var progress = energy.GetProperty("progress");
        var battery = new Stamina(Int(progress, "current"), Int(progress, "max"), Int(energy, "restore"), 360, DateTime.Now);
        var vitality = d.GetProperty("vitality");
        int cur = Int(vitality, "current"), max = Int(vitality, "max");
        bool done = cur >= max;
        return new Note(battery, $"Engagement {cur}/{max}" + (done ? " ✓" : ""), done);
    }
}
