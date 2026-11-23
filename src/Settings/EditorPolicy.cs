// EditorPolicy.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. Who may read and change settings through the
//           pipe: accounts listed in <config folder>\editors.json (the
//           installer adds the user who installed), elevated administrators,
//           and the account the monitor itself runs as (e.g. dotnet run).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RtcCallMonitor.Configuration;

namespace RtcCallMonitor.Settings
{
    /// <summary>The Windows account on the other end of the pipe.</summary>
    public class Caller
    {
        public string Sid { get; set; }
        public string Name { get; set; }

        /// <summary>Running elevated as an administrator (not just a member of the group).</summary>
        public bool IsElevatedAdmin { get; set; }

        /// <summary>Same account the monitor runs as.</summary>
        public bool IsSelf { get; set; }
    }

    public class EditorsFile
    {
        public List<EditorEntry> Editors { get; set; } = new List<EditorEntry>();
    }

    public class EditorEntry
    {
        public string Sid { get; set; }

        /// <summary>For people reading the file, e.g. "PC\Joshua".</summary>
        public string Name { get; set; }
    }

    public class EditorPolicy
    {
        private readonly ConfigPaths _paths;

        public EditorPolicy(ConfigPaths paths)
        {
            _paths = paths;
        }

        /// <summary>Null when allowed, otherwise the reason to show.</summary>
        public string Check(Caller caller)
        {
            if (caller == null) return "Could not tell which Windows account is asking.";
            if (caller.IsSelf || caller.IsElevatedAdmin) return null;
            if (!string.IsNullOrEmpty(caller.Sid) && Editors().Any(e => string.Equals(e.Sid, caller.Sid, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }
            return $"{caller.Name ?? "This account"} is not allowed to change RTC Call Monitor settings. " +
                   "Settings can be changed by the account that installed it (re-run Install.cmd to add yours).";
        }

        public IReadOnlyList<EditorEntry> Editors()
        {
            try
            {
                if (!File.Exists(_paths.EditorsFile)) return Array.Empty<EditorEntry>();
                var file = JsonSerializer.Deserialize<EditorsFile>(File.ReadAllText(_paths.EditorsFile),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip });
                return (IReadOnlyList<EditorEntry>)file?.Editors ?? Array.Empty<EditorEntry>();
            }
            catch (Exception)
            {
                return Array.Empty<EditorEntry>();
            }
        }
    }
}
