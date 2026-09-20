# ストア配布のパッケージ定義（Windows / Linux）

`clients/desktop`（C# / .NET 10 + Avalonia 12）を Microsoft Store・Snap Store・Flathub へ出すための定義とスクリプト。要件定義 付録CH が正本。掲載文（ストアの説明・スクリーンショット要件）は `docs/store-listing/` にある。

| 先 | 形式 | 定義 | 出すもの | アカウントの状態（2026-09-20） |
|---|---|---|---|---|
| Microsoft Store | MSIX（x64 + arm64 の bundle） | `windows/msix/` | `.msixupload` を Partner Center へ | 法人アカウント登録中。メール・法人は検証済み、**在籍確認（ドメイン登録の領収書 PDF）が未提出** |
| Snap Store | snap（strict、core24） | `linux/snap/snapcraft.yaml` | `snapcraft upload --release=stable` | 開発者契約に同意済み。snap 名 `replyfive` を予約済み（手動審査、最長 30 日） |
| Flathub | Flatpak（`app.replyfive.ReplyFive`） | `linux/flatpak/` | `flathub/flathub` へ PR | 未着手（GitHub アカウントで PR。公開後にサイトの `.well-known` で検証） |

共通：

- アプリ ID は `app.replyfive.ReplyFive`（ドメイン replyfive.app の逆順。Flathub の検証条件を満たす）。Windows の Package Identity は Partner Center がアプリ名予約時に発行するので `Package.appxmanifest` の `Identity` はその値に差し替える。
- 実行ファイルは `ReplyFive`（`AssemblyName`）だけ。共通コアは C#（`src/ReplyFive.Core`）に移植済みで、Go の `replyfive-cli` は同梱しない（付録CG）。
- 自動更新（`Services/Updater.cs`）はストア版では無効にする（ストアが更新を配る）。判定は環境変数 `REPLYFIVE_DISTRIBUTION=store`（snap / flatpak の定義で設定）と、MSIX は `Package.Current` の有無。macOS の `Distribution.swift` と同じ考え方。
- サイトの「Windows 版・Linux 版は準備中」表記は、最初のストア公開と同時に外す（付録CE の是正 1）。

## 先に組む（全ストア共通）

```sh
# ~/.cache/dotnet/env.sh を source してから（この Mac の .NET 10 SDK。dotnet はサンドボックス外で実行）
# .NET（self-contained、単一ファイルにはしない。MSIX / snap / flatpak はディレクトリをそのまま包む）
cd clients/desktop
dotnet publish src/ReplyFive.Desktop -c Release -f net10.0-windows10.0.19041.0 -r win-x64   --self-contained -p:SentryDsn="$SENTRY_DSN_DESKTOP" -o packaging/out/win-x64
dotnet publish src/ReplyFive.Desktop -c Release -f net10.0-windows10.0.19041.0 -r win-arm64 --self-contained -p:SentryDsn="$SENTRY_DSN_DESKTOP" -o packaging/out/win-arm64
dotnet publish src/ReplyFive.Desktop -c Release -f net10.0 -r linux-x64   --self-contained -p:SentryDsn="$SENTRY_DSN_DESKTOP" -o packaging/out/linux-x64
dotnet publish src/ReplyFive.Desktop -c Release -f net10.0 -r linux-arm64 --self-contained -p:SentryDsn="$SENTRY_DSN_DESKTOP" -o packaging/out/linux-arm64
```

`packaging/out/` は Git 除外。バージョンは `ReplyFive.Desktop.csproj` の `<Version>` と `Package.appxmanifest` / `snapcraft.yaml` / `metainfo.xml` の `releases` を同時に上げる（`scripts/bump-version.sh <ver>`）。

## Windows（Microsoft Store）

1. Partner Center → Apps and games → 新しい製品 → 名前 **ReplyFive** を予約。発行された `Package/Identity/Name`・`Publisher`・`PublisherDisplayName` を `windows/msix/Package.appxmanifest` に写す（`REPLACE_*`）。
2. Windows 実機（Windows SDK 入り）で `windows/msix/build-msix.ps1 -Version 0.8.0.0` → `out/ReplyFive_0.8.0.0.msixupload`（x64 + arm64 の bundle）。ストアが署名するので配布用の証明書は不要。手元で動かすときだけ `-SelfSign` で自己署名する。
3. Partner Center の提出：パッケージに `.msixupload`、ストア掲載は `docs/store-listing/microsoft-store.md`、プライバシー URL `https://replyfive.app/legal/privacy`、年齢区分は IARC の質問票（暴力・課金なし。通信あり）。
4. 制限付き機能：`runFullTrust` は MSIX の通常アプリ（Desktop Bridge）で認められる。UI Automation で他アプリを読むこと、`Ctrl+V` 送出、`RegisterHotKey`、起動時の自動開始（`windows.startupTask`。利用者が設定から切れる）はすべて full trust の範囲。**アクセシビリティ目的で他アプリを読むことは審査で理由を求められる**ので提出ノートに用途（返信欄の祖先＝会話ペインだけを読む、送信はしない、本文を保存しない）を書く。
5. `replyfive://` は `windows.protocol` で宣言。管理画面の connect フローがそのまま使える。

## Linux（Snap Store）

1. Linux 実機または LXD/Multipass で `linux/snap/build-snap.sh` → `replyfive_<ver>_amd64.snap`（`snapcraft` は `packaging/out/linux-x64` を `dump` するだけ。ビルド環境に .NET は不要）。arm64 も同じ手順で `linux-arm64`。
2. `snap install --dangerous ./replyfive_*.snap` で KDE / GNOME の X11・Wayland で確認（`snap connect replyfive:password-manager-service` 等の手動接続は自動接続の申請が通るまで必要）。
3. `snapcraft login` → `snapcraft upload --release=edge replyfive_*.snap` → 動作確認後 `snapcraft release replyfive <rev> stable`。
4. 自動接続の申請（forum.snapcraft.io の store-requests）：`password-manager-service`（Secret Service）、`desktop-legacy`（AT-SPI バス）。strict で AT-SPI の読み取り・差し込みが通らなければ classic を申請する（理由：他アプリの入力欄をアクセシビリティ API で読み書きする）。
5. ストア掲載（snapcraft.io/replyfive/listing）：`docs/store-listing/snap-store.md`。アイコン 256×256、スクリーンショット 3 枚以上。

## Linux（Flathub）

1. `flatpak-dotnet-generator.py`（flatpak-builder-tools）で NuGet の `nuget-sources.json` を作る（Flathub はビルド中のネットワークを許さない）。`linux/flatpak/README.md` の手順。
2. `flatpak-builder --user --install --force-clean build-dir linux/flatpak/app.replyfive.ReplyFive.yml` で確認。
3. `flathub/flathub` を fork → `new-pr` ブランチにマニフェスト・metainfo・desktop・アイコンを置いて PR（テンプレートの質問に答える。ソースは GitHub のリリースタグを `git` ソースで参照するので、**このリポジトリを公開するか、公開用のミラーが要る**）。
4. マージ後、Flathub が発行する検証トークンを `https://replyfive.app/.well-known/org.flathub.VerifiedApps.txt` に置く（`server/internal/site/public/.well-known/`）。

## 未決

- Microsoft の在籍確認書類（ドメイン登録の領収書）は遠藤さんの手元。
- Flathub はソース公開が前提。非公開のままなら Flathub を見送り Snap のみ、または extra-data で配布物を取る方式（Flathub は原則不可）。
- Windows / Linux クライアント本体は開発中（`docs/desktop-tech-stack.md`）。ここにある定義はクライアントの完成後にそのまま使う。
