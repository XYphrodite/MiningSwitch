using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MiningSwitch.App.Services;
using SelfUpdateKit;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace MiningSwitch.App;

public partial class MainWindow : Window
{
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x61, 0xE8, 0xBA));
    private static readonly Brush AccentDark = new SolidColorBrush(Color.FromRgb(0x10, 0x25, 0x1E));
    private static readonly Brush SmallSurface = new SolidColorBrush(Color.FromRgb(0x2B, 0x38, 0x4E));
    private static readonly Brush LightText = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xFA));
    private static readonly Brush Idle = new SolidColorBrush(Color.FromRgb(0xAB, 0xC4, 0xF7));
    private static readonly Brush Error = new SolidColorBrush(Color.FromRgb(0xFF, 0xAB, 0x9E));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x92, 0x9F, 0xB4));

    private readonly Func<EventWaitHandle?>? _showEventAccessor;
    private readonly TrayIcon _tray = new();
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _resourceTimer = new() { Interval = TimeSpan.FromSeconds(12) };
    private readonly DispatcherTimer _incomeTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };

    private AgentClient? _client;
    private readonly IncomeService _income = new();

    private bool _online, _busy, _running, _gpuRunning, _gpuKnown, _cpuKnown, _manualOff, _exitRequested;
    private bool _pollBusy, _incomeBusy, _updateBusy, _gpuAvailable, _gpuExecutableConfigured;
    private GpuMinerSoftware? _configuredGpuMiner;
    private DateTimeOffset _lastConfigAt = DateTimeOffset.MinValue;
    private IncomeSnapshot? _lastIncome;
    private SelfUpdateReport? _availableUpdate;

    public MainWindow(Func<EventWaitHandle?>? showEventAccessor, bool startHidden)
    {
        _showEventAccessor = showEventAccessor;
        InitializeComponent();
        Title = $"Mining Switch {MiningSwitchUpdate.DisplayVersion}";
        VersionLabel.Text = MiningSwitchUpdate.DisplayVersion;
        ComputerLabel.Text = $"{Environment.MachineName}  /  ЛОКАЛЬНОЕ УПРАВЛЕНИЕ";

        _tray.OpenRequested += ShowWindow;
        _tray.EnableRequested += () => _ = StartControlAsync(enable: true);
        _tray.DisableRequested += () => _ = StartControlAsync(enable: false);
        _tray.UpdateRequested += () => _ = HandleUpdateActionAsync(userInitiated: true);
        _tray.ExitRequested += ExitApplication;

        PowerButton.Click += (_, _) =>
            _ = StartControlAsync(enable: !_running && !_gpuRunning && _manualOff);

        _pollTimer.Tick += (_, _) => _ = PollAgentAsync();
        _resourceTimer.Tick += (_, _) => UpdateResources();
        _incomeTimer.Tick += (_, _) => _ = UpdateIncomeAsync();
        _updateTimer.Tick += (_, _) => _ = CheckForUpdateAsync(userInitiated: false);
        _pollTimer.Start();
        _resourceTimer.Start();
        _incomeTimer.Start();
        _updateTimer.Start();

        UpdateResources();
        _ = PollAgentAsync();
        _ = CheckForUpdateStartupAsync();

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
        _updateTimer.Stop();
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
                    if (config is not null)
                    {
                        _manualOff = config.ManualOff;
                        _configuredGpuMiner = config.GpuMiner?.Miner;
                        _gpuExecutableConfigured = !string.IsNullOrWhiteSpace(config.GpuMiner?.ExecutablePath);
                    }
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
                _gpuAvailable = gpu is not null && (_gpuExecutableConfigured || gpu.Running);
                var isSrMiner = (gpu?.Software ?? _configuredGpuMiner) == GpuMinerSoftware.SrMiner
                    || string.Equals(gpu?.Algorithm, "pearlhash", StringComparison.OrdinalIgnoreCase);
                if (gpu is null)
                {
                    _gpuRunning = false;
                    GpuTitleLabel.Text = "GPU · видеокарта";
                    GpuStatusLabel.Text = "GPU-майнер не поддерживается";
                    GpuStatusLabel.Foreground = Muted;
                    GpuRateLabel.Text = "—";
                }
                else if (!_gpuAvailable)
                {
                    _gpuRunning = false;
                    GpuTitleLabel.Text = "GPU · видеокарта";
                    GpuStatusLabel.Text = "Не используется на этом ПК";
                    GpuStatusLabel.Foreground = Muted;
                    GpuRateLabel.Text = "—";
                }
                else
                {
                    _gpuRunning = gpu.Running;
                    GpuTitleLabel.Text = isSrMiner ? "SRBMiner · видеокарта" : "lolMiner · видеокарта";
                    GpuStatusLabel.Text = gpu.Running ? "GPU майнит" : "GPU остановлен";
                    GpuStatusLabel.Foreground = gpu.Running ? Accent : Idle;
                    GpuRateLabel.Text = gpu.Running && gpu.Hashrate is > 0
                        ? IncomeMath.FormatGpuRate(gpu.Hashrate, gpu.HashrateUnit)
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
                $"{snapshot.Cpu.Average24:N0} H/s CPU · {IncomeMath.FormatGpuRate(gpu.Average24, gpu.Unit)} GPU в среднем за 24 ч";
            if (gpu.Note is not null) IncomeDetailLabel.Text += nl + gpu.Note;
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
        if (snapshot.Gpu is { } gpuRateQuote)
            IncomeNoteLabel.Text += $" · {gpuRateQuote.Coin} {gpuRateQuote.RubRate:N2} ₽";
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
            _lastConfigAt = DateTimeOffset.MinValue;
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
            var enableGpu = on && _gpuAvailable;
            var patch = new MinerConfigDto
            {
                AutoStartMiner = on,
                MinerWanted = on,
                MinerStoppedByPause = false,
                MinerStoppedByThrottle = false,
                GpuStoppedByPause = false,
                GpuWanted = enableGpu,
                GpuMiner = new GpuMinerSettingsDto { Enabled = enableGpu },
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
                ? (_gpuAvailable
                    ? "CPU и GPU запущены. Общая кнопка выключает оба майнера."
                    : "Майнинг на процессоре запущен.")
                : (_gpuAvailable
                    ? "CPU и GPU выключены. Автозапуск отключён до твоей команды."
                    : "Майнинг выключен. Автозапуск отключён до твоей команды."));
        else
            SetNote("Команда выполнена не полностью. Проверь статусы майнеров. " + string.Join(" ", errors), error: true);
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

        var scope = _gpuAvailable ? "CPU + GPU" : "майнинг";
        if (_running)
        {
            StatusLabel.Text = "Майнинг работает";
            StatusLabel.Foreground = Accent;
            PowerLabel.Text = $"Выключить {scope}";
            PowerButton.Background = Accent;
        }
        else
        {
            StatusLabel.Text = _manualOff ? "Майнинг выключен" : "Майнинг на паузе";
            StatusLabel.Foreground = Idle;
            PowerLabel.Text = _manualOff && !_gpuRunning ? $"Включить {scope}" : $"Выключить {scope}";
            PowerButton.Background = _gpuRunning ? Accent : Idle;
        }

        _tray.SetStatus(_running && _gpuRunning
            ? "Mining Switch — CPU и GPU работают"
            : _gpuRunning
                ? "Mining Switch — GPU работает"
                : _running
                    ? "Mining Switch — CPU работает"
                    : "Mining Switch — майнер остановлен");
        _tray.SetMenuState(
            canEnable: _online && !_busy && (!_running || (_gpuAvailable && !_gpuRunning)),
            canDisable: _online && !_busy);
    }

    private void SetNote(string text, bool error = false)
    {
        NoteLabel.Text = text;
        NoteLabel.Foreground = error ? Error : Muted;
    }

    private void OnRefreshIncome(object sender, RoutedEventArgs e) => _ = UpdateIncomeAsync();

    // ---- self-update (SelfUpdateKit) -----------------------------------

    private void OnUpdateClick(object sender, RoutedEventArgs e) =>
        _ = HandleUpdateActionAsync(userInitiated: true);

    private async Task CheckForUpdateStartupAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        await CheckForUpdateAsync(userInitiated: false);
    }

    private Task HandleUpdateActionAsync(bool userInitiated) =>
        _availableUpdate is { Status: SelfUpdateStatus.UpdateAvailable }
            ? ApplyUpdateAsync()
            : CheckForUpdateAsync(userInitiated);

    private async Task CheckForUpdateAsync(bool userInitiated)
    {
        if (_updateBusy || !MiningSwitchUpdate.CanSelfUpdate(Environment.ProcessPath))
            return;

        _updateBusy = true;
        UpdateButton.IsEnabled = false;
        _tray.SetUpdateState("Проверяем обновления…", enabled: false);
        if (userInitiated)
        {
            UpdateStatusLabel.Foreground = Muted;
            UpdateStatusLabel.Text = "Проверяем GitHub Releases…";
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var report = await MiningSwitchUpdate.CheckAsync(cts.Token);
            if (report.Status == SelfUpdateStatus.UpdateAvailable)
            {
                _availableUpdate = report;
                var label = $"Обновить до v{report.Release}";
                UpdateButton.Content = label;
                UpdateButton.Background = Accent;
                UpdateButton.Foreground = AccentDark;
                UpdateStatusLabel.Foreground = Accent;
                UpdateStatusLabel.Text = $"Доступна новая версия {report.Tag}";
                _tray.SetUpdateState(label, enabled: true);
            }
            else
            {
                _availableUpdate = null;
                ResetUpdateButtonAppearance();
                _tray.SetUpdateState("Проверить обновления", enabled: true);
                if (userInitiated)
                {
                    UpdateStatusLabel.Foreground = Muted;
                    UpdateStatusLabel.Text = $"Актуальная версия ({MiningSwitchUpdate.DisplayVersion})";
                }
            }
        }
        catch (Exception ex)
        {
            _tray.SetUpdateState(
                _availableUpdate is { Status: SelfUpdateStatus.UpdateAvailable } avail
                    ? $"Обновить до v{avail.Release}"
                    : "Проверить обновления",
                enabled: true);
            if (userInitiated)
            {
                UpdateStatusLabel.Foreground = Error;
                UpdateStatusLabel.Text = "Не удалось проверить: " + ex.Message;
            }
        }
        finally
        {
            _updateBusy = false;
            UpdateButton.IsEnabled = true;
        }
    }

    private async Task ApplyUpdateAsync()
    {
        if (_updateBusy) return;
        _updateBusy = true;
        UpdateButton.IsEnabled = false;
        _tray.SetUpdateState("Обновляем…", enabled: false);
        UpdateStatusLabel.Foreground = Muted;
        UpdateStatusLabel.Text = "Скачивание обновления…";

        try
        {
            var target = _availableUpdate;
            var report = await MiningSwitchUpdate.ApplyWithElevationFallbackAsync(
                target?.Release,
                target?.Tag,
                progress => Dispatcher.BeginInvoke(() => ShowUpdateProgress(progress)),
                CancellationToken.None);

            if (report.Status == SelfUpdateStatus.Updated)
            {
                UpdateStatusLabel.Foreground = Accent;
                UpdateStatusLabel.Text = $"Установлена {report.Tag}. Перезапуск…";
                var startInTray = !IsVisible;
                _exitRequested = true;
                Close();
                if (Application.Current is App app)
                    app.RestartIntoUpdatedBinary(startInTray);
                return;
            }

            _availableUpdate = null;
            ResetUpdateButtonAppearance();
            _tray.SetUpdateState("Проверить обновления", enabled: true);
            UpdateStatusLabel.Foreground = Muted;
            UpdateStatusLabel.Text = $"Актуальная версия ({MiningSwitchUpdate.DisplayVersion})";
        }
        catch (Exception ex)
        {
            UpdateStatusLabel.Foreground = Error;
            UpdateStatusLabel.Text = "Ошибка обновления: " + ex.Message;
            _tray.SetUpdateState(
                _availableUpdate is { } avail ? $"Обновить до v{avail.Release}" : "Проверить обновления",
                enabled: true);
        }
        finally
        {
            _updateBusy = false;
            UpdateButton.IsEnabled = true;
        }
    }

    private void ShowUpdateProgress(SelfUpdateProgress progress)
    {
        UpdateStatusLabel.Foreground = Muted;
        UpdateStatusLabel.Text = progress.Phase switch
        {
            SelfUpdatePhase.Checking => "Проверка релиза…",
            SelfUpdatePhase.Downloading when progress.TotalBytes is > 0 && progress.ReceivedBytes is { } r =>
                $"Скачивание {Math.Min(100, (int)(100.0 * r / progress.TotalBytes.Value))}%…",
            SelfUpdatePhase.Downloading => "Скачивание…",
            SelfUpdatePhase.Verifying => "Проверка SHA-256…",
            SelfUpdatePhase.Installing => "Установка и проверка запуска…",
            _ => UpdateStatusLabel.Text,
        };
    }

    private void ResetUpdateButtonAppearance()
    {
        UpdateButton.Content = "Проверить обновления";
        UpdateButton.Background = SmallSurface;
        UpdateButton.Foreground = LightText;
    }
}
