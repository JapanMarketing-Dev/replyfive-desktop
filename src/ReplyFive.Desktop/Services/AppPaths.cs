namespace ReplyFive.Desktop.Services;

/// <summary>端末内の保存場所。設定（秘密でないもの）・暗号化した記録・ログ・一時ソケット。</summary>
public static class AppPaths
{
    const string Name = "ReplyFive";

    /// <summary>設定ファイル（settings.json）。Windows は %APPDATA%\ReplyFive、Linux は $XDG_CONFIG_HOME/replyfive。</summary>
    public static string ConfigDir { get; } = Resolve(Environment.SpecialFolder.ApplicationData, "replyfive");

    /// <summary>暗号化した記録（learning.bin など）と更新の一時置き場。Windows は %LOCALAPPDATA%\ReplyFive、Linux は $XDG_DATA_HOME/replyfive。</summary>
    public static string DataDir { get; } = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name)
        : OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", Name + "Desktop")
            : Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"), "replyfive");

    /// <summary>診断ログ。本文は書かない。</summary>
    public static string LogDir { get; } = OperatingSystem.IsWindows()
        ? Path.Combine(DataDir, "logs")
        : OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", Name + "Desktop")
            : Path.Combine(Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } s ? s : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"), "replyfive");

    public static string RuntimeDir { get; } = OperatingSystem.IsWindows() ? DataDir
        : Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } r ? Path.Combine(r, "replyfive") : Path.Combine(DataDir, "run");

    public static string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    public static string LearningFile => Path.Combine(DataDir, "learning.bin");
    public static string ConversationsFile => Path.Combine(DataDir, "conversations.bin");
    public static string StyleFile => Path.Combine(DataDir, "style.bin");
    public static string EvaluationsFile => Path.Combine(DataDir, "background-evaluations.bin");
    public static string EventsFile => Path.Combine(DataDir, "events.jsonl");
    public static string UpdatesDir => Path.Combine(DataDir, "updates");

    /// <summary>管理配布の接続先（サーバ URL だけ）。Windows は HKLM\Software\ReplyFive の ServerURL、Linux は /etc/replyfive/config.json。</summary>
    public static string ManagedConfigFile => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Name, "config.json")
        : "/etc/replyfive/config.json";

    static string Resolve(Environment.SpecialFolder folder, string linuxName)
    {
        if (OperatingSystem.IsWindows()) return Path.Combine(Environment.GetFolderPath(folder), Name);
        if (OperatingSystem.IsMacOS()) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Preferences", Name + "Desktop");
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var basePath = string.IsNullOrEmpty(xdg) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config") : xdg;
        return Path.Combine(basePath, linuxName);
    }

    public static void EnsureDirs()
    {
        foreach (var d in new[] { ConfigDir, DataDir, LogDir })
        {
            try
            {
                Directory.CreateDirectory(d);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (Exception) { }
        }
    }
}
