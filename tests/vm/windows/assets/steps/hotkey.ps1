# 会話アプリ（Edge の Slack 風ページ）を前面にしてショートカット Ctrl+Shift+R を送る（対話セッションで実行）
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName Microsoft.VisualBasic
Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);'
$p = Get-Process msedge -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like "*Slack*" } | Select-Object -First 1
if (-not $p) { "no edge window"; exit 1 }
[void][W.U]::ShowWindow($p.MainWindowHandle, 9)
[void][W.U]::SetForegroundWindow($p.MainWindowHandle)
try { [Microsoft.VisualBasic.Interaction]::AppActivate($p.Id) } catch { }
Start-Sleep -Milliseconds 800
$sb = New-Object System.Text.StringBuilder 512
[void][W.U]::GetWindowText([W.U]::GetForegroundWindow(), $sb, 512)
"foreground: " + $sb.ToString()
[System.Windows.Forms.SendKeys]::SendWait("^+r")
Start-Sleep 3
"sent"
