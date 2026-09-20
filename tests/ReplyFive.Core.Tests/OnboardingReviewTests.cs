using System.Text.Json;
using ReplyFive.Core;
using Xunit;

namespace ReplyFive.Core.Tests;

/// <summary>macOS 版 OnboardingReviewTests の移植（付録CF・CF-8）。</summary>
public class OnboardingReviewTests
{
    static ConversationEntry Entry(string key, (string role, string text)[] msgs, long at, Platform platform = Platform.Slack) => new()
    {
        Platform = platform, ContactKey = key, ContactName = "person " + new string(key.Where(c => !char.IsDigit(c)).ToArray()),
        AppName = platform == Platform.Gmail ? "Gmail" : "Slack",
        Messages = msgs.Select(m => new ConversationMessage(m.role, m.text)).ToList(), UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(at),
    };

    [Fact]
    public void CandidatesEndAtLastReceivedAndSkipContactsWithoutReceived()
    {
        var entries = new[]
        {
            Entry("a", [("other", "こんにちは"), ("me", "どうも"), ("other", "水曜どう？"), ("me", "確認します")], 10),
            Entry("b", [("me", "送りました")], 20),
            Entry("c", [("unknown", "x"), ("other", "見積お願いします")], 30),
        };
        var c = OnboardingReview.Candidates(entries);
        Assert.Equal(["c", "a"], c.Select(x => x.ContactKey));
        Assert.Equal("水曜どう？", c[1].LastReceived);
        Assert.Equal(["こんにちは", "どうも", "水曜どう？"], c[1].Context.Select(m => m.Text));
        Assert.Equal(2, c[0].Context.Count);
        Assert.Equal(["other", "me", "other", "me"], c[1].Exchange.Select(m => m.Role));
        Assert.Equal(["見積お願いします"], c[0].Exchange.Select(m => m.Text));
    }

    [Fact]
    public void UiFragmentsAreNotContacts()
    {
        foreach (var bad in new[] { "概要はありません", "リマインダーを作成する", "1件の返信 22時間前", "3 replies", "既読", "", "Teams and Channels" }) Assert.False(OnboardingReview.IsPersonLikeName(bad), bad);
        foreach (var good in new[] { "山下 正樹(Masaki Yamashita)", "JapanMarketing合同会社様×吉川弁護士", "Daniel Reed", "かとう" }) Assert.True(OnboardingReview.IsPersonLikeName(good), good);
        var junk = new ConversationEntry { Platform = Platform.Slack, ContactKey = "j", ContactName = "1件の返信 22時間前", AppName = "Slack", Messages = [new("other", "x")] };
        var anon = new ConversationEntry { Platform = Platform.Slack, ContactKey = "n", ContactName = null, AppName = "Slack", Messages = [new("other", "x")] };
        Assert.Empty(OnboardingReview.Candidates([junk, anon]));
    }

    [Fact]
    public void CandidatesRespectMaxAndChars()
    {
        var many = Enumerable.Range(0, 8).Select(i => Entry("k" + i, [("other", new string('あ', 3000))], i)).ToList();
        Assert.Equal(3, OnboardingReview.Candidates(many, max: 3).Count);
        var longEntry = Entry("l", [("other", new string('い', 3000)), ("other", new string('う', 3000))], 1);
        var c = OnboardingReview.Candidates([longEntry], maxChars: 3100);
        Assert.Single(c[0].Context);
        Assert.Equal('う', c[0].LastReceived[0]);
    }

    [Fact]
    public void AutoIntentEncoding()
    {
        var req = new FormatRequest(Platform.Slack, RecipientType.Internal, Tone.Natural, "", new ConversationContext(ContextSource.Window, "Slack", "x"), [], new FormatRequest.ClientInfo("linux", "0.8.1")) { AutoIntent = true };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(req, ContractJson.Options));
        Assert.True(doc.RootElement.GetProperty("auto_intent").GetBoolean());
        Assert.Equal("", doc.RootElement.GetProperty("user_intent").GetString());
        var plain = new FormatRequest(Platform.Slack, RecipientType.Internal, Tone.Natural, "ok", null, [], new FormatRequest.ClientInfo("linux", "0.8.1"));
        using var doc2 = JsonDocument.Parse(JsonSerializer.Serialize(plain, ContractJson.Options));
        Assert.False(doc2.RootElement.TryGetProperty("auto_intent", out _));
    }

    [Fact]
    public void ProgressEnoughAndStyleSamples()
    {
        var slackA = Entry("a", [("other", "水曜どう？"), ("me", "水曜OKです"), ("other", "では14時に")], 10);
        var slackB = Entry("b", [("other", "資料ください")], 9);
        var mailC = Entry("c", [("other", "見積の件"), ("me", "金曜までにお送りします")], 8, Platform.Gmail);
        var prog = OnboardingReview.Progress([slackA, slackB, mailC], AppCatalog.Entries(["slack", "gmail", "chatwork"]));
        Assert.Equal([2, 1, 0], prog.Select(p => p.Contacts));
        Assert.Equal([1, 1, 0], prog.Select(p => p.WithOwnReply));
        Assert.False(OnboardingReview.IsEnough(prog));
        Assert.True(OnboardingReview.IsEnough(OnboardingReview.Progress([slackA, slackB, mailC], AppCatalog.Entries(["slack", "gmail"]))));
        Assert.False(OnboardingReview.IsEnough(OnboardingReview.Progress([slackA], AppCatalog.Entries(["slack"]))));
        var many = Enumerable.Range(0, 5).Select(i => Entry("p" + i, [("other", "q"), ("me", "a")], i)).ToList();
        Assert.True(OnboardingReview.IsEnough(OnboardingReview.Progress(many, AppCatalog.Entries(["slack", "line"]))));
        var samples = OnboardingReview.StyleSamples([slackA, slackB, mailC]);
        Assert.Equal(["水曜OKです", "金曜までにお送りします"], samples.Select(s => s.Reply));
        Assert.Equal("水曜どう？", samples[0].Received);
    }

    [Fact]
    public void CatalogFilterAndMatching()
    {
        Assert.True(AppCatalog.All.Count > 60);
        Assert.Equal(AppCatalog.All.Count, AppCatalog.All.Select(a => a.Id).Distinct().Count());
        Assert.Equal(["slack"], AppCatalog.Filter("slack", null).Select(a => a.Id));
        Assert.All(AppCatalog.Filter("", AppCategory.Mail), a => Assert.Equal(AppCategory.Mail, a.Category));
        Assert.Equal("mercari", AppCatalog.Filter("メルカリ", AppCategory.Support).First().Id);
        var discord = AppCatalog.Entry("discord")!;
        Assert.True(discord.Matches("Discord", Platform.Generic));
        Assert.False(discord.Matches("Slack", Platform.Slack));
        Assert.True(AppCatalog.Entry("slack")!.Matches("Google Chrome", Platform.Slack));
        Assert.Equal(8, AppCatalog.Entries(["slack", "teams", "gmail", "outlook", "chatwork", "lineworks", "googlechat", "line"]).Count);
        Assert.Contains("slack", AppCatalog.MatchInstalled(["Slack", "Google Chrome"]).Select(a => a.Id));
        Assert.Contains("chatwork", AppCatalog.MatchConversations([("Google Chrome", Platform.Chatwork)]).Select(a => a.Id));
    }
}
