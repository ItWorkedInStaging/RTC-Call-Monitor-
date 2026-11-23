// ConfigValidator.cs
// Version: 1.1.0
// Changelog:
//   1.1.0 - ValidateProviders: Providers:AutoUpdate:IntervalHours.
//   1.0.0 - Initial version. Startup checks for the Detection, Webhook and
//           Mqtt sections, returning plain-language error messages.

using System;
using System.Collections.Generic;

namespace RtcCallMonitor.Configuration
{
    public static class ConfigValidator
    {
        /// <summary>Returns a list of configuration errors; empty when the config is usable.</summary>
        public static List<string> Validate(DetectionOptions detection, WebhookOptions webhook, MqttOptions mqtt)
        {
            var errors = new List<string>();

            // Detection
            if (detection.LocalNetworks == null || detection.LocalNetworks.Length == 0)
            {
                errors.Add("Detection.LocalNetworks is empty: add your local subnet, e.g. [\"192.168.1.0/24\"].");
            }
            else
            {
                foreach (var net in detection.LocalNetworks)
                {
                    try { NetworkParser.Parse(net, out _); }
                    catch (FormatException) { errors.Add($"Detection.LocalNetworks: '{net}' is not a valid range (expected e.g. 192.168.1.0/24)."); }
                }
            }
            if (detection.CheckIntervalMs < 100) errors.Add("Detection.CheckIntervalMs must be at least 100.");
            if (detection.MinPacketRate < 1) errors.Add("Detection.MinPacketRate must be at least 1.");
            if (detection.CallStartIntervals < 1) errors.Add("Detection.CallStartIntervals must be at least 1.");
            if (detection.CallEndIntervals < 1) errors.Add("Detection.CallEndIntervals must be at least 1.");

            // Webhook
            if (webhook.Enabled)
            {
                if (string.IsNullOrWhiteSpace(webhook.CallStartUrl) && string.IsNullOrWhiteSpace(webhook.CallEndUrl))
                {
                    errors.Add("Webhook.Enabled is true but neither Webhook.CallStartUrl nor Webhook.CallEndUrl is set.");
                }
                CheckUrl(webhook.CallStartUrl, "Webhook.CallStartUrl", errors);
                CheckUrl(webhook.CallEndUrl, "Webhook.CallEndUrl", errors);
            }

            // MQTT
            if (mqtt.Enabled)
            {
                if (string.IsNullOrWhiteSpace(mqtt.Host)) errors.Add("Mqtt.Enabled is true but Mqtt.Host is empty.");
                if (mqtt.Port < 1 || mqtt.Port > 65535) errors.Add($"Mqtt.Port {mqtt.Port} is not a valid port (1-65535).");
                CheckTopic(mqtt.BaseTopic, "Mqtt.BaseTopic", errors);
                CheckTopic(mqtt.DiscoveryPrefix, "Mqtt.DiscoveryPrefix", errors);
                if (!string.IsNullOrEmpty(mqtt.Password) && string.IsNullOrWhiteSpace(mqtt.Username))
                {
                    errors.Add("Mqtt.Password is set but Mqtt.Username is empty.");
                }
                if (string.IsNullOrEmpty(MqttTopics.Slug(mqtt.EffectiveDeviceName)))
                {
                    errors.Add("Mqtt.DeviceName must contain at least one letter or digit.");
                }
            }

            return errors;
        }

        /// <summary>Checks the Providers section (the ranges themselves are checked by ProviderCatalog).</summary>
        public static List<string> ValidateProviders(ProvidersOptions providers)
        {
            var errors = new List<string>();
            if (providers.AutoUpdate != null && providers.AutoUpdate.Enabled && providers.AutoUpdate.IntervalHours < 1)
            {
                errors.Add("Providers.AutoUpdate.IntervalHours must be at least 1.");
            }
            return errors;
        }

        private static void CheckUrl(string value, string name, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                errors.Add($"{name} '{value}' is not a valid http(s) URL.");
            }
        }

        private static void CheckTopic(string value, string name, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(value)) errors.Add($"{name} is empty.");
            else if (value.IndexOfAny(new[] { '+', '#' }) >= 0) errors.Add($"{name} '{value}' must not contain the MQTT wildcards + or #.");
            else if (value.StartsWith("/") || value.EndsWith("/")) errors.Add($"{name} '{value}' must not start or end with '/'.");
        }
    }
}
