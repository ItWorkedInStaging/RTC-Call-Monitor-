// MqttTopics.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. MQTT topic names and Home Assistant discovery
//           payloads (binary_sensor on_call, sensor call_provider,
//           sensor last_call_duration). Pure functions so they can be tested.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace RtcCallMonitor
{
    public class MqttTopics
    {
        public const string Online = "online";
        public const string Offline = "offline";
        public const string On = "ON";
        public const string Off = "OFF";
        public const string NoProvider = "none";

        public string DeviceName { get; }
        public string DeviceId { get; }
        public string DiscoveryPrefix { get; }

        public string Base { get; }
        public string State => Base + "/state";
        public string Provider => Base + "/provider";
        public string Attributes => Base + "/attributes";
        public string LastDuration => Base + "/last_duration";
        public string Availability => Base + "/availability";

        /// <summary>Home Assistant publishes "online" here when it (re)starts.</summary>
        public string HomeAssistantStatus => DiscoveryPrefix + "/status";

        public MqttTopics(string baseTopic, string discoveryPrefix, string deviceName)
        {
            DeviceName = deviceName;
            DeviceId = Slug(deviceName);
            DiscoveryPrefix = discoveryPrefix.Trim().Trim('/');
            Base = $"{baseTopic.Trim().Trim('/')}/{DeviceId}";
        }

        /// <summary>
        /// Lower-case id safe for topics and HA unique_ids: letters, digits, '-' and '_';
        /// anything else becomes '_'. "Josh's PC" -> "josh_s_pc".
        /// </summary>
        public static string Slug(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var sb = new StringBuilder();
            foreach (var ch in value.Trim().ToLowerInvariant())
            {
                var ok = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_';
                var c = ok ? ch : '_';
                if (c == '_' && sb.Length > 0 && sb[sb.Length - 1] == '_') continue;
                sb.Append(c);
            }
            return sb.ToString().Trim('_');
        }

        /// <summary>Discovery (topic, JSON payload) pairs, published retained on connect and when HA restarts.</summary>
        public IReadOnlyList<(string Topic, string Payload)> DiscoveryMessages(string softwareVersion)
        {
            var device = new Dictionary<string, object>
            {
                ["identifiers"] = new[] { $"rtc_call_monitor_{DeviceId}" },
                ["name"] = DeviceName,
                ["model"] = "RTC Call Monitor",
                ["manufacturer"] = "RTC-Call-Monitor",
                ["sw_version"] = softwareVersion,
            };
            var origin = new Dictionary<string, object>
            {
                ["name"] = "RTC Call Monitor",
                ["sw_version"] = softwareVersion,
            };

            Dictionary<string, object> Common(string objectId, string name) => new Dictionary<string, object>
            {
                ["name"] = name,
                ["unique_id"] = $"rtc_call_monitor_{DeviceId}_{objectId}",
                ["availability_topic"] = Availability,
                ["payload_available"] = Online,
                ["payload_not_available"] = Offline,
                ["device"] = device,
                ["origin"] = origin,
            };

            var onCall = Common("on_call", "On call");
            onCall["state_topic"] = State;
            onCall["payload_on"] = On;
            onCall["payload_off"] = Off;
            onCall["device_class"] = "occupancy";
            onCall["icon"] = "mdi:phone-in-talk";
            onCall["json_attributes_topic"] = Attributes;

            var provider = Common("call_provider", "Call provider");
            provider["state_topic"] = Provider;
            provider["icon"] = "mdi:video-account";

            var duration = Common("last_call_duration", "Last call duration");
            duration["state_topic"] = LastDuration;
            duration["device_class"] = "duration";
            duration["unit_of_measurement"] = "s";
            duration["icon"] = "mdi:timer-outline";

            return new[]
            {
                ($"{DiscoveryPrefix}/binary_sensor/{DeviceId}/on_call/config", JsonSerializer.Serialize(onCall)),
                ($"{DiscoveryPrefix}/sensor/{DeviceId}/call_provider/config", JsonSerializer.Serialize(provider)),
                ($"{DiscoveryPrefix}/sensor/{DeviceId}/last_call_duration/config", JsonSerializer.Serialize(duration)),
            };
        }

        /// <summary>Attributes JSON for the on_call sensor: provider, ip and start time while on a call; {} otherwise.</summary>
        public static string AttributesPayload(string provider, string ip, DateTimeOffset? startedAt)
        {
            if (provider == null) return "{}";
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["provider"] = provider,
                ["ip"] = ip,
                ["started"] = startedAt?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            });
        }
    }
}
