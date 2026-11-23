// Program.cs
// Version: 2.2.0
// Changelog:
//   2.2.0 - Rolling log file (LogFile section) as an extra logging provider.
//   2.1.0 - Registers the provider auto-update and provider file watcher services.
//   2.0.0 - Config layers: appsettings.json, appsettings.{Environment}.json,
//           appsettings.Local.json (git-ignored), RTCMON_ environment variables,
//           command line; old-style settings translated by LegacyConfig.
//           Registers the provider catalog and the webhook/MQTT notifiers.
//           Console log lines are single-line with a timestamp.
//   1.x   - Original host setup.

using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RtcCallMonitor.Configuration;
using RtcCallMonitor.Notifications;
using RtcCallMonitor.ProviderUpdates;

namespace RtcCallMonitor
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args)
        {
            var configWarnings = new ConfigWarnings();

            return Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((hostContext, config) =>
                {
                    config.Sources.Clear();

                    var env = hostContext.HostingEnvironment;
                    config.SetBasePath(env.ContentRootPath)
                          .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                          .AddJsonFile($"appsettings.{env.EnvironmentName}.json", optional: true, reloadOnChange: false)
                          .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
                          .AddEnvironmentVariables("RTCMON_")
                          .AddCommandLine(args);

                    // translate pre-2.1 settings; lowest priority so new-style values always win
                    var legacy = LegacyConfig.Map(config.Build(), configWarnings.Items);
                    if (legacy.Count > 0)
                    {
                        config.Sources.Insert(0, new MemoryConfigurationSource { InitialData = legacy });
                    }
                })
                .ConfigureLogging((hostContext, builder) =>
                {
                    builder.AddSimpleConsole(o =>
                    {
                        o.SingleLine = true;
                        o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
                    });

                    var logDir = LogFile.AddTo(builder, hostContext.Configuration, out var problem);
                    if (problem != null) configWarnings.Items.Add(problem);
                    if (logDir != null) configWarnings.Info.Add($"Writing log files to {logDir}");
                })
                .ConfigureServices((hostContext, services) =>
                {
                    var config = hostContext.Configuration;
                    services.Configure<DetectionOptions>(config.GetSection(DetectionOptions.Section));
                    services.Configure<ProvidersOptions>(config.GetSection(ProvidersOptions.Section));
                    services.Configure<WebhookOptions>(config.GetSection(WebhookOptions.Section));
                    services.Configure<MqttOptions>(config.GetSection(MqttOptions.Section));

                    services.AddSingleton(configWarnings);
                    services.AddSingleton(sp => ProviderCatalog.Load(
                        sp.GetRequiredService<IOptions<ProvidersOptions>>().Value,
                        sp.GetRequiredService<IHostEnvironment>().ContentRootPath));

                    services.AddSingleton<NetworkListener>();
                    services.AddSingleton<TrafficMonitor>();

                    services.AddSingleton<ICallNotifier, WebhookNotifier>();
                    services.AddSingleton<ICallNotifier, MqttNotifier>();

                    services.AddHostedService<Worker>();
                    services.AddHostedService<ProviderFileWatcher>();
                    services.AddHostedService<ProviderUpdateService>();
                })
                .UseWindowsService();
        }
    }
}
