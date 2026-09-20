using System.Text;
using System.Text.RegularExpressions;

namespace ReplyFive.Core;

/// <summary>前面ウインドウから読んだ可視要素 1 件。Control はボタン・リンクの表題（Slack の送信者名など）。</summary>
public readonly record struct VisibleItem(string Text, VisibleItem.ItemKind Kind = VisibleItem.ItemKind.Text)
{
    public enum ItemKind { Text, Control }
}

/// <summary>付録BE：入力欄の直上にある最後のメッセージの送信者（＝返信相手）を、可視テキストから推定する。
/// 画面構造は「送信者名 → 時刻 → 本文」の並びが多い（Slack・Teams・Chatwork・LINE WORKS・メール）。
/// 末尾の本文を見つけ、そこから遡って最初の「名前らしい短い行」を相手とする。自分の名前と一致するものは飛ばす。判定できなければ null。</summary>
public static class ContactDetector
{
    public const int MaxNameLength = 40;

    public static string? LastSender(string text, string? ownName = null)
        => LastSender(text.Split('\n').Where(l => l.Length > 0).Select(l => new VisibleItem(l)).ToList(), ownName);

    public static string? LastSender(IReadOnlyList<VisibleItem> raw, string? ownName = null)
    {
        var items = raw.SelectMany(Normalize).ToList();
        if (items.Count == 0) return null;
        // メールは「差出人:」「From:」のラベル付きが最も確実
        for (var i = items.Count - 1; i >= 0; i--)
        {
            var name = FromLabel(items[i].Text);
            if (name is not null) { if (!IsOwn(name, ownName)) return name; break; }
        }
        var lastBody = -1;
        for (var i = items.Count - 1; i >= 0; i--) if (IsBody(items[i])) { lastBody = i; break; }
        if (lastBody < 0)
        {
            // 本文が無い（名前だけ並ぶ）ときは末尾の名前候補
            for (var i = items.Count - 1; i >= 0; i--) if (IsNameCandidate(items[i].Text) && !IsOwn(items[i].Text, ownName)) return items[i].Text;
            return null;
        }
        for (var i = lastBody - 1; i >= 0; i--)
        {
            var t = items[i].Text;
            if (IsNameCandidate(t) && !IsOwn(t, ownName)) return t;
        }
        return null;
    }

    // MARK: - 正規化

    static readonly Regex trailingTime = new(@"\s*(午前|午後|AM|PM|am|pm)?\s*\d{1,2}:\d{2}(\s*(AM|PM|am|pm))?\s*$", RegexOptions.Compiled);
    static readonly string[] editedMarks = ["(edited)", "（編集済み）", "(編集済み)", "(編集済)", "(you)", "(あなた)", "(自分)"];
    static readonly Regex nameBody = new(@"^([^:：]{1,40})[:：]\s*(\S.*)$", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>1 要素を整える。「名前: 本文」の 1 行は名前と本文に分ける。時刻・編集済み表示・末尾の括弧書きを落とす。</summary>
    public static IEnumerable<VisibleItem> Normalize(VisibleItem item)
    {
        var t = item.Text.Trim();
        if (t.Length == 0 || IsNoise(t)) return [];
        var m = nameBody.Match(t);
        if (m.Success && !char.IsDigit(m.Groups[1].Value[^1])) // 「佐藤 10:24」の時刻のコロンは区切りではない
        {
            var name = CleanName(m.Groups[1].Value);
            var body = m.Groups[2].Value.Trim();
            if (IsNameCandidate(name) && body.Length > 0 && FromLabel(t) is null)
                return [new VisibleItem(name, item.Kind), new VisibleItem(body, VisibleItem.ItemKind.Text)];
        }
        t = CleanName(t);
        return t.Length == 0 ? [] : [new VisibleItem(t, item.Kind)];
    }

    public static string CleanName(string s)
    {
        var t = s.Trim();
        foreach (var mark in editedMarks) if (t.EndsWith(mark, StringComparison.Ordinal)) t = t[..^mark.Length].Trim();
        t = trailingTime.Replace(t, "");
        // 末尾の括弧書き（役職・組織）は落とす：「佐藤 (営業)」→「佐藤」
        if (t.Length > 0 && (t[^1] == ')' || t[^1] == '）'))
        {
            var open = t.LastIndexOfAny(['(', '（']);
            if (open > 0) t = t[..open];
        }
        if (t.StartsWith('@')) t = t[1..];
        return t.Trim();
    }

    // MARK: - 判定

    static readonly HashSet<string> uiWords =
    [
        "reply", "replies", "thread", "threads", "message", "messages", "send", "sent", "edit", "edited", "delete", "more", "new", "today", "yesterday",
        "unread", "online", "away", "you", "me", "to", "from", "cc", "bcc", "re", "fw", "fwd", "draft", "drafts", "inbox", "starred", "important", "all",
        "返信", "スレッド", "送信", "送信済み", "編集", "削除", "その他", "新着", "今日", "昨日", "未読", "既読", "メッセージ", "自分", "あなた", "宛先",
        "件名", "下書き", "受信トレイ", "重要", "すべて", "オンライン", "離席中", "添付", "ファイル", "画像", "リアクション", "ピン留め", "ブックマーク",
    ];
    static readonly Regex[] noisePatterns = new[]
    {
        @"^\d+\s*(replies|reply|reactions?|件の返信|件のリアクション|返信)$",
        @"^(last reply|最終返信|new messages|新着メッセージ|new|新規)\b.*$",
        @"^(https?://|www\.)",
        @"^[\d\s:./\-年月日曜()（）,]+$",                       // 日付・時刻だけ
        @"^(mon|tue|wed|thu|fri|sat|sun)[a-z]*(day)?,?\s+\w+\s+\d+.*$",  // "Monday, September 15th"
        @"^\d{1,2}月\d{1,2}日.*$",
        @"^[\p{So}\p{Sk}\s\d]+$",                              // 絵文字・記号・数字だけ
        @"^(返信元|メッセージリンク|引用|quoted|replied to|thread|スレッド|既読|read|edited|編集済み|ボイスメッセージ|voice message)\b.*$",
    }.Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled)).ToArray();
    static readonly HashSet<char> sentencePunctuation = [.. "。、．，！？!?,.;:；：「」『』〈〉《》【】〔〕()（）[]{}<>\"'“”‘’…・/\\|=+*&%$#~^`"];

    public static bool IsNoise(string t) => noisePatterns.Any(p => p.IsMatch(t));

    static bool HasLetter(string s) => s.EnumerateRunes().Any(Rune.IsLetter);

    /// <summary>名前らしさ：短い、句読点や記号が無い、文字を含む、UI の定型語ではない。</summary>
    public static bool IsNameCandidate(string s)
    {
        var t = s.Trim();
        if (t.Length == 0 || t.Length > MaxNameLength || IsNoise(t)) return false;
        if (t.Any(sentencePunctuation.Contains)) return false;
        if (!HasLetter(t)) return false;
        var words = t.Split([' ', '　'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 4) return false;
        if (uiWords.Contains(t.ToLowerInvariant())) return false;
        if (words.Length <= 2 && words.All(w => uiWords.Contains(w.ToLowerInvariant()))) return false;
        if (t.EndsWith('…') || t.EndsWith("...", StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>本文らしさ：名前候補ではなく、文字を含む。</summary>
    public static bool IsBody(VisibleItem item)
    {
        var t = item.Text;
        if (t.Length == 0 || IsNoise(t) || item.Kind != VisibleItem.ItemKind.Text) return false;
        if (IsNameCandidate(t)) return false;
        return HasLetter(t);
    }

    static readonly string[] fromLabels = ["from:", "差出人:", "差出人：", "from：", "送信者:", "送信者："];
    public static string? FromLabel(string s)
    {
        var lower = s.ToLowerInvariant();
        foreach (var l in fromLabels)
        {
            if (!lower.StartsWith(l, StringComparison.Ordinal)) continue;
            var rest = s[l.Length..].Trim();
            var lt = rest.IndexOf('<');
            if (lt >= 0) rest = rest[..lt].Trim(); // "佐藤 <sato@example.com>" → 佐藤
            rest = CleanName(rest);
            return rest.Length == 0 || rest.Length > MaxNameLength ? null : rest;
        }
        return null;
    }

    /// <summary>自分の名前は複数の表記をカンマ・読点・スラッシュ区切りで持てる（例：「遠藤巧巳, Takumi Endoh, takumi」）。</summary>
    public static string[] OwnAliases(string? own)
        => (own ?? "").Split([',', '、', '/', '，']).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();

    public static bool IsOwn(string name, string? own)
    {
        var n = name.ToLowerInvariant().Trim();
        if (n is "you" or "me" or "自分" or "あなた") return true;
        var aliases = OwnAliases(own);
        if (aliases.Length > 1) return aliases.Any(a => IsOwn(name, a));
        var o = own?.ToLowerInvariant().Trim();
        if (string.IsNullOrEmpty(o)) return false;
        if (n == o) return true;
        var a = n.Replace(" ", ""); var b = o.Replace(" ", "");
        if (a == b) return true;
        // 「遠藤巧巳」と「遠藤」、"Takumi Endoh" と "Takumi" のような部分一致（2 文字以上）
        if (b.Length >= 2 && a.Contains(b)) return true;
        if (a.Length >= 2 && b.Contains(a)) return true;
        return false;
    }
}
