// TrafficMonitor.cs
// Version: 2.0.0
// Changelog:
//   2.0.0 - Uses NetworkListener's multi-address capture; re-scans interfaces
//           2 s after Windows reports an address change (replaces the 8.8.8.8
//           single-address detection).
//   1.x   - Original monitor.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RtcCallMonitor.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;

namespace RtcCallMonitor
{
    public class TrafficMonitor
    {
        private readonly ProviderCatalog _providers;
        private readonly CallDetector _detector;
        private readonly ILogger<TrafficMonitor> _logger;
        private readonly DetectionOptions _detection;
        private readonly NetworkListener _listener;
        private Timer _refreshTimer;

        public delegate void UnknownNetwork(string ip, int packetCount);
        public delegate void CallStart(string ip, string provider, int packetCount, DateTimeOffset startedAt);
        public delegate void CallEnd(string provider, TimeSpan ts);

        public event UnknownNetwork OnUnknownProvider;
        public event CallStart OnCallStarted;
        public event CallEnd OnCallEnded;

        public TrafficMonitor(ILogger<TrafficMonitor> logger, NetworkListener listener, ProviderCatalog providers, IOptions<DetectionOptions> detection)
        {
            _logger = logger;
            _listener = listener;
            _providers = providers;
            _detection = detection.Value;

            _detector = new CallDetector(
                _providers.Lookup,
                _detection.MinPacketRate,
                _detection.CallStartIntervals,
                _detection.CallEndIntervals);
        }

        public void Start()
        {
            _listener.OnUdpPacket += (remote, inbound) =>
            {
                if (inbound) _detector.RecordInbound(remote.ToString());
                else _detector.RecordOutbound(remote.ToString());
            };

            _listener.Refresh();

            // re-scan when addresses change (VPN up/down, Wi-Fi switch, DHCP renew);
            // Windows raises several events in a row, so wait for them to settle
            _refreshTimer = new Timer(_ => RefreshListener(), null, Timeout.Infinite, Timeout.Infinite);
            NetworkChange.NetworkAddressChanged += (_, __) => _refreshTimer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }

        private void RefreshListener()
        {
            try
            {
                _logger.LogInformation("network addresses changed, re-scanning interfaces");
                _listener.Refresh();
            }
            catch (Exception e)
            {
                _logger.LogWarning("re-scanning interfaces failed: {Message}", e.Message);
            }
        }

        public void CheckStats()
        {
            foreach (var e in _detector.Evaluate(DateTimeOffset.Now))
            {
                switch (e.Type)
                {
                    case CallEventType.UnknownProvider:
                        OnUnknownProvider?.Invoke(e.Ip, e.PacketCount);
                        break;
                    case CallEventType.CallStarted:
                        OnCallStarted?.Invoke(e.Ip, e.Provider, e.PacketCount, e.StartedAt);
                        break;
                    case CallEventType.CallEnded:
                        OnCallEnded?.Invoke(e.Provider, e.Duration);
                        break;
                }
            }
        }
    }
}
