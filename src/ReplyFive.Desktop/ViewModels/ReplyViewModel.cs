using CommunityToolkit.Mvvm.ComponentModel;
using ReplyFive.Core;
using ReplyFive.Desktop.Platform;
using ReplyFive.Desktop.Services;
using static ReplyFive.Desktop.Services.L10n;

namespace ReplyFive.Desktop.ViewModels;

public enum Phase { Idle, Drafting, Ready, Failed }

/// <summary>パネル 1 枚の状態。会話文脈・生成文はメモリだけに置き、閉じたら破棄する。</summary>
public sealed partial class ReplyViewModel : ObservableObject
{
    [ObservableProperty] CapturedContext? context;
    /// <summary>付録BR：この相手について溜めてある会話の行数。</summary>
    [ObservableProperty] int storedLines;
    [ObservableProperty] string intent = "";
    [ObservableProperty] Core.Platform platform = Core.Platform.Generic;
    [ObservableProperty] RecipientType recipient;
    [ObservableProperty] Tone tone;
    [ObservableProperty] string language;
    [ObservableProperty] string result = "";
    [ObservableProperty] Phase phase = Phase.Idle;
    [ObservableProperty] ApiException? failure;
    [ObservableProperty] string meta = "";
    [ObservableProperty] string? notice;
    [ObservableProperty] string? warningNote;
    [ObservableProperty] bool showContext;
    [ObservableProperty] int learnedForContact;
    /// <summary>Return で生成→そのまま元の入力欄へ差し込む途中。</summary>
    [ObservableProperty] bool isAutoInserting;

    string generated = "";
    FormatResponse? lastResponse;
    string? feedbackSentFor;
    int generation;
    CancellationTokenSource? inflight;
    bool insertWhenReady;
    bool presented, generatedOnce, usedResult;
    bool suppressIntentReset;
    readonly Func<FormatRequest, CancellationToken, Task<FormatResponse>>? formatter;
    public AppSettings Settings { get; }
    public IPlatform Platform_ { get; }
    public Action? OnClose { get; set; }
    /// <summary>挿入に失敗してコピーに留めたときだけ知らせる（付録BH）。</summary>
    public Action<string, bool>? ShowToast { get; set; }

    public ReplyViewModel(AppSettings settings, IPlatform platform, Func<FormatRequest, CancellationToken, Task<FormatResponse>>? formatter = null)
    {
        Settings = settings; Platform_ = platform; this.formatter = formatter;
        recipient = settings.DefaultRecipient;
        tone = settings.DefaultTone;
        language = settings.OutputLanguage;
    }

    public bool CanGenerate => Intent.Trim().Length > 0;
    public bool HasResult => Result.Length > 0;
    public bool IsWaiting => Phase == Phase.Drafting;

    partial void OnIntentChanged(string? oldValue, string newValue)
    {
        if (suppressIntentReset || oldValue == newValue || Phase == Phase.Idle) return;
        generation++;
        inflight?.Cancel(); inflight = null;
        insertWhenReady = false; IsAutoInserting = false;
        SendFeedbackIfPending("dismiss");
        Result = ""; generated = ""; Phase = Phase.Idle; Meta = ""; WarningNote = null; Failure = null;
        ClearResponse();
    }


    public void Present(CapturedContext captured)
    {
        Reset();
        presented = true;
        Context = captured;
        Platform = captured.Platform;
        // 社内チャットは既定を internal に寄せる
        if (captured.Platform is Core.Platform.Slack or Core.Platform.Teams or Core.Platform.Googlechat && Settings.DefaultRecipient == RecipientType.External) Recipient = RecipientType.Internal;
        if (captured.ContactKey is { } key)
        {
            LearnedForContact = Settings.Records.CountFor(Platform, key);
            StoredLines = Settings.EffectiveConversationEnabled ? Settings.Conversations.MessageCount(Platform, key) : 0;
        }
    }

    /// <summary>後から届いた取得結果を反映する（付録BP）。意図・生成結果は触らない。既に結果があるときは文脈を変えない。</summary>
    public void Update(CapturedContext captured)
    {
        if (HasResult || Phase is not (Phase.Idle or Phase.Drafting)) return;
        Context = captured;
        Platform = captured.Platform;
        if (captured.Platform is Core.Platform.Slack or Core.Platform.Teams or Core.Platform.Googlechat && Settings.DefaultRecipient == RecipientType.External) Recipient = RecipientType.Internal;
        LearnedForContact = captured.ContactKey is { } k ? Settings.Records.CountFor(Platform, k) : 0;
    }

    /// <summary>付録BR／BS：溜めてある会話（相手／自分に分けた発言列）に、いま見えている会話を重ねて会話形式で渡す。合わせて 5,000 文字まで。</summary>
    ConversationContext? MergedConversationContext()
    {
        var c = Context;
        if (c is null) return null;
        var own = Settings.UserName.Trim();
        var currentMsgs = ConversationParser.Parse(c.Text, own.Length == 0 ? null : own, c.ContactName);
        var recipientName = ReplyNameSafety.RecipientName(currentMsgs, c.ContactName, c.AppName);
        if (!Settings.EffectiveConversationEnabled || c.ContactKey is null || c.Platform == Core.Platform.Generic)
        {
            if (currentMsgs.Count == 0) return c.Text.Length == 0 ? null : new ConversationContext(c.Source, c.AppName, c.Text);
            return new ConversationContext(c.Source, c.AppName, ConversationParser.Render(currentMsgs), recipientName, currentMsgs.Select(m => m.ToContext()).ToList());
        }
        var stored = Settings.Conversations.RecentMessages(c.Platform, c.ContactKey, 5000);
        var merged = ConversationStore.Merge(stored, currentMsgs);
        var total = merged.Sum(m => m.Text.Length + 12);
        while (total > 5000 && merged.Count > 0) { total -= merged[0].Text.Length + 12; merged.RemoveAt(0); }
        if (merged.Count == 0) return null;
        return new ConversationContext(c.Text.Length == 0 ? ContextSource.Window : c.Source, c.AppName, ConversationParser.Render(merged), recipientName, merged.Select(m => m.ToContext()).ToList());
    }

    public void Reset()
    {
        if (presented)
        {
            var outcome = !generatedOnce ? (Intent.Trim().Length == 0 ? "no_intent" : "intent_not_generated") : (usedResult ? null : "generated_not_used");
            if (outcome is not null) Growth.Track("panel_closed", new() { ["outcome"] = outcome });
            _ = Growth.Flush();
        }
        presented = false; generatedOnce = false; usedResult = false;
        generation++;
        insertWhenReady = false; IsAutoInserting = false;
        inflight?.Cancel(); inflight = null;
        SendFeedbackIfPending("dismiss");
        suppressIntentReset = true;
        Context = null; Intent = ""; Result = ""; generated = ""; Phase = Phase.Idle; Failure = null; Meta = ""; Notice = null; WarningNote = null; ShowContext = false; LearnedForContact = 0; StoredLines = 0;
        suppressIntentReset = false;
        ClearResponse();
        Recipient = Settings.DefaultRecipient; Tone = Settings.DefaultTone; Language = Settings.OutputLanguage;
    }

    void ClearResponse()
    {
        lastResponse = null; feedbackSentFor = null;
    }

    /// <summary>Return：生成が終わったら元の入力欄へ差し込んで閉じる。Alt+Return：生成だけして確認する。</summary>
    public void GenerateAndInsert() => Generate(automaticallyInsert: true);

    public void Generate(bool automaticallyInsert = false)
    {
        var text = Intent.Trim();
        if (text.Length == 0) return;
        insertWhenReady = automaticallyInsert;
        IsAutoInserting = automaticallyInsert;
        generatedOnce = true;
        inflight?.Cancel();
        generation++;
        var gen = generation;
        SendFeedbackIfPending("dismiss");
        // 付録AA: 即時に端末側下書きを出す
        Result = LocalDraft.Format(Platform, Recipient, text);
        generated = "";
        ClearResponse();
        Failure = null;
        Phase = Phase.Drafting;
        Meta = L("panel.status.drafting");
        var examples = Settings.EffectiveLearningEnabled ? Settings.Records.Examples(Platform, Context?.ContactKey) : [];
        var conversationContext = Settings.Policy?.ContextCaptureAllowed == false ? null : MergedConversationContext();
        var req = new FormatRequest(Platform, Recipient, Tone, text, conversationContext, examples, Settings.ClientInfo, Language, Settings.SenderForRequest);
        var started = DateTimeOffset.UtcNow;
        var cts = new CancellationTokenSource();
        inflight = cts;
        var client = Settings.MakeClient();
        _ = Task.Run(async () =>
        {
            try
            {
                var res = formatter is not null ? await formatter(req, cts.Token) : await client.Format(req, cts.Token);
                Diag.Log($"format ok provider={res.Provider} ms={res.ElapsedMs}");
                await Ui(() =>
                {
                    if (cts.IsCancellationRequested || gen != generation) return;
                    // 端末側で生成結果を捨てない
                    Result = res.Text;
                    generated = res.Text;
                    ApplyResponseMeta(res);
                    Phase = Phase.Ready;
                    Settings.RecordUsage();
                    WarningNote = LocalizedWarning(res.Warnings);
                    var ms = (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
                    Meta = res.Warnings.Contains("license_expired_grace") ? L("panel.status.grace") : L("panel.status.ready", ms);
                    if (insertWhenReady) { insertWhenReady = false; InsertResult(); }
                    IsAutoInserting = false;
                });
            }
            catch (ApiException e)
            {
                Diag.Log("format error " + e.Code);
                await Ui(() =>
                {
                    if (cts.IsCancellationRequested || gen != generation) return;
                    if (e.RequiresReregistration) Settings.ClearRegistration(); // パネルの「サインイン」で再接続する（付録BF）
                    if (e.Kind == ApiErrorKind.Server && e.Status >= 500) Crash.Record($"format failed: {e.Code} ({e.Status})");
                    insertWhenReady = false; IsAutoInserting = false; // 失敗時は自動で入れない。下書きを見せて利用者に任せる
                    Failure = e; Phase = Phase.Failed; Meta = "";
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Crash.Exception(ex);
                await Ui(() =>
                {
                    if (cts.IsCancellationRequested || gen != generation) return;
                    insertWhenReady = false; IsAutoInserting = false;
                    Failure = ApiException.Unreachable(); Phase = Phase.Failed;
                });
            }
        });
    }

    static Task Ui(Action a) => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(a).GetTask();

    // MARK: - 付録BC・BG: 修正率の計測

    void ApplyResponseMeta(FormatResponse res)
    {
        lastResponse = res;
        feedbackSentFor = null;
    }

    /// <summary>修正 KPI の計測（本文なし）。1 応答につき 1 回だけ、失敗は無視する。</summary>
    void SendFeedbackIfPending(string action)
    {
        var res = lastResponse;
        if (res is null || feedbackSentFor == res.RequestId || Phase != Phase.Ready) return;
        feedbackSentFor = res.RequestId;
        var edited = action != "dismiss" && generated.Length > 0 && Result != generated;
        var body = new FeedbackRequest
        {
            RequestId = res.RequestId, Action = action, Edited = edited, EditRatio = edited ? EditDistance.Ratio(generated, Result) : 0, EditChars = edited ? EditDistance.Levenshtein(generated, Result) : 0,
            Client = Settings.ClientInfo,
        };
        Diag.Log($"feedback action={action} edited={edited}");
        if (!Settings.IsRegistered || formatter is not null) return;
        var client = Settings.MakeClient();
        _ = Task.Run(async () => { try { await client.Feedback(body); } catch (Exception) { } });
    }

    public string ErrorMessage(ApiException e)
    {
        switch (e.Kind)
        {
            case ApiErrorKind.NotRegistered: return L("error.not_registered");
            case ApiErrorKind.Unreachable: return L("error.unreachable");
            case ApiErrorKind.Timeout: return L("error.timeout");
            case ApiErrorKind.InvalidResponse: return L("error.invalid_response");
            default:
                if (!string.IsNullOrEmpty(e.ServerMessage)) return e.ServerMessage;
                return Has("error." + e.Code) ? L("error." + e.Code) : L("error.generic", e.Code);
        }
    }

    /// <summary>返信の記録（付録BG）。挿入・コピーのたびに、修正の有無を問わず端末内に残す。利用者に確認は求めない。組織が禁止していれば記録しない。</summary>
    void RecordReply(string action)
    {
        if (!Settings.EffectiveLearningEnabled || Phase != Phase.Ready || generated.Length == 0) return;
        var store = Settings.Records;
        var intent = Intent.Trim();
        var g = generated; var final = Result; var platform = Platform;
        var contactKey = Context?.ContactKey; var appName = Context?.AppName;
        _ = Task.Run(() => { try { store.Record(intent, g, final, platform, contactKey, appName, action); } catch (Exception) { } });
    }

    static string? LocalizedWarning(List<string> warnings)
    {
        var notes = new List<string>();
        if (warnings.Contains("learning_ignored_by_policy")) notes.Add(L("panel.warning.records_ignored"));
        if (warnings.Contains("context_ignored_by_policy")) notes.Add(L("panel.warning.context_ignored"));
        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    public void CopyResult()
    {
        if (!HasResult) return;
        usedResult = true;
        RecordReply("copy");
        SendFeedbackIfPending("copy");
        Platform_.CopyToClipboard(Result);
        Notice = L("panel.notice.copied");
        var gen = generation;
        _ = Task.Delay(1200).ContinueWith(_ => Ui(() => { if (gen == generation) Notice = null; }));
    }

    public void InsertResult()
    {
        if (!HasResult) return;
        usedResult = true;
        var platformName = Platform.Wire();
        RecordReply("insert");
        SendFeedbackIfPending("insert");
        var ctx = Context;
        var text = Result;
        Diag.Log($"insert trusted={Platform_.AccessibilityTrusted} element={ctx?.FocusedElement is not null} chars={text.Length}");
        OnClose?.Invoke();
        if (ctx is null || !Platform_.AccessibilityTrusted)
        {
            Platform_.CopyToClipboard(text);
            Growth.Track("insert_completed", new() { ["platform"] = platformName, ["ok"] = false, ["method"] = "copy_only" });
            ShowToast?.Invoke(L("panel.notice.copied_paste"), false);
            if (!Platform_.AccessibilityTrusted) Platform_.RequestAccessibility();
            return;
        }
        var platform = Platform_;
        _ = Task.Run(async () =>
        {
            bool ok;
            try { ok = await platform.Insert(text, ctx); } catch (Exception e) { Crash.Exception(e); ok = false; }
            Diag.Log("insert result ok=" + ok);
            await Ui(() =>
            {
                Growth.Track("insert_completed", new() { ["platform"] = platformName, ["ok"] = ok, ["method"] = ok ? "accessibility" : "paste" });
                // 成功時は何も出さない（付録BH）。入らなかったときだけ、コピー済みで貼り付けが要ることを知らせる
                if (!ok) { platform.CopyToClipboard(text); ShowToast?.Invoke(L("panel.notice.copied_paste"), false); }
            });
        });
    }

    /// <summary>管理画面の請求ページ（サーバと同じホスト）をブラウザで開く。</summary>
    public void OpenBilling(string origin = "panel_banner")
    {
        Growth.Track("billing_opened", new() { ["origin"] = origin });
        Platform_.OpenUrl(ServerAddress.BillingUrl(Settings.Server).ToString());
        OnClose?.Invoke();
    }

    /// <summary>トーン・相手・言語の変更は既定として記憶し、結果を表示中なら同じ意図で作り直す。</summary>
    public void OptionsChanged()
    {
        Settings.DefaultTone = Tone;
        Settings.DefaultRecipient = Recipient;
        Settings.OutputLanguage = Language;
        if (Phase != Phase.Idle && CanGenerate) { insertWhenReady = false; Generate(); }
    }
}
