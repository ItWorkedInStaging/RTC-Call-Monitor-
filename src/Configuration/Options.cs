// Options.cs
// Version: 1.3.0
// Changelog:
//   1.3.0 - LogFile section (rolling log file).
//   1.2.0 - Detection.Interfaces, ExcludeInterfaces, CaptureIPv6; LocalNetworks
//           is now optional (each interface's own subnet is added automatically).
//   1.1.0 - Providers.AutoUpdate settings (Enabled, IntervalHours).
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

        /// <summary>Extra local subnets in CIDR notation whose traffic is ignored; each interface's own subnet is always ignored.</summary>
        public string[] LocalNetworks { get; set; } = Array.Empty<string>();

        /// <summary>Only capture on interfaces whose name or description matches one of these (* and ? wildcards). Empty: all.</summary>
        public string[] Interfaces { get; set; } = Array.Empty<string>();

        /// <summary>Skip interfaces matching these patterns (ignored when Interfaces is set). Null: built-in list of virtual adapters.</summary>
        public string[] ExcludeInterfaces { get; set; }

        /// <summary>Also capture on global IPv6 addresses.</summary>
        public bool CaptureIPv6 { get; set; } = true;

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

        /// <summary>Download official lists (Teams, Zoom) into &lt;Directory&gt;/auto.</summary>
        public ProviderAutoUpdateOptions AutoUpdate { get; set; } = new ProviderAutoUpdateOptions();
    }

    /// <summary>"Providers:AutoUpdate": automatic download of published provider IP lists.</summary>
    public class ProviderAutoUpdateOptions
    {
        public bool Enabled { get; set; } = true;

        /// <summary>Hours between checks (minimum 1).</summary>
        public int IntervalHours { get; set; } = 24;
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

    /// <summary>"LogFile" section: rolling log file (in addition to the console and Event Viewer).</summary>
    public class LogFileOptions
    {
        public const string Section = "LogFile";

        public bool Enabled { get; set; } = true;

        /// <summary>Folder for the log files; empty means %ProgramData%\RtcCallMonitor\logs.</summary>
        public string Directory { get; set; } = "";

        /// <summary>How many daily files to keep.</summary>
        public int RetainedDays { get; set; } = 14;

        /// <summary>A file that reaches this size continues in a new file (_001, _002, ...).</summary>
        public int MaxFileSizeMB { get; set; } = 10;

        public string EffectiveDirectory => string.IsNullOrWhiteSpace(Directory)
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RtcCallMonitor", "logs")
            : Environment.ExpandEnvironmentVariables(Directory.Trim());
    }
}
