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
    Icon okIcon, badIcon;

    Settings settings = Settings.Load();
    string? cookie = Storage.LoadCookie();
    Dictionary<Game, List<GameAccount>>? accounts; // null until discovered for the current login
    readonly Dictionary<string, Note> notes = [];  // keyed by Settings.Key(game, account)
    readonly Dictionary<string, string> errors = [];
    string? globalError;
    DateTime? lastUpdated;
    bool refreshing, loginOpen, settingsOpen;

    public TrayApp()
    {
        (okIcon, badIcon) = Theme.TrayIcons(settings.Icon);
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
                accounts = null; // re-detect accounts for this login
                notes.Clear();
                errors.Clear();
                globalError = null;
                await Refresh();
            }
        }
        finally { loginOpen = false; }
    }

    async Task OpenSettings()
    {
        if (settingsOpen) return;
        if (cookie == null) { await Login(); if (cookie == null) return; }
        if (accounts == null) await Refresh();
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
        try
        {
            globalError = null;

            if (accounts == null)
            {
                var found = new Dictionary<Game, List<GameAccount>>();
                foreach (var g in Game.All)
                    found[g] = await HoyoApi.GetAccounts(cookie, g.Biz);
                accounts = found;
                Log.Write("Accounts: " + string.Join(", ", found.Select(kv => $"{kv.Key.Key}={kv.Value.Count}")));
            }

            foreach (var (g, a) in Shown())
            {
                var key = Settings.Key(g, a);
                if (onlyMissing && notes.ContainsKey(key)) continue;
                try
                {
                    var note = await g.Fetch(cookie, a);
                    notes[key] = note;
                    errors.Remove(key);
                    Log.Write($"{key} ok {note.Stamina.Current}/{note.Stamina.Max}, {note.Side}");
                }
                catch (HoyoException e) when (e.AuthExpired) { SetAuthExpired(); break; }
                catch (Exception e)
                {
                    // Keep the previous note so the estimate keeps ticking.
                    errors[key] = Describe(e);
                    Log.Write($"{key} error: {LogText(e)}");
                }
            }

            if (globalError == null) lastUpdated = DateTime.Now;
        }
        catch (HoyoException e) when (e.AuthExpired) { SetAuthExpired(); }
        catch (Exception e)
        {
            globalError = Describe(e);
            Log.Write($"Refresh error: {LogText(e)}");
        }
        finally
        {
            refreshing = false;
            UpdateTooltip();
        }
    }

    void SetAuthExpired()
    {
        bool first = globalError != "Login expired";
        globalError = "Login expired";
        if (first)
        {
            Log.Write("Login expired");
            tray.ShowBalloonTip(5000, "HoyoTray", "HoYoLAB login expired. Click here or right-click → Log in.", ToolTipIcon.Warning);
        }
    }

    static string Describe(Exception e) => e switch
    {
        HoyoException { NotPublic: true } => "Enable Real-Time Notes in HoYoLAB",
        HoyoException { Retcode: 1034 or 10035 or 5003 } => "Captcha/rate limit, will retry",
        HoyoException h => $"API error {h.Retcode}: {h.Message}",
        HttpRequestException or TaskCanceledException => "Network error",
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
        if (lines.Count == 0) lines.Add(accounts != null && shown.Count == 0 ? "HoyoTray – nothing selected" : "HoyoTray – loading…");

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

        var shown = Shown();
        foreach (var (g, a) in shown)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new SepItem(style));
            var key = Settings.Key(g, a);
            bool several = shown.Count(s => s.Game == g) > 1;
            var item = new StatItem(style)
            {
                Game = g.Name,
                Account = several ? $"{a.Nickname} · {a.Uid}" : a.Nickname,
                Label = g.StaminaName,
                Accent = g.Accent,
                Warning = errors.GetValueOrDefault(key),
            };
            if (notes.TryGetValue(key, out var note))
            {
                item.Current = note.Stamina.Estimate();
                item.Max = note.Stamina.Max;
                item.Footer = Footer(note.Stamina);
                item.Side = note.Side;
                item.SideDone = note.SideDone;
            }
            menu.Items.Add(item);
        }
        if (accounts != null && shown.Count == 0)
            menu.Items.Add(new TextItem(style, "Nothing selected. Pick games in Settings."));

        if (globalError != null) menu.Items.Add(new TextItem(style, "⚠  " + globalError, Theme.Warn));
        menu.Items.Add(new TextItem(style, lastUpdated is { } t
            ? $"Updated {t:HH:mm} · next {t + PollInterval:HH:mm}"
            : "Not updated yet"));

        menu.Items.Add(new SepItem(style));
        menu.Items.Add(new ActionItem(style, "", "Refresh now", async (_, _) => await Refresh()));
        menu.Items.Add(new ActionItem(style, "", "Settings…", async (_, _) => await OpenSettings()));
        menu.Items.Add(new ActionItem(style, "", "Start with Windows",
            (_, _) => Storage.StartupEnabled = !Storage.StartupEnabled) { Toggle = Storage.StartupEnabled });
        menu.Items.Add(new ActionItem(style, "", cookie == null ? "Log in…" : "Log in again…", async (_, _) => await Login()));
        menu.Items.Add(new SepItem(style));
        menu.Items.Add(new ActionItem(style, "", "Exit", (_, _) => ExitThread()));

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
