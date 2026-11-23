// ProviderFeedsTests.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. Microsoft 365 endpoints/version and Zoom list parsing.

using System;
using RtcCallMonitor.ProviderUpdates;
using Xunit;

namespace RtcCallMonitor.Tests
{
    public class ProviderFeedsTests
    {
        // shape documented for https://endpoints.office.com/endpoints/Worldwide
        internal const string MicrosoftEndpointsSample = @"[
          { ""id"": 1, ""serviceArea"": ""Exchange"", ""category"": ""Optimize"", ""required"": true,
            ""ips"": [ ""13.107.6.152/31"" ], ""tcpPorts"": ""443"" },
          { ""id"": 11, ""serviceArea"": ""Skype"", ""serviceAreaDisplayName"": ""Skype for Business Online and Microsoft Teams"",
            ""category"": ""Optimize"", ""required"": true, ""udpPorts"": ""3478,3479,3480,3481"",
            ""ips"": [ ""13.107.64.0/18"", ""52.112.0.0/14"", ""52.122.0.0/15"", ""2603:1063::/38"" ] },
          { ""id"": 12, ""serviceArea"": ""Skype"", ""category"": ""Allow"", ""required"": true,
            ""ips"": [ ""52.238.119.141/32"" ], ""tcpPorts"": ""443"" },
          { ""id"": 46, ""serviceArea"": ""Common"", ""category"": ""Default"", ""urls"": [ ""*.example.com"" ] }
        ]";

        [Fact]
        public void MicrosoftTeams_TakesOnlySkypeOptimizeRanges()
        {
            var ranges = ProviderFeeds.ParseMicrosoftTeams(MicrosoftEndpointsSample);
            Assert.Equal(4, ranges.Count);
            Assert.Equal("13.107.64.0/18", ranges[0]);
            Assert.Equal("2603:1063::/38", ranges[3]);
        }

        [Fact]
        public void MicrosoftTeams_RejectsNonArray()
        {
            Assert.Throws<FormatException>(() => ProviderFeeds.ParseMicrosoftTeams(@"{ ""error"": ""rate limited"" }"));
        }

        [Fact]
        public void MicrosoftVersion_ReadsLatest_FromObjectOrArray()
        {
            Assert.Equal("2026110100", ProviderFeeds.ParseMicrosoftVersion(@"{ ""instance"": ""Worldwide"", ""latest"": ""2026110100"" }"));
            Assert.Equal("2026110100", ProviderFeeds.ParseMicrosoftVersion(
                @"[ { ""instance"": ""China"", ""latest"": ""2026090100"" }, { ""instance"": ""Worldwide"", ""latest"": ""2026110100"" } ]"));
        }

        [Fact]
        public void Zoom_ReadsOneRangePerLine_HandlingCrLfAndBlanks()
        {
            var ranges = ProviderFeeds.ParseZoomText("3.7.35.0/25\r\n3.21.137.128/25\r\n\r\n3.7.35.0/25\n");
            Assert.Equal(2, ranges.Count);
            Assert.Equal("3.21.137.128/25", ranges[1]);
        }

        [Fact]
        public void AutoFile_HasHeaderAndRanges()
        {
            var text = ProviderFeeds.FormatAutoFile("zoom", ProviderFeeds.ZoomMeetingsUrl, null, new[] { "3.7.35.0/25" },
                new DateTimeOffset(2026, 11, 23, 1, 2, 3, TimeSpan.Zero));
            Assert.Contains("# Source: https://assets.zoom.us/docs/ipranges/ZoomMeetings.txt", text);
            Assert.Contains("# Downloaded: 2026-11-23 01:02:03 UTC", text);
            Assert.Contains("\n3.7.35.0/25\n", text);
        }
    }
}
