// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MARS.Core;
using Pwiz.Data.Common.Cv;
using Pwiz.Data.MsData.Processing;
using Pwiz.Data.MsData.Spectra;

namespace MARS.Pwiz;

/// <summary>
/// A pwiz spectrum list that applies MARS's correction as spectra are pulled through it.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of the integration. MARS's science is untouched and reached through the
/// same <see cref="SpectrumCorrector"/> the byte-splice writer uses, so whichever format is
/// being written gets exactly the values mzML would have got - verified by writing both and
/// diffing them with <c>mars compare</c>, which found no difference across 82,349,582 peaks.
/// </para>
/// <para>
/// Derived from <c>SpectrumListBase</c> in <c>Pwiz.Data.MsData</c> rather than
/// <c>SpectrumListWrapper</c> in <c>Pwiz.Analysis</c>, at the cost of three delegating
/// members. Analysis references the Waters reader, which stages a native Windows-only
/// MassLynxRaw.dll into the output, and MARS ships four non-Windows artifacts. Nothing here
/// needs Analysis.
/// </para>
/// <para>
/// <b>Batched, because the model is where the time goes.</b> Writing one Astral run took 308 s,
/// of which the reader was 16.6 s and pwiz's encoder about 49 s - the remaining 243 s, 79% of
/// it, was scoring the model. pwiz's writers pull spectra one at a time, so left alone that
/// 243 s runs on one core. Instead of serving each pull straight through, this reads a batch
/// ahead and corrects the batch in parallel, then serves the batch one spectrum at a time as
/// the writer asks for them.
/// </para>
/// <para>
/// Reads stay sequential and single-threaded: they are 5% of the cost, and the vendor readers
/// are not thread-safe. Only the correction is parallel, and it is embarrassingly so - each
/// spectrum is independent, and <see cref="SpectrumCorrector"/> holds nothing mutable, taking
/// its scratch space as an argument. Results do not depend on scheduling, so the output is the
/// same however many threads run.
/// </para>
/// </remarks>
internal sealed class MarsSpectrumList : SpectrumListBase
{
    /// <summary>
    /// Spectra read ahead per thread. Enough that the parallel loop is not dominated by its
    /// own fan-out cost, small enough that the batch stays a few megabytes: an Astral MS2 is
    /// around 2,400 peaks, so two arrays of doubles is roughly 38 KB.
    /// </summary>
    private const int BatchPerThread = 4;

    private const int MaxBatch = 256;

    private readonly ISpectrumList _inner;
    private readonly SpectrumCorrector? _corrector;
    private readonly TemperatureSet? _temperatures;
    private readonly double? _acquisitionStart;
    private readonly int _threads;

    private readonly Worker _serial = new();
    private readonly Spectrum?[] _batch;
    private int _batchStart = -1;
    private int _batchCount;

    private long _correctorTicks;
    private long _readerTicks;

    public MarsSpectrumList(
        ISpectrumList inner, MzCalibrator? calibrator, CorrectionOptions options,
        double? acquisitionStart, TemperatureSet? temperatures, int threads)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _corrector = calibrator is null ? null : new SpectrumCorrector(calibrator, options);
        _acquisitionStart = acquisitionStart;
        _temperatures = temperatures;
        _threads = threads <= 0 ? Environment.ProcessorCount : threads;
        _batch = new Spectrum?[Math.Min(MaxBatch, Math.Max(1, _threads) * BatchPerThread)];
    }

    public long SpectraSeen { get; private set; }

    public long SpectraCorrected { get; private set; }

    public long MonotonicityFixes { get; private set; }

    public long SpectraReverted { get; private set; }

    /// <summary>Wall time spent inside the reader, pulling spectra from the file.</summary>
    public TimeSpan ReaderTime => TimeSpan.FromTicks(Interlocked.Read(ref _readerTicks));

    /// <summary>
    /// Time spent applying the model, summed across threads - so it is CPU time rather than
    /// wall time, and on a parallel run it exceeds the elapsed time of the write.
    /// </summary>
    public TimeSpan CorrectorTime => TimeSpan.FromTicks(Interlocked.Read(ref _correctorTicks));

    public override int Count => _inner.Count;

    public override SpectrumIdentity SpectrumIdentity(int index) => _inner.SpectrumIdentity(index);

    public override DataProcessing? DataProcessing => _inner.DataProcessing;

    public override Spectrum GetSpectrum(int index, bool getBinaryData = false)
    {
        // Metadata-only pulls skip the batch entirely: there is nothing to correct, and
        // priming a batch of full spectra to answer one would be pure waste.
        if (_corrector is null || !getBinaryData || _threads <= 1) return Sequential(index, getBinaryData);

        if (index >= _batchStart && index < _batchStart + _batchCount)
            return Take(index);

        // A pull that is not the start of the next batch means the caller is not walking the
        // list in order. Serve it directly rather than reading ahead from the wrong place.
        if (index != _batchStart + _batchCount || _batchStart < 0)
        {
            if (_batchStart >= 0 && index != _batchStart + _batchCount) Reset();
            if (_batchStart >= 0) return Sequential(index, getBinaryData);
        }

        FillBatch(index);
        return _batchCount > 0 && index >= _batchStart && index < _batchStart + _batchCount
            ? Take(index)
            : Sequential(index, getBinaryData);
    }

    protected override void DisposeCore() => _inner.Dispose();

    /// <summary>Hands over a spectrum the batch already corrected, and releases the slot.</summary>
    private Spectrum Take(int index)
    {
        int slot = index - _batchStart;
        Spectrum spectrum = _batch[slot]
                            ?? throw new InvalidOperationException($"Spectrum {index} was taken twice.");

        // Dropped as soon as it is handed over: the writer encodes it and moves on, and
        // holding the whole batch alive until the next refill would double the footprint.
        _batch[slot] = null;
        return spectrum;
    }

    private void Reset()
    {
        Array.Clear(_batch);
        _batchStart = -1;
        _batchCount = 0;
    }

    /// <summary>
    /// Reads the next batch sequentially, then corrects it in parallel.
    /// </summary>
    /// <remarks>
    /// Reading and correcting are separate phases rather than a pipeline. Overlapping them
    /// would save the read, which is 5% of the work, at the cost of a producer/consumer queue
    /// and the ordering it has to preserve. Two phases is a great deal easier to be sure of.
    /// </remarks>
    private void FillBatch(int start)
    {
        Array.Clear(_batch);
        _batchStart = start;
        _batchCount = Math.Min(_batch.Length, Count - start);
        if (_batchCount <= 0) return;

        long readStart = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < _batchCount; i++)
            _batch[i] = _inner.GetSpectrum(start + i, getBinaryData: true);
        AddElapsed(ref _readerTicks, readStart);

        long seen = 0, corrected = 0, fixes = 0, reverted = 0;

        Parallel.For(
            0,
            _batchCount,
            new ParallelOptions { MaxDegreeOfParallelism = _threads },
            () => new Worker(),
            (i, _, worker) =>
            {
                Spectrum? spectrum = _batch[i];
                if (spectrum is not null) Correct(spectrum, worker);
                return worker;
            },
            worker =>
            {
                // Totals are summed here rather than incremented per spectrum, so the counters
                // need no interlocking in the hot loop.
                Interlocked.Add(ref seen, worker.Seen);
                Interlocked.Add(ref corrected, worker.Corrected);
                Interlocked.Add(ref fixes, worker.MonotonicityFixes);
                Interlocked.Add(ref reverted, worker.Reverted);
                Interlocked.Add(ref _correctorTicks, worker.Ticks);
            });

        SpectraSeen += seen;
        SpectraCorrected += corrected;
        MonotonicityFixes += fixes;
        SpectraReverted += reverted;
    }

    /// <summary>The unbatched path: read one spectrum and correct it on this thread.</summary>
    private Spectrum Sequential(int index, bool getBinaryData)
    {
        long readStart = System.Diagnostics.Stopwatch.GetTimestamp();
        Spectrum spectrum = _inner.GetSpectrum(index, getBinaryData);
        AddElapsed(ref _readerTicks, readStart);

        if (_corrector is null || !getBinaryData) return spectrum;

        Correct(spectrum, _serial);
        SpectraSeen = _serial.Seen;
        SpectraCorrected = _serial.Corrected;
        MonotonicityFixes = _serial.MonotonicityFixes;
        SpectraReverted = _serial.Reverted;
        Interlocked.Exchange(ref _correctorTicks, _serial.Ticks);
        return spectrum;
    }

    /// <summary>
    /// Applies the model to one spectrum, in place, using this worker's scratch space.
    /// </summary>
    private void Correct(Spectrum spectrum, Worker worker)
    {
        if (spectrum.Params.CvParamValueOrDefault(CVID.MS_ms_level, 0) != 2) return;

        BinaryDataArray? mz = spectrum.GetMZArray();
        BinaryDataArray? intensity = spectrum.GetIntensityArray();
        if (mz is null || intensity is null || mz.Data.Count == 0) return;

        worker.Seen++;
        Fill(worker.Record, spectrum, mz, intensity);

        int peaks = mz.Data.Count;
        if (worker.Corrections.Length < peaks) worker.Corrections = new double[peaks];
        Span<double> corrected = worker.Corrections.AsSpan(0, peaks);

        // Corrected into scratch rather than in place: the corrector reverts a whole spectrum
        // when the correction would reorder its peaks, and it cannot revert what it has
        // already overwritten.
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        SpectrumCorrectionResult result =
            _corrector!.Correct(worker.Record, _temperatures, worker.Workspace, corrected);
        worker.Ticks += Elapsed(start);

        worker.MonotonicityFixes += result.MonotonicityFixes;
        if (result.Reverted) worker.Reverted++;
        if (!result.Corrected) return;

        worker.Corrected++;
        for (int i = 0; i < peaks; i++) mz.Data[i] = corrected[i];
    }

    /// <summary>Copies what MARS's features are computed from out of a pwiz spectrum.</summary>
    private void Fill(SpectrumRecord record, Spectrum spectrum, BinaryDataArray mz, BinaryDataArray intensity)
    {
        Scan? scan = spectrum.ScanList.Scans.Count > 0 ? spectrum.ScanList.Scans[0] : null;

        record.Id = spectrum.Id;
        record.Index = spectrum.Index;
        record.ScanNumber = ScanNumberOf(spectrum.Id, spectrum.Index);
        record.MsLevel = 2;
        record.InstrumentConfigurationRef = null;
        record.FilterString = scan?.CvParam(CVID.MS_filter_string).Value;

        // pwiz normalizes scan start time to minutes, which is the unit MARS stores.
        record.RetentionTime = scan?.CvParamValueOrDefault(CVID.MS_scan_start_time, 0.0) ?? 0.0;

        // MARS holds injection time in seconds; the cvParam is in milliseconds.
        double injectionMs = scan?.CvParamValueOrDefault(CVID.MS_ion_injection_time, 0.0) ?? 0.0;
        record.InjectionTime = injectionMs > 0 ? injectionMs / 1000.0 : null;

        record.PrecursorMzCenter = 0;
        record.PrecursorMzLow = 0;
        record.PrecursorMzHigh = 0;
        if (spectrum.Precursors.Count > 0)
        {
            IsolationWindow window = spectrum.Precursors[0].IsolationWindow;
            double target = window.CvParamValueOrDefault(CVID.MS_isolation_window_target_m_z, 0.0);
            double lower = window.CvParamValueOrDefault(CVID.MS_isolation_window_lower_offset, 0.0);
            double upper = window.CvParamValueOrDefault(CVID.MS_isolation_window_upper_offset, 0.0);
            record.PrecursorMzCenter = target;
            record.PrecursorMzLow = target - lower;
            record.PrecursorMzHigh = target + upper;
        }

        record.ReportedTic = spectrum.Params.CvParamValueOrDefault(CVID.MS_total_ion_current, 0.0);

        int peaks = mz.Data.Count;
        if (record.MzArray.Length < peaks) record.MzArray = new double[peaks];
        if (record.IntensityArray.Length < peaks) record.IntensityArray = new double[peaks];

        // Summed here rather than taken from the TIC cvParam, because that is what the Python
        // matcher did and the log_tic and tic_injection_time features are defined on it.
        double summed = 0;
        for (int i = 0; i < peaks; i++)
        {
            record.MzArray[i] = mz.Data[i];
            double value = intensity.Data[i];
            record.IntensityArray[i] = value;
            summed += value;
        }

        record.PeakCount = peaks;
        record.SummedIntensity = summed;
        record.AcquisitionStartTime = _acquisitionStart;
        record.AbsoluteTime = (_acquisitionStart ?? 0) + (record.RetentionTime * 60.0);
    }

    private static long Elapsed(long since) =>
        (long)((System.Diagnostics.Stopwatch.GetTimestamp() - since)
               * (10_000_000.0 / System.Diagnostics.Stopwatch.Frequency));

    private static void AddElapsed(ref long target, long since) =>
        Interlocked.Add(ref target, Elapsed(since));

    /// <summary>
    /// Pulls the scan number out of a nativeID, falling back to the index. MARS uses this only
    /// for reporting, but a wrong number in a warning sends someone to the wrong spectrum.
    /// </summary>
    private static int ScanNumberOf(string id, int fallback)
    {
        const string marker = "scan=";
        int at = id.LastIndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return fallback;

        int start = at + marker.Length;
        int end = start;
        while (end < id.Length && char.IsDigit(id[end])) end++;

        return end > start &&
               int.TryParse(id.AsSpan(start, end - start), NumberStyles.Integer,
                   CultureInfo.InvariantCulture, out int scan)
            ? scan
            : fallback;
    }

    /// <summary>
    /// One thread's scratch space and running totals.
    /// </summary>
    /// <remarks>
    /// <see cref="SpectrumCorrector"/> is safe to share - it holds only the model and the
    /// options - provided every caller brings its own record, workspace and output buffer.
    /// This is what makes the correction parallel without any locking in the hot loop.
    /// </remarks>
    private sealed class Worker
    {
        public readonly SpectrumRecord Record = new();

        public readonly CorrectionWorkspace Workspace = new();

        public double[] Corrections = Array.Empty<double>();

        public long Seen;

        public long Corrected;

        public long MonotonicityFixes;

        public long Reverted;

        public long Ticks;
    }
}
