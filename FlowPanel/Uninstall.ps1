$ErrorActionPreference = 'Stop'
$launcher = Join-Path $PSScriptRoot 'zapretgui.exe'
$tasks = @(Get-ScheduledTask -TaskPath '\' | Where-Object {
    $_.TaskName -like 'zapret-GUI-*' -and @($_.Actions | Where-Object Execute -EQ $launcher).Count -gt 0
})
foreach ($task in $tasks) {
    Unregister-ScheduledTask -InputObject $task -Confirm:$false
}
$root = Split-Path -Parent $PSScriptRoot
$paths = @((Join-Path $root 'Zapret\bin\winws.exe'), (Join-Path $root 'TG\TgWsProxy_windows.exe'))
foreach ($process in @(Get-Process -Name 'winws','TgWsProxy_windows' -ErrorAction SilentlyContinue)) {
    if ($process.Path -in $paths) { Stop-Process -InputObject $process -Force -ErrorAction SilentlyContinue }
}
