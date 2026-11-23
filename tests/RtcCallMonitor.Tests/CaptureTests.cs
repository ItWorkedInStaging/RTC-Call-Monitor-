// CaptureTests.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. IPv4/IPv6 packet parsing, interface selection
//           (include/exclude patterns, address kinds, subnets) and the packet
//           filter (direction, local networks, non-unicast).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using RtcCallMonitor.Capture;
using Xunit;

namespace RtcCallMonitor.Tests
{
    public class CaptureTests
    {
        private static byte[] V4(string src, string dst, byte protocol = 17)
        {
            var p = new byte[28];
            p[0] = 0x45; p[9] = protocol;
            IPAddress.Parse(src).GetAddressBytes().CopyTo(p, 12);
            IPAddress.Parse(dst).GetAddressBytes().CopyTo(p, 16);
            return p;
        }

        private static byte[] V6(string src, string dst, params byte[] headerChain)
        {
            // headerChain: next-header values; all but the last get an 8-byte extension header
            var ext = (headerChain.Length - 1) * 8;
            var p = new byte[40 + ext + 8];
            p[0] = 0x60; p[6] = headerChain[0];
            IPAddress.Parse(src).GetAddressBytes().CopyTo(p, 8);
            IPAddress.Parse(dst).GetAddressBytes().CopyTo(p, 24);
            for (int i = 1; i < headerChain.Length; i++)
            {
                var at = 40 + (i - 1) * 8;
                p[at] = headerChain[i]; // next header
                p[at + 1] = 0;          // hdr ext len: (0 + 1) * 8 bytes
            }
            return p;
        }

        [Fact]
        public void ParsesIPv4Udp()
        {
            Assert.True(IpPacket.TryParse(V4("192.168.1.5", "52.112.1.2"), out var p));
            Assert.True(p.IsUdp);
            Assert.Equal(IPAddress.Parse("192.168.1.5"), p.Source);
            Assert.Equal(IPAddress.Parse("52.112.1.2"), p.Destination);
        }

        [Fact]
        public void ParsesIPv4Tcp_AsNotUdp()
        {
            Assert.True(IpPacket.TryParse(V4("192.168.1.5", "52.112.1.2", protocol: 6), out var p));
            Assert.False(p.IsUdp);
        }

        [Fact]
        public void ParsesIPv6Udp_DirectAndAfterExtensionHeaders()
        {
            Assert.True(IpPacket.TryParse(V6("2001:db8::5", "2603:1063::1", 17), out var direct));
            Assert.True(direct.IsUdp);
            Assert.Equal(IPAddress.Parse("2603:1063::1"), direct.Destination);

            Assert.True(IpPacket.TryParse(V6("2001:db8::5", "2603:1063::1", 0, 60, 17), out var withExt));
            Assert.True(withExt.IsUdp);
        }

        [Fact]
        public void RejectsTruncatedOrUnknownPackets()
        {
            Assert.False(IpPacket.TryParse(new byte[] { 0x45, 0, 0 }, out _));
            Assert.False(IpPacket.TryParse(new byte[39].Select((b, i) => i == 0 ? (byte)0x60 : b).ToArray(), out _));
            Assert.False(IpPacket.TryParse(new byte[] { 0x20, 1, 2, 3 }, out _));
            Assert.False(IpPacket.TryParse(ReadOnlySpan<byte>.Empty, out _));
        }

        private static InterfaceInfo Nic(string name, string description, params (string Address, int Prefix)[] addresses) => new InterfaceInfo
        {
            Name = name,
            Description = description,
            IsUp = true,
            Addresses = addresses.Select(a => (IPAddress.Parse(a.Address), a.Prefix)).ToList(),
        };

        private static readonly List<InterfaceInfo> Pc = new List<InterfaceInfo>
        {
            Nic("Wi-Fi", "Intel(R) Wi-Fi 6E AX211", ("10.69.199.37", 16), ("fe80::1", 64), ("2001:db8:1::37", 64)),
            Nic("NordLynx", "NordLynx Tunnel", ("10.5.0.2", 32)),
            Nic("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", ("172.24.0.1", 20)),
            Nic("Ethernet 3", "VirtualBox Host-Only Ethernet Adapter", ("192.168.56.1", 24)),
            new InterfaceInfo { Name = "Ethernet", Description = "Realtek PCIe", IsUp = false, Addresses = new[] { (IPAddress.Parse("192.168.1.9"), 24) } },
            new InterfaceInfo { Name = "Loopback Pseudo-Interface 1", Description = "Software Loopback", IsUp = true, IsLoopback = true, Addresses = new[] { (IPAddress.Loopback, 8) } },
            Nic("Ethernet 2", "Realtek USB", ("169.254.10.10", 16)),
        };

        [Fact]
        public void Select_Default_SkipsVirtualDownLoopbackAndLinkLocal()
        {
            var targets = CaptureAddresses.Select(Pc, null, null, captureIPv6: true);
            Assert.Equal(new[] { "10.69.199.37", "2001:db8:1::37", "10.5.0.2" }, targets.Select(t => t.Address.ToString()).ToArray());
            Assert.Equal("10.69.0.0/16", targets[0].Subnet.ToString());
            Assert.Equal("NordLynx", targets[2].InterfaceName);
        }

        [Fact]
        public void Select_WithoutIPv6_OnlyIPv4()
        {
            var targets = CaptureAddresses.Select(Pc, null, null, captureIPv6: false);
            Assert.Equal(new[] { "10.69.199.37", "10.5.0.2" }, targets.Select(t => t.Address.ToString()).ToArray());
        }

        [Fact]
        public void Select_IncludePatterns_MatchNameOrDescription()
        {
            Assert.Equal(new[] { "10.5.0.2" },
                CaptureAddresses.Select(Pc, new[] { "nord*" }, null, true).Select(t => t.Address.ToString()).ToArray());
            Assert.Equal(new[] { "10.69.199.37", "2001:db8:1::37" },
                CaptureAddresses.Select(Pc, new[] { "*Wi-Fi 6E*" }, null, true).Select(t => t.Address.ToString()).ToArray());
        }

        [Fact]
        public void Select_CustomExcludes_ReplaceDefaults()
        {
            var targets = CaptureAddresses.Select(Pc, null, new[] { "NordLynx" }, captureIPv6: false);
            Assert.Equal(new[] { "10.69.199.37", "172.24.0.1", "192.168.56.1" }, targets.Select(t => t.Address.ToString()).ToArray());
        }

        private static PacketFilter Filter() => new PacketFilter(
            new[] { IPAddress.Parse("10.69.199.37"), IPAddress.Parse("10.5.0.2"), IPAddress.Parse("2001:db8:1::37") },
            new[] { IPNetwork.Parse("10.69.0.0/16"), IPNetwork.Parse("10.5.0.2/32"), IPNetwork.Parse("2001:db8:1::/64") });

        private static bool Classify(PacketFilter f, byte[] raw, out IPAddress remote, out bool inbound)
        {
            Assert.True(IpPacket.TryParse(raw, out var p));
            return f.TryClassify(p, out remote, out inbound);
        }

        [Fact]
        public void Filter_GivesRemoteAndDirection_OnEveryLocalAddress()
        {
            var f = Filter();
            Assert.True(Classify(f, V4("10.69.199.37", "52.112.1.2"), out var r1, out var in1));
            Assert.Equal("52.112.1.2", r1.ToString()); Assert.False(in1);

            Assert.True(Classify(f, V4("170.114.0.9", "10.5.0.2"), out var r2, out var in2));
            Assert.Equal("170.114.0.9", r2.ToString()); Assert.True(in2);

            Assert.True(Classify(f, V6("2603:1063::1", "2001:db8:1::37", 17), out var r3, out var in3));
            Assert.Equal("2603:1063::1", r3.ToString()); Assert.True(in3);
        }

        [Fact]
        public void Filter_IgnoresLocalTcpMulticastAndForeignPackets()
        {
            var f = Filter();
            Assert.False(Classify(f, V4("10.69.199.37", "10.69.1.1"), out _, out _));        // local subnet
            Assert.False(Classify(f, V4("10.69.199.37", "52.112.1.2", 6), out _, out _));    // TCP
            Assert.False(Classify(f, V4("10.69.199.37", "239.255.255.250"), out _, out _));  // multicast (SSDP)
            Assert.False(Classify(f, V4("10.69.199.37", "255.255.255.255"), out _, out _));  // broadcast
            Assert.False(Classify(f, V4("192.168.9.9", "52.112.1.2"), out _, out _));        // not this PC
            Assert.False(Classify(f, V6("fe80::9", "2001:db8:1::37", 17), out _, out _));    // link-local
        }
    }
}
