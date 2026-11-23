# Changelog

All notable changes to this fork of [ericgla/RTC-Call-Monitor](https://github.com/ericgla/RTC-Call-Monitor) are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions use [Semantic Versioning](https://semver.org/).

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
