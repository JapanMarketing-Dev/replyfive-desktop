using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ReplyFive.Core;
using ReplyFive.Desktop.Services;
using static ReplyFive.Desktop.Services.L10n;
using static ReplyFive.Desktop.Views.OnboardingStyle;
using static ReplyFive.Desktop.Views.SettingsWindow;

namespace ReplyFive.Desktop.Views;

/// <summary>同意した利用規約・プライバシーポリシーの版（付録CF-6）。規約・ポリシーを変えたらここを上げると、次回起動で同意ページが再び出る。</summary>
public static class Legal
{
    public const string Version = "2026-09-19";
    public static Uri Terms => new(ReplyFiveInfo.DefaultServerUrl, "legal/terms");
    public static Uri Privacy => new(ReplyFiveInfo.DefaultServerUrl, "legal/privacy");
}

/// <summary>初回設定（付録CF-6、macOS 版 OnboardingView.swift の移植）。設定はさせない。
/// 0) 利用規約・プライバシーポリシーへの同意 → 1) 普段使うアプリの選択（付録CF-8、自動検出は CF-9）・読み取りの許可（Linux）・サインイン・名前
/// → 2) 会話を見せてもらう（一定量まで。StyleSetupView）。返し方は実際の返信から自動で判定し、画面には出さない。</summary>
public sealed class OnboardingWindow : Window
{
    readonly App app;
    AppSettings S => app.Settings;
    int page;
    readonly ContentControl host = new();
    readonly DispatcherTimer timer;
    TextBox? nameBox;
    TextBox? queryBox;
    bool skipped;
    bool agreed;
    string appQuery = "";
    AppCategory? appCategory;
    readonly HashSet<string> selected = [];
    List<string> detected = [];
    bool detectedNote;
    bool lastTrusted;
    bool lastRegistered;

    public OnboardingWindow(App app)
    {
        this.app = app;
        Title = L("onboarding.title");
        Width = 520; Height = 740; MinWidth = 480; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;   // 付録CF：利用者が Slack や Gmail を前面にしても一覧が見え続ける
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://ReplyFive/Assets/replyfive-64.png")));
        Bind(BackgroundProperty, this.GetResourceObservable("RfWindowBg"));
        Content = host;
        Closing += (_, e) => { e.Cancel = true; Hide(); };
        Opened += (_, _) =>
        {
            selected.Clear(); foreach (var id in S.SelectedPlatforms) selected.Add(id);
            AutoDetect(replace: selected.Count == 0);
            page = S.TermsAcceptedVersion != Legal.Version ? 0 : (S.OnboardingDone && !S.StyleOnboardingDone ? 2 : 1);
            Growth.Track("onboarding_shown");
            Build();
        };
        timer = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) =>
        {
            if (!IsVisible || page != 1) return;
            var trusted = app.Platform.AccessibilityTrusted;
            if (trusted != lastTrusted || S.IsRegistered != lastRegistered) Build(keepState: true);
        });
        timer.Start();
        S.PropertyChanged += (_, e) =>
        {
            if (!IsVisible) return;
            if (e.PropertyName is nameof(AppSettings.Appearance)) Dispatcher.UIThread.Post(() => Build(keepState: true));
            else if (page == 1 && e.PropertyName is nameof(AppSettings.IsRegistered) or nameof(AppSettings.Entitlement) or nameof(AppSettings.OrganizationName)) Dispatcher.UIThread.Post(() => Build(keepState: true));
        };
    }

    public void Relocalize() { Title = L("onboarding.title"); if (IsVisible) Build(keepState: true); }

    /// <summary>検証用（cmd:onboarding-page）：ページを直接開く。</summary>
    public void DevGoToPage(int n) { page = Math.Clamp(n, 0, 2); Build(keepState: true); }

    string NameValue => (nameBox?.Text ?? "").Trim();

    void Build(bool keepState = false)
    {
        host.Content = page switch
        {
            0 => new ScrollViewer { Content = ConsentPage() },
            1 => new ScrollViewer { Content = SetupPage(keepState) },
            _ => new StyleSetupView(app, finish: FinishAll, stepOffset: 2),
        };
    }

    // MARK: - 0) 同意

    Control ConsentPage()
    {
        var stack = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        stack.Children.Add(Hero(1, 3, L("onboarding.consent.title"), L("onboarding.consent.subtitle"), S, () => Build(keepState: true)));
        var lines = new StackPanel { Spacing = 10 };
        foreach (var k in new[] { "reads", "keeps", "sends", "never" })
        {
            var never = k == "never";
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
            var icon = Icon_(never ? Icons.Alert : Icons.CheckCircle, never ? "#D97706" : "#16A34A");
            var text = new TextBlock { Text = L("onboarding.consent." + k), FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = OnboardingStyle.Text };
            Grid.SetColumn(icon, 0); Grid.SetColumn(text, 1);
            row.Children.Add(icon); row.Children.Add(text);
            lines.Children.Add(row);
        }
        stack.Children.Add(Card(lines));
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        var terms = new Button { Content = L("onboarding.consent.terms"), Classes = { "link" } }; terms.Click += (_, _) => app.Platform.OpenUrl(Legal.Terms.ToString());
        var privacy = new Button { Content = L("onboarding.consent.privacy"), Classes = { "link" } }; privacy.Click += (_, _) => app.Platform.OpenUrl(Legal.Privacy.ToString());
        links.Children.Add(terms); links.Children.Add(privacy);
        stack.Children.Add(links);
        Button? cont = null;
        var agree = new CheckBox { Content = new TextBlock { Text = L("onboarding.consent.agree"), FontSize = 13, TextWrapping = TextWrapping.Wrap }, IsChecked = agreed };
        agree.IsCheckedChanged += (_, _) => { agreed = agree.IsChecked == true; if (cont is not null) cont.IsEnabled = agreed; };
        stack.Children.Add(agree);
        cont = GradientButton(L("onboarding.consent.continue"));
        cont.IsEnabled = agreed; cont.HorizontalAlignment = HorizontalAlignment.Right;
        cont.Click += (_, _) =>
        {
            S.TermsAcceptedVersion = Legal.Version;
            Growth.Track("terms_accepted", new() { ["version"] = Legal.Version });
            page = 1; Build();
        };
        stack.Children.Add(cont);
        return stack;
    }

    // MARK: - 1) アプリ・許可・サインイン・名前

    Control SetupPage(bool keepState)
    {
        var platform = app.Platform;
        var name = keepState && nameBox is not null ? nameBox.Text ?? "" : (S.UserName.Length == 0 ? platform.SuggestedUserName : S.UserName);
        var trusted = lastTrusted = platform.AccessibilityTrusted;
        lastRegistered = S.IsRegistered;
        var stack = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        stack.Children.Add(Hero(2, 3, L("onboarding.title"), L("onboarding.hero.subtitle"), S, () => Build(keepState: true)));

        // 普段使うアプリ（付録CF-8）
        Button? next = null;
        void RefreshNext()
        {
            if (next is null) return;
            next.IsEnabled = (trusted || skipped || !platform.NeedsAccessibilitySetup) && NameValue.Length > 0 && selected.Count > 0;
        }
        var appsBody = new StackPanel { Spacing = 8 };
        appsBody.Children.Add(Cap(L("onboarding.apps.body")));
        var detectRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        if (detectedNote && detected.Count > 0) detectRow.Children.Add(new TextBlock { Text = "✦ " + L("onboarding.apps.detected", detected.Count), FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#16A34A")), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var redetect = new Button { Content = L("onboarding.apps.redetect"), FontSize = 12, Padding = new Thickness(8, 3) };
        redetect.Click += (_, _) => { AutoDetect(replace: false); Build(keepState: true); };
        Grid.SetColumn(redetect, 1); detectRow.Children.Add(redetect);
        appsBody.Children.Add(detectRow);
        var chips = new WrapPanel { Orientation = Orientation.Horizontal };
        void RenderChips()
        {
            chips.Children.Clear();
            foreach (var a in AppCatalog.Entries(selected.OrderBy(x => x, StringComparer.Ordinal)))
            {
                var b = new Button { Classes = { "chip", "on", "pill" }, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 5) };
                b.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { AppLogo(a, 14), new TextBlock { Text = a.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = "✕", FontSize = 10, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center } } };
                b.Click += (_, _) => { selected.Remove(a.Id); SaveSelection(); Build(keepState: true); };
                chips.Children.Add(b);
            }
            chips.IsVisible = chips.Children.Count > 0;
        }
        RenderChips();
        appsBody.Children.Add(chips);
        queryBox = new TextBox { Text = appQuery, PlaceholderText = L("onboarding.apps.search") };
        var grid = new WrapPanel { Orientation = Orientation.Horizontal };
        var none = Cap(L("onboarding.apps.none"));
        void RenderGrid()
        {
            grid.Children.Clear();
            var shown = AppCatalog.Filter(appQuery, appCategory);
            foreach (var a in shown)
            {
                var on = selected.Contains(a.Id);
                var b = new Button { Classes = { "chip" }, Width = 148, Margin = new Thickness(0, 0, 6, 6) };
                if (on) b.Classes.Add("on");
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 6 };
                var logo = AppLogo(a, 16); Grid.SetColumn(logo, 0);
                var label = new TextBlock { Text = a.Name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(label, 1);
                row.Children.Add(logo); row.Children.Add(label);
                if (on) { var check = new TextBlock { Text = "✓", FontSize = 12, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(check, 2); row.Children.Add(check); }
                b.Content = row;
                b.Click += (_, _) =>
                {
                    if (!selected.Add(a.Id)) selected.Remove(a.Id);
                    SaveSelection(); RenderChips(); RenderGrid(); RefreshNext();
                };
                grid.Children.Add(b);
            }
            none.IsVisible = shown.Count == 0;
        }
        queryBox.TextChanged += (_, _) => { appQuery = queryBox.Text ?? ""; RenderGrid(); };
        appsBody.Children.Add(queryBox);
        var cats = new WrapPanel { Orientation = Orientation.Horizontal };
        void RenderCats()
        {
            cats.Children.Clear();
            foreach (var c in new AppCategory?[] { null, AppCategory.Mail, AppCategory.TeamChat, AppCategory.BusinessChat, AppCategory.Messaging, AppCategory.Sns, AppCategory.Support })
            {
                var key = c is { } cc ? AppCatalog.All.First(e => e.Category == cc).CategoryKey : "all";
                var b = new Button { Content = L("apps.category." + key), Classes = { "chip", "pill" }, Margin = new Thickness(0, 0, 6, 6) };
                if (appCategory == c) b.Classes.Add("on");
                b.Click += (_, _) => { appCategory = c; RenderCats(); RenderGrid(); };
                cats.Children.Add(b);
            }
        }
        RenderCats();
        appsBody.Children.Add(cats);
        RenderGrid();
        appsBody.Children.Add(new Border { Height = 220, ClipToBounds = true, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Colors.Black, 0.03), Padding = new Thickness(6, 6, 0, 0), Child = new ScrollViewer { Content = grid } });
        appsBody.Children.Add(none);
        appsBody.Children.Add(Cap(L("onboarding.apps.other")));
        stack.Children.Add(Card(Step(L("onboarding.apps.title"), selected.Count == 0 ? Icons.Circle : Icons.CheckCircle, selected.Count == 0 ? "#9CA3AF" : "#16A34A", appsBody)));

        // 読み取りの許可（Linux の AT-SPI。Windows は許可が要らない）
        if (platform.NeedsAccessibilitySetup)
        {
            var allow = new Button { Content = L("settings.accessibility.open"), IsVisible = !trusted };
            allow.Click += (_, _) => platform.RequestAccessibility();
            stack.Children.Add(Card(Step(L("onboarding.step1.title"), trusted ? Icons.CheckCircle : Icons.Alert, trusted ? "#16A34A" : "#D97706", Col(Cap(L("onboarding.step1.body")), allow))));
        }

        // サインイン
        Control connectBody;
        if (S.IsRegistered)
        {
            var col = Col(new TextBlock { Text = L("settings.registered", S.OrganizationName ?? ""), FontSize = 13, Foreground = OnboardingStyle.Text });
            if (S.Entitlement?.Status == "trial" && S.Entitlement.Trial is { } trial) col.Children.Add(new TextBlock { Text = L("onboarding.trial_started", trial.DaysLeft), FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#16A34A")) });
            connectBody = col;
        }
        else
        {
            var signIn = GradientButton(L("settings.sign_in"));
            signIn.Click += (_, _) => { S.UserName = NameValue; Growth.Track("sign_in_opened", new() { ["origin"] = "onboarding" }); AutoConnect.OpenSignIn(S, platform); };
            var change = new Button { Content = L("onboarding.step2.change_server"), Classes = { "link" } };
            change.Click += (_, _) => app.OpenSettings();
            connectBody = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { signIn, change } };
        }
        stack.Children.Add(Card(Step(L("onboarding.step2.title"), S.IsRegistered ? Icons.CheckCircle : Icons.Circle, S.IsRegistered ? "#16A34A" : "#9CA3AF", Col(connectBody, Cap(L("onboarding.step2.body"))))));

        // 名前（必須）
        nameBox = new TextBox { Text = name, PlaceholderText = L("settings.user_name"), MaxWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
        var nameHint = Cap("");
        void RefreshName()
        {
            var empty = NameValue.Length == 0;
            nameHint.Text = empty ? L("onboarding.name.required") : L("onboarding.name.body");
            nameHint.Foreground = empty ? new SolidColorBrush(Color.Parse("#D97706")) : SecondaryText;
            RefreshNext();
        }
        nameBox.TextChanged += (_, _) => RefreshName();
        stack.Children.Add(Card(Step(L("onboarding.name.title"), NameValue.Length == 0 ? Icons.Alert : Icons.CheckCircle, NameValue.Length == 0 ? "#D97706" : "#16A34A", Col(nameBox, nameHint))));

        var skip = new Button { Content = L("onboarding.skip"), Classes = { "link" }, IsEnabled = !trusted, IsVisible = platform.NeedsAccessibilitySetup };
        skip.Click += (_, _) => { skipped = true; RefreshNext(); };
        next = GradientButton(L("onboarding.next"));
        next.Click += (_, _) =>
        {
            S.UserName = NameValue;
            SaveSelection();
            Growth.Track("onboarding_completed", new() { ["skipped_accessibility"] = !trusted, ["name_set"] = S.UserName.Length > 0 });
            S.OnboardingDone = true;
            _ = S.PushDeviceProfile();
            page = 2; Build();
        };
        RefreshName();
        stack.Children.Add(new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { skip, next } });
        Grid.SetColumn(skip, 0); Grid.SetColumn(next, 2);
        return stack;
    }

    /// <summary>付録CF-9：この端末のアプリ・起動中のアプリ・読み取り済みの会話から自動で選ぶ。replace なら置き換え、そうでなければ足す。</summary>
    void AutoDetect(bool replace)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>();
        void Add(IEnumerable<AppCatalogEntry> entries) { foreach (var e in entries) if (seen.Add(e.Id)) ids.Add(e.Id); }
        try { Add(AppCatalog.MatchInstalled(app.Platform.InstalledAppNames())); } catch (Exception e) { Diag.Log("app detection failed " + e.GetType().Name); }
        try { Add(AppCatalog.MatchConversations(S.Conversations.Load().Select(c => (c.AppName, c.Platform)))); } catch (Exception) { }
        detected = AppCatalog.All.Select(a => a.Id).Where(seen.Contains).ToList();
        detectedNote = detected.Count > 0;
        if (replace) selected.Clear();
        foreach (var id in detected) selected.Add(id);
        SaveSelection();
        Growth.Track("apps_detected", new() { ["count"] = detected.Count });
    }

    void SaveSelection() => S.SelectedPlatforms = AppCatalog.All.Select(a => a.Id).Where(selected.Contains).ToList();

    void FinishAll() { _ = S.PushDeviceProfile(); Hide(); }

    static Grid Step(string title, Geometry icon, string color, Control content)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        var i = Icon_(icon, color);
        var col = Col(Head(title), content);
        Grid.SetColumn(i, 0); Grid.SetColumn(col, 1);
        g.Children.Add(i); g.Children.Add(col);
        return g;
    }
}

/// <summary>設定の「返し方 → 会話を見せる…」。初回設定の最後のステップと同じ画面を単独で開く（付録CF-6）。</summary>
public sealed class StyleWindow : Window
{
    readonly App app;
    public StyleWindow(App app)
    {
        this.app = app;
        Title = L("style.window_title");
        Width = 520; Height = 740; MinWidth = 480; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://ReplyFive/Assets/replyfive-64.png")));
        Bind(BackgroundProperty, this.GetResourceObservable("RfWindowBg"));
        Closing += (_, e) => { e.Cancel = true; Hide(); };
        app.Settings.PropertyChanged += (_, e) => { if (IsVisible && e.PropertyName == nameof(AppSettings.Appearance)) Dispatcher.UIThread.Post(Reload); };
    }

    /// <summary>開き直すたびに保存済みの内容から始める。</summary>
    public void Reload()
    {
        Title = L("style.window_title");
        Content = new StyleSetupView(app, finish: Hide);
    }
}
