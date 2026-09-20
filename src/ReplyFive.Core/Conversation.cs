using System.Text.Json.Serialization;

namespace ReplyFive.Core;

/// <summary>会話の 1 発言（付録BS）。role は other（相手）／me（自分）／unknown（判別できない行）。</summary>
public sealed class ConversationMessage
{
    public string Role { get; set; } = "unknown";
    public string? Sender { get; set; }
    public string Text { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    public ConversationMessage() { }
    public ConversationMessage(string role, string text, string? sender = null) { Role = role; Text = text; Sender = sender; }

    /// <summary>重なり判定用の同一性（時刻は見ない）。</summary>
    [JsonIgnore] public string Identity => Role + "\u0001" + (Sender ?? "") + "\u0001" + Text;

    public ContextMessage ToContext() => new(Role, Sender, Text);
}

/// <summary>相手ごとの会話の記録（付録BR／BS）。相手の発言／自分の発言に分けて端末内にだけ溜め、返信を作るときにその相手の直近の会話を渡す。</summary>
public sealed class ConversationEntry
{
    public Platform Platform { get; set; }
    public string ContactKey { get; set; } = "";
    public string? ContactName { get; set; }
    public string? AppName { get; set; }
    public List<ConversationMessage> Messages { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>画面から読んだ行を発言に分ける。名前らしい行（ContactDetector の判定）が送信者の切り替わり。
/// 「自分: 」「〈自分の名前〉: 」で始まる行、自分の名前の送信者行は me。連続する同じ話者の本文行は 1 発言にまとめる。</summary>
public static class ConversationParser
{
    public const string OwnLabel = "自分";
    static readonly HashSet<string> uiLines = ["メッセージを入力", "メッセージを入力...", "メッセージを入力…", "Type a message", "Message", "既読", "未読"];

    public static List<ConversationMessage> Parse(string text, string? ownName, string? contactName = null)
    {
        var out_ = new List<ConversationMessage>();
        var role = "unknown";
        string? sender = null;
        var buffer = new List<string>();
        var awaitingBody = false;
        void Flush()
        {
            var body = ConversationNoise.Clean(string.Join("\n", buffer)).Trim();
            if (body.Length > 0) out_.Add(new ConversationMessage(role, body, role == "me" ? null : sender));
            buffer.Clear();
        }
        var labels = new List<string> { OwnLabel };
        if (!string.IsNullOrEmpty(ownName)) labels.Add(ownName);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim(' ', '\t');
            if (line.Length == 0) continue;
            var rest = StripPrefix(line, labels);
            if (rest is not null)
            {
                if (role != "me" || sender is not null) { Flush(); role = "me"; sender = null; }
                awaitingBody = false;
                buffer.Add(rest);
                continue;
            }
            if (uiLines.Contains(line) || line.StartsWith("ここから未読", StringComparison.Ordinal) || ConversationNoise.IsNoise(line)) continue;
            var items = ContactDetector.Normalize(new VisibleItem(line)).ToList();
            if (items.Count == 0) continue;
            if (items.Count == 2) // 「名前: 本文」
            {
                Flush();
                awaitingBody = false;
                var name = items[0].Text;
                if (ContactDetector.IsOwn(name, ownName)) { role = "me"; sender = null; } else { role = "other"; sender = name; }
                buffer.Add(items[1].Text);
                continue;
            }
            var t = items[0].Text;
            // 送信者行（時刻・編集済み表示は落ちている）。ただし送信者行の直後の短い行（「うんうん」「了解」）は本文とみなす
            if (ContactDetector.IsNameCandidate(t) && (!awaitingBody || ContactDetector.IsOwn(t, ownName)))
            {
                Flush();
                if (ContactDetector.IsOwn(t, ownName)) { role = "me"; sender = null; } else { role = "other"; sender = t; }
                awaitingBody = true;
                continue;
            }
            awaitingBody = false;
            buffer.Add(t);
        }
        Flush();
        // 相手名だけ分かっていて送信者行が無い（メール等）ときは、unknown を相手の発言とみなす
        if (!string.IsNullOrEmpty(contactName))
            foreach (var m in out_) if (m.Role == "unknown") { m.Role = "other"; m.Sender = contactName; }
        return out_;
    }

    static string? StripPrefix(string line, List<string> labels)
    {
        foreach (var label in labels)
        {
            if (label.Length == 0) continue;
            foreach (var sep in new[] { ": ", "：", ":" })
                if (line.StartsWith(label + sep, StringComparison.Ordinal)) return line[(label.Length + sep.Length)..].Trim();
        }
        return null;
    }

    /// <summary>サーバへ渡す／表示する会話形式の文字列。「相手名: 本文」「自分: 本文」を 1 行ずつ。</summary>
    public static string Render(IEnumerable<ConversationMessage> messages, string ownLabel = OwnLabel, string otherFallback = "相手")
        => string.Join("\n", messages.Select(m =>
        {
            var who = m.Role == "me" ? ownLabel : (string.IsNullOrEmpty(m.Sender) ? otherFallback : m.Sender);
            return who + ": " + m.Text.Replace("\n", "\n    ");
        }));
}

public sealed class ConversationStore : IDisposable
{
    public const int MaxContacts = 50;
    public const int MaxMessagesPerContact = 200;
    public const int MaxCharsPerContact = 20_000;

    readonly EncryptedJsonFile file;
    readonly object gate = new();
    List<ConversationEntry>? cache;
    bool dirty;
    Timer? flushTimer;

    public ConversationStore(string path, byte[] key) { file = new EncryptedJsonFile(path, key); }

    /// <summary>付録BU：復号と JSON の読み込みは初回だけ。以後はメモリのキャッシュを使い、書き込みは 1.5 秒まとめて行う。</summary>
    public List<ConversationEntry> Load()
    {
        lock (gate)
        {
            cache ??= file.Load<List<ConversationEntry>>() ?? [];
            return cache.Select(e => e).ToList();
        }
    }

    void Save(List<ConversationEntry> items)
    {
        lock (gate)
        {
            cache = items.OrderByDescending(e => e.UpdatedAt).Take(MaxContacts).ToList();
            dirty = true;
            flushTimer ??= new Timer(_ => Flush(), null, 1500, Timeout.Infinite);
        }
    }

    /// <summary>すぐに書き出す（終了時・テスト用）。</summary>
    public void Flush()
    {
        List<ConversationEntry>? items;
        lock (gate)
        {
            flushTimer?.Dispose(); flushTimer = null;
            if (!dirty || cache is null) return;
            dirty = false;
            items = cache.ToList();
        }
        try { file.Save(items); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() => Flush();

    int FindIndex(List<ConversationEntry> items, Platform platform, string contactKey, string? contactName, out bool fuzzy)
    {
        fuzzy = false;
        var idx = items.FindIndex(e => e.Platform == platform && e.ContactKey == contactKey);
        // 付録BU：画面の文字認識では相手名が数文字ぶれる（「巧巳」→「巧日」）。同じアプリで名前がほぼ同じ相手がいればそこへ寄せる
        if (idx < 0 && contactName is not null && contactName.Length >= 4)
        {
            var a = EditDistance.Elements(contactName);
            idx = items.FindIndex(e =>
            {
                if (e.Platform != platform || e.ContactName is null) return false;
                var n = EditDistance.Elements(e.ContactName);
                if (Math.Abs(n.Length - a.Length) > 2) return false;
                return EditDistance.Levenshtein(a, n) <= Math.Max(1, a.Length / 6);
            });
            fuzzy = idx >= 0;
        }
        return idx;
    }

    /// <summary>画面から読んだ発言列を取り込み、増えた発言数を返す。前回の末尾と今回の先頭の重なりの先だけを足す。</summary>
    public int Ingest(Platform platform, string contactKey, string? contactName, string? appName, List<ConversationMessage> snapshot)
    {
        if (snapshot.Count == 0) return 0;
        lock (gate)
        {
            var items = Load();
            var idx = FindIndex(items, platform, contactKey, contactName, out var fuzzy);
            var entry = idx >= 0 ? items[idx] : new ConversationEntry { Platform = platform, ContactKey = contactKey, ContactName = contactName, AppName = appName };
            var merged = Merge(entry.Messages, snapshot);
            if (merged.Select(m => m.Identity).SequenceEqual(entry.Messages.Select(m => m.Identity))) return 0;
            var added = merged.Count - entry.Messages.Count;
            entry.Messages = Trim(merged);
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            if (!string.IsNullOrEmpty(contactName) && !fuzzy) entry.ContactName = contactName;
            if (appName is not null) entry.AppName = appName;
            if (idx >= 0) items[idx] = entry; else items.Add(entry);
            Save(items);
            return Math.Max(added, 0);
        }
    }

    /// <summary>付録BV：snapshot のうち、この相手の記録にまだ無い発言（サーバの判定に回す対象）。</summary>
    public List<ConversationMessage> NewMessages(Platform platform, string contactKey, string? contactName, List<ConversationMessage> snapshot)
    {
        var items = Load();
        var idx = FindIndex(items, platform, contactKey, contactName, out _);
        if (idx < 0) return snapshot;
        var known = items[idx].Messages.Select(m => m.Identity).ToHashSet();
        return snapshot.Where(m => !known.Contains(m.Identity)).ToList();
    }

    /// <summary>自分が挿入・コピーした返信を、その相手との会話の末尾に自分の発言として足す（連続して返信するときの文脈）。</summary>
    public void AppendOwn(Platform platform, string contactKey, string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return;
        lock (gate)
        {
            var items = Load();
            var idx = items.FindIndex(e => e.Platform == platform && e.ContactKey == contactKey);
            var entry = idx >= 0 ? items[idx] : new ConversationEntry { Platform = platform, ContactKey = contactKey };
            var last = entry.Messages.LastOrDefault();
            if (last is not null && last.Role == "me" && last.Text == t) return;
            entry.Messages = Trim([.. entry.Messages, new ConversationMessage("me", t)]);
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            if (idx >= 0) items[idx] = entry; else items.Add(entry);
            Save(items);
        }
    }

    /// <summary>その相手の直近の発言列（末尾 maxChars 文字ぶん）。</summary>
    public List<ConversationMessage> RecentMessages(Platform platform, string contactKey, int maxChars = 5000)
    {
        var e = Load().FirstOrDefault(x => x.Platform == platform && x.ContactKey == contactKey);
        if (e is null) return [];
        var out_ = new List<ConversationMessage>();
        var total = 0;
        for (var i = e.Messages.Count - 1; i >= 0; i--)
        {
            var m = e.Messages[i];
            if (total + m.Text.Length + 12 > maxChars) break;
            out_.Add(m);
            total += m.Text.Length + 12;
        }
        out_.Reverse();
        return out_;
    }

    public string? Recent(Platform platform, string contactKey, int maxChars = 5000)
    {
        var m = RecentMessages(platform, contactKey, maxChars);
        return m.Count == 0 ? null : ConversationParser.Render(m);
    }

    public int MessageCount(Platform platform, string contactKey) => Load().FirstOrDefault(e => e.Platform == platform && e.ContactKey == contactKey)?.Messages.Count ?? 0;
    public int ContactCount => Load().Count;
    public int TotalMessages => Load().Sum(e => e.Messages.Count);

    public void DeleteAll()
    {
        lock (gate) { cache = []; dirty = false; flushTimer?.Dispose(); flushTimer = null; file.Delete(); }
    }

    public static List<ConversationMessage> Merge(List<ConversationMessage> existing, List<ConversationMessage> snapshot)
    {
        if (existing.Count == 0) return snapshot.ToList();
        var e = existing.Select(m => m.Identity).ToArray();
        var s = snapshot.Select(m => m.Identity).ToArray();
        var maxK = Math.Min(e.Length, s.Length);
        for (var k = maxK; k >= 1; k--)
            if (e.Skip(e.Length - k).SequenceEqual(s.Take(k))) return [.. existing, .. snapshot.Skip(k)];
        for (var k = maxK; k >= 1; k--)
            if (s.Skip(s.Length - k).SequenceEqual(e.Take(k))) return [.. snapshot.Take(snapshot.Count - k), .. existing];
        var known = e.ToHashSet();
        var fresh = snapshot.Where(m => !known.Contains(m.Identity)).ToList();
        return fresh.Count == 0 ? existing.ToList() : [.. existing, .. fresh];
    }

    internal static List<ConversationMessage> Trim(List<ConversationMessage> messages)
    {
        var out_ = messages.Skip(Math.Max(0, messages.Count - MaxMessagesPerContact)).ToList();
        var chars = out_.Sum(m => m.Text.Length);
        while (chars > MaxCharsPerContact && out_.Count > 0) { chars -= out_[0].Text.Length; out_.RemoveAt(0); }
        return out_;
    }
}
