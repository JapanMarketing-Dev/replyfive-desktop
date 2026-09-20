using System.Text.Json;
using System.Text.Json.Nodes;
using ReplyFive.Core;

namespace ReplyFive.Core.Tests;

/// <summary>shared/fixtures を読み、サーバ参照実装と同じ結果になることを確認する。</summary>
public class FixtureTests
{
    public static string FixturesDir
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            for (var i = 0; i < 12; i++)
            {
                var candidate = Path.Combine(dir, "shared", "fixtures");
                if (Directory.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir) ?? throw new DirectoryNotFoundException("shared/fixtures");
            }
            throw new DirectoryNotFoundException("shared/fixtures");
        }
    }

    static JsonNode Load(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(FixturesDir, name)))!;

    [Fact]
    public void PlatformDetectionMatchesFixture()
    {
        var cases = Load("platform-detection.json").AsArray();
        Assert.True(cases.Count > 10);
        foreach (var c in cases)
        {
            var appName = c!["app_name"]?.GetValue<string>();
            var text = c["text"]?.GetValue<string>() ?? "";
            Assert.True(c["expected"]!.GetValue<string>() == PlatformDetector.Detect(appName, text).Wire(), $"{appName} / {text}");
        }
    }

    [Fact]
    public void LocalDraftMatchesFixture()
    {
        var cases = Load("local-draft.json").AsArray();
        Assert.True(cases.Count > 8);
        foreach (var c in cases)
        {
            var platform = ContractJson.ParsePlatform(c!["platform"]!.GetValue<string>())!.Value;
            var recipient = ContractJson.ParseRecipient(c["recipient_type"]!.GetValue<string>())!.Value;
            var got = LocalDraft.Format(platform, recipient, c["text"]!.GetValue<string>());
            Assert.True(c["expected"]!.GetValue<string>() == got, c["name"]!.GetValue<string>() + ": " + got);
        }
    }

    [Fact]
    public void FormatRequestEncodesContractKeys()
    {
        var req = new FormatRequest(Platform.Gmail, RecipientType.Customer, Tone.Polite, "ok",
            new ConversationContext(ContextSource.SelectedText, "Google Chrome", "hi"), [new LearnedExample("a", "b")], new FormatRequest.ClientInfo("windows", "0.2.0"));
        var json = JsonNode.Parse(JsonSerializer.Serialize(req, ContractJson.Options))!;
        Assert.Equal("customer", json["recipient_type"]!.GetValue<string>());
        Assert.Equal("polite", json["tone"]!.GetValue<string>());
        Assert.Equal("gmail", json["platform"]!.GetValue<string>());
        Assert.Equal("ok", json["user_intent"]!.GetValue<string>());
        Assert.Equal("auto", json["language"]!.GetValue<string>());
        Assert.Equal("Google Chrome", json["conversation_context"]!["app_name"]!.GetValue<string>());
        Assert.Equal("selected_text", json["conversation_context"]!["source"]!.GetValue<string>());
        Assert.Single(json["learned_examples"]!.AsArray());
        Assert.Null(json["sender"]);
        Assert.Equal("windows", json["client"]!["os"]!.GetValue<string>());
    }

    [Fact]
    public void ValidFixtureRequestsRoundTrip()
    {
        // 契約の妥当なリクエストは端末のモデルで読めて、同じ項目名で書き戻せる
        var valid = Load("format-requests.json")["valid"]!.AsArray();
        Assert.True(valid.Count >= 5);
        foreach (var c in valid)
        {
            // 語彙外の platform をサーバが generic に寄せる例は、端末側の列挙では読めなくてよい
            if (c!["expect_platform"] is not null) continue;
            var raw = c["request"]!.ToJsonString();
            var req = JsonSerializer.Deserialize<FormatRequest>(raw, ContractJson.Options);
            Assert.NotNull(req);
            var back = JsonNode.Parse(JsonSerializer.Serialize(req, ContractJson.Options))!;
            var expectedIntent = c["request"]!["user_intent"]?.GetValue<string>() ?? "";
            Assert.Equal(expectedIntent, back["user_intent"]!.GetValue<string>());
            if (c["request"]!["conversation_context"]?["messages"] is JsonArray messages)
                Assert.Equal(messages.Count, back["conversation_context"]!["messages"]!.AsArray().Count);
        }
    }

    [Fact]
    public void DeviceSelfFixtureDecodes()
    {
        var examples = Load("device-self.json")["examples"]!.AsArray();
        Assert.Equal(3, examples.Count);
        var responses = examples.Select(e => JsonSerializer.Deserialize<DeviceSelfResponse>(e!["response"]!.ToJsonString(), ContractJson.Options)!).ToList();
        Assert.Equal("trial", responses[0].Entitlement.Status);
        Assert.Equal(12, responses[0].Entitlement.Trial!.DaysLeft);
        Assert.Equal("0.4.0", responses[0].Client.LatestVersion);
        Assert.False(responses[1].Policy.LearningAllowed);
        Assert.Null(responses[1].Client.LatestVersion);
        Assert.Equal("license_grace", responses[2].Entitlement.Status);
    }

    [Fact]
    public void MetaLatestClientCarriesEveryOs()
    {
        var meta = JsonSerializer.Deserialize<MetaResponse>("""{"mode":"saas","version":"0.8.5","contract_version":"0.2","llm":{"provider":"groq","model":"m"},"latest_client":{"macos":{"version":"0.8.0","download_url":"https://replyfive.app/download/macos"},"windows":{"version":"0.8.0","download_url":"https://replyfive.app/download/windows"},"linux":{"version":null,"download_url":"https://replyfive.app/download/linux"}}}""", ContractJson.Options)!;
        Assert.Equal("0.8.0", meta.LatestClient!.For("windows")!.LatestVersion);
        Assert.Null(meta.LatestClient.For("linux")!.LatestVersion);
        Assert.Null(meta.LatestClient.For("freebsd"));
    }
}
