using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using ReplyFive.Core;
using ReplyFive.Desktop.Platform;
using ReplyFive.Desktop.Services;
using ReplyFive.Desktop.ViewModels;
using ReplyFive.Desktop.Views;
using static ReplyFive.Desktop.Services.L10n;

namespace ReplyFive.Desktop;

/// <summary>常駐アプリの本体（macOS 版の AppDelegate に相当）。通知領域・ショートカット・パネル・設定・初回設定・接続リンク・自動更新。</summary>
public sealed class App : Application
{
    public static string? PendingUrl { get; set; }
    public static SingleInstance? Instance { get; set; }
    public static App Current_ => (App)Current!;

    public IPlatform Platform { get; private set; } = null!;
    public AppSettings Settings { get; private set; } = null!;
    public Updater Updater { get; private set; } = null!;
    ConversationCollector collector = null!;
    PanelWindow panel = null!;
    ReplyViewModel vm = null!;
    SettingsWindow? settingsWindow;
    OnboardingWindow? onboardingWindow;
    StyleWindow? styleWindow;
    TrayIcon tray = null!;
    readonly NativeMenu trayMenu = new();
    IHotKeyRegistration? hotKey;
    DateTimeOffset lastInteraction = DateTimeOffset.UtcNow;
    DispatcherTimer? autoInstallTimer;
    bool capturing;
    TargetApp? lastExternalApp;
    IClassicDesktopStyleApplicationLifetime desktop = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        AppPaths.EnsureDirs();
        Platform = PlatformFactory.Create();
        Settings = new AppSettings(Platform);
        L10n.Apply(Settings.UiLanguage, Platform.OsName);
        Crash.Start(Platform.OsName);
        Diag.Log($"launch distribution={Distribution.Name} os={Platform.OsName} version={ReplyFiveInfo.Version} trusted={Platform.AccessibilityTrusted} registered={Settings.IsRegistered}");
        Growth.Launch(Settings, Platform);
        if (Settings.PreviousRunVersion is { } previous && previous != ReplyFiveInfo.Version)
            Growth.Track("update_applied", new() { ["from_version"] = previous, ["to_version"] = ReplyFiveInfo.Version });
        Updater = new Updater(Settings, Platform);
        vm = new ReplyViewModel(Settings, Platform);
        panel = new PanelWindow(vm, OpenSettings, OpenTrial);
        vm.OnClose = () => panel.HidePanel();
        vm.ShowToast = (text, ok) => Toast.Show(text, ok);
        collector = new ConversationCollector(Settings, Platform);
        SetupTray();
        RecreateHotKey();
        try { Platform.RegisterUrlScheme(); } catch (Exception e) { Crash.Exception(e); }
        SingleInstance.MessageReceived += m => Dispatcher.UIThread.Post(() => HandleMessage(m));
        L10n.Changed += () => { BuildMenu(); settingsWindow?.Relocalize(); onboardingWindow?.Relocalize(); };
        Settings.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(AppSettings.HotKey): RecreateHotKey(); BuildMenu(); Growth.Track("settings_changed", new() { ["key"] = "hotkey" }); break;
                case nameof(AppSettings.UiLanguage): Growth.Track("settings_changed", new() { ["key"] = "language" }); break;
                case nameof(AppSettings.IsRecordingHotKey): if (Settings.IsRecordingHotKey) { hotKey?.Dispose(); hotKey = null; } else RecreateHotKey(); break;
                case nameof(AppSettings.IsRegistered): BuildMenu(); if (Settings.IsRegistered) _ = Settings.PushDeviceProfile(); break;
                case nameof(AppSettings.OrganizationName) or nameof(AppSettings.Entitlement) or nameof(AppSettings.LatestClientVersion) or nameof(AppSettings.DownloadURL) or nameof(AppSettings.UserName): BuildMenu(); break;
            }
        };
        Updater.OnReady = version => { Toast.Show(L("update.ready.toast", version), true); BuildMenu(); ScheduleAutoInstall(); };
        Updater.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Updater.State)) BuildMenu(); };
        desktop.Exit += (_, _) => { collector.Dispose(); Settings.Conversations.Flush(); Growth.Terminate(); Instance?.Dispose(); };
        _ = Task.Run(async () =>
        {
            await Settings.RefreshClientMetadata(force: true);
            await Settings.RefreshEntitlement(force: true);
            await Settings.PushDeviceProfile();
        });
        // 管理配布の接続先（サーバ URL だけ）があれば既定にする。登録は利用者のサインインで行う（付録BF）
        if (!Settings.IsRegistered && Platform.ManagedServerUrl() is { } managed && ServerAddress.Normalized(managed) is { } server) Settings.ServerURL = server.Canonical;
        if (Platform.NeedsAccessibilitySetup && !Platform.AccessibilityTrusted) Platform.RequestAccessibility();
        if (PendingUrl is { } url) { PendingUrl = null; HandleMessage(url); }
        if (!Settings.OnboardingDone || !Settings.StyleOnboardingDone) OpenOnboarding();
        base.OnFrameworkInitializationCompleted();
    }

    void HandleMessage(string message)
    {
        if (message.StartsWith("replyfive://", StringComparison.OrdinalIgnoreCase)) { HandleUrl(message); return; }
        if (message.StartsWith("cmd:", StringComparison.Ordinal)) { HandleCommand(message[4..]); return; }
        // 2 つ目の起動：設定か初回設定を出す
        if (!Settings.OnboardingDone || !Settings.IsRegistered) OpenOnboarding(); else OpenSettings();
    }

    /// <summary>開発・検証用の操作（--send cmd:…）。画面の確認（PNG 書き出し）と各ウインドウの開閉だけ。本文は扱わない。</summary>
    void HandleCommand(string command)
    {
        var parts = command.Split(' ', 2);
        switch (parts[0])
        {
            case "toggle": Toggle(); break;
            case "settings": OpenSettings(); break;
            case "onboarding": OpenOnboarding(); break;
            case "style": OpenStyleSetup(); break;
            case "hide": panel.HidePanel(); settingsWindow?.Hide(); onboardingWindow?.Hide(); styleWindow?.Hide(); break;
            case "quit": desktop.Shutdown(); break;
            case "intent" when parts.Length == 2: vm.Intent = parts[1]; break;
            case "server" when parts.Length == 2: if (ServerAddress.Normalized(parts[1]) is { } srv) Settings.ServerURL = srv.Canonical; break;
            case "lang" when parts.Length == 2: Settings.UiLanguage = parts[1]; break;
            case "generate": vm.Generate(); break;
            case "generate-insert": vm.GenerateAndInsert(); break;
            case "insert": vm.InsertResult(); break;
            case "copy": vm.CopyResult(); break;
            case "update-check": _ = Updater.Check(force: true); break;           // 検証用：更新の確認と取得
            case "update-install": if (Updater.ReadyVersion is not null) Updater.InstallAndRelaunch(); else Diag.Log("update-install: nothing ready"); break;
            case "state": Diag.Log($"state phase={vm.Phase} result_chars={vm.Result.Length} meta={vm.Meta} failure={vm.Failure?.Code ?? "-"} registered={Settings.IsRegistered} org={Settings.OrganizationName ?? "-"} entitlement={Settings.Entitlement?.Status ?? "-"}"); break;
            case "shot" when parts.Length == 2:
                foreach (var (name, w) in new (string, Window?)[] { ("panel", panel), ("settings", settingsWindow), ("onboarding", onboardingWindow), ("style", styleWindow) })
                {
                    if (w is null || !w.IsVisible) continue;
                    try
                    {
                        var scale = w.RenderScaling;
                        var size = new PixelSize((int)(w.Bounds.Width * scale), (int)(w.Bounds.Height * scale));
                        if (size.Width == 0 || size.Height == 0) continue;
                        using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
                        bmp.Render(w);
                        Directory.CreateDirectory(parts[1]);
                        bmp.Save(Path.Combine(parts[1], name + ".png"));
                    }
                    catch (Exception e) { Diag.Log("shot failed " + e.GetType().Name); }
                }
                break;
        }
    }

    /// <summary>replyfive://connect?server=…&amp;link=… を開いたとき。自分が開いたサインインの戻り（設定中のサーバと同じ）は確認なしで接続する。</summary>
    void HandleUrl(string url)
    {
        var src = AutoConnect.FromUrl(url);
        if (src is null) { Diag.Log("connect link ignored (malformed)"); return; }
        if (ServerAddress.Same(Settings.ServerURL, src.Server.Canonical)) { _ = Connect(src); return; }
        var dialog = new ConfirmWindow(L("connect.confirm.title"), L("connect.confirm.body", src.Server.Canonical), L("connect.confirm.accept"), L("connect.confirm.cancel"));
        dialog.Confirmed += () => _ = Connect(src);
        dialog.Show();
        dialog.Activate();
    }

    async Task Connect(AutoConnect.Source src)
    {
        Diag.Log("connect start origin=" + src.Origin);
        var (org, error) = await AutoConnect.Connect(src, Settings);
        if (error is null)
        {
            Diag.Log("connect ok origin=" + src.Origin);
            Growth.Track("connect_succeeded", new() { ["origin"] = src.Origin });
            _ = Growth.Flush();
            Toast.Show(L("connect.done", org ?? "", Settings.HotKeyCombo.Display), true);
            settingsWindow?.Hide();
            if (onboardingWindow?.IsVisible == true) onboardingWindow.Activate();
            BuildMenu();
        }
        else
        {
            Diag.Log($"connect failed origin={src.Origin} code={error.Code}");
            Growth.Track("connect_failed", new() { ["origin"] = src.Origin, ["error_code"] = error.Code });
            var msg = Has("error." + error.Code) ? L("error." + error.Code) : L("error.generic", error.Code);
            Toast.Show(L("connect.failed", msg), false);
        }
    }

    // MARK: - 通知領域

    void SetupTray()
    {
        tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://ReplyFive/Assets/replyfive-64.png"))),
            ToolTipText = "ReplyFive",
            IsVisible = true,
            Menu = trayMenu,
        };
        tray.Clicked += (_, _) => Toggle();
        TrayIcon.SetIcons(this, [tray]);
        BuildMenu();
    }

    void BuildMenu()
    {
        if (tray is null) return;
        try { BuildMenuCore(); }
        catch (Exception e) { Diag.Log("menu rebuild failed " + e.GetType().Name); }
    }

    /// <summary>通知領域のメニューは同じ NativeMenu の項目を入れ替える（macOS の Avalonia は Menu の差し替えで例外になる）。</summary>
    void BuildMenuCore()
    {
        var menu = trayMenu;
        menu.Items.Clear();
        var connected = Settings.IsRegistered;
        var who = Settings.PrimaryUserName;
        var statusTitle = connected
            ? (who.Length == 0 ? L("menu.status.connected", Settings.OrganizationName ?? "") : L("menu.status.connected_as", who, Settings.OrganizationName ?? ""))
            : L("menu.status.not_connected");
        var status = new NativeMenuItem(statusTitle) { IsEnabled = !connected };
        status.Click += (_, _) => OpenOnboarding();
        menu.Add(status);
        if (connected)
        {
            var inactive = Settings.Entitlement?.Status == "inactive";
            var plan = new NativeMenuItem(PlanTitle) { IsEnabled = inactive };
            plan.Click += (_, _) => { Growth.Track("billing_opened", new() { ["origin"] = "menu" }); Platform.OpenUrl(ServerAddress.BillingUrl(Settings.Server).ToString()); };
            menu.Add(plan);
            if (Settings.Entitlement?.Seats.Limit is { } limit) menu.Add(new NativeMenuItem(L("menu.seats", Settings.Entitlement.Seats.Used, limit)) { IsEnabled = false });
        }
        // 付録CD：準備済みなら「更新して再起動」、取得中は進み具合、取得に失敗していればダウンロードページ。ストア版は何も出さない（付録CH）
        if (Distribution.IsStore) { }
        else if (Updater.ReadyVersion is { } ready)
        {
            var item = new NativeMenuItem(L("menu.update.install", ready));
            item.Click += (_, _) => { Growth.Track("update_install_clicked", new() { ["latest_version"] = ready }); Updater.InstallAndRelaunch(); };
            menu.Add(item);
        }
        else if (Updater.State == UpdateState.Downloading)
            menu.Add(new NativeMenuItem(L("menu.update.downloading", Updater.StateVersion ?? "", (int)Math.Round(Updater.Progress * 100))) { IsEnabled = false });
        else if (Settings.UpdateAvailable && Settings.DownloadURL is { } dl && (Updater.State == UpdateState.Failed || !Platform.Updates.CanSelfInstall))
        {
            var item = new NativeMenuItem(L("menu.update", Settings.LatestClientVersion ?? ""));
            item.Click += (_, _) => { Growth.Track("update_opened", new() { ["latest_version"] = Settings.LatestClientVersion ?? "" }); Platform.OpenUrl(dl); };
            menu.Add(item);
        }
        var open = new NativeMenuItem(L("menu.open") + "   " + Settings.HotKeyCombo.Display);
        open.Click += (_, _) => Toggle();
        menu.Add(open);
        var onboarding = new NativeMenuItem(L("menu.onboarding"));
        onboarding.Click += (_, _) => OpenOnboarding();
        menu.Add(onboarding);
        menu.Add(new NativeMenuItemSeparator());
        if (Platform.NeedsAccessibilitySetup && !Platform.AccessibilityTrusted)
        {
            var allow = new NativeMenuItem(L("menu.allow_accessibility"));
            allow.Click += (_, _) => Platform.RequestAccessibility();
            menu.Add(allow);
        }
        var prefs = new NativeMenuItem(L("menu.settings"));
        prefs.Click += (_, _) => OpenSettings();
        menu.Add(prefs);
        menu.Add(new NativeMenuItemSeparator());
        var quit = new NativeMenuItem(L("menu.quit"));
        quit.Click += (_, _) => desktop.Shutdown();
        menu.Add(quit);
    }

    string PlanTitle
    {
        get
        {
            var e = Settings.Entitlement;
            if (e is null) return L("menu.plan.active");
            return e.Status switch
            {
                "trial" => e.Trial is null ? L("menu.plan.trial_today") : e.Trial.DaysLeft == 0 ? L("menu.plan.trial_today") : L("menu.plan.trial", e.Trial.DaysLeft),
                "active" or "license_active" => L("menu.plan.active"),
                "license_grace" => L("menu.plan.grace"),
                "license_expired" => L("menu.plan.expired"),
                _ => L("menu.plan.inactive"),
            };
        }
    }

    // MARK: - ショートカット

    void RecreateHotKey()
    {
        hotKey?.Dispose(); hotKey = null;
        if (Settings.IsRecordingHotKey) return;
        var combo = Settings.HotKeyCombo;
        try { hotKey = Platform.RegisterHotKey(combo, () => Dispatcher.UIThread.Post(Toggle)); }
        catch (Exception e) { Crash.Exception(e); hotKey = null; }
        var registered = hotKey?.Registered ?? Platform.OsName == "macos";
        if (Settings.HotKeyRegistered != registered) Settings.HotKeyRegistered = registered;
        if (!registered) Diag.Log("hotkey registration failed " + combo.Stored);
    }

    /// <summary>付録AC: 取得→表示→通知。取得は 700ms で打ち切り、UI スレッドは止めない。</summary>
    public void Toggle()
    {
        lastInteraction = DateTimeOffset.UtcNow;
        if (panel.IsVisible) { panel.HidePanel(); return; }
        if (capturing) return;
        capturing = true;
        var includeContext = Settings.Policy?.ContextCaptureAllowed != false;
        var ownName = Settings.UserName.Trim();
        var front = Platform.FrontmostApp() ?? lastExternalApp;
        lastExternalApp = front;
        var shown = false;
        var shownByTimeout = false;
        void Show(CapturedContext ctx)
        {
            if (shown) return;
            shown = true;
            panel.ShowPanel(ctx, Platform.CursorPosition());
            capturing = false;
            Growth.DailyActive();
            Growth.Track("hotkey_pressed", new()
            {
                ["platform"] = ctx.Platform.Wire(), ["capture_source"] = ctx.Source.Wire(), ["context_chars_bucket"] = GrowthBuckets.Chars(ctx.Text.Length),
                ["had_contact"] = ctx.ContactKey is not null, ["trusted"] = Platform.AccessibilityTrusted,
            });
        }
        _ = Task.Run(() =>
        {
            CapturedContext c;
            try { c = Platform.Capture(front, includeContext, ownName.Length == 0 ? null : ownName, 0.35, true); }
            catch (Exception e) { Crash.Exception(e); c = CapturedContext.Empty(front, CaptureError.Failed); }
            Diag.Log($"capture app={c.AppName ?? "nil"} element={c.FocusedElement is not null} source={c.Source} error={c.Error?.ToString() ?? "none"} platform={c.Platform} chars={c.Text.Length} contact={c.ContactName is not null}");
            Dispatcher.UIThread.Post(() =>
            {
                collector.Ingest(c);
                if (shown && shownByTimeout) { panel.UpdatePanel(c); Diag.Log($"late capture applied source={c.Source} chars={c.Text.Length}"); }
                else Show(c);
            });
        });
        DispatcherTimer.RunOnce(() =>
        {
            if (!shown) shownByTimeout = true;
            Show(CapturedContext.Empty(front, CaptureError.Failed));
        }, TimeSpan.FromMilliseconds(700));
    }

    // MARK: - 自動更新（付録CD）：準備済みの版があり、パネルも設定も開いておらず、10 分以上操作が無ければ入れ替えて再起動する

    void ScheduleAutoInstall()
    {
        autoInstallTimer?.Stop();
        autoInstallTimer = new DispatcherTimer(TimeSpan.FromSeconds(60), DispatcherPriority.Background, (_, _) => AutoInstallIfIdle());
        autoInstallTimer.Start();
    }

    void AutoInstallIfIdle()
    {
        if (!Settings.AutoUpdate || Updater.ReadyVersion is null) { autoInstallTimer?.Stop(); autoInstallTimer = null; return; }
        if (panel.IsVisible || settingsWindow?.IsVisible == true || onboardingWindow?.IsVisible == true || styleWindow?.IsVisible == true) return;
        if ((DateTimeOffset.UtcNow - lastInteraction).TotalSeconds <= 600) return;
        Diag.Log("update auto-install idle");
        autoInstallTimer?.Stop(); autoInstallTimer = null;
        Updater.InstallAndRelaunch();
    }

    // MARK: - ウインドウ

    public void OpenSettings()
    {
        lastInteraction = DateTimeOffset.UtcNow;
        _ = Settings.RefreshEntitlement();
        settingsWindow ??= new SettingsWindow(this, () => collector.RunTuningNow(), OpenStyleSetup);
        settingsWindow.Show();
        settingsWindow.Activate();
    }

    void OpenTrial()
    {
        Growth.Track("sign_in_opened", new() { ["origin"] = "panel" });
        AutoConnect.OpenSignIn(Settings, Platform);
    }

    public void OpenStyleSetup()
    {
        lastInteraction = DateTimeOffset.UtcNow;
        styleWindow ??= new StyleWindow(this);
        styleWindow.Reload();
        styleWindow.Show();
        styleWindow.Activate();
    }

    public void OpenOnboarding()
    {
        lastInteraction = DateTimeOffset.UtcNow;
        onboardingWindow ??= new OnboardingWindow(this);
        onboardingWindow.Show();
        onboardingWindow.Activate();
    }

    public void Touch() => lastInteraction = DateTimeOffset.UtcNow;
}
