// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Passthrough mzML reader and writer.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MARS.Core;

namespace MARS.IO;

/// <summary>What MARS needs to know about a file before streaming it.</summary>
public sealed class MzMLFileInfo
{
    public required string Path { get; init; }

    public required long Length { get; init; }

    /// <summary>Run startTimeStamp as a Unix timestamp in seconds, or null when absent.</summary>
    public required double? AcquisitionStartTime { get; init; }

    /// <summary>
    /// Byte offset where the copied content stops: the start of the existing indexList, or
    /// the end of the mzML element for an unindexed file. Everything before it is preserved
    /// byte for byte; everything after it is regenerated.
    /// </summary>
    public required long ContentCutOffset { get; init; }

    public required bool WasIndexed { get; init; }

    /// <summary>
    /// True when the root element is indexedmzML. A plain mzML has nowhere to put an index,
    /// so MARS copies it through unindexed rather than producing invalid XML.
    /// </summary>
    public required bool IsIndexedMzML { get; init; }

    /// <summary>
    /// Measuring analyzer accession per instrumentConfiguration id, from the file header.
    /// Empty when the header did not say.
    /// </summary>
    public IReadOnlyDictionary<string, string> AnalyzerByConfiguration { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// The run's defaultInstrumentConfigurationRef, used by spectra that do not name one.
    /// </summary>
    public string? DefaultConfiguration { get; init; }
}

public static class MzMLFile
{
    private const int HeaderProbeBytes = 512 * 1024;
    private const int TailProbeBytes = 64 * 1024;

    /// <summary>
    /// Reads the run header and locates where the regenerated trailer begins, without
    /// scanning the body.
    /// </summary>
    public static MzMLFileInfo Inspect(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("mzML file not found.", path);

        double? acquisitionStart = null;
        string? defaultConfiguration = null;
        Dictionary<string, string> analyzers;
        bool indexedRoot;
        using (FileStream stream = File.OpenRead(path))
        {
            int probeLength = (int)Math.Min(HeaderProbeBytes, file.Length);
            var probe = new byte[probeLength];
            int read = stream.Read(probe, 0, probeLength);
            string header = Encoding.UTF8.GetString(probe, 0, read);

            indexedRoot = header.Contains("<indexedmzML", StringComparison.Ordinal);

            analyzers = ParseAnalyzers(header);

            const string configMarker = "defaultInstrumentConfigurationRef=\"";
            int refAt = header.IndexOf(configMarker, StringComparison.Ordinal);
            if (refAt >= 0)
            {
                int start = refAt + configMarker.Length;
                int end = header.IndexOf('"', start);
                if (end > start) defaultConfiguration = header[start..end];
            }

            const string marker = "startTimeStamp=\"";
            int at = header.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0)
            {
                int start = at + marker.Length;
                int end = header.IndexOf('"', start);
                if (end > start) acquisitionStart = MzMLSpectrumParser.ParseStartTimeStamp(header[start..end]);
            }
        }

        (long cut, bool indexed) = FindContentCut(path, file.Length);

        return new MzMLFileInfo
        {
            Path = path,
            Length = file.Length,
            AcquisitionStartTime = acquisitionStart,
            ContentCutOffset = cut,
            WasIndexed = indexed,
            IsIndexedMzML = indexedRoot,
            AnalyzerByConfiguration = analyzers,
            DefaultConfiguration = defaultConfiguration,
        };
    }

    /// <summary>
    /// Reads instrumentConfiguration ids and the analyzer each one measures with, out of the
    /// header text already in hand.
    /// </summary>
    /// <remarks>
    /// Deliberately a scan of the header probe rather than an XML parse: this runs on files
    /// of several gigabytes whose header MARS otherwise only reads to find two attributes,
    /// and a configuration list that falls outside the probe is a missing answer rather than
    /// a wrong one - detection degrades to "unknown" and the caller keeps its default.
    /// </remarks>
    private static Dictionary<string, string> ParseAnalyzers(string header)
    {
        var byConfiguration = new Dictionary<string, string>(StringComparer.Ordinal);

        const string open = "<instrumentConfiguration id=\"";
        int at = header.IndexOf(open, StringComparison.Ordinal);
        while (at >= 0)
        {
            int idStart = at + open.Length;
            int idEnd = header.IndexOf('"', idStart);
            if (idEnd < 0) break;

            string id = header[idStart..idEnd];
            int close = header.IndexOf("</instrumentConfiguration>", idEnd, StringComparison.Ordinal);
            int next = header.IndexOf(open, idEnd, StringComparison.Ordinal);
            if (close < 0) close = next >= 0 ? next : header.Length;

            var analyzers = new List<(int Order, string Accession)>();
            foreach ((int order, string accession) in ScanAnalyzers(header, idEnd, close))
                analyzers.Add((order, accession));

            if (MassAnalyzers.MeasuringAnalyzer(analyzers) is string measuring)
                byConfiguration[id] = measuring;

            at = next;
        }

        return byConfiguration;
    }

    private static IEnumerable<(int Order, string Accession)> ScanAnalyzers(string header, int from, int to)
    {
        const string open = "<analyzer order=\"";
        int at = header.IndexOf(open, from, StringComparison.Ordinal);
        while (at >= 0 && at < to)
        {
            int orderStart = at + open.Length;
            int orderEnd = header.IndexOf('"', orderStart);
            if (orderEnd < 0 || orderEnd > to) yield break;

            int close = header.IndexOf("</analyzer>", orderEnd, StringComparison.Ordinal);
            if (close < 0 || close > to) close = to;

            if (int.TryParse(header.AsSpan(orderStart, orderEnd - orderStart), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int order))
            {
                // The first accession inside the element is the analyzer type; anything after
                // it describes the analyzer rather than naming it.
                const string accessionMarker = "accession=\"";
                int accessionAt = header.IndexOf(accessionMarker, orderEnd, StringComparison.Ordinal);
                if (accessionAt >= 0 && accessionAt < close)
                {
                    int accessionStart = accessionAt + accessionMarker.Length;
                    int accessionEnd = header.IndexOf('"', accessionStart);
                    if (accessionEnd > accessionStart)
                        yield return (order, header[accessionStart..accessionEnd]);
                }
            }

            at = header.IndexOf(open, close, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Works out which analyzer recorded this run's MS2 spectra - the ones MARS calibrates.
    /// </summary>
    /// <remarks>
    /// The run's default configuration is not the answer on its own. A file from an Orbitrap
    /// Astral names the orbitrap as the run default because that is what takes the MS1
    /// survey, and points each MS2 spectrum at a second configuration for the Astral
    /// analyzer. Reading only the header would classify such a run by its MS1 analyzer, which
    /// on a hybrid instrument is exactly the wrong one.
    /// </remarks>
    public static MassAnalyzerClass DetectMs2Analyzer(MzMLFileInfo info)
    {
        if (info.AnalyzerByConfiguration.Count == 0) return MassAnalyzerClass.Unknown;

        // One configuration means every spectrum used it, and no spectrum needs reading.
        if (info.AnalyzerByConfiguration.Count == 1)
        {
            foreach (string accession in info.AnalyzerByConfiguration.Values)
                return MassAnalyzers.Classify(accession);
        }

        foreach (SpectrumRecord record in ReadSpectra(info, msLevel: 2))
        {
            string? configuration = record.InstrumentConfigurationRef ?? info.DefaultConfiguration;
            if (configuration is not null &&
                info.AnalyzerByConfiguration.TryGetValue(configuration, out string? accession))
            {
                return MassAnalyzers.Classify(accession);
            }

            // The file has several configurations but this spectrum does not say which, so
            // the header cannot settle it. Thermo's filter string can.
            return MassAnalyzers.ClassifyFilterString(record.FilterString);
        }

        return MassAnalyzerClass.Unknown;
    }

    /// <summary>
    /// Finds where the preserved content ends. An indexed file records the offset of its own
    /// indexList in the trailer, so this costs one small read rather than a scan.
    /// </summary>
    private static (long Cut, bool Indexed) FindContentCut(string path, long fileLength)
    {
        int tailLength = (int)Math.Min(TailProbeBytes, fileLength);
        var tail = new byte[tailLength];
        using (FileStream stream = File.OpenRead(path))
        {
            stream.Seek(fileLength - tailLength, SeekOrigin.Begin);
            ReadExactly(stream, tail, tailLength);
        }

        string text = Encoding.UTF8.GetString(tail);
        const string offsetOpen = "<indexListOffset>";
        const string offsetClose = "</indexListOffset>";
        int at = text.LastIndexOf(offsetOpen, StringComparison.Ordinal);
        if (at >= 0)
        {
            int start = at + offsetOpen.Length;
            int end = text.IndexOf(offsetClose, start, StringComparison.Ordinal);
            if (end > start &&
                long.TryParse(text.AsSpan(start, end - start).Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out long recorded) &&
                recorded > 0 && recorded < fileLength)
            {
                long cutAt = FindIndexListAt(path, recorded);
                if (cutAt >= 0) return (cutAt, true);
            }
        }

        // Unindexed, or the recorded offset does not point at an indexList. Fall back to the
        // end of the mzML element and write an index the file did not previously have.
        const string mzmlClose = "</mzML>";
        int closeAt = text.LastIndexOf(mzmlClose, StringComparison.Ordinal);
        if (closeAt < 0)
            throw new InvalidDataException($"No </mzML> found near the end of {path}; this does not look like an mzML file.");

        long cut = fileLength - tailLength + Encoding.UTF8.GetByteCount(text[..(closeAt + mzmlClose.Length)]);
        return (cut, false);
    }

    /// <summary>
    /// Confirms the recorded offset really points at an indexList and returns the offset of
    /// its opening angle bracket. Some writers record the start of the indentation rather
    /// than the tag, so leading whitespace is skipped and kept in the preserved content.
    /// Returns -1 when there is no indexList there.
    /// </summary>
    private static long FindIndexListAt(string path, long offset)
    {
        using FileStream stream = File.OpenRead(path);
        stream.Seek(offset, SeekOrigin.Begin);
        var probe = new byte[32];
        int read = stream.Read(probe, 0, probe.Length);
        if (read <= 0) return -1;

        int skipped = 0;
        while (skipped < read &&
               (probe[skipped] == (byte)' ' || probe[skipped] == (byte)'\t' ||
                probe[skipped] == (byte)'\r' || probe[skipped] == (byte)'\n'))
        {
            skipped++;
        }

        string text = Encoding.UTF8.GetString(probe, skipped, read - skipped);
        return text.StartsWith("<indexList", StringComparison.Ordinal) ? offset + skipped : -1;
    }

    /// <summary>
    /// Streams spectra. The arrays on the yielded record are reused between iterations, so
    /// a consumer that needs to keep them must copy.
    /// </summary>
    public static IEnumerable<SpectrumRecord> ReadSpectra(MzMLFileInfo info, int? msLevel = 2)
    {
        using FileStream input = new(info.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 20, FileOptions.SequentialScan);
        using var scanner = new MzMLSpanScanner(input, info.ContentCutOffset);

        var context = new DecodeContext();

        while (scanner.TryReadRegion(out MzMLRegionKind kind, out int start, out int length))
        {
            if (kind != MzMLRegionKind.Spectrum)
            {
                scanner.Advance(length);
                continue;
            }

            ParsedSpectrum parsed = MzMLSpectrumParser.Parse(scanner.Buffer, start, length);
            SpectrumRecord record = parsed.Record;

            bool wanted = !msLevel.HasValue || record.MsLevel == msLevel.Value;
            if (wanted && context.Decode(scanner.Buffer, start, parsed, record))
            {
                record.AcquisitionStartTime = info.AcquisitionStartTime;
                record.AbsoluteTime = info.AcquisitionStartTime is double t
                    ? t + (record.RetentionTime * 60.0)
                    : record.RetentionTime * 60.0;

                yield return record;
            }

            scanner.Advance(length);
        }
    }

    internal static void ReadExactly(Stream stream, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer, total, count - total);
            if (read <= 0) throw new EndOfStreamException();
            total += read;
        }
    }

    /// <summary>Scratch buffers for decoding one spectrum's arrays.</summary>
    internal sealed class DecodeContext
    {
        private byte[] _base64 = new byte[1 << 16];
        private byte[] _inflated = new byte[1 << 16];
        private double[] _mz = new double[4096];
        private double[] _intensity = new double[4096];

        /// <summary>
        /// Decodes the m/z and intensity arrays into the record. Returns false when the
        /// spectrum carries no peaks.
        /// </summary>
        public bool Decode(byte[] buffer, int spanStart, ParsedSpectrum parsed, SpectrumRecord record)
        {
            if (parsed.MzArrayIndex < 0 || parsed.IntensityArrayIndex < 0) return false;

            BinaryArrayLocation mzArray = parsed.Arrays[parsed.MzArrayIndex];
            BinaryArrayLocation intensityArray = parsed.Arrays[parsed.IntensityArrayIndex];
            if (mzArray.BinaryTextStart < 0 || intensityArray.BinaryTextStart < 0) return false;

            int mzCount = MzMLBinaryCodec.Decode(
                new ReadOnlySpan<byte>(buffer, spanStart + mzArray.BinaryTextStart, mzArray.BinaryTextLength),
                mzArray.Encoding, ref _base64, ref _inflated, ref _mz);

            int intensityCount = MzMLBinaryCodec.Decode(
                new ReadOnlySpan<byte>(buffer, spanStart + intensityArray.BinaryTextStart, intensityArray.BinaryTextLength),
                intensityArray.Encoding, ref _base64, ref _inflated, ref _intensity);

            if (mzCount == 0 || mzCount != intensityCount) return false;

            record.MzArray = _mz;
            record.IntensityArray = _intensity;
            record.PeakCount = mzCount;

            double sum = 0;
            for (int i = 0; i < mzCount; i++) sum += _intensity[i];
            record.SummedIntensity = sum;
            return true;
        }
    }
}
