using System.Drawing;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Patterns;
using FlaUI.UIA3;
using ReplyFive.Desktop.Services;

namespace ReplyFive.Desktop.Platform.Windows;

/// <summary>UI Automation の土台。COM のプロキシはプロセス内で 1 つを使い回す（MTA なのでスレッドプールから共用できる）。
/// UIA は許可の要らない仕組みなので、macOS の Accessibility のような事前承認は無い。
/// 要素への問い合わせは相手プロセスが落ちていると例外になるので、ここの補助関数はすべて安全な既定値を返す。</summary>
internal static class WinUia
{
    static readonly object gate = new();
    static UIA3Automation? shared;
    static bool failed;

    /// <summary>使い回しの UIA3Automation。作れなければ null（会話の読み取りをあきらめる）。</summary>
    internal static UIA3Automation? Automation()
    {
        lock (gate)
        {
            if (shared is not null || failed) return shared;
            try
            {
                // COM の生成が STA スレッドに乗らないようにする（UIA は MTA 前提。UI スレッドから呼ばれた場合の保険）
                if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                    shared = Task.Run(() => new UIA3Automation()).GetAwaiter().GetResult();
                else
                    shared = new UIA3Automation();
            }
            catch (Exception e)
            {
                failed = true;
                Diag.Log("uia unavailable " + e.GetType().Name);
            }
            return shared;
        }
    }

    // MARK: - 要素の基本情報（例外を出さない）

    internal static Rectangle Rect(AutomationElement? el)
    {
        if (el is null) return Rectangle.Empty;
        try { return el.BoundingRectangle; }
        catch (Exception) { return Rectangle.Empty; }
    }

    internal static ControlType Kind(AutomationElement? el)
    {
        if (el is null) return ControlType.Unknown;
        try { return el.ControlType; }
        catch (Exception) { return ControlType.Unknown; }
    }

    internal static string? NameOf(AutomationElement? el)
    {
        if (el is null) return null;
        try { return el.Name; }
        catch (Exception) { return null; }
    }

    internal static bool Offscreen(AutomationElement? el)
    {
        if (el is null) return false;
        try { return el.IsOffscreen; }
        catch (Exception) { return false; }
    }

    internal static int ProcessIdOf(AutomationElement? el)
    {
        if (el is null) return 0;
        try { return el.Properties.ProcessId.ValueOrDefault; }
        catch (Exception) { return 0; }
    }

    internal static bool IsPassword(AutomationElement? el)
    {
        if (el is null) return false;
        try { return el.Properties.IsPassword.ValueOrDefault; }
        catch (Exception) { return false; }
    }

    internal static AutomationElement[] Children(AutomationElement? el)
    {
        if (el is null) return [];
        try { return el.FindAllChildren(); }
        catch (Exception) { return []; }
    }

    internal static AutomationElement? ParentOf(AutomationElement? el)
    {
        if (el is null) return null;
        try { return el.Parent; }
        catch (Exception) { return null; }
    }

    // MARK: - 入力欄

    /// <summary>会話の返信欄になりうる役割。Chromium（Slack・Teams・Chatwork・ブラウザ）の contenteditable は Edit として出る。
    /// Document は Web ページ全体もこの役割なので、書き込める値を持つときだけ入力欄とみなす。</summary>
    internal static bool IsInputKind(AutomationElement? el)
    {
        var kind = Kind(el);
        if (kind is ControlType.Edit or ControlType.ComboBox) return true;
        if (kind != ControlType.Document) return false;
        var v = ValuePatternOf(el);
        return v is not null && !ReadOnly(v);
    }

    /// <summary>差し込み先にしてよい欄か（伏せ字の欄は除く）。</summary>
    internal static bool IsEditable(AutomationElement? el) => el is not null && !IsPassword(el) && IsInputKind(el);

    internal static IValuePattern? ValuePatternOf(AutomationElement? el)
    {
        if (el is null) return null;
        try { return el.Patterns.Value.PatternOrDefault; }
        catch (Exception) { return null; }
    }

    internal static ITextPattern? TextPatternOf(AutomationElement? el)
    {
        if (el is null) return null;
        try { return el.Patterns.Text.PatternOrDefault; }
        catch (Exception) { return null; }
    }

    internal static bool ReadOnly(IValuePattern pattern)
    {
        try { return pattern.IsReadOnly.ValueOrDefault; }
        catch (Exception) { return false; }
    }

    /// <summary>入力欄の現在の値。読めなければ null（「読めない」と「空」を区別する：二重挿入を避けるため）。</summary>
    internal static string? ValueOf(AutomationElement? el)
    {
        var v = ValuePatternOf(el);
        if (v is null) return null;
        try { return v.Value.ValueOrDefault; }
        catch (Exception) { return null; }
    }

    /// <summary>選択されている文字列。無ければ null。</summary>
    internal static string? SelectedText(AutomationElement? el, int maxChars)
    {
        var tp = TextPatternOf(el);
        if (tp is null) return null;
        try
        {
            var ranges = tp.GetSelection();
            if (ranges is null || ranges.Length == 0) return null;
            var s = ranges[0].GetText(maxChars);
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch (Exception) { return null; }
    }

    // MARK: - 対象アプリの焦点

    /// <summary>対象アプリの中で焦点のある要素。別プロセス（自分のパネルなど）に焦点があるときは null。</summary>
    internal static AutomationElement? FocusedIn(UIA3Automation automation, int pid)
    {
        try
        {
            var el = automation.FocusedElement();
            if (el is null) return null;
            return ProcessIdOf(el) == pid ? el : null;
        }
        catch (Exception) { return null; }
    }

    internal static bool Same(UIA3Automation automation, AutomationElement? a, AutomationElement? b)
    {
        if (a is null || b is null) return false;
        try { return automation.Compare(a, b); }
        catch (Exception) { return false; }
    }

    // MARK: - 走査の下ごしらえ

    /// <summary>木をたどる間に使う取り置き。1 要素ずつプロセス間で問い合わせると遅いので、必要な属性だけまとめて取る。
    /// AutomationElementMode.Full のままにしておき、走査のあとも親子をたどれるようにする。</summary>
    internal static CacheRequest WalkCache(UIA3Automation automation)
    {
        var cache = new CacheRequest
        {
            AutomationElementMode = AutomationElementMode.Full,
            TreeScope = TreeScope.Element | TreeScope.Children,
        };
        cache.Add(automation.PropertyLibrary.Element.Name);
        cache.Add(automation.PropertyLibrary.Element.ControlType);
        cache.Add(automation.PropertyLibrary.Element.BoundingRectangle);
        cache.Add(automation.PropertyLibrary.Element.IsOffscreen);
        return cache;
    }
}
