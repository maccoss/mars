// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Globalization;

namespace MARS.Cli;

/// <summary>
/// Decides how many worker threads a run gets.
/// </summary>
/// <remarks>
/// <para>
/// One number drives all three parallel stages: the mzML writer, the pwiz spectrum list, and
/// the histogram build inside the boosting implementation. Matching is not among them - it
/// streams spectra in order on one thread - so a run's wall clock never falls in proportion
/// to this.
/// </para>
/// <para>
/// The default is every logical processor, which on a machine with simultaneous multithreading
/// is twice the physical core count. That is worth measuring rather than assuming, because the
/// usual advice is that the extra hardware threads do little for numeric work. On the reference
/// machine, an 8-core i9-9900K with 16 logical processors, correcting and rewriting one 1.2 GB
/// Stellar run:
/// </para>
/// <code>
///   threads    2      4      6      8     10     12     16
///   seconds  150.5   77.4   52.5   45.4   42.8   38.7   36.8
/// </code>
/// <para>
/// Scaling is near-perfect to 4 and keeps improving to the end: the 16 logical processors are
/// 24% faster than the 8 physical ones. So the default stays at every logical processor, and
/// capping at physical cores would cost most of a quarter of the throughput for nothing.
/// </para>
/// <para>
/// It is a shallow curve past 8, though - half the ideal speedup by 16 - and the writer drains
/// its results in order on one thread, which has to become the limit somewhere. Where that is
/// on a 64- or 128-core machine has not been measured, so no ceiling is imposed here: a guessed
/// one would be worse than none. The chosen number is reported instead, so anyone with such a
/// machine can see what they got and set <c>--threads</c> against it.
/// </para>
/// </remarks>
public static class ThreadCount
{
    /// <summary>What <c>--threads</c> accepts in place of a number.</summary>
    public const string Automatic = "auto";

    /// <summary>
    /// Registers the option so the unknown-option check knows it is real.
    /// </summary>
    /// <remarks>
    /// Only needed where a command resolves the count after that check has run. Reading it
    /// twice is harmless; not reading it before the check reports it as a typo.
    /// </remarks>
    public static void Touch(CommandLineArgs args) => args.Has("threads");

    /// <summary>
    /// Resolves <c>--threads</c> to a concrete count, reporting what it settled on.
    /// </summary>
    /// <param name="log">Where the decision is reported. Null to decide silently.</param>
    /// <param name="warn">Where an oversubscribed request is reported.</param>
    public static int Resolve(CommandLineArgs args, Action<string>? log, Action<string>? warn)
    {
        int available = Environment.ProcessorCount;
        string? text = args.String("threads");

        if (text is null || text.Equals(Automatic, StringComparison.OrdinalIgnoreCase))
        {
            log?.Invoke(
                $"Using {available} worker thread{(available == 1 ? string.Empty : "s")}, one per "
                + "logical processor. --threads <n> to change it.");
            return available;
        }

        // How the parser records an option given without a value, which is what
        // `--threads -4` looks like to it: the -4 is read as the next option, not as a value.
        if (text == "true")
        {
            throw new FormatException(
                $"--threads was given no value. Pass a whole number or '{Automatic}'; note that "
                + "a negative count has to be written --threads=-1 to keep it from being read "
                + "as another option.");
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int requested))
        {
            throw new FormatException(
                $"--threads expects a whole number or '{Automatic}', got '{text}'.");
        }

        if (requested < 1)
        {
            // Silently meaning "all of them" is the kind of default that hides a scripting
            // mistake: --threads $N with N unset should say so, not quietly use the machine.
            throw new FormatException(
                $"--threads must be at least 1, got {requested}. Use '{Automatic}' for one per "
                + "logical processor.");
        }

        if (requested > available)
        {
            warn?.Invoke(
                $"--threads {requested} is more than the {available} logical processors this "
                + "machine has. The extra threads contend for the same cores rather than adding "
                + "any, and the correction is unaffected either way.");
        }
        else
        {
            log?.Invoke($"Using {requested} of {available} worker threads, as asked.");
        }

        return requested;
    }
}
