#requires -Version 5.1
<#
    Установщик Mining Switch.
    Запуск (PowerShell 5.1 или новее, для Program Files нужны права администратора):

        powershell -c "irm https://raw.githubusercontent.com/XYphrodite/MiningSwitch/main/install.ps1 | iex"

    Что делает: скачивает MiningSwitch.zip с GitHub, устанавливает в
    C:\Program Files\MiningSwitch (или %LOCALAPPDATA%\Programs\MiningSwitch без
    прав администратора), создаёт ярлыки, запускает приложение.
#>
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repo = 'XYphrodite/MiningSwitch'
$zipUrl = "https://raw.githubusercontent.com/$repo/main/MiningSwitch.zip"

function Get-DetectedWorker {
    # Имя воркера берём из локального mining-fleet-agent; запасной вариант — имя ПК.
    try {
        $agentPath = Join-Path $env:ProgramFiles 'mining-fleet-agent\appsettings.json'
        if (-not (Test-Path -LiteralPath $agentPath)) { return $env:COMPUTERNAME }
        $agent = Get-Content -LiteralPath $agentPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $port = ([uri]$agent.Agent.ListenUrl).Port
        $headers = @{ 'X-Fleet-Token' = ([string]$agent.Agent.Token).Trim() }
        $cfg = Invoke-RestMethod -Uri ("http://127.0.0.1:{0}/api/v1/config" -f $port) -Headers $headers -TimeoutSec 6
        $name = [string]$cfg.workerName
        if (-not [string]::IsNullOrWhiteSpace($name)) { return $name }
    } catch { }
    return $env:COMPUTERNAME
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$dest = if ($isAdmin) { Join-Path $env:ProgramFiles 'MiningSwitch' } else { Join-Path $env:LOCALAPPDATA 'Programs\MiningSwitch' }

$tmp = Join-Path $env:TEMP ('miningswitch-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $tmp | Out-Null
    $zip = Join-Path $tmp 'MiningSwitch.zip'
    Write-Output "Скачиваю $zipUrl ..."
    Invoke-WebRequest -Uri $zipUrl -OutFile $zip -UseBasicParsing
    $stage = Join-Path $tmp 'app'
    Expand-Archive -LiteralPath $zip -DestinationPath $stage

    Write-Output "Устанавливаю в $dest ..."
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    Copy-Item -Path (Join-Path $stage '*') -Destination $dest -Recurse -Force

    $settingsPath = Join-Path $dest 'settings.json'
    if (-not (Test-Path -LiteralPath $settingsPath)) {
        $worker = Get-DetectedWorker
        @{ Computer = $env:COMPUTERNAME; PoolWorker = $worker } | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding UTF8
        Write-Output "Настройки: Computer=$env:COMPUTERNAME, PoolWorker=$worker"
    } else {
        Write-Output 'Настройки уже есть — не меняю.'
    }

    $ws = New-Object -ComObject WScript.Shell
    $exe = Join-Path $dest 'MiningSwitch.exe'
    $ico = Join-Path $dest 'MiningSwitch.ico'
    $desktopLink = $ws.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'MiningSwitch.lnk'))
    $desktopLink.TargetPath = $exe; $desktopLink.WorkingDirectory = $dest; $desktopLink.IconLocation = $ico; $desktopLink.Save()
    $startupLink = $ws.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Startup')) 'MiningSwitch.lnk'))
    $startupLink.TargetPath = $exe; $startupLink.Arguments = '-Tray'; $startupLink.WorkingDirectory = $dest; $startupLink.IconLocation = $ico; $startupLink.Save()
    Write-Output 'Ярлыки созданы (рабочий стол + автозагрузка).'

    $running = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -like '*MiningSwitch.ps1*' }
    if ($running) {
        Write-Output 'Приложение уже запущено.'
    } elseif ([Environment]::UserInteractive) {
        Start-Process -FilePath $exe
        Write-Output 'Mining Switch запущен.'
    } else {
        Write-Output 'Сессия неинтерактивна — приложение не запущено. Запусти ярлык MiningSwitch на рабочем столе.'
    }
    Write-Output 'Установка завершена.'
} finally {
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force }
}
