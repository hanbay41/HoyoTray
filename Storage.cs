using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace HoyoTray;

static class Storage
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HoyoTray");

    public static string WebViewDir => Path.Combine(Dir, "WebView2");
    static string CookieFile => Path.Combine(Dir, "cookie.bin");

    // Cookie string is encrypted with DPAPI, so only this Windows user can read it.
    public static string? LoadCookie()
    {
        try
        {
            if (!File.Exists(CookieFile)) return null;
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(CookieFile), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return null; }
    }

    public static void SaveCookie(string cookie)
    {
        Directory.CreateDirectory(Dir);
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(cookie), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(CookieFile, bytes);
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "HoyoTray";

    public static bool StartupEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(RunName, false);
        }
    }
}
