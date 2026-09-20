namespace ReplyFive.Core;

/// <summary>付録CF：初回設定「会話を見せてください」の候補。収集済みの会話（付録BR）から、相手の発言で終わる形に切り出したもの。
/// 本文は端末内だけで使う（macOS 版 OnboardingReview.swift の移植）。</summary>
public sealed record ReviewCandidate(
    Platform Platform, string ContactKey, string? ContactName, string? AppName,
    /// <summary>相手の最後の発言（返信案はこれへの返事）</summary>
    string LastReceived,
    /// <summary>相手の最後の発言までの会話（末尾 maxChars ぶん）</summary>
    IReadOnlyList<ConversationMessage> Context,
    /// <summary>一覧で見せる直近のやり取り（相手の発言と自分の返信を含む末尾数件）</summary>
    IReadOnlyList<ConversationMessage> Exchange,
    DateTimeOffset UpdatedAt)
{
    public string Id => ContractJson.Wire(Platform) + "\u0001" + ContactKey;
}

/// <summary>付録CF-6／CF-8：「会話を見せる」の進み具合（選んだアプリごと）。相手の数と、自分の返信が入っている相手の数。</summary>
public sealed record AppProgress(AppCatalogEntry App, int Contacts, int WithOwnReply)
{
    /// <summary>このアプリぶんは足りたか（自分の返信が入った相手が 1 人以上）</summary>
    public bool Done => WithOwnReply >= 1;
}

public static class OnboardingReview
{
    /// <summary>一定量の目安：選んだアプリすべてに自分の返信入りの相手が 1 人以上、かつ全体で MinContacts 人以上。
    /// 使っていないアプリを選んでいても、全体で EnoughContacts 人に達したら足りたとみなす。</summary>
    public const int MinContacts = 2;
    public const int EnoughContacts = 5;

    /// <summary>背景収集が相手名として拾ってしまう UI 文言を除く。人名・会社名に普通は現れない語（助詞「を」、丁寧語の語尾、相対時刻、件数）で判定する。</summary>
    static readonly string[] uiFragments = ["件の返信", "時間前", "分前", "日前", "秒前", "ありません", "ください", "します", "しました", "する", "メッセージ", "スレッド", "返信", "既読", "未読", "リマインダー", "を", "Reply", "replies", "ago", "Thread", "Channels", "チャネル", "チャンネル一覧", "Teams and", "Unread", "Activity"];

    public static bool IsPersonLikeName(string? name)
    {
        var t = (name ?? "").Trim();
        if (t.Length == 0 || t.Length > 40) return false;
        if (t.Any(char.IsDigit)) return false;
        foreach (var f in uiFragments) if (t.Contains(f, StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>一覧用：相手の発言と自分の返信が両方入るように末尾から拾う（最大 max 件、unknown は除く）。</summary>
    public static List<ConversationMessage> RecentExchange(IReadOnlyList<ConversationMessage> messages, int max = 4, string? contactName = null)
    {
        // 見出し（チャンネル名・相手名そのもの）が発言として拾われていれば一覧には出さない
        var known = messages.Where(m => (m.Role == "other" || m.Role == "me") && m.Text.Trim().Length > 0 && !IsHeading(m.Text, contactName)).ToList();
        return known.Skip(Math.Max(0, known.Count - max)).ToList();
    }

    static bool IsHeading(string text, string? contactName)
    {
        var t = text.Trim();
        return contactName is { Length: > 0 } c && (t == c.Trim() || t == "#" + c.Trim() || "#" + t == c.Trim());
    }

    /// <summary>相手の発言が 1 つ以上ある相手を、新しい順に最大 max 人。自分の発言で終わっていれば、最後の相手の発言までで切る。</summary>
    public static List<ReviewCandidate> Candidates(IEnumerable<ConversationEntry> entries, int max = 5, int maxChars = 5000)
    {
        var outList = new List<ReviewCandidate>();
        foreach (var e in entries.OrderByDescending(e => e.UpdatedAt))
        {
            if (!IsPersonLikeName(e.ContactName)) continue; // 相手名が UI 文言（「1件の返信 22時間前」等）の記録は見せない
            var messages = e.Messages.Where(m => !IsHeading(m.Text, e.ContactName)).ToList();
            var lastOther = messages.FindLastIndex(m => m.Role == "other" && m.Text.Trim().Length > 0);
            if (lastOther < 0) continue;
            var upTo = messages.Take(lastOther + 1).ToList();
            var context = new List<ConversationMessage>();
            var total = 0;
            for (var i = upTo.Count - 1; i >= 0; i--)
            {
                var m = upTo[i];
                if (total + m.Text.Length + 12 > maxChars && context.Count > 0) break;
                context.Add(m);
                total += m.Text.Length + 12;
            }
            context.Reverse();
            outList.Add(new ReviewCandidate(e.Platform, e.ContactKey, e.ContactName, e.AppName, upTo[lastOther].Text, context, RecentExchange(e.Messages, contactName: e.ContactName), e.UpdatedAt));
            if (outList.Count >= max) break;
        }
        return outList;
    }

    public static List<AppProgress> Progress(IEnumerable<ConversationEntry> entries, IReadOnlyList<AppCatalogEntry> selected)
    {
        var usable = Candidates(entries, int.MaxValue);
        return selected.Select(app =>
        {
            var mine = usable.Where(c => app.Matches(c.AppName, c.Platform)).ToList();
            return new AppProgress(app, mine.Count, mine.Count(c => c.Exchange.Any(m => m.Role == "me")));
        }).ToList();
    }

    public static bool IsEnough(IReadOnlyList<AppProgress> progress)
    {
        var total = progress.Sum(p => p.WithOwnReply);
        if (total >= EnoughContacts) return true;
        return progress.Count > 0 && progress.All(p => p.Done) && total >= MinContacts;
    }

    /// <summary>返し方の判定に使う実際のやり取り：相手の発言 → 直後の自分の返信。新しい相手から順に最大 max 組（相手 1 人につき 1 組）。</summary>
    public static List<(string Scenario, string Received, string Reply)> StyleSamples(IEnumerable<ConversationEntry> entries, int max = 5)
    {
        var outList = new List<(string, string, string)>();
        foreach (var e in entries.OrderByDescending(e => e.UpdatedAt))
        {
            if (outList.Count >= max) break;
            if (!IsPersonLikeName(e.ContactName)) continue;
            ConversationMessage? lastOther = null;
            (string, string)? pair = null;
            foreach (var m in e.Messages)
            {
                if (m.Role == "other") { lastOther = m; continue; }
                if (m.Role == "me" && lastOther is not null && m.Text.Trim().Length > 0) pair = (lastOther.Text, m.Text);
            }
            if (pair is { } p) outList.Add((ContractJson.Wire(e.Platform) + ":" + e.ContactKey, p.Item1, p.Item2));
        }
        return outList;
    }
}
