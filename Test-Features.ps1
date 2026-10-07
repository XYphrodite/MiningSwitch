#requires -Version 5.1
[CmdletBinding()]
param([string]$Root=$PSScriptRoot)
$ErrorActionPreference='Stop'
. (Join-Path $Root 'MiningSwitch.Features.ps1')
function Assert([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
function Assert-Throws([scriptblock]$Code,[string]$Message){$caught=$false;try{& $Code}catch{$caught=$true};Assert $caught $Message}
$pool=[pscustomobject]@{network_statistics=@{difficulty=1000000;value=600000000000};pool_statistics=@{general=@{last10blocksAvgReward=600000000000}};config=@{sigDivisor=1e12;pplns_fee=0.9};market=@{price_rub=40000;last_updated=[datetimeoffset]::UtcNow.ToString('o')}}
$wallet=[pscustomobject]@{revenue=@{totalPaid=9999999999999999};collectiveWorkers=@([pscustomobject]@{name='other-pc';activeMiners=1;totalHashes=999999999;avg24hashRate=999999;hashRate=999999;validShares=100000},[pscustomobject]@{name='this-pc';activeMiners=1;totalHashes=1000;avg24hashRate=100;hashRate=120;validShares=3})}
$quote=Get-WorkerQuote $pool $wallet 'this-pc'
$expected=100*86400*0.6/1000000*0.991
Assert ([math]::Abs($quote.Xmr24-$expected) -lt 1e-12) 'Incorrect per-PC estimate or pool fee'
$state=Update-EarningsLedger $null $quote 'wallet-A'
Assert ($state.Xmr -eq 0) 'First sample must be a baseline, not invented historical earnings'
$quote.TotalHashes=1100;$state=Update-EarningsLedger $state $quote 'wallet-A'
Assert ([math]::Abs($state.Xmr-100*$quote.XmrPerHash) -lt 1e-12) 'Delta ledger incorrect'
$saved=$state.Xmr;$state=Update-EarningsLedger $state $quote 'wallet-A'
Assert ($state.Xmr -eq $saved) 'Duplicate observation must not count twice'
$quote.TotalHashes=5;$state=Update-EarningsLedger $state $quote 'wallet-A'
Assert ($state.Xmr -eq $saved -and $state.CounterResets -eq 1) 'Counter reset must not inflate earnings'
$state=Update-EarningsLedger $state $quote 'wallet-B'
Assert ($state.Xmr -eq 0) 'Wallet change must not mix financial histories'
$wallet.collectiveWorkers[1].activeMiners=2
$reconnected=Get-WorkerQuote $pool $wallet 'this-pc'
Assert ($reconnected.ActiveConnections -eq 2 -and $reconnected.Xmr24 -eq $quote.Xmr24) 'Reconnect must not duplicate worker earnings'
$wallet.collectiveWorkers[1].activeMiners=1
$pool.market.last_updated=[datetimeoffset]::UtcNow.AddHours(-3).ToString('o')
Assert-Throws {Get-WorkerQuote $pool $wallet 'this-pc'} 'Stale exchange rate accepted'
Assert-Throws {Get-WorkerQuote $pool $wallet 'missing-pc'} 'Missing worker accepted'
'Earnings tests OK: attribution, fee, baseline, deltas, duplicate, reset, identity, reconnect, stale price'
$gpuPool=[pscustomobject]@{config=@{symbol='XTM';algo='Cuckaroo29';coinUnits=1000000;fee=0.9};stats=@{profit24hPer1Ghs=40000000000};network=@{difficulty=1000000;reward=10000000000}}
$gpuWallet=[pscustomobject]@{workers=@([pscustomobject]@{name='other-pc';hashrateAvg=@{'24h'=10};hashes=999999},[pscustomobject]@{name='this-pc';hashrateAvg=@{'24h'=2};hashes=100})}
$gpuPrice=[pscustomobject]@{minotari=@{rub=0.13;last_updated_at=[datetimeoffset]::UtcNow.ToUnixTimeSeconds()}}
$gpuQuote=Get-LuckyQuote $gpuPool $gpuWallet $gpuPrice 'this-pc'
Assert ([math]::Abs($gpuQuote.Rub24-(2*40*0.991*0.13)) -lt 1e-10) 'GPU quote units or worker attribution incorrect'
Assert ([math]::Abs($gpuQuote.FleetRub24-(12*40*0.991*0.13)) -lt 1e-10) 'Fleet GPU total must include each worker once'
Assert ($gpuQuote.TotalHashes -eq 100) 'Another worker counter leaked into own ledger'
Assert ([math]::Abs($gpuQuote.XmrPerHash-(10000/1000000*0.991)) -lt 1e-12) 'Accepted-share difficulty must use block reward/difficulty, not graphs per second'
Assert-Throws {Get-LuckyQuote $gpuPool $gpuWallet $gpuPrice 'missing'} 'Missing GPU worker must not silently become zero'
$gpuPrice.minotari.last_updated_at=[datetimeoffset]::UtcNow.AddHours(-3).ToUnixTimeSeconds()
Assert-Throws {Get-LuckyQuote $gpuPool $gpuWallet $gpuPrice 'this-pc'} 'Stale XTM rate accepted'
$gpuPrice.minotari.last_updated_at=[datetimeoffset]::UtcNow.ToUnixTimeSeconds()
$gpuWallet.workers[1].hashrateAvg.'24h'=[double]::NaN
Assert-Throws {Get-LuckyQuote $gpuPool $gpuWallet $gpuPrice 'this-pc'} 'Invalid GPU hashrate accepted'
'GPU earnings tests OK: currency units, own worker, fleet sum, missing worker, stale quote, invalid rate'

$base='C:\ProgramData\PC-Care\feature-tests'
$fixture=Join-Path $base ([guid]::NewGuid().ToString('N'))
$tempRoot=Join-Path $fixture 'temp';$outside=Join-Path $fixture 'outside';$link=Join-Path $tempRoot 'junction'
New-Item -ItemType Directory -Path $tempRoot,$outside -Force | Out-Null
$lock=$null
try{
 $old=(Get-Date).AddDays(-14)
 foreach($name in @('old.tmp','changed.tmp','locked.tmp','fresh.tmp','recently-created.tmp')){[IO.File]::WriteAllText((Join-Path $tempRoot $name),'test payload')}
 foreach($name in @('old.tmp','changed.tmp','locked.tmp')){$p=Join-Path $tempRoot $name;[IO.File]::SetCreationTime($p,$old);[IO.File]::SetLastWriteTime($p,$old)}
 [IO.File]::SetLastWriteTime((Join-Path $tempRoot 'recently-created.tmp'),$old)
 $outsideFile=Join-Path $outside 'keep.tmp';[IO.File]::WriteAllText($outsideFile,'must survive');[IO.File]::SetCreationTime($outsideFile,$old);[IO.File]::SetLastWriteTime($outsideFile,$old)
 New-Item -ItemType Junction -Path $link -Target $outside | Out-Null
 $scan=Find-CleanupCandidates @($tempRoot)
 Assert ($scan.Count -eq 3) 'Scan must skip recent files and junction targets'
 [IO.File]::AppendAllText((Join-Path $tempRoot 'changed.tmp'),'changed after preview')
 $lock=[IO.File]::Open((Join-Path $tempRoot 'locked.tmp'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
 $clean=Remove-CleanupCandidates $scan @($tempRoot)
 Assert ($clean.RemovedCount -eq 1 -and $clean.Skipped -eq 2) 'Cleaner failed modified/locked file protection'
 Assert (-not(Test-Path (Join-Path $tempRoot 'old.tmp'))) 'Eligible temporary file survived'
 Assert (Test-Path $outsideFile) 'Cleaner crossed a junction'
 Assert (Test-Path (Join-Path $tempRoot 'fresh.tmp')) 'Fresh file was deleted'
 Assert-Throws {Assert-OrdinaryPath $outsideFile $tempRoot} 'Outside-root path accepted'
 $fake=[pscustomobject]@{Files=@([pscustomobject]@{Path=$outsideFile;Root=$outside;Length=12});ScannedAt=(Get-Date).ToString('o')}
 $reject=Remove-CleanupCandidates $fake @($tempRoot)
 Assert ($reject.RemovedCount -eq 0 -and (Test-Path $outsideFile)) 'Unapproved root was deleted'
 $file=Join-Path $fixture 'saved.json';Write-AtomicJson @{Value=1} $file;Write-AtomicJson @{Value=2} $file
 Assert ((Read-SavedJson $file).Value -eq 2) 'Atomic state persistence failed'
 [IO.File]::WriteAllText($file,'broken JSON');Assert ((Read-SavedJson $file).Value -eq 1) 'Backup recovery failed'
 'Cleanup tests OK: age/creation, changed files, locked files, junction, out-of-root rejection, atomic backup'
}finally{
 if($lock){$lock.Dispose()}
 if(Test-Path -LiteralPath $link){[IO.Directory]::Delete($link)}
 $resolved=[IO.Path]::GetFullPath((Resolve-Path -LiteralPath $fixture).Path)
 if(-not $resolved.StartsWith($base+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected fixture deletion path'}
 Remove-Item -LiteralPath $resolved -Recurse -Force
}
