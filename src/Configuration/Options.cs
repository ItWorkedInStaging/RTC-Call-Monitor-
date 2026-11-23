// Options.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. Settings classes for the 2.1 config layout:
//           Detection, Providers, Webhook and Mqtt sections.

using System;
using System.Collections.Generic;

namespace RtcCallMonitor.Configuration
{
    /// <summary>"Detection" section: how calls are recognised.</summary>
    public class DetectionOptions
    {
        public const string Section = "Detection";

        /// <summary>Local subnets in CIDR notation; traffic inside them is ignored.</summary>
        public string[] LocalNetworks { get; set; } = Array.Empty<string>();

        /// <summary>Length of each measurement interval in milliseconds.</summary>
        public int CheckIntervalMs { get; set; } = 1000;

        /// <summary>UDP packets per interval to/from a provider IP that count as call traffic.</summary>
        public int MinPacketRate { get; set; } = 1;

        /// <summary>Consecutive busy intervals before a call is reported as started.</summary>
        public int CallStartIntervals { get; set; } = 2;

        /// <summary>Consecutive quiet intervals before a call is reported as ended.</summary>
        public int CallEndIntervals { get; set; } = 3;
    }

    /// <summary>"Providers" section: where the provider IP ranges come from.</summary>
    public class ProvidersOptions
    {
        public const string Section = "Providers";

        /// <summary>Folder with one &lt;provider&gt;.txt file per provider, relative to the app folder.</summary>
        public string Directory { get; set; } = "providers";

        /// <summary>Extra ranges per provider, merged with the files (also used for old-format configs).</summary>
        public Dictionary<string, string[]> Inline { get; set; } = new Dictionary<string, string[]>();
    }

    /// <summary>"Webhook" section: HTTP POST on call start/end.</summary>
    public class WebhookOptions
    {
        public const string Section = "Webhook";

        public bool Enabled { get; set; }
        public string CallStartUrl { get; set; } = "";
        public string CallEndUrl { get; set; } = "";

        /// <summary>Extra HTTP headers, e.g. "Authorization": "Bearer ...".</summary>
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>"Mqtt" section: state publishing with Home Assistant discovery.</summary>
    public class MqttOptions
    {
        public const string Section = "Mqtt";

        public bool Enabled { get; set; }
        public string Host { get; set; } = "";
        public int Port { get; set; } = 1883;
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public bool UseTls { get; set; }

        /// <summary>Accept self-signed/untrusted broker certificates (TLS only).</summary>
        public bool AllowUntrustedCertificates { get; set; }

        /// <summary>Name of this PC in Home Assistant; defaults to the computer name.</summary>
        public string DeviceName { get; set; } = "";

        public string BaseTopic { get; set; } = "rtc-call-monitor";
        public string DiscoveryPrefix { get; set; } = "homeassistant";

        public string EffectiveDeviceName =>
            string.IsNullOrWhiteSpace(DeviceName) ? Environment.MachineName.ToLowerInvariant() : DeviceName.Trim();
    }
}
