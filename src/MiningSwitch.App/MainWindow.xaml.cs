using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MiningSwitch.App.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace MiningSwitch.App;

public partial class MainWindow : Window
{
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x61, 0xE8, 0xBA));
    private static readonly Brush Idle = new SolidColorBrush(Color.FromRgb(0xAB, 0xC4, 0xF7));
    private static readonly Brush Error = new SolidColorBrush(Color.FromRgb(0xFF, 0xAB, 0x9E));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x92, 0x9F, 0xB4));

    private readonly Func<EventWaitHandle?>? _showEventAccessor;
    private readonly TrayIcon _tray = new();
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _resourceTimer = new() { Interval = TimeSpan.FromSeconds(12) };
    private readonly DispatcherTimer _incomeTimer = new() { Interval = TimeSpan.FromMinutes(5) };

    private AgentClient? _client;
    private readonly IncomeService _income = new();

    private bool _online, _busy, _running, _gpuRunning, _gpuKnown, _cpuKnown, _manualOff, _exitRequested;
    private bool _pollBusy, _incomeBusy, _gpuAvailable = true;
    private DateTimeOffset _lastConfigAt = DateTimeOffset.MinValue;
    private IncomeSnapshot? _lastIncome;

    public MainWindow(Func<EventWaitHandle?>? showEventAccessor, bool startHidden)
    {
        _showEventAccessor = showEventAccessor;
        InitializeComponent();
        ComputerLabel.Text = $"{Environment.MachineName}  /  ЛОКАЛЬНОЕ УПРАВЛЕНИЕ";

        _tray.OpenRequested += ShowWindow;
        _tray.EnableRequested += () => _ = StartControlAsync(enable: true);
        _tray.DisableRequested += () => _ = StartControlAsync(enable: false);
        _tray.ExitRequested += ExitApplication;

        PowerButton.Click += (_, _) =>
            _ = StartControlAsync(enable: !_running && !_gpuRunning && _manualOff);

        _pollTimer.Tick += (_, _) => _ = PollAgentAsync();
        _resourceTimer.Tick += (_, _) => UpdateResources();
        _incomeTimer.Tick += (_, _) => _ = UpdateIncomeAsync();
        _pollTimer.Start();
        _resourceTimer.Start();
        _incomeTimer.Start();

        UpdateResources();
        _ = PollAgentAsync();

        if (startHidden) Hide();
    }

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exitRequested)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _pollTimer.Stop();
        _resourceTimer.Stop();
        _incomeTimer.Stop();
        _tray.Dispose();
        _client?.Dispose();
        _income.Dispose();
        base.OnClosed(e);
    }

    // ---- agent polling -------------------------------------------------

    private async Task PollAgentAsync()
    {
        if (_pollBusy) return;
        _pollBusy = true;
        try
        {
            if (_showEventAccessor?.Invoke() is { } showEvent && showEvent.WaitOne(0))
                ShowWindow();

            if (_client is null)
            {
                try { _client = new AgentClient(AgentEndpoint.Discover()); }
                catch (Exception ex)
                {
                    SetNote(ex.Message, error: true);
                    RefreshControls();
                    return;
                }
            }

            try
            {
                var now = DateTimeOffset.Now;
                if (now - _lastConfigAt >= TimeSpan.FromSeconds(30))
                {
                    var config = await _client.GetConfigAsync(default);
                    if (config is not null) _manualOff = config.ManualOff;
                    _lastConfigAt = now;
                }

                var miner = await _client.GetMinerAsync(default);
                if (miner is not null)
                {
                    _online = true;
                    _cpuKnown = true;
                    _running = miner.Running;
                    HashrateLabel.Text = _running && miner.Hashrate10s is { } rate ? $"{rate:N0}" : "—";
                }

                var gpu = await _client.GetGpuAsync(default);
                _gpuKnown = true;
                _gpuAvailable = gpu is not null;
                if (gpu is null)
                {
                    GpuStatusLabel.Text = "GPU-майнер не поддерживается";
                    GpuStatusLabel.Foreground = Muted;
                    GpuRateLabel.Text = "—";
                }
                else
                {
                    _gpuRunning = gpu.Running;
                    GpuStatusLabel.Text = gpu.Running ? "GPU майнит" : "GPU остановлен";
                    GpuStatusLabel.Foreground = gpu.Running ? Accent : Idle;
                    GpuRateLabel.Text = gpu.Running && gpu.Hashrate is { } rate
                        ? $"{rate:N2} {gpu.HashrateUnit}"
                        : "—";
                }

                RefreshControls();

                // Income needs the agent's wallet and worker; fetch it as soon as there is a link.
                if (_online && _lastIncome is null && !_incomeBusy)
                    _ = UpdateIncomeAsync();
            }
            catch (Exception ex)
            {
                _online = false;
                SetNote("Не удалось подтвердить состояние. " + ex.Message, error: true);
                RefreshControls();
            }
        }
        finally { _pollBusy = false; }
    }

    private void UpdateResources()
    {
        try
        {
            var snapshot = ResourceMonitor.Take();
            CpuLabel.Text = $"{snapshot.CpuPercent:N0} % загрузка";
            RamLabel.Text = $"{snapshot.RamUsedGiB:N1} / {snapshot.RamTotalGiB:N0} ГБ";
            DiskLabel.Text = string.Join("  ·  ",
                snapshot.Drives.Select(d => $"{d.Drive} {d.FreeGiB:N0} ГБ"));
        }
        catch (Exception)
        {
            CpuLabel.Text = "Нет данных";
            RamLabel.Text = "Нет данных";
            DiskLabel.Text = "Нет данных";
        }
    }

    private async Task UpdateIncomeAsync()
    {
        if (_incomeBusy) return;
        _incomeBusy = true;
        RefreshIncomeButton.IsEnabled = false;
        try
        {
            if (_client is null || !_online) throw new InvalidOperationException("Нет связи с агентом.");
            var miner = await _client.GetMinerAsync(default)
                ?? throw new InvalidOperationException("Агент не вернул статус майнера.");
            var config = await _client.GetConfigAsync(default)
                ?? throw new InvalidOperationException("Агент не вернул конфигурацию.");
            var snapshot = await _income.GetSnapshotAsync(miner, config, default);
            ShowIncome(snapshot);
        }
        catch (Exception ex)
        {
            IncomeNoteLabel.Foreground = Error;
            IncomeNoteLabel.Text = ex.Message + (_lastIncome is null
                ? ""
                : " Показана последняя полученная оценка.");
            if (_lastIncome is null)
            {
                IncomeDayLabel.Text = "Нет данных";
                IncomeDetailLabel.Text = "Проверь доступность HashVault и настройки воркера.";
                FleetIncomeLabel.Text = "— ₽";
            }
        }
        finally
        {
            _incomeBusy = false;
            RefreshIncomeButton.IsEnabled = true;
            // Without any figure yet a slow retry would leave the card empty for minutes.
            _incomeTimer.Interval = _lastIncome is null
                ? TimeSpan.FromSeconds(30)
                : TimeSpan.FromMinutes(5);
        }
    }

    private void ShowIncome(IncomeSnapshot snapshot)
    {
        _lastIncome = snapshot;
        var nl = Environment.NewLine;
        var cpuRub = snapshot.Cpu.WorkerRub24;

        if (snapshot.Gpu is { } gpu)
        {
            IncomeDayLabel.Text = $"≈ {snapshot.WorkerRub24:N2} ₽ / сутки";
            IncomeDetailLabel.Text =
                $"CPU ≈ {cpuRub:N2} ₽  +  GPU ≈ {gpu.WorkerRub24:N2} ₽{nl}" +
                $"{snapshot.Cpu.Average24:N0} H/s CPU · {gpu.Average24:N2} H/s GPU в среднем за 24 ч";
            FleetIncomeLabel.Text = $"≈ {snapshot.FleetRub24:N2} ₽ / сутки";
            FleetDetailsLabel.Text =
                $"CPU ≈ {snapshot.Cpu.FleetRub24:N2} ₽ + GPU ≈ {gpu.FleetRub24:N2} ₽{nl}" +
                $"Воркеров: CPU {snapshot.Cpu.FleetWorkers}, GPU {gpu.FleetWorkers}. Включает твой ПК.";
        }
        else
        {
            IncomeDayLabel.Text = "— ₽";
            FleetIncomeLabel.Text = "— ₽";
            IncomeDetailLabel.Text = $"CPU ≈ {cpuRub:N2} ₽ · GPU: ожидаем данные пула";
            FleetDetailsLabel.Text = $"CPU всех ПК ≈ {snapshot.Cpu.FleetRub24:N2} ₽ / сутки. Общая сумма появится после данных GPU.";
        }

        IncomeNoteLabel.Foreground = Muted;
        IncomeNoteLabel.Text = $"Обновлено {snapshot.FetchedAt:HH:mm}. XMR {snapshot.Cpu.RubRate:N0} ₽";
        if (snapshot.GpuError is not null) IncomeNoteLabel.Text += " · " + snapshot.GpuError;
    }

    // ---- mining control ------------------------------------------------

    private async Task StartControlAsync(bool enable)
    {
        if (_busy || !_online) return;
        _busy = true;
        RefreshControls();
        SetNote("Отправляем команду локальному агенту…");

        var errors = new List<string>();
        try
        {
            if (enable)
            {
                await PutPolicyAsync(on: true, errors);
                if (!_running) await CommandAsync("start", _client!.StartAsync(default), errors);
                if (_gpuAvailable && !_gpuRunning) await CommandAsync("gpu-start", _client!.GpuStartAsync(default), errors);
            }
            else
            {
                await PutPolicyAsync(on: false, errors);
                if (_gpuAvailable) await CommandAsync("gpu-stop", _client!.GpuStopAsync(default), errors);
                await CommandAsync("stop", _client!.StopAsync(default), errors);
                await PutPolicyAsync(on: false, errors);
            }
            await PollAgentAsync();
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            _online = false;
        }

        _busy = false;
        CompleteControl(enable, errors);
        RefreshControls();
    }

    private async Task PutPolicyAsync(bool on, List<string> errors)
    {
        try
        {
            var patch = new MinerConfigDto
            {
                AutoStartMiner = on,
                MinerWanted = on,
                MinerStoppedByPause = false,
                MinerStoppedByThrottle = false,
                GpuStoppedByPause = false,
                GpuWanted = on,
                GpuMiner = new GpuMinerSettingsDto { Enabled = on },
            };
            await _client!.PutConfigAsync(patch, default);
        }
        catch (Exception)
        {
            errors.Add("Не подтверждена политика автозапуска.");
        }
    }

    private async Task CommandAsync(string kind, Task<CommandResultDto?> task, List<string> errors)
    {
        try
        {
            var result = await task;
            if (result is not { Ok: true }) errors.Add($"Не подтверждена команда {kind}.");
        }
        catch (Exception)
        {
            errors.Add($"Ошибка {kind}.");
        }
    }

    private void CompleteControl(bool enable, List<string> errors)
    {
        var confirmed = _cpuKnown && _gpuKnown
            && (enable
                ? _running && (!_gpuAvailable || _gpuRunning)
                : !_running && (!_gpuAvailable || !_gpuRunning) && _manualOff);
        if (confirmed)
            SetNote(enable
                ? "CPU и GPU запущены. Общая кнопка выключает оба майнера."
                : "CPU и GPU выключены. Автозапуск отключён до твоей команды.");
        else
            SetNote("Команда выполнена не полностью. Проверь статусы CPU и GPU. " + string.Join(" ", errors), error: true);
    }

    private void RefreshControls()
    {
        PowerButton.IsEnabled = _online && !_busy;

        if (_busy)
        {
            PowerLabel.Text = "Применяем…";
            _tray.SetStatus("Mining Switch — применяем команду");
            _tray.SetMenuState(canEnable: false, canDisable: false);
            return;
        }

        if (!_online)
        {
            StatusLabel.Text = "Нет связи с агентом";
            StatusLabel.Foreground = Error;
            PowerLabel.Text = "Ожидаем подключение";
            HashrateLabel.Text = "—";
            _tray.SetStatus("Mining Switch — нет связи");
            _tray.SetMenuState(canEnable: false, canDisable: false);
            return;
        }

        if (_running)
        {
            StatusLabel.Text = "Майнинг работает";
            StatusLabel.Foreground = Accent;
            PowerLabel.Text = "Выключить CPU + GPU";
            PowerButton.Background = Accent;
        }
        else
        {
            StatusLabel.Text = _manualOff ? "Майнинг выключен" : "Майнинг на паузе";
            StatusLabel.Foreground = Idle;
            PowerLabel.Text = _manualOff && !_gpuRunning ? "Включить CPU + GPU" : "Выключить CPU + GPU";
            PowerButton.Background = Idle;
        }

        _tray.SetStatus(_gpuRunning
            ? "Mining Switch — GPU работает"
            : _running
                ? "Mining Switch — майнинг работает"
                : "Mining Switch — майнер остановлен");
        _tray.SetMenuState(
            canEnable: _online && !_busy && (!_running || !_gpuRunning),
            canDisable: _online && !_busy);
    }

    private void SetNote(string text, bool error = false)
    {
        NoteLabel.Text = text;
        NoteLabel.Foreground = error ? Error : Muted;
    }

    private void OnRefreshIncome(object sender, RoutedEventArgs e) => _ = UpdateIncomeAsync();
}
