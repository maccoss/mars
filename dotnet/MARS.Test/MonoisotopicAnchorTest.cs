// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using MARS.Core;
using Xunit;

namespace MARS.Test;

/// <summary>
/// The anchor's job is to find the coarse m/z offset of a run and to land on the fragment's
/// monoisotopic peak rather than an isotope of it. These build runs whose offset is known.
/// </summary>
public sealed class MonoisotopicAnchorTest
{
    private const double Spacing = MonoisotopicAnchor.Carbon13Spacing;

    /// <summary>A run of synthetic MS2 spectra, held in memory.</summary>
    private sealed class FakeSource : ISpectrumSource
    {
        private readonly List<SpectrumRecord> _spectra;

        public FakeSource(List<SpectrumRecord> spectra) => _spectra = spectra;

        public string Path => "synthetic";

        public long Length => 0;

        public double? AcquisitionStartTime => 0;

        public MassAnalyzerClass Analyzer => MassAnalyzerClass.UnitResolution;

        public IEnumerable<SpectrumRecord> ReadSpectra(int? msLevel = 2) => _spectra;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// A library of simple peptides whose fragments are evenly spread over the mass range,
    /// all in one isolation window so every spectrum sees every fragment.
    /// </summary>
    private static SpectralLibrary BuildLibrary(int entries = 60, int fragmentsPerEntry = 8)
    {
        var builder = new SpectralLibraryBuilder();
        for (int e = 0; e < entries; e++)
        {
            builder.BeginEntry($"PEPTIDE{e}", 2, 650.0, double.NaN, double.NaN);
            for (int f = 0; f < fragmentsPerEntry; f++)
            {
                // Spread so that no two fragments sit within an isotope spacing of each
                // other: the scan must not be able to find one fragment's peak under
                // another fragment's target.
                double mz = 300.0 + (e * fragmentsPerEntry + f) * 1.7;
                builder.AddFragment(mz, 1000, 'y', f + 1, charge: 1);
            }

            builder.EndEntry();
        }

        return builder.Build();
    }

    /// <summary>
    /// Spectra whose peaks sit <paramref name="offset"/> from where the library puts them.
    /// </summary>
    /// <param name="monoFraction">
    /// Share of fragments whose M+0 is present at all. Below 1 the isotope is the more
    /// frequently seen peak, so the hit count prefers it and the scan lands there - which is
    /// the only way the scan ever lands on an isotope, and so the only thing that gives the
    /// monoisotopic check anything to do.
    /// </param>
    private static FakeSource BuildRun(
        SpectralLibrary library,
        double offset,
        int spectra = 200,
        double monoIntensity = 5000,
        double isotopeIntensity = 1500,
        double monoFraction = 1.0,
        int fragmentCharge = 1)
    {
        var records = new List<SpectrumRecord>(spectra);
        var random = new Random(7);

        for (int s = 0; s < spectra; s++)
        {
            var peaks = new List<(double Mz, double Intensity)>();
            for (int f = 0; f < library.FragmentCount; f++)
            {
                double mono = library.FragmentMz[f] + offset;

                // A little jitter, so the answer cannot come from an exact grid coincidence.
                double jitter = (random.NextDouble() - 0.5) * 0.04;
                if (random.NextDouble() < monoFraction) peaks.Add((mono + jitter, monoIntensity));
                peaks.Add((mono + (Spacing / fragmentCharge) + jitter, isotopeIntensity));
            }

            peaks.Sort((a, b) => a.Mz.CompareTo(b.Mz));
            var mz = new double[peaks.Count];
            var intensity = new double[peaks.Count];
            for (int i = 0; i < peaks.Count; i++) (mz[i], intensity[i]) = peaks[i];

            records.Add(new SpectrumRecord
            {
                ScanNumber = s,
                Index = s,
                MsLevel = 2,
                RetentionTime = s * 0.01,
                PrecursorMzLow = 600,
                PrecursorMzHigh = 700,
                PrecursorMzCenter = 650,
                MzArray = mz,
                IntensityArray = intensity,
                PeakCount = mz.Length,
                SummedIntensity = 1e6,
            });
        }

        return new FakeSource(records);
    }

    private static MatchOptions Trap() => new() { MzToleranceTh = 0.3, MinIntensity = 500 };

    [Theory]
    [InlineData(-0.75)]
    [InlineData(-0.50)]
    [InlineData(0.62)]
    public void FindsACoarseOffset(double offset)
    {
        SpectralLibrary library = BuildLibrary();
        AnchorResult result = MonoisotopicAnchor.Estimate(
            BuildRun(library, offset), library, Trap());

        Assert.Equal(AnchorOutcome.Applied, result.Outcome);
        Assert.Equal(offset, result.ShiftTh, 2);
    }

    [Fact]
    public void LandsOnTheMonoisotopeNotTheIsotope()
    {
        // The failure this whole class exists for: at -0.75 Th the M+0 has left a 0.3 Th
        // window and the M+1 sits inside it, so matching unshifted finds the isotope and
        // calls its tight, wrong residual a calibration.
        SpectralLibrary library = BuildLibrary();
        AnchorResult result = MonoisotopicAnchor.Estimate(
            BuildRun(library, -0.75), library, Trap());

        Assert.Equal(-0.75, result.ShiftTh, 2);
        Assert.NotEqual(-0.75 + Spacing, result.ShiftTh, 2);
    }

    [Fact]
    public void StepsDownWhenTheScanLandsOnTheIsotope()
    {
        // Only three fragments in four show an M+0, so the isotope is the more frequently
        // seen peak and the scan's hit count prefers it. The monoisotopic check is then the
        // only thing that can pull the answer back down onto the M+0 - and it can, because
        // where the M+0 is there it is the taller of the two.
        SpectralLibrary library = BuildLibrary();
        AnchorResult result = MonoisotopicAnchor.Estimate(
            BuildRun(library, -0.80, monoFraction: 0.75), library, Trap());

        Assert.Equal(-0.80, result.ShiftTh, 2);
        Assert.True(result.IsotopeSteps >= 1, $"expected a step down, got {result.IsotopeSteps}");
        Assert.Equal(1, result.StepDownCharge);
    }

    [Fact]
    public void ATieBetweenIsotopesGoesToTheLowerOne()
    {
        // When every fragment shows both peaks the hit count cannot tell them apart. The
        // M+1 is always the higher of the two, so the lower candidate is the monoisotope and
        // the tie must break downwards - without needing the step-down check at all.
        SpectralLibrary library = BuildLibrary();
        AnchorResult result = MonoisotopicAnchor.Estimate(
            BuildRun(library, -0.80), library, Trap());

        Assert.Equal(-0.80, result.ShiftTh, 2);
    }

    [Fact]
    public void DoesNotStepDownPastTheMonoisotope()
    {
        // Nothing sits an isotope below the M+0, so a correct answer must stay put however
        // many times the check is allowed to run.
        SpectralLibrary library = BuildLibrary();
        AnchorResult result = MonoisotopicAnchor.Estimate(
            BuildRun(library, -0.70), library, Trap());

        Assert.Equal(0, result.IsotopeSteps);
        Assert.Equal(-0.70, result.ShiftTh, 2);
    }

    [Fact]
    public void DeclinesWhenTheOffsetIsInsideTheWindow()
    {
        // Half a window or less and the monoisotopic peak is already being matched. Shifting
        // anyway would change the output of every well-calibrated run MARS has been checked
        // against, for nothing.
        SpectralLibrary library = BuildLibrary();
        AnchorResult result = MonoisotopicAnchor.Estimate(
            BuildRun(library, -0.10), library, Trap());

        Assert.Equal(AnchorOutcome.WithinTolerance, result.Outcome);
        Assert.Equal(0, result.ShiftTh);
        Assert.Equal(-0.10, result.ScannedShiftTh, 2);
    }

    [Fact]
    public void DoesNotRunForPpmTolerances()
    {
        // A ppm window is far narrower than any isotope spacing, so it cannot admit one.
        SpectralLibrary library = BuildLibrary();
        AnchorResult result = MonoisotopicAnchor.Estimate(
            BuildRun(library, -0.75), library, new MatchOptions { TolerancePpm = 10 });

        Assert.Equal(AnchorOutcome.NotAttempted, result.Outcome);
        Assert.Equal(0, result.ShiftTh);
    }

    [Fact]
    public void DeclinesWhenThereIsTooLittleToGoOn()
    {
        SpectralLibrary library = BuildLibrary();
        AnchorResult result = MonoisotopicAnchor.Estimate(
            BuildRun(library, -0.75, spectra: 1), library, Trap());

        Assert.Equal(AnchorOutcome.TooFewTargets, result.Outcome);
        Assert.Equal(0, result.ShiftTh);
    }

    [Fact]
    public void UsesTheChargesOwnIsotopeSpacing()
    {
        // A 2+ fragment's isotopes are 0.5017 Th apart, which is why they are captured at a
        // much smaller offset than a 1+ fragment's. Stepping down by the 1+ spacing here
        // would overshoot by half an isotope.
        var builder = new SpectralLibraryBuilder();
        for (int e = 0; e < 60; e++)
        {
            builder.BeginEntry($"PEPTIDE{e}", 2, 650.0, double.NaN, double.NaN);
            for (int f = 0; f < 8; f++)
                builder.AddFragment(300.0 + (e * 8 + f) * 1.7, 1000, 'y', f + 1, charge: 2);
            builder.EndEntry();
        }

        SpectralLibrary library = builder.Build();
        AnchorResult result = MonoisotopicAnchor.Estimate(
            BuildRun(library, -0.45, monoFraction: 0.75, fragmentCharge: 2), library, Trap());

        Assert.Equal(-0.45, result.ShiftTh, 2);
        Assert.Equal(2, result.StepDownCharge);
    }

    [Fact]
    public void RefinementBeatsTheGrid()
    {
        // The grid answers to 0.01 Th and its hit tolerance is wider than the error, so the
        // raw scan can only find the middle of a plateau. The median of the peaks it found
        // is the better answer, and is what the shift ends up being.
        SpectralLibrary library = BuildLibrary();
        AnchorResult result = MonoisotopicAnchor.Estimate(
            BuildRun(library, -0.733), library, Trap());

        Assert.Equal(-0.733, result.ShiftTh, 3);
    }
}
