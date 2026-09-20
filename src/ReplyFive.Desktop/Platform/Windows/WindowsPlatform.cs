using System.Diagnostics;
using System.Drawing;
using Microsoft.Win32;
using ReplyFive.Desktop.Services;

namespace ReplyFive.Desktop.Platform.Windows;

/// <summary>Windows の OS 固有層。会話の読み取りと差し込みは UI Automation（許可は不要）、ショートカットは RegisterHotKey、
/// 鍵は DPAPI、画面の文字認識は Windows の OCR。会話アプリが前面のときだけ、会話のペインだけを読む（付録BQ・BR・BU）。</summary>
public sealed class WindowsPlatform : IPlatform
{
    /// <summary>付録CF-9：スタートメニューのショートカット名、Uninstall の DisplayName、起動中プロセスの名前（中身は読まない）。</summary>
    public IReadOnlyList<string> InstalledAppNames()
    {
        var names = new List<string>();
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) })
        {
            try { if (Directory.Exists(root)) names.AddRange(Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).Select(Path.GetFileNameWithoutExtension).OfType<string>()); } catch (Exception) { }
        }
        foreach (var (hive, path) in new[] { (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"), (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"), (Microsoft.Win32.Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall") })
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key is null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    try { using var k = key.OpenSubKey(sub); if (k?.GetValue("DisplayName") is string n && n.Length > 0) names.Add(n); } catch (Exception) { }
                }
            }
            catch (Exception) { }
        }
        try { names.AddRange(System.Diagnostics.Process.GetProcesses().Select(p => p.ProcessName)); } catch (Exception) { }
        return names;
    }

    public string OsName => "windows";
    public string OsVersion { get; } = DescribeOs();
    public string DefaultDeviceName => Environment.MachineName is { Length: > 0 } m ? m : "Windows";
    public string SuggestedUserName { get; } = Native.DisplayUserName();

    /// <summary>UI Automation は OS の許可設定が要らない（macOS の Accessibility、Linux の AT-SPI と違う）。</summary>
    public bool NeedsAccessibilitySetup => false;
    public bool AccessibilityTrusted => true;
    public void RequestAccessibility() { }

    /// <summary>画面の取り込みにも許可は要らない。文字認識の言語が入っていれば使える。</summary>
    public bool ScreenTextAvailable => WinOcr.Available;
    public bool ScreenTextAllowed => true;
    public void RequestScreenText() { }

    public ISecretStore Secrets { get; } = new WinSecretStore(Path.Combine(AppPaths.DataDir, "secrets.dat"));
    public IUpdateInstaller Updates { get; } = new WinUpdateInstaller();

    static string DescribeOs()
    {
        var v = Environment.OSVersion.Version;
        // Windows 11 は build 22000 以降も major 10 のまま
        var name = v.Build >= 22000 ? "Windows 11" : "Windows 10";
        return $"{name} ({v.Major}.{v.Minor}.{v.Build})";
    }

    // MARK: - 前面のアプリ

    TargetApp? lastExternal;
    readonly object frontGate = new();

    public TargetApp? FrontmostApp()
    {
        var hwnd = Native.GetForegroundWindow();
        // 前面が一瞬取れないことがある（ウインドウの切り替わり）。そのときだけ直前の対象を返す
        if (hwnd == IntPtr.Zero) return Last();
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return Last();
        // 付録BR：自分（パネル・設定）が前面のときは会話アプリではないので読まない。
        // ショートカットの取得は App 側が直前の対象（lastExternalApp）を覚えているので、ここで代わりを返す必要はない
        if ((int)pid == Environment.ProcessId) return null;
        var app = new TargetApp(hwnd.ToInt64(), (int)pid, FriendlyName((int)pid), Native.WindowText(hwnd));
        lock (frontGate) lastExternal = app;
        return app;
    }

    TargetApp? Last() { lock (frontGate) return lastExternal is { } a && Native.IsWindow(new IntPtr(a.Handle)) ? a : null; }

    static readonly Dictionary<int, (string? Name, DateTime At)> nameCache = [];
    static readonly object nameGate = new();

    /// <summary>利用者に見える名前（「Slack」「Microsoft Teams」「Google Chrome」）。実行ファイルの説明が取れなければプロセス名。</summary>
    internal static string? FriendlyName(int pid)
    {
        lock (nameGate)
        {
            if (nameCache.TryGetValue(pid, out var hit) && (DateTime.UtcNow - hit.At).TotalSeconds < 30) return hit.Name;
        }
        string? name = null;
        var path = Native.ProcessImagePath(pid);
        if (path is not null)
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                if (!string.IsNullOrEmpty(description)) name = description;
            }
            catch (Exception) { }
            name ??= Path.GetFileNameWithoutExtension(path);
        }
        if (string.IsNullOrEmpty(name))
        {
            try { name = Process.GetProcessById(pid).ProcessName; } catch (Exception) { name = null; }
        }
        lock (nameGate)
        {
            if (nameCache.Count > 64) nameCache.Clear();
            nameCache[pid] = (name, DateTime.UtcNow);
        }
        return name;
    }

    /// <summary>プロセスの主ウインドウ（取り込んだ HWND が閉じていたときの引き当て）。</summary>
    internal static IntPtr MainWindowOf(int pid)
    {
        var best = IntPtr.Zero;
        var bestArea = 0L;
        try
        {
            Native.EnumWindows((hwnd, _) =>
            {
                Native.GetWindowThreadProcessId(hwnd, out var owner);
                if ((int)owner != pid || !Native.IsWindowVisible(hwnd)) return true;
                var r = Native.WindowRect(hwnd);
                var area = (long)r.Width * r.Height;
                if (area > bestArea) { bestArea = area; best = hwnd; }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception) { }
        return best;
    }

    public (int x, int y)? CursorPosition() => Native.GetCursorPos(out var p) ? (p.X, p.Y) : null;

    /// <summary>対象アプリを前面に戻す。背景のプロセスからの SetForegroundWindow は OS に弾かれるので、
    /// 入力キューを一時的に繋いでから呼ぶ。</summary>
    public void Activate(TargetApp app)
    {
        var hwnd = new IntPtr(app.Handle);
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) hwnd = MainWindowOf(app.Pid);
        if (hwnd == IntPtr.Zero) return;
        try
        {
            if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
            Native.AllowSetForegroundWindow(Native.ASFW_ANY);
            var front = Native.GetForegroundWindow();
            var frontThread = front != IntPtr.Zero ? Native.GetWindowThreadProcessId(front, out _) : 0;
            var myThread = Native.GetCurrentThreadId();
            var attached = frontThread != 0 && frontThread != myThread && Native.AttachThreadInput(myThread, frontThread, true);
            try
            {
                Native.SetForegroundWindow(hwnd);
                Native.BringWindowToTop(hwnd);
            }
            finally { if (attached) Native.AttachThreadInput(myThread, frontThread, false); }
        }
        catch (Exception e) { Diag.Log("activate failed " + e.GetType().Name); }
    }

    // MARK: - 取得・差し込み

    public CapturedContext Capture(TargetApp? app, bool includeContext, string? ownName, double budgetSeconds, bool allowScreenText)
    {
        // 取得の失敗でアプリを止めない。読めなければ理由を付けて空で返す（パネルは必ず開く）
        try { return WinAccessibility.Capture(app, includeContext, ownName, budgetSeconds, allowScreenText); }
        catch (Exception e)
        {
            Crash.Exception(e);
            return CapturedContext.Empty(app, CaptureError.Failed);
        }
    }

    public int? ScreenSignature(TargetApp app)
    {
        try
        {
            var hwnd = new IntPtr(app.Handle);
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) hwnd = MainWindowOf(app.Pid);
            return hwnd == IntPtr.Zero ? null : WinOcr.Signature(hwnd, WinAccessibility.RememberedRegion(app.Pid));
        }
        catch (Exception) { return null; }
    }

    public Task<bool> Insert(string text, CapturedContext context) => WinInsert.Insert(this, text, context);

    // MARK: - クリップボード

    public void CopyToClipboard(string text) => Native.ClipboardWrite(text);
    public string? ReadClipboard() => Native.ClipboardRead();

    // MARK: - ショートカット

    public IHotKeyRegistration? RegisterHotKey(HotKeyCombo combo, Action handler)
    {
        if (WinHotKey.VirtualKey(combo.Key) == 0) return null;
        return new WinHotKey(combo, handler);
    }

    // MARK: - URL スキーム・自動起動・外部

    static string ExecutablePath => Environment.ProcessPath ?? "ReplyFive.exe";
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "ReplyFive";

    /// <summary>replyfive:// を自分に向ける。MSIX ではパッケージのマニフェストが担うので触らない。</summary>
    public void RegisterUrlScheme()
    {
        if (Native.IsPackaged()) return;
        try
        {
            var exe = ExecutablePath;
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\replyfive");
            if (key is null) return;
            key.SetValue("", "URL:ReplyFive Protocol");
            key.SetValue("URL Protocol", "");
            using (var icon = key.CreateSubKey("DefaultIcon")) icon?.SetValue("", "\"" + exe + "\",0");
            using var command = key.CreateSubKey(@"shell\open\command");
            command?.SetValue("", "\"" + exe + "\" \"%1\"");
        }
        catch (Exception e) { Diag.Log("url scheme registration failed " + e.GetType().Name); }
    }

    /// <summary>MSIX ではスタートアップは OS の設定（StartupTask）が持つので、ここでは何もしない。</summary>
    public bool SupportsLaunchAtLogin => !Native.IsPackaged();

    public bool LaunchAtLogin
    {
        get
        {
            if (Native.IsPackaged()) return false;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(RunValueName) is string s && s.Length > 0;
            }
            catch (Exception) { return false; }
        }
        set
        {
            if (Native.IsPackaged()) return;
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                if (key is null) return;
                if (value) key.SetValue(RunValueName, "\"" + ExecutablePath + "\"");
                else key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
            catch (Exception e) { Diag.Log("launch at login failed " + e.GetType().Name); }
        }
    }

    public void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { Diag.Log("open url failed " + e.GetType().Name); }
    }

    public void OpenFolder(string path)
    {
        try { Directory.CreateDirectory(path); } catch (Exception) { }
        OpenUrl(path);
    }

    /// <summary>管理配布の接続先（サーバ URL だけ）。まず HKLM\Software\ReplyFive、無ければ ProgramData の config.json。</summary>
    public string? ManagedServerUrl()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"Software\ReplyFive");
            if (key?.GetValue("ServerURL") is string s && s.Trim().Length > 0) return s.Trim();
        }
        catch (Exception) { }
        try
        {
            if (!File.Exists(AppPaths.ManagedConfigFile)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(AppPaths.ManagedConfigFile));
            return doc.RootElement.TryGetProperty("server_url", out var v) ? v.GetString() : null;
        }
        catch (Exception) { return null; }
    }
}
