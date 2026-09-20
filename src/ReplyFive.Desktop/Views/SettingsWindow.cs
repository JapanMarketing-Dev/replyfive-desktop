using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ReplyFive.Core;
using ReplyFive.Desktop.Services;
using static ReplyFive.Desktop.Services.L10n;

namespace ReplyFive.Desktop.Views;

/// <summary>設定。初回はここが導線になる：1) 読み取りの許可（Linux）2) サインイン。返信の記録（収集）はここでオフにできる。</summary>
public sealed class SettingsWindow : Window
{
    readonly App app;
    AppSettings S => app.Settings;
    readonly Action openStyle;
    readonly StackPanel root = new() { Spacing = 8, Margin = new Thickness(20) };
    readonly DispatcherTimer timer;
    string? serverStatus;
    string? registerError;

    public SettingsWindow(App app, Action openStyle)
    {
        this.app = app; this.openStyle = openStyle;
        Title = L("settings.window_title");
        Width = 560; Height = 760; MinWidth = 520; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://ReplyFive/Assets/replyfive-64.png")));
        Content = new ScrollViewer { Content = root };
        Bind(BackgroundProperty, this.GetResourceObservable("RfWindowBg"));   // 付録CF-7：ライト／ダーク
        Closing += (_, e) => { e.Cancel = true; Hide(); };
        Opened += (_, _) => { Build(); _ = S.RefreshEntitlement(); };
        timer = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) => { if (IsVisible) RefreshDynamic(); });
        timer.Start();
        S.PropertyChanged += (_, e) => { if (IsVisible && e.PropertyName is nameof(AppSettings.IsRegistered) or nameof(AppSettings.Entitlement) or nameof(AppSettings.OrganizationName) or nameof(AppSettings.LatestClientVersion) or nameof(AppSettings.HotKey)) Dispatcher.UIThread.Post(Build); };
        app.Updater.PropertyChanged += (_, _) => { if (IsVisible) Dispatcher.UIThread.Post(Build); };
        var pushDebounce = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => { }) ;
        pushDebounce.Tick += (_, _) => { pushDebounce.Stop(); _ = S.PushDeviceProfile(); };
        S.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(AppSettings.UserName) or nameof(AppSettings.DeviceName)) { pushDebounce.Stop(); pushDebounce.Start(); } };
        app.Touch();
    }

    public void Relocalize() { Title = L("settings.window_title"); if (IsVisible) Build(); }

    // MARK: - 組み立て

    TextBlock? kpiText, sentKpiText, usageText, recordsCountText, convCountText;
    StackPanel? recentList;
    Button? deleteRecords, deleteConversations;
    PathIcon? accessibilityIcon;

    void Build()
    {
        root.Children.Clear();
        var platform = app.Platform;
        if (platform.NeedsAccessibilitySetup)
        {
            accessibilityIcon = Icon_(platform.AccessibilityTrusted ? Icons.CheckCircle : Icons.Alert, platform.AccessibilityTrusted ? "#16A34A" : "#D97706");
            var allow = new Button { Content = L("settings.accessibility.open"), IsVisible = !platform.AccessibilityTrusted };
            allow.Click += (_, _) => platform.RequestAccessibility();
            root.Children.Add(Card(Row(accessibilityIcon, Col(Head(L("settings.accessibility.title")), Cap(L("settings.accessibility.body"))), allow)));
        }

        // 表示
        var lang = new ComboBox { Width = 200, ItemsSource = new[] { L("settings.ui_language.system"), "English", "日本語" }, SelectedIndex = S.UiLanguage switch { "en" => 1, "ja" => 2, _ => 0 } };
        lang.SelectionChanged += (_, _) => S.UiLanguage = lang.SelectedIndex switch { 1 => "en", 2 => "ja", _ => "system" };
        root.Children.Add(Section(L("settings.display.title"), Labeled(L("settings.ui_language"), lang)));

        // サーバ
        var server = new StackPanel { Spacing = 8 };
        var serverInput = new TextBox { Text = S.ServerURL, PlaceholderText = L("settings.server.url") };
        var test = new Button { Content = L("settings.server.test") };
        var statusText = new TextBlock { Classes = { "caption" }, Text = serverStatus ?? "", VerticalAlignment = VerticalAlignment.Center };
        test.Click += async (_, _) =>
        {
            var addr = ServerAddress.Normalized(serverInput.Text);
            if (addr is null) { statusText.Text = L("settings.server.invalid_url"); return; }
            statusText.Text = "…";
            var client = new ApiClient(addr.Uri, null, ReplyFiveInfo.Version, platform.OsName, S.ApiLanguage);
            try { var m = await client.Meta(); serverStatus = L("settings.server.ok", m.Mode, m.Llm.Provider); }
            catch (Exception) { serverStatus = L("settings.server.unreachable"); }
            statusText.Text = serverStatus;
        };
        server.Children.Add(Labeled(L("settings.server.url"), serverInput));
        server.Children.Add(Row(test, statusText));
        if (S.IsRegistered && ServerAddress.Same(serverInput.Text, S.ServerURL))
        {
            var unregister = new Button { Content = L("settings.unregister") };
            unregister.Click += async (_, _) => { var c = S.MakeClient(); try { await c.RevokeSelf(); } catch (Exception) { } S.ClearRegistration(); Build(); };
            server.Children.Add(Row(Icon_(Icons.Seal, "#16A34A"), new TextBlock { Text = L("settings.registered", S.OrganizationName ?? ""), VerticalAlignment = VerticalAlignment.Center }, unregister));
            server.Children.Add(Cap(platform.AccessibilityTrusted ? L("settings.ready_hint", S.HotKeyCombo.Display) : L("settings.ready_hint.no_accessibility")));
            if (S.Entitlement is { } e)
            {
                var plan = new StackPanel { Spacing = 2 };
                plan.Children.Add(new TextBlock { Text = PlanTitle(e), FontSize = 13 });
                if (e.Trial?.EndsAt is { } ends && DateTimeOffset.TryParse(ends, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)) plan.Children.Add(Cap(L("settings.plan.trial_end", date.ToLocalTime().ToString("d", CultureInfo.CurrentCulture))));
                if (e.Seats.Limit is { } limit) plan.Children.Add(Cap(L("settings.plan.seats", e.Seats.Used, limit)));
                if (e.DailyLimit is { } daily) plan.Children.Add(Cap(L("settings.plan.daily_limit", daily)));
                server.Children.Add(plan);
            }
        }
        else
        {
            var deviceName = new TextBox { Text = S.DeviceName, PlaceholderText = L("settings.device_name") };
            deviceName.TextChanged += (_, _) => S.DeviceName = deviceName.Text ?? "";
            server.Children.Add(Labeled(L("settings.device_name"), deviceName));
            var signIn = new Button { Content = L("settings.sign_in"), Classes = { "primary" } };
            var err = new TextBlock { Classes = { "caption" }, Foreground = Brushes.Red, Text = registerError ?? "", IsVisible = registerError is not null };
            signIn.Click += (_, _) =>
            {
                var addr = ServerAddress.Normalized(serverInput.Text);
                if (addr is null) { registerError = L("settings.server.invalid_url"); err.Text = registerError; err.IsVisible = true; return; }
                registerError = null; err.IsVisible = false;
                S.ServerURL = addr.Canonical;
                Growth.Track("sign_in_opened", new() { ["origin"] = "settings" });
                AutoConnect.OpenSignIn(S, platform);
            };
            server.Children.Add(Row(signIn, Cap(L("settings.sign_in_hint"))));
            server.Children.Add(err);
        }
        root.Children.Add(Section(L("settings.server.title"), server));

        // 生成設定
        var behavior = new StackPanel { Spacing = 8 };
        var userName = new TextBox { Text = S.UserName, PlaceholderText = platform.SuggestedUserName };
        userName.TextChanged += (_, _) => S.UserName = userName.Text ?? "";
        behavior.Children.Add(Labeled(L("settings.user_name"), userName));
        behavior.Children.Add(Cap(L("settings.user_name.hint")));
        var styleBtn = new Button { Content = L("settings.style.open") };
        styleBtn.Click += (_, _) => openStyle();
        behavior.Children.Add(Labeled(L("settings.style.title"), new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = StyleSummary(), Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260 }, styleBtn } }));
        behavior.Children.Add(Cap(L("settings.style.hint")));
        behavior.Children.Add(Labeled(L("settings.shortcut"), new ShortcutRecorder(S)));
        var langs = new List<(string code, string name)> { ("auto", L("language.auto")) };
        langs.AddRange(Languages);
        var outLang = new ComboBox { Width = 200, ItemsSource = langs.Select(l => l.name).ToList(), SelectedIndex = Math.Max(0, langs.FindIndex(l => l.code == S.OutputLanguage)) };
        outLang.SelectionChanged += (_, _) => { if (outLang.SelectedIndex >= 0) S.OutputLanguage = langs[outLang.SelectedIndex].code; };
        behavior.Children.Add(Labeled(L("settings.output_language"), outLang));
        var review = new ToggleSwitch { IsChecked = S.ReviewBeforeInsert, OnContent = null, OffContent = null };
        review.IsCheckedChanged += (_, _) => { S.ReviewBeforeInsert = review.IsChecked == true; Growth.Track("settings_changed", new() { ["key"] = "review_before_insert", ["enabled"] = S.ReviewBeforeInsert }); };
        behavior.Children.Add(Labeled(L("settings.review_before_insert"), review));
        if (platform.SupportsLaunchAtLogin)
        {
            var launch = new ToggleSwitch { IsChecked = platform.LaunchAtLogin, OnContent = null, OffContent = null };
            launch.IsCheckedChanged += (_, _) => { try { platform.LaunchAtLogin = launch.IsChecked == true; } catch (Exception) { launch.IsChecked = platform.LaunchAtLogin; } Growth.Track("settings_changed", new() { ["key"] = "launch_at_login", ["enabled"] = platform.LaunchAtLogin }); };
            behavior.Children.Add(Labeled(L("settings.launch_at_login"), launch));
        }
        usageText = Cap(""); kpiText = Cap(""); sentKpiText = Cap("");
        behavior.Children.Add(Labeled(L("settings.usage"), usageText));
        behavior.Children.Add(Labeled(L("settings.kpi"), kpiText));
        behavior.Children.Add(Labeled(L("settings.sent_kpi"), sentKpiText));
        recentList = new StackPanel { Spacing = 4 };
        behavior.Children.Add(recentList);
        root.Children.Add(Section(L("settings.behavior.title"), behavior));

        // 会話の保持
        var conv = new StackPanel { Spacing = 6 };
        var convToggle = new ToggleSwitch { IsChecked = S.ConversationEnabled, IsEnabled = S.Policy?.ContextCaptureAllowed != false, OnContent = null, OffContent = null };
        convToggle.IsCheckedChanged += (_, _) => { S.ConversationEnabled = convToggle.IsChecked == true; Growth.Track("settings_changed", new() { ["key"] = "conversation", ["enabled"] = S.ConversationEnabled }); };
        conv.Children.Add(Row(Col(Head(L("settings.conversations.title")), Cap(L("settings.conversations.body"))), convToggle));
        convCountText = Cap("");
        deleteConversations = new Button { Content = L("settings.records.delete") };
        deleteConversations.Click += (_, _) => { S.Conversations.DeleteAll(); RefreshDynamic(); };
        conv.Children.Add(Row(convCountText, deleteConversations));
        root.Children.Add(Card(conv));

        // 返信の記録
        var rec = new StackPanel { Spacing = 6 };
        var recToggle = new ToggleSwitch { IsChecked = S.LearningEnabled, IsEnabled = S.Policy?.LearningAllowed != false, OnContent = null, OffContent = null };
        recToggle.IsCheckedChanged += (_, _) => { S.LearningEnabled = recToggle.IsChecked == true; Growth.Track("settings_changed", new() { ["key"] = "learning", ["enabled"] = S.LearningEnabled }); };
        rec.Children.Add(Row(Col(Head(L("settings.records.title")), Cap(L("settings.records.body"))), recToggle));
        if (S.Policy?.LearningAllowed == false) rec.Children.Add(Cap(L("settings.records.organization_disabled")));
        recordsCountText = Cap("");
        deleteRecords = new Button { Content = L("settings.records.delete") };
        deleteRecords.Click += (_, _) => { S.Records.DeleteAll(); RefreshDynamic(); };
        rec.Children.Add(Row(recordsCountText, deleteRecords));
        var privacy = new Button { Content = L("settings.privacy_policy"), Classes = { "link" } };
        privacy.Click += (_, _) => platform.OpenUrl(ReplyFiveInfo.DefaultServerUrl + "legal/privacy");
        var terms = new Button { Content = L("settings.terms"), Classes = { "link" } };
        terms.Click += (_, _) => platform.OpenUrl(ReplyFiveInfo.DefaultServerUrl + "legal/terms");
        rec.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { privacy, terms } });
        rec.Children.Add(Cap(L("settings.privacy.body")));
        root.Children.Add(Section(L("settings.privacy.title"), rec));

        // サポート
        var support = new StackPanel { Spacing = 6 };
        var copyDiag = new Button { Content = L("settings.copy_diagnostics") };
        copyDiag.Click += (_, _) => { platform.CopyToClipboard(Diagnostics()); Toast.Show(L("settings.diagnostics.copied"), true); };
        var openLogs = new Button { Content = L("settings.open_logs") };
        openLogs.Click += (_, _) => platform.OpenFolder(AppPaths.LogDir);
        support.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { copyDiag, openLogs } });
        support.Children.Add(Cap(L("settings.diagnostics.hint")));
        root.Children.Add(Section(L("settings.support.title"), support));

        // アップデート
        var upd = new StackPanel { Spacing = 6 };
        upd.Children.Add(Labeled(L("settings.version"), Cap($"{ReplyFiveInfo.Version} · contract {ReplyFiveInfo.ContractVersion}")));
        var updater = app.Updater;
        if (Distribution.IsStore)
        {
            // ストア版：更新はストアが配る（付録CH）。確認ボタンも自動更新の切替も出さない
            upd.Children.Add(Cap(L("settings.update.store")));
            root.Children.Add(Section(L("settings.update.title"), upd));
            return;
        }
        var updRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        updRow.Children.Add(new TextBlock { Text = UpdateStatus(), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 });
        if (updater.ReadyVersion is { } ready)
        {
            var install = new Button { Content = L("settings.update.install", ready), Classes = { "primary" } };
            install.Click += (_, _) => updater.InstallAndRelaunch();
            updRow.Children.Add(install);
        }
        else
        {
            var check = new Button { Content = L("settings.update.check"), IsEnabled = !updater.IsBusy };
            check.Click += async (_, _) => await updater.Check(force: true);
            updRow.Children.Add(check);
        }
        upd.Children.Add(updRow);
        if ((updater.State == UpdateState.Failed || !platform.Updates.CanSelfInstall && S.UpdateAvailable) && S.DownloadURL is not null)
        {
            var manual = new Button { Content = L("settings.update.manual") };
            manual.Click += (_, _) => updater.OpenDownloadPage();
            upd.Children.Add(manual);
        }
        if (platform.Updates.CanSelfInstall)
        {
            var auto = new ToggleSwitch { IsChecked = S.AutoUpdate, OnContent = null, OffContent = null };
            auto.IsCheckedChanged += (_, _) => { S.AutoUpdate = auto.IsChecked == true; Growth.Track("settings_changed", new() { ["key"] = "auto_update", ["enabled"] = S.AutoUpdate }); };
            upd.Children.Add(Labeled(L("settings.update.auto"), auto));
            upd.Children.Add(Cap(L("settings.update.auto.hint")));
        }
        root.Children.Add(Section(L("settings.update.title"), upd));

        RefreshDynamic();
    }

    void RefreshDynamic()
    {
        if (usageText is null) return;
        var platform = app.Platform;
        if (accessibilityIcon is not null) { accessibilityIcon.Data = platform.AccessibilityTrusted ? Icons.CheckCircle : Icons.Alert; accessibilityIcon.Foreground = new SolidColorBrush(Color.Parse(platform.AccessibilityTrusted ? "#16A34A" : "#D97706")); }
        usageText.Text = L("settings.usage.value", S.Usage(1), S.Usage(30));
        var kpi = S.Records.Kpi(30);
        kpiText!.Text = kpi.Count == 0 ? L("settings.kpi.none") : L("settings.kpi.value", (int)Math.Round(kpi.UneditedRate * 100), kpi.AverageEditChars, kpi.Count);
        sentKpiText!.Text = kpi.Inserted == 0 ? L("settings.sent_kpi.none") : L("settings.sent_kpi.value", (int)Math.Round(kpi.SentRate * 100), kpi.Sent, kpi.Inserted);
        var count = S.Records.Count;
        recordsCountText!.Text = L("settings.records.count", count);
        deleteRecords!.IsEnabled = count > 0;
        var lines = S.Conversations.TotalMessages;
        convCountText!.Text = L("settings.conversations.count", S.Conversations.ContactCount, lines);
        deleteConversations!.IsEnabled = lines > 0;
        recentList!.Children.Clear();
        var recent = S.Records.Latest();
        if (recent.Count > 0)
        {
            recentList.Children.Add(new TextBlock { Text = L("settings.records.latest"), FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
            foreach (var row in recent)
            {
                var title = string.Join(" · ", new[] { row.AppName, row.Platform.Wire() }.Where(x => !string.IsNullOrEmpty(x)));
                var state = row.Action == "insert" ? (row.Sent == true ? L("settings.records.sent") : L("settings.records.inserted")) : L("settings.records.copied");
                recentList.Children.Add(Row(Col(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeight.SemiBold }, Cap(state)), Cap(L("settings.records.edit_chars", row.EditChars ?? 0))));
            }
        }
    }

    string StyleSummary()
    {
        var p = S.StyleProfile?.Normalized;
        if (p is null || p.IsEmpty) return L("settings.style.none");
        return string.Join(" · ", StyleProfile.Fields.Select(f => p.Get(f) is { } v ? L($"style.value.{f}.{v}") : null).Where(x => x is not null));
    }

    string UpdateStatus()
    {
        var u = app.Updater;
        return u.State switch
        {
            UpdateState.Idle => S.UpdateAvailable ? L("settings.update.state.available", S.LatestClientVersion ?? "") : L("settings.update.state.unknown"),
            UpdateState.Checking => L("settings.update.state.checking"),
            UpdateState.UpToDate => L("settings.update.state.up_to_date"),
            UpdateState.Downloading => L("settings.update.state.downloading", u.StateVersion ?? "", (int)Math.Round(u.Progress * 100)),
            UpdateState.Ready => L("settings.update.state.ready", u.StateVersion ?? ""),
            UpdateState.Installing => L("settings.update.state.installing"),
            _ => L("settings.update.state.failed", u.StateVersion ?? "", u.FailureMessage ?? ""),
        };
    }

    string PlanTitle(EntitlementView e) => e.Status switch
    {
        "trial" => L("menu.plan.trial", e.Trial?.DaysLeft ?? 0),
        "active" or "license_active" => L("menu.plan.active"),
        "license_grace" => L("menu.plan.grace"),
        "license_expired" => L("menu.plan.expired"),
        _ => L("menu.plan.inactive"),
    };

    string Diagnostics() => string.Join("\n",
        "ReplyFive diagnostics",
        "App version: " + ReplyFiveInfo.Version,
        "OS: " + app.Platform.OsName + " " + app.Platform.OsVersion,
        "Accessibility: " + (app.Platform.AccessibilityTrusted ? "yes" : "no"),
        "Connected: " + (S.IsRegistered ? "yes (" + (S.OrganizationName ?? "") + ")" : "no"),
        "Server host: " + S.Server.Uri.Host,
        "Shortcut: " + S.HotKeyCombo.Display + (S.HotKeyRegistered ? "" : " (not registered)"),
        "Reply records enabled: " + (S.LearningEnabled ? "yes" : "no"),
        "Reply records: " + S.Records.Count,
        "UI language: " + S.UiLanguage);

    public static readonly (string code, string name)[] Languages =
    [
        ("en", "English"), ("ja", "日本語"), ("zh", "中文"), ("ko", "한국어"), ("es", "Español"), ("fr", "Français"),
        ("de", "Deutsch"), ("pt", "Português"), ("it", "Italiano"), ("id", "Bahasa Indonesia"), ("th", "ไทย"), ("vi", "Tiếng Việt"),
    ];

    // MARK: - 部品

    public static TextBlock Head(string t) => new() { Text = t, Classes = { "headline" }, TextWrapping = TextWrapping.Wrap };
    public static TextBlock Cap(string t) => new() { Text = t, Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center };
    public static PathIcon Icon_(Geometry g, string color) => new() { Data = g, Width = 18, Height = 18, Foreground = new SolidColorBrush(Color.Parse(color)), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
    public static StackPanel Col(params Control[] children) { var s = new StackPanel { Spacing = 3 }; foreach (var c in children) s.Children.Add(c); return s; }
    public static Grid Row(params Control[] children)
    {
        var g = new Grid { ColumnSpacing = 10 };
        for (var i = 0; i < children.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition(i == (children.Length >= 2 ? (children.Length == 2 ? 0 : 1) : 0) ? GridLength.Star : GridLength.Auto));
            Grid.SetColumn(children[i], i);
            children[i].VerticalAlignment = children[i] is StackPanel ? VerticalAlignment.Top : VerticalAlignment.Center;
            g.Children.Add(children[i]);
        }
        return g;
    }
    public static Grid Labeled(string label, Control value)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        var t = new TextBlock { Text = label, FontSize = 13, VerticalAlignment = value is StackPanel ? VerticalAlignment.Top : VerticalAlignment.Center, Margin = value is StackPanel ? new Thickness(0, 6, 0, 0) : default, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(t, 0); Grid.SetColumn(value, 1);
        value.VerticalAlignment = VerticalAlignment.Center;
        if (value is TextBox tb) { tb.MinWidth = 260; }
        g.Children.Add(t); g.Children.Add(value);
        return g;
    }
    public static Border Card(Control content) => new() { Classes = { "card" }, Child = content };
    public static StackPanel Section(string title, Control content) => new() { Spacing = 4, Children = { new TextBlock { Text = title, Classes = { "section" } }, Card(content) } };
}
