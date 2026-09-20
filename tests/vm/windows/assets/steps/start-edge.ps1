# 前回のセッション（フォームの内容）を復元させないよう、毎回まっさらなプロファイルで起動する
Remove-Item C:\rf\edge-profile -Recurse -Force -ErrorAction SilentlyContinue
$edge = (Get-ChildItem "C:\Program Files*\Microsoft\Edge\Application\msedge.exe" | Select-Object -First 1).FullName
Start-Process -FilePath $edge -ArgumentList "--new-window", "--no-first-run", "--user-data-dir=C:\rf\edge-profile", "--disable-features=Translate,msTranslate", "--disable-session-crashed-bubble", "--hide-crash-restore-bubble", "--window-size=1000,700", "--window-position=40,40", "file:///C:/rf/app.slack.com/client/general.html"
Start-Sleep 8
"edge: $edge"
