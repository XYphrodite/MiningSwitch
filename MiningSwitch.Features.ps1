#requires -Version 5.1
# Background operations. This module never changes miner wallets or credentials.
$ErrorActionPreference='Stop'
function Get-PublicJson([string]$Url) {
    if($Url -notmatch '^https://' -or $Url.Contains('"')){throw 'Invalid public API URL'}
    $curl=Join-Path $env:SystemRoot 'System32\curl.exe'
    if(Test-Path -LiteralPath $curl){
        $start=New-Object Diagnostics.ProcessStartInfo
        $start.FileName=$curl;$start.Arguments='--silent --show-error --fail --compressed --ipv4 --connect-timeout 4 --max-time 12 --url "'+$Url+'"'
        $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle='Hidden';$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
        $start.StandardOutputEncoding=New-Object Text.UTF8Encoding($false)
        $process=[Diagnostics.Process]::Start($start)
        try {
            $output=$process.StandardOutput.ReadToEndAsync();$errors=$process.StandardError.ReadToEndAsync()
            if(-not $process.WaitForExit(15000)){$process.Kill();throw 'Public API timeout'}
            if($process.ExitCode -ne 0){throw ('Public API unavailable: '+([uri]$Url).Host+' (code '+$process.ExitCode+')')}
            return ($output.GetAwaiter().GetResult() | ConvertFrom-Json)
        }finally{$process.Dispose()}
    }
    Add-Type -AssemblyName System.Net.Http
    $handler=New-Object Net.Http.HttpClientHandler
    $handler.UseProxy=$false
    $http=New-Object Net.Http.HttpClient($handler)
    $http.Timeout=[timespan]::FromSeconds(12)
    $http.DefaultRequestHeaders.UserAgent.ParseAdd('MiningSwitch/3.0')
    try {
        $task=$http.GetStringAsync($Url)
        if(-not $task.Wait(15000)){throw 'Public API timeout'}
        return ($task.GetAwaiter().GetResult() | ConvertFrom-Json)
    }finally{$http.Dispose()}
}

function Get-DataDirectory {
    $dir=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MiningSwitch'
    if(-not(Test-Path -LiteralPath $dir)){New-Item -Path $dir -ItemType Directory -Force | Out-Null}
    return $dir
}
function Write-AtomicJson($Value,[string]$Path) {
    $temp=$Path+'.new';$backup=$Path+'.bak'
    [IO.File]::WriteAllText($temp,($Value | ConvertTo-Json -Depth 10),(New-Object Text.UTF8Encoding($false)))
    if(Test-Path -LiteralPath $Path){[IO.File]::Replace($temp,$Path,$backup)}else{[IO.File]::Move($temp,$Path)}
}
function Read-SavedJson([string]$Path) {
    foreach($candidate in @($Path,($Path+'.bak'))){
        if(Test-Path -LiteralPath $candidate){try{return (Get-Content -LiteralPath $candidate -Raw -Encoding UTF8 | ConvertFrom-Json)}catch{}}
    }
    return $null
}
function Get-WorkerQuote($Pool,$Wallet,[string]$Worker) {
    $matches=@($Wallet.collectiveWorkers | Where-Object {$_.name -ceq $Worker})
    if($matches.Count -ne 1){throw 'Пул не нашёл отдельный воркер этого ПК. История сохранена; обновим позже.'}
    $w=$matches[0]
    # A reconnect can leave two live pool sessions for the same unique worker name.
    # Count the worker's aggregate once; expose the session count for a UI caveat.
    foreach($required in @('totalHashes','avg24hashRate')){if($null -eq $w.$required -or [double]$w.$required -lt 0){throw 'Неполная статистика воркера.'}}
    $diff=[double]$Pool.network_statistics.difficulty
    $divisor=[double]$Pool.config.sigDivisor
    $reward=[double]$Pool.pool_statistics.general.last10blocksAvgReward
    if($reward -le 0){$reward=[double]$Pool.network_statistics.value}
    $fee=[double]$Pool.config.pplns_fee
    $price=[double]$Pool.market.price_rub
    if($null -eq $Pool.config.pplns_fee){throw 'Нет данных о комиссии пула.'}
    foreach($number in @($diff,$divisor,$reward,$fee,$price,[double]$w.totalHashes,[double]$w.avg24hashRate)){
        if([double]::IsNaN($number) -or [double]::IsInfinity($number)){throw 'Некорректное числовое значение в статистике пула.'}
    }
    if($diff -le 0 -or $divisor -le 0 -or $reward -le 0 -or $fee -lt 0 -or $fee -ge 100 -or $price -le 0){throw 'Нет достоверных данных сети или курса XMR/RUB.'}
    $priceTime=[datetimeoffset]::Parse([string]$Pool.market.last_updated,[Globalization.CultureInfo]::InvariantCulture)
    if(([datetimeoffset]::UtcNow-$priceTime).TotalHours -gt 2 -or ($priceTime-[datetimeoffset]::UtcNow).TotalMinutes -gt 5){throw 'Курс XMR/RUB устарел. Сумма будет обновлена при получении свежего курса.'}
    $perHash=($reward/$divisor)/$diff*(1-$fee/100)
    [pscustomobject]@{Worker=$Worker;ActiveConnections=[int]$w.activeMiners;TotalHashes=[double]$w.totalHashes;Average24=[double]$w.avg24hashRate;RateNow=[double]$w.hashRate;XmrPerHash=$perHash;Xmr24=[double]$w.avg24hashRate*86400*$perHash;RubRate=$price;PriceTime=$priceTime.ToString('o');ValidShares=$w.validShares;PoolFee=$fee;FetchedUtc=[datetimeoffset]::UtcNow.ToString('o')}
}
function Update-EarningsLedger($State,$Quote,[string]$Identity,[datetimeoffset]$Now=[datetimeoffset]::UtcNow) {
    if(-not $State -or $State.Identity -ne $Identity -or $State.Schema -ne 1){
        $State=[pscustomobject]@{Schema=1;Identity=$Identity;StartedUtc=$Now.ToString('o');LastUtc=$Now.ToString('o');LastHashes=$Quote.TotalHashes;Xmr=0.0;CounterResets=0}
    } else {
        foreach($field in @('Xmr','LastHashes')){
            if($null -eq $State.$field -or [double]$State.$field -lt 0 -or [double]::IsNaN([double]$State.$field) -or [double]::IsInfinity([double]$State.$field)){throw 'История дохода повреждена. Автоматический сброс отключён.'}
        }
        $delta=[double]$Quote.TotalHashes-[double]$State.LastHashes
        if($delta -ge 0){$State.Xmr=[double]$State.Xmr+$delta*[double]$Quote.XmrPerHash}else{$State.CounterResets=[int]$State.CounterResets+1}
        $State.LastHashes=$Quote.TotalHashes;$State.LastUtc=$Now.ToString('o')
    }
    return $State
}
function Get-EarningsSnapshot([string]$Root) {
    [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
    $s=Get-Content (Join-Path $env:ProgramFiles 'mining-fleet-agent\appsettings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $listen=[uri]$s.Agent.ListenUrl
    $headers=@{'X-Fleet-Token'=([string]$s.Agent.Token).Trim()}
    $miner=Invoke-RestMethod ('http://127.0.0.1:{0}/api/v1/miner' -f $listen.Port) -Headers $headers -TimeoutSec 6
    $uri=[string]$miner.poolUrl
    if($uri -notmatch '://'){$uri='stratum+tcp://'+$uri}
    if(([uri]$uri).Host -notin @('pool.hashvault.pro','pool.hashvault.sh')){throw 'Для этого пула расчёт ещё не настроен.'}
    $prefs=Get-Content (Join-Path $Root 'settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if($prefs.Computer -ne $env:COMPUTERNAME){throw 'Учёт дохода привязан к другому компьютеру.'}
    $worker=[string]$prefs.PoolWorker
    if([string]::IsNullOrWhiteSpace($worker) -or [string]::IsNullOrWhiteSpace($miner.wallet)){throw 'Не настроен отдельный воркер ПК.'}
    # The fleet token is deliberately NOT sent to the public pool API.
    $pool=Get-PublicJson 'https://api.hashvault.pro/v3/monero/pool/stats'
    $url='https://api.hashvault.pro/v3/monero/wallet/{0}/stats?workers=true&chart=false&inactivityThreshold=10080' -f [uri]::EscapeDataString($miner.wallet)
    $wallet=Get-PublicJson $url
    $quote=Get-WorkerQuote $pool $wallet $worker
    $sha=[Security.Cryptography.SHA256]::Create()
    try{$identity=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($miner.wallet+'|'+$worker+'|'+$env:COMPUTERNAME)))).Replace('-','')}finally{$sha.Dispose()}
    $path=Join-Path (Get-DataDirectory) 'earnings.json'
    $oldState=Read-SavedJson $path
    if(-not $oldState -and ((Test-Path -LiteralPath $path) -or (Test-Path -LiteralPath ($path+'.bak')))){throw 'Файл истории повреждён. Автоматический сброс отключён.'}
    $state=Update-EarningsLedger $oldState $quote $identity
    Write-AtomicJson $state $path
    $fleetCpuRate=[double](($wallet.collectiveWorkers | Measure-Object avg24hashRate -Sum).Sum)
    $gpu=$null;$gpuError=$null
    try {
        $config=Invoke-RestMethod ('http://127.0.0.1:{0}/api/v1/config' -f $listen.Port) -Headers $headers -TimeoutSec 6
        $gpu=Get-GpuEarningsSnapshot $config.gpuMiner
    }catch{$gpuError='Нет достоверных данных GPU. Общая сумма не подменяется доходом CPU.'}
    [pscustomobject]@{Quote=$quote;TrackedXmr=$state.Xmr;TrackedRub=[double]$state.Xmr*$quote.RubRate;StartedUtc=$state.StartedUtc;CounterResets=$state.CounterResets;Estimated=$true;Gpu=$gpu;GpuError=$gpuError;FleetCpuRub24=$fleetCpuRate*86400*$quote.XmrPerHash*$quote.RubRate;CpuWorkers=@($wallet.collectiveWorkers).Count}
}
function Get-LuckyQuote($Pool,$Wallet,$Price,[string]$Worker) {
    $matches=@($Wallet.workers | Where-Object {$_.name -ceq $Worker})
    if($matches.Count -ne 1){throw 'Нет отдельной статистики GPU этого ПК.'}
    $w=$matches[0]
    if($Pool.config.symbol -ne 'XTM' -or $Pool.config.algo -ne 'Cuckaroo29'){throw 'Неожиданный алгоритм пула.'}
    $profit=[double]$Pool.stats.profit24hPer1Ghs;$units=[double]$Pool.config.coinUnits;$fee=[double]$Pool.config.fee
    $rate=[double]$Price.minotari.rub
    $priceTime=[datetimeoffset]::FromUnixTimeSeconds([long]$Price.minotari.last_updated_at)
    if(([datetimeoffset]::UtcNow-$priceTime).TotalHours -gt 2 -or ($priceTime-[datetimeoffset]::UtcNow).TotalMinutes -gt 5){throw 'Курс XTM/RUB устарел.'}
    foreach($value in @($profit,$units,$rate)){if($value -le 0 -or [double]::IsNaN($value) -or [double]::IsInfinity($value)){throw 'Неполная оценка GPU.'}}
    if($null -eq $Pool.config.fee -or $fee -lt 0 -or $fee -ge 100){throw 'Неизвестная комиссия GPU пула.'}
    $fleetRate=0.0
    foreach($item in @($Wallet.workers)){
        $average=$item.hashrateAvg.'24h'
        if($null -eq $average -or [double]$average -lt 0 -or [double]::IsNaN([double]$average) -or [double]::IsInfinity([double]$average)){throw 'Нет средней скорости GPU за сутки.'}
        $fleetRate+=[double]$average
    }
    # Same conversion as LuckyPool's own calculator: H/s / 1e9 * 1e6 * profit / coinUnits.
    # Deduct the advertised pool fee as a conservative estimate; no electricity is deducted.
    $coinsPerRateDay=$profit/1e9*1e6/$units*(1-$fee/100)
    # Accepted-share hashes are accumulated share difficulty, not graphs/second.
    # Value that work directly against block difficulty, independently of the pool's speed calculator.
    $difficulty=[double]$Pool.network.difficulty;$reward=[double]$Pool.network.reward/$units
    foreach($value in @($difficulty,$reward)){if($value -le 0 -or [double]::IsNaN($value) -or [double]::IsInfinity($value)){throw 'Нет параметров сети для учёта GPU.'}}
    $hashes=[double]$w.hashes
    $newWorker=$null -eq $w.hashes -and [double]$w.hashrateAvg.'24h' -eq 0 -and [double]$w.hashrate -eq 0 -and -not $w.lastShare
    if(($null -eq $w.hashes -and -not $newWorker) -or $hashes -lt 0 -or [double]::IsNaN($hashes) -or [double]::IsInfinity($hashes)){throw 'Нет счётчика работы GPU.'}
    [pscustomobject]@{Worker=$Worker;Average24=[double]$w.hashrateAvg.'24h';Coins24=[double]$w.hashrateAvg.'24h'*$coinsPerRateDay;Rub24=[double]$w.hashrateAvg.'24h'*$coinsPerRateDay*$rate;RubRate=$rate;PriceTime=$priceTime.ToString('o');FleetRub24=$fleetRate*$coinsPerRateDay*$rate;Workers=@($Wallet.workers).Count;TotalHashes=$hashes;XmrPerHash=$reward/$difficulty*(1-$fee/100)}
}
function Get-GpuEarningsSnapshot($Settings) {
    if($Settings.poolUrl -ne 'taric29.luckypool.io:3111'){throw 'Этот GPU пул не поддержан.'}
    $login=[string]$Settings.user
    if($login -notmatch '^([^.=/]+)\.([A-Za-z0-9_-]+)$'){throw 'Для отдельного учёта GPU нужно имя воркера.'}
    $address=$Matches[1];$worker=$Matches[2]
    if($worker -cne $env:COMPUTERNAME){throw 'GPU воркер не совпадает с ПК.'}
    $pool=Get-PublicJson 'https://taric29.luckypool.io/api/stats?v=2'
    $wallet=Get-PublicJson ('https://taric29.luckypool.io/api/stats_address?address='+[uri]::EscapeDataString($address))
    $price=Get-PublicJson 'https://api.coingecko.com/api/v3/simple/price?ids=minotari&vs_currencies=rub&include_last_updated_at=true'
    $quote=Get-LuckyQuote $pool $wallet $price $worker
    $sha=[Security.Cryptography.SHA256]::Create()
    try{$identity=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes('XTM|LuckyPool|'+$address+'|'+$worker)))).Replace('-','')}finally{$sha.Dispose()}
    $path=Join-Path (Get-DataDirectory) 'gpu-share-earnings.json'
    $old=Read-SavedJson $path
    if(-not $old -and ((Test-Path -LiteralPath $path) -or (Test-Path -LiteralPath ($path+'.bak')))){throw 'Повреждена история GPU.'}
    $state=Update-EarningsLedger $old $quote $identity
    Write-AtomicJson $state $path
    [pscustomobject]@{Quote=$quote;TrackedCoins=$state.Xmr;TrackedRub=[double]$state.Xmr*$quote.RubRate;StartedUtc=$state.StartedUtc;CounterResets=$state.CounterResets}
}
function Invoke-PowerCfg([string]$Arguments) {
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=Join-Path $env:SystemRoot 'System32\powercfg.exe';$start.Arguments=$Arguments
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle='Hidden'
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $process=[Diagnostics.Process]::Start($start)
    try {
        $output=$process.StandardOutput.ReadToEndAsync();$errorOutput=$process.StandardError.ReadToEndAsync()
        if(-not $process.WaitForExit(10000)){$process.Kill();throw 'Windows не ответила на команду питания за 10 секунд.'}
        if($process.ExitCode -ne 0){throw ('Не удалось изменить или прочитать схему питания: '+$errorOutput.GetAwaiter().GetResult().Trim())}
        return $output.GetAwaiter().GetResult()
    }finally{$process.Dispose()}
}
function Get-ActivePowerScheme {
    # Win32_PowerPlan denies access in a non-elevated interactive session on this PC.
    # powercfg reads the same active scheme without requiring administrator rights.
    $text=Invoke-PowerCfg '/getactivescheme'
    $match=[regex]::Match($text,'(?i)([0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})(?:\s+\((.+)\))?')
    if(-not $match.Success){throw 'Windows не вернула идентификатор активной схемы питания.'}
    $name=$match.Groups[2].Value
    if([string]::IsNullOrWhiteSpace($name)){$name=$match.Groups[1].Value}
    [pscustomobject]@{Id=$match.Groups[1].Value;Name=$name}
}
function Get-ResourceSnapshot {
    $os=Get-CimInstance Win32_OperatingSystem
    $cpu=Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'" | Select-Object -First 1
    $drives=@(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | Where-Object DeviceID -in @('C:','D:') | Select-Object DeviceID,FreeSpace,Size)
    $plan=Get-ActivePowerScheme
    $planName=switch -Regex ([string]$plan.Name){'^Ultimate Performance$'{'Максимальная производительность';break}'^High performance$'{'Высокая производительность';break}'^Balanced$'{'Сбалансированный';break}default{$plan.Name}}
    [pscustomobject]@{Cpu=[double]$cpu.PercentProcessorTime;RamUsedGiB=([double]$os.TotalVisibleMemorySize-[double]$os.FreePhysicalMemory)/1MB;RamTotalGiB=[double]$os.TotalVisibleMemorySize/1MB;Drives=$drives;PlanName=$planName;PlanId=$plan.Id;Time=(Get-Date).ToString('HH:mm:ss')}
}
function Get-CleanupRoots {
    $profile=[Environment]::GetFolderPath('LocalApplicationData')
    @((Join-Path $profile 'Temp'),(Join-Path $env:SystemRoot 'Temp'),'D:\Temp','D:\TMP') | Select-Object -Unique
}
function Assert-OrdinaryPath([string]$Path,[string]$Root) {
    $full=[IO.Path]::GetFullPath($Path);$base=[IO.Path]::GetFullPath($Root).TrimEnd('\')
    if(-not $full.StartsWith($base+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Путь за пределами выбранной временной папки.'}
    $current=$full
    while($current -and $current.Length -ge $base.Length){
        $item=Get-Item -LiteralPath $current -Force -ErrorAction Stop
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Ссылки и точки подключения пропускаются.'}
        if($current -eq $base){break};$current=[IO.Path]::GetDirectoryName($current)
    }
    return $full
}
function Find-CleanupCandidates([string[]]$Roots=(Get-CleanupRoots),[datetime]$Now=(Get-Date)) {
    $cutoff=$Now.AddDays(-7);$files=New-Object 'Collections.Generic.List[object]';$groups=New-Object 'Collections.Generic.List[object]'
    foreach($root in $Roots){
        if(-not(Test-Path -LiteralPath $root -PathType Container)){continue}
        $resolved=[IO.Path]::GetFullPath((Resolve-Path -LiteralPath $root).Path).TrimEnd('\')
        if((Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){continue}
        $stack=New-Object 'Collections.Generic.Stack[string]';$stack.Push($resolved);$bytes=[long]0;$count=0;$skipped=0
        while($stack.Count){
            $dir=$stack.Pop()
            try{$entries=@(Get-ChildItem -LiteralPath $dir -Force -ErrorAction Stop)}catch{$skipped++;continue}
            foreach($item in $entries){
                if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){continue}
                if($item.PSIsContainer){$stack.Push($item.FullName);continue}
                if($item.LastWriteTime -ge $cutoff -or $item.CreationTime -ge $cutoff){continue}
                $files.Add([pscustomobject]@{Path=$item.FullName;Root=$resolved;Length=$item.Length;LastWriteUtc=$item.LastWriteTimeUtc.ToString('o');CreationUtc=$item.CreationTimeUtc.ToString('o')});$count++;$bytes+=$item.Length
            }
        }
        $groups.Add([pscustomobject]@{Path=$resolved;Count=$count;Bytes=$bytes;Skipped=$skipped})
    }
    [pscustomobject]@{Files=$files.ToArray();Groups=$groups.ToArray();Bytes=[long](($files | Measure-Object Length -Sum).Sum);Count=$files.Count;ScannedAt=$Now.ToString('o')}
}
function Remove-CleanupCandidates($Scan,[string[]]$AllowedRoots=(Get-CleanupRoots)) {
    if(-not $Scan -or ((Get-Date)-[datetime]$Scan.ScannedAt).TotalMinutes -gt 15){throw 'Предпросмотр устарел. Повтори анализ.'}
    $allowed=@($AllowedRoots | ForEach-Object {[IO.Path]::GetFullPath($_).TrimEnd('\')})
    $removed=[long]0;$count=0;$skipped=0;$cutoff=(Get-Date).AddDays(-7)
    foreach($f in $Scan.Files){
        try {
            if($f.Root -notin $allowed){throw 'Папка отсутствует в списке разрешённых.'}
            $full=Assert-OrdinaryPath $f.Path $f.Root
            $item=Get-Item -LiteralPath $full -Force
            if($item.PSIsContainer -or $item.LastWriteTime -ge $cutoff -or $item.CreationTime -ge $cutoff -or $item.Length -ne $f.Length -or $item.LastWriteTimeUtc.ToString('o') -ne $f.LastWriteUtc -or $item.CreationTimeUtc.ToString('o') -ne $f.CreationUtc){throw 'Файл изменился после анализа.'}
            # Do not remove files another process currently holds open.
            $probe=[IO.File]::Open($full,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);$probe.Dispose()
            Remove-Item -LiteralPath $full -Force -ErrorAction Stop
            $removed+=$f.Length;$count++
        } catch {$skipped++}
    }
    [pscustomobject]@{RemovedBytes=$removed;RemovedCount=$count;Skipped=$skipped;FinishedAt=(Get-Date).ToString('o')}
}
function Set-PowerScheme([string]$Mode) {
    $path=Join-Path (Get-DataDirectory) 'power-backup.json'
    $current=Get-ActivePowerScheme
    $id=$current.Id
    if($Mode -eq 'Restore'){$saved=Read-SavedJson $path;$target=[string]$saved.Id}else{
        $target=switch($Mode){'Balanced'{'381b4222-f694-41f0-9685-ff5bb260df2e'}'Performance'{'8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c'}default{throw 'Неизвестная схема питания.'}}
        if(-not(Read-SavedJson $path)){Write-AtomicJson @{Id=$id;Name=$current.Name;SavedAt=(Get-Date).ToString('o')} $path}
    }
    if($target -notmatch '^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$'){throw 'Исходная схема ещё не сохранена.'}
    $null=Invoke-PowerCfg ('/setactive '+$target)
    $active=Get-ActivePowerScheme
    if($active.Id -ne $target){throw 'Изменение схемы не подтверждено.'}
    [pscustomobject]@{Name=$active.Name;Id=$target}
}
