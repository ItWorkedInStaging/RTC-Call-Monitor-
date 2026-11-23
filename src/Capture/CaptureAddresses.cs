// CaptureAddresses.cs
// Version: 1.1.0
// Changelog:
//   1.1.0 - OnePerInterfaceAndFamily: the address that gets the capture socket.
//   1.0.0 - Initial version. Chooses which local addresses to capture on, from
//           the PC's network interfaces and Detection.Interfaces /
//           ExcludeInterfaces / CaptureIPv6, and works out the local subnets
//           whose traffic is ignored. Pure logic, so it can be tested.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace RtcCallMonitor.Capture
{
    /// <summary>What the selector needs to know about one network interface.</summary>
    public class InterfaceInfo
    {
        public string Name { get; init; }
        public string Description { get; init; }
        public bool IsUp { get; init; }
        public bool IsLoopback { get; init; }
        public IReadOnlyList<(IPAddress Address, int PrefixLength)> Addresses { get; init; } = Array.Empty<(IPAddress, int)>();

        /// <summary>Reads the PC's interfaces.</summary>
        public static List<InterfaceInfo> FromSystem()
        {
            var result = new List<InterfaceInfo>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); }
                catch (NetworkInformationException) { continue; }

                result.Add(new InterfaceInfo
                {
                    Name = nic.Name,
                    Description = nic.Description,
                    IsUp = nic.OperationalStatus == OperationalStatus.Up,
                    IsLoopback = nic.NetworkInterfaceType == NetworkInterfaceType.Loopback,
                    Addresses = props.UnicastAddresses.Select(u => (u.Address, u.PrefixLength)).ToList(),
                });
            }
            return result;
        }
    }

    /// <summary>One address to capture on.</summary>
    public class CaptureTarget
    {
        public string InterfaceName { get; init; }
        public IPAddress Address { get; init; }
        public IPNetwork Subnet { get; init; }

        public override string ToString() => $"{InterfaceName} ({Address})";
    }

    public static class CaptureAddresses
    {
        /// <summary>Used when Detection.ExcludeInterfaces is not set: virtual adapters that never carry call media.</summary>
        public static readonly string[] DefaultExcludes =
        {
            "vEthernet*",            // Hyper-V, WSL, Docker Desktop switches
            "*Hyper-V Virtual*",
            "*VirtualBox Host-Only*",
            "*VMware Virtual*",
            "*Teredo*",
            "*isatap*",
            "*Bluetooth*",
        };

        /// <summary>
        /// Picks the addresses to capture on: interfaces that are up, not loopback,
        /// match Interfaces (if any) and match none of the excludes; their IPv4
        /// addresses (not 169.254.x.x) and, with captureIPv6, global IPv6 addresses.
        /// </summary>
        public static List<CaptureTarget> Select(IEnumerable<InterfaceInfo> interfaces, string[] include, string[] exclude, bool captureIPv6)
        {
            var includes = (include ?? Array.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
            var excludes = exclude ?? DefaultExcludes;
            var result = new List<CaptureTarget>();

            foreach (var nic in interfaces)
            {
                if (!nic.IsUp || nic.IsLoopback) continue;
                if (includes.Length > 0 && !includes.Any(p => Matches(p, nic))) continue;
                if (includes.Length == 0 && excludes.Any(p => Matches(p, nic))) continue;

                foreach (var (address, prefix) in nic.Addresses)
                {
                    if (!IsCapturable(address, captureIPv6)) continue;
                    result.Add(new CaptureTarget
                    {
                        InterfaceName = nic.Name,
                        Address = address,
                        Subnet = SubnetOf(address, prefix),
                    });
                }
            }
            return result;
        }

        /// <summary>
        /// One address per connection and IP version, for the capture socket: the
        /// first stable one (Windows lists permanent addresses before temporary ones).
        /// </summary>
        public static List<CaptureTarget> OnePerInterfaceAndFamily(IEnumerable<CaptureTarget> targets) =>
            targets.GroupBy(t => (t.InterfaceName, t.Address.AddressFamily)).Select(g => g.First()).ToList();

        /// <summary>Case-insensitive wildcard match (* and ?) against the interface name or description.</summary>
        public static bool Matches(string pattern, InterfaceInfo nic)
        {
            var regex = "^" + Regex.Escape(pattern.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            return Regex.IsMatch(nic.Name ?? "", regex, RegexOptions.IgnoreCase)
                || Regex.IsMatch(nic.Description ?? "", regex, RegexOptions.IgnoreCase);
        }

        private static bool IsCapturable(IPAddress address, bool captureIPv6)
        {
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                return !IPAddress.IsLoopback(address) && !IpPacket.IsNonUnicast(address);
            }
            if (address.AddressFamily == AddressFamily.InterNetworkV6 && captureIPv6)
            {
                // global unicast only: not link-local (fe80::), site-local, loopback or IPv4-mapped
                return !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !IPAddress.IsLoopback(address)
                    && !address.IsIPv4MappedToIPv6 && !address.IsIPv6Multicast;
            }
            return false;
        }

        private static IPNetwork SubnetOf(IPAddress address, int prefix)
        {
            var max = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            if (prefix <= 0 || prefix > max) prefix = max;
            var bytes = address.GetAddressBytes();
            for (int i = 0; i < bytes.Length; i++)
            {
                var bits = Math.Clamp(prefix - i * 8, 0, 8);
                bytes[i] &= (byte)(0xFF << (8 - bits));
            }
            return new IPNetwork(new IPAddress(bytes), prefix);
        }
    }

    /// <summary>Decides whether a captured packet is call-relevant, and which side is remote.</summary>
    public class PacketFilter
    {
        private readonly HashSet<IPAddress> _localAddresses;
        private readonly List<IPNetwork> _localNetworks;

        public PacketFilter(IEnumerable<IPAddress> localAddresses, IEnumerable<IPNetwork> localNetworks)
        {
            _localAddresses = new HashSet<IPAddress>(localAddresses);
            _localNetworks = localNetworks.ToList();
        }

        /// <summary>
        /// For a UDP packet between one of this PC's addresses and a remote host
        /// outside the local networks, returns the remote address and whether the
        /// packet was inbound. Otherwise returns false.
        /// </summary>
        public bool TryClassify(in IpPacket packet, out IPAddress remote, out bool inbound)
        {
            remote = null;
            inbound = false;
            if (!packet.IsUdp) return false;

            if (_localAddresses.Contains(packet.Destination)) { remote = packet.Source; inbound = true; }
            else if (_localAddresses.Contains(packet.Source)) { remote = packet.Destination; inbound = false; }
            else return false;

            if (IpPacket.IsNonUnicast(remote) || IPAddress.IsLoopback(remote) || _localAddresses.Contains(remote)) return false;
            foreach (var network in _localNetworks)
            {
                if (network.Contains(remote)) return false;
            }
            return true;
        }
    }
}
