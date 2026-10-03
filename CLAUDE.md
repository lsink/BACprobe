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
   name the process holding 47808 (Windows `GetExtendedUdpTable`), warn on virtual adapters and duplicate subnets,
   and warn when Windows Firewall would drop incoming UDP 47808 (`FirewallInspector` + `PreflightRules.CheckFirewall`; never a Fail).
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

Network check: `DiscoveryService` keeps EVERY I-Am (`Heard`), not just the first per device number, because a duplicate would otherwise be invisible; `NetworkCheck.Analyze` finds conflicts and `NetworkCheck.Compare` diffs against a saved job (IP only: a changed port is not a move). Simulator: `--dup`, `--unassigned`.

Point search: `Search/PointSearch` (pure; all words must match, name hits rank first, `is:overridden`). App: `FindWindow` over the same index as Export all (`_pointCache`); `GoToPointAsync` selects the device, waits for its objects to load, then the point. CLI: `bacprobe find`.

Routers / network map: `AddressInfo` (the library reports a device behind a router as the ROUTER's address (net 0) plus `RoutedSource` holding the real network and MAC; always go through `AddressInfo`, never `adr.net`); an I-Am-Router-To-Network body is big-endian uint16 network numbers; `DiscoveryService.AskForRouters` sends Who-Is-Router; `NetworkMapBuilder` is pure. `SimulatedRouter` (`--router "1001:3,1002:0;1001:1"`) makes routed devices that are discoverable but NOT readable. Known gap: job files do not store `RoutedSource`, so a reloaded job cannot talk to a routed device (jobs are offline anyway).

Write explainer: `DeviceWriter.ExecuteAsync` returns `WriteOutcome.Explanation` (a `PromptContent`). After an accepted write it re-reads the point for up to 3 s (`PointProber`) and `WriteExplainer.ExplainIneffective` names why nothing changed; after a refusal it probes once and `ExplainFailure` explains. Pure logic in `WriteExplainer`; simulator can misbehave via `--stuck` (bv:1 held at priority 5) and `--protected` (av:1 refuses); the damper (ao:1) enforces 0-100. Unverified against real hardware.

State text: `Browsing/StateText` (pure) labels multi-state values with State_Text ("Standby (3)") and binary values with Inactive/Active_Text ("Open (Active)"). `ObjectSummary.StateNames` holds the names (binary as [inactive, active]); `PresentValue` stays raw so live updates and COV compare as before, and `DisplayValue`/`ValueText`/`PriorityArrayText` add the names. Read with the summaries; saved in job files (schema 3, `objects.state_texts`). App: one state dropdown for binary and named multi-state points; CLI `write --value Standby`. Simulator sample devices have MSV/MSO/MSI points.

Live trend (temporary): `Trends/LiveTrend` keeps one point's samples in memory (PC clock, capped at a day of 1 s samples, "no answer" gaps recorded); `LiveTrendSampler` reads through its own copy of the point via `RefreshValuesAsync`, so it never touches the main window's summaries. App: "Trend live..." next to the properties heading opens `LiveTrendWindow` (one point per window, interval 1-60 s, pause, clear, export with `pcClock: true`). Polling only, no COV. Closing the window stops the requests and discards the samples.

Problem points: `Browsing/PointHealth` (pure) decodes Status_Flags (`BacnetBitString.ConvertToInt()` gives `BacnetStatusFlags`) and Reliability, and words each problem with a likely cause and next step. Summaries and live polls read Status_Flags (COV notifications carry it too); Reliability is read only for points in fault (`FillReliabilityAsync`; 0 = asked, no reason given). `ObjectSummary.HasProblem`/`ProblemText`/`ProblemTooltip`. Find filters `is:fault|alarm|oos|problem|overridden` (combinable, plurals accepted). App: Status column, Show filter above the object list (re-applied on live changes), footer counts problems. Export: Status column. Job files schema 4 (`status_flags`, `reliability`). Simulator `--faults`: AI 2 open loop at -40, AI 1 in alarm, BV 1 out of service.

BBMD check: `Bbmd/BvlcTables` encodes Read-BDT/Read-FDT and decodes the acks by hand (10-byte entries; NAK = BVLC-Result 0x0020/0x0040); `BbmdTableReader` uses its own UDP socket on the adapter (port 0), so no scan or BACnet connection is needed; `BbmdCheck` (pure) judges: no answer, refused (not a BBMD), empty / only itself, not listing itself, duplicates, one-hop masks, two BBMDs on this PC's subnet, and each peer's own table (one-way = Problem, differing lists, silent or refusing peers). App: "Check BBMD..." next to the BBMD box; CLI `bacprobe bbmd <ip[:port]>`. Simulator: its BBMD answers both reads; `--bbmd-peer` adds a mismatched second BBMD on the next port.

Read-only mode, quick-copy, Out_Of_Service (checklist items 22, 24, 2): "Read-only" checkbox at the top of the app (remembered via `%LOCALAPPDATA%\BACprobe\read-only.flag`) shows a banner, hides the override and Out_Of_Service panels and makes `DoWriteAsync` refuse; the CLI is not affected. Right-click a device or point to copy address, object ID, name, value or a Wireshark filter (`Browsing/QuickCopy`, pure; the filter field names `bacapp.objectType` / `bacapp.instance_number` are from memory, unverified). Out_Of_Service: `DeviceWriter.SetOutOfServiceAsync` with `OutOfServiceRequest` and the same confirmation dialog and write log; turning it on is tracked as a `TrackedOverride` with priority 0 (`OutOfServicePriority`) so leaving offers to put the point back, and priority 0 prints as no priority in the log. It only toggles the flag; writing Present_Value of an input while out of service is not built (the simulator still refuses it). No CLI command yet.

Learned the hard way:
- `BacnetClient.MaxSegments` defaults to MAX_SEG0 ("send me nothing bigger than one packet"): big object lists and PROP_ALL
  then abort. `DiscoveryService` sets MAX_SEG65. Never ask the device object for PROP_ALL either (it drags in Object_List); `DeviceBrowser` names its properties.
- After 3 timeouts in a row (`BacnetFailure.MaxTimeoutsInARow`) a device counts as not answering: reads, COV subscribe/renew stop
  instead of waiting out every request. Summary batches fit the device's max APDU when it cannot segment; an abort halves the batch.
- Windows Firewall: read it BEFORE anything binds 47808. Binding makes Windows show "allow this app?" and add a temporary
  block rule for the exe while it waits, which looks exactly like a refusal. Rules are per exe path, so every build folder
  gets its own. Read via late-bound `HNetCfg.FwPolicy2` (indexed properties take ONE profile bit, not the combined mask).
- Network category: `NetworkListManager` late-bound, but `GetAdapterId` returns a GUID that late binding cannot carry;
  cast to the `[ComImport]` `INetworkConnection` in `FirewallInspector`. An unidentified network counts as Public.
- Simulator test aids: `--objects n` (big controller, needs segmentation), `--outage after,seconds` (silent, then back with COV
  subscriptions forgotten, like a restart; its port stays the same, unlike restarting `simulate`).
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
