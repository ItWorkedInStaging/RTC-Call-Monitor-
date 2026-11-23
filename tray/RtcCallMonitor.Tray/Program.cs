// Program.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. Starts the tray app (one copy per signed-in user).

using System;
using System.Threading;
using System.Windows.Forms;

namespace RtcCallMonitor.Tray
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            using var mutex = new Mutex(true, @"Local\RtcCallMonitor.Tray", out var first);
            if (!first) return; // already running for this user

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext());
        }
    }
}
