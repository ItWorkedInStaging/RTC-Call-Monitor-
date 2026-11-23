// TrayIpcTests.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. Pause/resume rules (MonitorState), the pipe
//           request handling, and a real named-pipe round trip.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RtcCallMonitor.Configuration;
using RtcCallMonitor.Ipc;
using RtcCallMonitor.Notifications;
using Xunit;

namespace RtcCallMonitor.Tests
{
    public class TrayIpcTests
    {
        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 11, 23, 4, 0, 0, TimeSpan.Zero);

        [Fact]
        public void PausingDuringACall_EndsIt_AndResumingReportsItAgain()
        {
            var state = new MonitorState(() => T0);
            Assert.Equal(MonitorState.Report.CallStarted, state.CallDetected("msteams", "52.112.1.2", T0));
            Assert.Equal(MonitorState.Report.CallEnded, state.Pause(null));
            Assert.True(state.Paused);
            Assert.True(state.OnCall);
            Assert.Equal(MonitorState.Report.CallStarted, state.Resume());
            Assert.Equal(MonitorState.Report.CallEnded, state.CallFinished());
        }

        [Fact]
        public void CallsWhilePaused_AreNotReported_EvenWhenTheyEnd()
        {
            var state = new MonitorState(() => T0);
            state.Pause(null);
            Assert.Equal(MonitorState.Report.None, state.CallDetected("zoom", "170.114.0.9", T0));
            Assert.Equal(MonitorState.Report.None, state.CallFinished());
            Assert.Equal(MonitorState.Report.None, state.Resume());
        }

        [Fact]
        public void TimedPause_Expires()
        {
            var now = T0;
            var state = new MonitorState(() => now);
            state.Pause(TimeSpan.FromMinutes(15));
            Assert.Equal(T0.AddMinutes(15), state.PausedUntil);
            now = T0.AddMinutes(14);
            Assert.False(state.PauseExpired());
            now = T0.AddMinutes(15);
            Assert.True(state.PauseExpired());
        }

        [Fact]
        public void ResumeWithoutPause_DoesNothing()
        {
            var state = new MonitorState(() => T0);
            state.CallDetected("msteams", "52.112.1.2", T0);
            Assert.Equal(MonitorState.Report.None, state.Resume());
        }

        private static StatusPipeServer Server(out CallReporter reporter, string pipeName = "rtcmon-test")
        {
            reporter = new CallReporter(new MonitorState(), Array.Empty<ICallNotifier>(), NullLogger<CallReporter>.Instance);
            var listener = new NetworkListener(NullLogger<NetworkListener>.Instance, Options.Create(new DetectionOptions()));
            var info = new ConfigWarnings { LogDirectory = "/var/log/rtc" };
            return new StatusPipeServer(reporter, listener, info, new FakeEnv(), NullLogger<StatusPipeServer>.Instance, pipeName);
        }

        [Fact]
        public void Handle_Status_Pause_Resume()
        {
            var server = Server(out var reporter);

            var status = server.Handle(@"{""command"":""status""}");
            Assert.Equal("idle", status.State);
            Assert.Equal("/var/log/rtc", status.LogDirectory);
            Assert.Equal("/app", status.ConfigDirectory);
            Assert.Contains("No output enabled (MQTT or webhook)", status.Problems);

            reporter.CallDetected("msteams", "52.112.1.2", T0);
            Assert.Equal("onCall", server.Handle(@"{""command"":""status""}").State);
            Assert.Equal("msteams", server.Handle("{}").Provider);

            var paused = server.Handle(@"{""command"":""pause"",""minutes"":60}");
            Assert.Equal("paused", paused.State);
            Assert.True(paused.PausedUntilUtc.HasValue);

            Assert.Equal("onCall", server.Handle(@"{""command"":""resume""}").State);
        }

        [Fact]
        public void Handle_RejectsBadRequests_WithoutThrowing()
        {
            var server = Server(out _);
            Assert.Equal("request is not valid JSON", server.Handle("not json").Error);
            Assert.Contains("unknown command", server.Handle(@"{""command"":""format c:""}").Error);
            Assert.Contains("between 1 and 10080", server.Handle(@"{""command"":""pause"",""minutes"":0}").Error);
            Assert.Equal("idle", server.Handle(@"{""command"":""pause"",""minutes"":0}").State);
        }

        [Fact]
        public async Task RealPipe_RoundTrip()
        {
            var name = "rtcmon-test-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var server = Server(out _, name);
            using var cts = new CancellationTokenSource();
            await server.StartAsync(cts.Token);
            try
            {
                var status = await SendTo(name, new PipeRequest { Command = "pause", Minutes = 15 });
                Assert.Equal("paused", status.State);
                status = await SendTo(name, new PipeRequest { Command = "status" });
                Assert.True(status.Paused);
                status = await SendTo(name, new PipeRequest { Command = "resume" });
                Assert.Equal("idle", status.State);
            }
            finally
            {
                cts.Cancel();
                await server.StopAsync(CancellationToken.None);
            }
        }

        // same as PipeClient.SendAsync, with a test pipe name
        private static async Task<MonitorStatus> SendTo(string name, PipeRequest request)
        {
            using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            var writer = new System.IO.StreamWriter(pipe) { AutoFlush = true };
            var reader = new System.IO.StreamReader(pipe);
            await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(request, PipeContract.Json));
            return System.Text.Json.JsonSerializer.Deserialize<MonitorStatus>(await reader.ReadLineAsync(), PipeContract.Json);
        }

        private class FakeEnv : IHostEnvironment
        {
            public string EnvironmentName { get; set; } = "Test";
            public string ApplicationName { get; set; } = "RtcCallMonitor";
            public string ContentRootPath { get; set; } = "/app";
            public IFileProvider ContentRootFileProvider { get; set; }
        }
    }
}
