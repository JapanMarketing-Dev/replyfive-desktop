using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ReplyFive.Core;

/// <summary>サーバとの通信。本文をログに出さない。タイムアウトは契約 client_timeout_ms（10 秒）。リダイレクト先へ本文やトークンを送らない。</summary>
public sealed class ApiClient
{
    public Uri BaseUrl { get; set; }
    public string? DeviceToken { get; set; }
    public string ClientVersion { get; }
    public string AcceptLanguage { get; set; }
    /// <summary>X-ReplyFive-Client の OS 名（windows / linux / macos）。</summary>
    public string OsName { get; }
    readonly HttpClient http;

    static readonly HttpClient shared = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(10),
    })
    { Timeout = TimeSpan.FromSeconds(10) };

    public ApiClient(Uri baseUrl, string? deviceToken, string clientVersion, string osName, string acceptLanguage = "en", HttpClient? client = null)
    {
        BaseUrl = baseUrl; DeviceToken = deviceToken; ClientVersion = clientVersion; OsName = osName; AcceptLanguage = acceptLanguage;
        http = client ?? shared;
    }

    HttpRequestMessage Build(HttpMethod method, string path, object? body, bool auth)
    {
        var server = ServerAddress.Normalized(BaseUrl.ToString()) ?? throw ApiException.InvalidResponse();
        var req = new HttpRequestMessage(method, new Uri(server.Canonical + "/" + path));
        req.Headers.TryAddWithoutValidation("x-replyfive-client", OsName + "/" + ClientVersion);
        req.Headers.TryAddWithoutValidation("accept-language", AcceptLanguage);
        if (auth)
        {
            var tok = DeviceToken ?? throw ApiException.NotRegistered();
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tok);
        }
        if (body is not null)
        {
            req.Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), ContractJson.Options), Encoding.UTF8, "application/json");
        }
        return req;
    }

    async Task<T> Send<T>(HttpRequestMessage req, CancellationToken ct)
    {
        HttpResponseMessage res;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try { res = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested) { throw ApiException.Timeout(); }
        }
        catch (ApiException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException) { throw ApiException.Unreachable(); }
        catch (Exception) { throw ApiException.Unreachable(); }
        using (res)
        {
            var data = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var status = (int)res.StatusCode;
            if (status is >= 200 and < 300)
            {
                try { return JsonSerializer.Deserialize<T>(data, ContractJson.Options) ?? throw ApiException.InvalidResponse(); }
                catch (JsonException) { throw ApiException.InvalidResponse(); }
            }
            ApiErrorBody? body = null;
            try { body = JsonSerializer.Deserialize<ApiErrorBody>(data, ContractJson.Options); } catch (JsonException) { }
            throw ApiException.Server(string.IsNullOrEmpty(body?.Error) ? "http_" + status : body!.Error, status, body?.Message);
        }
    }

    sealed class OkBody { public bool ok { get; set; } }
    sealed class AcceptedBody { public int accepted { get; set; } }

    public Task<MetaResponse> Meta(CancellationToken ct = default) => Send<MetaResponse>(Build(HttpMethod.Get, "v1/meta", null, false), ct);

    public Task<RegisterResponse> Register(string linkToken, string deviceName, string? installId = null, string? userName = null, CancellationToken ct = default)
        => Send<RegisterResponse>(Build(HttpMethod.Post, "v1/devices/register", new RegisterRequest(linkToken, deviceName, OsName, ClientVersion, installId, userName), false), ct);

    public Task<DeviceSelfResponse> DeviceSelf(CancellationToken ct = default) => Send<DeviceSelfResponse>(Build(HttpMethod.Get, "v1/devices/self", null, true), ct);

    /// <summary>付録CD：端末名・利用者名の更新。失敗は呼び出し側が無視してよい（次回また送る）。</summary>
    public Task UpdateSelfDevice(DeviceProfileRequest body, CancellationToken ct = default) => Send<OkBody>(Build(HttpMethod.Put, "v1/devices/self", body, true), ct);

    /// <summary>付録CD：初回設定の返信サンプルから文体ラベルを判定する。本文は判定にだけ使われ、サーバに残らない。</summary>
    public Task<StyleProfileResponse> StyleProfile(StyleProfileRequest body, CancellationToken ct = default) => Send<StyleProfileResponse>(Build(HttpMethod.Post, "v1/style/profile", body, true), ct);

    public Task<FormatResponse> Format(FormatRequest body, CancellationToken ct = default) => Send<FormatResponse>(Build(HttpMethod.Post, "v1/format", body, true), ct);

    /// <summary>付録BV：新しく読んだ発言の「発言か」判定。失敗時は呼び出し側が規則だけで保存する。</summary>
    public Task<ContextFilterResponse> FilterContext(ContextFilterRequest body, CancellationToken ct = default) => Send<ContextFilterResponse>(Build(HttpMethod.Post, "v1/context/filter", body, true), ct);

    /// <summary>付録BC：修正率の計測を送る。本文は含まない。呼び出し側は失敗を無視してよい。</summary>
    public Task Feedback(FeedbackRequest body, CancellationToken ct = default) => Send<OkBody>(Build(HttpMethod.Post, "v1/feedback", body, true), ct);

    /// <summary>成長計測。端末トークンがあれば付け、無ければ install_id だけで送る。本文は含まない。</summary>
    public async Task<int> Events(EventsRequest body, CancellationToken ct = default)
        => (await Send<AcceptedBody>(Build(HttpMethod.Post, "v1/events", body, DeviceToken is not null), ct).ConfigureAwait(false)).accepted;

    public Task RevokeSelf(CancellationToken ct = default) => Send<OkBody>(Build(HttpMethod.Delete, "v1/devices/self", null, true), ct);
}
