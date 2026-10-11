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

/// <summary>A regenerating stamina value (resin / battery) as of FetchedAt.</summary>
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

record GenshinNote(Stamina Resin, int Dailies, int DailiesMax, bool DailyRewardClaimed);
record ZzzNote(Stamina Battery, int Engagement, int EngagementMax);

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

    /// <summary>Highest-level account for a game_biz (hk4e_global / nap_global), or null if none.</summary>
    public static async Task<GameAccount?> GetAccount(string cookie, string gameBiz)
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
            .FirstOrDefault();
    }

    public static async Task<GenshinNote> GetGenshin(string cookie, GameAccount acc)
    {
        var d = await Get(
            $"https://bbs-api-os.hoyolab.com/game_record/genshin/api/dailyNote?server={acc.Region}&role_id={acc.Uid}",
            cookie, ds: true);
        var resin = new Stamina(
            d.GetProperty("current_resin").GetInt32(),
            d.GetProperty("max_resin").GetInt32(),
            int.Parse(d.GetProperty("resin_recovery_time").GetString() ?? "0"),
            480, DateTime.Now);
        return new GenshinNote(resin,
            d.GetProperty("finished_task_num").GetInt32(),
            d.GetProperty("total_task_num").GetInt32(),
            d.GetProperty("is_extra_task_reward_received").GetBoolean());
    }

    public static async Task<ZzzNote> GetZzz(string cookie, GameAccount acc)
    {
        var d = await Get(
            $"https://sg-act-nap-api.hoyolab.com/event/game_record_zzz/api/zzz/note?server={acc.Region}&role_id={acc.Uid}",
            cookie, ds: true);
        var energy = d.GetProperty("energy");
        var progress = energy.GetProperty("progress");
        var battery = new Stamina(
            progress.GetProperty("current").GetInt32(),
            progress.GetProperty("max").GetInt32(),
            energy.GetProperty("restore").GetInt32(),
            360, DateTime.Now);
        var vitality = d.GetProperty("vitality");
        return new ZzzNote(battery,
            vitality.GetProperty("current").GetInt32(),
            vitality.GetProperty("max").GetInt32());
    }
}
