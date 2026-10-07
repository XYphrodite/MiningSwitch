#requires -Version 5.1
[CmdletBinding()]
param([switch]$Tray,[switch]$SelfTest,[string]$PreviewPath)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Windows.Forms,System.Drawing,System.Net.Http
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run with powershell.exe -STA.' }
$script:mutex = New-Object Threading.Mutex($false, 'Local\MiningSwitch.UI')
$script:ownsMutex = $false
$script:showEvent = $null
if (-not $SelfTest -and -not $PreviewPath) {
    try { $script:ownsMutex = $script:mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $script:ownsMutex = $true }
    if (-not $script:ownsMutex) {
        $existingEvent=$null
        for($attempt=0;$attempt -lt 10 -and -not $existingEvent;$attempt++) {
            try {$existingEvent=[Threading.EventWaitHandle]::OpenExisting('Local\MiningSwitch.Show')} catch [Threading.WaitHandleCannotBeOpenedException] {Start-Sleep -Milliseconds 100}
        }
        if($existingEvent){$null=$existingEvent.Set();$existingEvent.Dispose()}else{[Windows.Forms.MessageBox]::Show('Приложение уже работает. Нажми на значок возле часов.','Mining Switch') | Out-Null}
        $script:mutex.Dispose();exit
    }
    $script:showEvent=New-Object Threading.EventWaitHandle($false,[Threading.EventResetMode]::AutoReset,'Local\MiningSwitch.Show')
}

[xml]$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Title="Mining Switch" Width="550" Height="630" MinWidth="500" MinHeight="590" WindowStartupLocation="CenterScreen" Background="#10141D" Foreground="#F1F5FA" FontFamily="Segoe UI" FontSize="14">
 <Window.Resources>
  <Style TargetType="Button">
   <Setter Property="Cursor" Value="Hand"/><Setter Property="BorderThickness" Value="0"/>
   <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
    <Border x:Name="Surface" Background="{TemplateBinding Background}" CornerRadius="20" Padding="20"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
    <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="Opacity" Value="0.88"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter TargetName="Surface" Property="Opacity" Value="0.48"/></Trigger></ControlTemplate.Triggers>
   </ControlTemplate></Setter.Value></Setter>
  </Style>
 </Window.Resources>
 <Grid Margin="30">
  <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
  <StackPanel>
   <TextBlock Text="MINING SWITCH" Foreground="#61E8BA" FontSize="12" FontWeight="Bold"/>
   <TextBlock Text="Твой ПК. Твой режим." FontSize="27" FontWeight="SemiBold" Margin="0,9,0,5"/>
   <TextBlock x:Name="ComputerLabel" Foreground="#929FB4" FontSize="12"/>
  </StackPanel>
  <Border Grid.Row="1" Background="#1B2331" CornerRadius="18" Padding="20" Margin="0,24,0,18">
   <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
    <StackPanel><TextBlock Text="XMRig · процессор" Foreground="#929FB4" FontSize="12"/><TextBlock x:Name="StatusLabel" Text="Подключение…" FontSize="23" FontWeight="SemiBold" Margin="0,6,0,0"/></StackPanel>
    <StackPanel Grid.Column="1" VerticalAlignment="Center" HorizontalAlignment="Right"><TextBlock x:Name="HashrateLabel" Text="—" FontSize="20" HorizontalAlignment="Right"/><TextBlock Text="H/s" Foreground="#929FB4" HorizontalAlignment="Right"/></StackPanel>
   </Grid>
  </Border>
  <Button x:Name="PowerButton" Grid.Row="2" Background="#61E8BA" Foreground="#10251E" IsEnabled="False" MinHeight="150" Margin="0,0,0,18">
   <StackPanel><TextBlock Text="&#xE7E8;" FontFamily="Segoe MDL2 Assets" FontSize="44" HorizontalAlignment="Center"/><TextBlock x:Name="PowerLabel" Text="Проверяем майнер" FontSize="23" FontWeight="SemiBold" Margin="0,14,0,0" HorizontalAlignment="Center"/></StackPanel>
  </Button>
  <Border Grid.Row="3" Background="#1B2331" CornerRadius="16" Padding="16">
   <StackPanel><TextBlock Text="Игра в приоритете" FontWeight="SemiBold"/><TextBlock x:Name="GameLabel" Text="Проверяем автоматическую паузу…" Foreground="#A7B4C8" TextWrapping="Wrap" Margin="0,6,0,0" FontSize="12"/></StackPanel>
  </Border>
  <TextBlock x:Name="NoteLabel" Grid.Row="4" Text="Крестик сворачивает окно к часам. Ручное выключение сохраняется после перезагрузки." Foreground="#929FB4" TextWrapping="Wrap" Margin="2,17,2,0" FontSize="12" MinHeight="36"/>
 </Grid>
</Window>
'@
$reader = New-Object Xml.XmlNodeReader $xaml
$script:window = [Windows.Markup.XamlReader]::Load($reader)
$reader.Close()
$iconPath=Join-Path $PSScriptRoot 'MiningSwitch.ico'
if(Test-Path -LiteralPath $iconPath){$window.Icon=New-Object Windows.Media.Imaging.BitmapImage([uri]$iconPath)}
foreach ($name in @('ComputerLabel','StatusLabel','HashrateLabel','PowerButton','PowerLabel','GameLabel','NoteLabel')) { Set-Variable -Scope Script -Name $name -Value $window.FindName($name) }
$ComputerLabel.Text = "$env:COMPUTERNAME  /  ЛОКАЛЬНОЕ УПРАВЛЕНИЕ"
$script:client = $null
$script:pending = $null
$script:queue = New-Object 'Collections.Generic.Queue[object]'
$script:running = $false
$script:online = $false
$script:busy = $false
$script:closing = $false
$script:lastPoll = [datetime]::MinValue
$script:lastConfig = [datetime]::MinValue
$script:manualOff = $false
$script:pauseDota = $false

function Connect-Agent {
    if ($script:client) { return }
    $path = Join-Path $env:ProgramFiles 'mining-fleet-agent\appsettings.json'
    $settings = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    $listen = [uri]$settings.Agent.ListenUrl
    if (-not $listen.IsAbsoluteUri -or $listen.Port -lt 1 -or $listen.Port -gt 65535) { throw 'Некорректный адрес локального агента.' }
    $token = [string]$settings.Agent.Token
    if ([string]::IsNullOrWhiteSpace($token)) { throw 'В настройках агента отсутствует ключ.' }
    $handler = New-Object Net.Http.HttpClientHandler
    $handler.UseProxy = $false
    $script:client = New-Object Net.Http.HttpClient($handler)
    $client.Timeout = [timespan]::FromSeconds(5)
    $client.DefaultRequestHeaders.Add('X-Fleet-Token', $token.Trim())
    $script:api = 'http://127.0.0.1:{0}/api/v1' -f $listen.Port
}
function Begin-Request([string]$Method,[string]$Path,$Body,[string]$Kind) {
    Connect-Agent
    $req = New-Object Net.Http.HttpRequestMessage((New-Object Net.Http.HttpMethod($Method)), "$script:api/$Path")
    if ($null -ne $Body) { $req.Content = New-Object Net.Http.StringContent(($Body | ConvertTo-Json -Depth 10 -Compress),[Text.Encoding]::UTF8,'application/json') }
    $script:pending = @{Task=$client.SendAsync($req); Request=$req; Kind=$Kind}
}
function Add-Operation([string]$Method,[string]$Path,$Body,[string]$Kind) { $script:queue.Enqueue(@{Method=$Method;Path=$Path;Body=$Body;Kind=$Kind}) }
function Set-Note([string]$Text,[bool]$ErrorState=$false) {
    $NoteLabel.Text = $Text
    $NoteLabel.Foreground = if($ErrorState){'#FFAB9E'}else{'#929FB4'}
}
function Refresh-Controls {
    $PowerButton.IsEnabled = $script:online -and -not $script:busy
    if ($script:trayIcon) {
        $startMenu.Enabled = $script:online -and -not $script:busy -and -not $script:running
        $stopMenu.Enabled = $script:online -and -not $script:busy
    }
    if ($script:busy) { $PowerLabel.Text = 'Применяем…'; return }
    if (-not $script:online) {
        $StatusLabel.Text = 'Нет связи с агентом'; $StatusLabel.Foreground = '#FFAB9E'
        $PowerLabel.Text = 'Ожидаем подключение'; $HashrateLabel.Text = '—'
        if($script:trayIcon){$trayIcon.Text='Mining Switch — нет связи';$trayIcon.Icon=$script:unknownIcon}
        return
    }
    if ($script:running) {
        $StatusLabel.Text = 'Майнинг работает'; $StatusLabel.Foreground = '#61E8BA'
        $PowerLabel.Text = 'Выключить майнинг'; $PowerButton.Background = '#61E8BA'
        if($script:trayIcon){$trayIcon.Text='Mining Switch — майнинг работает';$trayIcon.Icon=$script:activeIcon}
    } else {
        $StatusLabel.Text = if($script:manualOff){'Майнинг выключен'}else{'Майнинг на паузе'}
        $StatusLabel.Foreground = '#B8C9E3'; $PowerLabel.Text = if($script:manualOff){'Включить майнинг'}else{'Выключить майнинг'}; $PowerButton.Background = '#ABC4F7'
        if($script:trayIcon){$trayIcon.Text='Mining Switch — майнер остановлен';$trayIcon.Icon=$script:idleIcon}
    }
}
function Start-Control([bool]$Enable) {
    if ($script:busy -or -not $script:online) { return }
    if ($Enable -and (Get-Process -Name dota2,cs2 -ErrorAction SilentlyContinue)) {
        Set-Note 'Сначала закрой Dota / CS2. Майнинг не должен мешать игре.'; return
    }
    $script:busy = $true
    if ($Enable) {
        Add-Operation POST 'miner/start' $null 'start'
        Add-Operation PUT 'config' @{autoStartMiner=$true;minerWanted=$true} 'policy-on'
    } else {
        Add-Operation PUT 'config' @{autoStartMiner=$false;minerWanted=$false;minerStoppedByPause=$false;minerStoppedByThrottle=$false} 'policy-off'
        Add-Operation POST 'miner/stop' $null 'stop'
        Add-Operation PUT 'config' @{autoStartMiner=$false;minerWanted=$false;minerStoppedByPause=$false;minerStoppedByThrottle=$false} 'policy-off'
    }
    Add-Operation GET 'config' $null 'config'
    Add-Operation GET 'miner' $null 'done'
    Set-Note 'Отправляем команду локальному агенту…'
    Refresh-Controls
}
function Handle-Response($Data,[string]$Kind) {
    if ($Kind -in @('start','stop') -and $Data.ok -ne $true) { throw 'Агент не смог выполнить команду. Проверь его журнал.' }
    if ($Kind -eq 'config') {
        $script:manualOff = -not [bool]$Data.autoStartMiner -and -not [bool]$Data.minerWanted
        $script:pauseDota = @($Data.pauseWhile.processNames) -contains 'dota2' -or $Data.pauseWhile.processName -eq 'dota2'
        $GameLabel.Text = if($script:pauseDota){'Dota 2 → майнинг на паузе. После игры агент возобновит его, если ты не выключил майнинг вручную.'}else{'Автоматическая пауза для Dota 2 не настроена. Перед игрой выключи майнинг кнопкой.'}
        $script:lastConfig = Get-Date
        Refresh-Controls
    }
    if ($Kind -in @('poll','done')) {
        $script:online = $true
        $script:running = [bool]$Data.running
        $rate = $Data.hashrate10s
        $HashrateLabel.Text = if($running -and $null -ne $rate){'{0:N0}' -f [double]$rate}else{'—'}
        $script:lastPoll = Get-Date
        if($Kind -eq 'done') { $script:busy=$false; Set-Note 'Готово. Крестик сворачивает окно к часам. Ручное выключение сохраняется после перезагрузки.' }
        Refresh-Controls
    }
}
function Tick {
    try {
        if($script:showEvent -and $script:showEvent.WaitOne(0)){Show-Window}
        if ($script:pending -and $script:pending.Task.IsCompleted) {
            $operation = $script:pending; $script:pending = $null; $response = $null
            try {
                $response = $operation.Task.GetAwaiter().GetResult()
                if (-not $response.IsSuccessStatusCode) { throw ('Локальный агент ответил HTTP {0}.' -f [int]$response.StatusCode) }
                $payload = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                $data = if($payload){$payload | ConvertFrom-Json}else{$null}
                Handle-Response $data $operation.Kind
            } finally { if($response){$response.Dispose()}; $operation.Request.Dispose() }
        }
        if (-not $script:pending) {
            if($queue.Count -gt 0) { $op=$queue.Dequeue(); Begin-Request $op.Method $op.Path $op.Body $op.Kind }
            elseif(((Get-Date)-$script:lastConfig).TotalSeconds -ge 30) { Begin-Request GET config $null config }
            elseif(((Get-Date)-$script:lastPoll).TotalSeconds -ge 4) { Begin-Request GET miner $null poll }
        }
    } catch {
        $script:pending = $null; $queue.Clear(); $script:busy = $false; $script:online = $false
        $script:lastConfig=Get-Date; $script:lastPoll=Get-Date
        Set-Note ('Не удалось подтвердить состояние. {0}' -f $_.Exception.Message) $true
        Refresh-Controls
    }
}

if ($SelfTest) {
    try {
        Connect-Agent
        foreach($route in @('config','miner')) {
            $resp=$client.GetAsync("$api/$route").GetAwaiter().GetResult()
            try { $resp.EnsureSuccessStatusCode() | Out-Null; $value=$resp.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json; Handle-Response $value $(if($route -eq 'miner'){'poll'}else{'config'}) } finally {$resp.Dispose()}
        }
        [pscustomobject]@{UI='OK';AgentReachable=$online;MinerRunning=$running;DotaPause=$pauseDota;ManualOff=$manualOff;ControlsEnabled=$PowerButton.IsEnabled} | ConvertTo-Json
    } finally { if($client){$client.Dispose()}; $mutex.Dispose() }
    exit
}
if ($PreviewPath) {
    $StatusLabel.Text='Майнинг выключен';$StatusLabel.Foreground='#B8C9E3';$PowerLabel.Text='Включить майнинг';$PowerButton.IsEnabled=$true;$PowerButton.Background='#ABC4F7'
    $GameLabel.Text='Dota 2 → майнинг на паузе. После игры агент возобновит его, если ты не выключил майнинг вручную.'
    $window.Show(); $window.UpdateLayout()
    $bitmap=New-Object Windows.Media.Imaging.RenderTargetBitmap(550,630,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($window);$encoder=New-Object Windows.Media.Imaging.PngBitmapEncoder;$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream=[IO.File]::Create($PreviewPath);try{$encoder.Save($stream)}finally{$stream.Dispose();$window.Close();$mutex.Dispose()};exit
}

function New-PowerIcon([string]$Color) {
    # PNG-backed ICO avoids unmanaged HICON ownership and needs no compiled helper.
    $bitmap=New-Object Drawing.Bitmap(32,32);$g=[Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode='AntiAlias';$g.Clear([Drawing.Color]::FromArgb(16,20,29))
    $pen=New-Object Drawing.Pen([Drawing.ColorTranslator]::FromHtml($Color),3)
    $g.DrawArc($pen,7,7,18,18,-45,270);$g.DrawLine($pen,16,4,16,16)
    $png=New-Object IO.MemoryStream;$ico=New-Object IO.MemoryStream
    try {
        $bitmap.Save($png,[Drawing.Imaging.ImageFormat]::Png);$bytes=$png.ToArray();$writer=New-Object IO.BinaryWriter($ico)
        $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]1)
        $writer.Write([byte]32);$writer.Write([byte]32);$writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$bytes.Length);$writer.Write([uint32]22);$writer.Write($bytes);$writer.Flush();$ico.Position=0
        $icon=New-Object Drawing.Icon($ico);try{return $icon.Clone()}finally{$icon.Dispose()}
    } finally {$pen.Dispose();$g.Dispose();$bitmap.Dispose();$png.Dispose();$ico.Dispose()}
}
$script:activeIcon=New-PowerIcon '#61E8BA';$script:idleIcon=New-PowerIcon '#ABC4F7';$script:unknownIcon=New-PowerIcon '#FFAB9E'
$script:trayIcon=New-Object Windows.Forms.NotifyIcon
$trayIcon.Icon=$unknownIcon;$trayIcon.Text='Mining Switch — подключение'
$menu=New-Object Windows.Forms.ContextMenuStrip
$openMenu=$menu.Items.Add('Открыть Mining Switch')
$script:startMenu=$menu.Items.Add('Включить майнинг')
$script:stopMenu=$menu.Items.Add('Выключить майнинг')
$null=$menu.Items.Add('-');$exitMenu=$menu.Items.Add('Закрыть приложение')
$trayIcon.ContextMenuStrip=$menu;$trayIcon.Visible=$true
function Show-Window { $window.Show();$window.WindowState='Normal';$null=$window.Activate() }
$openMenu.Add_Click({Show-Window});$trayIcon.Add_DoubleClick({Show-Window})
$startMenu.Add_Click({Start-Control $true});$stopMenu.Add_Click({Start-Control $false})
$exitMenu.Add_Click({$script:closing=$true;$window.Close()})
$PowerButton.Add_Click({Start-Control (-not $script:running -and $script:manualOff)})
$window.Add_Closing({param($sender,$eventArgs) if(-not $script:closing){$eventArgs.Cancel=$true;$window.Hide()}})
$script:timer=New-Object Windows.Threading.DispatcherTimer
$timer.Interval=[timespan]::FromMilliseconds(200);$timer.Add_Tick({Tick});$timer.Start()
$app=New-Object Windows.Application;$app.ShutdownMode='OnExplicitShutdown'
$window.Add_Closed({$app.Shutdown()})
try {
    if(-not $Tray){$window.Show()}
    $null=$app.Run()
} finally {
    $timer.Stop();$trayIcon.Visible=$false;$trayIcon.Dispose();$menu.Dispose()
    $activeIcon.Dispose();$idleIcon.Dispose();$unknownIcon.Dispose()
    if($client){$client.Dispose()};if($script:pending){$script:pending.Request.Dispose()}
    if($script:showEvent){$script:showEvent.Dispose()}
    if($script:ownsMutex){$mutex.ReleaseMutex()};$mutex.Dispose()
}
