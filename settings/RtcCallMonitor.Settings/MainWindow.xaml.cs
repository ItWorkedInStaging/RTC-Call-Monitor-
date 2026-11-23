// MainWindow.xaml.cs (RtcCallMonitor.Settings)
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. Loads the settings from the monitor (getSettings),
//           saves them (saveSettings; the monitor checks them, stores them and
//           restarts itself), tests MQTT and webhooks, and asks before closing
//           with unsaved changes. Secrets are never shown: empty password or
//           secret header boxes keep the saved value.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RtcCallMonitor.Ipc;

namespace RtcCallMonitor.Settings
{
    public sealed partial class MainWindow : Window
    {
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan SaveTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

        private sealed class HeaderRow
        {
            public Grid Root;
            public TextBox Name;
            public TextBox Value;        // plain headers
            public PasswordBox Secret;   // headers whose value is saved encrypted
        }

        private sealed class CustomRow
        {
            public Border Root;
            public TextBox Name;
            public TextBox Ranges;
        }

        private readonly List<HeaderRow> _headers = new List<HeaderRow>();
        private readonly List<CustomRow> _custom = new List<CustomRow>();
        private SettingsDocument _loaded;
        private string _loadedJson;
        private string[] _defaultExcludes = Array.Empty<string>();
        private bool _busy;
        private bool _closeConfirmed;

        public MainWindow()
        {
            InitializeComponent();
            Title = App.WindowTitle;
            SystemBackdrop = new MicaBackdrop();

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var scale = GetDpiForWindow(hwnd) / 96.0;
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(980 * scale), (int)(780 * scale)));
            AppWindow.Closing += OnClosing;

            Nav.SelectedItem = NavMqtt;

            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += (_, __) => UpdateFooter();
            timer.Start();

            if (Content is FrameworkElement root)
            {
                root.Loaded += async (_, __) => await LoadAsync();
            }
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        // ------------------------------------------------------------------ pipe

        private static async Task<SettingsResult> SendAsync(PipeRequest request, TimeSpan timeout)
        {
            var result = await PipeClient.SendAsync<SettingsResult>(request, timeout);
            if (result == null) return new SettingsResult { Ok = false, Message = "No answer from the monitor." };
            if (!result.Ok && string.IsNullOrEmpty(result.Message) && (result.Errors == null || result.Errors.Count == 0))
            {
                // a monitor older than 3.0 answers with its status and "unknown command"
                result.Message = "The monitor is an older version without the settings window. Install the latest release.";
            }
            return result;
        }

        private static bool IsMonitorDown(Exception e) =>
            e is TimeoutException || e is OperationCanceledException || e is IOException || e is UnauthorizedAccessException;

        // ------------------------------------------------------------------ load

        private async Task LoadAsync()
        {
            SetBusy(true, "Loading the settings...");
            try
            {
                var result = await SendAsync(new PipeRequest { Command = "getSettings" }, ReadTimeout);
                if (!result.Ok)
                {
                    ShowBanner(InfoBarSeverity.Error, "The settings can't be opened", result.Message, retry: true);
                    return;
                }
                Show(result.Settings);
                Banner.IsOpen = false;
            }
            catch (Exception e) when (IsMonitorDown(e))
            {
                ShowBanner(InfoBarSeverity.Error, "The monitor isn't running",
                    "Settings are read and saved by the monitor. Start it from the tray menu (Restart monitor), then press Retry.", retry: true);
            }
            catch (Exception e)
            {
                ShowBanner(InfoBarSeverity.Error, "The settings can't be opened", e.Message, retry: true);
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        private void Show(SettingsDocument doc)
        {
            if (doc == null) return;
            _loaded = doc;

            // MQTT
            var m = doc.Mqtt ?? new MqttSettings();
            MqttEnabled.IsOn = m.Enabled;
            MqttHost.Text = m.Host ?? "";
            MqttPort.Value = m.Port;
            MqttUser.Text = m.Username ?? "";
            MqttPassword.Password = "";
            MqttPassword.PlaceholderText = m.PasswordSet ? "Saved (leave empty to keep it)" : "";
            MqttClearPassword.IsChecked = false;
            MqttClearPassword.Visibility = m.PasswordSet ? Visibility.Visible : Visibility.Collapsed;
            MqttTls.IsOn = m.UseTls;
            MqttUntrusted.IsChecked = m.AllowUntrustedCertificates;
            MqttUntrusted.IsEnabled = m.UseTls;
            MqttDevice.Text = m.DeviceName ?? "";
            MqttDevice.PlaceholderText = m.DefaultDeviceName ?? "";
            MqttBaseTopic.Text = m.BaseTopic ?? "";
            MqttDiscovery.Text = m.DiscoveryPrefix ?? "";
            MqttTestResult.Text = "";

            // Webhooks
            var w = doc.Webhook ?? new WebhookSettings();
            WebhookEnabled.IsOn = w.Enabled;
            WebhookStart.Text = w.CallStartUrl ?? "";
            WebhookEnd.Text = w.CallEndUrl ?? "";
            HeadersPanel.Children.Clear();
            _headers.Clear();
            foreach (var h in w.Headers ?? new List<WebhookHeader>()) AddHeaderRow(h.Name, h.Value, h.IsSecret);
            WebhookTestResult.Text = "";

            // Detection
            var d = doc.Detection ?? new DetectionSettings();
            CheckInterval.Value = d.CheckIntervalMs;
            MinRate.Value = d.MinPacketRate;
            StartIntervals.Value = d.CallStartIntervals;
            EndIntervals.Value = d.CallEndIntervals;
            IgnorePorts.Text = string.Join(", ", (d.IgnoreUdpPorts ?? Array.Empty<int>()).Where(p => p != 0));
            LocalNetworks.Text = JoinLines(d.LocalNetworks);
            CaptureIPv6.IsOn = d.CaptureIPv6;
            Interfaces.Text = JoinLines(d.Interfaces);
            _defaultExcludes = d.DefaultExcludeInterfaces ?? Array.Empty<string>();
            UseDefaultExcludes.IsChecked = d.ExcludeInterfaces == null;
            Excludes.Text = JoinLines(d.ExcludeInterfaces ?? _defaultExcludes);
            Excludes.IsEnabled = d.ExcludeInterfaces != null;

            // Providers
            var p = doc.Providers ?? new ProviderSettings();
            AutoUpdate.IsOn = p.AutoUpdateEnabled;
            AutoUpdateHours.Value = p.AutoUpdateIntervalHours;
            LoadedPanel.Children.Clear();
            foreach (var l in p.Loaded ?? new List<LoadedProvider>())
            {
                LoadedPanel.Children.Add(new TextBlock
                {
                    Text = $"{l.Name}  ·  {l.Ranges:N0} range{(l.Ranges == 1 ? "" : "s")}  ·  {l.Source}",
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            if (LoadedPanel.Children.Count == 0) LoadedPanel.Children.Add(new TextBlock { Text = "No provider lists are loaded." });
            CustomPanel.Children.Clear();
            _custom.Clear();
            foreach (var c in p.Custom ?? new List<CustomProvider>()) AddCustomRow(c.Name, c.Ranges);

            _loadedJson = Snapshot();
            SaveButton.IsEnabled = true;
            RevertButton.IsEnabled = true;
            UpdateFooter();
        }

        // ------------------------------------------------------------------ collect

        /// <summary>The settings as shown, plus problems that can be found without the monitor.</summary>
        private (SettingsDocument Doc, List<string> Errors) Collect()
        {
            var errors = new List<string>();
            var doc = new SettingsDocument
            {
                Mqtt = CollectMqtt(),
                Webhook = new WebhookSettings
                {
                    Enabled = WebhookEnabled.IsOn,
                    CallStartUrl = WebhookStart.Text.Trim(),
                    CallEndUrl = WebhookEnd.Text.Trim(),
                    Headers = CollectHeaders(),
                },
                Detection = new DetectionSettings
                {
                    CheckIntervalMs = Int(CheckInterval, 1000),
                    MinPacketRate = Int(MinRate, 1),
                    CallStartIntervals = Int(StartIntervals, 2),
                    CallEndIntervals = Int(EndIntervals, 3),
                    IgnoreUdpPorts = Ports(IgnorePorts.Text, errors),
                    LocalNetworks = Lines(LocalNetworks.Text),
                    CaptureIPv6 = CaptureIPv6.IsOn,
                    Interfaces = Lines(Interfaces.Text),
                    ExcludeInterfaces = UseDefaultExcludes.IsChecked == true ? null : Lines(Excludes.Text),
                },
                Providers = new ProviderSettings
                {
                    AutoUpdateEnabled = AutoUpdate.IsOn,
                    AutoUpdateIntervalHours = Int(AutoUpdateHours, 24),
                    Custom = _custom.Select(c => new CustomProvider { Name = c.Name.Text.Trim(), Ranges = Lines(c.Ranges.Text).ToList() })
                                    .Where(c => c.Name.Length > 0 || c.Ranges.Count > 0)
                                    .ToList(),
                },
            };
            return (doc, errors);
        }

        private MqttSettings CollectMqtt()
        {
            string newPassword = null;                                   // keep the saved one
            if (MqttClearPassword.IsChecked == true) newPassword = "";
            else if (MqttPassword.Password.Length > 0) newPassword = MqttPassword.Password;

            return new MqttSettings
            {
                Enabled = MqttEnabled.IsOn,
                Host = MqttHost.Text.Trim(),
                Port = Int(MqttPort, 1883),
                Username = MqttUser.Text.Trim(),
                NewPassword = newPassword,
                UseTls = MqttTls.IsOn,
                AllowUntrustedCertificates = MqttTls.IsOn && MqttUntrusted.IsChecked == true,
                DeviceName = MqttDevice.Text.Trim(),
                BaseTopic = MqttBaseTopic.Text.Trim(),
                DiscoveryPrefix = MqttDiscovery.Text.Trim(),
            };
        }

        private List<WebhookHeader> CollectHeaders() =>
            _headers.Where(h => h.Name.Text.Trim().Length > 0)
                    .Select(h => new WebhookHeader
                    {
                        Name = h.Name.Text.Trim(),
                        // secret: empty keeps the saved value
                        Value = h.Secret != null ? (h.Secret.Password.Length == 0 ? null : h.Secret.Password) : h.Value.Text,
                    })
                    .ToList();

        private string Snapshot() => JsonSerializer.Serialize(Collect().Doc, PipeContract.Json);

        private bool IsDirty() => _loaded != null && _loadedJson != null && Snapshot() != _loadedJson;

        private static int Int(NumberBox box, int fallback) =>
            double.IsNaN(box.Value) ? fallback : (int)Math.Round(box.Value);

        private static string[] Lines(string text) =>
            (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

        private static string JoinLines(IEnumerable<string> lines) => string.Join("\r", lines ?? Array.Empty<string>());

        private static int[] Ports(string text, List<string> errors)
        {
            var ports = new List<int>();
            foreach (var part in (text ?? "").Split(new[] { ',', ';', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part, out var port) && port >= 1 && port <= 65535) ports.Add(port);
                else errors.Add($"Detection: '{part}' is not a port number (1-65535).");
            }
            return ports.Count == 0 ? new[] { 0 } : ports.Distinct().ToArray(); // [0] = ignore none
        }

        // ------------------------------------------------------------------ rows

        private void AddHeaderRow(string name, string value, bool secret)
        {
            var row = new HeaderRow { Root = new Grid { ColumnSpacing = 8 } };
            row.Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            row.Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Name = new TextBox { Text = name ?? "", PlaceholderText = "Header name, e.g. Authorization" };
            row.Root.Children.Add(row.Name);

            FrameworkElement valueBox;
            if (secret)
            {
                row.Secret = new PasswordBox { PlaceholderText = "Saved (leave empty to keep it)" };
                valueBox = row.Secret;
            }
            else
            {
                row.Value = new TextBox { Text = value ?? "", PlaceholderText = "Value" };
                valueBox = row.Value;
            }
            Grid.SetColumn(valueBox, 1);
            row.Root.Children.Add(valueBox);

            var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 } };
            ToolTipService.SetToolTip(remove, "Remove this header");
            remove.Click += (_, __) =>
            {
                HeadersPanel.Children.Remove(row.Root);
                _headers.Remove(row);
            };
            Grid.SetColumn(remove, 2);
            row.Root.Children.Add(remove);

            _headers.Add(row);
            HeadersPanel.Children.Add(row.Root);
        }

        private void AddCustomRow(string name, IEnumerable<string> ranges)
        {
            var row = new CustomRow();
            var panel = new StackPanel { Spacing = 8 };

            var top = new Grid { ColumnSpacing = 8 };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Name = new TextBox
            {
                Header = "Name",
                Text = name ?? "",
                PlaceholderText = "e.g. 3cx (letters, digits, - and _)",
                Description = "Reported as the call's provider. Same name as a bundled list: these ranges are added to it.",
            };
            top.Children.Add(row.Name);
            var remove = new Button { Content = "Remove", VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 28, 0, 0) };
            remove.Click += (_, __) =>
            {
                CustomPanel.Children.Remove(row.Root);
                _custom.Remove(row);
            };
            Grid.SetColumn(remove, 1);
            top.Children.Add(remove);
            panel.Children.Add(top);

            row.Ranges = new TextBox
            {
                Header = "Addresses and ranges (one per line)",
                Text = JoinLines(ranges),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 96,
                PlaceholderText = "203.0.113.10\r203.0.113.0/28",
            };
            panel.Children.Add(row.Ranges);

            row.Root = new Border
            {
                Child = panel,
                Padding = new Thickness(16),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
                Background = ThemeBrush("CardBackgroundFillColorDefaultBrush"),
            };
            _custom.Add(row);
            CustomPanel.Children.Add(row.Root);
        }

        // ------------------------------------------------------------------ events

        private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            var tag = (args.SelectedItemContainer?.Tag as string) ?? "mqtt";
            PageMqtt.Visibility = tag == "mqtt" ? Visibility.Visible : Visibility.Collapsed;
            PageWebhooks.Visibility = tag == "webhooks" ? Visibility.Visible : Visibility.Collapsed;
            PageDetection.Visibility = tag == "detection" ? Visibility.Visible : Visibility.Collapsed;
            PageProviders.Visibility = tag == "providers" ? Visibility.Visible : Visibility.Collapsed;
            PageScroller.ChangeView(null, 0, null, disableAnimation: true);
        }

        private void MqttTls_Toggled(object sender, RoutedEventArgs e)
        {
            MqttUntrusted.IsEnabled = MqttTls.IsOn;
        }

        private void UseDefaultExcludes_Click(object sender, RoutedEventArgs e)
        {
            var useDefaults = UseDefaultExcludes.IsChecked == true;
            Excludes.IsEnabled = !useDefaults;
            if (useDefaults) Excludes.Text = JoinLines(_defaultExcludes);
        }

        private void AddHeader_Click(object sender, RoutedEventArgs e)
        {
            AddHeaderRow("", "", secret: false);
            _headers.Last().Name.Focus(FocusState.Programmatic);
        }

        private void AddCustom_Click(object sender, RoutedEventArgs e)
        {
            AddCustomRow("", Array.Empty<string>());
            _custom.Last().Name.Focus(FocusState.Programmatic);
        }

        private async void MqttTest_Click(object sender, RoutedEventArgs e)
        {
            MqttTestButton.IsEnabled = false;
            MqttTestRing.IsActive = true;
            MqttTestResult.Text = "Connecting...";
            MqttTestResult.ClearValue(TextBlock.ForegroundProperty);
            try
            {
                var result = await SendAsync(new PipeRequest { Command = "testMqtt", Mqtt = CollectMqtt() }, TestTimeout);
                ShowResult(MqttTestResult, result.Ok, result.Message);
            }
            catch (Exception ex) when (IsMonitorDown(ex))
            {
                ShowResult(MqttTestResult, false, "The monitor isn't running; the test runs in the monitor.");
            }
            finally
            {
                MqttTestButton.IsEnabled = true;
                MqttTestRing.IsActive = false;
            }
        }

        private async void WebhookTest_Click(object sender, RoutedEventArgs e)
        {
            WebhookTestButton.IsEnabled = false;
            WebhookTestRing.IsActive = true;
            WebhookTestResult.Text = "Sending...";
            WebhookTestResult.ClearValue(TextBlock.ForegroundProperty);
            try
            {
                var request = new PipeRequest
                {
                    Command = "testWebhook",
                    Webhook = new WebhookSettings { CallStartUrl = WebhookStart.Text.Trim(), Headers = CollectHeaders() },
                };
                var result = await SendAsync(request, TestTimeout);
                ShowResult(WebhookTestResult, result.Ok, result.Message);
            }
            catch (Exception ex) when (IsMonitorDown(ex))
            {
                ShowResult(WebhookTestResult, false, "The monitor isn't running; the test is sent by the monitor.");
            }
            finally
            {
                WebhookTestButton.IsEnabled = true;
                WebhookTestRing.IsActive = false;
            }
        }

        private void Revert_Click(object sender, RoutedEventArgs e)
        {
            if (_loaded != null) Show(_loaded);
            Banner.IsOpen = false;
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            await SaveAsync();
        }

        /// <summary>Returns true when the monitor accepted the settings.</summary>
        private async Task<bool> SaveAsync()
        {
            if (_busy || _loaded == null) return false;
            var (doc, errors) = Collect();
            if (errors.Count > 0)
            {
                ShowBanner(InfoBarSeverity.Error, "Not saved", string.Join("\n", errors));
                return false;
            }

            SetBusy(true, "Saving...");
            try
            {
                var result = await SendAsync(new PipeRequest { Command = "saveSettings", Settings = doc }, SaveTimeout);
                if (!result.Ok)
                {
                    var problems = result.Errors != null && result.Errors.Count > 0 ? string.Join("\n", result.Errors) : result.Message;
                    ShowBanner(InfoBarSeverity.Error, "Not saved", problems);
                    return false;
                }

                Show(result.Settings);
                ShowBanner(InfoBarSeverity.Informational, "Saved", result.Message);
                _ = WaitForRestartAsync();
                return true;
            }
            catch (Exception ex) when (IsMonitorDown(ex))
            {
                ShowBanner(InfoBarSeverity.Error, "Not saved", "The monitor isn't running. Start it from the tray menu (Restart monitor) and save again.");
                return false;
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        /// <summary>After a save the monitor restarts; tell the user when it is back.</summary>
        private async Task WaitForRestartAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            for (int i = 0; i < 30; i++)
            {
                try
                {
                    var status = await PipeClient.SendAsync(new PipeRequest { Command = "status" }, TimeSpan.FromSeconds(2));
                    if (status != null)
                    {
                        var problems = status.Problems != null && status.Problems.Length > 0 ? " Check: " + string.Join("; ", status.Problems) + "." : "";
                        ShowBanner(problems.Length == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                            "Saved", "The monitor is running with the new settings." + problems);

                        // refresh what is loaded (e.g. a new provider list), unless the user is already editing again
                        if (!IsDirty())
                        {
                            var fresh = await SendAsync(new PipeRequest { Command = "getSettings" }, ReadTimeout);
                            if (fresh.Ok && !IsDirty()) Show(fresh.Settings);
                        }
                        return;
                    }
                }
                catch (Exception)
                {
                    // still restarting
                }
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
            ShowBanner(InfoBarSeverity.Warning, "Saved",
                "The settings are saved, but the monitor hasn't come back yet. Check the tray icon, or use Restart monitor.");
        }

        private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_closeConfirmed || !IsDirty()) return;
            args.Cancel = true;

            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Save your changes?",
                Content = "Some settings were changed but not saved.",
                PrimaryButtonText = "Save",
                SecondaryButtonText = "Don't save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            var choice = await dialog.ShowAsync();
            if (choice == ContentDialogResult.None) return;
            if (choice == ContentDialogResult.Primary && !await SaveAsync()) return;

            _closeConfirmed = true;
            Close();
        }

        // ------------------------------------------------------------------ helpers

        private void SetBusy(bool busy, string text)
        {
            _busy = busy;
            BusyRing.IsActive = busy;
            SaveButton.IsEnabled = !busy && _loaded != null;
            RevertButton.IsEnabled = !busy && _loaded != null;
            if (text != null) FooterText.Text = text;
            else UpdateFooter();
        }

        private void UpdateFooter()
        {
            if (_busy) return;
            if (_loaded == null) { FooterText.Text = ""; return; }
            FooterText.Text = IsDirty() ? "Unsaved changes" : $"Saved in {_loaded.ConfigDirectory}";
        }

        private void ShowBanner(InfoBarSeverity severity, string title, string message, bool retry = false)
        {
            Banner.Severity = severity;
            Banner.Title = title ?? "";
            Banner.Message = message ?? "";
            if (retry)
            {
                var button = new Button { Content = "Retry" };
                button.Click += async (_, __) => await LoadAsync();
                Banner.ActionButton = button;
            }
            else
            {
                Banner.ActionButton = null;
            }
            Banner.IsOpen = true;
        }

        private static void ShowResult(TextBlock target, bool ok, string message)
        {
            target.Text = (ok ? "✓ " : "✗ ") + (message ?? "");
            var brush = ThemeBrush(ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush");
            if (brush != null) target.Foreground = brush;
        }

        private static Brush ThemeBrush(string key) =>
            Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;
    }
}
