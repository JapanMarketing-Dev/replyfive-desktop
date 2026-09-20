# 検証 VM のログオン中セッション（session 1）で常駐し、C:\rf\cmd\*.ps1 を順に実行して結果を *.out に書く。
# ssh（session 0）からは UI Automation も画面の取り込みもできないので、GUI に触る操作はすべてこの経路で実行する。
# 起動: schtasks で対話セッションに /it で登録して /run（run.sh が行う）
$dir = 'C:\rf\cmd'
New-Item -ItemType Directory -Path $dir -Force | Out-Null
"agent start $(Get-Date -Format s) session=$([System.Diagnostics.Process]::GetCurrentProcess().SessionId) user=$env:USERNAME" | Out-File C:\rf\agent.log -Append
while ($true) {
  $f = Get-ChildItem -Path $dir -Filter '*.ps1' -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -First 1
  if ($f) {
    $out = [System.IO.Path]::ChangeExtension($f.FullName, '.out')
    $tmp = $out + '.tmp'
    # 子 powershell の出力はパイプではなくファイルへ（cmd の > で）。パイプにすると、命令が Start-Process で起動した常駐プロセス（サーバ・アプリ）が
    # ハンドルを継承して EOF が来ず、agent が止まる。待つのは cmd の終了だけ（Start-Process -Wait は子孫まで待つので使わない）
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'cmd.exe'
    # 出力は UTF-8 で（既定の OEM コードページだと日本語が ? になる）
    $psi.Arguments = "/c powershell -NoProfile -ExecutionPolicy Bypass -Command `"[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; & '$($f.FullName)'`" > `"$tmp`" 2>&1"
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    try { $p = [System.Diagnostics.Process]::Start($psi); $p.WaitForExit() }
    catch { $_ | Out-File -FilePath $tmp -Append -Encoding utf8 }
    # tmp は常駐プロセスがハンドルを継承して開いたままのことがあるので、移動ではなく読み写す
    $text = ''; try { $text = Get-Content -Path $tmp -Raw -Encoding utf8 -ErrorAction SilentlyContinue } catch { }
    Set-Content -Path $out -Value $text -Encoding utf8
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    Remove-Item $f.FullName -Force
  }
  Start-Sleep -Milliseconds 300
}
