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
        bool indexedRoot;
        using (FileStream stream = File.OpenRead(path))
        {
            int probeLength = (int)Math.Min(HeaderProbeBytes, file.Length);
            var probe = new byte[probeLength];
            int read = stream.Read(probe, 0, probeLength);
            string header = Encoding.UTF8.GetString(probe, 0, read);

            indexedRoot = header.Contains("<indexedmzML", StringComparison.Ordinal);

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
        };
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
