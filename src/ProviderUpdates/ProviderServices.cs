// ProviderServices.cs
// Version: 1.0.1
// Changelog:
//   1.0.1 - "provider ranges reloaded" is logged only when the ranges changed
//           (the update and the file watcher no longer both log the same reload).
//   1.0.0 - Initial version. Background services for providers: a scheduled
//           auto-update (startup + every Providers:AutoUpdate:IntervalHours) and
//           a file watcher that reloads the ranges when any providers/*.txt
//           file changes, without restarting.

using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RtcCallMonitor.Configuration;

namespace RtcCallMonitor.ProviderUpdates
{
    /// <summary>Downloads published provider lists at startup and then on a schedule.</summary>
    public class ProviderUpdateService : BackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);

        private readonly ProviderCatalog _catalog;
        private readonly ProviderAutoUpdateOptions _options;
        private readonly ILogger<ProviderUpdateService> _logger;

        public ProviderUpdateService(ProviderCatalog catalog, IOptions<ProvidersOptions> options, ILogger<ProviderUpdateService> logger)
        {
            _catalog = catalog;
            _options = options.Value.AutoUpdate ?? new ProviderAutoUpdateOptions();
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.Enabled)
            {
                _logger.LogInformation("Provider auto-update is off (Providers:AutoUpdate:Enabled)");
                return;
            }
            if (!Directory.Exists(_catalog.DirectoryPath))
            {
                return; // nothing to update into; reported by the startup check
            }

            var interval = TimeSpan.FromHours(Math.Max(1, _options.IntervalHours));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"RTC-Call-Monitor/{Version()}");
            var updater = new ProviderUpdater(http, _catalog.DirectoryPath, _logger);

            try { await Task.Delay(StartupDelay, stoppingToken); }
            catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var results = await updater.RunOnceAsync(stoppingToken);
                    foreach (var r in results)
                    {
                        switch (r.Outcome)
                        {
                            case UpdateOutcome.Updated:
                                _logger.LogInformation("provider list {Provider} updated: {Message}", r.Provider, r.Message); break;
                            case UpdateOutcome.Unchanged:
                                _logger.LogInformation("provider list {Provider} is up to date ({Message})", r.Provider, r.Message); break;
                            case UpdateOutcome.Rejected:
                                _logger.LogWarning("provider list {Provider} not updated: {Message}", r.Provider, r.Message); break;
                            case UpdateOutcome.Failed:
                                _logger.LogWarning("provider list {Provider} could not be downloaded: {Message}; keeping the current list", r.Provider, r.Message); break;
                        }
                    }

                    if (results.Any(r => r.Outcome == UpdateOutcome.Updated))
                    {
                        ProviderFileWatcher.ReloadAndLog(_catalog, _logger, "after update");
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    _logger.LogWarning("provider auto-update failed: {Message}", e.Message);
                }

                _logger.LogDebug("next provider list check in {Hours} h", interval.TotalHours);
                try { await Task.Delay(interval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        private static string Version()
        {
            var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            var plus = info.IndexOf('+');
            return plus > 0 ? info.Substring(0, plus) : info;
        }
    }

    /// <summary>Reloads the provider ranges when a providers/*.txt file is added, changed or removed.</summary>
    public class ProviderFileWatcher : IHostedService, IDisposable
    {
        private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

        private readonly ProviderCatalog _catalog;
        private readonly ILogger<ProviderFileWatcher> _logger;
        private FileSystemWatcher _watcher;
        private Timer _timer;

        public ProviderFileWatcher(ProviderCatalog catalog, ILogger<ProviderFileWatcher> logger)
        {
            _catalog = catalog;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (!Directory.Exists(_catalog.DirectoryPath)) return Task.CompletedTask;

            _timer = new Timer(_ => ReloadAndLog(_catalog, _logger, "file change"), null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(_catalog.DirectoryPath, "*.txt")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            FileSystemEventHandler changed = (_, __) => _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
            _watcher.Changed += changed;
            _watcher.Created += changed;
            _watcher.Deleted += changed;
            _watcher.Renamed += (_, __) => _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
            _watcher.EnableRaisingEvents = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            if (_watcher != null) _watcher.EnableRaisingEvents = false;
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            return Task.CompletedTask;
        }

        internal static void ReloadAndLog(ProviderCatalog catalog, ILogger logger, string reason)
        {
            try
            {
                if (catalog.Reload(out var errors, out var warnings, out var changed))
                {
                    if (!changed)
                    {
                        logger.LogDebug("provider files re-read ({Reason}): no changes", reason);
                        return;
                    }
                    foreach (var w in warnings) logger.LogWarning(w);
                    var summary = string.Join(", ", catalog.Networks.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value.Length}"));
                    logger.LogInformation("provider ranges reloaded ({Reason}): {Summary}", reason, summary);
                }
                else
                {
                    logger.LogWarning("provider files have {Count} problem(s); still using the previous ranges:", errors.Count);
                    foreach (var e in errors) logger.LogWarning("  - {Error}", e);
                }
            }
            catch (Exception e)
            {
                logger.LogWarning("reloading provider ranges failed: {Message}", e.Message);
            }
        }

        public void Dispose()
        {
            _watcher?.Dispose();
            _timer?.Dispose();
        }
    }
}
