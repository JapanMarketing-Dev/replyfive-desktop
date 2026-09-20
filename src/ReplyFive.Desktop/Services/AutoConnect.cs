using ReplyFive.Core;
using ReplyFive.Desktop.Platform;

namespace ReplyFive.Desktop.Services;

/// <summary>付録BF：招待コードは無い。アプリの「サインイン」がブラウザで &lt;server&gt;/admin/?connect=1 を開き、
/// サインイン（新規登録を含む）後にブラウザが replyfive://connect?server=…&amp;link=… を返す。link で端末を登録する。</summary>
public static class AutoConnect
{
    public sealed record Source(ServerAddress Server, string Link, string Origin);
    static bool connecting;

    public static Source? FromUrl(string url)
    {
        var parsed = ServerAddress.Connection(url);
        return parsed is null ? null : new Source(parsed.Value.server, parsed.Value.link, "link");
    }

    public static void OpenSignIn(AppSettings settings, IPlatform platform)
    {
        Diag.Log("sign-in opened");
        platform.OpenUrl(ServerAddress.SignInUrl(settings.Server).ToString());
    }

    /// <summary>登録して組織名を返す。失敗理由は本文を含まない。</summary>
    public static async Task<(string? organization, ApiException? error)> Connect(Source src, AppSettings settings)
    {
        if (connecting) return (null, ApiException.InvalidResponse());
        connecting = true;
        try
        {
            var client = new ApiClient(src.Server.Uri, null, ReplyFiveInfo.Version, settings.ClientInfo.Os, settings.ApiLanguage);
            var res = await client.Register(src.Link, settings.DeviceName, settings.InstallId, settings.PrimaryUserName).ConfigureAwait(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                settings.ServerURL = src.Server.Canonical;
                settings.ClearRegistration();
                settings.Apply(res);
            });
            await settings.RefreshEntitlement(force: true).ConfigureAwait(false);
            return (res.Organization.Name, null);
        }
        catch (ApiException e) { return (null, e); }
        catch (Exception e) { Diag.Log("connect exception " + e.GetType().Name + ": " + e.Message); Crash.Exception(e); return (null, ApiException.Unreachable()); }
        finally { connecting = false; }
    }
}
