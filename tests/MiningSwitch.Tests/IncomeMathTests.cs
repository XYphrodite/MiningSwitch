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
