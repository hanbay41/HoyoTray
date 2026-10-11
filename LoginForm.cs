using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HoyoTray;

/// <summary>
/// Shows hoyolab.com in an embedded browser. Once the session cookies exist and actually work,
/// stores them and closes. The browser profile persists, so re-login is often automatic.
/// </summary>
class LoginForm : Form
{
    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    readonly System.Windows.Forms.Timer poll = new() { Interval = 1500 };
    bool checking;

    public string? Cookie { get; private set; }

    public LoginForm()
    {
        Text = "HoyoTray – log in to HoYoLAB";
        Size = new Size(1000, 760);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        Controls.Add(web);
        poll.Tick += async (_, _) => await CheckCookies();
        Load += async (_, _) => await Init();
        FormClosed += (_, _) => poll.Stop();
    }

    async Task Init()
    {
        var env = await CoreWebView2Environment.CreateAsync(null, Storage.WebViewDir);
        await web.EnsureCoreWebView2Async(env);
        web.CoreWebView2.Navigate("https://www.hoyolab.com/home");
        poll.Start();
    }

    async Task CheckCookies()
    {
        if (checking || web.CoreWebView2 == null) return;
        checking = true;
        try
        {
            var cookies = await web.CoreWebView2.CookieManager.GetCookiesAsync("https://www.hoyolab.com");
            if (!cookies.Any(c => c.Name == "ltoken_v2") || !cookies.Any(c => c.Name == "ltuid_v2")) return;

            var cookie = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
            // Validate: an old expired token may still sit in the browser profile.
            try { await HoyoApi.GetAccount(cookie, "hk4e_global"); }
            catch (HoyoException e) when (e.AuthExpired) { return; }

            poll.Stop();
            Cookie = cookie;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch { /* transient; try again next tick */ }
        finally { checking = false; }
    }
}
