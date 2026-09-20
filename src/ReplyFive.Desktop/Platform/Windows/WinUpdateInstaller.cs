using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using ReplyFive.Core;
using ReplyFive.Desktop.Platform.Linux;
using ReplyFive.Desktop.Services;

namespace ReplyFive.Desktop.Platform.Windows;

/// <summary>付録CD：アプリ内アップデート。インストーラ（Inno Setup）を取得して Authenticode の署名と版を確かめ、
/// 利用者の「更新して再起動」か、操作の無いときに静かに入れ替える。
/// MSIX（ストア）配布では OS が更新するので自分では差し替えない。</summary>
internal sealed class WinUpdateInstaller : IUpdateInstaller
{
    const string SignerName = "JapanMarketing";
    const string FileName = "ReplyFive-Setup.exe";

    static string InstallDir => Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";

    public bool CanSelfInstall => !Native.IsPackaged() && InstallDir.Length > 0 && IsWritable(InstallDir);

    static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".replyfive-write-test-" + Environment.ProcessId);
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception) { return false; }
    }

    public async Task<string> Prepare(string version, Uri url, string currentVersion, Action<double> progress, CancellationToken ct)
    {
        var dir = Path.Combine(AppPaths.UpdatesDir, version);
        CleanupStaging();
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, FileName);
        await UpdateDownload.Download(url, file, progress, ct);

        // 1) Authenticode として有効か（連鎖・改ざんの検証は OS に任せる）
        if (!Native.VerifyAuthenticode(file))
        {
            Diag.Log("update signature invalid");
            throw new UpdateException(L10n.L("update.error.signature"));
        }
        // 2) 署名者が自社か（有効な署名でも他社のものは受け付けない）
        if (!SignedByVendor(file))
        {
            Diag.Log("update signer mismatch");
            throw new UpdateException(L10n.L("update.error.signature"));
        }
        // 3) 今より新しいか
        var reported = FileVersion(file);
        if (reported is null || !ReplyFiveInfo.IsNewerVersion(reported, currentVersion))
        {
            Diag.Log("update not newer reported=" + (reported ?? "nil"));
            throw new UpdateException(L10n.L("update.error.not_newer"));
        }
        return file;
    }

    static bool SignedByVendor(string file)
    {
        try
        {
            // 署名済み実行ファイルから署名者の証明書を取り出す唯一の API。差し替えとなる新しい API が無いので抑止する
            // （連鎖と改ざんの検証は WinVerifyTrust 側で済んでいる。ここは「誰の署名か」だけを見る）
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
            return cert.Subject.Contains(SignerName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) { Diag.Log("update signer read failed " + e.GetType().Name); return false; }
    }

    static string? FileVersion(string file)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(file);
            var v = info.ProductVersion ?? info.FileVersion;
            if (string.IsNullOrWhiteSpace(v)) return null;
            // 「0.8.1.0 (build …)」のような表記から先頭の版だけを取る
            return v.Trim().Split(' ', '+')[0];
        }
        catch (Exception) { return null; }
    }

    public void InstallAndRelaunch(string staged)
    {
        if (!File.Exists(staged)) throw new UpdateException(L10n.L("update.error.install", "missing"));
        // Inno Setup の無人インストール。アプリの再起動はインストーラ側の script が行う
        var psi = new ProcessStartInfo(staged) { UseShellExecute = false };
        psi.ArgumentList.Add("/VERYSILENT");
        psi.ArgumentList.Add("/SUPPRESSMSGBOXES");
        psi.ArgumentList.Add("/NORESTART");
        psi.ArgumentList.Add("/CLOSEAPPLICATIONS");
        Process.Start(psi);
        Environment.Exit(0);
    }

    public void CleanupStaging()
    {
        try { if (Directory.Exists(AppPaths.UpdatesDir)) Directory.Delete(AppPaths.UpdatesDir, true); }
        catch (Exception) { }
    }
}
