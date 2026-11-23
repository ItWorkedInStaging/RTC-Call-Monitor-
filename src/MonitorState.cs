// MonitorState.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. The current call and pause state, shared by the
//           worker (which reports calls) and the status pipe (which shows them
//           and pauses/resumes). Decides when notifiers are told about a call.

using System;

namespace RtcCallMonitor
{
    public class MonitorState
    {
        private readonly object _lock = new object();
        private readonly Func<DateTimeOffset> _clock;

        private string _provider;
        private string _ip;
        private DateTimeOffset? _callStarted;
        private bool _callReported;
        private bool _paused;
        private DateTimeOffset? _pausedUntil;

        public MonitorState(Func<DateTimeOffset> clock = null)
        {
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        /// <summary>What the notifiers should be told after a state change.</summary>
        public enum Report { None, CallStarted, CallEnded }

        public bool OnCall { get { lock (_lock) return _callStarted.HasValue; } }
        public bool Paused { get { lock (_lock) return _paused; } }
        public DateTimeOffset? PausedUntil { get { lock (_lock) return _pausedUntil; } }
        public string Provider { get { lock (_lock) return _provider; } }
        public string Ip { get { lock (_lock) return _ip; } }
        public DateTimeOffset? CallStarted { get { lock (_lock) return _callStarted; } }

        /// <summary>A call was detected. Reported unless paused.</summary>
        public Report CallDetected(string provider, string ip, DateTimeOffset startedAt)
        {
            lock (_lock)
            {
                _provider = provider;
                _ip = ip;
                _callStarted = startedAt;
                _callReported = !_paused;
                return _callReported ? Report.CallStarted : Report.None;
            }
        }

        /// <summary>A call ended. Reported only if its start was reported.</summary>
        public Report CallFinished()
        {
            lock (_lock)
            {
                var report = _callReported ? Report.CallEnded : Report.None;
                _provider = null;
                _ip = null;
                _callStarted = null;
                _callReported = false;
                return report;
            }
        }

        /// <summary>Stops reporting calls, for a while or until Resume. A reported call is ended.</summary>
        public Report Pause(TimeSpan? duration)
        {
            lock (_lock)
            {
                _paused = true;
                _pausedUntil = duration.HasValue ? _clock() + duration.Value : (DateTimeOffset?)null;
                if (_callReported)
                {
                    _callReported = false;
                    return Report.CallEnded;
                }
                return Report.None;
            }
        }

        /// <summary>Reports calls again. A call that is still going is reported as started.</summary>
        public Report Resume()
        {
            lock (_lock)
            {
                if (!_paused) return Report.None;
                _paused = false;
                _pausedUntil = null;
                if (_callStarted.HasValue && !_callReported)
                {
                    _callReported = true;
                    return Report.CallStarted;
                }
                return Report.None;
            }
        }

        /// <summary>Ends a timed pause whose time is up. Call once per check interval.</summary>
        public bool PauseExpired()
        {
            lock (_lock)
            {
                return _paused && _pausedUntil.HasValue && _clock() >= _pausedUntil.Value;
            }
        }
    }
}
