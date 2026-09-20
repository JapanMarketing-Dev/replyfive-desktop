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

    // 付録BV：規則（ConversationNoise）で落ちなかった発言のうち、この相手の記録にまだ無いものを保存する。本文はサーバへ送らない。
    void Store(string source, Core.Platform plat, string contactKey, string? contactName, string? appName, List<ConversationMessage> snapshot)
    {
        var fresh = settings.Conversations.NewMessages(plat, contactKey, contactName, snapshot);
        if (fresh.Count == 0) return;
        var added = settings.Conversations.Ingest(plat, contactKey, contactName, appName, snapshot);
        if (added > 0 && settings.EffectiveLearningEnabled)
            foreach (var m in snapshot) if (m.Role == "me") { try { settings.Records.MarkSent(plat, contactKey, m.Text); } catch (Exception) { } }
        if (added > 0) Diag.Log($"conversation ingest source={source} platform={plat.Wire()} fresh={fresh.Count} added={added}");
    }

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

    public void Dispose() { poll.Stop(); settings.Conversations.Flush(); }
}
