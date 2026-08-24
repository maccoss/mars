// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// The passthrough contract, exercised on a synthetic mzML built in the test itself so the
// suite needs no data files.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MARS.Core;
using MARS.IO;
using Xunit;

namespace MARS.Test;

public sealed class MzMLPassthroughTest : IDisposable
{
    private readonly string _directory;

    public MzMLPassthroughTest()
    {
        _directory = Path.Combine(Path.GetTempPath(), "mars-test-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A locked temp file must not fail the suite.
        }
    }

    [Fact]
    public void NullCorrectionRoundTripsBitIdentically()
    {
        string input = Path.Combine(_directory, "input.mzML");
        SyntheticMzML.Write(input, spectrumCount: 12, chromatogramCount: 2);

        string output = Path.Combine(_directory, "output.mzML");
        MzMLFileInfo info = MzMLFile.Inspect(input);
        Assert.True(info.IsIndexedMzML);
        Assert.True(info.WasIndexed);

        MzMLWriteResult result = MzMLWriter.Write(info, output, () => new NullMzTransform());

        Assert.Equal(12, result.SpectraSeen);
        Assert.Equal(2, result.ChromatogramsCopied);
        Assert.True(result.WroteIndex);

        MzMLComparison comparison = MzMLComparer.Compare(input, output);
        Assert.Equal(0, comparison.SpectraOnlyInA);
        Assert.Equal(0, comparison.SpectraOnlyInB);
        Assert.True(comparison.MzBitIdentical, string.Join("; ", comparison.Problems));
        Assert.True(comparison.IntensityBitIdentical);
    }

    [Fact]
    public void RegeneratedIndexAndChecksumValidate()
    {
        string input = Path.Combine(_directory, "input.mzML");
        SyntheticMzML.Write(input, spectrumCount: 20, chromatogramCount: 1);

        string output = Path.Combine(_directory, "output.mzML");
        MzMLWriter.Write(MzMLFile.Inspect(input), output, () => new NullMzTransform());

        IndexValidationResult validation = MzMLValidator.Validate(output);
        Assert.True(validation.IsIndexed);
        Assert.Equal(20, validation.SpectrumOffsets);
        Assert.Equal(1, validation.ChromatogramOffsets);
        Assert.Empty(validation.BadOffsets);
        Assert.True(validation.ChecksumPresent);
        Assert.True(validation.ChecksumValid,
            $"recorded {validation.RecordedChecksum}, computed {validation.ComputedChecksum}");
    }

    /// <summary>
    /// Only the m/z array of a selected spectrum may change. The intensity array, the
    /// metadata and every other byte have to survive untouched.
    /// </summary>
    [Fact]
    public void CorrectionTouchesOnlyTheMzArray()
    {
        string input = Path.Combine(_directory, "input.mzML");
        SyntheticMzML.Write(input, spectrumCount: 8, chromatogramCount: 0);

        string output = Path.Combine(_directory, "output.mzML");
        MzMLWriter.Write(MzMLFile.Inspect(input), output, () => new ShiftTransform(0.01));

        MzMLComparison comparison = MzMLComparer.Compare(input, output);
        Assert.True(comparison.IntensityBitIdentical);
        Assert.False(comparison.MzBitIdentical);
        Assert.Equal(comparison.MzValuesCompared, comparison.MzValuesDiffering);
        Assert.InRange(comparison.MaxAbsoluteMzDifference, 0.0099, 0.0101);

        // Structural checks still pass after a real modification.
        IndexValidationResult validation = MzMLValidator.Validate(output);
        Assert.Empty(validation.BadOffsets);
        Assert.True(validation.ChecksumValid);
    }

    [Fact]
    public void OutputIsDeterministicAcrossThreadCounts()
    {
        string input = Path.Combine(_directory, "input.mzML");
        SyntheticMzML.Write(input, spectrumCount: 30, chromatogramCount: 1);

        string single = Path.Combine(_directory, "t1.mzML");
        string many = Path.Combine(_directory, "t16.mzML");

        MzMLWriter.Write(MzMLFile.Inspect(input), single, () => new ShiftTransform(0.003),
            new MzMLWriteOptions { MaxDegreeOfParallelism = 1 });
        MzMLWriter.Write(MzMLFile.Inspect(input), many, () => new ShiftTransform(0.003),
            new MzMLWriteOptions { MaxDegreeOfParallelism = 16 });

        // Inference has no cross-row accumulation, so thread count cannot change a value:
        // assert on file bytes, which is the strongest form of the guarantee.
        Assert.Equal(File.ReadAllBytes(single), File.ReadAllBytes(many));
    }

    [Fact]
    public void EncodingIsPreservedPerArray()
    {
        // 64-bit zlib m/z beside 32-bit uncompressed intensity is the case that catches a
        // writer that reads encoding once per spectrum instead of once per array.
        string input = Path.Combine(_directory, "mixed.mzML");
        SyntheticMzML.Write(input, spectrumCount: 6, chromatogramCount: 0,
            mzEncoding: new BinaryArrayEncoding(true, true),
            intensityEncoding: new BinaryArrayEncoding(false, false));

        string output = Path.Combine(_directory, "mixed-out.mzML");
        MzMLWriter.Write(MzMLFile.Inspect(input), output, () => new NullMzTransform());

        string text = File.ReadAllText(output);
        Assert.Contains("MS:1000523", text);  // 64-bit float still declared
        Assert.Contains("MS:1000521", text);  // 32-bit float still declared
        Assert.Contains("MS:1000574", text);  // zlib still declared

        MzMLComparison comparison = MzMLComparer.Compare(input, output);
        Assert.True(comparison.MzBitIdentical);
        Assert.True(comparison.IntensityBitIdentical);
    }

    /// <summary>
    /// Regression: a payload larger than the decompressor's internal buffer must still
    /// decode. Inflating in place used to corrupt the compressed bytes still waiting to be
    /// read, which no small spectrum could ever surface -- the whole payload fit in one
    /// buffered read, so the aliasing never mattered until an array got big.
    /// </summary>
    [Fact]
    public void LargeCompressedArraysDecodeCorrectly()
    {
        string input = Path.Combine(_directory, "large.mzML");
        SyntheticMzML.Write(input, spectrumCount: 3, chromatogramCount: 0, peaksPerSpectrum: 20000);

        // Well past the 8 KB the decompressor buffers internally.
        var info = MzMLFile.Inspect(input);
        var peakCounts = new List<int>();
        foreach (SpectrumRecord spectrum in MzMLFile.ReadSpectra(info, msLevel: null))
            peakCounts.Add(spectrum.PeakCount);

        Assert.Equal(3, peakCounts.Count);
        Assert.All(peakCounts, count => Assert.Equal(20000, count));

        string output = Path.Combine(_directory, "large-out.mzML");
        MzMLWriter.Write(info, output, () => new NullMzTransform());

        MzMLComparison comparison = MzMLComparer.Compare(input, output);
        Assert.Equal(60000, comparison.MzValuesCompared);
        Assert.True(comparison.MzBitIdentical, string.Join("; ", comparison.Problems));
        Assert.True(comparison.IntensityBitIdentical);
    }

    [Fact]
    public void EncodedLengthMatchesTheNewPayload()
    {
        string input = Path.Combine(_directory, "input.mzML");
        SyntheticMzML.Write(input, spectrumCount: 5, chromatogramCount: 0);

        string output = Path.Combine(_directory, "output.mzML");
        MzMLWriter.Write(MzMLFile.Inspect(input), output, () => new ShiftTransform(0.05));

        string text = File.ReadAllText(output);
        var checkedAny = false;
        var cursor = 0;
        while (true)
        {
            int at = text.IndexOf("encodedLength=\"", cursor, StringComparison.Ordinal);
            if (at < 0) break;

            int start = at + "encodedLength=\"".Length;
            int end = text.IndexOf('"', start);
            int declared = int.Parse(text[start..end], CultureInfo.InvariantCulture);

            int binaryOpen = text.IndexOf("<binary>", end, StringComparison.Ordinal);
            int binaryClose = text.IndexOf("</binary>", end, StringComparison.Ordinal);
            if (binaryOpen < 0 || binaryClose < 0) break;

            int actual = binaryClose - (binaryOpen + "<binary>".Length);
            Assert.Equal(declared, actual);
            checkedAny = true;
            cursor = binaryClose;
        }

        Assert.True(checkedAny, "no binary arrays were checked");
    }

    private sealed class ShiftTransform : IMzTransform
    {
        private readonly double _shift;

        public ShiftTransform(double shift) => _shift = shift;

        public MzTransformResult Transform(SpectrumRecord spectrum, Span<double> corrected)
        {
            ReadOnlySpan<double> mz = spectrum.Mz;
            for (var i = 0; i < mz.Length; i++) corrected[i] = mz[i] + _shift;
            return new MzTransformResult { Rewrite = true };
        }
    }
}
