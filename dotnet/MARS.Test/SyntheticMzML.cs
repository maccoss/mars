// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Builds a small, valid indexed mzML so the test suite needs no data files.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MARS.IO;

namespace MARS.Test;

public static partial class SyntheticMzML
{
    /// <summary>
    /// Writes an indexed mzML shaped like pwiz output: an indexedmzML wrapper, Thermo
    /// nativeIDs, alternating MS1 and MS2 with isolation windows and injection times, and a
    /// trailer whose checksum follows the specification.
    /// </summary>
    public static void Write(
        string path,
        int spectrumCount,
        int chromatogramCount,
        BinaryArrayEncoding? mzEncoding = null,
        BinaryArrayEncoding? intensityEncoding = null,
        int seed = 12345,
        int peaksPerSpectrum = 0,
        MassAnalyzerLayout analyzers = MassAnalyzerLayout.None,
        bool constantInjectionTime = false)
    {
        BinaryArrayEncoding mzArrayEncoding = mzEncoding ?? new BinaryArrayEncoding(true, true);
        BinaryArrayEncoding intensityArrayEncoding = intensityEncoding ?? new BinaryArrayEncoding(true, true);

        var body = new StringBuilder();
        body.Append("""
            <?xml version="1.0" encoding="utf-8"?>
            <indexedmzML xmlns="http://psi.hupo.org/ms/mzml" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:schemaLocation="http://psi.hupo.org/ms/mzml http://psidev.info/files/ms/mzML/xsd/mzML1.1.2_idx.xsd">
              <mzML xmlns="http://psi.hupo.org/ms/mzml" id="synthetic" version="1.1.0">
                <cvList count="2">
                  <cv id="MS" fullName="Proteomics Standards Initiative Mass Spectrometry Ontology" version="4.1.163" URI="https://raw.githubusercontent.com/HUPO-PSI/psi-ms-CV/master/psi-ms.obo"/>
                  <cv id="UO" fullName="Unit Ontology" version="09:04:2014" URI="https://raw.githubusercontent.com/bio-ontology-research-group/unit-ontology/master/unit.obo"/>
                </cvList>
                <fileDescription>
                  <fileContent>
                    <cvParam cvRef="MS" accession="MS:1000579" name="MS1 spectrum" value=""/>
                    <cvParam cvRef="MS" accession="MS:1000580" name="MSn spectrum" value=""/>
                  </fileContent>
                  <sourceFileList count="1">
                    <sourceFile id="RAW1" name="synthetic.raw" location="file:///synthetic">
                      <cvParam cvRef="MS" accession="MS:1000768" name="Thermo nativeID format" value=""/>
                    </sourceFile>
                  </sourceFileList>
                </fileDescription>INSTRUMENT_CONFIGURATION_LIST
                <run id="synthetic" startTimeStamp="2024-12-03T04:05:54Z"DEFAULT_CONFIGURATION defaultSourceFileRef="RAW1">

            """.Replace("\r\n", "\n"));

        body.Replace("INSTRUMENT_CONFIGURATION_LIST", InstrumentConfiguration(analyzers))
            .Replace("DEFAULT_CONFIGURATION", DefaultConfigurationAttribute(analyzers));

        body.Append("      <spectrumList count=\"")
            .Append(spectrumCount.ToString(CultureInfo.InvariantCulture))
            .Append("\" defaultDataProcessingRef=\"dp\">\n");

        var random = new Random(seed);
        for (var i = 0; i < spectrumCount; i++)
        {
            int scan = i + 1;
            int msLevel = i % 4 == 0 ? 1 : 2;
            int peaks = peaksPerSpectrum > 0 ? peaksPerSpectrum : 5 + random.Next(40);

            var mz = new double[peaks];
            var intensity = new double[peaks];
            double value = 200.0 + random.NextDouble();
            for (var p = 0; p < peaks; p++)
            {
                value += 0.5 + (random.NextDouble() * 25.0);
                mz[p] = Math.Round(value, 6);
                intensity[p] = Math.Round(100.0 + (random.NextDouble() * 50000.0), 4);
            }

            double retentionTime = i * 0.01;
            double tic = 0;
            foreach (double v in intensity) tic += v;

            body.Append("        <spectrum index=\"").Append(i.ToString(CultureInfo.InvariantCulture))
                .Append("\" id=\"controllerType=0 controllerNumber=1 scan=")
                .Append(scan.ToString(CultureInfo.InvariantCulture))
                .Append("\" defaultArrayLength=\"").Append(peaks.ToString(CultureInfo.InvariantCulture))
                .Append("\"")
                .Append(Ms2ConfigurationReference(analyzers, msLevel))
                .Append(">\n");

            body.Append("          <cvParam cvRef=\"MS\" accession=\"MS:1000511\" name=\"ms level\" value=\"")
                .Append(msLevel.ToString(CultureInfo.InvariantCulture)).Append("\"/>\n");
            body.Append("          <cvParam cvRef=\"MS\" accession=\"MS:1000127\" name=\"centroid spectrum\" value=\"\"/>\n");
            body.Append("          <cvParam cvRef=\"MS\" accession=\"MS:1000285\" name=\"total ion current\" value=\"")
                .Append(tic.ToString("R", CultureInfo.InvariantCulture)).Append("\"/>\n");

            body.Append("          <scanList count=\"1\">\n            <scan>\n");
            body.Append("              <cvParam cvRef=\"MS\" accession=\"MS:1000016\" name=\"scan start time\" value=\"")
                .Append(retentionTime.ToString("R", CultureInfo.InvariantCulture))
                .Append("\" unitCvRef=\"UO\" unitAccession=\"UO:0000031\" unitName=\"minute\"/>\n");
            body.Append("              <cvParam cvRef=\"MS\" accession=\"MS:1000927\" name=\"ion injection time\" value=\"")
                .Append((constantInjectionTime ? 10.0 : 10.0 + (i % 7))
                    .ToString("R", CultureInfo.InvariantCulture))
                .Append("\" unitCvRef=\"UO\" unitAccession=\"UO:0000028\" unitName=\"millisecond\"/>\n");
            body.Append("            </scan>\n          </scanList>\n");

            if (msLevel == 2)
            {
                double target = 400.0 + (i % 20);
                body.Append("          <precursorList count=\"1\">\n            <precursor>\n              <isolationWindow>\n");
                body.Append("                <cvParam cvRef=\"MS\" accession=\"MS:1000827\" name=\"isolation window target m/z\" value=\"")
                    .Append(target.ToString("R", CultureInfo.InvariantCulture)).Append("\"/>\n");
                body.Append("                <cvParam cvRef=\"MS\" accession=\"MS:1000828\" name=\"isolation window lower offset\" value=\"0.5\"/>\n");
                body.Append("                <cvParam cvRef=\"MS\" accession=\"MS:1000829\" name=\"isolation window upper offset\" value=\"0.5\"/>\n");
                // pwiz writes a userParam that shares the name of a real cvParam; a reader
                // matching on name rather than accession trips over exactly this.
                body.Append("                <userParam name=\"ms level\" value=\"1\"/>\n");
                body.Append("              </isolationWindow>\n            </precursor>\n          </precursorList>\n");
            }

            body.Append("          <binaryDataArrayList count=\"2\">\n");
            AppendBinaryArray(body, mz, mzArrayEncoding, isMzArray: true);
            AppendBinaryArray(body, intensity, intensityArrayEncoding, isMzArray: false);
            body.Append("          </binaryDataArrayList>\n");
            body.Append("        </spectrum>\n");
        }

        body.Append("      </spectrumList>\n");

        if (chromatogramCount > 0)
        {
            body.Append("      <chromatogramList count=\"")
                .Append(chromatogramCount.ToString(CultureInfo.InvariantCulture))
                .Append("\" defaultDataProcessingRef=\"dp\">\n");

            for (var c = 0; c < chromatogramCount; c++)
            {
                var times = new double[10];
                var values = new double[10];
                for (var p = 0; p < times.Length; p++)
                {
                    times[p] = p * 0.1;
                    values[p] = 1000.0 * (p + 1);
                }

                body.Append("        <chromatogram index=\"").Append(c.ToString(CultureInfo.InvariantCulture))
                    .Append("\" id=\"chrom").Append(c.ToString(CultureInfo.InvariantCulture))
                    .Append("\" defaultArrayLength=\"10\">\n");
                body.Append("          <cvParam cvRef=\"MS\" accession=\"MS:1000235\" name=\"total ion current chromatogram\" value=\"\"/>\n");
                body.Append("          <binaryDataArrayList count=\"2\">\n");
                AppendBinaryArray(body, times, new BinaryArrayEncoding(true, true), isMzArray: false, timeArray: true);
                AppendBinaryArray(body, values, new BinaryArrayEncoding(true, true), isMzArray: false);
                body.Append("          </binaryDataArrayList>\n");
                body.Append("        </chromatogram>\n");
            }

            body.Append("      </chromatogramList>\n");
        }

        body.Append("    </run>\n  </mzML>\n");

        string content = body.ToString();
        byte[] contentBytes = Encoding.UTF8.GetBytes(content);

        // Index offsets are byte positions into the finished stream, so build the index from
        // the encoded bytes rather than from character positions.
        var spectrumOffsets = new List<(string Id, long Offset)>();
        var chromatogramOffsets = new List<(string Id, long Offset)>();
        CollectOffsets(contentBytes, "<spectrum ", spectrumOffsets);
        CollectOffsets(contentBytes, "<chromatogram ", chromatogramOffsets);

        var index = new StringBuilder();
        index.Append("  <indexList count=\"2\">\n");
        AppendIndex(index, "spectrum", spectrumOffsets);
        AppendIndex(index, "chromatogram", chromatogramOffsets);
        index.Append("  </indexList>\n");

        byte[] indexBytes = Encoding.UTF8.GetBytes(index.ToString());
        long indexListOffset = contentBytes.Length + 2; // past the two-space indent
        byte[] offsetLine = Encoding.UTF8.GetBytes(
            "  <indexListOffset>" + indexListOffset.ToString(CultureInfo.InvariantCulture) + "</indexListOffset>\n");
        byte[] checksumOpen = Encoding.UTF8.GetBytes("  <fileChecksum>");

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha.AppendData(contentBytes);
        sha.AppendData(indexBytes);
        sha.AppendData(offsetLine);
        sha.AppendData(checksumOpen);
        string checksum = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();

        using FileStream stream = File.Create(path);
        stream.Write(contentBytes);
        stream.Write(indexBytes);
        stream.Write(offsetLine);
        stream.Write(checksumOpen);
        stream.Write(Encoding.UTF8.GetBytes(checksum + "</fileChecksum>\n</indexedmzML>"));
    }

    private static void AppendIndex(StringBuilder text, string name, List<(string Id, long Offset)> entries)
    {
        text.Append("    <index name=\"").Append(name).Append("\">\n");
        foreach ((string id, long offset) in entries)
        {
            text.Append("      <offset idRef=\"").Append(id).Append("\">")
                .Append(offset.ToString(CultureInfo.InvariantCulture)).Append("</offset>\n");
        }

        text.Append("    </index>\n");
    }

    private static void CollectOffsets(byte[] content, string openTag, List<(string Id, long Offset)> destination)
    {
        byte[] needle = Encoding.UTF8.GetBytes(openTag);
        var at = 0;
        while (true)
        {
            int found = content.AsSpan(at).IndexOf(needle);
            if (found < 0) break;
            int absolute = at + found;

            int idAt = content.AsSpan(absolute).IndexOf(" id=\""u8);
            if (idAt < 0) break;
            int idStart = absolute + idAt + 5;
            int idEnd = content.AsSpan(idStart).IndexOf((byte)'"') + idStart;

            destination.Add((Encoding.UTF8.GetString(content, idStart, idEnd - idStart), absolute));
            at = absolute + needle.Length;
        }
    }

    private static void AppendBinaryArray(
        StringBuilder text, double[] values, BinaryArrayEncoding encoding, bool isMzArray, bool timeArray = false)
    {
        string base64 = EncodeBase64(values, encoding);

        text.Append("            <binaryDataArray encodedLength=\"")
            .Append(base64.Length.ToString(CultureInfo.InvariantCulture)).Append("\">\n");
        text.Append(encoding.Is64Bit
            ? "              <cvParam cvRef=\"MS\" accession=\"MS:1000523\" name=\"64-bit float\" value=\"\"/>\n"
            : "              <cvParam cvRef=\"MS\" accession=\"MS:1000521\" name=\"32-bit float\" value=\"\"/>\n");
        text.Append(encoding.Zlib
            ? "              <cvParam cvRef=\"MS\" accession=\"MS:1000574\" name=\"zlib compression\" value=\"\"/>\n"
            : "              <cvParam cvRef=\"MS\" accession=\"MS:1000576\" name=\"no compression\" value=\"\"/>\n");

        if (timeArray)
        {
            text.Append("              <cvParam cvRef=\"MS\" accession=\"MS:1000595\" name=\"time array\" value=\"\" unitCvRef=\"UO\" unitAccession=\"UO:0000031\" unitName=\"minute\"/>\n");
        }
        else if (isMzArray)
        {
            text.Append("              <cvParam cvRef=\"MS\" accession=\"MS:1000514\" name=\"m/z array\" value=\"\" unitCvRef=\"MS\" unitAccession=\"MS:1000040\" unitName=\"m/z\"/>\n");
        }
        else
        {
            text.Append("              <cvParam cvRef=\"MS\" accession=\"MS:1000515\" name=\"intensity array\" value=\"\" unitCvRef=\"MS\" unitAccession=\"MS:1000131\" unitName=\"number of detector counts\"/>\n");
        }

        text.Append("              <binary>").Append(base64).Append("</binary>\n");
        text.Append("            </binaryDataArray>\n");
    }

    private static string EncodeBase64(double[] values, BinaryArrayEncoding encoding)
    {
        byte[] raw;
        if (encoding.Is64Bit)
        {
            raw = new byte[values.Length * 8];
            MemoryMarshal.Cast<double, byte>(values).CopyTo(raw);
        }
        else
        {
            var floats = new float[values.Length];
            for (var i = 0; i < values.Length; i++) floats[i] = (float)values[i];
            raw = new byte[values.Length * 4];
            MemoryMarshal.Cast<float, byte>(floats).CopyTo(raw);
        }

        if (!encoding.Zlib) return Convert.ToBase64String(raw);

        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        return Convert.ToBase64String(output.ToArray());
    }
}
