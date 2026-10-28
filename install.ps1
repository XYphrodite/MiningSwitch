#requires -Version 5.1
<#
    Установщик и обновлятор Mining Switch.
    Запуск (PowerShell 5.1):

        powershell -c "irm https://raw.githubusercontent.com/XYphrodite/MiningSwitch/main/install.ps1 | iex"

    Скачивает последний релиз MiningSwitch.zip с GitHub Releases, проверяет SHA-256 и
    работоспособность бинарника (--help), останавливает запущенный экземпляр (если был),
    устанавливает в %LOCALAPPDATA%\Programs\MiningSwitch (чтобы дальнейшее автообновление
    через SelfUpdateKit работало в один клик без UAC), создаёт ярлыки и запускает приложение.
    Требуется .NET 10 Desktop Runtime (есть на всех ПК с mining-fleet-agent).
#>
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\MiningSwitch'),
    [string] $Version = 'latest'
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repo = 'XYphrodite/MiningSwitch'
$asset = 'MiningSwitch.zip'
$headers = @{ 'User-Agent' = 'miningswitch-installer/4.2' }

$desktopRuntime = & dotnet --list-runtimes 2>$null |
    Where-Object { $_ -like 'Microsoft.WindowsDesktop.App 10.*' }
if (-not $desktopRuntime) {
    throw '.NET 10 Desktop Runtime не найден. Установи его: https://dotnet.microsoft.com/download/dotnet/10.0'
}

if ($Version -eq 'latest') {
    $zipUrl = "https://github.com/$repo/releases/latest/download/$asset"
    $shaUrl = "https://github.com/$repo/releases/latest/download/$asset.sha256"
} else {
    $tag = if ($Version -match '^v') { $Version } else { "v$Version" }
    $zipUrl = "https://github.com/$repo/releases/download/$tag/$asset"
    $shaUrl = "https://github.com/$repo/releases/download/$tag/$asset.sha256"
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('miningswitch-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $tmp | Out-Null
    $zip = Join-Path $tmp $asset
    $sha = Join-Path $tmp "$asset.sha256"

    Write-Output "Скачиваю $zipUrl ..."
    Invoke-WebRequest -Uri $zipUrl -OutFile $zip -UseBasicParsing -Headers $headers
    Invoke-WebRequest -Uri $shaUrl -OutFile $sha -UseBasicParsing -Headers $headers

    $shaText = [IO.File]::ReadAllText($sha)
    if ($shaText -notmatch '([0-9a-fA-F]{64})') {
        throw 'Не удалось прочитать контрольную сумму SHA-256 релиза.'
    }
    $expectedHash = $Matches[1].ToLowerInvariant()
    $actualHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "Не совпал SHA-256 архива: ожидался $expectedHash, получен $actualHash."
    }
    Write-Output 'SHA-256 проверен.'

    $stage = Join-Path $tmp 'app'
    Expand-Archive -LiteralPath $zip -DestinationPath $stage
    $stagedExe = Join-Path $stage 'MiningSwitch.exe'
    if (-not (Test-Path -LiteralPath $stagedExe)) {
        throw 'В архиве релиза не найден MiningSwitch.exe.'
    }

    # Пробный запуск без окна перед заменой установленного приложения.
    $probe = Start-Process -FilePath $stagedExe -ArgumentList '--help' -Wait -PassThru -WindowStyle Hidden
    if ($probe.ExitCode -ne 0) {
        throw "Проверка нового MiningSwitch.exe не прошла (код $($probe.ExitCode))."
    }

    $running = @(Get-Process -Name 'MiningSwitch' -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        Write-Output 'Останавливаю запущенный Mining Switch перед обновлением...'
        $running | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    }

    Write-Output "Устанавливаю в $InstallDir ..."
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Get-ChildItem -LiteralPath $InstallDir -Filter 'MiningSwitch.exe.old-*' -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
    Copy-Item -Path (Join-Path $stage '*') -Destination $InstallDir -Recurse -Force

    $ws = New-Object -ComObject WScript.Shell
    $exe = Join-Path $InstallDir 'MiningSwitch.exe'
    $desktopLink = $ws.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'MiningSwitch.lnk'))
    $desktopLink.TargetPath = $exe; $desktopLink.WorkingDirectory = $InstallDir; $desktopLink.Save()
    $startupLink = $ws.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Startup')) 'MiningSwitch.lnk'))
    $startupLink.TargetPath = $exe; $startupLink.Arguments = '-Tray'; $startupLink.WorkingDirectory = $InstallDir; $startupLink.Save()
    Write-Output 'Ярлыки обновлены (рабочий стол + автозагрузка).'

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
