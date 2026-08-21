// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using MARS.Core;

namespace MARS.Pwiz;

/// <summary>An output format MARS can write.</summary>
public enum MarsOutputFormat
{
    /// <summary>mzML. MARS writes this itself, by splicing bytes into a copy of the input.</summary>
    MzML,

    /// <summary>mzXML 3.2, through pwiz.</summary>
    MzXml,

    /// <summary>mzMLb, mzML in an HDF5 container, through pwiz.</summary>
    MzMLb,

    /// <summary>Mascot Generic Format - MS/MS peak lists only - through pwiz.</summary>
    Mgf,
}

/// <summary>What a pwiz-backed write did.</summary>
public sealed class PwizWriteResult
{
    public long SpectraSeen { get; init; }

    public long SpectraCorrected { get; init; }

    public long MonotonicityFixes { get; init; }

    public long SpectraReverted { get; init; }

    public long OutputLength { get; init; }
}

/// <summary>
/// Everything one pwiz-backed write needs. Deliberately free of pwiz types so that
/// <c>MARS</c> can reference this assembly whether or not pwiz-sharp was available at build
/// time.
/// </summary>
public sealed class PwizWriteRequest
{
    public required string InputPath { get; init; }

    public required string OutputPath { get; init; }

    public required MarsOutputFormat Format { get; init; }

    /// <summary>The fitted model, or null to copy spectra through uncorrected.</summary>
    public MzCalibrator? Calibrator { get; init; }

    public CorrectionOptions Options { get; init; } = new();

    /// <summary>Run start as a Unix timestamp, for the absolute_time feature.</summary>
    public double? AcquisitionStartTime { get; init; }

    public TemperatureSet? Temperatures { get; init; }

    /// <summary>
    /// How to encode the binary arrays. Defaults to what msconvert writes; callers should
    /// pass <see cref="MzMLEncoding.Sniff"/> of the input so the output matches it.
    /// </summary>
    /// <remarks>
    /// pwiz's own default is 64-bit UNCOMPRESSED, which inflated a Stellar run by 61% before
    /// this was set deliberately. An output larger than it needs to be is a cost the user did
    /// not ask for.
    /// </remarks>
    public SpectrumEncoding Encoding { get; init; } = SpectrumEncoding.Default;
}

/// <summary>
/// Writes MARS-corrected spectra in the formats pwiz can serialize.
/// </summary>
/// <remarks>
/// <para>
/// mzML does not come through here. MARS writes mzML by splicing corrected bytes into a copy
/// of the input, which keeps every byte it did not change identical by construction; see
/// docs/mzml-passthrough.md. This exists for the formats that byte-splicing cannot reach,
/// where the file has to be built rather than edited.
/// </para>
/// <para>
/// When MARS is built without a pwiz-sharp checkout, <see cref="Available"/> is false and
/// <see cref="Write"/> throws. Everything else about MARS is unaffected.
/// </para>
/// </remarks>
public static partial class PwizOutput
{
    /// <summary>Whether this build can write the pwiz-backed formats.</summary>
    public static bool Available =>
#if MARS_NO_PWIZ
        false;
#else
        true;
#endif

    /// <summary>The formats this build can write, mzML included.</summary>
    public static IReadOnlyList<MarsOutputFormat> Supported =>
        Available
            ? new[] { MarsOutputFormat.MzML, MarsOutputFormat.MzXml, MarsOutputFormat.MzMLb, MarsOutputFormat.Mgf }
            : new[] { MarsOutputFormat.MzML };

    /// <summary>Parses a format name, case-insensitively. Returns false for anything else.</summary>
    public static bool TryParse(string? name, out MarsOutputFormat format)
    {
        switch (name?.Trim().ToLowerInvariant())
        {
            case null or "" or "mzml": format = MarsOutputFormat.MzML; return true;
            case "mzxml": format = MarsOutputFormat.MzXml; return true;
            case "mzmlb": format = MarsOutputFormat.MzMLb; return true;
            case "mgf": format = MarsOutputFormat.Mgf; return true;
            default: format = MarsOutputFormat.MzML; return false;
        }
    }

    /// <summary>The file extension for a format, including the dot.</summary>
    public static string Extension(MarsOutputFormat format) => format switch
    {
        MarsOutputFormat.MzML => ".mzML",
        MarsOutputFormat.MzXml => ".mzXML",
        MarsOutputFormat.MzMLb => ".mzMLb",
        MarsOutputFormat.Mgf => ".mgf",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown output format."),
    };

    /// <summary>The name a user types for a format.</summary>
    public static string Name(MarsOutputFormat format) => format switch
    {
        MarsOutputFormat.MzML => "mzML",
        MarsOutputFormat.MzXml => "mzXML",
        MarsOutputFormat.MzMLb => "mzMLb",
        MarsOutputFormat.Mgf => "mgf",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown output format."),
    };

    /// <summary>
    /// Whether a format loses information relative to mzML, so callers can warn rather than
    /// let a user discover it downstream.
    /// </summary>
    public static string? LossWarning(MarsOutputFormat format) => format switch
    {
        // MGF carries MS2 peak lists and little else: no MS1, no chromatograms, and none of
        // the scan metadata MARS's own features are computed from. A corrected MGF cannot be
        // fed back to MARS.
        MarsOutputFormat.Mgf =>
            "mgf keeps MS2 peak lists only - no MS1 spectra, no chromatograms, and none of the "
            + "scan metadata MARS reads. The result cannot be re-calibrated or re-analysed by "
            + "MARS.",

        // mzXML predates most of the CV vocabulary and cannot express ion mobility or several
        // isolation-window terms.
        MarsOutputFormat.MzXml =>
            "mzXML cannot express everything mzML can - ion mobility and some isolation-window "
            + "terms have nowhere to go. Prefer mzML or mzMLb unless a downstream tool requires "
            + "mzXML.",

        _ => null,
    };

    /// <summary>
    /// Reads <paramref name="request"/>'s input, applies the model, and writes the result in
    /// the requested format.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// This build has no pwiz-sharp, or the format is one pwiz cannot write.
    /// </exception>
    public static PwizWriteResult Write(PwizWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Checked BEFORE availability, because mzML never needs pwiz. Reporting "this build
        // has no pwiz-sharp" for the one format MARS writes itself would send the reader off
        // to fix something that is not broken.
        if (request.Format == MarsOutputFormat.MzML)
        {
            // Not a limitation - a deliberate refusal. MARS writes mzML by splicing corrected
            // bytes into a copy of the input, which keeps everything it did not change
            // identical by construction. Routing mzML through here would quietly give that up.
            throw new NotSupportedException(
                "mzML is written by MARS's own byte-splice writer, not through pwiz. "
                + "See docs/mzml-passthrough.md.");
        }

        RequireAvailable(request.Format);

#if MARS_NO_PWIZ
        throw new NotSupportedException("unreachable: RequireAvailable throws first.");
#else
        return PwizWriteBackend.Write(request);
#endif
    }

    private static void RequireAvailable(MarsOutputFormat format)
    {
        if (Available) return;

        throw new NotSupportedException(
            $"This build of MARS cannot write {Name(format)}: it was built without a pwiz-sharp "
            + "checkout. Rebuild with -p:PwizSharpDir=<path>/pwiz/pwiz-sharp, or write mzML, "
            + "which MARS writes itself.");
    }
}
