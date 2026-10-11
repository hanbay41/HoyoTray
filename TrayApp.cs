using Microsoft.Win32;

namespace HoyoTray;

class TrayApp : ApplicationContext
{
    static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(20);

    readonly NotifyIcon tray = new();
    readonly ContextMenuStrip menu = new();
    readonly System.Windows.Forms.Timer pollTimer = new();
    readonly System.Windows.Forms.Timer tickTimer = new() { Interval = 30_000 };
    readonly MenuStyle style = new();
    readonly Icon okIcon, badIcon;

    string? cookie = Storage.LoadCookie();
    GameAccount? genshinAcc, zzzAcc;
    bool genshinChecked, zzzChecked;
    GenshinNote? genshin;
    ZzzNote? zzz;
    string? genshinError, zzzError, globalError;
    DateTime? lastUpdated;
    bool refreshing, loginOpen;

    public TrayApp()
    {
        (okIcon, badIcon) = Theme.TrayIcons();
        MenuRenderer.Apply(menu, style);
        menu.HandleCreated += (_, _) => Theme.RoundCorners(menu.Handle);

        tray.Icon = okIcon;
        tray.Text = "HoyoTray";
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        // Left click opens the same menu. NotifyIcon only exposes this privately; it also handles
        // focus so the menu closes when clicking elsewhere.
        tray.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                typeof(NotifyIcon).GetMethod("ShowContextMenu",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.Invoke(tray, null);
        };
        tray.BalloonTipClicked += (_, _) => { if (cookie == null || globalError != null) _ = Login(); };
        menu.Opening += (_, _) => BuildMenu();

        pollTimer.Interval = (int)PollInterval.TotalMilliseconds;
        pollTimer.Tick += async (_, _) => await Refresh();
        tickTimer.Tick += (_, _) => UpdateTooltip();
        pollTimer.Start();
        tickTimer.Start();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        if (cookie == null) _ = Login();
        else _ = Refresh();
    }

    async void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        await Task.Delay(TimeSpan.FromSeconds(15)); // give the network a moment
        await Refresh();
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
                genshinChecked = zzzChecked = false; // re-detect accounts
                globalError = null;
                await Refresh();
            }
        }
        finally { loginOpen = false; }
    }

    async Task Refresh()
    {
        if (refreshing) return;
        if (cookie == null) { globalError = "Not logged in"; UpdateTooltip(); return; }
        refreshing = true;
        try
        {
            globalError = null;

            if (!genshinChecked) { genshinAcc = await HoyoApi.GetAccount(cookie, "hk4e_global"); genshinChecked = true; }
            if (!zzzChecked) { zzzAcc = await HoyoApi.GetAccount(cookie, "nap_global"); zzzChecked = true; }

            if (genshinAcc != null)
                (genshin, genshinError) = await Fetch(() => HoyoApi.GetGenshin(cookie, genshinAcc));
            if (zzzAcc != null)
                (zzz, zzzError) = await Fetch(() => HoyoApi.GetZzz(cookie, zzzAcc));

            if (globalError == null) lastUpdated = DateTime.Now;
        }
        catch (HoyoException e) when (e.AuthExpired) { SetAuthExpired(); }
        catch (Exception e) { globalError = Describe(e); }
        finally
        {
            refreshing = false;
            UpdateTooltip();
        }
    }

    // Per-game fetch: keeps the previous value on failure so the estimate keeps ticking.
    async Task<(T?, string?)> Fetch<T>(Func<Task<T>> f) where T : class
    {
        var previous = typeof(T) == typeof(GenshinNote) ? genshin as T : zzz as T;
        try { return (await f(), null); }
        catch (HoyoException e) when (e.AuthExpired) { SetAuthExpired(); return (previous, null); }
        catch (Exception e) { return (previous, Describe(e)); }
    }

    void SetAuthExpired()
    {
        bool first = globalError != "Login expired";
        globalError = "Login expired";
        if (first)
            tray.ShowBalloonTip(5000, "HoyoTray", "HoYoLAB login expired. Click here or right-click → Log in.", ToolTipIcon.Warning);
    }

    static string Describe(Exception e) => e switch
    {
        HoyoException { NotPublic: true } => "Enable Real-Time Notes in HoYoLAB",
        HoyoException { Retcode: 1034 or 10035 or 5003 } => "Captcha/rate limit, will retry",
        HoyoException h => $"API error {h.Retcode}: {h.Message}",
        HttpRequestException or TaskCanceledException => "Network error",
        _ => e.Message,
    };

    void UpdateTooltip()
    {
        var lines = new List<string>();
        if (genshin != null) lines.Add($"Resin {genshin.Resin.Estimate()}/{genshin.Resin.Max} ({genshin.Resin.FullIn()})");
        if (zzz != null) lines.Add($"Battery {zzz.Battery.Estimate()}/{zzz.Battery.Max} ({zzz.Battery.FullIn()})");
        if (globalError != null) lines.Add("⚠ " + globalError);
        else if (genshinError != null || zzzError != null) lines.Add("⚠ " + (genshinError ?? zzzError));
        if (lines.Count == 0) lines.Add("HoyoTray – loading…");

        var text = string.Join("\n", lines);
        tray.Text = text.Length > 127 ? text[..127] : text;
        tray.Icon = globalError != null ? badIcon : okIcon;
    }

    void BuildMenu()
    {
        style.T = Theme.Current();
        style.Scale = menu.DeviceDpi / 96f;

        foreach (ToolStripItem old in menu.Items.Cast<ToolStripItem>().ToList()) old.Dispose();
        menu.Items.Clear();

        static string Footer(Stamina s) =>
            s.Estimate() >= s.Max ? "Full!" : $"F{s.FullIn()[1..]} · {s.FullAt:HH:mm}";

        if (genshinAcc != null)
        {
            var item = new StatItem(style)
            {
                Game = "Genshin Impact", Account = genshinAcc.Nickname, Label = "Original Resin",
                Accent = Theme.Resin, Warning = genshinError,
            };
            if (genshin != null)
            {
                item.Current = genshin.Resin.Estimate();
                item.Max = genshin.Resin.Max;
                item.Footer = Footer(genshin.Resin);
                item.Side = $"Commissions {genshin.Dailies}/{genshin.DailiesMax}" + (genshin.DailyRewardClaimed ? " ✓" : "");
                item.SideDone = genshin.DailyRewardClaimed;
            }
            menu.Items.Add(item);
        }
        if (zzzAcc != null)
        {
            if (genshinAcc != null) menu.Items.Add(new SepItem(style));
            var item = new StatItem(style)
            {
                Game = "Zenless Zone Zero", Account = zzzAcc.Nickname, Label = "Battery Charge",
                Accent = Theme.Battery, Warning = zzzError,
            };
            if (zzz != null)
            {
                item.Current = zzz.Battery.Estimate();
                item.Max = zzz.Battery.Max;
                item.Footer = Footer(zzz.Battery);
                item.Side = $"Engagement {zzz.Engagement}/{zzz.EngagementMax}" + (zzz.Engagement >= zzz.EngagementMax ? " ✓" : "");
                item.SideDone = zzz.Engagement >= zzz.EngagementMax;
            }
            menu.Items.Add(item);
        }

        if (globalError != null) menu.Items.Add(new TextItem(style, "⚠  " + globalError, Theme.Warn));
        menu.Items.Add(new TextItem(style, lastUpdated is { } t
            ? $"Updated {t:HH:mm} · next {t + PollInterval:HH:mm}"
            : "Not updated yet"));

        menu.Items.Add(new SepItem(style));
        menu.Items.Add(new ActionItem(style, "\uE72C", "Refresh now", async (_, _) => await Refresh()));
        menu.Items.Add(new ActionItem(style, "\uE7E8", "Start with Windows",
            (_, _) => Storage.StartupEnabled = !Storage.StartupEnabled) { Toggle = Storage.StartupEnabled });
        menu.Items.Add(new ActionItem(style, "\uE77B", cookie == null ? "Log in…" : "Log in again…", async (_, _) => await Login()));
        menu.Items.Add(new SepItem(style));
        menu.Items.Add(new ActionItem(style, "\uE711", "Exit", (_, _) => ExitThread()));

        MenuRenderer.FitWidth(menu, style);
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        tray.Visible = false;
        tray.Dispose();
        base.ExitThreadCore();
    }
}
