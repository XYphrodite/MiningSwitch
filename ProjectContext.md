# Mining Switch 3 - Project Context

## Project Overview

Mining Switch 3 is a Windows desktop app for one PC (`MOSCOW_ENJOYER`) that controls XMRig (CPU) and lolMiner (GPU) with one big button and a tray icon, estimates mining income for the local worker and the whole fleet, and performs basic Windows PC care (temp cleanup, game mode, power plans). It talks to a locally installed `mining-fleet-agent` over loopback and reads public pool APIs (HashVault, LuckyPool) for earnings estimates — it never changes wallets, credentials, or payout settings.

**Platform**: Windows, PowerShell 5.1 + WPF (`.NET Framework`), launcher in C#
**Language**: PowerShell, C#
**Domain**: Mining monitoring / Desktop utility

---

## Technical Stack

### Runtimes

- **Windows PowerShell 5.1** (`#requires -Version 5.1`, must run with `-STA`)
- **.NET Framework** — WPF (`PresentationFramework`, `PresentationCore`, `WindowsBase`) for UI, `System.Windows.Forms` / `System.Drawing` for tray icon and dialogs
- **C#** — `Launcher.cs`, compiled to `MiningSwitch.exe` on the target PC with `csc`

### Key Dependencies

| Package / Component | Version | Purpose |
|---------------------|---------|---------|
| Windows PowerShell | 5.1 | Runtime for the whole UI and logic |
| PresentationFramework (WPF) | .NET Framework | Main window, tabs, controls |
| System.Windows.Forms | .NET Framework | NotifyIcon (tray), MessageBox |
| System.Net.Http | .NET Framework | Loopback calls to `mining-fleet-agent` API |
| Windows `curl` | Built-in | Public pool API requests (no window, no agent token) |
| `mining-fleet-agent` | Installed separately (`%ProgramFiles%\mining-fleet-agent\appsettings.json`) | Local miner control API |

External services: **HashVault** (Monero pool + XMR/RUB quote), **LuckyPool** (Tari C29 GPU pool), **CoinGecko** (XTM/RUB quote). Pool API docs: <https://hashvault.pro/monero/api>

---

## Architecture Overview

`MiningSwitch.exe` is a thin C# launcher: it starts hidden `powershell.exe -STA -File MiningSwitch.ps1`, which owns a WPF window and a tray icon. The UI polls the local agent over loopback for miner status and control; heavier work (public API fetches, earnings ledgers, cleanup scans, power schemes) runs in background jobs defined in `MiningSwitch.Features.ps1` and is fed back into the UI through a result queue.

```
┌────────────────────────────────────────────────────┐
│  MiningSwitch.exe (C# launcher, no console window) │
└───────────────────────┬────────────────────────────┘
                        │ powershell.exe -STA -File
┌───────────────────────▼────────────────────────────┐
│  MiningSwitch.ps1 — WPF UI + tray icon             │
│  (single instance via mutex Local\MiningSwitch.UI) │
│  ┌─────────────────┐    ┌───────────────────────┐  │
│  │ Tabs:           │    │ Timers / job queues   │  │
│  │ · Mining/Income │◄──►│ (agent ops, features) │  │
│  │ · PC Care       │    └───────────┬───────────┘  │
│  └─────────────────┘                │              │
└───────┬─────────────────────┬───────┴──────────────┘
        │ loopback HTTP       │ dot-source
┌───────▼────────┐   ┌────────▼──────────────────────┐
│ mining-fleet-  │   │ MiningSwitch.Features.ps1     │
│ agent API      │   │ (earnings, cleanup, power,    │
│ (local)        │   │  resources, public JSON)      │
└────────────────┘   └───────┬───────────┬───────────┘
                             │           │
                     ┌───────▼───┐   ┌───▼──────────┐
                     │ HashVault │   │ LuckyPool +  │
                     │ (XMR, ₽)  │   │ CoinGecko    │
                     └───────────┘   └──────────────┘

State files: %LOCALAPPDATA%\MiningSwitch\earnings.json (+ backup),
             gpu-share-earnings.json
```

Key design points:

- **Single instance**: named mutex `Local\MiningSwitch.UI`; a second launch signals `Local\MiningSwitch.Show` to raise the existing window instead of starting another copy.
- **Async job pattern**: feature work is queued (`Queue-Feature`) and run one job at a time; results come back through `Handle-FeatureResult`, so the UI thread never blocks on network or disk.
- **Control queue**: agent commands (start/stop, game mode) go through `Add-Operation` → `Begin-Request` → `Handle-Response`, keeping state transitions serialized.
- **Conservative estimates**: income is always labelled an estimate (24 h average hashrate × block reward / difficulty, minus pool fee), never a payout balance.

---

## Project Components

### 1. **Launcher (`Launcher.cs` / `MiningSwitch.exe`)**
**Type**: Console-less WinForms launcher (compiled EXE)
**Location**: `Launcher.cs` → `MiningSwitch.exe`
**Purpose**: Starts `MiningSwitch.ps1` hidden, so no PowerShell console appears.

**Key Features**:
- Validates `MiningSwitch.ps1` sits next to the EXE; shows a MessageBox on failure
- Accepts only one argument: `-Tray` (start minimized to tray)
- Runs `powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File ...` with `CreateNoWindow=true`

### 2. **Main UI (`MiningSwitch.ps1`)**
**Type**: WPF application (PowerShell)
**Location**: `MiningSwitch.ps1`
**Purpose**: Window, tray icon, tabs, timers, and all user interaction (UI text is Russian).

**Key Features**:
- Tabs: **«Майнинг и доход»** (mining status, big power button, income estimates) and **«Уход за ПК»** (cleanup, game mode, power plans)
- Params: `-Tray` (start in tray), `-SelfTest`, `-PreviewPath` / `-PreviewCare` (UI previews), `-SmokeReportPath` (read-only WPF integration check: miner status, income, resources, Temp analysis)
- Big button controls **both** XMRig (CPU) and lolMiner (GPU); stop is attempted for both even if one fails, with a warning on partial success
- Resource tiles (CPU / RAM / free disk C: & D:) refresh about every 12 seconds; income refreshes every 5 minutes or via the «Обновить доход» button
- Manual shutdown survives reboot (persisted); enabling mining restarts miners and re-enables autostart
- Close button hides to tray; «Выход» in the tray menu closes the UI but leaves mining state intact

**Dependencies**:
- `MiningSwitch.Features.ps1` (dot-sourced for background work)
- `settings.json` (computer name, pool worker name)
- `MiningSwitch.ico` (window/tray icon)

### 3. **Features Module (`MiningSwitch.Features.ps1`)**
**Type**: PowerShell module (dot-sourced)
**Location**: `MiningSwitch.Features.ps1`
**Purpose**: All non-UI work. Never changes miner wallets or credentials.

**Key Features**:
- **Earnings**: `Get-WorkerQuote` / `Get-EarningsSnapshot` (HashVault CPU, worker `moscowenjoyer`), `Get-LuckyQuote` / `Get-GpuEarningsSnapshot` (LuckyPool Tari C29, worker `MOSCOW_ENJOYER`); `Update-EarningsLedger` keeps a delta ledger keyed by PC + wallet/worker hash (first sample is baseline only, counter resets and duplicate observations are ignored, wallet change starts a new history)
- **Prices**: XMR/RUB from HashVault market data, XTM/RUB from CoinGecko (`minotari`); quotes older than 2 hours are rejected
- **PC care**: `Find-CleanupCandidates` / `Remove-CleanupCandidates` (files older than 7 days in user Temp, `Windows\Temp`, `D:\Temp`, `D:\TMP`; skips fresh, modified-after-scan, locked, and junction-crossed paths; only approved roots), `Get-ResourceSnapshot`, `Get-ActivePowerScheme` / `Set-PowerScheme` / `Invoke-PowerCfg`
- **Persistence**: `Write-AtomicJson` / `Read-SavedJson` with `.bak` recovery; state in `%LOCALAPPDATA%\MiningSwitch\` (`earnings.json`, `gpu-share-earnings.json`)
- Public HTTP via hidden Windows `curl` (`Get-PublicJson`) with timeouts; the agent token is used in memory for loopback only

### 4. **Tests (`Test-Features.ps1`)**
**Type**: Assertion-style test script (no framework)
**Location**: `Test-Features.ps1`
**Purpose**: Verifies calculations, history persistence, and cleanup safety on any Windows machine.

**Key Features**:
- Earnings: per-PC attribution, pool fee, baseline (no invented history), deltas, duplicate observations, counter resets, wallet identity separation, reconnects, stale price rejection, missing worker rejection
- GPU earnings: unit conversion (accepted-share difficulty vs `g/s`), own-worker isolation, fleet sum, stale XTM rate, invalid hashrate
- Cleanup: age/creation filters, changed-file and locked-file protection, junction non-traversal, out-of-root rejection, atomic write + backup recovery
- Run: `powershell.exe -STA -File Test-Features.ps1`

### 5. **Configuration & Assets**
**Type**: Data / resources
**Location**: repo root

| File | Purpose |
|------|---------|
| `settings.json` | `{ "Computer": "MOSCOW_ENJOYER", "PoolWorker": "moscowenjoyer" }` — which worker is "mine" |
| `MiningSwitch.ico` | Window and tray icon |
| `preview.png`, `preview-v2*.png`, `preview-v3.png` | UI screenshots for reference |
| `MiningSwitch.zip` | Distribution archive (all files must sit side by side) |
| `README.md` | Russian user documentation |
| `MiningSwitch-v1.ps1` | Legacy v1 script, kept for reference |

---

## Directory Structure

```
MiningSwitch/
├── Launcher.cs                  # C# launcher source (builds to MiningSwitch.exe)
├── MiningSwitch.exe             # Compiled launcher — the app entry point
├── MiningSwitch.ps1             # Main WPF UI + orchestration (485 lines)
├── MiningSwitch.Features.ps1    # Background logic: earnings, cleanup, power
├── Test-Features.ps1            # Assertion tests for earnings & cleanup
├── settings.json                # Computer name + pool worker name
├── MiningSwitch.ico             # Window / tray icon
├── MiningSwitch.zip             # Release archive
├── MiningSwitch-v1.ps1          # Legacy version 1 (reference only)
├── preview*.png                 # UI screenshots
└── README.md                    # User documentation (Russian)
```

---

## Build & Run

### Run

```powershell
# Normal start (no console window appears)
MiningSwitch.exe

# Start directly to tray
MiningSwitch.exe -Tray
```

Installed on the target PC at `C:\Program Files\MiningSwitch`, with a desktop shortcut and user autostart entry. All archive files must sit next to the EXE.

### Tests & Smoke Check

```powershell
# Unit-style tests (earnings math, ledgers, cleanup safety)
powershell.exe -STA -File Test-Features.ps1

# Read-only WPF integration check (writes a report file)
powershell.exe -STA -File MiningSwitch.ps1 -SmokeReportPath report.txt
```

### Build

```powershell
# Launcher is compiled ON THE TARGET PC (no local build workflow):
csc /target:winexe /out:MiningSwitch.exe Launcher.cs
```

---

## Development Status

### Implemented Features ✅
- [x] Hidden launcher EXE with `-Tray` support
- [x] WPF UI with single-instance mutex and show-existing-window on relaunch
- [x] Unified CPU (XMRig) + GPU (lolMiner) start/stop via `mining-fleet-agent` loopback API
- [x] Per-PC income estimate (24 h average hashrate, pool fee, XMR/RUB)
- [x] Accumulated earnings ledger since first run (CPU and GPU separately, with counter-reset protection)
- [x] Fleet-wide estimate across two shared wallets (HashVault + LuckyPool), with missing-source honesty
- [x] Game mode (stops both miners, blocks auto-resume until manual re-enable)
- [x] Temp cleanup: analyze → delete, 7-day age filter, junction/lock/modified-file protection
- [x] Power plan switching with original-scheme restore
- [x] Resource tiles (CPU / RAM / disk C:, D:) and storage/autostart buttons
- [x] Atomic state persistence with backup recovery
- [x] Test suite for earnings, GPU quotes, and cleanup safety

### Planned Features 📋
- [ ] Nothing documented in-repo — next features are decided ad hoc

### Known Issues ⚠️
- Income figures are **estimates**, not payouts: PPLNS, pool luck, and electricity are not accounted for (by design)
- Earnings history before first launch cannot be recovered; pool counter resets or missed observations can undercount accumulated totals (a warning is shown on reset)
- Multiple CPU connections to one worker show a warning (reconnects are not double-counted)
- Build happens on the target PC; there is no repeatable build script in the repo (Date: 2026-09-28)

---

## Document Information

**Last Updated**: 2026-10-07
**Version**: 1.0
**Status**: Active
**Repository**: `/mnt/c/Repos/MiningSwitch`
**Related Docs**: `README.md` (Russian user guide), <https://hashvault.pro/monero/api>
