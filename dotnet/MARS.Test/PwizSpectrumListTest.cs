// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Compiled only when a pwiz-sharp checkout is present; see MARS.Test.csproj.

using System;
using System.Collections.Generic;
using System.Linq;
using MARS.Core;
using MARS.Pwiz;
using Pwiz.Data.Common.Cv;
using Pwiz.Data.MsData.Spectra;
using Xunit;

namespace MARS.Test;

/// <summary>
/// The batching in <c>MarsSpectrumList</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the riskiest code in the pwiz path and the least visible. pwiz's writers pull
/// spectra one at a time, and 79% of a conversion's time is scoring the model, so MARS reads a
/// batch ahead and corrects it in parallel. That means index arithmetic, a reset path for
/// callers that do not walk in order, and a guard against handing the same spectrum out twice -
/// none of which announces itself when it goes wrong. A batching bug would reorder or drop
/// spectra in a written file, which no exception would report.
/// </para>
/// <para>
/// Driven through a fake inner list rather than a real vendor file, so it runs without one and
/// can be pushed into the edge cases a real file would not reach.
/// </para>
/// </remarks>
public class PwizSpectrumListTest
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    public void EverySpectrumIsReturnedOnceInOrder(int threads)
    {
        var inner = new FakeSpectrumList(count: 101);
        using var list = Wrap(inner, threads);

        var ids = new List<string>();
        for (int i = 0; i < list.Count; i++)
            ids.Add(list.GetSpectrum(i, getBinaryData: true).Id);

        Assert.Equal(Enumerable.Range(0, 101).Select(i => $"scan={i}"), ids);
    }

    /// <summary>
    /// A count that is not a multiple of the batch size leaves a short final batch. Reading off
    /// the end of it is the obvious way to get this wrong.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(33)]
    [InlineData(64)]
    [InlineData(65)]
    public void AShortFinalBatchIsServedCompletely(int count)
    {
        var inner = new FakeSpectrumList(count);
        using var list = Wrap(inner, threads: 8);

        for (int i = 0; i < count; i++)
            Assert.Equal($"scan={i}", list.GetSpectrum(i, getBinaryData: true).Id);
    }

    /// <summary>
    /// pwiz's writers walk in order, but nothing in the interface promises it. An out-of-order
    /// pull has to be served correctly rather than from the wrong place in a read-ahead batch.
    /// </summary>
    [Fact]
    public void OutOfOrderAccessIsStillCorrect()
    {
        var inner = new FakeSpectrumList(count: 60);
        using var list = Wrap(inner, threads: 8);

        foreach (int index in new[] { 0, 1, 2, 40, 41, 3, 59, 0, 30 })
            Assert.Equal($"scan={index}", list.GetSpectrum(index, getBinaryData: true).Id);
    }

    /// <summary>
    /// The whole point of the batch is the correction, so it has to actually happen - and only
    /// on MS2, which is all MARS calibrates.
    /// </summary>
    [Fact]
    public void Ms2IsCorrectedAndMs1IsNot()
    {
        var inner = new FakeSpectrumList(count: 40);
        using var list = Wrap(inner, threads: 8);

        var corrected = new List<double>();
        var untouched = new List<double>();
        for (int i = 0; i < inner.Count; i++)
        {
            Spectrum s = list.GetSpectrum(i, getBinaryData: true);
            double first = s.GetMZArray()!.Data[0];
            if (s.Params.CvParamValueOrDefault(CVID.MS_ms_level, 0) == 2) corrected.Add(first);
            else untouched.Add(first);
        }

        Assert.NotEmpty(corrected);
        Assert.NotEmpty(untouched);

        // MS1 keeps the value the fake produced; MS2 does not.
        Assert.All(untouched, v => Assert.Equal(FakeSpectrumList.BaseMz, v, 12));
        Assert.Contains(corrected, v => Math.Abs(v - FakeSpectrumList.BaseMz) > 1e-9);
    }

    /// <summary>
    /// Correcting in parallel must not change the answer. Verified on real data by hashing an
    /// mzXML written on 1 thread against one written on 12; this pins it without a 5-minute
    /// conversion.
    /// </summary>
    [Fact]
    public void TheResultDoesNotDependOnThreadCount()
    {
        double[] one = CorrectedMz(threads: 1);
        double[] many = CorrectedMz(threads: 12);

        Assert.Equal(one.Length, many.Length);
        for (int i = 0; i < one.Length; i++)
            Assert.Equal(one[i], many[i], 15);
    }

    /// <summary>Counters have to survive being summed across workers.</summary>
    [Fact]
    public void TheCountersAgreeAcrossThreadCounts()
    {
        var single = new FakeSpectrumList(count: 101);
        using MarsSpectrumList a = Wrap(single, threads: 1);
        for (int i = 0; i < single.Count; i++) a.GetSpectrum(i, getBinaryData: true);

        var parallel = new FakeSpectrumList(count: 101);
        using MarsSpectrumList b = Wrap(parallel, threads: 12);
        for (int i = 0; i < parallel.Count; i++) b.GetSpectrum(i, getBinaryData: true);

        Assert.Equal(a.SpectraSeen, b.SpectraSeen);
        Assert.Equal(a.SpectraCorrected, b.SpectraCorrected);
        Assert.True(a.SpectraSeen > 0);
    }

    /// <summary>A metadata-only pull must not prime a batch of fully decoded spectra.</summary>
    [Fact]
    public void MetadataOnlyPullsAreCheap()
    {
        var inner = new FakeSpectrumList(count: 60);
        using var list = Wrap(inner, threads: 8);

        for (int i = 0; i < inner.Count; i++) list.GetSpectrum(i, getBinaryData: false);

        Assert.Equal(0, inner.BinaryReads);
    }

    private static double[] CorrectedMz(int threads)
    {
        var inner = new FakeSpectrumList(count: 101);
        using var list = Wrap(inner, threads);

        var values = new List<double>();
        for (int i = 0; i < inner.Count; i++)
        {
            Spectrum s = list.GetSpectrum(i, getBinaryData: true);
            values.AddRange(s.GetMZArray()!.Data);
        }

        return values.ToArray();
    }

    private static MarsSpectrumList Wrap(FakeSpectrumList inner, int threads) =>
        new(inner, TinyModel(), new CorrectionOptions(), acquisitionStart: 0, temperatures: null, threads);

    /// <summary>A model that moves m/z measurably, so a missed correction is visible.</summary>
    private static MzCalibrator TinyModel()
    {
        var table = new MatchTable(new[] { MarsFeature.FragmentMz, MarsFeature.LogIntensity });
        var random = new Random(3);

        for (var i = 0; i < 400; i++)
        {
            double fragmentMz = 300 + (random.NextDouble() * 700);
            double intensity = 500 + (random.NextDouble() * 100000);

            table.Set(MarsFeature.FragmentMz, fragmentMz);
            table.Set(MarsFeature.LogIntensity, Math.Log10(intensity));
            table.DeltaMz.Add(0.01 + (fragmentMz * 1e-5));
            table.ObservedIntensity.Add(intensity);
            table.PeptideGroup.Add(i / 8);
            table.CommitRow();
        }

        return MzCalibrator.Fit(table, new CalibrationOptions { CvFolds = 0 }, absoluteTimeOffset: 0);
    }

    /// <summary>
    /// A spectrum list that answers from nothing, so the batching can be driven directly.
    /// Every fourth spectrum is MS1, as a real DIA run alternates.
    /// </summary>
    private sealed class FakeSpectrumList : SpectrumListBase
    {
        public const double BaseMz = 500.0;

        public FakeSpectrumList(int count) => Count = count;

        public override int Count { get; }

        /// <summary>How many times a caller asked for decoded arrays.</summary>
        public int BinaryReads { get; private set; }

        public override SpectrumIdentity SpectrumIdentity(int index) =>
            new() { Index = index, Id = $"scan={index}" };

        public override Spectrum GetSpectrum(int index, bool getBinaryData = false)
        {
            if (getBinaryData) BinaryReads++;

            int msLevel = index % 4 == 0 ? 1 : 2;
            var spectrum = new Spectrum { Index = index, Id = $"scan={index}" };
            spectrum.Params.Set(CVID.MS_ms_level, msLevel.ToString());
            spectrum.Params.Set(CVID.MS_total_ion_current, "1000000");

            var scan = new Scan();
            scan.Set(CVID.MS_scan_start_time, (index * 0.01).ToString(), CVID.UO_minute);
            scan.Set(CVID.MS_ion_injection_time, (10.0 + (index % 5)).ToString(), CVID.UO_millisecond);
            spectrum.ScanList.Scans.Add(scan);

            var precursor = new Precursor();
            precursor.IsolationWindow.Set(CVID.MS_isolation_window_target_m_z, "600");
            precursor.IsolationWindow.Set(CVID.MS_isolation_window_lower_offset, "5");
            precursor.IsolationWindow.Set(CVID.MS_isolation_window_upper_offset, "5");
            spectrum.Precursors.Add(precursor);

            if (!getBinaryData) return spectrum;

            var mz = new BinaryDataArray { Data = { BaseMz, BaseMz + 1, BaseMz + 2 } };
            mz.Params.Set(CVID.MS_m_z_array);
            var intensity = new BinaryDataArray { Data = { 5000.0, 6000.0, 7000.0 } };
            intensity.Params.Set(CVID.MS_intensity_array);
            spectrum.BinaryDataArrays.Add(mz);
            spectrum.BinaryDataArrays.Add(intensity);
            spectrum.DefaultArrayLength = 3;
            return spectrum;
        }
    }
}
