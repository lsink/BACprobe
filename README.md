# BACprobe

A free, open-source BACnet explorer for Windows, built for building-automation technicians.
New techs get guided setup and plain-English diagnostics; experienced techs get fast discovery,
raw properties and packet-level detail.

**Status:** early development (Phase 1, BACnet/IP).

## Planned features
- Connect wizard with pre-flight checks (network adapter, firewall, UDP 47808 conflicts, BBMD)
- Device discovery across routed networks, object browsing, read/write with a priority-array view
- Safe writes: plain-English priority picker, write log, "release my overrides" check
- Point-list export (CSV, Excel, EDE)
- MS/TP trunk diagnostics over an FTDI USB-RS485 adapter (later phase)

## Building
Requires Windows, the .NET 10 SDK, and Visual Studio 2026 (".NET desktop development" workload) or VS Code.

```
dotnet build BACprobe.slnx
dotnet test
```

## License
MIT. BACprobe uses the MIT-licensed [BACnet](https://github.com/ela-compil/BACnet) library for .NET.
"BACnet" is a registered trademark of ASHRAE; this project is not affiliated with ASHRAE or BACnet International.
