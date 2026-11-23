// IpPacket.cs
// Version: 1.1.0
// Changelog:
//   1.1.0 - Reads the UDP source and destination ports.
//   1.0.0 - Initial version. Replaces IPHeader.cs: parses the IPv4 header and
//           the IPv6 header (walking hop-by-hop, routing, fragment and
//           destination-options extension headers) to find the transport
//           protocol and the two addresses.

using System;
using System.Net;
using System.Net.Sockets;

namespace RtcCallMonitor.Capture
{
    public readonly struct IpPacket
    {
        public const byte ProtocolUdp = 17;

        public IPAddress Source { get; }
        public IPAddress Destination { get; }

        /// <summary>Transport protocol (17 = UDP), after any IPv6 extension headers.</summary>
        public byte Protocol { get; }

        public bool IsUdp => Protocol == ProtocolUdp;

        /// <summary>UDP ports; 0 when not UDP or the packet is too short to hold them.</summary>
        public int SourcePort { get; }
        public int DestinationPort { get; }

        private IpPacket(IPAddress source, IPAddress destination, byte protocol, ReadOnlySpan<byte> data, int transportOffset)
        {
            Source = source;
            Destination = destination;
            Protocol = protocol;
            if (protocol == ProtocolUdp && transportOffset >= 0 && data.Length >= transportOffset + 4)
            {
                SourcePort = (data[transportOffset] << 8) | data[transportOffset + 1];
                DestinationPort = (data[transportOffset + 2] << 8) | data[transportOffset + 3];
            }
            else
            {
                SourcePort = 0;
                DestinationPort = 0;
            }
        }

        /// <summary>Parses a raw IP packet. Returns false for anything too short or not IPv4/IPv6.</summary>
        public static bool TryParse(ReadOnlySpan<byte> data, out IpPacket packet)
        {
            packet = default;
            if (data.Length < 1) return false;

            switch (data[0] >> 4)
            {
                case 4: return TryParseV4(data, out packet);
                case 6: return TryParseV6(data, out packet);
                default: return false;
            }
        }

        private static bool TryParseV4(ReadOnlySpan<byte> data, out IpPacket packet)
        {
            packet = default;
            var headerLength = (data[0] & 0x0F) * 4;
            if (data.Length < 20 || headerLength < 20 || data.Length < headerLength) return false;

            packet = new IpPacket(
                new IPAddress(data.Slice(12, 4)),
                new IPAddress(data.Slice(16, 4)),
                data[9], data, headerLength);
            return true;
        }

        private static bool TryParseV6(ReadOnlySpan<byte> data, out IpPacket packet)
        {
            packet = default;
            if (data.Length < 40) return false;

            var next = data[6];
            var offset = 40;

            // walk the extension headers that can come before the transport header
            for (int hops = 0; hops < 8; hops++)
            {
                switch (next)
                {
                    case 0:   // hop-by-hop options
                    case 43:  // routing
                    case 60:  // destination options
                        if (data.Length < offset + 2) return false;
                        next = data[offset];
                        offset += (data[offset + 1] + 1) * 8;
                        continue;
                    case 44:  // fragment (fixed 8 bytes)
                        if (data.Length < offset + 8) return false;
                        next = data[offset];
                        offset += 8;
                        continue;
                }
                break;
            }

            packet = new IpPacket(
                new IPAddress(data.Slice(8, 16)),
                new IPAddress(data.Slice(24, 16)),
                next, data, offset);
            return true;
        }

        /// <summary>Multicast, broadcast, link-local or unspecified: never call media.</summary>
        public static bool IsNonUnicast(IPAddress address)
        {
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = address.GetAddressBytes();
                return b[0] >= 224                                  // multicast and reserved, incl. 255.255.255.255
                    || (b[0] == 169 && b[1] == 254)                 // link-local
                    || (b[0] == 0);                                 // unspecified
            }
            return address.IsIPv6Multicast || address.IsIPv6LinkLocal || address.Equals(IPAddress.IPv6None);
        }
    }
}
