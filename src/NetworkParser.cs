// NetworkParser.cs
// Version: 1.0.0
// Changelog:
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
        /// Parses a CIDR range such as "192.168.1.0/24". If the address has host
        /// bits set they are cleared, and <paramref name="corrected"/> is set to
        /// the range actually used; otherwise it is null.
        /// </summary>
        /// <exception cref="FormatException">The value is not a valid CIDR range.</exception>
        public static IPNetwork Parse(string cidr, out string corrected)
        {
            corrected = null;
            var value = cidr?.Trim() ?? string.Empty;

            if (value.IndexOf('/') < 0 || !IPNetwork.TryParse(value, out var network))
            {
                throw new FormatException($"'{cidr}' is not a valid CIDR range (expected e.g. 192.168.1.0/24).");
            }

            var givenAddress = IPAddress.Parse(value.Substring(0, value.IndexOf('/')));
            if (!givenAddress.Equals(network.BaseAddress))
            {
                corrected = network.ToString();
            }

            return network;
        }
    }
}
