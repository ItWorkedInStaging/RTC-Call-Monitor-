// App.xaml.cs (RtcCallMonitor.Settings)
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. One settings window at a time: a second launch
//           brings the open window to the front and exits.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Xaml;

namespace RtcCallMonitor.Settings
{
    public partial class App : Application
    {
        public const string WindowTitle = "RTC Call Monitor settings";

        private static Mutex _single;
        private Window _window;

        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _single = new Mutex(true, @"Local\RtcCallMonitor.Settings", out var first);
            if (!first)
            {
                BringExistingToFront();
                Exit();
                return;
            }

            _window = new MainWindow();
            _window.Activate();
        }

        private static void BringExistingToFront()
        {
            var hwnd = FindWindow(null, WindowTitle);
            if (hwnd == IntPtr.Zero) return;
            ShowWindow(hwnd, 9 /* SW_RESTORE */);
            SetForegroundWindow(hwnd);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
