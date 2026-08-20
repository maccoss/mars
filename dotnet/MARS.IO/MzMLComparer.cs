// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Compares two mzML files on DECODED array values.
//
// Equivalence is deliberately not defined on file bytes: .NET's zlib and Python's zlib
// produce different compressed output at the same nominal level, and any writer may differ
// in whitespace. What has to match is what a consumer actually reads back.

using System;
using System.Collections.Generic;
using System.IO;
using MARS.Core;

namespace MARS.IO;

public sealed class MzMLComparison
{
    public long SpectraCompared { get; set; }

    public long SpectraOnlyInA { get; set; }

    public long SpectraOnlyInB { get; set; }

    public long MzValuesCompared { get; set; }

    public long MzValuesDiffering { get; set; }

    public long IntensityValuesDiffering { get; set; }

    public double MaxAbsoluteMzDifference { get; set; }

    public double MaxAbsoluteIntensityDifference { get; set; }

    public List<string> Problems { get; } = new();

    public bool MzBitIdentical => MzValuesDiffering == 0 && SpectraOnlyInA == 0 && SpectraOnlyInB == 0;

    public bool IntensityBitIdentical => IntensityValuesDiffering == 0;
}

public static class MzMLComparer
{
    /// <summary>
    /// Streams both files in parallel, matching spectra by id, and compares decoded m/z and
    /// intensity arrays bit for bit.
    /// </summary>
    /// <param name="maxProblemsReported">Cap on the detail list; counters stay exact.</param>
    public static MzMLComparison Compare(string pathA, string pathB, int maxProblemsReported = 20)
    {
        var result = new MzMLComparison();

        MzMLFileInfo infoA = MzMLFile.Inspect(pathA);
        MzMLFileInfo infoB = MzMLFile.Inspect(pathB);

        using IEnumerator<SpectrumRecord> a = MzMLFile.ReadSpectra(infoA, msLevel: null).GetEnumerator();
        using IEnumerator<SpectrumRecord> b = MzMLFile.ReadSpectra(infoB, msLevel: null).GetEnumerator();

        while (true)
        {
            bool hasA = a.MoveNext();
            bool hasB = b.MoveNext();

            if (!hasA && !hasB) break;
            if (!hasA)
            {
                result.SpectraOnlyInB++;
                continue;
            }

            if (!hasB)
            {
                result.SpectraOnlyInA++;
                continue;
            }

            SpectrumRecord left = a.Current;
            SpectrumRecord right = b.Current;

            if (!string.Equals(left.Id, right.Id, StringComparison.Ordinal))
            {
                if (result.Problems.Count < maxProblemsReported)
                    result.Problems.Add($"spectrum id mismatch: '{left.Id}' vs '{right.Id}'");
                result.SpectraOnlyInA++;
                result.SpectraOnlyInB++;
                continue;
            }

            result.SpectraCompared++;

            if (left.PeakCount != right.PeakCount)
            {
                if (result.Problems.Count < maxProblemsReported)
                    result.Problems.Add($"{left.Id}: peak count {left.PeakCount} vs {right.PeakCount}");
                continue;
            }

            ReadOnlySpan<double> mzA = left.Mz;
            ReadOnlySpan<double> mzB = right.Mz;
            ReadOnlySpan<double> intensityA = left.Intensity;
            ReadOnlySpan<double> intensityB = right.Intensity;

            for (int i = 0; i < left.PeakCount; i++)
            {
                result.MzValuesCompared++;

                if (BitConverter.DoubleToInt64Bits(mzA[i]) != BitConverter.DoubleToInt64Bits(mzB[i]))
                {
                    result.MzValuesDiffering++;
                    double difference = Math.Abs(mzA[i] - mzB[i]);
                    if (difference > result.MaxAbsoluteMzDifference) result.MaxAbsoluteMzDifference = difference;
                    if (result.Problems.Count < maxProblemsReported)
                    {
                        result.Problems.Add(
                            $"{left.Id} peak {i}: m/z {mzA[i]:R} vs {mzB[i]:R}");
                    }
                }

                if (BitConverter.DoubleToInt64Bits(intensityA[i]) != BitConverter.DoubleToInt64Bits(intensityB[i]))
                {
                    result.IntensityValuesDiffering++;
                    double difference = Math.Abs(intensityA[i] - intensityB[i]);
                    if (difference > result.MaxAbsoluteIntensityDifference)
                        result.MaxAbsoluteIntensityDifference = difference;
                }
            }
        }

        return result;
    }
}
