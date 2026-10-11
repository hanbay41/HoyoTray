using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace HoyoTray;

class TrayApp : ApplicationContext
{
    static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(20);
    // After a failed refresh, try again sooner than the normal poll interval.
    static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    readonly NotifyIcon tray = new();
    readonly ContextMenuStrip menu = new();
    readonly System.Windows.Forms.Timer pollTimer = new();
    readonly System.Windows.Forms.Timer retryTimer = new();
    readonly System.Windows.Forms.Timer tickTimer = new() { Interval = 30_000 };
    readonly MenuStyle style = new();
    readonly SynchronizationContext ui;
    Icon okIcon, badIcon;

    Settings settings = Settings.Load();
    string? cookie = Storage.LoadCookie();
    Dictionary<Game, List<GameAccount>>? accounts; // null until known (from cache or HoYoLAB)
    bool accountsFresh;                             // discovered from HoYoLAB during this run
    readonly Dictionary<string, Note> notes = [];  // keyed by Settings.Key(game, account)
    readonly Dictionary<string, string> errors = [];
    string? globalError;
    DateTime? lastUpdated;
    bool refreshing, loginOpen, settingsOpen, needsRetry, keepMenuOpen;
    int retryCount;
    RewardState rewards = new();
    bool rewardsRunning;
    Action? balloonAction; // what clicking the last notification does

    // What the currently built menu shows, so an open menu can be updated in place.
    readonly List<(Game Game, GameAccount Account, StatItem Item)> menuStats = [];
    TextItem? menuStatus;
    string menuLayout = "";
    bool buildingMenu;

    public TrayApp()
    {
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Log.Write($"Started v{Application.ProductVersion}");
        if (cookie != null) LoadCache();

        (okIcon, badIcon) = Theme.TrayIcons(settings.Icon);
        MenuRenderer.Apply(menu, style);
        menu.HandleCreated += (_, _) => Theme.RoundCorners(menu.Handle);

        tray.Icon = okIcon;
        tray.Text = "HoyoTray";
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        // Left click opens the same menu. NotifyIcon only exposes this privately; it also handles
        // focus so the menu closes when clicking elsewhere.
        tray.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) ShowMenu(); };
        tray.BalloonTipClicked += (_, _) =>
        {
            if (balloonAction != null) balloonAction();
            else if (cookie == null || globalError != null) _ = Login();
        };
        // NotifyIcon positions the menu using its size *before* Opening fires. Building it on mouse down
        // means that size is already right; otherwise a size change pushes the menu into the screen corner.
        tray.MouseDown += (_, _) => PrepareMenu();
        menu.Opening += (_, e) =>
        {
            PrepareMenu(); // still needed for keyboard opening (Win+B, Enter)
            e.Cancel = false; // WinForms pre-cancels opening an empty menu, i.e. the very first open
        };
        // ItemClicked fires before the menu closes itself, so it can veto that close.
        menu.ItemClicked += (_, e) => keepMenuOpen = e.ClickedItem is ActionItem { KeepOpen: true };
        menu.Closing += (_, e) =>
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked && keepMenuOpen) e.Cancel = true;
            keepMenuOpen = false;
        };

        pollTimer.Interval = (int)PollInterval.TotalMilliseconds;
        pollTimer.Tick += async (_, _) => await Refresh();
        retryTimer.Tick += async (_, _) => { retryTimer.Stop(); await Refresh(); };
        tickTimer.Tick += (_, _) => { UpdateTooltip(); UpdateMenuIfOpen(); };
        pollTimer.Start();
        tickTimer.Start();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;

        UpdateTooltip();
        if (cookie == null) _ = Login();
        else _ = Refresh();
    }

    void ShowMenu()
    {
        PrepareMenu();
        typeof(NotifyIcon).GetMethod("ShowContextMenu",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.Invoke(tray, null);
    }

    /// <summary>Windows notification; onClick runs if the user clicks it.</summary>
    void Notify(string text, Action? onClick = null, ToolTipIcon icon = ToolTipIcon.Info)
    {
        balloonAction = onClick;
        tray.ShowBalloonTip(5000, "HoyoTray", text.Length > 250 ? text[..250] + "…" : text, icon);
    }

    void LoadCache()
    {
        var cache = Cache.Load();
        if (cache.Accounts.Count > 0)
            accounts = Game.All.ToDictionary(g => g, g => cache.Accounts.GetValueOrDefault(g.Key) ?? []);
        foreach (var (key, note) in cache.Notes) notes[key] = note;
        lastUpdated = cache.LastUpdated;
        rewards = cache.Rewards ?? new();
        Log.Write($"Cache: {cache.Accounts.Values.Sum(l => l.Count)} accounts, {cache.Notes.Count} notes from {cache.LastUpdated:HH:mm}");
    }

    void SaveCache() => new Cache
    {
        Accounts = accounts?.ToDictionary(kv => kv.Key.Key, kv => kv.Value) ?? [],
        Notes = new(notes),
        LastUpdated = lastUpdated,
        Rewards = rewards,
    }.Save();

    async void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        await Task.Delay(TimeSpan.FromSeconds(15)); // give the network a moment
        await Refresh();
    }

    // Raised on a background thread; e.g. Wi-Fi connecting after Windows startup.
    void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) ui.Post(async _ => { if (needsRetry) await Refresh(); }, null);
    }

    /// <summary>Enabled games and the accounts the user chose to show, in display order.</summary>
    List<(Game Game, GameAccount Account)> Shown()
    {
        var result = new List<(Game, GameAccount)>();
        if (accounts == null) return result;
        foreach (var g in Game.All)
        {
            if (!settings.GameEnabled(g) || !accounts.TryGetValue(g, out var list)) continue;
            foreach (var a in list)
                if (settings.AccountShown(g, a, list)) result.Add((g, a));
        }
        return result;
    }

    async Task Login()
    {
        if (loginOpen) return;
        loginOpen = true;
        try
        {
            using var form = new LoginForm();
            if (form.ShowDialog() == DialogResult.OK && form.Cookie != null)
            {
                cookie = form.Cookie;
                Storage.SaveCookie(cookie);
                // Possibly a different HoYoLAB account: forget everything cached.
                Cache.Delete();
                accounts = null;
                accountsFresh = false;
                notes.Clear();
                errors.Clear();
                globalError = null;
                lastUpdated = null;
                await Refresh();
            }
        }
        finally { loginOpen = false; }
    }

    async Task OpenSettings()
    {
        if (settingsOpen) return;
        if (cookie == null) { await Login(); if (cookie == null) return; }
        if (!accountsFresh) await Refresh(); // make sure newly linked accounts are listed
        if (accounts == null)
        {
            MessageBox.Show("Couldn't load your game accounts from HoYoLAB.\n\n" + (globalError ?? "Try again in a moment."),
                "HoyoTray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        settingsOpen = true;
        try
        {
            using var form = new SettingsForm(settings, accounts);
            if (form.ShowDialog() != DialogResult.OK) return;

            bool iconChanged = form.Result.Icon != settings.Icon;
            settings = form.Result;
            settings.Save();
            if (iconChanged)
            {
                var (oldOk, oldBad) = (okIcon, badIcon);
                (okIcon, badIcon) = Theme.TrayIcons(settings.Icon);
                UpdateTooltip();
                oldOk.Dispose();
                oldBad.Dispose();
            }
            await Refresh(onlyMissing: true); // fetch accounts that were just switched on
        }
        finally { settingsOpen = false; }
    }

    async Task Refresh(bool onlyMissing = false)
    {
        if (refreshing) return;
        if (cookie == null) { globalError = "Not logged in"; UpdateTooltip(); return; }
        refreshing = true;
        UpdateTooltip();
        UpdateMenuIfOpen();
        bool transientFailure = false;
        try
        {
            globalError = null;

            if (!accountsFresh)
            {
                // All games at once; on failure the cached account list (if any) stays in use.
                var lists = await Task.WhenAll(Game.All.Select(g => HoyoApi.GetAccounts(cookie, g.Biz)));
                accounts = Game.All.Zip(lists).ToDictionary(p => p.First, p => p.Second);
                accountsFresh = true;
                Log.Write("Accounts: " + string.Join(", ", accounts.Select(kv => $"{kv.Key.Key}={kv.Value.Count}")));
            }

            var targets = Shown().Where(s => !onlyMissing || !notes.ContainsKey(Settings.Key(s.Game, s.Account))).ToList();
            var results = await Task.WhenAll(targets.Select(async s =>
            {
                try { return (s.Game, s.Account, Note: await s.Game.Fetch(cookie, s.Account), Error: (Exception?)null); }
                catch (Exception e) { return (s.Game, s.Account, Note: (Note?)null, Error: e); }
            }));

            foreach (var (g, a, note, error) in results)
            {
                var key = Settings.Key(g, a);
                if (note != null)
                {
                    notes[key] = note;
                    errors.Remove(key);
                    Log.Write($"{key} ok {note.Stamina.Current}/{note.Stamina.Max}, {note.Side}");
                }
                else if (error is HoyoException { AuthExpired: true }) SetAuthExpired();
                else
                {
                    // Keep the previous note so the estimate keeps ticking.
                    errors[key] = Describe(error!);
                    transientFailure |= IsTransient(error!);
                    Log.Write($"{key} error: {LogText(error!)}");
                }
            }

            if (globalError == null && results.Any(r => r.Note != null)) lastUpdated = DateTime.Now;
            SaveCache();
        }
        catch (HoyoException e) when (e.AuthExpired) { SetAuthExpired(); }
        catch (Exception e)
        {
            globalError = Describe(e);
            transientFailure |= IsTransient(e);
            Log.Write($"Refresh error: {LogText(e)}");
        }
        finally
        {
            refreshing = false;
            ScheduleRetry(transientFailure);
            UpdateTooltip();
            UpdateMenuIfOpen();
        }
        if (globalError == null && !transientFailure) _ = RunRewards();
    }

    // ---------------- Daily rewards: check-in and redemption codes ----------------

    IEnumerable<GameAccount> ShownAccounts(Game g) => Shown().Where(s => s.Game == g).Select(s => s.Account);

    static string CodeKey(Game g, GameAccount a, string code) => $"{g.Key}:{a.Uid}:{code}";

    /// <summary>Codes currently on HoYoLAB that still need redeeming for a shown account (skips ones left to the website).</summary>
    List<(Game Game, GameAccount Account, string Code)> PendingCodes(Func<Game, bool> include)
    {
        var result = new List<(Game, GameAccount, string)>();
        foreach (var g in Game.All)
        {
            if (!settings.GameEnabled(g) || settings.CodeMode(g) == "off" || !include(g)) continue;
            foreach (var offer in rewards.Offers.GetValueOrDefault(g.Key) ?? [])
                foreach (var a in ShownAccounts(g))
                {
                    var key = CodeKey(g, a, offer.Code);
                    if (!rewards.CodesDone.Contains(key) && !rewards.CodesNeedYou.Contains(key)) result.Add((g, a, offer.Code));
                }
        }
        return result;
    }

    /// <summary>Codes that hit a security check or similar, to redeem on the official website.</summary>
    List<(Game Game, string Code)> CodesForWebsite() =>
        Game.All.SelectMany(g => (rewards.Offers.GetValueOrDefault(g.Key) ?? [])
                .Where(o => ShownAccounts(g).Any(a => rewards.CodesNeedYou.Contains(CodeKey(g, a, o.Code))))
                .Select(o => (g, o.Code)))
            .ToList();

    List<Game> CheckInsForWebsite() =>
        Game.All.Where(g => settings.AutoCheckIn.Contains(g.Key)
            && rewards.CheckInNeedsYou.GetValueOrDefault(g.Key) == Rewards.Today
            && rewards.CheckedIn.GetValueOrDefault(g.Key) != Rewards.Today).ToList();

    /// <summary>
    /// Claims today's check-in where enabled, looks for new codes, redeems them in "auto" mode
    /// (or in "notify" mode when the user clicked Redeem), and sends one notification summarising it.
    /// </summary>
    async Task RunRewards(bool redeemNotifyCodes = false)
    {
        if (rewardsRunning || cookie == null || accounts == null) return;
        rewardsRunning = true;
        UpdateMenuIfOpen();
        var messages = new List<string>();
        Action? onClick = null;
        try
        {
            string today = Rewards.Today;

            var claimed = new List<string>();
            foreach (var g in Game.All)
            {
                if (!settings.GameEnabled(g) || !settings.AutoCheckIn.Contains(g.Key) || !ShownAccounts(g).Any()) continue;
                if (rewards.CheckedIn.GetValueOrDefault(g.Key) == today) continue;
                try
                {
                    var (signed, days) = await Rewards.GetCheckIn(cookie, g);
                    if (signed) { rewards.CheckedIn[g.Key] = today; continue; }
                    var result = await Rewards.CheckIn(cookie, g);
                    Log.Write($"Check-in {g.Key}: {result}");
                    if (result == CheckInResult.NeedsYou)
                    {
                        if (rewards.CheckInNeedsYou.GetValueOrDefault(g.Key) != today)
                            messages.Add($"{g.Name} check-in hit a security check. Open the menu to claim it on the website.");
                        rewards.CheckInNeedsYou[g.Key] = today;
                        onClick = ShowMenu;
                        continue;
                    }
                    rewards.CheckedIn[g.Key] = today;
                    rewards.CheckInNeedsYou.Remove(g.Key);
                    if (result == CheckInResult.Claimed)
                    {
                        string? reward = null;
                        try { reward = await Rewards.CheckInReward(cookie, g, days); } catch { }
                        claimed.Add(reward == null ? g.Name : $"{g.Name} ({reward})");
                    }
                }
                catch (Exception e) { Log.Write($"Check-in {g.Key} error: {LogText(e)}"); }
            }
            if (claimed.Count > 0) messages.Insert(0, "Checked in: " + string.Join(", ", claimed));

            foreach (var g in Game.All)
            {
                if (!settings.GameEnabled(g) || settings.CodeMode(g) == "off" || !ShownAccounts(g).Any()) continue;
                try
                {
                    var offers = await Rewards.GetCodes(g);
                    rewards.Offers[g.Key] = offers;
                    if (offers.Count > 0) Log.Write($"Codes {g.Key}: {string.Join(", ", offers.Select(o => o.Code))}");
                }
                catch (Exception e) { Log.Write($"Codes {g.Key} error: {LogText(e)}"); }
            }

            var fresh = new List<(Game Game, string Code)>();
            foreach (var (g, _, code) in PendingCodes(g => settings.CodeMode(g) == "notify"))
                if (rewards.CodesAnnounced.Add($"{g.Key}:{code}")) fresh.Add((g, code));
            if (fresh.Count > 0 && !redeemNotifyCodes)
            {
                messages.Add(fresh.Count == 1
                    ? $"New {fresh[0].Game.Name} code: {fresh[0].Code}. Click to redeem."
                    : $"{fresh.Count} new redemption codes. Click to redeem.");
                onClick = () => _ = RunRewards(redeemNotifyCodes: true);
            }

            var todo = PendingCodes(g => settings.CodeMode(g) == "auto" || (redeemNotifyCodes && settings.CodeMode(g) == "notify"));
            var (redeemMessages, loginNeeded) = await RedeemCodes(todo);
            messages.AddRange(redeemMessages);
            if (loginNeeded) onClick = () => _ = Login();
            else if (redeemMessages.Any(m => m.Contains("website"))) onClick = ShowMenu;
        }
        finally
        {
            rewardsRunning = false;
            SaveCache();
            UpdateMenuIfOpen();
        }
        if (messages.Count > 0) Notify(string.Join("\n", messages), onClick);
    }

    async Task<(List<string> Messages, bool LoginNeeded)> RedeemCodes(List<(Game Game, GameAccount Account, string Code)> todo)
    {
        var messages = new List<string>();
        var redeemed = new List<string>();
        bool loginNeeded = false;
        for (int i = 0; i < todo.Count; i++)
        {
            var (g, a, code) = todo[i];
            var key = CodeKey(g, a, code);
            if (i > 0) await Task.Delay(TimeSpan.FromSeconds(5.5)); // HoYoverse rejects redemptions less than ~5 s apart
            try
            {
                var (result, detail) = await Rewards.Redeem(cookie!, g, a, code);
                if (result == RedeemResult.Cooldown)
                {
                    await Task.Delay(TimeSpan.FromSeconds(6));
                    (result, detail) = await Rewards.Redeem(cookie!, g, a, code);
                }
                Log.Write($"Redeem {key}: {result} ({detail})");
                switch (result)
                {
                    case RedeemResult.Redeemed:
                        rewards.CodesDone.Add(key);
                        redeemed.Add(todo.Count(t => t.Game == g && t.Code == code) > 1 ? $"{code} ({a.Nickname})" : code);
                        break;
                    case RedeemResult.AlreadyUsed or RedeemResult.Expired or RedeemResult.Invalid or RedeemResult.LevelTooLow:
                        rewards.CodesDone.Add(key);
                        break;
                    case RedeemResult.LoginNeeded:
                        loginNeeded = true;
                        break;
                    default:
                        rewards.CodesNeedYou.Add(key);
                        messages.Add($"{code} needs to be redeemed on the website (open the menu).");
                        break;
                }
            }
            catch (Exception e) { Log.Write($"Redeem {key} error: {LogText(e)}"); }
            if (loginNeeded) break;
            UpdateMenuIfOpen();
        }
        if (loginNeeded) messages.Add("Redeeming codes needs a fresh HoYoLAB login. Click here to log in again.");
        if (redeemed.Count > 0) messages.Insert(0, "Redeemed: " + string.Join(", ", redeemed));
        return (messages, loginNeeded);
    }

    /// <summary>Reward-related menu rows: (glyph, text, action, keep menu open).</summary>
    List<(string Glyph, string Text, Action Run, bool KeepOpen)> RewardMenuItems()
    {
        var items = new List<(string, string, Action, bool)>();
        var notify = PendingCodes(g => settings.CodeMode(g) == "notify").Select(p => p.Code).Distinct().ToList();
        if (notify.Count > 0 && !rewardsRunning)
            items.Add((style.GiftGlyph, notify.Count == 1 ? $"Redeem new code {notify[0]}" : $"Redeem {notify.Count} new codes",
                () => _ = RunRewards(redeemNotifyCodes: true), true));
        foreach (var (g, code) in CodesForWebsite())
            items.Add(("\uE774", $"Redeem {code} on the website", () => Rewards.OpenRedeemPage(g, code), false));
        foreach (var g in CheckInsForWebsite())
            items.Add(("\uE774", $"Claim {g.Name} check-in online", () => Rewards.OpenCheckInPage(g), false));
        return items;
    }

    // Posted so it never runs inside a menu item's own click handling.
    void UpdateMenuIfOpen()
    {
        if (menu.Visible) ui.Post(_ => UpdateMenu(), null);
    }

    /// <summary>
    /// Refreshes an open menu. Same layout → update the existing items and repaint (no resize, no flicker).
    /// Different layout (cards added/removed, warnings appearing) → full rebuild.
    /// </summary>
    void UpdateMenu()
    {
        if (menu.Visible) PrepareMenu();
    }

    /// <summary>Rebuilds the menu only if its layout changed; otherwise just refreshes the values shown.</summary>
    void PrepareMenu()
    {
        if (buildingMenu) return;
        if (menu.Items.Count == 0 || MenuLayout() != menuLayout) { BuildMenu(); return; }
        foreach (var (g, a, item) in menuStats) FillStat(item, g, a);
        if (menuStatus != null) menuStatus.Value = StatusText();
        menu.Invalidate(true);
    }

    void ScheduleRetry(bool failed)
    {
        needsRetry = failed;
        retryTimer.Stop();
        if (!failed) { retryCount = 0; return; }
        var delay = RetryDelays[Math.Min(retryCount, RetryDelays.Length - 1)];
        retryCount++;
        retryTimer.Interval = (int)delay.TotalMilliseconds;
        retryTimer.Start();
        Log.Write($"Retrying in {delay.TotalSeconds:0}s");
    }

    void SetAuthExpired()
    {
        bool first = globalError != "Login expired";
        globalError = "Login expired";
        if (first)
        {
            Log.Write("Login expired");
            Notify("HoYoLAB login expired. Click here or right-click → Log in.", () => _ = Login(), ToolTipIcon.Warning);
        }
    }

    // Worth retrying soon: network trouble or rate limiting. Not: bad login, notes not public, etc.
    static bool IsTransient(Exception e) =>
        e is HttpRequestException or TaskCanceledException or HoyoException { Retcode: 1034 or 10035 or 5003 };

    static string Describe(Exception e) => e switch
    {
        HoyoException { NotPublic: true } => "Enable Real-Time Notes in HoYoLAB",
        HoyoException { Retcode: 1034 or 10035 or 5003 } => "Captcha/rate limit, will retry",
        HoyoException h => $"API error {h.Retcode}: {h.Message}",
        HttpRequestException or TaskCanceledException => "Network error, retrying",
        _ => e.Message,
    };

    static string LogText(Exception e) => e is HoyoException h ? $"retcode {h.Retcode} {h.Message}" : $"{e.GetType().Name}: {e.Message}";

    void UpdateTooltip()
    {
        var lines = new List<string>();
        var shown = Shown();
        foreach (var group in shown.GroupBy(s => s.Game))
        {
            var withNotes = group.Where(s => notes.ContainsKey(Settings.Key(s.Game, s.Account)))
                .Select(s => notes[Settings.Key(s.Game, s.Account)].Stamina).ToList();
            if (withNotes.Count == 0) continue;
            lines.Add(withNotes.Count == 1
                ? $"{group.Key.ShortName} {withNotes[0].Estimate()}/{withNotes[0].Max} ({withNotes[0].FullIn()})"
                : $"{group.Key.ShortName} " + string.Join(", ", withNotes.Select(s => $"{s.Estimate()}/{s.Max}")));
        }
        if (globalError != null) lines.Add("⚠ " + globalError);
        else if (shown.Select(s => errors.GetValueOrDefault(Settings.Key(s.Game, s.Account))).FirstOrDefault(e => e != null) is { } err)
            lines.Add("⚠ " + err);
        if (lines.Count == 0)
            lines.Add(refreshing || accounts == null ? "HoyoTray – loading…" : "HoyoTray – nothing selected");

        var text = string.Join("\n", lines);
        tray.Text = text.Length > 127 ? text[..127] : text;
        tray.Icon = globalError != null ? badIcon : okIcon;
    }

    // Everything that changes which items exist or their heights.
    string MenuLayout()
    {
        var shown = Shown();
        var parts = shown.Select(s => Settings.Key(s.Game, s.Account) + (errors.ContainsKey(Settings.Key(s.Game, s.Account)) ? "!" : "")).ToList();
        if (shown.Count == 0) parts.Add(EmptyText());
        parts.Add(globalError ?? "");
        parts.Add($"{Theme.Current().Dark}/{menu.DeviceDpi}/{cookie == null}/{Storage.StartupEnabled}");
        parts.AddRange(RewardMenuItems().Select(r => r.Text));
        return string.Join("|", parts);
    }

    string EmptyText() => refreshing || accounts == null ? "Loading your accounts…" : "Nothing selected. Pick games in Settings.";

    string StatusText() => refreshing ? "Updating…"
        : rewardsRunning ? "Checking daily rewards…"
        : lastUpdated is { } t ? $"Updated {t:HH:mm} · next {t + PollInterval:HH:mm}"
        : "Not updated yet";

    void FillStat(StatItem item, Game g, GameAccount a)
    {
        var key = Settings.Key(g, a);
        item.Warning = errors.GetValueOrDefault(key);
        item.Badge = null;
        if (settings.AutoCheckIn.Contains(g.Key))
        {
            if (rewards.CheckedIn.GetValueOrDefault(g.Key) == Rewards.Today) (item.Badge, item.BadgeColor) = ("✓ Checked in", Theme.Good);
            else if (rewards.CheckInNeedsYou.GetValueOrDefault(g.Key) == Rewards.Today) (item.Badge, item.BadgeColor) = ("⚠ Check-in", Theme.Warn);
        }
        if (notes.TryGetValue(key, out var note))
        {
            var s = note.Stamina;
            item.Current = s.Estimate();
            item.Max = s.Max;
            item.Footer = s.Estimate() >= s.Max ? "Full!" : $"F{s.FullIn()[1..]} · {s.FullAt:HH:mm}";
            item.Side = note.Side;
            item.SideDone = note.SideDone;
        }
        else
        {
            item.Current = item.Max = 0;
            item.Footer = refreshing ? "Loading…" : "No data yet";
            item.Side = "";
            item.SideDone = false;
        }
    }

    void BuildMenu()
    {
        buildingMenu = true;
        try
        {
            style.T = Theme.Current();
            style.Scale = menu.DeviceDpi / 96f;

            foreach (ToolStripItem old in menu.Items.Cast<ToolStripItem>().ToList()) old.Dispose();
            menu.Items.Clear();
            menuStats.Clear();

            var shown = Shown();
            foreach (var (g, a) in shown)
            {
                if (menu.Items.Count > 0) menu.Items.Add(new SepItem(style));
                bool several = shown.Count(s => s.Game == g) > 1;
                var item = new StatItem(style)
                {
                    Game = g.Name,
                    Account = several ? $"{a.Nickname} · {a.Uid}" : a.Nickname,
                    Label = g.StaminaName,
                    Accent = g.Accent,
                };
                FillStat(item, g, a);
                menuStats.Add((g, a, item));
                menu.Items.Add(item);
            }
            if (shown.Count == 0) menu.Items.Add(new TextItem(style, EmptyText()));

            if (globalError != null) menu.Items.Add(new TextItem(style, "⚠  " + globalError, Theme.Warn));
            menuStatus = new TextItem(style, StatusText());
            menu.Items.Add(menuStatus);

            menu.Items.Add(new SepItem(style));
            foreach (var (glyph, text, run, keepOpen) in RewardMenuItems())
                menu.Items.Add(new ActionItem(style, glyph, text, (_, _) => run()) { KeepOpen = keepOpen });
            menu.Items.Add(new ActionItem(style, "\uE72C", "Refresh now", async (_, _) => await Refresh()) { KeepOpen = true });
            menu.Items.Add(new ActionItem(style, "\uE713", "Settings…", async (_, _) => await OpenSettings()));
            menu.Items.Add(new ActionItem(style, "\uE7E8", "Start with Windows", (s, _) =>
            {
                Storage.StartupEnabled = !Storage.StartupEnabled;
                var item = (ActionItem)s!;
                item.Toggle = Storage.StartupEnabled;
                item.Invalidate();
            }) { Toggle = Storage.StartupEnabled, KeepOpen = true });
            menu.Items.Add(new ActionItem(style, "\uE77B", cookie == null ? "Log in…" : "Log in again…", async (_, _) => await Login()));
            menu.Items.Add(new SepItem(style));
            menu.Items.Add(new ActionItem(style, "\uE711", "Exit", (_, _) => ExitThread()));

            menuLayout = MenuLayout();
            // Resizing a visible menu pumps messages, hence the buildingMenu guard against re-entry.
            MenuRenderer.FitWidth(menu, style);
        }
        finally { buildingMenu = false; }
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        tray.Visible = false;
        tray.Dispose();
        base.ExitThreadCore();
    }
}
