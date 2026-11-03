using MiningSwitch.App.Services;
using Xunit;

namespace MiningSwitch.Tests;

public class NoteBannerStateTests
{
    [Fact]
    public void PollError_clears_back_to_default_hint_when_polling_recovers()
    {
        var state = new NoteBannerState();
        Assert.Equal(NoteBannerState.DefaultText, state.Text);
        Assert.False(state.IsError);

        Assert.True(state.SetPollError("Не удалось подтвердить состояние. Connection refused (127.0.0.1:47800)"));
        Assert.True(state.IsError);
        Assert.True(state.HasPollError);

        Assert.True(state.ClearPollErrorOnRecovery());
        Assert.Equal(NoteBannerState.DefaultText, state.Text);
        Assert.False(state.IsError);
        Assert.False(state.HasPollError);

        // Subsequent polls are a no-op once already recovered.
        Assert.False(state.ClearPollErrorOnRecovery());
    }

    [Fact]
    public void ControlNote_is_preserved_across_routine_polls()
    {
        var state = new NoteBannerState();

        state.SetControlNote("CPU и GPU запущены. Общая кнопка выключает оба майнера.");
        Assert.False(state.HasPollError);
        Assert.False(state.IsError);

        Assert.False(state.ClearPollErrorOnRecovery());
        Assert.Equal("CPU и GPU запущены. Общая кнопка выключает оба майнера.", state.Text);
    }
}
