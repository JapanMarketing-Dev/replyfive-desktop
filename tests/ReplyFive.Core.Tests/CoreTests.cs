using System.Text.Json;
using System.Text.Json.Nodes;
using ReplyFive.Core;

namespace ReplyFive.Core.Tests;

public class ContactDetectorTests
{
    [Fact]
    public void SlackLikeSequencePicksSenderOfLastMessage()
    {
        var items = new List<VisibleItem>
        {
            new("# general"), new("# random"),
            new("鈴木", VisibleItem.ItemKind.Control), new("9:50", VisibleItem.ItemKind.Control), new("資料ありがとうございます。"),
            new("佐藤", VisibleItem.ItemKind.Control), new("10:24", VisibleItem.ItemKind.Control), new("来週の件、どうなりそうですか？"),
            new("Bold", VisibleItem.ItemKind.Control), new("Send", VisibleItem.ItemKind.Control),
        };
        Assert.Equal("佐藤", ContactDetector.LastSender(items, "遠藤"));
    }

    [Fact]
    public void SkipsOwnNameWhenLastMessageIsMine()
    {
        var text = "佐藤\n10:24\n来週の件、どうなりそうですか？\n遠藤巧巳\n10:30\n確認して戻します。";
        Assert.Equal("佐藤", ContactDetector.LastSender(text, "遠藤"));
        Assert.Equal("遠藤巧巳", ContactDetector.LastSender(text, "Takumi Endoh"));
    }

    [Fact]
    public void NameColonBodyLines()
    {
        Assert.Equal("田中", ContactDetector.LastSender("田中: 明日の打ち合わせ、15時でいいですか？\n自分: はい大丈夫です\n田中: では会議室Aで", null));
    }

    [Fact]
    public void EmailFromLabelWins()
    {
        Assert.Equal("山本 太郎", ContactDetector.LastSender("受信トレイ\n来週の件\n差出人: 山本 太郎 <yamamoto@example.com>\n宛先: 遠藤\nいつもお世話になっております。XYZ商事の山本です。", "遠藤"));
        Assert.Equal("Jane Doe", ContactDetector.LastSender("Inbox\nFrom: Jane Doe <jane@example.com>\nTo: me\nHi Takumi, could you send the deck?", "Takumi"));
    }

    [Fact]
    public void IgnoresTimestampsEditedMarksAndUiWords()
    {
        Assert.Equal("佐藤", ContactDetector.LastSender("佐藤 10:24 (edited)\n来週の件、どうなりそうですか？\n3 replies\nLast reply today\nReply", null));
        Assert.Equal("Alex Kim", ContactDetector.LastSender("Alex Kim (Design)\n10:24 AM\nCan you review the mock?\n2 件の返信\n返信", null));
    }

    [Fact]
    public void ReturnsNullWhenNothingLooksLikeAName()
    {
        Assert.Null(ContactDetector.LastSender("来週の件、どうなりそうですか？\nよろしくお願いします。", null));
        Assert.Null(ContactDetector.LastSender("", null));
    }

    [Fact]
    public void ConversationContextEncodesContactName()
    {
        var ctx = new ConversationContext(ContextSource.Window, "Slack", "hi", " 佐藤 ");
        var json = JsonNode.Parse(JsonSerializer.Serialize(ctx, ContractJson.Options))!;
        Assert.Equal("佐藤", json["contact_name"]!.GetValue<string>());
        Assert.Null(new ConversationContext(ContextSource.Window, "Slack", "hi", "  ").ContactName);
    }
}

public class ConversationStoreTests
{
    static string TempFile(string name) => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), name);

    [Fact]
    public void ParserSeparatesSpeakers()
    {
        var text = "山田 10:12\nリリース日の件、どうですか？\n遠藤 10:20\n10/22なら間に合います。\n自分: 営業には伝えておきます\nSheena\nうんうん";
        var msgs = ConversationParser.Parse(text, "遠藤");
        Assert.Equal(["other", "me", "other"], msgs.Select(m => m.Role));
        Assert.Equal("山田", msgs[0].Sender);
        Assert.Equal("10/22なら間に合います。\n営業には伝えておきます", msgs[1].Text);
        Assert.Equal("Sheena", msgs[2].Sender);
        Assert.Equal("山田: リリース日の件、どうですか？", ConversationParser.Render(msgs).Split('\n')[0]);
        var mail = ConversationParser.Parse("お世話になっております。\n見積の件、10%の値引きは可能でしょうか。", "遠藤", "田中");
        Assert.Equal(["other"], mail.Select(m => m.Role));
        Assert.Equal("田中", mail[0].Sender);
    }

    [Fact]
    public void MergeAppendsOnlyTheNewTailAndOwnReplies()
    {
        var a = new ConversationMessage("other", "リリース日の件", "山田");
        var b = new ConversationMessage("me", "10/22で確定します");
        var c = new ConversationMessage("other", "了解です", "山田");
        Assert.Equal(new[] { a, b, c }.Select(m => m.Identity), ConversationStore.Merge([a, b], [b, c]).Select(m => m.Identity));
        Assert.Equal(new[] { a, b }.Select(m => m.Identity), ConversationStore.Merge([a, b], [b]).Select(m => m.Identity));
        var store = new ConversationStore(TempFile("conversations.bin"), SealedBox.NewKey());
        store.Ingest(Platform.Slack, "k1", "山田", "Slack", [a]);
        store.AppendOwn(Platform.Slack, "k1", "10/22で確定します");
        store.AppendOwn(Platform.Slack, "k1", "10/22で確定します");
        Assert.Equal(2, store.MessageCount(Platform.Slack, "k1"));
        Assert.Equal("me", store.RecentMessages(Platform.Slack, "k1").Last().Role);
        Assert.Equal("山田: リリース日の件\n自分: 10/22で確定します", store.Recent(Platform.Slack, "k1"));
        for (var i = 0; i < 300; i++) store.Ingest(Platform.Chatwork, "k2", null, null, [new ConversationMessage("other", "m" + i, "A")]);
        Assert.Equal(ConversationStore.MaxMessagesPerContact, store.MessageCount(Platform.Chatwork, "k2"));
        for (var i = 0; i < 60; i++) store.Ingest(Platform.Gmail, "c" + i, null, null, [new ConversationMessage("other", "hello " + i)]);
        Assert.Equal(ConversationStore.MaxContacts, store.ContactCount);
        store.DeleteAll();
        Assert.Equal(0, store.ContactCount);
    }

    [Fact]
    public void FileIsEncryptedAndReloadable()
    {
        var path = TempFile("conversations.bin");
        var key = SealedBox.NewKey();
        var store = new ConversationStore(path, key);
        store.Ingest(Platform.Line, "k", "Sheena", "LINE", [new ConversationMessage("other", "うんうん", "Sheena")]);
        store.Flush();
        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("うんうん", raw);
        Assert.Equal(0, new ConversationStore(path, SealedBox.NewKey()).ContactCount);
        var again = new ConversationStore(path, key);
        Assert.Equal(1, again.ContactCount);
        Assert.Equal("Sheena", again.Load()[0].ContactName);
    }

    [Fact]
    public void NoiseLinesAreDropped()
    {
        foreach (var s in new[] { "スレッドを表示する", "2件の返信", "5ヶ月前 スレッドを表示する", "PDF 20260810_スキルシート_西田翔平.pdf", "Word 文書 職務経歴書.doc", "Excel スプレッドシート を Slack で表示する",
                                  "既読 3", "ここから未読メッセージ", "アルバムに6件のコンテンツを 追加しました。", "9/25 18:30", "午後 10:02", "返信元", "ボイスメッセージ", "保存｜名前を付けて保存｜転送｜Keepメモに転送", "●●●" })
            Assert.True(ConversationNoise.IsNoise(s), s);
        foreach (var s in new[] { "了解です！", "承知しました。資料は前日までに共有します。", "9/25 18:30からここで！", "PDFは明日送ります", "田中 10:12" })
            Assert.False(ConversationNoise.IsNoise(s), s);
        var msgs = ConversationParser.Parse("山田\nスレッドを表示する\n明日までにお願いします\nPDF 見積書.pdf\n自分: 確認します\n2件の返信", "遠藤");
        Assert.Equal(["明日までにお願いします", "確認します"], msgs.Select(m => m.Text));
        Assert.Equal(["other", "me"], msgs.Select(m => m.Role));
    }

    [Fact]
    public void NewMessagesReturnsOnlyUnknown()
    {
        var store = new ConversationStore(TempFile("c.bin"), SealedBox.NewKey());
        var a = new ConversationMessage("other", "おはよう", "村上亮太");
        var b = new ConversationMessage("me", "おはようございます");
        store.Ingest(Platform.Line, "k", "村上亮太", "LINE", [a]);
        var fresh = store.NewMessages(Platform.Line, "k", "村上亮太", [a, b]);
        Assert.Single(fresh);
        Assert.Equal("me", fresh[0].Role);
        // 名前が数文字ぶれても同じ相手へ寄せる（付録BU）
        Assert.Single(store.NewMessages(Platform.Line, "other-key", "村上亮大", [a, b]));
    }
}

public class LearningStoreTests
{
    static string TempFile(string name) => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), name);

    [Fact]
    public void RoundTripAndLimits()
    {
        var path = TempFile("learning.bin");
        var store = new LearningStore(path, SealedBox.NewKey());
        Assert.Empty(store.Load());
        store.Record("断る", "見送ります。", "見送ります。", Platform.Slack, appName: "Slack");
        Assert.Equal(1, store.Count);
        Assert.Equal(0, store.Load()[0].EditChars);
        Assert.Equal("Slack", store.Load()[0].AppName);
        Assert.Equal(new ReplyRecordKPI(1, 1, 0), store.Kpi(30));
        store.Record("断る", "見送ります。", "今回は見送らせてください。", Platform.Slack);
        store.Record("断る", "x", "y", Platform.Slack);
        Assert.Equal(1, store.Count);
        Assert.Equal(1, store.Load()[0].EditChars);
        Assert.Equal(0, store.Kpi(30).Unedited);
        for (var i = 0; i < 40; i++) store.Record("i" + i, "g", "c" + i, Platform.Gmail, "tanaka");
        Assert.Equal(LearningStore.MaxStored, store.Count);
        Assert.Equal(LearningStore.MaxSent, store.Examples(Platform.Gmail, "tanaka").Count);
        Assert.StartsWith("c", store.Examples(Platform.Gmail, "tanaka")[0].Corrected);
        Assert.Empty(store.Examples(Platform.Gmail));
        Assert.Empty(store.Examples(Platform.Gmail, "suzuki"));
        Assert.Empty(new LearningStore(path, SealedBox.NewKey()).Load());
        Assert.DoesNotContain("見送", File.ReadAllText(path));
        store.DeleteAll();
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void MarkSentOnlyMatchesInsertedText()
    {
        var store = new LearningStore(TempFile("l.bin"), SealedBox.NewKey());
        store.Record("greet", "hello", "hello A", Platform.Slack, "contact-a", action: "insert");
        Assert.False(store.MarkSent(Platform.Slack, "contact-a", "hello A")); // 2 秒未満
        Assert.True(store.MarkSent(Platform.Slack, "contact-a", "hello A", DateTimeOffset.UtcNow.AddSeconds(5)));
        Assert.Equal(1, store.Kpi(30).Sent);
    }
}

public class StyleTests
{
    static string TempFile(string name) => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), name);

    [Fact]
    public void StoreRoundTripReplacesScenario()
    {
        var store = new StyleStore(TempFile("style.bin"), SealedBox.NewKey());
        store.Put("a", "hi", "first");
        store.Put("b", "yo", "second");
        store.Put("a", "hi", "replaced");
        store.Put("c", "x", "   ");
        Assert.Equal(2, store.Count);
        Assert.Equal("replaced", store.Load().First(s => s.Scenario == "a").Reply);
        Assert.Equal(2, store.Request().Samples.Count);
        store.DeleteAll();
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void ProfileNormalizationAndEncoding()
    {
        var p = new StyleProfile { Formality = "casual", Length = "huge", Closing = "light", Emoji = "some" }.Normalized;
        Assert.Null(p.Length);
        Assert.Equal("casual", p.Formality);
        var json = JsonNode.Parse(JsonSerializer.Serialize(new FormatRequest(Platform.Slack, RecipientType.Internal, Tone.Natural, "ok", null, [], new("windows", "0.8.0"), sender: new("", p)), ContractJson.Options))!;
        Assert.Equal("casual", json["sender"]!["style"]!["formality"]!.GetValue<string>());
        Assert.Null(json["sender"]!["style"]!["length"]);
        var empty = JsonNode.Parse(JsonSerializer.Serialize(new FormatRequest(Platform.Slack, RecipientType.Internal, Tone.Natural, "ok", null, [], new("windows", "0.8.0"), sender: new(" ", new StyleProfile())), ContractJson.Options))!;
        Assert.Null(empty["sender"]);
        var res = JsonSerializer.Deserialize<StyleProfileResponse>("""{"profile":{"formality":"standard","greeting":"light"},"confidence":{"formality":0.9},"source":"rules","elapsed_ms":120}""", ContractJson.Options)!;
        Assert.Equal("light", res.Profile.Greeting);
        Assert.Equal("rules", res.Source);
    }

    [Fact]
    public void RegisterRequestCarriesUserName()
    {
        var data = JsonSerializer.Serialize(new RegisterRequest("t", "PC", "windows", "0.8.0", userName: "遠藤巧巳"), ContractJson.Options);
        Assert.Contains("\"user_name\":\"\\u9060\\u85E4\\u5DE7\\u5DF3\"", data, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"os\":\"windows\"", data);
        Assert.Equal("{}", JsonSerializer.Serialize(new DeviceProfileRequest("", "  "), ContractJson.Options));
    }
}

public class ServerAddressTests
{
    [Fact]
    public void RejectsUnencryptedRemoteAndCredentialBearingAddresses()
    {
        foreach (var address in new[] { "http://company.example", "https://user:pass@company.example", "https://company.example?token=secret", "https://company.example#part", "file:///tmp/server", "https:///", "company.example" })
            Assert.True(ServerAddress.Normalized(address) is null, address);
        Assert.NotNull(ServerAddress.Normalized("http://127.0.0.1:8787"));
        Assert.NotNull(ServerAddress.Normalized("http://localhost:8787"));
        Assert.Equal("https://example.com", ServerAddress.Normalized(" https://EXAMPLE.COM:443/ ")!.Canonical);
        Assert.Equal("http://127.0.0.1:8787", ServerAddress.Normalized("http://127.0.0.1:8787/")!.Canonical);
        Assert.True(ServerAddress.Same("https://replyfive.app/", "HTTPS://replyfive.app"));
    }

    [Fact]
    public void DuplicateConnectionParametersAreRejected()
    {
        foreach (var query in new[] { "server=https://example.com&server=https://other.example&link=one", "server=https://example.com&link=one&link=two", "server=http://other.example&link=one", "server=https://example.com&link=", "server=https://example.com&code=OLD-INVITE" })
            Assert.True(ServerAddress.Connection("replyfive://connect?" + query) is null, query);
        var valid = ServerAddress.Connection("replyfive://connect?server=https%3A%2F%2Fexample.com%2F&link=abc.def");
        Assert.NotNull(valid);
        Assert.Equal("https://example.com", valid!.Value.server.Canonical);
        Assert.Equal("abc.def", valid.Value.link);
        Assert.Equal("https://replyfive.app/admin/?connect=1", ServerAddress.SignInUrl(ServerAddress.Normalized("https://replyfive.app")!).ToString());
    }

    [Fact]
    public async Task InsecureServerIsRejectedBeforeNetworkRequest()
    {
        var client = new ApiClient(new Uri("http://private.example"), "secret", "test", "windows");
        var e = await Assert.ThrowsAsync<ApiException>(() => client.DeviceSelf());
        Assert.Equal(ApiErrorKind.InvalidResponse, e.Kind);
    }

    [Fact]
    public void ApiErrorClassification()
    {
        Assert.True(ApiException.Server("subscription_inactive", 402, null).KeepsDraft);
        Assert.True(ApiException.Server("subscription_inactive", 402, null).SuggestsBilling);
        Assert.True(ApiException.Server("device_revoked", 403, null).RequiresReregistration);
        Assert.False(ApiException.Server("prohibited_phrase", 422, null).KeepsDraft);
        Assert.True(ApiException.Timeout().KeepsDraft);
        Assert.Equal("http_502", ApiException.Server("http_502", 502, null).Code);
    }
}

public class MiscTests
{
    [Fact]
    public void EditDistanceCountsCharacters()
    {
        Assert.Equal(0, EditDistance.Levenshtein("同じ", "同じ"));
        Assert.Equal(1, EditDistance.Levenshtein("x", "y"));
        Assert.Equal(1.0, EditDistance.Ratio("", "abc"));
        Assert.Equal(0.0, EditDistance.Ratio("", ""));
        Assert.Equal(1, EditDistance.Levenshtein("👍🏽a", "👍🏽b")); // 絵文字は 1 文字
    }

    [Fact]
    public void VersionComparison()
    {
        Assert.True(ReplyFiveInfo.IsNewerVersion("0.8.1", "0.8.0"));
        Assert.False(ReplyFiveInfo.IsNewerVersion("0.8.0", "0.8.0"));
        Assert.True(ReplyFiveInfo.IsNewerVersion("1.0.0", "0.9.9"));
        Assert.False(ReplyFiveInfo.IsNewerVersion("1.0.0-beta.1", "1.0.0"));
        Assert.True(ReplyFiveInfo.IsNewerVersion("1.0.0", "1.0.0-beta.1"));
        Assert.True(ReplyFiveInfo.IsNewerVersion("1.0.0-beta.2", "1.0.0-beta.1"));
    }

    [Fact]
    public void GrowthQueueKeepsFailedBatches()
    {
        var q = new GrowthQueue(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "events.jsonl"));
        q.Record(new GrowthEvent("app_launched", new() { ["version"] = "0.8.0", ["trusted"] = true, ["count"] = 3 }));
        Assert.Equal(1, q.Count);
        q.Flush(_ => throw new InvalidOperationException()).GetAwaiter().GetResult();
        Assert.Equal(1, q.Count);
        var again = new GrowthQueue(q.Path);
        Assert.Equal(1, again.Count);
        again.Flush(_ => Task.CompletedTask).GetAwaiter().GetResult();
        Assert.Equal(0, again.Count);
        var json = JsonSerializer.Serialize(new GrowthEvent("x", new() { ["a"] = new string('あ', 100) }), ContractJson.Options);
        Assert.Contains("\"ts\":", json);
        Assert.True(JsonNode.Parse(json)!["props"]!["a"]!.GetValue<string>().Length == 64);
    }

    [Fact]
    public void ReplyNameSafetySkipsRoomTitles()
    {
        var msgs = new List<ConversationMessage> { new("other", "hi", "#general チャンネル"), new("other", "yo", "佐藤"), new("me", "ok") };
        Assert.Equal("佐藤", ReplyNameSafety.RecipientName(msgs, null, "Slack"));
        Assert.Null(ReplyNameSafety.RecipientName(msgs, "佐藤", "Slack"));
        Assert.Null(ReplyNameSafety.RecipientName(msgs, null, "Slack", requireRepeated: true));
    }
}
