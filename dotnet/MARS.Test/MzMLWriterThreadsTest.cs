// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.IO;
using System.Threading;
using MARS.Core;
using MARS.IO;
using Xunit;

namespace MARS.Test;

/// <summary>
/// <c>--threads</c> has to bound the mzML write path, not just be accepted by it.
/// </summary>
/// <remarks>
/// The correction is per-spectrum with no cross-row state, so the thread count cannot change
/// the output - which is exactly why an unbounded write is invisible. It shows up only as CPU
/// use, and the person who asked for one core is usually sharing the machine with someone.
/// </remarks>
public sealed class MzMLWriterThreadsTest : IDisposable
{
    private readonly string _directory;

    public MzMLWriterThreadsTest()
    {
        _directory = Path.Combine(Path.GetTempPath(), "mars-test-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A locked temp file must not fail the suite.
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void NoMoreSpectraAreInFlightThanThreadsAllows(int threads)
    {
        string input = Path.Combine(_directory, "input.mzML");
        SyntheticMzML.Write(input, spectrumCount: 200, chromatogramCount: 0);

        var counter = new ConcurrencyCounter();
        MzMLWriteResult result = MzMLWriter.Write(
            MzMLFile.Inspect(input),
            Path.Combine(_directory, $"output-{threads}.mzML"),
            () => new CountingTransform(counter),
            new MzMLWriteOptions { MaxDegreeOfParallelism = threads });

        Assert.Equal(200, result.SpectraSeen);
        Assert.True(
            counter.Peak <= threads,
            $"asked for {threads} thread(s), but {counter.Peak} spectra were in flight at once");
    }

    private sealed class ConcurrencyCounter
    {
        private int _current;
        private int _peak;

        public int Peak => Volatile.Read(ref _peak);

        public void Enter()
        {
            int now = Interlocked.Increment(ref _current);

            // Raise the recorded peak to `now` unless something already recorded higher.
            int peak = Volatile.Read(ref _peak);
            while (now > peak)
            {
                int seen = Interlocked.CompareExchange(ref _peak, now, peak);
                if (seen == peak) break;
                peak = seen;
            }
        }

        public void Exit() => Interlocked.Decrement(ref _current);
    }

    /// <summary>
    /// Holds the worker briefly so overlapping work actually overlaps. Without the pause an
    /// unbounded writer could still finish each spectrum before dispatching the next and look
    /// bounded by luck.
    /// </summary>
    private sealed class CountingTransform : IMzTransform
    {
        private readonly ConcurrencyCounter _counter;

        public CountingTransform(ConcurrencyCounter counter) => _counter = counter;

        public MzTransformResult Transform(SpectrumRecord spectrum, Span<double> corrected)
        {
            _counter.Enter();
            try
            {
                Thread.Sleep(2);
                spectrum.Mz.CopyTo(corrected);
                return new MzTransformResult { Rewrite = true };
            }
            finally
            {
                _counter.Exit();
            }
        }
    }
}
