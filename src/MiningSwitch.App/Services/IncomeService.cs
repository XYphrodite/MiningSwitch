using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace MiningSwitch.App.Services;

/// <summary>
/// Rough income estimates — a forecast, never a payout balance. Pool statistics (Hashvault
/// for CPU, Kryptex Pearl or LuckyPool Tari for GPU) are converted with each pool's own units;
/// nothing is accumulated or stored, the number answers "about how much per day right now".
/// Math checked into mining-fleet's income services and MiningSwitch's earlier script.
/// </summary>
public static class IncomeMath
{
    private static readonly string[] KryptexRegionSuffixes =
        ["-eu", "-us", "-br", "-sg", "-hk", "-ru", "-ae"];

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

    /// <summary>
    /// Net coins per 1 H/s per day on Kryptex PPS+: estimated_profit_day minus pool fee fraction (e.g. 0.03).
    /// </summary>
    public static double KryptexCoinsPerHashrateDay(double estimatedProfitDay, double feeFraction) =>
        estimatedProfitDay * (1 - feeFraction);

    /// <summary>
    /// Net coins per 1 unit of hashrate per day on Kryptex PROP pools (e.g. QTC Poseidon2, XTM C29),
    /// where estimated_profit_day is null and block parameters define daily emission.
    /// </summary>
    public static double KryptexPropCoinsPerHashrateDay(
        double netHashrate, double blockTimeSeconds, double blockReward, double feeFraction) =>
        86400.0 / blockTimeSeconds * blockReward / netHashrate * (1 - feeFraction);

    /// <summary>
    /// Picks the most representative Kryptex worker average: Kryptex divides 24h averages by a full
    /// 86,400 seconds even for a worker started 20 minutes ago, so short-lived sessions use 30m/3h windows.
    /// </summary>
    public static double SelectKryptexWorkerRate(
        double avg24h, double avg3h, double avg30m, long? openedAtUnixMs, DateTimeOffset now)
    {
        if (openedAtUnixMs is > 0)
        {
            var age = now - DateTimeOffset.FromUnixTimeMilliseconds(openedAtUnixMs.Value);
            if (age >= TimeSpan.Zero && age < TimeSpan.FromHours(3) && avg30m > avg24h)
                return avg30m;
            if (age >= TimeSpan.Zero && age < TimeSpan.FromHours(24) && avg3h > avg24h)
                return avg3h;
        }
        return avg24h;
    }

    /// <summary>Accepted-share difficulty values work at block reward / difficulty, in coins.</summary>
    public static double GpuCoinsPerShareHash(double networkReward, double coinUnits, double networkDifficulty) =>
        networkReward / coinUnits / networkDifficulty;

    /// <summary>Quotes older than two hours are not accepted; neither are quotes from the future.</summary>
    public static bool IsFresh(DateTimeOffset quoteTime, DateTimeOffset now) =>
        now - quoteTime <= TimeSpan.FromHours(2) && quoteTime - now <= TimeSpan.FromMinutes(5);

    public static string? HostOf(string? poolUrl)
    {
        if (string.IsNullOrWhiteSpace(poolUrl)) return null;
        var withScheme = poolUrl.Contains("://") ? poolUrl : "stratum+tcp://" + poolUrl;
        return Uri.TryCreate(withScheme, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    /// <summary>
    /// Extracts the Kryptex pool coin slug from a stratum endpoint such as prl.kryptex.network:7048
    /// or regional prl-eu.kryptex.network:7048, returning null for non-Kryptex hosts.
    /// </summary>
    public static string? KryptexPoolSlug(string? poolUrl)
    {
        var host = HostOf(poolUrl);
        if (host is null || !host.EndsWith(".kryptex.network", StringComparison.OrdinalIgnoreCase))
            return null;

        var subdomain = host[..^".kryptex.network".Length].ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(subdomain) || subdomain.Contains('.'))
            return null;

        foreach (var suffix in KryptexRegionSuffixes)
        {
            if (subdomain.Length > suffix.Length && subdomain.EndsWith(suffix, StringComparison.Ordinal))
                return subdomain[..^suffix.Length];
        }

        return subdomain;
    }

    /// <summary>
    /// Scales raw H/s rates (e.g. pearlhash TH/s) to SI units while preserving pre-scaled units like g/s.
    /// </summary>
    public static string FormatGpuRate(double? rate, string? unit, IFormatProvider? provider = null)
    {
        if (rate is null or < 0 || double.IsNaN(rate.Value) || double.IsInfinity(rate.Value))
            return "—";

        var normalizedUnit = string.IsNullOrWhiteSpace(unit) ? "H/s" : unit.Trim();
        if (!normalizedUnit.Equals("H/s", StringComparison.OrdinalIgnoreCase))
            return string.Create(provider, $"{rate.Value:N2} {normalizedUnit}");

        var value = rate.Value;
        return value switch
        {
            >= 1e15 => string.Create(provider, $"{value / 1e15:N2} PH/s"),
            >= 1e12 => string.Create(provider, $"{value / 1e12:N2} TH/s"),
            >= 1e9  => string.Create(provider, $"{value / 1e9:N2} GH/s"),
            >= 1e6  => string.Create(provider, $"{value / 1e6:N2} MH/s"),
            >= 1e3  => string.Create(provider, $"{value / 1e3:N2} kH/s"),
            0       => "0 H/s",
            _       => string.Create(provider, $"{value:N0} H/s"),
        };
    }
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
    int FleetWorkers)
{
    public string Coin { get; init; } = "PRL";
    public string Unit { get; init; } = "H/s";

    /// <summary>Why this PC's share is zero when it is zero.</summary>
    public string? Note { get; init; }
}

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
    private static readonly TimeSpan CoinGeckoCacheTtl = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly SemaphoreSlim _priceGate = new(1, 1);
    private readonly Dictionary<string, (double RubRate, DateTimeOffset PriceTimeUtc, DateTimeOffset CachedAtUtc)> _priceCache =
        new(StringComparer.OrdinalIgnoreCase);

    public IncomeService()
    {
        // No proxy on public pool APIs either; a captive portal must not answer instead of the pool.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MiningSwitch/4.1");
    }

    public async Task<IncomeSnapshot> GetSnapshotAsync(
        MinerStatusDto miner,
        MinerConfigDto config,
        CancellationToken ct,
        GpuMinerStatusDto? liveGpu = null)
    {
        var worker = (miner.WorkerName ?? config.WorkerName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(worker) || string.IsNullOrWhiteSpace(miner.Wallet))
            throw new InvalidOperationException("Не настроен отдельный воркер ПК.");

        var poolHost = IncomeMath.HostOf(miner.PoolUrl ?? config.PoolUrl);
        if (poolHost is not ("pool.hashvault.pro" or "pool.hashvault.sh"))
            throw new InvalidOperationException("Для этого пула расчёт ещё не настроен.");

        var cpu = await GetCpuQuoteAsync(miner.Wallet!, worker, ct);

        GpuQuote? gpu = null;
        string? gpuError = null;
        try
        {
            // Even a switched-off card's wallet still answers for the fleet forecast; only
            // this PC's share is zero — a known zero, not missing data.
            var gpuActive = config.GpuMiner?.Enabled is true || liveGpu?.Running is true;
            gpu = await GetGpuQuoteAsync(config.GpuMiner, Environment.MachineName, gpuActive, liveGpu, ct);
        }
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

    private sealed record KryptexCoinSpec(
        string Slug,
        string Symbol,
        string ExpectedAlgo,
        string CoinGeckoId,
        string Unit);

    private async Task<GpuQuote> GetGpuQuoteAsync(
        GpuMinerSettingsDto? settings,
        string computerName,
        bool enabled,
        GpuMinerStatusDto? liveGpu,
        CancellationToken ct)
    {
        if (settings is null || string.IsNullOrWhiteSpace(settings.User))
            throw new InvalidOperationException("GPU-майнер не настроен.");

        // Pools disagree about the login shape: "address.worker", "address/worker", and "email@domain/worker".
        var login = settings.User.Trim();
        var slash = login.IndexOf('/');
        var sep = slash > 0 ? slash : (login.Contains('@') ? -1 : login.IndexOf('.'));
        if (sep <= 0 || sep == login.Length - 1)
            throw new InvalidOperationException("Для отдельного учёта GPU нужно имя воркера.");
        var address = login[..sep].Trim();
        var worker = login[(sep + 1)..].Trim();
        if (address.Length == 0 || worker.Length == 0)
            throw new InvalidOperationException("Для отдельного учёта GPU нужно имя воркера.");

        // This PC's share counts only while its card mines under a worker of its own.
        var mine = enabled && string.Equals(worker, computerName, StringComparison.OrdinalIgnoreCase);

        var gpuHost = IncomeMath.HostOf(settings.PoolUrl);
        if (gpuHost is not null && gpuHost.EndsWith(".luckypool.io", StringComparison.OrdinalIgnoreCase))
            return await GetLuckyPoolTariQuoteAsync(address, worker, enabled, mine, ct);

        if (IncomeMath.KryptexPoolSlug(settings.PoolUrl) is { } slug)
        {
            var spec = slug switch
            {
                "prl" => new KryptexCoinSpec("prl", "PRL", "PearlHash", "pearl-2", "H/s"),
                "qtc" => new KryptexCoinSpec("qtc", "QTC", "Poseidon2", "quantus", "H/s"),
                "xtm-c29" => new KryptexCoinSpec("xtm-c29", "XTM", "Cuckaroo29", "minotari", "g/s"),
                _ => null,
            };
            if (spec is null)
                throw new InvalidOperationException($"Монета Kryptex '{slug}' ещё не поддержана.");

            return await GetKryptexCoinQuoteAsync(spec, address, worker, enabled, mine, liveGpu, ct);
        }

        throw new InvalidOperationException("Этот GPU пул не поддержан.");
    }

    private async Task<GpuQuote> GetKryptexCoinQuoteAsync(
        KryptexCoinSpec spec,
        string address,
        string worker,
        bool enabled,
        bool mine,
        GpuMinerStatusDto? liveGpu,
        CancellationToken ct)
    {
        var poolTask = GetJsonAsync($"https://pool.kryptex.com/{spec.Slug}/api/v1/pool/stats", ct);
        var walletTask = GetJsonAsync(
            $"https://pool.kryptex.com/{spec.Slug}/api/v3/miner/workers/" + Uri.EscapeDataString(address), ct);
        var priceTask = GetCoinGeckoRubPriceAsync(spec.CoinGeckoId, spec.Symbol, ct);
        await Task.WhenAll(poolTask, walletTask, priceTask);

        var pool = poolTask.Result
            ?? throw new InvalidOperationException($"Пул Kryptex {spec.Symbol} недоступен.");
        var wallet = walletTask.Result
            ?? throw new InvalidOperationException("Статистика GPU-кошелька на Kryptex недоступна.");

        if (!string.Equals(String(pool, "ticker"), spec.Symbol, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(String(pool, "algo"), spec.ExpectedAlgo, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Неожиданный алгоритм пула Kryptex {spec.Symbol}.");

        var fee = Number(pool, "fee");
        if (fee is null or < 0 or >= 1)
            throw new InvalidOperationException("Неполная оценка GPU от Kryptex.");

        double coinsPerRateDay;
        if (Number(pool, "estimated_profit_day") is { } est && est > 0 && double.IsFinite(est))
        {
            coinsPerRateDay = IncomeMath.KryptexCoinsPerHashrateDay(est, fee.Value);
        }
        else if (Number(pool, "net_hashrate") is { } netHr && netHr > 0 && double.IsFinite(netHr)
                 && Number(pool, "block_time") is { } blockTime && blockTime > 0 && double.IsFinite(blockTime)
                 && Number(pool, "block_reward") is { } reward && reward > 0 && double.IsFinite(reward))
        {
            coinsPerRateDay = IncomeMath.KryptexPropCoinsPerHashrateDay(netHr, blockTime, reward, fee.Value);
        }
        else
        {
            throw new InvalidOperationException("Неполная оценка GPU от Kryptex.");
        }

        var (rubRate, priceTime) = priceTask.Result;
        var now = DateTimeOffset.UtcNow;

        var workers = Path(wallet, "results");
        double workerRate = 0, fleetRate = 0;
        int count = 0;
        bool found = false;
        if (workers.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in workers.EnumerateArray())
            {
                var avg24 = Number(item, "avg_hashrate_24h") ?? -1;
                if (avg24 < 0) throw new InvalidOperationException("Нет средней скорости GPU за сутки.");
                var avg3h = Number(item, "avg_hashrate_3h") ?? 0;
                var avg30m = Number(item, "avg_hashrate_30m") ?? 0;
                var openedAtMs = (long?)Number(item, "opened_at");
                var effective = IncomeMath.SelectKryptexWorkerRate(avg24, avg3h, avg30m, openedAtMs, now);

                fleetRate += effective;
                count++;
                if (mine && string.Equals(String(item, "worker"), worker, StringComparison.OrdinalIgnoreCase))
                {
                    workerRate = effective;
                    found = true;
                }
            }
        }

        string? note = null;
        if (!mine)
        {
            workerRate = 0;
            note = enabled ? "GPU-воркер не этого ПК — его доход не приписывается." : "GPU выключен — 0 ₽.";
        }
        else if (workerRate <= 0 && liveGpu is { Running: true, Hashrate: > 0 })
        {
            workerRate = liveGpu.Hashrate.Value;
            fleetRate += workerRate;
            if (!found) count++;
            note = "Пул ещё копит статистику за 24 ч — расчёт по текущей скорости карты.";
        }
        else if (!found)
        {
            note = "Пул ещё не видит GPU-воркер этого ПК.";
        }

        return new GpuQuote(
            worker,
            workerRate,
            workerRate * coinsPerRateDay * rubRate,
            fleetRate * coinsPerRateDay * rubRate,
            rubRate,
            priceTime,
            count)
        {
            Coin = spec.Symbol,
            Unit = spec.Unit,
            Note = note,
        };
    }

    private async Task<GpuQuote> GetLuckyPoolTariQuoteAsync(
        string address, string worker, bool enabled, bool mine, CancellationToken ct)
    {
        var pool = await GetJsonAsync("https://taric29.luckypool.io/api/stats?v=2", ct)
            ?? throw new InvalidOperationException("LuckyPool недоступен.");
        var wallet = await GetJsonAsync(
            "https://taric29.luckypool.io/api/stats_address?address=" + Uri.EscapeDataString(address), ct)
            ?? throw new InvalidOperationException("Статистика GPU-кошелька недоступна.");

        var poolConfig = Path(pool, "config");
        if (String(poolConfig, "symbol") != "XTM" || String(poolConfig, "algo") != "Cuckaroo29")
            throw new InvalidOperationException("Неожиданный алгоритм пула.");
        var profit = Number(Path(pool, "stats"), "profit24hPer1Ghs") ?? 0;
        var units = Number(poolConfig, "coinUnits") ?? 0;
        var fee = Number(poolConfig, "fee");
        if (profit <= 0 || units <= 0 || fee is null or < 0 or >= 100)
            throw new InvalidOperationException("Неполная оценка GPU.");

        var (rubRate, priceTime) = await GetCoinGeckoRubPriceAsync("minotari", "XTM", ct);
        var coinsPerRateDay = IncomeMath.GpuCoinsPerHashrateDay(profit, units, fee.Value);

        var workers = Path(wallet, "workers");
        double workerRate = 0, fleetRate = 0;
        int count = 0;
        if (workers.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in workers.EnumerateArray())
            {
                var average = Number(Path(item, "hashrateAvg"), "24h") ?? -1;
                if (average < 0) throw new InvalidOperationException("Нет средней скорости GPU за сутки.");
                fleetRate += average;
                count++;
                if (mine
                    && item.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String
                    && string.Equals(name.GetString(), worker, StringComparison.OrdinalIgnoreCase))
                    workerRate = average;
            }
        }

        string? note = null;
        if (!mine)
        {
            workerRate = 0;
            note = enabled ? "GPU-воркер не этого ПК — его доход не приписывается." : "GPU выключен — 0 ₽.";
        }

        return new GpuQuote(
            worker,
            workerRate,
            workerRate * coinsPerRateDay * rubRate,
            fleetRate * coinsPerRateDay * rubRate,
            rubRate,
            priceTime,
            count)
        {
            Coin = "XTM",
            Unit = "g/s",
            Note = note,
        };
    }

    private async Task<(double RubRate, DateTimeOffset PriceTimeUtc)> GetCoinGeckoRubPriceAsync(
        string coinGeckoId, string symbol, CancellationToken ct)
    {
        await _priceGate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_priceCache.TryGetValue(coinGeckoId, out var cached)
                && now - cached.CachedAtUtc < CoinGeckoCacheTtl
                && IncomeMath.IsFresh(cached.PriceTimeUtc, now))
                return (cached.RubRate, cached.PriceTimeUtc);

            var price = await GetJsonAsync(
                $"https://api.coingecko.com/api/v3/simple/price?ids={Uri.EscapeDataString(coinGeckoId)}&vs_currencies=rub&include_last_updated_at=true",
                ct);
            double rubRate = 0;
            var priceTime = DateTimeOffset.MinValue;
            if (price is not null)
            {
                var coin = Path(price.Value, coinGeckoId);
                rubRate = Number(coin, "rub") ?? 0;
                priceTime = DateTimeOffset.FromUnixTimeSeconds((long)(Number(coin, "last_updated_at") ?? 0));
            }

            if (rubRate <= 0 || !IncomeMath.IsFresh(priceTime, now))
            {
                var krxRub = await GetKryptexSpotRubPriceAsync(symbol, ct);
                if (krxRub is > 0)
                {
                    rubRate = krxRub.Value;
                    priceTime = now;
                }
            }

            if (rubRate <= 0)
            {
                if (_priceCache.TryGetValue(coinGeckoId, out var fallback) && IncomeMath.IsFresh(fallback.PriceTimeUtc, now))
                    return (fallback.RubRate, fallback.PriceTimeUtc);
                throw new InvalidOperationException($"Курс {symbol}/RUB недоступен.");
            }
            if (!IncomeMath.IsFresh(priceTime, now))
                throw new InvalidOperationException($"Курс {symbol}/RUB устарел.");

            _priceCache[coinGeckoId] = (rubRate, priceTime, now);
            return (rubRate, priceTime);
        }
        finally
        {
            _priceGate.Release();
        }
    }

    private async Task<double?> GetKryptexSpotRubPriceAsync(string symbol, CancellationToken ct)
    {
        var rates = await GetJsonAsync("https://pool.kryptex.com/api/v1/rates", ct);
        if (rates is null) return null;
        var crypto = Path(rates.Value, "crypto");
        var fiat = Path(rates.Value, "fiat");
        var usd = Number(crypto, symbol.ToUpperInvariant());
        var rubPerUsd = Number(fiat, "RUB");
        return usd is > 0 && rubPerUsd is > 0 ? usd.Value * rubPerUsd.Value : null;
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

    public void Dispose()
    {
        _http.Dispose();
        _priceGate.Dispose();
    }
}
