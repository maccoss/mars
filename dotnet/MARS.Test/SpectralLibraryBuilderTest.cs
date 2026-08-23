// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using MARS.Core;
using Xunit;

namespace MARS.Test;

/// <summary>
/// The builder's per-entry arrays have to stay aligned when an entry is dropped.
/// </summary>
/// <remarks>
/// <see cref="SpectralLibraryBuilder.EndEntry"/> removes an entry that collected no fragments,
/// which happens routinely - a PRISM row whose product m/z is missing contributes nothing, and
/// a precursor whose rows are all like that ends up empty. Every per-entry array has to shed
/// that entry together, or the arrays index different peptides from each other.
///
/// The one that matters most is the peptide group. Cross-validation splits folds by peptide
/// specifically so that one peptide's fragments cannot straddle the boundary and let
/// fragment_mz be memorised; a misaligned group array reintroduces exactly that leak, and it
/// does so silently - the reported out-of-fold accuracy simply comes out better than the truth.
/// </remarks>
public class SpectralLibraryBuilderTest
{
    [Fact]
    public void DroppingAnEmptyEntryKeepsEveryArrayAligned()
    {
        var builder = new SpectralLibraryBuilder(keepSequences: true);

        builder.BeginEntry("PEPTIDEA", 2, 500.0, 1.0, 2.0);
        builder.AddFragment(300.0, 100.0, 'y', 3, 1);
        builder.EndEntry();

        // Collects nothing, so EndEntry drops it.
        builder.BeginEntry("DROPPEDONE", 2, 600.0, 1.0, 2.0);
        builder.EndEntry();

        builder.BeginEntry("PEPTIDEC", 2, 700.0, 1.0, 2.0);
        builder.AddFragment(400.0, 100.0, 'y', 4, 1);
        builder.EndEntry();

        SpectralLibrary library = builder.Build();

        Assert.Equal(2, library.PrecursorMz.Length);
        Assert.Equal(library.PrecursorMz.Length, library.PeptideGroup.Length);

        // The surviving entries must still carry their own groups. With the dropped entry left
        // in the group array, entry 1 inherits the group id minted for DROPPEDONE.
        Assert.NotEqual(library.PeptideGroup[0], library.PeptideGroup[1]);
        Assert.Equal(700.0, library.PrecursorMz[1]);
    }

    /// <summary>
    /// Two entries of the same peptide share a group, which is what keeps them in one fold.
    /// A dropped entry in between must not break that.
    /// </summary>
    [Fact]
    public void TheSamePeptideKeepsOneGroupAcrossADrop()
    {
        var builder = new SpectralLibraryBuilder(keepSequences: true);

        builder.BeginEntry("SHARED", 2, 500.0, 1.0, 2.0);
        builder.AddFragment(300.0, 100.0, 'y', 3, 1);
        builder.EndEntry();

        builder.BeginEntry("EMPTY", 2, 600.0, 1.0, 2.0);
        builder.EndEntry();

        builder.BeginEntry("SHARED", 3, 350.0, 1.0, 2.0);
        builder.AddFragment(310.0, 100.0, 'y', 3, 1);
        builder.EndEntry();

        SpectralLibrary library = builder.Build();

        Assert.Equal(2, library.PeptideGroup.Length);
        Assert.Equal(library.PeptideGroup[0], library.PeptideGroup[1]);
    }
}
