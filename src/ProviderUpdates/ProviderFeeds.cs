// ProviderFeeds.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. Parsers for the published IP lists used by the
//           auto-updater: Microsoft 365 endpoints web service (Teams media,
//           Skype service area, Optimize category) and Zoom's ZoomMeetings.txt;
//           plus the file format written to providers/auto.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace RtcCallMonitor.ProviderUpdates
{
    public static class ProviderFeeds
    {
        public const string MicrosoftVersionUrl = "https://endpoints.office.com/version/Worldwide?ClientRequestId={0}";
        public const string MicrosoftEndpointsUrl = "https://endpoints.office.com/endpoints/Worldwide?ServiceAreas=Skype&ClientRequestId={0}";
        public const string MicrosoftDocsUrl = "https://learn.microsoft.com/en-us/microsoft-365/enterprise/urls-and-ip-address-ranges";
        public const string ZoomMeetingsUrl = "https://assets.zoom.us/docs/ipranges/ZoomMeetings.txt";

        /// <summary>Reads "latest" (YYYYMMDDNN) from the Microsoft 365 version method response.</summary>
        public static string ParseMicrosoftVersion(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                root = root.EnumerateArray().FirstOrDefault(e =>
                    e.TryGetProperty("instance", out var i) && string.Equals(i.GetString(), "Worldwide", StringComparison.OrdinalIgnoreCase));
            }
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("latest", out var latest))
            {
                return latest.GetString();
            }
            throw new FormatException("Microsoft version response has no 'latest' field.");
        }

        /// <summary>
        /// Teams media ranges from the Microsoft 365 endpoints method: endpoint sets in the
        /// Skype (Teams) service area with category Optimize, which carry the UDP media traffic.
        /// </summary>
        public static List<string> ParseMicrosoftTeams(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("Microsoft endpoints response is not a JSON array.");
            }

            var result = new List<string>();
            foreach (var set in doc.RootElement.EnumerateArray())
            {
                if (!Is(set, "serviceArea", "Skype") || !Is(set, "category", "Optimize")) continue;
                if (!set.TryGetProperty("ips", out var ips) || ips.ValueKind != JsonValueKind.Array) continue;

                foreach (var ip in ips.EnumerateArray())
                {
                    var value = ip.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(value)) result.Add(value);
                }
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Zoom publishes one CIDR per line; anything that isn't a range is skipped.</summary>
        public static List<string> ParseZoomText(string text)
        {
            var result = new List<string>();
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                result.Add(line);
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Text written to providers/auto/&lt;name&gt;.txt.</summary>
        public static string FormatAutoFile(string provider, string source, string version, IEnumerable<string> ranges, DateTimeOffset downloadedAt)
        {
            var sb = new StringBuilder();
            sb.Append("# ").Append(provider).Append(".txt - downloaded automatically by RTC Call Monitor; changes are overwritten.\n");
            sb.Append("# Delete this file to go back to the bundled providers/").Append(provider).Append(".txt.\n");
            sb.Append("# Source: ").Append(source).Append('\n');
            if (!string.IsNullOrEmpty(version)) sb.Append("# Version: ").Append(version).Append('\n');
            sb.Append("# Downloaded: ").Append(downloadedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss")).Append(" UTC\n");
            sb.Append('\n');
            foreach (var r in ranges) sb.Append(r).Append('\n');
            return sb.ToString();
        }

        private static bool Is(JsonElement e, string property, string value) =>
            e.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String &&
            string.Equals(p.GetString(), value, StringComparison.OrdinalIgnoreCase);
    }
}
