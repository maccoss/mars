// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Byte-splicing mzML writer: copies the input verbatim except for the m/z arrays it is
// asked to replace, then regenerates the index and checksum.

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MARS.Core;

namespace MARS.IO;

public readonly struct MzTransformResult
{
    /// <summary>False leaves the spectrum's bytes untouched.</summary>
    public bool Rewrite { get; init; }

    public int MonotonicityFixes { get; init; }

    public bool Reverted { get; init; }
}

/// <summary>Per-worker m/z transform. One instance is created per pipeline worker.</summary>
public interface IMzTransform
{
    /// <summary>Fills <paramref name="corrected"/> with the m/z values to write.</summary>
    MzTransformResult Transform(SpectrumRecord spectrum, Span<double> corrected);
}

/// <summary>
/// An identity transform: decodes and re-encodes every m/z array without changing a value.
/// This is the null correction the passthrough acceptance test runs, and it is the cheapest
/// way to prove the file-format work is right before any science is layered on top.
/// </summary>
public sealed class NullMzTransform : IMzTransform
{
    public MzTransformResult Transform(SpectrumRecord spectrum, Span<double> corrected)
    {
        spectrum.Mz.CopyTo(corrected);
        return new MzTransformResult { Rewrite = true };
    }
}

public sealed class MzMLWriteOptions
{
    /// <summary>
    /// Workers used for the per-spectrum decode, predict and re-encode. Inference carries
    /// no cross-row accumulation, so parallelizing it cannot change any result.
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; } = -1;

    /// <summary>Spectra allowed in flight. Bounds memory; output order is always preserved.</summary>
    public int MaxPendingSpectra { get; set; } = 512;
}

public sealed class MzMLWriteResult
{
    public long SpectraSeen { get; init; }

    public long SpectraCorrected { get; init; }

    public long ChromatogramsCopied { get; init; }

    public long MonotonicityFixes { get; init; }

    public long SpectraReverted { get; init; }

    public long OutputLength { get; init; }

    public string FileChecksum { get; init; } = string.Empty;

    public bool WroteIndex { get; init; }
}

public static class MzMLWriter
{
    /// <summary>
    /// Writes a corrected copy of <paramref name="info"/> to <paramref name="outputPath"/>.
    /// </summary>
    public static MzMLWriteResult Write(
        MzMLFileInfo info,
        string outputPath,
        Func<IMzTransform> workerFactory,
        MzMLWriteOptions? options = null,
        Action<string>? log = null)
    {
        options ??= new MzMLWriteOptions();
        int workers = options.MaxDegreeOfParallelism <= 0
            ? Environment.ProcessorCount
            : options.MaxDegreeOfParallelism;

        string? directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var statePool = new ConcurrentBag<WorkerState>();
        var scheduler = new ParallelOptions { MaxDegreeOfParallelism = workers };

        using FileStream input = new(info.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 20, FileOptions.SequentialScan);
        using FileStream output = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
            1 << 20, FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var scanner = new MzMLSpanScanner(input, info.ContentCutOffset);

        var spectrumIndex = new List<(string Id, long Offset)>();
        var chromatogramIndex = new List<(string Id, long Offset)>();
        var pending = new Queue<PendingItem>();

        long outputPosition = 0;
        long spectraSeen = 0, spectraCorrected = 0, monotonicityFixes = 0, spectraReverted = 0;

        void Emit(byte[] buffer, int start, int count)
        {
            output.Write(buffer, start, count);
            hash.AppendData(buffer, start, count);
            outputPosition += count;
        }

        void Drain(PendingItem item)
        {
            if (item.Work is not null)
            {
                SpectrumOutcome outcome = item.Work.GetAwaiter().GetResult();
                spectrumIndex.Add((outcome.Id, outputPosition));
                if (outcome.Corrected) spectraCorrected++;
                monotonicityFixes += outcome.MonotonicityFixes;
                if (outcome.Reverted) spectraReverted++;
                Emit(outcome.Bytes, 0, outcome.Length);
                ArrayPool<byte>.Shared.Return(outcome.Bytes);
                if (!ReferenceEquals(outcome.Bytes, item.Bytes)) ArrayPool<byte>.Shared.Return(item.Bytes!);
                return;
            }

            if (item.Kind == MzMLRegionKind.Chromatogram)
                chromatogramIndex.Add((item.Id ?? string.Empty, outputPosition));

            Emit(item.Bytes!, 0, item.Length);
            ArrayPool<byte>.Shared.Return(item.Bytes!);
        }

        while (scanner.TryReadRegion(out MzMLRegionKind kind, out int start, out int length))
        {
            if (kind == MzMLRegionKind.Spectrum)
            {
                spectraSeen++;

                byte[] copy = ArrayPool<byte>.Shared.Rent(length);
                Array.Copy(scanner.Buffer, start, copy, 0, length);
                int spanLength = length;

                Task<SpectrumOutcome> work = Task.Factory.StartNew(
                    () => ProcessSpectrum(copy, spanLength, info, workerFactory, statePool),
                    default, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

                pending.Enqueue(new PendingItem { Work = work, Bytes = copy, Length = spanLength, Kind = kind });

                while (pending.Count > options.MaxPendingSpectra) Drain(pending.Dequeue());
            }
            else if (pending.Count == 0 && kind == MzMLRegionKind.Gap)
            {
                // Nothing is in flight, so the gap can go straight out. This is the common
                // case between spectra, where the gap is a newline and some indentation.
                Emit(scanner.Buffer, start, length);
            }
            else
            {
                byte[] copy = ArrayPool<byte>.Shared.Rent(length);
                Array.Copy(scanner.Buffer, start, copy, 0, length);
                string? id = kind == MzMLRegionKind.Chromatogram
                    ? ReadIdAttribute(copy, length)
                    : null;
                pending.Enqueue(new PendingItem { Bytes = copy, Length = length, Kind = kind, Id = id });

                while (pending.Count > options.MaxPendingSpectra) Drain(pending.Dequeue());
            }

            scanner.Advance(length);
        }

        while (pending.Count > 0) Drain(pending.Dequeue());

        bool writeIndex = info.IsIndexedMzML;
        long indexListOffset = outputPosition;
        string checksum;

        if (writeIndex)
        {
            byte[] indexBytes = BuildIndex(spectrumIndex, chromatogramIndex);
            Emit(indexBytes, 0, indexBytes.Length);

            byte[] offsetLine = Encoding.UTF8.GetBytes(
                "  <indexListOffset>" + indexListOffset.ToString(CultureInfo.InvariantCulture) + "</indexListOffset>\n");
            Emit(offsetLine, 0, offsetLine.Length);

            // The mzML checksum covers every byte up to AND INCLUDING the fileChecksum
            // opening tag. Verified against pwiz-written input, whose recorded digest only
            // reproduces under that convention.
            byte[] checksumOpen = "  <fileChecksum>"u8.ToArray();
            Emit(checksumOpen, 0, checksumOpen.Length);

            checksum = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            byte[] tail = Encoding.UTF8.GetBytes(checksum + "</fileChecksum>\n</indexedmzML>");
            output.Write(tail, 0, tail.Length);
            outputPosition += tail.Length;
        }
        else
        {
            checksum = string.Empty;
            log?.Invoke("Input is a plain mzML with no indexedmzML wrapper; writing an unindexed copy. " +
                        "DIA-NN requires an indexed file, so run msconvert with indexing before MARS.");
        }

        output.Flush();

        return new MzMLWriteResult
        {
            SpectraSeen = spectraSeen,
            SpectraCorrected = spectraCorrected,
            ChromatogramsCopied = chromatogramIndex.Count,
            MonotonicityFixes = monotonicityFixes,
            SpectraReverted = spectraReverted,
            OutputLength = outputPosition,
            FileChecksum = checksum,
            WroteIndex = writeIndex,
        };
    }

    private sealed class PendingItem
    {
        public Task<SpectrumOutcome>? Work;
        public byte[]? Bytes;
        public int Length;
        public MzMLRegionKind Kind;
        public string? Id;
    }

    private readonly struct SpectrumOutcome
    {
        public SpectrumOutcome(byte[] bytes, int length, string id, bool corrected, int fixes, bool reverted)
        {
            Bytes = bytes;
            Length = length;
            Id = id;
            Corrected = corrected;
            MonotonicityFixes = fixes;
            Reverted = reverted;
        }

        public byte[] Bytes { get; }

        public int Length { get; }

        public string Id { get; }

        public bool Corrected { get; }

        public int MonotonicityFixes { get; }

        public bool Reverted { get; }
    }

    private sealed class WorkerState
    {
        public IMzTransform Transform = null!;
        public MzMLFile.DecodeContext Decode = new();
        public double[] Corrected = new double[4096];
        public byte[] RawScratch = new byte[1 << 16];
        public byte[] DeflateScratch = new byte[1 << 16];
        public byte[] Base64Scratch = new byte[1 << 16];
    }

    private static SpectrumOutcome ProcessSpectrum(
        byte[] span,
        int length,
        MzMLFileInfo info,
        Func<IMzTransform> workerFactory,
        ConcurrentBag<WorkerState> statePool)
    {
        if (!statePool.TryTake(out WorkerState? state))
            state = new WorkerState { Transform = workerFactory() };

        try
        {
            ParsedSpectrum parsed = MzMLSpectrumParser.Parse(span, 0, length);
            SpectrumRecord record = parsed.Record;

            if (parsed.MzArrayIndex < 0 || !state.Decode.Decode(span, 0, parsed, record))
                return new SpectrumOutcome(span, length, record.Id, false, 0, false);

            record.AcquisitionStartTime = info.AcquisitionStartTime;
            record.AbsoluteTime = info.AcquisitionStartTime is double t
                ? t + (record.RetentionTime * 60.0)
                : record.RetentionTime * 60.0;

            if (state.Corrected.Length < record.PeakCount)
                state.Corrected = new double[Math.Max(record.PeakCount, 4096)];

            var corrected = state.Corrected.AsSpan(0, record.PeakCount);
            MzTransformResult result = state.Transform.Transform(record, corrected);
            if (!result.Rewrite)
                return new SpectrumOutcome(span, length, record.Id, false, result.MonotonicityFixes, result.Reverted);

            BinaryArrayLocation mzArray = parsed.Arrays[parsed.MzArrayIndex];
            if (mzArray.BinaryTextStart < 0 || mzArray.EncodedLengthStart < 0)
                return new SpectrumOutcome(span, length, record.Id, false, 0, false);

            int base64Length = MzMLBinaryCodec.Encode(
                corrected, mzArray.Encoding,
                ref state.RawScratch, ref state.DeflateScratch, ref state.Base64Scratch);

            byte[] replacement = Splice(span, length, mzArray, state.Base64Scratch, base64Length, out int newLength);
            return new SpectrumOutcome(
                replacement, newLength, record.Id, true, result.MonotonicityFixes, result.Reverted);
        }
        finally
        {
            statePool.Add(state);
        }
    }

    /// <summary>
    /// Rebuilds the spectrum bytes with a new base64 payload and a matching encodedLength.
    /// Everything outside those two ranges is copied byte for byte.
    /// </summary>
    private static byte[] Splice(
        byte[] span,
        int length,
        BinaryArrayLocation mzArray,
        byte[] base64,
        int base64Length,
        out int newLength)
    {
        // encodedLength records the base64 CHARACTER count, not the decoded byte count.
        byte[] lengthText = Encoding.UTF8.GetBytes(base64Length.ToString(CultureInfo.InvariantCulture));

        int encodedLengthEnd = mzArray.EncodedLengthStart + mzArray.EncodedLengthLength;
        int binaryTextEnd = mzArray.BinaryTextStart + mzArray.BinaryTextLength;

        newLength = mzArray.EncodedLengthStart
                    + lengthText.Length
                    + (mzArray.BinaryTextStart - encodedLengthEnd)
                    + base64Length
                    + (length - binaryTextEnd);

        byte[] result = ArrayPool<byte>.Shared.Rent(newLength);
        int at = 0;

        Array.Copy(span, 0, result, at, mzArray.EncodedLengthStart);
        at += mzArray.EncodedLengthStart;

        Array.Copy(lengthText, 0, result, at, lengthText.Length);
        at += lengthText.Length;

        int middle = mzArray.BinaryTextStart - encodedLengthEnd;
        Array.Copy(span, encodedLengthEnd, result, at, middle);
        at += middle;

        Array.Copy(base64, 0, result, at, base64Length);
        at += base64Length;

        int tail = length - binaryTextEnd;
        Array.Copy(span, binaryTextEnd, result, at, tail);

        return result;
    }

    private static byte[] BuildIndex(
        List<(string Id, long Offset)> spectra,
        List<(string Id, long Offset)> chromatograms)
    {
        var builder = new StringBuilder(64 * (spectra.Count + chromatograms.Count) + 256);
        builder.Append("<indexList count=\"2\">\n");
        AppendIndex(builder, "spectrum", spectra);
        AppendIndex(builder, "chromatogram", chromatograms);
        builder.Append("  </indexList>\n");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void AppendIndex(StringBuilder builder, string name, List<(string Id, long Offset)> entries)
    {
        builder.Append("    <index name=\"").Append(name).Append("\">\n");
        foreach ((string id, long offset) in entries)
        {
            builder.Append("      <offset idRef=\"").Append(EscapeAttribute(id)).Append("\">")
                .Append(offset.ToString(CultureInfo.InvariantCulture)).Append("</offset>\n");
        }

        builder.Append("    </index>\n");
    }

    private static string EscapeAttribute(string value)
    {
        if (value.IndexOfAny(new[] { '&', '<', '>', '"' }) < 0) return value;
        return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    /// <summary>Reads the id attribute from an element's opening tag.</summary>
    private static string ReadIdAttribute(byte[] span, int length)
    {
        var window = new ReadOnlySpan<byte>(span, 0, Math.Min(length, 4096));
        int tagEnd = window.IndexOf((byte)'>');
        if (tagEnd < 0) tagEnd = window.Length;

        ReadOnlySpan<byte> attr = " id=\""u8;
        int at = window[..tagEnd].IndexOf(attr);
        if (at < 0) return string.Empty;

        int start = at + attr.Length;
        int end = window[start..tagEnd].IndexOf((byte)'"');
        if (end < 0) return string.Empty;

        return Encoding.UTF8.GetString(span, start, end);
    }
}
