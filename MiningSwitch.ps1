#requires -Version 5.1
[CmdletBinding()]
param([switch]$Tray,[switch]$SelfTest,[string]$PreviewPath,[switch]$PreviewCare,[string]$SmokeReportPath)

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
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Title="Mining Switch" Width="960" Height="820" MinWidth="880" MinHeight="680" WindowStartupLocation="CenterScreen" Background="#10141D" Foreground="#F1F5FA" FontFamily="Segoe UI" FontSize="14">
 <Window.Resources>
  <Style TargetType="Button">
   <Setter Property="Cursor" Value="Hand"/><Setter Property="BorderThickness" Value="0"/>
   <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
    <Border x:Name="Surface" Background="{TemplateBinding Background}" CornerRadius="14" Padding="14,12"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
    <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="Opacity" Value="0.88"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter TargetName="Surface" Property="Opacity" Value="0.48"/></Trigger></ControlTemplate.Triggers>
   </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType="TabItem"><Setter Property="Foreground" Value="#F1F5FA"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabItem"><Border x:Name="TabSurface" Background="#1B2331" CornerRadius="12" Padding="22,12" Margin="0,0,10,14"><ContentPresenter x:Name="TabCaption" ContentSource="Header" TextElement.Foreground="#A7B4C8" TextElement.FontWeight="SemiBold"/></Border><ControlTemplate.Triggers><Trigger Property="IsSelected" Value="True"><Setter TargetName="TabSurface" Property="Background" Value="#61E8BA"/><Setter TargetName="TabCaption" Property="TextElement.Foreground" Value="#10251E"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
  <Style TargetType="ScrollBar"><Setter Property="Width" Value="8"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollBar"><Track x:Name="PART_Track" IsDirectionReversed="True" Orientation="Vertical" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" ViewportSize="{TemplateBinding ViewportSize}" Value="{Binding Value, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}"><Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType="Thumb"><Border Background="#41516B" CornerRadius="4" Margin="2,0"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb></Track></ControlTemplate></Setter.Value></Setter></Style>
  <Style x:Key="SmallButton" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}"><Setter Property="Background" Value="#2B384E"/><Setter Property="Foreground" Value="#F1F5FA"/><Setter Property="Margin" Value="0,0,8,0"/></Style>
 </Window.Resources>
 <Grid Margin="28,22">
  <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
  <StackPanel>
   <TextBlock Text="MINING SWITCH" Foreground="#61E8BA" FontSize="12" FontWeight="Bold"/>
   <TextBlock Text="Твой ПК. Всё под контролем." FontSize="27" FontWeight="SemiBold" Margin="0,7,0,4"/>
   <TextBlock x:Name="ComputerLabel" Foreground="#929FB4" FontSize="12"/>
  </StackPanel>
  <UniformGrid Grid.Row="1" Columns="3" Margin="0,18,0,16">
   <Border Background="#1B2331" CornerRadius="12" Padding="14" Margin="0,0,10,0"><StackPanel><TextBlock Text="ПРОЦЕССОР" FontSize="10" Foreground="#929FB4"/><TextBlock x:Name="CpuLabel" Text="—" FontSize="19" Margin="0,4,0,0"/></StackPanel></Border>
   <Border Background="#1B2331" CornerRadius="12" Padding="14" Margin="0,0,10,0"><StackPanel><TextBlock Text="ОПЕРАТИВНАЯ ПАМЯТЬ" FontSize="10" Foreground="#929FB4"/><TextBlock x:Name="RamLabel" Text="—" FontSize="19" Margin="0,4,0,0"/></StackPanel></Border>
   <Border Background="#1B2331" CornerRadius="12" Padding="14"><StackPanel><TextBlock Text="СВОБОДНО НА ДИСКАХ" FontSize="10" Foreground="#929FB4"/><TextBlock x:Name="DiskLabel" Text="—" FontSize="16" Margin="0,6,0,0"/></StackPanel></Border>
  </UniformGrid>
  <TabControl x:Name="MainTabs" Grid.Row="2" Background="Transparent" BorderThickness="0" Padding="0">
   <TabItem Header="Майнинг и доход">
    <ScrollViewer VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">
     <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="340"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
      <StackPanel Margin="0,0,18,0">
       <Border Background="#1B2331" CornerRadius="16" Padding="18"><StackPanel><TextBlock Text="XMRig · процессор" Foreground="#929FB4" FontSize="12"/><TextBlock x:Name="StatusLabel" Text="Подключение…" FontSize="22" FontWeight="SemiBold" Margin="0,6,0,10"/><StackPanel Orientation="Horizontal"><TextBlock x:Name="HashrateLabel" Text="—" FontSize="24"/><TextBlock Text=" H/s" Foreground="#929FB4" VerticalAlignment="Bottom" Margin="0,0,0,3"/></StackPanel></StackPanel></Border>
       <Border Background="#1B2331" CornerRadius="16" Padding="18" Margin="0,12,0,0"><StackPanel><TextBlock Text="lolMiner · видеокарта" Foreground="#929FB4" FontSize="12"/><TextBlock x:Name="GpuStatusLabel" Text="Проверяем GPU…" FontSize="18" FontWeight="SemiBold" Margin="0,6,0,6"/><TextBlock x:Name="GpuRateLabel" Text="—" FontSize="20"/><TextBlock x:Name="GpuDetailLabel" Text="" Foreground="#A7B4C8" FontSize="12" TextWrapping="Wrap" Margin="0,5,0,0"/></StackPanel></Border>
       <Button x:Name="PowerButton" Background="#61E8BA" Foreground="#10251E" IsEnabled="False" Height="136" Margin="0,14,0,14"><StackPanel><TextBlock Text="&#xE7E8;" FontFamily="Segoe MDL2 Assets" FontSize="32" HorizontalAlignment="Center"/><TextBlock x:Name="PowerLabel" Text="Проверяем майнеры" FontSize="19" FontWeight="SemiBold" Margin="0,14,0,0" HorizontalAlignment="Center"/></StackPanel></Button>
       <Border Background="#1B2331" CornerRadius="14" Padding="16"><StackPanel><TextBlock Text="Игра в приоритете" FontWeight="SemiBold"/><TextBlock x:Name="GameLabel" Text="Проверяем автоматическую паузу…" Foreground="#A7B4C8" TextWrapping="Wrap" Margin="0,6,0,0" FontSize="12"/></StackPanel></Border>
      </StackPanel>
      <Border Grid.Column="1" Background="#1B2331" CornerRadius="16" Padding="22" VerticalAlignment="Top"><StackPanel>
       <TextBlock Text="ТВОЙ ПК · CPU + GPU · ОЦЕНКА" Foreground="#61E8BA" FontSize="12" FontWeight="SemiBold"/>
       <TextBlock Text="Прогноз ₽/сутки · по средней скорости пула" Foreground="#A7B4C8" Margin="0,18,0,2" FontSize="12"/>
       <TextBlock x:Name="IncomeDayLabel" Text="— ₽" FontSize="42" FontWeight="SemiBold"/>
       <TextBlock x:Name="IncomeXmrLabel" Text="Загружаем статистику твоего воркера…" Foreground="#A7B4C8" FontSize="12" TextWrapping="Wrap" Margin="0,4,0,18"/>
       <Border Background="#293449" Height="1" Margin="0,0,0,14"/>
       <TextBlock Text="ВСЕ КОМПЬЮТЕРЫ · CPU + GPU" Foreground="#61E8BA" FontSize="12" FontWeight="SemiBold"/>
       <TextBlock x:Name="FleetIncomeLabel" Text="— ₽" FontSize="27" FontWeight="SemiBold" Margin="0,5,0,4"/>
       <TextBlock x:Name="FleetDetailsLabel" Text="Оценка за последние 24 часа по двум общим кошелькам." Foreground="#A7B4C8" FontSize="12" TextWrapping="Wrap" Margin="0,0,0,14"/>
       <Border Background="#293449" Height="1"/>
       <TextBlock x:Name="IncomeSinceLabel" Text="Накоплено с начала учёта" Foreground="#A7B4C8" Margin="0,17,0,4"/>
       <TextBlock x:Name="IncomeTotalLabel" Text="— ₽" FontSize="27" FontWeight="SemiBold"/>
       <TextBlock x:Name="IncomeSourceLabel" Text="HashVault · отдельная статистика ПК" Foreground="#929FB4" FontSize="12" Margin="0,10,0,10" TextWrapping="Wrap"/>
       <TextBlock Text="Прогноз использует среднюю скорость пула за 24 ч или доступную историю нового воркера. Это не начисления за прошедшие сутки. Накопление — оценка принятой работы с начала учёта. Электричество не вычитается." Foreground="#929FB4" FontSize="12" TextWrapping="Wrap"/>
       <Button x:Name="RefreshIncomeButton" Content="Обновить доход" Style="{StaticResource SmallButton}" Margin="0,16,0,0" HorizontalAlignment="Left"/>
       <TextBlock x:Name="IncomeNoteLabel" Foreground="#929FB4" FontSize="11" TextWrapping="Wrap" Margin="0,10,0,0"/>
      </StackPanel></Border>
     </Grid>
    </ScrollViewer>
   </TabItem>
   <TabItem Header="Уход за ПК">
    <ScrollViewer VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled"><StackPanel>
     <Border Background="#1B2331" CornerRadius="16" Padding="18" Margin="0,0,0,14"><StackPanel>
      <DockPanel><TextBlock Text="Очистка временных файлов" FontSize="20" FontWeight="SemiBold"/><TextBlock x:Name="CleanupSizeLabel" Text="—" FontSize="22" Foreground="#61E8BA" HorizontalAlignment="Right"/></DockPanel>
      <TextBlock Text="Файлы старше 7 дней в Temp пользователя, Windows и на диске D. Сначала анализ — затем удаление найденного." Foreground="#A7B4C8" TextWrapping="Wrap" FontSize="12" Margin="0,7,0,12"/>
      <TextBlock x:Name="CleanupDetailsLabel" Text="Нажми «Анализ», чтобы узнать, сколько места можно освободить." Foreground="#A7B4C8" TextWrapping="Wrap" Margin="0,0,0,12"/>
      <StackPanel Orientation="Horizontal"><Button x:Name="ScanButton" Content="Анализ" Style="{StaticResource SmallButton}"/><Button x:Name="CleanButton" Content="Удалить найденное" Style="{StaticResource SmallButton}" IsEnabled="False"/><Button x:Name="StorageButton" Content="Хранилище Windows" Style="{StaticResource SmallButton}"/></StackPanel>
     </StackPanel></Border>
     <Border Background="#1B2331" CornerRadius="16" Padding="18" Margin="0,0,0,14"><StackPanel>
      <TextBlock Text="Подготовить ПК к игре" FontSize="20" FontWeight="SemiBold"/>
      <TextBlock Text="Остановить CPU и GPU майнеры и запретить их автоматическое возобновление до твоей команды." Foreground="#A7B4C8" TextWrapping="Wrap" FontSize="12" Margin="0,7,0,12"/>
      <StackPanel Orientation="Horizontal"><Button x:Name="GameModeButton" Content="Включить игровой режим" Style="{StaticResource SmallButton}"/><Button x:Name="StartupButton" Content="Проверить автозагрузку" Style="{StaticResource SmallButton}"/></StackPanel>
     </StackPanel></Border>
     <Border Background="#1B2331" CornerRadius="16" Padding="18"><StackPanel>
      <TextBlock Text="Питание и производительность" FontSize="20" FontWeight="SemiBold"/>
      <TextBlock x:Name="PowerPlanLabel" Text="Читаем текущую схему…" Foreground="#A7B4C8" TextWrapping="Wrap" FontSize="12" Margin="0,7,0,12"/>
      <StackPanel Orientation="Horizontal"><Button x:Name="BalancedButton" Content="Сбалансированный" Style="{StaticResource SmallButton}"/><Button x:Name="PerformanceButton" Content="Производительность" Style="{StaticResource SmallButton}"/><Button x:Name="RestorePowerButton" Content="Вернуть прежнюю схему" Style="{StaticResource SmallButton}"/></StackPanel>
      <TextBlock Text="Высокая производительность может увеличить расход энергии и нагрев. Исходная схема сохраняется перед первым переключением." Foreground="#929FB4" TextWrapping="Wrap" FontSize="12" Margin="0,12,0,0"/>
     </StackPanel></Border>
     <TextBlock x:Name="CareNoteLabel" Foreground="#61E8BA" TextWrapping="Wrap" Margin="2,12,2,0"/>
    </StackPanel></ScrollViewer>
   </TabItem>
  </TabControl>
  <TextBlock x:Name="NoteLabel" Grid.Row="3" Text="Крестик сворачивает окно к часам. Ручное выключение сохраняется после перезагрузки." Foreground="#929FB4" TextWrapping="Wrap" Margin="2,15,2,0" FontSize="12" MinHeight="30"/>
 </Grid>
</Window>
'@
$reader = New-Object Xml.XmlNodeReader $xaml
$script:window = [Windows.Markup.XamlReader]::Load($reader)
$reader.Close()
$iconPath=Join-Path $PSScriptRoot 'MiningSwitch.ico'
if(Test-Path -LiteralPath $iconPath){$window.Icon=New-Object Windows.Media.Imaging.BitmapImage([uri]$iconPath)}
foreach ($name in @('ComputerLabel','StatusLabel','HashrateLabel','PowerButton','PowerLabel','GameLabel','NoteLabel','CpuLabel','RamLabel','DiskLabel','IncomeDayLabel','IncomeXmrLabel','IncomeSinceLabel','IncomeTotalLabel','IncomeSourceLabel','IncomeNoteLabel','RefreshIncomeButton','CleanupSizeLabel','CleanupDetailsLabel','ScanButton','CleanButton','StorageButton','GameModeButton','StartupButton','PowerPlanLabel','BalancedButton','PerformanceButton','RestorePowerButton','CareNoteLabel')) { Set-Variable -Scope Script -Name $name -Value $window.FindName($name) }
$ComputerLabel.Text = "$env:COMPUTERNAME  /  ЛОКАЛЬНОЕ УПРАВЛЕНИЕ"
$script:client = $null
$script:pending = $null
$script:queue = New-Object 'Collections.Generic.Queue[object]'
$script:running = $false
$script:gpuRunning = $false
$script:gpuKnown = $false
$script:cpuKnown = $false
$script:controlErrors = New-Object 'Collections.Generic.List[string]'
$script:controlEnable = $false
$script:lastGpuPoll = [datetime]::MinValue
foreach($name in @('GpuStatusLabel','GpuRateLabel','GpuDetailLabel')){Set-Variable -Scope Script -Name $name -Value $window.FindName($name)}
foreach($name in @('FleetIncomeLabel','FleetDetailsLabel')){Set-Variable -Scope Script -Name $name -Value $window.FindName($name)}
$script:online = $false
$script:busy = $false
$script:closing = $false
$script:lastPoll = [datetime]::MinValue
$script:lastConfig = [datetime]::MinValue
$script:manualOff = $false
$script:pauseDota = $false
$script:featureJob=$null
$script:featureQueue=New-Object 'Collections.Generic.Queue[object]'
$script:lastResources=[datetime]::MinValue
$script:lastIncome=[datetime]::MinValue
$script:cleanupScan=$null
$script:cleanupBusy=$false
$script:powerBusy=$false
$script:incomeSnapshot=$null
$script:gameRequested=$false
$script:featureModule=Join-Path $PSScriptRoot 'MiningSwitch.Features.ps1'

function Start-FeatureJob([string]$Kind,$Argument) {
    $ps=[Management.Automation.PowerShell]::Create()
    $null=$ps.AddScript({param($module,$kind,$arg,$root)
        $ErrorActionPreference='Stop'
        . $module
        switch($kind){
            'resources' {Get-ResourceSnapshot}
            'income' {Get-EarningsSnapshot $root}
            'scan' {Find-CleanupCandidates}
            'clean' {Remove-CleanupCandidates $arg}
            'power' {Set-PowerScheme $arg}
        }
    }).AddArgument($script:featureModule).AddArgument($Kind).AddArgument($Argument).AddArgument($PSScriptRoot)
    $script:featureJob=@{Shell=$ps;Handle=$ps.BeginInvoke();Kind=$Kind}
}
function Queue-Feature([string]$Kind,$Argument=$null){$script:featureQueue.Enqueue(@{Kind=$Kind;Argument=$Argument})}
function Format-Bytes([double]$Bytes){if($Bytes -ge 1GB){return ('{0:N2} ГБ' -f ($Bytes/1GB))};return ('{0:N1} МБ' -f ($Bytes/1MB))}
function Show-Income($Snapshot) {
    $q=$Snapshot.Quote
    $script:incomeSnapshot=$Snapshot
    $cpuRub=[double]$q.Xmr24*[double]$q.RubRate
    $IncomeSinceLabel.Text='Накоплено по журналам CPU + GPU'
    if($Snapshot.Gpu){
        $g=$Snapshot.Gpu.Quote
        $IncomeDayLabel.Text='≈ {0:N2} ₽' -f ($cpuRub+$g.Rub24)
        $IncomeXmrLabel.Text='CPU ≈ {0:N2} ₽  +  GPU ≈ {1:N2} ₽{2}{3:N0} H/s CPU · {4:N2} H/s GPU в среднем за 24 ч' -f $cpuRub,$g.Rub24,[Environment]::NewLine,$q.Average24,$g.Average24
        $IncomeTotalLabel.Text='≈ {0:N2} ₽' -f ($Snapshot.TrackedRub+$Snapshot.Gpu.TrackedRub)
        $IncomeSourceLabel.Text='CPU с {0:dd.MM HH:mm} · GPU с {1:dd.MM HH:mm}{2}XMR {3:N0} ₽ · XTM {4:N4} ₽{2}Курсы: {5:HH:mm} / {6:HH:mm}' -f ([datetimeoffset]::Parse($Snapshot.StartedUtc).LocalDateTime),([datetimeoffset]::Parse($Snapshot.Gpu.StartedUtc).LocalDateTime),[Environment]::NewLine,$q.RubRate,$g.RubRate,([datetimeoffset]::Parse($q.PriceTime).LocalDateTime),([datetimeoffset]::Parse($g.PriceTime).LocalDateTime)
        $FleetIncomeLabel.Text='≈ {0:N2} ₽ / сутки' -f ($Snapshot.FleetCpuRub24+$g.FleetRub24)
        $FleetDetailsLabel.Text='CPU ≈ {0:N2} ₽ + GPU ≈ {1:N2} ₽{2}Прогноз для всех воркеров кошельков: CPU {3}, GPU {4}. Включает твой ПК.' -f $Snapshot.FleetCpuRub24,$g.FleetRub24,[Environment]::NewLine,$Snapshot.CpuWorkers,$g.Workers
    }else{
        $IncomeDayLabel.Text='— ₽';$IncomeTotalLabel.Text='— ₽';$FleetIncomeLabel.Text='— ₽'
        $IncomeXmrLabel.Text='CPU ≈ {0:N2} ₽ · GPU: ожидаем данные пула' -f $cpuRub
        $IncomeSourceLabel.Text=$Snapshot.GpuError
        $FleetDetailsLabel.Text='CPU всех ПК ≈ {0:N2} ₽ / 24 ч. Общая сумма появится после получения данных GPU.' -f $Snapshot.FleetCpuRub24
    }
    $IncomeNoteLabel.Text='Обновлено {0:HH:mm}. История сохраняется на этом ПК.' -f (Get-Date)
    if($q.ActiveConnections -gt 1){$IncomeNoteLabel.Text+=' У CPU-воркера несколько подключений (возможно, после перезапуска). Его имя должно использоваться только этим ПК.'}
    if($Snapshot.CounterResets -gt 0 -or ($Snapshot.Gpu -and $Snapshot.Gpu.CounterResets -gt 0)){$IncomeNoteLabel.Text+=' Пул сбрасывал счётчик: часть работы могла не попасть в накопление.'}
    $IncomeNoteLabel.Foreground='#929FB4'
}
function Handle-FeatureResult([string]$Kind,$Result) {
    switch($Kind){
        'resources' {
            $CpuLabel.Text='{0:N0} % загрузка' -f $Result.Cpu
            $RamLabel.Text='{0:N1} / {1:N0} ГБ' -f $Result.RamUsedGiB,$Result.RamTotalGiB
            $DiskLabel.Text=(@($Result.Drives | ForEach-Object {'{0} {1:N0} ГБ' -f $_.DeviceID,($_.FreeSpace/1GB)}) -join '  ·  ')
            $PowerPlanLabel.Text='Текущая схема: '+$Result.PlanName
            $script:lastResources=Get-Date
        }
        'income' {Show-Income $Result;$script:lastIncome=Get-Date;$RefreshIncomeButton.IsEnabled=$true}
        'scan' {
            $script:cleanupScan=$Result;$script:cleanupBusy=$false
            $CleanupSizeLabel.Text=Format-Bytes $Result.Bytes
            $CleanupDetailsLabel.Text=(@($Result.Groups | ForEach-Object {'{0} — {1} файлов, {2}{3}' -f $_.Path,$_.Count,(Format-Bytes $_.Bytes),$(if($_.Skipped){' · часть папок недоступна'}else{''})}) -join [Environment]::NewLine)
            if($Result.Count -eq 0){$CleanupDetailsLabel.Text+=' · Подходящих файлов нет.'}
            $ScanButton.IsEnabled=$true;$CleanButton.IsEnabled=$Result.Count -gt 0
            $CareNoteLabel.Text='Анализ завершён. Удалятся только перечисленные временные файлы.'
        }
        'clean' {
            $script:cleanupScan=$null;$script:cleanupBusy=$false;$ScanButton.IsEnabled=$true;$CleanButton.IsEnabled=$false
            $CareNoteLabel.Text='Освобождено {0}; удалено файлов: {1}; пропущено изменённых, занятых или недоступных: {2}.' -f (Format-Bytes $Result.RemovedBytes),$Result.RemovedCount,$Result.Skipped
            $CleanupSizeLabel.Text=Format-Bytes $Result.RemovedBytes;$CleanupDetailsLabel.Text='Очистка завершена. Для новой очистки запусти анализ ещё раз.';$script:lastResources=[datetime]::MinValue
        }
        'power' {$script:powerBusy=$false;$CareNoteLabel.Text='Схема питания: '+$Result.Name;$PowerPlanLabel.Text='Текущая схема: '+$Result.Name;$BalancedButton.IsEnabled=$true;$PerformanceButton.IsEnabled=$true;$RestorePowerButton.IsEnabled=$true}
    }
}
function Tick-Features {
    if($script:featureJob -and $script:featureJob.Handle.IsCompleted){
        $job=$script:featureJob;$script:featureJob=$null
        try{
            $results=$job.Shell.EndInvoke($job.Handle)
            if($job.Shell.HadErrors){throw $job.Shell.Streams.Error[0].Exception}
            if($results.Count -eq 0){throw 'Операция не вернула результат.'}
            Handle-FeatureResult $job.Kind $results[$results.Count-1]
        }catch{
            switch($job.Kind){
                'income' {$script:lastIncome=Get-Date;$RefreshIncomeButton.IsEnabled=$true;$IncomeNoteLabel.Foreground='#FFAB9E';$message=$_.Exception.Message;$IncomeNoteLabel.Text=if($message -match '^(Пул |Это имя|Нет достоверных|Курс |Файл истории|История дохода|Учёт дохода|Для этого|Неполная статистика|Не настроен)'){$message}else{'Не удалось обновить данные пула.'};if($script:incomeSnapshot){$IncomeNoteLabel.Text+=' Показана последняя полученная оценка.'}else{$IncomeDayLabel.Text='Нет данных';$IncomeTotalLabel.Text='—';$IncomeXmrLabel.Text='Проверь доступность HashVault и настройки воркера.'}}
                'resources' {$script:lastResources=Get-Date;$CpuLabel.Text='Нет данных';$RamLabel.Text='Нет данных';$DiskLabel.Text='Нет данных'}
                default {$script:cleanupBusy=$false;$script:powerBusy=$false;$ScanButton.IsEnabled=$true;$CleanButton.IsEnabled=$false;$BalancedButton.IsEnabled=$true;$PerformanceButton.IsEnabled=$true;$RestorePowerButton.IsEnabled=$true;$CareNoteLabel.Text='Операция не завершена: '+$_.Exception.Message}
            }
        }finally{$job.Shell.Dispose()}
    }
    if(-not $script:featureJob){
        if($featureQueue.Count -gt 0){$next=$featureQueue.Dequeue();Start-FeatureJob $next.Kind $next.Argument}
        elseif(((Get-Date)-$script:lastResources).TotalSeconds -ge 12){Start-FeatureJob resources $null}
        elseif(((Get-Date)-$script:lastIncome).TotalMinutes -ge 5){$RefreshIncomeButton.IsEnabled=$false;Start-FeatureJob income $null}
    }
}
function Start-CleanupScan {
    if($script:cleanupBusy){return};$script:cleanupBusy=$true;$ScanButton.IsEnabled=$false;$CleanButton.IsEnabled=$false;$CareNoteLabel.Text='Проверяем временные папки…';Queue-Feature scan
}
function Start-Cleanup {
    if($script:cleanupBusy -or -not $script:cleanupScan){return}
    $script:cleanupBusy=$true;$ScanButton.IsEnabled=$false;$CleanButton.IsEnabled=$false;$CareNoteLabel.Text='Удаляем найденные старые временные файлы…';Queue-Feature clean $script:cleanupScan
}
function Start-PowerChange([string]$Mode){
    if($script:powerBusy){return};$script:powerBusy=$true;$BalancedButton.IsEnabled=$false;$PerformanceButton.IsEnabled=$false;$RestorePowerButton.IsEnabled=$false;$CareNoteLabel.Text='Меняем схему питания…';Queue-Feature power $Mode
}

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
    $client.Timeout = [timespan]::FromSeconds(20)
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
    $GameModeButton.IsEnabled=$PowerButton.IsEnabled
    if ($script:trayIcon) {
        $startMenu.Enabled = $script:online -and -not $script:busy -and (-not $script:running -or -not $script:gpuRunning)
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
        $PowerLabel.Text = 'Выключить CPU + GPU'; $PowerButton.Background = '#61E8BA'
        if($script:trayIcon){$trayIcon.Text='Mining Switch — майнинг работает';$trayIcon.Icon=$script:activeIcon}
    } else {
        $StatusLabel.Text = if($script:manualOff){'Майнинг выключен'}else{'Майнинг на паузе'}
        $StatusLabel.Foreground = '#B8C9E3'; $PowerLabel.Text = if($script:manualOff -and -not $script:gpuRunning){'Включить CPU + GPU'}else{'Выключить CPU + GPU'}; $PowerButton.Background = '#ABC4F7'
        if($script:trayIcon){$trayIcon.Text='Mining Switch — майнер остановлен';$trayIcon.Icon=$script:idleIcon}
    }
    if($script:trayIcon -and $script:gpuRunning){$trayIcon.Text='Mining Switch — GPU работает';$trayIcon.Icon=$script:activeIcon}
}
function Start-Control([bool]$Enable) {
    if ($script:busy -or -not $script:online) { return }
    if ($Enable -and (Get-Process -Name dota2,cs2 -ErrorAction SilentlyContinue)) {
        Set-Note 'Сначала закрой Dota / CS2. Майнинг не должен мешать игре.'; return
    }
    $script:busy = $true
    $script:controlEnable=$Enable
    $script:controlErrors.Clear()
    if ($Enable) {
        Add-Operation PUT 'config' @{autoStartMiner=$true;minerWanted=$true;minerStoppedByPause=$false;minerStoppedByThrottle=$false;gpuMiner=@{enabled=$true};gpuWanted=$true;gpuStoppedByPause=$false} 'policy-on'
        if(-not $script:running){Add-Operation POST 'miner/start' $null 'start'}
        if(-not $script:gpuRunning){Add-Operation POST 'gpu/start' $null 'gpu-start'}
    } else {
        $off=@{autoStartMiner=$false;minerWanted=$false;minerStoppedByPause=$false;minerStoppedByThrottle=$false;gpuMiner=@{enabled=$false};gpuWanted=$false;gpuStoppedByPause=$false}
        Add-Operation PUT 'config' $off 'policy-off'
        Add-Operation POST 'gpu/stop' $null 'gpu-stop'
        Add-Operation POST 'miner/stop' $null 'stop'
        Add-Operation PUT 'config' $off 'policy-off'
    }
    $script:cpuKnown=$false;$script:gpuKnown=$false
    Add-Operation GET 'config' $null 'config'
    Add-Operation GET 'miner' $null 'poll'
    Add-Operation GET 'gpu' $null 'done'
    Set-Note 'Отправляем команду локальному агенту…'
    Refresh-Controls
}
function Handle-Response($Data,[string]$Kind) {
    if ($Kind -in @('start','stop','gpu-start','gpu-stop') -and $Data.ok -ne $true) { $script:controlErrors.Add("Не подтверждена команда $Kind.") }
    if ($Kind -eq 'config') {
        $script:manualOff = -not [bool]$Data.autoStartMiner -and -not [bool]$Data.minerWanted -and -not [bool]$Data.gpuWanted -and -not [bool]$Data.gpuMiner.enabled -and -not [bool]$Data.gpuStoppedByPause -and -not [bool]$Data.minerStoppedByPause -and -not [bool]$Data.minerStoppedByThrottle
        $script:pauseDota = (@($Data.pauseWhile.processNames) -contains 'dota2' -or $Data.pauseWhile.processName -eq 'dota2') -and (@($Data.gpuMiner.pauseWhile.processNames) -contains 'dota2' -or $Data.gpuMiner.pauseWhile.processName -eq 'dota2')
        $GameLabel.Text = if($script:pauseDota){'Dota 2 → CPU и GPU на паузе. Ручное выключение запрещает возобновление до твоей команды.'}else{'Перед игрой выключи CPU и GPU общей кнопкой.'}
        $script:lastConfig = Get-Date
        Refresh-Controls
    }
    if ($Kind -eq 'poll') {
        $script:online = $true
        $script:cpuKnown = $true
        $script:running = [bool]$Data.running
        $rate = $Data.hashrate10s
        $HashrateLabel.Text = if($running -and $null -ne $rate){'{0:N0}' -f [double]$rate}else{'—'}
        $script:lastPoll = Get-Date
        Refresh-Controls
    }
    if($Kind -in @('gpu-poll','done')){
        $script:gpuKnown=$true;$script:gpuRunning=[bool]$Data.running;$script:lastGpuPoll=Get-Date
        $GpuStatusLabel.Text=if($script:gpuRunning){'GPU майнит'}else{'GPU остановлен'}
        $GpuStatusLabel.Foreground=if($script:gpuRunning){'#61E8BA'}else{'#B8C9E3'}
        $GpuRateLabel.Text=if($script:gpuRunning -and $null -ne $Data.hashrate){'{0:N2} {1}' -f [double]$Data.hashrate,$Data.hashrateUnit}else{'—'}
        $device=@($Data.devices) | Select-Object -First 1
        $GpuDetailLabel.Text=([string]$Data.algorithm)+$(if($null -ne $device.temperatureC){' · {0:N0} °C' -f [double]$device.temperatureC})
        if($Kind -eq 'done'){Complete-Control}
        Refresh-Controls
    }
}
function Complete-Control {
    $script:busy=$false
    $confirmed=$script:cpuKnown -and $script:gpuKnown -and $(if($script:controlEnable){$script:running -and $script:gpuRunning}else{-not $script:running -and -not $script:gpuRunning -and $script:manualOff})
    if($confirmed){Set-Note $(if($script:controlEnable){'CPU и GPU запущены. Общая кнопка выключает оба майнера.'}else{'CPU и GPU выключены. Автозапуск отключён до твоей команды.'})}
    else{Set-Note ('Команда выполнена не полностью. Проверь статусы CPU и GPU. '+($script:controlErrors -join ' ')) $true}
    if($script:gameRequested){$CareNoteLabel.Text=if($confirmed){'Игровой режим включён. CPU и GPU остановлены до твоей команды.'}else{'Остановка обоих майнеров не подтверждена. Повтори выключение.'};$script:gameRequested=$false}
}
function Tick {
    try{Tick-Features}catch{$IncomeNoteLabel.Text='Не удалось запустить фоновую проверку.'}
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
            } catch {
                if(-not $script:busy){throw}
                $script:controlErrors.Add('Ошибка '+$operation.Kind+'.')
                if($operation.Kind -eq 'done'){Complete-Control}
            } finally { if($response){$response.Dispose()}; $operation.Request.Dispose() }
        }
        if (-not $script:pending) {
            if($queue.Count -gt 0) { $op=$queue.Dequeue(); Begin-Request $op.Method $op.Path $op.Body $op.Kind }
            elseif(((Get-Date)-$script:lastConfig).TotalSeconds -ge 30) { Begin-Request GET config $null config }
            elseif(((Get-Date)-$script:lastPoll).TotalSeconds -ge 4) { Begin-Request GET miner $null poll }
            elseif(((Get-Date)-$script:lastGpuPoll).TotalSeconds -ge 4) { Begin-Request GET gpu $null gpu-poll }
        }
    } catch {
        $script:pending = $null; $queue.Clear(); $script:busy = $false; $script:online = $false
        $script:lastConfig=Get-Date; $script:lastPoll=Get-Date
        Set-Note ('Не удалось подтвердить состояние. {0}' -f $_.Exception.Message) $true
        if($script:gameRequested){$CareNoteLabel.Text='Игровой режим не подтверждён. Проверь соединение с агентом.';$script:gameRequested=$false}
        Refresh-Controls
    }
}

if ($SelfTest) {
    try {
        Connect-Agent
        foreach($route in @('config','miner','gpu')) {
            $resp=$client.GetAsync("$api/$route").GetAwaiter().GetResult()
            try { $resp.EnsureSuccessStatusCode() | Out-Null; $value=$resp.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json; Handle-Response $value $(if($route -eq 'miner'){'poll'}elseif($route -eq 'gpu'){'gpu-poll'}else{'config'}) } finally {$resp.Dispose()}
        }
        [pscustomobject]@{UI='OK';AgentReachable=$online;MinerRunning=$running;DotaPause=$pauseDota;ManualOff=$manualOff;ControlsEnabled=$PowerButton.IsEnabled} | ConvertTo-Json
        . $script:featureModule
        $snapshot=Get-EarningsSnapshot $PSScriptRoot
        [pscustomobject]@{Earnings='OK';Worker=$snapshot.Quote.Worker;Estimated=$snapshot.Estimated;Rub24=($snapshot.Quote.Xmr24*$snapshot.Quote.RubRate);TrackedRub=$snapshot.TrackedRub;Resources=(Get-ResourceSnapshot)} | ConvertTo-Json -Depth 4
    } finally { if($client){$client.Dispose()}; $mutex.Dispose() }
    exit
}
if ($PreviewPath) {
    try{
        Connect-Agent
        foreach($route in @('config','miner','gpu')){
            $resp=$client.GetAsync("$api/$route").GetAwaiter().GetResult()
            try{$resp.EnsureSuccessStatusCode()|Out-Null;$value=$resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()|ConvertFrom-Json;Handle-Response $value $(if($route -eq 'miner'){'poll'}elseif($route -eq 'gpu'){'gpu-poll'}else{'config'})}finally{$resp.Dispose()}
        }
    }catch{$NoteLabel.Text='Не удалось прочитать локальный статус.'}finally{if($client){$client.Dispose();$script:client=$null}}
    . $script:featureModule
    try{Handle-FeatureResult resources (Get-ResourceSnapshot);Show-Income (Get-EarningsSnapshot $PSScriptRoot)}catch{$IncomeNoteLabel.Text='Статистика пула временно недоступна.'}
    if($PreviewCare){$window.FindName('MainTabs').SelectedIndex=1;Handle-FeatureResult scan (Find-CleanupCandidates)}
    $window.Show(); $window.UpdateLayout()
    $bitmap=New-Object Windows.Media.Imaging.RenderTargetBitmap(960,820,96,96,[Windows.Media.PixelFormats]::Pbgra32)
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
$PowerButton.Add_Click({Start-Control (-not $script:running -and -not $script:gpuRunning -and $script:manualOff)})
$GameModeButton.Add_Click({$script:gameRequested=$true;Start-Control $false;$CareNoteLabel.Text='Применяем игровой режим: останавливаем майнер…'})
$RefreshIncomeButton.Add_Click({$RefreshIncomeButton.IsEnabled=$false;Queue-Feature income})
$ScanButton.Add_Click({Start-CleanupScan});$CleanButton.Add_Click({Start-Cleanup})
$BalancedButton.Add_Click({Start-PowerChange Balanced});$PerformanceButton.Add_Click({Start-PowerChange Performance});$RestorePowerButton.Add_Click({Start-PowerChange Restore})
$StorageButton.Add_Click({try{Start-Process 'ms-settings:storagesense'}catch{$CareNoteLabel.Text='Не удалось открыть параметры хранилища.'}})
$StartupButton.Add_Click({try{Start-Process 'ms-settings:startupapps'}catch{$CareNoteLabel.Text='Не удалось открыть параметры автозагрузки.'}})
$window.Add_Closing({param($sender,$eventArgs) if(-not $script:closing){$eventArgs.Cancel=$true;$window.Hide()}})
$script:timer=New-Object Windows.Threading.DispatcherTimer
$timer.Interval=[timespan]::FromMilliseconds(200);$timer.Add_Tick({Tick});$timer.Start()
$script:smokeTimer=$null
if($SmokeReportPath){
    $script:smokeStarted=Get-Date
    $script:smokeTimer=New-Object Windows.Threading.DispatcherTimer
    $smokeTimer.Interval=[timespan]::FromSeconds(1)
    $smokeTimer.Add_Tick({
        if($script:incomeSnapshot -and -not $script:cleanupBusy -and -not $script:cleanupScan){Start-CleanupScan}
        if(($script:online -and $script:incomeSnapshot -and $script:cleanupScan) -or ((Get-Date)-$script:smokeStarted).TotalSeconds -gt 45){
            $ok=$script:online -and $null -ne $script:incomeSnapshot -and $null -ne $script:cleanupScan
            [pscustomobject]@{Passed=$ok;MinerStatus=$StatusLabel.Text;IncomeDay=$IncomeDayLabel.Text;IncomeTotal=$IncomeTotalLabel.Text;CPU=$CpuLabel.Text;RAM=$RamLabel.Text;Disks=$DiskLabel.Text;ScanCount=$script:cleanupScan.Count;IncomeNote=$IncomeNoteLabel.Text;CareNote=$CareNoteLabel.Text} | ConvertTo-Json | Set-Content -LiteralPath $SmokeReportPath -Encoding UTF8
            $smokeTimer.Stop();$script:closing=$true;$window.Close()
        }
    });$smokeTimer.Start()
}
$app=New-Object Windows.Application;$app.ShutdownMode='OnExplicitShutdown'
$window.Add_Closed({$app.Shutdown()})
try {
    if(-not $Tray){$window.Show()}
    $null=$app.Run()
} finally {
    $timer.Stop();$trayIcon.Visible=$false;$trayIcon.Dispose();$menu.Dispose()
    if($script:smokeTimer){$smokeTimer.Stop()}
    $activeIcon.Dispose();$idleIcon.Dispose();$unknownIcon.Dispose()
    if($client){$client.Dispose()};if($script:pending){$script:pending.Request.Dispose()}
    if($script:featureJob){try{$script:featureJob.Shell.Stop()}catch{};$script:featureJob.Shell.Dispose()}
    if($script:showEvent){$script:showEvent.Dispose()}
    if($script:ownsMutex){$mutex.ReleaseMutex()};$mutex.Dispose()
}
