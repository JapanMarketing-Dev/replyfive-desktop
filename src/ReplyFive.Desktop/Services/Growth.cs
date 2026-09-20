using System.Globalization;
using ReplyFive.Core;
using ReplyFive.Desktop.Platform;

namespace ReplyFive.Desktop.Services;

/// <summary>成長計測の記録点（docs/marketing/growth-analytics.md）。本文・相手の名前・ウインドウタイトルは入れない。
/// 端末内の JSONL キューに溜め、起動時・パネルを閉じたとき・60 秒ごとに送る。</summary>
public static class Growth
{
    static GrowthQueue? queue;
    static AppSettings? settings;
    static IPlatform? platform;
    static readonly DateTimeOffset launchedAt = DateTimeOffset.UtcNow;
    static Timer? flushTimer;
    static Timer? accessibilityTimer;

    public static void Track(string name, Dictionary<string, GrowthProp>? props = null)
    {
        queue?.Record(new GrowthEvent(name, props));
        Diag.Log("growth " + name);
    }

    public static void Launch(AppSettings s, IPlatform p)
    {
        settings = s; platform = p;
        queue = new GrowthQueue(AppPaths.EventsFile);
        s.LaunchCount += 1;
        Track("app_launched", new()
        {
            ["version"] = ReplyFiveInfo.Version, ["os"] = p.OsName, ["os_version"] = p.OsVersion, ["launch_count"] = s.LaunchCount,
            ["days_since_install"] = s.DaysSinceInstall, ["trusted"] = p.AccessibilityTrusted, ["registered"] = s.IsRegistered,
            ["launch_at_login"] = p.SupportsLaunchAtLogin && p.LaunchAtLogin, ["ui_language"] = s.UiLanguage,
        });
        DailyActive();
        if (!p.AccessibilityTrusted) WatchAccessibility();
        flushTimer = new Timer(_ => _ = Flush(), null, 60_000, 60_000);
        _ = Flush();
    }

    /// <summary>その日最初の利用を 1 回だけ記録する（維持率の「活性端末」の元）。</summary>
    public static void DailyActive()
    {
        if (settings is null || platform is null) return;
        var today = DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (settings.DailyActiveDay == today) return;
        settings.DailyActiveDay = today;
        Track("daily_active", new() { ["trusted"] = platform.AccessibilityTrusted, ["registered"] = settings.IsRegistered, ["days_since_install"] = settings.DaysSinceInstall });
    }

    static void WatchAccessibility()
    {
        accessibilityTimer?.Dispose();
        accessibilityTimer = new Timer(_ =>
        {
            if (platform is null || settings is null || !platform.AccessibilityTrusted) return;
            accessibilityTimer?.Dispose(); accessibilityTimer = null;
            Track("accessibility_granted", new() { ["days_since_install"] = settings.DaysSinceInstall, ["launch_count"] = settings.LaunchCount });
            _ = Flush();
        }, null, 2000, 2000);
    }

    public static void Terminate()
    {
        Track("app_terminated", new() { ["session_minutes"] = Math.Round((DateTimeOffset.UtcNow - launchedAt).TotalMinutes) });
        try { Flush().Wait(1500); } catch (Exception) { }
    }

    public static async Task Flush()
    {
        if (settings is null || queue is null) return;
        var client = settings.MakeClient();
        var id = settings.InstallId;
        await queue.Flush(batch => client.Events(new EventsRequest(id, batch))).ConfigureAwait(false);
    }
}
