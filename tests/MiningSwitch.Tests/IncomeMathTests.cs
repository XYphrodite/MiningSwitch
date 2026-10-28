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
    [InlineData("xtm-c29-eu.kryptex.network:7040", "xtm-c29")]
    [InlineData("c29.luckypool.io:3118", null)]
    [InlineData(null, null)]
    public void Kryptex_pool_slug_normalizes_regional_endpoints(string? poolUrl, string? expected)
    {
        Assert.Equal(expected, IncomeMath.KryptexPoolSlug(poolUrl));
    }

    [Fact]
    public void FormatGpuRate_scales_raw_hashes_and_preserves_custom_units()
    {
        var inv = CultureInfo.InvariantCulture;
        Assert.Equal("51.48 TH/s", IncomeMath.FormatGpuRate(51_479_178_915_826.5, "H/s", inv));
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
}
