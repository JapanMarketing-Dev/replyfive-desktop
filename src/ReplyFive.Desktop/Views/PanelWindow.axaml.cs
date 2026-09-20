using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using ReplyFive.Core;
using ReplyFive.Desktop.Platform;
using ReplyFive.Desktop.Services;
using ReplyFive.Desktop.ViewModels;
using static ReplyFive.Desktop.Services.L10n;

namespace ReplyFive.Desktop.Views;

/// <summary>返信パネル（付録BE）。画面下から出る 1 枚のカード。Return で生成→元の入力欄へ差し込み→閉じる。Alt+Return は先に確認。Esc で閉じる。
/// 取得→表示→通知の順序（付録AC）を守る。閉じたら会話文脈と生成文をメモリから捨てる。</summary>
public partial class PanelWindow : Window
{
    readonly ReplyViewModel vm;
    readonly Action openSettings;
    readonly Action openTrial;
    DateTimeOffset? shownAt;
    bool syncingResult;
    bool syncingIntent;
    readonly Brush orange = new SolidColorBrush(Color.Parse("#FF9F1C"));
    readonly Brush secondary = new SolidColorBrush(Color.Parse("#A9ABB5"));
    readonly Brush green = new SolidColorBrush(Color.Parse("#4ADE80"));

    public PanelWindow(ReplyViewModel vm, Action openSettings, Action openTrial)
    {
        this.vm = vm; this.openSettings = openSettings; this.openTrial = openTrial;
        InitializeComponent();
        AppIcon.Data = Icons.Bubble; ContactIcon.Data = Icons.Person; RecordsIcon.Data = Icons.Sparkles; GearIcon.Data = Icons.Gear;
        RegIcon.Data = Icons.Key; InactiveIcon.Data = Icons.Warning; VerifiedIcon.Data = Icons.Seal; HintIcon.Data = Icons.Bubble2;
        vm.PropertyChanged += OnVmChanged;
        vm.Settings.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        L10n.Changed += () => Dispatcher.UIThread.Post(Refresh);
        IntentBox.TextChanged += (_, _) => { if (!syncingIntent) { syncingIntent = true; vm.Intent = IntentBox.Text ?? ""; syncingIntent = false; } };
        ResultBox.TextChanged += (_, _) => { if (!syncingResult) { syncingResult = true; vm.Result = ResultBox.Text ?? ""; syncingResult = false; } };
        IntentBox.AddHandler(KeyDownEvent, OnIntentKey, RoutingStrategies.Tunnel);
        ResultBox.AddHandler(KeyDownEvent, OnResultKey, RoutingStrategies.Tunnel);
        ContactButton.Click += (_, _) => vm.ShowContext = !vm.ShowContext;
        SettingsButton.Click += (_, _) => openSettings();
        SignInButton.Click += (_, _) => openTrial();
        OpenSettingsButton.Click += (_, _) => openSettings();
        InactiveBillingButton.Click += (_, _) => vm.OpenBilling("inactive_banner");
        CopyButton.Click += (_, _) => vm.CopyResult();
        RegenerateButton.Click += (_, _) => vm.Generate();
        InsertButton.Click += (_, _) => vm.InsertResult();
        Candidate0.IsCheckedChanged += (_, _) => { if (Candidate0.IsChecked == true) vm.SelectedCandidate = 0; };
        Candidate1.IsCheckedChanged += (_, _) => { if (Candidate1.IsChecked == true) vm.SelectedCandidate = 1; };
        Deactivated += (_, _) => { if (IsVisible) HidePanel(); }; // 外をクリックしたら閉じる（macOS 版のマウス監視に相当）
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { HidePanel(); e.Handled = true; } };
        SizeChanged += (_, _) => Reposition();
        Refresh();
    }

    void OnIntentKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return; // 改行
            e.Handled = true;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) { Submit(); return; }
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) { vm.Generate(); return; }
            Submit();
        }
        else if (e.Key == Key.Escape) { HidePanel(); e.Handled = true; }
    }

    void OnResultKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { vm.InsertResult(); e.Handled = true; }
        else if (e.Key == Key.Escape) { HidePanel(); e.Handled = true; }
    }

    void Submit()
    {
        if (vm.IsAutoInserting) return;
        if (vm.HasResult && vm.Phase == Phase.Ready) vm.InsertResult();
        else if (vm.Settings.ReviewBeforeInsert) vm.Generate();
        else vm.GenerateAndInsert();
    }

    // MARK: - 表示

    (int x, int y)? cursor;

    public void ShowPanel(CapturedContext captured, (int x, int y)? cursorPosition)
    {
        vm.Present(captured);
        cursor = cursorPosition;
        _ = vm.Settings.RefreshEntitlement();
        shownAt = DateTimeOffset.UtcNow;
        if (!IsVisible) Show();
        Reposition();
        Activate();
        Dispatcher.UIThread.Post(() => { IntentBox.Focus(); Reposition(); }, DispatcherPriority.Loaded);
        Diag.Log("panel shown");
    }

    /// <summary>取得が 700ms を超えて後から届いたとき、開いているパネルの文脈だけを差し替える。入力中の文は保つ。</summary>
    public void UpdatePanel(CapturedContext captured) { if (IsVisible) vm.Update(captured); }

    public void HidePanel()
    {
        if (shownAt is { } t) Diag.Log($"panel hidden after {(int)(DateTimeOffset.UtcNow - t).TotalMilliseconds}ms");
        shownAt = null;
        vm.Reset(); // 会話文脈と生成文をメモリから捨てる
        if (IsVisible) Hide();
    }

    /// <summary>マウスのある画面の下端中央（タスクバーのすぐ上）。中身に合わせて高さが変わっても下辺を固定する。</summary>
    void Reposition()
    {
        if (!IsVisible) return;
        Screen? screen = null;
        if (cursor is { } c) screen = Screens.ScreenFromPoint(new PixelPoint(c.x, c.y));
        screen ??= Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;
        var wa = screen.WorkingArea;
        var scale = screen.Scaling;
        var w = (int)Math.Round(Bounds.Width * scale);
        var h = (int)Math.Round(Bounds.Height * scale);
        if (w == 0 || h == 0) return;
        var x = wa.X + (wa.Width - w) / 2;
        var y = wa.Y + wa.Height - h + (int)(6 * scale); // 下余白（18）の分だけ下げ、突起がタスクバーのすぐ上に来る
        var p = new PixelPoint(x, Math.Max(wa.Y, y));
        if (Position != p) Position = p;
    }

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReplyViewModel.Result) && !syncingResult) { syncingResult = true; if (ResultBox.Text != vm.Result) ResultBox.Text = vm.Result; syncingResult = false; }
        if (e.PropertyName == nameof(ReplyViewModel.Intent) && !syncingIntent) { syncingIntent = true; if (IntentBox.Text != vm.Intent) IntentBox.Text = vm.Intent; syncingIntent = false; }
        Refresh();
    }

    void Refresh()
    {
        var s = vm.Settings;
        var ctx = vm.Context;
        var platform = vm.Platform_;
        AppNameText.Text = ctx?.AppName ?? "ReplyFive";
        var contact = !string.IsNullOrEmpty(ctx?.ContactName) ? ctx.ContactName : !string.IsNullOrEmpty(ctx?.WindowTitle) ? ctx.WindowTitle : L("panel.contact.unknown");
        ContactText.Text = contact;
        ContactText.Foreground = ctx?.ContactName is null ? secondary : Brushes.White;
        ContactIcon.Foreground = ContactText.Foreground;
        ToolTip.SetTip(ContactButton, L("panel.contact.help"));
        RecordsIcon.IsVisible = vm.LearnedForContact > 0;
        ToolTip.SetTip(RecordsIcon, L("panel.contact.records", vm.LearnedForContact));
        KeysHint.Text = L("panel.hint.keys");
        ToolTip.SetTip(SettingsButton, L("menu.settings"));
        IntentBox.PlaceholderText = L("panel.placeholder");
        IntentBox.Opacity = vm.IsAutoInserting ? 0.5 : 1;

        // 帯
        RegistrationBanner.IsVisible = !s.IsRegistered;
        RegText.Text = L("panel.banner.not_registered");
        SignInButton.Content = L("settings.sign_in");
        OpenSettingsButton.Content = L("panel.banner.open_settings");
        InactiveBanner.IsVisible = s.IsRegistered && s.Entitlement?.Status == "inactive";
        InactiveText.Text = L("panel.plan.inactive");
        InactiveBillingButton.Content = L("panel.open_billing");

        // 読み取った会話
        ContextPreview.IsVisible = vm.ShowContext && ctx is not null && ctx.Text.Length > 0;
        if (ContextPreview.IsVisible && ctx is not null)
        {
            ContextLabel.Text = ctx.Source == ContextSource.Window ? L("panel.captured.window") : L("panel.captured");
            ContextText.Text = ctx.Text;
            if (s.Policy?.ContextCaptureAllowed == false) { ContextNote.Text = L("panel.context.not_sent"); ContextNote.IsVisible = true; }
            else if (vm.StoredLines > 0) { ContextNote.Text = L("panel.context.stored", vm.StoredLines); ContextNote.IsVisible = true; }
            else ContextNote.IsVisible = false;
        }

        // 会話を読み取れなかったときの案内（付録BO）
        var showStatus = ctx is not null && ctx.Text.Length == 0;
        CaptureStatus.IsVisible = showStatus;
        if (showStatus && ctx is not null)
        {
            var notTrusted = ctx.Error == CaptureError.NotTrusted || !platform.AccessibilityTrusted;
            var screenNeeded = !notTrusted && ctx.Error == CaptureError.ScreenNotAllowed;
            var reading = !notTrusted && ctx.Error == CaptureError.Failed;
            CaptureTitle.Text = notTrusted ? L("panel.context.no_permission_title") : screenNeeded ? L("panel.context.screen_title", ctx.AppName ?? "") : reading ? L("panel.context.reading_title") : L("panel.context.none_title");
            CaptureBody.Text = notTrusted ? L("panel.context.no_permission") : screenNeeded ? L("panel.context.screen_body", ctx.AppName ?? "") : reading ? L("panel.context.reading_body") : L("panel.context.no_selection", s.HotKeyCombo.Display);
            CaptureIcon.Data = notTrusted ? Icons.Hand : screenNeeded ? Icons.Screen : reading ? Icons.Hourglass : Icons.EyeOff;
            CaptureIcon.Foreground = reading ? secondary : orange;
            CaptureStatus.Background = new SolidColorBrush(Color.Parse(reading ? "#0DFFFFFF" : "#1FFF9F1C"));
            CaptureAllowButton.IsVisible = notTrusted || screenNeeded;
            CaptureAllowButton.Content = L("panel.context.allow");
            CaptureAllowButton.Click -= OnAllowClick; CaptureAllowButton.Click += OnAllowClick;
        }

        // 結果欄
        var showResult = !vm.IsAutoInserting && (vm.HasResult || vm.Phase != Phase.Idle);
        ResultArea.IsVisible = showResult;
        if (showResult)
        {
            ResultLabel.Text = (vm.Phase == Phase.Ready ? L("panel.result.ready") : L("panel.result.draft")).ToUpperInvariant();
            ResultSpinner.IsVisible = vm.Phase == Phase.Drafting;
            VerifiedBadge.IsVisible = vm.Verified && vm.Phase == Phase.Ready;
            VerifiedText.Text = L("panel.verified");
            ToolTip.SetTip(VerifiedBadge, L("panel.verified.help"));
            MetaText.Text = vm.Meta;
            var cands = vm.Phase == Phase.Ready && vm.CandidateKinds.Count == 2;
            Candidates.IsVisible = cands;
            if (cands)
            {
                Candidate0.Content = ReplyViewModel.KindLabel(vm.CandidateKinds[0]);
                Candidate1.Content = ReplyViewModel.KindLabel(vm.CandidateKinds[1]);
                ToolTip.SetTip(Candidates, L("panel.candidates.help"));
                if (vm.SelectedCandidate == 0 && Candidate0.IsChecked != true) Candidate0.IsChecked = true;
                if (vm.SelectedCandidate == 1 && Candidate1.IsChecked != true) Candidate1.IsChecked = true;
            }
            ResultBox.Opacity = vm.Phase == Phase.Drafting ? 0.6 : 1;
            HintChip.IsVisible = vm.Hint is not null && vm.Phase == Phase.Ready;
            HintText.Text = vm.Hint ?? "";
            WarningText.IsVisible = vm.WarningNote is not null;
            WarningText.Text = vm.WarningNote ?? "";
        }

        // 下段
        var trialDays = s.Entitlement?.Status == "trial" ? s.Entitlement.Trial?.DaysLeft : null;
        var showTrial = trialDays is { } d && d <= 7;
        var hasFooter = vm.IsAutoInserting || vm.Notice is not null || vm.Phase is Phase.Failed or Phase.Ready || showTrial;
        Footer.IsVisible = hasFooter;
        if (hasFooter)
        {
            FooterSpinner.IsVisible = false; FooterIcon.IsVisible = false; FooterActionButton.IsVisible = false;
            FooterText.Foreground = secondary;
            FooterActionButton.Click -= OnFooterAction;
            if (vm.IsAutoInserting) { FooterSpinner.IsVisible = true; FooterText.Text = L("panel.status.inserting"); }
            else if (vm.Phase == Phase.Failed && vm.Failure is { } e)
            {
                FooterIcon.IsVisible = true; FooterIcon.Data = Icons.Warning; FooterIcon.Foreground = orange;
                FooterText.Text = vm.ErrorMessage(e); FooterText.Foreground = Brushes.White;
                FooterActionButton.IsVisible = true;
                FooterActionButton.Content = e.RequiresReregistration ? L("panel.banner.open_settings") : e.SuggestsBilling ? L("panel.open_billing") : L("panel.retry");
                footerAction = e.RequiresReregistration ? openSettings : e.SuggestsBilling ? () => vm.OpenBilling("error") : () => vm.Generate();
                FooterActionButton.Click += OnFooterAction;
            }
            else if (vm.Notice is { } notice) { FooterText.Text = notice; FooterText.Foreground = green; }
            else if (showTrial)
            {
                FooterIcon.IsVisible = true; FooterIcon.Data = Icons.Sparkles; FooterIcon.Foreground = orange;
                FooterText.Text = trialDays == 0 ? L("panel.trial.ends_today") : L("panel.trial.days_left", trialDays!.Value); FooterText.Foreground = orange;
                FooterActionButton.IsVisible = true; FooterActionButton.Content = L("panel.open_billing");
                footerAction = () => vm.OpenBilling("panel_banner");
                FooterActionButton.Click += OnFooterAction;
                if (!trialShown) { trialShown = true; Growth.Track("trial_banner_shown", new() { ["days_left"] = trialDays.Value }); }
            }
            else if (vm.Phase == Phase.Ready) FooterText.Text = L("panel.hint.edit");
            else FooterText.Text = "";
            FooterButtons.IsVisible = vm.HasResult && vm.Phase == Phase.Ready;
            CopyButton.Content = L("panel.copy"); RegenerateButton.Content = L("panel.regenerate"); InsertButton.Content = L("panel.insert");
        }
        Dispatcher.UIThread.Post(Reposition, DispatcherPriority.Loaded);
    }

    bool trialShown;
    Action? footerAction;
    void OnFooterAction(object? sender, RoutedEventArgs e) => footerAction?.Invoke();
    void OnAllowClick(object? sender, RoutedEventArgs e)
    {
        var platform = vm.Platform_;
        if (!platform.AccessibilityTrusted) platform.RequestAccessibility(); else platform.RequestScreenText();
    }
}
