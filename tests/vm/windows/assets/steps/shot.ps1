# 画面全体を PNG に（対話セッションで実行）。引数: 出力パス
param([string]$Out = "C:\rf\shots\shot.png")
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
$bmp.Save($Out)
"shot: $Out ($($b.Width)x$($b.Height))"
