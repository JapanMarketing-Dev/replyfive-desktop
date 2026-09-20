using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ReplyFive.Core;
using ReplyFive.Desktop.Services;
using static ReplyFive.Desktop.Services.L10n;

namespace ReplyFive.Desktop.Views;

/// <summary>付録CF-5／CF-7：初回設定の見た目（macOS 版 OnboardingStyle.swift の移植）。アプリアイコンの青→紫のグラデーション、ステップの点、
/// 相手のアバター、チャットの吹き出し、読み取り中の点、グラデーションのボタン。色はライト／ダークで App.axaml の資源から取る。</summary>
public static class OnboardingStyle
{
    public static IBrush Res(string key) => Application.Current?.TryFindResource(key, Application.Current.ActualThemeVariant, out var v) == true && v is IBrush b ? b : Brushes.Gray;
    public static IBrush Gradient => Res("RfGradient");
    public static IBrush SecondaryText => Res("RfSecondaryText");
    public static IBrush Text => Res("RfText");
    public static IBrush CardFill => Res("RfCardFill");
    public static IBrush CardStroke => Res("RfCardStroke");
    public static IBrush ChipFill => Res("RfChipFill");
    public static IBrush WindowBg => Res("RfWindowBg");

    /// <summary>アプリごとの識別色（各社のブランド色に寄せる）</summary>
    public static Color PlatformColor(Core.Platform p) => p switch
    {
        Core.Platform.Slack => Color.FromRgb(140, 56, 143),
        Core.Platform.Teams => Color.FromRgb(97, 99, 166),
        Core.Platform.Gmail => Color.FromRgb(217, 64, 56),
        Core.Platform.Outlook => Color.FromRgb(0, 115, 209),
        Core.Platform.Chatwork => Color.FromRgb(230, 64, 51),
        Core.Platform.Line or Core.Platform.Lineworks => Color.FromRgb(5, 184, 77),
        Core.Platform.Googlechat => Color.FromRgb(26, 140, 89),
        _ => Color.FromRgb(128, 128, 128),
    };

    public static Color CategoryColor(AppCategory c) => c switch
    {
        AppCategory.Mail => Color.FromRgb(217, 77, 64),
        AppCategory.TeamChat => Color.FromRgb(89, 102, 217),
        AppCategory.BusinessChat => Color.FromRgb(230, 115, 38),
        AppCategory.Messaging => Color.FromRgb(13, 166, 89),
        AppCategory.Sns => Color.FromRgb(191, 64, 166),
        _ => Color.FromRgb(51, 140, 191),
    };

    public static string Initials(string? name)
    {
        var t = (name ?? "").Trim();
        if (t.Length == 0) return "?";
        var first = t[0];
        if (char.IsAscii(first))
        {
            var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1 && parts[1].Length > 0) return (char.ToUpperInvariant(first).ToString() + char.ToUpperInvariant(parts[1][0])).ToString();
            return char.ToUpperInvariant(first).ToString();
        }
        return char.IsSurrogate(first) && t.Length > 1 ? t[..2] : first.ToString();
    }

    static readonly Dictionary<string, Bitmap?> logoCache = [];
    /// <summary>アプリの公式ロゴ（`Assets/apps/app-<id>.png`、サイトと同じ SVG から）。無ければ null。</summary>
    public static Bitmap? Logo(string id)
    {
        lock (logoCache)
        {
            if (logoCache.TryGetValue(id, out var cached)) return cached;
            Bitmap? bmp = null;
            try
            {
                var uri = new Uri("avares://ReplyFive/Assets/apps/app-" + id + ".png");
                if (AssetLoader.Exists(uri)) { using var s = AssetLoader.Open(uri); bmp = new Bitmap(s); }
            }
            catch (Exception) { }
            logoCache[id] = bmp;
            return bmp;
        }
    }

    /// <summary>ロゴ。無ければ色の角丸に頭文字。</summary>
    public static Control AppLogo(string id, double size, string? fallbackName, Color fallbackColor)
    {
        if (Logo(id) is { } bmp) return new Image { Source = bmp, Width = size, Height = size, Stretch = Stretch.Uniform };
        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(new Border { Background = new SolidColorBrush(fallbackColor), CornerRadius = new CornerRadius(size * 0.25) });
        if (fallbackName is not null) grid.Children.Add(new TextBlock { Text = Initials(fallbackName), FontSize = size * 0.5, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        return grid;
    }
    public static Control AppLogo(AppCatalogEntry app, double size) => AppLogo(app.Id, size, app.Name, CategoryColor(app.Category));
    public static Control AppLogo(Core.Platform p, double size) => AppLogo(p.Wire(), size, null, PlatformColor(p));

    /// <summary>ロゴ＋名前のバッジ</summary>
    public static Border Badge(Control logo, string text) => new()
    {
        Background = ChipFill, CornerRadius = new CornerRadius(999), Padding = new Thickness(7, 3), BorderBrush = CardStroke, BorderThickness = new Thickness(1),
        Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { logo, new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = Text } } },
    };
    public static Border AppBadge(AppCatalogEntry app) => Badge(AppLogo(app, 12), app.Name);
    public static Border PlatformBadge(Core.Platform p, string? appName) => Badge(AppLogo(p, 12), appName ?? p.Wire());

    /// <summary>相手のアバター（頭文字、アプリの色）と右下にアプリのロゴ</summary>
    public static Control ContactAvatar(string? name, Core.Platform p, double size = 36)
    {
        var c = PlatformColor(p);
        var grid = new Grid { Width = size + 4, Height = size + 4 };
        var circle = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        circle.Children.Add(new Ellipse { Fill = new LinearGradientBrush { StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative), GradientStops = { new GradientStop(c, 0), new GradientStop(Color.FromArgb(153, c.R, c.G, c.B), 1) } } });
        circle.Children.Add(new TextBlock { Text = Initials(name), FontSize = size * 0.4, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(circle);
        var logo = new Border { Background = CardFill, CornerRadius = new CornerRadius(999), Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Child = AppLogo(p, size * 0.42) };
        grid.Children.Add(logo);
        return grid;
    }

    /// <summary>吹き出し。相手は左（灰）、自分は右（グラデーション）。送信者名を上に出す。</summary>
    public static Control ChatBubble(ConversationMessage m, string? contactName, int maxChars = 160)
    {
        var isMe = m.Role == "me";
        var text = m.Text.Length > maxChars ? m.Text[..maxChars] + "…" : m.Text;
        var col = new StackPanel { Spacing = 3, HorizontalAlignment = isMe ? HorizontalAlignment.Right : HorizontalAlignment.Left, MaxWidth = 360 };
        col.Children.Add(new TextBlock { Text = isMe ? L("style.show.you") : (!string.IsNullOrEmpty(m.Sender) ? m.Sender : contactName ?? L("style.review.unknown_contact")), FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = SecondaryText, HorizontalAlignment = isMe ? HorizontalAlignment.Right : HorizontalAlignment.Left });
        col.Children.Add(new Border
        {
            Background = isMe ? Gradient : Res("RfBubbleOther"), CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 7),
            Child = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = isMe ? Brushes.White : Text },
        });
        return new Grid { Children = { col } };
    }

    /// <summary>読み取り中の脈打つ緑の点</summary>
    public static Control LiveDot()
    {
        var dot = new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94)), VerticalAlignment = VerticalAlignment.Center, Opacity = 1 };
        var on = false;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background, (_, _) => { on = !on; dot.Opacity = on ? 1 : 0.5; dot.Width = dot.Height = on ? 10 : 8; });
        dot.AttachedToVisualTree += (_, _) => timer.Start();
        dot.DetachedFromVisualTree += (_, _) => timer.Stop();
        return dot;
    }



    public static Button GradientButton(string text) => new() { Content = text, Classes = { "gradient" } };

    /// <summary>ステップの点（現在は長い）</summary>
    public static Control StepDots(int current, int total)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 1; i <= Math.Max(total, 1); i++)
            row.Children.Add(new Border { Width = i == current ? 22 : 8, Height = 8, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Colors.White, i <= current ? 1 : 0.35) });
        return row;
    }

    /// <summary>付録CF-7：外観の切り替え（OS に従う／ライト／ダーク）</summary>
    public static Control AppearancePicker(AppSettings settings, bool onDark, Action? changed = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (key, glyph) in new[] { ("system", "◐"), ("light", "☀"), ("dark", "☾") })
        {
            var on = settings.Appearance == key;
            var b = new Button
            {
                Content = new TextBlock { Text = glyph, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                Width = 26, Height = 22, Padding = new Thickness(0), CornerRadius = new CornerRadius(6),
                Background = on ? new SolidColorBrush(onDark ? Colors.White : Colors.Black, onDark ? 0.3 : 0.12) : Brushes.Transparent,
                Foreground = onDark ? Brushes.White : Text,
            };
            ToolTip.SetTip(b, L("appearance." + key));
            b.Click += (_, _) => { settings.Appearance = key; Growth.Track("settings_changed", new() { ["key"] = "appearance", ["value"] = key }); changed?.Invoke(); };
            row.Children.Add(b);
        }
        return new Border { Background = new SolidColorBrush(onDark ? Colors.White : Colors.Black, onDark ? 0.15 : 0.06), CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Child = row, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>上部の帯：グラデーション背景に見出しと説明、右上にステップの点と外観の切り替え。</summary>
    public static Control Hero(int? step, int total, string title, string subtitle, AppSettings? settings = null, Action? appearanceChanged = null)
    {
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 10 };
        if (step is { } s)
        {
            var dots = StepDots(s, total); Grid.SetColumn(dots, 0); top.Children.Add(dots);
            var page = new TextBlock { Text = L("onboarding.page", s, total), FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Colors.White, 0.85), VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(page, 2); top.Children.Add(page);
        }
        if (settings is not null) { var picker = AppearancePicker(settings, onDark: true, appearanceChanged); Grid.SetColumn(picker, 3); top.Children.Add(picker); }
        var stack = new StackPanel { Spacing = 8, Children = { top, new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeight.Bold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap }, new TextBlock { Text = subtitle, FontSize = 13, Foreground = new SolidColorBrush(Colors.White, 0.9), TextWrapping = TextWrapping.Wrap } } };
        var canvas = new Canvas { ClipToBounds = true };
        canvas.Children.Add(new Ellipse { Width = 220, Height = 220, Fill = new SolidColorBrush(Colors.White, 0.12) }); Canvas.SetLeft(canvas.Children[0], 330); Canvas.SetTop(canvas.Children[0], -130);
        canvas.Children.Add(new Ellipse { Width = 140, Height = 140, Fill = new SolidColorBrush(Colors.White, 0.08) }); Canvas.SetLeft(canvas.Children[1], -50); Canvas.SetTop(canvas.Children[1], 60);
        var grid = new Grid { Children = { canvas, new Border { Padding = new Thickness(20), Child = stack } } };
        return new Border { Background = Gradient, CornerRadius = new CornerRadius(16), ClipToBounds = true, Child = grid };
    }
}
