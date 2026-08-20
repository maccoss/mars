// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Streaming scanner that carves an mzML file into regions without ever holding the file in
// memory: runs of bytes to pass through untouched, and the indexed elements between them.

using System;
using System.IO;

namespace MARS.IO;

internal enum MzMLRegionKind
{
    /// <summary>Bytes to copy through untouched.</summary>
    Gap,

    Spectrum,

    Chromatogram,
}

/// <summary>
/// Walks an mzML file as a sequence of regions.
/// <para>
/// Everything that is not a spectrum or chromatogram comes back as a gap the caller copies
/// verbatim, which is what makes the passthrough contract hold by construction: anything
/// MARS does not deliberately replace is byte-identical to the input, including attribute
/// order, whitespace, namespace declarations and every cvParam.
/// </para>
/// <para>
/// Memory is bounded by the largest single element rather than by the file. A long stretch
/// with no elements is handed back in chunks instead of accumulating.
/// </para>
/// </summary>
internal sealed class MzMLSpanScanner : IDisposable
{
    private static readonly byte[] SpectrumOpen = "<spectrum"u8.ToArray();
    private static readonly byte[] SpectrumClose = "</spectrum>"u8.ToArray();
    private static readonly byte[] ChromatogramOpen = "<chromatogram"u8.ToArray();
    private static readonly byte[] ChromatogramClose = "</chromatogram>"u8.ToArray();
    private static readonly int MaxOpenTagLength = Math.Max(SpectrumOpen.Length, ChromatogramOpen.Length);

    private const int MaxGapChunk = 4 << 20;

    private readonly Stream _input;
    private readonly long _limit;

    private byte[] _buffer;
    private int _dataEnd;
    private int _cursor;
    private long _consumedFromFile;
    private bool _inputExhausted;

    private int _pendingElementStart = -1;
    private MzMLRegionKind _pendingElementKind;

    public MzMLSpanScanner(Stream input, long limit, int initialBufferSize = 1 << 20)
    {
        _input = input;
        _limit = limit;
        _buffer = new byte[initialBufferSize];
    }

    public byte[] Buffer => _buffer;

    /// <summary>
    /// Produces the next region. The caller must handle it and then call
    /// <see cref="Advance"/> with its length before asking for another.
    /// </summary>
    public bool TryReadRegion(out MzMLRegionKind kind, out int start, out int length)
    {
        kind = MzMLRegionKind.Gap;
        start = _cursor;
        length = 0;

        // An element located on a previous call, with gap bytes still in front of it.
        if (_pendingElementStart >= 0)
        {
            if (_cursor < _pendingElementStart)
            {
                length = _pendingElementStart - _cursor;
                return true;
            }

            kind = _pendingElementKind;
            byte[] closeTag = kind == MzMLRegionKind.Spectrum ? SpectrumClose : ChromatogramClose;
            int end = FindElementEnd(closeTag);
            if (end < 0)
                throw new InvalidDataException($"mzML ended in the middle of a {kind} element.");

            _pendingElementStart = -1;
            start = _cursor;
            length = end - _cursor;
            return true;
        }

        int elementStart = FindElementStart(out MzMLRegionKind found, out int gapChunk);
        if (elementStart < 0)
        {
            if (gapChunk > 0)
            {
                start = _cursor;
                length = gapChunk;
                return true;
            }

            return false;
        }

        if (elementStart > _cursor)
        {
            _pendingElementStart = elementStart;
            _pendingElementKind = found;
            start = _cursor;
            length = elementStart - _cursor;
            return true;
        }

        kind = found;
        byte[] close = found == MzMLRegionKind.Spectrum ? SpectrumClose : ChromatogramClose;
        int elementEnd = FindElementEnd(close);
        if (elementEnd < 0)
            throw new InvalidDataException($"mzML ended in the middle of a {found} element.");

        start = _cursor;
        length = elementEnd - _cursor;
        return true;
    }

    /// <summary>Consumes bytes the caller has finished with.</summary>
    public void Advance(int count)
    {
        _cursor += count;
        _consumedFromFile += count;
    }

    public void Dispose()
    {
    }

    // Search positions are tracked RELATIVE to _cursor, because FillMore compacts the
    // buffer down to _cursor == 0 and an absolute index would silently go stale.
    private int FindElementStart(out MzMLRegionKind kind, out int gapChunk)
    {
        kind = MzMLRegionKind.Gap;
        gapChunk = 0;
        int relative = 0;

        while (true)
        {
            int best = -1;
            MzMLRegionKind bestKind = MzMLRegionKind.Gap;
            bool needMore = false;

            foreach ((byte[] tag, MzMLRegionKind tagKind) in new[]
            {
                (SpectrumOpen, MzMLRegionKind.Spectrum),
                (ChromatogramOpen, MzMLRegionKind.Chromatogram),
            })
            {
                int searchFrom = _cursor + relative;
                while (true)
                {
                    int hit = IndexOf(searchFrom, tag);
                    if (hit < 0) break;

                    int after = hit + tag.Length;
                    if (after >= _dataEnd)
                    {
                        // Cannot yet tell <spectrum from <spectrumList.
                        needMore = true;
                        break;
                    }

                    byte next = _buffer[after];
                    if (next == (byte)' ' || next == (byte)'\t' || next == (byte)'\r' ||
                        next == (byte)'\n' || next == (byte)'>' || next == (byte)'/')
                    {
                        if (best < 0 || hit < best)
                        {
                            best = hit;
                            bestKind = tagKind;
                        }

                        break;
                    }

                    searchFrom = after;
                }
            }

            if (best >= 0)
            {
                kind = bestKind;
                return best;
            }

            int resident = _dataEnd - _cursor;
            if (!needMore && resident > MaxGapChunk)
            {
                // Hand back part of the gap so a long run without elements does not grow
                // the resident window without bound.
                gapChunk = resident - MaxOpenTagLength;
                return -1;
            }

            // Everything except the last few bytes has now been searched and rejected. Work
            // this out BEFORE reading more, or the freshly read window gets skipped whole.
            int searchedTo = Math.Max(0, resident - MaxOpenTagLength);

            if (!FillMore())
            {
                gapChunk = _dataEnd - _cursor;
                return -1;
            }

            relative = searchedTo;
        }
    }

    private int FindElementEnd(byte[] closeTag)
    {
        int relative = 0;
        while (true)
        {
            int found = IndexOf(_cursor + relative, closeTag);
            if (found >= 0) return found + closeTag.Length;

            relative = Math.Max(0, (_dataEnd - _cursor) - (closeTag.Length - 1));
            if (!FillMore()) return -1;
        }
    }

    private int IndexOf(int from, byte[] needle)
    {
        if (from >= _dataEnd) return -1;
        int found = _buffer.AsSpan(from, _dataEnd - from).IndexOf(needle);
        return found < 0 ? -1 : found + from;
    }

    /// <summary>
    /// Reads more input, compacting first and growing the buffer only when the resident
    /// window genuinely needs to be larger (one very large element).
    /// </summary>
    private bool FillMore()
    {
        if (_inputExhausted) return false;

        if (_cursor > 0)
        {
            int live = _dataEnd - _cursor;
            if (live > 0) Array.Copy(_buffer, _cursor, _buffer, 0, live);
            _dataEnd = live;
            if (_pendingElementStart >= 0) _pendingElementStart -= _cursor;
            _cursor = 0;
        }

        if (_dataEnd == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);

        long remainingInFile = _limit - (_consumedFromFile + _dataEnd);
        if (remainingInFile <= 0)
        {
            _inputExhausted = true;
            return false;
        }

        int want = (int)Math.Min(_buffer.Length - _dataEnd, remainingInFile);
        int read = _input.Read(_buffer, _dataEnd, want);
        if (read <= 0)
        {
            _inputExhausted = true;
            return false;
        }

        _dataEnd += read;
        return true;
    }
}
