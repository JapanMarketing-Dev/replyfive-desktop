using System.Security.Cryptography;
using System.Text.Json;

namespace ReplyFive.Core;

/// <summary>AES-256-GCM の 1 ファイル。形式は nonce(12) ‖ 暗号文 ‖ タグ(16)。鍵は呼び出し側（OS の資格情報ストア）が渡す。</summary>
public static class SealedBox
{
    public const int KeyBytes = 32;

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeyBytes);

    public static byte[] Seal(byte[] key, byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        var out_ = new byte[12 + cipher.Length + 16];
        Buffer.BlockCopy(nonce, 0, out_, 0, 12);
        Buffer.BlockCopy(cipher, 0, out_, 12, cipher.Length);
        Buffer.BlockCopy(tag, 0, out_, 12 + cipher.Length, 16);
        return out_;
    }

    public static byte[]? Open(byte[] key, byte[] sealed_)
    {
        if (key.Length != KeyBytes || sealed_.Length < 28) return null;
        try
        {
            var nonce = sealed_.AsSpan(0, 12);
            var tag = sealed_.AsSpan(sealed_.Length - 16, 16);
            var cipher = sealed_.AsSpan(12, sealed_.Length - 28);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return plain;
        }
        catch (CryptographicException) { return null; }
    }
}

/// <summary>暗号化 JSON ファイルの読み書き。書き込みは一時ファイルへ書いてから置き換える。所有者だけが読める権限にする。</summary>
public sealed class EncryptedJsonFile
{
    public string Path { get; }
    readonly byte[] key;
    static readonly JsonSerializerOptions json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public EncryptedJsonFile(string path, byte[] key) { Path = path; this.key = key; }

    public T? Load<T>() where T : class
    {
        try
        {
            if (!File.Exists(Path)) return null;
            var plain = SealedBox.Open(key, File.ReadAllBytes(Path));
            if (plain is null) return null;
            return JsonSerializer.Deserialize<T>(plain, json);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public void Save<T>(T value)
    {
        var dir = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(dir);
        TrySetMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var data = SealedBox.Seal(key, JsonSerializer.SerializeToUtf8Bytes(value, json));
        var tmp = Path + ".tmp";
        File.WriteAllBytes(tmp, data);
        TrySetMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, Path, overwrite: true);
    }

    public void Delete()
    {
        try { if (File.Exists(Path)) File.Delete(Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void TrySetMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, mode); } catch (Exception) { }
    }
}
