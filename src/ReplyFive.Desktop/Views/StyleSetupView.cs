using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ReplyFive.Core;
using ReplyFive.Desktop.Services;
using static ReplyFive.Desktop.Services.L10n;
using static ReplyFive.Desktop.Views.SettingsWindow;

namespace ReplyFive.Desktop.Views;

/// <summary>付録CD：「返し方」の収集と最適化。
/// 1) 3 つの場面（顧客メール・同僚チャット・取引先チャット）に、利用者が自分の言葉で返信を書く（サインイン済みなら AI の案から直せる）
/// 2) その返信から文体のラベルを Jev で判定し、利用者が確認・調整して保存する。プレビューで生成を試せる。</summary>
public sealed class StyleSetupView : ScrollViewer
{
    readonly App app;
    AppSettings S => app.Settings;
    readonly Action finish;
    public sealed record Scenario(string Id, Core.Platform Platform, RecipientType Recipient)
    {
        public string Title => L($"style.scenario.{Id}.title");
        public string Received => L($"style.scenario.{Id}.received");
        public string Hint => L($"style.scenario.{Id}.hint");
    }
    public static readonly Scenario[] Scenarios =
    [
        new("email_customer", Core.Platform.Gmail, RecipientType.Customer),
        new("chat_colleague", Core.Platform.Slack, RecipientType.Internal),
        new("chat_partner", Core.Platform.Chatwork, RecipientType.Partner),
    ];

    readonly string[] replies = new string[Scenarios.Length];
    StyleProfile profile = new();
    Dictionary<string, double> confidence = [];
    string? profileStatus;
    bool judging;
    string? preview;
    bool previewBusy;

    public StyleSetupView(App app, Action finish)
    {
        this.app = app; this.finish = finish;
        for (var i = 0; i < replies.Length; i++) replies[i] = "";
        var saved = S.StyleSamples.Load();
        for (var i = 0; i < Scenarios.Length; i++) if (saved.FirstOrDefault(s => s.Scenario == Scenarios[i].Id) is { } s) replies[i] = s.Reply;
        if (S.StyleProfile is { } p) profile = p.Clone();
        BuildSamples();
    }

    // MARK: - 1) 返し方の収集

    int FilledCount => replies.Count(r => r.Trim().Length > 0);

    void BuildSamples()
    {
        var stack = new StackPanel { Spacing = 16, Margin = new Thickness(24) };
        stack.Children.Add(new TextBlock { Text = L("style.title"), FontSize = 20, FontWeight = FontWeight.SemiBold });
        stack.Children.Add(new TextBlock { Text = L("style.intro"), FontSize = 13, Foreground = new SolidColorBrush(Color.Parse("#6B7280")), TextWrapping = TextWrapping.Wrap });
        var errorText = new TextBlock { Classes = { "caption" }, Foreground = Brushes.Red, IsVisible = false };
        Button? next = null;
        for (var index = 0; index < Scenarios.Length; index++)
        {
            var i = index;
            var sc = Scenarios[i];
            var box = new TextBox { Text = replies[i], AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72 };
            var count = Cap(L("style.reply.count", replies[i].Trim().Length));
            box.TextChanged += (_, _) => { replies[i] = box.Text ?? ""; count.Text = L("style.reply.count", replies[i].Trim().Length); if (next is not null) next.IsEnabled = FilledCount > 0; };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            if (S.IsRegistered)
            {
                var suggest = new Button { Content = L("style.suggest"), FontSize = 12 };
                suggest.Click += async (_, _) =>
                {
                    suggest.IsEnabled = false; suggest.Content = L("style.suggest.busy"); errorText.IsVisible = false;
                    var req = new FormatRequest(sc.Platform, sc.Recipient, S.DefaultTone, sc.Hint, new ConversationContext(ContextSource.SelectedText, null, sc.Received), [], S.ClientInfo, S.OutputLanguage,
                        S.SenderForRequest is { } sender ? new FormatRequest.SenderInfo(sender.Name) : null);
                    try { var res = await S.MakeClient().Format(req); box.Text = res.Text; }
                    catch (ApiException e) { errorText.Text = L("style.suggest.failed", e.Code); errorText.IsVisible = true; }
                    catch (Exception) { errorText.Text = L("style.suggest.failed", "unreachable"); errorText.IsVisible = true; }
                    suggest.IsEnabled = true; suggest.Content = L("style.suggest");
                };
                Grid.SetColumn(suggest, 0); row.Children.Add(suggest);
            }
            Grid.SetColumn(count, 2); row.Children.Add(count);
            stack.Children.Add(Col(
                Head(sc.Title),
                new Border { Background = new SolidColorBrush(Color.Parse("#0F000000")), CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Child = new TextBlock { Text = sc.Received, FontSize = 13, TextWrapping = TextWrapping.Wrap } },
                Cap(L("style.reply.prompt", sc.Hint)),
                box, row));
        }
        stack.Children.Add(errorText);
        stack.Children.Add(Cap(L("style.privacy")));
        var later = new Button { Content = L("style.later"), Classes = { "link" } };
        later.Click += (_, _) => { Growth.Track("style_setup_skipped"); finish(); };
        next = new Button { Content = L("style.next"), Classes = { "primary" }, Padding = new Thickness(18, 6), IsEnabled = FilledCount > 0 };
        next.Click += (_, _) => ToOptimize();
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { later, next } };
        Grid.SetColumn(later, 0); Grid.SetColumn(next, 2);
        stack.Children.Add(footer);
        Content = stack;
    }

    void ToOptimize()
    {
        for (var i = 0; i < Scenarios.Length; i++)
        {
            var reply = replies[i].Trim();
            if (reply.Length == 0) continue;
            try { S.StyleSamples.Put(Scenarios[i].Id, Scenarios[i].Received, reply); } catch (Exception) { }
        }
        Growth.Track("style_samples_saved", new() { ["count"] = FilledCount });
        BuildOptimize();
        Judge();
    }

    // MARK: - 2) 最適化

    TextBlock? statusText;
    ProgressBar? statusSpinner;
    readonly Dictionary<string, List<RadioButton>> fieldButtons = [];
    readonly Dictionary<string, TextBlock> confidenceTexts = [];
    TextBlock? previewText;
    Border? previewBox;
    Button? previewButton;
    Button? doneButton;

    void BuildOptimize()
    {
        var stack = new StackPanel { Spacing = 14, Margin = new Thickness(24) };
        stack.Children.Add(new TextBlock { Text = L("style.optimize.title"), FontSize = 20, FontWeight = FontWeight.SemiBold });
        stack.Children.Add(new TextBlock { Text = L("style.optimize.intro"), FontSize = 13, Foreground = new SolidColorBrush(Color.Parse("#6B7280")), TextWrapping = TextWrapping.Wrap });
        statusSpinner = new ProgressBar { IsIndeterminate = true, Width = 60, Height = 4, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
        statusText = Cap("");
        stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { statusSpinner, statusText } });
        fieldButtons.Clear(); confidenceTexts.Clear();
        foreach (var field in StyleProfile.Fields)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(new TextBlock { Text = L("style.field." + field), FontSize = 13, FontWeight = FontWeight.SemiBold });
            var conf = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#6B7280")), VerticalAlignment = VerticalAlignment.Center };
            confidenceTexts[field] = conf;
            header.Children.Add(conf);
            var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var buttons = new List<RadioButton>();
            foreach (var option in StyleProfile.Vocabulary[field])
            {
                var rb = new RadioButton { Content = L($"style.value.{field}.{option}"), GroupName = "style-" + field, Tag = option, FontSize = 13 };
                rb.IsCheckedChanged += (_, _) => { if (rb.IsChecked == true) profile.Set(field, option); };
                buttons.Add(rb);
                options.Children.Add(rb);
            }
            fieldButtons[field] = buttons;
            stack.Children.Add(Col(header, options));
        }
        stack.Children.Add(new Separator());
        previewButton = new Button { Content = L("style.preview.button"), IsEnabled = S.IsRegistered };
        previewButton.Click += (_, _) => RunPreview();
        stack.Children.Add(SettingsWindow.Row(previewButton, Cap(L("style.preview.hint"))));
        previewText = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
        previewBox = new Border { Background = new SolidColorBrush(Color.Parse("#143B6CFF")), CornerRadius = new CornerRadius(8), Padding = new Thickness(10), IsVisible = false, Child = Col(Cap(Scenarios[0].Received), new SelectableTextBlock { Text = "", FontSize = 13, TextWrapping = TextWrapping.Wrap, Name = "PreviewBody" }) };
        stack.Children.Add(previewBox);
        var back = new Button { Content = L("style.back"), Classes = { "link" } };
        back.Click += (_, _) => BuildSamples();
        doneButton = new Button { Content = L("style.done"), Classes = { "primary" }, Padding = new Thickness(18, 6) };
        doneButton.Click += (_, _) => Save();
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { back, doneButton } };
        Grid.SetColumn(back, 0); Grid.SetColumn(doneButton, 2);
        stack.Children.Add(footer);
        Content = stack;
        ApplyProfileToButtons();
    }

    void ApplyProfileToButtons()
    {
        foreach (var (field, buttons) in fieldButtons)
        {
            var value = profile.Get(field);
            foreach (var b in buttons) b.IsChecked = (string?)b.Tag == value;
            confidenceTexts[field].Text = confidence.TryGetValue(field, out var c) && c > 0 ? L("style.field.confidence", (int)Math.Round(c * 100)) : "";
        }
        if (statusText is not null) statusText.Text = profileStatus ?? "";
        if (statusSpinner is not null) statusSpinner.IsVisible = judging;
        if (doneButton is not null) doneButton.IsEnabled = !judging;
    }

    static readonly StyleProfile Fallback = new() { Formality = "standard", Length = "medium", Greeting = "light", Closing = "light", Emoji = "none" };

    /// <summary>保存済みのサンプルから Jev の判定を取る。サインインしていない・失敗したときは既定値を置き、利用者が選ぶ。</summary>
    void Judge()
    {
        var request = S.StyleSamples.Request();
        if (!S.IsRegistered || request.Samples.Count == 0)
        {
            profile = (S.StyleProfile ?? Fallback).Clone();
            profileStatus = L("style.optimize.status.offline");
            ApplyProfileToButtons();
            return;
        }
        judging = true;
        profileStatus = L("style.optimize.status.judging");
        ApplyProfileToButtons();
        var client = S.MakeClient();
        _ = Task.Run(async () =>
        {
            StyleProfileResponse? res = null;
            try { res = await client.StyleProfile(request); } catch (Exception) { }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                judging = false;
                var judged = res?.Profile.Normalized;
                if (res is null || res.Source == "none" || judged is null || judged.IsEmpty)
                {
                    profile = (S.StyleProfile ?? Fallback).Clone();
                    profileStatus = L("style.optimize.status.failed");
                }
                else
                {
                    profile = new StyleProfile { Formality = judged.Formality ?? Fallback.Formality, Length = judged.Length ?? Fallback.Length, Greeting = judged.Greeting ?? Fallback.Greeting, Closing = judged.Closing ?? Fallback.Closing, Emoji = judged.Emoji ?? Fallback.Emoji };
                    confidence = res.Confidence ?? [];
                    profileStatus = L("style.optimize.status.done");
                    Growth.Track("style_profile_judged", new() { ["source"] = res.Source });
                }
                ApplyProfileToButtons();
            });
        });
    }

    void RunPreview()
    {
        if (previewBusy || previewButton is null) return;
        previewBusy = true;
        previewButton.IsEnabled = false; previewButton.Content = L("style.preview.busy");
        var sc = Scenarios[0];
        var sender = new FormatRequest.SenderInfo(S.SenderForRequest?.Name ?? "", profile.Normalized);
        var req = new FormatRequest(sc.Platform, sc.Recipient, S.DefaultTone, sc.Hint, new ConversationContext(ContextSource.SelectedText, null, sc.Received), [], S.ClientInfo, S.OutputLanguage, sender);
        var client = S.MakeClient();
        _ = Task.Run(async () =>
        {
            string text;
            try { text = (await client.Format(req)).Text; } catch (Exception) { text = L("style.preview.failed"); }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                previewBusy = false;
                previewButton.IsEnabled = S.IsRegistered; previewButton.Content = L("style.preview.button");
                preview = text;
                if (previewBox?.Child is StackPanel sp && sp.Children.OfType<SelectableTextBlock>().FirstOrDefault() is { } body) body.Text = text;
                if (previewBox is not null) previewBox.IsVisible = true;
            });
        });
    }

    void Save()
    {
        S.StyleProfile = profile.Normalized;
        S.StyleOnboardingDone = true;
        Growth.Track("style_profile_saved", new() { ["formality"] = profile.Formality ?? "", ["length"] = profile.Length ?? "" });
        finish();
    }
}
