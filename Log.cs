namespace HoyoTray;

/// <summary>Small rolling log in %LOCALAPPDATA%\HoyoTray\log.txt for troubleshooting. Never logs cookies.</summary>
static class Log
{
    static string FilePath => Path.Combine(Storage.Dir, "log.txt");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Storage.Dir);
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > 256 * 1024)
                File.Move(FilePath, FilePath + ".old", overwrite: true);
            File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { }
    }
}
