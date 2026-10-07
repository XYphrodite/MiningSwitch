using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace MiningSwitch.App.Services;

/// <summary>
/// Rough income estimates — a forecast, never a payout balance. Pool statistics (Hashvault
/// for CPU, LuckyPool for the Tari GPU) are converted with each pool's own units; nothing is
/// accumulated or stored, the number answers "about how much per day right now".
/// Math checked into mining-fleet's income services and MiningSwitch's earlier script.
/// </summary>
public static class IncomeMath
{
    /// <summary>XMR earned per hash of accepted work: block reward over difficulty, minus the pool fee.</summary>
    public static double XmrPerHash(double rewardCoins, double difficulty, double feePercent) =>
        rewardCoins / difficulty * (1 - feePercent / 100);

    /// <summary>
    /// XMR per day for a hashrate: a day of work at that rate values at rate × 86400 hashes.
    /// </summary>
    public static double RubPerDay(double averageHashrate, double perHash, double rubRate) =>
        averageHashrate * 86400 * perHash * rubRate;

    /// <summary>
    /// Coins per unit of GPU hashrate per day, using LuckyPool's own calculator conversion:
    /// H/s / 1e9 * 1e6 * profit24hPer1Ghs / coinUnits, minus the advertised pool fee.
    /// </summary>
    public static double GpuCoinsPerHashrateDay(double profit24hPer1Ghs, double coinUnits, double feePercent) =>
        profit24hPer1Ghs / 1e9 * 1e6 / coinUnits * (1 - feePercent / 100);

    /// <summary>Accepted-share difficulty values work at block reward / difficulty, in coins.</summary>
    public static double GpuCoinsPerShareHash(double networkReward, double coinUnits, double networkDifficulty) =>
        networkReward / coinUnits / networkDifficulty;

    /// <summary>Quotes older than two hours are not accepted; neither are quotes from the future.</summary>
    public static bool IsFresh(DateTimeOffset quoteTime, DateTimeOffset now) =>
        now - quoteTime <= TimeSpan.FromHours(2) && quoteTime - now <= TimeSpan.FromMinutes(5);
}

public sealed record CpuQuote(
    string Worker,
    double Average24,
    double WorkerRub24,
    double FleetRub24,
    double RubRate,
    DateTimeOffset PriceTimeUtc,
    int FleetWorkers);

public sealed record GpuQuote(
    string Worker,
    double Average24,
    double WorkerRub24,
    double FleetRub24,
    double RubRate,
    DateTimeOffset PriceTimeUtc,
    int FleetWorkers);

public sealed record IncomeSnapshot(
    CpuQuote Cpu,
    GpuQuote? Gpu,
    string? GpuError,
    DateTimeOffset FetchedAt)
{
    public double? WorkerRub24 => Gpu is null ? Cpu.WorkerRub24 : Cpu.WorkerRub24 + Gpu.WorkerRub24;
    public double? FleetRub24 => Gpu is null ? null : Cpu.FleetRub24 + Gpu.FleetRub24;
}

public sealed class IncomeService : IDisposable
{
    private const double DefaultSigDivisor = 1e12;
    private const int MaxQuoteAgeHours = 2;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public IncomeService()
    {
        // No proxy on public pool APIs either; a captive portal must not answer instead of the pool.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MiningSwitch/4.0");
    }

    public async Task<IncomeSnapshot> GetSnapshotAsync(
        MinerStatusDto miner, MinerConfigDto config, CancellationToken ct)
    {
        var worker = (miner.WorkerName ?? config.WorkerName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(worker) || string.IsNullOrWhiteSpace(miner.Wallet))
            throw new InvalidOperationException("Не настроен отдельный воркер ПК.");

        var poolHost = HostOf(miner.PoolUrl ?? config.PoolUrl);
        if (poolHost is not ("pool.hashvault.pro" or "pool.hashvault.sh"))
            throw new InvalidOperationException("Для этого пула расчёт ещё не настроен.");

        var cpu = await GetCpuQuoteAsync(miner.Wallet!, worker, ct);

        GpuQuote? gpu = null;
        string? gpuError = null;
        try { gpu = await GetGpuQuoteAsync(config.GpuMiner, Environment.MachineName, ct); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or FormatException)
        {
            // A missing GPU source never becomes a confident zero; the caller shows the CPU part.
            gpuError = ex.Message;
        }

        return new IncomeSnapshot(cpu, gpu, gpuError, DateTimeOffset.Now);
    }

    private async Task<CpuQuote> GetCpuQuoteAsync(string wallet, string worker, CancellationToken ct)
    {
        var pool = await GetJsonAsync("https://api.hashvault.pro/v3/monero/pool/stats", ct)
            ?? throw new InvalidOperationException("HashVault недоступен.");
        var url = "https://api.hashvault.pro/v3/monero/wallet/" + Uri.EscapeDataString(wallet)
            + "/stats?workers=true&chart=false&inactivityThreshold=10080";
        var walletStats = await GetJsonAsync(url, ct)
            ?? throw new InvalidOperationException("Статистика кошелька недоступна.");

        var config = Path(pool, "config");
        var divisor = Number(config, "sigDivisor") ?? DefaultSigDivisor;
        var difficulty = Number(Path(pool, "network_statistics"), "difficulty") ?? 0;
        var reward = Scale(Number(Path(pool, "pool_statistics", "general"), "last10blocksAvgReward"), divisor)
            ?? Scale(Number(Path(pool, "network_statistics"), "value"), divisor) ?? 0;
        var fee = Number(config, "pplns_fee");
        var (rubRate, priceTime) = ReadRubPrice(Path(pool, "market"));

        if (divisor <= 0 || difficulty <= 0 || reward <= 0 || fee is null or < 0 or >= 100 || rubRate <= 0)
            throw new InvalidOperationException("Нет достоверных данных сети или курса XMR/RUB.");
        if (!IncomeMath.IsFresh(priceTime, DateTimeOffset.UtcNow))
            throw new InvalidOperationException("Курс XMR/RUB устарел.");

        var perHash = IncomeMath.XmrPerHash(reward, difficulty, fee.Value);

        var workers = Path(walletStats, "collectiveWorkers");
        double workerRate = -1, fleetRate = 0;
        int count = 0;
        if (workers.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in workers.EnumerateArray())
            {
                var average = Number(item, "avg24hashRate") ?? -1;
                if (average < 0) throw new InvalidOperationException("Неполная статистика воркера.");
                fleetRate += average;
                count++;
                if (item.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String
                    && string.Equals(name.GetString(), worker, StringComparison.OrdinalIgnoreCase))
                    workerRate = average;
            }
        }
        if (workerRate < 0)
            throw new InvalidOperationException("Пул не нашёл отдельный воркер этого ПК.");

        return new CpuQuote(
            worker,
            workerRate,
            IncomeMath.RubPerDay(workerRate, perHash, rubRate),
            IncomeMath.RubPerDay(fleetRate, perHash, rubRate),
            rubRate,
            priceTime,
            count);
    }

    private async Task<GpuQuote> GetGpuQuoteAsync(GpuMinerSettingsDto? settings, string computerName, CancellationToken ct)
    {
        if (settings is null || settings.Enabled is not true)
            throw new InvalidOperationException("GPU-майнер не включён.");
        if (HostOf(settings.PoolUrl) is not "taric29.luckypool.io")
            throw new InvalidOperationException("Этот GPU пул не поддержан.");

        var login = settings.User ?? "";
        var dot = login.IndexOf('.');
        if (dot <= 0 || dot == login.Length - 1)
            throw new InvalidOperationException("Для отдельного учёта GPU нужно имя воркера.");
        var address = login[..dot];
        var worker = login[(dot + 1)..];
        if (!string.Equals(worker, computerName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("GPU воркер не совпадает с ПК.");

        var pool = await GetJsonAsync("https://taric29.luckypool.io/api/stats?v=2", ct)
            ?? throw new InvalidOperationException("LuckyPool недоступен.");
        var wallet = await GetJsonAsync(
            "https://taric29.luckypool.io/api/stats_address?address=" + Uri.EscapeDataString(address), ct)
            ?? throw new InvalidOperationException("Статистика GPU-кошелька недоступна.");
        var price = await GetJsonAsync(
            "https://api.coingecko.com/api/v3/simple/price?ids=minotari&vs_currencies=rub&include_last_updated_at=true", ct)
            ?? throw new InvalidOperationException("Курс XTM/RUB недоступен.");

        var poolConfig = Path(pool, "config");
        if (String(poolConfig, "symbol") != "XTM" || String(poolConfig, "algo") != "Cuckaroo29")
            throw new InvalidOperationException("Неожиданный алгоритм пула.");
        var profit = Number(Path(pool, "stats"), "profit24hPer1Ghs") ?? 0;
        var units = Number(poolConfig, "coinUnits") ?? 0;
        var fee = Number(poolConfig, "fee");
        if (profit <= 0 || units <= 0 || fee is null or < 0 or >= 100)
            throw new InvalidOperationException("Неполная оценка GPU.");

        var minotari = Path(price, "minotari");
        var rubRate = Number(minotari, "rub") ?? 0;
        var priceTime = DateTimeOffset.FromUnixTimeSeconds((long)(Number(minotari, "last_updated_at") ?? 0));
        if (rubRate <= 0)
            throw new InvalidOperationException("Неполная оценка GPU.");
        if (!IncomeMath.IsFresh(priceTime, DateTimeOffset.UtcNow))
            throw new InvalidOperationException("Курс XTM/RUB устарел.");

        var coinsPerRateDay = IncomeMath.GpuCoinsPerHashrateDay(profit, units, fee.Value);

        var workers = Path(wallet, "workers");
        double workerRate = -1, fleetRate = 0;
        int count = 0;
        if (workers.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in workers.EnumerateArray())
            {
                var average = Number(Path(item, "hashrateAvg"), "24h") ?? -1;
                if (average < 0) throw new InvalidOperationException("Нет средней скорости GPU за сутки.");
                fleetRate += average;
                count++;
                if (item.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String
                    && string.Equals(name.GetString(), worker, StringComparison.OrdinalIgnoreCase))
                    workerRate = average;
            }
        }
        if (workerRate < 0)
            throw new InvalidOperationException("Нет отдельной статистики GPU этого ПК.");

        return new GpuQuote(
            worker,
            workerRate,
            workerRate * coinsPerRateDay * rubRate,
            fleetRate * coinsPerRateDay * rubRate,
            rubRate,
            priceTime,
            count);
    }

    private static (double RubRate, DateTimeOffset PriceTimeUtc) ReadRubPrice(JsonElement market)
    {
        var price = Number(market, "price_rub") ?? 0;
        var raw = market.ValueKind == JsonValueKind.Object && market.TryGetProperty("last_updated", out var value)
            ? value.GetString()
            : null;
        var time = DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
        return (price, time);
    }

    private static string? HostOf(string? poolUrl)
    {
        if (string.IsNullOrWhiteSpace(poolUrl)) return null;
        var withScheme = poolUrl.Contains("://") ? poolUrl : "stratum+tcp://" + poolUrl;
        return Uri.TryCreate(withScheme, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    private async Task<JsonElement?> GetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;
            var text = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static JsonElement Path(JsonElement element, params string[] names)
    {
        var current = element;
        foreach (var name in names)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out var next))
                return default;
            current = next;
        }
        return current;
    }

    private static double? Scale(double? atomic, double divisor) =>
        atomic is null || divisor <= 0 ? null : atomic.Value / divisor;

    private static double? Number(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number) return value.GetDouble();
            if (value.ValueKind == JsonValueKind.String
                && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return null;
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public void Dispose() => _http.Dispose();
}
