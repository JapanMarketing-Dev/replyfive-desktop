using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using ReplyFive.Core;
using ReplyFive.Desktop.Platform;

namespace ReplyFive.Desktop.Services;

/// <summary>秘密でない設定（settings.json）。端末トークンと記録の鍵は OS の資格情報ストア（ISecretStore）。本文は持たない。</summary>
public sealed partial class AppSettings : ObservableObject
{
    readonly IPlatform platform;
    readonly JsonObject d;
    readonly object gate = new();
    bool loaded;

    public AppSettings(IPlatform platform)
    {
        this.platform = platform;
        d = LoadFile();
        serverURL = Str("serverURL") ?? ReplyFiveInfo.DefaultServerUrl.ToString().TrimEnd('/');
        deviceName = Str("deviceName") ?? platform.DefaultDeviceName;
        userName = Str("userName") ?? "";
        organizationName = Str("organizationName");
        learningEnabled = Bool("learningEnabled") ?? true;   // 付録BG：インストール時からオン
        conversationEnabled = Bool("conversationEnabled") ?? true;
        defaultTone = ContractJson.ParseTone(Str("defaultTone")) ?? Tone.Natural;
        defaultRecipient = ContractJson.ParseRecipient(Str("defaultRecipient")) ?? RecipientType.External;
        outputLanguage = Str("outputLanguage") ?? "auto";
        hotKey = HotKeyCombo.Parse(Str("hotKey"))?.Stored ?? HotKeyCombo.Default.Stored;
        uiLanguage = Str("uiLanguage") ?? "system";
        reviewBeforeInsert = Bool("reviewBeforeInsert") ?? false;
        onboardingDone = Bool("onboardingDone") ?? false;
        styleOnboardingDone = Bool("styleOnboardingDone") ?? false;
        styleProfile = Obj<StyleProfile>("styleProfile");
        autoUpdate = Bool("autoUpdate") ?? true;
        PreviousRunVersion = Str("lastRunVersion");
        d["lastRunVersion"] = ReplyFiveInfo.Version;
        entitlement = Obj<EntitlementView>("entitlement");
        policy = Obj<PolicyView>("policy");
        latestClientVersion = Str("latestClientVersion");
        downloadURL = Str("downloadURL");
        lastEntitlementRefresh = Date("lastEntitlementRefresh");
        lastClientUpdateCheck = Date("lastClientUpdateCheck");
        isRegistered = platform.Secrets.Get("device-token") is not null;
        if (isRegistered && Str("registeredServerURL") is null) d["registeredServerURL"] = ServerAddress.Normalized(serverURL)?.Canonical;
        loaded = true;
        Save();
    }

    // MARK: - 永続化

    static JsonObject LoadFile()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile)) return JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile)) as JsonObject ?? [];
        }
        catch (Exception) { }
        return [];
    }

    string? Str(string key) => d[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    bool? Bool(string key) => d[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    DateTimeOffset? Date(string key) => d[key] is JsonValue v && v.TryGetValue<string>(out var s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ? dt : null;
    T? Obj<T>(string key) where T : class
    {
        try { return d[key] is JsonNode n ? n.Deserialize<T>(ContractJson.Options) : null; } catch (JsonException) { return null; }
    }
    void Put(string key, object? value)
    {
        lock (gate)
        {
            if (value is null) d.Remove(key);
            else d[key] = value switch
            {
                string s => JsonValue.Create(s),
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                DateTimeOffset dt => JsonValue.Create(dt.ToString("o", CultureInfo.InvariantCulture)),
                _ => JsonSerializer.SerializeToNode(value, value.GetType(), ContractJson.Options),
            };
        }
        Save();
    }
    void Save()
    {
        if (!loaded) return;
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.ConfigDir);
                File.WriteAllText(AppPaths.SettingsFile + ".tmp", d.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(AppPaths.SettingsFile + ".tmp", AppPaths.SettingsFile, overwrite: true);
            }
            catch (Exception) { }
        }
    }

    // MARK: - 設定値

    [ObservableProperty] string serverURL;
    partial void OnServerURLChanged(string value) => Put("serverURL", value);
    [ObservableProperty] string deviceName;
    partial void OnDeviceNameChanged(string value) => Put("deviceName", value);
    /// <summary>返信の宛名判定に使う自分の呼び名。利用者が確認・編集した値だけを送る。</summary>
    [ObservableProperty] string userName;
    partial void OnUserNameChanged(string value) => Put("userName", value);
    [ObservableProperty] string? organizationName;
    partial void OnOrganizationNameChanged(string? value) => Put("organizationName", value);
    [ObservableProperty] bool learningEnabled;
    partial void OnLearningEnabledChanged(bool value) => Put("learningEnabled", value);
    /// <summary>付録BR：会話のバックグラウンド収集（端末内だけ）。既定オン。</summary>
    [ObservableProperty] bool conversationEnabled;
    partial void OnConversationEnabledChanged(bool value) => Put("conversationEnabled", value);
    [ObservableProperty] Tone defaultTone;
    partial void OnDefaultToneChanged(Tone value) => Put("defaultTone", value.Wire());
    [ObservableProperty] RecipientType defaultRecipient;
    partial void OnDefaultRecipientChanged(RecipientType value) => Put("defaultRecipient", value.Wire());
    [ObservableProperty] string outputLanguage;
    partial void OnOutputLanguageChanged(string value) => Put("outputLanguage", value);
    [ObservableProperty] string hotKey;
    partial void OnHotKeyChanged(string value) => Put("hotKey", value);
    public HotKeyCombo HotKeyCombo
    {
        get => HotKeyCombo.Parse(HotKey) ?? HotKeyCombo.Default;
        set { if (value.Stored != HotKey) HotKey = value.Stored; }
    }
    /// <summary>設定画面でキーを記録中（この間グローバルショートカットを外す）。</summary>
    [ObservableProperty] bool isRecordingHotKey;
    /// <summary>現在のショートカットを OS に登録できたか。</summary>
    [ObservableProperty] bool hotKeyRegistered = true;
    [ObservableProperty] string uiLanguage;
    partial void OnUiLanguageChanged(string value) { Put("uiLanguage", value); L10n.Apply(value, platform.OsName); }
    [ObservableProperty] bool reviewBeforeInsert;
    partial void OnReviewBeforeInsertChanged(bool value) => Put("reviewBeforeInsert", value);
    [ObservableProperty] bool onboardingDone;
    partial void OnOnboardingDoneChanged(bool value) => Put("onboardingDone", value);
    /// <summary>付録CD：「返し方」の収集と最適化まで終えたか。</summary>
    [ObservableProperty] bool styleOnboardingDone;
    partial void OnStyleOnboardingDoneChanged(bool value) => Put("styleOnboardingDone", value);
    [ObservableProperty] StyleProfile? styleProfile;
    partial void OnStyleProfileChanged(StyleProfile? value) => Put("styleProfile", value);
    [ObservableProperty] bool autoUpdate;
    partial void OnAutoUpdateChanged(bool value) => Put("autoUpdate", value);
    public string? PreviousRunVersion { get; }
    [ObservableProperty] bool isRegistered;
    [ObservableProperty] EntitlementView? entitlement;
    [ObservableProperty] PolicyView? policy;
    [ObservableProperty] string? latestClientVersion;
    [ObservableProperty] string? downloadURL;
    [ObservableProperty] DateTimeOffset? lastEntitlementRefresh;
    DateTimeOffset? lastClientUpdateCheck;

    // MARK: - 利用回数（FR-09）。本文は持たない。日別の回数だけ（直近 62 日）

    public void RecordUsage()
    {
        var m = Usage();
        var day = DayKey(DateTimeOffset.Now);
        m[day] = m.GetValueOrDefault(day) + 1;
        var keep = Enumerable.Range(0, 62).Select(i => DayKey(DateTimeOffset.Now.AddDays(-i))).ToHashSet();
        Put("usageByDay", m.Where(kv => keep.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));
        OnPropertyChanged(nameof(UsageChanged));
    }
    public int UsageChanged => 0;
    public int Usage(int days)
    {
        var keep = Enumerable.Range(0, days).Select(i => DayKey(DateTimeOffset.Now.AddDays(-i))).ToHashSet();
        return Usage().Where(kv => keep.Contains(kv.Key)).Sum(kv => kv.Value);
    }
    Dictionary<string, int> Usage() { try { return d["usageByDay"]?.Deserialize<Dictionary<string, int>>() ?? []; } catch (JsonException) { return []; } }
    static string DayKey(DateTimeOffset t) => t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // MARK: - 派生

    public FormatRequest.SenderInfo? SenderForRequest
    {
        get
        {
            var n = UserName.Trim();
            var style = StyleProfile?.Normalized;
            var styleOrNull = style is null || style.IsEmpty ? null : style;
            if (n.Length == 0) return styleOrNull is null ? null : new FormatRequest.SenderInfo("", styleOrNull);
            // 表記ゆれを登録していても、生成側へ渡す自分の表示名は先頭の正本だけ
            var primary = ContactDetector.OwnAliases(n).FirstOrDefault() ?? n;
            return new FormatRequest.SenderInfo(ContractJson.Prefix(primary, 100), styleOrNull);
        }
    }

    /// <summary>管理画面の「利用者」に出す名前（付録CD）。</summary>
    public string PrimaryUserName
    {
        get
        {
            var n = UserName.Trim();
            if (n.Length == 0) return "";
            return ContractJson.Prefix(ContactDetector.OwnAliases(n).FirstOrDefault() ?? n, 100);
        }
    }

    public string ApiLanguage => UiLanguage == "system" ? L10n.SystemLanguage : UiLanguage;

    public ServerAddress Server => ServerAddress.Normalized(ServerURL) ?? ServerAddress.Normalized(ReplyFiveInfo.DefaultServerUrl.ToString())!;

    public FormatRequest.ClientInfo ClientInfo => new(platform.OsName, ReplyFiveInfo.Version);

    public ApiClient MakeClient()
    {
        var bound = Str("registeredServerURL");
        var matches = bound is not null && ServerAddress.Same(bound, ServerURL);
        return new ApiClient(Server.Uri, matches ? platform.Secrets.Get("device-token") : null, ReplyFiveInfo.Version, platform.OsName, ApiLanguage);
    }

    public void SetRegistered(string token, string organization)
    {
        platform.Secrets.Set("device-token", token);
        Put("registeredServerURL", Server.Canonical);
        OrganizationName = organization;
        IsRegistered = true;
    }

    public void Apply(RegisterResponse response)
    {
        SetRegistered(response.DeviceToken, response.Organization.Name);
        if (response.Entitlement is not null) { Entitlement = response.Entitlement; Put("entitlement", response.Entitlement); }
        if (response.Policy is not null) { Policy = response.Policy; Put("policy", response.Policy); }
    }

    public void ClearRegistration()
    {
        platform.Secrets.Set("device-token", null);
        Put("pushedDeviceProfile", null);
        OrganizationName = null;
        IsRegistered = false;
        Entitlement = null; Policy = null; LastEntitlementRefresh = null;
        Put("entitlement", null); Put("policy", null); Put("lastEntitlementRefresh", null); Put("registeredServerURL", null);
    }

    public bool EffectiveConversationEnabled => ConversationEnabled && (Policy?.ContextCaptureAllowed ?? true);
    public bool EffectiveLearningEnabled => LearningEnabled && (Policy?.LearningAllowed ?? true);

    public bool UpdateAvailable => LatestClientVersion is not null && ReplyFiveInfo.IsNewerVersion(LatestClientVersion, ReplyFiveInfo.Version);

    /// <summary>付録CD：端末名と利用者名をサーバへ送る（PUT /v1/devices/self）。同じ値を送り直さない。失敗は次回に持ち越す。</summary>
    public async Task PushDeviceProfile(bool force = false)
    {
        if (!IsRegistered) return;
        var user = PrimaryUserName;
        var device = DeviceName.Trim();
        var signature = user + "\u001F" + device;
        if (!force && Str("pushedDeviceProfile") == signature) return;
        if (user.Length == 0 && device.Length == 0) return;
        try
        {
            await MakeClient().UpdateSelfDevice(new DeviceProfileRequest(device, user)).ConfigureAwait(false);
            Put("pushedDeviceProfile", signature);
        }
        catch (ApiException e) { if (e.RequiresReregistration) await Ui(ClearRegistration); }
        catch (Exception) { }
    }

    public async Task RefreshEntitlement(bool force = false)
    {
        if (!IsRegistered) return;
        var now = DateTimeOffset.UtcNow;
        if (!force && LastEntitlementRefresh is { } last && (now - last).TotalSeconds < 600) return;
        try
        {
            var response = await MakeClient().DeviceSelf().ConfigureAwait(false);
            var updateClient = force || lastClientUpdateCheck is null || (now - lastClientUpdateCheck.Value).TotalSeconds >= 21_600;
            await Ui(() => Apply(response, now, updateClient));
        }
        catch (ApiException e)
        {
            if (e.RequiresReregistration) await Ui(ClearRegistration);
            else
            {
                if (e.Status == 404 || e.Kind == ApiErrorKind.InvalidResponse) await RefreshClientMetadata();
                await Ui(() => { LastEntitlementRefresh = now; Put("lastEntitlementRefresh", now); });
            }
        }
        catch (Exception) { await Ui(() => { LastEntitlementRefresh = now; Put("lastEntitlementRefresh", now); }); }
    }

    public async Task RefreshClientMetadata(bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && lastClientUpdateCheck is { } last && (now - last).TotalSeconds < 21_600) return;
        MetaResponse meta;
        try { meta = await MakeClient().Meta().ConfigureAwait(false); } catch (Exception) { return; }
        var client = meta.LatestClient?.For(platform.OsName);
        if (client is null) return;
        await Ui(() =>
        {
            DownloadURL = client.DownloadUrl;
            LatestClientVersion = client.LatestVersion;
            Put("latestClientVersion", client.LatestVersion);
            Put("downloadURL", client.DownloadUrl);
            lastClientUpdateCheck = now;
            Put("lastClientUpdateCheck", now);
        });
    }

    void Apply(DeviceSelfResponse response, DateTimeOffset date, bool updateClient)
    {
        Entitlement = response.Entitlement;
        Policy = response.Policy;
        OrganizationName = response.Organization.Name;
        LastEntitlementRefresh = date;
        Put("entitlement", response.Entitlement);
        Put("policy", response.Policy);
        Put("lastEntitlementRefresh", date);
        if (updateClient)
        {
            DownloadURL = response.Client.DownloadUrl;
            Put("downloadURL", response.Client.DownloadUrl);
            LatestClientVersion = response.Client.LatestVersion;
            Put("latestClientVersion", response.Client.LatestVersion);
            lastClientUpdateCheck = date;
            Put("lastClientUpdateCheck", date);
        }
    }

    static Task Ui(Action a) => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(a).GetTask();

    // MARK: - 端末内の記録（暗号化。鍵は OS の資格情報ストア）

    byte[]? key;
    byte[] Key => key ??= platform.Secrets.LearningKey();
    LearningStore? records; ConversationStore? conversations; StyleStore? styleSamples; BackgroundEvaluationStore? evaluations;
    public LearningStore Records => records ??= new LearningStore(AppPaths.LearningFile, Key);
    public ConversationStore Conversations => conversations ??= new ConversationStore(AppPaths.ConversationsFile, Key);
    public StyleStore StyleSamples => styleSamples ??= new StyleStore(AppPaths.StyleFile, Key);
    public BackgroundEvaluationStore BackgroundEvaluations => evaluations ??= new BackgroundEvaluationStore(AppPaths.EvaluationsFile, Key);

    public string InstallId
    {
        get
        {
            var id = Str("growthInstallID");
            if (id is not null) return id;
            id = Guid.NewGuid().ToString();
            Put("growthInstallID", id);
            Put("growthInstallDate", DateTimeOffset.UtcNow);
            return id;
        }
    }
    public int DaysSinceInstall { get { _ = InstallId; var t = Date("growthInstallDate"); return t is null ? 0 : Math.Max(0, (int)(DateTimeOffset.UtcNow - t.Value).TotalDays); } }
    public int LaunchCount { get => d["growthLaunchCount"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0; set => Put("growthLaunchCount", value); }
    public string? DailyActiveDay { get => Str("growthDailyActiveDay"); set => Put("growthDailyActiveDay", value); }
}
