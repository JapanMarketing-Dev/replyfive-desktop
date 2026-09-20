using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using ReplyFive.Desktop.Platform.Shared;
using ReplyFive.Desktop.Services;

namespace ReplyFive.Desktop.Platform.Windows;

/// <summary>取得した入力欄へだけ差し込む。Enter や送信のキーは決して送らない（守る制約）。
/// macOS 版 Paste.swift の移植：1) 値を直接書けるならそれ、2) 書けなければ対象アプリを前面にして Ctrl+V。
/// 直接書き込みが成功したあと、変化を確かめられないまま貼り付けへ進むと同じ返信が二重に入るので、そこでは必ず止める。</summary>
internal static class WinInsert
{
    enum DirectResult
    {
        Inserted,      // 値に反映された
        Unavailable,   // 値を書けない欄
        NoEffect,      // 書き込みは成功するが値が変わらない。Ctrl+V へ進んでよい
        Uncertain,     // 値が読めない・部分的に変わった。二重挿入を避けて止める
    }

    internal static async Task<bool> Insert(WindowsPlatform platform, string text, CapturedContext context)
    {
        var app = context.App;
        var automation = WinUia.Automation();
        if (app is null || automation is null) { platform.CopyToClipboard(text); return false; }

        var element = context.FocusedElement as AutomationElement;
        if (element is not null && WinUia.ProcessIdOf(element) == app.Pid && StillFocused(automation, element, app) && WinUia.IsEditable(element))
        {
            switch (Direct(element, text))
            {
                case DirectResult.Inserted: return true;
                case DirectResult.Uncertain: platform.CopyToClipboard(text); return false;
                default: break;   // Unavailable / NoEffect は貼り付けへ
            }
        }
        return await ViaClipboard(platform, automation, text, element, app).ConfigureAwait(false);
    }

    // MARK: - 直接書き込み

    static DirectResult Direct(AutomationElement element, string text)
    {
        var pattern = WinUia.ValuePatternOf(element);
        if (pattern is null || WinUia.ReadOnly(pattern)) return DirectResult.Unavailable;
        // UIA の値の書き込みは「差し込み」ではなく「置き換え」なので、今の中身が読めないまま書くと利用者の書きかけを消してしまう。
        // 読めないときは書かずに Ctrl+V へ回す
        var before = WinUia.ValueOf(element);
        if (before is null) return DirectResult.Unavailable;
        var selectedBefore = WinUia.SelectedText(element, WinAccessibility.MaxChars);
        // 選択範囲が読めればそこへ挟み、読めなければ末尾に足す（書きかけを消さない）
        var composed = Compose(element, before, text) ?? before + text;
        try { pattern.SetValue(composed); }
        catch (Exception) { return DirectResult.Unavailable; }
        var after = WinUia.ValueOf(element);
        if (after is null) return DirectResult.Uncertain;
        if (after.Contains(text, StringComparison.Ordinal) && (after != before || selectedBefore == text)) return DirectResult.Inserted;
        if (after == before) return DirectResult.NoEffect;
        return DirectResult.Uncertain;
    }

    /// <summary>カーソル位置（選択範囲）へ入れた値を組み立てる。UIA には「挿入」が無く値の差し替えしかないので、
    /// 選択範囲の前後を文字列として取り出して挟む。前後を継ぎ合わせても今の値と一致しないときは諦めて末尾に足す。</summary>
    static string? Compose(AutomationElement element, string? before, string text)
    {
        if (before is null) return null;
        var tp = WinUia.TextPatternOf(element);
        if (tp is null) return null;
        try
        {
            var selection = tp.GetSelection();
            if (selection is null || selection.Length == 0) return null;
            var document = tp.DocumentRange;
            var head = document.Clone();
            head.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection[0], TextPatternRangeEndpoint.Start);
            var tail = document.Clone();
            tail.MoveEndpointByRange(TextPatternRangeEndpoint.Start, selection[0], TextPatternRangeEndpoint.End);
            var prefix = head.GetText(WinAccessibility.MaxChars) ?? "";
            var suffix = tail.GetText(WinAccessibility.MaxChars) ?? "";
            var selected = selection[0].GetText(WinAccessibility.MaxChars) ?? "";
            // 継ぎ合わせが今の値と一致しないなら、改行の扱いなどがずれている。壊すより末尾へ足す
            if (Normalize(prefix + selected + suffix) != Normalize(before)) return null;
            return prefix + text + suffix;
        }
        catch (Exception) { return null; }
    }

    static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

    // MARK: - クリップボード経由（Ctrl+V）

    static async Task<bool> ViaClipboard(WindowsPlatform platform, UIA3Automation automation, string text, AutomationElement? element, TargetApp app)
    {
        platform.Activate(app);
        await Task.Delay(120).ConfigureAwait(false);
        // 対象アプリが前面に居なければ貼らない
        var front = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(front, out var frontPid);
        if ((int)frontPid != app.Pid)
        {
            Diag.Log("paste skipped: target app is not in front");
            platform.CopyToClipboard(text);
            return false;
        }
        // 取得元の入力欄が分かっていて、今の焦点が別の欄なら貼らない（別の会話へ移っていた場合の誤挿入を防ぐ）
        if (element is not null)
        {
            var current = WinUia.FocusedIn(automation, app.Pid);
            if (current is not null && !WinUia.Same(automation, element, current))
            {
                Diag.Log("paste skipped: focus moved to another field");
                platform.CopyToClipboard(text);
                return false;
            }
        }
        return await ClipboardPaste.Run(platform, text, () => Task.FromResult(Native.SendCtrlV()), () => WinUia.ValueOf(element)).ConfigureAwait(false);
    }

    // MARK: - 焦点の確認

    static bool StillFocused(UIA3Automation automation, AutomationElement element, TargetApp app)
    {
        var front = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(front, out var frontPid);
        // 自分のパネルが前面のときも、対象アプリの焦点は元の入力欄のままなので許す
        if ((int)frontPid != app.Pid && (int)frontPid != Environment.ProcessId) return false;
        var current = WinUia.FocusedIn(automation, app.Pid);
        return WinUia.Same(automation, element, current);
    }
}
