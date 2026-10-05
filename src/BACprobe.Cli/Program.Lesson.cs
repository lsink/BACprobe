using BACprobe.Core.Learning;

namespace BACprobe.Cli;

internal static partial class Program
{
    /// <summary>`bacprobe lesson` lists the built-in lessons; `bacprobe lesson bbmd` prints one.</summary>
    private static int Lesson(string[] args)
    {
        var id = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (id is null)
        {
            Console.WriteLine("Built-in lessons (bacprobe lesson <name>):");
            foreach (var l in Lessons.All) Console.WriteLine($"  {l.Id,-16} {l.Summary}");
            return 0;
        }
        var lesson = Lessons.Find(id);
        if (lesson is null) return Fail($"No lesson called '{id}'. Run 'bacprobe lesson' to see the list.");
        Console.WriteLine(Lessons.ToText(lesson));
        return 0;
    }
}
