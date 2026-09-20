using System.Globalization;

namespace ReplyFive.Desktop.Services;

/// <summary>動作診断ログ（app.log）。本文（会話文脈・入力・生成文）は書かない。</summary>
public static class Diag
{
    static readonly object gate = new();
    static readonly Dictionary<string, string> lastLines = [];
    public static string Path { get; } = System.IO.Path.Combine(AppPaths.LogDir, "app.log");

    /// <summary>同じ内容が続く間は 1 回だけ書く（0.5 秒ごとの収集で同じ行が並ばないように）。</summary>
    public static void LogIfChanged(string s)
    {
        var key = s.Split(' ', 2)[0];
        bool same;
        lock (gate)
        {
            same = lastLines.TryGetValue(key, out var prev) && prev == s;
            if (!same) lastLines[key] = s;
        }
        if (!same) Log(s);
    }

    public static void Log(string s)
    {
        var line = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + " " + s + "\n";
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                if (File.Exists(Path) && new FileInfo(Path).Length > 4 * 1024 * 1024) File.Move(Path, Path + ".1", overwrite: true);
                File.AppendAllText(Path, line);
            }
            catch (Exception) { }
        }
    }
}
