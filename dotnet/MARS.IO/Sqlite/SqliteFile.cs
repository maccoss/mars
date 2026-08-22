// Copyright (c) University of Washington 2026. Licensed under the MIT License.
//
// A read-only SQLite reader: enough of the file format to full-scan a table.
//
// MARS deliberately has no native dependencies, so that the assembly drops into a managed
// ProteoWizard tree without adding per-platform build artifacts. Microsoft.Data.Sqlite
// would pull in SQLitePCLRaw and a native e_sqlite3 for every runtime identifier, which is
// exactly the thing being avoided. BiblioSpec libraries only ever need sequential scans of
// a handful of tables, and that is a small, well-specified subset of the format.
//
// Supports: table b-trees (interior and leaf), overflow page chains, the record format,
// and UTF-8 / UTF-16 text. Does NOT support: indices, WAL, encryption, or writing.

using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MARS.IO.Sqlite;

public enum SqliteValueKind
{
    Null,
    Integer,
    Real,
    Text,
    Blob,
}

/// <summary>One column value from a row.</summary>
public readonly struct SqliteValue
{
    private readonly long _integer;
    private readonly double _real;
    private readonly byte[]? _bytes;
    private readonly int _offset;
    private readonly int _length;
    private readonly Encoding? _encoding;

    private SqliteValue(SqliteValueKind kind, long integer, double real, byte[]? bytes, int offset, int length, Encoding? encoding)
    {
        Kind = kind;
        _integer = integer;
        _real = real;
        _bytes = bytes;
        _offset = offset;
        _length = length;
        _encoding = encoding;
    }

    public SqliteValueKind Kind { get; }

    public bool IsNull => Kind == SqliteValueKind.Null;

    public static SqliteValue Null() => new(SqliteValueKind.Null, 0, 0, null, 0, 0, null);

    public static SqliteValue FromInteger(long value) => new(SqliteValueKind.Integer, value, 0, null, 0, 0, null);

    public static SqliteValue FromReal(double value) => new(SqliteValueKind.Real, 0, value, null, 0, 0, null);

    public static SqliteValue FromText(byte[] bytes, int offset, int length, Encoding encoding) =>
        new(SqliteValueKind.Text, 0, 0, bytes, offset, length, encoding);

    public static SqliteValue FromBlob(byte[] bytes, int offset, int length) =>
        new(SqliteValueKind.Blob, 0, 0, bytes, offset, length, null);

    public long AsInteger() => Kind switch
    {
        SqliteValueKind.Integer => _integer,
        SqliteValueKind.Real => (long)_real,
        // Invariant, explicitly. A SQLite text value holds a number the way SQLite wrote it,
        // which has nothing to do with the locale of the machine reading it - a library built
        // in Seattle has to read the same in Munich.
        SqliteValueKind.Text =>
            long.TryParse(AsText(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
                ? parsed
                : 0,
        _ => 0,
    };

    public double AsDouble() => Kind switch
    {
        SqliteValueKind.Real => _real,
        SqliteValueKind.Integer => _integer,
        SqliteValueKind.Text =>
            double.TryParse(AsText(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : double.NaN,
        _ => double.NaN,
    };

    public string AsText() => Kind switch
    {
        SqliteValueKind.Text => _encoding!.GetString(_bytes!, _offset, _length),
        SqliteValueKind.Blob => Encoding.UTF8.GetString(_bytes!, _offset, _length),
        SqliteValueKind.Integer => _integer.ToString(System.Globalization.CultureInfo.InvariantCulture),
        SqliteValueKind.Real => _real.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => string.Empty,
    };

    /// <summary>The raw bytes of a blob. The array is the row's payload buffer; copy to keep.</summary>
    public ReadOnlySpan<byte> AsBlob() =>
        _bytes is null ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(_bytes, _offset, _length);
}

/// <summary>A table's schema entry from sqlite_master.</summary>
public sealed class SqliteTable
{
    public required string Name { get; init; }

    public required int RootPage { get; init; }

    public required string CreateSql { get; init; }

    public required IReadOnlyList<string> Columns { get; init; }

    public int ColumnIndex(string name)
    {
        for (int i = 0; i < Columns.Count; i++)
        {
            if (string.Equals(Columns[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return -1;
    }
}

public sealed class SqliteFile : IDisposable
{
    private readonly FileStream _stream;
    private readonly int _pageSize;
    private readonly int _usableSize;
    private readonly Encoding _textEncoding;
    private readonly byte[] _page;
    private readonly Dictionary<string, SqliteTable> _tables = new(StringComparer.OrdinalIgnoreCase);

    public SqliteFile(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);

        var header = new byte[100];
        MzMLFile.ReadExactly(_stream, header, header.Length);

        if (Encoding.ASCII.GetString(header, 0, 15) != "SQLite format 3")
            throw new InvalidDataException($"Not a SQLite database: {path}");

        int declaredPageSize = (header[16] << 8) | header[17];
        _pageSize = declaredPageSize == 1 ? 65536 : declaredPageSize;
        if (_pageSize < 512 || (_pageSize & (_pageSize - 1)) != 0)
            throw new InvalidDataException($"Invalid SQLite page size {_pageSize} in {path}");

        int reserved = header[20];
        _usableSize = _pageSize - reserved;

        int encoding = ReadInt32BigEndian(header, 56);
        _textEncoding = encoding switch
        {
            2 => Encoding.Unicode,
            3 => Encoding.BigEndianUnicode,
            _ => Encoding.UTF8,
        };

        _page = new byte[_pageSize];
        LoadSchema();
    }

    public IReadOnlyDictionary<string, SqliteTable> Tables => _tables;

    public bool HasTable(string name) => _tables.ContainsKey(name);

    public SqliteTable Table(string name) =>
        _tables.TryGetValue(name, out SqliteTable? table)
            ? table
            : throw new InvalidDataException($"Table '{name}' not found in the database.");

    /// <summary>
    /// Streams every row of a table in b-tree order. The values reference a shared payload
    /// buffer that is reused between rows, so blobs must be copied to outlive the iteration.
    /// </summary>
    public IEnumerable<SqliteRow> Scan(SqliteTable table)
    {
        var payload = new byte[_pageSize * 2];
        var values = new SqliteValue[Math.Max(table.Columns.Count, 8)];
        var pages = new Stack<int>();
        pages.Push(table.RootPage);

        // The b-tree is walked with an explicit stack rather than recursion so that a deep
        // or corrupt tree cannot blow the call stack.
        var pageBuffer = new byte[_pageSize];
        while (pages.Count > 0)
        {
            int pageNumber = pages.Pop();
            ReadPage(pageNumber, pageBuffer);
            int headerOffset = pageNumber == 1 ? 100 : 0;
            byte pageType = pageBuffer[headerOffset];

            int cellCount = (pageBuffer[headerOffset + 3] << 8) | pageBuffer[headerOffset + 4];

            if (pageType == 0x05)
            {
                // Interior table page: push children in reverse so they are visited in order.
                int rightMost = ReadInt32BigEndian(pageBuffer, headerOffset + 8);
                var children = new List<int>(cellCount + 1);
                for (int i = 0; i < cellCount; i++)
                {
                    int cellPointer = ReadCellPointer(pageBuffer, headerOffset, i, interior: true);
                    children.Add(ReadInt32BigEndian(pageBuffer, cellPointer));
                }

                children.Add(rightMost);
                for (int i = children.Count - 1; i >= 0; i--) pages.Push(children[i]);
                continue;
            }

            if (pageType != 0x0d)
            {
                // Index pages carry no table rows; a table b-tree should never contain them.
                continue;
            }

            for (int i = 0; i < cellCount; i++)
            {
                int cellPointer = ReadCellPointer(pageBuffer, headerOffset, i, interior: false);
                int at = cellPointer;

                long payloadSize = ReadVarint(pageBuffer, ref at);
                long rowId = ReadVarint(pageBuffer, ref at);

                int payloadLength = ReadPayload(pageBuffer, at, payloadSize, ref payload);
                int count = ParseRecord(payload, payloadLength, ref values);
                yield return new SqliteRow(rowId, values, count);
            }
        }
    }

    public void Dispose() => _stream.Dispose();

    private void LoadSchema()
    {
        // sqlite_master always lives at page 1 and has the fixed shape
        // (type, name, tbl_name, rootpage, sql).
        var master = new SqliteTable
        {
            Name = "sqlite_master",
            RootPage = 1,
            CreateSql = string.Empty,
            Columns = new[] { "type", "name", "tbl_name", "rootpage", "sql" },
        };

        foreach (SqliteRow row in Scan(master))
        {
            if (row.Count < 5) continue;
            if (!string.Equals(row[0].AsText(), "table", StringComparison.OrdinalIgnoreCase)) continue;

            string name = row[1].AsText();
            var rootPage = (int)row[3].AsInteger();
            string sql = row[4].AsText();
            if (rootPage <= 0) continue;

            _tables[name] = new SqliteTable
            {
                Name = name,
                RootPage = rootPage,
                CreateSql = sql,
                Columns = SqliteSchemaParser.ParseColumns(sql),
            };
        }
    }

    private void ReadPage(int pageNumber, byte[] destination)
    {
        long offset = (long)(pageNumber - 1) * _pageSize;
        _stream.Seek(offset, SeekOrigin.Begin);
        MzMLFile.ReadExactly(_stream, destination, _pageSize);
    }

    private static int ReadCellPointer(byte[] page, int headerOffset, int index, bool interior)
    {
        int arrayStart = headerOffset + (interior ? 12 : 8);
        int at = arrayStart + (index * 2);
        return (page[at] << 8) | page[at + 1];
    }

    /// <summary>
    /// Copies a cell's payload into <paramref name="payload"/>, following the overflow page
    /// chain when the record does not fit on its page.
    /// </summary>
    private int ReadPayload(byte[] page, int at, long payloadSize, ref byte[] payload)
    {
        if (payloadSize > int.MaxValue) throw new InvalidDataException("SQLite payload too large.");
        var total = (int)payloadSize;
        if (payload.Length < total) payload = new byte[Math.Max(total, payload.Length * 2)];

        // Table leaf spill thresholds, straight from the file format definition.
        int maxLocal = _usableSize - 35;
        int minLocal = (((_usableSize - 12) * 32 / 255) - 23);

        int localSize;
        if (total <= maxLocal)
        {
            localSize = total;
        }
        else
        {
            int candidate = minLocal + ((total - minLocal) % (_usableSize - 4));
            localSize = candidate > maxLocal ? minLocal : candidate;
        }

        Array.Copy(page, at, payload, 0, localSize);
        int written = localSize;

        if (written < total)
        {
            int overflowPage = ReadInt32BigEndian(page, at + localSize);
            var overflowBuffer = new byte[_pageSize];
            while (overflowPage != 0 && written < total)
            {
                ReadPage(overflowPage, overflowBuffer);
                int chunk = Math.Min(_usableSize - 4, total - written);
                Array.Copy(overflowBuffer, 4, payload, written, chunk);
                written += chunk;
                overflowPage = ReadInt32BigEndian(overflowBuffer, 0);
            }

            if (written < total)
                throw new InvalidDataException("SQLite overflow chain ended before the payload was complete.");
        }

        return total;
    }

    /// <summary>Splits a record payload into column values.</summary>
    private int ParseRecord(byte[] payload, int length, ref SqliteValue[] values)
    {
        var at = 0;
        long headerSize = ReadVarint(payload, ref at);
        int headerEnd = (int)headerSize;
        int body = headerEnd;

        var count = 0;
        while (at < headerEnd && body <= length)
        {
            long serialType = ReadVarint(payload, ref at);
            if (count == values.Length) Array.Resize(ref values, values.Length * 2);

            switch (serialType)
            {
                case 0:
                    values[count++] = SqliteValue.Null();
                    break;
                case 1:
                    values[count++] = SqliteValue.FromInteger((sbyte)payload[body]);
                    body += 1;
                    break;
                case 2:
                    values[count++] = SqliteValue.FromInteger((short)((payload[body] << 8) | payload[body + 1]));
                    body += 2;
                    break;
                case 3:
                {
                    int raw = (payload[body] << 16) | (payload[body + 1] << 8) | payload[body + 2];
                    if ((raw & 0x800000) != 0) raw |= unchecked((int)0xFF000000);
                    values[count++] = SqliteValue.FromInteger(raw);
                    body += 3;
                    break;
                }

                case 4:
                    values[count++] = SqliteValue.FromInteger(ReadInt32BigEndian(payload, body));
                    body += 4;
                    break;
                case 5:
                {
                    long raw = 0;
                    for (var i = 0; i < 6; i++) raw = (raw << 8) | payload[body + i];
                    if ((raw & 0x800000000000L) != 0) raw |= unchecked((long)0xFFFF000000000000);
                    values[count++] = SqliteValue.FromInteger(raw);
                    body += 6;
                    break;
                }

                case 6:
                    values[count++] = SqliteValue.FromInteger(ReadInt64BigEndian(payload, body));
                    body += 8;
                    break;
                case 7:
                    values[count++] = SqliteValue.FromReal(BitConverter.Int64BitsToDouble(ReadInt64BigEndian(payload, body)));
                    body += 8;
                    break;
                case 8:
                    values[count++] = SqliteValue.FromInteger(0);
                    break;
                case 9:
                    values[count++] = SqliteValue.FromInteger(1);
                    break;
                case 10:
                case 11:
                    values[count++] = SqliteValue.Null();
                    break;
                default:
                {
                    var size = (int)((serialType - (serialType % 2 == 0 ? 12 : 13)) / 2);
                    values[count++] = serialType % 2 == 0
                        ? SqliteValue.FromBlob(payload, body, size)
                        : SqliteValue.FromText(payload, body, size, _textEncoding);
                    body += size;
                    break;
                }
            }
        }

        return count;
    }

    internal static long ReadVarint(byte[] buffer, ref int offset)
    {
        long value = 0;
        for (var i = 0; i < 8; i++)
        {
            byte b = buffer[offset++];
            value = (value << 7) | (byte)(b & 0x7F);
            if ((b & 0x80) == 0) return value;
        }

        // Ninth byte contributes all eight bits.
        value = (value << 8) | buffer[offset++];
        return value;
    }

    internal static int ReadInt32BigEndian(byte[] buffer, int offset) =>
        (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];

    internal static long ReadInt64BigEndian(byte[] buffer, int offset)
    {
        long value = 0;
        for (var i = 0; i < 8; i++) value = (value << 8) | buffer[offset + i];
        return value;
    }
}

/// <summary>One row, valid until the next iteration step.</summary>
public readonly struct SqliteRow
{
    private readonly SqliteValue[] _values;

    internal SqliteRow(long rowId, SqliteValue[] values, int count)
    {
        RowId = rowId;
        _values = values;
        Count = count;
    }

    public long RowId { get; }

    public int Count { get; }

    public SqliteValue this[int index] =>
        index >= 0 && index < Count ? _values[index] : SqliteValue.Null();
}
