using ReplyFive.Core;
using ReplyFive.Desktop.Platform.Shared;
using ReplyFive.Desktop.Services;

namespace ReplyFive.Desktop.Platform.Linux;

/// <summary>AT-SPI2 による読み取りと差し込み（macOS 版 Accessibility.swift / Paste.swift の移植）。
/// ショートカット押下時と、会話アプリが前面の間の 0.5 秒ごとに、会話のペインだけを 1 回読む。本文はログに出さない。</summary>
public sealed partial class LinuxPlatform
{
    AtSpi? atspi;
    readonly object atspiGate = new();
    bool? a11yEnabled;
    long a11yCheckedAt;
    sealed record WindowRef(AtSpi.Ref App, AtSpi.Ref Window);
    sealed record FocusRef(AtSpi.Ref Element, AtSpi.Ref App, AtSpi.Ref Window);
    static readonly HashSet<uint> inputRoles = [AtSpi.RoleEntry, AtSpi.RoleText, AtSpi.RoleDocumentText, AtSpi.RoleTerminal, AtSpi.RoleParagraph];
    static readonly HashSet<uint> skipRoles = [AtSpi.RoleMenu, AtSpi.RoleMenuBar, AtSpi.RoleToolBar, AtSpi.RoleScrollBar];
    static readonly HashSet<uint> textRoles = [AtSpi.RoleText, AtSpi.RoleParagraph, AtSpi.RoleLabel, AtSpi.RoleHeading, AtSpi.RoleLink, AtSpi.RoleStatic, AtSpi.RoleListItem, AtSpi.RoleTableCell, AtSpi.RoleSection, AtSpi.RoleDocumentText, AtSpi.RoleEntry];
    static readonly HashSet<uint> controlRoles = [AtSpi.RolePushButton, AtSpi.RoleToggleButton];

    AtSpi? Bus()
    {
        lock (atspiGate)
        {
            if (atspi is not null) return atspi;
            atspi = AtSpi.ConnectAsync().GetAwaiter().GetResult();
            return atspi;
        }
    }

    void ResetBus() { lock (atspiGate) { atspi?.Dispose(); atspi = null; } }

    static T? Sync<T>(Task<T> t, int timeoutMs = 700)
    {
        try { return t.Wait(timeoutMs) ? t.Result : default; } catch (Exception) { return default; }
    }

    public bool AccessibilityTrusted
    {
        get
        {
            var now = Environment.TickCount64;
            if (a11yEnabled is null || now - a11yCheckedAt > 5000)
            {
                a11yEnabled = Sync(AtSpi.IsEnabledAsync(), 800) ?? false;
                a11yCheckedAt = now;
            }
            return a11yEnabled == true;
        }
    }

    public void RequestAccessibility()
    {
        _ = Task.Run(async () =>
        {
            var ok = await AtSpi.EnableAsync();
            Diag.Log("a11y enable ok=" + ok);
            a11yEnabled = null;
            ResetBus();
        });
    }

    // MARK: - 前面のアプリ

    public TargetApp? FrontmostApp()
    {
        var bus = Bus();
        if (bus is null) return null;
        var active = Sync(bus.ActiveWindow(), 600);
        if (active is null) return null;
        var (app, win, appName, title) = active.Value;
        var name = string.IsNullOrWhiteSpace(appName) ? null : appName;
        if (name is "ReplyFive" or "replyfive") return null;
        return new TargetApp(StableHash(app.Bus), 0, name, string.IsNullOrWhiteSpace(title) ? null : title, new WindowRef(app, win));
    }

    static long StableHash(string s) { unchecked { long h = 1469598103934665603L; foreach (var c in s) h = (h ^ c) * 1099511628211L; return h == 0 ? 1 : h; } }

    public (int x, int y)? CursorPosition()
    {
        if (IsWayland) return null;
        if (Which("xdotool") is not { } xdotool) return null;
        var out_ = Run(xdotool, "getmouselocation", "--shell");
        if (out_ is null) return null;
        int? x = null, y = null;
        foreach (var line in out_.Split('\n'))
        {
            if (line.StartsWith("X=", StringComparison.Ordinal) && int.TryParse(line[2..], out var vx)) x = vx;
            if (line.StartsWith("Y=", StringComparison.Ordinal) && int.TryParse(line[2..], out var vy)) y = vy;
        }
        return x is not null && y is not null ? (x.Value, y.Value) : null;
    }

    /// <summary>対象アプリを前面に戻す。X11 なら xdotool でウインドウ名から、Wayland は元の入力欄への GrabFocus に任せる。</summary>
    public void Activate(TargetApp app)
    {
        if (IsWayland || app.WindowTitle is null || Which("xdotool") is not { } xdotool) return;
        Run(xdotool, "search", "--name", "--limit", "1", "^" + System.Text.RegularExpressions.Regex.Escape(app.WindowTitle) + "$", "windowactivate", "--sync");
    }

    // MARK: - 読み取り（付録BQ／BE／BT）

    public CapturedContext Capture(TargetApp? app, bool includeContext, string? ownName, double budgetSeconds, bool allowScreenText)
    {
        var name = app?.Name;
        if (!AccessibilityTrusted)
        {
            // 未有効：画面は読めないので、利用者がコピーした文だけを文脈にする（契約 source = clipboard）
            var clip = includeContext ? (ReadClipboard() ?? "").Trim() : "";
            if (clip.Length > 5000) clip = clip[^5000..];
            return new CapturedContext(app, null, name, app?.WindowTitle, clip, PlatformDetector.Detect(name, clip), clip.Length == 0 ? ContextSource.None : ContextSource.Clipboard, CaptureError.NotTrusted,
                clip.Length == 0 ? null : ContactDetector.LastSender(clip, ownName));
        }
        var bus = Bus();
        if (bus is null || app?.Native is not WindowRef wr) return CapturedContext.Empty(app, CaptureError.Failed);
        try { return CaptureAsync(bus, app, wr, includeContext, ownName, budgetSeconds).GetAwaiter().GetResult(); }
        catch (Exception e) { Diag.Log("capture failed " + e.GetType().Name); return CapturedContext.Empty(app, CaptureError.Failed); }
    }

    async Task<CapturedContext> CaptureAsync(AtSpi bus, TargetApp app, WindowRef wr, bool includeContext, string? ownName, double budgetSeconds)
    {
        var name = app.Name;
        var title = app.WindowTitle;
        var deadline = Environment.TickCount64 + (long)(budgetSeconds * 1000) + 300;
        var focused = await bus.Focused(wr.App, wr.Window).ConfigureAwait(false);
        FocusRef? focus = focused is { } f ? new FocusRef(f, wr.App, wr.Window) : null;
        uint focusRole = 0; ulong focusStates = 0; List<string> focusIfaces = [];
        if (focused is { } fe)
        {
            try { focusRole = await bus.Role(fe).ConfigureAwait(false); focusStates = await bus.States(fe).ConfigureAwait(false); focusIfaces = await bus.Interfaces(fe).ConfigureAwait(false); } catch (Exception) { }
            if (focusRole == AtSpi.RolePasswordText) focus = null;
        }
        var focusIsInput = focus is not null && (AtSpi.Has(focusStates, AtSpi.StateEditable) || inputRoles.Contains(focusRole));
        if (!includeContext)
            return new CapturedContext(app, focusIsInput ? focus : null, name, title, "", PlatformDetector.Detect(name), ContextSource.None, null);
        var text = "";
        var source = ContextSource.None;
        string? contactName = null;
        var items = new List<VisibleItem>();
        // 1) 選択テキスト → 入力欄の内容
        if (focus is not null && focusIfaces.Contains("org.a11y.atspi.Text"))
        {
            try
            {
                var (s, e) = await bus.Selection(focus.Element).ConfigureAwait(false);
                if (e > s) text = (await bus.GetText(focus.Element, s, e).ConfigureAwait(false)).Trim();
                if (text.Length == 0 && focusIsInput) { var v = (await bus.GetText(focus.Element).ConfigureAwait(false)).Trim(); if (v.Length > 5000) v = v[^5000..]; text = v; }
                if (text.Length > 0) source = ContextSource.SelectedText;
            }
            catch (Exception) { }
        }
        var hint = text + "\n" + (title ?? "");
        // 2) 会話のペイン（入力欄の祖先で、入力欄の 3 倍以上の高さの最初の容れ物。窓幅いっぱいならその 1 つ手前）
        if (text.Length == 0)
        {
            AtSpi.Ref? anchor = focusIsInput ? focus!.Element : await FindReplyBox(bus, wr).ConfigureAwait(false);
            AtSpi.Rect winRect = default;
            try { winRect = await bus.Extents(wr.Window).ConfigureAwait(false); } catch (Exception) { }
            AtSpi.Ref pane = wr.Window;
            if (anchor is { } a && winRect.H > 0)
            {
                var chosen = await ConversationPane(bus, a, wr.Window, winRect).ConfigureAwait(false);
                if (chosen is { } p) pane = p;
            }
            var read = await ReadPane(bus, pane, focus?.Element, title, deadline, budgetSeconds > 0.5 ? 1500 : 600).ConfigureAwait(false);
            items = read.items;
            if (read.text.Length > 0) { text = read.text; hint = text + "\n" + hint; source = ContextSource.Window; }
            var plat = PlatformDetector.Detect(name, hint);
            contactName = (plat is Core.Platform.Slack or Core.Platform.Teams or Core.Platform.Chatwork or Core.Platform.Lineworks or Core.Platform.Googlechat ? ContactFromTitle(title, name) : null)
                          ?? ContactDetector.LastSender(items, ownName);
        }
        else contactName = ContactDetector.LastSender(text, ownName);
        var error = text.Length == 0 ? CaptureError.NoSelection : (CaptureError?)null;
        return new CapturedContext(app, focusIsInput ? focus : null, name, title, text, PlatformDetector.Detect(name, hint), source, error, contactName);
    }

    /// <summary>付録BT：ウインドウタイトル（「#general - JapanMarketing - Slack」「山田 太郎 | Chatwork」）から会話の相手を取る。</summary>
    public static string? ContactFromTitle(string? title, string? appName)
    {
        if (string.IsNullOrEmpty(title)) return null;
        var head = title;
        foreach (var sep in new[] { " - ", " | ", " – ", " — " }) { var i = head.IndexOf(sep, StringComparison.Ordinal); if (i >= 0) head = head[..i]; }
        head = head.Trim();
        if ((head.StartsWith('(') || head.StartsWith('（')) && head.IndexOfAny([')', '）']) is var close && close > 0) head = head[(close + 1)..].Trim();
        head = System.Text.RegularExpressions.Regex.Replace(head, @"^\d+\s+", "");
        if (head.Length == 0 || head.Length > 60 || head.Equals(appName ?? "", StringComparison.OrdinalIgnoreCase)) return null;
        return head;
    }

    /// <summary>焦点が入力欄でないとき：ウインドウ下半分にある最も幅の広い編集可能要素を返信欄とみなす。</summary>
    async Task<AtSpi.Ref?> FindReplyBox(AtSpi bus, WindowRef wr)
    {
        try
        {
            var win = await bus.Extents(wr.Window).ConfigureAwait(false);
            var mid = win.Y + win.H / 2;
            List<AtSpi.Ref> candidates;
            try { candidates = await bus.MatchStates(wr.App, [AtSpi.StateEditable, AtSpi.StateShowing], 20).ConfigureAwait(false); }
            catch (Exception) { return null; }
            AtSpi.Ref? best = null; var bestW = 0;
            foreach (var c in candidates)
            {
                var r = await bus.Extents(c).ConfigureAwait(false);
                if (r.W >= 150 && r.H >= 20 && r.Y >= mid && r.W > bestW) { best = c; bestW = r.W; }
            }
            return best;
        }
        catch (Exception) { return null; }
    }

    async Task<AtSpi.Ref?> ConversationPane(AtSpi bus, AtSpi.Ref input, AtSpi.Ref window, AtSpi.Rect winRect)
    {
        var inputRect = await bus.Extents(input).ConfigureAwait(false);
        var chain = new List<(AtSpi.Ref r, AtSpi.Rect rect)> { (input, inputRect) };
        var el = input;
        for (var i = 0; i < 12; i++)
        {
            AtSpi.Ref parent;
            try { parent = await bus.Parent(el).ConfigureAwait(false); } catch (Exception) { break; }
            if (parent.IsNull || parent == window || parent == el) break;
            el = parent;
            AtSpi.Rect r; try { r = await bus.Extents(el).ConfigureAwait(false); } catch (Exception) { break; }
            chain.Add((el, r));
        }
        for (var i = 1; i < chain.Count; i++)
        {
            var r = chain[i].rect;
            var tall = r.H >= Math.Max(inputRect.H * 3, winRect.H * 0.4);
            if (!tall || r.W < inputRect.W * 0.8) continue;
            if (r.W <= winRect.W * 0.85) return chain[i].r;
            var prev = chain[i - 1];
            if (prev.rect.H >= winRect.H * 0.4 && prev.rect.W >= inputRect.W * 0.8 && prev.rect.W <= winRect.W * 0.85) return prev.r;
            return r.W < winRect.W * 0.98 || r.H < winRect.H * 0.98 ? chain[i].r : null;
        }
        return null;
    }

    /// <summary>ペイン内の可視テキストを表示順に集める。Text インターフェースを持つ要素は GetText、それ以外は Name。上限は要素数と時間。末尾 5,000 文字を残す。</summary>
    async Task<(string text, List<VisibleItem> items)> ReadPane(AtSpi bus, AtSpi.Ref pane, AtSpi.Ref? exclude, string? windowTitle, long deadline, int maxElements)
    {
        var parts = new List<string>();
        var items = new List<VisibleItem>();
        var stack = new Stack<AtSpi.Ref>(); stack.Push(pane);
        var visited = 0;
        while (stack.Count > 0)
        {
            if (++visited > maxElements || Environment.TickCount64 > deadline) break;
            var el = stack.Pop();
            if (el.IsNull || (exclude is { } ex && el == ex)) continue;
            ulong st; uint role;
            try { st = await bus.States(el).ConfigureAwait(false); role = await bus.Role(el).ConfigureAwait(false); } catch (Exception) { continue; }
            if (el != pane && !AtSpi.Has(st, AtSpi.StateShowing)) continue;
            if (skipRoles.Contains(role) || role == AtSpi.RolePasswordText) continue;
            if (AtSpi.Has(st, AtSpi.StateEditable) && el != pane) continue; // 別の入力欄は読まない
            if (textRoles.Contains(role))
            {
                string t = "";
                try
                {
                    var ifaces = await bus.Interfaces(el).ConfigureAwait(false);
                    if (ifaces.Contains("org.a11y.atspi.Text")) t = await bus.GetText(el).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(t)) t = await bus.Name(el).ConfigureAwait(false);
                }
                catch (Exception) { }
                t = t.Trim();
                if (t.Length > 0 && t != windowTitle) { parts.Add(t); items.Add(new VisibleItem(t)); }
            }
            else if (controlRoles.Contains(role))
            {
                try { var n = (await bus.Name(el).ConfigureAwait(false)).Trim(); if (n.Length > 0 && n.Length <= ContactDetector.MaxNameLength) items.Add(new VisibleItem(n, VisibleItem.ItemKind.Control)); } catch (Exception) { }
            }
            List<AtSpi.Ref> kids;
            try { kids = await bus.Children(el).ConfigureAwait(false); } catch (Exception) { continue; }
            for (var i = kids.Count - 1; i >= 0; i--) stack.Push(kids[i]);
        }
        var joined = string.Join("\n", parts);
        if (joined.Length > 5000) joined = joined[^5000..];
        Diag.LogIfChanged($"window read elements={visited} parts={parts.Count} items={items.Count} chars={joined.Length}");
        return (joined, items);
    }

    // MARK: - 差し込み（付録BF）

    public async Task<bool> Insert(string text, CapturedContext context)
    {
        var bus = Bus();
        if (bus is null || !AccessibilityTrusted) { CopyToClipboard(text); return false; }
        if (context.FocusedElement is FocusRef focus)
        {
            try
            {
                var current = await bus.Focused(focus.App, focus.Window).ConfigureAwait(false);
                if (current is { } c && c != focus.Element) { Diag.Log("insert skipped: focus moved to another field"); CopyToClipboard(text); return false; }
                var ifaces = await bus.Interfaces(focus.Element).ConfigureAwait(false);
                if (!ifaces.Contains("org.a11y.atspi.EditableText")) Diag.Log("insert direct: no EditableText on focused element");
                if (ifaces.Contains("org.a11y.atspi.EditableText"))
                {
                    var before = await bus.GetText(focus.Element).ConfigureAwait(false);
                    var caret = 0;
                    try { caret = await bus.CaretOffset(focus.Element).ConfigureAwait(false); } catch (Exception) { }
                    if (caret < 0 || caret > before.Length) caret = before.Length;
                    var ok = await bus.InsertText(focus.Element, caret, text).ConfigureAwait(false);
                    var after = await bus.GetText(focus.Element).ConfigureAwait(false);
                    Diag.Log($"insert direct ok={ok} caret={caret} before_chars={before.Length} after_chars={after.Length}");
                    if (after.Contains(text, StringComparison.Ordinal) && after != before) return true;
                    if (ok && after != before) { CopyToClipboard(text); return false; } // 部分的に変わった：二重挿入を避けて止める
                }
                // 直接書けない欄：焦点を戻して貼り付けキー
                try { await bus.GrabFocus(focus.Element).ConfigureAwait(false); } catch (Exception) { }
            }
            catch (Exception e) { Diag.Log("insert direct failed " + e.GetType().Name); }
        }
        if (context.App is { } app) Activate(app);
        await Task.Delay(120).ConfigureAwait(false);
        Func<string?>? readTarget = null;
        if (context.FocusedElement is FocusRef fr) readTarget = () => Sync(bus.GetText(fr.Element), 500);
        var pasted = await ClipboardPaste.Run(this, text, () => Task.FromResult(SendPasteKey()), readTarget).ConfigureAwait(false);
        if (!pasted) CopyToClipboard(text);
        return pasted;
    }

    /// <summary>Ctrl+V を送る。X11 は xdotool、Wayland は wtype か ydotool。送信キー（Enter）は決して送らない。</summary>
    bool SendPasteKey()
    {
        if (!IsWayland && Which("xdotool") is { } xdotool) return Run(xdotool, "key", "--clearmodifiers", "ctrl+v") is not null;
        if (Which("wtype") is { } wtype) return Run(wtype, "-M", "ctrl", "v", "-m", "ctrl") is not null;
        if (Which("ydotool") is { } ydotool) return Run(ydotool, "key", "29:1", "47:1", "47:0", "29:0") is not null;
        if (Which("xdotool") is { } xd2) return Run(xd2, "key", "--clearmodifiers", "ctrl+v") is not null;
        return false;
    }
}
