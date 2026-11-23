// CallReporter.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. Sends call start/end to the enabled notifiers
//           (moved out of Worker) and applies pause/resume: pausing ends a
//           reported call, resuming reports a call that is still going, and
//           notifiers that support it show the paused state (MQTT).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RtcCallMonitor.Notifications
{
    /// <summary>A notifier that can also show that detection is paused.</summary>
    public interface IPauseAwareNotifier
    {
        Task SetPausedAsync(bool paused, DateTimeOffset? until, CancellationToken cancellationToken);
    }

    public class CallReporter
    {
        private readonly MonitorState _state;
        private readonly ILogger<CallReporter> _logger;
        private readonly List<ICallNotifier> _notifiers;

        public CallReporter(MonitorState state, IEnumerable<ICallNotifier> notifiers, ILogger<CallReporter> logger)
        {
            _state = state;
            _logger = logger;
            _notifiers = notifiers.Where(n => n.Enabled).ToList();
        }

        public IReadOnlyList<ICallNotifier> Notifiers => _notifiers;
        public MonitorState State => _state;

        public void CallDetected(string provider, string ip, DateTimeOffset startedAt)
        {
            var report = _state.CallDetected(provider, ip, startedAt);
            if (report == MonitorState.Report.None) _logger.LogInformation("call not reported: detection is paused");
            Send(report, provider, ip, startedAt);
        }

        public void CallFinished(string provider, TimeSpan duration)
        {
            var started = _state.CallStarted;
            var report = _state.CallFinished();
            Send(report, provider, null, started ?? DateTimeOffset.UtcNow, duration);
        }

        public void Pause(TimeSpan? duration)
        {
            var provider = _state.Provider;
            var started = _state.CallStarted;
            var report = _state.Pause(duration);
            _logger.LogInformation("detection paused {Until}", duration.HasValue ? $"for {(int)duration.Value.TotalMinutes} min" : "until resumed");
            Send(report, provider, null, started ?? DateTimeOffset.UtcNow, started.HasValue ? DateTimeOffset.UtcNow - started.Value : TimeSpan.Zero);
            SendPaused(true, _state.PausedUntil);
        }

        public void Resume(string reason = "resumed")
        {
            var report = _state.Resume();
            _logger.LogInformation("detection {Reason}", reason);
            SendPaused(false, null);
            Send(report, _state.Provider, _state.Ip, _state.CallStarted ?? DateTimeOffset.UtcNow);
        }

        /// <summary>Call once per check interval: ends a timed pause whose time is up.</summary>
        public void Tick()
        {
            if (_state.PauseExpired()) Resume("resumed (pause time is up)");
        }

        private void Send(MonitorState.Report report, string provider, string ip, DateTimeOffset startedAt, TimeSpan? duration = null)
        {
            switch (report)
            {
                case MonitorState.Report.CallStarted:
                    Notify(n => n.CallStartedAsync(new CallInfo { Provider = provider, Ip = ip, StartedAt = startedAt }, CancellationToken.None));
                    break;
                case MonitorState.Report.CallEnded:
                    Notify(n => n.CallEndedAsync(new CallInfo { Provider = provider, StartedAt = startedAt, Duration = duration }, CancellationToken.None));
                    break;
            }
        }

        private void SendPaused(bool paused, DateTimeOffset? until)
        {
            foreach (var n in _notifiers.OfType<IPauseAwareNotifier>())
            {
                _ = Task.Run(async () =>
                {
                    try { await n.SetPausedAsync(paused, until, CancellationToken.None); }
                    catch (Exception e) { _logger.LogWarning("{Notifier} paused state failed: {Message}", ((ICallNotifier)n).Name, e.Message); }
                });
            }
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
    }
}
