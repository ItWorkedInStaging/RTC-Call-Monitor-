// StatusPipeServer.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. Local named pipe "RtcCallMonitor" for the tray app:
//           answers "status", "pause" and "resume" (one JSON line each way).
//           On Windows the pipe is open to signed-in users of this PC only.

using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using RtcCallMonitor.Notifications;

namespace RtcCallMonitor.Ipc
{
    public class StatusPipeServer : BackgroundService
    {
        private const int MaxInstances = 4;

        private readonly CallReporter _reporter;
        private readonly NetworkListener _listener;
        private readonly ConfigWarnings _info;
        private readonly IHostEnvironment _env;
        private readonly ILogger<StatusPipeServer> _logger;
        private readonly string _pipeName;

        public StatusPipeServer(CallReporter reporter, NetworkListener listener, ConfigWarnings info, IHostEnvironment env, ILogger<StatusPipeServer> logger)
            : this(reporter, listener, info, env, logger, PipeContract.PipeName) { }

        /// <summary>For tests: a different pipe name.</summary>
        public StatusPipeServer(CallReporter reporter, NetworkListener listener, ConfigWarnings info, IHostEnvironment env, ILogger<StatusPipeServer> logger, string pipeName)
        {
            _reporter = reporter;
            _listener = listener;
            _info = info;
            _env = env;
            _logger = logger;
            _pipeName = pipeName;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                NamedPipeServerStream pipe;
                try
                {
                    pipe = CreatePipe();
                }
                catch (Exception e)
                {
                    _logger.LogWarning("tray status pipe unavailable: {Message}", e.Message);
                    return;
                }

                try
                {
                    await pipe.WaitForConnectionAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    pipe.Dispose();
                    break;
                }

                // handle this client in the background and wait for the next one
                _ = Task.Run(() => HandleAsync(pipe, stoppingToken));
            }
        }

        private NamedPipeServerStream CreatePipe()
        {
            if (OperatingSystem.IsWindows())
            {
                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                    PipeAccessRights.ReadWrite, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    PipeAccessRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    PipeAccessRights.FullControl, AccessControlType.Allow));
                // deny anything coming in over the network
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                    PipeAccessRights.FullControl, AccessControlType.Deny));

                return NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, MaxInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
            }
            return new NamedPipeServerStream(_pipeName, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }

        private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
        {
            using (pipe)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                    var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };

                    var line = await reader.ReadLineAsync(timeout.Token);
                    if (line == null) return;
                    if (line.Length > 4096) return;

                    var response = Handle(line);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(response, PipeContract.Json).AsMemory(), timeout.Token);
                }
                catch (Exception e) when (e is IOException || e is OperationCanceledException || e is ObjectDisposedException)
                {
                    // client went away; nothing to do
                }
                catch (Exception e)
                {
                    _logger.LogWarning("tray status request failed: {Message}", e.Message);
                }
            }
        }

        /// <summary>Handles one request line and returns the status (public for tests).</summary>
        public MonitorStatus Handle(string requestLine)
        {
            PipeRequest request;
            try
            {
                request = JsonSerializer.Deserialize<PipeRequest>(requestLine, PipeContract.Json) ?? new PipeRequest();
            }
            catch (JsonException)
            {
                var bad = BuildStatus();
                bad.Error = "request is not valid JSON";
                return bad;
            }

            string error = null;
            switch ((request.Command ?? "status").Trim().ToLowerInvariant())
            {
                case "status":
                    break;
                case "pause":
                    if (request.Minutes.HasValue && (request.Minutes < 1 || request.Minutes > 7 * 24 * 60))
                        error = "minutes must be between 1 and 10080";
                    else
                        _reporter.Pause(request.Minutes.HasValue ? TimeSpan.FromMinutes(request.Minutes.Value) : (TimeSpan?)null);
                    break;
                case "resume":
                    _reporter.Resume();
                    break;
                default:
                    error = $"unknown command '{request.Command}'";
                    break;
            }

            var status = BuildStatus();
            status.Error = error;
            return status;
        }

        private MonitorStatus BuildStatus()
        {
            var state = _reporter.State;
            var mqtt = _reporter.Notifiers.OfType<MqttNotifier>().FirstOrDefault();
            var capturing = _listener.Targets.Select(t => t.ToString()).ToArray();

            var problems = new System.Collections.Generic.List<string>();
            if (capturing.Length == 0) problems.Add("Not capturing on any network connection");
            if (mqtt != null && !mqtt.IsConnected) problems.Add("MQTT not connected");
            if (_reporter.Notifiers.Count == 0) problems.Add("No output enabled (MQTT or webhook)");

            return new MonitorStatus
            {
                Version = Version(),
                State = state.Paused ? "paused" : state.OnCall ? "onCall" : "idle",
                Provider = state.Provider,
                CallStartedUtc = state.CallStarted?.ToUniversalTime(),
                Paused = state.Paused,
                PausedUntilUtc = state.PausedUntil?.ToUniversalTime(),
                MqttEnabled = mqtt != null,
                MqttConnected = mqtt?.IsConnected == true,
                WebhookEnabled = _reporter.Notifiers.Any(n => n is WebhookNotifier),
                Capturing = capturing,
                Problems = problems.ToArray(),
                LogDirectory = _info.LogDirectory,
                ConfigDirectory = _env.ContentRootPath,
                RunningAsService = WindowsServiceHelpers.IsWindowsService(),
            };
        }

        private static string Version()
        {
            var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            var plus = info.IndexOf('+');
            return plus > 0 ? info.Substring(0, plus) : info;
        }
    }
}
