// CallDetector.cs
// Version: 1.1.0
// Changelog:
//   1.1.0 - CallStarted events carry StartedAt (first interval with call traffic).
//   1.0.0 - Initial version. Thread-safe packet counters and the call start/end
//           state machine moved out of TrafficMonitor, with fixes for the
//           inbound/outbound comparison, start/end debouncing, matching an
//           active call by provider instead of a single IP, and call duration
//           measured to the last interval that had call traffic.
//
// Deliberately free of external packages so it can be unit-tested in isolation.

using System;
using System.Collections.Generic;
using System.Linq;

namespace RtcCallMonitor
{
    public enum CallEventType
    {
        CallStarted,
        CallEnded,
        UnknownProvider
    }

    public class CallEvent
    {
        public CallEventType Type { get; init; }
        public string Ip { get; init; }
        public string Provider { get; init; }
        public int PacketCount { get; init; }
        public TimeSpan Duration { get; init; }

        /// <summary>For CallStarted: when call traffic was first seen.</summary>
        public DateTimeOffset StartedAt { get; init; }
    }

    /// <summary>
    /// Counts UDP packets per remote IP and decides, once per check interval,
    /// whether a call has started or ended.
    /// RecordInbound/RecordOutbound may be called from the socket thread while
    /// Evaluate runs on the worker thread.
    /// </summary>
    public class CallDetector
    {
        private readonly object _lock = new object();
        private Dictionary<string, int> _inbound = new Dictionary<string, int>();
        private Dictionary<string, int> _outbound = new Dictionary<string, int>();

        private readonly Func<string, string> _providerLookup;
        private readonly int _minPacketRate;
        private readonly int _startIntervals;
        private readonly int _endIntervals;

        // pending start
        private string _candidateProvider;
        private int _candidateCount;
        private DateTimeOffset _candidateFirstSeen;

        // active call
        private bool _callActive;
        private string _callProvider;
        private DateTimeOffset _callStart;
        private DateTimeOffset _callLastSeen;
        private int _quietCount;

        /// <param name="providerLookup">Returns the provider name for an IP, or null if unknown.</param>
        /// <param name="minPacketRate">Packets per interval needed to count as call traffic.</param>
        /// <param name="startIntervals">Consecutive busy intervals needed before a call is reported as started.</param>
        /// <param name="endIntervals">Consecutive quiet intervals needed before a call is reported as ended.</param>
        public CallDetector(Func<string, string> providerLookup, int minPacketRate, int startIntervals, int endIntervals)
        {
            _providerLookup = providerLookup ?? throw new ArgumentNullException(nameof(providerLookup));
            _minPacketRate = Math.Max(1, minPacketRate);
            _startIntervals = Math.Max(1, startIntervals);
            _endIntervals = Math.Max(1, endIntervals);
        }

        public bool CallActive => _callActive;
        public string CallProvider => _callProvider;

        public void RecordInbound(string remoteIp) => Increment(isInbound: true, remoteIp);

        public void RecordOutbound(string remoteIp) => Increment(isInbound: false, remoteIp);

        private void Increment(bool isInbound, string ip)
        {
            if (string.IsNullOrEmpty(ip)) return;
            lock (_lock)
            {
                var stats = isInbound ? _inbound : _outbound;
                stats.TryGetValue(ip, out var count);
                stats[ip] = count + 1;
            }
        }

        /// <summary>
        /// Call once per check interval. Takes the counts gathered since the
        /// last call, resets them, and returns any events that happened.
        /// </summary>
        public IReadOnlyList<CallEvent> Evaluate(DateTimeOffset now)
        {
            Dictionary<string, int> inbound, outbound;
            lock (_lock)
            {
                inbound = _inbound;
                outbound = _outbound;
                _inbound = new Dictionary<string, int>();
                _outbound = new Dictionary<string, int>();
            }

            var events = new List<CallEvent>();

            // per-IP rate = the busier direction for that IP
            var rates = new Dictionary<string, int>();
            foreach (var kv in inbound) rates[kv.Key] = kv.Value;
            foreach (var kv in outbound)
            {
                rates.TryGetValue(kv.Key, out var existing);
                rates[kv.Key] = Math.Max(existing, kv.Value);
            }

            if (_callActive)
            {
                EvaluateActiveCall(now, rates, events);
            }
            else
            {
                EvaluateIdle(now, rates, events);
            }

            return events;
        }

        private void EvaluateActiveCall(DateTimeOffset now, Dictionary<string, int> rates, List<CallEvent> events)
        {
            // Count any IP of the same provider: providers often move a call
            // between media relays, which should not end the call.
            var providerRate = rates
                .Where(r => _providerLookup(r.Key) == _callProvider)
                .Select(r => r.Value)
                .DefaultIfEmpty(0)
                .Max();

            if (providerRate >= _minPacketRate)
            {
                _quietCount = 0;
                _callLastSeen = now;
                return;
            }

            _quietCount++;
            if (_quietCount >= _endIntervals)
            {
                events.Add(new CallEvent
                {
                    Type = CallEventType.CallEnded,
                    Provider = _callProvider,
                    Duration = _callLastSeen - _callStart
                });
                _callActive = false;
                _callProvider = null;
                _quietCount = 0;
            }
        }

        private void EvaluateIdle(DateTimeOffset now, Dictionary<string, int> rates, List<CallEvent> events)
        {
            // Busiest known-provider IP this interval; unknown busy IPs are reported for debugging.
            string bestIp = null, bestProvider = null;
            int bestRate = 0;

            foreach (var r in rates.Where(r => r.Value >= _minPacketRate).OrderByDescending(r => r.Value))
            {
                var provider = _providerLookup(r.Key);
                if (provider == null)
                {
                    events.Add(new CallEvent { Type = CallEventType.UnknownProvider, Ip = r.Key, PacketCount = r.Value });
                    continue;
                }
                if (bestProvider == null)
                {
                    bestIp = r.Key;
                    bestProvider = provider;
                    bestRate = r.Value;
                }
            }

            if (bestProvider == null)
            {
                _candidateProvider = null;
                _candidateCount = 0;
                return;
            }

            if (bestProvider == _candidateProvider)
            {
                _candidateCount++;
            }
            else
            {
                _candidateProvider = bestProvider;
                _candidateCount = 1;
                _candidateFirstSeen = now;
            }

            if (_candidateCount >= _startIntervals)
            {
                _callActive = true;
                _callProvider = bestProvider;
                _callStart = _candidateFirstSeen;
                _callLastSeen = now;
                _quietCount = 0;
                _candidateProvider = null;
                _candidateCount = 0;

                events.Add(new CallEvent
                {
                    Type = CallEventType.CallStarted,
                    Ip = bestIp,
                    Provider = bestProvider,
                    PacketCount = bestRate,
                    StartedAt = _callStart
                });
            }
        }
    }
}
