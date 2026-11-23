// AppConfiguration.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version (moved from Program.cs). Config layers, lowest to
//           highest: appsettings.json, appsettings.{Environment}.json,
//           appsettings.Local.json next to the program (old location),
//           <config folder>\appsettings.Local.json, RTCMON_ environment
//           variables, command line. Old-style keys are mapped by LegacyConfig.
//           Settings left next to the installed program are reported (service only).

using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;

namespace RtcCallMonitor.Configuration
{
    public static class AppConfiguration
    {
        public static void Add(IConfigurationBuilder config, string contentRoot, string environmentName, ConfigPaths paths,
            string[] args, List<string> warnings)
        {
            config.Sources.Clear();
            config.SetBasePath(contentRoot)
                  .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                  .AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: false)
                  .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

            if (paths != null)
            {
                // dotnet run from src: src\appsettings.Local.json is the normal place, no warning
                if (warnings != null && Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService() && File.Exists(Path.Combine(contentRoot, "appsettings.Local.json")) &&
                    !PathsEqual(contentRoot, paths.ConfigDirectory))
                {
                    warnings.Add($"Settings found next to the program ({Path.Combine(contentRoot, "appsettings.Local.json")}). " +
                                 $"They still apply, but settings now live in {paths.SettingsFile}; run the installer to move them.");
                }
                config.AddJsonFile(paths.SettingsFile, optional: true, reloadOnChange: false);
            }

            config.AddEnvironmentVariables("RTCMON_");
            if (args != null) config.AddCommandLine(args);

            // translate pre-2.1 settings; lowest priority so new-style values always win
            var legacy = LegacyConfig.Map(config.Build(), warnings ?? new List<string>());
            if (legacy.Count > 0)
            {
                config.Sources.Insert(0, new MemoryConfigurationSource { InitialData = legacy });
            }
        }

        private static bool PathsEqual(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), System.StringComparison.OrdinalIgnoreCase);
    }
}
