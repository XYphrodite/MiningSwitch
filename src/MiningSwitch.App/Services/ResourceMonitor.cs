using System.IO;
using System.Runtime.InteropServices;

namespace MiningSwitch.App.Services;

/// <summary>CPU load, RAM and free disk space for the dashboard tiles.</summary>
public sealed record ResourceSnapshot(
    double CpuPercent,
    double RamUsedGiB,
    double RamTotalGiB,
    IReadOnlyList<(string Drive, double FreeGiB)> Drives,
    DateTimeOffset TakenAt);

public static class ResourceMonitor
{
    public static ResourceSnapshot Take()
    {
        var status = MemoryStatus();
        return new ResourceSnapshot(
            ReadCpuPercent(),
            (double)(status.ullTotalPhys - status.ullAvailPhys) / 1024 / 1024 / 1024,
            (double)status.ullTotalPhys / 1024 / 1024 / 1024,
            ReadDrives(),
            DateTimeOffset.Now);
    }

    private static List<(string, double)> ReadDrives()
    {
        var result = new List<(string, double)>();
        foreach (var name in new[] { "C:", "D:" })
        {
            try
            {
                var drive = new DriveInfo(name);
                if (drive.IsReady)
                    result.Add((name, drive.AvailableFreeSpace / 1024.0 / 1024 / 1024));
            }
            catch (ArgumentException) { /* drive does not exist on this machine */ }
        }
        return result;
    }

    private static double _lastBusy;
    private static long _lastIdle, _lastKernel, _lastUser;
    private static DateTimeOffset _lastCpuAt = DateTimeOffset.MinValue;

    private static double ReadCpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return _lastBusy;

        long idleDelta = (long)(idle - _lastIdle);
        long totalDelta = (long)((kernel - _lastKernel) + (user - _lastUser));
        if (_lastCpuAt != DateTimeOffset.MinValue && totalDelta > 0)
            _lastBusy = Math.Clamp((1.0 - (double)idleDelta / totalDelta) * 100.0, 0, 100);
        _lastIdle = (long)idle; _lastKernel = (long)kernel; _lastUser = (long)user;
        _lastCpuAt = DateTimeOffset.Now;
        return _lastBusy;
    }

    private static MEMORYSTATUSEX MemoryStatus()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref status);
        return status;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}
