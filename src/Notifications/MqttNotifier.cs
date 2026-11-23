// MqttNotifier.cs
// Version: 1.1.0
// Changelog:
//   1.1.0 - Publishes the paused state (IPauseAwareNotifier) for the tray app's
//           pause/resume, retained and re-sent after reconnecting.
//   1.0.0 - Initial version. Publishes call state to MQTT (MQTTnet 5) with
//           Home Assistant discovery: retained state/provider/attributes/
//           last_duration, availability with Last Will, re-announce when HA
//           restarts (homeassistant/status), and reconnect with backoff.

using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using RtcCallMonitor.Configuration;

namespace RtcCallMonitor.Notifications
{
    public class MqttNotifier : ICallNotifier, IPauseAwareNotifier, IDisposable
    {
        private static readonly TimeSpan[] Backoff =
        {
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60)
        };

        private readonly MqttOptions _options;
        private readonly ILogger<MqttNotifier> _logger;
        private readonly MqttTopics _topics;
        private readonly string _version;
        private readonly SemaphoreSlim _publishLock = new SemaphoreSlim(1, 1);

        private IMqttClient _client;
        private MqttClientOptions _clientOptions;
        private CancellationTokenSource _loopCts;
        private Task _loop;
        private volatile bool _stopping;

        // current state, republished after every (re)connect
        private readonly object _stateLock = new object();
        private bool _onCall;
        private string _provider;
        private string _ip;
        private DateTimeOffset? _startedAt;
        private int? _lastDurationSeconds;
        private bool _paused;

        public MqttNotifier(IOptions<MqttOptions> options, ILogger<MqttNotifier> logger)
        {
            _options = options.Value;
            _logger = logger;
            _topics = new MqttTopics(_options.BaseTopic ?? "rtc-call-monitor", _options.DiscoveryPrefix ?? "homeassistant", _options.EffectiveDeviceName);
            _version = GetVersion();
        }

        public string Name => "mqtt";
        public bool Enabled => _options.Enabled;
        public bool IsConnected => _client?.IsConnected == true;
        public MqttTopics Topics => _topics;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            var factory = new MqttClientFactory();
            _client = factory.CreateMqttClient();
            _client.ApplicationMessageReceivedAsync += OnMessageAsync;
            _client.DisconnectedAsync += e =>
            {
                if (e.ClientWasConnected && !_stopping)
                {
                    _logger.LogWarning("MQTT disconnected from {Host}:{Port}: {Reason}", _options.Host, _options.Port, e.Reason);
                }
                return Task.CompletedTask;
            };

            var builder = new MqttClientOptionsBuilder()
                .WithTcpServer(_options.Host, _options.Port)
                .WithClientId($"rtc-call-monitor-{_topics.DeviceId}")
                .WithCleanSession(true)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                .WithTimeout(TimeSpan.FromSeconds(10))
                .WithWillTopic(_topics.Availability)
                .WithWillPayload(MqttTopics.Offline)
                .WithWillRetain(true)
                .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);

            if (!string.IsNullOrWhiteSpace(_options.Username))
            {
                builder.WithCredentials(_options.Username, _options.Password ?? "");
            }
            if (_options.UseTls)
            {
                builder.WithTlsOptions(tls => tls
                    .UseTls(true)
                    .WithAllowUntrustedCertificates(_options.AllowUntrustedCertificates)
                    .WithIgnoreCertificateChainErrors(_options.AllowUntrustedCertificates)
                    .WithCertificateValidationHandler(_options.AllowUntrustedCertificates ? _ => true : null));
            }

            _clientOptions = builder.Build();
            _loopCts = new CancellationTokenSource();
            _loop = Task.Run(() => ConnectionLoopAsync(_loopCts.Token));

            _logger.LogInformation("MQTT enabled: {Host}:{Port}{Tls}, topics under {Base}, HA discovery prefix {Prefix}",
                _options.Host, _options.Port, _options.UseTls ? " (TLS)" : "", _topics.Base, _topics.DiscoveryPrefix);
            return Task.CompletedTask;
        }

        private async Task ConnectionLoopAsync(CancellationToken ct)
        {
            int attempt = 0;
            while (!ct.IsCancellationRequested)
            {
                if (_client.IsConnected)
                {
                    await Delay(TimeSpan.FromSeconds(5), ct);
                    continue;
                }

                try
                {
                    var result = await _client.ConnectAsync(_clientOptions, ct);
                    if (result.ResultCode != MqttClientConnectResultCode.Success)
                    {
                        var hint = result.ResultCode == MqttClientConnectResultCode.NotAuthorized ||
                                   result.ResultCode == MqttClientConnectResultCode.BadUserNameOrPassword
                            ? " - check Mqtt.Username and Mqtt.Password"
                            : "";
                        throw new InvalidOperationException($"broker refused connection: {result.ResultCode}{hint}");
                    }

                    var subscribe = new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter(_topics.HomeAssistantStatus, MqttQualityOfServiceLevel.AtLeastOnce)
                        .Build();
                    await _client.SubscribeAsync(subscribe, ct);

                    await PublishAllAsync(ct);
                    _logger.LogInformation("MQTT connected to {Host}:{Port}", _options.Host, _options.Port);
                    attempt = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    var wait = Backoff[Math.Min(attempt, Backoff.Length - 1)];
                    // warn on the first failures, then keep quieter while the broker stays down
                    var level = attempt < 3 ? LogLevel.Warning : LogLevel.Debug;
                    _logger.Log(level, "MQTT connect to {Host}:{Port} failed: {Message} (retrying in {Seconds}s)",
                        _options.Host, _options.Port, e.Message, (int)wait.TotalSeconds);
                    attempt++;
                    await Delay(wait, ct);
                }
            }
        }

        private async Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
        {
            if (e.ApplicationMessage.Topic == _topics.HomeAssistantStatus &&
                string.Equals(e.ApplicationMessage.ConvertPayloadToString(), MqttTopics.Online, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Home Assistant came online, re-sending discovery and state");
                // don't publish from inside the receive handler
                _ = Task.Run(async () =>
                {
                    try { await PublishAllAsync(CancellationToken.None); }
                    catch (Exception ex) { _logger.LogWarning("MQTT re-announce failed: {Message}", ex.Message); }
                });
            }
            await Task.CompletedTask;
        }

        public async Task CallStartedAsync(CallInfo call, CancellationToken cancellationToken)
        {
            lock (_stateLock)
            {
                _onCall = true;
                _provider = call.Provider;
                _ip = call.Ip;
                _startedAt = call.StartedAt;
            }
            await PublishStateAsync(cancellationToken);
        }

        public async Task CallEndedAsync(CallInfo call, CancellationToken cancellationToken)
        {
            lock (_stateLock)
            {
                _onCall = false;
                _provider = null;
                _ip = null;
                _startedAt = null;
                _lastDurationSeconds = (int)(call.Duration ?? TimeSpan.Zero).TotalSeconds;
            }
            await PublishStateAsync(cancellationToken);
        }

        private async Task PublishAllAsync(CancellationToken ct)
        {
            foreach (var (topic, payload) in _topics.DiscoveryMessages(_version))
            {
                await PublishAsync(topic, payload, ct);
            }
            await PublishAsync(_topics.Availability, MqttTopics.Online, ct);
            await PublishStateAsync(ct);
        }

        public async Task SetPausedAsync(bool paused, DateTimeOffset? until, CancellationToken cancellationToken)
        {
            lock (_stateLock) _paused = paused;
            await PublishAsync(_topics.Paused, paused ? MqttTopics.On : MqttTopics.Off, cancellationToken);
        }

        private async Task PublishStateAsync(CancellationToken ct)
        {
            bool onCall; string provider, ip; DateTimeOffset? started; int? last; bool paused;
            lock (_stateLock)
            {
                onCall = _onCall; provider = _provider; ip = _ip; started = _startedAt; last = _lastDurationSeconds; paused = _paused;
            }

            await PublishAsync(_topics.Paused, paused ? MqttTopics.On : MqttTopics.Off, ct);

            await PublishAsync(_topics.State, onCall ? MqttTopics.On : MqttTopics.Off, ct);
            await PublishAsync(_topics.Provider, provider ?? MqttTopics.NoProvider, ct);
            await PublishAsync(_topics.Attributes, MqttTopics.AttributesPayload(provider, ip, started), ct);
            if (last.HasValue)
            {
                await PublishAsync(_topics.LastDuration, last.Value.ToString(), ct);
            }
        }

        private async Task PublishAsync(string topic, string payload, CancellationToken ct)
        {
            if (_client == null || !_client.IsConnected) return; // state is kept and sent after reconnecting

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithRetainFlag(true)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await _publishLock.WaitAsync(ct);
            try
            {
                await _client.PublishAsync(message, ct);
            }
            finally
            {
                _publishLock.Release();
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _stopping = true;
            _loopCts?.Cancel();
            if (_loop != null)
            {
                try { await _loop; } catch { /* loop ends on cancellation */ }
            }

            if (_client?.IsConnected == true)
            {
                try
                {
                    // a clean disconnect doesn't trigger the Last Will, so mark offline ourselves
                    await PublishAsync(_topics.Availability, MqttTopics.Offline, cancellationToken);
                    await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), cancellationToken);
                    _logger.LogInformation("MQTT disconnected cleanly");
                }
                catch (Exception e)
                {
                    _logger.LogWarning("MQTT disconnect failed: {Message}", e.Message);
                }
            }
        }

        private static async Task Delay(TimeSpan delay, CancellationToken ct)
        {
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { }
        }

        private static string GetVersion()
        {
            var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            var plus = info.IndexOf('+');
            return plus > 0 ? info.Substring(0, plus) : info;
        }

        public void Dispose()
        {
            _client?.Dispose();
            _loopCts?.Dispose();
            _publishLock.Dispose();
        }
    }
}
