// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System.Collections.Generic;
using MARS.Core;

namespace MARS.IO;

/// <summary>
/// Reads spectra from an mzML, which is what MARS has always done.
/// </summary>
/// <remarks>
/// A thin adapter over <see cref="MzMLFile"/>. It also carries the
/// <see cref="MzMLFileInfo"/>, because writing mzML needs it: MARS produces mzML by splicing
/// corrected bytes into a copy of the input, and that needs the byte offsets this inspection
/// found.
/// </remarks>
public sealed class MzMLSpectrumSource : ISpectrumSource
{
    public MzMLSpectrumSource(MzMLFileInfo info)
    {
        Info = info;
        Analyzer = MzMLFile.DetectMs2Analyzer(info);
    }

    public MzMLSpectrumSource(string path)
        : this(MzMLFile.Inspect(path))
    {
    }

    /// <summary>The inspection result, for the byte-splice writer.</summary>
    public MzMLFileInfo Info { get; }

    public string Path => Info.Path;

    public long Length => Info.Length;

    public double? AcquisitionStartTime => Info.AcquisitionStartTime;

    public MassAnalyzerClass Analyzer { get; }

    public IEnumerable<SpectrumRecord> ReadSpectra(int? msLevel = 2) =>
        MzMLFile.ReadSpectra(Info, msLevel);

    public void Dispose()
    {
        // Nothing held open: MzMLFile opens and closes a stream per read.
    }
}
