// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.IO;
using MARS.Cli;
using MARS.Core;
using MARS.IO;
using Xunit;

namespace MARS.Test;

public class MassAnalyzerTest
{
    [Theory]
    [InlineData(MassAnalyzers.Orbitrap, MassAnalyzerClass.HighResolution)]
    [InlineData(MassAnalyzers.AsymmetricTrackLosslessTimeOfFlight, MassAnalyzerClass.HighResolution)]
    [InlineData(MassAnalyzers.TimeOfFlight, MassAnalyzerClass.HighResolution)]
    [InlineData(MassAnalyzers.FourierTransformIonCyclotronResonance, MassAnalyzerClass.HighResolution)]
    [InlineData(MassAnalyzers.RadialEjectionLinearIonTrap, MassAnalyzerClass.UnitResolution)]
    [InlineData(MassAnalyzers.QuadrupoleIonTrap, MassAnalyzerClass.UnitResolution)]
    [InlineData(MassAnalyzers.Quadrupole, MassAnalyzerClass.UnitResolution)]
    [InlineData("MS:9999999", MassAnalyzerClass.Unknown)]
    [InlineData(null, MassAnalyzerClass.Unknown)]
    public void AccessionsClassify(string? accession, MassAnalyzerClass expected) =>
        Assert.Equal(expected, MassAnalyzers.Classify(accession));

    [Theory]
    [InlineData("ITMS + c NSI t Full ms2 601.02@hcd30.00", MassAnalyzerClass.UnitResolution)]
    [InlineData("FTMS + c NSI Full ms [375.0000-985.0000]", MassAnalyzerClass.HighResolution)]
    [InlineData("ASTMS + c NSI Full ms2 413.93@hcd27.00", MassAnalyzerClass.HighResolution)]
    [InlineData("something else entirely", MassAnalyzerClass.Unknown)]
    [InlineData("", MassAnalyzerClass.Unknown)]
    public void FilterStringsClassify(string filter, MassAnalyzerClass expected) =>
        Assert.Equal(expected, MassAnalyzers.ClassifyFilterString(filter));

    /// <summary>
    /// The quadrupole in a hybrid configuration isolates rather than measures. Choosing it
    /// would call an Astral run unit-resolution and set a tolerance two orders of magnitude
    /// too wide.
    /// </summary>
    [Fact]
    public void TheMeasuringAnalyzerIsNotTheIsolatingQuadrupole()
    {
        string? measuring = MassAnalyzers.MeasuringAnalyzer(new[]
        {
            (2, MassAnalyzers.Quadrupole),
            (3, MassAnalyzers.AsymmetricTrackLosslessTimeOfFlight),
        });

        Assert.Equal(MassAnalyzers.AsymmetricTrackLosslessTimeOfFlight, measuring);
    }

    [Fact]
    public void AQuadrupoleOnlyConfigurationStillReportsTheQuadrupole()
    {
        string? measuring = MassAnalyzers.MeasuringAnalyzer(new[] { (2, MassAnalyzers.Quadrupole) });
        Assert.Equal(MassAnalyzers.Quadrupole, measuring);
    }

    /// <summary>Order decides among analyzers, not the order they happen to be listed in.</summary>
    [Fact]
    public void TheHighestOrderAnalyzerWinsWhicheverWayRoundTheyAreListed()
    {
        var forwards = new[] { (2, MassAnalyzers.IonTrap), (3, MassAnalyzers.Orbitrap) };
        var backwards = new[] { (3, MassAnalyzers.Orbitrap), (2, MassAnalyzers.IonTrap) };

        Assert.Equal(MassAnalyzers.Orbitrap, MassAnalyzers.MeasuringAnalyzer(forwards));
        Assert.Equal(MassAnalyzers.Orbitrap, MassAnalyzers.MeasuringAnalyzer(backwards));
    }

    [Fact]
    public void ATrapFileIsReadAsUnitResolution() =>
        Assert.Equal(
            MassAnalyzerClass.UnitResolution,
            Detect(SyntheticMzML.MassAnalyzerLayout.UnitResolutionTrap));

    /// <summary>
    /// The case the whole mechanism exists for. The run names the orbitrap as its default
    /// because that is what takes the MS1 survey; only the MS2 spectra point at the Astral
    /// analyzer, and MS2 is what MARS calibrates.
    /// </summary>
    [Fact]
    public void AHybridFileIsClassifiedByItsMs2AnalyzerNotItsRunDefault() =>
        Assert.Equal(
            MassAnalyzerClass.HighResolution,
            Detect(SyntheticMzML.MassAnalyzerLayout.HybridOrbitrapAstral));

    [Fact]
    public void AFileThatDoesNotSayIsUnknownRatherThanGuessed() =>
        Assert.Equal(MassAnalyzerClass.Unknown, Detect(SyntheticMzML.MassAnalyzerLayout.None));

    /// <summary>
    /// Adding the configuration list must not disturb a fixture without one - every other
    /// test in the suite reads those bytes.
    /// </summary>
    [Fact]
    public void TheDefaultFixtureIsUnchangedByTheNewParameter()
    {
        string directory = NewDirectory();
        try
        {
            string implicitDefault = Path.Combine(directory, "implicit.mzML");
            string explicitNone = Path.Combine(directory, "explicit.mzML");
            SyntheticMzML.Write(implicitDefault, spectrumCount: 8, chromatogramCount: 1);
            SyntheticMzML.Write(
                explicitNone, spectrumCount: 8, chromatogramCount: 1,
                analyzers: SyntheticMzML.MassAnalyzerLayout.None);

            Assert.Equal(File.ReadAllBytes(implicitDefault), File.ReadAllBytes(explicitNone));
        }
        finally
        {
            Delete(directory);
        }
    }

    /// <summary>
    /// Detection sets a default; it never overrules the person at the terminal, who can be
    /// sure in a way a heuristic cannot.
    /// </summary>
    [Fact]
    public void AnExplicitToleranceBeatsDetection()
    {
        string directory = NewDirectory();
        try
        {
            string path = Path.Combine(directory, "astral.mzML");
            SyntheticMzML.Write(
                path, spectrumCount: 8, chromatogramCount: 0,
                analyzers: SyntheticMzML.MassAnalyzerLayout.HybridOrbitrapAstral);

            var options = new MatchOptions();
            CommandLineArgs args = CommandLineArgs.Parse(new[] { "qc", "--tolerance", "0.5" });
            options.MzToleranceTh = args.Double("tolerance") ?? ResolutionMode.DefaultToleranceTh;

            ResolutionMode mode = ResolutionMode.Resolve(args, Detect(path), options, _ => { });

            Assert.Equal(MassAnalyzerClass.HighResolution, mode.Analyzer);
            Assert.Equal(0.5, options.MzToleranceTh);
            Assert.Equal(0, options.TolerancePpm);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public void DetectionPicksThePpmToleranceOnHighResolutionData()
    {
        string directory = NewDirectory();
        try
        {
            string path = Path.Combine(directory, "astral.mzML");
            SyntheticMzML.Write(
                path, spectrumCount: 8, chromatogramCount: 0,
                analyzers: SyntheticMzML.MassAnalyzerLayout.HybridOrbitrapAstral);

            var options = new MatchOptions();
            CommandLineArgs args = CommandLineArgs.Parse(new[] { "qc" });
            ResolutionMode mode = ResolutionMode.Resolve(args, Detect(path), options, _ => { });

            Assert.True(mode.ReportInPpm);
            Assert.Equal(ResolutionMode.DefaultTolerancePpm, options.TolerancePpm);
            Assert.Equal(0, options.MzToleranceTh);
        }
        finally
        {
            Delete(directory);
        }
    }

    /// <summary>
    /// --resolution overrides what the file says, for data whose header is wrong or absent.
    /// </summary>
    [Fact]
    public void AnExplicitModeOverridesWhatTheFileSays()
    {
        string directory = NewDirectory();
        try
        {
            string path = Path.Combine(directory, "trap.mzML");
            SyntheticMzML.Write(
                path, spectrumCount: 8, chromatogramCount: 0,
                analyzers: SyntheticMzML.MassAnalyzerLayout.UnitResolutionTrap);

            var options = new MatchOptions();
            CommandLineArgs args = CommandLineArgs.Parse(new[] { "qc", "--resolution", "hram" });
            ResolutionMode mode = ResolutionMode.Resolve(args, Detect(path), options, _ => { });

            Assert.Equal(MassAnalyzerClass.HighResolution, mode.Analyzer);
            Assert.Equal(ResolutionMode.DefaultTolerancePpm, options.TolerancePpm);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public void AnUnrecognizedModeIsRejected()
    {
        CommandLineArgs args = CommandLineArgs.Parse(new[] { "qc", "--resolution", "sideways" });
        Assert.Throws<FormatException>(
            () => ResolutionMode.Resolve(args, MassAnalyzerClass.Unknown, new MatchOptions(), _ => { }));
    }

    /// <summary>
    /// mars verify writes a round-tripped copy and deletes it unless --keep, so an --output
    /// pointing at the input would destroy the file the command exists to vouch for.
    /// </summary>
    [Fact]
    public void VerifyRefusesToWriteOverItsInput()
    {
        string directory = NewDirectory();
        try
        {
            string path = Path.Combine(directory, "run.mzML");
            SyntheticMzML.Write(path, spectrumCount: 8, chromatogramCount: 0);
            long before = new FileInfo(path).Length;

            int exit = VerifyCommand.Run(
                CommandLineArgs.Parse(new[] { "verify", "--input", path, "--output", path }));

            Assert.Equal(Program.ExitInputError, exit);
            Assert.True(File.Exists(path), "verify deleted its own input");
            Assert.Equal(before, new FileInfo(path).Length);
        }
        finally
        {
            Delete(directory);
        }
    }

    /// <summary>The same file reached by a different spelling of the path is still the same file.</summary>
    [Fact]
    public void VerifyRefusesAnInputAndOutputThatOnlyLookDifferent()
    {
        string directory = NewDirectory();
        try
        {
            string path = Path.Combine(directory, "run.mzML");
            SyntheticMzML.Write(path, spectrumCount: 8, chromatogramCount: 0);
            string roundabout = Path.Combine(directory, ".", "run.mzML");

            int exit = VerifyCommand.Run(
                CommandLineArgs.Parse(new[] { "verify", "--input", path, "--output", roundabout }));

            Assert.Equal(Program.ExitInputError, exit);
            Assert.True(File.Exists(path));
        }
        finally
        {
            Delete(directory);
        }
    }

    /// <summary>What a reader would detect for a file already on disk.</summary>
    private static MassAnalyzerClass Detect(string path) =>
        MzMLFile.DetectMs2Analyzer(MzMLFile.Inspect(path));

    private static MassAnalyzerClass Detect(SyntheticMzML.MassAnalyzerLayout layout)
    {
        string directory = NewDirectory();
        try
        {
            string path = Path.Combine(directory, "input.mzML");
            SyntheticMzML.Write(path, spectrumCount: 12, chromatogramCount: 0, analyzers: layout);
            return MzMLFile.DetectMs2Analyzer(MzMLFile.Inspect(path));
        }
        finally
        {
            Delete(directory);
        }
    }

    private static string NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mars-analyzer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Delete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
    }
}
