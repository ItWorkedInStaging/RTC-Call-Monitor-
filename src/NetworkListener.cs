// NetworkListener.cs
// Version: 2.0.0
// Changelog:
//   2.0.0 - Captures on every selected local address at once (one raw socket
//           each, IPv4 and IPv6) instead of a single IPv4 address; Refresh()
//           opens/closes sockets as addresses come and go; local subnets come
//           from the interfaces plus Detection.LocalNetworks; each socket has
//           its own receive buffer.
//   1.x   - Single raw IPv4 socket on the address found via 8.8.8.8.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RtcCallMonitor.Capture;
using RtcCallMonitor.Configuration;

namespace RtcCallMonitor
{
    public class NetworkListener : IDisposable
    {
        private const int SIO_RCVALL = unchecked((int)0x98000001);
        private const int BufferSize = 65535;

        private readonly ILogger<NetworkListener> _logger;
        private readonly DetectionOptions _detection;
        private readonly List<IPNetwork> _configuredLocalNetworks = new List<IPNetwork>();
        private readonly object _lock = new object();
        private readonly Dictionary<IPAddress, CaptureSocket> _sockets = new Dictionary<IPAddress, CaptureSocket>();
        private volatile PacketFilter _filter = new PacketFilter(Array.Empty<IPAddress>(), Array.Empty<IPNetwork>());

        private long _ipv6Packets;
        private long _unparsedPackets;

        /// <summary>A UDP packet between this PC and a remote host outside the local networks.</summary>
        public delegate void UdpPacketHandler(IPAddress remote, bool inbound);

        public event UdpPacketHandler OnUdpPacket;

        private sealed class CaptureSocket
        {
            public CaptureTarget Target;
            public Socket Socket;
            public readonly byte[] Buffer = new byte[BufferSize];
        }

        public NetworkListener(ILogger<NetworkListener> logger, IOptions<DetectionOptions> detection)
        {
            _logger = logger;
            _detection = detection.Value;

            foreach (var network in _detection.LocalNetworks ?? Array.Empty<string>())
            {
                try
                {
                    _configuredLocalNetworks.Add(NetworkParser.Parse(network, out var corrected));
                    if (corrected != null)
                    {
                        _logger.LogWarning($"LocalNetworks '{network}' is not a network address, using {corrected}");
                    }
                }
                catch (FormatException)
                {
                    // reported by ConfigValidator at startup
                }
            }
        }

        /// <summary>Addresses currently captured on.</summary>
        public IReadOnlyList<CaptureTarget> Targets
        {
            get { lock (_lock) return _sockets.Values.Select(s => s.Target).ToList(); }
        }

        /// <summary>
        /// Re-reads the network interfaces and captures on exactly the selected
        /// addresses: opens sockets for new ones, closes those that went away.
        /// Throws PlatformNotSupportedException off Windows.
        /// </summary>
        public void Refresh()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // SIO_RCVALL (promiscuous raw socket) is a Windows-only IOControl
                throw new PlatformNotSupportedException("Packet capture currently requires Windows (SIO_RCVALL is not available on this OS).");
            }

            var wanted = CaptureAddresses.Select(
                InterfaceInfo.FromSystem(), _detection.Interfaces, _detection.ExcludeInterfaces, _detection.CaptureIPv6);

            lock (_lock)
            {
                foreach (var gone in _sockets.Keys.Where(a => wanted.All(w => !w.Address.Equals(a))).ToList())
                {
                    _logger.LogInformation("stopped capturing on {Target}", _sockets[gone].Target);
                    _sockets[gone].Socket.Close();
                    _sockets.Remove(gone);
                }

                foreach (var target in wanted.Where(w => !_sockets.ContainsKey(w.Address)))
                {
                    try
                    {
                        var capture = new CaptureSocket { Target = target, Socket = Open(target.Address) };
                        _sockets[target.Address] = capture;
                        Receive(capture);
                        _logger.LogInformation("capturing on {Target}", target);
                    }
                    catch (Exception e) when (e is SocketException || e is ArgumentException)
                    {
                        var level = target.Address.AddressFamily == AddressFamily.InterNetworkV6 ? LogLevel.Information : LogLevel.Warning;
                        _logger.Log(level, "cannot capture on {Target}: {Message}", target, e.Message);
                    }
                }

                var localNetworks = _sockets.Values.Select(s => s.Target.Subnet).Concat(_configuredLocalNetworks).Distinct().ToList();
                _filter = new PacketFilter(_sockets.Keys, localNetworks);

                if (_sockets.Count == 0)
                {
                    _logger.LogWarning("no network address to capture on (check Detection.Interfaces / ExcludeInterfaces, or the PC is offline)");
                }
            }
        }

        private static Socket Open(IPAddress address)
        {
            Socket s;
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                s = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Udp);
                s.Bind(new IPEndPoint(address, 0));
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.HeaderIncluded, true);
            }
            else
            {
                // SIO_RCVALL needs IPPROTO_IP (0) on the raw socket
                s = new Socket(AddressFamily.InterNetworkV6, SocketType.Raw, (ProtocolType)0);
                s.Bind(new IPEndPoint(address, 0));
            }

            var outValue = new byte[4];
            try
            {
                s.IOControl(SIO_RCVALL, new byte[] { 1, 0, 0, 0 }, outValue);
            }
            catch
            {
                s.Dispose();
                throw;
            }
            return s;
        }

        private void Receive(CaptureSocket capture)
        {
            try
            {
                capture.Socket.BeginReceive(capture.Buffer, 0, capture.Buffer.Length, SocketFlags.None, OnReceive, capture);
            }
            catch (ObjectDisposedException) { }
        }

        private void OnReceive(IAsyncResult result)
        {
            var capture = (CaptureSocket)result.AsyncState;
            try
            {
                // a raw socket is never "Connected": always EndReceive to get the real length
                int length = capture.Socket.EndReceive(result);

                if (IpPacket.TryParse(capture.Buffer.AsSpan(0, length), out var packet))
                {
                    if (packet.Source.AddressFamily == AddressFamily.InterNetworkV6) _ipv6Packets++;
                    if (_filter.TryClassify(packet, out var remote, out var inbound))
                    {
                        OnUdpPacket?.Invoke(remote, inbound);
                    }
                }
                else
                {
                    _unparsedPackets++;
                }
            }
            catch (Exception e) when (e is ObjectDisposedException ||
                                      (e is SocketException se && se.SocketErrorCode == SocketError.OperationAborted))
            {
                return; // socket closed by Refresh/Stop: don't re-arm
            }
            catch (Exception e)
            {
                _logger.LogError(e, "error reading from {Target}", capture.Target);
            }

            Receive(capture);
        }

        /// <summary>Counters for the debug log: IPv6 packets seen and packets that could not be parsed.</summary>
        public (long Ipv6, long Unparsed) Counters => (_ipv6Packets, _unparsedPackets);

        public void Stop()
        {
            lock (_lock)
            {
                foreach (var s in _sockets.Values) s.Socket.Close();
                _sockets.Clear();
            }
        }

        public void Dispose() => Stop();
    }
}
