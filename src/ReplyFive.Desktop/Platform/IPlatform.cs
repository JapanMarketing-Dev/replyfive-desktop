using System.Security.Cryptography;
using System.Text;
using ReplyFive.Core;
using ReplyFive.Desktop.Services;

namespace ReplyFive.Desktop.Platform;

/// <summary>前面にある会話アプリ。中身（本文）は持たず、識別に必要なものだけ。Native は OS 側の実体（HWND・AT-SPI の参照など）。</summary>
public sealed record TargetApp(long Handle, int Pid, string? Name, string? WindowTitle, object? Native = null)
{
    public bool SameAs(TargetApp? other) => other is not null && (Pid == other.Pid && Pid != 0 || Handle == other.Handle && Handle != 0);
}

public enum CaptureError { NotTrusted, NoSelection, Failed, ScreenNotAllowed }

/// <summary>ショートカット押下時に 1 回だけ読んだ会話。画面の常時監視はしない。</summary>
public sealed class CapturedContext
{
    public TargetApp? App { get; }
    /// <summary>ショートカット時にカーソルがあった入力欄。ここへ結果を直接差し込む。</summary>
    public object? FocusedElement { get; }
    public string? AppName { get; }
    public string? WindowTitle { get; }
    /// <summary>付録BE：入力欄直上のメッセージの送信者（返信相手）。推定できなければ null。</summary>
    public string? ContactName { get; }
    public string? ContactKey { get; }
    public string Text { get; }
    public Core.Platform Platform { get; }
    public ContextSource Source { get; }
    public CaptureError? Error { get; }

    public CapturedContext(TargetApp? app, object? focusedElement, string? appName, string? windowTitle, string text, Core.Platform platform, ContextSource source, CaptureError? error, string? contactName = null)
    {
        App = app; FocusedElement = focusedElement; AppName = appName; WindowTitle = windowTitle; ContactName = contactName;
        ContactKey = MakeContactKey(platform, contactName, windowTitle);
        Text = text; Platform = platform; Source = source; Error = error;
    }

    public static CapturedContext Empty(TargetApp? app, CaptureError? error)
        => new(app, null, app?.Name, null, "", PlatformDetector.Detect(app?.Name), ContextSource.None, error);

    /// <summary>返信の記録の相手キー。相手名が分かればそれを、無ければウインドウタイトルを使う（どちらもハッシュだけ保存）。</summary>
    static string? MakeContactKey(Core.Platform platform, string? contactName, string? windowTitle)
    {
        string seed;
        if (!string.IsNullOrEmpty(contactName)) seed = "contact:" + contactName;
        else if (!string.IsNullOrEmpty(windowTitle)) seed = windowTitle;
        else return null;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(platform.Wire() + "|" + seed));
        return Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant();
    }
}

public interface IHotKeyRegistration : IDisposable
{
    bool Registered { get; }
}

/// <summary>端末トークンと記録ファイルの鍵。Windows は DPAPI、Linux は Secret Service（無ければ所有者だけが読めるファイル）。</summary>
public interface ISecretStore
{
    string? Get(string name);
    void Set(string name, string? value);
    /// <summary>記録ファイルの暗号鍵（32 バイト）。無ければ生成して保存する。</summary>
    byte[] LearningKey();
}

/// <summary>付録CD：アプリ内アップデートの OS 固有部分。</summary>
public interface IUpdateInstaller
{
    /// <summary>この配布形態で自分を差し替えられるか（ストア配布・パッケージ管理下では false）。</summary>
    bool CanSelfInstall { get; }
    /// <summary>取得して検証し、端末内に置く。戻り値は準備済みの実体のパス。</summary>
    Task<string> Prepare(string version, Uri url, string currentVersion, Action<double> progress, CancellationToken ct);
    /// <summary>準備済みの版へ入れ替えて再起動する。呼び出し側はこのあと終了する。</summary>
    void InstallAndRelaunch(string staged);
    void CleanupStaging();
}

/// <summary>OS 固有の層。Windows / Linux（と開発確認用の macOS スタブ）が実装する。</summary>
public interface IPlatform
{
    string OsName { get; }          // windows | linux | macos
    string OsVersion { get; }
    string DefaultDeviceName { get; }
    string SuggestedUserName { get; }
    /// <summary>付録CF-9：この端末に入っているアプリと起動中のアプリの表示名（中身・履歴は読まない）。初回設定の自動選択に使う。</summary>
    IReadOnlyList<string> InstalledAppNames() => [];

    /// <summary>会話の読み取り・差し込みに OS の許可や設定が要るか（Linux の AT-SPI 有効化）。Windows は不要。</summary>
    bool NeedsAccessibilitySetup { get; }
    bool AccessibilityTrusted { get; }
    void RequestAccessibility();

    /// <summary>画面の文字認識（付録BP）を備えているか。</summary>
    bool ScreenTextAvailable { get; }
    bool ScreenTextAllowed { get; }
    void RequestScreenText();

    /// <summary>前面のアプリ（自分自身を除く）。</summary>
    TargetApp? FrontmostApp();
    /// <summary>マウスの位置（物理ピクセル）。パネルを出す画面を決めるためだけに使う。分からなければ null。</summary>
    (int x, int y)? CursorPosition();
    /// <summary>対象アプリを前面に戻す。</summary>
    void Activate(TargetApp app);

    /// <summary>1 回だけ読む。budget はウインドウ走査の上限秒。allowScreenText が false なら文字認識をしない。</summary>
    CapturedContext Capture(TargetApp? app, bool includeContext, string? ownName, double budgetSeconds, bool allowScreenText);
    /// <summary>付録BU：文字認識に頼るアプリの変化検知用の署名。無ければ null。</summary>
    int? ScreenSignature(TargetApp app);

    /// <summary>取得した入力欄へ差し込む。直接書けなければ対象アプリを前面にしてクリップボード経由（Ctrl+V）。送信キーは送らない。</summary>
    Task<bool> Insert(string text, CapturedContext context);

    void CopyToClipboard(string text);
    string? ReadClipboard();

    IHotKeyRegistration? RegisterHotKey(HotKeyCombo combo, Action handler);

    ISecretStore Secrets { get; }
    IUpdateInstaller Updates { get; }

    /// <summary>replyfive:// の URL スキームを自分に向ける（起動時）。</summary>
    void RegisterUrlScheme();
    bool LaunchAtLogin { get; set; }
    bool SupportsLaunchAtLogin { get; }

    void OpenUrl(string url);
    void OpenFolder(string path);

    /// <summary>管理配布の接続先（サーバ URL だけ）。無ければ null。</summary>
    string? ManagedServerUrl();
}
