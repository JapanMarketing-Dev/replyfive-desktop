# Edge の返信欄（aria-label "Message #general"）の値を UI Automation で読む（差し込みの検証）
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Message #general")
$e = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
if ($e) {
  $vp = $null
  if ($e.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { "entry: " + $vp.Current.Value }
  else { "entry: (no value pattern) name=" + $e.Current.Name }
} else { "entry: (not found)" }
