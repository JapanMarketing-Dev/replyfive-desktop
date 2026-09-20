# 実機検証用の VM（UTM）

`clients/desktop`（C# / .NET 10 + Avalonia 12）の OS 固有部分は、コンテナやコンパイルでは確かめられない。この Mac の UTM に VM を作って、macOS 版と同じ範囲（接続 → ショートカット → 会話の読み取り → 生成 → 差し込み）を自動で流す。正本は要件定義の付録CL（Windows）・付録CG-2（Linux コンテナ）。

| | Windows | Linux |
|---|---|---|
| VM 名 | `ReplyFive Windows` | `ReplyFive Linux` |
| 作る | `windows/build-iso.sh` → `windows/make-tools-iso.sh` → `windows/create-vm.sh` | `ubuntu/make-iso.sh` → `ubuntu/create-vm.sh` → `ubuntu/detach-iso.sh` |
| ssh | `ssh -p 2223 rf@127.0.0.1` | `ssh -p 2222 rf@127.0.0.1` |
| QMP（キー・マウス・画面） | `windows/assets/qmp.py`（4445） | `QMP_PORT=4446 windows/assets/qmp.py`（4446） |
| 流す | `windows/run.sh`（直接配布版）、`RF_EXE=replyfive.exe windows/run.sh`（MSIX 版） | `ubuntu/run.sh` |
| ストア提出物 | `windows/msix.sh`（MSIX をばら置き登録して確認） | `tests/linux-packaging/run.sh`（AppImage / .deb） |

共通の注意：

- UTM の AppleScript で作った VM は Network が `Shared`（vmnet）でポート転送が効かない。`config.plist` で `Mode=Emulated` にしてから PortForward を足す（各 `create-vm.sh` が行う）。
- UTM（sandbox）の QEMU は QMP の `screendump` でファイルを書けない。画面は `screencapture -l $(~/.cache/utm/winid "<VM 名>")` で撮る（`qmp.py shot` がこれを行う）。
- `qmp.py type` は 1 文字ずつ押して離し 90ms 空ける（`QMP_TYPE_DELAY`）。速いと文字が落ちる・順が入れ替わる。ショートカットは QMP ではなくゲスト内から送る（離しの取りこぼしでキーが押しっぱなしになる）。
- ISO を作り直すときは同じ inode へ上書きする（`cat new > old`）。消して作り直すと UTM のブックマークが外れて VM を作り直すことになる。
