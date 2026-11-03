using System.Globalization;
using MiningSwitch.App.Services;
using Xunit;

namespace MiningSwitch.Tests;

/// <summary>
/// Ports of the earnings assertions from the earlier Test-Features.ps1: the estimate must
/// value accepted work at block reward over difficulty minus the pool fee, never invent
/// history, and refuse stale prices and foreign workers.
/// </summary>
public class IncomeMathTests
{
    [Fact]
    public void Cpu_estimate_uses_block_reward_over_difficulty_minus_pool_fee()
    {
        // 100 H/s, reward 0.6 XMR, difficulty 1,000,000, fee 0.9%, rate 40,000 ₽.
        var perHash = IncomeMath.XmrPerHash(rewardCoins: 0.6, difficulty: 1_000_000, feePercent: 0.9);
        Assert.Equal(0.6 / 1_000_000 * 0.991, perHash, precision: 12);

        var rubPerDay = IncomeMath.RubPerDay(averageHashrate: 100, perHash, rubRate: 40_000);
        Assert.Equal(100 * 86400 * (0.6 / 1_000_000 * 0.991) * 40_000, rubPerDay, precision: 6);
    }

    [Fact]
    public void Gpu_estimate_uses_the_pool_calculator_conversion()
    {
        // profit24hPer1Ghs 40 g/s-units on coinUnits 1e6, fee 0.9% → 39.64 coins per H/s-day.
        var coinsPerRateDay = IncomeMath.GpuCoinsPerHashrateDay(
            profit24hPer1Ghs: 40_000_000_000, coinUnits: 1_000_000, feePercent: 0.9);
        Assert.Equal(39.64, coinsPerRateDay, precision: 9);

        // 2 H/s average at 0.13 ₽/XTM.
        Assert.Equal(2 * 39.64 * 0.13, coinsPerRateDay * 2 * 0.13, precision: 9);
    }

    [Fact]
    public void Kryptex_pearl_estimate_deducts_pps_fee_fraction()
    {
        // 1.38e-14 PRL per H/s-day gross, 3% PPS+ fee (0.03), 50 TH/s, 138.44 ₽/PRL.
        var netCoinsPerHsDay = IncomeMath.KryptexCoinsPerHashrateDay(1.38e-14, feeFraction: 0.03);
        Assert.Equal(1.38 * 0.97, netCoinsPerHsDay * 1e14, precision: 12);

        var rubPerDay = 50e12 * netCoinsPerHsDay * 138.44;
        Assert.Equal(50e12 * 1.38e-14 * 0.97 * 138.44, rubPerDay, precision: 6);
    }

    [Theory]
    [InlineData("prl.kryptex.network:7048", "prl")]
    [InlineData("prl-eu.kryptex.network:7048", "prl")]
    [InlineData("stratum+ssl://prl-ru.kryptex.network:8048", "prl")]
    [InlineData("qtc.kryptex.network:7049", "qtc")]
    [InlineData("qtc-ru.kryptex.network:7049", "qtc")]
    [InlineData("xtm-c29-eu.kryptex.network:7040", "xtm-c29")]
    [InlineData("c29.luckypool.io:3118", null)]
    [InlineData(null, null)]
    public void Kryptex_pool_slug_normalizes_regional_endpoints(string? poolUrl, string? expected)
    {
        Assert.Equal(expected, IncomeMath.KryptexPoolSlug(poolUrl));
    }

    [Fact]
    public void Kryptex_prop_estimate_and_worker_window_selection_handle_quantus()
    {
        var netCoinsPerHsDay = IncomeMath.KryptexPropCoinsPerHashrateDay(
            netHashrate: 37_612_150_453_683.0,
            blockTimeSeconds: 12.0,
            blockReward: 0.3,
            feeFraction: 0.03);
        var expected = 86400.0 / 12.0 * 0.3 / 37_612_150_453_683.0 * 0.97;
        Assert.Equal(expected * 1e12, netCoinsPerHsDay * 1e12, precision: 9);

        var now = new DateTimeOffset(2026, 11, 3, 6, 0, 0, TimeSpan.FromHours(3));
        var opened20mAgo = now.AddMinutes(-20).ToUnixTimeMilliseconds();
        var opened5hAgo = now.AddHours(-5).ToUnixTimeMilliseconds();
        var opened2dAgo = now.AddDays(-2).ToUnixTimeMilliseconds();

        Assert.Equal(82e6, IncomeMath.SelectKryptexWorkerRate(1e6, 10e6, 82e6, opened20mAgo, now));
        Assert.Equal(75e6, IncomeMath.SelectKryptexWorkerRate(15e6, 75e6, 80e6, opened5hAgo, now));
        Assert.Equal(60e6, IncomeMath.SelectKryptexWorkerRate(60e6, 80e6, 82e6, opened2dAgo, now));
    }

    [Fact]
    public void FormatGpuRate_scales_raw_hashes_and_preserves_custom_units()
    {
        var inv = CultureInfo.InvariantCulture;
        Assert.Equal("51.48 TH/s", IncomeMath.FormatGpuRate(51_479_178_915_826.5, "H/s", inv));
        Assert.Equal("82.16 MH/s", IncomeMath.FormatGpuRate(82_160_000.0, "H/s", inv));
        Assert.Equal("0 H/s", IncomeMath.FormatGpuRate(0, "H/s", inv));
        Assert.Equal("4.52 g/s", IncomeMath.FormatGpuRate(4.52, "g/s", inv));
        Assert.Equal("—", IncomeMath.FormatGpuRate(null, "H/s", inv));
    }

    [Fact]
    public void Accepted_share_hashes_value_at_reward_over_difficulty()
    {
        var coinsPerShareHash = IncomeMath.GpuCoinsPerShareHash(
            networkReward: 10_000, coinUnits: 1_000_000, networkDifficulty: 1_000_000);
        Assert.Equal(10_000.0 / 1_000_000 / 1_000_000, coinsPerShareHash, precision: 15);
    }

    [Fact]
    public void Stale_prices_are_refused_and_fresh_ones_accepted()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(IncomeMath.IsFresh(now.AddMinutes(-30), now));
        Assert.True(IncomeMath.IsFresh(now.AddMinutes(3), now));
        Assert.False(IncomeMath.IsFresh(now.AddHours(-3), now));
        Assert.False(IncomeMath.IsFresh(now.AddMinutes(10), now));
    }

    [Fact]
    public void ExtractGpuPoolTargets_discovers_multi_coin_fleet_wallets_and_deduplicates_regions()
    {
        const string fleetJson = """
        {
          "gpuMiner": {
            "poolUrl": "prl-ru.kryptex.network:7048",
            "user": "0xAbCdEf1234567890"
          },
          "gpuProfiles": {
            "pearlhash": { "poolUrl": "prl-ru.kryptex.network:7048", "user": "" },
            "CR29": { "poolUrl": "taric29.luckypool.io:3111", "user": "" }
          },
          "nodes": [
            {
              "name": "mks68i7rtx",
              "gpuMiner": {
                "poolUrl": "prl.kryptex.network:7048",
                "user": "0xabcdef1234567890/mks68i7rtx"
              }
            },
            {
              "name": "desktop-ib88isg",
              "gpuMiner": {
                "poolUrl": "qtc-ru.kryptex.network:7049",
                "user": "qzTestWalletCaseSensitive123/desktop-ib88isg"
              }
            }
          ]
        }
        """;

        // On Xeon (primary is QTC), only PRL is returned as extra fleet GPU target (deduplicated across prl-ru and prl).
        var fromXeon = IncomeMath.ExtractGpuPoolTargets(
            fleetJson,
            excludePoolKey: "kryptex:qtc",
            excludeAddress: "qzTestWalletCaseSensitive123");
        var prl = Assert.Single(fromXeon);
        Assert.Equal("kryptex:prl", prl.PoolKey);
        Assert.Equal("0xAbCdEf1234567890", prl.Address);

        // On MKS68 (primary is PRL), only QTC is returned, with SS58 case preserved.
        var fromMks = IncomeMath.ExtractGpuPoolTargets(
            fleetJson,
            excludePoolKey: "kryptex:prl",
            excludeAddress: "0xabcdef1234567890");
        var qtc = Assert.Single(fromMks);
        Assert.Equal("kryptex:qtc", qtc.PoolKey);
        Assert.Equal("qzTestWalletCaseSensitive123", qtc.Address);
    }
}
