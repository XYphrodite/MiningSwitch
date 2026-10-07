using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

internal static class Launcher
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            string script = Path.Combine(root, "MiningSwitch.ps1");
            if (!File.Exists(script)) throw new FileNotFoundException("Не найден MiningSwitch.ps1 рядом с приложением.");
            bool tray = args.Length == 1 && string.Equals(args[0], "-Tray", StringComparison.OrdinalIgnoreCase);
            if (args.Length > 0 && !tray) throw new ArgumentException("Допустимый параметр: -Tray");
            var start = new ProcessStartInfo {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                Arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File \"" + script + "\"" + (tray ? " -Tray" : ""),
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using (var child = Process.Start(start)) { }
            return 0;
        }
        catch (Exception error) { MessageBox.Show(error.Message, "Mining Switch", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
    }
}
