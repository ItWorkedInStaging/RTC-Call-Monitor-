# RTC-Call-Monitor

## About

RTC-Call-Monitor is a tool to detect an active UDP based voice/video call by:

- Monitoring the rate of incoming/outgoing UDP packets from your machine's IP address.
- Checking the source/destination address against known network blocks for the major voice/video conference providers.

When an active call is detected, it reports the start and end of the call over MQTT (with Home Assistant auto-discovery) and/or webhooks.

### Supported Providers

- [Zoom](https://assets.zoom.us/docs/ipranges/ZoomMeetings.txt)
- [Microsoft Teams](https://learn.microsoft.com/en-us/microsoft-365/enterprise/urls-and-ip-address-ranges?view=o365-worldwide#skype-for-business-online-and-microsoft-teams)
  - ID: 11, Category: Optimize Required
- WebEx
- Slack
- Google Meet
- GoTo Meeting

Additional providers can be added by dropping a file into the `providers` folder. See _Providers_ below.

Consolidating subnet ranges (optional): `python .\consolidate-ips.py --input "D:\Downloads\ZoomMeetings.txt"`

## Getting Started

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Configuration files

All settings live in the `src` folder:

| File | Committed to git | Purpose |
|---|---|---|
| `appsettings.json` | yes | Default settings |
| `appsettings.Local.json` | **no** (git-ignored) | Your own values and secrets; overrides `appsettings.json` |
| `appsettings.Local.example.json` | yes | Starting point: copy it to `appsettings.Local.json` |
| `providers/<name>.txt` | yes | IP ranges per call provider |

Settings are read in this order, later ones winning: `appsettings.json` → `appsettings.{Environment}.json` → `appsettings.Local.json` → environment variables starting with `RTCMON_` (use `__` between levels, e.g. `RTCMON_Mqtt__Password`) → command-line arguments.

The configuration is checked at startup. If something is wrong, the app lists every problem and exits instead of failing later, for example:

```text
crit: Configuration has 2 problem(s); fix them and start again:
crit:   - Mqtt.Enabled is true but Mqtt.Host is empty.
crit:   - providers/zoom.txt line 12: '3.7.35/25' is not a valid range (expected e.g. 3.7.35.0/25)
```

Configs from older versions (`Application` / `Provider` sections) still work; a warning at startup asks you to move them to the new sections.

### Minimal Configuration

Copy `src/appsettings.Local.example.json` to `src/appsettings.Local.json` and set `Detection.LocalNetworks` to your local network in [CIDR notation](https://www.digitalocean.com/community/tutorials/understanding-ip-addresses-subnets-and-cidr-notation-for-networking).

Most users will have a network configuration similar to the following:

```bash
  IP Address:  192.168.1.100
  Subnet Mask: 255.255.255.0
```

The configuration will be

```json
"Detection": { "LocalNetworks": [ "192.168.1.0/24" ] }
```

Then switch on at least one output: `Mqtt` or `Webhook` (see below). With neither enabled, calls are only written to the log.

### Build/Run

RTC-Call-Monitor requires that your console has elevated privileges in order to monitor network traffic.

Windows: Open a new Powershell or command prompt using _Run as Administrator_

```bash
cd src
dotnet run
```

Run the tests from the repository root (no elevation needed):

```bash
dotnet test
```

Linux/macOS: not currently supported. Packet capture uses `SIO_RCVALL`, which only exists on Windows, so the app exits with an error on other platforms.

## Settings

### Detection

| Setting | Default | Meaning |
|---|---|---|
| `LocalNetworks` | – | Your local subnets; traffic inside them is ignored |
| `CheckIntervalMs` | `1000` | Length of each measurement interval, in milliseconds |
| `MinPacketRate` | `1` | UDP packets per interval to or from a provider IP that count as call traffic |
| `CallStartIntervals` | `2` | Consecutive intervals with call traffic before a call is reported as started |
| `CallEndIntervals` | `3` | Consecutive intervals without call traffic before a call is reported as ended |

The end delay stops short network hiccups from ending a call and starting it again. While a call is active, traffic to any IP of the same provider keeps it alive, so a provider moving the call to another media server doesn't end it.

### MQTT and Home Assistant

```json
"Mqtt": {
  "Enabled": true,
  "Host": "homeassistant.local",
  "Port": 1883,
  "Username": "rtc-monitor",
  "Password": "...",
  "UseTls": false,
  "AllowUntrustedCertificates": false,
  "DeviceName": "",
  "BaseTopic": "rtc-call-monitor",
  "DiscoveryPrefix": "homeassistant"
}
```

- `DeviceName` defaults to the computer name. It becomes the device name in Home Assistant and part of the topics.
- With the Home Assistant **Mosquitto broker** add-on, logins are Home Assistant users: create a dedicated user (e.g. `rtc-monitor`) for this app.
- `UseTls` connects with TLS (usually port `8883`); `AllowUntrustedCertificates` accepts a self-signed broker certificate.

Home Assistant discovers a device named after `DeviceName` with three entities (for a device called `josh-pc`):

| Entity | Value |
|---|---|
| `binary_sensor.josh_pc_on_call` | `on` during a call; attributes `provider`, `ip`, `started` |
| `sensor.josh_pc_call_provider` | `msteams`, `zoom`, ... or `none` |
| `sensor.josh_pc_last_call_duration` | Length of the last call, in seconds |

All entities become *unavailable* when the app stops or loses its connection (Last Will), so Home Assistant never gets stuck showing a call.

Topics (all retained):

```text
rtc-call-monitor/<device>/state          ON | OFF
rtc-call-monitor/<device>/provider       msteams | zoom | ... | none
rtc-call-monitor/<device>/attributes     {"provider":"msteams","ip":"52.112.1.2","started":"2026-11-23T00:40:00Z"}
rtc-call-monitor/<device>/last_duration  1834
rtc-call-monitor/<device>/availability   online | offline
```

Discovery is published at startup and again whenever Home Assistant restarts (`homeassistant/status` = `online`). If the broker is unreachable, the app retries with increasing delays (5 s up to 60 s) and re-sends everything once connected.

### Webhooks

```json
"Webhook": {
  "Enabled": true,
  "CallStartUrl": "http://homeassistant.local:8123/api/webhook/call-started",
  "CallEndUrl": "http://homeassistant.local:8123/api/webhook/call-ended",
  "Headers": { "Authorization": "Bearer ..." }
}
```

All webhooks are HTTP POST requests with a JSON body. Either URL may be left empty.

Call start:

```json
{
  "provider": "slack"
}
```

Call end:

```json
{
  "provider": "slack",
  "duration": 123
}
```

`duration` is the call length in seconds. `Headers` are added to every request. Webhook failures (timeouts after 10 s, non-2xx responses) are logged as warnings.

MQTT and webhooks can be enabled together.

### Providers

Each file in `src/providers` is one provider; the file name is the provider name (`zoom.txt` → `zoom`). One CIDR range per line; blank lines and anything after `#` are ignored:

```text
# zoom.txt - source: https://assets.zoom.us/docs/ipranges/ZoomMeetings.txt
3.7.35.0/25
3.21.137.128/25
```

To add a provider, add a new file. A different folder can be set with `Providers.Directory`, and extra ranges can be given in config with `Providers.Inline` (`{ "zoom": [ "203.0.113.0/24" ] }`).

#### Adding new network blocks

If the application is not detecting the start of your call, the most likely reason is that the IP address is not in a known network block for your provider. To see the destination address of your call provider, enable debug logging.

In _appsettings.Local.json:_
`"Logging": { "LogLevel": { "RtcCallMonitor": "Debug" } }`

In the console a message similar to the following will show:

`Unmapped network 66.77.89.91 count 122`

Add the network block to the provider's file, e.g. `66.77.0.0/16` in `providers/zoom.txt`, then restart the application and your call should be detected.
