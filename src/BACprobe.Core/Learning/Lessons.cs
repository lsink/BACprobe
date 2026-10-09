using System.Text.RegularExpressions;
using BACprobe.Core.Discovery;

namespace BACprobe.Core.Learning;

/// <summary>A short, plain-English lesson. Paragraphs are shown in order; a paragraph starting "- " is a bullet.</summary>
public sealed record Lesson(string Id, string Title, string Summary, IReadOnlyList<string> Paragraphs);

/// <summary>
/// Short built-in lessons, and the matching of a finding to the lesson that explains it (checklist item 25).
/// Pure. Findings are matched by their title, so a lesson link survives wording changes in the detail text.
/// </summary>
public static class Lessons
{
    public static IReadOnlyList<Lesson> All { get; } =
    [
        new("device-numbers", "Device numbers and duplicates",
            "Every BACnet device needs its own device number on the whole internetwork.",
            [
                "A device number (also called device instance) is the unique ID in a device's Device object. It must be different for every device on the whole BACnet internetwork, not just on one subnet or one trunk.",
                "When two devices share a number, replies get mixed up: a read can come from either one, names and points seem to change by themselves, and workbenches show one device flapping between two addresses.",
                "4194303 is reserved to mean \"any device\" and must never be assigned. A device still on it has not been given a number.",
                "- Find the two devices by their addresses (BACprobe lists both).",
                "- Change the number on the one that was added most recently, using its own configuration tool.",
                "- Check again with a fresh scan. Controllers that bind points by device number may need their references updated.",
            ]),
        new("duplicate-mac", "Two devices on one address",
            "Two devices that answer from the same address cannot both work.",
            [
                "On MS/TP every master and slave needs a unique MAC (0-127 for masters). Two devices on the same MAC collide whenever both transmit, which shows up as damaged frames and missed tokens.",
                "On IP, two devices with the same IP address make the PC's ARP cache flip between them, so the same address answers sometimes from one device and sometimes from the other.",
                "- Look at the address switches or DIP settings on each device on that network.",
                "- On IP, find the second device with its MAC address from the PC's ARP table (arp -a) while only one is plugged in at a time.",
            ]),
        new("bbmd", "BBMDs and broadcasts",
            "A BBMD carries broadcasts (such as Who-Is) between IP subnets. Every BBMD must list every other one.",
            [
                "Broadcasts do not cross routers. A BBMD (BACnet Broadcast Management Device) on each subnet forwards them to its peers so devices on different subnets can find each other.",
                "Each BBMD keeps a Broadcast Distribution Table (BDT) listing every BBMD, itself included. The tables should match on every BBMD. If A lists B but B does not list A, broadcasts only get through in one direction (\"one-way\"), which is a classic reason a device can see another but not be seen.",
                "The mask on each entry decides how a forwarded broadcast is delivered. 255.255.255.255 means the peer BBMD re-broadcasts it on its own subnet (two-hop). 255.255.255.0 or similar means the sender relies on the router to deliver it directly (one-hop), which only works if the router allows directed broadcasts.",
                "Only one BBMD belongs on each subnet. Two of them make every broadcast arrive twice, and in bad cases loop.",
                "- Read the table from each BBMD and compare. Add the missing entries on the BBMD that lacks them.",
                "- Prefer two-hop entries (mask 255.255.255.255) unless you know the router forwards directed broadcasts.",
            ]),
        new("foreign-device", "Foreign devices",
            "A foreign device registers with a BBMD to take part from a subnet that has no BBMD of its own.",
            [
                "A PC on a subnet with no BBMD can register with a BBMD elsewhere. The BBMD then forwards broadcasts to it and relays what the PC broadcasts. This is how BACprobe reaches devices on other subnets from a laptop.",
                "The BBMD keeps a Foreign Device Table (FDT). Entries expire after their time-to-live unless the PC registers again, so a PC that stops renewing quietly drops off.",
                "A BBMD may refuse foreign devices (the setting is often off by default), or the device you pointed at may not be a BBMD at all.",
                "- Check the address and port you entered really belong to a BBMD (BACprobe's BBMD check shows this).",
                "- Enable foreign device registration on that BBMD, then register again.",
            ]),
        new("routers", "Routers and network numbers",
            "Each BACnet network has one number, and routers connect them.",
            [
                "A BACnet router joins two networks, for example the IP network and an MS/TP trunk behind it. Every network on the internetwork needs its own number (1-65534) that is the same on every router that touches it.",
                "Two different networks with the same number, or one network with different numbers on two routers, send traffic to the wrong place or nowhere. Two routers announcing the same network is fine only if they really are two doors into the same network and agree on the number.",
                "- Compare the network numbers each router is configured with.",
                "- A network found with no router announcing it often means the router answers Who-Is but not Who-Is-Router-To-Network, or the device is reachable only because it sits on the same subnet.",
            ]),
        new("slow-device", "Slow devices, message size and segmentation",
            "A device that answers slowly, or only accepts small messages, makes everything that talks to it slower.",
            [
                "BACprobe times a simple read. A second or more usually means the device is busy (a heavy program, many subscribers), the network between you is lossy and retrying, or the device sits at the end of a slow MS/TP trunk.",
                "Max APDU is the largest message a device accepts. MS/TP devices commonly accept 480 bytes and IP devices 1476. A very small value forces many small requests. Segmentation lets a device send or receive a message in pieces; a device that cannot send its answers in pieces can only be read a few points at a time. One that only cannot receive pieces reads normally, because BACprobe's requests are small.",
                "- Re-check at a quiet time to see whether it is load or the network.",
                "- Read fewer properties per request from that device.",
            ]),
        new("clock", "Device clocks",
            "A device clock that is off makes trend logs and alarm times misleading.",
            [
                "Trend records and alarm timestamps come from the device's own clock. If it is hours or days off, you cannot line its history up with anything else, and time-based schedules fire at the wrong time.",
                "Devices lose time when the battery is dead or a power loss reset it, or when nothing sends them the time. BACnet has a time-synchronisation service, and many sites use one device as the time master.",
                "- Check the site's time-sync setup and fix the source rather than setting each device by hand.",
                "- If one device is wrong and the rest are fine, look at its battery and its time-sync setting.",
            ]),
        new("device-names", "Duplicate device names",
            "Names are for people; two devices with the same name cause mix-ups.",
            [
                "Object names inside one device must be unique, and by convention device names should be unique on the whole site. Two devices with the same name look identical in a workbench and in graphics.",
                "It usually comes from copying a device's configuration to a spare without renaming it.",
                "- Rename one of them. Device numbers stay as they are.",
            ]),
        new("saved-job", "Comparing with a saved job",
            "A saved job is a snapshot. Differences from it are changes, not necessarily faults.",
            [
                "When you open a saved job and scan, BACprobe lists devices that did not answer, moved to a different address, were renamed, changed firmware or model, or are new.",
                "Most of these are normal after work on site. They matter when you did not expect them: a device that moved may have been given a new IP address by DHCP; one that vanished may simply be switched off.",
                "- Anything that should not have changed: find out who changed it and when.",
            ]),
        new("mstp-token", "The MS/TP token",
            "On MS/TP, only the device holding the token may speak. Everything slow comes back to the token.",
            [
                "MS/TP is a shared two-wire bus. Masters pass a token in address order; only the holder may send, then it passes the token on. Each master also polls for new masters (Poll For Master) so devices can join.",
                "A master that is passed the token but does not use it within 20 ms is skipped. A master that is often skipped, or never takes the token, is either not powered, not listening (wrong baud rate), or its transmissions arrive damaged.",
                "Max Master is a setting on each master that limits how high an address it polls for. If a master has an address above the Max Master of the others, it is never found and never gets the token.",
                "A long time for the token to go round the ring (the token loop time) means every master waits that long for its turn. Many masters, slow devices holding the token, or lost tokens all lengthen it.",
                "- Make sure Max Master is the same, and not lower than the highest address in use, on every master.",
                "- Keep the number of masters reasonable and use a higher baud rate if every device supports it.",
            ]),
        new("mstp-wiring", "MS/TP wiring faults",
            "Damaged frames, retries and lost tokens usually mean a wiring problem, not a software one.",
            [
                "MS/TP runs on RS-485 over a daisy-chained pair (plus shield). It needs a chain, not a star; a termination resistor at each physical end only (not in the middle); and bias (one place only) so an idle bus has a defined level.",
                "Typical faults: missing or extra termination, a spur or star wiring, A and B swapped on one device, a broken shield or a shield grounded at both ends, and two devices on the same MAC.",
                "BACprobe ties the symptom to the evidence: if the damaged frames follow one node's transmissions, that node, its wiring drop or its address is the suspect. If errors appear everywhere, suspect the trunk itself, termination or the baud rate.",
                "- Unplug the suspect node and listen again; if the errors go away, the problem is at that node or its drop.",
                "- Check the ends of the trunk for termination and that only one place provides bias.",
            ]),
        new("mstp-baud", "Baud rate and the USB adapter",
            "Every device on an MS/TP trunk must run at the same baud rate; most sites use 38400 or 76800.",
            [
                "A listener at the wrong baud rate sees noise: bytes arrive but no valid frames. BACprobe's auto-detect listens at the common rates and picks the one that produces clean frames.",
                "Rates of 19200 and below leave little room on a busy trunk, so BACprobe warns about them.",
                "USB-to-RS-485 adapters based on FTDI chips have a latency timer that defaults to 16 ms; MS/TP needs about 1-2 ms, because the token handling is time critical. A high setting makes a transmitting master miss its turn. It is changed in Device Manager (port properties, Advanced).",
                "- Set the latency timer to 1 ms, then unplug and replug the adapter.",
                "- Confirm the rate with the trunk's other devices' settings rather than guessing.",
            ]),
        new("priority-array", "The priority array and overrides",
            "A commandable point has 16 priorities; the lowest-numbered one with a value wins.",
            [
                "A commandable point (output, value) holds up to 16 values, one per priority. The point uses the lowest-numbered priority that has a value, and falls back to its Relinquish Default when all are empty (null).",
                "Priority 8 is Manual Operator, the right one for a person overriding a point. Priority 6 is reserved for minimum on/off times, and 1 and 2 are life-safety, so tools should not write them.",
                "If you write at 8 and a program holds priority 5, your write is accepted but the point does not change; the higher priority still wins. BACprobe names the holder when this happens.",
                "An override stays until it is released (written as null at the same priority). Always release what you overrode; BACprobe lists leftovers when you leave.",
            ]),
        new("out-of-service", "Out of service",
            "Out_Of_Service disconnects a point from its hardware, so you can set a value by hand.",
            [
                "With Out_Of_Service true, the device stops updating Present_Value from the real input or output. It holds whatever was written. It is used to test the logic that reads the point.",
                "The point stays that way until someone turns it off, which is why it is dangerous: the point looks alive but is frozen, and the building can run on a stale value for months.",
                "BACprobe tracks it with your overrides and offers to put the point back in service when you leave.",
            ]),
    ];

    public static Lesson? Find(string id) => All.FirstOrDefault(l => l.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    // The first rule that matches the finding's title wins, so specific rules go before general ones.
    private static readonly (Regex Pattern, string LessonId)[] Rules =
    [
        (R(@"^Device number \d+ is used by"), "device-numbers"),
        (R(@"still has the unassigned device number"), "device-numbers"),
        (R(@"answered as several devices"), "duplicate-mac"),
        (R(@"share the same MAC address"), "duplicate-mac"),
        (R(@"is in the job but did not answer|has moved$|was renamed$|has different firmware or model|is new$"), "saved-job"),
        (R(@"^Two devices are both named"), "device-names"),
        (R(@"is slow to answer|tiny maximum message size|cannot send long answers"), "slow-device"),
        (R(@"clock is off"), "clock"),
        (R(@"is announced by \d+ routers|no router announced it|announces network \d+, but no devices"), "routers"),
        (R(@"foreign devices|^This PC is registered with"), "foreign-device"),
        (R(@"^BBMD |broadcast table|is listed \d+ times|one-hop|Two BBMDs on|Peer .*BBMD|One-way|acting as a BBMD|lists only itself"), "bbmd"),
        (R(@"USB adapter's latency timer"), "mstp-baud"),
        (R(@"baud|The trunk is \d+% busy"), "mstp-baud"),
        (R(@"Heard bytes but no valid frames"), "mstp-baud"),
        (R(@"damaged|CRC|transmissions arrive"), "mstp-wiring"),
        (R(@"Heard nothing on the trunk|stray bytes"), "mstp-wiring"),
        (R(@"token|Max Master|No master heard|Only one master|No free master|Not enough MS/TP traffic|MAC \d+ is above"), "mstp-token"),
    ];

    private static Regex R(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The lesson that explains this finding, or null when none fits.</summary>
    public static Lesson? ForFinding(NetworkFinding finding)
    {
        foreach (var (pattern, id) in Rules)
            if (pattern.IsMatch(finding.Title)) return Find(id);
        return null;
    }

    /// <summary>The whole lesson as plain text, for the command line.</summary>
    public static string ToText(Lesson lesson)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(lesson.Title).AppendLine(new string('-', lesson.Title.Length)).AppendLine(lesson.Summary).AppendLine();
        for (var i = 0; i < lesson.Paragraphs.Count; i++)
        {
            var p = lesson.Paragraphs[i];
            var bullet = p.StartsWith("- ");
            sb.AppendLine(bullet ? "  " + p : p);
            var nextIsBullet = i + 1 < lesson.Paragraphs.Count && lesson.Paragraphs[i + 1].StartsWith("- ");
            if (!(bullet && nextIsBullet)) sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}
