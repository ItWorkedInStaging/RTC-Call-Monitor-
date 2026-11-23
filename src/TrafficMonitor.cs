using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RtcCallMonitor.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace RtcCallMonitor
{
    public class TrafficMonitor
    {
        private readonly ProviderCatalog _providers;
        private readonly CallDetector _detector;
        private readonly ILogger<TrafficMonitor> _logger;
        private readonly DetectionOptions _detection;
        private NetworkListener _listener;
        private IPAddress _ipAddress = IPAddress.Loopback;

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
            _listener.OnUDPTafficReceived += (IPHeader ipHeader) =>
            {
                if (ipHeader.Inbound)
                {
                    _detector.RecordInbound(ipHeader.SourceAddress.ToString());
                }
                else
                {
                    _detector.RecordOutbound(ipHeader.DestinationAddress.ToString());
                }
            };

            NetworkChange.NetworkAddressChanged += NetworkChange_NetworkAddressChanged;
            NetworkChange_NetworkAddressChanged(null, null);
        }

        private void NetworkChange_NetworkAddressChanged(object sender, EventArgs e)
        {
            _logger.LogInformation($"network changed!");
            Thread.Sleep(_detection.CheckIntervalMs);
            IPAddress newIpAddress = IPAddress.Loopback;
            try
            {
                /* 
                 * Attempt to detect the current IP address by defining a fake connection to an arbitrary remote IP
                 * This implementation is cross-platform and should work on Windows/Linux
                 */
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect("8.8.8.8", 65530);
                    if (socket.LocalEndPoint is IPEndPoint endPoint)
                    {
                        newIpAddress = endPoint.Address;
                        _logger.LogInformation($"Auto-detected IP address: {newIpAddress}");
                    }
                    else
                    {
                        _logger.LogWarning("Failed to auto-detect IP adress!");
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Unable to detect correct IP address - no valid network available. Exception type: {FullName}", ex.GetType().FullName);
                _logger.LogWarning($"Defaulting IP address to loopback: {newIpAddress}");
            }

            if ((newIpAddress != IPAddress.Loopback) && (_ipAddress != newIpAddress))
            {
                _ipAddress = newIpAddress;
                _listener.UpdateIpAddress(_ipAddress);
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
