using System.Text.Json.Serialization;

namespace MiningSwitch.App.Services;

// DTO subset adapted from mining-fleet (XYphrodite/mining-fleet, src/MiningFleet.Contracts/Dtos.cs).
// The agent speaks that contract unchanged; only the fields this app reads are kept.

public sealed record CommandResultDto(bool Ok, string Message);

public sealed record MinerStatusDto
{
    public bool Installed { get; init; }
    public bool Running { get; init; }
    public int? Pid { get; init; }
    public string? ExecutablePath { get; init; }
    public string? Version { get; init; }
    public string? Algorithm { get; init; }
    public string? PoolUrl { get; init; }
    public string? Wallet { get; init; }
    public string? WorkerName { get; init; }
    public double UptimeSeconds { get; init; }
    public double? Hashrate10s { get; init; }
    public double? Hashrate60s { get; init; }
    public double? Hashrate15m { get; init; }
    public long SharesGood { get; init; }
    public long SharesTotal { get; init; }
    public long PoolDifficulty { get; init; }
    public string? ApiError { get; init; }
}

public sealed record GpuDeviceStatusDto(
    string Name,
    double? Hashrate,
    double? TemperatureC,
    double? FanPercent,
    double? CoreClockMhz,
    double? MemoryClockMhz);

public sealed record GpuMinerStatusDto
{
    public bool Running { get; init; }
    public string? Algorithm { get; init; }
    public string? Pool { get; init; }
    public double? Hashrate { get; init; }
    public string? HashrateUnit { get; init; }
    public int? AcceptedShares { get; init; }
    public IReadOnlyList<GpuDeviceStatusDto> Devices { get; init; } = [];
    public string? Notice { get; init; }
}

public sealed record GpuPauseRuleDto
{
    public int? TcpPort { get; init; }
    public string? ProcessName { get; init; }
    public IReadOnlyList<string>? ProcessNames { get; init; }
}

public sealed record GpuMinerSettingsDto
{
    public bool? Enabled { get; init; }
    public string? Algorithm { get; init; }
    public string? PoolUrl { get; init; }
    public string? User { get; init; }
    public string? ExecutablePath { get; init; }
    public int? ApiPort { get; init; }
    public GpuPauseRuleDto? PauseWhile { get; init; }
}

public sealed record MinerConfigDto
{
    public string? ExecutablePath { get; init; }
    public string? PoolUrl { get; init; }
    public string? Wallet { get; init; }
    public string? WorkerName { get; init; }
    public bool? AutoStartMiner { get; init; }
    public GpuPauseRuleDto? PauseWhile { get; init; }
    public bool? MinerStoppedByPause { get; init; }
    public bool? MinerStoppedByThrottle { get; init; }
    public bool? GpuStoppedByPause { get; init; }
    public bool? MinerWanted { get; init; }
    public bool? GpuWanted { get; init; }
    public GpuMinerSettingsDto? GpuMiner { get; init; }

    /// <summary>True when the last explicit command was a shutdown — mining stays off until asked.</summary>
    [JsonIgnore]
    public bool ManualOff =>
        AutoStartMiner is not true
        && MinerWanted is not true
        && GpuWanted is not true
        && GpuMiner?.Enabled is not true
        && GpuStoppedByPause is not true
        && MinerStoppedByPause is not true
        && MinerStoppedByThrottle is not true;
}
