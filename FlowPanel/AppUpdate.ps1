param([int]$ParentProcess, [string]$Installer, [string]$Hash, [string]$AppRoot)
$ErrorActionPreference = 'Stop'
try {
    $parent = Get-Process -Id $ParentProcess -ErrorAction SilentlyContinue
    if ($parent -and !$parent.WaitForExit(30000)) { throw 'Запущенное приложение не закрылось.' }
    if ((Get-FileHash -LiteralPath $Installer -Algorithm SHA256).Hash -ne $Hash) { throw 'Скачанный файл изменился.' }
    $install = Start-Process -FilePath $Installer -ArgumentList '/S' -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw "Ошибка установки, код $($install.ExitCode)." }
    $null = Start-Process -FilePath (Join-Path $AppRoot 'zapretgui.exe')
} catch {
    Add-Type -AssemblyName System.Windows.Forms
    [void][Windows.Forms.MessageBox]::Show($_.Exception.Message,'Обновление zapretgui')
} finally {
    Remove-Item -LiteralPath $Installer -Force -ErrorAction SilentlyContinue
}
