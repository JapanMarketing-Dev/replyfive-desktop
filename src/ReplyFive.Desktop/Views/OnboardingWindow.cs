using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ReplyFive.Desktop.Services;
using static ReplyFive.Desktop.Services.L10n;
using static ReplyFive.Desktop.Views.SettingsWindow;

namespace ReplyFive.Desktop.Views;

/// <summary>初回設定。1 ページ目：（Linux は読み取りの許可）・サインイン・名前（必須）・ショートカット・ログイン時起動。2 ページ目（付録CD）：返し方の収集と最適化。</summary>
public sealed class OnboardingWindow : Window
{
    readonly App app;
    AppSettings S => app.Settings;
    int page;
    readonly ContentControl host = new();
    readonly DispatcherTimer timer;
    TextBox? nameBox;
    bool skipped;

    public OnboardingWindow(App app)
    {
        this.app = app;
        Title = L("onboarding.title");
        Width = 520; Height = 720; MinWidth = 480; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://ReplyFive/Assets/replyfive-64.png")));
        Background = new SolidColorBrush(Color.Parse("#F7F8FB"));
        Content = host;
        Closing += (_, e) => { e.Cancel = true; Hide(); };
        Opened += (_, _) =>
        {
            page = S.OnboardingDone && !S.StyleOnboardingDone ? 1 : 0;
            Growth.Track("onboarding_shown");
            Build();
        };
        timer = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) => { if (IsVisible && page == 0) Build(keepName: true); });
        timer.Start();
        S.PropertyChanged += (_, e) => { if (IsVisible && page == 0 && e.PropertyName is nameof(AppSettings.IsRegistered) or nameof(AppSettings.Entitlement) or nameof(AppSettings.OrganizationName)) Dispatcher.UIThread.Post(() => Build(keepName: true)); };
    }

    public void Relocalize() { Title = L("onboarding.title"); if (IsVisible) Build(keepName: true); }

    string NameValue => (nameBox?.Text ?? "").Trim();

    void Build(bool keepName = false)
    {
        if (page == 1)
        {
            host.Content = new StyleSetupView(app, finish: () => { _ = S.PushDeviceProfile(); Hide(); });
            return;
        }
        var platform = app.Platform;
        var name = keepName && nameBox is not null ? nameBox.Text ?? "" : (S.UserName.Length == 0 ? platform.SuggestedUserName : S.UserName);
        var trusted = platform.AccessibilityTrusted;
        var stack = new StackPanel { Spacing = 18, Margin = new Thickness(24) };
        stack.Children.Add(new TextBlock { Text = L("onboarding.title"), FontSize = 20, FontWeight = FontWeight.SemiBold });
        stack.Children.Add(Cap(L("onboarding.page", 1, 2)));

        if (platform.NeedsAccessibilitySetup)
        {
            var allow = new Button { Content = L("settings.accessibility.open"), IsVisible = !trusted };
            allow.Click += (_, _) => platform.RequestAccessibility();
            stack.Children.Add(Step(L("onboarding.step1.title"), trusted ? Icons.CheckCircle : Icons.Alert, trusted ? "#16A34A" : "#D97706", Col(Cap(L("onboarding.step1.body")), allow)));
        }

        Control connectBody;
        if (S.IsRegistered)
        {
            var col = Col(new TextBlock { Text = L("settings.registered", S.OrganizationName ?? ""), FontSize = 13 });
            if (S.Entitlement?.Status == "trial" && S.Entitlement.Trial is { } trial) col.Children.Add(new TextBlock { Text = L("onboarding.trial_started", trial.DaysLeft), FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#16A34A")) });
            connectBody = col;
        }
        else
        {
            var signIn = new Button { Content = L("settings.sign_in"), Classes = { "primary" } };
            signIn.Click += (_, _) => { S.UserName = NameValue; Growth.Track("sign_in_opened", new() { ["origin"] = "onboarding" }); AutoConnect.OpenSignIn(S, platform); };
            var change = new Button { Content = L("onboarding.step2.change_server"), Classes = { "link" } };
            change.Click += (_, _) => app.OpenSettings();
            connectBody = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { signIn, change } };
        }
        stack.Children.Add(Step(L("onboarding.step2.title"), S.IsRegistered ? Icons.CheckCircle : Icons.Circle, S.IsRegistered ? "#16A34A" : "#9CA3AF", Col(connectBody, Cap(L("onboarding.step2.body")))));

        nameBox = new TextBox { Text = name, PlaceholderText = L("settings.user_name"), MaxWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
        var nameHint = Cap("");
        Button? next = null;
        void RefreshName()
        {
            var empty = NameValue.Length == 0;
            nameHint.Text = empty ? L("onboarding.name.required") : L("onboarding.name.body");
            nameHint.Foreground = new SolidColorBrush(Color.Parse(empty ? "#D97706" : "#6B7280"));
            if (next is not null) next.IsEnabled = !empty && (trusted || skipped || !platform.NeedsAccessibilitySetup);
        }
        nameBox.TextChanged += (_, _) => RefreshName();
        stack.Children.Add(Step(L("onboarding.name.title"), NameValue.Length == 0 ? Icons.Alert : Icons.CheckCircle, NameValue.Length == 0 ? "#D97706" : "#16A34A", Col(nameBox, nameHint)));

        stack.Children.Add(Step(L("onboarding.step3.title"), Icons.Keyboard, "#6B7280", Col(new ShortcutRecorder(S), Cap(L("onboarding.step3.body", S.HotKeyCombo.Display)))));

        var launchCol = new StackPanel { Spacing = 6 };
        if (platform.SupportsLaunchAtLogin)
        {
            var toggle = new ToggleSwitch { IsChecked = platform.LaunchAtLogin, OnContent = L("onboarding.step4.toggle"), OffContent = L("onboarding.step4.toggle") };
            toggle.IsCheckedChanged += (_, _) => { try { platform.LaunchAtLogin = toggle.IsChecked == true; } catch (Exception) { toggle.IsChecked = platform.LaunchAtLogin; } };
            launchCol.Children.Add(toggle);
        }
        launchCol.Children.Add(Cap(L("onboarding.records.body")));
        stack.Children.Add(Step(L("onboarding.step4.title"), Icons.Power, "#6B7280", launchCol));

        var skip = new Button { Content = L("onboarding.skip"), Classes = { "link" }, IsEnabled = !trusted && platform.NeedsAccessibilitySetup, IsVisible = platform.NeedsAccessibilitySetup };
        skip.Click += (_, _) => { skipped = true; RefreshName(); };
        next = new Button { Content = L("onboarding.next"), Classes = { "primary" }, Padding = new Thickness(18, 6) };
        next.Click += (_, _) =>
        {
            S.UserName = NameValue;
            Growth.Track("onboarding_completed", new() { ["skipped_accessibility"] = !trusted, ["name_set"] = S.UserName.Length > 0 });
            S.OnboardingDone = true;
            _ = S.PushDeviceProfile();
            page = 1;
            Build();
        };
        RefreshName();
        stack.Children.Add(new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { skip, next } });
        Grid.SetColumn(skip, 0); Grid.SetColumn(next, 2);
        host.Content = new ScrollViewer { Content = stack };
    }

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

/// <summary>付録CD：設定の「返し方を最適化…」。初回設定の 2 ページ目と同じ画面を単独で開く。</summary>
public sealed class StyleWindow : Window
{
    readonly App app;
    public StyleWindow(App app)
    {
        this.app = app;
        Title = L("style.window_title");
        Width = 520; Height = 720; MinWidth = 480; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://ReplyFive/Assets/replyfive-64.png")));
        Background = new SolidColorBrush(Color.Parse("#F7F8FB"));
        Closing += (_, e) => { e.Cancel = true; Hide(); };
    }

    /// <summary>開き直すたびに保存済みの内容から始める。</summary>
    public void Reload()
    {
        Title = L("style.window_title");
        Content = new StyleSetupView(app, finish: Hide);
    }
}
