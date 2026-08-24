// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Ported from load_prism_library in mars/library.py.

using System;
using System.Collections.Generic;
using System.IO;
using MARS.Core;

namespace MARS.IO;

public sealed class PrismLibraryOptions
{
    /// <summary>
    /// mzML file names being processed. Rows from other replicates are skipped. Empty means
    /// no filtering.
    /// </summary>
    public IReadOnlyList<string> RunNames { get; set; } = Array.Empty<string>();

    /// <summary>Keep modified sequences, for per-match reporting. Costs memory at plate scale.</summary>
    public bool KeepSequences { get; set; }

    /// <summary>
    /// Collapse transitions that repeat across replicates. A Skyline report lists every
    /// transition once per replicate with an identical theoretical Product Mz, so the copies
    /// are exact duplicates: they multiply matching work and training rows without adding
    /// information.
    /// </summary>
    public bool DedupeFragments { get; set; } = true;
}

public static class PrismCsvLibraryReader
{
    public const string PeptideColumn = "Peptide Modified Sequence Unimod Ids";
    public const string PrecursorChargeColumn = "Precursor Charge";
    public const string PrecursorMzColumn = "Precursor Mz";
    public const string FragmentIonColumn = "Fragment Ion";
    public const string ProductChargeColumn = "Product Charge";
    public const string ProductMzColumn = "Product Mz";
    public const string StartTimeColumn = "Start Time";
    public const string EndTimeColumn = "End Time";
    public const string AreaColumn = "Area";
    public const string FileNameColumn = "File Name";
    public const string ReplicateNameColumn = "Replicate Name";

    public static SpectralLibrary Load(string path, PrismLibraryOptions options, Action<string>? log = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("PRISM report not found.", path);

        using var source = new CsvPrismRowSource(path);
        return PrismLibraryLoader.Load(source, options, log);
    }

    /// <summary>
    /// Splits a Skyline fragment annotation such as "y7", "b3", "y5-H2O" into its ion type
    /// and series number. Anything unrecognized becomes '?' with number 0, which matches how
    /// the Python implementation fills unparsed annotations.
    /// </summary>
    public static (char IonType, int IonNumber) ParseFragmentIon(string annotation)
    {
        char ionType = '?';
        if (annotation.Length > 0)
        {
            char first = annotation[0];
            if (first is 'y' or 'b' or 'a' or 'z' or 'c' or 'x') ionType = first;
        }

        var ionNumber = 0;
        for (int i = 0; i < annotation.Length; i++)
        {
            if (!char.IsDigit(annotation[i])) continue;

            int j = i;
            while (j < annotation.Length && char.IsDigit(annotation[j]))
            {
                ionNumber = (ionNumber * 10) + (annotation[j] - '0');
                if (ionNumber > short.MaxValue) return (ionType, 0);
                j++;
            }

            break;
        }

        return (ionType, ionNumber);
    }
}

/// <summary>
/// Decides whether a replicate or file name in a report belongs to the run being processed.
/// Skyline reports name replicates in whatever way the analyst set up, so matching tries an
/// exact base-name match first and only then falls back to a substring test.
/// </summary>
public sealed class RunNameFilter
{
    private static readonly string[] InputSuffixes = { "_uncalibrated", "_calibrated", "-mars", ".mzML", ".mzml", ".raw" };
    private static readonly string[] ReportSuffixes = { ".mzML", ".mzml", ".raw", ".wiff", ".d", "-mars" };

    private readonly List<string> _baseNames = new();
    private readonly Dictionary<string, bool> _cache = new(StringComparer.OrdinalIgnoreCase);

    public RunNameFilter(IReadOnlyList<string> runNames)
    {
        foreach (string name in runNames)
        {
            string baseName = Path.GetFileNameWithoutExtension(name);
            foreach (string suffix in InputSuffixes)
                baseName = baseName.Replace(suffix, string.Empty, StringComparison.OrdinalIgnoreCase);
            if (baseName.Length > 0 && !_baseNames.Contains(baseName)) _baseNames.Add(baseName);
        }
    }

    public bool Active => _baseNames.Count > 0;

    public string Describe() => string.Join(", ", _baseNames);

    public bool Matches(string reportValue)
    {
        if (!Active) return true;
        if (_cache.TryGetValue(reportValue, out bool cached)) return cached;

        string normalized = reportValue;
        foreach (string suffix in ReportSuffixes)
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^suffix.Length];
                break;
            }
        }

        var matched = false;
        foreach (string baseName in _baseNames)
        {
            if (string.Equals(normalized, baseName, StringComparison.OrdinalIgnoreCase))
            {
                matched = true;
                break;
            }
        }

        if (!matched)
        {
            foreach (string baseName in _baseNames)
            {
                if (normalized.Contains(baseName, StringComparison.OrdinalIgnoreCase) ||
                    baseName.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    matched = true;
                    break;
                }
            }
        }

        if (_cache.Count < 4096) _cache[reportValue] = matched;
        return matched;
    }
}
