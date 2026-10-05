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

Read-only mode, quick-copy, Out_Of_Service (checklist items 22, 24, 2): "Read-only" checkbox at the top of the app (ON by default; unticking is remembered as `%LOCALAPPDATA%\BACprobe\read-write.flag`, and an unreadable setting means read-only) shows a banner, hides the override and Out_Of_Service panels and makes `DoWriteAsync` refuse; the CLI is not affected. Right-click a device or point to copy address, object ID, name, value or a Wireshark filter (`Browsing/QuickCopy`, pure; the filter field names `bacapp.objectType` / `bacapp.instance_number` are from memory, unverified). Out_Of_Service: `DeviceWriter.SetOutOfServiceAsync` with `OutOfServiceRequest` and the same confirmation dialog and write log; turning it on is tracked as a `TrackedOverride` with priority 0 (`OutOfServicePriority`) so leaving offers to put the point back, and priority 0 prints as no priority in the log. It only toggles the flag; writing Present_Value of an input while out of service is not built (the simulator still refuses it). No CLI command yet.

MS/TP passive monitor (Phase 2, first item of the MS/TP diagnostics list): `Mstp/MstpFrames` (frame record, `MstpCrc` per Annex G, `MstpFrameParser` stream parser that resyncs after noise and skips a data-CRC-damaged frame by its vouched length), `Mstp/MstpBusAnalyzer` (pure: per-MAC stats, token pass taken/not taken, token loop time, utilisation, highest polled MAC; `Findings()` with likely cause and next step: silence, wrong baud, CRC error rate, node never taking the token, Max Master too low, busy trunk, slow loop), `Mstp/MstpSampleCapture` (made-up bus: dead MAC 7, MAC 9 above the polls, noise). CLI `bacprobe mstp-monitor --port COMn --baud 38400 [--frames] [--record f]`, `--replay f` (a raw recording has no clock, so `TimingKnown=false` drops load and loop findings), `--list`, `--make-sample f`. Receive-only: it never transmits. Verified only against synthetic bytes and the CRC residuals (0x55 / 0xF0B8), NOT against a real adapter or trunk. `Mstp/MstpAdapterChecks`: `MstpBaudDetector` (pure; `--baud auto` listens 3 s at 9600/19200/38400/76800/115200 and picks the clear winner, or explains why not) and `FtdiLatency` (reads the driver's LatencyTimer from `HKLM\...\Enum\FTDIBUS`, flags over 2 ms; shown by `--list` and before a live listen; detect only, the fix is manual in Device Manager because writing HKLM needs admin). A baud of 19200 or less gets an Info finding. Both are untested against real hardware (registry layout is from memory). WPF window: main window button "MS/TP monitor..." opens `MstpWindow` (`MstpViewModel`): port and baud pickers (Auto-detect = `MstpPortCapture.DetectBaud`), Start/Stop, nodes table (double-click filters the frame list to that MAC), findings, live frame list (MAC filter, hide token/polls, pause), FTDI latency banner, Open/Save capture (live runs record to `%LOCALAPPDATA%\BACprobe\mstp\capture-*.bin`, never cleaned up yet), Demo (the made-up trunk, so it works with no adapter). `Mstp/MstpMonitor` is the thread-safe session (parser + analyzer + 2000-line log + `Snapshot()`); `MstpMonitor.FromRecording` replays a capture. The serial path in the window is untested on real hardware; Demo and recordings were checked by eye in the running app. Follow-ups: per-node "pickup" time (how long a node takes to start transmitting after being passed the token; avg 15 ms or more is flagged, the standard skips a master after 20 ms; skipped for recordings), and "turn errors": damaged frames arriving straight after a node was given its turn (token or poll), counted once per burst. A node with 20% or more of its turns damaged gets "MAC n's transmissions arrive damaged" (Problem at 50%), whose causes are a duplicate MAC, a bad drop or missing bias/termination, or A/B swapped, and whose next step is to unplug that node and listen again. This is the evidence-tied wiring hint, and also the only duplicate-MAC signal (there is no direct duplicate detection: two devices on one MAC are only visible as collisions). The generic "often misses the token" finding is suppressed when damage explains it. pcap export: `MstpPcap` writes link type 165 (BACNET_MS_TP) with the full wire frame including the 0x55 0xFF preamble, rebuilt from decoded frames (damaged frames are not included); `MstpMonitor.ExportPcap` (last 300000 frames), the window's "Export for Wireshark..." button, and CLI `--pcap`. VERIFIED with Wireshark 4.x `tshark` on the sample capture: every frame decodes as BACnet MS/TP (polls, tokens, ReadProperty APDUs), header and data CRCs show "correct / Checksum Good", and nothing is malformed. That also confirms our CRC code and frame encoding against an independent implementation. Still not built: moving the CLI onto `MstpMonitor`, and a real adapter/trunk test. Wireshark's MS/TP view is the cross-check once an adapter is available.

Device health (checklist item 3): `Discovery/DeviceHealth.Check` (pure) adds to the network check, in the app and in `discover`: slow answer (1 s or more), devices that cannot receive segmented requests (one Info line), tiny Max_APDU (under 206), clock off by 5 min or more, duplicate device names. `EnrichOneAsync` times its first ReadPropertyMultiple (`DiscoveredDevice.ResponseTime`, which includes retries and 8-way parallel load, so it overstates a lone device) and reads Local_Date/Local_Time in the same call (`ClockSkew` = device clock minus PC clock, both local time; a device in another time zone shows hours of skew and the wording says so). Device grid has Response, Max APDU, Segments and Clock columns. Simulator: `--skew <minutes>` on the first device. Job files do not store these yet.

Session notes (checklist item 20): `Jobs/SessionNotes` (pure; a note per device or per point, trimmed, capped at 4000 chars, empty deletes it). Job files are schema 5 (`notes` table; device note stored under object type -1; older files load with none); `JobSnapshot.Notes`/`AllNotes`. App: a Note box under the properties for the selected point, or the device when no point is selected, saved with Save job and restored by Open job (notes are local, never sent to a device, and work in read-only mode). CLI: `job note <file> --device n [--object ai:1] --text "..."` (empty text removes), `job show` lists notes, re-saving a job with `job save --force` keeps them. Notes are not in exports or Find yet. The main window's page `MinHeight` is 960: the scan row wraps to two lines, and below that height the grid area overflowed and the footer drew over the Note box.

Compare (checklist item 4): `Browsing/Comparison` (pure) lines two devices' points up by object type and instance (the two Device objects are matched as "the device"), flags different values, points only on one side, and renamed points; live inputs (AI/BI/MSI/pulse/accumulator) are marked and set aside by default because they differ by nature. `CompareProperties` does one object's properties by name. CLI `bacprobe compare --device a --with b [--object av:1] [--all] [--inputs]` (exit 5 = differences found). App: main window "Compare..." opens `CompareWindow` (A/B pickers, Differences only, Set aside live inputs; selecting a row compares that point's properties on both; only while connected, a saved job compares its saved points). Simulator `--differ` changes the second device's Zone Setpoint to 68 and adds AV 900. Values are compared as the display text, so 72 vs 72.0 would show as different.

Watch lists (checklist item 23): `Live/WatchList` (pure; ordered, a point once, capped at 200, kept with the job as schema 6 `watch` table with the point's label so a saved job can show it offline), `Live/WatchSession` (reads every entry once, in parallel across devices, then runs one `LiveWatcher` per device: COV where supported, polling for the rest; per-device status and failure text). App: "Watch" button next to the properties heading adds or removes the selected point; main window "Watch list (n)..." opens `WatchWindow` (device, object, name, value, status, last updated, per-device live status; Remove, Clear all, Refresh). Offline (a saved job) it shows the saved list with saved values. Reads only. `job show` lists the watch list. Verified live against the simulator (3 points on 2 devices by COV); `WatchSession` itself has no automated test because the existing tests do not use real UDP. No CLI command to edit the list.

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
