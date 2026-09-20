using System.Diagnostics;
using ReplyFive.Core;
using ReplyFive.Desktop.Platform.Shared;
using ReplyFive.Desktop.Services;

namespace ReplyFive.Desktop.Platform.Mac;

/// <summary>開発確認用のスタブ（製品版の macOS クライアントは clients/macos）。この Mac で画面・サーバ連携・状態遷移を試すためだけに使う。
/// 会話はクリップボードから、差し込みはコピーまで。ショートカットは登録しない（トレイ／メニューから開く）。</summary>
public sealed class MacDevPlatform : IPlatform
{
    public string OsName => "macos";
    public string OsVersion => Environment.OSVersion.VersionString;
    public string DefaultDeviceName => "Mac (dev)";
    public string SuggestedUserName => Environment.UserName;
    public bool NeedsAccessibilitySetup => false;
    public bool AccessibilityTrusted => true;
    public void RequestAccessibility() { }
    public bool ScreenTextAvailable => false;
    public bool ScreenTextAllowed => false;
    public void RequestScreenText() { }
    public ISecretStore Secrets { get; } = new FileSecretStore(Path.Combine(AppPaths.DataDir, "secrets.dev.json"));
    public IUpdateInstaller Updates { get; } = new NoUpdateInstaller();
    public bool SupportsLaunchAtLogin => false;
    public bool LaunchAtLogin { get; set; }

    public TargetApp? FrontmostApp()
    {
        var name = Run("/usr/bin/osascript", "-e", "tell application \"System Events\" to get name of first application process whose frontmost is true")?.Trim();
        return string.IsNullOrEmpty(name) || name == "ReplyFive" ? null : new TargetApp(0, 0, name, null);
    }

    public (int x, int y)? CursorPosition() => null;

    public void Activate(TargetApp app) { if (app.Name is not null) Run("/usr/bin/osascript", "-e", $"tell application \"{app.Name}\" to activate"); }

    public CapturedContext Capture(TargetApp? app, bool includeContext, string? ownName, double budgetSeconds, bool allowScreenText)
    {
        var text = includeContext ? (ReadClipboard() ?? "").Trim() : "";
        if (text.Length > 5000) text = text[^5000..];
        var platform = PlatformDetector.Detect(app?.Name, text);
        return new CapturedContext(app, null, app?.Name, null, text, platform, text.Length == 0 ? ContextSource.None : ContextSource.Clipboard, text.Length == 0 ? CaptureError.NoSelection : null,
            text.Length == 0 ? null : ContactDetector.LastSender(text, ownName));
    }

    public int? ScreenSignature(TargetApp app) => null;

    public Task<bool> Insert(string text, CapturedContext context) { CopyToClipboard(text); return Task.FromResult(false); }

    public void CopyToClipboard(string text)
    {
        var p = Process.Start(new ProcessStartInfo("/usr/bin/pbcopy") { RedirectStandardInput = true, UseShellExecute = false });
        if (p is null) return;
        p.StandardInput.Write(text); p.StandardInput.Close(); p.WaitForExit(2000);
    }

    public string? ReadClipboard() => Run("/usr/bin/pbpaste");

    public IHotKeyRegistration? RegisterHotKey(HotKeyCombo combo, Action handler) => null;
    public void RegisterUrlScheme() { }
    public void OpenUrl(string url) => Process.Start(new ProcessStartInfo("/usr/bin/open", url) { UseShellExecute = false });
    public void OpenFolder(string path) => Process.Start(new ProcessStartInfo("/usr/bin/open", path) { UseShellExecute = false });
    public string? ManagedServerUrl() => null;

    static string? Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var out_ = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return out_;
        }
        catch (Exception) { return null; }
    }
}

public sealed class NoUpdateInstaller : IUpdateInstaller
{
    public bool CanSelfInstall => false;
    public Task<string> Prepare(string version, Uri url, string currentVersion, Action<double> progress, CancellationToken ct) => throw new NotSupportedException();
    public void InstallAndRelaunch(string staged) { }
    public void CleanupStaging() { }
}
