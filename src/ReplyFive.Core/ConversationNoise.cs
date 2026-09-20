using System.Text.RegularExpressions;

namespace ReplyFive.Core;

/// <summary>付録BV：画面から読んだ行のうち、人が書いた発言ではないもの（UI の操作語・添付チップ・日付区切り・既読・引用見出し・システム通知）を規則で落とす。
/// ここで落ちなかったものはサーバの Jev 判定（POST /v1/context/filter）に回し、残ったものだけを端末内の記録に保存する。</summary>
public static class ConversationNoise
{
    static readonly HashSet<string> exact =
    [
        "メッセージを入力", "メッセージを入力...", "メッセージを入力…", "Type a message", "Message", "既読", "未読", "Load", "Loading", "読み込み中",
        "スレッドを表示する", "スレッドで返信する", "View thread", "Reply in thread", "返信元", "メッセージリンク", "Message link",
        "ボイスメッセージ", "Voice message", "スタンプ", "Sticker", "写真", "Photo", "動画", "Video", "ファイル", "File", "画像", "Image",
        "TO", "RE", "編集済み", "(edited)", "edited", "削除されたメッセージです", "This message was deleted", "送信取消", "Unsent",
    ];

    static readonly Regex[] patterns = new[]
    {
        @"^(既読|Read)\s*\d*$",
        @"^ここから未読",
        @"^\d+\s*(件の返信|replies|reply|件のリアクション|reactions?)(\s.*)?$",
        @"^(最終返信|last reply|\d+\s*(ヶ月|か月|日|時間|分|秒|週間)前|\d+\s*(months?|days?|hours?|minutes?|weeks?)\s+ago)\b.*$",
        @"^(PDF|Word 文書|Excel スプレッドシート|PowerPoint プレゼンテーション|Zip|ZIP|テキストファイル|Word document|Excel spreadsheet|PowerPoint presentation)\b.*$",
        @".*(を Slack で表示する|View in Slack)$",
        @"^.+\.(pdf|docx?|xlsx?|pptx?|zip|csv|png|jpe?g|gif|heic|mov|mp4|txt|md)$",
        @"^アルバムに\d+件のコンテンツを\s*追加しました。?$",
        @"^.+(が参加しました|が退出しました|がグループに招待しました|joined the channel|left the channel|has joined|has left)。?$",
        @"^(保存｜名前を付けて保存｜転送｜Keepメモに転送|保存\|名前を付けて保存\|転送\|Keepメモに転送)$",
        @"^[\d\s:./\-年月日曜()（）,]+$",                      // 日付・時刻だけ
        @"^(午前|午後)\s*\d{1,2}:\d{2}$",
        @"^(mon|tue|wed|thu|fri|sat|sun)[a-z]*(day)?,?\s+\w+\s+\d+.*$",
        @"^(今日|昨日|Today|Yesterday)$",
        @"^[\p{So}\p{Sk}\p{P}\s\d]+$",                           // 絵文字・記号・数字だけ
    }.Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled)).ToArray();

    /// <summary>この行は保存対象ではない。</summary>
    public static bool IsNoise(string line)
    {
        var t = line.Trim();
        if (t.Length == 0 || exact.Contains(t)) return true;
        return patterns.Any(p => p.IsMatch(t));
    }

    /// <summary>発言の本文から、末尾や単独行のノイズ行を取り除く（複数行の発言の中に「スレッドを表示する」が混ざるため）。</summary>
    public static string Clean(string text)
        => string.Join("\n", text.Split('\n').Where(l => l.Length > 0 && !IsNoise(l)));
}

/// <summary>宛名の決定に使う端末側の補助。生成結果の宛名検証はサーバ側の Jev 検証（付録BN）に任せ、端末側では生成結果を捨てない。</summary>
public static class ReplyNameSafety
{
    static readonly string[] markers = ["×", "チャンネル", "チーム", "グループ", "トークルーム", "room", "channel", "株式会社", "合同会社", "法人", "事務所", "お問い合わせ先", "プレビュー", "管理者", "参加しています", "メッセージ検索"];

    /// <summary>画面の見出し（ルーム・チャンネル名）を宛名にせず、発言に付いた送信者だけを採用する。</summary>
    public static string? RecipientName(IReadOnlyList<ConversationMessage> messages, string? fallback, string? appName, bool requireRepeated = false)
    {
        var fb = fallback?.Trim();
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var m = messages[i];
            if (m.Role != "other") continue;
            var sender = m.Sender?.Trim();
            if (string.IsNullOrEmpty(sender) || sender == fb || !IsDirect(sender, appName)) continue;
            if (requireRepeated)
            {
                var occurrences = messages.Count(x => x.Role == "other" && x.Sender?.Trim() == sender);
                if (occurrences < 2) continue;
            }
            return sender;
        }
        return null;
    }

    static bool IsDirect(string raw, string? appName)
    {
        var name = raw.Trim();
        if (name.Length == 0 || name.Length > 40 || name.Equals(appName ?? "", StringComparison.OrdinalIgnoreCase)) return false;
        var lower = name.ToLowerInvariant();
        return !markers.Any(m => name.Contains(m) || lower.Contains(m));
    }
}
