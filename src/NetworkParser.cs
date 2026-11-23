// NetworkParser.cs
// Version: 1.2.0
// Changelog:
//   1.2.0 - A single address ("203.0.113.10", "2001:db8::1") is accepted as a
//           one-address range (/32 or /128), e.g. for a PBX's IP.
//   1.1.0 - Reject shorthand IPv4 such as "3.7.35/25", which .NET would read as
//           3.7.0.35 and silently turn into the wrong network.
//   1.0.0 - Initial version. Wraps System.Net.IPNetwork parsing (replaces the
//           IPNetwork2 package) and reports ranges whose address has host bits
//           set, e.g. "192.168.1.100/24", which are silently widened to the
//           enclosing network ("192.168.1.0/24") so the config can be fixed.

using System;
using System.Net;

namespace RtcCallMonitor
{
    public static class NetworkParser
    {
        /// <summary>
        /// Parses a CIDR range such as "192.168.1.0/24", or a single address. If the address has host
        /// bits set they are cleared, and <paramref name="corrected"/> is set to
        /// the range actually used; otherwise it is null.
        /// </summary>
        /// <exception cref="FormatException">The value is not a valid CIDR range.</exception>
        public static IPNetwork Parse(string cidr, out string corrected)
        {
            corrected = null;
            var value = cidr?.Trim() ?? string.Empty;

            // a single address is a one-address range
            if (value.IndexOf('/') < 0 && IPAddress.TryParse(value, out var single) && (single.AddressFamily !=
                System.Net.Sockets.AddressFamily.InterNetwork || value.Split('.').Length == 4))
            {
                value += single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "/32" : "/128";
            }

            if (value.IndexOf('/') < 0 || !IPNetwork.TryParse(value, out var network))
            {
                throw new FormatException($"'{cidr}' is not a valid CIDR range (expected e.g. 192.168.1.0/24).");
            }

            var addressText = value.Substring(0, value.IndexOf('/'));
            var givenAddress = IPAddress.Parse(addressText);

            // IPAddress.Parse accepts inet_aton shorthand ("10.1/8" = 10.0.0.1); require a.b.c.d
            if (givenAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                addressText.Split('.').Length != 4)
            {
                throw new FormatException($"'{cidr}' is not a valid CIDR range: IPv4 addresses need all four parts (e.g. 3.7.35.0/25).");
            }
            if (!givenAddress.Equals(network.BaseAddress))
            {
                corrected = network.ToString();
            }

            return network;
        }
    }
}
