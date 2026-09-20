using Avalonia.Threading;
using ReplyFive.Core;
using ReplyFive.Desktop.Platform;

namespace ReplyFive.Desktop.Services;

/// <summary>会話のバックグラウンド収集（付録BR／BU）。会話アプリ（platform が generic でないアプリ）が前面の間だけ 0.5 秒ごとに会話のペインを読み、
/// 前回と同じ本文なら捨てる。文字認識に頼るアプリは画面の署名が変わったときだけ読む。本文はログに出さない。組織ポリシーで会話取得が禁止なら動かない。</summary>
public sealed class ConversationCollector : IDisposable
{
    readonly AppSettings settings;
    readonly IPlatform platform;
    readonly DispatcherTimer poll;
    readonly DispatcherTimer evaluationTimer;
    TargetApp? current;
    bool reading;
    int? lastSignature;
    string lastText = "";
    bool usesScreenText;

    public ConversationCollector(AppSettings settings, IPlatform platform)
    {
        this.settings = settings; this.platform = platform;
        poll = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Tick());
        poll.Start();
        evaluationTimer = new DispatcherTimer(TimeSpan.FromHours(24), DispatcherPriority.Background, (_, _) => RunPeriodicEvaluation());
        evaluationTimer.Start();
        Dispatcher.UIThread.Post(RunPeriodicEvaluation, DispatcherPriority.Background);
    }

    bool Enabled => settings.EffectiveConversationEnabled;

    void Tick()
    {
        if (!Enabled || reading) return;
        var front = platform.FrontmostApp();
        if (front is null || front.Name is null) { current = null; return; }
        var plat = PlatformDetector.Detect(front.Name);
        if (plat == Core.Platform.Generic) { if (current is not null) { current = null; settings.Conversations.Flush(); } return; }
        if (!front.SameAs(current))
        {
            // 前面アプリが変わった：監視対象を付け替えて 1 回読む
            current = front;
            lastSignature = null; lastText = "";
            usesScreenText = plat == Core.Platform.Line && platform.ScreenTextAvailable;
        }
        var own = settings.UserName.Trim();
        if (usesScreenText)
        {
            reading = true;
            var app = front;
            _ = Task.Run(() =>
            {
                var sig = platform.ScreenSignature(app);
                Dispatcher.UIThread.Post(() =>
                {
                    reading = false;
                    if (sig is not null && sig != lastSignature) { lastSignature = sig; Read(app, own); }
                });
            });
        }
        else Read(front, own);
    }

    /// <summary>ショートカットで取得した内容も溜める（同じ相手の続きになる）。</summary>
    public void Ingest(CapturedContext c)
    {
        if (!Enabled || c.Text.Length == 0 || c.ContactKey is null || c.Platform == Core.Platform.Generic) return;
        var own = settings.UserName.Trim();
        var msgs = ConversationParser.Parse(c.Text, own.Length == 0 ? null : own, c.ContactName);
        Store("shortcut", c.Platform, c.ContactKey, c.ContactName, c.AppName, msgs);
    }

    // 付録BV：規則で落ちなかった発言のうち、この相手の記録にまだ無いものだけをサーバ（Jev）に「人が書いた発言か」を問い、残ったものだけを保存する。
    sealed record PendingBatch(string Source, Core.Platform Platform, string ContactKey, string? ContactName, string? AppName, List<ConversationMessage> Snapshot);
    readonly List<PendingBatch> filterQueue = [];
    bool filtering;
    const int MaxFilterMessages = 60;
    readonly HashSet<string> evaluating = [];

    void Store(string source, Core.Platform plat, string contactKey, string? contactName, string? appName, List<ConversationMessage> snapshot)
    {
        var fresh = settings.Conversations.NewMessages(plat, contactKey, contactName, snapshot);
        if (fresh.Count == 0) return;
        filterQueue.RemoveAll(b => b.Platform == plat && b.ContactKey == contactKey);
        filterQueue.Add(new PendingBatch(source, plat, contactKey, contactName, appName, snapshot));
        DrainFilterQueue();
    }

    void DrainFilterQueue()
    {
        if (filtering || filterQueue.Count == 0) return;
        var batch = filterQueue[0]; filterQueue.RemoveAt(0);
        filtering = true;
        var fresh = settings.Conversations.NewMessages(batch.Platform, batch.ContactKey, batch.ContactName, batch.Snapshot);
        fresh = fresh.Skip(Math.Max(0, fresh.Count - MaxFilterMessages)).ToList();
        if (fresh.Count == 0) { filtering = false; DrainFilterQueue(); return; }
        var client = settings.MakeClient();
        var req = new ContextFilterRequest(batch.Platform.Wire(), batch.ContactName, fresh.Select(m => m.ToContext()).ToList());
        _ = Task.Run(async () =>
        {
            var drop = new HashSet<string>();
            var source = "rules";
            long elapsed = 0;
            if (client.DeviceToken is not null)
            {
                try
                {
                    var res = await client.FilterContext(req);
                    if (res.Keep.Count == fresh.Count)
                    {
                        source = res.Source; elapsed = res.ElapsedMs ?? 0;
                        for (var i = 0; i < res.Keep.Count; i++) if (!res.Keep[i]) drop.Add(fresh[i].Identity);
                    }
                }
                catch (Exception) { }
            }
            var kept = batch.Snapshot.Where(m => !drop.Contains(m.Identity)).ToList();
            var added = kept.Count == 0 ? 0 : settings.Conversations.Ingest(batch.Platform, batch.ContactKey, batch.ContactName, batch.AppName, kept);
            if (added > 0 && settings.EffectiveLearningEnabled)
                foreach (var m in kept) if (m.Role == "me") { try { settings.Records.MarkSent(batch.Platform, batch.ContactKey, m.Text); } catch (Exception) { } }
            if (added > 0 || drop.Count > 0) Diag.Log($"conversation ingest source={batch.Source} platform={batch.Platform.Wire()} judged={fresh.Count} dropped={drop.Count} added={added} filter={source} ms={elapsed}");
            Dispatcher.UIThread.Post(() => { filtering = false; DrainFilterQueue(); });
        });
    }

    /// <summary>収集済みの実際の往復を教師用／テスト用に分け、テスト組だけを裏側で生成へ通す。返った文面は対象アプリへ渡さず、設定画面で確認する暗号化された生成例として端末内へ残す。</summary>
    void ScheduleBackgroundEvaluation(Core.Platform plat, string contactKey, string? contactName, string? appName)
    {
        if (!settings.IsRegistered || !Enabled || !settings.EffectiveLearningEnabled) return;
        var key = plat.Wire() + "\u0001" + contactKey;
        if (evaluating.Contains(key)) return;
        var messages = settings.Conversations.RecentMessages(plat, contactKey, 12_000);
        var dataset = ConversationEvaluation.Dataset(plat, contactKey, messages);
        var pending = ConversationEvaluation.PendingTests(dataset, settings.BackgroundEvaluations.CompletedIds());
        if (pending.Count == 0) return;
        evaluating.Add(key);
        var teacher = dataset.Teacher.Select(p => new LearnedExample(p.Intent, p.Expected));
        var learned = settings.Records.Examples(plat, contactKey);
        var examples = teacher.Concat(learned).Take(LearningStore.MaxSent).ToList();
        var client = settings.MakeClient();
        var requests = pending.Select(pair =>
        {
            var recipient = ReplyNameSafety.RecipientName(pair.Context, contactName, appName, requireRepeated: true);
            var context = new ConversationContext(ContextSource.Window, appName, ConversationParser.Render(pair.Context), recipient, pair.Context.Select(m => m.ToContext()).ToList());
            return (pair.Id, new FormatRequest(plat, settings.DefaultRecipient, settings.DefaultTone, pair.Intent, context, examples, settings.ClientInfo, settings.OutputLanguage, settings.SenderForRequest));
        }).ToList();
        var store = settings.BackgroundEvaluations;
        _ = Task.Run(async () =>
        {
            var passed = 0;
            foreach (var (id, request) in requests)
            {
                if (client.DeviceToken is null) break;
                try
                {
                    var response = await client.Format(request);
                    var accepted = response.MeaningVerified && (response.Verification?.Passed ?? true);
                    store.Record(id, accepted, accepted, plat, request.ConversationContext?.ContactName, appName, response.Text);
                    if (accepted) passed++;
                }
                catch (Exception) { }
            }
            Diag.Log($"background evaluation platform={plat.Wire()} cases={requests.Count} passed={passed}");
            Dispatcher.UIThread.Post(() => evaluating.Remove(key));
        });
    }

    /// <summary>1 日 1 回、全接触先の最新データを再評価する。</summary>
    void RunPeriodicEvaluation()
    {
        if (!settings.IsRegistered || !Enabled || !settings.EffectiveLearningEnabled) return;
        foreach (var e in settings.Conversations.Load()) ScheduleBackgroundEvaluation(e.Platform, e.ContactKey, e.ContactName, e.AppName);
    }

    public void RunTuningNow() => RunPeriodicEvaluation();

    void Read(TargetApp app, string own)
    {
        if (!Enabled || reading) return;
        reading = true;
        var previous = lastText;
        var screen = usesScreenText;
        _ = Task.Run(() =>
        {
            CapturedContext c;
            try { c = platform.Capture(app, includeContext: true, ownName: own.Length == 0 ? null : own, budgetSeconds: 0.8, allowScreenText: screen); }
            catch (Exception e) { Crash.Exception(e); Dispatcher.UIThread.Post(() => reading = false); return; }
            var axEmpty = !screen && c.Text.Length == 0 && c.Error == CaptureError.NoSelection;
            var parsed = new List<ConversationMessage>();
            if (c.Text.Length > 0 && c.Text != previous && c.ContactKey is not null && c.Platform != Core.Platform.Generic)
                parsed = ConversationParser.Parse(c.Text, own.Length == 0 ? null : own, c.ContactName);
            Dispatcher.UIThread.Post(() =>
            {
                reading = false;
                if (c.Text.Length > 0) lastText = c.Text;
                if (axEmpty && platform.ScreenTextAvailable) usesScreenText = true;
                if (parsed.Count > 0 && c.ContactKey is not null) Store("background", c.Platform, c.ContactKey, c.ContactName, c.AppName, parsed);
            });
        });
    }

    public void Dispose() { poll.Stop(); evaluationTimer.Stop(); settings.Conversations.Flush(); }
}
