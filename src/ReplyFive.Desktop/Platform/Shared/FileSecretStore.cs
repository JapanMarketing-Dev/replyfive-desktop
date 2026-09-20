using System.Text.Json;
using ReplyFive.Core;

namespace ReplyFive.Desktop.Platform.Shared;

/// <summary>OS の資格情報ストアが使えないときの保険：所有者だけが読めるファイル（0600）。Linux で Secret Service が無い環境と、開発確認用の macOS。</summary>
public sealed class FileSecretStore : ISecretStore
{
    readonly string path;
    readonly object gate = new();
    Dictionary<string, string> cache;

    public FileSecretStore(string path)
    {
        this.path = path;
        cache = Load();
    }

    Dictionary<string, string> Load()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [] : []; }
        catch (Exception) { return []; }
    }

    public string? Get(string name) { lock (gate) return cache.TryGetValue(name, out var v) ? v : null; }

    public void Set(string name, string? value)
    {
        lock (gate)
        {
            if (value is null) cache.Remove(name); else cache[name] = value;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(cache));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path + ".tmp", UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch (Exception) { }
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
