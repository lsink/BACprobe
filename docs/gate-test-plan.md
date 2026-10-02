# Phase 1 gate test: "a new tech finds and safely overrides a point with no help"

Phase 1 is done when someone who has never used BACprobe can do the task below **unaided**, without overriding the
wrong thing and without leaving an override behind. Your job as the observer is to stay quiet and take notes.

## Setup
- A Windows PC with BACprobe built (`dotnet build BACprobe.slnx`).
- Devices to practise on, one of:
  - **Simulator (safe, anywhere):** run `dotnet run --project src/BACprobe.Cli -- simulate --devices 3 --bbmd` in a terminal and leave it running.
  - **Real site:** a controller you are allowed to override, ideally with a point that is safe to command (a spare output, or a setpoint).
- The tester should be a new BMS tech, or someone who knows the basics of BACnet but has never seen this tool.

## The task (read this to the tester; give no other help)
> You are at a building and need to override one point to a value you choose, check that it took effect, and put it back.
> 1. Connect to the building network and find the controllers.
> 2. Find the point called **Damper Position** (or the point your lead chose) on controller **1002** (or the one your lead chose).
> 3. Override it to a new value.
> 4. Confirm the value changed.
> 5. Put it back and close the program.

## What counts as passing
- [ ] They reached a device list without help.
- [ ] They found the right point on the right device.
- [ ] The override went in at a sensible priority (8, Manual Operator) and they could say, roughly, what that means.
- [ ] They verified the new value (grid or properties panel).
- [ ] They released the override, **or** the exit prompt caught it and they released it there.
- [ ] They never wrote to a point they did not intend to.

## Things to watch for (write down the exact moment, not your interpretation)
- Where they paused for more than ~10 seconds, and what they said or looked at.
- Any message they could not act on. (Every error is supposed to give a likely cause and next step.)
- Whether they read the confirmation dialog or just clicked through it.
- Whether the priority list confused them; whether they picked something other than 8, and why.
- Whether they noticed the "overrides in place" counter and the write log.
- Anything they tried that the app did not support.

## If a problem appears
Record it, then check the likely causes below before blaming the tester.
| Symptom | Check |
|---|---|
| No devices found | Wrong adapter selected; pre-flight warning ignored; Windows Firewall prompt dismissed; devices on another subnet (needs a BBMD address) |
| Port 47808 warning | Another BACnet tool is open; close it |
| Writes are refused | The point is an input, or not commandable; the error text should say so |
| Override left behind after a crash | Run `bacprobe release --device <n> --object <type:n> --priority 8`, then check `%LOCALAPPDATA%\BACprobe\write-log.txt` |

## Results
Date / tester role / environment (simulator or site) / pass or fail / list of stumbling points. Open an issue for each stumbling point
with the exact message or screen, **without** real site names, addresses or point names if the site is not yours to share.
