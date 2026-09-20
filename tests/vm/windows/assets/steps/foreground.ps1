# 前面ウインドウの表題（確認用）
Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);'
$sb = New-Object System.Text.StringBuilder 512
[void][W.U]::GetWindowText([W.U]::GetForegroundWindow(), $sb, 512)
"foreground: " + $sb.ToString()
