# BACprobe

A free, open-source BACnet explorer for Windows, built for building-automation technicians.
New techs get guided setup and plain-English diagnostics; experienced techs get fast discovery,
raw properties and packet-level detail.

**Status:** Phase 1 (BACnet/IP) feature-complete, **tested against simulated devices only**.
It has not yet been run against real controllers. Expect rough edges, and please report what you find.

## What it does today
- **Connect wizard with pre-flight checks** - adapter up, valid IPv4 (not 169.254), UDP 47808 free, names the program
  holding the port, warns about virtual adapters and two adapters on one subnet. Every problem comes with a likely cause and a next step.
- **Discovery** - Who-Is (optionally an instance range), then reads name, vendor, model and firmware for each device.
- **Object browser** - object list with names, values and units; every property of an object. Vendor-specific
  properties and object types are shown as "vendor-specific" with raw values, never hidden.
- **Live values** - switch on "Live values" and the point list keeps itself up to date, so you can watch a damper move, see a write take effect, or catch someone else's override appear. It asks the device to push changes (COV) and polls only the points or devices that refuse; changed values flash blue. Subscriptions are renewed automatically, and a slow safety poll catches anything missed.
- **Override indicator** - points held at manual-operator priority (8) or higher show in orange with the priority, whoever set the override.
- **Trend logs** - select a trend log and open it to see its recorded history as a chart (with hover read-out) and a table, for the latest 100, 500, 2000 or all records. Shows what the log records, how often, and whether it is switched off or has overwritten old data. Save the records to Excel (real dates and numbers, so Excel can chart them) or CSV.
- **Safe writes** - a confirmation in plain English that names the priority (8 = Manual Operator is the default),
  a write log, and a prompt before you disconnect that lists the overrides you left in place and offers to release them.
- **BBMD foreign-device registration** - reach devices on other subnets; renews automatically and explains refusals and timeouts.
- **Export** - CSV, Excel (.xlsx) and EDE point lists. Text that came from devices is neutralised so a point named `=...` cannot run as a spreadsheet formula.
- **Job files** (`.bacprobe`, SQLite) - save a site visit (devices, points, notes, write history) and reopen it later, offline.
- **Simulator** - fake devices and a fake BBMD, so you can try everything with no hardware.

Fully local: no cloud calls, no telemetry.

## Not yet
- MS/TP (needs an FTDI USB-RS485 adapter; planned for a later phase)
- Trend Log Multiple and event logs. (Schedules and calendars are intentionally left out: this is a troubleshooting tool.)
- Writes to properties other than Present Value
- EDE state-text, limit and COV columns (left empty); the EDE layout has not been checked against a real EDE consumer

## Quick start
Requires Windows and the .NET 10 SDK.

```
dotnet build BACprobe.slnx
dotnet test

# desktop app
dotnet run --project src/BACprobe.App

# no hardware? start fake devices in one terminal...
dotnet run --project src/BACprobe.Cli -- simulate --devices 3 --bbmd
# ...then scan from the app, or from another terminal:
dotnet run --project src/BACprobe.Cli -- discover
```

## Command line
`bacprobe` (from `src/BACprobe.Cli`) is also a test harness and a scripting tool.

| Command | Purpose |
|---|---|
| `adapters` / `preflight` | List network adapters; run the pre-flight checks |
| `discover [--bbmd <ip>]` | Find devices |
| `objects --device <n>` | List a device's objects with values |
| `trend --device <n> --object tl:1 [--last 50 \| --all] [--out file.xlsx]` | Show a trend log and save its records |
| `watch --device <n> [--object ai:1] [--poll]` | Print a line whenever a value or override changes (COV where supported) |
| `read --device <n> --object ai:1 [--property present-value]` | Read all (or one) property of a point |
| `write` / `release --device <n> --object ao:1 --value 25 [--priority 8]` | Override a point / give it back (asks to confirm) |
| `export (--device <n> \| --all \| --job <file>) [--format csv\|xlsx\|ede]` | Save a point list |
| `job save --out site.bacprobe --all` / `job show site.bacprobe` | Save and browse a site visit |
| `simulate [--devices n] [--no-rpm] [--no-cov] [--cov-limit n] [--bbmd]` | Run fake devices (and a fake BBMD) |

Run `bacprobe --help` for every option. Write history is also appended to `%LOCALAPPDATA%\BACprobe\write-log.txt`.

## Building
Requires Windows, the .NET 10 SDK, and Visual Studio 2026 (".NET desktop development" workload) or VS Code.

## Contributing
Real-device reports are the most useful thing right now: which controller, what happened, and (if you can) a Wireshark capture.
Do not include real site data in issues; `.bacprobe` job files and captures are git-ignored for that reason.

## License
MIT. BACprobe uses these MIT-licensed libraries: [BACnet](https://github.com/ela-compil/BACnet) for .NET,
[ClosedXML](https://github.com/ClosedXML/ClosedXML), [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) and
[CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet).
"BACnet" is a registered trademark of ASHRAE; this project is not affiliated with ASHRAE or BACnet International.
