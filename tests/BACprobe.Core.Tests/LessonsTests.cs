using BACprobe.Core.Discovery;
using BACprobe.Core.Learning;

namespace BACprobe.Core.Tests;

public class LessonsTests
{
    private static NetworkFinding F(string title) => new(FindingSeverity.Warning, title, "", "cause", "next");

    [Fact]
    public void Lesson_ids_are_unique_and_every_lesson_has_text()
    {
        Assert.Equal(Lessons.All.Count, Lessons.All.Select(l => l.Id).Distinct().Count());
        Assert.All(Lessons.All, l =>
        {
            Assert.NotEmpty(l.Title);
            Assert.NotEmpty(l.Summary);
            Assert.NotEmpty(l.Paragraphs);
        });
    }

    [Theory]
    [InlineData("Device number 1001 is used by 2 devices", "device-numbers")]
    [InlineData("A device at 10.0.0.5 still has the unassigned device number 4194303", "device-numbers")]
    [InlineData("Network 5: devices 1, 2 share the same MAC address 7", "duplicate-mac")]
    [InlineData("Device 1001 \"AHU-1\" has moved", "saved-job")]
    [InlineData("Two devices are both named \"AHU-1\"", "device-names")]
    [InlineData("Device 1001 \"AHU-1\" is slow to answer", "slow-device")]
    [InlineData("3 devices cannot send long answers in pieces", "slow-device")]
    [InlineData("Device 7 \"x\"'s clock is off", "clock")]
    [InlineData("Network 1001 is announced by 2 routers", "routers")]
    [InlineData("10.0.0.1 does not accept foreign devices", "foreign-device")]
    [InlineData("One-way: 10.0.1.1 does not list 10.0.0.1", "bbmd")]
    [InlineData("10.0.0.1 has an empty broadcast table", "bbmd")]
    [InlineData("Heard nothing on the trunk", "mstp-wiring")]
    [InlineData("Heard bytes but no valid frames", "mstp-baud")]
    [InlineData("The trunk runs at 9600 baud, which is slow", "mstp-baud")]
    [InlineData("The trunk is 80% busy", "mstp-baud")]
    [InlineData("12 damaged frames (6.0% of all traffic)", "mstp-wiring")]
    [InlineData("MAC 7's transmissions arrive damaged", "mstp-wiring")]
    [InlineData("MAC 7 never takes the token", "mstp-token")]
    [InlineData("Max Master is set too low", "mstp-token")]
    [InlineData("The token takes 400 ms to go round", "mstp-token")]
    [InlineData("The USB adapter's latency timer is too high", "mstp-baud")]
    public void A_finding_title_points_at_its_lesson(string title, string lessonId)
        => Assert.Equal(lessonId, Lessons.ForFinding(F(title))?.Id);

    [Fact]
    public void An_unknown_finding_has_no_lesson() => Assert.Null(Lessons.ForFinding(F("Something nobody wrote a lesson for")));

    [Fact]
    public void Find_ignores_case_and_ToText_includes_the_bullets()
    {
        var l = Lessons.Find("BBMD");
        Assert.NotNull(l);
        var text = Lessons.ToText(l!);
        Assert.Contains(l!.Title, text);
        Assert.Contains("  - ", text);
    }
}
