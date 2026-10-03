# BACprobe — project guide for Claude

BACprobe is a free, open-source (MIT) Windows BACnet explorer for building-automation technicians.
It must be easy for a brand-new BMS tech and fast for an experienced one.
Full research and plan: https://claude.ai/code/artifact/9a36dbdb-bbd9-4b84-9254-080340b72c75

## Decisions (settled — don't relitigate)
- Community tool, MIT license, published at https://github.com/lsink/BACprobe. No BTL listing.
- Windows only. C# on .NET 10, WPF UI (Fluent theme via `ThemeMode`; suppress WPF0001 if needed).
- Solo developer (Larry, a BAS/BACnet field tech). Keep each phase small and shippable.
- Standard BACnet objects only. Proprietary objects/properties show as "vendor-specific" with raw values.
- Schedules and calendars are deliberately NOT planned: Larry's field experience is that a BACnet troubleshooting tool does not need them. Parked on the back burner; do not propose them again unless asked.
- Field sites mostly run MS/TP at 38.4k or 76.8k, so active MS/TP master over an FTDI USB-RS485 adapter is in scope (Phase 3). Warn at 19.2k and below.

## Solution layout
- `src/BACprobe.Core` (net10.0): networking pre-flight, BACnet session/discovery. No UI code.
- `src/BACprobe.Cli` (net10.0 exe): test harness, e.g. `bacprobe adapters`, `bacprobe preflight`, `bacprobe discover`.
- `src/BACprobe.App` (net10.0-windows, WPF, CommunityToolkit.Mvvm).
- `tests/BACprobe.Core.Tests` (xunit): pure logic only (subnet math, pre-flight rules).
- Central package versions in `Directory.Packages.props`; shared settings in `Directory.Build.props`.

## BACnet library notes (NuGet `BACnet` 4.0.0, namespace `System.IO.BACnet`)
- Source: https://github.com/ela-compil/BACnet (tag v4.0.0). Check signatures there before guessing.
- Transport: `new BacnetIpUdpProtocolTransport(port, useExclusivePort: false, localEndpointIp: "<nic ip>")`.
  Always pass the selected NIC's IP; with several NICs and no IP, `Start()` throws.
  Non-exclusive mode binds 0.0.0.0:47808 with ReuseAddress (shared, for broadcasts) plus an ephemeral
  unicast port on the NIC, so it can coexist with other BACnet tools on the same PC.
  With a NIC IP set, Who-Is goes to that subnet's directed broadcast.
- Client: `new BacnetClient(transport, timeoutMs, retries)`, then `Start()`. `Dispose()` when done.
- Discovery: `client.OnIam += (sender, adr, deviceId, maxApdu, segmentation, vendorId) => ...`; `client.WhoIs(low, high)` (-1 = no limit).
- Reads: `ReadPropertyAsync(adr, BacnetObjectId, BacnetPropertyIds, ...)` returns `IList<BacnetValue>`.
  `ReadPropertyMultipleAsync(adr, BacnetObjectId, IList<BacnetPropertyReference>, ...)` returns `IList<BacnetReadAccessResult>`
  (fields `objectIdentifier`, `values`; each `BacnetPropertyValue` has `property`, `value`).
  A per-property error comes back as a `BacnetValue` with tag `BACNET_APPLICATION_TAG_ERROR`.
- Errors and timeouts are thrown as plain `System.Exception` ("Error from device: …", "Wait Timeout"); `TimeoutException` after retries on async calls.
- Writes: `WritePropertyAsync(adr, objectId, propertyId, values, priority: …)`.
- MS/TP: `SerialTransport.Mstp(...)` from the separate `BACnet.Serial` package (Phase 2/3).

## Phase 1 scope (current): BACnet/IP explorer MVP
1. Connect wizard with pre-flight checks: adapter up, valid IPv4 (not 169.254), UDP 47808 bindable,
   name the process holding 47808 (Windows `GetExtendedUdpTable`), warn on virtual adapters and duplicate subnets.
2. Discovery: Who-Is (optional instance range), collect I-Am, then enrich each device
   (object-name, vendor-name, model-name, firmware) with ReadPropertyMultiple, falling back to ReadProperty.
3. Device tree, object list, property read; writes with a plain-English priority picker and a write log.
4. Foreign-device registration to a BBMD; CSV/Excel/EDE export; job files (SQLite).
Gate: a new tech finds and safely overrides a point with no help.

## Guardrails (product rules)
- Never write to a device without a confirmation that names the priority in plain English (8 = Manual Operator).
- Log every write; before disconnecting, list overrides this session left in place and offer to release them.
- Every error shown to the user gets a likely cause and a next step.
- Fully local: no cloud calls, no telemetry.

## Working agreements
- Build with `dotnet build BACprobe.slnx`; run tests with `dotnet test`.
- Keep Core free of WPF references so it stays testable.
- Commit messages: short imperative subject line.

## Phase 1 status (all 4 scope items built; only the gate test remains)
Everything below was verified against `bacprobe simulate` (fake devices + fake BBMD) only. No real device has been tried yet.
The gate ("a new tech finds and safely overrides a point with no help") is described in `docs/gate-test-plan.md`.

Core layout (`src/BACprobe.Core`): `Networking` (adapters, pre-flight rules, port owner), `Discovery` (Who-Is, enrich),
`Browsing` (object list, properties, `BacnetNames` formatting), `Writing` (confirmation text, write log, override tracker),
`Bbmd` (foreign-device registration), `Export` (CSV/xlsx/EDE), `Jobs` (SQLite `.bacprobe`), `Simulation`.
CLI commands: adapters, preflight, discover, objects, read, write, release, export, job (save|show), simulate.

Phase 2 started: live values. `Live/LiveWatcher` keeps a device current with COV (`CovSession`: unconfirmed subscriptions, renewed at half the lifetime, cancelled on exit) and polls points the device refuses (`DeviceBrowser.RefreshValuesAsync`), plus a 30 s safety poll (notifications can be lost, devices forget subscriptions on restart, and COV does not carry priority arrays). App toggle + `bacprobe watch`. The simulator supports COV (`--no-cov` makes the last device refuse, `--cov-limit n` caps subscriptions) and drifts analog inputs (`--still` stops that). Subscription process ids come from one process-wide counter: the device keys on (subscriber, id, object).

Trend logs: `Trends/` (`TrendLogReader` reads settings + pages through records by position with ReadRange; `TrendRecordDecoder`; `TrendStats`; `TrendExporter`). App: "View trend..." opens `TrendWindow` (custom `TrendChart` control, no chart library). The simulator has two trend logs (a record every 10 s, 300/120 records of history) and answers ReadRange in small pages to force paging.

Learned the hard way:
- BBMD registration is confirmed via `transport.Bvlc.MessageReceived` (BVLC-Result); the client method alone gives no feedback. Registrations must be renewed (done at TTL/2).
- `BacnetClient.WritePropertyAsync` takes `byte?` priority.
- ClosedXML's `SaveAs(path)` rejects non-.xlsx extensions; save to a stream (the exporter writes a temp file first).
- SQLite: use `Pooling=False`, or the job file stays locked after Dispose.
- Text from devices is untrusted: parameterised SQL always, and neutralise leading `= + - @` in CSV.
- WPF projects do not implicitly import `System.IO`.
- `Services.DecodeLogRecord` decodes ONE record per call and returns the bytes it used: loop on it. BACnet log timestamps have 1/100 s precision and no time zone.
- `ReadRangeAsync` returns only bytes + item count (no MORE_ITEMS flag): page by position using the log's Record_Count. Never ask a log object for PROP_ALL (it can drag in Log_Buffer); name the properties.
- Log_Interval is in hundredths of a second (0 = logs on change).
- Do not `BasedOn="{StaticResource {x:Type ...}}"` a WPF control style here: it resolved to the light classic theme. Use element styles/converters instead.
- EDE layout was written from memory of the common format and is unverified against a real consumer.
