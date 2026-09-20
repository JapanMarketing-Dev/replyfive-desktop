using System.Text.Json.Serialization;

namespace ReplyFive.Core;

/// <summary>相手別の返信の記録（付録BG）。「どのアプリで誰にどう返したか」を、挿入・コピーのたびに利用者へ何も聞かずに
/// AES-GCM で暗号化した 1 ファイルへ最大 30 件保存する。会話文脈の本文は保存しない（識別用のプラットフォーム・アプリ名・相手キー（ハッシュ）だけ）。
/// Corrected は利用者が実際に入れた最終文。修正が無ければ生成文と同じ。</summary>
public sealed class StoredExample
{
    public string Intent { get; set; } = "";
    public string Corrected { get; set; } = "";
    public Platform Platform { get; set; }
    public string? ContactKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Generated { get; set; }
    public string? AppName { get; set; }
    public int? EditChars { get; set; }
    public double? EditRatio { get; set; }
    public string? Action { get; set; } // insert | copy
    public bool? Sent { get; set; }
    public DateTimeOffset? SentAt { get; set; }

    /// <summary>修正なし（生成文をそのまま使った）か。旧版の記録は修正ありとして扱う。</summary>
    [JsonIgnore] public bool Unedited => (EditChars ?? 1) == 0;
}

/// <summary>端末内の修正 KPI（付録BG）。本文を含まない数値だけ。</summary>
public readonly record struct ReplyRecordKPI(int Count, int Unedited, int EditCharsTotal, int Inserted = 0, int Sent = 0)
{
    public double UneditedRate => Count > 0 ? (double)Unedited / Count : 0;
    public double AverageEditChars => Count > 0 ? (double)EditCharsTotal / Count : 0;
    public double SentRate => Inserted > 0 ? (double)Sent / Inserted : 0;
}

public sealed class LearningStore
{
    public const int MaxStored = 30;
    public const int MaxSent = 10; // 付録BD：サーバが関連度で 3 件に絞る
    readonly EncryptedJsonFile file;
    readonly object gate = new();

    public LearningStore(string path, byte[] key) { file = new EncryptedJsonFile(path, key); }

    public List<StoredExample> Load()
    {
        lock (gate) return file.Load<List<StoredExample>>() ?? [];
    }

    public void Save(List<StoredExample> items)
    {
        lock (gate) file.Save(items.OrderByDescending(e => e.CreatedAt).Take(MaxStored).ToList());
    }

    /// <summary>挿入・コピーのたびに記録する（付録BG）。insert は実送信をまだ確認していない状態で保存する。同じ intent と会話の古い例は置き換える。</summary>
    public void Record(string intent, string generated, string final, Platform platform, string? contactKey = null, string? appName = null, string? action = null)
    {
        var g = generated.Trim();
        var f = final.Trim();
        if (f.Length == 0 || intent.Length == 0) return;
        lock (gate)
        {
            var chars = EditDistance.Levenshtein(g, f);
            var items = Load().Where(e => !(e.Intent == intent && e.Platform == platform && e.ContactKey == contactKey)).ToList();
            items.Insert(0, new StoredExample
            {
                Intent = ContractJson.Prefix(intent, 500), Corrected = ContractJson.Prefix(f, 1000), Platform = platform, ContactKey = contactKey,
                Generated = ContractJson.Prefix(g, 1000), AppName = appName, EditChars = chars, EditRatio = EditDistance.Ratio(g, f),
                Action = action, Sent = action == "insert" ? false : null,
            });
            Save(items);
        }
    }

    /// <summary>会話収集で、挿入した文面と同じ自分の発言が現れたときだけ実送信済みに更新する。</summary>
    public bool MarkSent(Platform platform, string contactKey, string text, DateTimeOffset? at = null)
    {
        var value = text.Trim();
        if (value.Length == 0) return false;
        var now = at ?? DateTimeOffset.UtcNow;
        lock (gate)
        {
            var items = Load();
            var index = items.FindIndex(e => e.Platform == platform && e.ContactKey == contactKey && e.Action == "insert" && e.Sent != true
                                             && (now - e.CreatedAt).TotalSeconds >= 2 && e.Corrected.Trim() == value);
            if (index < 0) return false;
            items[index].Sent = true;
            items[index].SentAt = now;
            Save(items);
            return true;
        }
    }

    /// <summary>直近 days 日の修正 KPI（本文なし）。</summary>
    public ReplyRecordKPI Kpi(int days)
    {
        var from = DateTimeOffset.UtcNow.AddDays(-days);
        var recent = Load().Where(e => e.CreatedAt >= from).ToList();
        return new ReplyRecordKPI(recent.Count, recent.Count(e => e.Unedited), recent.Sum(e => e.EditChars ?? 0),
                                  recent.Count(e => e.Action == "insert"), recent.Count(e => e.Action == "insert" && e.Sent == true));
    }

    /// <summary>送信用。同じ相手（contactKey が一致）の例だけを最大 10 件（付録BN）。相手が分からないときは何も送らない。</summary>
    public List<LearnedExample> Examples(Platform platform, string? contactKey = null)
    {
        if (string.IsNullOrEmpty(contactKey)) return [];
        return Load().Where(e => e.Platform == platform && e.ContactKey == contactKey).Take(MaxSent).Select(e => new LearnedExample(e.Intent, e.Corrected)).ToList();
    }

    public int CountFor(Platform platform, string contactKey) => Load().Count(e => e.Platform == platform && e.ContactKey == contactKey);

    /// <summary>設定画面で、直近の操作結果（本文は表示しない）を確認するための一覧。</summary>
    public List<StoredExample> Latest(int limit = 10) => limit <= 0 ? [] : Load().Take(limit).ToList();

    public void DeleteAll() { lock (gate) file.Delete(); }

    public int Count => Load().Count;
}

/// <summary>付録CD：初回設定で利用者が自分の言葉で書いた返信のサンプル。文体の判定にだけ使う。</summary>
public sealed class StyleSample
{
    public string Scenario { get; set; } = "";   // email_customer / chat_colleague / chat_partner
    public string Received { get; set; } = "";
    public string Reply { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class StyleStore
{
    public const int MaxStored = 5;
    readonly EncryptedJsonFile file;
    readonly object gate = new();

    public StyleStore(string path, byte[] key) { file = new EncryptedJsonFile(path, key); }

    public List<StyleSample> Load() { lock (gate) return file.Load<List<StyleSample>>() ?? []; }

    public void Save(List<StyleSample> items) { lock (gate) file.Save(items.Take(MaxStored).ToList()); }

    /// <summary>同じシナリオの古いサンプルを置き換える。空の返信は保存しない。</summary>
    public void Put(string scenario, string received, string reply)
    {
        var r = reply.Trim();
        if (r.Length == 0) return;
        lock (gate)
        {
            var items = Load().Where(s => s.Scenario != scenario).ToList();
            items.Add(new StyleSample { Scenario = scenario, Received = ContractJson.Prefix(received, 2000), Reply = ContractJson.Prefix(r, 1000) });
            Save(items);
        }
    }

    public void DeleteAll() { lock (gate) file.Delete(); }
    public int Count => Load().Count;

    /// <summary>判定リクエストの形にする。</summary>
    public StyleProfileRequest Request() => new() { Samples = Load().Select(s => new StyleProfileRequest.Sample(s.Received, s.Reply)).ToList() };
}
