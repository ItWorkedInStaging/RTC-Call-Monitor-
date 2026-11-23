// SettingsStore.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. Reads the settings in effect and saves changes
//           from the settings window: appsettings.Local.json in the config
//           folder (other keys in it are kept; the previous file is kept as
//           .bak) and your own provider lists (config\providers\*.txt).
//           Secrets are encrypted (SecretProtector) and never sent back.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using RtcCallMonitor.Capture;
using RtcCallMonitor.Configuration;
using RtcCallMonitor.Ipc;

namespace RtcCallMonitor.Settings
{
    public class SettingsStore
    {
        private static readonly Regex ProviderName = new Regex("^[A-Za-z0-9_-]{1,40}$", RegexOptions.Compiled);
        private static readonly JsonSerializerOptions WriteJson = new JsonSerializerOptions { WriteIndented = true };
        private static readonly object FileLock = new object();

        private readonly string _contentRoot;
        private readonly string _environmentName;
        private readonly ConfigPaths _paths;
        private readonly ProviderCatalog _catalog;

        public SettingsStore(string contentRoot, string environmentName, ConfigPaths paths, ProviderCatalog catalog)
        {
            _contentRoot = contentRoot;
            _environmentName = environmentName;
            _paths = paths;
            _catalog = catalog;
        }

        public ConfigPaths Paths => _paths;

        /// <summary>Header names whose values are secrets (encrypted, never sent to the settings window).</summary>
        public static bool IsSecretHeader(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var n = name.ToLowerInvariant();
            return n == "authorization" || n == "proxy-authorization" || n == "cookie" ||
                   n.Contains("token") || n.Contains("key") || n.Contains("secret") || n.Contains("password");
        }

        private IConfiguration Current()
        {
            var builder = new ConfigurationBuilder();
            AppConfiguration.Add(builder, _contentRoot, _environmentName, _paths, null, null);
            return builder.Build();
        }

        // ------------------------------------------------------------------ read

        public SettingsDocument Read()
        {
            var config = Current();
            var detection = config.GetSection(DetectionOptions.Section).Get<DetectionOptions>() ?? new DetectionOptions();
            var mqtt = config.GetSection(MqttOptions.Section).Get<MqttOptions>() ?? new MqttOptions();
            var webhook = config.GetSection(WebhookOptions.Section).Get<WebhookOptions>() ?? new WebhookOptions();
            var providers = config.GetSection(ProvidersOptions.Section).Get<ProvidersOptions>() ?? new ProvidersOptions();
            var autoUpdate = providers.AutoUpdate ?? new ProviderAutoUpdateOptions();

            return new SettingsDocument
            {
                ConfigDirectory = _paths.ConfigDirectory,
                Detection = new DetectionSettings
                {
                    LocalNetworks = detection.LocalNetworks ?? Array.Empty<string>(),
                    CheckIntervalMs = detection.CheckIntervalMs,
                    MinPacketRate = detection.MinPacketRate,
                    CallStartIntervals = detection.CallStartIntervals,
                    CallEndIntervals = detection.CallEndIntervals,
                    Interfaces = detection.Interfaces ?? Array.Empty<string>(),
                    ExcludeInterfaces = detection.ExcludeInterfaces,
                    DefaultExcludeInterfaces = CaptureAddresses.DefaultExcludes.ToArray(),
                    CaptureIPv6 = detection.CaptureIPv6,
                    IgnoreUdpPorts = (detection.IgnoreUdpPorts ?? Array.Empty<int>()).Distinct().ToArray(),
                },
                Mqtt = new MqttSettings
                {
                    Enabled = mqtt.Enabled,
                    Host = mqtt.Host ?? "",
                    Port = mqtt.Port,
                    Username = mqtt.Username ?? "",
                    PasswordSet = !string.IsNullOrEmpty(mqtt.Password),
                    UseTls = mqtt.UseTls,
                    AllowUntrustedCertificates = mqtt.AllowUntrustedCertificates,
                    DeviceName = mqtt.DeviceName ?? "",
                    DefaultDeviceName = new MqttOptions().EffectiveDeviceName,
                    BaseTopic = mqtt.BaseTopic ?? "",
                    DiscoveryPrefix = mqtt.DiscoveryPrefix ?? "",
                },
                Webhook = new WebhookSettings
                {
                    Enabled = webhook.Enabled,
                    CallStartUrl = webhook.CallStartUrl ?? "",
                    CallEndUrl = webhook.CallEndUrl ?? "",
                    Headers = (webhook.Headers ?? new Dictionary<string, string>())
                        .Where(h => !string.IsNullOrEmpty(h.Value))
                        .Select(h => IsSecretHeader(h.Key)
                            ? new WebhookHeader { Name = h.Key, Value = "", IsSecret = true }
                            : new WebhookHeader { Name = h.Key, Value = h.Value })
                        .ToList(),
                },
                Providers = new ProviderSettings
                {
                    AutoUpdateEnabled = autoUpdate.Enabled,
                    AutoUpdateIntervalHours = autoUpdate.IntervalHours,
                    Custom = ReadCustomProviders(),
                    Loaded = _catalog == null
                        ? new List<LoadedProvider>()
                        : _catalog.Networks.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                            .Select(p => new LoadedProvider
                            {
                                Name = p.Key,
                                Ranges = p.Value.Length,
                                Source = _catalog.Sources.TryGetValue(p.Key, out var src) ? src : "",
                            }).ToList(),
                },
            };
        }

        private List<CustomProvider> ReadCustomProviders()
        {
            var list = new List<CustomProvider>();
            if (!Directory.Exists(_paths.CustomProvidersDirectory)) return list;
            foreach (var file in Directory.GetFiles(_paths.CustomProvidersDirectory, "*.txt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(new CustomProvider
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Ranges = File.ReadAllLines(file)
                        .Select(l => l.Trim())
                        .Where(l => l.Length > 0 && !l.StartsWith("#"))
                        .ToList(),
                });
            }
            return list;
        }

        /// <summary>
        /// Encrypts a plain MQTT password or secret webhook header in the settings file
        /// (e.g. typed in by hand or carried over from an older version). Returns true if the file changed.
        /// </summary>
        public static bool ProtectPlainSecrets(ConfigPaths paths)
        {
            lock (FileLock)
            {
                if (!File.Exists(paths.SettingsFile)) return false;
                JsonObject root;
                try
                {
                    root = JsonNode.Parse(File.ReadAllText(paths.SettingsFile),
                        documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
                }
                catch (JsonException) { return false; }
                if (root == null) return false;

                var changed = false;
                void Protect(JsonObject obj, string key)
                {
                    if (obj[key] is JsonValue v && v.TryGetValue<string>(out var plain) && plain.Length > 0 && !SecretProtector.IsProtected(plain))
                    {
                        var protectedValue = SecretProtector.Protect(plain);
                        if (protectedValue != plain) { obj[key] = protectedValue; changed = true; }
                    }
                }

                if (root[MqttOptions.Section] is JsonObject m) Protect(m, "Password");
                if (root[WebhookOptions.Section] is JsonObject w && w["Headers"] is JsonObject h)
                {
                    foreach (var name in h.Select(kv => kv.Key).Where(IsSecretHeader).ToList()) Protect(h, name);
                }
                if (!changed) return false;

                // comments in a hand-edited file are lost here; the original is kept as .bak
                File.Copy(paths.SettingsFile, paths.SettingsFile + ".bak", overwrite: true);
                var tmp = paths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, root.ToJsonString(WriteJson), new UTF8Encoding(false));
                File.Move(tmp, paths.SettingsFile, overwrite: true);
                return true;
            }
        }

        // ------------------------------------------------------------------ secrets for tests

        /// <summary>The MQTT settings to try: NewPassword null means the stored password.</summary>
        public MqttOptions ResolveMqtt(MqttSettings s)
        {
            string password;
            if (s.NewPassword != null) password = s.NewPassword;
            else password = SecretProtector.Unprotect(Current()[$"{MqttOptions.Section}:Password"] ?? "");

            return new MqttOptions
            {
                Enabled = true,
                Host = (s.Host ?? "").Trim(),
                Port = s.Port,
                Username = (s.Username ?? "").Trim(),
                Password = password,
                UseTls = s.UseTls,
                AllowUntrustedCertificates = s.AllowUntrustedCertificates,
                DeviceName = s.DeviceName ?? "",
            };
        }

        /// <summary>Header values to send: a secret header with Value null uses the stored value.</summary>
        public Dictionary<string, string> ResolveHeaders(IEnumerable<WebhookHeader> headers)
        {
            var stored = Current().GetSection($"{WebhookOptions.Section}:Headers");
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in headers ?? Enumerable.Empty<WebhookHeader>())
            {
                if (string.IsNullOrWhiteSpace(h.Name)) continue;
                var value = h.Value ?? SecretProtector.Unprotect(stored[h.Name.Trim()] ?? "");
                if (!string.IsNullOrEmpty(value)) result[h.Name.Trim()] = value;
            }
            return result;
        }

        // ------------------------------------------------------------------ save

        /// <summary>Checks and stores the settings. Nothing is written when there are errors.</summary>
        public List<string> Save(SettingsDocument doc)
        {
            var errors = new List<string>();
            if (doc == null) { errors.Add("No settings were sent."); return errors; }
            doc.Detection ??= new DetectionSettings();
            doc.Mqtt ??= new MqttSettings();
            doc.Webhook ??= new WebhookSettings();
            doc.Providers ??= new ProviderSettings();

            var current = Current();
            var detection = new DetectionOptions
            {
                LocalNetworks = Clean(doc.Detection.LocalNetworks),
                CheckIntervalMs = doc.Detection.CheckIntervalMs,
                MinPacketRate = doc.Detection.MinPacketRate,
                CallStartIntervals = doc.Detection.CallStartIntervals,
                CallEndIntervals = doc.Detection.CallEndIntervals,
                Interfaces = Clean(doc.Detection.Interfaces),
                ExcludeInterfaces = doc.Detection.ExcludeInterfaces == null ? null : Clean(doc.Detection.ExcludeInterfaces),
                CaptureIPv6 = doc.Detection.CaptureIPv6,
                IgnoreUdpPorts = (doc.Detection.IgnoreUdpPorts ?? Array.Empty<int>()).Distinct().ToArray(),
            };
            foreach (var port in detection.IgnoreUdpPorts)
            {
                if (port < 0 || port > 65535) errors.Add($"Detection.IgnoreUdpPorts: {port} is not a valid port (0-65535).");
            }

            var mqtt = ResolveMqttForSave(doc.Mqtt, current);
            var headers = MergeHeaders(doc.Webhook.Headers, current, errors);
            var webhook = new WebhookOptions
            {
                Enabled = doc.Webhook.Enabled,
                CallStartUrl = (doc.Webhook.CallStartUrl ?? "").Trim(),
                CallEndUrl = (doc.Webhook.CallEndUrl ?? "").Trim(),
            };
            var providers = new ProvidersOptions
            {
                AutoUpdate = new ProviderAutoUpdateOptions
                {
                    Enabled = doc.Providers.AutoUpdateEnabled,
                    IntervalHours = doc.Providers.AutoUpdateIntervalHours,
                },
            };

            errors.AddRange(ConfigValidator.Validate(detection, webhook, mqtt.Effective));
            errors.AddRange(ConfigValidator.ValidateProviders(providers));
            var customFiles = CheckCustomProviders(doc.Providers.Custom, errors);
            if (errors.Count > 0) return errors;

            lock (FileLock)
            {
                WriteSettingsFile(detection, mqtt, webhook, headers, providers);
                WriteCustomProviders(customFiles);
            }
            return errors;
        }

        private static string[] Clean(IEnumerable<string> values) =>
            (values ?? Enumerable.Empty<string>()).Select(v => (v ?? "").Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        private sealed class MqttSave
        {
            public MqttOptions Effective;   // for validation (decrypted password not needed, only "is set")
            public string StoredPassword;   // value for the file; null = leave the key as it is
        }

        private static MqttSave ResolveMqttForSave(MqttSettings s, IConfiguration current)
        {
            var storedRaw = current[$"{MqttOptions.Section}:Password"] ?? "";
            string forFile = null;
            var effectivePassword = storedRaw;
            if (s.NewPassword != null)
            {
                forFile = s.NewPassword.Length == 0 ? "" : SecretProtector.Protect(s.NewPassword);
                effectivePassword = s.NewPassword;
            }

            return new MqttSave
            {
                StoredPassword = forFile,
                Effective = new MqttOptions
                {
                    Enabled = s.Enabled,
                    Host = (s.Host ?? "").Trim(),
                    Port = s.Port,
                    Username = (s.Username ?? "").Trim(),
                    Password = effectivePassword,
                    UseTls = s.UseTls,
                    AllowUntrustedCertificates = s.AllowUntrustedCertificates,
                    DeviceName = (s.DeviceName ?? "").Trim(),
                    BaseTopic = (s.BaseTopic ?? "").Trim(),
                    DiscoveryPrefix = (s.DiscoveryPrefix ?? "").Trim(),
                },
            };
        }

        private static Dictionary<string, string> MergeHeaders(List<WebhookHeader> headers, IConfiguration current, List<string> errors)
        {
            var stored = current.GetSection($"{WebhookOptions.Section}:Headers");
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in headers ?? new List<WebhookHeader>())
            {
                var name = (h.Name ?? "").Trim();
                if (name.Length == 0) continue;
                if (!Regex.IsMatch(name, "^[A-Za-z0-9!#$%&'*+.^_`|~-]+$"))
                {
                    errors.Add($"Webhook header '{name}' is not a valid header name.");
                    continue;
                }
                if (result.ContainsKey(name))
                {
                    errors.Add($"Webhook header '{name}' is listed twice.");
                    continue;
                }

                string value;
                if (h.Value == null) value = IsSecretHeader(name) ? SecretProtector.Protect(stored[name] ?? "") : stored[name] ?? ""; // keep what is stored
                else if (IsSecretHeader(name)) value = SecretProtector.Protect(h.Value);
                else value = h.Value;
                result[name] = value;
            }
            return result;
        }

        private static Dictionary<string, List<string>> CheckCustomProviders(List<CustomProvider> custom, List<string> errors)
        {
            var files = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in custom ?? new List<CustomProvider>())
            {
                var name = (p.Name ?? "").Trim();
                if (!ProviderName.IsMatch(name) || string.Equals(name, ProviderCatalog.AutoFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"Provider name '{name}' can only use letters, digits, '-' and '_' (up to 40 characters).");
                    continue;
                }
                if (files.ContainsKey(name))
                {
                    errors.Add($"Provider '{name}' is listed twice.");
                    continue;
                }

                var lines = (p.Ranges ?? new List<string>()).Select(l => (l ?? "").Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")).ToList();
                if (lines.Count == 0)
                {
                    errors.Add($"Provider '{name}' has no ranges.");
                    continue;
                }
                var rangeErrors = new List<string>();
                ProviderCatalog.ParseLines(lines, name, rangeErrors, new List<string>());
                errors.AddRange(rangeErrors);
                files[name] = lines;
            }
            return files;
        }

        private void WriteSettingsFile(DetectionOptions detection, MqttSave mqtt, WebhookOptions webhook,
            Dictionary<string, string> headers, ProvidersOptions providers)
        {
            Directory.CreateDirectory(_paths.ConfigDirectory);
            JsonObject root = null;
            if (File.Exists(_paths.SettingsFile))
            {
                try
                {
                    root = JsonNode.Parse(File.ReadAllText(_paths.SettingsFile),
                        documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
                }
                catch (JsonException) { root = null; } // unreadable: replaced (the old one is kept as .bak)
            }
            root ??= new JsonObject();

            var d = Section(root, DetectionOptions.Section);
            d["LocalNetworks"] = ToNode(detection.LocalNetworks);
            d["CheckIntervalMs"] = detection.CheckIntervalMs;
            d["MinPacketRate"] = detection.MinPacketRate;
            d["CallStartIntervals"] = detection.CallStartIntervals;
            d["CallEndIntervals"] = detection.CallEndIntervals;
            d["Interfaces"] = ToNode(detection.Interfaces);
            if (detection.ExcludeInterfaces == null) d.Remove("ExcludeInterfaces");
            else d["ExcludeInterfaces"] = ToNode(detection.ExcludeInterfaces);
            d["CaptureIPv6"] = detection.CaptureIPv6;
            d["IgnoreUdpPorts"] = ToNode(detection.IgnoreUdpPorts.Length == 0 ? new[] { 0 } : detection.IgnoreUdpPorts);

            var m = Section(root, MqttOptions.Section);
            var e = mqtt.Effective;
            m["Enabled"] = e.Enabled;
            m["Host"] = e.Host;
            m["Port"] = e.Port;
            m["Username"] = e.Username;
            if (mqtt.StoredPassword != null) m["Password"] = mqtt.StoredPassword;
            else if (m["Password"] is JsonValue pw && pw.TryGetValue<string>(out var plain)) m["Password"] = SecretProtector.Protect(plain);
            m["UseTls"] = e.UseTls;
            m["AllowUntrustedCertificates"] = e.AllowUntrustedCertificates;
            m["DeviceName"] = e.DeviceName;
            m["BaseTopic"] = e.BaseTopic;
            m["DiscoveryPrefix"] = e.DiscoveryPrefix;

            var w = Section(root, WebhookOptions.Section);
            w["Enabled"] = webhook.Enabled;
            w["CallStartUrl"] = webhook.CallStartUrl;
            w["CallEndUrl"] = webhook.CallEndUrl;
            w["Headers"] = ToNode(headers);

            var p = Section(root, ProvidersOptions.Section);
            var a = Section(p, "AutoUpdate");
            a["Enabled"] = providers.AutoUpdate.Enabled;
            a["IntervalHours"] = providers.AutoUpdate.IntervalHours;

            var text = root.ToJsonString(WriteJson);
            var tmp = _paths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(_paths.SettingsFile)) File.Copy(_paths.SettingsFile, _paths.SettingsFile + ".bak", overwrite: true);
            File.Move(tmp, _paths.SettingsFile, overwrite: true);
        }

        private void WriteCustomProviders(Dictionary<string, List<string>> files)
        {
            var dir = _paths.CustomProvidersDirectory;
            Directory.CreateDirectory(dir);

            foreach (var existing in Directory.GetFiles(dir, "*.txt"))
            {
                if (!files.ContainsKey(Path.GetFileNameWithoutExtension(existing))) File.Delete(existing);
            }
            foreach (var (name, lines) in files)
            {
                var path = Path.Combine(dir, name + ".txt");
                var content = new StringBuilder()
                    .AppendLine($"# {name}: your own ranges, added to any bundled or downloaded list of the same name.")
                    .AppendLine("# Edited with the RTC Call Monitor settings window. One range or address per line.");
                foreach (var l in lines) content.AppendLine(l);

                if (File.Exists(path) && File.ReadAllText(path) == content.ToString()) continue; // no change: no reload
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, content.ToString(), new UTF8Encoding(false));
                File.Move(tmp, path, overwrite: true);
            }
        }

        private static JsonObject Section(JsonObject parent, string name)
        {
            if (parent[name] is JsonObject existing) return existing;
            var created = new JsonObject();
            parent[name] = created;
            return created;
        }

        private static JsonNode ToNode<T>(T value) => JsonSerializer.SerializeToNode(value);
    }
}
