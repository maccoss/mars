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
        public double StartTime = double.NaN;
        public double EndTime = double.NaN;
        public int NumPeaks;
    }

    /// <summary>How close a peak must sit to a computed b or y ion to be named after it.</summary>
    public const double InferenceTolerancePpm = 2.0;

    /// <summary>
    /// Share of an unannotated library's peaks that must be named by m/z before the library is
    /// taken to hold theoretical m/z. An Osprey result puts all of them there; a library of
    /// observed spectra almost none (0.02% on the unit-resolution example), so the threshold
    /// only needs to leave room for ions the annotator does not compute, such as neutral losses.
    /// </summary>
    public const double InferenceMinimumFraction = 0.8;

    /// <summary>
    /// Loads a BiblioSpec library.
    /// </summary>
    /// <param name="path">Path to the .blib file.</param>
    /// <param name="rtWindowMinutes">
    /// Half-width of the retention time window placed around an entry's library RT, used only
    /// when the library records no elution boundaries for that entry.
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
    /// <param name="runNames">
    /// mzML file names being processed. Per-run elution boundaries in the library's
    /// <c>RetentionTimes</c> table are taken from the source files that match these; empty
    /// or null takes them from every source file.
    /// </param>
    /// <remarks>
    /// A library with no peak annotations at all is accepted only if its peaks turn out to be
    /// theoretical: each is named as the b or y ion it sits on, and at least
    /// <see cref="InferenceMinimumFraction"/> of them have to sit on one. Osprey's result
    /// .blib is that kind of library.
    /// </remarks>
    public static SpectralLibrary Load(
        string path,
        double rtWindowMinutes = 0.083,
        Action<string>? log = null,
        bool recalculateFragmentMz = true,
        bool annotatedPeaksOnly = true,
        IReadOnlyList<string>? runNames = null)
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
        int startAt = refSpectra.ColumnIndex("startTime");
        int endAt = refSpectra.ColumnIndex("endTime");
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
                StartTime = startAt >= 0 && !row[startAt].IsNull ? row[startAt].AsDouble() : double.NaN,
                EndTime = endAt >= 0 && !row[endAt].IsNull ? row[endAt].AsDouble() : double.NaN,
                NumPeaks = numPeaksAt >= 0 ? (int)row[numPeaksAt].AsInteger() : 0,
            };
        }

        log?.Invoke($"blib: {metaById.Count:N0} reference spectra");

        Dictionary<long, List<(int Position, double Mass)>> modificationsById = ReadModifications(database);
        Dictionary<long, Dictionary<int, (char IonType, int IonNumber, int Charge)>> annotationsById =
            ReadAnnotations(database);
        Dictionary<long, (double Start, double End)> runWindows = ReadRunWindows(database, runNames, log);

        // No annotation anywhere: the peaks can still be named if their m/z are theoretical,
        // which is decided once every peak has been tried.
        bool inferAnnotations = annotationsById.Count == 0;

        var builder = new SpectralLibraryBuilder(keepSequences: false, dedupeFragments: false);
        int peaksIdAt = refSpectraPeaks.ColumnIndex("RefSpectraID");
        int peakMzAt = refSpectraPeaks.ColumnIndex("peakMZ");
        int peakIntensityAt = refSpectraPeaks.ColumnIndex("peakIntensity");

        var mzBuffer = new double[512];
        var intensityBuffer = new float[512];
        var ionBuffer = new (char IonType, int IonNumber, int Charge)[512];
        long entriesWithoutPeaks = 0, recalculated = 0, annotated = 0, unannotatedSkipped = 0;
        long entriesWithUnweighedMods = 0, peaksTried = 0;
        long windowsFromRuns = 0, windowsFromBoundaries = 0, windowsFromRt = 0;

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
            (string stripped, List<(int, double)> parsedMods, int unweighedMods) =
                PeptideMass.SplitModifiedSequence(sequenceForMass);
            if (stripped.Length == 0) stripped = meta.PeptideSequence;

            // Prefer the Modifications table, which stores exact numeric deltas by position.
            bool haveTable = modificationsById.TryGetValue(id, out List<(int, double)>? fromTable) &&
                             fromTable.Count > 0;
            List<(int Position, double Mass)>? modifications = haveTable ? fromTable : parsedMods;

            // A modification named rather than weighed - C[Carbamidomethyl] - has no mass here.
            // With no Modifications table to fall back on, a recalculated fragment would be
            // computed from the unmodified residue and be wrong by the delta, so keep the m/z
            // the library recorded instead.
            bool canRecalculate = haveTable || unweighedMods == 0;
            if (!canRecalculate) entriesWithUnweighedMods++;

            annotationsById.TryGetValue(id, out Dictionary<int, (char IonType, int IonNumber, int Charge)>? annotations);

            if (inferAnnotations)
            {
                if (ionBuffer.Length < mzCount) ionBuffer = new (char, int, int)[Math.Max(mzCount, 512)];
                peaksTried += mzCount;

                // A modification that cannot be weighed leaves no theoretical m/z to compare
                // against, so those peaks stay unnamed and count against the library.
                if (canRecalculate)
                {
                    FragmentAnnotator.Annotate(
                        stripped, modifications, Math.Min(Math.Max(meta.PrecursorCharge, 1), 3),
                        mzBuffer.AsSpan(0, mzCount), InferenceTolerancePpm, ionBuffer);
                }
                else
                {
                    ionBuffer.AsSpan(0, mzCount).Fill(FragmentAnnotator.Unassigned);
                }
            }

            (double rtStart, double rtEnd) = ElutionWindow(
                id, meta, runWindows, rtWindowMinutes, ref windowsFromRuns, ref windowsFromBoundaries, ref windowsFromRt);
            builder.BeginEntry(sequenceForMass, meta.PrecursorCharge, meta.PrecursorMz, rtStart, rtEnd);

            for (var i = 0; i < mzCount; i++)
            {
                double mz = mzBuffer[i];
                double intensity = maxIntensity > 0 ? intensityBuffer[i] / maxIntensity : 0.0;
                var ionType = '?';
                var ionNumber = 0;
                var charge = 1;

                (char IonType, int IonNumber, int Charge) annotation = FragmentAnnotator.Unassigned;
                bool known;
                if (inferAnnotations)
                {
                    annotation = ionBuffer[i];
                    known = annotation.IonType != '?';
                }
                else
                {
                    known = annotations is not null && annotations.TryGetValue(i, out annotation);
                }

                if (known)
                {
                    ionType = annotation.IonType;
                    ionNumber = annotation.IonNumber;
                    charge = annotation.Charge > 0 ? annotation.Charge : 1;
                    annotated++;

                    if (recalculateFragmentMz && canRecalculate && ionType is 'b' or 'y' && ionNumber > 0)
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

        if (entriesWithUnweighedMods > 0)
        {
            log?.Invoke(
                $"  {entriesWithUnweighedMods:N0} entries name a modification without giving its " +
                "mass and have no Modifications table; their recorded fragment m/z is used as-is");
        }

        log?.Invoke($"  elution windows: {windowsFromRuns:N0} from per-run boundaries, " +
                    $"{windowsFromBoundaries:N0} from library boundaries, " +
                    $"{windowsFromRt:N0} from library RT +/- {rtWindowMinutes:0.###} min");

        if (inferAnnotations)
        {
            double fraction = peaksTried > 0 ? (double)annotated / peaksTried : 0.0;
            if (fraction < InferenceMinimumFraction)
            {
                throw new InvalidDataException(
                    $"'{Path.GetFileName(path)}' carries no peak annotations, and its peak m/z are not " +
                    $"theoretical: {fraction:P1} of them lie within {InferenceTolerancePpm:0.#} ppm of a b or y " +
                    "ion computed from the sequence, where a library of computed fragment m/z - an Osprey " +
                    "result, for one - puts nearly all of them there. The rest would have to be matched on " +
                    "their OBSERVED reference m/z, which measures the difference between two runs' calibration " +
                    "errors rather than an absolute mass error, and would train the model on noise. Use a " +
                    "Skyline PRISM report (--prism-report) or a DIA-NN library (--library report-lib.parquet) " +
                    "instead, or rebuild the library with peak annotations.");
            }

            log?.Invoke(
                $"  no peak annotations; {annotated:N0} of {peaksTried:N0} peaks ({fraction:P1}) named as b or y " +
                $"ions within {InferenceTolerancePpm:0.#} ppm, so the library's m/z are theoretical");
        }

        if (annotated == 0)
        {
            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}' carries no peak annotations, so no peak in it can be identified " +
                "as a specific fragment ion. Every peak would have to be matched on its OBSERVED reference " +
                "m/z, which measures the difference between two runs' calibration errors rather than an " +
                "absolute mass error, and would train the model on noise. Use a Skyline PRISM report " +
                "(--prism-report) or a DIA-NN library (--library report-lib.parquet) instead, or rebuild the " +
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

    /// <summary>
    /// The window an entry is matched in: its elution boundaries in the runs being processed
    /// when the library records them, else its own boundaries, else a fixed window around its
    /// retention time.
    /// </summary>
    private static (double Start, double End) ElutionWindow(
        long id,
        SpectrumMeta meta,
        Dictionary<long, (double Start, double End)> runWindows,
        double rtWindowMinutes,
        ref long fromRuns,
        ref long fromBoundaries,
        ref long fromRt)
    {
        if (runWindows.TryGetValue(id, out (double Start, double End) window))
        {
            fromRuns++;
            return window;
        }

        if (meta.EndTime > meta.StartTime)
        {
            fromBoundaries++;
            return (meta.StartTime, meta.EndTime);
        }

        fromRt++;
        double rt = meta.RetentionTime;
        return double.IsNaN(rt) ? (double.NaN, double.NaN) : (rt - rtWindowMinutes, rt + rtWindowMinutes);
    }

    /// <summary>
    /// Per-entry elution windows from the <c>RetentionTimes</c> table, widened across every
    /// source file that matches a run being processed.
    /// </summary>
    /// <remarks>
    /// Only rows with real boundaries count. BiblioSpec writes this table for every library,
    /// but a library built from DDA searches leaves the start and end NULL or zero; Osprey
    /// fills them with each run's integration boundaries.
    /// </remarks>
    private static Dictionary<long, (double Start, double End)> ReadRunWindows(
        SqliteFile database,
        IReadOnlyList<string>? runNames,
        Action<string>? log)
    {
        var result = new Dictionary<long, (double, double)>();
        if (!database.HasTable("RetentionTimes") || !database.HasTable("SpectrumSourceFiles")) return result;

        SqliteTable sources = database.Table("SpectrumSourceFiles");
        int sourceIdAt = sources.ColumnIndex("id");
        int fileNameAt = sources.ColumnIndex("fileName");

        SqliteTable times = database.Table("RetentionTimes");
        int refIdAt = times.ColumnIndex("RefSpectraID");
        int sourceAt = times.ColumnIndex("SpectrumSourceID");
        int startAt = times.ColumnIndex("startTime");
        int endAt = times.ColumnIndex("endTime");
        if (fileNameAt < 0 || refIdAt < 0 || sourceAt < 0 || startAt < 0 || endAt < 0) return result;

        var filter = new RunNameFilter(runNames ?? Array.Empty<string>());
        var selected = new HashSet<long>();
        var sourceCount = 0;
        foreach (SqliteRow row in database.Scan(sources))
        {
            sourceCount++;
            long id = sourceIdAt >= 0 && !row[sourceIdAt].IsNull ? row[sourceIdAt].AsInteger() : row.RowId;
            if (filter.Matches(FileNameOf(row[fileNameAt].AsText()))) selected.Add(id);
        }

        if (selected.Count == 0)
        {
            if (sourceCount > 0)
            {
                log?.Invoke($"  none of the library's {sourceCount} source files matches a run being processed " +
                            $"({filter.Describe()}); its per-run elution boundaries are not used");
            }

            return result;
        }

        foreach (SqliteRow row in database.Scan(times))
        {
            if (row[refIdAt].IsNull || row[sourceAt].IsNull || row[startAt].IsNull || row[endAt].IsNull) continue;
            if (!selected.Contains(row[sourceAt].AsInteger())) continue;

            double start = row[startAt].AsDouble();
            double end = row[endAt].AsDouble();
            if (!(end > start)) continue;

            long id = row[refIdAt].AsInteger();
            result[id] = result.TryGetValue(id, out (double Start, double End) known)
                ? (Math.Min(known.Start, start), Math.Max(known.End, end))
                : (start, end);
        }

        if (result.Count > 0 && filter.Active)
            log?.Invoke($"  per-run elution boundaries from {selected.Count} of {sourceCount} source files");

        return result;
    }

    /// <summary>
    /// The file name from a stored path, whichever separator wrote it. A library built on
    /// Windows and read elsewhere still holds backslashes, which Path.GetFileName on Linux or
    /// macOS does not split on.
    /// </summary>
    private static string FileNameOf(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

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
