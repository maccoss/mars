// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Feature definitions transcribed from mars/matching.py and
// MzCalibrator._prepare_features in mars/calibration.py.

using System;
using System.Collections.Generic;

namespace MARS.Core;

/// <summary>
/// The MARS feature vocabulary, in the canonical order produced by the Python
/// <c>MzCalibrator._prepare_features</c>. The enum values ARE the model's feature order;
/// a model file records the active subset by name, and loading a model whose name list
/// does not match the extractor is a hard error. Do not reorder without a format bump.
/// </summary>
public enum MarsFeature
{
    /// <summary>Isolation window target m/z of the DIA window, Th.</summary>
    PrecursorMz = 0,

    /// <summary>Theoretical library m/z when training; the observed peak m/z when correcting.</summary>
    FragmentMz = 1,

    /// <summary>log10(max(summed spectrum intensity, 1)).</summary>
    LogTic = 2,

    /// <summary>log10(max(peak intensity, 1)).</summary>
    LogIntensity = 3,

    /// <summary>Seconds since the earliest acquisition start across the processed files.</summary>
    AbsoluteTime = 4,

    /// <summary>Ion injection time, seconds.</summary>
    InjectionTime = 5,

    /// <summary>Summed spectrum intensity multiplied by injection time.</summary>
    TicInjectionTime = 6,

    /// <summary>Peak intensity multiplied by injection time; an ion count rather than a rate.</summary>
    FragmentIons = 7,

    /// <summary>Injection-time-scaled intensity in (x + 0.5, x + 1.5] Th.</summary>
    IonsAbove01 = 8,

    /// <summary>Injection-time-scaled intensity in (x + 1.5, x + 2.5] Th.</summary>
    IonsAbove12 = 9,

    /// <summary>Injection-time-scaled intensity in (x + 2.5, x + 3.5] Th.</summary>
    IonsAbove23 = 10,

    /// <summary>Injection-time-scaled intensity in (x - 1.5, x - 0.5] Th.</summary>
    IonsBelow01 = 11,

    /// <summary>Injection-time-scaled intensity in (x - 2.5, x - 1.5] Th.</summary>
    IonsBelow12 = 12,

    /// <summary>Injection-time-scaled intensity in (x - 3.5, x - 2.5] Th.</summary>
    IonsBelow23 = 13,

    /// <summary>IonsAbove01 divided by FragmentIons, or 0 when FragmentIons is not positive.</summary>
    AdjacentRatio01 = 14,

    AdjacentRatio12 = 15,

    AdjacentRatio23 = 16,

    AdjacentRatioBelow01 = 17,

    AdjacentRatioBelow12 = 18,

    AdjacentRatioBelow23 = 19,

    /// <summary>RFA2 RF-generator temperature at this retention time, degrees C.</summary>
    Rfa2Temp = 20,

    /// <summary>RFC2 RF-generator temperature at this retention time, degrees C.</summary>
    Rfc2Temp = 21,
}

public static class MarsFeatures
{
    /// <summary>Total size of the feature vocabulary.</summary>
    public const int Count = 22;

    /// <summary>
    /// Feature names, index-aligned with <see cref="MarsFeature"/>. These strings are the
    /// on-disk contract with the model file and match the Python column names exactly.
    /// </summary>
    public static readonly string[] Names =
    {
        "precursor_mz",
        "fragment_mz",
        "log_tic",
        "log_intensity",
        "absolute_time",
        "injection_time",
        "tic_injection_time",
        "fragment_ions",
        "ions_above_0_1",
        "ions_above_1_2",
        "ions_above_2_3",
        "ions_below_0_1",
        "ions_below_1_2",
        "ions_below_2_3",
        "adjacent_ratio_0_1",
        "adjacent_ratio_1_2",
        "adjacent_ratio_2_3",
        "adjacent_ratio_below_0_1",
        "adjacent_ratio_below_1_2",
        "adjacent_ratio_below_2_3",
        "rfa2_temp",
        "rfc2_temp",
    };

    /// <summary>
    /// Neighbor-density window bounds in Th relative to the reference m/z x, as
    /// (lowExclusive, highInclusive]. Index-aligned with IonsAbove01..IonsBelow23.
    /// </summary>
    public static readonly (double Low, double High)[] NeighborWindows =
    {
        (0.5, 1.5),
        (1.5, 2.5),
        (2.5, 3.5),
        (-1.5, -0.5),
        (-2.5, -1.5),
        (-3.5, -2.5),
    };

    /// <summary>The six neighbor-density features, in canonical order.</summary>
    public static readonly MarsFeature[] NeighborFeatures =
    {
        MarsFeature.IonsAbove01,
        MarsFeature.IonsAbove12,
        MarsFeature.IonsAbove23,
        MarsFeature.IonsBelow01,
        MarsFeature.IonsBelow12,
        MarsFeature.IonsBelow23,
    };

    /// <summary>The six ratio features, index-aligned with <see cref="NeighborFeatures"/>.</summary>
    public static readonly MarsFeature[] RatioFeatures =
    {
        MarsFeature.AdjacentRatio01,
        MarsFeature.AdjacentRatio12,
        MarsFeature.AdjacentRatio23,
        MarsFeature.AdjacentRatioBelow01,
        MarsFeature.AdjacentRatioBelow12,
        MarsFeature.AdjacentRatioBelow23,
    };

    public static string NameOf(MarsFeature feature) => Names[(int)feature];

    public static bool TryParse(string name, out MarsFeature feature)
    {
        for (int i = 0; i < Names.Length; i++)
        {
            if (string.Equals(Names[i], name, StringComparison.Ordinal))
            {
                feature = (MarsFeature)i;
                return true;
            }
        }

        feature = default;
        return false;
    }

    /// <summary>
    /// True when the feature is only defined once the ion injection time is known.
    /// The Python implementation drops this whole group when no spectrum reports one.
    /// </summary>
    public static bool RequiresInjectionTime(MarsFeature feature) =>
        feature >= MarsFeature.InjectionTime && feature <= MarsFeature.AdjacentRatioBelow23;
}

/// <summary>
/// The ordered subset of the vocabulary a particular model was trained on. The Python
/// implementation selects this subset at fit time from which columns carry data.
/// </summary>
public sealed class FeatureSet
{
    private readonly int[] _slotOf; // vocabulary index -> column index, or -1

    public FeatureSet(IReadOnlyList<MarsFeature> features)
    {
        Features = new MarsFeature[features.Count];
        for (int i = 0; i < features.Count; i++) Features[i] = features[i];

        _slotOf = new int[MarsFeatures.Count];
        for (int i = 0; i < _slotOf.Length; i++) _slotOf[i] = -1;
        for (int i = 0; i < Features.Length; i++)
        {
            int v = (int)Features[i];
            if (_slotOf[v] >= 0)
                throw new ArgumentException("Duplicate feature: " + MarsFeatures.Names[v], nameof(features));
            _slotOf[v] = i;
        }
    }

    public MarsFeature[] Features { get; }

    public int Count => Features.Length;

    /// <summary>Column index of a feature in this set, or -1 when it is not present.</summary>
    public int SlotOf(MarsFeature feature) => _slotOf[(int)feature];

    public bool Contains(MarsFeature feature) => _slotOf[(int)feature] >= 0;

    /// <summary>True when any neighbor-density or ratio feature is active.</summary>
    public bool NeedsNeighborDensity
    {
        get
        {
            for (int i = 0; i < MarsFeatures.NeighborFeatures.Length; i++)
            {
                if (Contains(MarsFeatures.NeighborFeatures[i])) return true;
                if (Contains(MarsFeatures.RatioFeatures[i])) return true;
            }

            return false;
        }
    }

    public string[] Names()
    {
        var names = new string[Features.Length];
        for (int i = 0; i < Features.Length; i++) names[i] = MarsFeatures.NameOf(Features[i]);
        return names;
    }

    public static FeatureSet FromNames(IReadOnlyList<string> names)
    {
        var features = new List<MarsFeature>(names.Count);
        foreach (string name in names)
        {
            if (!MarsFeatures.TryParse(name, out MarsFeature f))
                throw new ArgumentException("Unknown MARS feature name: " + name, nameof(names));
            features.Add(f);
        }

        return new FeatureSet(features);
    }
}
