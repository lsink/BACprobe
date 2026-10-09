# Bench test: BACprobe against real controllers

Everything in BACprobe has so far been checked only against `bacprobe simulate`. This plan is the first contact with real hardware.
Do it on a **bench**, never on a customer's site: some steps restart or mute a controller, or change its setup.

Work top to bottom. Part 1 must pass before Part 2 (every change needs discovery and reads to work). Inside Part 2 the steps are in
order of risk, most dangerous first, so problems show up while you are paying the most attention. Each step says what to run, what
you should see, and how to undo it. Tick the box when it behaves; write down anything else in the results sheet at the end.

## Setup
- **Controllers:** 2 or 3 from **different vendors**, at least one BACnet/IP and one MS/TP behind a BACnet router. Bench power, no real
  equipment wired to outputs (a restart may drive outputs). Know each one's device password, if it has one.
- **The vendor's own tool** for each controller, to look at and undo anything BACprobe changes (recipient lists, settings, overrides).
- **Network:** a switch with only the laptop, the controllers and the router on it. No BBMD needed unless you test it (Part 3).
- **Laptop:** BACprobe built from `main` (`dotnet build BACprobe.slnx`); Wireshark running on the wired adapter with the filter
  `udp.port == 47808` during every Part 2 step (save the capture per step: it is the proof of what was actually sent).
- **For Part 4:** an FTDI-based USB-RS485 adapter and access to the MS/TP trunk's terminals.
- Throughout, `<ip>` is the laptop's wired address and `<n>` a device instance. Add `--adapter <ip>` to every command if the laptop has
  more than one adapter. Every change is logged in `%LOCALAPPDATA%\BACprobe\write-log.txt`: keep it with the results.
- Before you start, note on paper each controller's: device instance, Max APDU, segmentation, vendor, firmware, and one writable
  analog point, one binary point, one input with alarm limits set, and its Notification Class objects (from the vendor tool).

## Part 1 – Baseline: find and read (no changes)
| ✓ | Step | What to run | What you should see | If not |
|---|---|---|---|---|
| ☐ | 1.1 Pre-flight | `bacprobe preflight` | All green, or warnings that make sense (VPN adapters etc.) | Note the message; check the cause and next step it gives |
| ☐ | 1.2 Discover | `bacprobe discover` | Every IP controller, and the MS/TP ones behind the router (with their network number); vendor, model, firmware filled in; Response, Max APDU, Segments, Clock columns plausible | Missing devices: Who-Is range, router, firewall. Wrong names: note vendor and firmware |
| ☐ | 1.3 Network map | (same output) | The router and its network listed; no false duplicate warnings | Copy the network check text into the results |
| ☐ | 1.4 Object list | `bacprobe objects --device <n>` for each controller | All objects listed, names and values right (compare with the vendor tool) | Note how long it took and any "timeout"/"abort" |
| ☐ | 1.5 Big controller | `objects` on the controller with the most points | Complete list. The object list may come one entry at a time on a device that cannot send long answers: slow is fine, missing points are not | Note point count, Max APDU, segmentation and time taken |
| ☐ | 1.6 Properties | `bacprobe read --device <n> --object ai:1` (and an AO, BV, MSV) | All properties shown; vendor-specific ones marked; state names on binary and multi-state points | Note any property shown wrongly, with the vendor tool's value |
| ☐ | 1.7 Status | (app) Status column, Show filter = problems | Faults, alarms, out of service match the vendor tool | Note the point and both readings |
| ☐ | 1.8 Folders | `bacprobe objects --device <n> --tree` | If the controller has Structured Views: the same folders as the vendor tool | Note the vendor and what differs |

## Part 2 – Changes, most dangerous first
Watch Wireshark for each one. If a step leaves something in a bad state, undo it with the vendor tool before going on.

### 2.1 Mute and un-mute (DeviceCommunicationControl)
| ✓ | Step | What to run | What you should see | Undo / if not |
|---|---|---|---|---|
| ☐ | Mute for 1 min | `bacprobe mute --device <n> --minutes 1` (type the device number; add `--password` if it has one) | "muted for 1 minutes"; the device stops answering (`bacprobe read ...` times out); it **un-mutes by itself** after a minute | Power-cycle the controller if it never comes back. Note the vendor: this must be reported |
| ☐ | Mute, then un-mute | `mute --minutes 5`, then `bacprobe unmute --device <n> --address <its ip>` (the mute prints the exact command) | Answers again right after un-mute | Power-cycle; note whether un-mute was refused, timed out or ignored |
| ☐ | Wrong password | `mute ... --password wrong` | Refused with "wrong or missing password", nothing muted | Note the exact error text |
| ☐ | Exit prompt | (app) mute from the device card, then close the app | The close prompt lists the device as muted and offers to undo it; "Release them" un-mutes it | Un-mute from the CLI |
| ☐ | Send-only mute | `mute --initiation --minutes 2` | Still answers reads; stops sending COV (watch a point with `bacprobe watch`) and its own I-Am | `unmute` |

### 2.2 Restart (ReinitializeDevice)
| ✓ | Step | What to run | What you should see | Undo / if not |
|---|---|---|---|---|
| ☐ | Warm start | `bacprobe restart --device <n> --warm` (type the device number) | "accepted and is restarting"; the device drops off for a while, then answers again with its settings intact | Power-cycle if it never comes back |
| ☐ | Cold start | `restart --cold` on a controller you are happy to reset | Same, and overrides/values go back to their power-up defaults | Note what changed |
| ☐ | Password | `restart ... --password wrong`, then the right one | First refused with the password message, second accepted | Note the error text and whether the device needs a password at all |

### 2.3 Live alarms (BACprobe on a Notification Class recipient list)
| ✓ | Step | What to run | What you should see | Undo / if not |
|---|---|---|---|---|
| ☐ | Join | `bacprobe alarms --device <n> --listen --minutes 5` | Lists the Notification Class objects, asks, then "Listening". In the vendor tool the recipient list has a new entry: BACprobe's IP and port, process 1112539137 | Remove the entry in the vendor tool |
| ☐ | Alarm arrives | While listening, make an input go into alarm (change its High Limit, or force the sensor) | A line prints within seconds: "device n AI x: Normal -> High limit" | Note whether nothing arrives (Wireshark: did the device send a notification, and to which port?) |
| ☐ | Fault arrives | Unplug the sensor of a point with fault reporting | A "-> Fault" line (or "alarm changed" if the device uses an event type the library cannot read) | Note the event type from Wireshark |
| ☐ | Leave | Wait for the 5 minutes, or Ctrl+C | "Done"; the vendor tool's recipient list is back to what it was | **Remove the entry by hand** and note the error BACprobe gave |
| ☐ | App | Alarms window → Live..., trigger an alarm, close the window | The list updates by itself; closing removes the entry | As above |
| ☐ | Full list | Join a controller whose recipient list is already full (if any) | A clear "no room for another recipient" message | Note the error text |

### 2.4 Acknowledge alarms
| ✓ | Step | What to run | What you should see | Undo / if not |
|---|---|---|---|---|
| ☐ | List | `bacprobe alarms` | Every active and unacknowledged alarm, with times that match the vendor tool | Note any missing alarm, and whether the device answered GetEventInformation or the fallback note appeared |
| ☐ | Still active | `alarms --device <n> --ack ai:x` on an alarm still active | "Acknowledged"; it stays listed, now acknowledged; the vendor tool/front-end shows it acknowledged by "BACprobe (user)" | Note the error |
| ☐ | Already cleared | Clear the alarm, then `--ack` it | Both transitions (to alarm, to normal) acknowledged; it leaves the list. **This is the uncertain one**: BACprobe sends "off-normal" for the cleared to-alarm transition | If the device refuses with "invalid event state", note vendor and firmware: BACprobe may need to send the original alarm state |

### 2.5 Settings (WriteProperty without a priority)
| ✓ | Step | What to run | What you should see | Undo / if not |
|---|---|---|---|---|
| ☐ | A limit | `bacprobe write --device <n> --object ai:x --property high-limit --value 80` | Confirmation shows old and new; "device accepted it; it now reads 80"; the vendor tool agrees | Write the old value back (it is in the write log) |
| ☐ | Text | `--property description --value "Bench test"` | Same | Write the old text back |
| ☐ | COV increment | `--property cov-increment --value 0.5` | Same | Write the old value back |
| ☐ | Read-only property | `--property status-flags --value 0` | Refused before anything is sent ("read-only") | – |
| ☐ | Refused by device | A property the device protects (e.g. object-type, or one its tool says is read-only) | A refusal with a likely cause and next step | Note the exact error |
| ☐ | App | Select a property, change it in the box under the properties | Same as the CLI; disabled in read-only mode | – |

### 2.6 Overrides and Out of service (the original Phase 1 writes)
| ✓ | Step | What to run | What you should see | Undo / if not |
|---|---|---|---|---|
| ☐ | Override | (app) Damper or other AO → write at priority 8 | Value changes; priority array shows slot 8; orange in the list | Release |
| ☐ | Ineffective write | Write at 8 to a point held at a higher priority by the controller's program | The write explainer says why nothing changed | Release |
| ☐ | Out of service | Take an input out of service, then back | Flag changes; exit prompt offers to put it back | Put back in service |
| ☐ | Exit prompt | Leave one override, close the app | Listed; "Release them" releases it | Release in the vendor tool |

### 2.7 Clock
| ✓ | Step | What to run | What you should see | Undo / if not |
|---|---|---|---|---|
| ☐ | Set clock | Set a controller's clock 10 minutes wrong in its tool, then `bacprobe clock --device <n>` | "device clock is now within a minute of this PC" | Set it in the vendor tool |
| ☐ | UTC | `clock --utc` on a controller with a UTC offset configured | Clock right in local time | Note if it lands hours off (time zone / UTC offset setup) |

## Part 3 – Reading under real conditions
| ✓ | Step | What to run | What you should see | If not |
|---|---|---|---|---|
| ☐ | 3.1 Segmentation | `bacprobe export --all` | All points of all controllers; the "cannot send long answers in pieces" note lists only devices that really cannot (segmentation none/receive) | Note devices that time out or abort, with Max APDU and segmentation |
| ☐ | 3.2 Parallel reads | Same, with the MS/TP controllers behind the router | The output says how many devices at a time; MS/TP devices still complete; the trunk is not overloaded (Part 4 monitor running at the same time, if you have the adapter) | Note any MS/TP timeouts that do not happen with one device at a time (`export --device <n>`) |
| ☐ | 3.3 Routed reads | `objects`, `read`, `write` on a device behind the router | Works like a local device | Note the error and the router vendor |
| ☐ | 3.4 COV | `bacprobe watch --device <n>` and the app's Live values | Changes arrive by COV; devices that refuse COV are polled; nothing stops after the subscription lifetime | Note whether renewals fail (Wireshark) |
| ☐ | 3.5 Trend logs | `bacprobe trend --device <n> --object tl:1 --all` | All records, times right, matching the vendor tool | Note record counts that differ |
| ☐ | 3.6 Who-Has | `bacprobe who-has "<exact point name>"` and `who-has ai:1` | Every controller with that point answers, including those behind the router | Note which devices stay silent (they may not support Who-Has) |
| ☐ | 3.7 Health | (same discover output) | Slow-answer and clock notes are believable | Note false alarms |
| ☐ | 3.8 BBMD (optional) | `bacprobe bbmd <bbmd ip>` and `discover --bbmd <bbmd ip>` | Tables read; registration works; remote devices found | Note the BBMD vendor |
| ☐ | 3.9 Exports | Open the xlsx in Excel; load the EDE file into whatever tool you use EDE with | Opens cleanly; EDE accepted (the EDE layout was written from memory) | Note what the other tool rejects |
| ☐ | 3.10 Wireshark filter | Right-click a point → Copy Wireshark filter; paste it into Wireshark | Filters to that point (the field names were written from memory) | Note the right field names |

## Part 4 – MS/TP through the USB adapter
Listening first; joining only once listening is clean.

| ✓ | Step | What to run | What you should see | If not |
|---|---|---|---|---|
| ☐ | 4.1 Adapter | `bacprobe mstp-monitor --list` | The adapter's COM port; FTDI latency timer shown (2 ms or less is good) | Note what it shows for latency (the registry path is from memory) |
| ☐ | 4.2 Baud | `mstp-monitor --port COMn --baud auto` | Detects the trunk's baud rate | Note the real rate and what it guessed |
| ☐ | 4.3 Listen | `mstp-monitor --port COMn --baud 38400 --seconds 60 --record bench.bin --pcap bench.pcap` | Every master listed, token passing, few or no damaged frames, believable loop time | Open `bench.pcap` in Wireshark: frames should decode with good CRCs |
| ☐ | 4.4 Fault finding | Unplug one controller, then swap A/B on one, while listening | "never takes the token", then damaged frames pinned on that node | Note what was reported for each |
| ☐ | 4.5 Join | `bacprobe mstp-discover --port COMn --baud 38400` (type JOIN) | Survey passes, a free MAC chosen, devices on the trunk found | Note why it refused, or what broke on the trunk |
| ☐ | 4.6 Through MS/TP | `objects --device <n> --mstp COMn --baud 38400` | Reads work through the trunk | Note timeouts |
| ☐ | 4.7 Leave | Ctrl+C / close | The ring recovers within seconds (watch with the monitor from another adapter if you have one) | Note how long the trunk took to settle |

## Part 5 – The app end to end
| ✓ | Step | What to do | What you should see |
|---|---|---|---|
| ☐ | 5.1 Gate dry run | Follow `docs/gate-test-plan.md` yourself on the bench | Scan, find the point, override at 8, see it, release, exit, with no scrolling and no hunting |
| ☐ | 5.2 Laptop screen | Run on the smallest laptop you take to site | Every window fits; nothing cut off |
| ☐ | 5.3 Light and dark | View > Theme, both | Everything readable, selected rows included |
| ☐ | 5.4 Job file | Save a job, close, open it offline | Devices, points, notes, watch list and write log are back |

## Results
For each failing or surprising step: step number, controller vendor / model / firmware, the exact BACprobe message (copy it), what the
vendor tool showed instead, and the Wireshark capture for that step. Keep the write log from `%LOCALAPPDATA%\BACprobe\write-log.txt`.

| Step | Controller (vendor, model, firmware) | What happened | Expected | Capture file |
|---|---|---|---|---|
| | | | | |

When Part 1 and Part 2 pass on every controller, BACprobe is ready for the Phase 1 gate test with a new tech (`docs/gate-test-plan.md`).
