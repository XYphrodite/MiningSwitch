using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using MiningSwitch.App.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace MiningSwitch.App;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = e.Args;
        var selfTest = TryGetOption(args, "--selftest", out var reportPath);
        var tray = args.Contains("-Tray", StringComparer.OrdinalIgnoreCase);

        if (!selfTest)
        {
            _mutex = new Mutex(false, @"Local\MiningSwitch.UI");
            try { _ownsMutex = _mutex.WaitOne(0); }
            catch (AbandonedMutexException) { _ownsMutex = true; }

            if (!_ownsMutex)
            {
                // A second launch raises the window that is already there instead of a second copy.
                try
                {
                    using var existing = EventWaitHandle.OpenExisting(@"Local\MiningSwitch.Show");
                    existing.Set();
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    MessageBox.Show("Приложение уже работает. Нажми на значок возле часов.",
                        "Mining Switch", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                Shutdown();
                return;
            }
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MiningSwitch.Show");
        }

        if (selfTest)
        {
            // Off the dispatcher thread: the self-test blocks on HTTP, and a continuation
            // posted to a waiting UI thread never runs.
            var passed = Task.Run(() => RunSelfTest(reportPath)).GetAwaiter().GetResult();
            Environment.ExitCode = passed ? 0 : 1;
            Shutdown();
            return;
        }

        var window = new MainWindow(() => _showEvent, startHidden: tray);
        MainWindow = window;
        window.Closed += (_, _) => Shutdown();
        if (!tray) window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showEvent?.Dispose();
        if (_ownsMutex && _mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* never owned */ }
        }
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private static bool TryGetOption(string[] args, string name, out string value)
    {
        value = "";
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 >= args.Length) return true;
            value = args[i + 1];
            return true;
        }
        return false;
    }

    /// <summary>
    /// Headless check used by CI and remote installs: reads the agent, the pools and the
    /// resources once, writes a JSON report and exits without ever showing a window.
    /// </summary>
    private static bool RunSelfTest(string reportPath)
    {
        var report = new Dictionary<string, object?>();
        var passed = true;
        try
        {
            using var agent = new AgentClient(AgentEndpoint.Discover());
            var miner = agent.GetMinerAsync(default).GetAwaiter().GetResult();
            var config = agent.GetConfigAsync(default).GetAwaiter().GetResult();
            var gpu = agent.GetGpuAsync(default).GetAwaiter().GetResult();
            report["agent"] = "ok";
            report["minerRunning"] = miner?.Running;
            report["gpuRunning"] = gpu?.Running;

            if (miner is not null && config is not null)
            {
                try
                {
                    using var income = new IncomeService();
                    var snapshot = income.GetSnapshotAsync(miner, config, default).GetAwaiter().GetResult();
                    report["cpuRub24"] = snapshot.Cpu.WorkerRub24;
                    report["fleetRub24"] = snapshot.FleetRub24;
                    report["gpuRub24"] = snapshot.Gpu?.WorkerRub24;
                    report["gpuError"] = snapshot.GpuError;
                }
                catch (Exception ex)
                {
                    passed = false;
                    report["incomeError"] = ex.Message;
                }
            }

            var resources = ResourceMonitor.Take();
            report["cpuPercent"] = resources.CpuPercent;
            report["ramUsedGiB"] = resources.RamUsedGiB;
            report["drives"] = resources.Drives.Select(d => $"{d.Drive}={d.FreeGiB:F1}GiB").ToArray();
        }
        catch (Exception ex)
        {
            passed = false;
            report["agent"] = "failed";
            report["error"] = ex.Message;
        }

        report["passed"] = passed;
        var path = string.IsNullOrWhiteSpace(reportPath) ? "miningswitch-selftest.json" : reportPath;
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Self-test {(passed ? "OK" : "FAILED")} -> {Path.GetFullPath(path)}");
        return passed;
    }
}
