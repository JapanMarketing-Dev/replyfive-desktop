using System.Reflection;
using ReplyFive.Core;
using Sentry;

namespace ReplyFive.Desktop.Services;

/// <summary>クラッシュ・エラー報告（Sentry プロジェクト replyfive-desktop、os タグで区別）。DSN はビルド時に埋め込む。無ければ無効。
/// 会話文脈・入力・生成文・修正例を送らない：breadcrumb data と extra を落とし、自動 breadcrumb を止める。</summary>
public static class Crash
{
    static bool enabled;

    public static void Start(string osName)
    {
        var dsn = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "SentryDsn")?.Value;
        if (string.IsNullOrEmpty(dsn)) return;
        SentrySdk.Init(o =>
        {
            o.Dsn = dsn;
            o.Release = "replyfive@" + ReplyFiveInfo.Version;
#if DEBUG
            o.Environment = "development";
#else
            o.Environment = "production";
#endif
            o.SendDefaultPii = false;
            o.MaxBreadcrumbs = 0;
            o.TracesSampleRate = 0;
            o.AutoSessionTracking = false;
            o.SetBeforeSend((e, _) =>
            {
                // 本文を含む可能性のある付随情報は落とす（自分で渡すメッセージとスタックトレースだけ送る）
                e.User = new SentryUser();
                e.Request = new SentryRequest();
                return e;
            });
        });
        SentrySdk.ConfigureScope(s => s.SetTag("os", osName));
        enabled = true;
    }

    /// <summary>本文を含まないエラーだけ渡す（API のエラーコードなど）。</summary>
    public static void Record(string message)
    {
        if (!enabled) return;
        SentrySdk.CaptureMessage(message);
    }

    public static void Exception(Exception e)
    {
        Diag.Log("exception " + e.GetType().Name + ": " + e.Message);
        if (enabled) SentrySdk.CaptureException(e);
    }
}
