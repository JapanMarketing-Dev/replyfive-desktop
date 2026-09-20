# ゲスト側で押しっぱなし扱いになったキーを離す（QMP 入力の後始末）
Add-Type -Namespace W -Name K -MemberDefinition '[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra); [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);'
$held = @()
for ($vk = 8; $vk -le 254; $vk++) { if ([W.K]::GetAsyncKeyState($vk) -band 0x8000) { $held += $vk; [W.K]::keybd_event([byte]$vk, 0, 2, [UIntPtr]::Zero) } }
"released: " + ($held -join ",")
