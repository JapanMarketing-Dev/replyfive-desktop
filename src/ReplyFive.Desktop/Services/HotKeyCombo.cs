using Avalonia.Input;

namespace ReplyFive.Desktop.Services;

/// <summary>利用者が自由に割り当てるショートカット（キー＋修飾キー）。保存形式は "combo:&lt;Key 名&gt;:&lt;修飾ビット&gt;"。旧 Go シェルの既定 Ctrl+Shift+R と同じ。</summary>
public readonly record struct HotKeyCombo(Key Key, KeyModifiers Modifiers)
{
    public static readonly HotKeyCombo Default = new(Key.R, KeyModifiers.Control | KeyModifiers.Shift);

    public string Stored => $"combo:{Key}:{(int)Modifiers}";

    public static HotKeyCombo? Parse(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        var parts = stored.Split(':');
        if (parts.Length == 3 && parts[0] == "combo" && Enum.TryParse<Key>(parts[1], out var key) && int.TryParse(parts[2], out var mods))
            return new HotKeyCombo(key, (KeyModifiers)mods);
        return stored switch
        {
            "ctrl_shift_r" => Default,
            "ctrl_alt_r" => new HotKeyCombo(Key.R, KeyModifiers.Control | KeyModifiers.Alt),
            "ctrl_shift_space" => new HotKeyCombo(Key.Space, KeyModifiers.Control | KeyModifiers.Shift),
            "ctrl_alt_space" => new HotKeyCombo(Key.Space, KeyModifiers.Control | KeyModifiers.Alt),
            _ => null,
        };
    }

    static readonly HashSet<Key> modifierKeys = [Key.LeftCtrl, Key.RightCtrl, Key.LeftShift, Key.RightShift, Key.LeftAlt, Key.RightAlt, Key.LWin, Key.RWin, Key.None];
    public static bool IsFunctionKey(Key k) => k is >= Key.F1 and <= Key.F24;

    /// <summary>Ctrl・Alt・Win のいずれかを含むか（Shift 単独は誤動作するので不可。ファンクションキーは単独可）。</summary>
    public static bool HasStrongModifier(KeyModifiers m) => (m & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0;

    /// <summary>キー入力から作る。修飾キー単独・Esc・修飾なし（ファンクションキー以外）は null。</summary>
    public static HotKeyCombo? FromKeyEvent(Key key, KeyModifiers modifiers)
    {
        if (modifierKeys.Contains(key) || key == Key.Escape) return null;
        var mods = modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta);
        if (!HasStrongModifier(mods) && !IsFunctionKey(key)) return null;
        return new HotKeyCombo(key, mods);
    }

    public bool RequiresModifierHint => !HasStrongModifier(Modifiers) && !IsFunctionKey(Key);

    /// <summary>Ctrl+Alt+Shift+Win の順＋キー名（Windows / Linux の慣習）。</summary>
    public string Display
    {
        get
        {
            var parts = new List<string>();
            if (Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(KeyModifiers.Meta)) parts.Add(OperatingSystem.IsWindows() ? "Win" : "Super");
            parts.Add(KeyName(Key));
            return string.Join("+", parts);
        }
    }

    public static string KeyName(Key k)
    {
        if (k >= Key.A && k <= Key.Z) return k.ToString();
        if (k >= Key.D0 && k <= Key.D9) return ((int)k - (int)Key.D0).ToString();
        if (k >= Key.NumPad0 && k <= Key.NumPad9) return "Num" + ((int)k - (int)Key.NumPad0);
        return k switch
        {
            Key.Space => "Space", Key.Return => "Enter", Key.Tab => "Tab", Key.Back => "Backspace", Key.Delete => "Delete",
            Key.Left => "←", Key.Right => "→", Key.Up => "↑", Key.Down => "↓", Key.Home => "Home", Key.End => "End",
            Key.PageUp => "Page Up", Key.PageDown => "Page Down", Key.Insert => "Insert",
            Key.OemComma => ",", Key.OemPeriod => ".", Key.OemMinus => "-", Key.OemPlus => "=", Key.OemQuestion => "/", Key.OemSemicolon => ";",
            Key.OemQuotes => "'", Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]", Key.OemPipe => "\\", Key.OemTilde => "`",
            _ => k.ToString(),
        };
    }
}
