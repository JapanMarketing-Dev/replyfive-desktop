using System.Globalization;
using System.Reflection;
using System.Text;

namespace ReplyFive.Desktop.Services;

/// <summary>文言。macOS 版の Localizable.strings（en が基準、ja）をそのまま埋め込み、OS 固有の言い回しは overrides.&lt;os&gt;.&lt;lang&gt;.strings で差し替える。</summary>
public static class L10n
{
    static Dictionary<string, string> table = [];
    static Dictionary<string, string> fallback = [];
    public static string Language { get; private set; } = "en";
    public static event Action? Changed;

    public static string SystemLanguage => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja" ? "ja" : "en";

    /// <summary>"system" | "en" | "ja"。</summary>
    public static void Apply(string setting, string os)
    {
        var lang = setting == "system" ? SystemLanguage : setting;
        if (lang != "ja") lang = "en";
        Language = lang;
        fallback = Merge("en", os);
        table = lang == "en" ? fallback : Merge(lang, os);
        Changed?.Invoke();
    }

    static Dictionary<string, string> Merge(string lang, string os)
    {
        var d = new Dictionary<string, string>(Load($"base.{lang}.strings"));
        foreach (var kv in Load($"overrides.desktop.{lang}.strings")) d[kv.Key] = kv.Value;
        foreach (var kv in Load($"overrides.{os}.{lang}.strings")) d[kv.Key] = kv.Value;
        return d;
    }

    static Dictionary<string, string> Load(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        var resource = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(name, StringComparison.Ordinal));
        if (resource is null) return [];
        using var stream = asm.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Apple の .strings 形式："key" = "value"; とコメント。</summary>
    public static Dictionary<string, string> Parse(string text)
    {
        var d = new Dictionary<string, string>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || c == ';') { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*') { var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal); i = end < 0 ? text.Length : end + 2; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { var end = text.IndexOf('\n', i); i = end < 0 ? text.Length : end + 1; continue; }
            if (c != '"') { i++; continue; }
            var key = ReadString(text, ref i);
            while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == '=')) i++;
            if (i >= text.Length || text[i] != '"') continue;
            var value = ReadString(text, ref i);
            d[key] = value;
        }
        return d;
    }

    static string ReadString(string text, ref int i)
    {
        var sb = new StringBuilder();
        i++; // opening quote
        while (i < text.Length)
        {
            var c = text[i++];
            if (c == '"') break;
            if (c == '\\' && i < text.Length)
            {
                var e = text[i++];
                sb.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '"' => '"', '\\' => '\\', _ => e });
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    public static bool Has(string key) => table.ContainsKey(key) || fallback.ContainsKey(key);

    public static string Get(string key) => table.TryGetValue(key, out var v) ? v : fallback.TryGetValue(key, out var f) ? f : key;

    /// <summary>printf 風の書式（%@ %d %.1f %% と %1$@ の位置指定）。</summary>
    public static string Format(string format, params object?[] args)
    {
        if (args.Length == 0) return format;
        var sb = new StringBuilder();
        var next = 0;
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c != '%' || i + 1 >= format.Length) { sb.Append(c); continue; }
            var j = i + 1;
            if (format[j] == '%') { sb.Append('%'); i = j; continue; }
            int? position = null;
            var digits = "";
            while (j < format.Length && char.IsDigit(format[j])) digits += format[j++];
            if (j < format.Length && format[j] == '$' && digits.Length > 0) { position = int.Parse(digits) - 1; j++; digits = ""; while (j < format.Length && char.IsDigit(format[j])) digits += format[j++]; }
            var precision = -1;
            if (j < format.Length && format[j] == '.') { j++; var p = ""; while (j < format.Length && char.IsDigit(format[j])) p += format[j++]; precision = p.Length > 0 ? int.Parse(p) : 0; }
            if (j < format.Length && format[j] is 'l' or 'h') j++;
            if (j >= format.Length) { sb.Append(format, i, format.Length - i); break; }
            var spec = format[j];
            var index = position ?? next++;
            var arg = index < args.Length ? args[index] : null;
            sb.Append(spec switch
            {
                'd' or 'i' or 'u' => arg is IFormattable n ? Convert.ToInt64(n, CultureInfo.InvariantCulture).ToString(CultureInfo.CurrentCulture) : arg?.ToString(),
                'f' => arg is IFormattable f ? Convert.ToDouble(f, CultureInfo.InvariantCulture).ToString(precision >= 0 ? "F" + precision : "F6", CultureInfo.CurrentCulture) : arg?.ToString(),
                _ => arg?.ToString(),
            });
            i = j;
        }
        return sb.ToString();
    }

    public static string L(string key, params object?[] args) => Format(Get(key), args);
}
