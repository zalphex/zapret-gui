$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$legacy = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'FlowPanel'
if (!(Test-Path -LiteralPath $legacy -PathType Container)) { exit 0 }

$marker = Join-Path $root '.flowpanel-migrated'
if (!(Test-Path -LiteralPath $marker)) {
    foreach ($name in @('Zapret','TG','state.json')) {
        $source = Join-Path $legacy $name
        $destination = Join-Path $root $name
        if (!(Test-Path -LiteralPath $source) -or (Test-Path -LiteralPath $destination)) { continue }
        $temp = Join-Path $root ([guid]::NewGuid().ToString('N') + '.migration')
        try {
            Copy-Item -LiteralPath $source -Destination $temp -Recurse
            Move-Item -LiteralPath $temp -Destination $destination
        } finally {
            if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
        }
    }
    $null = New-Item -Path $marker -ItemType File
}

$oldLauncher = Join-Path $legacy 'Desktop\zapret GUI.exe'
$newLauncher = Join-Path $PSScriptRoot 'zapretgui.exe'
foreach ($task in @(Get-ScheduledTask -TaskPath '\' | Where-Object { $_.TaskName -like 'zapret-GUI-*' })) {
    $actions = @($task.Actions)
    if ($actions.Count -ne 1 -or $actions[0].Execute -ne $oldLauncher) { continue }
    $disabled = $task.State -eq 'Disabled'
    $action = New-ScheduledTaskAction -Execute $newLauncher -WorkingDirectory $PSScriptRoot
    $null = Set-ScheduledTask -TaskPath $task.TaskPath -TaskName $task.TaskName -Action $action
    if ($disabled) { $null = Disable-ScheduledTask -TaskPath $task.TaskPath -TaskName $task.TaskName }
}

$paths = @((Join-Path $legacy 'Zapret\bin\winws.exe'), (Join-Path $legacy 'TG\TgWsProxy_windows.exe'))
foreach ($process in @(Get-Process -Name 'winws','TgWsProxy_windows' -ErrorAction SilentlyContinue)) {
    if ($process.Path -in $paths) {
        Stop-Process -InputObject $process -Force
        if (!$process.WaitForExit(5000)) { throw "Could not stop legacy process $($process.Id)." }
    }
}
