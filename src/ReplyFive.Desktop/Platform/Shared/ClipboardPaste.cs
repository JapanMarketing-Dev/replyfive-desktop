namespace ReplyFive.Desktop.Platform.Shared;

/// <summary>差し込みの共通手順（付録BF）。直接書けない入力欄には、クリップボードへ置いて貼り付けキーを送り、元の内容を戻す。</summary>
public static class ClipboardPaste
{
    /// <summary>saved を戻すのは、貼り付けの間に利用者がクリップボードを変えていないときだけ。</summary>
    public static async Task<bool> Run(IPlatform platform, string text, Func<Task<bool>> sendPasteKey, Func<string?>? readTarget, int settleMs = 350)
    {
        var before = readTarget?.Invoke();
        var saved = platform.ReadClipboard();
        platform.CopyToClipboard(text);
        var sent = await sendPasteKey().ConfigureAwait(false);
        if (!sent) return false;
        await Task.Delay(settleMs).ConfigureAwait(false);
        var after = readTarget?.Invoke();
        // 値が読める欄で変化が無ければ失敗。読めない欄（Electron の contenteditable 等）は貼り付けキーが届いたとみなす
        var inserted = before is not null && after is not null ? after != before && after.Contains(text, StringComparison.Ordinal) : true;
        if (inserted && platform.ReadClipboard() == text)
        {
            if (saved is not null) platform.CopyToClipboard(saved);
        }
        return inserted;
    }
}
