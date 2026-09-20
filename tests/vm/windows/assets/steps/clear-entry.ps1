# 返信欄（aria-label "Message #general"）を空にして焦点を当てる。前の実行の残りが混ざらないようにする
Add-Type -AssemblyName UIAutomationClient; Add-Type -AssemblyName UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Message #general")
$e = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
if (-not $e) { "entry not found"; exit 1 }
$vp = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
$vp.SetValue("")
try { $e.SetFocus() } catch { }
"cleared (focus=" + [System.Windows.Automation.AutomationElement]::FocusedElement.Current.ControlType.ProgrammaticName + ")"
