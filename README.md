# RTC-Call-Monitor

Detects when you are on a voice or video call (Teams, Zoom, Google Meet, Webex and others) by watching your PC's network traffic, and tells Home Assistant (over MQTT) or any other system (over webhooks) when a call starts and ends.

Typical uses: an "On Air" light outside your door, pausing music, muting speakers, or setting a status while you're on a call.

This is a maintained fork of [ericgla/RTC-Call-Monitor](https://github.com/ericgla/RTC-Call-Monitor). See [CHANGELOG.md](CHANGELOG.md) for what has changed.

## How it works

Call audio and video travel over UDP to the provider's media servers. Every second the monitor:

1. Counts UDP packets between your PC and each remote IP address (traffic inside your local network is ignored).
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

2. **Create your settings file**: copy `src\appsettings.Local.example.json` to `src\appsettings.Local.json` (this file is git-ignored) and set at least:

   ```json
   {
     "Detection": { "LocalNetworks": [ "192.168.1.0/24" ] },
     "Mqtt": {
       "Enabled": true,
       "Host": "192.168.1.10",
       "Username": "rtc-monitor",
       "Password": "your-password"
     }
   }
   ```

   `LocalNetworks` is your home/office subnet in [CIDR notation](https://www.digitalocean.com/community/tutorials/understanding-ip-addresses-subnets-and-cidr-notation-for-networking): for IP `192.168.1.100` with mask `255.255.255.0` it is `192.168.1.0/24`.

3. **Run it** from a terminal opened with *Run as administrator*:

   ```powershell
   cd src
   dotnet run
   ```

   You should see the providers load and, with MQTT enabled, `MQTT connected to ...`. Join a call: after about 2 seconds the log shows `call started for msteams ...`.

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
| `LocalNetworks` | – | Your local subnets; traffic inside them is ignored |
| `CheckIntervalMs` | `1000` | Length of each measurement interval, in milliseconds |
| `MinPacketRate` | `1` | UDP packets per interval to/from a provider IP that count as call traffic |
| `CallStartIntervals` | `2` | Busy intervals in a row before a call is reported as started |
| `CallEndIntervals` | `3` | Quiet intervals in a row before a call is reported as ended |

While a call is active, traffic to *any* IP of the same provider keeps it alive, so the provider moving your call to another server doesn't end it.

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

## Running as a Windows service

To start the monitor automatically with Windows:

```powershell
# from the repository root, in an administrator PowerShell 7 window
dotnet publish src -c Release -r win-x64 --self-contained false -o publish
copy install.ps1 publish\
cd publish
.\install.ps1
Start-Service "RTC Call Monitor"
```

- `install.ps1` creates (or recreates) the **RTC Call Monitor** service with automatic start. It uses `Remove-Service`, which needs PowerShell 7 (`winget install Microsoft.PowerShell`).
- The service runs as *Local System* by default, which has the rights needed for packet capture.
- The service reads its settings from the `publish` folder. `appsettings.Local.json` is copied there by `dotnet publish` if it exists in `src`; after changing settings, publish again or edit the copy in `publish`, then restart the service.
- Warnings and errors from the service are written to the Windows **Event Viewer** (Windows Logs → Application, source *RtcCallMonitor*). To see everything, run it with `dotnet run` instead.

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
| `An attempt was made to access a socket in a way forbidden` | Not running as administrator |

## Development

```text
src/
  CallDetector.cs           start/end logic (no dependencies, unit-tested)
  NetworkListener.cs        raw socket capture
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
