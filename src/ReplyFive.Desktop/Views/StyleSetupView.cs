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

/// <summary>付録CF-6：「会話を見せてください」（macOS 版 StyleSetupView.swift の移植）。初回設定の最後のステップとして、また設定の「返し方 → 会話を見せる…」から単独で開く。
/// 利用者が普段のチャット・メールアプリを開くと、ReplyFive が読み取った相手と、そこでのやり取り（相手の発言と自分の返信）が並ぶ。
/// 選んだアプリごとに「自分の返信入りの相手」が集まり、一定量に達したら完了。返し方は実際の返信から裏で判定し、画面には出さない。</summary>
public sealed class StyleSetupView : ScrollViewer
{
    readonly App app;
    AppSettings S => app.Settings;
    readonly Action finish;
    readonly int? stepOffset;
    readonly DispatcherTimer timer;
    List<ReviewCandidate> candidates = [];
    List<AppProgress> progress = [];
    bool finishing;

    public StyleSetupView(App app, Action finish, int? stepOffset = null)
    {
        this.app = app; this.finish = finish; this.stepOffset = stepOffset;
        Refresh(force: true);
        timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => { if (IsVisible) Refresh(); });
        AttachedToVisualTree += (_, _) => timer.Start();
        DetachedFromVisualTree += (_, _) => timer.Stop();
    }

    List<AppCatalogEntry> Selected => AppCatalog.Entries(S.SelectedPlatforms);
    bool Enough => OnboardingReview.IsEnough(progress);
    int OwnReplies => progress.Sum(p => p.WithOwnReply);

    void Refresh(bool force = false)
    {
        List<ConversationEntry> entries;
        try { entries = S.Conversations.Load(); } catch (Exception) { entries = []; }
        var fresh = OnboardingReview.Candidates(entries);
        var p = OnboardingReview.Progress(entries, Selected);
        var changed = force || !fresh.Select(c => c.Id).SequenceEqual(candidates.Select(c => c.Id))
                      || !fresh.SelectMany(c => c.Exchange.Select(m => m.Identity)).SequenceEqual(candidates.SelectMany(c => c.Exchange.Select(m => m.Identity)))
                      || !p.SequenceEqual(progress);
        if (!changed) return;
        candidates = fresh; progress = p;
        Diag.LogIfChanged($"onboarding show candidates={fresh.Count} own_replies={OwnReplies} enough={Enough} apps={progress.Count}");   // 本文は書かない
        Build();
    }

    void Build()
    {
        var stack = new StackPanel { Spacing = 14, Margin = new Thickness(24) };
        stack.Children.Add(Hero(stepOffset is { } so ? so + 1 : null, (stepOffset ?? 0) + 1, L("style.show.title"), L("style.show.intro"), S, Build));

        // 目安：選んだアプリごとの進み具合
        var goal = new StackPanel { Spacing = 10 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        var mark = Enough ? Icon_(Icons.CheckCircle, "#16A34A") : LiveDot(); Grid.SetColumn(mark, 0);
        var title = Head(Enough ? L("style.show.enough") : L("style.show.goal")); title.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(title, 1);
        var count = Cap(L("style.show.count", candidates.Count)); Grid.SetColumn(count, 2);
        head.Children.Add(mark); head.Children.Add(title); head.Children.Add(count);
        goal.Children.Add(head);
        goal.Children.Add(new ProgressBar { Minimum = 0, Maximum = OnboardingReview.EnoughContacts, Value = Math.Min(OwnReplies, OnboardingReview.EnoughContacts), Height = 6, Foreground = Gradient });
        foreach (var p in progress)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(Icon_(p.Done ? Icons.CheckCircle : Icons.Circle, p.Done ? "#16A34A" : "#9CA3AF"));
            row.Children.Add(AppBadge(p.App));
            row.Children.Add(Cap(p.Done ? L("style.show.app.done", p.WithOwnReply) : (p.Contacts > 0 ? L("style.show.app.partial", p.Contacts) : L("style.show.app.waiting"))));
            goal.Children.Add(row);
        }
        if (!S.IsRegistered) goal.Children.Add(new TextBlock { Text = L("style.show.signin"), FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#D97706")), TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(Card(goal));

        // 読み取った相手と、そこでのやり取り
        foreach (var c in candidates)
        {
            var card = new StackPanel { Spacing = 8 };
            var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
            var avatar = ContactAvatar(c.ContactName, c.Platform); Grid.SetColumn(avatar, 0);
            var info = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = c.ContactName ?? L("style.review.unknown_contact"), FontSize = 13, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = OnboardingStyle.Text });
            var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            meta.Children.Add(PlatformBadge(c.Platform, c.AppName));
            meta.Children.Add(new TextBlock { Text = L("style.show.contact.messages", c.Context.Count), FontSize = 11, Foreground = SecondaryText, VerticalAlignment = VerticalAlignment.Center });
            if (c.Exchange.Any(m => m.Role == "me")) meta.Children.Add(new TextBlock { Text = "↩ " + L("style.show.contact.replied"), FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#16A34A")), VerticalAlignment = VerticalAlignment.Center });
            info.Children.Add(meta);
            Grid.SetColumn(info, 1);
            top.Children.Add(avatar); top.Children.Add(info);
            card.Children.Add(top);
            var bubbles = new StackPanel { Spacing = 6 };
            foreach (var m in c.Exchange) bubbles.Children.Add(ChatBubble(m, c.ContactName, 120));
            card.Children.Add(bubbles);
            stack.Children.Add(Card(card));
        }
        if (candidates.Count == 0)
        {
            var empty = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            empty.Children.Add(LiveDot());
            empty.Children.Add(new TextBlock { Text = L("style.show.empty"), FontSize = 13, Foreground = SecondaryText, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            stack.Children.Add(Card(empty));
        }
        stack.Children.Add(Cap(L("style.show.apps")));
        stack.Children.Add(Cap(L("style.privacy")));

        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        var later = new Button { Content = L("style.later"), Classes = { "link" } };
        later.Click += (_, _) => { Growth.Track("style_setup_skipped"); finish(); };
        Grid.SetColumn(later, 0); buttons.Children.Add(later);
        if (!Enough && OwnReplies >= 1)
        {
            var early = new Button { Content = L("style.show.finish_early"), Classes = { "link" }, IsEnabled = !finishing };
            early.Click += (_, _) => Complete();
            Grid.SetColumn(early, 2); buttons.Children.Add(early);
        }
        var done = GradientButton(finishing ? L("style.show.finishing") : L("style.done"));
        done.IsEnabled = Enough && !finishing;
        done.Click += (_, _) => Complete();
        Grid.SetColumn(done, 3); buttons.Children.Add(done);
        stack.Children.Add(buttons);
        Content = stack;
    }

    /// <summary>完了：実際のやり取り（相手の発言 → 自分の返信）を style.bin に置き、返し方を裏で判定して保存する。画面には出さない。
    /// 判定できなくても完了にする（生成時は相手ごとの会話・修正例が優先されるので、判定は補助）。</summary>
    async void Complete()
    {
        if (finishing) return;
        finishing = true; Build();
        List<ConversationEntry> entries;
        try { entries = S.Conversations.Load(); } catch (Exception) { entries = []; }
        var samples = OnboardingReview.StyleSamples(entries);
        try
        {
            S.StyleSamples.DeleteAll();
            foreach (var s in samples) S.StyleSamples.Put(s.Scenario, s.Received, s.Reply);
        }
        catch (Exception e) { Diag.Log("style samples save failed " + e.GetType().Name); }
        Growth.Track("onboarding_conversations", new() { ["contacts"] = candidates.Count });
        Growth.Track("style_samples_saved", new() { ["count"] = samples.Count });
        var request = S.StyleSamples.Request();
        var source = "none";
        if (S.IsRegistered && request.Samples.Count > 0)
        {
            try
            {
                var res = await S.MakeClient().StyleProfile(request);
                if (res.Source != "none" && !res.Profile.Normalized.IsEmpty) { S.StyleProfile = res.Profile.Normalized; source = res.Source; }
            }
            catch (Exception e) { Diag.Log("style profile failed " + e.GetType().Name); }
        }
        Done(source);
    }

    void Done(string source)
    {
        S.StyleOnboardingDone = true;
        Growth.Track("style_profile_saved", new() { ["formality"] = S.StyleProfile?.Formality ?? "", ["length"] = S.StyleProfile?.Length ?? "", ["source"] = source });
        finishing = false;
        finish();
    }
}
