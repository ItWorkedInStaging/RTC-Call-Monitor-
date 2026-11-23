// TrayContext.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. Tray icon and menu: status every 2 s from the
//           monitor's pipe, pause/resume, open log/config/README, restart the
//           service (UAC), start with Windows, about, exit.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.ServiceProcess;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using RtcCallMonitor.Ipc;

namespace RtcCallMonitor.Tray
{
    internal sealed class TrayContext : ApplicationContext
    {
        private const string ServiceName = "RTC Call Monitor";          // as created by install.ps1
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "RtcCallMonitorTray";
        private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

        private readonly NotifyIcon _icon;
        private readonly ContextMenuStrip _menu = new ContextMenuStrip();
        private readonly Timer _timer = new Timer();
        private readonly System.Collections.Generic.Dictionary<TrayState, Icon> _icons = new System.Collections.Generic.Dictionary<TrayState, Icon>();

        private readonly ToolStripMenuItem _statusItem = new ToolStripMenuItem { Enabled = false };
        private readonly ToolStripMenuItem _problemsItem = new ToolStripMenuItem { Enabled = false, Visible = false };
        private readonly ToolStripMenuItem _pauseItem = new ToolStripMenuItem("Pause detection");
        private readonly ToolStripMenuItem _resumeItem = new ToolStripMenuItem("Resume detection");
        private readonly ToolStripMenuItem _restartItem = new ToolStripMenuItem("Restart monitor");
        private readonly ToolStripMenuItem _startupItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = false };

        private MonitorStatus _last;
        private bool _polling;
        private TrayState _shown = (TrayState)(-1);

        public TrayContext()
        {
            BuildMenu();
            _icon = new NotifyIcon
            {
                ContextMenuStrip = _menu,
                Icon = IconFor(TrayState.Offline),
                Text = "RTC Call Monitor",
                Visible = true,
            };
            _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowStatusBalloon(); };

            _timer.Interval = (int)PollEvery.TotalMilliseconds;
            _timer.Tick += async (_, __) => await PollAsync();
            _timer.Start();
            _ = PollAsync();
        }

        private void BuildMenu()
        {
            _pauseItem.DropDownItems.Add("15 minutes", null, async (_, __) => await PauseAsync(15));
            _pauseItem.DropDownItems.Add("1 hour", null, async (_, __) => await PauseAsync(60));
            _pauseItem.DropDownItems.Add("4 hours", null, async (_, __) => await PauseAsync(240));
            _pauseItem.DropDownItems.Add("Until I resume", null, async (_, __) => await PauseAsync(null));
            _resumeItem.Click += async (_, __) => await SendAsync(new PipeRequest { Command = "resume" });
            _restartItem.Click += (_, __) => RestartService();
            _startupItem.Click += (_, __) => ToggleStartup();

            _menu.Items.Add(_statusItem);
            _menu.Items.Add(_problemsItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_pauseItem);
            _menu.Items.Add(_resumeItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("Open log file", null, (_, __) => OpenLogFile());
            _menu.Items.Add("Open log folder", null, (_, __) => OpenFolder(LogDirectory));
            _menu.Items.Add("Open config folder", null, (_, __) => OpenConfigFolder());
            _menu.Items.Add("Open README", null, (_, __) => OpenReadme());
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_restartItem);
            _menu.Items.Add(_startupItem);
            _menu.Items.Add("About", null, (_, __) => ShowAbout());
            _menu.Items.Add("Exit tray", null, (_, __) => ExitThread());

            _menu.Opening += (_, __) => RefreshMenu();
            RefreshMenu();
        }

        // ---------- status ----------

        private async Task PollAsync()
        {
            if (_polling) return;
            _polling = true;
            try
            {
                _last = await PipeClient.SendAsync(new PipeRequest { Command = "status" }, RequestTimeout);
            }
            catch (Exception)
            {
                _last = null; // monitor not running (or not reachable)
            }
            finally
            {
                _polling = false;
            }
            ShowStatus();
        }

        private void ShowStatus()
        {
            var state = StateOf(_last);
            if (state != _shown)
            {
                _icon.Icon = IconFor(state);
                _shown = state;
            }
            _icon.Text = Truncate("RTC Call Monitor - " + Describe(_last), 127);
            if (_menu.Visible) RefreshMenu();
        }

        private static TrayState StateOf(MonitorStatus s)
        {
            if (s == null) return TrayState.Offline;
            if (s.Paused) return TrayState.Paused;
            if (s.State == "onCall") return TrayState.OnCall;
            if (s.Problems != null && s.Problems.Length > 0) return TrayState.Problem;
            return TrayState.Idle;
        }

        private static string Describe(MonitorStatus s)
        {
            if (s == null) return "monitor not running";
            if (s.Paused)
            {
                return s.PausedUntilUtc.HasValue
                    ? $"paused until {s.PausedUntilUtc.Value.ToLocalTime():HH:mm}"
                    : "paused until resumed";
            }
            if (s.State == "onCall")
            {
                var minutes = s.CallStartedUtc.HasValue ? (int)Math.Max(0, (DateTimeOffset.UtcNow - s.CallStartedUtc.Value).TotalMinutes) : 0;
                return $"on call: {s.Provider} ({minutes} min)";
            }
            return s.Problems != null && s.Problems.Length > 0 ? "watching (" + s.Problems[0] + ")" : "watching, no call";
        }

        private void RefreshMenu()
        {
            var s = _last;
            _statusItem.Text = char.ToUpper(Describe(s)[0]) + Describe(s).Substring(1);
            _problemsItem.Visible = s?.Problems != null && s.Problems.Length > 0;
            if (_problemsItem.Visible) _problemsItem.Text = "⚠ " + string.Join("; ", s.Problems);

            _pauseItem.Enabled = s != null && !s.Paused;
            _resumeItem.Enabled = s != null && s.Paused;

            var installed = ServiceInstalled();
            _restartItem.Enabled = installed;
            _restartItem.Text = installed ? "Restart monitor" : "Restart monitor (not installed as a service)";
            _startupItem.Checked = StartsWithWindows();
        }

        private void ShowStatusBalloon()
        {
            var s = _last;
            var text = Describe(s);
            if (s?.Capturing != null && s.Capturing.Length > 0) text += "\nCapturing on: " + string.Join(", ", s.Capturing);
            if (s != null) text += "\nMQTT: " + (s.MqttEnabled ? (s.MqttConnected ? "connected" : "NOT connected") : "off") +
                                   " | Webhook: " + (s.WebhookEnabled ? "on" : "off");
            _icon.ShowBalloonTip(5000, "RTC Call Monitor", Truncate(text, 255), ToolTipIcon.None);
        }

        // ---------- commands ----------

        private Task PauseAsync(int? minutes) => SendAsync(new PipeRequest { Command = "pause", Minutes = minutes });

        private async Task SendAsync(PipeRequest request)
        {
            try
            {
                _last = await PipeClient.SendAsync(request, RequestTimeout);
                if (!string.IsNullOrEmpty(_last.Error))
                {
                    _icon.ShowBalloonTip(5000, "RTC Call Monitor", _last.Error, ToolTipIcon.Warning);
                }
            }
            catch (Exception e)
            {
                _icon.ShowBalloonTip(5000, "RTC Call Monitor", "The monitor isn't reachable: " + e.Message, ToolTipIcon.Warning);
            }
            ShowStatus();
        }

        private static bool ServiceInstalled()
        {
            try { return ServiceController.GetServices().Any(s => s.ServiceName == ServiceName); }
            catch { return false; }
        }

        private void RestartService()
        {
            try
            {
                // Restart-Service needs admin: Windows asks with a UAC prompt
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -WindowStyle Hidden -Command \"Restart-Service -Name '{ServiceName}'\"",
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
                _icon.ShowBalloonTip(3000, "RTC Call Monitor", "Restarting the monitor...", ToolTipIcon.Info);
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223)
            {
                // the user said no to the UAC prompt
            }
            catch (Exception e)
            {
                MessageBox.Show("Could not restart the monitor: " + e.Message, "RTC Call Monitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static bool StartsWithWindows()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }

        private void ToggleStartup()
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (StartsWithWindows()) key.DeleteValue(RunValue, false);
            else key.SetValue(RunValue, $"\"{Application.ExecutablePath}\"");
            _startupItem.Checked = StartsWithWindows();
        }

        // ---------- open ----------

        private string LogDirectory => _last?.LogDirectory ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RtcCallMonitor", "logs");

        private void OpenLogFile()
        {
            var dir = LogDirectory;
            var latest = Directory.Exists(dir)
                ? new DirectoryInfo(dir).GetFiles("rtc-call-monitor-*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
                : null;
            if (latest == null)
            {
                MessageBox.Show($"No log file found in {dir}.", "RTC Call Monitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Shell(latest.FullName);
        }

        private void OpenConfigFolder()
        {
            var dir = _last?.ConfigDirectory;
            if (string.IsNullOrEmpty(dir))
            {
                MessageBox.Show("Start the monitor first: its config folder is only known while it runs.", "RTC Call Monitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var local = Path.Combine(dir, "appsettings.Local.json");
            if (File.Exists(local)) Process.Start("explorer.exe", $"/select,\"{local}\"");
            else OpenFolder(dir);
        }

        private void OpenReadme()
        {
            var dir = _last?.ConfigDirectory;
            var candidates = new[]
            {
                dir == null ? null : Path.Combine(dir, "README.md"),
                dir == null ? null : Path.Combine(dir, "..", "README.md"),      // running from src with dotnet run
                Path.Combine(AppContext.BaseDirectory, "README.md"),
            };
            var readme = candidates.FirstOrDefault(p => p != null && File.Exists(p));
            if (readme == null)
            {
                MessageBox.Show("README.md not found next to the monitor.", "RTC Call Monitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Shell(Path.GetFullPath(readme));
        }

        private static void OpenFolder(string dir)
        {
            if (!Directory.Exists(dir))
            {
                MessageBox.Show($"Folder not found: {dir}", "RTC Call Monitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Shell(dir);
        }

        private static void Shell(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch (Win32Exception)
            {
                // no app registered for the file type (e.g. .md): open it in Notepad
                Process.Start("notepad.exe", $"\"{path}\"");
            }
        }

        private void ShowAbout()
        {
            var tray = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
            var monitor = _last?.Version ?? "not running";
            MessageBox.Show($"RTC Call Monitor\n\nTray app: {tray}\nMonitor: {monitor}\n" +
                            (_last != null ? $"Running as a service: {(_last.RunningAsService ? "yes" : "no")}" : ""),
                "About", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---------- housekeeping ----------

        private Icon IconFor(TrayState state)
        {
            if (!_icons.TryGetValue(state, out var icon))
            {
                icon = TrayIcons.For(state, SystemInformation.SmallIconSize.Width * 2 > 32 ? 32 : Math.Max(16, SystemInformation.SmallIconSize.Width));
                _icons[state] = icon;
            }
            return icon;
        }

        private static string Truncate(string text, int max) => text.Length <= max ? text : text.Substring(0, max - 1) + "…";

        protected override void ExitThreadCore()
        {
            _timer.Stop();
            _icon.Visible = false;
            _icon.Dispose();
            foreach (var i in _icons.Values) i.Dispose();
            _menu.Dispose();
            base.ExitThreadCore();
        }
    }
}
