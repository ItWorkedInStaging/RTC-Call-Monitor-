// NetworkParserTests.cs
// Version: 1.2.0
// Changelog:
//   1.2.0 - A single address is accepted as a /32 or /128 range.
//   1.1.0 - Shorthand IPv4 ranges ("3.7.35/25") are rejected.
//   1.0.0 - Initial version. Valid, host-bits-set and invalid CIDR ranges.

using System;
using System.Net;
using Xunit;

namespace RtcCallMonitor.Tests
{
    public class NetworkParserTests
    {
        [Theory]
        [InlineData("192.168.1.0/24")]
        [InlineData("13.107.64.0/18")]
        [InlineData("2001:db8::/32")]
        public void ValidRange_ParsesWithoutCorrection(string cidr)
        {
            var net = NetworkParser.Parse(cidr, out var corrected);
            Assert.Null(corrected);
            Assert.Equal(cidr, net.ToString());
        }

        [Theory]
        [InlineData("202.173.24.0/20", "202.173.16.0/20")] // real entry in appsettings.json
        [InlineData("192.168.1.100/24", "192.168.1.0/24")]  // common LocalNetwork mistake
        [InlineData("10.1.2.3/8", "10.0.0.0/8")]
        public void HostBitsSet_AreMasked(string cidr, string expected)
        {
            var net = NetworkParser.Parse(cidr, out var corrected);
            Assert.Equal(expected, corrected);
            Assert.True(net.Contains(IPAddress.Parse(expected.Split('/')[0])));
        }

        [Theory]
        [InlineData("")]
        [InlineData("192.168.1")]
        [InlineData("10")]
        [InlineData("192.168.1.0/33")]
        [InlineData("not-an-ip/24")]
        [InlineData("3.7.35/25")]
        [InlineData("192.168.1/24")]
        public void InvalidRange_Throws(string cidr)
        {
            Assert.Throws<FormatException>(() => NetworkParser.Parse(cidr, out _));
        }

        [Theory]
        [InlineData("203.0.113.10", "203.0.113.10/32")]
        [InlineData("2001:db8::1", "2001:db8::1/128")]
        public void SingleAddress_IsAOneAddressRange(string value, string expected)
        {
            var net = NetworkParser.Parse(value, out var corrected);
            Assert.Equal(expected, net.ToString());
            Assert.Null(corrected);
        }
    }
}
