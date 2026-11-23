// StatusContract.cs
// Version: 1.1.0
// Changelog:
//   1.1.0 - Settings commands (getSettings, saveSettings, testMqtt, testWebhook,
//           restart) and SendAsync<T>; the client lets the monitor identify the
//           caller's Windows account (who may change settings).
//           MonitorStatus.ProgramDirectory; ConfigDirectory is now the
//           settings folder.
//   1.0.0 - Initial version. Messages between the monitor and the tray app over
//           the local named pipe "RtcCallMonitor": one JSON request line, one
//           JSON response line. Shared by both projects (linked into the tray).

using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RtcCallMonitor.Ipc
{
    public static class PipeContract
    {
        public const string PipeName = "RtcCallMonitor";
        public const int ProtocolVersion = 2;

        /// <summary>Largest request line accepted (a saveSettings with provider lists).</summary>
        public const int MaxRequestBytes = 512 * 1024;

        public static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }

    public class PipeRequest
    {
        /// <summary>"status", "pause", "resume", "getSettings", "saveSettings", "testMqtt", "testWebhook" or "restart".</summary>
        public string Command { get; set; }

        /// <summary>For "pause": minutes, or null for until resumed.</summary>
        public int? Minutes { get; set; }

        /// <summary>For "saveSettings".</summary>
        public SettingsDocument Settings { get; set; }

        /// <summary>For "testMqtt": the settings to try (NewPassword null = the stored one).</summary>
        public MqttSettings Mqtt { get; set; }

        /// <summary>For "testWebhook": the URL in CallStartUrl and the headers to send.</summary>
        public WebhookSettings Webhook { get; set; }
    }

    public class MonitorStatus
    {
        public int Protocol { get; set; } = PipeContract.ProtocolVersion;
        public string Version { get; set; }

        /// <summary>"idle", "onCall" or "paused".</summary>
        public string State { get; set; }

        public string Provider { get; set; }
        public DateTimeOffset? CallStartedUtc { get; set; }

        public bool Paused { get; set; }

        /// <summary>When a timed pause ends; null while paused means until resumed.</summary>
        public DateTimeOffset? PausedUntilUtc { get; set; }

        public bool MqttEnabled { get; set; }
        public bool MqttConnected { get; set; }
        public bool WebhookEnabled { get; set; }

        /// <summary>e.g. "Wi-Fi (10.69.199.37)".</summary>
        public string[] Capturing { get; set; } = Array.Empty<string>();

        /// <summary>Plain-language problems the tray shows, e.g. "MQTT not connected".</summary>
        public string[] Problems { get; set; } = Array.Empty<string>();

        public string LogDirectory { get; set; }
        public string ConfigDirectory { get; set; }

        /// <summary>Folder the monitor runs from (README.md, bundled provider lists). Protocol 2+.</summary>
        public string ProgramDirectory { get; set; }
        public bool RunningAsService { get; set; }

        /// <summary>Set when a pause/resume request could not be done.</summary>
        public string Error { get; set; }
    }

    /// <summary>Client side: one request, one response, then the connection closes.</summary>
    public static class PipeClient
    {
        public static Task<MonitorStatus> SendAsync(PipeRequest request, TimeSpan timeout, CancellationToken ct = default) =>
            SendAsync<MonitorStatus>(request, timeout, ct);

        /// <summary>status/pause/resume answer with MonitorStatus, the settings commands with SettingsResult.</summary>
        public static async Task<T> SendAsync<T>(PipeRequest request, TimeSpan timeout, CancellationToken ct = default, string pipeName = PipeContract.PipeName)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            // Identification: the monitor may look at who we are (to allow settings changes), not act as us
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(cts.Token);

            var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            var reader = new StreamReader(pipe, Encoding.UTF8);
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, PipeContract.Json).AsMemory(), cts.Token);
            var line = await reader.ReadLineAsync(cts.Token);
            if (line == null) throw new IOException("monitor closed the connection without answering");
            return JsonSerializer.Deserialize<T>(line, PipeContract.Json);
        }
    }
}
