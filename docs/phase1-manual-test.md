# Phase 1 manual test checklist

Automated tests cover the logic (131 tests), and everything was exercised against the simulator from the command line.
This list is what only a person at the screen, or a real controller, can check. Phase 1 is done when part A passes
and the gate test in part B is complete.

Tester: \***\*\_\_\_\_\*\*** Date: \***\*\_\_\_\_\*\*** Build/commit: \***\*\_\_\_\_\*\***

## Setup (part A)

1. Terminal 1: `dotnet run --project src/BACprobe.Cli -- simulate --devices 3 --no-rpm --bbmd` (leave running; Ctrl+C to stop)
2. Terminal 2: `dotnet run --project src/BACprobe.App`
3. Simulator devices are 1001, 1002 and 1003 (1003 refuses ReadPropertyMultiple, like an older device). The fake BBMD is at `<your PC IP>:47809`.

For anything that fails, write down the exact message or screen and what you clicked. Leave out real site names and addresses.

## A. At your desk, with the simulator

### Connect and scan

- [ x] Adapter list defaults to your physical adapter; pre-flight shows ✔ / ⚠ items with readable help text
- [ x] Scan finds 3 devices; 1003 still shows name, vendor, model and firmware
- [x ] Instance range 1002 to 1002 finds only device 1002
- [x ] Bad range input (`abc`, or high below low) shows a clear message and does not scan
- [ x] First run: the Windows Firewall prompt (if any) makes sense, and dismissing it leaves a usable message

### Browse

- [ x] Selecting a device fills the object grid with values and units (°F, %, Active/Inactive)
- [ ] Selecting an object fills the properties panel, including a readable Priority Array line
- [x ] Switching devices quickly never shows one device's objects under another
- [ x] Status bar messages are readable and not cut off

### Writes (most important)

- [ x] Override panel appears for Damper Position, Zone Setpoint, Fan Command, Occupied; **not** for Zone Temp, Fan Status
- [ x] Write 25 at priority 8: confirmation names the device, the point, the current value and "priority 8 (Manual Operator)"
- [x ] After Yes: grid value and priority array update (slot 8 shows 25)
- [ x] Answering No writes nothing; status says "Cancelled. Nothing was written."
- [ x] `abc` on an analog point, `maybe` on a binary point: message appears, no dialog
- [ x] Priority 1 or 2 adds the life-safety warning to the dialog
- [ x] Binary write (`on` / `off`) works on Fan Command
- [ x] "Release my override" clears the slot; priority array shows no overrides
- [ x] Write log expander lists every attempt (OK and failed); `%LOCALAPPDATA%\BACprobe\write-log.txt` matches
- [ x] "N override(s) in place" counter is correct after writes and releases
- [ x] Leave an override, close the window: prompt lists it. **Yes** releases it, **No** leaves it, **Cancel** keeps the window open
- [ x] Leave an override, click Scan again: the same prompt appears
- [ x] With no overrides, the window closes immediately with no prompt

### BBMD

- [ x] BBMD `<your PC IP>:47809`: status says registered; Scan still finds the devices
- [ x] Restart the simulator with `--bbmd-refuse`: status says refused, with cause and next step
- [ x] Wrong port (for example `:47999`): "did not answer", with cause and next step; the scan still runs locally
- [ x] `abc` or `bbmd.example.com`: readable error, no scan

### Export

- [ x] Export device… saves .xlsx, .csv and EDE (pick from the dialog's file-type list)
- [ x] **Open the .xlsx in real Excel:** no repair prompt, bold header, filter works, °F displays correctly, Devices sheet present
- [ x] Open the .csv in Excel: accented/degree characters display correctly
- [ x] Save over a file that is open in Excel: friendly message, no crash
- [ x] Export all… produces one file containing all 3 devices
- [ x] Status bar reports the point and device counts

### Job files

- [ x] Type a job name and notes; Save job… writes a .bacprobe file
- [ x] Close the app, reopen it, Open job…: orange "saved job… snapxshot, not live" banner appears
- [ x] Devices, points, notes and write history all come back
- [ x] Offline: the properties panel says offline; writes are unavailable; Export device… still works
- [ x] Scan after opening a job goes live and the banner disappears
- [ x] Open job… on a random file renamed to .bacprobe: readable error, app still usable

### General

- [x ] Window is usable at about 1024 px wide; text is readable in both light and dark Windows themes
- [x ] Nothing crashes when the simulator is stopped mid-use; you get a timeout message with a next step
- [ x] Closing the app leaves no stray `bacprobe` / `BACprobe.App` processes

## B. Real hardware (the actual Phase 1 gate)

Use a point that is safe to command. Record the controller make and model for every result.

- [ ] **Discovery:** the real controllers all appear. Note any that do not (vendor and model)
- [ ] **Large object list:** a controller with hundreds of points loads without errors, timeouts or missing objects. Note how long it takes
- [ ] **Vendor-specific** objects and properties show as "vendor-specific" with raw values; nothing is hidden and nothing crashes
- [ ] **Real write and release** on a safe point: accepted; priority array matches; release clears it
- [ ] **Refused write** (an input, or a locked point): the message makes sense to a tech
- [ ] **Real BBMD:** registration works and devices on another subnet appear. Also try a BBMD that refuses you. Note the BBMD make
- [ ] **Behind a router** (for example MS/TP behind an IP router): devices appear and can be read
- [ ] **EDE:** load the exported EDE file into the tool that will consume it; record what it rejects or ignores (the layout is unverified)
- [ ] **Other BACnet tools open** (YABE or a vendor workbench): both tools work; the port warning is accurate
- [ ] **The gate test:** run `docs/gate-test-plan.md` with someone who has never used BACprobe. Pass means they find and safely override a point, verify it, and release it, unaided

## Result

Part A: pass / fail (list failures below) Part B: pass / fail (list failures below)

Failures and notes:

Phase 1 sign-off (both parts pass): \***\*\_\_\_\_\*\*** Date: \***\*\_\_\_\_\*\***

