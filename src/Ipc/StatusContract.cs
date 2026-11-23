// StatusContract.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. Messages between the monitor and the tray app over
//           the local named pipe "RtcCallMonitor": one JSON request line, one
//           JSON response line. Shared by both projects (linked into the tray).

using System;
using System.IO;
using System.IO.Pipes;
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
        public const int ProtocolVersion = 1;

        public static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }

    public class PipeRequest
    {
        /// <summary>"status", "pause" or "resume".</summary>
        public string Command { get; set; }

        /// <summary>For "pause": minutes, or null for until resumed.</summary>
        public int? Minutes { get; set; }
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
        public bool RunningAsService { get; set; }

        /// <summary>Set when a pause/resume request could not be done.</summary>
        public string Error { get; set; }
    }

    /// <summary>Client side: one request, one response, then the connection closes.</summary>
    public static class PipeClient
    {
        public static async Task<MonitorStatus> SendAsync(PipeRequest request, TimeSpan timeout, CancellationToken ct = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            using var pipe = new NamedPipeClientStream(".", PipeContract.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cts.Token);

            var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            var reader = new StreamReader(pipe, Encoding.UTF8);
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, PipeContract.Json).AsMemory(), cts.Token);
            var line = await reader.ReadLineAsync(cts.Token);
            if (line == null) throw new IOException("monitor closed the connection without answering");
            return JsonSerializer.Deserialize<MonitorStatus>(line, PipeContract.Json);
        }
    }
}
