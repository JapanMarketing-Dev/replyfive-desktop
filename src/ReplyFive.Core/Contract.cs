using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReplyFive.Core;

/// <summary>shared/replyfive-contract.json v0.2 の C# 表現。JSON の項目名は契約どおり snake_case。</summary>
public enum Platform { Gmail, Outlook, Slack, Teams, Chatwork, Line, Lineworks, Googlechat, Generic }

public enum RecipientType { Customer, Partner, Internal, External }

public enum Tone { Natural, Polite, Short }

public enum ContextSource
{
    SelectedText,
    Window,     // 選択が無いとき、前面ウインドウの可視テキスト（ショートカット押下時の 1 回だけ）
    Clipboard,
    None,
}

public static class ContractJson
{
    /// <summary>契約の JSON。列挙は snake_case の小文字、null の項目は省く。</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        PropertyNameCaseInsensitive = false,
    };

    public static string Wire(this Platform p) => JsonNamingPolicy.SnakeCaseLower.ConvertName(p.ToString());
    public static string Wire(this RecipientType r) => JsonNamingPolicy.SnakeCaseLower.ConvertName(r.ToString());
    public static string Wire(this Tone t) => JsonNamingPolicy.SnakeCaseLower.ConvertName(t.ToString());
    public static string Wire(this ContextSource s) => JsonNamingPolicy.SnakeCaseLower.ConvertName(s.ToString());

    public static Platform? ParsePlatform(string? raw)
    {
        foreach (var p in Enum.GetValues<Platform>()) if (p.Wire() == raw) return p;
        return null;
    }
    public static RecipientType? ParseRecipient(string? raw)
    {
        foreach (var p in Enum.GetValues<RecipientType>()) if (p.Wire() == raw) return p;
        return null;
    }
    public static Tone? ParseTone(string? raw)
    {
        foreach (var p in Enum.GetValues<Tone>()) if (p.Wire() == raw) return p;
        return null;
    }

    public static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    public static string Prefix(string s, int max) => s.Length <= max ? s : (char.IsHighSurrogate(s[max - 1]) ? s[..(max - 1)] : s[..max]);
}

/// <summary>会話形式の 1 発言（付録BS）。role は other／me／unknown。</summary>
public sealed class ContextMessage : IEquatable<ContextMessage>
{
    [JsonPropertyName("role")] public string Role { get; set; } = "unknown";
    [JsonPropertyName("sender")] public string? Sender { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    public ContextMessage() { }
    public ContextMessage(string role, string? sender, string text) { Role = role; Sender = sender; Text = text; }
    public bool Equals(ContextMessage? o) => o is not null && Role == o.Role && Sender == o.Sender && Text == o.Text;
    public override bool Equals(object? obj) => Equals(obj as ContextMessage);
    public override int GetHashCode() => HashCode.Combine(Role, Sender, Text);
}

public sealed class ConversationContext
{
    [JsonPropertyName("source")] public ContextSource Source { get; set; }
    [JsonPropertyName("app_name")] public string? AppName { get; set; }
    /// <summary>返信相手の表示名（付録BE）。空白だけなら省略。100 文字まで。</summary>
    [JsonPropertyName("contact_name")] public string? ContactName { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    /// <summary>付録BS：相手／自分に分けた発言列（任意）。あればサーバはこちらを優先し、text は表示・互換用。</summary>
    [JsonPropertyName("messages")] public List<ContextMessage>? Messages { get; set; }

    public ConversationContext() { }
    public ConversationContext(ContextSource source, string? appName, string text, string? contactName = null, List<ContextMessage>? messages = null)
    {
        Source = source; AppName = appName; Text = text; Messages = messages;
        var n = contactName?.Trim() ?? "";
        ContactName = n.Length == 0 ? null : ContractJson.Prefix(n, 100);
    }
}

public sealed class LearnedExample
{
    [JsonPropertyName("intent")] public string Intent { get; set; } = "";
    [JsonPropertyName("corrected")] public string Corrected { get; set; } = "";
    public LearnedExample() { }
    public LearnedExample(string intent, string corrected) { Intent = intent; Corrected = corrected; }
}

/// <summary>付録CD：利用者自身の文体ラベル。契約 sender.style と同じ固定語彙。null の項目は送らない。</summary>
public sealed class StyleProfile : IEquatable<StyleProfile>
{
    [JsonPropertyName("formality")] public string? Formality { get; set; } // casual | standard | formal
    [JsonPropertyName("length")] public string? Length { get; set; }       // short | medium | long
    [JsonPropertyName("greeting")] public string? Greeting { get; set; }   // none | light | formal
    [JsonPropertyName("closing")] public string? Closing { get; set; }     // none | light | formal
    [JsonPropertyName("emoji")] public string? Emoji { get; set; }         // none | some

    public static readonly IReadOnlyDictionary<string, string[]> Vocabulary = new Dictionary<string, string[]>
    {
        ["formality"] = ["casual", "standard", "formal"],
        ["length"] = ["short", "medium", "long"],
        ["greeting"] = ["none", "light", "formal"],
        ["closing"] = ["none", "light", "formal"],
        ["emoji"] = ["none", "some"],
    };
    public static readonly string[] Fields = ["formality", "length", "greeting", "closing", "emoji"];

    [JsonIgnore] public bool IsEmpty => Formality is null && Length is null && Greeting is null && Closing is null && Emoji is null;

    public string? Get(string field) => field switch { "formality" => Formality, "length" => Length, "greeting" => Greeting, "closing" => Closing, "emoji" => Emoji, _ => null };
    public void Set(string field, string? value)
    {
        switch (field)
        {
            case "formality": Formality = value; break;
            case "length": Length = value; break;
            case "greeting": Greeting = value; break;
            case "closing": Closing = value; break;
            case "emoji": Emoji = value; break;
        }
    }

    /// <summary>語彙外の値を落とす。</summary>
    [JsonIgnore]
    public StyleProfile Normalized
    {
        get
        {
            static string? Ok(string name, string? v) => v is not null && Vocabulary[name].Contains(v) ? v : null;
            return new StyleProfile { Formality = Ok("formality", Formality), Length = Ok("length", Length), Greeting = Ok("greeting", Greeting), Closing = Ok("closing", Closing), Emoji = Ok("emoji", Emoji) };
        }
    }

    public StyleProfile Clone() => new() { Formality = Formality, Length = Length, Greeting = Greeting, Closing = Closing, Emoji = Emoji };
    public bool Equals(StyleProfile? o) => o is not null && Formality == o.Formality && Length == o.Length && Greeting == o.Greeting && Closing == o.Closing && Emoji == o.Emoji;
    public override bool Equals(object? obj) => Equals(obj as StyleProfile);
    public override int GetHashCode() => HashCode.Combine(Formality, Length, Greeting, Closing, Emoji);
}

public sealed class FormatRequest
{
    [JsonPropertyName("platform")] public Platform Platform { get; set; }
    [JsonPropertyName("recipient_type")] public RecipientType RecipientType { get; set; }
    [JsonPropertyName("tone")] public Tone Tone { get; set; }
    [JsonPropertyName("language")] public string Language { get; set; } = "auto";
    [JsonPropertyName("user_intent")] public string UserIntent { get; set; } = "";
    /// <summary>付録CF：true なら user_intent は空でよく、会話だけから「次に送りそうな返信」を作る（conversation_context が必須）。</summary>
    [JsonPropertyName("auto_intent"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? AutoIntent { get; set; }
    [JsonPropertyName("conversation_context")] public ConversationContext? ConversationContext { get; set; }
    [JsonPropertyName("learned_examples")] public List<LearnedExample> LearnedExamples { get; set; } = [];
    [JsonPropertyName("sender")] public SenderInfo? Sender { get; set; }
    [JsonPropertyName("client")] public ClientInfo Client { get; set; } = new();

    /// <summary>利用者自身の呼び名。文脈中の宛名（自分宛）と差出人（相手）の取り違えを防ぐ。名前も文体も無ければ送らない。</summary>
    public sealed class SenderInfo
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("style")] public StyleProfile? Style { get; set; }
        public SenderInfo() { }
        public SenderInfo(string name, StyleProfile? style = null) { Name = name; Style = style; }
        [JsonIgnore] public bool IsEmpty => string.IsNullOrWhiteSpace(Name) && (Style?.IsEmpty ?? true);
    }

    public sealed class ClientInfo
    {
        [JsonPropertyName("os")] public string Os { get; set; } = "";
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        public ClientInfo() { }
        public ClientInfo(string os, string version) { Os = os; Version = version; }
    }

    public FormatRequest() { }

    public FormatRequest(Platform platform, RecipientType recipientType, Tone tone, string userIntent, ConversationContext? conversationContext,
                         List<LearnedExample> learnedExamples, ClientInfo client, string language = "auto", SenderInfo? sender = null)
    {
        Platform = platform; RecipientType = recipientType; Tone = tone; Language = language; UserIntent = userIntent;
        ConversationContext = conversationContext; LearnedExamples = learnedExamples; Client = client;
        Sender = sender is null || sender.IsEmpty ? null : sender;
    }
}

public sealed class FormatResponse
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("context_supplied")] public bool ContextSupplied { get; set; }
    [JsonPropertyName("policy_applied")] public bool PolicyApplied { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("request_id")] public string RequestId { get; set; } = "";
    [JsonPropertyName("elapsed_ms")] public int ElapsedMs { get; set; }
    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = [];
}

/// <summary>付録BC：修正率の計測。本文（意図・文脈・生成文・最終文）は一切含めない。</summary>
public sealed class FeedbackRequest
{
    [JsonPropertyName("request_id")] public string RequestId { get; set; } = "";
    [JsonPropertyName("action")] public string Action { get; set; } = "dismiss"; // insert | copy | dismiss
    [JsonPropertyName("edited")] public bool Edited { get; set; }
    [JsonPropertyName("edit_ratio")] public double EditRatio { get => editRatio; set => editRatio = Math.Clamp(value, 0, 1); }
    private double editRatio;
    /// <summary>付録BG：生成文と最終文のレーベンシュタイン距離（文字数）。修正されないことが KPI。</summary>
    [JsonPropertyName("edit_chars")] public int EditChars { get => editChars; set => editChars = Math.Max(0, value); }
    private int editChars;
    [JsonPropertyName("client")] public FormatRequest.ClientInfo Client { get; set; } = new();
}

public sealed class RegisterRequest
{
    /// <summary>付録BF：サインイン済みブラウザが発行した接続トークン（10 分有効）。</summary>
    [JsonPropertyName("link_token")] public string LinkToken { get; set; } = "";
    [JsonPropertyName("device_name")] public string DeviceName { get; set; } = "";
    [JsonPropertyName("os")] public string Os { get; set; } = "windows";
    [JsonPropertyName("client_version")] public string ClientVersion { get; set; } = "";
    /// <summary>成長計測：登録前の匿名イベントと組織を結ぶ。</summary>
    [JsonPropertyName("install_id")] public string? InstallId { get; set; }
    /// <summary>付録CD：利用者が初回設定で確認した自分の名前。管理画面の端末一覧に出す。</summary>
    [JsonPropertyName("user_name")] public string? UserName { get; set; }

    public RegisterRequest() { }
    public RegisterRequest(string linkToken, string deviceName, string os, string clientVersion, string? installId = null, string? userName = null)
    {
        LinkToken = linkToken; DeviceName = deviceName; Os = os; ClientVersion = clientVersion; InstallId = installId; UserName = ContractJson.Blank(userName);
    }
}

/// <summary>付録CD：PUT /v1/devices/self。空の項目は送らない（サーバも変更しない）。</summary>
public sealed class DeviceProfileRequest
{
    [JsonPropertyName("device_name")] public string? DeviceName { get; set; }
    [JsonPropertyName("user_name")] public string? UserName { get; set; }
    public DeviceProfileRequest() { }
    public DeviceProfileRequest(string? deviceName, string? userName) { DeviceName = ContractJson.Blank(deviceName); UserName = ContractJson.Blank(userName); }
}

/// <summary>付録CD：POST /v1/style/profile。</summary>
public sealed class StyleProfileRequest
{
    public sealed class Sample
    {
        [JsonPropertyName("received")] public string Received { get; set; } = "";
        [JsonPropertyName("reply")] public string Reply { get; set; } = "";
        public Sample() { }
        public Sample(string received, string reply) { Received = received; Reply = reply; }
    }
    [JsonPropertyName("samples")] public List<Sample> Samples { get; set; } = [];
}

public sealed class StyleProfileResponse
{
    [JsonPropertyName("profile")] public StyleProfile Profile { get; set; } = new();
    [JsonPropertyName("confidence")] public Dictionary<string, double>? Confidence { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("elapsed_ms")] public long? ElapsedMs { get; set; }
}

public sealed class NamedEntity
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class RegisterResponse
{
    [JsonPropertyName("device_token")] public string DeviceToken { get; set; } = "";
    [JsonPropertyName("device_id")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("organization")] public NamedEntity Organization { get; set; } = new();
    [JsonPropertyName("entitlement")] public EntitlementView? Entitlement { get; set; }
    [JsonPropertyName("policy")] public PolicyView? Policy { get; set; }
}

public sealed class EntitlementView : IEquatable<EntitlementView>
{
    public sealed class TrialInfo
    {
        [JsonPropertyName("days_left")] public int DaysLeft { get; set; }
        [JsonPropertyName("ends_at")] public string EndsAt { get; set; } = "";
    }
    public sealed class SeatsInfo
    {
        [JsonPropertyName("used")] public int Used { get; set; }
        [JsonPropertyName("limit")] public int? Limit { get; set; }
    }
    [JsonPropertyName("mode")] public string Mode { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("trial")] public TrialInfo? Trial { get; set; }
    [JsonPropertyName("seats")] public SeatsInfo Seats { get; set; } = new();
    [JsonPropertyName("daily_limit")] public int? DailyLimit { get; set; }

    public bool Equals(EntitlementView? o) => o is not null && Mode == o.Mode && Status == o.Status && Trial?.DaysLeft == o.Trial?.DaysLeft && Trial?.EndsAt == o.Trial?.EndsAt
        && Seats.Used == o.Seats.Used && Seats.Limit == o.Seats.Limit && DailyLimit == o.DailyLimit;
    public override bool Equals(object? obj) => Equals(obj as EntitlementView);
    public override int GetHashCode() => HashCode.Combine(Mode, Status, Seats.Used, Seats.Limit, DailyLimit);
}

public sealed class PolicyView : IEquatable<PolicyView>
{
    [JsonPropertyName("learning_allowed")] public bool LearningAllowed { get; set; } = true;
    [JsonPropertyName("context_capture_allowed")] public bool ContextCaptureAllowed { get; set; } = true;
    public bool Equals(PolicyView? o) => o is not null && LearningAllowed == o.LearningAllowed && ContextCaptureAllowed == o.ContextCaptureAllowed;
    public override bool Equals(object? obj) => Equals(obj as PolicyView);
    public override int GetHashCode() => HashCode.Combine(LearningAllowed, ContextCaptureAllowed);
}

/// <summary>最新版の情報。/v1/meta は "version"、/v1/devices/self は "latest_version" で返す。</summary>
public sealed class ClientUpdateView
{
    [JsonPropertyName("latest_version")] public string? LatestVersionField { get; set; }
    [JsonPropertyName("version")] public string? VersionField { get; set; }
    [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
    [JsonIgnore] public string? LatestVersion => LatestVersionField ?? VersionField;
}

public sealed class LatestClientView
{
    [JsonPropertyName("macos")] public ClientUpdateView? Macos { get; set; }
    [JsonPropertyName("windows")] public ClientUpdateView? Windows { get; set; }
    [JsonPropertyName("linux")] public ClientUpdateView? Linux { get; set; }

    public ClientUpdateView? For(string os) => os switch { "windows" => Windows, "linux" => Linux, "macos" => Macos, _ => null };
}

public sealed class DeviceSelfResponse
{
    [JsonPropertyName("device")] public NamedEntity Device { get; set; } = new();
    [JsonPropertyName("organization")] public NamedEntity Organization { get; set; } = new();
    [JsonPropertyName("entitlement")] public EntitlementView Entitlement { get; set; } = new();
    [JsonPropertyName("policy")] public PolicyView Policy { get; set; } = new();
    /// <summary>この端末の OS 向けの最新版（サーバが X-ReplyFive-Client の OS で選ぶ）。</summary>
    [JsonPropertyName("client")] public ClientUpdateView Client { get; set; } = new();
}

public sealed class MetaResponse
{
    public sealed class LlmInfo
    {
        [JsonPropertyName("provider")] public string Provider { get; set; } = "";
        [JsonPropertyName("model")] public string Model { get; set; } = "";
    }
    [JsonPropertyName("mode")] public string Mode { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("contract_version")] public string ContractVersion { get; set; } = "";
    [JsonPropertyName("llm")] public LlmInfo Llm { get; set; } = new();
    [JsonPropertyName("latest_client")] public LatestClientView? LatestClient { get; set; }
}

/// <summary>サーバのエラー応答。code は契約 errors.codes。</summary>
public sealed class ApiErrorBody
{
    [JsonPropertyName("error")] public string Error { get; set; } = "";
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public enum ApiErrorKind { Server, Unreachable, Timeout, InvalidResponse, NotRegistered }

/// <summary>API の失敗。本文を含まない。契約 errors.codes の client 欄に従う判定を持つ。</summary>
public sealed class ApiException : Exception
{
    public ApiErrorKind Kind { get; }
    public int Status { get; }
    /// <summary>契約のエラーコード（unreachable / timeout / invalid_response / not_registered を含む）。</summary>
    public string Code { get; }
    public string? ServerMessage { get; }

    public ApiException(ApiErrorKind kind, string? code = null, int status = 0, string? message = null)
        : base(code ?? kind.ToString())
    {
        Kind = kind; Status = status; ServerMessage = message;
        Code = kind switch
        {
            ApiErrorKind.Server => code ?? "http_" + status,
            ApiErrorKind.Unreachable => "unreachable",
            ApiErrorKind.Timeout => "timeout",
            ApiErrorKind.InvalidResponse => "invalid_response",
            _ => "not_registered",
        };
    }

    public static ApiException Unreachable() => new(ApiErrorKind.Unreachable);
    public static ApiException Timeout() => new(ApiErrorKind.Timeout);
    public static ApiException InvalidResponse() => new(ApiErrorKind.InvalidResponse);
    public static ApiException NotRegistered() => new(ApiErrorKind.NotRegistered);
    public static ApiException Server(string code, int status, string? message) => new(ApiErrorKind.Server, code, status, message);

    static readonly HashSet<string> keepDraftCodes = ["llm_not_configured", "llm_timeout", "llm_unavailable", "license_expired", "incomplete_generation", "subscription_inactive", "rate_limited"];

    /// <summary>端末側下書きを維持したまま表示するべきエラーか。</summary>
    public bool KeepsDraft => Kind != ApiErrorKind.Server || keepDraftCodes.Contains(Code);
    /// <summary>管理画面の請求ページを開く導線を出すべきか（SaaS の未契約・トライアル終了）。</summary>
    public bool SuggestsBilling => Kind == ApiErrorKind.Server && Code == "subscription_inactive";
    /// <summary>端末登録をやり直すべきか。</summary>
    public bool RequiresReregistration => Kind == ApiErrorKind.NotRegistered || (Kind == ApiErrorKind.Server && (Code == "unauthorized" || Code == "device_revoked"));
}
