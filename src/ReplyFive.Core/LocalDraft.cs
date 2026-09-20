using System.Text;
using System.Text.RegularExpressions;

namespace ReplyFive.Core;

/// <summary>契約 local_draft の C# 実装。生成ボタン押下の瞬間に表示し、サーバ応答で置き換える。fixtures/local-draft.json が正。参照実装は server/internal/draft。</summary>
public static class LocalDraft
{
    public enum Script { Ja, Zh, En, Other }

    public static Script ScriptOf(string text)
    {
        int cjk = 0, latin = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var v = rune.Value;
            if ((v >= 0x3040 && v <= 0x309F) || (v >= 0x30A0 && v <= 0x30FF) || (v >= 0x31F0 && v <= 0x31FF)) return Script.Ja;
            if ((v >= 0x4E00 && v <= 0x9FFF) || (v >= 0x3400 && v <= 0x4DBF)) { cjk++; continue; }
            if (v < 0x0250 && Rune.IsLetter(rune)) latin++;
        }
        if (cjk > 0) return Script.Zh;
        if (latin > 0) return Script.En;
        return Script.Other;
    }

    static readonly HashSet<char> terminators = ['。', '！', '？', '.', '!', '?', ',', ':', ';', '）', ')', '」', '』', '】', '"', '\''];
    const string Greeting = "お世話になっております。";
    const string Closing = "よろしくお願いいたします。";
    static readonly Regex blankLines = new("\n{3,}", RegexOptions.Compiled);

    public static string Format(Platform platform, RecipientType recipientType, string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return "";
        var lang = ScriptOf(t);
        t = blankLines.Replace(t, "\n\n");
        var lines = t.Split('\n').Select(raw =>
        {
            var l = raw.TrimEnd(' ', '\t');
            if (l.Length > 0 && !terminators.Contains(l[^1])) l += lang is Script.Ja or Script.Zh ? "。" : ".";
            return l;
        });
        t = string.Join("\n", lines);
        var isMail = platform is Platform.Gmail or Platform.Outlook;
        var isOutside = recipientType != RecipientType.Internal;
        if (lang == Script.Ja && isMail && isOutside)
        {
            if (!t.StartsWith(Greeting, StringComparison.Ordinal)) t = Greeting + "\n" + t;
            if (!t.EndsWith(Closing, StringComparison.Ordinal)) t += "\n" + Closing;
        }
        return t;
    }
}

/// <summary>契約 platform_detection の C# 実装。fixtures/platform-detection.json が正。</summary>
public static class PlatformDetector
{
    static readonly string[] browsers = ["chrome", "safari", "edge", "firefox", "arc", "brave"];
    static readonly (Regex re, Platform platform)[] contentHints =
    [
        (new Regex(@"mail\.google\.com|gmail", RegexOptions.Compiled), Platform.Gmail),
        (new Regex(@"outlook\.office|outlook\.live", RegexOptions.Compiled), Platform.Outlook),
        (new Regex(@"app\.slack\.com|slack\.com", RegexOptions.Compiled), Platform.Slack),
        (new Regex(@"teams\.microsoft\.com|teams\.live\.com", RegexOptions.Compiled), Platform.Teams),
        (new Regex(@"chatwork\.com", RegexOptions.Compiled), Platform.Chatwork),
        (new Regex(@"line\.worksmobile\.com|works\.do", RegexOptions.Compiled), Platform.Lineworks),
        (new Regex(@"chat\.google\.com", RegexOptions.Compiled), Platform.Googlechat),
    ];
    static readonly Regex wordLine = new(@"(^|\s)line(\s|$)", RegexOptions.Compiled);
    static readonly Regex wordMail = new(@"(^|\s)mail(\s|$)", RegexOptions.Compiled);

    public static Platform Detect(string? appName, string text = "")
    {
        var n = (appName ?? "").ToLowerInvariant().Trim();
        var c = text.ToLowerInvariant();
        Platform FromContent() { foreach (var (re, p) in contentHints) if (re.IsMatch(c)) return p; return Platform.Generic; }
        if (n.Length == 0) return FromContent();
        if (browsers.Any(n.Contains)) return FromContent();
        if (n.Contains("slack")) return Platform.Slack;
        if (n.Contains("teams")) return Platform.Teams;
        if (n.Contains("chatwork")) return Platform.Chatwork;
        if (n.Contains("line works") || n.Contains("lineworks")) return Platform.Lineworks;
        if (n == "line" || wordLine.IsMatch(n)) return Platform.Line;
        if (n.Contains("google chat")) return Platform.Googlechat;
        if (n.Contains("outlook")) return Platform.Outlook;
        if (n == "mail" || n.Contains("gmail") || wordMail.IsMatch(n)) return Platform.Gmail;
        return FromContent();
    }
}

/// <summary>サーバの正規化された所在。資格情報と会話本文は TLS でだけ送る（例外はこの端末上の開発サーバ）。</summary>
public sealed class ServerAddress
{
    public Uri Uri { get; }
    /// <summary>末尾スラッシュ無しの正規表記（例 https://replyfive.app）。等価判定と保存に使う。</summary>
    public string Canonical { get; }
    ServerAddress(Uri uri, string canonical) { Uri = uri; Canonical = canonical; }

    static readonly string[] localHosts = ["localhost", "127.0.0.1", "[::1]", "::1"];

    public static ServerAddress? Normalized(string? value)
    {
        if (value is null) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var u)) return null;
        if (string.IsNullOrEmpty(u.Host) || !string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment)) return null;
        var scheme = u.Scheme.ToLowerInvariant();
        var host = u.Host.ToLowerInvariant();
        var local = localHosts.Contains(host) || (u.HostNameType == UriHostNameType.IPv6 && host.Trim('[', ']') == "::1");
        if (!(scheme == "https" || (scheme == "http" && local))) return null;
        var port = u.IsDefaultPort || (scheme == "https" && u.Port == 443) || (scheme == "http" && u.Port == 80) ? "" : ":" + u.Port;
        var path = u.AbsolutePath.TrimEnd('/');
        var hostPart = u.HostNameType == UriHostNameType.IPv6 ? "[" + host.Trim('[', ']') + "]" : host;
        var canonical = scheme + "://" + hostPart + port + path;
        return new ServerAddress(new Uri(canonical + "/"), canonical);
    }

    public static bool Same(string? a, string? b)
    {
        var x = Normalized(a); var y = Normalized(b);
        return x is not null && y is not null && x.Canonical == y.Canonical;
    }

    /// <summary>接続リンク replyfive://connect?server=…&amp;link=…（付録BF）。link はサインイン済みのブラウザが発行した 10 分有効の署名トークン。</summary>
    public static (ServerAddress server, string link)? Connection(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return null;
        if (!u.Scheme.Equals("replyfive", StringComparison.OrdinalIgnoreCase) || u.Host != "connect") return null;
        var items = ParseQuery(u.Query);
        var servers = items.Where(i => i.Key == "server").ToList();
        var links = items.Where(i => i.Key == "link").ToList();
        if (servers.Count != 1 || links.Count != 1) return null;
        var server = Normalized(servers[0].Value);
        var link = links[0].Value;
        if (server is null || string.IsNullOrWhiteSpace(link)) return null;
        return (server, link);
    }

    public static List<KeyValuePair<string, string>> ParseQuery(string query)
    {
        var list = new List<KeyValuePair<string, string>>();
        var q = query.StartsWith('?') ? query[1..] : query;
        if (q.Length == 0) return list;
        foreach (var part in q.Split('&'))
        {
            if (part.Length == 0) continue;
            var eq = part.IndexOf('=');
            var k = eq < 0 ? part : part[..eq];
            var v = eq < 0 ? "" : part[(eq + 1)..];
            list.Add(new(Uri.UnescapeDataString(k.Replace('+', ' ')), Uri.UnescapeDataString(v.Replace('+', ' '))));
        }
        return list;
    }

    /// <summary>アプリの「サインイン」が開くページ。サインイン（新規登録を含む）後に接続リンクが返ってくる。</summary>
    public static Uri SignInUrl(ServerAddress server) => new(server.Canonical + "/admin/?connect=1");

    /// <summary>管理画面の請求ページ。</summary>
    public static Uri BillingUrl(ServerAddress server) => new(server.Canonical + "/admin/#billing");
}

public static class ReplyFiveInfo
{
    public const string Version = "0.8.0";
    public const string ContractVersion = "0.2";
    public static readonly Uri DefaultServerUrl = new("https://replyfive.app");

    public static bool IsNewerVersion(string candidate, string current)
    {
        static (int[] numbers, string[]? pre) Parts(string value)
        {
            var core = value.Split('+', 2)[0];
            var pieces = core.Split('-', 2);
            var numbers = pieces[0].Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
            var pre = pieces.Length > 1 ? pieces[1].Split('.') : null;
            return (numbers, pre);
        }
        var (ln, lp) = Parts(candidate);
        var (rn, rp) = Parts(current);
        for (var i = 0; i < Math.Max(ln.Length, rn.Length); i++)
        {
            var l = i < ln.Length ? ln[i] : 0;
            var r = i < rn.Length ? rn[i] : 0;
            if (l != r) return l > r;
        }
        if (lp is null && rp is null) return false;
        if (lp is null) return true;
        if (rp is null) return false;
        for (var i = 0; i < Math.Max(lp.Length, rp.Length); i++)
        {
            if (i >= lp.Length) return false;
            if (i >= rp.Length) return true;
            if (lp[i] == rp[i]) continue;
            var li = int.TryParse(lp[i], out var lnum);
            var ri = int.TryParse(rp[i], out var rnum);
            if (li && ri) return lnum > rnum;
            if (li) return false;
            if (ri) return true;
        }
        return false;
    }
}

/// <summary>生成文と最終文の差の大きさ（付録BC）。本文は送らず、この比率だけを計測に使う。文字（テキスト要素）単位。</summary>
public static class EditDistance
{
    public static string[] Elements(string s)
    {
        var list = new List<string>();
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) list.Add(e.GetTextElement());
        return list.ToArray();
    }

    /// <summary>正規化レーベンシュタイン距離（0 = 同一、1 = 全面書き換え）。</summary>
    public static double Ratio(string a, string b)
    {
        var x = Elements(a); var y = Elements(b);
        var n = Math.Max(x.Length, y.Length);
        if (n == 0) return 0;
        return Math.Clamp((double)Levenshtein(x, y) / n, 0, 1);
    }

    public static int Levenshtein(string a, string b) => Levenshtein(Elements(a), Elements(b));

    public static int Levenshtein(string[] x, string[] y)
    {
        if (x.Length == 0) return y.Length;
        if (y.Length == 0) return x.Length;
        var prev = new int[y.Length + 1];
        var cur = new int[y.Length + 1];
        for (var j = 0; j <= y.Length; j++) prev[j] = j;
        for (var i = 1; i <= x.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= y.Length; j++)
            {
                var cost = x[i - 1] == y[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[y.Length];
    }
}
