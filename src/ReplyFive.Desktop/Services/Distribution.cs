namespace ReplyFive.Desktop.Services;

/// <summary>配布形態（付録CH）。ストア版（Microsoft Store の MSIX、Snap、Flatpak）は更新をストアが配るので、アプリ内アップデートの確認・取得・案内をすべて切る。
/// 判定は環境変数 REPLYFIVE_DISTRIBUTION=store（snap / flatpak の定義で設定）、Flatpak / Snap のサンドボックス変数、Windows はパッケージ ID の有無。macOS 版の Distribution.swift と同じ考え方。</summary>
public static class Distribution
{
    public static bool IsStore { get; } = Detect();

    public static string Name => IsStore ? "store" : "direct";

    static bool Detect()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("REPLYFIVE_DISTRIBUTION"), "store", StringComparison.OrdinalIgnoreCase)) return true;
        if (Environment.GetEnvironmentVariable("FLATPAK_ID") is { Length: > 0 }) return true;
        if (Environment.GetEnvironmentVariable("SNAP") is { Length: > 0 } && Environment.GetEnvironmentVariable("SNAP_NAME") is { Length: > 0 }) return true;
#if WINDOWS
        try { if (Platform.Windows.Native.IsPackaged()) return true; } catch (Exception) { }
#endif
        return false;
    }
}
