// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Parses one <spectrum> element out of an mzML byte span.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using MARS.Core;

namespace MARS.IO;

/// <summary>Where one binaryDataArray lives inside a spectrum span, in bytes relative to it.</summary>
public sealed class BinaryArrayLocation
{
    public BinaryArrayEncoding Encoding;

    public bool IsMzArray;

    public bool IsIntensityArray;

    /// <summary>Offset of the encodedLength attribute's value, or -1 when absent.</summary>
    public int EncodedLengthStart = -1;

    public int EncodedLengthLength;

    /// <summary>Offset of the text inside the binary element, or -1 for an empty element.</summary>
    public int BinaryTextStart = -1;

    public int BinaryTextLength;
}

/// <summary>Everything MARS needs from one spectrum, plus the byte ranges to splice.</summary>
public sealed class ParsedSpectrum
{
    public readonly SpectrumRecord Record = new();

    public readonly List<BinaryArrayLocation> Arrays = new();

    public int MzArrayIndex = -1;

    public int IntensityArrayIndex = -1;

    public int DefaultArrayLength;
}

/// <summary>
/// Reads spectrum metadata by CV ACCESSION rather than by name. Names are display strings
/// that vary between writers; accessions are the contract. It also means the
/// <c>userParam name="ms level"</c> that pwiz writes inside an isolation window cannot be
/// mistaken for the real MS:1000511.
/// </summary>
public static class MzMLSpectrumParser
{
    public const string MsLevel = "MS:1000511";
    public const string TotalIonCurrent = "MS:1000285";
    public const string ScanStartTime = "MS:1000016";
    public const string IonInjectionTime = "MS:1000927";
    public const string IsolationWindowTarget = "MS:1000827";
    public const string IsolationWindowLowerOffset = "MS:1000828";
    public const string IsolationWindowUpperOffset = "MS:1000829";
    public const string SelectedIonMz = "MS:1000744";
    public const string Bit32Float = "MS:1000521";
    public const string Bit64Float = "MS:1000523";
    public const string ZlibCompression = "MS:1000574";
    public const string MzArray = "MS:1000514";
    public const string IntensityArray = "MS:1000515";
    public const string UnitMinute = "UO:0000031";
    public const string UnitSecond = "UO:0000010";

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        ConformanceLevel = ConformanceLevel.Fragment,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        DtdProcessing = DtdProcessing.Prohibit,
        CloseInput = false,
    };

    /// <summary>
    /// Parses metadata and array encodings from a spectrum span. Binary payloads are NOT
    /// decoded here; the caller decides which arrays it needs.
    /// </summary>
    public static ParsedSpectrum Parse(byte[] buffer, int start, int length)
    {
        var parsed = new ParsedSpectrum();
        SpectrumRecord record = parsed.Record;

        using var stream = new MemoryStream(buffer, start, length, writable: false);
        using XmlReader reader = XmlReader.Create(stream, ReaderSettings);

        bool inIsolationWindow = false;
        bool haveIsolationWindow = false;
        bool inSelectedIon = false;
        double target = 0, lowerOffset = 0, upperOffset = 0;
        double selectedIonMz = double.NaN;
        BinaryArrayLocation? currentArray = null;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                switch (reader.LocalName)
                {
                    case "isolationWindow":
                        inIsolationWindow = false;
                        break;
                    case "selectedIon":
                        inSelectedIon = false;
                        break;
                    case "binaryDataArray":
                        currentArray = null;
                        break;
                }

                continue;
            }

            if (reader.NodeType != XmlNodeType.Element) continue;

            switch (reader.LocalName)
            {
                case "spectrum":
                    record.Id = reader.GetAttribute("id") ?? string.Empty;
                    record.Index = ParseInt(reader.GetAttribute("index"));
                    parsed.DefaultArrayLength = ParseInt(reader.GetAttribute("defaultArrayLength"));
                    break;

                case "isolationWindow":
                    inIsolationWindow = true;
                    break;

                case "selectedIon":
                    inSelectedIon = true;
                    break;

                case "binaryDataArray":
                    currentArray = new BinaryArrayLocation
                    {
                        // mzML has no "uncompressed" default marker that must be present, so
                        // absence of MS:1000574 means no compression.
                        Encoding = new BinaryArrayEncoding(is64Bit: true, zlib: false),
                    };
                    parsed.Arrays.Add(currentArray);
                    break;

                case "cvParam":
                {
                    string accession = reader.GetAttribute("accession") ?? string.Empty;
                    string? value = reader.GetAttribute("value");

                    if (currentArray is not null)
                    {
                        switch (accession)
                        {
                            case Bit64Float:
                                currentArray.Encoding = new BinaryArrayEncoding(true, currentArray.Encoding.Zlib);
                                continue;
                            case Bit32Float:
                                currentArray.Encoding = new BinaryArrayEncoding(false, currentArray.Encoding.Zlib);
                                continue;
                            case ZlibCompression:
                                currentArray.Encoding = new BinaryArrayEncoding(currentArray.Encoding.Is64Bit, true);
                                continue;
                            case MzArray:
                                currentArray.IsMzArray = true;
                                parsed.MzArrayIndex = parsed.Arrays.Count - 1;
                                continue;
                            case IntensityArray:
                                currentArray.IsIntensityArray = true;
                                parsed.IntensityArrayIndex = parsed.Arrays.Count - 1;
                                continue;
                        }

                        continue;
                    }

                    switch (accession)
                    {
                        case MsLevel:
                            record.MsLevel = ParseInt(value);
                            break;

                        case TotalIonCurrent:
                            record.ReportedTic = ParseDouble(value);
                            break;

                        case ScanStartTime:
                        {
                            double time = ParseDouble(value);
                            string unit = reader.GetAttribute("unitAccession") ?? string.Empty;
                            // Thermo files record minutes; the specification permits seconds.
                            record.RetentionTime = unit == UnitSecond ? time / 60.0 : time;
                            break;
                        }

                        case IonInjectionTime:
                            // The cvParam is milliseconds; MARS works in seconds throughout.
                            record.InjectionTime = ParseDouble(value) / 1000.0;
                            break;

                        case IsolationWindowTarget when inIsolationWindow:
                            target = ParseDouble(value);
                            haveIsolationWindow = true;
                            break;

                        case IsolationWindowLowerOffset when inIsolationWindow:
                            lowerOffset = ParseDouble(value);
                            break;

                        case IsolationWindowUpperOffset when inIsolationWindow:
                            upperOffset = ParseDouble(value);
                            break;

                        case SelectedIonMz when inSelectedIon:
                            if (double.IsNaN(selectedIonMz)) selectedIonMz = ParseDouble(value);
                            break;
                    }

                    break;
                }
            }
        }

        if (haveIsolationWindow)
        {
            record.PrecursorMzCenter = target;
            record.PrecursorMzLow = target - lowerOffset;
            record.PrecursorMzHigh = target + upperOffset;
        }
        else if (!double.IsNaN(selectedIonMz))
        {
            // Fallback used by the Python implementation when no isolation window is written.
            record.PrecursorMzCenter = selectedIonMz;
            record.PrecursorMzLow = selectedIonMz - 0.5;
            record.PrecursorMzHigh = selectedIonMz + 0.5;
        }

        record.ScanNumber = ParseScanNumber(record.Id, record.Index);
        LocateArrayBytes(buffer, start, length, parsed);
        return parsed;
    }

    /// <summary>
    /// Finds the byte ranges of each binaryDataArray's encodedLength value and binary text.
    /// <para>
    /// This is a byte scan rather than another XmlReader pass because XmlReader reports
    /// line and character positions, not byte offsets, and the writer splices bytes. Tag
    /// delimiters cannot appear unescaped inside attribute values or base64 content, so
    /// scanning for them inside a single spectrum element is unambiguous.
    /// </para>
    /// </summary>
    private static void LocateArrayBytes(byte[] buffer, int start, int length, ParsedSpectrum parsed)
    {
        var span = new ReadOnlySpan<byte>(buffer, start, length);
        ReadOnlySpan<byte> arrayTag = "<binaryDataArray"u8;
        ReadOnlySpan<byte> encodedLengthAttr = "encodedLength=\""u8;
        ReadOnlySpan<byte> binaryOpen = "<binary>"u8;
        ReadOnlySpan<byte> binaryClose = "</binary>"u8;
        ReadOnlySpan<byte> binaryEmpty = "<binary/>"u8;

        int cursor = 0;
        int arrayIndex = 0;
        while (arrayIndex < parsed.Arrays.Count)
        {
            int tag = IndexOf(span, arrayTag, cursor);
            if (tag < 0) break;

            int after = tag + arrayTag.Length;
            // Skip <binaryDataArrayList ...>, which shares the prefix.
            if (after < span.Length && span[after] != (byte)' ' && span[after] != (byte)'>' &&
                span[after] != (byte)'\r' && span[after] != (byte)'\n' && span[after] != (byte)'\t')
            {
                cursor = after;
                continue;
            }

            BinaryArrayLocation location = parsed.Arrays[arrayIndex];

            int tagEnd = IndexOf(span, ">"u8, after);
            if (tagEnd < 0) break;

            int attr = IndexOf(span[..tagEnd], encodedLengthAttr, after);
            if (attr >= 0)
            {
                int valueStart = attr + encodedLengthAttr.Length;
                int valueEnd = IndexOf(span, "\""u8, valueStart);
                if (valueEnd > valueStart)
                {
                    location.EncodedLengthStart = valueStart;
                    location.EncodedLengthLength = valueEnd - valueStart;
                }
            }

            int open = IndexOf(span, binaryOpen, tagEnd);
            int empty = IndexOf(span, binaryEmpty, tagEnd);
            if (empty >= 0 && (open < 0 || empty < open))
            {
                location.BinaryTextStart = -1;
                cursor = empty + binaryEmpty.Length;
            }
            else if (open >= 0)
            {
                int textStart = open + binaryOpen.Length;
                int close = IndexOf(span, binaryClose, textStart);
                if (close < 0) break;
                location.BinaryTextStart = textStart;
                location.BinaryTextLength = close - textStart;
                cursor = close + binaryClose.Length;
            }
            else
            {
                break;
            }

            arrayIndex++;
        }
    }

    public static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle, int from)
    {
        if (from >= haystack.Length) return -1;
        int found = haystack[from..].IndexOf(needle);
        return found < 0 ? -1 : found + from;
    }

    /// <summary>
    /// Pulls the scan number out of a Thermo nativeID
    /// ("controllerType=0 controllerNumber=1 scan=4321"), falling back to the list index.
    /// </summary>
    public static int ParseScanNumber(string id, int index)
    {
        const string marker = "scan=";
        int at = id.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return index;

        int start = at + marker.Length;
        int end = start;
        while (end < id.Length && char.IsDigit(id[end])) end++;
        return end > start && int.TryParse(id.AsSpan(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out int scan)
            ? scan
            : index;
    }

    private static int ParseInt(string? value) =>
        value is not null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : 0;

    private static double ParseDouble(string? value) =>
        value is not null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
            ? result
            : 0.0;

    /// <summary>Parses the run's startTimeStamp attribute into a Unix timestamp in seconds.</summary>
    public static double? ParseStartTimeStamp(string timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp)) return null;
        string text = timestamp.Trim();
        if (text.EndsWith("Z", StringComparison.Ordinal)) text = text[..^1] + "+00:00";

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
        {
            return parsed.ToUnixTimeMilliseconds() / 1000.0;
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTime plain))
            return new DateTimeOffset(plain.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeMilliseconds() / 1000.0;

        return null;
    }

    internal static string GetUtf8String(byte[] buffer, int start, int length) =>
        Encoding.UTF8.GetString(buffer, start, length);
}
