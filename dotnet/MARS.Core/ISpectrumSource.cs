// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Collections.Generic;

namespace MARS.Core;

/// <summary>
/// A run MARS can read spectra from, whatever file format it arrives in.
/// </summary>
/// <remarks>
/// <para>
/// MARS was built around mzML, and everything above this interface still is: the matcher, the
/// feature extraction and the model all consume <see cref="SpectrumRecord"/> and neither know
/// nor care where it came from. This exists so a Thermo <c>.raw</c> can be read directly,
/// without a conversion step whose only purpose was to produce something MARS could open.
/// </para>
/// <para>
/// Implementations are in the assemblies that own the format - mzML in <c>MARS.IO</c>, vendor
/// formats in <c>MARS.Pwiz</c> - so that <c>MARS.Core</c> depends on neither.
/// </para>
/// </remarks>
public interface ISpectrumSource : IDisposable
{
    /// <summary>Path of the file or directory backing this source.</summary>
    string Path { get; }

    /// <summary>Size in bytes, for reporting. Zero when it cannot be determined cheaply.</summary>
    long Length { get; }

    /// <summary>
    /// Run start as a Unix timestamp in seconds, or null when the file does not record one.
    /// The absolute_time feature is undefined without it.
    /// </summary>
    double? AcquisitionStartTime { get; }

    /// <summary>
    /// The analyzer that recorded this run's MS2 spectra, which decides the default fragment
    /// tolerance and the units the QC report is drawn in.
    /// </summary>
    /// <remarks>
    /// MS2 specifically. On a hybrid instrument that is not the analyzer the run names as its
    /// default: an Orbitrap Astral file declares the orbitrap, which takes the MS1 survey,
    /// and points only its MS2 spectra at the Astral analyzer.
    /// </remarks>
    MassAnalyzerClass Analyzer { get; }

    /// <summary>
    /// Streams spectra at the given MS level, or every spectrum when null.
    /// </summary>
    /// <remarks>
    /// The arrays on the yielded record may be reused between iterations, so a consumer that
    /// keeps them must copy. This is what lets MARS hold a 4.9 GB run in a bounded working
    /// set.
    /// </remarks>
    IEnumerable<SpectrumRecord> ReadSpectra(int? msLevel = 2);
}

/// <summary>What a run's ion injection times look like, and so whether they can be a feature.</summary>
public enum InjectionTimeUse
{
    /// <summary>The run does not record one.</summary>
    Absent,

    /// <summary>Recorded, but the same on every spectrum, so it carries no information.</summary>
    Constant,

    /// <summary>Recorded and varying, as a trap's gain control makes it.</summary>
    Varying,
}
