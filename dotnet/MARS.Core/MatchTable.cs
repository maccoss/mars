// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Column store for the fragment matches that become training rows.

using System;
using System.Collections.Generic;

namespace MARS.Core;

/// <summary>Growable array that hands back its backing store without a final copy.</summary>
public sealed class GrowableArray<T>
{
    private T[] _items;

    public GrowableArray(int capacity = 1024) => _items = new T[Math.Max(4, capacity)];

    public int Count { get; private set; }

    public T[] Items => _items;

    public T this[int i]
    {
        get => _items[i];
        set => _items[i] = value;
    }

    public void Add(T value)
    {
        if (Count == _items.Length) Array.Resize(ref _items, _items.Length * 2);
        _items[Count++] = value;
    }

    /// <summary>Discards the most recently added <paramref name="n"/> values.</summary>
    public void Truncate(int n) => Count -= n;

    public T[] ToArray()
    {
        var copy = new T[Count];
        Array.Copy(_items, copy, Count);
        return copy;
    }
}

/// <summary>
/// The matched-fragment table, stored column-major so that a 9-million-row Astral plate
/// fits without a managed object per row. One row is one library fragment matched to one
/// observed peak in one spectrum.
/// </summary>
public sealed class MatchTable
{
    private readonly GrowableArray<double>?[] _columns = new GrowableArray<double>?[MarsFeatures.Count];

    public MatchTable(IReadOnlyList<MarsFeature> collect, bool keepDetail = false)
    {
        Collected = new MarsFeature[collect.Count];
        for (int i = 0; i < collect.Count; i++)
        {
            Collected[i] = collect[i];
            _columns[(int)collect[i]] = new GrowableArray<double>();
        }

        KeepDetail = keepDetail;
        if (keepDetail)
        {
            ScanNumber = new GrowableArray<int>();
            LibraryEntryIndex = new GrowableArray<int>();
            FragmentIndex = new GrowableArray<int>();
            ObservedMz = new GrowableArray<double>();
            RetentionTime = new GrowableArray<double>();
        }
    }

    public MarsFeature[] Collected { get; }

    public bool KeepDetail { get; }

    public int Count { get; private set; }

    /// <summary>Label: observed m/z minus theoretical library m/z, in Th.</summary>
    public GrowableArray<double> DeltaMz { get; } = new();

    /// <summary>Sample weight source: intensity of the matched peak.</summary>
    public GrowableArray<double> ObservedIntensity { get; } = new();

    /// <summary>
    /// Peptide identity of the library entry this row came from. Cross-validation folds
    /// are assigned over this so a peptide never straddles a train/test boundary; it is
    /// always collected, since the split depends on it whether or not anything else does.
    /// </summary>
    public GrowableArray<int> PeptideGroup { get; } = new();

    public GrowableArray<int>? ScanNumber { get; }

    public GrowableArray<int>? LibraryEntryIndex { get; }

    /// <summary>Index into the library's flat fragment arrays, identifying which fragment
    /// of the entry matched. With the scan number this is a unique key for a row, which is
    /// what lets a dump be joined against another implementation's output.</summary>
    public GrowableArray<int>? FragmentIndex { get; }

    public GrowableArray<double>? ObservedMz { get; }

    public GrowableArray<double>? RetentionTime { get; }

    public bool Has(MarsFeature feature) => _columns[(int)feature] is not null;

    /// <summary>Backing column for a feature. Only valid for collected features.</summary>
    public GrowableArray<double> Column(MarsFeature feature) =>
        _columns[(int)feature] ?? throw new InvalidOperationException(
            "Feature not collected: " + MarsFeatures.NameOf(feature));

    public void Set(MarsFeature feature, double value) => _columns[(int)feature]?.Add(value);

    /// <summary>Commits the values staged by Set / DeltaMz.Add for the current row.</summary>
    public void CommitRow() => Count++;

    /// <summary>
    /// Adds a constant offset to a column across every row. Used to re-base absolute_time
    /// once the earliest acquisition across all input files is known.
    /// </summary>
    public void OffsetColumn(MarsFeature feature, double offset)
    {
        GrowableArray<double>? column = _columns[(int)feature];
        if (column is null) return;
        double[] values = column.Items;
        for (int i = 0; i < column.Count; i++) values[i] += offset;
    }

    public double MinOf(MarsFeature feature)
    {
        GrowableArray<double>? column = _columns[(int)feature];
        if (column is null || column.Count == 0) return double.NaN;
        double[] values = column.Items;
        double min = double.PositiveInfinity;
        for (int i = 0; i < column.Count; i++)
        {
            if (values[i] < min) min = values[i];
        }

        return min;
    }

    public double MaxOf(MarsFeature feature)
    {
        GrowableArray<double>? column = _columns[(int)feature];
        if (column is null || column.Count == 0) return double.NaN;
        double[] values = column.Items;
        double max = double.NegativeInfinity;
        for (int i = 0; i < column.Count; i++)
        {
            if (values[i] > max) max = values[i];
        }

        return max;
    }

    /// <summary>True when at least one row has a finite value in this column.</summary>
    public bool AnyFinite(MarsFeature feature)
    {
        GrowableArray<double>? column = _columns[(int)feature];
        if (column is null) return false;
        double[] values = column.Items;
        for (int i = 0; i < column.Count; i++)
        {
            if (!double.IsNaN(values[i])) return true;
        }

        return false;
    }

    /// <summary>True when every row has a finite value in this column.</summary>
    public bool AllFinite(MarsFeature feature)
    {
        GrowableArray<double>? column = _columns[(int)feature];
        if (column is null) return false;
        double[] values = column.Items;
        for (int i = 0; i < column.Count; i++)
        {
            if (double.IsNaN(values[i])) return false;
        }

        return true;
    }
}
