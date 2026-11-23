// Program.cs
// Version: 3.0.0
// Changelog:
//   3.0.0 - Editable settings and your own provider lists move to the config
//           folder (%ProgramData%\RtcCallMonitor\config, locked to SYSTEM and
//           Administrators). Encrypted secrets are decrypted on load and plain
//           ones encrypted. Settings window support (SettingsStore,
//           EditorPolicy, RestartCoordinator); when not a service, Main
//           rebuilds the host after a restart request.
//   2.3.0 - Registers MonitorState, CallReporter and the tray status pipe.
//   2.2.0 - Rolling log file (LogFile section) as an extra logging provider.
//   2.1.0 - Registers the provider auto-update and provider file watcher services.
//   2.0.0 - Config layers: appsettings.json, appsettings.{Environment}.json,
//           appsettings.Local.json (git-ignored), RTCMON_ environment variables,
//           command line; old-style settings translated by LegacyConfig.
//           Registers the provider catalog and the webhook/MQTT notifiers.
//           Console log lines are single-line with a timestamp.
//   1.x   - Original host setup.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RtcCallMonitor.Configuration;
using RtcCallMonitor.Ipc;
using RtcCallMonitor.Notifications;
using RtcCallMonitor.ProviderUpdates;
using RtcCallMonitor.Settings;

namespace RtcCallMonitor
{
    public class Program
    {
        public static void Main(string[] args)
        {
            // a service is restarted by the SCM (RestartCoordinator); here only for dotnet run / console
            while (true)
            {
                var restart = new RestartFlag();
                using (var host = CreateHostBuilder(args, restart).Build())
                {
                    host.Run();
                }
                if (!restart.Requested || WindowsServiceHelpers.IsWindowsService()) return;
            }
        }

        public static IHostBuilder CreateHostBuilder(string[] args) => CreateHostBuilder(args, new RestartFlag());

        public static IHostBuilder CreateHostBuilder(string[] args, RestartFlag restart)
        {
            var configWarnings = new ConfigWarnings();
            var paths = ConfigPaths.Resolve();

            // lock the folder only when we may (service/elevated); a normal dotnet run leaves it as it is
            if (!OperatingSystem.IsWindows() || WindowsServiceHelpers.IsWindowsService() || IsElevated())
            {
                var problem = paths.EnsureCreatedAndLocked();
                if (problem != null) configWarnings.Items.Add(problem);
                try
                {
                    if (SettingsStore.ProtectPlainSecrets(paths)) configWarnings.Info.Add($"Encrypted the passwords in {paths.SettingsFile}");
                }
                catch (Exception e)
                {
                    configWarnings.Items.Add($"Could not encrypt the passwords in {paths.SettingsFile}: {e.Message}");
                }
            }
            configWarnings.Info.Add($"Settings folder: {paths.ConfigDirectory}");

            return Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((hostContext, config) =>
                {
                    var env = hostContext.HostingEnvironment;
                    AppConfiguration.Add(config, env.ContentRootPath, env.EnvironmentName, paths, args, configWarnings.Items);
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
                    configWarnings.LogDirectory = logDir;
                })
                .ConfigureServices((hostContext, services) =>
                {
                    var config = hostContext.Configuration;
                    services.Configure<DetectionOptions>(config.GetSection(DetectionOptions.Section));
                    services.Configure<ProvidersOptions>(config.GetSection(ProvidersOptions.Section));
                    services.Configure<WebhookOptions>(config.GetSection(WebhookOptions.Section));
                    services.Configure<MqttOptions>(config.GetSection(MqttOptions.Section));

                    // secrets are stored as "dpapi:..."; the notifiers get them decrypted
                    services.PostConfigure<MqttOptions>(o => o.Password = Decrypt(o.Password, "Mqtt.Password", configWarnings));
                    services.PostConfigure<WebhookOptions>(o =>
                    {
                        if (o.Headers == null) return;
                        foreach (var name in o.Headers.Keys.ToList())
                        {
                            o.Headers[name] = Decrypt(o.Headers[name], $"Webhook.Headers.{name}", configWarnings);
                            if (string.IsNullOrEmpty(o.Headers[name])) o.Headers.Remove(name);
                        }
                    });

                    services.AddSingleton(configWarnings);
                    services.AddSingleton(paths);
                    services.AddSingleton(restart);
                    services.AddSingleton(sp => ProviderCatalog.Load(
                        sp.GetRequiredService<IOptions<ProvidersOptions>>().Value,
                        sp.GetRequiredService<IHostEnvironment>().ContentRootPath, paths));
                    services.AddSingleton(sp =>
                    {
                        var env = sp.GetRequiredService<IHostEnvironment>();
                        return new SettingsStore(env.ContentRootPath, env.EnvironmentName, paths, sp.GetRequiredService<ProviderCatalog>());
                    });
                    services.AddSingleton<EditorPolicy>();
                    services.AddSingleton<RestartCoordinator>();

                    services.AddSingleton<NetworkListener>();
                    services.AddSingleton<TrafficMonitor>();

                    services.AddSingleton<ICallNotifier, WebhookNotifier>();
                    services.AddSingleton<ICallNotifier, MqttNotifier>();
                    services.AddSingleton<MonitorState>(_ => new MonitorState());
                    services.AddSingleton<CallReporter>();

                    services.AddHostedService<Worker>();
                    services.AddHostedService<ProviderFileWatcher>();
                    services.AddHostedService<ProviderUpdateService>();
                    services.AddHostedService<StatusPipeServer>();
                })
                .UseWindowsService();
        }

        private static string Decrypt(string value, string name, ConfigWarnings warnings)
        {
            try
            {
                return SecretProtector.Unprotect(value);
            }
            catch (Exception e)
            {
                // e.g. the file was copied from another PC: DPAPI values only open on the PC that wrote them
                warnings.Items.Add($"{name} could not be decrypted ({e.Message}); enter it again in the settings window.");
                return "";
            }
        }

        private static bool IsElevated()
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }
}
