param([string]$Action, [string]$Target, [string]$Choice, [string]$SourceDir)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -Path (Join-Path $SourceDir 'Probe.cs') -IgnoreWarnings
$Root = Join-Path $env:ProgramFiles 'zapretgui'
$StateFile = Join-Path $Root 'state.json'
$TgFile = 'TgWsProxy_windows.exe'
$TgData = Join-Path $Root 'TG\TgWsProxy_data'
$TgConfig = Join-Path $TgData 'config.json'
$ZapExe = Join-Path $Root 'Zapret\bin\winws.exe'
$GameFlag = Join-Path $Root 'Zapret\utils\game_filter.enabled'
$IPList = Join-Path $Root 'Zapret\lists\ipset-all.txt'
$IPBackup = "$IPList.backup"
$TgExe = Join-Path $Root "TG\$TgFile"
$Targets = @{
    YouTube = @('www.youtube.com', 'https://www.youtube.com/')
    Discord = @('discord.com', 'https://discord.com/api/v10/gateway')
    Telegram = @('telegram.org', 'https://web.telegram.org/')
}
$Profiles = [ordered]@{
    'Standard' = @{ cfproxy = $true; h2 = $true }
    'CF без HTTP/2' = @{ cfproxy = $true; h2 = $false }
    'Без CF' = @{ cfproxy = $false; h2 = $false }
}
$Repos = @{ Zapret = 'zapret-discord-youtube'; TG = 'tg-ws-proxy' }
$State = @{ Zapret = ''; TG = ''; ZapretVersion = ''; TGVersion = '' }

function Say([string]$Message) { Write-Output @{ Type = 'Log'; Message = $Message } }

function Write-Json($Path, $Value) {
    $temp = "$Path.new"
    [IO.File]::WriteAllText($temp, ($Value | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
    if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temp, $Path, [NullString]::Value) }
    else { [IO.File]::Move($temp, $Path) }
}

function Save-State { Write-Json $StateFile $State }
function Update-State([string]$Key, [string]$Value) {
    $before = $State[$Key]
    $State[$Key] = $Value
    try { Save-State } catch { $State[$Key] = $before; throw }
}

function Managed([string]$Path) {
    $name = [IO.Path]::GetFileNameWithoutExtension($Path)
    Get-Process -Name $name -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Path }
}

function Assert-NoExternal([string]$Name) {
    if ($Name -eq 'Zapret') {
        $service = Get-Service -Name 'zapret' -ErrorAction SilentlyContinue
        if ($service -and $service.Status -ne 'Stopped') {
            throw 'Уже запущена служба zapret. Останови её в services.msc и повтори действие.'
        }
        $exe = $ZapExe
    } else { $exe = $TgExe }
    $names = [IO.Path]::GetFileNameWithoutExtension($exe)
    if ($Name -eq 'TG') { $names = @($names, 'TgWsProxy_windows_arm64', 'TgWsProxy_windows_7_64bit', 'TgWsProxy_windows_7_32bit') }
    $other = @(Get-Process -Name $names -ErrorAction SilentlyContinue | Where-Object { $_.Path -ne $exe })
    if ($other.Count) { throw "Закрой уже запущенный $Name, чтобы проверить экземпляр zapretgui." }
}

function Stop-Managed([string]$Path) {
    $running = @(Managed $Path)
    foreach ($process in $running) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    foreach ($process in $running) {
        if (!$process.WaitForExit(4500)) { throw "Процесс $($process.Id) не остановился." }
    }
}

function Strategies {
    if (!(Test-Path -LiteralPath $ZapExe -PathType Leaf)) { return }
    Get-ChildItem -LiteralPath (Join-Path $Root 'Zapret') -Filter '*.bat' -File |
        Where-Object {
            $_.Name -match '^[a-zA-Z0-9() ._+-]+\.bat$' -and
            $_.Name -notmatch '^service' -and
            [IO.File]::ReadAllText($_.FullName) -match '(?im)^[ \t]*@?start\b[^\r\n]*winws\.exe'
        } | Sort-Object Name | ForEach-Object Name
}

function ConvertTo-WindowlessStrategy([string]$Script) {
    $start = [regex]'(?im)^[ \t]*@?start\b(?=[^\r\n]*winws\.exe)'
    $supported = [regex]'(?im)^(?<indent>[ \t]*)@?start[ \t]+(?:"[^"\r\n]*"[ \t]+)?(?:/(?:min|max|b)[ \t]+)*(?<exe>"[^"\r\n]*winws\.exe"|[^ \t"\r\n]*winws\.exe)(?=[ \t\r\n]|$)'
    $count = $start.Matches($Script).Count
    if (!$count -or $supported.Matches($Script).Count -ne $count) {
        throw 'В стратегии не распознана строка запуска winws.exe. Проверь формат команды start.'
    }
    return $supported.Replace($Script, '${indent}start "" /b ${exe}')
}

function Start-Zap([string]$Name) {
    if (@(Strategies) -notcontains $Name) { throw 'Стратегия отсутствует в установленном релизе.' }
    $folder = Join-Path $Root 'Zapret'
    $temp = Join-Path $folder ('gui-' + [Guid]::NewGuid().ToString('N') + '.cmd')
    # Use a reversible single-byte encoding to preserve the original script bytes.
    $encoding = [Text.Encoding]::GetEncoding(28591)
    $text = $encoding.GetString([IO.File]::ReadAllBytes((Join-Path $folder $Name)))
    $quiet = ConvertTo-WindowlessStrategy $text

    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = "$env:SystemRoot\System32\cmd.exe"
    $info.Arguments = '/d /s /c ""' + $temp + '""'
    $info.WorkingDirectory = $folder
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.EnvironmentVariables['NO_UPDATE_CHECK'] = '1'
    $launcher = $null
    try {
        [IO.File]::WriteAllBytes($temp, $encoding.GetBytes($quiet))
        $launcher = [Diagnostics.Process]::Start($info)
        if (!$launcher.WaitForExit(12000)) {
            Stop-Process -Id $launcher.Id -Force -ErrorAction SilentlyContinue
            Stop-Managed $ZapExe
            throw 'Скрипт стратегии не завершил запуск.'
        }
        Start-Sleep -Milliseconds 420
        if (!(Managed $ZapExe)) { throw 'winws.exe не удержался после запуска.' }
    } finally {
        if ($launcher) { $launcher.Dispose() }
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    }
}

function Ensure-TgConfig {
    if (!(Test-Path -LiteralPath $TgExe -PathType Leaf)) { throw 'Сначала скачай TG WS Proxy.' }
    if (!(Test-Path -LiteralPath $TgConfig -PathType Leaf)) {
        $null = New-Item -Path $TgData -ItemType Directory -Force
        $bytes = New-Object byte[] 16
        $random = [Security.Cryptography.RandomNumberGenerator]::Create()
        try { $random.GetBytes($bytes) } finally { $random.Dispose() }
        $secret = [BitConverter]::ToString($bytes).Replace('-', '').ToLowerInvariant()
        Write-Json $TgConfig @{ host = '127.0.0.1'; port = 1443; secret = $secret; appearance = 'dark'; language = 'ru'; autostart = $false; check_updates = $false; cfproxy = $true; h2 = $true }
    }
    foreach ($marker in @('.first_run_done_mtproto', '.ipv6_warned')) {
        $path = Join-Path $TgData $marker
        if (!(Test-Path -LiteralPath $path)) { $null = New-Item -ItemType File -Path $path }
    }
}

function Get-TgConfig {
    $raw = Get-Content -LiteralPath $TgConfig -Raw -Encoding UTF8 | ConvertFrom-Json
    $config = @{}
    foreach ($property in $raw.PSObject.Properties) { $config[$property.Name] = $property.Value }
    if ($config.host -ne '127.0.0.1' -or [string]$config.secret -notmatch '^[0-9a-fA-F]{32}$' -or
        [string]$config.port -notmatch '^\d{1,5}$' -or [int]$config.port -lt 1 -or [int]$config.port -gt 65535) {
        throw 'В config.json нужен адрес 127.0.0.1, порт 1–65535 и secret из 32 hex-символов.'
    }
    return $config
}

function Set-TgProfile([string]$Name) {
    if (!$Profiles.Contains($Name)) { throw 'Выбери профиль TG WS Proxy.' }
    $config = Get-TgConfig
    foreach ($key in $Profiles[$Name].Keys) { $config[$key] = $Profiles[$Name][$key] }
    Write-Json $TgConfig $config
}

function Start-Tg {
    Ensure-TgConfig
    $config = Get-TgConfig
    if (Get-NetTCPConnection -State Listen -LocalPort ([int]$config.port) -ErrorAction SilentlyContinue) {
        throw "Порт $($config.port) уже занят."
    }
    $null = Start-Process -FilePath $TgExe -ArgumentList '--portable' -WorkingDirectory (Split-Path -Parent $TgExe) -PassThru
    $until = [DateTime]::UtcNow.AddSeconds(18)
    do {
        Start-Sleep -Milliseconds 250
        $listeners = @(Get-NetTCPConnection -State Listen -LocalAddress '127.0.0.1' -LocalPort ([int]$config.port) -ErrorAction SilentlyContinue)
        $pids = @(Managed $TgExe | ForEach-Object Id)
        if (@($listeners | Where-Object { $pids -contains $_.OwningProcess }).Count) { return }
    } while ([DateTime]::UtcNow -lt $until)
    Stop-Managed $TgExe
    throw 'TG WS Proxy не начал слушать локальный порт.'
}

function Tg-Time([int]$Dc) {
    if (!(Managed $TgExe) -or !(Test-Path -LiteralPath $TgConfig)) { return -1 }
    $config = Get-TgConfig
    try { return [FlowProbe]::Telegram([int]$config.port, [string]$config.secret, [int16]$Dc) }
    catch { return -1 }
}

function Latency([string]$Name, [bool]$IncludePing) {
    $data = $Targets[$Name]
    $icmp = -1
    $https = [FlowProbe]::Https($data[1])
    if ($IncludePing) { $icmp = [FlowProbe]::Icmp($data[0]) }
    $mtp = -1
    $proxyRunning = $false
    if ($Name -eq 'Telegram') {
        $proxyRunning = [bool](Managed $TgExe)
        if ($proxyRunning) { $mtp = Tg-Time 2 }
    }
    Write-Output @{ Type = 'Latency'; Name = $Name; HTTPS = $https; ICMP = $icmp; MTProto = $mtp; ProxyRunning = $proxyRunning; Full = $IncludePing }
}

function Check-Releases([string]$Name) {
    $repo = $Repos[$Name]
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/Flowseal/$repo/releases/latest" -UseBasicParsing -TimeoutSec 20 -UserAgent 'zapretgui'
    $assets = @($release.assets | Where-Object {
        if ($Name -eq 'Zapret') { $_.name -match '^zapret-discord-youtube-[a-zA-Z0-9._-]+\.zip$' }
        else { $_.name -ceq $TgFile }
    })
    if ($assets.Count -ne 1) { throw "В последнем релизе $Name не найден подходящий файл." }
    $asset = $assets[0]
    $uri = [Uri]$asset.browser_download_url
    if ($uri.Scheme -ne 'https' -or $uri.Host -ne 'github.com' -or !$uri.AbsolutePath.StartsWith("/Flowseal/$repo/releases/download/", [StringComparison]::Ordinal)) {
        throw 'GitHub вернул неожиданный адрес файла.'
    }
    return @{ Version = [string]$release.tag_name; Asset = $asset }
}

function Expand-Release([string]$Archive, [string]$Destination) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $prefix = [IO.Path]::GetFullPath($Destination) + [IO.Path]::DirectorySeparatorChar
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.Contains(':') -or ![IO.Path]::GetFullPath((Join-Path $Destination $entry.FullName)).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) {
                throw 'Архив содержит недопустимый путь.'
            }
        }
    } finally { $zip.Dispose() }
    [IO.Compression.ZipFile]::ExtractToDirectory($Archive, $Destination)
}

function Prepare-ZapRelease([string]$Expanded, [string]$Staged, [string]$Live) {
    $folders = @($Expanded) + @(Get-ChildItem -LiteralPath $Expanded -Directory | ForEach-Object FullName)
    $layout = @($folders | Where-Object {
        (Test-Path -LiteralPath (Join-Path $_ 'bin\winws.exe')) -and
        (Test-Path -LiteralPath (Join-Path $_ 'service.bat'))
    })
    if ($layout.Count -ne 1) { throw 'Не найдены bin\winws.exe и service.bat в архиве zapret.' }
    Move-Item -LiteralPath $layout[0] -Destination $Staged
    foreach ($relative in @('lists', 'utils')) {
        $old = Join-Path $Live $relative
        $new = Join-Path $Staged $relative
        if (!(Test-Path -LiteralPath $old) -or !(Test-Path -LiteralPath $new)) { continue }
        if ($relative -eq 'utils' -and !(Test-Path -LiteralPath (Join-Path $old 'check_updates.enabled'))) {
            Remove-Item -LiteralPath (Join-Path $new 'check_updates.enabled') -Force -ErrorAction SilentlyContinue
        }
        Get-ChildItem -LiteralPath $old -File | Where-Object {
            $_.Name -match '(?:-user(?:-[a-z0-9_-]+)?\.txt$|^game_filter\.enabled$|^ipset-all\.txt(?:\.backup|\.before-update\.[0-9-]+\.bak)?$|^check_updates\.enabled$)'
        } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $new $_.Name) -Force }
    }
}

function Install([string]$Name) {
    $release = Check-Releases $Name
    $asset = $release.Asset
    $live = Join-Path $Root $Name
    $exe = if ($Name -eq 'Zapret') { $ZapExe } else { $TgExe }
    if ((Test-Path -LiteralPath $exe) -and $State[$Name + 'Version'] -eq $release.Version) {
        Say "$Name уже обновлён до $($release.Version)."
        return
    }
    $temp = Join-Path $Root ('stage-' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -Path $temp -ItemType Directory
    $backup = Join-Path $temp 'previous'
    $staged = Join-Path $temp 'ready'
    $download = Join-Path $temp ([string]$asset.name)
    $wasRunning = $false
    $switched = $false
    $keepBackup = $false
    try {
        Say "Скачиваю $Name $($release.Version) с GitHub."
        $progressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $asset.browser_download_url -UseBasicParsing -OutFile $download -TimeoutSec 120 -UserAgent 'zapretgui'
        if ((Get-Item -LiteralPath $download).Length -ne [long]$asset.size) { throw 'Размер файла отличается от указанного в релизе.' }
        if ([string]$asset.digest -match '^sha256:([0-9a-fA-F]{64})$') {
            if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ine $Matches[1]) { throw 'SHA-256 файла отличается от указанного в релизе.' }
        }
        if ($Name -eq 'Zapret') {
            $expanded = Join-Path $temp 'expanded'
            Expand-Release $download $expanded
            Prepare-ZapRelease $expanded $staged $live
        } else {
            $null = New-Item -Path $staged -ItemType Directory
            Move-Item -LiteralPath $download -Destination (Join-Path $staged $TgFile)
        }
        $wasRunning = [bool](Managed $exe)
        if ($wasRunning) { Assert-NoExternal $Name; Stop-Managed $exe }
        if ($Name -eq 'TG' -and (Test-Path -LiteralPath $TgData)) {
            Copy-Item -LiteralPath $TgData -Destination (Join-Path $staged 'TgWsProxy_data') -Recurse
        }
        if (Test-Path -LiteralPath $live) { Move-Item -LiteralPath $live -Destination $backup }
        Move-Item -LiteralPath $staged -Destination $live
        $switched = $true
        if ($wasRunning) {
            if ($Name -eq 'Zapret') { Start-Zap $State.Zapret } else { Start-Tg }
        }
        Update-State ($Name + 'Version') $release.Version
        Say "$Name $($release.Version) установлен."
    } catch {
        $original = $_
        try {
            if ($switched) {
                Stop-Managed $exe
                Move-Item -LiteralPath $live -Destination (Join-Path $temp 'failed')
            }
            if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $live }
            if ($wasRunning -and !(Managed $exe)) {
                if ($Name -eq 'Zapret') { Start-Zap $State.Zapret } else { Start-Tg }
            }
        } catch {
            $keepBackup = $true
            Say "Восстановление не завершилось. Рабочая папка обновления: $temp"
        }
        throw $original
    } finally {
        if (!$keepBackup) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

function Apply-Choice([string]$Name, [string]$Config) {
    Assert-NoExternal $Name
    if ($Name -eq 'Zapret') {
        if (@(Strategies) -notcontains $Config) { throw 'Выбери стратегию zapret.' }
        $running = [bool](Managed $ZapExe)
        $old = $State.Zapret
        try {
            Stop-Managed $ZapExe
            Start-Zap $Config
            Update-State 'Zapret' $Config
        } catch {
            Stop-Managed $ZapExe
            if ($running -and $old) { Start-Zap $old }
            throw
        }
    } else {
        Ensure-TgConfig
        $running = [bool](Managed $TgExe)
        $old = Get-TgConfig
        try {
            Stop-Managed $TgExe
            Set-TgProfile $Config
            Start-Tg
            Update-State 'TG' $Config
        } catch {
            Stop-Managed $TgExe
            Write-Json $TgConfig $old
            if ($running) { Start-Tg }
            throw
        }
    }
    Say "Применён конфиг $Config."
}

function Test-Choices([string]$Name) {
    Assert-NoExternal $Name
    $rows = @()
    if ($Name -eq 'Zapret') {
        $configs = @(Strategies)
        if (!$configs.Count) { throw 'Сначала скачай zapret.' }
        $exe = $ZapExe
        $previous = $State.Zapret
    } else {
        Ensure-TgConfig
        $configs = @($Profiles.Keys)
        $exe = $TgExe
        $previous = Get-TgConfig
    }
    $running = [bool](Managed $exe)
    $i = 0
    try {
        foreach ($config in $configs) {
            $i++
            Stop-Managed $exe
            Write-Output @{ Type = 'Progress'; Message = "$Name · $i/$($configs.Count) · $config"; Current = $i; Total = $configs.Count }
            $first = @()
            $second = @()
            try {
                if ($Name -eq 'Zapret') {
                    Start-Zap $config
                    foreach ($attempt in 1..2) {
                        $first += [FlowProbe]::Https($Targets.YouTube[1])
                        $second += [FlowProbe]::Https($Targets.Discord[1])
                    }
                } else {
                    Set-TgProfile $config
                    Start-Tg
                    foreach ($attempt in 1..2) {
                        $first += Tg-Time 2
                        $second += Tg-Time 4
                    }
                }
            } catch { Say "${config}: $($_.Exception.Message)" }
            $firstOk = @($first | Where-Object { $_ -ge 0 })
            $secondOk = @($second | Where-Object { $_ -ge 0 })
            $ok = $firstOk + $secondOk
            $coverage = [int]($firstOk.Count -gt 0) + [int]($secondOk.Count -gt 0)
            $score = if ($ok.Count) { [int]($ok | Measure-Object -Average).Average } else { [int]::MaxValue }
            $rows += [pscustomobject]@{ Name = $config; Coverage = $coverage; Count = $ok.Count; Score = $score }
            $ms = if ($ok.Count) { " · среднее $score мс" } else { '' }
            $labels = if ($Name -eq 'Zapret') { @('YouTube','Discord') } else { @('DC2','DC4') }
            Say "${config}: $($labels[0]) $($firstOk.Count)/2 · $($labels[1]) $($secondOk.Count)/2$ms"
        }
    } finally {
        Stop-Managed $exe
        if ($Name -eq 'TG') { Write-Json $TgConfig $previous }
        if ($running) {
            if ($Name -eq 'Zapret') { Start-Zap $previous } else { Start-Tg }
        }
    }
    $winner = $rows | Where-Object { $_.Count -gt 0 } | Sort-Object -Property @{Expression = 'Coverage'; Descending = $true}, @{Expression = 'Count'; Descending = $true}, Score, Name | Select-Object -First 1
    if (!$winner) { throw 'Ни один конфиг не получил ответа. Предыдущий выбор сохранён.' }
    Write-Output @{ Type = 'Winner'; Name = $Name; Choice = $winner.Name }
    if ($winner.Count -lt 4) { Say "Лучший вариант ответил $($winner.Count) раз из 4." }
    if ($Action -eq 'Auto') { Apply-Choice $Name $winner.Name }
    else { Say "Лучший конфиг: $($winner.Name). Его можно применить из списка." }
}

function Restart-Tool([string]$Name) {
    Assert-NoExternal $Name
    if ($Name -eq 'Zapret') {
        if (!$State.Zapret) { throw 'Сначала примени стратегию zapret.' }
        Stop-Managed $ZapExe
        Start-Zap $State.Zapret
    } else {
        Ensure-TgConfig
        $null = Get-TgConfig
        Stop-Managed $TgExe
        Start-Tg
    }
    Say "$Name перезапущен."
}

function Start-InstalledTools {
    $tools = [ordered]@{ Zapret = $ZapExe; TG = $TgExe }
    foreach ($name in $tools.Keys) {
        $exe = $tools[$name]
        if (!(Test-Path -LiteralPath $exe -PathType Leaf) -or (Managed $exe)) { continue }
        $label = if ($name -eq 'Zapret') { 'zapret' } else { 'TG WS Proxy' }
        try {
            Assert-NoExternal $name
            if ($name -eq 'Zapret') {
                if (!$State.Zapret) {
                    Apply-Choice 'Zapret' 'general.bat'
                } else {
                    Start-Zap $State.Zapret
                }
            } else { Start-Tg }
            Say "$label запущен вместе с панелью."
        } catch {
            Write-Output @{ Type = 'Error'; Message = "Автозапуск ${label}: $($_.Exception.Message)" }
        }
    }
}

function Check-ServiceStatus {
    foreach ($item in @('YouTube', 'Discord', 'Telegram')) { Latency $item $false }
}

function Read-GameFilter {
    $settings = @{ Mode = 'disabled'; TCP = '1024-65535'; UDP = '1024-65535' }
    if (Test-Path -LiteralPath $GameFlag -PathType Leaf) {
        foreach ($line in [IO.File]::ReadLines($GameFlag)) {
            if ($line -match '^mode=(disabled|all|tcp|udp)$') { $settings.Mode = $Matches[1].ToLowerInvariant() }
            elseif ($line -match '^(all|tcp|udp)$') { $settings.Mode = $Matches[1].ToLowerInvariant() }
            elseif ($line -match '^(tcp|udp)=([0-9,-]+)$') { $settings[$Matches[1]] = $Matches[2] }
        }
    }
    return $settings
}

function Read-IPSet([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -eq 0) { return 'any' }
    if ([IO.File]::ReadAllText($Path).Contains('203.0.113.113/32')) { return 'none' }
    return 'loaded'
}

function Write-FilterFile([string]$Path, [AllowEmptyCollection()][byte[]]$Data) {
    $temp = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllBytes($temp, $Data)
        if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temp, $Path, [NullString]::Value) }
        else { [IO.File]::Move($temp, $Path) }
    } finally {
        if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) }
    }
}

function Set-ZapFilters([string]$Selection) {
    if ($Selection -notmatch '^(disabled|all|tcp|udp)\|(loaded|none|any)$') { throw 'Выбери оба режима фильтра.' }
    $gameMode, $ipMode = $Matches[1], $Matches[2]
    if (!(Test-Path -LiteralPath $ZapExe -PathType Leaf)) { throw 'Сначала скачай zapret.' }
    Assert-NoExternal 'Zapret'
    $game = Read-GameFilter
    $oldIP = Read-IPSet $IPList
    if ($game.Mode -eq $gameMode -and $oldIP -eq $ipMode) { Say 'Фильтры уже настроены.'; return }
    if ($ipMode -eq 'loaded' -and $oldIP -ne 'loaded' -and (Read-IPSet $IPBackup) -ne 'loaded') {
        throw 'Резервный список IPSet отсутствует. Помести исходный список в lists\ipset-all.txt.backup и повтори.'
    }
    $previous = @{}
    foreach ($file in @($GameFlag,$IPList,$IPBackup)) {
        $previous[$file] = if (Test-Path -LiteralPath $file -PathType Leaf) { ,([IO.File]::ReadAllBytes($file)) } else { $null }
    }
    $wasRunning = [bool](Managed $ZapExe)
    if ($wasRunning) { Stop-Managed $ZapExe }
    try {
        if ($game.Mode -ne $gameMode) {
            $content = "mode=$gameMode`r`ntcp=$($game.TCP)`r`nudp=$($game.UDP)`r`n"
            Write-FilterFile $GameFlag ([Text.Encoding]::ASCII.GetBytes($content))
        }
        if ($oldIP -ne $ipMode) {
            if ($oldIP -eq 'loaded') { Write-FilterFile $IPBackup $previous[$IPList] }
            $bytes = switch ($ipMode) {
                'loaded' { ,([IO.File]::ReadAllBytes($IPBackup)) }
                'none' { ,([Text.Encoding]::ASCII.GetBytes("203.0.113.113/32`r`n")) }
                'any' { ,([byte[]]@()) }
            }
            Write-FilterFile $IPList $bytes
        }
        if ($wasRunning) { Start-Zap $State.Zapret }
    } catch {
        $failure = $_
        try {
            if ($wasRunning) { Stop-Managed $ZapExe }
            foreach ($file in $previous.Keys) {
                if ($null -ne $previous[$file]) { Write-FilterFile $file $previous[$file] }
                elseif (Test-Path -LiteralPath $file -PathType Leaf) { Remove-Item -LiteralPath $file }
            }
            if ($wasRunning) { Start-Zap $State.Zapret }
        } catch { throw "Не удалось восстановить фильтры или перезапустить zapret. $($_.Exception.Message)" }
        throw $failure
    }
    if ($wasRunning) { Say 'Фильтры сохранены, zapret перезапущен.' }
    else { Say 'Фильтры сохранены. Они применятся при запуске zapret.' }
}

function Measure-IPSetEntries($Lines) {
    $entries = 0
    foreach ($line in $Lines) {
        $value = $line.Trim()
        if (!$value -or $value.StartsWith('#')) { continue }
        $match = [regex]::Match($value, '^(?<address>[0-9A-Fa-f:.]+)(?:/(?<prefix>\d{1,3}))?$')
        $address = $null
        if (!$match.Success -or ![Net.IPAddress]::TryParse($match.Groups['address'].Value, [ref]$address)) {
            throw "Некорректная строка в списке IPSet: $value"
        }
        $width = if ($address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork) { 32 } else { 128 }
        if ($match.Groups['prefix'].Success -and [int]$match.Groups['prefix'].Value -gt $width) {
            throw "Некорректная подсеть в списке IPSet: $value"
        }
        $entries++
    }
    if (!$entries) { throw 'Список IPSet не содержит адресов.' }
    return $entries
}

function Update-IPSet {
    if (!(Test-Path -LiteralPath $ZapExe -PathType Leaf)) { throw 'Сначала скачай zapret.' }
    $mode = Read-IPSet $IPList
    $target = if ($mode -eq 'loaded') { $IPList } else { $IPBackup }
    if ($mode -eq 'loaded') { Assert-NoExternal 'Zapret' }
    $temp = Join-Path (Split-Path -Parent $IPList) ('ipset-' + [guid]::NewGuid().ToString('N') + '.tmp')
    $url = 'https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/ipset-service.txt'
    try {
        $null = Invoke-WebRequest -Uri $url -UseBasicParsing -OutFile $temp -TimeoutSec 30
        if ((Get-Item -LiteralPath $temp).Length -gt 8MB -or (Read-IPSet $temp) -ne 'loaded') {
            throw 'GitHub вернул недопустимый список IPSet.'
        }
        $entries = Measure-IPSetEntries ([IO.File]::ReadLines($temp))

        $new = [IO.File]::ReadAllBytes($temp)
        $old = if ([IO.File]::Exists($target)) { ,([IO.File]::ReadAllBytes($target)) } else { $null }
        if ($null -ne $old -and [Convert]::ToBase64String($old) -ceq [Convert]::ToBase64String($new)) {
            Say 'IPSet уже актуален.'
            return
        }
        if ($null -ne $old -and (Read-IPSet $target) -eq 'loaded') {
            $backup = Join-Path (Split-Path -Parent $IPList) ('ipset-all.txt.before-update.' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.bak')
            Write-FilterFile $backup $old
        }
        $restart = $mode -eq 'loaded' -and [bool](Managed $ZapExe)
        if ($restart) {
            if (!$State.Zapret) { throw 'Не удалось определить текущую стратегию zapret для перезапуска.' }
            Stop-Managed $ZapExe
        }
        try {
            Write-FilterFile $target $new
            if ($restart) { Start-Zap $State.Zapret }
        } catch {
            $failure = $_
            if ($restart) {
                Stop-Managed $ZapExe
                Write-FilterFile $target $old
                Start-Zap $State.Zapret
            }
            throw $failure
        }
        if ($restart) { Say "IPSet обновлён ($entries записей), zapret перезапущен." }
        elseif ($mode -eq 'loaded') { Say "IPSet обновлён ($entries записей)." }
        else { Say "IPSet обновлён ($entries записей) и применится при выборе режима «По списку»." }
    } finally {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    }
}

$EditableLists = @('list-general-user.txt', 'list-exclude-user.txt', 'ipset-all.txt', 'ipset-exclude-user.txt')

function Read-EditableList([string]$Name) {
    if ($Name -cnotin $EditableLists) { throw 'Неизвестный список.' }
    if (!(Test-Path -LiteralPath $ZapExe -PathType Leaf)) { throw 'Сначала скачай zapret.' }
    $inactive = $Name -eq 'ipset-all.txt' -and (Read-IPSet $IPList) -ne 'loaded'
    $path = if ($inactive) { $IPBackup } else { Join-Path (Split-Path -Parent $IPList) $Name }
    $bytes = if ([IO.File]::Exists($path)) { ,([IO.File]::ReadAllBytes($path)) } else { $null }
    $text = ''
    $token = 'missing'
    if ($null -ne $bytes) {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $token = [Convert]::ToBase64String($sha.ComputeHash($bytes)) }
        finally { $sha.Dispose() }
        $text = [Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xfeff)
    }
    return @{
        Type = 'List'; Name = $Name; Text = $text; Path = $path; Bytes = $bytes
        Token = ($(if ($inactive) { 'inactive:' } else { 'active:' }) + $token)
        Inactive = $inactive
    }
}

function Send-EditableList($List) {
    Write-Output @{
        Type = 'List'; Name = $List.Name; Text = $List.Text
        Token = $List.Token; Inactive = $List.Inactive
    }
}

function Save-EditableList([string]$Payload) {
    $edit = ConvertFrom-Json -InputObject $Payload
    $list = Read-EditableList ([string]$edit.Name)
    if ($list.Token -cne [string]$edit.Token) {
        throw 'Список или режим IPSet изменились. Скопируй свои правки и перечитай файл.'
    }
    $text = ([string]$edit.Text).Replace("`r`n", "`n").Replace("`r", "`n")
    if ($text.IndexOf([char]0) -ge 0) { throw 'Список содержит недопустимый нулевой символ.' }
    $hasEntries = [regex]::IsMatch($text, '(?m)^[ \t]*[^#\s]')
    if ($list.Name -eq 'ipset-all.txt' -and (!$hasEntries -or $text.Contains('203.0.113.113/32'))) {
        throw 'Для IPSet укажи хотя бы один IP или подсеть. Режим фильтра меняется выше.'
    }
    if (!$hasEntries) {
        # Match service.bat defaults so an empty file does not change winws matching.
        $placeholder = if ($list.Name -eq 'ipset-exclude-user.txt') { '203.0.113.113/32' } else { 'domain.example.abc' }
        $text = $text.TrimEnd() + "`n" + $placeholder
    }
    if ($list.Name -like 'ipset-*') { $null = Measure-IPSetEntries ($text -split "`n") }
    $text = ($text.TrimEnd([char]10) + "`n").Replace("`n", "`r`n")
    $data = [Text.Encoding]::UTF8.GetBytes($text)
    if ($data.Length -gt 8MB) { throw 'Список превышает 8 МБ.' }

    if (!$list.Inactive) { Assert-NoExternal 'Zapret' }
    $restart = !$list.Inactive -and [bool](Managed $ZapExe)
    if ($restart) {
        if (!$State.Zapret) { throw 'Не удалось определить текущую стратегию zapret для перезапуска.' }
        Stop-Managed $ZapExe
    }
    try {
        Write-FilterFile $list.Path $data
        if ($restart) { Start-Zap $State.Zapret }
    } catch {
        $failure = $_
        if ($restart) {
            Stop-Managed $ZapExe
            if ($null -eq $list.Bytes) { Remove-Item -LiteralPath $list.Path -Force -ErrorAction SilentlyContinue }
            else { Write-FilterFile $list.Path $list.Bytes }
            Start-Zap $State.Zapret
        }
        throw $failure
    }
    Send-EditableList (Read-EditableList $list.Name)
    if ($restart) { Say 'Список сохранён, zapret перезапущен.' }
    elseif ($list.Inactive) { Say 'Список сохранён. Он применится в режиме IPSet «По списку».' }
    else { Say 'Список сохранён.' }
}

function Get-AppUpdate([string]$Current) {
    $source = 'https://api.github.com/repos/zalphex/zapret-gui/contents'
    $headers = @{ Accept='application/vnd.github.raw+json'; 'X-GitHub-Api-Version'='2022-11-28' }
    $encrypted = Get-ItemPropertyValue -LiteralPath 'HKCU:\Software\zapretgui' -Name GitHubUpdateToken -ErrorAction SilentlyContinue
    if ($encrypted) {
        $secure = ConvertTo-SecureString $encrypted
        try { $headers.Authorization = 'Bearer ' + [Net.NetworkCredential]::new('', $secure).Password }
        finally { $secure.Dispose() }
    }
    try {
        $manifest = Invoke-RestMethod -Uri "$source/release/update.json?ref=main" -Headers $headers -UserAgent 'zapretgui' -MaximumRedirection 0 -TimeoutSec 20
    } catch {
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        if ($status -in @(401,403,404)) { throw 'GitHub не дал доступ к версии. Проверь токен чтения приватного репозитория в настройках.' }
        throw
    }
    if ($manifest -is [string]) { $manifest = ConvertFrom-Json $manifest }
    $version = $null
    if ([string]$manifest.version -notmatch '^\d+\.\d+\.\d+$' -or
        ![version]::TryParse([string]$manifest.version,[ref]$version) -or
        [string]$manifest.sha256 -notmatch '^[a-fA-F0-9]{64}$') {
        throw 'Файл версии GitHub содержит неверные данные.'
    }
    if ($version -le [version]$Current) { Say 'Установлена актуальная версия zapretgui.'; return }
    $folder = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'zapretgui\Desktop'
    $path = Join-Path $folder ('update-' + [guid]::NewGuid().ToString('N') + '.exe')
    $ready = $false
    try {
        # Fixed API host and location: never send the token to a redirect destination.
        $null = Invoke-WebRequest -Uri "$source/release/zapretgui_Setup_x64.exe?ref=main" -Headers $headers -UserAgent 'zapretgui' -MaximumRedirection 0 -UseBasicParsing -OutFile $path -TimeoutSec 120
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($hash -ine $manifest.sha256) { throw 'Скачанный установщик не совпадает с контрольной суммой GitHub.' }
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
        if ($info.ProductName -cne 'zapretgui' -or $info.FileVersion -ne [string]$version) {
            throw 'Версия скачанного установщика не совпадает с данными GitHub.'
        }
        Write-Output @{ Type='AppUpdate'; Path=$path; Hash=$hash; Version=[string]$version }
        $ready = $true
    } finally {
        if (!$ready) { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
    }
}

function Report-State {
    Write-Output @{
        Type = 'State'; Zapret = $State.Zapret; TG = $State.TG
        ZapretVersion = $State.ZapretVersion; TGVersion = $State.TGVersion
        ZapInstalled = (Test-Path -LiteralPath $ZapExe -PathType Leaf)
        TGInstalled = (Test-Path -LiteralPath $TgExe -PathType Leaf)
        ZapRunning = [bool](Managed $ZapExe); TGRunning = [bool](Managed $TgExe)
        ExternalZap = [bool](Get-Process -Name 'winws' -ErrorAction SilentlyContinue | Where-Object { $_.Path -ne $ZapExe })
        ExternalTG = [bool](Get-Process -Name 'TgWsProxy_windows','TgWsProxy_windows_arm64','TgWsProxy_windows_7_64bit','TgWsProxy_windows_7_32bit' -ErrorAction SilentlyContinue | Where-Object { $_.Path -ne $TgExe })
        GameFilter = (Read-GameFilter).Mode; IPSet = (Read-IPSet $IPList)
        Strategies = @(Strategies); Profiles = @($Profiles.Keys)
    }
}

try {
    if (!(Test-Path -LiteralPath $Root)) { $null = New-Item -Path $Root -ItemType Directory }
    if (Test-Path -LiteralPath $StateFile -PathType Leaf) {
        $saved = Get-Content -LiteralPath $StateFile -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($key in @($State.Keys)) { if ($null -ne $saved.$key) { $State[$key] = [string]$saved.$key } }
    }
    switch ($Action) {
        'Startup' {
            Start-InstalledTools
            Check-ServiceStatus
            $updateFlag = Join-Path $Root 'Zapret\utils\check_updates.enabled'
            if ($State.ZapretVersion -and (Test-Path -LiteralPath $updateFlag -PathType Leaf)) {
                try {
                    $latest = Check-Releases 'Zapret'
                    if ($latest.Version -ne $State.ZapretVersion) {
                        Say "zapret: доступна версия $($latest.Version). Обновление находится во вкладке «Установка и тесты»."
                    }
                } catch { Say "Проверка обновления zapret: $($_.Exception.Message)" }
            }
        }
        'Status' { Check-ServiceStatus }
        'Ping' { Latency $Target $true }
        'Download' { Install $Target }
        'DownloadAll' {
            foreach ($name in @('Zapret', 'TG')) {
                try { Install $name }
                catch { Write-Output @{ Type = 'Error'; Message = "${name}: $($_.Exception.Message)" } }
            }
        }
        'Update' {
            foreach ($name in @('Zapret', 'TG')) {
                try {
                    $release = Check-Releases $name
                    $local = $State[$name + 'Version']
                    if (!$local) { $local = 'не установлен' }
                    $message = if ($local -eq $release.Version) { 'обновлений нет' } else { "на GitHub $($release.Version), здесь $local" }
                    Say "${name}: $message."
                } catch { Say "${name}: $($_.Exception.Message)" }
            }
        }
        'Filters' { Set-ZapFilters $Choice }
        'UpdateIPSet' { Update-IPSet }
        'ReadList' { Send-EditableList (Read-EditableList $Choice) }
        'SaveList' { Save-EditableList $Choice }
        'ListsFolder' {
            $path = Join-Path $Root 'Zapret\lists'
            if (!(Test-Path -LiteralPath $path -PathType Container)) { throw 'Сначала скачай zapret.' }
            Write-Output @{ Type = 'Folder'; Path = $path }
        }
        'Folder' {
            if ($Target -eq 'Zapret') {
                if (!(Test-Path -LiteralPath $ZapExe -PathType Leaf)) { throw 'Сначала скачай zapret.' }
                $path = Join-Path $Root 'Zapret'
            } else {
                Ensure-TgConfig
                $path = $TgData
            }
            Write-Output @{ Type = 'Folder'; Path = $path }
        }
        'Apply' { Apply-Choice $Target $Choice }
        'Test' { Test-Choices $Target }
        'Auto' { Test-Choices $Target }
        'Restart' { Restart-Tool $Target }
        'Stop' {
            if ($Target -eq 'Zapret') { Stop-Managed $ZapExe } else { Stop-Managed $TgExe }
            Say "$Target остановлен."
        }
        'Connect' {
            if (!(Managed $TgExe)) { throw 'Сначала запусти TG WS Proxy.' }
            $config = Get-TgConfig
            Write-Output @{ Type = 'Connect'; Uri = "tg://proxy?server=127.0.0.1&port=$($config.port)&secret=dd$($config.secret)" }
        }
        'AppUpdate' { Get-AppUpdate $Choice }
        'Init' {}
        default { throw 'Неизвестная операция.' }
    }
} catch {
    Write-Output @{ Type = 'Error'; Message = $_.Exception.Message }
} finally { Report-State }
