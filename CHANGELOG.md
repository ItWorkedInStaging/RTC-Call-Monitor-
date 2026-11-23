# Changelog

All notable changes to this fork of [ericgla/RTC-Call-Monitor](https://github.com/ericgla/RTC-Call-Monitor) are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions use [Semantic Versioning](https://semver.org/).

## [2.1.0] - 2026-11-23

### Added
- **MQTT output with Home Assistant discovery** (`Mqtt` section): a device with `On call` (binary sensor, with provider/ip/started attributes), `Call provider` and `Last call duration` entities. All topics are retained; availability uses a Last Will so entities go unavailable if the app stops or crashes. Discovery is re-sent when Home Assistant restarts, and the app reconnects with backoff (5-60 s) when the broker is unreachable. Username/password and optional TLS. Uses MQTTnet 5.2.
- MQTT and webhooks are switched on separately with `Enabled`; both can run together, or neither (log only).
- Webhook `Headers` setting for authentication tokens.
- `appsettings.Local.json` (git-ignored) for personal settings and secrets, plus `appsettings.Local.example.json` as a template. `RTCMON_` environment variables and command-line arguments also override settings.
- Startup validation: every config problem is listed in plain language and the app exits with code 2.
- Unit tests for provider files, config translation, validation and MQTT topics/discovery (48 tests in total).

### Changed
- **New config layout**: `Detection`, `Providers`, `Webhook` and `Mqtt` sections replace `Application` and `Provider`. Old configs still work and log a warning asking you to migrate.
- Provider IP ranges moved from `appsettings.json` into one text file per provider in `src/providers` (one CIDR per line, `#` comments). Problems are reported with file name and line number.
- `CheckInterval` renamed `CheckIntervalMs`; `LocalNetwork` renamed `LocalNetworks`; `CallStartWebhook`/`CallEndWebhook` renamed `Webhook.CallStartUrl`/`CallEndUrl`.
- Console log lines are single-line with a timestamp (was systemd format).

### Fixed
- Shorthand IPv4 ranges such as `3.7.35/25` are now rejected. .NET reads `3.7.35` as `3.7.0.35`, so such a typo silently matched the wrong network.
- An invalid `LocalNetworks` entry no longer crashes at startup with a raw exception; it is reported by the config check.

## [2.0.0] - 2026-11-23

### Changed
- **Breaking:** requires the .NET 10 SDK/runtime (was .NET 5, out of support since 2022).
- Updated `Microsoft.Extensions.Hosting` and `Microsoft.Extensions.Hosting.WindowsServices` to 10.0.0.
- Replaced the `IPNetwork2` package with the built-in `System.Net.IPNetwork`.
- Runtime identifiers changed to the portable `win-x64`, `linux-x64` and `osx-x64`.
- Dockerfile and VS Code launch configuration target .NET 10.
- CI workflow uses the .NET 10 SDK and `actions/checkout` / `actions/setup-dotnet` v4, builds in Release and runs the tests. It can also be started manually.

### Added
- `NetworkParser`: logs a startup warning when a configured range has host bits set and shows the range actually used (for example, GoTo Meeting `202.173.24.0/20` is really `202.173.16.0/20`).
- `tests/RtcCallMonitor.Tests` xUnit project covering call detection and range parsing.
- README: how to run the tests.

### Removed
- `Microsoft.VisualStudio.Azure.Containers.Tools.Targets` package (Visual Studio Docker tooling only).

### Fixed
- README example for `LocalNetwork` now shows it as an array.

## [1.1.0] - 2026-11-22

### Added
- `CallStartIntervals` (default 2) and `CallEndIntervals` (default 3) settings to debounce call start and end, so short network dips no longer end a call.
- The call-end webhook payload includes `provider` alongside `duration`.
- Webhook failures and non-2xx responses are logged as warnings; requests time out after 10 seconds.

### Changed
- During an active call, traffic to any IP of the same provider keeps the call alive, so a provider moving the call to another media relay doesn't end it.
- Call duration is measured to the last interval with call traffic, excluding the end delay.
- Detection logic moved into the dependency-free `CallDetector` class.
- On Linux/macOS the app now stops with a clear message: packet capture uses `SIO_RCVALL`, which is Windows-only. README updated to match.

### Fixed
- Inbound traffic was ignored: the inbound/outbound comparison compared a value with itself.
- Race between the socket callback and the worker loop on the packet counters.
- Raw socket reads never completed, because `EndReceive` was only called when the socket was "connected", which a raw socket never is.
- After an IP address change, the old socket's callback could act on the new socket.

## [1.0.0] - 2023-02-07

- Original upstream version (ericgla/RTC-Call-Monitor, last commit "Update IPs for MSTeams").
