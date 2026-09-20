using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ReplyFive.Desktop.Services;

/// <summary>単一インスタンス。Windows は名前付きパイプ、Linux / macOS は Unix ドメインソケット。
/// 2 つ目のプロセスは接続リンク（replyfive://…）か「open」を送って終了する。</summary>
public sealed class SingleInstance : IDisposable
{
    public static event Action<string>? MessageReceived;
    readonly CancellationTokenSource cts = new();
    NamedPipeServerStream? pipe;
    Socket? socket;

    static string PipeName => "replyfive-desktop-" + Environment.UserName;
    static string SocketPath => Path.Combine(AppPaths.RuntimeDir, "replyfive.sock");

    public static SingleInstance? Acquire()
    {
        var me = new SingleInstance();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                me.pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            }
            else
            {
                Directory.CreateDirectory(AppPaths.RuntimeDir);
                if (File.Exists(SocketPath))
                {
                    // 前回の異常終了で残ったソケットか、別プロセスが生きているか
                    if (TryForward(null, probeOnly: true)) return null;
                    File.Delete(SocketPath);
                }
                me.socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                me.socket.Bind(new UnixDomainSocketEndPoint(SocketPath));
                me.socket.Listen(4);
                try { File.SetUnixFileMode(SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch (Exception) { }
            }
        }
        catch (IOException) { return null; }
        catch (SocketException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        _ = me.Loop();
        return me;
    }

    /// <summary>動いているインスタンスへメッセージを送る。届けば true。</summary>
    public static bool TryForward(string? url, bool probeOnly = false)
    {
        var message = url ?? "open";
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(400);
                if (probeOnly) return true;
                var bytes = Encoding.UTF8.GetBytes(message);
                client.Write(bytes, 0, bytes.Length);
                client.Flush();
                return true;
            }
            if (!File.Exists(SocketPath)) return false;
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Connect(new UnixDomainSocketEndPoint(SocketPath));
            if (probeOnly) return true;
            s.Send(Encoding.UTF8.GetBytes(message));
            s.Shutdown(SocketShutdown.Send);
            return true;
        }
        catch (Exception) { return false; }
    }

    async Task Loop()
    {
        var buffer = new byte[8192];
        while (!cts.IsCancellationRequested)
        {
            try
            {
                string text;
                if (pipe is not null)
                {
                    await pipe.WaitForConnectionAsync(cts.Token).ConfigureAwait(false);
                    var n = await pipe.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
                    text = Encoding.UTF8.GetString(buffer, 0, n);
                    pipe.Disconnect();
                }
                else if (socket is not null)
                {
                    using var client = await socket.AcceptAsync(cts.Token).ConfigureAwait(false);
                    var n = await client.ReceiveAsync(buffer, SocketFlags.None, cts.Token).ConfigureAwait(false);
                    text = Encoding.UTF8.GetString(buffer, 0, n);
                }
                else return;
                if (text.Length > 0) MessageReceived?.Invoke(text);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { await Task.Delay(200).ConfigureAwait(false); }
        }
    }

    public void Dispose()
    {
        cts.Cancel();
        pipe?.Dispose();
        socket?.Dispose();
        if (!OperatingSystem.IsWindows()) { try { File.Delete(SocketPath); } catch (Exception) { } }
    }
}
