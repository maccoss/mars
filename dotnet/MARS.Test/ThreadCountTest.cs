// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using MARS.Cli;
using Xunit;

namespace MARS.Test;

/// <summary>
/// How <c>--threads</c> is resolved.
/// </summary>
/// <remarks>
/// The count changes how long a run takes and nothing else - every stage it feeds is
/// per-spectrum or per-feature work with no cross-row accumulation - so a wrong value here is
/// invisible in the output. That is exactly why it is worth pinning: the failure mode is a run
/// that quietly takes four times as long, or one that quietly uses a machine someone else is
/// sharing.
/// </remarks>
public class ThreadCountTest
{
    [Fact]
    public void TheDefaultIsOnePerLogicalProcessor() =>
        Assert.Equal(Environment.ProcessorCount, Resolve());

    /// <summary>Naming the default explicitly has to mean the same as leaving it out.</summary>
    [Theory]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("Auto")]
    public void AutoMeansTheSameAsSayingNothing(string spelling) =>
        Assert.Equal(Environment.ProcessorCount, Resolve("--threads", spelling));

    [Fact]
    public void ANumberIsTakenAsGiven() =>
        Assert.Equal(3, Resolve("--threads", "3"));

    /// <summary>
    /// A count below one is refused rather than read as "use everything". `--threads $N` with
    /// N unset expands to nothing or to zero, and quietly taking the whole machine is a poor
    /// way to report a scripting mistake.
    /// </summary>
    [Theory]
    [InlineData("--threads", "0")]
    [InlineData("--threads=-1", null)]
    [InlineData("--threads=-16", null)]
    public void ACountBelowOneIsRefused(string first, string? second)
    {
        string[] options = second is null ? new[] { first } : new[] { first, second };
        FormatException error = Assert.Throws<FormatException>(() => Resolve(options));
        Assert.Contains("at least 1", error.Message, StringComparison.Ordinal);
        Assert.Contains("auto", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// `--threads -4` is not a negative count to the parser, it is the option followed by
    /// another option. Saying so beats reporting that --threads got the value 'true'.
    /// </summary>
    [Fact]
    public void AnOptionGivenNoValueSaysSo()
    {
        FormatException error = Assert.Throws<FormatException>(() => Resolve("--threads", "-4"));
        Assert.Contains("no value", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("lots")]
    [InlineData("8.5")]
    [InlineData("")]
    public void SomethingThatIsNeitherANumberNorAutoIsRefused(string value) =>
        Assert.Throws<FormatException>(() => Resolve("--threads", value));

    /// <summary>
    /// Asking for more threads than the machine has is allowed - it may be deliberate, and it
    /// cannot corrupt anything - but it is said out loud, because it does not go faster.
    /// </summary>
    [Fact]
    public void AskingForMoreThreadsThanTheMachineHasWarnsButIsHonoured()
    {
        var warnings = new List<string>();
        int requested = Environment.ProcessorCount * 4;

        int resolved = ThreadCount.Resolve(
            CommandLineArgs.Parse(new[] { "verify", "--threads", requested.ToString() }),
            log: null,
            warn: warnings.Add);

        Assert.Equal(requested, resolved);
        Assert.Single(warnings);
        Assert.Contains("logical processors", warnings[0], StringComparison.Ordinal);
    }

    /// <summary>A run has to be able to say what it settled on; silence is what prompted this.</summary>
    [Fact]
    public void TheChosenCountIsReported()
    {
        var reported = new List<string>();
        ThreadCount.Resolve(CommandLineArgs.Parse(new[] { "verify" }), reported.Add, warn: null);

        Assert.Single(reported);
        Assert.Contains(Environment.ProcessorCount.ToString(), reported[0], StringComparison.Ordinal);
    }

    private static int Resolve(params string[] options)
    {
        var argv = new List<string> { "verify" };
        argv.AddRange(options);
        return ThreadCount.Resolve(CommandLineArgs.Parse(argv.ToArray()), log: null, warn: null);
    }
}
