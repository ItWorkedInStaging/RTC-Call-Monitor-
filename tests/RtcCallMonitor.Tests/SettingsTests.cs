// SettingsTests.cs
// Version: 1.0.1
// Changelog:
//   1.0.1 - Fixes for Windows: the test folders are not locked (that shut the
//           tests out of their own folder), and a kept password may come back
//           encrypted (DPAPI), so it is compared decrypted.
//   1.0.0 - Initial version. Config folder layout, provider lists from the
//           config folder, SettingsStore read/save (secrets kept and never
//           sent back, other keys kept, nothing written on errors), the
//           editor check and the settings commands on the pipe.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RtcCallMonitor.Configuration;
using RtcCallMonitor.Ipc;
using RtcCallMonitor.Notifications;
using RtcCallMonitor.Settings;
using Xunit;

namespace RtcCallMonitor.Tests
{
    public class SettingsTests : IDisposable
    {
        private readonly string _root;
        private readonly string _app;
        private readonly ConfigPaths _paths;

        public SettingsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "rtcmon-settings-" + Guid.NewGuid().ToString("N"));
            _app = Path.Combine(_root, "app");
            Directory.CreateDirectory(Path.Combine(_app, "providers"));
            File.WriteAllText(Path.Combine(_app, "appsettings.json"), @"{
              ""Detection"": { ""CheckIntervalMs"": 1000, ""MinPacketRate"": 1, ""CallStartIntervals"": 2, ""CallEndIntervals"": 3, ""IgnoreUdpPorts"": [ 443 ] },
              ""Mqtt"": { ""Enabled"": false, ""Port"": 1883, ""BaseTopic"": ""rtc-call-monitor"", ""DiscoveryPrefix"": ""homeassistant"" },
              ""Providers"": { ""Directory"": ""providers"", ""AutoUpdate"": { ""Enabled"": true, ""IntervalHours"": 24 } }
            }");
            File.WriteAllLines(Path.Combine(_app, "providers", "zoom.txt"), new[] { "3.7.35.0/25" });
            File.WriteAllLines(Path.Combine(_app, "providers", "msteams.txt"), new[] { "52.112.0.0/14" });

            _paths = new ConfigPaths(Path.Combine(_root, "config"));
            _paths.EnsureCreatedAndLocked(lockFolder: false);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private ProviderCatalog Catalog() => ProviderCatalog.Load(new ProvidersOptions { Directory = "providers" }, _app, _paths);
        private SettingsStore Store() => new SettingsStore(_app, "Test", _paths, Catalog());

        private JsonObject SavedFile() => (JsonObject)JsonNode.Parse(File.ReadAllText(_paths.SettingsFile));

        // ---------------------------------------------------------------- layout

        [Fact]
        public void ConfigFolder_HasProvidersAndAutoSubfolders()
        {
            Assert.True(Directory.Exists(_paths.CustomProvidersDirectory));
            Assert.True(Directory.Exists(_paths.AutoProvidersDirectory));
            Assert.EndsWith("appsettings.Local.json", _paths.SettingsFile);
            Assert.Equal(Path.Combine(_paths.ConfigDirectory, "providers", "auto"), _paths.AutoProvidersDirectory);
        }

        [Fact]
        public void Catalog_DownloadsReplaceBundled_OwnListsAdd()
        {
            File.WriteAllLines(Path.Combine(_paths.AutoProvidersDirectory, "zoom.txt"), new[] { "170.114.0.0/16", "3.7.35.0/25" });
            File.WriteAllLines(Path.Combine(_paths.CustomProvidersDirectory, "3cx.txt"), new[] { "# office PBX", "203.0.113.10" });
            File.WriteAllLines(Path.Combine(_paths.CustomProvidersDirectory, "msteams.txt"), new[] { "198.51.100.0/24" });

            var catalog = Catalog();
            Assert.Empty(catalog.Errors);
            Assert.Equal(2, catalog.Networks["zoom"].Length);
            Assert.Equal("config/providers/auto/zoom.txt", catalog.Sources["zoom"]);
            Assert.Equal("3cx", catalog.Lookup("203.0.113.10"));
            Assert.Equal("msteams", catalog.Lookup("198.51.100.7"));
            Assert.Equal("msteams", catalog.Lookup("52.112.1.1"));
            Assert.Contains("config/providers/msteams.txt", catalog.Sources["msteams"]);
        }

        [Fact]
        public void Catalog_ReloadPicksUpANewOwnList()
        {
            var catalog = Catalog();
            Assert.Null(catalog.Lookup("203.0.113.10"));
            File.WriteAllLines(Path.Combine(_paths.CustomProvidersDirectory, "3cx.txt"), new[] { "203.0.113.10" });
            Assert.True(catalog.Reload(out _, out _, out var changed));
            Assert.True(changed);
            Assert.Equal("3cx", catalog.Lookup("203.0.113.10"));
        }

        // ---------------------------------------------------------------- read

        [Fact]
        public void Read_ShowsEffectiveSettings_AndLoadedProviders()
        {
            var doc = Store().Read();
            Assert.Equal(_paths.ConfigDirectory, doc.ConfigDirectory);
            Assert.Equal(1883, doc.Mqtt.Port);
            Assert.False(doc.Mqtt.PasswordSet);
            Assert.Equal(new[] { 443 }, doc.Detection.IgnoreUdpPorts);
            Assert.True(doc.Detection.DefaultExcludeInterfaces.Length > 0);
            Assert.Null(doc.Detection.ExcludeInterfaces);
            Assert.Equal(new[] { "msteams", "zoom" }, doc.Providers.Loaded.Select(p => p.Name).ToArray());
            Assert.Equal("providers/zoom.txt", doc.Providers.Loaded.Single(p => p.Name == "zoom").Source);
        }

        [Fact]
        public void Read_NeverSendsSecrets()
        {
            File.WriteAllText(_paths.SettingsFile, @"{ ""Mqtt"": { ""Password"": ""hunter2"" },
                ""Webhook"": { ""Headers"": { ""Authorization"": ""Bearer abc"", ""X-Room"": ""office"" } } }");
            var doc = Store().Read();
            Assert.True(doc.Mqtt.PasswordSet);
            Assert.Null(doc.Mqtt.NewPassword);
            var json = JsonSerializer.Serialize(doc, PipeContract.Json);
            Assert.False(json.Contains("hunter2"));
            Assert.False(json.Contains("Bearer abc"));
            var auth = doc.Webhook.Headers.Single(h => h.Name == "Authorization");
            Assert.True(auth.IsSecret);
            Assert.Equal("", auth.Value);
            Assert.Equal("office", doc.Webhook.Headers.Single(h => h.Name == "X-Room").Value);
        }

        // ---------------------------------------------------------------- save

        [Fact]
        public void Save_RoundTrips_AndKeepsOtherKeys()
        {
            File.WriteAllText(_paths.SettingsFile, @"{ ""LogFile"": { ""RetainedDays"": 30 }, ""Mqtt"": { ""Password"": ""hunter2"" } }");
            var store = Store();
            var doc = store.Read();
            doc.Mqtt.Enabled = true;
            doc.Mqtt.Host = " 10.69.69.17 ";
            doc.Mqtt.Username = "rtcmon";
            doc.Detection.LocalNetworks = new[] { "10.69.0.0/16", "" };
            doc.Detection.IgnoreUdpPorts = new[] { 443, 443, 5353 };
            doc.Providers.AutoUpdateIntervalHours = 12;

            Assert.Empty(store.Save(doc));

            var file = SavedFile();
            Assert.Equal(30, (int)file["LogFile"]["RetainedDays"]);
            Assert.Equal("10.69.69.17", (string)file["Mqtt"]["Host"]);
            // NewPassword null: kept; on Windows a plain password is encrypted on save
            Assert.Equal("hunter2", SecretProtector.Unprotect((string)file["Mqtt"]["Password"]));
            if (OperatingSystem.IsWindows()) Assert.True(SecretProtector.IsProtected((string)file["Mqtt"]["Password"]));
            Assert.Equal(12, (int)file["Providers"]["AutoUpdate"]["IntervalHours"]);

            var again = Store().Read();
            Assert.True(again.Mqtt.Enabled);
            Assert.True(again.Mqtt.PasswordSet);
            Assert.Equal(new[] { "10.69.0.0/16" }, again.Detection.LocalNetworks);
            Assert.Equal(new[] { 443, 5353 }, again.Detection.IgnoreUdpPorts);
            Assert.True(File.Exists(_paths.SettingsFile + ".bak"));
        }

        [Fact]
        public void Save_NewPassword_ReplacesOrClears()
        {
            var store = Store();
            var doc = store.Read();
            doc.Mqtt.Username = "rtcmon";
            doc.Mqtt.NewPassword = "s3cret";
            Assert.Empty(store.Save(doc));
            Assert.Equal("s3cret", store.ResolveMqtt(new MqttSettings { Host = "h" }).Password);

            doc = store.Read();
            doc.Mqtt.NewPassword = "";
            Assert.Empty(store.Save(doc));
            Assert.False(Store().Read().Mqtt.PasswordSet);
        }

        [Fact]
        public void Save_SecretHeaderWithNullValue_KeepsStoredValue()
        {
            File.WriteAllText(_paths.SettingsFile, @"{ ""Webhook"": { ""Headers"": { ""Authorization"": ""Bearer abc"" } } }");
            var store = Store();
            var doc = store.Read();
            doc.Webhook.Headers.Single(h => h.Name == "Authorization").Value = null; // the window sends null = unchanged
            doc.Webhook.Headers.Add(new WebhookHeader { Name = "X-Room", Value = "office" });
            Assert.Empty(store.Save(doc));

            var sent = store.ResolveHeaders(Store().Read().Webhook.Headers.Select(h => new WebhookHeader { Name = h.Name, Value = h.IsSecret ? null : h.Value }));
            Assert.Equal("Bearer abc", sent["Authorization"]);
            Assert.Equal("office", sent["X-Room"]);
        }

        [Fact]
        public void Save_WithErrors_WritesNothing()
        {
            var store = Store();
            var doc = store.Read();
            doc.Mqtt.Enabled = true;
            doc.Mqtt.Host = "";
            doc.Detection.LocalNetworks = new[] { "10.0.0/8" };
            doc.Providers.Custom.Add(new CustomProvider { Name = "bad name", Ranges = { "1.2.3.4" } });
            doc.Providers.Custom.Add(new CustomProvider { Name = "3cx", Ranges = { "not-an-ip" } });

            var errors = store.Save(doc);
            Assert.Contains(errors, e => e.Contains("Mqtt.Host"));
            Assert.Contains(errors, e => e.Contains("LocalNetworks"));
            Assert.Contains(errors, e => e.Contains("bad name"));
            Assert.Contains(errors, e => e.Contains("not-an-ip"));
            Assert.False(File.Exists(_paths.SettingsFile));
            Assert.Empty(Directory.GetFiles(_paths.CustomProvidersDirectory, "*.txt"));
        }

        [Fact]
        public void Save_WritesAndRemovesOwnProviderLists_NotDownloads()
        {
            File.WriteAllLines(Path.Combine(_paths.AutoProvidersDirectory, "zoom.txt"), new[] { "170.114.0.0/16" });
            File.WriteAllLines(Path.Combine(_paths.CustomProvidersDirectory, "old.txt"), new[] { "192.0.2.1" });
            var store = Store();
            var doc = store.Read();
            Assert.Single(doc.Providers.Custom);

            doc.Providers.Custom.Clear();
            doc.Providers.Custom.Add(new CustomProvider { Name = "3cx", Ranges = { "203.0.113.10", " # comment", "203.0.113.0/28" } });
            Assert.Empty(store.Save(doc));

            Assert.False(File.Exists(Path.Combine(_paths.CustomProvidersDirectory, "old.txt")));
            Assert.True(File.Exists(Path.Combine(_paths.AutoProvidersDirectory, "zoom.txt")));
            var custom = Store().Read().Providers.Custom.Single();
            Assert.Equal("3cx", custom.Name);
            Assert.Equal(new List<string> { "203.0.113.10", "203.0.113.0/28" }, custom.Ranges);
        }

        // ---------------------------------------------------------------- editors + pipe

        [Fact]
        public void EditorPolicy_AllowsListedSelfAndElevated_RefusesOthers()
        {
            File.WriteAllText(_paths.EditorsFile, @"{ ""editors"": [ { ""sid"": ""S-1-5-21-1-2-3-1001"", ""name"": ""PC\\Joshua"" } ] }");
            var policy = new EditorPolicy(_paths);
            Assert.Null(policy.Check(new Caller { Sid = "S-1-5-21-1-2-3-1001", Name = "PC\\Joshua" }));
            Assert.Null(policy.Check(new Caller { Sid = "S-1-5-21-1-2-3-1002", IsElevatedAdmin = true }));
            Assert.Null(policy.Check(new Caller { Sid = "S-1-5-18", IsSelf = true }));
            Assert.Contains("not allowed", policy.Check(new Caller { Sid = "S-1-5-21-1-2-3-1002", Name = "PC\\Guest" }));
            Assert.Contains("Could not tell", policy.Check(null));
        }

        private StatusPipeServer Server(FakeLifetime lifetime, RestartFlag flag)
        {
            var reporter = new CallReporter(new MonitorState(), Array.Empty<ICallNotifier>(), NullLogger<CallReporter>.Instance);
            var listener = new NetworkListener(NullLogger<NetworkListener>.Instance, Options.Create(new DetectionOptions()));
            var restart = new RestartCoordinator(lifetime, flag, NullLogger<RestartCoordinator>.Instance);
            return new StatusPipeServer(reporter, listener, new ConfigWarnings(), new Env(_app), NullLogger<StatusPipeServer>.Instance,
                "rtcmon-settings-test", Store(), new EditorPolicy(_paths), restart);
        }

        [Fact]
        public async Task Pipe_RefusesSettingsToOtherAccounts()
        {
            var server = Server(new FakeLifetime(), new RestartFlag());
            var stranger = new Caller { Sid = "S-1-5-21-9", Name = "PC\\Guest" };
            var answer = JsonSerializer.Deserialize<SettingsResult>(
                await server.HandleAsync(@"{""command"":""getSettings""}", stranger), PipeContract.Json);
            Assert.False(answer.Ok);
            Assert.Null(answer.Settings);
            Assert.Contains("not allowed", answer.Message);

            // status still works for everyone
            var status = JsonSerializer.Deserialize<MonitorStatus>(await server.HandleAsync(@"{""command"":""status""}", stranger), PipeContract.Json);
            Assert.Equal("idle", status.State);
            Assert.Equal(_paths.ConfigDirectory, status.ConfigDirectory);
        }

        [Fact]
        public async Task Pipe_SaveSettings_SavesAndRestarts()
        {
            var lifetime = new FakeLifetime();
            var flag = new RestartFlag();
            var server = Server(lifetime, flag);
            var me = new Caller { Sid = "S-1-5-21-1", Name = "PC\\Joshua", IsSelf = true };

            var got = JsonSerializer.Deserialize<SettingsResult>(await server.HandleAsync(@"{""command"":""getSettings""}", me), PipeContract.Json);
            Assert.True(got.Ok);
            got.Settings.Webhook.Enabled = true;
            got.Settings.Webhook.CallStartUrl = "http://ha.local:8123/api/webhook/start";

            var request = JsonSerializer.Serialize(new PipeRequest { Command = "saveSettings", Settings = got.Settings }, PipeContract.Json);
            var saved = JsonSerializer.Deserialize<SettingsResult>(await server.HandleAsync(request, me), PipeContract.Json);
            Assert.True(saved.Ok);
            Assert.Contains("restarting", saved.Message);
            Assert.Equal("http://ha.local:8123/api/webhook/start", saved.Settings.Webhook.CallStartUrl);

            for (int i = 0; i < 50 && !lifetime.Stopped; i++) await Task.Delay(100);
            Assert.True(lifetime.Stopped);
            Assert.True(flag.Requested);
        }

        [Fact]
        public async Task Pipe_SaveWithErrors_ReturnsThem_NoRestart()
        {
            var lifetime = new FakeLifetime();
            var server = Server(lifetime, new RestartFlag());
            var me = new Caller { IsSelf = true };
            var doc = Store().Read();
            doc.Webhook.Enabled = true; // no URLs
            var request = JsonSerializer.Serialize(new PipeRequest { Command = "saveSettings", Settings = doc }, PipeContract.Json);
            var saved = JsonSerializer.Deserialize<SettingsResult>(await server.HandleAsync(request, me), PipeContract.Json);
            Assert.False(saved.Ok);
            Assert.Contains(saved.Errors, e => e.Contains("Webhook"));
            await Task.Delay(1500);
            Assert.False(lifetime.Stopped);
        }

        [Fact]
        public async Task Pipe_TestWebhook_ReportsBadUrl()
        {
            var server = Server(new FakeLifetime(), new RestartFlag());
            var request = JsonSerializer.Serialize(new PipeRequest { Command = "testWebhook", Webhook = new WebhookSettings { CallStartUrl = "ha.local/hook" } }, PipeContract.Json);
            var answer = JsonSerializer.Deserialize<SettingsResult>(await server.HandleAsync(request, new Caller { IsSelf = true }), PipeContract.Json);
            Assert.False(answer.Ok);
            Assert.Contains("http", answer.Message);
        }

        [Fact]
        public async Task Pipe_TestMqtt_ReportsUnreachableBroker()
        {
            var server = Server(new FakeLifetime(), new RestartFlag());
            var request = JsonSerializer.Serialize(new PipeRequest { Command = "testMqtt", Mqtt = new MqttSettings { Host = "127.0.0.1", Port = 1 } }, PipeContract.Json);
            var answer = JsonSerializer.Deserialize<SettingsResult>(await server.HandleAsync(request, new Caller { IsSelf = true }), PipeContract.Json);
            Assert.False(answer.Ok);
            Assert.Contains("127.0.0.1:1", answer.Message);
        }

        [Fact]
        public async Task Pipe_RealRoundTrip_LargeSave()
        {
            var name = "rtcmon-settings-rt-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var reporter = new CallReporter(new MonitorState(), Array.Empty<ICallNotifier>(), NullLogger<CallReporter>.Instance);
            var listener = new NetworkListener(NullLogger<NetworkListener>.Instance, Options.Create(new DetectionOptions()));
            var server = new StatusPipeServer(reporter, listener, new ConfigWarnings(), new Env(_app), NullLogger<StatusPipeServer>.Instance,
                name, Store(), new EditorPolicy(_paths), null);
            using var cts = new CancellationTokenSource();
            await server.StartAsync(cts.Token);
            try
            {
                var doc = Store().Read();
                doc.Providers.Custom.Add(new CustomProvider { Name = "big", Ranges = Enumerable.Range(0, 4000).Select(i => $"10.{i / 250}.{i % 250}.0/24").ToList() });
                var result = await PipeClient.SendAsync<SettingsResult>(new PipeRequest { Command = "saveSettings", Settings = doc },
                    TimeSpan.FromSeconds(10), default, name);
                Assert.True(result.Ok);
                Assert.Equal(4000, Store().Read().Providers.Custom.Single().Ranges.Count);
            }
            finally
            {
                cts.Cancel();
                await server.StopAsync(CancellationToken.None);
            }
        }

        private class Env : IHostEnvironment
        {
            public Env(string root) { ContentRootPath = root; }
            public string EnvironmentName { get; set; } = "Test";
            public string ApplicationName { get; set; } = "RtcCallMonitor";
            public string ContentRootPath { get; set; }
            public IFileProvider ContentRootFileProvider { get; set; }
        }

        private class FakeLifetime : IHostApplicationLifetime
        {
            public volatile bool Stopped;
            public CancellationToken ApplicationStarted => CancellationToken.None;
            public CancellationToken ApplicationStopping => CancellationToken.None;
            public CancellationToken ApplicationStopped => CancellationToken.None;
            public void StopApplication() => Stopped = true;
        }
    }
}
