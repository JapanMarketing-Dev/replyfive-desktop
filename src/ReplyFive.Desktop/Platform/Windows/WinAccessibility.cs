using System.Drawing;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using ReplyFive.Core;
using ReplyFive.Desktop.Services;

namespace ReplyFive.Desktop.Platform.Windows;

/// <summary>ショートカット押下時（と付録BU の収集契機）に 1 回だけ、前面アプリの会話を UI Automation で読む。
/// 画面の常時監視はしない。読むのは「入力欄の祖先＝会話のペイン」だけで、アプリ全体は読まない（付録BQ）。
/// macOS 版 Accessibility.swift の移植で、AX の役割を UIA の ControlType に置き換えてある。</summary>
internal static partial class WinAccessibility
{
    internal const int MaxChars = 5000;
    internal const int MaxElements = 600;
    /// <summary>背景の収集（付録BU）は会話ペインに限るので、要素数の上限を広げても時間内に収まる。</summary>
    internal const int MaxElementsBackground = 1500;

    /// <summary>付録BT：ウインドウタイトルに相手（チャンネル・DM 相手）が出るアプリ。</summary>
    static readonly Core.Platform[] titlePlatforms = [Core.Platform.Slack, Core.Platform.Teams, Core.Platform.Chatwork, Core.Platform.Lineworks, Core.Platform.Googlechat];

    static readonly ControlType[] textKinds =
    [
        ControlType.Text, ControlType.Edit, ControlType.Document, ControlType.Header, ControlType.HeaderItem,
        ControlType.Hyperlink, ControlType.ListItem, ControlType.DataItem, ControlType.TreeItem,
    ];
    /// <summary>送信者名がボタンとして出るアプリ（Slack など）のために、表題も別系統（items）に残す。文脈テキストには入れない。</summary>
    static readonly ControlType[] controlKinds = [ControlType.Button, ControlType.SplitButton, ControlType.MenuItem];
    static readonly ControlType[] skipKinds =
    [
        ControlType.Menu, ControlType.MenuBar, ControlType.ToolBar, ControlType.ScrollBar, ControlType.TitleBar, ControlType.ToolTip,
    ];

    [GeneratedRegex(@"^\d+\s+")] private static partial Regex LeadingCount();
    [GeneratedRegex(@"\s*[\(（]\d+[\)）]\s*$")] private static partial Regex TrailingCount();
    [GeneratedRegex(@"[\(（]\d+[\)）]")] private static partial Regex AnyCount();

    // MARK: - 相手名

    /// <summary>付録BT：チャットアプリのウインドウタイトル（「#general - JapanMarketing - Slack」「山田 太郎 | Chatwork」）から
    /// 会話の相手（チャンネル・ルーム・DM 相手）を取る。</summary>
    internal static string? ContactFromTitle(string? title, string? appName)
    {
        if (string.IsNullOrEmpty(title)) return null;
        var head = title;
        foreach (var sep in new[] { " - ", " | ", " – ", " — " })
        {
            var i = head.IndexOf(sep, StringComparison.Ordinal);
            if (i >= 0) head = head[..i];
        }
        head = head.Trim();
        // 「(3) #general」の未読数
        if (head.Length > 0 && (head[0] == '(' || head[0] == '（'))
        {
            var close = head.IndexOfAny([')', '）']);
            if (close > 0) head = head[(close + 1)..].Trim();
        }
        head = LeadingCount().Replace(head, "");
        if (head.Length == 0 || head.Length > 60) return null;
        if (string.Equals(head, appName ?? "", StringComparison.OrdinalIgnoreCase)) return null;
        return head;
    }

    static readonly HashSet<string> tabWords =
    [
        "すべて", "友だち", "グループ", "オープンチャット", "公式アカウント", "all", "friends", "groups", "open chat", "official accounts",
    ];
    static readonly HashSet<char> iconGlyphs = [.. "目三≡Q&+t·・oO0lI|"];

    /// <summary>付録BT：画面の文字認識の 1 行目（会話ペインの見出し＝相手名・ルーム名）を相手として取り、本文から外す。</summary>
    internal static (string? Contact, string Body) SplitHeader(string ocr, string? appName)
    {
        var lines = ocr.Split('\n').Where(l => l.Trim().Length > 0).ToList();
        // 付録BU：領域をウインドウ上端まで広げるので、1 行目がアプリ名（タイトルバーの「LINE」）のことがある
        if (!string.IsNullOrEmpty(appName) && lines.Count > 0 && string.Equals(lines[0].Trim(), appName, StringComparison.OrdinalIgnoreCase)) lines.RemoveAt(0);
        // トーク一覧のタブ（横に溢れて会話列の左上に写る）は見出しではない
        while (lines.Count > 0)
        {
            var core = lines[0].Trim().ToLowerInvariant().Trim([' ', '。', '、', '.', ',', ':', '：', '・', '|', '-']);
            var words = core.Split([' ', '　'], StringSplitOptions.RemoveEmptyEntries).Where(w => w.Any(char.IsLetter)).ToArray();
            if (words.Length == 0) break;
            if (!(words.All(tabWords.Contains) || tabWords.Contains(core))) break;
            lines.RemoveAt(0);
        }
        string Body() => string.Join("\n", lines);
        if (lines.Count == 0) return (null, "");
        var first = lines[0].Trim();
        if (first.Length == 0 || first.Length > 40 || first.StartsWith("自分: ", StringComparison.Ordinal)) return (null, Body());
        var name = TrailingCount().Replace(first, "");   // 「Takumi/Sheena (5)」の人数
        // 付録BU：見出しの右端のアイコン（検索・通話・メニュー）が「+ & Q 目」のように読まれる。文字を含まない語で切る
        var kept = new List<string>();
        foreach (var raw in name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var tok = AnyCount().Replace(raw, "");
            if (tok.Length == 0) continue;
            if (!tok.Any(char.IsLetter)) break;
            if (tok.Any(c => char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.MathSymbol or System.Globalization.UnicodeCategory.OtherSymbol)) break;
            if (kept.Count > 0 && tok.Length <= 2 && tok.All(iconGlyphs.Contains)) break;  // 「目」「Q」等、アイコンの誤読
            kept.Add(tok);
        }
        name = string.Join(" ", kept);
        while (name.Length > 0 && !char.IsLetterOrDigit(name[^1]) && !"）)」』".Contains(name[^1])) name = name[..^1];
        if (name.Length == 0 || ContactDetector.IsNoise(name)) return (null, Body());
        lines.RemoveAt(0);
        return (name, Body());
    }

    /// <summary>付録BU：ウインドウタイトルに相手名が無いアプリ（Chatwork は常に「Chatwork」）は、会話ペインの直上にある見出しを
    /// ルーム名として使う。ペインと同じ横幅の帯（上 90px）だけを見る。</summary>
    internal static string? ContactFromHeader(UIA3Automation automation, Rectangle pane, AutomationElement window, double budgetSeconds = 0.15)
    {
        var band = Rectangle.FromLTRB(pane.Left - 20, pane.Top - 90, pane.Right + 20, pane.Top + 2);
        if (band.Width <= 0 || band.Height <= 0) return null;
        var deadline = DateTime.UtcNow.AddSeconds(budgetSeconds);
        var stack = new Stack<AutomationElement>();
        stack.Push(window);
        var visited = 0;
        string? heading = null, fallback = null;
        using (WinUia.WalkCache(automation).Activate())
        {
            while (stack.Count > 0)
            {
                var el = stack.Pop();
                if (++visited > 1500 || DateTime.UtcNow > deadline) break;
                var r = WinUia.Rect(el);
                // 帯と重ならない枝は見ない（枠が取れない要素は子をたどる）
                if (r.Width > 0 && r.Height > 0 && !r.IntersectsWith(band) && !r.Contains(band)) continue;
                var kind = WinUia.Kind(el);
                if (band.Contains(new Point(r.Left + r.Width / 2, r.Top + r.Height / 2)) && kind is ControlType.Header or ControlType.HeaderItem or ControlType.Text)
                {
                    var s = (WinUia.NameOf(el) ?? "").Trim();
                    if (s.Length > 0 && s.Length <= 60 && !ContactDetector.IsNoise(s))
                    {
                        if (kind != ControlType.Text) { heading = s; break; }
                        fallback ??= s;
                    }
                }
                var kids = WinUia.Children(el);
                for (var i = kids.Length - 1; i >= 0; i--) stack.Push(kids[i]);
            }
        }
        return heading ?? fallback;
    }

    // MARK: - 返信欄とペイン（付録BQ・BU）

    static readonly Dictionary<int, (AutomationElement? El, DateTime At)> replyBoxCache = [];
    static readonly object replyBoxGate = new();

    /// <summary>付録BU：焦点が入力欄でないとき（ルーム一覧の行、メッセージ一覧、Web 領域全体）は、
    /// ウインドウ内の返信欄を探してペイン判定の起点にする。返信欄＝ウインドウ下半分にある最も幅の広い入力欄。</summary>
    internal static AutomationElement? FindReplyBox(UIA3Automation automation, AutomationElement window, int pid, double budgetSeconds = 0.4)
    {
        (AutomationElement? El, DateTime At) cached;
        bool hasCached;
        lock (replyBoxGate) hasCached = replyBoxCache.TryGetValue(pid, out cached);
        if (hasCached)
        {
            // 前回見つけた返信欄がまだ生きていればそれ（Chromium は DOM が変わると枠が空になる）
            if (cached.El is not null && WinUia.IsInputKind(cached.El) && WinUia.Rect(cached.El).Width >= 150) return cached.El;
            if ((DateTime.UtcNow - cached.At).TotalSeconds < 5) return null;   // 探し直しは 5 秒に 1 回まで
        }

        var winRect = WinUia.Rect(window);
        var mid = winRect.Top + winRect.Height / 2;
        var deadline = DateTime.UtcNow.AddSeconds(budgetSeconds);
        var stack = new Stack<AutomationElement>();
        stack.Push(window);
        var visited = 0;
        AutomationElement? best = null;
        var bestWidth = 0;
        var candidates = new List<AutomationElement>();
        using (WinUia.WalkCache(automation).Activate())
        {
            while (stack.Count > 0)
            {
                var el = stack.Pop();
                if (++visited > 2500 || DateTime.UtcNow > deadline) break;
                var kind = WinUia.Kind(el);
                if (kind is ControlType.Menu or ControlType.MenuBar or ControlType.ToolBar) continue;
                if (kind is ControlType.Edit or ControlType.ComboBox or ControlType.Document)
                {
                    var r = WinUia.Rect(el);
                    if (r.Width >= 150 && r.Height >= 20 && r.Top >= mid && candidates.Count < 32) candidates.Add(el);
                    if (kind != ControlType.Document) continue;   // Document は Web ページ全体のこともあるので中も見る
                }
                var kids = WinUia.Children(el);
                if (kids.Length == 0) continue;
                // 返信欄はたいてい画面の下にあるので、上半分に収まる大きな枝は見ない
                if (kids.Length > 3)
                {
                    var r = WinUia.Rect(el);
                    if (r.Height > 0 && r.Bottom < mid) continue;
                }
                for (var i = kids.Length - 1; i >= 0; i--) stack.Push(kids[i]);
            }
        }
        // 取り置きの外で、書き込める欄かどうかを確かめる（Document の大半は読み取り専用の Web ページ）
        foreach (var el in candidates)
        {
            if (!WinUia.IsInputKind(el) || WinUia.IsPassword(el)) continue;
            var w = WinUia.Rect(el).Width;
            if (w > bestWidth) { best = el; bestWidth = w; }
        }
        lock (replyBoxGate) replyBoxCache[pid] = (best, DateTime.UtcNow);
        return best;
    }

    static string lastPaneLog = "";
    static readonly object paneLogGate = new();

    /// <summary>付録BQ：会話の領域。入力欄から親をたどり、入力欄の 3 倍以上の高さを持つ最初の容れ物を「この会話のペイン」とみなす。
    /// それがウインドウ幅いっぱい（サイドバーを含む）なら 1 つ手前を使う（左のルーム一覧を読まないため）。</summary>
    internal static (AutomationElement El, Rectangle Rect)? ConversationPane(AutomationElement input, AutomationElement window, string? appName)
    {
        var inputRect = WinUia.Rect(input);
        var winRect = WinUia.Rect(window);
        var chain = new List<AutomationElement> { input };
        var el = input;
        for (var i = 0; i < 12; i++)
        {
            var parent = WinUia.ParentOf(el);
            if (parent is null) break;
            el = parent;
            if (WinUia.Kind(el) == ControlType.Window) break;
            chain.Add(el);
        }

        (AutomationElement El, Rectangle Rect)? chosen = null;
        for (var i = 1; i < chain.Count; i++)
        {
            var r = WinUia.Rect(chain[i]);
            var tall = r.Height >= Math.Max(inputRect.Height * 3, (int)(winRect.Height * 0.4));
            if (!tall || r.Width < inputRect.Width * 0.8) continue;
            if (r.Width <= winRect.Width * 0.85) { chosen = (chain[i], r); break; }
            var prev = chain[i - 1];
            var pr = WinUia.Rect(prev);
            if (pr.Height >= winRect.Height * 0.4 && pr.Width >= inputRect.Width * 0.8 && pr.Width <= winRect.Width * 0.85) { chosen = (prev, pr); break; }
            chosen = r.Width < winRect.Width * 0.98 || r.Height < winRect.Height * 0.98 ? (chain[i], r) : null;
            break;
        }

        // 診断：祖先の列（役割と枠の数値だけ。本文は書かない）。同じ結果が続く間は出さない
        var summary = string.Join(" > ", chain.Select(e => { var r = WinUia.Rect(e); return $"{WinUia.Kind(e)}{r.Width}x{r.Height}@{r.Left},{r.Top}"; }));
        var line = $"pane app={appName ?? "?"} win={winRect.Width}x{winRect.Height} chosen={(chosen is { } c ? $"{c.Rect.Width}x{c.Rect.Height}@{c.Rect.Left},{c.Rect.Top}" : "nil")} chain={summary}";
        lock (paneLogGate)
        {
            if (line != lastPaneLog) { lastPaneLog = line; Diag.Log(line); }
        }
        return chosen;
    }

    // MARK: - 可視テキストの収集

    /// <summary>前面ウインドウ（またはその会話ペイン）の可視テキストを 1 回だけ集める。時間と要素数に上限を置き、末尾（最新のメッセージ側）を残す。</summary>
    internal static (string Text, List<VisibleItem> Items) ReadVisible(UIA3Automation automation, AutomationElement root, AutomationElement? focused,
        string? windowTitle, double budgetSeconds, int maxElements)
    {
        var deadline = DateTime.UtcNow.AddSeconds(budgetSeconds);
        var focusedRect = WinUia.Rect(focused);
        var parts = new List<string>();
        var items = new List<VisibleItem>();
        // Name を持たない入力系（メールの本文欄など）は取り置きの外で値を読む。順番を保つため場所だけ先に空けておく
        var lazy = new List<(int PartIndex, int ItemIndex, AutomationElement El)>();
        var stack = new Stack<AutomationElement>();
        stack.Push(root);
        var visited = 0;
        using (WinUia.WalkCache(automation).Activate())
        {
            while (stack.Count > 0)
            {
                var el = stack.Pop();
                if (++visited > maxElements || DateTime.UtcNow > deadline) break;
                var kind = WinUia.Kind(el);
                if (skipKinds.Contains(kind)) continue;
                if (WinUia.Offscreen(el)) continue;
                var rect = WinUia.Rect(el);
                // 取得元の入力欄そのものは読まない（自分が書きかけの文を文脈に混ぜない）
                if (!focusedRect.IsEmpty && rect == focusedRect && kind is ControlType.Edit or ControlType.Document or ControlType.ComboBox) continue;

                if (textKinds.Contains(kind))
                {
                    var t = (WinUia.NameOf(el) ?? "").Trim();
                    if (t.Length > 0 && t != windowTitle) { parts.Add(t); items.Add(new VisibleItem(t)); }
                    else if (t.Length == 0 && kind is ControlType.Edit or ControlType.Document && lazy.Count < 3)
                    {
                        lazy.Add((parts.Count, items.Count, el));
                        parts.Add(""); items.Add(new VisibleItem(""));
                    }
                }
                else if (controlKinds.Contains(kind))
                {
                    var t = (WinUia.NameOf(el) ?? "").Trim();
                    if (t.Length > 0 && t.Length <= ContactDetector.MaxNameLength) items.Add(new VisibleItem(t, VisibleItem.ItemKind.Control));
                }

                var kids = WinUia.Children(el);
                // 逆順で積むと表示順に取り出せる
                for (var i = kids.Length - 1; i >= 0; i--) stack.Push(kids[i]);
            }
        }
        foreach (var (partIndex, itemIndex, el) in lazy)
        {
            var v = WinUia.ValueOf(el)?.Trim();
            if (string.IsNullOrEmpty(v)) continue;
            if (v.Length > MaxChars) v = v[^MaxChars..];
            parts[partIndex] = v;
            items[itemIndex] = new VisibleItem(v);
        }
        parts.RemoveAll(p => p.Length == 0);
        items.RemoveAll(i => i.Text.Length == 0);

        var joined = string.Join("\n", parts);
        if (joined.Length > MaxChars) joined = joined[^MaxChars..];
        Diag.LogIfChanged($"window read elements={visited} parts={parts.Count} items={items.Count} chars={joined.Length}");
        return (joined, items);
    }

    // MARK: - ブラウザのアドレス欄（判定材料。本文には入れない）

    static readonly string[] browserNames = ["chrome", "edge", "firefox", "brave", "arc", "opera", "vivaldi"];

    /// <summary>ブラウザは選択テキストに URL が含まれないので、アドレス欄の値を判定材料に足す。見つからなければ null。</summary>
    static string? BrowserUrl(UIA3Automation automation, AutomationElement? window, string? appName)
    {
        if (window is null || string.IsNullOrEmpty(appName)) return null;
        var n = appName.ToLowerInvariant();
        if (!browserNames.Any(n.Contains)) return null;
        var winRect = WinUia.Rect(window);
        if (winRect.Height <= 0) return null;
        var limit = winRect.Top + (int)(winRect.Height * 0.2);
        var deadline = DateTime.UtcNow.AddMilliseconds(60);
        var stack = new Stack<AutomationElement>();
        stack.Push(window);
        var visited = 0;
        var candidates = new List<AutomationElement>();
        using (WinUia.WalkCache(automation).Activate())
        {
            while (stack.Count > 0 && candidates.Count < 4)
            {
                var el = stack.Pop();
                if (++visited > 200 || DateTime.UtcNow > deadline) break;
                var r = WinUia.Rect(el);
                if (r.Height > 0 && r.Top > limit) continue;    // アドレス欄は窓の上端付近にしかない
                if (WinUia.Kind(el) == ControlType.Edit) { candidates.Add(el); continue; }
                var kids = WinUia.Children(el);
                for (var i = kids.Length - 1; i >= 0; i--) stack.Push(kids[i]);
            }
        }
        foreach (var el in candidates)
        {
            var v = WinUia.ValueOf(el)?.Trim();
            if (string.IsNullOrEmpty(v) || v.Length > 2048) continue;
            if (v.StartsWith("http", StringComparison.OrdinalIgnoreCase) || v.Contains('.')) return v;
        }
        return null;
    }

    // MARK: - 取得

    static readonly Dictionary<int, Rectangle> lastRegion = [];
    static readonly object lastRegionGate = new();

    /// <summary>付録BU：直近に本文が読めた会話の領域（アプリごと）。変化検知の署名をこの領域だけで取るために使う。</summary>
    internal static Rectangle? RememberedRegion(int pid)
    {
        lock (lastRegionGate) return lastRegion.TryGetValue(pid, out var r) ? r : null;
    }

    /// <summary>同期で速い（通常 数ms〜数十ms）。呼び出し側で 700ms のタイムアウトを掛ける。
    /// budget はウインドウ走査の時間（ショートカット時 0.35 秒、背景の収集は 0.8 秒＝付録BU）。</summary>
    internal static CapturedContext Capture(TargetApp? app, bool includeContext, string? ownName, double budgetSeconds, bool allowScreenText)
    {
        if (app is null) return CapturedContext.Empty(null, CaptureError.Failed);
        var automation = WinUia.Automation();
        if (automation is null) return CapturedContext.Empty(app, CaptureError.Failed);

        var name = app.Name;
        var hwnd = new IntPtr(app.Handle);
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) hwnd = WindowsPlatform.MainWindowOf(app.Pid);
        var windowTitle = Native.WindowText(hwnd) ?? app.WindowTitle;
        AutomationElement? window = null;
        if (hwnd != IntPtr.Zero)
        {
            try { window = automation.FromHandle(hwnd); }
            catch (Exception) { window = null; }
        }

        var focused = WinUia.FocusedIn(automation, app.Pid);
        // 伏せ字の欄（パスワード）は読まないし、差し込み先にもしない
        if (WinUia.IsPassword(focused))
            return new CapturedContext(app, null, name, windowTitle, "", PlatformDetector.Detect(name), ContextSource.None, null);
        var target = WinUia.IsEditable(focused) ? focused : null;
        if (!includeContext)
            return new CapturedContext(app, target, name, windowTitle, "", PlatformDetector.Detect(name), ContextSource.None, null);

        // 1) 選択している文、2) 入力欄の中身
        var text = WinUia.SelectedText(focused, MaxChars)?.Trim() ?? "";
        if (text.Length == 0 && WinUia.IsInputKind(focused))
        {
            var v = WinUia.ValueOf(focused)?.Trim();
            if (!string.IsNullOrEmpty(v)) text = v.Length > MaxChars ? v[..MaxChars] : v;
        }
        var hint = text;
        if (!string.IsNullOrEmpty(windowTitle)) hint += "\n" + windowTitle;
        if (BrowserUrl(automation, window, name) is { } url) hint += "\n" + url;

        var source = text.Length == 0 ? ContextSource.None : ContextSource.SelectedText;
        string? contactName = null;
        (AutomationElement El, Rectangle Rect)? pane = null;
        var focusIsInput = WinUia.IsInputKind(focused);
        var anchor = focused;   // ペイン判定と「入力欄より上」の切り出しの起点

        // 選択も入力欄の中身も無い（返信欄にカーソルがあるだけ）のが普通の状態。そのときウインドウの可視テキストを 1 回だけ読む
        if (text.Length == 0 && window is not null)
        {
            if (!focusIsInput) anchor = FindReplyBox(automation, window, app.Pid) ?? anchor;
            if (anchor is not null) pane = ConversationPane(anchor, window, name);
            var read = ReadVisible(automation, pane?.El ?? window, focused, windowTitle, budgetSeconds,
                budgetSeconds > 0.5 ? MaxElementsBackground : MaxElements);
            if (read.Text.Length > 0)
            {
                text = read.Text;
                hint = read.Text + "\n" + hint;
                source = ContextSource.Window;
            }
            // 付録BE：入力欄の直上にある最後のメッセージの送信者を相手とする。付録BT：チャットアプリはウインドウタイトルを優先
            var plat = PlatformDetector.Detect(name, hint);
            contactName = (titlePlatforms.Contains(plat) ? ContactFromTitle(windowTitle, name) : null)
                ?? (pane is { } p ? ContactFromHeader(automation, p.Rect, window) : null)
                ?? ContactDetector.LastSender(read.Items, ownName);
        }
        else if (text.Length > 0)
        {
            contactName = ContactDetector.LastSender(text, ownName);
        }

        // 付録BP：AX（UIA）で本文が取れないアプリ（LINE 等）は、画面の文字認識で 1 回だけ読む
        CaptureError? error = text.Length == 0 ? CaptureError.NoSelection : null;
        if (text.Length == 0 && allowScreenText)
        {
            var winRect = hwnd != IntPtr.Zero ? Native.WindowRect(hwnd) : Rectangle.Empty;
            var ef = WinUia.Rect(focused);
            Rectangle? region = pane?.Rect;
            Rectangle? exclude = WinUia.Rect(anchor) is { IsEmpty: false } ar ? ar : null;
            if (!focusIsInput && ef.Height >= 150 && ef.Width >= 300 && (winRect.Width == 0 || ef.Width <= winRect.Width * 0.85))
            {
                // 焦点が会話表示そのもの（LINE のメッセージ一覧）。ウインドウ幅いっぱいの要素（Web 領域）は会話列ではないので除く
                region = ef;
                exclude = null;
            }
            else if (pane is { } p && winRect.Width > 0)
            {
                // ペインの上端をウインドウの上端まで広げ、相手名の見出し（ペインの外にある）を 1 行目として読む。
                // 左端は 12px 内側にし、隣のトーク一覧の端が欠けて写るのを避ける
                var top = winRect.Top < p.Rect.Top && p.Rect.Top - winRect.Top < 160 ? winRect.Top : p.Rect.Top;
                region = Rectangle.FromLTRB(p.Rect.Left + 12, top, p.Rect.Right, p.Rect.Bottom);
            }
            else if (region is null && ef.Width >= 200)
            {
                var vs = Native.VirtualScreen();
                region = Rectangle.FromLTRB(ef.Left - 8, vs.Top, ef.Right + 8, vs.Bottom);
            }
            if (region is null)
            {
                lock (lastRegionGate)
                {
                    if (lastRegion.TryGetValue(app.Pid, out var prev)) { region = prev; exclude = null; }
                }
            }
            Diag.LogIfChanged($"screen region kind={WinUia.Kind(focused)} focus={ef.Width}x{ef.Height} " +
                $"pane={(pane is { } q ? $"{q.Rect.Width}x{q.Rect.Height}" : "nil")} " +
                $"region={(region is { } rg ? $"{rg.Left},{rg.Top} {rg.Width}x{rg.Height}" : "nil")}");

            var raw = WinOcr.Read(hwnd, region, exclude, winRect);
            if (raw is not null)
            {
                var (contact, body) = SplitHeader(raw, name);
                text = body;
                hint = body + "\n" + hint;
                // 見出しだけで本文が残らなかったときは「読めた」ことにしない
                source = body.Length == 0 ? ContextSource.None : ContextSource.Window;
                contactName = contact ?? ContactDetector.LastSender(body, ownName);
                error = body.Length == 0 ? CaptureError.NoSelection : null;
                if (region is { } keep && keep.Height < 100000)
                {
                    lock (lastRegionGate) lastRegion[app.Pid] = keep;
                }
                Diag.Log($"screen text chars={body.Length}");
            }
            else if (!WinOcr.Available)
            {
                // Windows は画面の取り込みに許可が要らない。読めないのは文字認識の言語が入っていないときで、
                // 利用者に許可を求めても意味が無いので ScreenNotAllowed にはせず、通常の「読み取れなかった」案内のままにする
                Diag.LogIfChanged("screen text skipped (no recognizer language)");
            }
        }

        return new CapturedContext(app, target, name, windowTitle, text, PlatformDetector.Detect(name, hint), source, error, contactName);
    }
}
