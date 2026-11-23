// ProviderUpdater.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. Downloads the Teams (Microsoft 365 endpoints web
//           service, version-checked) and Zoom (ZoomMeetings.txt) IP lists into
//           providers/auto, with safety checks: an empty, unparsable or much
//           shorter list is rejected and the current list is kept.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RtcCallMonitor.ProviderUpdates
{
    /// <summary>Outcome of one provider in an update run.</summary>
    public enum UpdateOutcome { Updated, Unchanged, Rejected, Failed }

    public class ProviderUpdateResult
    {
        public string Provider { get; init; }
        public UpdateOutcome Outcome { get; init; }
        public int Count { get; init; }
        public string Message { get; init; }
    }

    /// <summary>Remembered between runs in providers/auto/update-state.json.</summary>
    public class ProviderUpdateState
    {
        /// <summary>GUID required by the Microsoft web service, one per machine.</summary>
        public string ClientRequestId { get; set; }
        public string MicrosoftVersion { get; set; }
        public DateTimeOffset? LastRunUtc { get; set; }
    }

    public class ProviderUpdater
    {
        public const string StateFileName = "update-state.json";

        /// <summary>A new list shorter than this share of the current one is treated as broken.</summary>
        public const double MinimumShareOfCurrent = 0.5;

        private readonly HttpClient _http;
        private readonly string _autoDir;
        private readonly ILogger _logger;
        private readonly Func<DateTimeOffset> _clock;

        public ProviderUpdater(HttpClient http, string providersDir, ILogger logger, Func<DateTimeOffset> clock = null)
        {
            _http = http;
            _autoDir = Path.Combine(providersDir, ProviderCatalog.AutoFolderName);
            _logger = logger;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        /// <summary>Checks every source once. Returns one result per provider.</summary>
        public async Task<List<ProviderUpdateResult>> RunOnceAsync(CancellationToken ct)
        {
            Directory.CreateDirectory(_autoDir);
            var state = LoadState();
            if (string.IsNullOrWhiteSpace(state.ClientRequestId)) state.ClientRequestId = Guid.NewGuid().ToString();

            var results = new List<ProviderUpdateResult>
            {
                await UpdateMicrosoftTeamsAsync(state, ct),
                await UpdateZoomAsync(ct),
            };

            state.LastRunUtc = _clock();
            SaveState(state);
            return results;
        }

        private async Task<ProviderUpdateResult> UpdateMicrosoftTeamsAsync(ProviderUpdateState state, CancellationToken ct)
        {
            const string provider = "msteams";
            try
            {
                var version = ProviderFeeds.ParseMicrosoftVersion(
                    await GetStringAsync(string.Format(ProviderFeeds.MicrosoftVersionUrl, state.ClientRequestId), ct));

                if (version == state.MicrosoftVersion && File.Exists(AutoFile(provider)))
                {
                    return new ProviderUpdateResult { Provider = provider, Outcome = UpdateOutcome.Unchanged, Count = CountRanges(AutoFile(provider)), Message = $"version {version} is current" };
                }

                var ranges = ProviderFeeds.ParseMicrosoftTeams(
                    await GetStringAsync(string.Format(ProviderFeeds.MicrosoftEndpointsUrl, state.ClientRequestId), ct));

                var result = Apply(provider, ranges, ProviderFeeds.MicrosoftDocsUrl + " (Skype service area, Optimize category)", version);
                if (result.Outcome == UpdateOutcome.Updated || result.Outcome == UpdateOutcome.Unchanged)
                {
                    state.MicrosoftVersion = version;
                }
                return result;
            }
            catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
            {
                return Failed(provider, e);
            }
        }

        private async Task<ProviderUpdateResult> UpdateZoomAsync(CancellationToken ct)
        {
            const string provider = "zoom";
            try
            {
                var ranges = ProviderFeeds.ParseZoomText(await GetStringAsync(ProviderFeeds.ZoomMeetingsUrl, ct));
                return Apply(provider, ranges, ProviderFeeds.ZoomMeetingsUrl, null);
            }
            catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
            {
                return Failed(provider, e);
            }
        }

        /// <summary>Validates a downloaded list and writes it if it is sane and different.</summary>
        private ProviderUpdateResult Apply(string provider, List<string> ranges, string source, string version)
        {
            var errors = new List<string>();
            var parsed = ProviderCatalog.ParseLines(ranges, $"{provider} download", errors, new List<string>());

            if (errors.Count > 0)
            {
                return Rejected(provider, parsed.Count, $"download has {errors.Count} invalid line(s), e.g. {errors[0]}");
            }
            if (parsed.Count == 0)
            {
                return Rejected(provider, 0, "download contains no ranges");
            }

            // compare with the previous download from the same source; the bundled
            // list is curated differently, so its size says nothing about the feed
            var currentCount = File.Exists(AutoFile(provider)) ? CountRanges(AutoFile(provider)) : 0;
            if (currentCount > 0 && parsed.Count < currentCount * MinimumShareOfCurrent)
            {
                return Rejected(provider, parsed.Count, $"download has only {parsed.Count} ranges against {currentCount} last time; keeping the current list");
            }

            var normalized = ranges.Select(r => r.Trim()).OrderBy(r => r, StringComparer.Ordinal).ToList();
            if (File.Exists(AutoFile(provider)) && SameRanges(AutoFile(provider), normalized))
            {
                return new ProviderUpdateResult { Provider = provider, Outcome = UpdateOutcome.Unchanged, Count = normalized.Count, Message = "no changes" };
            }

            WriteAtomically(AutoFile(provider), ProviderFeeds.FormatAutoFile(provider, source, version, normalized, _clock()));
            var change = currentCount > 0 ? $" ({currentCount} before)" : " (first download)";
            return new ProviderUpdateResult { Provider = provider, Outcome = UpdateOutcome.Updated, Count = normalized.Count, Message = $"{normalized.Count} ranges{change}" };
        }

        private async Task<string> GetStringAsync(string url, CancellationToken ct)
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"{new Uri(url).Host} returned {(int)response.StatusCode} {response.ReasonPhrase}");
            }
            return await response.Content.ReadAsStringAsync(ct);
        }

        private string AutoFile(string provider) => Path.Combine(_autoDir, provider + ".txt");

        private static int CountRanges(string file) =>
            File.ReadAllLines(file).Count(l => { var t = l.Split('#')[0].Trim(); return t.Length > 0; });

        private static bool SameRanges(string file, List<string> normalized)
        {
            var existing = File.ReadAllLines(file)
                .Select(l => l.Split('#')[0].Trim())
                .Where(l => l.Length > 0)
                .OrderBy(l => l, StringComparer.Ordinal);
            return existing.SequenceEqual(normalized, StringComparer.Ordinal);
        }

        private static void WriteAtomically(string path, string content)
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }

        private ProviderUpdateState LoadState()
        {
            var path = Path.Combine(_autoDir, StateFileName);
            try
            {
                if (File.Exists(path)) return JsonSerializer.Deserialize<ProviderUpdateState>(File.ReadAllText(path)) ?? new ProviderUpdateState();
            }
            catch (Exception e)
            {
                _logger.LogWarning("could not read {File}, starting fresh: {Message}", path, e.Message);
            }
            return new ProviderUpdateState();
        }

        private void SaveState(ProviderUpdateState state)
        {
            WriteAtomically(Path.Combine(_autoDir, StateFileName),
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static ProviderUpdateResult Rejected(string provider, int count, string message) =>
            new ProviderUpdateResult { Provider = provider, Outcome = UpdateOutcome.Rejected, Count = count, Message = message };

        private static ProviderUpdateResult Failed(string provider, Exception e) =>
            new ProviderUpdateResult { Provider = provider, Outcome = UpdateOutcome.Failed, Message = e.Message };
    }
}
