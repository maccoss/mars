// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Minimal PNG encoder for the QC report's density layers.

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace MARS.Report;

/// <summary>
/// Encodes an RGB pixel buffer as a PNG, for embedding in the report as a data URI.
///
/// The density panels are the report's bulk: a couple of dozen panels of several thousand
/// cells each. Drawing them as SVG rectangles produced a six-megabyte file, too large to
/// email, and merging runs only reached five. As a PNG the same panel is a few kilobytes,
/// because a run of similar colours is exactly what deflate is good at.
///
/// Axes, labels and trend lines stay vector; only the density itself is raster, which is
/// also what a plotting library would do. There is no imaging dependency here - a PNG is a
/// header, a zlib stream and a CRC, and .NET has the zlib.
/// </summary>
public static class Png
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <param name="rgb">Row-major RGB triples, <paramref name="width"/> * <paramref name="height"/> * 3 bytes.</param>
    public static byte[] Encode(byte[] rgb, int width, int height)
    {
        if (rgb.Length != width * height * 3)
            throw new ArgumentException("Pixel buffer does not match the given dimensions.", nameof(rgb));

        using var png = new MemoryStream();
        png.Write(Signature, 0, Signature.Length);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;   // bits per channel
        header[9] = 2;   // colour type 2: truecolour RGB
        header[10] = 0;  // deflate
        header[11] = 0;  // adaptive filtering
        header[12] = 0;  // no interlace
        WriteChunk(png, "IHDR", header);

        // Each scanline is prefixed with its filter type. Filter 0 (none) keeps this simple;
        // the colour ramps are quantized, so deflate already finds long runs.
        var raw = new byte[height * ((width * 3) + 1)];
        int stride = (width * 3) + 1;
        for (int y = 0; y < height; y++)
        {
            raw[y * stride] = 0;
            Buffer.BlockCopy(rgb, y * width * 3, raw, (y * stride) + 1, width * 3);
        }

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);

        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    /// <summary>Encodes to a <c>data:</c> URI, ready to drop into an img or SVG image element.</summary>
    public static string DataUri(byte[] rgb, int width, int height) =>
        "data:image/png;base64," + Convert.ToBase64String(Encode(rgb, width, height));

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        var typeBytes = new byte[4];
        for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
        stream.Write(typeBytes, 0, 4);
        stream.Write(data, 0, data.Length);

        // The CRC covers the type and the data, but not the length.
        uint crc = 0xFFFFFFFF;
        crc = Crc(crc, typeBytes);
        crc = Crc(crc, data);
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc ^ 0xFFFFFFFF);
        stream.Write(checksum);
    }

    private static uint Crc(uint crc, byte[] data)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }
}
