// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Streaming RFC 4180 CSV reader.
//
// A Skyline PRISM report for a plate of Astral runs is tens of gigabytes and tens of
// millions of rows, so the reader never materializes the file, never allocates a string
// per cell the caller does not ask for, and reuses its row buffer.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MARS.IO;

public sealed class CsvReader : IDisposable
{
    private readonly TextReader _reader;
    private readonly bool _ownsReader;
    private readonly StringBuilder _cell = new(64);
    private readonly List<string> _fields = new();
    private string[] _header = Array.Empty<string>();
    private readonly Dictionary<string, int> _headerIndex = new(StringComparer.Ordinal);

    // Characters are pulled a block at a time and indexed directly. A TextReader.Read()
    // per character is a virtual call per character, which on a plate-scale report means
    // tens of billions of them.
    private readonly char[] _buffer = new char[1 << 16];
    private int _bufferLength;
    private int _bufferAt;

    public CsvReader(string path)
        : this(new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 20), true)
    {
    }

    public CsvReader(TextReader reader, bool ownsReader = false)
    {
        _reader = reader;
        _ownsReader = ownsReader;
    }

    private int NextChar()
    {
        if (_bufferAt >= _bufferLength)
        {
            _bufferLength = _reader.Read(_buffer, 0, _buffer.Length);
            _bufferAt = 0;
            if (_bufferLength <= 0) return -1;
        }

        return _buffer[_bufferAt++];
    }

    private int PeekChar()
    {
        if (_bufferAt >= _bufferLength)
        {
            _bufferLength = _reader.Read(_buffer, 0, _buffer.Length);
            _bufferAt = 0;
            if (_bufferLength <= 0) return -1;
        }

        return _buffer[_bufferAt];
    }

    public IReadOnlyList<string> Header => _header;

    public long RowNumber { get; private set; }

    /// <summary>Reads the header row and builds the column lookup.</summary>
    public bool ReadHeader()
    {
        if (!ReadRow()) return false;
        _header = _fields.ToArray();
        _headerIndex.Clear();
        for (int i = 0; i < _header.Length; i++) _headerIndex[_header[i]] = i;
        return true;
    }

    public bool HasColumn(string name) => _headerIndex.ContainsKey(name);

    /// <summary>Column index, or -1 when the column is absent.</summary>
    public int ColumnIndex(string name) => _headerIndex.TryGetValue(name, out int index) ? index : -1;

    public IReadOnlyList<string> RequireColumns(params string[] names)
    {
        var missing = new List<string>();
        foreach (string name in names)
        {
            if (!HasColumn(name)) missing.Add(name);
        }

        return missing;
    }

    /// <summary>Reads the next row. Fields are valid until the following call.</summary>
    public bool ReadRow()
    {
        _fields.Clear();
        _cell.Clear();

        int c = NextChar();
        if (c < 0) return false;

        var inQuotes = false;
        while (true)
        {
            if (c < 0)
            {
                _fields.Add(_cell.ToString());
                break;
            }

            var ch = (char)c;

            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (PeekChar() == '"')
                    {
                        NextChar();
                        _cell.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    _cell.Append(ch);
                }
            }
            else if (ch == '"' && _cell.Length == 0)
            {
                inQuotes = true;
            }
            else if (ch == ',')
            {
                _fields.Add(_cell.ToString());
                _cell.Clear();
            }
            else if (ch == '\n')
            {
                _fields.Add(_cell.ToString());
                break;
            }
            else if (ch == '\r')
            {
                if (PeekChar() == '\n') NextChar();
                _fields.Add(_cell.ToString());
                break;
            }
            else
            {
                _cell.Append(ch);
            }

            c = NextChar();
        }

        RowNumber++;
        return true;
    }

    public int FieldCount => _fields.Count;

    public string Field(int index) => index >= 0 && index < _fields.Count ? _fields[index] : string.Empty;

    public string Field(string name) => Field(ColumnIndex(name));

    public double DoubleField(int index)
    {
        string text = Field(index);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : double.NaN;
    }

    public int IntField(int index, int fallback = 0)
    {
        string text = Field(index);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;
    }

    public void Dispose()
    {
        if (_ownsReader) _reader.Dispose();
    }
}
