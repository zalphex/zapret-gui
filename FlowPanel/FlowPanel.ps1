$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

if (![Environment]::Is64BitOperatingSystem -or $env:PROCESSOR_ARCHITECTURE -ne 'AMD64') {
    [void][Windows.MessageBox]::Show('Эта сборка рассчитана на Windows 10/11 x64 (Intel и AMD).', 'zapretgui')
    exit
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    try {
        $arguments = '-NoLogo -NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $PSCommandPath + '"'
        $null = Start-Process -FilePath "$PSHOME\powershell.exe" -ArgumentList $arguments -Verb RunAs
    } catch {
        [void][Windows.MessageBox]::Show('Для запуска zapret нужны права администратора. Запусти zapretgui снова и подтверди запрос Windows.', 'zapretgui')
    }
    exit
}

$script:Lock = New-Object System.Threading.Mutex($false, 'Local\FlowPanel.Flowseal.Manager')
try { $hasLock = $script:Lock.WaitOne(0) }
catch [Threading.AbandonedMutexException] { $hasLock = $true }
if (!$hasLock) {
    [void][Windows.MessageBox]::Show('zapretgui уже открыт.', 'zapretgui')
    $script:Lock.Dispose()
    exit
}

try {
    $script:AppRoot = $PSScriptRoot
    $script:Engine = Join-Path $script:AppRoot 'Engine.ps1'
    $xaml = [IO.File]::ReadAllText((Join-Path $script:AppRoot 'Panel.xaml'))
    $script:Window = [Windows.Markup.XamlReader]::Parse($xaml)
    $script:UI = @{}
    foreach ($match in [regex]::Matches($xaml, 'x:Name="([^"]+)"')) {
        $name = $match.Groups[1].Value
        $control = $script:Window.FindName($name)
        if ($control) { $script:UI[$name] = $control }
    }

    $script:Task = $null
    $script:StartupStarted = $false
    $script:Status = $null
    $script:Winner = @{}
    $script:HadError = $false
    $script:LastLine = ''
    $script:CurrentAction = ''
    $script:Good = [Windows.Media.BrushConverter]::new().ConvertFromString('#F4F4F4')
    $script:Warning = [Windows.Media.BrushConverter]::new().ConvertFromString('#B9B9B9')
    $script:Muted = $script:Window.FindResource('Muted')
    $script:GameLabels = [ordered]@{ disabled = 'Выключен'; all = 'TCP и UDP'; tcp = 'Только TCP'; udp = 'Только UDP' }
    $script:IPLabels = [ordered]@{ loaded = 'По списку'; none = 'Выключен'; any = 'Все IP' }
    $script:UI.GameFilter.ItemsSource = [string[]]@($script:GameLabels.Values)
    $script:UI.IPSet.ItemsSource = [string[]]@($script:IPLabels.Values)
    $script:LoadedList = $null
    $script:UI.ListChoice.ItemsSource = [string[]]@('list-general-user.txt','list-exclude-user.txt','ipset-all.txt','ipset-exclude-user.txt')
    $script:UI.ListChoice.SelectedIndex = 0
    $script:AppUpdate = $null
    $script:Actions = @{
        OpenAppUpdate = @('AppUpdate', '', 'Скачиваю обновление zapretgui…')
        LoadList = @('ReadList', '', 'Читаю список…')
        SaveList = @('SaveList', '', 'Сохраняю список…')
        UpdateIPSet = @('UpdateIPSet', 'Zapret', 'Скачиваю список IPSet…')
        OpenLists = @('ListsFolder', 'Zapret', 'Открываю папку списков…')
        SaveFilters = @('Filters', 'Zapret', 'Применяю настройки фильтров…')
        ZapFolder = @('Folder', 'Zapret', 'Открываю папку стратегий…')
        TgFolder = @('Folder', 'TG', 'Открываю папку конфигов прокси…')
        Refresh = @('Status', '', 'Проверяю доступность сервисов…')
        DownloadBoth = @('DownloadAll', '', 'Загружаю утилиты…')
        Updates = @('Update', '', 'Проверяю версии на GitHub…')
        DownloadZap = @('Download', 'Zapret', 'Загружаю zapret…')
        DownloadTg = @('Download', 'TG', 'Загружаю TG WS Proxy…')
        ZapApply = @('Apply', 'Zapret', 'Применяю стратегию zapret…')
        TgApply = @('Apply', 'TG', 'Применяю профиль TG WS Proxy…')
        ZapTest = @('Test', 'Zapret', 'Проверяю стратегии zapret…')
        TgTest = @('Test', 'TG', 'Проверяю профили TG WS Proxy…')
        ZapAuto = @('Auto', 'Zapret', 'Выбираю стратегию zapret…')
        TgAuto = @('Auto', 'TG', 'Выбираю профиль TG WS Proxy…')
        ZapRestart = @('Restart', 'Zapret', 'Перезапускаю zapret…')
        TgRestart = @('Restart', 'TG', 'Перезапускаю TG WS Proxy…')
        ZapStop = @('Stop', 'Zapret', 'Останавливаю zapret…')
        TgStop = @('Stop', 'TG', 'Останавливаю TG WS Proxy…')
        TgConnect = @('Connect', 'TG', 'Открываю Telegram…')
        PingYouTube = @('Ping', 'YouTube', 'Замеряю задержку YouTube…')
        PingDiscord = @('Ping', 'Discord', 'Замеряю задержку Discord…')
        PingTelegram = @('Ping', 'Telegram', 'Замеряю задержку Telegram…')
    }

    function Append-Log([string]$Message) {
        if ($script:UI.Activity.Text.Length -gt 26000) {
            $script:UI.Activity.Text = $script:UI.Activity.Text.Substring($script:UI.Activity.Text.Length - 16000)
        }
        $script:UI.Activity.AppendText(('{0:HH:mm:ss}  {1}' -f (Get-Date), $Message) + [Environment]::NewLine)
        $script:UI.Activity.ScrollToEnd()
    }

    function Set-Buttons {
        $idle = !$script:Task
        foreach ($key in $script:Actions.Keys) { $script:UI[$key].IsEnabled = $idle }
        $zap = $idle -and $script:Status -and $script:Status.ZapInstalled
        $tg = $idle -and $script:Status -and $script:Status.TGInstalled
        foreach ($key in @('ZapTest','ZapAuto','ZapApply','ZapChoice','ZapFolder','GameFilter','IPSet','SaveFilters','UpdateIPSet','OpenLists')) { $script:UI[$key].IsEnabled = $zap }
        $script:UI.ListChoice.IsEnabled = $zap
        $script:UI.LoadList.IsEnabled = $zap
        $script:UI.ListEditor.IsReadOnly = !$zap -or !$script:LoadedList
        $script:UI.SaveList.IsEnabled = $zap -and $script:LoadedList -and ([string]$script:UI.ListChoice.SelectedItem -eq $script:LoadedList.Name)
        foreach ($key in @('TgTest','TgAuto','TgApply','TgChoice','TgRestart','TgFolder')) { $script:UI[$key].IsEnabled = $tg }
        $script:UI.ZapRestart.IsEnabled = $zap -and [bool]$script:Status.Zapret
        $script:UI.ZapStop.IsEnabled = $zap -and $script:Status.ZapRunning
        $script:UI.TgStop.IsEnabled = $tg -and $script:Status.TGRunning
        $script:UI.TgConnect.IsEnabled = $tg -and $script:Status.TGRunning
    }

    function Set-Choices($Box, $Entries, $Applied, $Best) {
        $selected = $Box.SelectedItem
        $items = [string[]]@($Entries | Where-Object { $_ })
        $Box.ItemsSource = $items
        foreach ($candidate in @($Best, $selected, $Applied, 'general.bat', 'Standard')) {
            if ($candidate -and $items -contains $candidate) { $Box.SelectedItem = $candidate; return }
        }
        if ($items.Count) { $Box.SelectedIndex = 0 }
    }

    function Show-Utility($Badge, $Detail, [bool]$Installed, [bool]$Running, [string]$Version, [string]$Config, [bool]$External) {
        if ($External) { $Badge.Text = 'Запущен вне zapretgui'; $Badge.Foreground = $script:Warning }
        elseif ($Running) { $Badge.Text = 'Запущен'; $Badge.Foreground = $script:Good }
        elseif ($Installed) { $Badge.Text = 'Остановлен'; $Badge.Foreground = $script:Warning }
        else { $Badge.Text = 'Не установлен'; $Badge.Foreground = $script:Muted }
        $parts = @()
        if ($Version) { $parts += $Version }
        if ($Config) { $parts += $Config }
        $Detail.Text = $parts -join '  ·  '
    }

    function Format-Metric([int]$Value) {
        if ($Value -lt 0) { return 'нет ответа' }
        return "$Value мс"
    }

    function Show-Latency($Result) {
        $name = [string]$Result.Name
        $badge = $script:UI[$name + 'State']
        $detail = $script:UI[$name + 'Detail']
        $web = Format-Metric $Result.HTTPS
        $proxy = Format-Metric $Result.MTProto
        if ($name -eq 'Telegram') {
            if (!$Result.ProxyRunning) { $proxy = 'не запущен' }
            $badge.Text = if ($Result.MTProto -ge 0) { 'Прокси отвечает' } elseif ($Result.HTTPS -ge 0) { 'Веб доступен' } else { 'Нет ответа' }
            $detail.Text = "Веб $web  ·  MTProto $proxy"
        } else {
            $badge.Text = if ($Result.HTTPS -ge 0) { 'Доступен' } else { 'Нет ответа' }
            $detail.Text = "HTTPS $web"
        }
        $badge.Foreground = if ($Result.HTTPS -ge 0 -or $Result.MTProto -ge 0) { $script:Good } else { $script:Warning }
        $script:UI.LastCheck.Text = 'Последний сетевой замер  ·  ' + (Get-Date -Format 'HH:mm:ss')
        if ($Result.Full) {
            $icmp = Format-Metric $Result.ICMP
            $line = "$name  ·  ICMP $icmp  ·  HTTPS $web"
            if ($name -eq 'Telegram') { $line += "  ·  MTProto $proxy" }
            $script:UI.PingResult.Text = $line
            $script:LastLine = $line
            Append-Log $line
        }
    }

    function Receive-Message($Result) {
        switch ($Result.Type) {
            'State' {
                if (!$script:Status -or $script:Status.GameFilter -ne $Result.GameFilter -or $script:CurrentAction -eq 'Filters') {
                    $script:UI.GameFilter.SelectedItem = $script:GameLabels[[string]$Result.GameFilter]
                }
                if (!$script:Status -or $script:Status.IPSet -ne $Result.IPSet -or $script:CurrentAction -eq 'Filters') {
                    $script:UI.IPSet.SelectedItem = $script:IPLabels[[string]$Result.IPSet]
                }
                $script:Status = $Result
                Set-Choices $script:UI.ZapChoice $Result.Strategies $Result.Zapret $script:Winner.Zapret
                Set-Choices $script:UI.TgChoice $Result.Profiles $Result.TG $script:Winner.TG
                Show-Utility $script:UI.ZapStatus $script:UI.ZapDetails $Result.ZapInstalled $Result.ZapRunning $Result.ZapretVersion $Result.Zapret $Result.ExternalZap
                Show-Utility $script:UI.TgStatus $script:UI.TgDetails $Result.TGInstalled $Result.TGRunning $Result.TGVersion $Result.TG $Result.ExternalTG
                $script:UI.DownloadZap.Content = if ($Result.ZapInstalled) { 'Обновить' } else { 'Скачать' }
                $script:UI.DownloadTg.Content = if ($Result.TGInstalled) { 'Обновить' } else { 'Скачать' }
            }
            'List' {
                $script:UI.ListEditor.Text = [string]$Result.Text
                $script:LoadedList = $Result
                $note = if ($Result.Inactive) { ' · применяется в режиме IPSet «По списку»' } else { '' }
                $script:UI.ListFeedback.Text = "$($Result.Name)$note"
            }
            'AppUpdate' { $script:AppUpdate = $Result }
            'Folder' {
                $null = Start-Process -FilePath "$env:SystemRoot\explorer.exe" -ArgumentList ('"' + $Result.Path + '"')
            }
            'Latency' { Show-Latency $Result }
            'Winner' { $script:Winner[$Result.Name] = [string]$Result.Choice }
            'Progress' {
                $script:UI.WorkProgress.IsIndeterminate = $false
                $script:UI.WorkProgress.Maximum = $Result.Total
                $script:UI.WorkProgress.Value = $Result.Current
                $script:UI.Footer.Text = $Result.Message
            }
            'Log' {
                $script:LastLine = $Result.Message
                $script:UI.Footer.Text = $Result.Message
                Append-Log $Result.Message
            }
            'Error' {
                $script:HadError = $true
                $script:LastLine = $Result.Message
                $script:UI.Footer.Text = $Result.Message
                Append-Log ('Ошибка · ' + $Result.Message)
            }
            'Connect' {
                $registration = Get-Item 'Registry::HKEY_CLASSES_ROOT\tg' -ErrorAction SilentlyContinue
                if (!$registration) {
                    $script:HadError = $true
                    $script:LastLine = 'Telegram Desktop не зарегистрировал ссылку tg://.'
                    Append-Log $script:LastLine
                } else {
                    $null = Start-Process -FilePath "$env:SystemRoot\explorer.exe" -ArgumentList ('"' + $Result.Uri + '"')
                    $script:LastLine = 'Подтверди подключение прокси в Telegram.'
                    Append-Log $script:LastLine
                }
            }
        }
    }

    function Clear-Metrics {
        foreach ($service in @('YouTube','Discord','Telegram')) {
            $script:UI[$service + 'State'].Text = 'Нужен новый замер'
            $script:UI[$service + 'State'].Foreground = $script:Muted
            $script:UI[$service + 'Detail'].Text = ''
        }
        $script:UI.LastCheck.Text = 'Конфигурация изменилась. Нажми «Обновить статус».'
        $script:UI.PingResult.Text = 'ICMP и HTTPS. Для Telegram также MTProto через запущенный прокси.'
    }

    function Start-Work([string]$Action, [string]$Target, [string]$Label, [string]$Choice = '') {
        if ($script:Task) { return }
        try {
            $script:Winner = @{}
            $script:HadError = $false
            $script:LastLine = ''
            $script:CurrentAction = $Action
            $script:Task = Start-Job -FilePath $script:Engine -ArgumentList $Action, $Target, $Choice, $script:AppRoot
            $script:UI.Footer.Text = $Label
            $script:UI.WorkProgress.IsIndeterminate = $true
            $script:UI.WorkProgress.Visibility = 'Visible'
            if ($Action -in @('Apply','Auto','Restart','Stop','Download','DownloadAll','Filters','UpdateIPSet','SaveList')) { Clear-Metrics }
            if ($Action -notin @('Init','Status')) { Append-Log $Label }
            Set-Buttons
            $script:Timer.Start()
        } catch {
            $script:UI.Footer.Text = $_.Exception.Message
            Append-Log $_.Exception.Message
        }
    }

    $script:Timer = New-Object Windows.Threading.DispatcherTimer
    $script:Timer.Interval = [TimeSpan]::FromMilliseconds(220)
    $script:Timer.Add_Tick({
        if (!$script:Task) { return }
        $errors = @()
        $results = @(Receive-Job -Job $script:Task -ErrorAction SilentlyContinue -ErrorVariable errors)
        foreach ($result in $results) {
            try { Receive-Message $result }
            catch { Receive-Message @{Type='Error'; Message=$_.Exception.Message} }
        }
        foreach ($err in $errors) { Receive-Message @{Type='Error'; Message=$err.Exception.Message} }
        if ($script:Task.State -notin @('Completed', 'Failed', 'Stopped')) { return }
        # Drain output written after the first receive but before job completion.
        foreach ($result in @(Receive-Job -Job $script:Task -ErrorAction SilentlyContinue)) {
            try { Receive-Message $result }
            catch { Receive-Message @{Type='Error'; Message=$_.Exception.Message} }
        }
        if ($script:Task.State -eq 'Failed' -and !$script:HadError) {
            $reason = $script:Task.ChildJobs[0].JobStateInfo.Reason
            $message = if ($reason) { $reason.Message } else { 'Операция прервана.' }
            Receive-Message @{Type='Error'; Message=$message}
        }
        Remove-Job -Job $script:Task -Force
        $script:Task = $null
        $script:Timer.Stop()
        $script:UI.WorkProgress.Visibility = 'Collapsed'
        $script:UI.Footer.Text = if ($script:LastLine) { $script:LastLine }
            elseif ($script:CurrentAction -eq 'Status') { 'Статус обновлён.' }
            else { '' }
        if ($script:CurrentAction -in @('Filters','UpdateIPSet')) {
            $script:UI.FilterFeedback.Text = $script:UI.Footer.Text
        }
        if ($script:CurrentAction -in @('ReadList','SaveList') -and $script:HadError) {
            $script:UI.ListFeedback.Text = $script:UI.Footer.Text
        }
        Set-Buttons
        if ($script:AppUpdate) {
            $update = $script:AppUpdate
            $script:AppUpdate = $null
            try {
                $runner = Join-Path $script:AppRoot 'AppUpdate.ps1'
                $arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $runner +
                    '" -ParentProcess ' + $PID + ' -Installer "' + $update.Path + '" -Hash ' + $update.Hash +
                    ' -AppRoot "' + $script:AppRoot + '"'
                $null = Start-Process -FilePath "$PSHOME\powershell.exe" -ArgumentList $arguments -WindowStyle Hidden
                $script:ExitRequested = $true
                $script:Window.Close()
            } catch {
                Set-SettingsFeedback "Не удалось запустить обновление. $($_.Exception.Message)" $true
            } finally {
                $script:ExitRequested = $false
            }
        }
    })

    function Test-ListEdits {
        if (!$script:LoadedList) { return $false }
        $original = ([string]$script:LoadedList.Text) -replace '\r\n?', "`n"
        $current = $script:UI.ListEditor.Text -replace '\r\n?', "`n"
        return $original -cne $current
    }

    function Confirm-DiscardListEdits {
        $answer = [Windows.MessageBox]::Show($script:Window, 'В редакторе есть несохранённые правки. Отбросить их?',
            'Списки обхода', 'YesNo', 'Warning', 'No')
        return $answer -eq 'Yes'
    }

    $script:UI.ListChoice.Add_SelectionChanged({ Set-Buttons })

    foreach ($key in $script:Actions.Keys) {
        $script:UI[$key].Tag = $key
        $script:UI[$key].Add_Click({
            param($sender, $eventArgs)
            $operation = $script:Actions[[string]$sender.Tag]
            $selected = switch ($operation[1]) {
                'Zapret' { [string]$script:UI.ZapChoice.SelectedItem }
                'TG' { [string]$script:UI.TgChoice.SelectedItem }
                default { '' }
            }
            if ($operation[0] -eq 'AppUpdate') {
                if (Test-ListEdits) {
                    Set-SettingsFeedback 'Сначала сохрани правки в редакторе списков.' $true
                    return
                }
                $selected = $script:AppVersion
            }
            if ($operation[0] -eq 'ReadList') {
                $selected = [string]$script:UI.ListChoice.SelectedItem
                if ((Test-ListEdits) -and !(Confirm-DiscardListEdits)) { return }
            }
            if ($operation[0] -eq 'SaveList') {
                $selected = @{Name=$script:LoadedList.Name; Token=$script:LoadedList.Token; Text=$script:UI.ListEditor.Text} |
                    ConvertTo-Json -Compress
            }
            if ($operation[0] -eq 'Filters') {
                $game = @($script:GameLabels.Keys)[$script:UI.GameFilter.SelectedIndex]
                $ipset = @($script:IPLabels.Keys)[$script:UI.IPSet.SelectedIndex]
                $selected = "$game|$ipset"
            }
            Start-Work $operation[0] $operation[1] $operation[2] $selected
        })
    }

    . (Join-Path $script:AppRoot 'Settings.ps1')
    Initialize-Settings

    $script:Window.Add_Closing({
        param($sender, $eventArgs)
        Invoke-PanelClosing $eventArgs
        if (!$eventArgs.Cancel -and (Test-ListEdits) -and !(Confirm-DiscardListEdits)) { $eventArgs.Cancel = $true }
    })
    $script:Window.Add_ContentRendered({
        if ($script:StartupStarted) { return }
        $script:StartupStarted = $true
        Start-Work 'Startup' '' 'Запускаю установленные утилиты…'
    })
    Set-Buttons
    $script:Window.Add_Closed({ $script:Window.Dispatcher.InvokeShutdown() })
    $script:Window.Show()
    [Windows.Threading.Dispatcher]::Run()
} catch {
    [void][Windows.MessageBox]::Show($_.Exception.Message, 'zapretgui', 'OK', 'Error')
} finally {
    if ($script:ProxyTrayTimer) { $script:ProxyTrayTimer.Stop() }
    if ($script:TrayIcon) {
        $script:TrayIcon.Visible = $false
        $script:TrayIcon.Dispose()
    }
    if ($script:TrayMenu) { $script:TrayMenu.Dispose() }
    if ($script:TrayImage) { $script:TrayImage.Dispose() }
    $script:Lock.ReleaseMutex()
    $script:Lock.Dispose()
}
