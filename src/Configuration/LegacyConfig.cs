// LegacyConfig.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. Translates the pre-2.1 "Application"/"Provider"
//           config sections into the new layout so existing files keep working.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace RtcCallMonitor.Configuration
{
    public static class LegacyConfig
    {
        // old key -> new key
        private static readonly (string Old, string New)[] ScalarMap =
        {
            ("Application:CheckInterval", "Detection:CheckIntervalMs"),
            ("Application:MinPacketRate", "Detection:MinPacketRate"),
            ("Application:CallStartIntervals", "Detection:CallStartIntervals"),
            ("Application:CallEndIntervals", "Detection:CallEndIntervals"),
            ("Application:CallStartWebhook", "Webhook:CallStartUrl"),
            ("Application:CallEndWebhook", "Webhook:CallEndUrl"),
        };

        /// <summary>
        /// Returns new-layout keys for any old-layout values found. Values already
        /// present in the new layout always win. Adds a warning when anything was mapped.
        /// </summary>
        public static Dictionary<string, string> Map(IConfiguration config, List<string> warnings)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var used = new List<string>();

            foreach (var (oldKey, newKey) in ScalarMap)
            {
                var value = config[oldKey];
                if (value != null && config[newKey] == null)
                {
                    result[newKey] = value;
                    used.Add(oldKey);
                }
            }

            // Application:LocalNetwork (array, or a plain string as the old README showed)
            var oldNets = config.GetSection("Application:LocalNetwork");
            if (oldNets.Exists() && !config.GetSection("Detection:LocalNetworks").Exists())
            {
                var values = oldNets.GetChildren().Select(c => c.Value).Where(v => v != null).ToList();
                if (values.Count == 0 && oldNets.Value != null) values.Add(oldNets.Value);
                for (int i = 0; i < values.Count; i++) result[$"Detection:LocalNetworks:{i}"] = values[i];
                used.Add("Application:LocalNetwork");
            }

            // old webhook URLs mean the webhook output was in use
            if (config["Webhook:Enabled"] == null &&
                (result.ContainsKey("Webhook:CallStartUrl") || result.ContainsKey("Webhook:CallEndUrl")))
            {
                var anyUrl = new[] { "Webhook:CallStartUrl", "Webhook:CallEndUrl" }
                    .Any(k => result.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v));
                result["Webhook:Enabled"] = anyUrl ? "true" : "false";
            }

            // Provider:KnownNetworks:<name>:<i> -> Providers:Inline:<name>:<i>
            var known = config.GetSection("Provider:KnownNetworks");
            if (known.Exists())
            {
                foreach (var provider in known.GetChildren())
                {
                    if (config.GetSection($"Providers:Inline:{provider.Key}").Exists()) continue;
                    foreach (var range in provider.GetChildren())
                    {
                        result[$"Providers:Inline:{provider.Key}:{range.Key}"] = range.Value;
                    }
                }
                used.Add("Provider:KnownNetworks");
            }

            if (used.Count > 0)
            {
                warnings.Add("Old-style settings found (" + string.Join(", ", used) +
                             "). They still work, but please move them to the Detection, Webhook and Providers sections (see README).");
            }

            return result;
        }
    }
}
