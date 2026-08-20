// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Ported from load_blib in mars/library.py, reading BiblioSpec libraries through the
// managed SQLite reader rather than a native provider.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using MARS.Core;
using MARS.IO.Sqlite;

namespace MARS.IO;

public static class BlibLibraryReader
{
    private sealed class SpectrumMeta
    {
        public string PeptideSequence = string.Empty;
        public string ModifiedSequence = string.Empty;
        public double PrecursorMz;
        public int PrecursorCharge;
        public double RetentionTime = double.NaN;
        public int NumPeaks;
    }

    /// <summary>
    /// Loads a BiblioSpec library.
    /// </summary>
    /// <param name="path">Path to the .blib file.</param>
    /// <param name="rtWindowMinutes">
    /// Half-width of the retention time window placed around each entry's library RT.
    /// </param>
    /// <param name="log">Progress sink.</param>
    /// <param name="recalculateFragmentMz">
    /// Recompute b and y fragment m/z from the sequence instead of trusting the stored peak
    /// m/z. A blib records OBSERVED reference-spectrum m/z, which carries the reference
    /// run's own miscalibration, so leaving this on is what makes a blib usable as ground
    /// truth at all.
    /// </param>
    /// <param name="annotatedPeaksOnly">
    /// Skip peaks the library does not annotate as a fragment ion. An unannotated peak has
    /// only its OBSERVED m/z from the reference run, so matching it measures the difference
    /// between two runs' calibration errors rather than an absolute mass error, and a
    /// library with hundreds of unannotated peaks per spectrum swamps the real fragments.
    /// The Python implementation keeps them; MARS does not, by default.
    /// </param>
    public static SpectralLibrary Load(
        string path,
        double rtWindowMinutes = 0.083,
        Action<string>? log = null,
        bool recalculateFragmentMz = true,
        bool annotatedPeaksOnly = true)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("BiblioSpec library not found.", path);

        using var database = new SqliteFile(path);

        SqliteTable refSpectra = database.Table("RefSpectra");
        SqliteTable refSpectraPeaks = database.Table("RefSpectraPeaks");

        var metaById = new Dictionary<long, SpectrumMeta>();
        int idAt = refSpectra.ColumnIndex("id");
        int peptideAt = refSpectra.ColumnIndex("peptideSeq");
        int precursorMzAt = refSpectra.ColumnIndex("precursorMZ");
        int chargeAt = refSpectra.ColumnIndex("precursorCharge");
        int modSeqAt = refSpectra.ColumnIndex("peptideModSeq");
        int rtAt = refSpectra.ColumnIndex("retentionTime");
        int numPeaksAt = refSpectra.ColumnIndex("numPeaks");

        foreach (SqliteRow row in database.Scan(refSpectra))
        {
            // "id INTEGER PRIMARY KEY" is an alias for the rowid: SQLite stores NULL in the
            // record and the real value only exists in the cell's rowid.
            long id = idAt >= 0 && !row[idAt].IsNull ? row[idAt].AsInteger() : row.RowId;
            metaById[id] = new SpectrumMeta
            {
                PeptideSequence = peptideAt >= 0 ? row[peptideAt].AsText() : string.Empty,
                ModifiedSequence = modSeqAt >= 0 ? row[modSeqAt].AsText() : string.Empty,
                PrecursorMz = precursorMzAt >= 0 ? row[precursorMzAt].AsDouble() : double.NaN,
                PrecursorCharge = chargeAt >= 0 ? (int)row[chargeAt].AsInteger() : 0,
                RetentionTime = rtAt >= 0 && !row[rtAt].IsNull ? row[rtAt].AsDouble() : double.NaN,
                NumPeaks = numPeaksAt >= 0 ? (int)row[numPeaksAt].AsInteger() : 0,
            };
        }

        log?.Invoke($"blib: {metaById.Count:N0} reference spectra");

        Dictionary<long, List<(int Position, double Mass)>> modificationsById = ReadModifications(database);
        Dictionary<long, Dictionary<int, (char IonType, int IonNumber, int Charge)>> annotationsById =
            ReadAnnotations(database);

        var builder = new SpectralLibraryBuilder(keepSequences: false, dedupeFragments: false);
        int peaksIdAt = refSpectraPeaks.ColumnIndex("RefSpectraID");
        int peakMzAt = refSpectraPeaks.ColumnIndex("peakMZ");
        int peakIntensityAt = refSpectraPeaks.ColumnIndex("peakIntensity");

        var mzBuffer = new double[512];
        var intensityBuffer = new float[512];
        long entriesWithoutPeaks = 0, recalculated = 0, annotated = 0, unannotatedSkipped = 0;

        foreach (SqliteRow row in database.Scan(refSpectraPeaks))
        {
            long id = peaksIdAt >= 0 && !row[peaksIdAt].IsNull ? row[peaksIdAt].AsInteger() : row.RowId;
            if (!metaById.TryGetValue(id, out SpectrumMeta? meta)) continue;

            int mzCount = DecodeDoubles(row[peakMzAt].AsBlob(), ref mzBuffer);
            int intensityCount = DecodeFloats(row[peakIntensityAt].AsBlob(), ref intensityBuffer);

            if (mzCount == 0 || mzCount != intensityCount)
            {
                entriesWithoutPeaks++;
                continue;
            }

            float maxIntensity = 0;
            for (var i = 0; i < intensityCount; i++)
            {
                if (intensityBuffer[i] > maxIntensity) maxIntensity = intensityBuffer[i];
            }

            string sequenceForMass = meta.ModifiedSequence.Length > 0 ? meta.ModifiedSequence : meta.PeptideSequence;
            (string stripped, List<(int, double)> parsedMods) = PeptideMass.SplitModifiedSequence(sequenceForMass);
            if (stripped.Length == 0) stripped = meta.PeptideSequence;

            // Prefer the Modifications table, which stores exact numeric deltas by position.
            List<(int Position, double Mass)>? modifications =
                modificationsById.TryGetValue(id, out List<(int, double)>? fromTable) && fromTable.Count > 0
                    ? fromTable
                    : parsedMods;

            annotationsById.TryGetValue(id, out Dictionary<int, (char IonType, int IonNumber, int Charge)>? annotations);

            double rt = meta.RetentionTime;
            builder.BeginEntry(
                sequenceForMass,
                meta.PrecursorCharge,
                meta.PrecursorMz,
                double.IsNaN(rt) ? double.NaN : rt - rtWindowMinutes,
                double.IsNaN(rt) ? double.NaN : rt + rtWindowMinutes);

            for (var i = 0; i < mzCount; i++)
            {
                double mz = mzBuffer[i];
                double intensity = maxIntensity > 0 ? intensityBuffer[i] / maxIntensity : 0.0;
                var ionType = '?';
                var ionNumber = 0;
                var charge = 1;

                if (annotations is not null && annotations.TryGetValue(i, out (char IonType, int IonNumber, int Charge) annotation))
                {
                    ionType = annotation.IonType;
                    ionNumber = annotation.IonNumber;
                    charge = annotation.Charge > 0 ? annotation.Charge : 1;
                    annotated++;

                    if (recalculateFragmentMz && ionType is 'b' or 'y' && ionNumber > 0)
                    {
                        double theoretical = PeptideMass.FragmentMz(stripped, ionType, ionNumber, charge, modifications);
                        if (!double.IsNaN(theoretical) && theoretical > 0)
                        {
                            mz = theoretical;
                            recalculated++;
                        }
                    }
                }
                else if (annotatedPeaksOnly)
                {
                    unannotatedSkipped++;
                    continue;
                }

                builder.AddFragment(mz, intensity, ionType, ionNumber, charge);
            }

            builder.EndEntry();
        }

        SpectralLibrary library = builder.Build();

        log?.Invoke($"  {library.EntryCount:N0} precursors, {library.FragmentCount:N0} fragments");
        log?.Invoke($"  {annotated:N0} annotated peaks, {recalculated:N0} fragment m/z recomputed from sequence");
        if (unannotatedSkipped > 0)
            log?.Invoke($"  {unannotatedSkipped:N0} unannotated peaks skipped");
        if (entriesWithoutPeaks > 0)
            log?.Invoke($"  {entriesWithoutPeaks:N0} spectra skipped for missing or mismatched peak arrays");

        if (annotated == 0)
        {
            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}' carries no peak annotations, so no peak in it can be identified " +
                "as a specific fragment ion. Every peak would have to be matched on its OBSERVED reference " +
                "m/z, which measures the difference between two runs' calibration errors rather than an " +
                "absolute mass error, and would train the model on noise. Use a Skyline PRISM report " +
                "(--prism-csv) or a DIA-NN library (--library report-lib.parquet) instead, or rebuild the " +
                "library with peak annotations.");
        }

        if (recalculated == 0)
        {
            log?.Invoke("  WARNING: no fragment m/z could be recomputed from sequence, so matching uses the " +
                        "reference spectra's OBSERVED m/z. Those carry the reference run's own mass error, " +
                        "which is the thing MARS is trying to remove.");
        }

        return library;
    }

    private static Dictionary<long, List<(int Position, double Mass)>> ReadModifications(SqliteFile database)
    {
        var result = new Dictionary<long, List<(int, double)>>();
        if (!database.HasTable("Modifications")) return result;

        SqliteTable table = database.Table("Modifications");
        int idAt = table.ColumnIndex("RefSpectraID");
        int positionAt = table.ColumnIndex("position");
        int massAt = table.ColumnIndex("mass");
        if (idAt < 0 || positionAt < 0 || massAt < 0) return result;

        foreach (SqliteRow row in database.Scan(table))
        {
            long id = row[idAt].AsInteger();
            if (!result.TryGetValue(id, out List<(int, double)>? list))
            {
                list = new List<(int, double)>(2);
                result[id] = list;
            }

            list.Add(((int)row[positionAt].AsInteger(), row[massAt].AsDouble()));
        }

        return result;
    }

    private static Dictionary<long, Dictionary<int, (char IonType, int IonNumber, int Charge)>> ReadAnnotations(
        SqliteFile database)
    {
        var result = new Dictionary<long, Dictionary<int, (char, int, int)>>();
        if (!database.HasTable("RefSpectraPeakAnnotations")) return result;

        SqliteTable table = database.Table("RefSpectraPeakAnnotations");
        int idAt = table.ColumnIndex("RefSpectraID");
        int peakIndexAt = table.ColumnIndex("peakIndex");
        int nameAt = table.ColumnIndex("name");
        int chargeAt = table.ColumnIndex("charge");
        if (idAt < 0 || peakIndexAt < 0 || nameAt < 0) return result;

        foreach (SqliteRow row in database.Scan(table))
        {
            long id = row[idAt].AsInteger();
            var peakIndex = (int)row[peakIndexAt].AsInteger();
            string name = row[nameAt].AsText();
            int charge = chargeAt >= 0 ? (int)row[chargeAt].AsInteger() : 1;

            (char ionType, int ionNumber) = PrismCsvLibraryReader.ParseFragmentIon(name);

            if (!result.TryGetValue(id, out Dictionary<int, (char, int, int)>? peaks))
            {
                peaks = new Dictionary<int, (char, int, int)>();
                result[id] = peaks;
            }

            // A peak can carry several annotations; the first wins, as in the Python loader.
            peaks.TryAdd(peakIndex, (ionType, ionNumber, charge));
        }

        return result;
    }

    private static int DecodeDoubles(ReadOnlySpan<byte> blob, ref double[] destination)
    {
        byte[] raw = Decompress(blob, out int length);
        int count = length / 8;
        if (destination.Length < count) destination = new double[Math.Max(count, 512)];
        MemoryMarshal.Cast<byte, double>(raw.AsSpan(0, count * 8)).CopyTo(destination.AsSpan(0, count));
        return count;
    }

    private static int DecodeFloats(ReadOnlySpan<byte> blob, ref float[] destination)
    {
        byte[] raw = Decompress(blob, out int length);
        int count = length / 4;
        if (destination.Length < count) destination = new float[Math.Max(count, 512)];
        MemoryMarshal.Cast<byte, float>(raw.AsSpan(0, count * 4)).CopyTo(destination.AsSpan(0, count));
        return count;
    }

    /// <summary>
    /// BiblioSpec compresses a peak blob only when that makes it smaller, so a blob may be
    /// either zlib or raw little-endian values.
    /// </summary>
    private static byte[] Decompress(ReadOnlySpan<byte> blob, out int length)
    {
        if (blob.Length == 0)
        {
            length = 0;
            return Array.Empty<byte>();
        }

        if (blob.Length >= 2 && blob[0] == 0x78)
        {
            try
            {
                using var input = new MemoryStream(blob.ToArray(), writable: false);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream(blob.Length * 4);
                zlib.CopyTo(output);
                length = (int)output.Length;
                return output.GetBuffer();
            }
            catch (InvalidDataException)
            {
                // Not actually compressed; fall through to the raw path.
            }
        }

        length = blob.Length;
        return blob.ToArray();
    }
}
