using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace HoyoTray;

/// <summary>A redemption code offered on HoYoLAB (e.g. after a livestream).</summary>
record CodeOffer(string Code, string Rewards);

enum CheckInResult { Claimed, AlreadyClaimed, NeedsYou }

enum RedeemResult { Redeemed, AlreadyUsed, Expired, Invalid, LevelTooLow, Cooldown, LoginNeeded, NeedsYou }

/// <summary>Daily check-in and redemption-code endpoints, plus the official pages to fall back to.</summary>
static class Rewards
{
    record Endpoints(string CheckInBase, string ActId, string? SignGame, int HoyolabGameId,
        string RedeemUrl, string CheckInPage, string RedeemPage);

    static readonly Dictionary<string, Endpoints> ByGame = new()
    {
        ["genshin"] = new("https://sg-hk4e-api.hoyolab.com/event/sol", "e202102251931481", null, 2,
            "https://sg-hk4e-api.hoyoverse.com/common/apicdkey/api/webExchangeCdkey",
            "https://act.hoyolab.com/ys/event/signin-sea-v3/index.html?act_id=e202102251931481",
            "https://genshin.hoyoverse.com/en/gift?code="),
        ["hsr"] = new("https://sg-public-api.hoyolab.com/event/luna/os", "e202303301540311", "hkrpg", 6,
            "https://sg-hkrpg-api.hoyoverse.com/common/apicdkey/api/webExchangeCdkey",
            "https://act.hoyolab.com/bbs/event/signin/hkrpg/e202303301540311.html?act_id=e202303301540311",
            "https://hsr.hoyoverse.com/gift?code="),
        ["zzz"] = new("https://sg-act-nap-api.hoyolab.com/event/luna/zzz/os", "e202406031448091", "zzz", 8,
            "https://public-operation-nap.hoyoverse.com/common/apicdkey/api/webExchangeCdkey",
            "https://act.hoyolab.com/bbs/event/signin/zzz/e202406031448091.html?act_id=e202406031448091",
            "https://zenless.hoyoverse.com/redemption?code="),
    };

    /// <summary>HoYoLAB's day rolls over at midnight UTC+8.</summary>
    public static string Today => DateTime.UtcNow.AddHours(8).ToString("yyyy-MM-dd");

    static Dictionary<string, string>? SignHeaders(Endpoints e) =>
        e.SignGame == null ? null : new() { ["x-rpc-signgame"] = e.SignGame };

    static JsonElement Data(JsonElement root)
    {
        int retcode = root.GetProperty("retcode").GetInt32();
        if (retcode != 0)
            throw new HoyoException(retcode, root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "");
        return root.GetProperty("data");
    }

    /// <summary>(already claimed today, days claimed this month).</summary>
    public static async Task<(bool Signed, int Days)> GetCheckIn(string cookie, Game g)
    {
        var e = ByGame[g.Key];
        var d = Data(await HoyoApi.Request(HttpMethod.Get, $"{e.CheckInBase}/info?lang=en-us&act_id={e.ActId}", cookie, headers: SignHeaders(e)));
        return (d.GetProperty("is_sign").GetBoolean(), d.GetProperty("total_sign_day").GetInt32());
    }

    public static async Task<CheckInResult> CheckIn(string cookie, Game g)
    {
        var e = ByGame[g.Key];
        var root = await HoyoApi.Request(HttpMethod.Post, $"{e.CheckInBase}/sign?lang=en-us", cookie,
            headers: SignHeaders(e), json: JsonSerializer.Serialize(new { act_id = e.ActId }));
        int retcode = root.GetProperty("retcode").GetInt32();
        if (retcode == -5003) return CheckInResult.AlreadyClaimed;
        var d = Data(root);
        // A captcha challenge comes back as success with a risk flag (Genshin nests it in gt_result).
        var risk = d.TryGetProperty("gt_result", out var gt) && gt.ValueKind == JsonValueKind.Object ? gt : d;
        bool isRisk = risk.TryGetProperty("is_risk", out var r) && r.ValueKind == JsonValueKind.True;
        bool riskCode = risk.TryGetProperty("risk_code", out var rc) && rc.ValueKind == JsonValueKind.Number && rc.GetInt32() != 0;
        return isRisk || riskCode ? CheckInResult.NeedsYou : CheckInResult.Claimed;
    }

    /// <summary>Name of the reward for the given day index this month, e.g. "20× Primogem".</summary>
    public static async Task<string?> CheckInReward(string cookie, Game g, int dayIndex)
    {
        var e = ByGame[g.Key];
        var d = Data(await HoyoApi.Request(HttpMethod.Get, $"{e.CheckInBase}/home?lang=en-us&act_id={e.ActId}", cookie, headers: SignHeaders(e)));
        var awards = d.GetProperty("awards");
        if (dayIndex < 0 || dayIndex >= awards.GetArrayLength()) return null;
        var a = awards[dayIndex];
        return $"{a.GetProperty("cnt").GetInt32()}× {a.GetProperty("name").GetString()}";
    }

    /// <summary>Codes currently shown in the redemption-code widget on the game's HoYoLAB page.</summary>
    public static async Task<List<CodeOffer>> GetCodes(Game g)
    {
        var e = ByGame[g.Key];
        var d = Data(await HoyoApi.Request(HttpMethod.Get,
            $"https://bbs-api-os.hoyolab.com/community/painter/wapi/circle/channel/guide/material?game_id={e.HoyolabGameId}", cookie: null,
            headers: new() { ["x-rpc-client_type"] = "4", ["x-rpc-app_version"] = "2.42.0" }));
        var offers = new List<CodeOffer>();
        foreach (var module in d.GetProperty("modules").EnumerateArray())
        {
            if (!module.TryGetProperty("exchange_group", out var group) || group.ValueKind != JsonValueKind.Object) continue;
            if (!group.TryGetProperty("bonuses", out var bonuses) || bonuses.ValueKind != JsonValueKind.Array) continue;
            foreach (var b in bonuses.EnumerateArray())
            {
                if (!b.TryGetProperty("exchange_code", out var codeEl) || codeEl.GetString()?.Trim().ToUpperInvariant() is not { Length: > 0 } code) continue;
                if (b.TryGetProperty("code_status", out var status) && status.GetString() is { } s && s != "ON") continue;
                var amounts = b.TryGetProperty("icon_bonuses", out var icons) && icons.ValueKind == JsonValueKind.Array
                    ? string.Join(" + ", icons.EnumerateArray().Select(i => i.TryGetProperty("bonus_num", out var n) ? n.ToString() : "").Where(x => x != ""))
                    : "";
                if (offers.All(o => o.Code != code)) offers.Add(new CodeOffer(code, amounts));
            }
        }
        return offers;
    }

    public static async Task<(RedeemResult Result, string Message)> Redeem(string cookie, Game g, GameAccount a, string code)
    {
        var e = ByGame[g.Key];
        var root = await HoyoApi.Request(HttpMethod.Get,
            $"{e.RedeemUrl}?uid={a.Uid}&region={a.Region}&lang=en&cdkey={Uri.EscapeDataString(code)}&game_biz={g.Biz}&sLangKey=en-us", cookie);
        int retcode = root.GetProperty("retcode").GetInt32();
        string message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        var result = retcode switch
        {
            0 => RedeemResult.Redeemed,
            -2017 or -2018 => RedeemResult.AlreadyUsed,
            -2001 => RedeemResult.Expired,
            -2003 or -2004 => RedeemResult.Invalid,
            -2011 => RedeemResult.LevelTooLow,
            -2016 => RedeemResult.Cooldown,
            -1071 => RedeemResult.LoginNeeded,
            _ => RedeemResult.NeedsYou,
        };
        return (result, $"retcode {retcode} {message}");
    }

    public static void OpenCheckInPage(Game g) => OpenUrl(ByGame[g.Key].CheckInPage);
    public static void OpenRedeemPage(Game g, string code) => OpenUrl(ByGame[g.Key].RedeemPage + Uri.EscapeDataString(code));

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"Couldn't open {url}: {ex.Message}"); }
    }
}
