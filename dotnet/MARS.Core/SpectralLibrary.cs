// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Ported from the MARS Python implementation (mars/library.py).

using System;
using System.Collections.Generic;

namespace MARS.Core;

/// <summary>
/// Column-oriented spectral library.
/// <para>
/// One "entry" is a precursor: a modified sequence plus a charge state. Each entry
/// owns a contiguous run of fragments in the fragment arrays, delimited by
/// FragmentStart[i] .. FragmentStart[i + 1].
/// </para>
/// <para>
/// The struct-of-arrays layout matters: a Skyline PRISM report for a plate of Astral
/// runs is tens of millions of fragment rows, and one managed object per fragment
/// would spend more memory on object headers than on data.
/// </para>
/// </summary>
public sealed class SpectralLibrary
{
    /// <summary>Precursor m/z, in Th.</summary>
    public required double[] PrecursorMz { get; init; }

    public required int[] PrecursorCharge { get; init; }

    /// <summary>Start of the RT window used for matching, in minutes; NaN when unknown.</summary>
    public required double[] RtStart { get; init; }

    /// <summary>End of the RT window used for matching, in minutes; NaN when unknown.</summary>
    public required double[] RtEnd { get; init; }

    /// <summary>Length EntryCount + 1. Fragment range for entry i is [i], [i + 1].</summary>
    public required int[] FragmentStart { get; init; }

    /// <summary>Only populated when per-match reporting is requested; null otherwise.</summary>
    public string[]? ModifiedSequence { get; init; }

    /// <summary>Theoretical (library) fragment m/z. This is the calibration ground truth.</summary>
    public required double[] FragmentMz { get; init; }

    /// <summary>Library relative intensity or peak area. Carried for reporting only.</summary>
    public required float[] FragmentIntensity { get; init; }

    /// <summary>ASCII code of the ion type ('y', 'b', ...), or '?' when unannotated.</summary>
    public required byte[] FragmentIonType { get; init; }

    public required short[] FragmentIonNumber { get; init; }

    public required byte[] FragmentCharge { get; init; }

    public int EntryCount => PrecursorMz.Length;

    public int FragmentCount => FragmentMz.Length;

    /// <summary>
    /// Entry indices ordered by ascending precursor m/z, with the entry index as a
    /// tiebreaker so the order is total and reproducible.
    /// </summary>
    public int[] OrderByPrecursorMz()
    {
        int n = EntryCount;
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        double[] mz = PrecursorMz;
        Array.Sort(order, (a, b) =>
        {
            int c = mz[a].CompareTo(mz[b]);
            return c != 0 ? c : a.CompareTo(b);
        });
        return order;
    }
}

/// <summary>Growable builder for <see cref="SpectralLibrary"/>.</summary>
public sealed class SpectralLibraryBuilder
{
    private readonly List<double> _precursorMz = new();
    private readonly List<int> _precursorCharge = new();
    private readonly List<double> _rtStart = new();
    private readonly List<double> _rtEnd = new();
    private readonly List<int> _fragmentStart = new();
    private readonly List<string>? _modifiedSequence;

    private readonly List<double> _fragmentMz = new();
    private readonly List<float> _fragmentIntensity = new();
    private readonly List<byte> _fragmentIonType = new();
    private readonly List<short> _fragmentIonNumber = new();
    private readonly List<byte> _fragmentCharge = new();

    private readonly HashSet<long>? _fragmentKeys;

    public SpectralLibraryBuilder(bool keepSequences = false, bool dedupeFragments = true)
    {
        _modifiedSequence = keepSequences ? new List<string>() : null;
        if (dedupeFragments) _fragmentKeys = new HashSet<long>();
    }

    public int EntryCount => _precursorMz.Count;

    public int FragmentCount => _fragmentMz.Count;

    /// <summary>Opens a new entry. Subsequent AddFragment calls belong to it.</summary>
    public int BeginEntry(string modifiedSequence, int charge, double precursorMz, double rtStart, double rtEnd)
    {
        _fragmentStart.Add(_fragmentMz.Count);
        _precursorMz.Add(precursorMz);
        _precursorCharge.Add(charge);
        _rtStart.Add(rtStart);
        _rtEnd.Add(rtEnd);
        _modifiedSequence?.Add(modifiedSequence);
        _fragmentKeys?.Clear();
        return _precursorMz.Count - 1;
    }

    /// <summary>
    /// Adds a fragment to the open entry. When deduplication is on, a fragment whose
    /// (m/z, charge, ion type, ion number) already appears in this entry is dropped:
    /// Skyline reports repeat every transition once per replicate and the theoretical
    /// m/z is identical across replicates, so the copies are exact duplicates.
    /// </summary>
    public bool AddFragment(double mz, double intensity, char ionType, int ionNumber, int charge)
    {
        if (_fragmentKeys is not null)
        {
            // Quantize m/z to 1e-6 Th so bit-level noise cannot defeat the key.
            long key = (long)Math.Round(mz * 1000000.0);
            key = (key * 31) + charge;
            key = (key * 31) + ionNumber;
            key = (key * 31) + ionType;
            if (!_fragmentKeys.Add(key)) return false;
        }

        _fragmentMz.Add(mz);
        _fragmentIntensity.Add((float)intensity);
        _fragmentIonType.Add((byte)ionType);
        _fragmentIonNumber.Add((short)Math.Clamp(ionNumber, 0, short.MaxValue));
        _fragmentCharge.Add((byte)Math.Clamp(charge, 0, 255));
        return true;
    }

    /// <summary>Drops the open entry if it collected no fragments. Mirrors the Python loaders.</summary>
    public void EndEntry()
    {
        int last = _precursorMz.Count - 1;
        if (last < 0) return;
        if (_fragmentStart[last] != _fragmentMz.Count) return;

        _fragmentStart.RemoveAt(last);
        _precursorMz.RemoveAt(last);
        _precursorCharge.RemoveAt(last);
        _rtStart.RemoveAt(last);
        _rtEnd.RemoveAt(last);
        _modifiedSequence?.RemoveAt(last);
    }

    public SpectralLibrary Build()
    {
        var starts = new int[_fragmentStart.Count + 1];
        _fragmentStart.CopyTo(starts);
        starts[_fragmentStart.Count] = _fragmentMz.Count;

        return new SpectralLibrary
        {
            PrecursorMz = _precursorMz.ToArray(),
            PrecursorCharge = _precursorCharge.ToArray(),
            RtStart = _rtStart.ToArray(),
            RtEnd = _rtEnd.ToArray(),
            FragmentStart = starts,
            ModifiedSequence = _modifiedSequence?.ToArray(),
            FragmentMz = _fragmentMz.ToArray(),
            FragmentIntensity = _fragmentIntensity.ToArray(),
            FragmentIonType = _fragmentIonType.ToArray(),
            FragmentIonNumber = _fragmentIonNumber.ToArray(),
            FragmentCharge = _fragmentCharge.ToArray(),
        };
    }
}
