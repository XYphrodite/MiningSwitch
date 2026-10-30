using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MiningSwitch.App;

/// <summary>
/// Borderless Win32 frame hook adapted from TerminalV: handles WM_GETMINMAXINFO so maximizing
/// respects the taskbar and multi-monitor DPI/MinWidth/MinHeight, and WM_NCHITTEST for 6 DIP
/// resize edges + 36 DIP custom TopBar caption drag while leaving the 3 caption buttons clickable.
/// </summary>
internal static class WindowFrame
{
    private const int WmGetMinMaxInfoMessage = 0x0024;
    private const int WmNcHitTestMessage = 0x0084;
    private const int MonitorDefaultToNearest = 2;

    private const int HtClient = 1;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    private const double ResizeBorderDip = 6.0;
    private const double CaptionHeightDip = 36.0;
    private const double CaptionButtonsWidthDip = 138.0; // 3 × 46 DIP

    public static void Hook(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                WndProc(window, h, msg, wParam, lParam, ref handled));
        };
    }

    private static IntPtr WndProc(
        Window window, IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfoMessage)
        {
            WmGetMinMaxInfo(window, hwnd, lParam);
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WmNcHitTestMessage)
        {
            var hit = HitTest(hwnd, lParam);
            if (hit != HtClient)
            {
                handled = true;
                return (IntPtr)hit;
            }
        }

        return IntPtr.Zero;
    }

    private static int HitTest(IntPtr hwnd, IntPtr lParam)
    {
        var x = (short)(lParam.ToInt32() & 0xFFFF);
        var y = (short)((lParam.ToInt32() >> 16) & 0xFFFF);
        if (!GetWindowRect(hwnd, out var rect)) return HtClient;

        const int GwlStyle = -16;
        const int WsMaximize = 0x01000000;
        var maximized = (GetWindowLong(hwnd, GwlStyle) & WsMaximize) != 0;

        var scale = HwndSource.FromHwnd(hwnd)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        if (!double.IsFinite(scale) || scale <= 0) scale = 1.0;

        var resizeBorder = (int)Math.Round(ResizeBorderDip * scale);
        var captionHeight = (int)Math.Round(CaptionHeightDip * scale);
        var captionButtonsWidth = (int)Math.Round(CaptionButtonsWidthDip * scale);

        if (!maximized)
        {
            var onLeft = x - rect.Left < resizeBorder;
            var onRight = rect.Right - x < resizeBorder;
            var onTop = y - rect.Top < resizeBorder;
            var onBottom = rect.Bottom - y < resizeBorder;

            if (onTop && onLeft) return HtTopLeft;
            if (onTop && onRight) return HtTopRight;
            if (onBottom && onLeft) return HtBottomLeft;
            if (onBottom && onRight) return HtBottomRight;
            if (onLeft) return HtLeft;
            if (onRight) return HtRight;
            if (onTop) return HtTop;
            if (onBottom) return HtBottom;
        }

        var inCaptionButtons = (rect.Right - x) < captionButtonsWidth && (y - rect.Top) < captionHeight;
        if (inCaptionButtons) return HtClient;

        if (y - rect.Top < captionHeight)
            return HtCaption;

        return HtClient;
    }

    private static void WmGetMinMaxInfo(Window window, IntPtr hwnd, IntPtr lParam)
    {
        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;

        var monitorInfo = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return;

        var work = monitorInfo.rcWork;
        var display = monitorInfo.rcMonitor;
        info.ptMaxPosition.X = Math.Abs(work.Left - display.Left);
        info.ptMaxPosition.Y = Math.Abs(work.Top - display.Top);
        info.ptMaxSize.X = Math.Abs(work.Right - work.Left);
        info.ptMaxSize.Y = Math.Abs(work.Bottom - work.Top);

        var scale = HwndSource.FromHwnd(hwnd)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        if (!double.IsFinite(scale) || scale <= 0) scale = 1.0;
        info.ptMinTrackSize.X = Math.Max(0, (int)Math.Round((double.IsFinite(window.MinWidth) ? window.MinWidth : 0) * scale));
        info.ptMinTrackSize.Y = Math.Max(0, (int)Math.Round((double.IsFinite(window.MinHeight) ? window.MinHeight : 0) * scale));

        Marshal.StructureToPtr(info, lParam, true);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point ptReserved;
        public Point ptMaxSize;
        public Point ptMaxPosition;
        public Point ptMinTrackSize;
        public Point ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }
}
