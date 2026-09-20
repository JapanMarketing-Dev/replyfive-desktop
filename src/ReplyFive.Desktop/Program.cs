using Avalonia;
using ReplyFive.Desktop.Services;

namespace ReplyFive.Desktop;

static class Program
{
    /// <summary>replyfive://connect?… で 2 つ目のプロセスとして起動されたら、動いているインスタンスへ URL を渡して終了する（単一インスタンス）。</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--version")) { Console.WriteLine(ReplyFive.Core.ReplyFiveInfo.Version); return 0; }
        // 開発・検証用：動いているインスタンスへ操作を送る（--send cmd:toggle など）。同じユーザーのソケット／パイプだけが届く
        var sendIndex = Array.IndexOf(args, "--send");
        if (sendIndex >= 0 && sendIndex + 1 < args.Length) return SingleInstance.TryForward(args[sendIndex + 1]) ? 0 : 1;
        var url = args.FirstOrDefault(a => a.StartsWith("replyfive://", StringComparison.OrdinalIgnoreCase));
        if (SingleInstance.TryForward(url)) return 0;
        using var instance = SingleInstance.Acquire();
        if (instance is null)
        {
            // 取得競合：わずかな時間差で別プロセスが先に起動した
            SingleInstance.TryForward(url);
            return 0;
        }
        App.PendingUrl = url;
        App.Instance = instance;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect();
        // Linux：Wayland コンポジタがあれば Wayland ネイティブ、無ければ X11（Avalonia.Wayland 12.1）。他 OS では何もしない。
        // REPLYFIVE_FORCE_WAYLAND=1 は検証用で、Wayland を強制して失敗理由を例外で出す
        if (OperatingSystem.IsLinux())
            builder = Environment.GetEnvironmentVariable("REPLYFIVE_FORCE_WAYLAND") == "1" ? builder.UseWayland() : builder.UseWaylandWithFallback();
        return builder
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
    }
}
