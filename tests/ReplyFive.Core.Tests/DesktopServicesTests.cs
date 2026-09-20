using Avalonia.Input;
using ReplyFive.Desktop.Services;

namespace ReplyFive.Core.Tests;

/// <summary>デスクトップ側の OS 非依存な部品：文言ファイルの読み込みと書式、ショートカットの保存形式。</summary>
public class L10nTests
{
    [Fact]
    public void ParsesAppleStringsFormat()
    {
        var d = L10n.Parse("/* comment */\n\"a.b\" = \"Hello %@\";\n// line\n\"c\" = \"Line\\nBreak \\\"q\\\"\";\n");
        Assert.Equal("Hello %@", d["a.b"]);
        Assert.Equal("Line\nBreak \"q\"", d["c"]);
    }

    [Fact]
    public void FormatsPrintfStyle()
    {
        Assert.Equal("Connected to Acme. Press Ctrl+Shift+R in any app to start.", L10n.Format("Connected to %@. Press %@ in any app to start.", "Acme", "Ctrl+Shift+R"));
        Assert.Equal("3/10 devices", L10n.Format("%d/%d devices", 3, 10));
        Assert.Equal("80% · avg. 1.5 characters changed (7 replies, 30 days)", L10n.Format("%d%% · avg. %.1f characters changed (%d replies, 30 days)", 80, 1.5, 7));
        Assert.Equal("Step 1 of 2", L10n.Format("Step %d of %d", 1, 2));
        Assert.Equal("100%", L10n.Format("100%"));
    }

    [Fact]
    public void EmbeddedStringsResolveForEveryOsAndLanguage()
    {
        foreach (var os in new[] { "windows", "linux" })
            foreach (var lang in new[] { "en", "ja" })
            {
                L10n.Apply(lang, os);
                Assert.NotEqual("panel.placeholder", L10n.Get("panel.placeholder"));
                Assert.DoesNotContain("Mac", L10n.Get("panel.banner.not_registered"));
                Assert.DoesNotContain("⌘", L10n.Get("panel.notice.copied_paste"));
                Assert.DoesNotContain("Mac", L10n.Get("settings.records.count"));
                Assert.DoesNotContain("Mac", L10n.Get("onboarding.name.body"));
                Assert.True(L10n.Has("panel.status.ready"));
            }
        L10n.Apply("ja", "windows");
        Assert.Equal("Windows の設定に合わせる", L10n.Get("settings.ui_language.system"));
        L10n.Apply("en", "linux");
        Assert.Contains("AT-SPI", L10n.Get("settings.accessibility.title"));
    }

    [Fact]
    public void MacOnlyWordingIsOverriddenEverywhereItMatters()
    {
        // 基準（macOS 版）の文言のうち Mac・⌘・システム設定を含む鍵は、Windows / Linux の上書きで消えている
        L10n.Apply("en", "windows");
        var suspects = new[] { "this Mac", "⌘V", "System Settings" };
        foreach (var key in new[] { "panel.banner.not_registered", "panel.notice.copied_paste", "settings.conversations.body", "settings.records.body", "settings.privacy.body", "style.intro", "style.privacy", "update.ready.toast", "settings.shortcut.hint", "onboarding.step3.body", "error.unauthorized" })
            foreach (var s in suspects) Assert.False(L10n.Get(key).Contains(s, StringComparison.Ordinal), key + " still says " + s);
    }
}

public class HotKeyComboTests
{
    [Fact]
    public void StoresAndParses()
    {
        var c = new HotKeyCombo(Key.R, KeyModifiers.Control | KeyModifiers.Shift);
        Assert.Equal(HotKeyCombo.Default, c);
        Assert.Equal("combo:R:6", c.Stored);
        Assert.Equal(c, HotKeyCombo.Parse(c.Stored));
        Assert.Equal("Ctrl+Shift+R", c.Display);
        Assert.Equal(HotKeyCombo.Default, HotKeyCombo.Parse("ctrl_shift_r"));
        Assert.Null(HotKeyCombo.Parse("garbage"));
    }

    [Fact]
    public void RejectsModifierOnlyAndShiftOnly()
    {
        Assert.Null(HotKeyCombo.FromKeyEvent(Key.LeftShift, KeyModifiers.Shift));
        Assert.Null(HotKeyCombo.FromKeyEvent(Key.A, KeyModifiers.Shift));
        Assert.Null(HotKeyCombo.FromKeyEvent(Key.Escape, KeyModifiers.Control));
        Assert.NotNull(HotKeyCombo.FromKeyEvent(Key.F9, KeyModifiers.None));
        Assert.NotNull(HotKeyCombo.FromKeyEvent(Key.Space, KeyModifiers.Alt));
        Assert.Equal("Alt+Space", HotKeyCombo.FromKeyEvent(Key.Space, KeyModifiers.Alt)!.Value.Display);
    }
}
