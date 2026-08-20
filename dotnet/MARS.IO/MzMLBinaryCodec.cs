// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// base64 + zlib codec for mzML binary data arrays.

using System;
using System.Buffers.Text;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace MARS.IO;

/// <summary>How one binary data array is stored in the file.</summary>
public readonly struct BinaryArrayEncoding
{
    public BinaryArrayEncoding(bool is64Bit, bool zlib)
    {
        Is64Bit = is64Bit;
        Zlib = zlib;
    }

    /// <summary>True for MS:1000523 (64-bit float), false for MS:1000521 (32-bit float).</summary>
    public bool Is64Bit { get; }

    /// <summary>True when MS:1000574 (zlib compression) is present.</summary>
    public bool Zlib { get; }

    public override string ToString() => (Is64Bit ? "64-bit" : "32-bit") + (Zlib ? " zlib" : " uncompressed");
}

/// <summary>
/// Decodes and re-encodes mzML binary data arrays.
/// <para>
/// Encoding is read per ARRAY, never per spectrum: m/z is typically 64-bit while intensity
/// is often 32-bit, and compression can differ between two arrays of the same spectrum.
/// A modified array is always re-encoded with exactly the precision and compression it was
/// decoded with.
/// </para>
/// </summary>
public static class MzMLBinaryCodec
{
    /// <summary>
    /// Decodes a base64 payload into doubles, growing the buffers as needed. Returns the
    /// number of values written.
    /// </summary>
    /// <param name="base64Scratch">Holds the base64-decoded bytes.</param>
    /// <param name="inflateScratch">
    /// Holds the inflated bytes. This MUST be a different array from
    /// <paramref name="base64Scratch"/>: the compressed bytes are read out of a
    /// MemoryStream that wraps the source array without copying it, so inflating in place
    /// overwrites compressed data that has not been consumed yet. The corruption only
    /// appears once a payload exceeds the decompressor's internal buffer, which is why it
    /// hides on small spectra and surfaces on large ones.
    /// </param>
    public static int Decode(
        ReadOnlySpan<byte> base64Utf8,
        BinaryArrayEncoding encoding,
        ref byte[] base64Scratch,
        ref byte[] inflateScratch,
        ref double[] values)
    {
        int rawLength = DecodeBase64(base64Utf8, ref base64Scratch);
        byte[] raw = base64Scratch;

        if (encoding.Zlib)
        {
            rawLength = Inflate(base64Scratch, rawLength, ref inflateScratch);
            raw = inflateScratch;
        }

        int bytesPerValue = encoding.Is64Bit ? 8 : 4;
        if (rawLength % bytesPerValue != 0)
        {
            throw new InvalidDataException(
                $"Binary array length {rawLength} is not a multiple of {bytesPerValue} for a {encoding} array.");
        }

        int count = rawLength / bytesPerValue;
        if (values.Length < count) values = new double[Math.Max(count, 1024)];

        // mzML binary arrays are little-endian by specification.
        if (encoding.Is64Bit)
        {
            ReadOnlySpan<double> source = MemoryMarshal.Cast<byte, double>(raw.AsSpan(0, rawLength));
            source.CopyTo(values.AsSpan(0, count));
        }
        else
        {
            ReadOnlySpan<float> source = MemoryMarshal.Cast<byte, float>(raw.AsSpan(0, rawLength));
            for (int i = 0; i < count; i++) values[i] = source[i];
        }

        return count;
    }

    /// <summary>
    /// Encodes values back to base64 with the same precision and compression, writing UTF-8
    /// base64 into <paramref name="base64Utf8"/>. Returns the base64 CHARACTER count, which
    /// is what the encodedLength attribute records.
    /// </summary>
    public static int Encode(
        ReadOnlySpan<double> values,
        BinaryArrayEncoding encoding,
        ref byte[] rawScratch,
        ref byte[] deflateScratch,
        ref byte[] base64Utf8)
    {
        int bytesPerValue = encoding.Is64Bit ? 8 : 4;
        int rawLength = values.Length * bytesPerValue;
        if (rawScratch.Length < rawLength) rawScratch = new byte[Math.Max(rawLength, 4096)];

        if (encoding.Is64Bit)
        {
            values.CopyTo(MemoryMarshal.Cast<byte, double>(rawScratch.AsSpan(0, rawLength)));
        }
        else
        {
            Span<float> destination = MemoryMarshal.Cast<byte, float>(rawScratch.AsSpan(0, rawLength));
            for (int i = 0; i < values.Length; i++) destination[i] = (float)values[i];
        }

        byte[] payload = rawScratch;
        int payloadLength = rawLength;

        if (encoding.Zlib)
        {
            payloadLength = Deflate(rawScratch, rawLength, ref deflateScratch);
            payload = deflateScratch;
        }

        int base64Length = Base64.GetMaxEncodedToUtf8Length(payloadLength);
        if (base64Utf8.Length < base64Length) base64Utf8 = new byte[Math.Max(base64Length, 4096)];

        System.Buffers.OperationStatus status = Base64.EncodeToUtf8(
            payload.AsSpan(0, payloadLength), base64Utf8, out _, out int written, isFinalBlock: true);
        if (status != System.Buffers.OperationStatus.Done)
            throw new InvalidDataException($"base64 encoding failed with status {status}.");

        return written;
    }

    private static int DecodeBase64(ReadOnlySpan<byte> base64Utf8, ref byte[] destination)
    {
        int maxLength = (base64Utf8.Length / 4 * 3) + 3;
        if (destination.Length < maxLength) destination = new byte[Math.Max(maxLength, 4096)];

        System.Buffers.OperationStatus status = Base64.DecodeFromUtf8(
            base64Utf8, destination, out int consumed, out int written, isFinalBlock: true);
        if (status == System.Buffers.OperationStatus.Done && consumed == base64Utf8.Length) return written;

        // The fast path rejects embedded whitespace, which the mzML specification permits
        // inside element content even though pwiz does not emit it. Fall back to the
        // whitespace-tolerant decoder.
        string text = Encoding.UTF8.GetString(base64Utf8);
        byte[] decoded = Convert.FromBase64String(text);
        if (destination.Length < decoded.Length) destination = new byte[decoded.Length];
        decoded.CopyTo(destination, 0);
        return decoded.Length;
    }

    private static int Inflate(byte[] source, int length, ref byte[] destination)
    {
        // mzML uses the zlib container, with its 2-byte header and Adler-32 trailer, not a
        // raw deflate stream. ZLibStream, never DeflateStream.
        using var input = new MemoryStream(source, 0, length, writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);

        int total = 0;
        while (true)
        {
            if (total == destination.Length) Array.Resize(ref destination, Math.Max(destination.Length * 2, 8192));
            int read = zlib.Read(destination, total, destination.Length - total);
            if (read == 0) break;
            total += read;
        }

        return total;
    }

    private static int Deflate(byte[] source, int length, ref byte[] destination)
    {
        using var output = new MemoryStream(Math.Max(length / 2, 256));
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(source, 0, length);
        }

        int compressed = (int)output.Length;
        if (destination.Length < compressed) destination = new byte[Math.Max(compressed, 4096)];
        output.GetBuffer().AsSpan(0, compressed).CopyTo(destination);
        return compressed;
    }
}
