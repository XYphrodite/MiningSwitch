# Mining Switch 4 - Project Context

## Project Overview

Mining Switch is a Windows desktop dashboard for a mining PC: one big button starts and stops XMRig (CPU) and lolMiner (GPU) through the local `mining-fleet-agent`, a tray icon keeps it one click away, and rough income estimates cover both this PC and every worker of the fleet's shared wallets. It never touches wallets, credentials or payout settings.

**Platform**: Windows 10/11, .NET 10 (`net10.0-windows`), WPF
**Language**: C# 14
**Domain**: Mining operations / desktop dashboard

---

## Technical Stack

### Runtimes & Frameworks

- **.NET 10** (`net10.0-windows`) — WPF UI, WinForms `NotifyIcon` for the tray
- **.NET 10 Desktop Runtime** — required on the target PC (present wherever `mining-fleet-agent` runs)
- Published as a single framework-dependent `MiningSwitch.exe` (~0.2 MB)

### Key Dependencies

| Package / Component | Version | Purpose |
|---------------------|---------|---------|
| Microsoft.WindowsDesktop.App | 10.x | WPF + WinForms (tray icon) |
| `mining-fleet-agent` | Installed separately | Local miner control API (`/api/v1`, loopback) |
| HashVault public API | v3 | CPU worker stats, network figures, XMR/RUB rate |
| LuckyPool public API | taric29 | GPU (Tari C29) worker stats, pool calculator rate |
| CoinGecko public API | v3 | XTM/RUB rate |

No NuGet packages in the app itself; DTOs and the agent client are adapted from
[mining-fleet](https://github.com/XYphrodite/mining-fleet) (`MiningFleet.Contracts`,
`AgentClient`). Tests use xUnit.

---

## Architecture Overview

`MiningSwitch.exe` is a single WPF application. It discovers the local agent from
`%ProgramFiles%\mining-fleet-agent\appsettings.json`, then polls it over loopback with a
shared-secret header. Income figures come from the public pool APIs and are recalculated
on a timer — nothing is accumulated or stored on disk.

```
┌─────────────────────────────────────────────────────┐
│  MiningSwitch.exe (WPF, single instance, tray icon) │
│  ┌──────────────┐  ┌──────────────┐  ┌───────────┐  │
│  │ Dashboard    │  │ Control      │  │ Timers    │  │
│  │ miners,      │  │ big button,  │  │ poll 4 s  │  │
│  │ income,      │  │ tray menu    │  │ res 12 s  │  │
│  │ resources    │  │              │  │ income 5m │  │
│  └──────┬───────┘  └──────┬───────┘  └─────┬─────┘  │
└─────────┼─────────────────┼────────────────┼────────┘
          │                 │ loopback HTTP  │
          │        ┌────────▼────────┐       │
          │        │ mining-fleet-   │       │
          │        │ agent /api/v1   │       │
          │        └────────┬────────┘       │
          │                 │                │
    ┌─────▼─────┐    ┌──────▼──────┐   ┌─────▼──────┐
    │  XMRig    │    │  lolMiner   │   │ Public APIs│
    │  (CPU)    │    │  (GPU)      │   │ HashVault, │
    └───────────┘    └─────────────┘   │ LuckyPool, │
                                       │ CoinGecko  │
                                       └────────────┘
```

Key design points:

- **Single instance**: named mutex `Local\MiningSwitch.UI`; a second launch signals
  `Local\MiningSwitch.Show` to raise the existing window.
- **Agent contract**: the DTO subset and `AgentClient` are taken from mining-fleet, so the
  app tracks that API as it evolves; 404 on `gpu` simply means an older agent.
- **Honest partials**: when one income source is unavailable the total is withheld and the
  available part is shown with an explanation — never a confident wrong number.
- **Persistent shutdown**: manual off sets the agent's policy fields (`minerWanted`,
  `gpuWanted`, `autoStartMiner` …), which survive reboots.

---

## Project Components

### 1. **App (`App.xaml`, `App.xaml.cs`)**
**Type**: WPF application entry point
**Location**: `src/MiningSwitch.App/`

**Key Features**:
- Args: `-Tray` (start in tray), `--selftest <path>` (headless check: agent, pools, resources → JSON report)
- Single-instance mutex + show-existing-window event
- Self-test runs off the dispatcher thread (blocking HTTP on a UI thread deadlocks)

### 2. **MainWindow (dashboard + control)**
**Type**: WPF window
**Location**: `src/MiningSwitch.App/MainWindow.xaml(.cs)`

**Key Features**:
- Status cards for XMRig and lolMiner with live hashrate, big power button for both miners
- Income card: this PC (CPU + GPU, ₽/day) and fleet-wide forecast, refreshed every 5 minutes
- Resource tiles (CPU / RAM / free disk C:, D:) every 12 seconds
- Control flow mirrors mining-fleet semantics: policy `PUT /config`, then `miner/start|stop`,
  `gpu/start|stop`, then a confirming poll; partial results get a warning
- Close button hides to tray; tray menu: open / enable / disable / exit

### 3. **Services**
**Type**: Class library code inside the app project
**Location**: `src/MiningSwitch.App/Services/`

**Key Features**:
- `AgentClient` + `Contracts` — mining-fleet's agent API subset (loopback, `X-Fleet-Token`)
- `IncomeService` — CPU estimate (HashVault: avg 24 h hashrate × 86400 × reward / difficulty
  − pool fee, × XMR/RUB) and GPU estimate (LuckyPool `profit24hPer1Ghs` conversion − fee,
  × XTM/RUB from CoinGecko); quotes older than 2 hours are refused
- `ResourceMonitor` — CPU load (`GetSystemTimes`), RAM (`GlobalMemoryStatusEx`), disks (`DriveInfo`)

### 4. **Tests**
**Type**: xUnit
**Location**: `tests/MiningSwitch.Tests/`

**Key Features**: ports of the earnings assertions of the earlier `Test-Features.ps1` —
fee handling, unit conversions, quote freshness.

---

## Directory Structure

```
MiningSwitch/
├── src/MiningSwitch.App/          # The application (C# 14, net10.0-windows)
│   ├── App.xaml(.cs)              # Startup, args, single instance, self-test
│   ├── MainWindow.xaml(.cs)       # Dashboard + mining control
│   ├── TrayIcon.cs                # Tray icon and menu
│   └── Services/                  # Agent client, income, resources
├── tests/MiningSwitch.Tests/      # xUnit tests for the estimate math
├── install.ps1                    # Remote installer (runs via `irm ... | iex`)
├── MiningSwitch.ico               # Application icon
├── MiningSwitch.zip               # Release archive (MiningSwitch.exe + README)
├── ProjectContext.md              # This document
└── README.md                      # User documentation (Russian)
```

---

## Build & Run

```powershell
# Build
dotnet build src/MiningSwitch.App -c Release

# Tests
dotnet test tests/MiningSwitch.Tests -c Release

# Release (single exe, requires .NET 10 Desktop Runtime on target)
dotnet publish src/MiningSwitch.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish

# Headless verification (writes a JSON report, no window)
.\publish\MiningSwitch.exe --selftest report.json
```

### Install (any PC)

```powershell
powershell -c "irm https://raw.githubusercontent.com/XYphrodite/MiningSwitch/main/install.ps1 | iex"
```

---

## Development Status

### Implemented Features ✅
- [x] WPF dashboard: miner status, hashrates, resource tiles
- [x] Single-instance window with tray icon and close-to-tray
- [x] Big button and tray menu controlling both miners via the agent (policy + start/stop + confirm)
- [x] Persistent manual shutdown (agent policy survives reboot)
- [x] Rough income estimate for this PC and the whole fleet (CPU + GPU), honest partials
- [x] Quote freshness guard (2 hours) for XMR/RUB and XTM/RUB
- [x] Headless `--selftest` mode with JSON report
- [x] `irm` installer with Desktop Runtime check
- [x] xUnit tests for the estimate math

### Planned Features 📋
- [ ] Nothing in-repo; features are scoped ad hoc

### Known Issues ⚠️
- Income figures are forecasts, not payouts: PPLNS, pool luck and electricity are excluded (by design)
- Fleet total is withheld entirely while the GPU source is unavailable (by design)
- The app needs .NET 10 Desktop Runtime; `install.ps1` stops with a clear message when it is missing (Date: 2026-10-07)

---

## Document Information

**Last Updated**: 2026-10-07
**Version**: 2.0
**Status**: Active
**Repository**: https://github.com/XYphrodite/MiningSwitch
**Related Docs**: `README.md` (Russian), [mining-fleet](https://github.com/XYphrodite/mining-fleet), <https://hashvault.pro/monero/api>
