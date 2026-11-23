// ProviderCatalog.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. Loads provider IP ranges from providers/<name>.txt
//           (one CIDR per line, # comments) plus Providers:Inline from config,
//           and maps remote IPs to provider names (cached).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using RtcCallMonitor.Configuration;

namespace RtcCallMonitor
{
    public class ProviderCatalog
    {
        private readonly Dictionary<string, IPNetwork[]> _networks;
        private readonly ConcurrentDictionary<string, string> _cache = new ConcurrentDictionary<string, string>();

        public IReadOnlyDictionary<string, IPNetwork[]> Networks => _networks;
        public IReadOnlyList<string> Errors { get; }
        public IReadOnlyList<string> Warnings { get; }

        /// <summary>Full path of the providers folder that was used (may not exist).</summary>
        public string DirectoryPath { get; }

        private ProviderCatalog(Dictionary<string, IPNetwork[]> networks, List<string> errors, List<string> warnings, string directoryPath)
        {
            _networks = networks;
            Errors = errors;
            Warnings = warnings;
            DirectoryPath = directoryPath;
        }

        /// <summary>Loads all provider files from the configured folder (relative to contentRoot) and merges inline ranges.</summary>
        public static ProviderCatalog Load(ProvidersOptions options, string contentRoot)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var ranges = new Dictionary<string, List<IPNetwork>>(StringComparer.OrdinalIgnoreCase);

            var dir = string.IsNullOrWhiteSpace(options.Directory) ? "providers" : options.Directory;
            var fullDir = Path.IsPathRooted(dir) ? dir : Path.Combine(contentRoot, dir);

            if (Directory.Exists(fullDir))
            {
                foreach (var file in Directory.GetFiles(fullDir, "*.txt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                    var label = $"{dir.TrimEnd('/', '\\')}/{Path.GetFileName(file)}";
                    var parsed = ParseLines(File.ReadAllLines(file), label, errors, warnings);
                    Add(ranges, name, parsed);
                }
            }
            else if (options.Inline == null || options.Inline.Count == 0)
            {
                errors.Add($"Providers folder not found: {fullDir}");
            }

            if (options.Inline != null)
            {
                foreach (var kv in options.Inline)
                {
                    var label = $"Providers:Inline:{kv.Key}";
                    var parsed = ParseLines(kv.Value ?? Array.Empty<string>(), label, errors, warnings);
                    Add(ranges, kv.Key.ToLowerInvariant(), parsed);
                }
            }

            var networks = ranges
                .Where(kv => kv.Value.Count > 0)
                .ToDictionary(kv => kv.Key, kv => kv.Value.Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);

            if (networks.Count == 0 && errors.Count == 0)
            {
                errors.Add($"No provider IP ranges loaded (looked in {fullDir}).");
            }

            return new ProviderCatalog(networks, errors, warnings, fullDir);
        }

        /// <summary>Builds a catalog directly from ranges (used by tests).</summary>
        public static ProviderCatalog FromRanges(IDictionary<string, string[]> ranges)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var networks = ranges.ToDictionary(
                kv => kv.Key.ToLowerInvariant(),
                kv => ParseLines(kv.Value, kv.Key, errors, warnings).ToArray(),
                StringComparer.OrdinalIgnoreCase);
            return new ProviderCatalog(networks, errors, warnings, "");
        }

        /// <summary>
        /// Parses provider lines: one CIDR range per line; blank lines and anything
        /// after '#' are ignored. Invalid lines are reported with their line number.
        /// </summary>
        public static List<IPNetwork> ParseLines(IEnumerable<string> lines, string label, List<string> errors, List<string> warnings)
        {
            var result = new List<IPNetwork>();
            int lineNo = 0;
            foreach (var raw in lines)
            {
                lineNo++;
                var line = raw ?? "";
                var hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash);
                line = line.Trim();
                if (line.Length == 0) continue;

                try
                {
                    result.Add(NetworkParser.Parse(line, out var corrected));
                    if (corrected != null)
                    {
                        warnings.Add($"{label} line {lineNo}: '{line}' is not a network address, using {corrected}");
                    }
                }
                catch (FormatException)
                {
                    errors.Add($"{label} line {lineNo}: '{line}' is not a valid range (expected e.g. 3.7.35.0/25)");
                }
            }
            return result;
        }

        /// <summary>Returns the provider name for a remote IP, or null if it is not in any known range.</summary>
        public string Lookup(string ip)
        {
            return _cache.GetOrAdd(ip, key =>
            {
                if (!IPAddress.TryParse(key, out var address)) return null;
                foreach (var provider in _networks)
                {
                    foreach (var network in provider.Value)
                    {
                        if (network.Contains(address)) return provider.Key;
                    }
                }
                return null;
            });
        }

        private static void Add(Dictionary<string, List<IPNetwork>> ranges, string name, List<IPNetwork> parsed)
        {
            if (!ranges.TryGetValue(name, out var list))
            {
                list = new List<IPNetwork>();
                ranges[name] = list;
            }
            list.AddRange(parsed);
        }
    }
}
