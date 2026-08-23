// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Writes the matched-fragment table as CSV, one row per match.

using System;
using System.Globalization;
using System.IO;
using System.Text;
using MARS.Core;

namespace MARS.IO;

/// <summary>
/// Dumps a <see cref="MatchTable"/> to CSV.
///
/// This exists to answer "which peak did MARS actually match, and what did it compute from
/// it" without a debugger. It is also the join point for comparing against another
/// implementation: the scan number and the library fragment index together identify a row
/// uniquely, so two dumps of the same input can be merged and differenced column by column.
///
/// Values are written round-trip ("R") so a re-read loses nothing. Rows come out in match
/// order, which is deterministic for a given input.
/// </summary>
public static class MatchDumpWriter
{
    /// <summary>Columns that identify the row, written before the feature columns.</summary>
    private static readonly string[] KeyColumns =
    {
        "scan_number", "retention_time", "entry_index", "fragment_index",
        "peptide_group", "peptide", "ion_annotation", "expected_mz", "observed_mz",
        "delta_mz", "observed_intensity",
    };

    /// <param name="predictions">
    /// Optional per-row model predictions, parallel to the table's rows. When supplied,
    /// two more columns are written: the predicted correction and the residual left after
    /// applying it. This is what makes the dump comparable against another
    /// implementation's model rather than only its features.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The table was built without detail columns, so its rows cannot be identified.
    /// </exception>
    public static void Write(
        string path, MatchTable table, SpectralLibrary library, double[]? predictions = null)
    {
        if (!table.KeepDetail)
        {
            throw new InvalidOperationException(
                "The match table was built without detail columns; construct it with " +
                "keepDetail: true before dumping.");
        }

        // Checked before a file is opened rather than discovered partway through writing
        // millions of rows, where the failure is a half-written dump and an index-out-of-range
        // with nothing in it naming the cause.
        if (predictions is not null && predictions.Length != table.Count)
        {
            throw new ArgumentException(
                $"The match table has {table.Count:N0} rows but {predictions.Length:N0} " +
                "predictions were supplied. They have to be parallel: each row's prediction is " +
                "written beside it and its residual computed from it.",
                nameof(predictions));
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // No BOM: this file exists to be read by other tools, and a BOM makes the first
        // column name compare unequal to "scan_number" in most CSV readers.
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));

        // A large cohort produces millions of rows, so this streams and reuses one
        // builder rather than composing strings per row.
        var line = new StringBuilder(512);

        for (int i = 0; i < KeyColumns.Length; i++)
        {
            if (i > 0) line.Append(',');
            line.Append(KeyColumns[i]);
        }

        foreach (MarsFeature feature in table.Collected)
        {
            line.Append(',');
            line.Append(MarsFeatures.NameOf(feature));
        }

        if (predictions is not null) line.Append(",predicted_delta_mz,residual");

        writer.WriteLine(line.ToString());

        int[] scanNumber = table.ScanNumber!.Items;
        int[] entryIndex = table.LibraryEntryIndex!.Items;
        int[] fragmentIndex = table.FragmentIndex!.Items;
        double[] observedMz = table.ObservedMz!.Items;
        double[] retentionTime = table.RetentionTime!.Items;
        double[] deltaMz = table.DeltaMz.Items;
        double[] observedIntensity = table.ObservedIntensity.Items;

        for (int row = 0; row < table.Count; row++)
        {
            line.Clear();
            int entry = entryIndex[row];
            int fragment = fragmentIndex[row];

            line.Append(scanNumber[row].ToString(CultureInfo.InvariantCulture));
            Append(line, retentionTime[row]);
            line.Append(',');
            line.Append(entry.ToString(CultureInfo.InvariantCulture));
            line.Append(',');
            line.Append(fragment.ToString(CultureInfo.InvariantCulture));
            line.Append(',');
            // The peptide, as the dense id folds are dealt over. Emitted so another
            // implementation can reproduce exactly the same split rather than approximate it.
            line.Append(table.PeptideGroup.Items[row].ToString(CultureInfo.InvariantCulture));
            line.Append(',');
            AppendQuoted(line, library.ModifiedSequence is null ? string.Empty : library.ModifiedSequence[entry]);
            line.Append(',');
            AppendAnnotation(line, library, fragment);
            Append(line, library.FragmentMz[fragment]);
            Append(line, observedMz[row]);
            Append(line, deltaMz[row]);
            Append(line, observedIntensity[row]);

            foreach (MarsFeature feature in table.Collected)
                Append(line, table.Column(feature).Items[row]);

            if (predictions is not null)
            {
                Append(line, predictions[row]);
                Append(line, deltaMz[row] - predictions[row]);
            }

            writer.WriteLine(line.ToString());
        }
    }

    private static void Append(StringBuilder line, double value)
    {
        line.Append(',');
        // NaN is meaningful here: it is how an undefined ratio reaches the model, and the
        // row-selection step drops on it. Write it rather than blanking it.
        line.Append(value.ToString("R", CultureInfo.InvariantCulture));
    }

    /// <summary>Reproduces the library's annotation form, e.g. <c>y7+1</c>.</summary>
    private static void AppendAnnotation(StringBuilder line, SpectralLibrary library, int fragment)
    {
        line.Append((char)library.FragmentIonType[fragment]);
        line.Append(library.FragmentIonNumber[fragment].ToString(CultureInfo.InvariantCulture));
        line.Append('+');
        line.Append(library.FragmentCharge[fragment].ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Peptide sequences carry modification syntax that can contain a comma, so this field
    /// is always quoted.
    /// </summary>
    private static void AppendQuoted(StringBuilder line, string value)
    {
        line.Append('"');
        line.Append(value.Replace("\"", "\"\"", StringComparison.Ordinal));
        line.Append('"');
    }
}
