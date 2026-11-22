// CallDetectorTests.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. Covers the round-1 fixes: inbound detection,
//           start/end debouncing, relay IP switches, duration, unknown
//           providers, threshold, provider switching and concurrent recording.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;

namespace RtcCallMonitor.Tests
{
    public class CallDetectorTests
    {
        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private static string Lookup(string ip) =>
            ip.StartsWith("13.") ? "msteams" :
            ip.StartsWith("170.") ? "zoom" :
            null;

        // Records n packets for ip, then evaluates interval i (2s per interval).
        private static IReadOnlyList<CallEvent> Tick(CallDetector d, int i, string ip, int n, bool inbound = false)
        {
            for (int k = 0; k < n; k++)
            {
                if (inbound) d.RecordInbound(ip); else d.RecordOutbound(ip);
            }
            return d.Evaluate(T0.AddSeconds(i * 2));
        }

        [Fact]
        public void InboundOnlyTraffic_StartsCall()
        {
            var d = new CallDetector(Lookup, minPacketRate: 5, startIntervals: 1, endIntervals: 1);
            var events = Tick(d, 0, "13.1.1.1", 50, inbound: true);
            Assert.Contains(events, e => e.Type == CallEventType.CallStarted && e.Provider == "msteams");
        }

        [Fact]
        public void Start_RequiresConfiguredNumberOfIntervals()
        {
            var d = new CallDetector(Lookup, 5, startIntervals: 2, endIntervals: 3);
            Assert.DoesNotContain(Tick(d, 0, "13.1.1.1", 50), e => e.Type == CallEventType.CallStarted);
            Assert.Single(Tick(d, 1, "13.1.1.1", 50), e => e.Type == CallEventType.CallStarted);
        }

        [Fact]
        public void ShortDip_DoesNotEndCall_AndSustainedQuietDoes()
        {
            var d = new CallDetector(Lookup, 5, startIntervals: 2, endIntervals: 3);
            Tick(d, 0, "13.1.1.1", 50);
            Tick(d, 1, "13.1.1.1", 50);

            Assert.Empty(Tick(d, 2, "13.1.1.1", 0));
            Assert.Empty(Tick(d, 3, "13.1.1.1", 0));
            Assert.Empty(Tick(d, 4, "13.1.1.1", 50));
            Assert.True(d.CallActive);

            // relay switch to another IP of the same provider keeps the call alive
            Assert.Empty(Tick(d, 5, "13.9.9.9", 50));
            Assert.True(d.CallActive);

            Assert.Empty(Tick(d, 6, "x", 0));
            Assert.Empty(Tick(d, 7, "x", 0));
            var end = Assert.Single(Tick(d, 8, "x", 0));

            Assert.Equal(CallEventType.CallEnded, end.Type);
            Assert.Equal("msteams", end.Provider);
            // first seen at interval 0 (t=0s), last call traffic at interval 5 (t=10s)
            Assert.Equal(10, end.Duration.TotalSeconds);
            Assert.False(d.CallActive);
        }

        [Fact]
        public void UnknownBusyIp_IsReported_AndDoesNotStartCall()
        {
            var d = new CallDetector(Lookup, 5, 1, 1);
            var e = Assert.Single(Tick(d, 0, "8.8.4.4", 50));
            Assert.Equal(CallEventType.UnknownProvider, e.Type);
            Assert.Equal("8.8.4.4", e.Ip);
            Assert.False(d.CallActive);
        }

        [Fact]
        public void TrafficBelowMinPacketRate_IsIgnored()
        {
            var d = new CallDetector(Lookup, 5, 1, 1);
            Assert.Empty(Tick(d, 0, "13.1.1.1", 4));
            Assert.False(d.CallActive);
        }

        [Fact]
        public void KnownProvider_StartsCall_EvenWhenUnknownIpIsBusier()
        {
            var d = new CallDetector(Lookup, 5, 1, 1);
            for (int k = 0; k < 100; k++) d.RecordOutbound("8.8.4.4");
            var events = Tick(d, 0, "170.1.1.1", 20);
            Assert.Contains(events, e => e.Type == CallEventType.CallStarted && e.Provider == "zoom");
        }

        [Fact]
        public void ProviderSwitch_ResetsStartDebounce()
        {
            var d = new CallDetector(Lookup, 5, startIntervals: 2, endIntervals: 1);
            Tick(d, 0, "13.1.1.1", 50);
            Assert.DoesNotContain(Tick(d, 1, "170.1.1.1", 50), e => e.Type == CallEventType.CallStarted);
        }

        [Fact]
        public void ConcurrentRecording_DoesNotThrow()
        {
            var d = new CallDetector(Lookup, 1, 1, 1);
            var stop = 0;
            var threads = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
            {
                while (Volatile.Read(ref stop) == 0) d.RecordInbound("13.1.1.1");
            })).ToList();
            threads.ForEach(t => t.Start());

            var ex = Record.Exception(() =>
            {
                for (int i = 0; i < 2000; i++) d.Evaluate(T0.AddSeconds(i));
            });

            Volatile.Write(ref stop, 1);
            threads.ForEach(t => t.Join());
            Assert.Null(ex);
        }
    }
}
