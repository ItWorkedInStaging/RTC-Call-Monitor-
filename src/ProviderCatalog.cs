// ProviderCatalog.cs
// Version: 1.1.0
// Changelog:
//   1.1.0 - Reloadable while running (Reload swaps in a new set of ranges, or
//           keeps the current one if the files have errors). Files in
//           <Directory>/auto (downloaded lists) replace the bundled file of the
//           same provider.
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
        public const string AutoFolderName = "auto";

        /// <summary>One loaded set of ranges with its lookup cache; replaced as a whole on reload.</summary>
        private sealed class Snapshot
        {
            public Dictionary<string, IPNetwork[]> Networks;
            public Dictionary<string, string> Sources; // provider -> file/section it came from
            public readonly ConcurrentDictionary<string, string> Cache = new ConcurrentDictionary<string, string>();
        }

        private readonly ProvidersOptions _options;
        private volatile Snapshot _snapshot;
        private readonly object _reloadLock = new object();

        public IReadOnlyDictionary<string, IPNetwork[]> Networks => _snapshot.Networks;

        /// <summary>Where each provider's ranges were read from, e.g. "providers/auto/zoom.txt".</summary>
        public IReadOnlyDictionary<string, string> Sources => _snapshot.Sources;

        /// <summary>Problems found by the first load (reported by the startup check).</summary>
        public IReadOnlyList<string> Errors { get; }
        public IReadOnlyList<string> Warnings { get; }

        /// <summary>Full path of the providers folder (may not exist).</summary>
        public string DirectoryPath { get; }

        /// <summary>Full path of the folder for downloaded lists.</summary>
        public string AutoDirectoryPath => Path.Combine(DirectoryPath, AutoFolderName);

        private ProviderCatalog(ProvidersOptions options, string directoryPath, Snapshot snapshot, List<string> errors, List<string> warnings)
        {
            _options = options;
            DirectoryPath = directoryPath;
            _snapshot = snapshot;
            Errors = errors;
            Warnings = warnings;
        }

        /// <summary>Loads all provider files from the configured folder (relative to contentRoot) and merges inline ranges.</summary>
        public static ProviderCatalog Load(ProvidersOptions options, string contentRoot)
        {
            var dir = string.IsNullOrWhiteSpace(options.Directory) ? "providers" : options.Directory;
            var fullDir = Path.IsPathRooted(dir) ? dir : Path.Combine(contentRoot, dir);
            var errors = new List<string>();
            var warnings = new List<string>();
            var snapshot = Build(options, dir, fullDir, errors, warnings);
            return new ProviderCatalog(options, fullDir, snapshot, errors, warnings);
        }

        /// <summary>Builds a catalog directly from ranges (used by tests).</summary>
        public static ProviderCatalog FromRanges(IDictionary<string, string[]> ranges)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var snapshot = new Snapshot
            {
                Networks = ranges.ToDictionary(
                    kv => kv.Key.ToLowerInvariant(),
                    kv => ParseLines(kv.Value, kv.Key, errors, warnings).ToArray(),
                    StringComparer.OrdinalIgnoreCase),
                Sources = ranges.ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => "test", StringComparer.OrdinalIgnoreCase),
            };
            return new ProviderCatalog(new ProvidersOptions(), "", snapshot, errors, warnings);
        }

        /// <summary>
        /// Re-reads the provider files. On success the new ranges are used from the
        /// next lookup on; if there are errors the current ranges stay in use.
        /// </summary>
        public bool Reload(out List<string> errors, out List<string> warnings)
        {
            errors = new List<string>();
            warnings = new List<string>();
            if (string.IsNullOrEmpty(DirectoryPath)) return false;

            lock (_reloadLock)
            {
                var dir = string.IsNullOrWhiteSpace(_options.Directory) ? "providers" : _options.Directory;
                var snapshot = Build(_options, dir, DirectoryPath, errors, warnings);
                if (errors.Count > 0) return false;
                _snapshot = snapshot;
                return true;
            }
        }

        private static Snapshot Build(ProvidersOptions options, string dir, string fullDir, List<string> errors, List<string> warnings)
        {
            var ranges = new Dictionary<string, List<IPNetwork>>(StringComparer.OrdinalIgnoreCase);
            var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var label = dir.TrimEnd('/', '\\').Replace('\\', '/');

            if (Directory.Exists(fullDir))
            {
                // bundled lists
                foreach (var (name, file) in TxtFiles(fullDir))
                {
                    ranges[name] = ParseLines(ReadLines(file, errors), $"{label}/{Path.GetFileName(file)}", errors, warnings);
                    sources[name] = $"{label}/{Path.GetFileName(file)}";
                }

                // downloaded lists replace the bundled list of the same provider
                var autoDir = Path.Combine(fullDir, AutoFolderName);
                if (Directory.Exists(autoDir))
                {
                    foreach (var (name, file) in TxtFiles(autoDir))
                    {
                        var fileLabel = $"{label}/{AutoFolderName}/{Path.GetFileName(file)}";
                        var parsed = ParseLines(ReadLines(file, errors), fileLabel, errors, warnings);
                        if (parsed.Count == 0)
                        {
                            warnings.Add($"{fileLabel} has no ranges; using the bundled list instead");
                            continue;
                        }
                        ranges[name] = parsed;
                        sources[name] = fileLabel;
                    }
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
                    var name = kv.Key.ToLowerInvariant();
                    var parsed = ParseLines(kv.Value ?? Array.Empty<string>(), $"Providers:Inline:{kv.Key}", errors, warnings);
                    if (!ranges.TryGetValue(name, out var list))
                    {
                        list = new List<IPNetwork>();
                        ranges[name] = list;
                        sources[name] = $"Providers:Inline:{kv.Key}";
                    }
                    else
                    {
                        sources[name] += " + Providers:Inline";
                    }
                    list.AddRange(parsed);
                }
            }

            var networks = ranges
                .Where(kv => kv.Value.Count > 0)
                .ToDictionary(kv => kv.Key, kv => kv.Value.Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);

            if (networks.Count == 0 && errors.Count == 0)
            {
                errors.Add($"No provider IP ranges loaded (looked in {fullDir}).");
            }

            return new Snapshot
            {
                Networks = networks,
                Sources = sources.Where(kv => networks.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            };
        }

        private static IEnumerable<(string Name, string File)> TxtFiles(string dir) =>
            Directory.GetFiles(dir, "*.txt")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f => (Path.GetFileNameWithoutExtension(f).ToLowerInvariant(), f));

        private static string[] ReadLines(string file, List<string> errors)
        {
            try { return File.ReadAllLines(file); }
            catch (IOException e) { errors.Add($"Could not read {file}: {e.Message}"); return Array.Empty<string>(); }
            catch (UnauthorizedAccessException e) { errors.Add($"Could not read {file}: {e.Message}"); return Array.Empty<string>(); }
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
            var snapshot = _snapshot; // one consistent set for this lookup
            return snapshot.Cache.GetOrAdd(ip, key =>
            {
                if (!IPAddress.TryParse(key, out var address)) return null;
                foreach (var provider in snapshot.Networks)
                {
                    foreach (var network in provider.Value)
                    {
                        if (network.Contains(address)) return provider.Key;
                    }
                }
                return null;
            });
        }
    }
}
