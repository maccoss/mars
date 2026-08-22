// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.IO;
using Pwiz.Data.Common.Cv;
using Pwiz.Data.MsData;
using Pwiz.Data.MsData.Encoding;
using Pwiz.Data.MsData.Readers;

namespace MARS.Pwiz;

/// <summary>
/// The pwiz side of a write: read, wrap with MARS's correction, serialize.
/// </summary>
/// <remarks>
/// Compiled only when a pwiz-sharp checkout was available. Everything that has to exist
/// unconditionally lives in <see cref="PwizOutput"/>.
/// </remarks>
internal static class PwizWriteBackend
{
    public static PwizWriteResult Write(PwizWriteRequest request)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var msd = new MSData();
        ReaderList.Default.Read(request.InputPath, msd);

        if (msd.Run.SpectrumList is null)
            throw new InvalidDataException($"No spectra in {request.InputPath}.");

        var mars = new MarsSpectrumList(
            msd.Run.SpectrumList,
            request.Calibrator,
            request.Options,
            request.AcquisitionStartTime,
            request.Temperatures,
            request.Threads);

        msd.Run.SpectrumList = mars;

        MSDataFile.Write(msd, request.OutputPath, new WriteConfig
        {
            Format = FormatOf(request.Format),
            Indexed = true,
            EncoderConfig = EncoderFor(request.Encoding),
        });

        return new PwizWriteResult
        {
            SpectraSeen = mars.SpectraSeen,
            SpectraCorrected = mars.SpectraCorrected,
            MonotonicityFixes = mars.MonotonicityFixes,
            SpectraReverted = mars.SpectraReverted,
            OutputLength = new FileInfo(request.OutputPath).Length,
            ReaderTime = mars.ReaderTime,
            CorrectorTime = mars.CorrectorTime,
        };
    }

    private static WriteFormat FormatOf(MarsOutputFormat format) => format switch
    {
        // Reached only when the input was a vendor file: an mzML input is spliced instead.
        MarsOutputFormat.MzML => WriteFormat.Mzml,
        MarsOutputFormat.MzXml => WriteFormat.MzXml,
        MarsOutputFormat.MzMLb => WriteFormat.MzMLb,
        MarsOutputFormat.Mgf => WriteFormat.Mgf,
        _ => throw new ArgumentOutOfRangeException(
            nameof(format), format, "No pwiz writer for this format."),
    };

    /// <summary>
    /// Builds an encoder that matches the input rather than taking pwiz's defaults.
    /// </summary>
    /// <remarks>
    /// The default is 64-bit uncompressed, which made a Stellar run 61% larger than its input
    /// before this was set. The base config carries the m/z encoding and the intensity array
    /// gets a per-array override, since the two commonly differ.
    /// </remarks>
    private static BinaryEncoderConfig EncoderFor(SpectrumEncoding encoding)
    {
        var config = new BinaryEncoderConfig
        {
            Precision = encoding.Mz.Bits64 ? BinaryPrecision.Bits64 : BinaryPrecision.Bits32,
            Compression = encoding.Mz.Zlib ? BinaryCompression.Zlib : BinaryCompression.None,
        };

        config.PrecisionOverrides[CVID.MS_intensity_array] =
            encoding.Intensity.Bits64 ? BinaryPrecision.Bits64 : BinaryPrecision.Bits32;
        config.CompressionOverrides[CVID.MS_intensity_array] =
            encoding.Intensity.Zlib ? BinaryCompression.Zlib : BinaryCompression.None;

        return config;
    }
}
