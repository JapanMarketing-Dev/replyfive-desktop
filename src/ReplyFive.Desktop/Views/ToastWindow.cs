using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace ReplyFive.Desktop.Views;

/// <summary>数秒で消える小さな通知（接続完了など）。画面上部中央。前面を奪わない。</summary>
public static class Toast
{
    static Window? current;

    public static void Show(string text, bool ok)
    {
        Dispatcher.UIThread.Post(() =>
        {
            current?.Close();
            var icon = new PathIcon { Data = ok ? Icons.CheckCircle : Icons.Warning, Width = 16, Height = 16, Foreground = new SolidColorBrush(ok ? Color.Parse("#4ADE80") : Color.Parse("#FF9F1C")), VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.Medium, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, VerticalAlignment = VerticalAlignment.Center };
            var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(14, 10, 16, 10), Children = { icon, label } };
            var border = new Border { Background = new SolidColorBrush(Color.Parse("#F0262730")), CornerRadius = new CornerRadius(12), BorderBrush = new SolidColorBrush(Color.Parse("#40FFFFFF")), BorderThickness = new Thickness(1), Child = stack, BoxShadow = BoxShadows.Parse("0 6 18 0 #66000000") };
            var w = new Window
            {
                Content = new Border { Padding = new Thickness(20), Child = border },
                WindowDecorations = WindowDecorations.None, Background = Brushes.Transparent, TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
                ShowInTaskbar = false, Topmost = true, CanResize = false, ShowActivated = false, SizeToContent = SizeToContent.WidthAndHeight, Title = "ReplyFive",
            };
            current = w;
            w.Show();
            var screen = w.Screens.Primary ?? w.Screens.All.FirstOrDefault();
            if (screen is not null)
            {
                var wa = screen.WorkingArea;
                var scale = w.RenderScaling;
                var width = (int)(w.Bounds.Width * scale);
                w.Position = new PixelPoint(wa.X + (wa.Width - width) / 2, wa.Y + (int)(8 * scale));
            }
            DispatcherTimer.RunOnce(() => { if (current == w) { w.Close(); current = null; } }, TimeSpan.FromSeconds(ok ? 2.8 : 5.0));
        });
    }
}

/// <summary>接続先の確認（別サーバへの接続リンクを開いたとき）。</summary>
public sealed class ConfirmWindow : Window
{
    public event Action? Confirmed;

    public ConfirmWindow(string title, string body, string accept, string cancel)
    {
        Title = title; Width = 460; SizeToContent = SizeToContent.Height; CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterScreen; Topmost = true;
        var ok = new Button { Content = accept, Classes = { "primary" }, Padding = new Thickness(16, 6) };
        var no = new Button { Content = cancel, Padding = new Thickness(16, 6) };
        ok.Click += (_, _) => { Confirmed?.Invoke(); Close(); };
        no.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 14,
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, FontSize = 13 },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { no, ok } },
            },
        };
    }
}
