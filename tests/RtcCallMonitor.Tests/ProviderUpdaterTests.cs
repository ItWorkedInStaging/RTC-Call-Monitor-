// ProviderUpdaterTests.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. Auto-update against a fake HTTP server: first
//           download, version-unchanged skip, broken/empty/shrunken downloads
//           rejected, network failure keeps the current list; catalog override
//           and live reload.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using RtcCallMonitor.Configuration;
using RtcCallMonitor.ProviderUpdates;
using Xunit;

namespace RtcCallMonitor.Tests
{
    public class ProviderUpdaterTests : IDisposable
    {
        private readonly string _root;
        private readonly string _providers;
        private readonly FakeHandler _http = new FakeHandler();

        public ProviderUpdaterTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "rtcmon-upd-" + Guid.NewGuid().ToString("N"));
            _providers = Path.Combine(_root, "providers");
            Directory.CreateDirectory(_providers);
            File.WriteAllLines(Path.Combine(_providers, "msteams.txt"), new[] { "# bundled", "52.112.0.0/14" });
            File.WriteAllLines(Path.Combine(_providers, "zoom.txt"), new[] { "# bundled", "3.7.35.0/25" });

            _http.Responses["endpoints.office.com/version"] = (HttpStatusCode.OK, @"{ ""instance"": ""Worldwide"", ""latest"": ""2026110100"" }");
            _http.Responses["endpoints.office.com/endpoints"] = (HttpStatusCode.OK, ProviderFeedsTests.MicrosoftEndpointsSample);
            _http.Responses["assets.zoom.us"] = (HttpStatusCode.OK, string.Join("\n", Enumerable.Range(0, 20).Select(i => $"170.114.{i}.0/24")));
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);

        private ProviderUpdater Updater() => new ProviderUpdater(new HttpClient(_http), _providers, NullLogger.Instance,
            () => new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.Zero));

        private string Auto(string name) => Path.Combine(_providers, "auto", name + ".txt");

        private static UpdateOutcome Outcome(List<ProviderUpdateResult> results, string provider) =>
            results.Single(r => r.Provider == provider).Outcome;

        [Fact]
        public async Task FirstRun_WritesBothLists_AndRemembersState()
        {
            var results = await Updater().RunOnceAsync(CancellationToken.None);

            Assert.Equal(UpdateOutcome.Updated, Outcome(results, "msteams"));
            Assert.Equal(UpdateOutcome.Updated, Outcome(results, "zoom"));
            Assert.Contains("13.107.64.0/18", File.ReadAllText(Auto("msteams")));
            Assert.Contains("# Version: 2026110100", File.ReadAllText(Auto("msteams")));
            Assert.Equal(20, File.ReadAllLines(Auto("zoom")).Count(l => l.StartsWith("170.")));

            var state = File.ReadAllText(Path.Combine(_providers, "auto", ProviderUpdater.StateFileName));
            Assert.Contains("2026110100", state);
            Assert.Contains("ClientRequestId", state);
            // the GUID is sent to Microsoft on every call
            Assert.True(_http.Requests.Where(u => u.Contains("office.com")).All(u => u.Contains("ClientRequestId=")));
        }

        [Fact]
        public async Task SecondRun_SameVersion_SkipsMicrosoftDownload()
        {
            await Updater().RunOnceAsync(CancellationToken.None);
            _http.Requests.Clear();

            var results = await Updater().RunOnceAsync(CancellationToken.None);

            Assert.Equal(UpdateOutcome.Unchanged, Outcome(results, "msteams"));
            Assert.Equal(UpdateOutcome.Unchanged, Outcome(results, "zoom"));
            Assert.DoesNotContain(_http.Requests, u => u.Contains("/endpoints/"));
        }

        [Fact]
        public async Task SameClientRequestId_IsReusedBetweenRuns()
        {
            await Updater().RunOnceAsync(CancellationToken.None);
            var firstId = _http.Requests.First(u => u.Contains("office.com")).Split("ClientRequestId=")[1];
            _http.Requests.Clear();
            await Updater().RunOnceAsync(CancellationToken.None);
            Assert.All(_http.Requests.Where(u => u.Contains("office.com")), u => Assert.EndsWith(firstId, u));
        }

        [Fact]
        public async Task NetworkFailure_KeepsCurrentList()
        {
            await Updater().RunOnceAsync(CancellationToken.None);
            var before = File.ReadAllText(Auto("zoom"));
            _http.Responses["assets.zoom.us"] = (HttpStatusCode.ServiceUnavailable, "down");

            var results = await Updater().RunOnceAsync(CancellationToken.None);

            Assert.Equal(UpdateOutcome.Failed, Outcome(results, "zoom"));
            Assert.Equal(before, File.ReadAllText(Auto("zoom")));
        }

        [Fact]
        public async Task EmptyOrGarbageDownload_IsRejected()
        {
            _http.Responses["assets.zoom.us"] = (HttpStatusCode.OK, "<html>maintenance</html>");
            var results = await Updater().RunOnceAsync(CancellationToken.None);
            Assert.Equal(UpdateOutcome.Rejected, Outcome(results, "zoom"));
            Assert.False(File.Exists(Auto("zoom")));

            _http.Responses["assets.zoom.us"] = (HttpStatusCode.OK, "\n\n");
            results = await Updater().RunOnceAsync(CancellationToken.None);
            Assert.Equal(UpdateOutcome.Rejected, Outcome(results, "zoom"));
        }

        [Fact]
        public async Task MuchShorterDownload_IsRejected()
        {
            await Updater().RunOnceAsync(CancellationToken.None); // 20 ranges
            _http.Responses["assets.zoom.us"] = (HttpStatusCode.OK, "170.114.0.0/24\n170.114.1.0/24\n");

            var results = await Updater().RunOnceAsync(CancellationToken.None);

            Assert.Equal(UpdateOutcome.Rejected, Outcome(results, "zoom"));
            Assert.Contains("only 2 ranges against 20", results.Single(r => r.Provider == "zoom").Message);
        }

        [Fact]
        public async Task Catalog_UsesDownloadedListsInsteadOfBundled_AfterReload()
        {
            var catalog = ProviderCatalog.Load(new ProvidersOptions { Directory = "providers" }, _root);
            Assert.Null(catalog.Lookup("170.114.5.9"));
            Assert.Equal("providers/zoom.txt", catalog.Sources["zoom"]);

            await Updater().RunOnceAsync(CancellationToken.None);
            Assert.True(catalog.Reload(out var errors, out _));

            Assert.Empty(errors);
            Assert.Equal("zoom", catalog.Lookup("170.114.5.9"));
            Assert.Null(catalog.Lookup("3.7.35.1")); // bundled-only range is replaced, not merged
            Assert.Equal("providers/auto/zoom.txt", catalog.Sources["zoom"]);
        }

        [Fact]
        public void Reload_WithBrokenFile_KeepsPreviousRanges()
        {
            var catalog = ProviderCatalog.Load(new ProvidersOptions { Directory = "providers" }, _root);
            File.AppendAllText(Path.Combine(_providers, "zoom.txt"), "3.7.35/25\n");

            Assert.False(catalog.Reload(out var errors, out _));
            Assert.Contains("zoom.txt line 3", Assert.Single(errors));
            Assert.Equal("zoom", catalog.Lookup("3.7.35.1"));
        }

        [Fact]
        public void Reload_PicksUpHandEditedFile()
        {
            var catalog = ProviderCatalog.Load(new ProvidersOptions { Directory = "providers" }, _root);
            File.WriteAllLines(Path.Combine(_providers, "3cx.txt"), new[] { "203.0.113.10/32" });

            Assert.True(catalog.Reload(out _, out _));
            Assert.Equal("3cx", catalog.Lookup("203.0.113.10"));
        }

        private class FakeHandler : HttpMessageHandler
        {
            public Dictionary<string, (HttpStatusCode Status, string Body)> Responses { get; } = new Dictionary<string, (HttpStatusCode, string)>();
            public List<string> Requests { get; } = new List<string>();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = request.RequestUri.ToString();
                lock (Requests) Requests.Add(url);
                var match = Responses.FirstOrDefault(r => url.Contains(r.Key));
                if (match.Key == null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                return Task.FromResult(new HttpResponseMessage(match.Value.Status)
                {
                    Content = new StringContent(match.Value.Body, Encoding.UTF8)
                });
            }
        }
    }
}
