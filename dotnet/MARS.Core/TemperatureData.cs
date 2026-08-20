// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Ported from mars/temperature.py.

using System;

namespace MARS.Core;

/// <summary>
/// An RF-generator temperature trace, sampled against chromatographic time.
/// Lookups are nearest-neighbor, as in the Python implementation.
/// </summary>
public sealed class TemperatureData
{
    private readonly double[] _timeMinutes;
    private readonly double[] _temperature;

    public TemperatureData(double[] timeMinutes, double[] temperature, string source)
    {
        if (timeMinutes.Length != temperature.Length)
            throw new ArgumentException("Time and temperature arrays must be the same length.");

        // Sort by time so lookups can binary search. Array.Sort with a key array is a
        // stable-enough total order here because duplicate times carry identical readings.
        var order = new int[timeMinutes.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            int c = timeMinutes[a].CompareTo(timeMinutes[b]);
            return c != 0 ? c : a.CompareTo(b);
        });

        _timeMinutes = new double[order.Length];
        _temperature = new double[order.Length];
        for (int i = 0; i < order.Length; i++)
        {
            _timeMinutes[i] = timeMinutes[order[i]];
            _temperature[i] = temperature[order[i]];
        }

        Source = source;
    }

    public string Source { get; }

    public int Count => _timeMinutes.Length;

    public double MinTemperature
    {
        get
        {
            double min = double.PositiveInfinity;
            foreach (double t in _temperature) if (t < min) min = t;
            return _temperature.Length == 0 ? double.NaN : min;
        }
    }

    public double MaxTemperature
    {
        get
        {
            double max = double.NegativeInfinity;
            foreach (double t in _temperature) if (t > max) max = t;
            return _temperature.Length == 0 ? double.NaN : max;
        }
    }

    /// <summary>
    /// Temperature at a retention time, in degrees C, using the nearest sample.
    /// Ties resolve to the earlier sample, matching numpy argmin.
    /// Returns NaN when the trace is empty.
    /// </summary>
    public double TemperatureAt(double retentionTimeMinutes)
    {
        int n = _timeMinutes.Length;
        if (n == 0) return double.NaN;

        int idx = PeakSearch.LowerBound(_timeMinutes, retentionTimeMinutes);
        if (idx == 0) return _temperature[0];
        if (idx >= n) return _temperature[n - 1];

        double distPrev = retentionTimeMinutes - _timeMinutes[idx - 1];
        double distCurr = _timeMinutes[idx] - retentionTimeMinutes;
        return distPrev <= distCurr ? _temperature[idx - 1] : _temperature[idx];
    }
}

/// <summary>The RF temperature traces available for a single run.</summary>
public sealed class TemperatureSet
{
    public TemperatureData? Rfa2 { get; init; }

    public TemperatureData? Rfc2 { get; init; }

    public bool IsEmpty => Rfa2 is null && Rfc2 is null;
}
