# RTC-Call-Monitor

Detects when you are on a voice or video call (Teams, Zoom, Google Meet, Webex and others) by watching your PC's network traffic, and tells Home Assistant (over MQTT) or any other system (over webhooks) when a call starts and ends.

Typical uses: an "On Air" light outside your door, pausing music, muting speakers, or setting a status while you're on a call.

This is a maintained fork of [ericgla/RTC-Call-Monitor](https://github.com/ericgla/RTC-Call-Monitor). See [CHANGELOG.md](CHANGELOG.md) for what has changed.

## How it works

Call audio and video travel over UDP to the provider's media servers. Every second the monitor:

1. Counts UDP packets between your PC and each remote IP address, on every active network connection at once (Wi-Fi, Ethernet, VPN; IPv4 and IPv6). Traffic inside your local networks is ignored.
2. Checks the busiest addresses against the known IP ranges of each provider (`src/providers/*.txt`).
3. Reports a **call started** when a provider's traffic stays above the threshold for a few seconds, and **call ended** when it stops.

It only looks at packet counts and IP addresses: it never reads call content. Capturing traffic needs administrator rights.

### Supported providers

| Provider | File | Source of the ranges |
|---|---|---|
| Microsoft Teams | `msteams.txt` | [Microsoft 365 IP web service](https://learn.microsoft.com/en-us/microsoft-365/enterprise/microsoft-365-ip-web-service) (Teams media, Optimize category). **Updated automatically** |
| Zoom | `zoom.txt` | [ZoomMeetings.txt](https://assets.zoom.us/docs/ipranges/ZoomMeetings.txt). **Updated automatically** |
| Webex | `webex.txt` | Original project config |
| Google Meet | `googlemeet.txt` | [Prepare your network for Meet](https://support.google.com/a/answer/1279090) (Workspace and personal accounts) |
| GoTo Meeting | `gotomeeting.txt` | Original project config plus [GoTo's published media ranges](https://support.goto.com/meeting/help/minimal-firewall-settings-for-the-goto-app) |
| Slack | `slack.txt` | Original project config |
| Discord | `discord.txt` | Original project config |

Other providers can be added with a text file; see [Providers](#providers). Changes to the files are picked up while the monitor is running.

**Self-hosted phone systems (3CX, FreePBX, ...)**: call media goes to *your own* PBX or media server rather than to a vendor's network, so it is not covered by the files above. Add your PBX's public IP to a new file such as `providers/3cx.txt` (see [Call not detected](#call-not-detected) for how to find it).

## Requirements

- **Windows** (packet capture uses `SIO_RCVALL`, which only exists on Windows; on Linux/macOS the app stops with a clear message)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0): `winget install --id Microsoft.DotNet.SDK.10 --exact`
- Administrator rights to run it
- Optional: an MQTT broker (for example the Home Assistant **Mosquitto broker** add-on)

## Quick start

1. **Clone and test**

   ```powershell
   git clone <your repo URL>
   cd RTC-Call-Monitor
   dotnet test
   ```

   If restore fails with `NU1100 Unable to resolve ...`, NuGet has no package source: run `dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org`.

2. **Create your settings file**: copy `src\appsettings.Local.example.json` to `src\appsettings.Local.json` (this file is git-ignored) and set at least an output:

   ```json
   {
     "Mqtt": {
       "Enabled": true,
       "Host": "192.168.1.10",
       "Username": "rtc-monitor",
       "Password": "your-password"
     }
   }
   ```

   Your local networks are detected from your network connections, so they usually need no setting (see [Detection](#detection)).

3. **Run it** from a terminal opened with *Run as administrator*:

   ```powershell
   cd src
   dotnet run
   ```

   You should see the providers load, a `capturing on ...` line for each network connection and, with MQTT enabled, `MQTT connected to ...`. Join a call: after about 2 seconds the log shows `call started for msteams ...`. The same lines go to a log file (see [Logs](#logs)).

## Home Assistant

With MQTT enabled, Home Assistant discovers a device named after your PC (`Mqtt.DeviceName`, default the computer name) under **Settings → Devices & services → MQTT**:

| Entity (device `josh-pc`) | Value |
|---|---|
| `binary_sensor.josh_pc_on_call` | `on` during a call; attributes `provider`, `ip`, `started` |
| `sensor.josh_pc_call_provider` | `msteams`, `zoom`, ... or `none` |
| `sensor.josh_pc_last_call_duration` | length of the last call, in seconds |

The entities become *unavailable* when the monitor stops, crashes or loses its connection, so Home Assistant never gets stuck showing a call. Discovery is sent again whenever Home Assistant restarts.

**Broker login with the Mosquitto add-on**: the add-on accepts Home Assistant users. Create a dedicated user (**Settings → People → Users → Add user**, "Can only log in from the local network" is fine) and use its **username** in `Mqtt.Username`. See [Troubleshooting](#troubleshooting) if the login is refused.

Example automation, an "On Air" light:

```yaml
automation:
  - alias: On Air light
    triggers:
      - trigger: state
        entity_id: binary_sensor.josh_pc_on_call
    actions:
      - action: "light.turn_{{ 'on' if trigger.to_state.state == 'on' else 'off' }}"
        target:
          entity_id: light.office_door
```

## Configuration

### Files and precedence

All settings live in the `src` folder:

| File | In git | Purpose |
|---|---|---|
| `appsettings.json` | yes | Defaults |
| `appsettings.Local.json` | **no** | Your values and secrets; overrides the defaults |
| `appsettings.Local.example.json` | yes | Template for the file above |
| `providers/<name>.txt` | yes | IP ranges per provider |

Later sources win: `appsettings.json` → `appsettings.{Environment}.json` → `appsettings.Local.json` → environment variables starting with `RTCMON_` (use `__` between levels, e.g. `RTCMON_Mqtt__Password`) → command-line arguments.

The configuration is checked at startup. If anything is wrong, every problem is listed and the app exits (exit code 2):

```text
crit: Configuration has 2 problem(s); fix them and start again:
crit:   - Mqtt.Enabled is true but Mqtt.Host is empty.
crit:   - providers/zoom.txt line 12: '3.7.35/25' is not a valid range (expected e.g. 3.7.35.0/25)
```

Config files from versions before 2.1 (`Application` and `Provider` sections) still work; a startup warning asks you to move to the sections below.

### Detection

| Setting | Default | Meaning |
|---|---|---|
| `LocalNetworks` | `[]` | Extra local subnets whose traffic is ignored ([CIDR notation](https://www.digitalocean.com/community/tutorials/understanding-ip-addresses-subnets-and-cidr-notation-for-networking), e.g. `192.168.1.0/24`). Each connection's own subnet is always ignored, so this is only needed for other local networks you reach through a router |
| `Interfaces` | `[]` | Capture only on connections whose name or description matches one of these patterns (`*` and `?` wildcards), e.g. `[ "Wi-Fi", "NordLynx*" ]`. Empty: all connections |
| `ExcludeInterfaces` | virtual adapters | Connections to skip when `Interfaces` is empty. Default: `vEthernet*` (Hyper-V/WSL/Docker), Hyper-V, VirtualBox and VMware adapters, Teredo, ISATAP, Bluetooth. Setting it replaces that list |
| `CaptureIPv6` | `true` | Also capture on global IPv6 addresses |
| `CheckIntervalMs` | `1000` | Length of each measurement interval, in milliseconds |
| `MinPacketRate` | `1` | UDP packets per interval to/from a provider IP that count as call traffic |
| `CallStartIntervals` | `2` | Busy intervals in a row before a call is reported as started |
| `CallEndIntervals` | `3` | Quiet intervals in a row before a call is reported as ended |

While a call is active, traffic to *any* IP of the same provider keeps it alive, so the provider moving your call to another server doesn't end it.

The monitor captures on every address of every selected connection and re-scans about 2 seconds after Windows reports a change, so connecting a VPN, switching Wi-Fi or plugging in a cable is picked up without a restart. With a VPN, calls are seen on the VPN connection (the physical one only carries the encrypted tunnel).

### Outputs

MQTT and webhooks are switched on separately with `Enabled`. Use either, both, or neither (calls are then only logged).

#### MQTT

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Turn MQTT on |
| `Host` / `Port` | – / `1883` | Broker address |
| `Username` / `Password` | – | Broker login (put the password in `appsettings.Local.json`) |
| `UseTls` | `false` | Connect with TLS (usually port `8883`) |
| `AllowUntrustedCertificates` | `false` | Accept a self-signed broker certificate |
| `DeviceName` | computer name | Device name in Home Assistant and in the topics |
| `BaseTopic` | `rtc-call-monitor` | Topic prefix |
| `DiscoveryPrefix` | `homeassistant` | Home Assistant discovery prefix |

Topics (all retained, QoS 1):

```text
rtc-call-monitor/<device>/state          ON | OFF
rtc-call-monitor/<device>/provider       msteams | zoom | ... | none
rtc-call-monitor/<device>/attributes     {"provider":"msteams","ip":"52.112.1.2","started":"2026-11-23T00:40:00Z"}
rtc-call-monitor/<device>/last_duration  1834
rtc-call-monitor/<device>/availability   online | offline   (Last Will: offline)
```

If the broker can't be reached, the monitor retries every 5 s, slowing to every 60 s, and re-sends everything once connected.

#### Webhooks

```json
"Webhook": {
  "Enabled": true,
  "CallStartUrl": "http://homeassistant.local:8123/api/webhook/call-started",
  "CallEndUrl": "http://homeassistant.local:8123/api/webhook/call-ended",
  "Headers": { "Authorization": "Bearer ..." }
}
```

Both are HTTP POST with a JSON body; either URL may be left empty. `Headers` are added to every request.

| Event | Body |
|---|---|
| Call start | `{"provider": "msteams"}` |
| Call end | `{"provider": "msteams", "duration": 123}` (seconds) |

Failures (10 s timeout, non-2xx responses) are logged as warnings.

### Providers

Each file in `src/providers` is one provider, and the file name is the provider name (`zoom.txt` → `zoom`). One CIDR range per line; blank lines and anything after `#` are ignored:

```text
# zoom.txt - source: https://assets.zoom.us/docs/ipranges/ZoomMeetings.txt
3.7.35.0/25
3.21.137.128/25
```

To add a provider, add a file. `Providers.Directory` sets a different folder, and `Providers.Inline` adds ranges from config (`{ "zoom": [ "203.0.113.0/24" ] }`).

**Live reload:** adding, editing or deleting a file in `providers` (or `providers/auto`) is picked up about 2 seconds later, without a restart. If the changed file has an error, the log lists it and the previous ranges stay in use.

#### Automatic updates

Teams and Zoom publish their IP lists in a machine-readable form, so the monitor downloads them at startup (after 10 seconds) and then every `IntervalHours`:

```json
"Providers": {
  "AutoUpdate": { "Enabled": true, "IntervalHours": 24 }
}
```

| Provider | Downloaded from |
|---|---|
| `msteams` | Microsoft 365 IP web service, `endpoints.office.com` (Skype/Teams service area, Optimize category). The version is checked first, so the full list is only fetched when Microsoft publishes a new one. |
| `zoom` | `https://assets.zoom.us/docs/ipranges/ZoomMeetings.txt` |

- Downloaded lists are saved in `providers/auto/` (git-ignored) and **replace** the bundled file for that provider. Delete a file there to go back to the bundled list.
- A download is only used if every line is a valid range, it isn't empty, and it isn't less than half the size of the previous download. Otherwise the log explains why and the current list stays in use. Network errors are logged as warnings and also keep the current list.
- The Microsoft web service needs a client ID; a random one is created once and kept in `providers/auto/update-state.json`.
- Set `Enabled` to `false` to use only the bundled files.

Long vendor lists can be merged into fewer ranges with the helper script (needs Python and `pip install -r requirements.txt`):

```powershell
python .\consolidate-ips.py --input "D:\Downloads\ZoomMeetings.txt"
```

## Installing (one click)

For everyday use, install the monitor as a Windows service plus the tray app with the installer.

**1. Build a release** (on the PC with the source, from the repository root):

```powershell
.\build-release.ps1
```

It first runs pre-flight checks (Windows, .NET 10 SDK and runtimes, a NuGet source, package restore, all `Microsoft.Extensions.*` packages on 10.x, no known-vulnerable packages) and stops with the reason if one fails. `-CheckOnly` runs just the checks; `-SkipChecks` skips them. It then runs the tests, publishes both apps and creates `release\RtcCallMonitor-<version>\` and `release\RtcCallMonitor-<version>.zip`. Your `appsettings.Local.json` is never put into a release.

**2. Install or upgrade:** double-click **`Install.cmd`** in that folder (or in the extracted zip). It asks for administrator rights once (UAC), then:

- checks for the .NET 10 Desktop Runtime and offers to install it with winget if it's missing;
- stops the running service and tray app (upgrade);
- copies the files to `C:\Program Files\RtcCallMonitor` (tray in `tray\`), **keeping** your `appsettings.Local.json`, the downloaded `providers\auto` lists and the logs;
- on a first install creates `appsettings.Local.json` from the example and opens it in Notepad for your MQTT/webhook details;
- creates or updates the **RTC Call Monitor** service: automatic (delayed) start, restarts itself 1 minute after a crash;
- starts the service and shows the last log lines;
- starts the tray app and sets it to start when you sign in.

Running `Install.cmd` from a newer release upgrades in place. Options for `installer\Install.ps1`: `-InstallDir <folder>`, `-NoTray`. A log of the elevated part is written to `%TEMP%\RtcCallMonitor-install.log`.

**Uninstall:** double-click **`Uninstall.cmd`**. It removes the service, the tray's sign-in entry and the program folder, and asks whether to delete your settings and logs. If you keep them, the settings are saved to `%ProgramData%\RtcCallMonitor\appsettings.Local.json.bak` and the next install restores them.

**Changing settings later:** edit `C:\Program Files\RtcCallMonitor\appsettings.Local.json` (open the editor as administrator, or use the tray's **Open config folder**), then **Restart monitor** in the tray menu.

- The service runs as *Local System*, which has the rights needed for packet capture.
- Warnings and errors also go to the Windows **Event Viewer** (Windows Logs → Application, source *RtcCallMonitor*).

## Logs

Everything the console shows is also written to a log file:

```text
C:\ProgramData\RtcCallMonitor\logs\rtc-call-monitor-20261123.log
```

- One file per day; a file that reaches `MaxFileSizeMB` continues in `..._001.log`. The last `RetainedDays` files are kept.
- Levels come from the `Logging` section, so `"RtcCallMonitor": "Debug"` adds the debug lines to the file too.
- Watch it live: `Get-Content C:\ProgramData\RtcCallMonitor\logs\rtc-call-monitor-*.log -Tail 20 -Wait`
- Warnings and errors also appear in Event Viewer when running as a service.

```json
"LogFile": {
  "Enabled": true,
  "Directory": "",
  "RetainedDays": 14,
  "MaxFileSizeMB": 10
}
```

`Directory` empty means `%ProgramData%\RtcCallMonitor\logs`; environment variables such as `%LOCALAPPDATA%` work. If the folder can't be written, the app logs a warning and carries on without the file.

## Tray app

`RtcCallMonitor.Tray` is a small helper in the notification area. It talks to the running monitor (service or `dotnet run`) over a local named pipe; closing it doesn't stop detection.

| Icon | Meaning |
|---|---|
| Green dot | Watching, no call |
| Red dot | On a call (tooltip shows provider and minutes) |
| Amber with ‖ | Detection paused |
| Orange with ! | Watching, but something needs attention (MQTT not connected, no network connection captured, no output enabled) |
| Grey ring | Monitor not running |

Left-click shows the status (connections captured, MQTT/webhook). The right-click menu has:

- **Pause detection** for 15 minutes, 1 hour, 4 hours or until resumed, and **Resume detection**. Pausing ends a call that was already reported; Home Assistant gets a **Detection paused** entity (`binary_sensor.<device>_detection_paused`). A call still going when you resume is reported again.
- **Open log file**, **Open log folder**, **Open config folder** (selects `appsettings.Local.json`), **Open README**.
- **Restart monitor**: restarts the Windows service so settings changes take effect (asks for administrator rights). Only available when the monitor is installed as a service.
- **Start with Windows**: starts the tray app when you sign in (for your user only, no admin needed).

The installer sets it up (see [Installing](#installing-one-click)). To run it from the source instead: `dotnet run --project tray\RtcCallMonitor.Tray`. Only one copy runs per user.

The pipe (`\\.\pipe\RtcCallMonitor`) only accepts connections from users signed in to this PC, not from the network.

## Troubleshooting

### Call not detected

The call's IP address is probably not in any provider file. Turn on debug logging in `appsettings.Local.json`:

```json
"Logging": { "LogLevel": { "RtcCallMonitor": "Debug" } }
```

During a call the log then shows the busiest unknown addresses:

```text
Unmapped network 66.77.89.91 count 122
```

Add the network to the right provider file (for example `66.77.0.0/16` in `providers/zoom.txt`, or `66.77.89.91/32` in a new `providers/3cx.txt` for your own PBX) and restart.

### MQTT: "not authorised"

- Use the Home Assistant user's **Username**, which can differ from its **Display name** (check under Settings → People → Users).
- The Mosquitto add-on caches failed logins: after fixing the password, **restart the Mosquitto broker add-on**, then the monitor.
- In Home Assistant, the Supervisor log shows `Successful login` / `Unauthorized login for '<user>'` for each attempt.

### Other messages

| Message | Meaning |
|---|---|
| `Neither Webhook nor Mqtt is enabled` | Calls are only logged; enable an output |
| `provider list zoom could not be downloaded: ...; keeping the current list` | No internet, or the vendor's site is down; detection carries on with the current list |
| `provider files have 1 problem(s); still using the previous ranges` | A provider file you edited has a mistake; fix it and save again |
| `'... ' is not a network address, using ...` | A range in a provider file has host bits set; it still works, but the file should be corrected |
| `Packet capture currently requires Windows` | Running on Linux/macOS (or in Docker), which isn't supported |
| `no network address to capture on` | Offline, or `Interfaces` / `ExcludeInterfaces` leave no connection; check the `capturing on` lines |
| `cannot capture on Wi-Fi (2001:...)` | IPv6 capture isn't available on that connection; IPv4 still works. Set `CaptureIPv6` to `false` to hide this |
| `An attempt was made to access a socket in a way forbidden` | Not running as administrator |

## Development

```text
src/
  CallDetector.cs           start/end logic (no dependencies, unit-tested)
  NetworkListener.cs        raw socket capture, one socket per address
  Capture/                  IPv4/IPv6 packet parsing, interface selection, packet filter
  LogFile.cs                rolling log file (Serilog)
  MonitorState.cs           call and pause state
  Ipc/                      named pipe for the tray app (shared message contract)
tray/RtcCallMonitor.Tray    WinForms tray app
  TrafficMonitor.cs         ties capture to the detector
  ProviderCatalog.cs        loads providers/*.txt (+ auto/), IP → provider lookup, live reload
  ProviderUpdates/          Teams/Zoom list downloads, file watcher
  NetworkParser.cs          CIDR parsing
  Configuration/            settings classes, validation, old-config translation
  Notifications/            webhook and MQTT outputs, HA discovery
  providers/                IP ranges per provider
tests/RtcCallMonitor.Tests  xUnit tests
```

- Build and test: `dotnet test` (no administrator rights needed)
- CI: GitHub Actions builds and tests every push and pull request to `main`
- The `Dockerfile` is kept from the original project, but the image can't capture traffic because capture is Windows-only.

## Credits

Original project by [ericgla](https://github.com/ericgla/RTC-Call-Monitor), with IP range updates from its contributors.
