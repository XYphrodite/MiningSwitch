#requires -Version 5.1
<#
    Установщик Mining Switch.
    Запуск (PowerShell 5.1, для Program Files нужны права администратора):

        powershell -c "irm https://raw.githubusercontent.com/XYphrodite/MiningSwitch/main/install.ps1 | iex"

    Что делает: скачивает MiningSwitch.zip с GitHub, устанавливает в
    C:\Program Files\MiningSwitch (или %LOCALAPPDATA%\Programs\MiningSwitch без
    прав администратора), создаёт ярлыки, запускает приложение.
    Требуется .NET 10 Desktop Runtime (есть на ПК с mining-fleet-agent).
#>
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repo = 'XYphrodite/MiningSwitch'
$zipUrl = "https://raw.githubusercontent.com/$repo/main/MiningSwitch.zip"

$desktopRuntime = & dotnet --list-runtimes 2>$null |
    Where-Object { $_ -like 'Microsoft.WindowsDesktop.App 10.*' }
if (-not $desktopRuntime) {
    throw '.NET 10 Desktop Runtime не найден. Установи его: https://dotnet.microsoft.com/download/dotnet/10.0'
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

    $ws = New-Object -ComObject WScript.Shell
    $exe = Join-Path $dest 'MiningSwitch.exe'
    $desktopLink = $ws.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'MiningSwitch.lnk'))
    $desktopLink.TargetPath = $exe; $desktopLink.WorkingDirectory = $dest; $desktopLink.Save()
    $startupLink = $ws.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Startup')) 'MiningSwitch.lnk'))
    $startupLink.TargetPath = $exe; $startupLink.Arguments = '-Tray'; $startupLink.WorkingDirectory = $dest; $startupLink.Save()
    Write-Output 'Ярлыки созданы (рабочий стол + автозагрузка).'

    if ([Environment]::UserInteractive) {
        Start-Process -FilePath $exe
        Write-Output 'Mining Switch запущен.'
    } else {
        Write-Output 'Сессия неинтерактивна — приложение не запущено. Запусти ярлык MiningSwitch на рабочем столе.'
    }
    Write-Output 'Установка завершена.'
} finally {
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force }
}
