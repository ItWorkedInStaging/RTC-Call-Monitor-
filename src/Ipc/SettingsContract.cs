// SettingsContract.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. The settings the settings window reads and saves
//           through the monitor's pipe (commands getSettings, saveSettings,
//           testMqtt, testWebhook, restart). Secrets are never sent back:
//           the window only learns whether one is set. Shared with the
//           settings app (linked source file, no dependencies).

using System;
using System.Collections.Generic;

namespace RtcCallMonitor.Ipc
{
    public class SettingsDocument
    {
        public DetectionSettings Detection { get; set; } = new DetectionSettings();
        public MqttSettings Mqtt { get; set; } = new MqttSettings();
        public WebhookSettings Webhook { get; set; } = new WebhookSettings();
        public ProviderSettings Providers { get; set; } = new ProviderSettings();

        /// <summary>Read only: where the settings are stored.</summary>
        public string ConfigDirectory { get; set; }
    }

    public class DetectionSettings
    {
        public string[] LocalNetworks { get; set; } = Array.Empty<string>();
        public int CheckIntervalMs { get; set; } = 1000;
        public int MinPacketRate { get; set; } = 1;
        public int CallStartIntervals { get; set; } = 2;
        public int CallEndIntervals { get; set; } = 3;
        public string[] Interfaces { get; set; } = Array.Empty<string>();

        /// <summary>Null means the built-in list of virtual adapters.</summary>
        public string[] ExcludeInterfaces { get; set; }

        /// <summary>Read only: the built-in exclude list, to show as a hint.</summary>
        public string[] DefaultExcludeInterfaces { get; set; }

        public bool CaptureIPv6 { get; set; } = true;
        public int[] IgnoreUdpPorts { get; set; } = { 443 };
    }

    public class MqttSettings
    {
        public bool Enabled { get; set; }
        public string Host { get; set; } = "";
        public int Port { get; set; } = 1883;
        public string Username { get; set; } = "";

        /// <summary>Read only: whether a password is stored.</summary>
        public bool PasswordSet { get; set; }

        /// <summary>On save or test: null keeps the stored password, "" clears it, anything else replaces it.</summary>
        public string NewPassword { get; set; }

        public bool UseTls { get; set; }
        public bool AllowUntrustedCertificates { get; set; }
        public string DeviceName { get; set; } = "";

        /// <summary>Read only: the name used when DeviceName is empty (the computer name).</summary>
        public string DefaultDeviceName { get; set; }

        public string BaseTopic { get; set; } = "rtc-call-monitor";
        public string DiscoveryPrefix { get; set; } = "homeassistant";
    }

    public class WebhookSettings
    {
        public bool Enabled { get; set; }
        public string CallStartUrl { get; set; } = "";
        public string CallEndUrl { get; set; } = "";
        public List<WebhookHeader> Headers { get; set; } = new List<WebhookHeader>();
    }

    public class WebhookHeader
    {
        public string Name { get; set; } = "";

        /// <summary>
        /// Sent back empty for secret headers (Authorization, *token*, *key*);
        /// on save, null keeps the stored value.
        /// </summary>
        public string Value { get; set; }

        /// <summary>Read only: the header holds a secret that is stored encrypted.</summary>
        public bool IsSecret { get; set; }
    }

    public class ProviderSettings
    {
        public bool AutoUpdateEnabled { get; set; } = true;
        public int AutoUpdateIntervalHours { get; set; } = 24;

        /// <summary>Your own lists (e.g. 3cx), added to the bundled/downloaded ranges.</summary>
        public List<CustomProvider> Custom { get; set; } = new List<CustomProvider>();

        /// <summary>Read only: what is loaded right now.</summary>
        public List<LoadedProvider> Loaded { get; set; } = new List<LoadedProvider>();
    }

    public class CustomProvider
    {
        /// <summary>Letters, digits, '-' and '_' (becomes the file name and the provider name).</summary>
        public string Name { get; set; } = "";
        public List<string> Ranges { get; set; } = new List<string>();
    }

    public class LoadedProvider
    {
        public string Name { get; set; }
        public int Ranges { get; set; }

        /// <summary>e.g. "providers/zoom.txt", "auto/zoom.txt", "custom/3cx.txt".</summary>
        public string Source { get; set; }
    }

    /// <summary>Answer to saveSettings / testMqtt / testWebhook / restart.</summary>
    public class SettingsResult
    {
        public bool Ok { get; set; }

        /// <summary>Plain-language problems, e.g. "Mqtt.Host is empty".</summary>
        public List<string> Errors { get; set; } = new List<string>();

        /// <summary>e.g. "Connected to 10.69.69.17:1883" or "Saved; the monitor restarts".</summary>
        public string Message { get; set; }

        /// <summary>Settings as stored after a save.</summary>
        public SettingsDocument Settings { get; set; }
    }
}
