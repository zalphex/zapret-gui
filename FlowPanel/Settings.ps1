$script:AppVersion = '1.3.8'
$script:SettingsKey = 'HKCU:\Software\zapretgui'
$script:AutoTaskName = 'zapret-GUI-' + $identity.User.Value
$script:DesktopDir = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'zapretgui\Desktop'
$script:PreviousWindowState = 'Normal'
$script:ExitRequested = $false

function Get-AutoStart {
    $task = Get-ScheduledTask -TaskPath '\' -TaskName $script:AutoTaskName -ErrorAction SilentlyContinue
    return [bool]($task -and $task.State -ne 'Disabled')
}

function Set-AutoStart([bool]$Enabled) {
    if (!$Enabled) {
        Unregister-ScheduledTask -TaskPath '\' -TaskName $script:AutoTaskName -Confirm:$false
        return
    }
    if (!(Test-Path -LiteralPath (Join-Path $script:DesktopDir 'zapretgui.exe') -PathType Leaf)) {
        throw 'Установи zapretgui через установщик, чтобы включить автозапуск.'
    }
    $user = $identity.Name
    $action = New-ScheduledTaskAction -Execute (Join-Path $script:DesktopDir 'zapretgui.exe') -WorkingDirectory $script:DesktopDir
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
    $options = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    $params = @{
        TaskPath = '\'; TaskName = $script:AutoTaskName
        Action = $action; Trigger = $trigger; Principal = $principal; Settings = $options
    }
    $null = Register-ScheduledTask @params -Force
}

function Show-PanelFromTray {
    $script:Window.ShowInTaskbar = $true
    $script:Window.WindowState = $script:PreviousWindowState
    if (!$script:Window.IsVisible) { $script:Window.Show() }
    $script:TrayIcon.Visible = $false
    [void]$script:Window.Activate()
}

function Hide-PanelToTray {
    $script:TrayIcon.Visible = $true
    $script:Window.ShowInTaskbar = $false
    $script:Window.Hide()
}

function Invoke-PanelClosing($EventArgs) {
    if ($script:UI.MinimizeToTray.IsChecked -and !$script:ExitRequested) {
        $EventArgs.Cancel = $true
        # WPF must finish canceling Close before Hide changes window visibility.
        $null = $script:Window.Dispatcher.InvokeAsync([Action]{ Hide-PanelToTray })
        return
    }
    if ($script:Task) {
        $EventArgs.Cancel = $true
        $script:UI.Footer.Text = 'Дождись окончания операции, чтобы сохранить состояние конфигурации.'
    }
}

function Set-SettingsFeedback([string]$Message, [bool]$IsError = $false) {
    $script:UI.SettingsFeedback.Text = $Message
    $script:UI.SettingsFeedback.Foreground = if ($IsError) { $script:Warning } else { $script:Muted }
}

function Initialize-WindowAppearance {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class ZapretWindowAppearance
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    public static void Apply(IntPtr window)
    {
        int enabled = 1;
        if (DwmSetWindowAttribute(window, 20, ref enabled, sizeof(int)) < 0)
            DwmSetWindowAttribute(window, 19, ref enabled, sizeof(int));

        // Windows 11 also accepts explicit caption and text colors in COLORREF order.
        int caption = 0x00101010;
        int text = 0x00F5F5F5;
        DwmSetWindowAttribute(window, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(window, 36, ref text, sizeof(int));
    }
}
'@
    Add-Type -Path (Join-Path $script:AppRoot 'TaskbarIdentity.cs')
    [ZapretTaskbarIdentity]::SetProcess()
    $script:Window.Add_SourceInitialized({
        $handle = [Windows.Interop.WindowInteropHelper]::new($script:Window).Handle
        [ZapretWindowAppearance]::Apply($handle)
        [ZapretTaskbarIdentity]::SetWindow($handle)
    })
}

function Initialize-Settings {
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    $exe = Join-Path $script:AppRoot 'zapretgui.exe'
    if (Test-Path -LiteralPath $exe -PathType Leaf) {
        $script:TrayImage = [Drawing.Icon]::ExtractAssociatedIcon($exe)
    }
    if ($script:TrayImage) {
        $bitmap = [Windows.Interop.Imaging]::CreateBitmapSourceFromHIcon(
            $script:TrayImage.Handle, [Windows.Int32Rect]::Empty, [Windows.Media.Imaging.BitmapSizeOptions]::FromEmptyOptions())
        $bitmap.Freeze()
        $script:Window.Icon = $bitmap
        $script:UI.BrandImage.Source = $bitmap
    }
    Initialize-WindowAppearance
    $script:TrayMenu = [Windows.Forms.ContextMenuStrip]::new()
    $open = $script:TrayMenu.Items.Add('Открыть zapretgui')
    $open.Add_Click({ Show-PanelFromTray })
    $exit = $script:TrayMenu.Items.Add('Выход')
    $exit.Add_Click({
        if ($script:Task) { Show-PanelFromTray }
        $script:ExitRequested = $true
        try { $script:Window.Close() }
        finally { $script:ExitRequested = $false }
    })
    $script:TrayIcon = [Windows.Forms.NotifyIcon]::new()
    $script:TrayIcon.Text = 'zapretgui'
    $script:TrayIcon.Icon = if ($script:TrayImage) { $script:TrayImage } else { [Drawing.SystemIcons]::Application }
    $script:TrayIcon.ContextMenuStrip = $script:TrayMenu
    $script:TrayIcon.Add_DoubleClick({ Show-PanelFromTray })
    $script:ManagedProxyPath = Join-Path $env:ProgramFiles 'zapretgui\TG\TgWsProxy_windows.exe'
    $script:ProxyTrayTimer = New-Object Windows.Threading.DispatcherTimer
    $script:ProxyTrayTimer.Interval = [TimeSpan]::FromSeconds(2)
    $script:ProxyTrayTimer.Add_Tick({ [ZapretProxyTray]::HideManagedProxy($script:ManagedProxyPath) })
    $script:ProxyTrayTimer.Start()


    $pref = Get-ItemProperty -LiteralPath $script:SettingsKey -Name MinimizeToTray -ErrorAction SilentlyContinue
    if ($null -eq $pref) {
        $pref = Get-ItemProperty -LiteralPath 'HKCU:\Software\FlowPanel' -Name MinimizeToTray -ErrorAction SilentlyContinue
    }
    $script:UI.MinimizeToTray.IsChecked = $null -eq $pref -or $pref.MinimizeToTray -eq 1
    $script:UI.AutoStart.IsChecked = Get-AutoStart
    $script:UI.InstalledAppVersion.Text = "Установленная версия: $script:AppVersion"
    $script:UI.AutoStart.Add_Click({
        $want = [bool]$script:UI.AutoStart.IsChecked
        try {
            Set-AutoStart $want
            $msg = if ($want) { 'Автозапуск включён.' } else { 'Автозапуск выключен.' }
            Set-SettingsFeedback $msg
        } catch {
            $script:UI.AutoStart.IsChecked = Get-AutoStart
            Set-SettingsFeedback "Не удалось изменить автозапуск. $($_.Exception.Message)" $true
        }
    })
    $script:UI.MinimizeToTray.Add_Click({
        $want = [bool]$script:UI.MinimizeToTray.IsChecked
        try {
            if (!(Test-Path -LiteralPath $script:SettingsKey)) {
                $null = New-Item -Path $script:SettingsKey -Force
            }
            $null = New-ItemProperty -LiteralPath $script:SettingsKey -Name MinimizeToTray -PropertyType DWord -Value ([int]$want) -Force
            $msg = if ($want) { 'Сворачивание в трей включено.' } else { 'Сворачивание в трей выключено.' }
            Set-SettingsFeedback $msg
        } catch {
            $script:UI.MinimizeToTray.IsChecked = !$want
            Set-SettingsFeedback "Не удалось сохранить настройку. $($_.Exception.Message)" $true
        }
    })
    $script:Window.Add_StateChanged({
        if ($script:Window.WindowState -ne 'Minimized') {
            $script:PreviousWindowState = $script:Window.WindowState
        } elseif ($script:UI.MinimizeToTray.IsChecked) {
            Hide-PanelToTray
        }
    })
}
