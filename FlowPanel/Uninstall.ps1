$ErrorActionPreference = 'Stop'
$launcher = Join-Path $PSScriptRoot 'zapretgui.exe'
$panel = Join-Path $PSScriptRoot 'FlowPanel.ps1'
$powershell = Join-Path $PSHOME 'powershell.exe'
$arguments = '-NoLogo -NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $panel + '" -WindowsStartup'
$tasks = @(Get-ScheduledTask -TaskPath '\' | Where-Object {
    $_.TaskName -like 'zapret-GUI-*' -and @($_.Actions | Where-Object {
        $_.Execute -eq $launcher -or ($_.Execute -eq $powershell -and $_.Arguments -eq $arguments)
    }).Count -gt 0
})
foreach ($task in $tasks) {
    Unregister-ScheduledTask -InputObject $task -Confirm:$false
}
$root = Split-Path -Parent $PSScriptRoot
$paths = @((Join-Path $root 'Zapret\bin\winws.exe'), (Join-Path $root 'TG\TgWsProxy_windows.exe'))
foreach ($process in @(Get-Process -Name 'winws','TgWsProxy_windows' -ErrorAction SilentlyContinue)) {
    if ($process.Path -in $paths) { Stop-Process -InputObject $process -Force -ErrorAction SilentlyContinue }
}
