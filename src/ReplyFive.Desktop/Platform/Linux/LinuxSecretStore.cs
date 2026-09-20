using System.Runtime.InteropServices;
using ReplyFive.Core;
using ReplyFive.Desktop.Platform.Shared;

namespace ReplyFive.Desktop.Platform.Linux;

/// <summary>端末トークンと記録の鍵。libsecret（Secret Service：GNOME Keyring / KWallet）があればそこへ、無ければ所有者だけが読めるファイル。</summary>
public sealed class LinuxSecretStore : ISecretStore
{
    readonly FileSecretStore fallback;
    readonly bool useLibsecret;
    const string Schema = "app.replyfive.desktop";

    public LinuxSecretStore(string fallbackPath)
    {
        fallback = new FileSecretStore(fallbackPath);
        useLibsecret = Probe();
    }

    /// <summary>libsecret が読み込めるか。スキーマの生成と解放だけを行い、NULL スキーマでの呼び出し（libsecret が CRITICAL を出す）はしない。</summary>
    static bool Probe()
    {
        try { var s = MakeSchema(); if (s != IntPtr.Zero) secret_schema_unref(s); return s != IntPtr.Zero; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch (Exception) { return false; }
    }

    /// <summary>Secret Service の同期呼び出しは、鍵束の解錠や既定コレクション作成のプロンプトが出せない環境（プロンプタ無し・ヘッドレス）で戻ってこないことがある。
    /// UI スレッドを止めないよう別スレッドで呼び、時間内に戻らなければこのセッションは libsecret を使わずファイルへ退避する。</summary>
    const int TimeoutMs = 4000;
    volatile bool libsecretHung;

    T? WithTimeout<T>(Func<T> call, string what)
    {
        if (libsecretHung) return default;
        var task = Task.Run(call);
        if (task.Wait(TimeoutMs)) return task.Result;
        libsecretHung = true;
        Services.Diag.Log($"libsecret {what} timed out after {TimeoutMs} ms; using file store for this session");
        return default;
    }

    public string? Get(string name)
    {
        if (useLibsecret && !libsecretHung)
        {
            var found = WithTimeout(() =>
            {
                var schema = MakeSchema();
                try
                {
                    var ptr = secret_password_lookup_sync(schema, IntPtr.Zero, out var err, "name", name, IntPtr.Zero);
                    if (err == IntPtr.Zero && ptr != IntPtr.Zero) { var s = Marshal.PtrToStringUTF8(ptr); secret_password_free(ptr); return s; }
                }
                catch (Exception) { }
                finally { secret_schema_unref(schema); }
                return null;
            }, "lookup");
            if (found is not null) return found;
        }
        return fallback.Get(name);
    }

    public void Set(string name, string? value)
    {
        if (useLibsecret && !libsecretHung)
        {
            var stored = WithTimeout(() =>
            {
                var schema = MakeSchema();
                try
                {
                    if (value is null) { secret_password_clear_sync(schema, IntPtr.Zero, out _, "name", name, IntPtr.Zero); return false; }
                    var ok = secret_password_store_sync(schema, "default", "ReplyFive " + name, value, IntPtr.Zero, out var err, "name", name, IntPtr.Zero);
                    return ok && err == IntPtr.Zero;
                }
                catch (Exception) { return false; }
                finally { secret_schema_unref(schema); }
            }, "store");
            if (stored) { fallback.Set(name, null); return; }
        }
        fallback.Set(name, value);
    }

    public byte[] LearningKey()
    {
        var b64 = Get("learning-key");
        if (b64 is not null) { try { var k = Convert.FromBase64String(b64); if (k.Length == SealedBox.KeyBytes) return k; } catch (FormatException) { } }
        var key = SealedBox.NewKey();
        Set("learning-key", Convert.ToBase64String(key));
        return key;
    }

    static IntPtr MakeSchema() => secret_schema_new(Schema, 0, "name", 0 /* SECRET_SCHEMA_ATTRIBUTE_STRING */, IntPtr.Zero);

    const string Lib = "libsecret-1.so.0";
    [DllImport(Lib)] static extern IntPtr secret_schema_new(string name, int flags, string attr1, int type1, IntPtr terminator);
    [DllImport(Lib)] static extern void secret_schema_unref(IntPtr schema);
    [DllImport(Lib)] static extern bool secret_password_store_sync(IntPtr schema, string collection, string label, string password, IntPtr cancellable, out IntPtr error, string attr1, string value1, IntPtr terminator);
    [DllImport(Lib)] static extern IntPtr secret_password_lookup_sync(IntPtr schema, IntPtr cancellable, out IntPtr error, string attr1, string value1, IntPtr terminator);
    [DllImport(Lib)] static extern bool secret_password_clear_sync(IntPtr schema, IntPtr cancellable, out IntPtr error, string attr1, string value1, IntPtr terminator);
    [DllImport(Lib)] static extern void secret_password_free(IntPtr password);
}

/// <summary>Linux の更新：パッケージ管理（.deb / Flatpak）やストア配布では自分を差し替えない。AppImage だけ、同じパスへ新しいファイルを置いて再起動する。</summary>
public sealed class LinuxUpdateInstaller : IUpdateInstaller
{
    static string? AppImagePath => Environment.GetEnvironmentVariable("APPIMAGE");
    public bool CanSelfInstall => AppImagePath is { Length: > 0 } p && File.Exists(p) && new FileInfo(p).Directory is { } d && IsWritable(d.FullName);

    static bool IsWritable(string dir)
    {
        try { var probe = Path.Combine(dir, ".replyfive-write-test-" + Environment.ProcessId); File.WriteAllText(probe, ""); File.Delete(probe); return true; } catch (Exception) { return false; }
    }

    public async Task<string> Prepare(string version, Uri url, string currentVersion, Action<double> progress, CancellationToken ct)
    {
        var dir = Path.Combine(Services.AppPaths.UpdatesDir, version);
        CleanupStaging();
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "ReplyFive.AppImage");
        await UpdateDownload.Download(url, file, progress, ct);
        // 配布物の同一性：サーバの download_url は TLS で配られ、S3 の版付きファイルを指す。実行ビットを付ける
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var reported = LinuxPlatform.Run(file, "--version")?.Trim();
        if (reported is null || !ReplyFiveInfo.IsNewerVersion(reported, currentVersion)) throw new Services.UpdateException(Services.L10n.L("update.error.not_newer"));
        return file;
    }

    public void InstallAndRelaunch(string staged)
    {
        var target = AppImagePath ?? throw new Services.UpdateException(Services.L10n.L("update.error.not_writable"));
        var incoming = target + ".new";
        File.Copy(staged, incoming, overwrite: true);
        File.SetUnixFileMode(incoming, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.Move(incoming, target, overwrite: true);
        CleanupStaging();
        var pid = Environment.ProcessId;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sh", ["-c", $"while kill -0 {pid} 2>/dev/null; do sleep 0.2; done; exec \"{target}\""]) { UseShellExecute = false });
        Environment.Exit(0);
    }

    public void CleanupStaging() { try { if (Directory.Exists(Services.AppPaths.UpdatesDir)) Directory.Delete(Services.AppPaths.UpdatesDir, true); } catch (Exception) { } }
}

/// <summary>更新ファイルの取得（共通）。</summary>
public static class UpdateDownload
{
    public static async Task Download(Uri url, string file, Action<double> progress, CancellationToken ct)
    {
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 3 }) { Timeout = TimeSpan.FromMinutes(10) };
        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new Services.UpdateException(Services.L10n.L("update.error.download"));
        var expected = res.Content.Headers.ContentLength ?? -1;
        await using var input = await res.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(file);
        var buffer = new byte[1 << 20];
        long received = 0; var last = 0.0;
        int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            received += n;
            if (expected > 0) { var p = (double)received / expected; if (p - last >= 0.02) { last = p; progress(p); } }
        }
        if (received == 0) throw new Services.UpdateException(Services.L10n.L("update.error.download"));
        progress(1);
    }
}
