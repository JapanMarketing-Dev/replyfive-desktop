using CommunityToolkit.Mvvm.ComponentModel;
using ReplyFive.Core;
using ReplyFive.Desktop.Platform;
using static ReplyFive.Desktop.Services.L10n;

namespace ReplyFive.Desktop.Services;

public enum UpdateState { Idle, Checking, UpToDate, Downloading, Ready, Installing, Failed }

/// <summary>付録CD：アプリ内アップデート。サーバ（GET /v1/meta・/v1/devices/self）の最新版が今より新しければ裏で取得・検証して端末内に置く。
/// 差し替えは利用者の「更新して再起動」か、自動更新がオンで操作の無いときに行う。OS 固有の取得・検証・入れ替えは IUpdateInstaller。</summary>
public sealed partial class Updater : ObservableObject
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    [ObservableProperty] UpdateState state = UpdateState.Idle;
    [ObservableProperty] string? stateVersion;
    [ObservableProperty] double progress;
    [ObservableProperty] string? failureMessage;
    [ObservableProperty] DateTimeOffset? lastChecked;
    public Action<string>? OnReady { get; set; }

    readonly AppSettings settings;
    readonly IPlatform platform;
    CancellationTokenSource? work;
    string? stagedPath;
    string? stagedVersion;
    readonly Timer timer;

    public Updater(AppSettings settings, IPlatform platform)
    {
        this.settings = settings; this.platform = platform;
        settings.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(AppSettings.LatestClientVersion) or nameof(AppSettings.DownloadURL)) PrepareIfNeeded(); };
        // ストア版は更新をストアが配る（付録CH）。定期確認も取得もしない
        timer = new Timer(_ => _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => Check(force: true)), null, Distribution.IsStore ? Timeout.InfiniteTimeSpan : CheckInterval, CheckInterval);
    }

    public string? ReadyVersion => State == UpdateState.Ready ? StateVersion : null;
    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing;

    /// <summary>「今すぐ確認」。サーバのメタ情報を取り直し、新しければ取得まで進める。</summary>
    public async Task Check(bool force)
    {
        if (IsBusy || Distribution.IsStore) return;
        State = UpdateState.Checking;
        await settings.RefreshClientMetadata(force);
        LastChecked = DateTimeOffset.UtcNow;
        Diag.Log($"update check latest={settings.LatestClientVersion ?? "-"} available={settings.UpdateAvailable} self_install={platform.Updates.CanSelfInstall}");
        if (!settings.UpdateAvailable) { State = UpdateState.UpToDate; return; }
        PrepareIfNeeded();
        if (State == UpdateState.Checking) State = UpdateState.UpToDate;
    }

    /// <summary>新しい版が分かっていて、まだその版を準備していなければ取得・検証を始める。自分を差し替えられない配布形態（ストア）では案内だけ。</summary>
    public void PrepareIfNeeded()
    {
        if (Distribution.IsStore) return;
        if (!settings.UpdateAvailable || settings.LatestClientVersion is not { } version || settings.DownloadURL is not { } url) return;
        if (!platform.Updates.CanSelfInstall) { if (State is UpdateState.Idle or UpdateState.Checking or UpdateState.UpToDate) State = UpdateState.Idle; return; }
        if (stagedVersion == version && stagedPath is not null && State == UpdateState.Ready) return;
        if (State == UpdateState.Downloading && StateVersion == version) return;
        if (State == UpdateState.Installing) return;
        if (State == UpdateState.Failed && StateVersion == version) return; // 同じ版で失敗したら、次の確認まで待つ
        // 取得先は TLS だけ。例外はこの端末上の開発サーバ（ループバックの http。/download/<os> が本来の https の置き場へ転送する）
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !(uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback))) { Diag.Log("update prepare skipped: download url is not https"); return; }
        work?.Cancel();
        var cts = new CancellationTokenSource();
        work = cts;
        State = UpdateState.Downloading; StateVersion = version; Progress = 0;
        Diag.Log("update prepare version=" + version);
        _ = Task.Run(async () =>
        {
            try
            {
                var staged = await platform.Updates.Prepare(version, uri, ReplyFiveInfo.Version, p => _ = Ui(() => { if (State == UpdateState.Downloading && StateVersion == version) Progress = p; }), cts.Token);
                if (cts.IsCancellationRequested) return;
                await Ui(() =>
                {
                    stagedPath = staged; stagedVersion = version;
                    State = UpdateState.Ready; StateVersion = version;
                    Diag.Log("update ready version=" + version);
                    Growth.Track("update_ready", new() { ["latest_version"] = version });
                    OnReady?.Invoke(version);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                var message = e is UpdateException u ? u.Message : L("update.error.download");
                Diag.Log($"update prepare failed version={version} error={e.GetType().Name}");
                Crash.Record("update prepare failed: " + e.GetType().Name);
                platform.Updates.CleanupStaging();
                await Ui(() => { State = UpdateState.Failed; StateVersion = version; FailureMessage = message; });
            }
        });
    }

    /// <summary>準備済みの版へ差し替えて再起動する。</summary>
    public void InstallAndRelaunch()
    {
        if (stagedPath is not { } staged || stagedVersion is not { } version || State != UpdateState.Ready) return;
        State = UpdateState.Installing;
        Diag.Log("update install version=" + version);
        Growth.Track("update_installed", new() { ["latest_version"] = version });
        settings.Conversations.Flush();
        try { Growth.Flush().Wait(1000); } catch (Exception) { }
        try { platform.Updates.InstallAndRelaunch(staged); }
        catch (Exception e)
        {
            Diag.Log("update install failed " + e.GetType().Name);
            State = UpdateState.Failed; FailureMessage = e is UpdateException u ? u.Message : L("update.error.install", e.GetType().Name);
        }
    }

    public void OpenDownloadPage() { if (settings.DownloadURL is { } url) platform.OpenUrl(url); }

    static Task Ui(Action a) => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(a).GetTask();
}

/// <summary>利用者に見せる文言を持つ更新エラー。</summary>
public sealed class UpdateException : Exception
{
    public UpdateException(string message) : base(message) { }
}
