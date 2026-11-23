// MqttTopicsTests.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. Topic names, device id slugs and Home Assistant
//           discovery payloads.

using System;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace RtcCallMonitor.Tests
{
    public class MqttTopicsTests
    {
        [Theory]
        [InlineData("josh-pc", "josh-pc")]
        [InlineData("JOSH-PC", "josh-pc")]
        [InlineData("Josh's PC", "josh_s_pc")]
        [InlineData("  office pc #2 ", "office_pc_2")]
        [InlineData("***", "")]
        public void Slug_MakesSafeIds(string input, string expected)
        {
            Assert.Equal(expected, MqttTopics.Slug(input));
        }

        [Fact]
        public void Topics_AreBuiltFromBaseTopicAndDevice()
        {
            var t = new MqttTopics("rtc-call-monitor/", "homeassistant", "josh-pc");
            Assert.Equal("rtc-call-monitor/josh-pc/state", t.State);
            Assert.Equal("rtc-call-monitor/josh-pc/provider", t.Provider);
            Assert.Equal("rtc-call-monitor/josh-pc/attributes", t.Attributes);
            Assert.Equal("rtc-call-monitor/josh-pc/last_duration", t.LastDuration);
            Assert.Equal("rtc-call-monitor/josh-pc/availability", t.Availability);
            Assert.Equal("homeassistant/status", t.HomeAssistantStatus);
        }

        [Fact]
        public void Discovery_DefinesThreeEntitiesOnOneDevice()
        {
            var t = new MqttTopics("rtc-call-monitor", "homeassistant", "josh-pc");
            var messages = t.DiscoveryMessages("2.1.0");

            Assert.Equal(3, messages.Count);
            Assert.Equal("homeassistant/binary_sensor/josh-pc/on_call/config", messages[0].Topic);
            Assert.Equal("homeassistant/sensor/josh-pc/call_provider/config", messages[1].Topic);
            Assert.Equal("homeassistant/sensor/josh-pc/last_call_duration/config", messages[2].Topic);

            var docs = messages.Select(m => JsonDocument.Parse(m.Payload).RootElement).ToList();
            foreach (var d in docs)
            {
                Assert.Equal("rtc-call-monitor/josh-pc/availability", d.GetProperty("availability_topic").GetString());
                Assert.Equal("rtc_call_monitor_josh-pc", d.GetProperty("device").GetProperty("identifiers")[0].GetString());
                Assert.Equal("2.1.0", d.GetProperty("device").GetProperty("sw_version").GetString());
            }

            var onCall = docs[0];
            Assert.Equal("rtc_call_monitor_josh-pc_on_call", onCall.GetProperty("unique_id").GetString());
            Assert.Equal("rtc-call-monitor/josh-pc/state", onCall.GetProperty("state_topic").GetString());
            Assert.Equal("ON", onCall.GetProperty("payload_on").GetString());
            Assert.Equal("OFF", onCall.GetProperty("payload_off").GetString());
            Assert.Equal("rtc-call-monitor/josh-pc/attributes", onCall.GetProperty("json_attributes_topic").GetString());

            var duration = docs[2];
            Assert.Equal("duration", duration.GetProperty("device_class").GetString());
            Assert.Equal("s", duration.GetProperty("unit_of_measurement").GetString());
        }

        [Fact]
        public void Attributes_AreEmptyObjectWhenIdle()
        {
            Assert.Equal("{}", MqttTopics.AttributesPayload(null, null, null));
        }

        [Fact]
        public void Attributes_IncludeProviderIpAndUtcStart()
        {
            var json = MqttTopics.AttributesPayload("msteams", "52.112.1.2", new DateTimeOffset(2026, 11, 23, 8, 40, 0, TimeSpan.FromHours(8)));
            var d = JsonDocument.Parse(json).RootElement;
            Assert.Equal("msteams", d.GetProperty("provider").GetString());
            Assert.Equal("52.112.1.2", d.GetProperty("ip").GetString());
            Assert.Equal("2026-11-23T00:40:00Z", d.GetProperty("started").GetString());
        }
    }
}
