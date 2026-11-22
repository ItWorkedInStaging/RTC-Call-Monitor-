using RtcCallMonitor.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Collections.Generic;

namespace RtcCallMonitor
{
    public class NetworkListener
    {
        private Socket _socket;
        private IPAddress _localIp;
        private List<IPNetwork> _localCIDR = new List<IPNetwork>();
        private byte[] _buffer = new byte[65507];  // max UDP packet size

        private readonly ILogger<NetworkListener> _logger;
        private readonly IOptions<Application> _appConfig;

        public delegate void Notify(IPHeader ipHeader);

        public event Notify OnUDPTafficReceived;

        private Socket CreateAndBindSocket()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // SIO_RCVALL (promiscuous raw socket) is a Windows-only IOControl
                throw new PlatformNotSupportedException("Packet capture currently requires Windows (SIO_RCVALL is not available on this OS).");
            }

            const int SIO_RCVALL = unchecked((int)0x98000001);

            var s = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Udp);

            s.Bind(new IPEndPoint(_localIp, 0));
            s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.HeaderIncluded, true);

            byte[] IN = new byte[4] { 1, 0, 0, 0 };
            byte[] OUT = new byte[4];

            s.IOControl(SIO_RCVALL, IN, OUT);

            if (BitConverter.ToInt32(OUT) != 0) throw new Exception("Got non-zero result from IOControl");

            return s;
        }

        public NetworkListener(ILogger<NetworkListener> logger, IOptions<Application> appConfig) {
            _logger = logger;
            _appConfig = appConfig;

            foreach (var network in _appConfig.Value.LocalNetwork)
            {
                _localCIDR.Add(NetworkParser.Parse(network, out var corrected));
                if (corrected != null)
                {
                    _logger.LogWarning($"LocalNetwork '{network}' is not a network address, using {corrected}");
                }
            }
        }
            

        public void Start()
        {
            _socket = CreateAndBindSocket();

            _logger.LogInformation($"monitoring traffic on {_localIp}");

            _socket.BeginReceive(_buffer, 0, _buffer.Length, SocketFlags.None, new AsyncCallback(OnReceive), _socket);
        }

        public void UpdateIpAddress(IPAddress newAddress)
        {
            Stop();
            _localIp = newAddress;
            _logger.LogInformation($"Updating monitor to watch traffic on {_localIp}");
            Start();
        }

        public void Stop()
        {
            if (_socket != null)
            {
                _socket.Close();
            }
        }
         
        private void OnReceive(IAsyncResult result)
        {
            // use the socket this read was started on, not _socket, which may already
            // have been replaced by UpdateIpAddress()
            var socket = (Socket)result.AsyncState;

            try
            {
                // A raw socket is never "Connected", so EndReceive must always be called
                // to complete the read and get the real packet length.
                int length = socket.EndReceive(result);

                IPHeader ipHeader = new(_buffer, length, _localIp);
                if (IsOutsideUDPTaffice(ipHeader))
                {
                    OnUDPTafficReceived?.Invoke(ipHeader);
                }
            }
            catch (Exception e) when (e is ObjectDisposedException ||
                                      (e is SocketException se && se.SocketErrorCode == SocketError.OperationAborted))
            {
                // socket was closed by Stop() (e.g. IP address changed); don't re-arm it
                return;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "OnReceive Exception");
            }

            try
            {
                socket.BeginReceive(_buffer, 0, _buffer.Length, SocketFlags.None, new AsyncCallback(OnReceive), socket);
            }
            catch (ObjectDisposedException) { }
        }

        private bool IsOutsideUDPTaffice(IPHeader header)
        {
            if (header.IsUDP && !header.IsMulticast && !header.IsBroadcast)
            {
                if (header.SourceAddress.Equals(_localIp) || header.DestinationAddress.Equals(_localIp))
                {
                    bool result = true;
                    foreach (var network in _localCIDR)
                    {
                        result &= !(network.Contains(header.SourceAddress) && network.Contains(header.DestinationAddress));
                    }

                    return result;
                }
            }
            return false;
        }
    }
}
