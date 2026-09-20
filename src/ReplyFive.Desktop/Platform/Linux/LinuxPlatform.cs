using System.Diagnostics;
using ReplyFive.Core;
using ReplyFive.Desktop.Platform.Shared;
using ReplyFive.Desktop.Services;

namespace ReplyFive.Desktop.Platform.Linux;

/// <summary>Linux（X11 / Wayland）。会話の読み取りと差し込みは AT-SPI2、ショートカットは XDG Portal（無ければ X11）、鍵は Secret Service。
/// この骨組みはクリップボード・URL スキーム・自動起動・外部コマンドを持つ。AT-SPI・ショートカットは同じフォルダの各ファイルが埋める。</summary>
public sealed partial class LinuxPlatform : IPlatform
{
    public string OsName => "linux";
    public string OsVersion { get; } = ReadOsRelease();
    public string DefaultDeviceName => Environment.MachineName is { Length: > 0 } m ? m : "Linux";
    public string SuggestedUserName { get; } = ReadFullName();
    public bool NeedsAccessibilitySetup => true;
    public bool ScreenTextAvailable => false;
    public bool ScreenTextAllowed => false;
    public void RequestScreenText() { }
    public int? ScreenSignature(TargetApp app) => null;
    public ISecretStore Secrets { get; }
    public IUpdateInstaller Updates { get; } = new LinuxUpdateInstaller();
    public bool SupportsLaunchAtLogin => true;

    public LinuxPlatform()
    {
        Secrets = new LinuxSecretStore(Path.Combine(AppPaths.DataDir, "secrets.json"));
    }

    static string ReadOsRelease()
    {
        try
        {
            foreach (var line in File.ReadLines("/etc/os-release"))
                if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal)) return line[12..].Trim('"');
        }
        catch (Exception) { }
        return Environment.OSVersion.VersionString;
    }

    static string ReadFullName()
    {
        try
        {
            foreach (var line in File.ReadLines("/etc/passwd"))
            {
                var f = line.Split(':');
                if (f.Length >= 5 && f[0] == Environment.UserName) { var gecos = f[4].Split(',')[0].Trim(); if (gecos.Length > 0) return gecos; }
            }
        }
        catch (Exception) { }
        return Environment.UserName;
    }

    // MARK: - クリップボード（wl-clipboard / xclip / xsel）

    static bool IsWayland => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    public void CopyToClipboard(string text)
    {
        if (IsWayland && Which("wl-copy") is { } wl) { RunInput(wl, text); return; }
        if (Which("xclip") is { } xclip) { RunInput(xclip, text, "-selection", "clipboard"); return; }
        if (Which("xsel") is { } xsel) { RunInput(xsel, text, "--clipboard", "--input"); return; }
        if (Which("wl-copy") is { } wl2) RunInput(wl2, text);
    }

    public string? ReadClipboard()
    {
        if (IsWayland && Which("wl-paste") is { } wl) return Run(wl, "--no-newline");
        if (Which("xclip") is { } xclip) return Run(xclip, "-selection", "clipboard", "-o");
        if (Which("xsel") is { } xsel) return Run(xsel, "--clipboard", "--output");
        if (Which("wl-paste") is { } wl2) return Run(wl2, "--no-newline");
        return null;
    }

    // MARK: - URL スキーム・自動起動・外部

    static string ExecutablePath => Environment.ProcessPath ?? "replyfive";
    static string ApplicationsDir => Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"), "applications");
    static string AutostartDir => Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "autostart");

    /// <summary>アプリ ID（付録CH）。.desktop・アイコン・Portal の app_id に使い、Snap / Flatpak / .deb のパッケージと同じ名前にする。</summary>
    public const string AppId = "app.replyfive.ReplyFive";

    /// <summary>付録CF-9：.desktop の Name=（システム・利用者・Flatpak・Snap）と、起動中プロセスの名前。</summary>
    public IReadOnlyList<string> InstalledAppNames()
    {
        var names = new List<string>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } dh ? dh : Path.Combine(home, ".local", "share");
        var dirs = new[] { "/usr/share/applications", "/usr/local/share/applications", Path.Combine(dataHome, "applications"),
            "/var/lib/flatpak/exports/share/applications", Path.Combine(dataHome, "flatpak", "exports", "share", "applications"), "/var/lib/snapd/desktop/applications" };
        foreach (var dir in dirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*.desktop"))
                {
                    try
                    {
                        foreach (var line in File.ReadLines(f))
                        {
                            if (line.StartsWith("Name=", StringComparison.Ordinal)) { names.Add(line[5..].Trim()); break; }
                            if (line.StartsWith('[') && !line.StartsWith("[Desktop Entry]", StringComparison.Ordinal)) break;
                        }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }
        try
        {
            foreach (var p in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(p), out _)) continue;
                try { names.Add(File.ReadAllText(Path.Combine(p, "comm")).Trim()); } catch (Exception) { }
            }
        }
        catch (Exception) { }
        return names;
    }
    static string DesktopFileName => AppId + ".desktop";
    static string IconsDir => Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"), "icons", "hicolor", "256x256", "apps");

    /// <summary>~/.local/share/applications/app.replyfive.ReplyFive.desktop を書き、x-scheme-handler/replyfive を自分に向ける。
    /// Flatpak / Snap はパッケージの .desktop が担うので書かない。.deb は /usr/share に同じ名前で入るが、利用者側の上書きは無害。AppImage 直接実行のためにアイコンも置く。</summary>
    public void RegisterUrlScheme()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("FLATPAK_ID") is { Length: > 0 } || Environment.GetEnvironmentVariable("SNAP") is { Length: > 0 }) return;
            Directory.CreateDirectory(ApplicationsDir);
            InstallIcon();
            var file = Path.Combine(ApplicationsDir, DesktopFileName);
            var content = string.Join("\n",
                "[Desktop Entry]", "Type=Application", "Name=ReplyFive", "Comment=Turn a short note into a reply you can send",
                $"Exec=\"{ExecutablePath}\" %u", "Icon=" + AppId, "Terminal=false", "Categories=Office;Utility;", "MimeType=x-scheme-handler/replyfive;",
                "StartupNotify=false", "X-GNOME-UsesNotifications=false", "");
            if (!File.Exists(file) || File.ReadAllText(file) != content) File.WriteAllText(file, content);
            if (Which("xdg-mime") is { } xdgMime) Run(xdgMime, "default", DesktopFileName, "x-scheme-handler/replyfive");
            if (Which("update-desktop-database") is { } udd) Run(udd, ApplicationsDir);
        }
        catch (Exception e) { Diag.Log("url scheme registration failed " + e.GetType().Name); }
    }

    /// <summary>埋め込みの 256px アイコンを ~/.local/share/icons へ置く（.desktop の Icon= が解決できるように。無ければ 1 回だけ）。</summary>
    static void InstallIcon()
    {
        try
        {
            Directory.CreateDirectory(IconsDir);
            var target = Path.Combine(IconsDir, AppId + ".png");
            if (File.Exists(target)) return;
            using var src = Avalonia.Platform.AssetLoader.Open(new Uri("avares://ReplyFive/Assets/replyfive-256.png"));
            using var dst = File.Create(target);
            src.CopyTo(dst);
        }
        catch (Exception e) { Diag.Log("icon install failed " + e.GetType().Name); }
    }

    public bool LaunchAtLogin
    {
        get => File.Exists(Path.Combine(AutostartDir, DesktopFileName));
        set
        {
            var file = Path.Combine(AutostartDir, DesktopFileName);
            if (!value) { if (File.Exists(file)) File.Delete(file); return; }
            Directory.CreateDirectory(AutostartDir);
            File.WriteAllText(file, string.Join("\n", "[Desktop Entry]", "Type=Application", "Name=ReplyFive", $"Exec=\"{ExecutablePath}\"", "Icon=" + AppId, "Terminal=false", "X-GNOME-Autostart-enabled=true", ""));
        }
    }

    public void OpenUrl(string url) { if (Which("xdg-open") is { } o) Process.Start(new ProcessStartInfo(o, url) { UseShellExecute = false }); }
    public void OpenFolder(string path) { Directory.CreateDirectory(path); OpenUrl(path); }

    public string? ManagedServerUrl()
    {
        try
        {
            if (!File.Exists(AppPaths.ManagedConfigFile)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(AppPaths.ManagedConfigFile));
            return doc.RootElement.TryGetProperty("server_url", out var v) ? v.GetString() : null;
        }
        catch (Exception) { return null; }
    }

    // MARK: - 外部コマンド

    internal static string? Which(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':'))
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    internal static string? Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var out_ = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return p.ExitCode == 0 ? out_ : null;
        }
        catch (Exception) { return null; }
    }

    internal static bool RunInput(string file, string input, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardInput = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.StandardInput.Write(input); p.StandardInput.Close();
            // wl-copy はデーモン化するので待ちすぎない
            p.WaitForExit(1500);
            return true;
        }
        catch (Exception) { return false; }
    }
}
