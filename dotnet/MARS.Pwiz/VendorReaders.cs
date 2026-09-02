// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using Pwiz.Data.MsData.Readers;
using Pwiz.Util.Misc;
using Pwiz.Vendor.Bruker;
using Pwiz.Vendor.Thermo;
#if MARS_SCIEX
using Pwiz.Vendor.Sciex;
#endif

namespace MARS.Pwiz;

/// <summary>
/// Plugs the vendor readers MARS was built with into pwiz's format dispatcher.
/// </summary>
/// <remarks>
/// <para>
/// <c>Pwiz.Data.MsData</c> deliberately does not reference the vendor projects - that would
/// drag every encrypted vendor SDK into everything that touches the core data model - so
/// <c>ReaderList.Default</c> knows only the open formats until a host adds the rest. Without
/// this, opening a <c>.raw</c> fails with "no registered reader recognized the file" even
/// though the reader is sitting in the same output directory.
/// </para>
/// <para>
/// Registered from a static constructor rather than from <c>Main</c>, because MARS reaches
/// pwiz from several places - reading a run, writing one - and a registration that depends on
/// the entry point having remembered to call it is one that will eventually be missed. Both
/// entry points inside this assembly call <see cref="EnsureRegistered"/>, and the runtime
/// guarantees the static constructor runs exactly once however many of them do.
/// </para>
/// <para>
/// A module initializer would be tidier still and was the first attempt, but CA2255 objects to
/// one in a library and is right to: it would run on assembly load, which is a side effect a
/// caller has no way to anticipate. A static constructor runs on first use instead.
/// </para>
/// </remarks>
internal static class VendorReaders
{
    /// <summary>
    /// The MS levels MARS asks a vendor reader to centroid: 1 and up, never 0.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>GetCentroidSpectrum</c> requires this set because pwiz moved the gate inside the
    /// reader, where cpp applies it against the reader's own FINAL MS level. Two things depend
    /// on that placement: Waters promotes MSe survey scans to level 2 before gating, and
    /// non-MS spectra report level 0. Centroiding one of those destroys it - MassLynx cannot
    /// centroid an absorbance-vs-wavelength trace and returns nothing.
    /// </para>
    /// <para>
    /// This is pwiz's <c>"1-"</c>, and deliberately NOT <c>IntegerSet.Positive</c>, which is
    /// the same value: <c>IntegerSet</c> is mutable and <c>Positive</c> is a
    /// <c>static readonly</c> instance shared with everything else in the process, so anything
    /// that inserted into it would silently change which levels MARS centroids. MARS owns this
    /// one.
    /// </para>
    /// </remarks>
    internal static readonly IntegerSet CentroidLevels = new(1, int.MaxValue);

    static VendorReaders() => Register();

    /// <summary>
    /// Ensures the vendor readers are registered. Cheap and idempotent: the work happens in
    /// the static constructor, which the runtime runs once on first touch of this type.
    /// </summary>
    internal static void EnsureRegistered()
    {
        // Referencing the type is what triggers the static constructor; there is nothing to do
        // in the body.
    }

    private static void Register()
    {
        // Appended once. ReaderList.Default rebuilds a list on every access and copies
        // AdditionalReaders into it, so registering twice would double every vendor reader.
        if (ReaderList.AdditionalReaders.Count > 0) return;

        // AdditionalReaders is a List<IReader>; ThermoReaderRegistration.AddTo wants a
        // ReaderList, and ReaderList.Default builds a fresh list each time it is read, so
        // adding to that would be adding to a copy that is thrown away.
        ReaderList.AdditionalReaders.Add(new Reader_Thermo());
        ReaderList.AdditionalReaders.Add(new Reader_Bruker());

        // Sciex only where its SDK runs. Everywhere else SpectrumSources still recognizes
        // .wiff and .wiff2 well enough to say why it cannot open them.
#if MARS_SCIEX
        ReaderList.AdditionalReaders.Add(new Reader_Sciex());
#endif
    }
}
