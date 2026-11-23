// LegacyConfigTests.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. Old "Application"/"Provider" settings are mapped
//           to the new sections, and new-style values win.

using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using RtcCallMonitor.Configuration;
using Xunit;

namespace RtcCallMonitor.Tests
{
    public class LegacyConfigTests
    {
        private static IConfiguration Build(Dictionary<string, string> values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        [Fact]
        public void OldSettings_AreMappedToNewSections()
        {
            var warnings = new List<string>();
            var config = Build(new Dictionary<string, string>
            {
                ["Application:LocalNetwork:0"] = "192.168.1.0/24",
                ["Application:CheckInterval"] = "2000",
                ["Application:MinPacketRate"] = "5",
                ["Application:CallStartWebhook"] = "http://ha.local/api/webhook/start",
                ["Application:CallEndWebhook"] = "",
                ["Provider:KnownNetworks:zoom:0"] = "170.114.0.0/16",
            });

            var mapped = LegacyConfig.Map(config, warnings);

            Assert.Equal("192.168.1.0/24", mapped["Detection:LocalNetworks:0"]);
            Assert.Equal("2000", mapped["Detection:CheckIntervalMs"]);
            Assert.Equal("5", mapped["Detection:MinPacketRate"]);
            Assert.Equal("http://ha.local/api/webhook/start", mapped["Webhook:CallStartUrl"]);
            Assert.Equal("true", mapped["Webhook:Enabled"]);
            Assert.Equal("170.114.0.0/16", mapped["Providers:Inline:zoom:0"]);
            Assert.Contains("Old-style settings found", Assert.Single(warnings));
        }

        [Fact]
        public void OldEmptyWebhookUrls_LeaveWebhookDisabled()
        {
            var mapped = LegacyConfig.Map(Build(new Dictionary<string, string>
            {
                ["Application:CallStartWebhook"] = "",
                ["Application:CallEndWebhook"] = "",
            }), new List<string>());

            Assert.Equal("false", mapped["Webhook:Enabled"]);
        }

        [Fact]
        public void NewSettings_WinOverOldOnes()
        {
            var mapped = LegacyConfig.Map(Build(new Dictionary<string, string>
            {
                ["Application:CheckInterval"] = "2000",
                ["Detection:CheckIntervalMs"] = "500",
                ["Application:LocalNetwork:0"] = "10.0.0.0/8",
                ["Detection:LocalNetworks:0"] = "192.168.1.0/24",
            }), new List<string>());

            Assert.False(mapped.ContainsKey("Detection:CheckIntervalMs"));
            Assert.False(mapped.ContainsKey("Detection:LocalNetworks:0"));
        }

        [Fact]
        public void NewStyleConfig_ProducesNoMappingsOrWarnings()
        {
            var warnings = new List<string>();
            var mapped = LegacyConfig.Map(Build(new Dictionary<string, string>
            {
                ["Detection:LocalNetworks:0"] = "192.168.1.0/24",
                ["Mqtt:Enabled"] = "true",
            }), warnings);

            Assert.Empty(mapped);
            Assert.Empty(warnings);
        }
    }
}
