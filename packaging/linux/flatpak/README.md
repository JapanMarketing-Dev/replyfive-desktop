# Flathub への提出手順（付録CH）

前提：Flathub はソースからのビルドが原則で、ビルド中にネットワークを使えない。このリポジトリ（または公開ミラー）の **リリースタグが公開されている**必要がある。非公開のままなら Flathub は見送り、Snap Store のみ。

1. 依存の固定
   ```sh
   # flatpak-builder-tools を取る
   git clone https://github.com/flatpak/flatpak-builder-tools "$TMPDIR/fbt"
   # NuGet（.NET 10 拡張のランタイム版に合わせる）
   python3 "$TMPDIR/fbt/dotnet/flatpak-dotnet-generator.py" --dotnet 10 --freedesktop 24.08 --runtime linux-x64 --runtime linux-arm64 \
     nuget-sources.json ../../../src/ReplyFive.Desktop/ReplyFive.Desktop.csproj
   ```
2. マニフェストの `REPLACE_OWNER` / `REPLACE_COMMIT` / `tag` を公開リポジトリのリリースタグに合わせる。
3. ローカル確認（Linux 実機）
   ```sh
   flatpak install -y flathub org.freedesktop.Platform//24.08 org.freedesktop.Sdk//24.08 org.freedesktop.Sdk.Extension.dotnet10//24.08
   flatpak-builder --user --install --force-clean build-dir app.replyfive.ReplyFive.yml
   flatpak run app.replyfive.ReplyFive
   flatpak run --command=appstreamcli org.freedesktop.Sdk//24.08 validate ../app.replyfive.ReplyFive.metainfo.xml
   ```
4. 提出：`github.com/flathub/flathub` を fork → `new-pr` ブランチ（`git checkout -b app.replyfive.ReplyFive new-pr`）にこのディレクトリの `app.replyfive.ReplyFive.yml`・`nuget-sources.json`・`flathub.json`（`{"only-arches": ["x86_64", "aarch64"]}`）を置き、PR を出す。PR テンプレートの質問に「所有者の許可あり（自社アプリ）」「アプリ ID はドメイン replyfive.app に基づく」と答える。
5. マージ後：Flathub の App verification で「Website」を選び、発行されたトークンを `server/internal/site/public/.well-known/org.flathub.VerifiedApps.txt` に置いてサーバを配布する（`https://replyfive.app/.well-known/org.flathub.VerifiedApps.txt`）。
6. 更新：Flathub の `app.replyfive.ReplyFive` リポジトリでタグとコミットを上げる PR。`metainfo.xml` の `releases` も同時に足す。
