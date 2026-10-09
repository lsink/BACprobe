namespace BACprobe.Cli;

internal static partial class Program
{
    /// <summary>What every command says when a device does not answer Who-Is.</summary>
    private static string NoWhoIsAnswer(string headline) =>
        headline + "\n" +
        "  Likely cause: wrong instance number, wrong adapter/subnet, or the device is behind a router/BBMD.\n" +
        "  Next step:    run 'bacprobe discover' to list the devices that do answer, or try a longer --wait.";

    private static string NoWhoIsAnswer(long instance) => NoWhoIsAnswer($"Device {instance} did not answer Who-Is.");

    private enum Consent { GoAhead, Cancelled, CannotAsk }

    /// <summary>
    /// Ask the person at the keyboard before something changes a device: --yes skips the question (scripts), and with no keyboard (input
    /// redirected) nobody can be asked, so it does not go ahead.
    /// </summary>
    private static Consent AskToGoAhead(Dictionary<string, string?> opts, string prompt, string expected = "y")
    {
        if (opts.ContainsKey("yes")) return Consent.GoAhead;
        if (Console.IsInputRedirected) return Consent.CannotAsk;
        Console.Write(prompt);
        return string.Equals(Console.ReadLine()?.Trim(), expected, StringComparison.OrdinalIgnoreCase) ? Consent.GoAhead : Consent.Cancelled;
    }

    /// <summary>
    /// <see cref="AskToGoAhead"/> for a command: null to go ahead, otherwise the exit code to return (0 cancelled, with
    /// <paramref name="cancelled"/> printed; 1 nobody to ask).
    /// </summary>
    private static int? ConfirmOrExit(Dictionary<string, string?> opts, string prompt, string cancelled, string expected = "y")
    {
        switch (AskToGoAhead(opts, prompt, expected))
        {
            case Consent.GoAhead:
                return null;
            case Consent.CannotAsk:
                return Fail("This needs a person to confirm. Run it in a terminal, or add --yes if you are scripting it.");
            default:
                Console.WriteLine(cancelled);
                return 0;
        }
    }
}
