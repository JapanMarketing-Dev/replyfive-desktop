Add-Type -AssemblyName UIAutomationClient; Add-Type -AssemblyName UIAutomationTypes
$f = [System.Windows.Automation.AutomationElement]::FocusedElement
"focused: pid=" + $f.Current.ProcessId + " type=" + $f.Current.ControlType.ProgrammaticName + " name=" + $f.Current.Name + " hwnd=" + $f.Current.NativeWindowHandle
$p = Get-Process msedge | Where-Object { $_.MainWindowTitle -like "*Slack*" } | Select-Object -First 1
"edge main: pid=" + $p.Id + " hwnd=" + $p.MainWindowHandle
Get-Process msedge | ft Id,MainWindowTitle
