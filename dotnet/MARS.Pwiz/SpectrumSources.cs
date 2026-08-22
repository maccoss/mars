// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using MARS.Core;
using MARS.IO;

namespace MARS.Pwiz;

/// <summary>
/// Opens a run, choosing a reader from the file rather than from a flag.
/// </summary>
/// <remarks>
/// mzML goes to MARS's own reader, which is faster and is what the byte-splice writer needs
/// the byte offsets from. Everything else goes to pwiz. A user should not have to tell MARS
/// which of its readers to use when the extension already says.
/// </remarks>
public static class SpectrumSources
{
    /// <summary>Extensions MARS can read without pwiz.</summary>
    private static readonly string[] NativeExtensions = { ".mzml" };

    /// <summary>
    /// Vendor formats pwiz can read. Thermo, Bruker and Sciex are referenced; the rest are
    /// listed so that MARS can say "this build cannot read that" rather than "unrecognized
    /// file", and so that a format gated by platform - Sciex off Windows - gives the same
    /// answer.
    /// </summary>
    private static readonly Dictionary<string, string> VendorExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".raw"] = "Thermo",
        [".wiff"] = "Sciex",
        [".wiff2"] = "Sciex",

        // Bruker and Agilent both use a .d directory; pwiz's readers identify which by what is
        // inside it, so MARS does not have to guess here.
        [".d"] = "Bruker or Agilent",
        [".tdf"] = "Bruker",
        [".tsf"] = "Bruker",
        [".baf"] = "Bruker",
        [".lcd"] = "Shimadzu",
        [".uimf"] = "UIMF",
    };

    /// <summary>
    /// Vendors this build carries a reader for AND can run on this machine.
    /// </summary>
    /// <remarks>
    /// Architecture matters, not only the build. Thermo's SDK is managed and runs anywhere;
    /// Bruker's and Sciex's are native x64, so an arm64 build stages libraries it cannot load.
    /// Advertising them there would be a promise broken at the moment someone opens a file,
    /// which is exactly what this list exists to avoid.
    /// </remarks>
    private static readonly HashSet<string> LinkedVendors = BuildLinkedVendors();

    private static HashSet<string> BuildLinkedVendors()
    {
        var vendors = new HashSet<string>(StringComparer.Ordinal);
#if !MARS_NO_PWIZ
        vendors.Add("Thermo");

        bool nativeX64 = RuntimeInformation.ProcessArchitecture == Architecture.X64;
        if (nativeX64)
        {
            vendors.Add("Bruker or Agilent");
            vendors.Add("Bruker");
#if MARS_SCIEX
            vendors.Add("Sciex");
#endif
        }
#endif
        return vendors;
    }

    /// <summary>True when this build can actually open the path.</summary>
    /// <remarks>
    /// Used for directory scanning, where picking up a file that cannot be opened would turn
    /// a usable folder into a failed run. A path named explicitly goes through
    /// <see cref="IsRecognized"/> instead, so that it gets a reason rather than silence.
    /// </remarks>
    public static bool IsReadable(string path) =>
        IsNative(path) ||
        (VendorExtensions.TryGetValue(Path.GetExtension(path), out string? vendor) &&
         LinkedVendors.Contains(vendor));

    /// <summary>
    /// True when MARS knows what the format is, whether or not this build can open it. A
    /// recognized-but-unavailable format earns an explanation from <see cref="Open"/>.
    /// </summary>
    public static bool IsRecognized(string path) =>
        IsNative(path) || VendorExtensions.ContainsKey(Path.GetExtension(path));

    /// <summary>True when this path is an mzML, which MARS reads itself.</summary>
    public static bool IsNative(string path) =>
        Array.IndexOf(NativeExtensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;

    /// <summary>
    /// Whether writing mzML for this input can use the byte-splice writer.
    /// </summary>
    /// <remarks>
    /// Only an mzML input can be spliced, because splicing means copying the input and
    /// replacing the ranges that changed. A vendor file has no mzML to copy, so its mzML has
    /// to be built - the guarantee does not apply and cannot be pretended at.
    /// </remarks>
    public static bool CanSplice(string path) => IsNative(path);

    /// <summary>Opens a run for reading.</summary>
    /// <exception cref="NotSupportedException">
    /// The format needs pwiz and this build has none, or MARS does not recognize it.
    /// </exception>
    public static ISpectrumSource Open(string path)
    {
        if (IsNative(path)) return new MzMLSpectrumSource(path);

        string extension = Path.GetExtension(path);
        if (!VendorExtensions.TryGetValue(extension, out string? vendor))
        {
            throw new NotSupportedException(
                $"MARS does not recognize '{extension}'. Expected .mzML or a vendor format "
                + "MARS was built to read.");
        }

#if MARS_NO_PWIZ
        throw new NotSupportedException(
            $"Reading {vendor} data ({extension}) needs pwiz-sharp, and this build of MARS was "
            + "made without it. Rebuild with -p:PwizSharpDir=<path>/pwiz/pwiz-sharp "
            + "-p:IAgreeToVendorLicenses=true, or convert to mzML first.");
#else
        try
        {
            return new PwizSpectrumSource(path);
        }
        catch (Exception ex) when (ex is TypeInitializationException or DllNotFoundException
                                      or FileNotFoundException or BadImageFormatException)
        {
            // The vendor SDK is gated behind IAgreeToVendorLicenses at pwiz build time, so a
            // build that has pwiz but not the SDK fails here rather than at compile time.
            // Saying which knob is missing beats a load-failure stack trace.
            throw new NotSupportedException(
                $"This build cannot read {vendor} data ({extension}): the vendor SDK was not "
                + "available when pwiz-sharp was built. Rebuild pwiz-sharp with "
                + $"-p:IAgreeToVendorLicenses=true. ({ex.GetType().Name}: {ex.Message})", ex);
        }
#endif
    }

    /// <summary>
    /// What this build can read, for <c>--version</c>. Only formats it actually carries a
    /// reader for - a list that promised Shimadzu because the name appears in a table would be
    /// worse than no list.
    /// </summary>
    public static IEnumerable<string> ReadableExtensions()
    {
        yield return ".mzML";
        foreach (KeyValuePair<string, string> entry in VendorExtensions)
        {
            if (LinkedVendors.Contains(entry.Value)) yield return entry.Key;
        }
    }
}
