// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System.Runtime.CompilerServices;
using Pwiz.Data.MsData.Readers;
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
/// A module initializer rather than a call from <c>Main</c>, because MARS reaches pwiz from
/// several places - reading a run, writing one - and a registration that depends on the entry
/// point having remembered to call it is a registration that will eventually be missed.
/// </para>
/// </remarks>
internal static class VendorReaders
{
    [ModuleInitializer]
    internal static void Register()
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
