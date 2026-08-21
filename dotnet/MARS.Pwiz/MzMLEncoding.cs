// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.IO;
using System.Text;

namespace MARS.Pwiz;

/// <summary>How one binary array is encoded.</summary>
public readonly struct ArrayEncoding
{
    public ArrayEncoding(bool bits64, bool zlib)
    {
        Bits64 = bits64;
        Zlib = zlib;
    }

    public bool Bits64 { get; }

    public bool Zlib { get; }

    /// <summary>What msconvert writes unless told otherwise.</summary>
    public static ArrayEncoding Default => new(bits64: true, zlib: true);

    public override string ToString() => (Bits64 ? "64-bit" : "32-bit") + (Zlib ? " zlib" : " uncompressed");
}

/// <summary>Encodings for the two arrays MARS cares about.</summary>
public readonly struct SpectrumEncoding
{
    public SpectrumEncoding(ArrayEncoding mz, ArrayEncoding intensity)
    {
        Mz = mz;
        Intensity = intensity;
    }

    public ArrayEncoding Mz { get; }

    public ArrayEncoding Intensity { get; }

    public static SpectrumEncoding Default => new(ArrayEncoding.Default, ArrayEncoding.Default);

    public override string ToString() => $"m/z {Mz}, intensity {Intensity}";
}

/// <summary>
/// Reads how an mzML encodes its binary arrays, so a pwiz-backed write can match it.
/// </summary>
/// <remarks>
/// <para>
/// This matters more than it sounds. pwiz's <c>BinaryEncoderConfig</c> defaults to 64-bit
/// <em>uncompressed</em>, and taking that default on a Stellar run produced a file 61% larger
/// than the input. Matching what the input actually used brings it back within 1%.
/// </para>
/// <para>
/// Read per array, not per file, because m/z is commonly 64-bit where intensity is 32-bit and
/// the compression can differ between two arrays of one spectrum. This is still a
/// simplification of what MARS's own writer does: the byte-splice reads the encoding of every
/// array it rewrites, whereas pwiz's config is global with per-array overrides, so a file
/// whose encoding varies from spectrum to spectrum cannot be reproduced exactly. Such a file
/// is unusual - a converter picks an encoding and holds it - and the first spectrum carrying
/// both arrays is taken as representative.
/// </para>
/// </remarks>
public static class MzMLEncoding
{
    private const string MzArray = "MS:1000514";
    private const string IntensityArray = "MS:1000515";
    private const string Bits32 = "MS:1000521";
    private const string Bits64 = "MS:1000523";
    private const string Zlib = "MS:1000574";

    /// <summary>How much of the file to read looking for the first complete spectrum.</summary>
    private const int ProbeBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Sniffs the encoding of the first spectrum that carries both arrays. Falls back to
    /// <see cref="SpectrumEncoding.Default"/> for a file this cannot read.
    /// </summary>
    public static SpectrumEncoding Sniff(string path)
    {
        string text;
        try
        {
            using FileStream stream = File.OpenRead(path);
            int length = (int)Math.Min(ProbeBytes, stream.Length);
            var buffer = new byte[length];
            int read = stream.Read(buffer, 0, length);
            text = Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (IOException)
        {
            return SpectrumEncoding.Default;
        }

        ArrayEncoding? mz = null;
        ArrayEncoding? intensity = null;

        int at = text.IndexOf("<binaryDataArray", StringComparison.Ordinal);
        while (at >= 0 && (mz is null || intensity is null))
        {
            int end = text.IndexOf("</binaryDataArray>", at, StringComparison.Ordinal);
            if (end < 0) break;

            ReadOnlySpan<char> element = text.AsSpan(at, end - at);
            var encoding = new ArrayEncoding(
                bits64: !Contains(element, Bits32) || Contains(element, Bits64),
                zlib: Contains(element, Zlib));

            if (mz is null && Contains(element, MzArray)) mz = encoding;
            else if (intensity is null && Contains(element, IntensityArray)) intensity = encoding;

            at = text.IndexOf("<binaryDataArray", end, StringComparison.Ordinal);
        }

        return new SpectrumEncoding(
            mz ?? ArrayEncoding.Default,
            intensity ?? mz ?? ArrayEncoding.Default);
    }

    private static bool Contains(ReadOnlySpan<char> element, string accession) =>
        element.IndexOf(accession.AsSpan(), StringComparison.Ordinal) >= 0;
}
