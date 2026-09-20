using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReplyFive.Core;

/// <summary>成長計測（docs/marketing/growth-analytics.md）。本文は一切含めない。名前は固定語彙、props は文字列・数値・真偽値だけ。</summary>
[JsonConverter(typeof(GrowthPropConverter))]
public readonly struct GrowthProp
{
    public string? S { get; }
    public double? N { get; }
    public bool? B { get; }
    GrowthProp(string? s, double? n, bool? b) { S = s; N = n; B = b; }
    public static GrowthProp Str(string s) => new(s, null, null);
    public static GrowthProp Num(double n) => new(null, n, null);
    public static GrowthProp Bool(bool b) => new(null, null, b);
    public static implicit operator GrowthProp(string s) => Str(s);
    public static implicit operator GrowthProp(double n) => Num(n);
    public static implicit operator GrowthProp(int n) => Num(n);
    public static implicit operator GrowthProp(bool b) => Bool(b);
}

sealed class GrowthPropConverter : JsonConverter<GrowthProp>
{
    public override GrowthProp Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.True => GrowthProp.Bool(true),
        JsonTokenType.False => GrowthProp.Bool(false),
        JsonTokenType.Number => GrowthProp.Num(reader.GetDouble()),
        _ => GrowthProp.Str(reader.GetString() ?? ""),
    };
    public override void Write(Utf8JsonWriter writer, GrowthProp value, JsonSerializerOptions options)
    {
        if (value.B is bool b) writer.WriteBooleanValue(b);
        else if (value.N is double n) writer.WriteNumberValue(n);
        else writer.WriteStringValue(ContractJson.Prefix(value.S ?? "", 64));
    }
}

public sealed class GrowthEvent
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("ts")] public string Ts { get; set; } = "";
    [JsonPropertyName("props")] public Dictionary<string, GrowthProp> Props { get; set; } = [];
    public GrowthEvent() { }
    public GrowthEvent(string name, Dictionary<string, GrowthProp>? props = null, DateTimeOffset? ts = null)
    {
        Name = name; Props = props ?? []; Ts = (ts ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    }
}

public sealed class EventsRequest
{
    [JsonPropertyName("install_id")] public string InstallId { get; set; } = "";
    [JsonPropertyName("events")] public List<GrowthEvent> Events { get; set; } = [];
    public EventsRequest() { }
    public EventsRequest(string installId, List<GrowthEvent> events) { InstallId = installId; Events = events; }
}

/// <summary>端末内のイベントキュー。1 行 1 イベントの JSONL。送信に失敗しても捨てず、上限を超えたら古い順に捨てる。</summary>
public sealed class GrowthQueue
{
    public const int MaxPending = 500;
    public const int BatchSize = 100;
    public string Path { get; }
    readonly object gate = new();
    readonly List<GrowthEvent> pending;
    bool flushing;

    public GrowthQueue(string path) { Path = path; pending = LoadFile(path); }

    public int Count { get { lock (gate) return pending.Count; } }

    public void Record(GrowthEvent e)
    {
        lock (gate)
        {
            pending.Add(e);
            if (pending.Count > MaxPending) pending.RemoveRange(0, pending.Count - MaxPending);
            Save();
        }
    }

    /// <summary>溜まった分を最大 BatchSize ずつ送る。送信が失敗したら残りは次回に回す。同時に 2 つは走らない。</summary>
    public async Task Flush(Func<List<GrowthEvent>, Task> send)
    {
        lock (gate) { if (flushing || pending.Count == 0) return; flushing = true; }
        try
        {
            while (true)
            {
                List<GrowthEvent> batch;
                lock (gate) batch = pending.Take(BatchSize).ToList();
                if (batch.Count == 0) return;
                try { await send(batch).ConfigureAwait(false); } catch (Exception) { return; }
                lock (gate) { pending.RemoveRange(0, Math.Min(batch.Count, pending.Count)); Save(); }
            }
        }
        finally { lock (gate) flushing = false; }
    }

    public void RemoveAll() { lock (gate) { pending.Clear(); Save(); } }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var lines = pending.Select(e => JsonSerializer.Serialize(e, ContractJson.Options));
            File.WriteAllText(Path + ".tmp", string.Join("\n", lines));
            File.Move(Path + ".tmp", Path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static List<GrowthEvent> LoadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var list = new List<GrowthEvent>();
            foreach (var line in File.ReadAllLines(path))
            {
                if (line.Length == 0) continue;
                try { var e = JsonSerializer.Deserialize<GrowthEvent>(line, ContractJson.Options); if (e is not null) list.Add(e); } catch (JsonException) { }
            }
            return list;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
}

public static class GrowthBuckets
{
    /// <summary>文字数を粗い区分にする（本文の長さを細かく残さない）。サーバの growth.CharsBucket と同じ。</summary>
    public static string Chars(int n) => n switch { < 1 => "0", <= 500 => "1-500", <= 2000 => "501-2000", _ => "2001+" };
}
