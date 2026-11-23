// ConfigPaths.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. Where the editable configuration lives:
//           %ProgramData%\RtcCallMonitor\config (appsettings.Local.json, your
//           own provider lists, downloaded lists, editors.json), outside
//           Program Files so the settings window can change it without UAC.
//           RTCMON_CONFIG_DIR overrides the folder (tests, portable use).

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RtcCallMonitor.Configuration
{
    public class ConfigPaths
    {
        public const string OverrideVariable = "RTCMON_CONFIG_DIR";
        public const string SettingsFileName = "appsettings.Local.json";
        public const string EditorsFileName = "editors.json";

        public ConfigPaths(string configDirectory)
        {
            ConfigDirectory = Path.GetFullPath(configDirectory);
        }

        /// <summary>The default location, or RTCMON_CONFIG_DIR when set.</summary>
        public static ConfigPaths Resolve()
        {
            var overridden = Environment.GetEnvironmentVariable(OverrideVariable);
            if (!string.IsNullOrWhiteSpace(overridden)) return new ConfigPaths(Environment.ExpandEnvironmentVariables(overridden));
            return new ConfigPaths(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RtcCallMonitor", "config"));
        }

        public string ConfigDirectory { get; }

        /// <summary>Settings written by the settings window (also hand-editable by an admin).</summary>
        public string SettingsFile => Path.Combine(ConfigDirectory, SettingsFileName);

        /// <summary>Your own provider lists, e.g. 3cx.txt; added to the bundled and downloaded ranges.</summary>
        public string CustomProvidersDirectory => Path.Combine(ConfigDirectory, "providers");

        /// <summary>Lists downloaded by the auto-updater; replace the bundled list of the same provider.</summary>
        public string AutoProvidersDirectory => Path.Combine(CustomProvidersDirectory, ProviderCatalog.AutoFolderName);

        /// <summary>SIDs of the Windows accounts allowed to change settings through the settings window.</summary>
        public string EditorsFile => Path.Combine(ConfigDirectory, EditorsFileName);

        /// <summary>
        /// Creates the folders. On Windows, when allowed to (service or elevated), locks the
        /// config folder to SYSTEM and Administrators: it holds the MQTT password.
        /// Returns a warning to log, or null.
        /// </summary>
        public string EnsureCreatedAndLocked()
        {
            try
            {
                Directory.CreateDirectory(CustomProvidersDirectory);
                Directory.CreateDirectory(AutoProvidersDirectory);
            }
            catch (Exception e)
            {
                return $"Cannot create the config folder {ConfigDirectory}: {e.Message}";
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
            try
            {
                Lock(ConfigDirectory);
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null; // not elevated (e.g. dotnet run from a normal prompt): leave as it is
            }
            catch (Exception e)
            {
                return $"Could not restrict access to {ConfigDirectory}: {e.Message}";
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static void Lock(string dir)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false); // drop ProgramData's "Users can read/create"
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            {
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null),
                    FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            }
            new DirectoryInfo(dir).SetAccessControl(security);
        }
    }
}
