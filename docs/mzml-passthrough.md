# How MARS writes mzML

MARS modifies one thing in a file: the m/z array of the MS2 spectra it corrects. Everything
else has to survive untouched, and that turns out to be harder than it sounds.

- [Why passthrough](#why-passthrough)
- [The contract](#the-contract)
- [How it works](#how-it-works)
- [Binary arrays](#binary-arrays)
- [Index and checksum](#index-and-checksum)
- [Verifying output](#verifying-output)
- [Memory and speed](#memory-and-speed)

## Why passthrough

The obvious way to modify an mzML is to parse it into a document tree, change what you want,
and serialize it back. The Python implementation of MARS tried that twice - once with
[psims](https://github.com/mobiusklein/psims) and once with an lxml round trip - and both
produced files that were **valid mzML and still broke DIA-NN and SeeMS**.

The reason is that a serializer is entitled to make changes a schema validator will not
object to: reordering attributes, normalizing whitespace, moving or re-declaring namespaces,
rewriting numeric formats. Downstream tools that parse mzML with hand-rolled scanners rather
than a full XML stack notice.

So MARS does not serialize. It copies the input byte for byte and splices replacement bytes
into the specific ranges it is changing. Everything outside those ranges is identical to the
input by construction, not by care.

## The contract

Non-negotiable, all of it load-bearing for downstream compatibility:

1. **Write indexed mzML.** DIA-NN fails silently on unindexed files.
2. **Preserve `cvRef="MS"`.** Never emit `cvRef="PSI-MS"`.
3. **Preserve the Thermo nativeID format** (`controllerType=0 controllerNumber=1 scan=NNNN`,
   `MS:1000768`) and every source file reference.
4. **Re-encode a modified array with the same compression and precision it was decoded
   with.** Read encoding per ARRAY, never per spectrum: m/z is typically 64-bit while
   intensity is often 32-bit, and compression can differ between two arrays in one spectrum.
5. **Update `encodedLength`** on every modified array, to the base64 **character** count.
6. **Regenerate `indexList`, `indexListOffset` and the SHA-1 `fileChecksum`** after any
   modification.
7. **Do not add or remove spectra**, and do not recompute derived CV terms (base peak m/z,
   TIC) unless the correction actually invalidates them.

## How it works

The file is walked as a sequence of regions:

```
[gap: header, run metadata, spectrumList open tag]   -> copied verbatim
[spectrum]                                            -> parsed; m/z spliced if corrected
[gap: whitespace]                                     -> copied verbatim
[spectrum]                                            -> ...
...
[gap: chromatogramList, closing tags]                 -> copied verbatim
[chromatogram]                                        -> copied verbatim, offset recorded
[trailer: index, indexListOffset, fileChecksum]       -> regenerated
```

Spectrum and chromatogram elements are located by scanning for their start tags. Within a
spectrum, metadata is parsed with `XmlReader` over just that span - a real XML parser, so
attribute quoting and entity escaping are handled correctly - while the byte ranges to
splice are found by scanning. That split matters: `XmlReader` reports line and character
positions, not byte offsets, and the writer needs bytes.

Metadata is read by **CV accession**, never by name. Names are display strings that vary
between writers, and pwiz emits a `<userParam name="ms level" value="1"/>` inside every
isolation window that a name-matching reader would happily mistake for the real
`MS:1000511`.

A corrected spectrum is rebuilt as:

```
[bytes before encodedLength value] [new length] [bytes up to <binary> text] [new base64] [rest]
```

Two ranges change. Everything else in the element - every cvParam, the scan list, the
precursor list, the intensity array, the indentation - is the input's own bytes.

## Binary arrays

Decoding is base64 then, if declared, zlib. mzML uses the **zlib container**, with its
2-byte header and Adler-32 trailer, so `ZLibStream` rather than `DeflateStream`. Binary
arrays are little-endian by specification.

Two details that cost real debugging time:

**Inflate must not write into its own source buffer.** Reading compressed bytes from a
stream that wraps the destination array corrupts data that has not been consumed yet. It
is invisible on small spectra, because the whole payload fits in one internal buffered read,
and only appears once an array exceeds about 8 KB compressed. There is a regression test
that decodes a 20,000-peak spectrum specifically to keep that path honest.

**Compressed bytes are not portable.** The zlib each runtime ships is not the same, so the
same input compresses to different bytes on Windows and Linux even though the values are
identical. Equivalence is therefore defined on decoded values throughout.

## Index and checksum

Both are regenerated from the bytes actually written, rather than adjusted from the input.
Offsets are recorded as each element is emitted, so they are correct by construction.

The `fileChecksum` is SHA-1 over every byte from the start of the file **up to and including
the `<fileChecksum>` opening tag**. This was established empirically rather than from the
specification text: a pwiz-written file reproduces its recorded digest only under that
convention.

> The Python implementation of MARS stops the hash two bytes earlier, before the indentation
> preceding `<fileChecksum>`. Every mzML it has written therefore carries a checksum that
> fails validation. Most consumers never check, which is why it went unnoticed. The C#
> writer uses the inclusive convention, and `mars verify` checks it.

A plain `<mzML>` file with no `<indexedmzML>` wrapper has nowhere to put an index, so MARS
copies it through unindexed and warns. Convert with msconvert first if DIA-NN is the
destination.

## Verifying output

The passthrough is testable independently of any science, which is the point:

```bash
mars verify run.mzML
```

This applies a **null correction** - decode and re-encode every m/z array without changing a
value - and then checks that the result:

- decodes to bit-identical m/z and intensity arrays,
- has an index whose every offset lands on the element it names,
- has a SHA-1 checksum that validates.

If something looks wrong with a corrected file, run this first. It separates "the file
format handling is broken" from "the model is doing something strange", and those have very
different fixes.

On the reference 1.2 GB Stellar file: 114,635 spectra, 56,972,925 peaks, m/z and intensity
bit-identical, index and checksum valid.

To compare two files that were produced independently, use decoded values rather than `cmp`:

```bash
mars compare a.mzML b.mzML --validate
```

## Memory and speed

Never load the file. Memory is bounded by the largest single spectrum plus the training
matrix, so a 4.9 GB Astral run streams in the same working set as a 1.2 GB Stellar one.

Pass 2 runs the per-spectrum decode, predict and re-encode across workers while writing in
order, so output byte order is unaffected by thread count. Inference carries no cross-row
accumulation, so parallelizing it cannot change a value.

Measured on 16 cores:

| | |
|---|---|
| Null-correction round trip, 1.2 GB | 6.9 s (176 MB/s) |
| Correct and write, per 1.2 GB file | 24 to 38 s |
| Full `calibrate` over 5 files, 6.0 GB in | 229 s |
