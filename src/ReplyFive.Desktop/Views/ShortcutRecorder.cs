using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ReplyFive.Desktop.Services;
using static ReplyFive.Desktop.Services.L10n;

namespace ReplyFive.Desktop.Views;

/// <summary>設定画面のショートカット記録。「変更…」を押すと次に押したキーの組み合わせを割り当てる。記録中はグローバルショートカットが外れる。</summary>
public sealed class ShortcutRecorder : StackPanel
{
    readonly AppSettings settings;
    readonly Border chip = new() { Background = new SolidColorBrush(Color.Parse("#14000000")), CornerRadius = new CornerRadius(6), Padding = new Thickness(9, 4), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock chipText = new() { FontSize = 13, FontWeight = FontWeight.SemiBold };
    readonly TextBlock status = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    readonly Button change = new();
    readonly Button reset = new();
    readonly Button cancel = new();
    readonly TextBlock hint = new() { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 };
    bool needsModifier;
    Window? window;

    public ShortcutRecorder(AppSettings settings)
    {
        this.settings = settings;
        Spacing = 4;
        chip.Child = chipText;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { chip, status, change, reset, cancel } };
        Children.Add(row);
        Children.Add(hint);
        change.Click += (_, _) => Start();
        reset.Click += (_, _) => settings.HotKeyCombo = HotKeyCombo.Default;
        cancel.Click += (_, _) => Stop();
        settings.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(AppSettings.HotKey) or nameof(AppSettings.IsRecordingHotKey) or nameof(AppSettings.HotKeyRegistered)) Refresh(); };
        L10n.Changed += Refresh;
        DetachedFromVisualTree += (_, _) => Stop();
        Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        window = TopLevel.GetTopLevel(this) as Window;
        if (window is not null) window.Deactivated += OnDeactivated;
    }

    void OnDeactivated(object? sender, EventArgs e) => Stop();

    void Refresh()
    {
        chipText.Text = settings.HotKeyCombo.Display;
        var recording = settings.IsRecordingHotKey;
        status.Text = recording ? L("settings.shortcut.recording") : "";
        status.Foreground = new SolidColorBrush(Color.Parse("#3B6CFF"));
        status.IsVisible = recording;
        change.Content = L("settings.shortcut.record"); change.IsVisible = !recording;
        reset.Content = L("settings.shortcut.reset"); reset.IsVisible = !recording && settings.HotKeyCombo != HotKeyCombo.Default;
        cancel.Content = L("settings.shortcut.cancel"); cancel.IsVisible = recording;
        if (needsModifier) { hint.Text = L("settings.shortcut.need_modifier"); hint.Foreground = new SolidColorBrush(Color.Parse("#D97706")); }
        else if (!settings.HotKeyRegistered) { hint.Text = L("settings.shortcut.unavailable"); hint.Foreground = new SolidColorBrush(Color.Parse("#D97706")); }
        else { hint.Text = L("settings.shortcut.hint"); hint.Foreground = new SolidColorBrush(Color.Parse("#6B7280")); }
    }

    void Start()
    {
        if (settings.IsRecordingHotKey) return;
        needsModifier = false;
        settings.IsRecordingHotKey = true;
        window?.AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Refresh();
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key == Key.Escape) { Stop(); return; }
        var combo = HotKeyCombo.FromKeyEvent(e.Key, e.KeyModifiers);
        if (combo is { } c) { settings.HotKeyCombo = c; Stop(); }
        else { needsModifier = true; Refresh(); }
    }

    void Stop()
    {
        window?.RemoveHandler(KeyDownEvent, OnKey);
        if (settings.IsRecordingHotKey) settings.IsRecordingHotKey = false;
        Refresh();
    }
}
