// ProviderCatalog.cs
// Version: 1.3.0
// Changelog:
//   1.3.0 - Three places: bundled lists (Program Files), downloaded lists and
//           your own lists in the config folder (ConfigPaths). Downloaded lists
//           replace the bundled list of a provider; your own lists add to it.
//   1.2.0 - Reload reports whether the ranges actually changed.
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
            public string Fingerprint;
            public readonly ConcurrentDictionary<string, string> Cache = new ConcurrentDictionary<string, string>();
        }

        private readonly ProvidersOptions _options;
        private readonly string _bundledLabel;
        private readonly string _autoDir;
        private readonly string _autoLabel;
        private readonly string _customDir;
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
        public string AutoDirectoryPath => _autoDir;

        /// <summary>Full path of the folder with your own lists, or null.</summary>
        public string CustomDirectoryPath => _customDir;

        private ProviderCatalog(ProvidersOptions options, string directoryPath, string bundledLabel, string autoDir, string autoLabel,
            string customDir, Snapshot snapshot, List<string> errors, List<string> warnings)
        {
            _options = options;
            DirectoryPath = directoryPath;
            _bundledLabel = bundledLabel;
            _autoDir = autoDir;
            _autoLabel = autoLabel;
            _customDir = customDir;
            _snapshot = snapshot;
            Errors = errors;
            Warnings = warnings;
        }

        /// <summary>Bundled lists in &lt;contentRoot&gt;/providers, downloaded lists in its auto subfolder (no config folder).</summary>
        public static ProviderCatalog Load(ProvidersOptions options, string contentRoot) => Load(options, contentRoot, null);

        /// <summary>
        /// Bundled lists from the configured folder (relative to contentRoot); with a config
        /// folder, downloaded lists and your own lists from there. Inline ranges are merged in.
        /// </summary>
        public static ProviderCatalog Load(ProvidersOptions options, string contentRoot, ConfigPaths paths)
        {
            var dir = string.IsNullOrWhiteSpace(options.Directory) ? "providers" : options.Directory;
            var fullDir = Path.IsPathRooted(dir) ? dir : Path.Combine(contentRoot, dir);
            var label = dir.TrimEnd('/', '\\').Replace('\\', '/');

            string autoDir, autoLabel, customDir = null;
            if (paths != null)
            {
                autoDir = paths.AutoProvidersDirectory;
                autoLabel = "config/providers/auto";
                customDir = paths.CustomProvidersDirectory;
            }
            else
            {
                autoDir = Path.Combine(fullDir, AutoFolderName);
                autoLabel = $"{label}/{AutoFolderName}";
            }

            var errors = new List<string>();
            var warnings = new List<string>();
            var snapshot = Build(options, label, fullDir, autoDir, autoLabel, customDir, errors, warnings);
            return new ProviderCatalog(options, fullDir, label, autoDir, autoLabel, customDir, snapshot, errors, warnings);
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
            return new ProviderCatalog(new ProvidersOptions(), "", "", null, null, null, snapshot, errors, warnings);
        }

        /// <summary>
        /// Re-reads the provider files. On success the new ranges are used from the
        /// next lookup on; if there are errors the current ranges stay in use.
        /// </summary>
        public bool Reload(out List<string> errors, out List<string> warnings) => Reload(out errors, out warnings, out _);

        /// <summary>As Reload, and says whether the ranges or their sources differ from before.</summary>
        public bool Reload(out List<string> errors, out List<string> warnings, out bool changed)
        {
            errors = new List<string>();
            warnings = new List<string>();
            changed = false;
            if (string.IsNullOrEmpty(DirectoryPath)) return false;

            lock (_reloadLock)
            {
                var snapshot = Build(_options, _bundledLabel, DirectoryPath, _autoDir, _autoLabel, _customDir, errors, warnings);
                if (errors.Count > 0) return false;
                changed = snapshot.Fingerprint != _snapshot.Fingerprint;
                if (changed) _snapshot = snapshot;
                return true;
            }
        }

        private static string FingerprintOf(Dictionary<string, IPNetwork[]> networks, Dictionary<string, string> sources) =>
            string.Join("|", networks.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
                kv.Key + "@" + (sources.TryGetValue(kv.Key, out var src) ? src : "") + ":" +
                string.Join(",", kv.Value.Select(n => n.ToString()).OrderBy(x => x, StringComparer.Ordinal))));

        private static Snapshot Build(ProvidersOptions options, string label, string fullDir, string autoDir, string autoLabel,
            string customDir, List<string> errors, List<string> warnings)
        {
            var ranges = new Dictionary<string, List<IPNetwork>>(StringComparer.OrdinalIgnoreCase);
            var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (Directory.Exists(fullDir))
            {
                // bundled lists
                foreach (var (name, file) in TxtFiles(fullDir))
                {
                    ranges[name] = ParseLines(ReadLines(file, errors), $"{label}/{Path.GetFileName(file)}", errors, warnings);
                    sources[name] = $"{label}/{Path.GetFileName(file)}";
                }
            }

            // downloaded lists replace the bundled list of the same provider
            if (autoDir != null && Directory.Exists(autoDir))
            {
                foreach (var (name, file) in TxtFiles(autoDir))
                {
                    var fileLabel = $"{autoLabel}/{Path.GetFileName(file)}";
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

            // your own lists add to whatever is there (e.g. 3cx, or extra Zoom ranges)
            if (customDir != null && Directory.Exists(customDir))
            {
                foreach (var (name, file) in TxtFiles(customDir))
                {
                    var fileLabel = $"config/providers/{Path.GetFileName(file)}";
                    var parsed = ParseLines(ReadLines(file, errors), fileLabel, errors, warnings);
                    if (ranges.TryGetValue(name, out var list))
                    {
                        list.AddRange(parsed);
                        sources[name] += " + " + fileLabel;
                    }
                    else
                    {
                        ranges[name] = parsed;
                        sources[name] = fileLabel;
                    }
                }
            }

            if (!Directory.Exists(fullDir) && ranges.Count == 0 && (options.Inline == null || options.Inline.Count == 0))
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

            var usedSources = sources.Where(kv => networks.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            return new Snapshot
            {
                Networks = networks,
                Sources = usedSources,
                Fingerprint = FingerprintOf(networks, usedSources),
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
