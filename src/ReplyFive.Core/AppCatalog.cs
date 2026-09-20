namespace ReplyFive.Core;

public enum AppCategory { Mail, TeamChat, BusinessChat, Messaging, Sns, Support }

/// <summary>付録CF-8：利用者が「普段使う」と選べるチャット・メールアプリ。platform は生成時の文体（契約の Platform）への対応。
/// aliases は前面アプリ名・ウインドウ名との照合語（小文字）。macOS 版 AppCatalog.swift の移植。</summary>
public sealed record AppCatalogEntry(string Id, string Name, AppCategory Category, Platform Platform, IReadOnlyList<string> Aliases, bool Primary)
{
    /// <summary>収集した会話の appName（前面アプリ名）がこのアプリか。代表アプリ（Primary）は platform が一致すれば aliases に無くても数える。</summary>
    public bool Matches(string? appName, Platform platform)
    {
        if (Primary && platform == Platform) return true;
        var n = appName?.ToLowerInvariant();
        if (string.IsNullOrEmpty(n)) return false;
        return Aliases.Any(a => n.Contains(a, StringComparison.Ordinal));
    }

    public string CategoryKey => Category switch
    {
        AppCategory.Mail => "mail", AppCategory.TeamChat => "teamChat", AppCategory.BusinessChat => "businessChat",
        AppCategory.Messaging => "messaging", AppCategory.Sns => "sns", _ => "support",
    };
}

public static class AppCatalog
{
    static AppCatalogEntry E(string id, string name, AppCategory c, Platform p, string[]? aliases = null, bool primary = false)
        => new(id, name, c, p, (aliases ?? []).Append(name).Select(a => a.ToLowerInvariant()).ToList(), primary);

    public static readonly IReadOnlyList<AppCatalogEntry> All =
    [
        // メール
        E("gmail", "Gmail", AppCategory.Mail, Platform.Gmail, ["mail.google.com"], primary: true),
        E("outlook", "Outlook", AppCategory.Mail, Platform.Outlook, ["outlook.office", "outlook.live", "hotmail"], primary: true),
        E("mail", "Apple Mail", AppCategory.Mail, Platform.Gmail, ["mail", "メール"]),
        E("yahoomail", "Yahoo!メール", AppCategory.Mail, Platform.Gmail, ["yahoo mail", "mail.yahoo"]),
        E("thunderbird", "Thunderbird", AppCategory.Mail, Platform.Gmail),
        E("spark", "Spark", AppCategory.Mail, Platform.Gmail),
        E("hey", "HEY", AppCategory.Mail, Platform.Gmail, ["app.hey.com"]),
        E("superhuman", "Superhuman", AppCategory.Mail, Platform.Gmail),
        E("proton", "Proton Mail", AppCategory.Mail, Platform.Gmail, ["protonmail", "proton.me"]),
        E("icloudmail", "iCloud メール", AppCategory.Mail, Platform.Gmail, ["icloud.com/mail"]),
        E("zohomail", "Zoho Mail", AppCategory.Mail, Platform.Gmail, ["mail.zoho"]),
        E("fastmail", "Fastmail", AppCategory.Mail, Platform.Gmail),
        E("front", "Front", AppCategory.Mail, Platform.Gmail, ["frontapp", "app.frontapp.com"]),
        E("missive", "Missive", AppCategory.Mail, Platform.Gmail),
        E("notionmail", "Notion Mail", AppCategory.Mail, Platform.Gmail),
        E("edison", "Edison Mail", AppCategory.Mail, Platform.Gmail),
        E("canary", "Canary Mail", AppCategory.Mail, Platform.Gmail),
        E("mailbird", "Mailbird", AppCategory.Mail, Platform.Gmail),
        // チームチャット
        E("slack", "Slack", AppCategory.TeamChat, Platform.Slack, ["app.slack.com"], primary: true),
        E("teams", "Microsoft Teams", AppCategory.TeamChat, Platform.Teams, ["teams.microsoft.com", "teams.live.com"], primary: true),
        E("googlechat", "Google Chat", AppCategory.TeamChat, Platform.Googlechat, ["chat.google.com"], primary: true),
        E("discord", "Discord", AppCategory.TeamChat, Platform.Line),
        E("zoomchat", "Zoom Team Chat", AppCategory.TeamChat, Platform.Teams, ["zoom.us", "zoom"]),
        E("webex", "Webex", AppCategory.TeamChat, Platform.Teams),
        E("mattermost", "Mattermost", AppCategory.TeamChat, Platform.Slack),
        E("rocketchat", "Rocket.Chat", AppCategory.TeamChat, Platform.Slack, ["rocket.chat"]),
        E("twist", "Twist", AppCategory.TeamChat, Platform.Slack),
        E("flock", "Flock", AppCategory.TeamChat, Platform.Slack),
        E("lark", "Lark（Feishu）", AppCategory.TeamChat, Platform.Slack, ["lark", "feishu"]),
        E("basecamp", "Basecamp", AppCategory.TeamChat, Platform.Slack),
        E("workplace", "Workplace", AppCategory.TeamChat, Platform.Slack, ["workplace.com"]),
        // ビジネスチャット（日本）
        E("chatwork", "Chatwork", AppCategory.BusinessChat, Platform.Chatwork, ["chatwork.com"], primary: true),
        E("lineworks", "LINE WORKS", AppCategory.BusinessChat, Platform.Lineworks, ["line works", "worksmobile", "works.do"], primary: true),
        E("talknote", "Talknote", AppCategory.BusinessChat, Platform.Chatwork),
        E("typetalk", "Typetalk", AppCategory.BusinessChat, Platform.Chatwork),
        E("direct", "direct", AppCategory.BusinessChat, Platform.Chatwork, ["direct4b"]),
        E("tocaro", "Tocaro", AppCategory.BusinessChat, Platform.Chatwork),
        E("elgana", "elgana", AppCategory.BusinessChat, Platform.Chatwork),
        E("wowtalk", "WowTalk", AppCategory.BusinessChat, Platform.Chatwork),
        E("backlog", "Backlog", AppCategory.BusinessChat, Platform.Chatwork, ["backlog.com", "backlog.jp"]),
        E("kintone", "kintone", AppCategory.BusinessChat, Platform.Chatwork, ["cybozu.com"]),
        E("garoon", "Garoon", AppCategory.BusinessChat, Platform.Chatwork),
        E("notion", "Notion", AppCategory.BusinessChat, Platform.Slack, ["notion.so"]),
        E("asana", "Asana", AppCategory.BusinessChat, Platform.Slack, ["app.asana.com"]),
        E("jira", "Jira", AppCategory.BusinessChat, Platform.Slack, ["atlassian.net"]),
        E("github", "GitHub", AppCategory.BusinessChat, Platform.Slack, ["github.com"]),
        // メッセージ
        E("line", "LINE", AppCategory.Messaging, Platform.Line, [], primary: true),
        E("whatsapp", "WhatsApp", AppCategory.Messaging, Platform.Line, ["web.whatsapp.com"]),
        E("messenger", "Messenger", AppCategory.Messaging, Platform.Line, ["messenger.com"]),
        E("telegram", "Telegram", AppCategory.Messaging, Platform.Line, ["web.telegram.org"]),
        E("signal", "Signal", AppCategory.Messaging, Platform.Line),
        E("imessage", "Apple メッセージ", AppCategory.Messaging, Platform.Line, ["messages", "メッセージ"]),
        E("viber", "Viber", AppCategory.Messaging, Platform.Line),
        E("wechat", "WeChat", AppCategory.Messaging, Platform.Line, ["weixin"]),
        E("kakaotalk", "KakaoTalk", AppCategory.Messaging, Platform.Line, ["kakao"]),
        E("skype", "Skype", AppCategory.Messaging, Platform.Line),
        E("plusmessage", "+メッセージ", AppCategory.Messaging, Platform.Line),
        // SNS のダイレクトメッセージ
        E("instagram", "Instagram", AppCategory.Sns, Platform.Line, ["instagram.com"]),
        E("x", "X（Twitter）", AppCategory.Sns, Platform.Line, ["x.com", "twitter", "twitter.com"]),
        E("facebook", "Facebook", AppCategory.Sns, Platform.Line, ["facebook.com"]),
        E("linkedin", "LinkedIn", AppCategory.Sns, Platform.Gmail, ["linkedin.com"]),
        E("threads", "Threads", AppCategory.Sns, Platform.Line, ["threads.net"]),
        E("tiktok", "TikTok", AppCategory.Sns, Platform.Line, ["tiktok.com"]),
        E("youtube", "YouTube", AppCategory.Sns, Platform.Line, ["youtube.com"]),
        // 顧客対応・問い合わせ
        E("intercom", "Intercom", AppCategory.Support, Platform.Gmail, ["app.intercom"]),
        E("zendesk", "Zendesk", AppCategory.Support, Platform.Gmail, ["zendesk.com"]),
        E("hubspot", "HubSpot", AppCategory.Support, Platform.Gmail, ["app.hubspot.com"]),
        E("salesforce", "Salesforce", AppCategory.Support, Platform.Gmail, ["salesforce.com", "lightning.force.com"]),
        E("freshdesk", "Freshdesk", AppCategory.Support, Platform.Gmail, ["freshdesk.com"]),
        E("channeltalk", "チャネルトーク", AppCategory.Support, Platform.Chatwork, ["channel.io", "channel talk"]),
        E("crisp", "Crisp", AppCategory.Support, Platform.Gmail, ["app.crisp.chat"]),
        E("helpscout", "Help Scout", AppCategory.Support, Platform.Gmail, ["helpscout.net"]),
        E("zohodesk", "Zoho Desk", AppCategory.Support, Platform.Gmail, ["desk.zoho"]),
        E("reamaze", "Re:amaze", AppCategory.Support, Platform.Gmail, ["reamaze.com"]),
        E("mercari", "メルカリ", AppCategory.Support, Platform.Line, ["mercari"]),
        E("yahooauction", "Yahoo!オークション", AppCategory.Support, Platform.Line, ["auctions.yahoo", "ヤフオク"]),
        E("rakuma", "楽天ラクマ", AppCategory.Support, Platform.Line, ["fril.jp", "ラクマ"]),
        E("airbnb", "Airbnb", AppCategory.Support, Platform.Gmail, ["airbnb.com", "airbnb.jp"]),
        E("booking", "Booking.com", AppCategory.Support, Platform.Gmail, ["booking.com"]),
    ];

    public static AppCatalogEntry? Entry(string id) => All.FirstOrDefault(e => e.Id == id);
    public static List<AppCatalogEntry> Entries(IEnumerable<string> ids) => ids.Select(Entry).OfType<AppCatalogEntry>().ToList();

    /// <summary>検索語（名前・別名の部分一致、大文字小文字を区別しない）とカテゴリで絞る。</summary>
    public static List<AppCatalogEntry> Filter(string query, AppCategory? category)
    {
        var q = query.Trim().ToLowerInvariant();
        return All.Where(e => (category is null || e.Category == category) && (q.Length == 0 || e.Name.ToLowerInvariant().Contains(q) || e.Aliases.Any(a => a.Contains(q)) || e.Id.Contains(q))).ToList();
    }

    /// <summary>付録CF-9：端末に入っているアプリ（表示名）からの自動選択。名前は大文字小文字を区別しない完全一致。
    /// Windows はスタートメニューの名前と Uninstall の DisplayName、Linux は .desktop の Name= を渡す。</summary>
    static readonly Dictionary<string, string[]> installNames = new()
    {
        ["gmail"] = ["Gmail"], ["outlook"] = ["Microsoft Outlook", "Outlook", "Outlook (new)", "Outlook (classic)"], ["mail"] = ["Mail", "メール"], ["yahoomail"] = ["Yahoo!メール", "Yahoo Mail"],
        ["thunderbird"] = ["Thunderbird", "Mozilla Thunderbird"], ["spark"] = ["Spark", "Spark Desktop", "Spark Mail"], ["hey"] = ["HEY"], ["superhuman"] = ["Superhuman"], ["proton"] = ["Proton Mail", "ProtonMail"],
        ["zohomail"] = ["Zoho Mail"], ["fastmail"] = ["Fastmail"], ["front"] = ["Front"], ["missive"] = ["Missive"], ["notionmail"] = ["Notion Mail"], ["edison"] = ["Edison Mail"], ["canary"] = ["Canary Mail"], ["mailbird"] = ["Mailbird"],
        ["slack"] = ["Slack"], ["teams"] = ["Microsoft Teams", "Microsoft Teams (work or school)", "Microsoft Teams classic", "Teams"], ["googlechat"] = ["Google Chat"], ["discord"] = ["Discord"],
        ["zoomchat"] = ["zoom.us", "Zoom", "Zoom Workplace"], ["webex"] = ["Webex", "Cisco Webex Meetings"], ["mattermost"] = ["Mattermost"], ["rocketchat"] = ["Rocket.Chat"], ["twist"] = ["Twist"], ["flock"] = ["Flock"],
        ["lark"] = ["Lark", "Feishu"], ["basecamp"] = ["Basecamp"], ["workplace"] = ["Workplace"],
        ["chatwork"] = ["Chatwork"], ["lineworks"] = ["LINE WORKS"], ["talknote"] = ["Talknote"], ["typetalk"] = ["Typetalk"], ["direct"] = ["direct"], ["tocaro"] = ["Tocaro"], ["elgana"] = ["elgana"], ["wowtalk"] = ["WowTalk"],
        ["backlog"] = ["Backlog"], ["kintone"] = ["kintone"], ["garoon"] = ["Garoon"], ["notion"] = ["Notion"], ["asana"] = ["Asana"], ["jira"] = ["Jira"], ["github"] = ["GitHub", "GitHub Desktop"],
        ["line"] = ["LINE"], ["whatsapp"] = ["WhatsApp"], ["messenger"] = ["Messenger"], ["telegram"] = ["Telegram", "Telegram Desktop"], ["signal"] = ["Signal"], ["imessage"] = ["Messages", "メッセージ"], ["viber"] = ["Viber"],
        ["wechat"] = ["WeChat"], ["kakaotalk"] = ["KakaoTalk"], ["skype"] = ["Skype"], ["plusmessage"] = ["+メッセージ"],
        ["instagram"] = ["Instagram"], ["x"] = ["X", "Twitter"], ["facebook"] = ["Facebook"], ["linkedin"] = ["LinkedIn"], ["threads"] = ["Threads"], ["tiktok"] = ["TikTok"], ["youtube"] = ["YouTube"],
        ["intercom"] = ["Intercom"], ["zendesk"] = ["Zendesk"], ["hubspot"] = ["HubSpot"], ["salesforce"] = ["Salesforce"], ["freshdesk"] = ["Freshdesk"], ["channeltalk"] = ["チャネルトーク", "Channel Talk", "Channel.io"],
        ["crisp"] = ["Crisp"], ["helpscout"] = ["Help Scout"], ["zohodesk"] = ["Zoho Desk"], ["reamaze"] = ["Re:amaze"], ["mercari"] = ["メルカリ", "Mercari"], ["yahooauction"] = ["Yahoo!オークション", "ヤフオク!"],
        ["rakuma"] = ["楽天ラクマ", "ラクマ"], ["airbnb"] = ["Airbnb"], ["booking"] = ["Booking.com"],
    };

    /// <summary>インストール済み・起動中のアプリ名に一致するアプリ。カタログの順で返す。</summary>
    public static List<AppCatalogEntry> MatchInstalled(IEnumerable<string> names)
    {
        var set = new HashSet<string>(names.Select(n => n.Trim().ToLowerInvariant()));
        return All.Where(e => installNames.TryGetValue(e.Id, out var ns) && ns.Any(n => set.Contains(n.ToLowerInvariant()))).ToList();
    }

    /// <summary>既に読み取った会話（前面アプリ名と platform）に一致するアプリ。ブラウザで使う Gmail や Chatwork はこちらで拾える。</summary>
    public static List<AppCatalogEntry> MatchConversations(IEnumerable<(string? AppName, Platform Platform)> pairs)
    {
        var list = pairs.ToList();
        return All.Where(e => list.Any(p => e.Matches(p.AppName, p.Platform))).ToList();
    }
}
