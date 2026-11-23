// StatusPipeServer.cs
// Version: 2.0.0
// Changelog:
//   2.0.0 - Settings commands for the settings window: getSettings,
//           saveSettings (then restarts the monitor), testMqtt, testWebhook,
//           restart. Only accounts allowed by EditorPolicy may use them; the
//           caller is identified from the pipe. Requests up to 512 KB.
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
using RtcCallMonitor.Configuration;
using RtcCallMonitor.Notifications;
using RtcCallMonitor.Settings;

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
        private readonly SettingsStore _settings;
        private readonly EditorPolicy _editors;
        private readonly RestartCoordinator _restart;

        public StatusPipeServer(CallReporter reporter, NetworkListener listener, ConfigWarnings info, IHostEnvironment env,
            ILogger<StatusPipeServer> logger, SettingsStore settings, EditorPolicy editors, RestartCoordinator restart)
            : this(reporter, listener, info, env, logger, PipeContract.PipeName, settings, editors, restart) { }

        /// <summary>For tests: a different pipe name; settings, editors and restart may be null (no settings commands).</summary>
        public StatusPipeServer(CallReporter reporter, NetworkListener listener, ConfigWarnings info, IHostEnvironment env,
            ILogger<StatusPipeServer> logger, string pipeName,
            SettingsStore settings = null, EditorPolicy editors = null, RestartCoordinator restart = null)
        {
            _reporter = reporter;
            _listener = listener;
            _info = info;
            _env = env;
            _logger = logger;
            _pipeName = pipeName;
            _settings = settings;
            _editors = editors;
            _restart = restart;
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
                timeout.CancelAfter(TimeSpan.FromSeconds(20)); // testMqtt can take up to 10 s
                try
                {
                    var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

                    var line = await ReadLineAsync(pipe, PipeContract.MaxRequestBytes, timeout.Token);
                    if (line == null) return;

                    var caller = IdentifyCaller(pipe);
                    var response = await HandleAsync(line, caller, timeout.Token);
                    await writer.WriteLineAsync(response.AsMemory(), timeout.Token);
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

        /// <summary>Reads one UTF-8 line of at most maxBytes; null if the client went away or sent too much.</summary>
        private static async Task<string> ReadLineAsync(Stream stream, int maxBytes, CancellationToken ct)
        {
            var buffer = new MemoryStream();
            var chunk = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(), ct);
                if (read == 0) return buffer.Length > 0 ? Encoding.UTF8.GetString(buffer.ToArray()) : null;
                var newline = Array.IndexOf(chunk, (byte)'\n', 0, read);
                buffer.Write(chunk, 0, newline >= 0 ? newline : read);
                if (buffer.Length > maxBytes) return null;
                if (newline >= 0) return Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r');
            }
        }

        /// <summary>The Windows account of the pipe client (null if it cannot be told).</summary>
        private Caller IdentifyCaller(NamedPipeServerStream pipe)
        {
            if (!OperatingSystem.IsWindows())
            {
                return new Caller { Name = Environment.UserName, IsSelf = true }; // tests / non-Windows development
            }
            try
            {
                return IdentifyWindowsCaller(pipe);
            }
            catch (Exception e)
            {
                _logger.LogDebug("could not identify the pipe client: {Message}", e.Message);
                return null;
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static Caller IdentifyWindowsCaller(NamedPipeServerStream pipe)
        {
            Caller caller = null;
            pipe.RunAsClient(() =>
            {
                using var id = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                caller = Describe(id);
            });
            return caller;
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static Caller Describe(WindowsIdentity id)
        {
            using var self = WindowsIdentity.GetCurrent();
            return new Caller
            {
                Sid = id.User?.Value,
                Name = id.Name,
                IsElevatedAdmin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator),
                IsSelf = id.User != null && id.User.Equals(self.User),
            };
        }

        /// <summary>Handles one request line from caller and returns the JSON answer (public for tests).</summary>
        public async Task<string> HandleAsync(string requestLine, Caller caller, CancellationToken ct = default)
        {
            PipeRequest request;
            try
            {
                request = JsonSerializer.Deserialize<PipeRequest>(requestLine, PipeContract.Json) ?? new PipeRequest();
            }
            catch (JsonException)
            {
                return Json(Handle(requestLine));
            }

            var command = (request.Command ?? "status").Trim().ToLowerInvariant();
            switch (command)
            {
                case "getsettings":
                case "savesettings":
                case "testmqtt":
                case "testwebhook":
                case "restart":
                    return Json(await HandleSettingsAsync(command, request, caller, ct));
                default:
                    return Json(Handle(requestLine));
            }
        }

        private static string Json<T>(T value) => JsonSerializer.Serialize(value, PipeContract.Json);

        private async Task<SettingsResult> HandleSettingsAsync(string command, PipeRequest request, Caller caller, CancellationToken ct)
        {
            if (_settings == null || _editors == null)
            {
                return Fail("Settings cannot be changed in this mode.");
            }
            var denied = _editors.Check(caller);
            if (denied != null)
            {
                _logger.LogWarning("settings request '{Command}' from {Caller} refused", command, caller?.Name ?? "an unknown account");
                return Fail(denied);
            }

            try
            {
                switch (command)
                {
                    case "getsettings":
                        return new SettingsResult { Ok = true, Settings = _settings.Read() };

                    case "savesettings":
                    {
                        var errors = _settings.Save(request.Settings);
                        if (errors.Count > 0) return new SettingsResult { Ok = false, Errors = errors, Message = "Not saved." };
                        _logger.LogInformation("settings saved by {Caller}", caller?.Name);
                        var restarting = _restart?.RequestRestart("settings changed") == true;
                        return new SettingsResult
                        {
                            Ok = true,
                            Settings = _settings.Read(),
                            Message = restarting ? "Saved. The monitor is restarting to use the new settings." : "Saved.",
                        };
                    }

                    case "testmqtt":
                    {
                        if (request.Mqtt == null) return Fail("No MQTT settings were sent.");
                        var (ok, message) = await MqttNotifier.TestConnectionAsync(_settings.ResolveMqtt(request.Mqtt), ct);
                        return new SettingsResult { Ok = ok, Message = message };
                    }

                    case "testwebhook":
                    {
                        if (request.Webhook == null) return Fail("No webhook settings were sent.");
                        var (ok, message) = await WebhookNotifier.TestAsync(request.Webhook.CallStartUrl,
                            _settings.ResolveHeaders(request.Webhook.Headers), ct);
                        return new SettingsResult { Ok = ok, Message = message };
                    }

                    case "restart":
                        return _restart?.RequestRestart($"asked by {caller?.Name}") == true
                            ? new SettingsResult { Ok = true, Message = "The monitor is restarting." }
                            : Fail("A restart is already under way.");
                }
            }
            catch (Exception e)
            {
                _logger.LogWarning("settings request '{Command}' failed: {Message}", command, e.Message);
                return Fail($"Something went wrong: {e.Message}");
            }
            return Fail($"unknown command '{command}'");
        }

        private static SettingsResult Fail(string message) =>
            new SettingsResult { Ok = false, Message = message, Errors = new System.Collections.Generic.List<string> { message } };

        /// <summary>Handles a status/pause/resume request line and returns the status (public for tests).</summary>
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
                ConfigDirectory = _settings?.Paths.ConfigDirectory ?? _env.ContentRootPath,
                ProgramDirectory = _env.ContentRootPath,
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
