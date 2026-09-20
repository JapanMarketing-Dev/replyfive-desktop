using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReplyFive.Core;
using ReplyFive.Desktop.Services;

namespace ReplyFive.Desktop.Platform.Windows;

/// <summary>端末トークンと記録ファイルの鍵。DPAPI（この Windows アカウントだけが開ける）で包んだものを
/// %LOCALAPPDATA%\ReplyFive\secrets.dat に置く。平文の秘密値はディスクにもログにも出さない。</summary>
[SupportedOSPlatform("windows")]
internal sealed class WinSecretStore : ISecretStore
{
    /// <summary>DPAPI の追加要素。秘密ではなく、他のアプリの暗号文と取り違えないための目印。</summary>
    static readonly byte[] entropy = Encoding.UTF8.GetBytes("app.replyfive.desktop/v1");

    readonly string path;
    readonly object gate = new();
    Dictionary<string, string> cache;

    internal WinSecretStore(string path)
    {
        this.path = path;
        cache = Load();
    }

    Dictionary<string, string> Load()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [] : []; }
        catch (Exception) { return []; }
    }

    public string? Get(string name)
    {
        string? sealedValue;
        lock (gate) sealedValue = cache.TryGetValue(name, out var v) ? v : null;
        if (sealedValue is null) return null;
        try
        {
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(sealedValue), entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e)
        {
            // 別のアカウント・別の端末へ持って行かれたファイルは開けない。値の中身はログに出さない
            Diag.Log("secret unprotect failed " + e.GetType().Name);
            return null;
        }
    }

    public void Set(string name, string? value)
    {
        string? sealedValue = null;
        if (value is not null)
        {
            try { sealedValue = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), entropy, DataProtectionScope.CurrentUser)); }
            catch (Exception e) { Diag.Log("secret protect failed " + e.GetType().Name); return; }
        }
        lock (gate)
        {
            if (sealedValue is null) cache.Remove(name); else cache[name] = sealedValue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(cache));
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch (Exception e) { Diag.Log("secret store write failed " + e.GetType().Name); }
        }
    }

    public byte[] LearningKey()
    {
        var b64 = Get("learning-key");
        if (b64 is not null)
        {
            try { var k = Convert.FromBase64String(b64); if (k.Length == SealedBox.KeyBytes) return k; } catch (FormatException) { }
        }
        var key = SealedBox.NewKey();
        Set("learning-key", Convert.ToBase64String(key));
        return key;
    }
}
