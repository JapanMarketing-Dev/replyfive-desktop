using System.Security.Cryptography;
using System.Text;

namespace ReplyFive.Core;

/// <summary>収集済み会話から作る、本文を外へ表示しない評価用の 1 組。Intent は直前の相手の発言、Expected は実際に自分が返した発言。</summary>
public sealed record ConversationEvaluationPair(string Id, string Intent, string Expected, List<ConversationMessage> Context);

public sealed record ConversationEvaluationDataset(List<ConversationEvaluationPair> Teacher, List<ConversationEvaluationPair> Test);

public static class ConversationEvaluation
{
    public const string Version = "names-v9";

    /// <summary>会話の順序を保ったまま、内容のハッシュで固定的に 80/20 分割する。新しい発言が増えても既存組の所属が変わらない。</summary>
    public static ConversationEvaluationDataset Dataset(Platform platform, string contactKey, List<ConversationMessage> messages, int maxPairs = 30)
    {
        var pairs = new List<ConversationEvaluationPair>();
        ConversationMessage? lastOther = null;
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role == "other") { lastOther = messages[i]; continue; }
            if (messages[i].Role != "me" || lastOther is null || i == 0) continue;
            var context = messages.Skip(Math.Max(0, i - 8)).Take(i - Math.Max(0, i - 8)).ToList();
            var seed = string.Join("\u0001", Version, platform.Wire(), contactKey, lastOther.Identity, messages[i].Identity);
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant()[..24];
            pairs.Add(new ConversationEvaluationPair(id, lastOther.Text, messages[i].Text, context));
        }
        var seen = new HashSet<string>();
        var unique = pairs.Where(p => seen.Add(p.Id)).Take(maxPairs).ToList();
        if (unique.Count <= 1) return new ConversationEvaluationDataset(unique, []);
        var teacher = new List<ConversationEvaluationPair>();
        var test = new List<ConversationEvaluationPair>();
        foreach (var pair in unique)
        {
            var b = SHA256.HashData(Encoding.UTF8.GetBytes(pair.Id))[0];
            if (b % 5 == 0) test.Add(pair); else teacher.Add(pair);
        }
        if (test.Count == 0 && teacher.Count > 0) { test.Add(teacher[^1]); teacher.RemoveAt(teacher.Count - 1); }
        if (teacher.Count == 0 && test.Count > 0) { teacher.Add(test[^1]); test.RemoveAt(test.Count - 1); }
        return new ConversationEvaluationDataset(teacher, test);
    }

    public static List<ConversationEvaluationPair> PendingTests(ConversationEvaluationDataset dataset, ISet<string> completedIds, int max = 3)
        => dataset.Test.Where(p => !completedIds.Contains(p.Id)).Take(max).ToList();
}

public sealed class BackgroundEvaluationRecord
{
    public string? Version { get; set; }
    public string Id { get; set; } = "";
    public bool Passed { get; set; }
    public bool Verified { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public Platform? Platform { get; set; }
    public string? ContactName { get; set; }
    public string? AppName { get; set; }
    /// <summary>設定画面で確認するための暗号化された生成例。対象アプリへは渡さない。</summary>
    public string? Preview { get; set; }
}

/// <summary>バックグラウンド評価の印と、設定画面で確認する生成例を暗号化保存する。</summary>
public sealed class BackgroundEvaluationStore
{
    readonly EncryptedJsonFile file;
    readonly object gate = new();
    List<BackgroundEvaluationRecord>? cache;

    public BackgroundEvaluationStore(string path, byte[] key) { file = new EncryptedJsonFile(path, key); }

    public List<BackgroundEvaluationRecord> Load()
    {
        lock (gate) { cache ??= file.Load<List<BackgroundEvaluationRecord>>() ?? []; return cache.ToList(); }
    }

    public bool Contains(string id) => Load().Any(r => r.Version == ConversationEvaluation.Version && r.Id == id && r.Passed && r.Verified);

    public HashSet<string> CompletedIds() => Load().Where(r => r.Version == ConversationEvaluation.Version && r.Passed && r.Verified).Select(r => r.Id).ToHashSet();

    public void Record(string id, bool passed, bool verified, Platform? platform = null, string? contactName = null, string? appName = null, string? preview = null)
    {
        lock (gate)
        {
            var rows = Load();
            rows.RemoveAll(r => r.Id == id);
            rows.Insert(0, new BackgroundEvaluationRecord
            {
                Version = ConversationEvaluation.Version, Id = id, Passed = passed, Verified = verified, Platform = platform, ContactName = contactName, AppName = appName,
                Preview = preview is null ? null : ContractJson.Prefix(preview, 2000),
            });
            cache = rows.Take(200).ToList();
            try { file.Save(cache); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    public List<BackgroundEvaluationRecord> Latest(int limit = 10) => Load().Where(r => r.Version == ConversationEvaluation.Version).Take(Math.Max(0, limit)).ToList();
}
