param([string]$PathContains='handoff-test')
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$processes=Get-Process CallOfAILauncher -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*$PathContains*" }
foreach($p in $processes) {
    "PID=$($p.Id) PATH=$($p.Path) RESPONDING=$($p.Responding)"
    $root=[System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
    if($null -eq $root){continue}
    $elements=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
    foreach($e in $elements) {
        if($e.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -or $e.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text) {
            "$($e.Current.ControlType.ProgrammaticName): $($e.Current.Name) enabled=$($e.Current.IsEnabled)"
        }
    }
}