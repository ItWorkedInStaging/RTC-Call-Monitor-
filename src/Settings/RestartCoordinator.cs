// RestartCoordinator.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. Restarts the monitor after settings are saved.
//           As a Windows service it starts a small detached helper that runs
//           "net stop" + "net start" for the service (a service cannot rebuild
//           itself in-process); otherwise Program.Main rebuilds the host.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

namespace RtcCallMonitor.Settings
{
    /// <summary>Set by a restart request; Program.Main checks it after the host stops.</summary>
    public class RestartFlag
    {
        public volatile bool Requested;
    }

    public class RestartCoordinator
    {
        public const string ServiceName = "RTC Call Monitor";

        private readonly IHostApplicationLifetime _lifetime;
        private readonly RestartFlag _flag;
        private readonly ILogger<RestartCoordinator> _logger;
        private int _pending;

        public RestartCoordinator(IHostApplicationLifetime lifetime, RestartFlag flag, ILogger<RestartCoordinator> logger)
        {
            _lifetime = lifetime;
            _flag = flag;
            _logger = logger;
        }

        /// <summary>Restarts shortly (after the pipe answer has gone out). Returns false if one is already pending.</summary>
        public bool RequestRestart(string reason)
        {
            if (Interlocked.Exchange(ref _pending, 1) == 1) return false;
            _logger.LogInformation("restarting the monitor: {Reason}", reason);

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService())
                {
                    try
                    {
                        // detached: keeps running while this process is stopped, then starts the service again
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\cmd.exe"),
                            Arguments = $"/c net stop \"{ServiceName}\" & net start \"{ServiceName}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        });
                        return;
                    }
                    catch (Exception e)
                    {
                        _logger.LogWarning("could not restart the service ({Message}); restart it from the tray menu", e.Message);
                        Interlocked.Exchange(ref _pending, 0);
                        return;
                    }
                }
                _flag.Requested = true;
                _lifetime.StopApplication();
            });
            return true;
        }
    }
}
