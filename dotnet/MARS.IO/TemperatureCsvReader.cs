// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Ported from mars/temperature.py.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using MARS.Core;

namespace MARS.IO;

/// <summary>
/// Loads RF-generator temperature traces exported from Xcalibur as chromatogram CSVs.
/// The export carries a few preamble lines before the "Time(min),..." header.
/// </summary>
public static class TemperatureCsvReader
{
    private static readonly Regex SourcePattern = new(@"(RF[AC]\d+)", RegexOptions.Compiled);

    public static readonly string[] DefaultSources = { "RFA2", "RFC2" };

    public static TemperatureData? Load(string path, Action<string>? log = null)
    {
        if (!File.Exists(path)) return null;

        try
        {
            string[] lines = File.ReadAllLines(path);

            var headerAt = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("Time", StringComparison.Ordinal))
                {
                    headerAt = i;
                    break;
                }
            }

            if (headerAt < 0)
            {
                log?.Invoke($"No Time header in {Path.GetFileName(path)}; skipping.");
                return null;
            }

            string[] headerFields = lines[headerAt].Split(',');
            if (headerFields.Length < 2)
            {
                log?.Invoke($"Unexpected header in {Path.GetFileName(path)}; skipping.");
                return null;
            }

            Match sourceMatch = SourcePattern.Match(headerFields[1]);
            string source = sourceMatch.Success
                ? sourceMatch.Groups[1].Value
                : Path.GetFileNameWithoutExtension(path).Split('-')[0];

            var times = new List<double>(lines.Length - headerAt);
            var temperatures = new List<double>(lines.Length - headerAt);

            for (int i = headerAt + 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0) continue;

                int comma = line.IndexOf(',');
                if (comma <= 0) continue;

                if (!double.TryParse(line.AsSpan(0, comma), NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double time))
                {
                    continue;
                }

                ReadOnlySpan<char> rest = line.AsSpan(comma + 1);
                int nextComma = rest.IndexOf(',');
                if (nextComma >= 0) rest = rest[..nextComma];

                if (!double.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out double temperature))
                    continue;

                times.Add(time);
                temperatures.Add(temperature);
            }

            if (times.Count == 0)
            {
                log?.Invoke($"No temperature points in {Path.GetFileName(path)}; skipping.");
                return null;
            }

            var data = new TemperatureData(times.ToArray(), temperatures.ToArray(), source);
            log?.Invoke($"Loaded {data.Count:N0} temperature points from {Path.GetFileName(path)} " +
                        $"(source {source}, {data.MinTemperature:F1} to {data.MaxTemperature:F1} C)");
            return data;
        }
        catch (IOException ex)
        {
            log?.Invoke($"Could not read {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Finds the traces belonging to one run. Files are named {source}-{mzml base name}.csv,
    /// for example RFA2-Ste-2024-12-02_HeLa_20msIIT_GPFDIA_400-500_14.csv.
    /// </summary>
    public static TemperatureSet Find(string mzmlPath, string? temperatureDirectory, Action<string>? log = null)
    {
        string baseName = Path.GetFileNameWithoutExtension(mzmlPath);
        string directory = string.IsNullOrEmpty(temperatureDirectory)
            ? Path.GetDirectoryName(Path.GetFullPath(mzmlPath)) ?? "."
            : temperatureDirectory;

        TemperatureData? rfa2 = Load(Path.Combine(directory, $"RFA2-{baseName}.csv"), log);
        TemperatureData? rfc2 = Load(Path.Combine(directory, $"RFC2-{baseName}.csv"), log);

        return new TemperatureSet { Rfa2 = rfa2, Rfc2 = rfc2 };
    }
}
