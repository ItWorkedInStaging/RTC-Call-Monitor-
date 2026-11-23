// ICallNotifier.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. Common interface for call outputs (webhook, MQTT).

using System;
using System.Threading;
using System.Threading.Tasks;

namespace RtcCallMonitor.Notifications
{
    /// <summary>Details of a call passed to notifiers.</summary>
    public class CallInfo
    {
        public string Provider { get; init; }
        public string Ip { get; init; }
        public DateTimeOffset StartedAt { get; init; }

        /// <summary>Set only for the call-ended notification.</summary>
        public TimeSpan? Duration { get; init; }
    }

    /// <summary>An output that is told about calls starting and ending.</summary>
    public interface ICallNotifier
    {
        /// <summary>Name shown in logs, e.g. "webhook".</summary>
        string Name { get; }

        /// <summary>Whether this output is switched on in config.</summary>
        bool Enabled { get; }

        /// <summary>Called once at startup. Must not block for long (connect in the background).</summary>
        Task StartAsync(CancellationToken cancellationToken);

        Task CallStartedAsync(CallInfo call, CancellationToken cancellationToken);

        Task CallEndedAsync(CallInfo call, CancellationToken cancellationToken);

        /// <summary>Called once at shutdown.</summary>
        Task StopAsync(CancellationToken cancellationToken);
    }
}
