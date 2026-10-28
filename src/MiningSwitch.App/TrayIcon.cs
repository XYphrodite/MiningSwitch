using System.Drawing;
using System.Windows.Forms;
using MiningSwitch.App.Services;

namespace MiningSwitch.App;

/// <summary>Tray icon and its menu. WinForms' NotifyIcon behind a tiny wrapper.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public event Action? OpenRequested;
    public event Action? EnableRequested;
    public event Action? DisableRequested;
    public event Action? UpdateRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem($"Открыть Mining Switch ({MiningSwitchUpdate.DisplayVersion})");
        var start = new ToolStripMenuItem("Включить майнинг");
        var stop = new ToolStripMenuItem("Выключить майнинг");
        var update = new ToolStripMenuItem("Проверить обновления");
        var exit = new ToolStripMenuItem("Закрыть приложение");
        menu.Items.Add(open);
        menu.Items.Add(start);
        menu.Items.Add(stop);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(update);
        menu.Items.Add(exit);

        open.Click += (_, _) => OpenRequested?.Invoke();
        start.Click += (_, _) => EnableRequested?.Invoke();
        stop.Click += (_, _) => DisableRequested?.Invoke();
        update.Click += (_, _) => UpdateRequested?.Invoke();
        exit.Click += (_, _) => ExitRequested?.Invoke();

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = $"Mining Switch {MiningSwitchUpdate.DisplayVersion} — подключение",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();

        StartEnabled = start;
        StopEnabled = stop;
        UpdateItem = update;
    }

    private ToolStripMenuItem StartEnabled { get; }
    private ToolStripMenuItem StopEnabled { get; }
    private ToolStripMenuItem UpdateItem { get; }

    public void SetStatus(string text) =>
        _icon.Text = text.Length <= 63 ? text : text[..63];

    /// <summary>Start/stop menu items follow the same availability rules as the big button.</summary>
    public void SetMenuState(bool canEnable, bool canDisable)
    {
        StartEnabled.Enabled = canEnable;
        StopEnabled.Enabled = canDisable;
    }

    public void SetUpdateState(string label, bool enabled)
    {
        UpdateItem.Text = label;
        UpdateItem.Enabled = enabled;
    }

    private static Icon LoadIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } path)
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null) return icon;
            }
        }
        catch (Exception) { /* fall through to the shell icon */ }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
