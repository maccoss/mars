// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.IO;
using System.Linq;
using MARS.Core;
using MARS.IO;
using MARS.Pwiz;
using Xunit;

namespace MARS.Test;

public class PwizOutputTest
{
    [Theory]
    [InlineData("mzML", MarsOutputFormat.MzML)]
    [InlineData("mzml", MarsOutputFormat.MzML)]
    [InlineData("MZML", MarsOutputFormat.MzML)]
    [InlineData("  mzXML  ", MarsOutputFormat.MzXml)]
    [InlineData("mzmlb", MarsOutputFormat.MzMLb)]
    [InlineData("mgf", MarsOutputFormat.Mgf)]
    public void FormatNamesParse(string name, MarsOutputFormat expected)
    {
        Assert.True(PwizOutput.TryParse(name, out MarsOutputFormat format));
        Assert.Equal(expected, format);
    }

    /// <summary>An absent --output-format means mzML, the format MARS has always written.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoFormatMeansMzML(string? name)
    {
        Assert.True(PwizOutput.TryParse(name, out MarsOutputFormat format));
        Assert.Equal(MarsOutputFormat.MzML, format);
    }

    [Theory]
    [InlineData("mz5")]
    [InlineData("mzdata")]
    [InlineData("parquet")]
    public void AnUnknownFormatIsRejected(string name) =>
        Assert.False(PwizOutput.TryParse(name, out _));

    [Fact]
    public void EveryFormatHasAnExtensionAndAName()
    {
        foreach (MarsOutputFormat format in Enum.GetValues<MarsOutputFormat>())
        {
            Assert.StartsWith(".", PwizOutput.Extension(format), StringComparison.Ordinal);
            Assert.True(PwizOutput.TryParse(PwizOutput.Name(format), out MarsOutputFormat parsed));
            Assert.Equal(format, parsed);
        }
    }

    /// <summary>
    /// Splicing means copying the input and replacing the ranges that changed, so it needs an
    /// mzML to copy. An mzML input can be spliced; a vendor file has nothing to splice into
    /// and its mzML has to be built like any other format.
    /// </summary>
    [Theory]
    [InlineData("run.mzML", true)]
    [InlineData("RUN.MZML", true)]
    [InlineData("run.raw", false)]
    [InlineData("run.wiff2", false)]
    [InlineData("run.d", false)]
    public void OnlyAnMzMLInputCanBeSpliced(string path, bool expected) =>
        Assert.Equal(expected, SpectrumSources.CanSplice(path));

    /// <summary>Every format MARS knows the name of is recognized, so Open can explain itself.</summary>
    [Theory]
    [InlineData("run.mzML")]
    [InlineData("run.raw")]
    [InlineData("run.wiff2")]
    [InlineData("run.lcd")]
    public void TheKnownFormatsAreRecognized(string path) =>
        Assert.True(SpectrumSources.IsRecognized(path));

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("run.mzXML")]
    public void AnUnknownFormatIsNotRecognized(string path) =>
        Assert.False(SpectrumSources.IsRecognized(path));

    /// <summary>
    /// Recognizing a format is not the same as being able to open it. Shimadzu is in the table
    /// so that a .lcd earns an explanation, but no build carries the reader, so a directory
    /// scan must not pick one up and a capability list must not advertise it.
    /// </summary>
    [Fact]
    public void ARecognizedFormatWithNoReaderIsNotReadable()
    {
        Assert.True(SpectrumSources.IsRecognized("run.lcd"));
        Assert.False(SpectrumSources.IsReadable("run.lcd"));
        Assert.DoesNotContain(".lcd", SpectrumSources.ReadableExtensions());
    }

    /// <summary>mzML is readable in every build; the vendor formats only where they are linked.</summary>
    [Fact]
    public void TheCapabilityListMatchesWhatIsLinked()
    {
        Assert.Contains(".mzML", SpectrumSources.ReadableExtensions());
        Assert.Equal(PwizOutput.Available, SpectrumSources.ReadableExtensions().Contains(".raw"));
        Assert.Equal(PwizOutput.Available, SpectrumSources.IsReadable("run.raw"));
    }

    /// <summary>
    /// A vendor format asked of a build that cannot open it must say so, rather than failing
    /// somewhere inside a reader with a load error.
    /// </summary>
    [Fact]
    public void AVendorFormatThisBuildCannotReadIsRefusedClearly()
    {
        string path = Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid().ToString("N") + ".wiff2");

        // Sciex is never referenced, so this is refused whether or not pwiz is present.
        var ex = Assert.Throws<NotSupportedException>(() => SpectrumSources.Open(path));
        Assert.Contains("Sciex", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// mzML is always available; the rest depend on whether this build found pwiz-sharp. A
    /// build without it must still write mzML, which is what MARS has always done.
    /// </summary>
    [Fact]
    public void MzMLIsSupportedWithOrWithoutPwiz()
    {
        Assert.Contains(MarsOutputFormat.MzML, PwizOutput.Supported);
        if (!PwizOutput.Available) Assert.Single(PwizOutput.Supported);
    }

    /// <summary>
    /// mzMLb is HDF5, and the native library that writes it is published for x64 only. It has
    /// to be absent from the list on arm64 rather than fail when someone asks for it.
    /// </summary>
    [Fact]
    public void MzMLbIsOfferedOnlyWhereItCanBeWritten()
    {
        bool x64 = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                   == System.Runtime.InteropServices.Architecture.X64;

        Assert.Equal(
            PwizOutput.Available && x64,
            PwizOutput.Supported.Contains(MarsOutputFormat.MzMLb));
    }

    /// <summary>The formats that drop information say so, so a user is warned rather than surprised.</summary>
    [Fact]
    public void TheLossyFormatsCarryAWarning()
    {
        Assert.Null(PwizOutput.LossWarning(MarsOutputFormat.MzML));
        Assert.Null(PwizOutput.LossWarning(MarsOutputFormat.MzMLb));
        Assert.NotNull(PwizOutput.LossWarning(MarsOutputFormat.MzXml));
        Assert.NotNull(PwizOutput.LossWarning(MarsOutputFormat.Mgf));
    }

    // ---- encoding sniffing -------------------------------------------------------------
    //
    // This is the part that has to be right whether or not pwiz is present: pwiz's encoder
    // defaults to 64-bit UNCOMPRESSED, and taking that default made a Stellar run 61% larger
    // than its input. MARS matches what the input used instead.

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, true)]     // 64-bit m/z, 32-bit intensity: the common case
    [InlineData(true, false, true, false)]    // uncompressed both
    [InlineData(false, true, false, true)]
    public void TheEncodingIsReadBackPerArray(
        bool mzBits64, bool mzZlib, bool intensityBits64, bool intensityZlib)
    {
        string directory = NewDirectory();
        try
        {
            string path = Path.Combine(directory, "input.mzML");
            SyntheticMzML.Write(
                path, spectrumCount: 8, chromatogramCount: 0,
                mzEncoding: new BinaryArrayEncoding(mzBits64, mzZlib),
                intensityEncoding: new BinaryArrayEncoding(intensityBits64, intensityZlib),
                peaksPerSpectrum: 12);

            SpectrumEncoding encoding = MzMLEncoding.Sniff(path);

            Assert.Equal(mzBits64, encoding.Mz.Bits64);
            Assert.Equal(mzZlib, encoding.Mz.Zlib);
            Assert.Equal(intensityBits64, encoding.Intensity.Bits64);
            Assert.Equal(intensityZlib, encoding.Intensity.Zlib);
        }
        finally
        {
            Delete(directory);
        }
    }

    /// <summary>
    /// A file this cannot read is not a reason to fail a conversion - fall back to what
    /// msconvert writes, which is what the input most likely was.
    /// </summary>
    [Fact]
    public void AnUnreadableFileFallsBackToTheUsualEncoding()
    {
        SpectrumEncoding encoding = MzMLEncoding.Sniff(
            Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".mzML"));

        Assert.True(encoding.Mz.Bits64);
        Assert.True(encoding.Mz.Zlib);
    }

    private static string NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mars-pwiz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Delete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
    }
}
