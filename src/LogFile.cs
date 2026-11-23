// LogFile.cs
// Version: 1.0.1
// Changelog:
//   1.0.1 - Apply the "Logging:LogLevel" levels to the file too (AddSerilog adds
//           its own allow-everything rule, which let debug and Microsoft.* lines through).
//   1.0.0 - Initial version. Adds a Serilog rolling file as an extra logging
//           provider: one file per day (rtc-call-monitor-YYYYMMDD.log), a new
//           part when a file reaches MaxFileSizeMB, RetainedDays files kept.
//           Levels still come from the "Logging" section.

using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RtcCallMonitor.Configuration;
using Serilog;

namespace RtcCallMonitor
{
    public static class LogFile
    {
        public const string FilePrefix = "rtc-call-monitor-";

        /// <summary>Adds the log file provider. Returns the folder used, or null if the file is off or can't be written.</summary>
        public static string AddTo(ILoggingBuilder logging, IConfiguration configuration, out string problem)
        {
            problem = null;
            var options = configuration.GetSection(LogFileOptions.Section).Get<LogFileOptions>() ?? new LogFileOptions();
            if (!options.Enabled) return null;

            var dir = options.EffectiveDirectory;
            try
            {
                Directory.CreateDirectory(dir);
                // fail now, with a clear message, rather than on every write
                var probe = Path.Combine(dir, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
            }
            catch (Exception e)
            {
                problem = $"Log file disabled: cannot write to {dir} ({e.Message}). Set LogFile.Directory to a writable folder.";
                return null;
            }

            var fileLogger = new LoggerConfiguration()
                .MinimumLevel.Verbose() // the "Logging" section decides what reaches this provider
                .WriteTo.File(
                    Path.Combine(dir, FilePrefix + ".log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: Math.Max(1, options.RetainedDays),
                    fileSizeLimitBytes: Math.Max(1, options.MaxFileSizeMB) * 1024L * 1024L,
                    rollOnFileSizeLimit: true,
                    shared: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(2),
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            logging.AddSerilog(fileLogger, dispose: true);

            // AddSerilog registers a Trace-level rule for its provider, which beats the
            // general "Logging:LogLevel" rules; repeat those rules for this provider
            // (added later, so they win) so the file gets the same levels as the console.
            foreach (var rule in configuration.GetSection("Logging:LogLevel").GetChildren())
            {
                if (!Enum.TryParse<LogLevel>(rule.Value, ignoreCase: true, out var level)) continue;
                var category = string.Equals(rule.Key, "Default", StringComparison.OrdinalIgnoreCase) ? null : rule.Key;
                logging.AddFilter<Serilog.Extensions.Logging.SerilogLoggerProvider>(category, level);
            }
            return dir;
        }
    }
}
