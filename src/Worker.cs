// Worker.cs
// Version: 2.0.0
// Changelog:
//   2.0.0 - Validates config at startup and exits with clear errors; sends call
//           events to every enabled notifier (webhook, MQTT); starts and stops
//           notifiers with the service; logs provider/config warnings.
//   1.x   - Original worker loop with built-in webhook calls.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RtcCallMonitor.Configuration;
using RtcCallMonitor.Notifications;

namespace RtcCallMonitor
{
    /// <summary>Warnings collected while building the configuration (e.g. old-style settings).</summary>
    public class ConfigWarnings
    {
        public List<string> Items { get; } = new List<string>();
    }

    public class Worker : BackgroundService
    {
        private readonly IHostApplicationLifetime _host;
        private readonly ILogger<Worker> _logger;
        private readonly TrafficMonitor _monitor;
        private readonly ProviderCatalog _providers;
        private readonly ConfigWarnings _configWarnings;
        private readonly DetectionOptions _detection;
        private readonly WebhookOptions _webhook;
        private readonly MqttOptions _mqtt;
        private readonly List<ICallNotifier> _notifiers;

        public Worker(
            IHostApplicationLifetime host,
            ILogger<Worker> logger,
            TrafficMonitor monitor,
            ProviderCatalog providers,
            ConfigWarnings configWarnings,
            IOptions<DetectionOptions> detection,
            IOptions<WebhookOptions> webhook,
            IOptions<MqttOptions> mqtt,
            IEnumerable<ICallNotifier> notifiers)
        {
            _host = host;
            _logger = logger;
            _monitor = monitor;
            _providers = providers;
            _configWarnings = configWarnings;
            _detection = detection.Value;
            _webhook = webhook.Value;
            _mqtt = mqtt.Value;
            _notifiers = notifiers.Where(n => n.Enabled).ToList();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            foreach (var warning in _configWarnings.Items) _logger.LogWarning(warning);
            foreach (var warning in _providers.Warnings) _logger.LogWarning(warning);

            var errors = ConfigValidator.Validate(_detection, _webhook, _mqtt);
            errors.AddRange(_providers.Errors);
            if (errors.Count > 0)
            {
                _logger.LogCritical("Configuration has {Count} problem(s); fix them and start again:", errors.Count);
                foreach (var error in errors) _logger.LogCritical("  - {Error}", error);
                Environment.ExitCode = 2;
                _host.StopApplication();
                return;
            }

            foreach (var provider in _providers.Networks.OrderBy(p => p.Key))
            {
                _logger.LogInformation("loaded provider {Provider}, {Count} network prefixes", provider.Key, provider.Value.Length);
            }

            if (_notifiers.Count == 0)
            {
                _logger.LogWarning("Neither Webhook nor Mqtt is enabled: calls will only be logged.");
            }
            foreach (var notifier in _notifiers)
            {
                try { await notifier.StartAsync(stoppingToken); }
                catch (Exception e) { _logger.LogError(e, "failed to start {Notifier} output", notifier.Name); }
            }

            _monitor.OnUnknownProvider += (ip, count) => _logger.LogDebug("Unmapped network {Ip} count {Count}", ip, count);
            _monitor.OnCallStarted += OnCallStarted;
            _monitor.OnCallEnded += OnCallEnded;

            try
            {
                _monitor.Start();
            }
            catch (Exception e)
            {
                _logger.LogError(e.ToString());
                Environment.ExitCode = 1;
                await StopNotifiersAsync();
                _host.StopApplication();
                return;
            }

            _logger.LogInformation("Using check interval of {Interval}ms, min packet rate {Rate}, start after {Start} interval(s), end after {End} interval(s)",
                _detection.CheckIntervalMs, _detection.MinPacketRate, _detection.CallStartIntervals, _detection.CallEndIntervals);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(_detection.CheckIntervalMs, stoppingToken);
                        _monitor.CheckStats();
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, string.Empty);
                    }
                }
            }
            finally
            {
                await StopNotifiersAsync();
            }
        }

        private void OnCallStarted(string ip, string provider, int packetCount, DateTimeOffset startedAt)
        {
            _logger.LogInformation("call started for {Provider} on {Ip} count {Count}", provider, ip, packetCount);
            Notify(n => n.CallStartedAsync(new CallInfo { Provider = provider, Ip = ip, StartedAt = startedAt }, CancellationToken.None));
        }

        private void OnCallEnded(string provider, TimeSpan duration)
        {
            _logger.LogInformation("call ended for {Provider}, time {Seconds} seconds", provider, (int)duration.TotalSeconds);
            Notify(n => n.CallEndedAsync(new CallInfo { Provider = provider, Duration = duration }, CancellationToken.None));
        }

        private void Notify(Func<ICallNotifier, Task> send)
        {
            foreach (var notifier in _notifiers)
            {
                _ = Task.Run(async () =>
                {
                    try { await send(notifier); }
                    catch (Exception e) { _logger.LogWarning("{Notifier} output failed: {Message}", notifier.Name, e.Message); }
                });
            }
        }

        private async Task StopNotifiersAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            foreach (var notifier in _notifiers)
            {
                try { await notifier.StopAsync(timeout.Token); }
                catch (Exception e) { _logger.LogWarning("stopping {Notifier} output failed: {Message}", notifier.Name, e.Message); }
            }
        }
    }
}
