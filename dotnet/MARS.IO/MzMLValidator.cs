// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Structural checks on a written mzML: the index points where it claims, and the SHA-1
// covers what the specification says it covers.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MARS.IO;

public sealed class IndexValidationResult
{
    public bool IsIndexed { get; init; }

    public int SpectrumOffsets { get; init; }

    public int ChromatogramOffsets { get; init; }

    /// <summary>Offsets that do not land on the element they claim to.</summary>
    public List<string> BadOffsets { get; init; } = new();

    public bool ChecksumPresent { get; init; }

    public bool ChecksumValid { get; init; }

    public string RecordedChecksum { get; init; } = string.Empty;

    public string ComputedChecksum { get; init; } = string.Empty;

    public bool IsValid => BadOffsets.Count == 0 && (!ChecksumPresent || ChecksumValid);
}

public static class MzMLValidator
{
    /// <summary>
    /// Verifies that every recorded index offset lands on the start tag of the element it
    /// names, and that the SHA-1 fileChecksum matches.
    /// </summary>
    /// <param name="maxOffsetsChecked">0 checks every offset; a positive value samples evenly.</param>
    public static IndexValidationResult Validate(string path, int maxOffsetsChecked = 0)
    {
        var file = new FileInfo(path);
        using FileStream stream = File.OpenRead(path);

        (long indexListOffset, string recordedChecksum, long checksumTagOffset) = ReadTrailer(stream, file.Length);
        if (indexListOffset < 0)
        {
            return new IndexValidationResult
            {
                IsIndexed = false,
                ChecksumPresent = recordedChecksum.Length > 0,
            };
        }

        string indexXml = ReadIndexList(stream, indexListOffset, file.Length);
        List<(string Name, string Id, long Offset)> entries = ParseIndex(indexXml);

        var spectrumCount = 0;
        var chromatogramCount = 0;
        foreach ((string name, _, _) in entries)
        {
            if (name == "spectrum") spectrumCount++;
            else if (name == "chromatogram") chromatogramCount++;
        }

        var bad = new List<string>();
        var probe = new byte[64];
        int step = maxOffsetsChecked > 0 && entries.Count > maxOffsetsChecked
            ? entries.Count / maxOffsetsChecked
            : 1;

        for (int i = 0; i < entries.Count; i += step)
        {
            (string name, string id, long offset) = entries[i];
            if (offset < 0 || offset >= file.Length)
            {
                bad.Add($"{name} '{id}' offset {offset} is outside the file");
                continue;
            }

            stream.Seek(offset, SeekOrigin.Begin);
            int read = stream.Read(probe, 0, probe.Length);
            string text = Encoding.UTF8.GetString(probe, 0, read);
            string expected = "<" + name;
            if (!text.StartsWith(expected, StringComparison.Ordinal))
            {
                bad.Add($"{name} '{id}' offset {offset} lands on \"{Truncate(text, 24)}\", not {expected}");
            }
        }

        bool checksumPresent = recordedChecksum.Length > 0;
        string computed = string.Empty;
        if (checksumPresent)
        {
            computed = ComputeChecksum(stream, checksumTagOffset);
        }

        return new IndexValidationResult
        {
            IsIndexed = true,
            SpectrumOffsets = spectrumCount,
            ChromatogramOffsets = chromatogramCount,
            BadOffsets = bad,
            ChecksumPresent = checksumPresent,
            ChecksumValid = checksumPresent &&
                            string.Equals(computed, recordedChecksum, StringComparison.OrdinalIgnoreCase),
            RecordedChecksum = recordedChecksum,
            ComputedChecksum = computed,
        };
    }

    /// <summary>
    /// The mzML checksum covers every byte up to AND INCLUDING the fileChecksum opening
    /// tag. Verified empirically against pwiz-written files, whose recorded digest only
    /// reproduces under that convention.
    /// </summary>
    private static string ComputeChecksum(FileStream stream, long checksumTagOffset)
    {
        const string openTag = "<fileChecksum>";
        stream.Seek(0, SeekOrigin.Begin);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[1 << 20];
        long remaining = checksumTagOffset;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int read = stream.Read(buffer, 0, want);
            if (read <= 0) break;
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }

        hash.AppendData(Encoding.UTF8.GetBytes(openTag));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static (long IndexListOffset, string Checksum, long ChecksumTagOffset) ReadTrailer(
        FileStream stream, long fileLength)
    {
        int tailLength = (int)Math.Min(64 * 1024, fileLength);
        var tail = new byte[tailLength];
        stream.Seek(fileLength - tailLength, SeekOrigin.Begin);
        MzMLFile.ReadExactly(stream, tail, tailLength);
        string text = Encoding.UTF8.GetString(tail);
        long tailStart = fileLength - tailLength;

        long indexListOffset = -1;
        int at = text.LastIndexOf("<indexListOffset>", StringComparison.Ordinal);
        if (at >= 0)
        {
            int start = at + "<indexListOffset>".Length;
            int end = text.IndexOf("</indexListOffset>", start, StringComparison.Ordinal);
            if (end > start)
            {
                long.TryParse(text.AsSpan(start, end - start).Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out indexListOffset);
            }
        }

        string checksum = string.Empty;
        long checksumTagOffset = -1;
        int checksumAt = text.LastIndexOf("<fileChecksum>", StringComparison.Ordinal);
        if (checksumAt >= 0)
        {
            checksumTagOffset = tailStart + Encoding.UTF8.GetByteCount(text[..checksumAt]);
            int start = checksumAt + "<fileChecksum>".Length;
            int end = text.IndexOf("</fileChecksum>", start, StringComparison.Ordinal);
            if (end > start) checksum = text[start..end].Trim();
        }

        return (indexListOffset, checksum, checksumTagOffset);
    }

    private static string ReadIndexList(FileStream stream, long offset, long fileLength)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        int length = (int)Math.Min(fileLength - offset, int.MaxValue);
        var buffer = new byte[length];
        int total = 0;
        while (total < length)
        {
            int read = stream.Read(buffer, total, length - total);
            if (read <= 0) break;
            total += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static List<(string Name, string Id, long Offset)> ParseIndex(string indexXml)
    {
        var entries = new List<(string, string, long)>();
        string currentName = string.Empty;
        int cursor = 0;

        while (true)
        {
            int indexAt = indexXml.IndexOf("<index name=\"", cursor, StringComparison.Ordinal);
            int offsetAt = indexXml.IndexOf("<offset idRef=\"", cursor, StringComparison.Ordinal);

            if (indexAt >= 0 && (offsetAt < 0 || indexAt < offsetAt))
            {
                int start = indexAt + "<index name=\"".Length;
                int end = indexXml.IndexOf('"', start);
                if (end < 0) break;
                currentName = indexXml[start..end];
                cursor = end;
                continue;
            }

            if (offsetAt < 0) break;

            int idStart = offsetAt + "<offset idRef=\"".Length;
            int idEnd = indexXml.IndexOf('"', idStart);
            if (idEnd < 0) break;
            string id = indexXml[idStart..idEnd];

            int valueStart = indexXml.IndexOf('>', idEnd) + 1;
            int valueEnd = indexXml.IndexOf('<', valueStart);
            if (valueStart <= 0 || valueEnd < 0) break;

            if (long.TryParse(indexXml.AsSpan(valueStart, valueEnd - valueStart), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out long offset))
            {
                entries.Add((currentName, id, offset));
            }

            cursor = valueEnd;
        }

        return entries;
    }

    private static string Truncate(string value, int length)
    {
        value = value.Replace("\n", "\\n").Replace("\r", string.Empty);
        return value.Length <= length ? value : value[..length];
    }
}
