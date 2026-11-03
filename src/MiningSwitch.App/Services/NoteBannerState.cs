namespace MiningSwitch.App.Services;

/// <summary>
/// Tracks the bottom status note so transient local-agent polling errors clear back to the
/// default hint automatically once polling recovers, without wiping explicit user-action feedback.
/// </summary>
public sealed class NoteBannerState
{
    public const string DefaultText =
        "Крестик сворачивает окно к часам. Ручное выключение сохраняется после перезагрузки.";

    public string Text { get; private set; } = DefaultText;
    public bool IsError { get; private set; }
    public bool HasPollError { get; private set; }

    public bool SetPollError(string message)
    {
        HasPollError = true;
        return Update(message, isError: true);
    }

    public bool ClearPollErrorOnRecovery()
    {
        if (!HasPollError) return false;
        HasPollError = false;
        return Update(DefaultText, isError: false);
    }

    public bool SetControlNote(string message, bool isError = false)
    {
        HasPollError = false;
        return Update(message, isError);
    }

    private bool Update(string text, bool isError)
    {
        if (Text == text && IsError == isError) return false;
        Text = text;
        IsError = isError;
        return true;
    }
}
