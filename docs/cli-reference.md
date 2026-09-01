# Command-line reference

Five commands. `calibrate` is the one that does the work; the other four exist to tell you
whether you should trust it.

| Command | What it does | Writes mzML |
|---|---|---|
| [`qc`](#mars-qc) | Reports the mass accuracy already in the files | no |
| [`calibrate`](#mars-calibrate) | Learns a correction and applies it | yes |
| [`apply`](#mars-apply) | Reuses a trained model on more files | yes |
| [`verify`](#mars-verify) | Round-trips a file and checks it survived | yes, then deletes it |
| [`compare`](#mars-compare) | Diffs two mzML files on decoded values | no |

A sensible first session:

```bash
mars qc --mzml-dir runs/ --prism-report report.csv     # is there anything to correct?
mars verify runs/one.mzML                              # can MARS handle this file at all?
mars calibrate --mzml-dir runs/ --prism-report report.csv --output-dir corrected/
```

## Exit codes

Worth wiring into a pipeline, because they distinguish "your data was not suitable" from
"MARS is broken".

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Input error: files not found, a required option missing, an unreadable library |
| 2 | Not enough training data. Fewer matches than `--min-training-rows`; no model was fitted and nothing was written |
| 3 | Output validation failed. A file was written but did not pass its own index/checksum check - treat the output as suspect and report it |

Exit 2 is a deliberate refusal rather than a crash. MARS would rather write nothing than
fit a model on a few hundred noisy rows and hand back a file that looks corrected.

---

## Resolution and tolerance

MARS reads the mass analyzer out of the mzML and picks a fragment tolerance to match, so
neither `qc` nor `calibrate` normally needs to be told what instrument produced the data.

| `--resolution` | Meaning |
|---|---|
| `auto` (default) | Read the analyzer from the file's `instrumentConfiguration` and pick |
| `unit` | Ion trap or quadrupole: 0.3 Th, QC report in Th |
| `hram` | Orbitrap, FT-ICR, TOF or Astral: 10 ppm, QC report in ppm |

The mode sets **defaults only**. An explicit `--tolerance` or `--tolerance-ppm` always wins;
detection can be wrong on a file MARS has not seen the shape of, and the person running it
can be certain in a way a heuristic cannot.

Detection reads the analyzer for the **MS2** spectra specifically, which on a hybrid
instrument is not the one the run names as its default. An Orbitrap Astral file declares two
configurations - the orbitrap that takes the MS1 survey, which is the run default, and the
Astral analyzer that only the MS2 spectra point at. MS2 is what MARS calibrates, so that is
the one that decides.

The choice appears in the log:

```
INFO   high-resolution data; fragment tolerance 10 ppm (--tolerance or --tolerance-ppm to override)
INFO   unit-resolution data; fragment tolerance 0.3 Th (--tolerance or --tolerance-ppm to override)
```

When the file does not say, MARS warns and falls back to 0.3 Th rather than guessing
silently:

```
WARNING: could not tell the mass analyzer from the file; assuming unit resolution and a
0.300 Th tolerance. Pass --resolution hram or --tolerance-ppm if this is Orbitrap, TOF or
Astral data.
```

One tolerance is chosen for the whole cohort, from the first file. If another file in the run
was recorded on a different kind of analyzer, MARS says so - a directory holding both trap and
high-resolution data gets one of them matched at the wrong width otherwise:

```
WARNING: astral.mzML was recorded on a high-resolution analyzer, but the fragment tolerance is
being set from a unit-resolution one. One tolerance is used for the whole cohort; calibrate the
instruments separately, or set --resolution to choose deliberately.
```

It is a warning rather than a refusal, because a mixed cohort can be deliberate and
`--resolution` is there to settle it. A file that does not name its analyzer is not treated as
a disagreement: it says nothing, and it already falls back to the default.

Getting this wrong is quiet rather than loud, which is why it is detected. See
[choosing a tolerance](spectral-libraries.md#choosing-a-tolerance) for what a mismatched
window actually does to the numbers.

---

## Input formats

MARS reads mzML itself. With a pwiz-sharp build it also reads Thermo `.raw` directly, so a
run can be calibrated straight off the instrument with no conversion step:

```bash
mars calibrate --mzml run.raw --library report-lib.parquet --diann-report report.parquet     --output-dir corrected/ --output-format mzMLb
```

`--mzml`, `--mzml-dir` and bare file arguments all accept any readable format; the name is
historical. A directory picks up every file MARS can read, not only `.mzML`.

Reading a `.raw` gives the same answer as reading the mzML msconvert would have made from it.
On an Astral run matched against the same DIA-NN library, both paths return 230,781 fragment
matches with the same median, standard deviation and MAD to every reported digit.

It is not faster to *read* - that run takes 53 s from `.raw` against 15 s from the converted
mzML, and vendor reading does not thread - so the saving is the conversion that no longer has
to happen and the intermediate file it no longer leaves behind.

The mass analyzer is detected from the vendor file exactly as it is from an mzML, so an Astral
`.raw` picks a 10 ppm tolerance and a ppm-scaled QC report on its own.

### Platforms

All five release targets build and run with the vendor reader, because the Thermo SDK MARS uses
is managed and cross-platform.

| Target | Reads `.raw` | mzML, mzXML | mzMLb |
|---|---|---|---|
| `win-x64`, `linux-x64` | yes | yes | yes |
| `win-arm64`, `linux-arm64`, `osx-arm64` | yes | yes | **no** |

mzMLb is the one gap: it is HDF5, and `HDF.PInvoke.1.10` bundles a native libhdf5 for x64 only,
so an arm64 build has nothing to write it with. Everything else - reading vendor files
included - works on all five.

A build also stages two files it never uses on non-Windows targets: `MassLynxRaw.dll`, which is
a Windows native library, and a Waters `license.key`. Both arrive through the same transitive
Thermo-to-Analysis-to-Waters reference that costs the IL3000 suppression, and both are inert.

### Which vendors

| Format | Vendor | Status |
|---|---|---|
| `.mzML` | - | Always, read by MARS itself |
| `.raw` | Thermo | Windows, Linux, macOS |
| `.d`, `.tdf`, `.tsf`, `.baf` | Bruker | Windows and Linux |
| `.wiff`, `.wiff2` | Sciex | **Windows only** |
| `.lcd` | Shimadzu | Not referenced yet |
| `.d` | Agilent | Not referenced yet |

Sciex is Windows-only because its SDK is: a SmartAssembly bundle loaded through a side-by-side
`AssemblyLoadContext`, needing a native SQLite interop, which pwiz-sharp gates on Windows for
that reason. Thermo's SDK is managed and runs anywhere. Bruker ships separate Windows and
Linux archives.

Bruker and Agilent runs are **directories** rather than files. `--mzml`, `--mzml-dir` and bare
arguments all accept them, and pwiz decides which vendor a `.d` belongs to by what is inside
it.

Verified against ProteoWizard's own vendor test files: a Bruker `diaPASEF.d`, a Sciex ZenoTOF
7600 `.wiff2`, a Sciex SWATH `.wiff2`, and a legacy `.wiff`. All were detected as
high-resolution except the legacy `.wiff`, which is unit-resolution, and all reported isolation
windows on every MS2.

**Injection time has to vary to be a feature.** A trap sets it per spectrum from its automatic
gain control, so it says how full the trap was. An instrument that accumulates for a fixed
period gives the same number every time, and then `injection_time` is a constant - which a tree
can never split on - while `tic_injection_time` is TIC times that constant, which is `log_tic`
rescaled. Two features carrying nothing, one of them a duplicate that splits permutation
importance with the feature it duplicates.

**That is decided from the whole run, not a sample of it.** A trap sits at the method's ceiling
until the trap actually fills, so the start of a gradient is flat no matter what follows -
every Stellar run tested is constant over its first several thousand MS2 and varies later, one
of them across two thirds of its spectra. MARS therefore collects the column during matching
and decides afterwards, when it has all of it. Nothing extra is read to do this.

**The ion-population features are a different question.** `fragment_ions`, the six
`ions_above_`/`ions_below_` windows and the six `adjacent_ratio_` features are peak sums over
m/z windows, multiplied by the injection time to turn a rate into a count. They need an
injection time to exist, but not to vary: a constant multiplies every one of them by the same
factor, which leaves them all varying and every split still available. So they are kept
whenever the run records an injection time at all, and dropped only when it records none.

Both cases are reported:

```
WARN   No ion injection time in this run; the ion-population features are off.
INFO     ion injection time is the same on every matched spectrum; injection_time and
         tic_injection_time are off. The ion-population features stay.
```

On the Bruker and Sciex files tested here the cvParam is absent altogether rather than
constant, so the first message applies; the second covers an instrument that genuinely
accumulates for a fixed period.

---|---|---|
| `.mzML` | - | Always |
| `.raw` | Thermo | With a pwiz-sharp build |
| `.wiff`, `.wiff2` | Sciex | Not referenced yet; Windows-only when it is |
| `.d` | Agilent, Bruker | Not referenced yet |
| `.lcd` | Shimadzu | Not referenced yet |

---

## Output formats

`calibrate` and `apply` write mzML by default. `--output-format` selects another.

| `--output-format` | Written by | Notes |
|---|---|---|
| `mzML` (default) | MARS, or pwiz | Spliced when the input is mzML; built when it is a vendor file |
| `mzXML` | pwiz | Cannot express ion mobility or some isolation-window terms |
| `mzMLb` | pwiz | mzML in an HDF5 container; roughly half the size |
| `mgf` | pwiz | MS2 peak lists only - no MS1, no chromatograms, no scan metadata |

Two different writers sit behind this, and which one runs depends only on the format.

**mzML is spliced when there is something to splice.** MARS copies the input and replaces
only the m/z arrays it corrected, so everything else is identical by construction rather than
by care. That is the whole of [the passthrough contract](mzml-passthrough.md), and it is why
mzML is the default.

Splicing needs an mzML to copy. Reading a `.raw` and writing mzML has nothing to splice into,
so that file is built by pwiz like any other format - the guarantee applies to mzML in and
mzML out, and is not pretended at otherwise.

**Everything else is built.** There is no input of that format to splice into, so the file is
serialized from scratch by [pwiz-sharp](https://github.com/ProteoWizard/pwiz/pull/4178) - the
same code msconvert uses, which is also what wrote the mzML MARS is reading. Both paths run
the same correction over the same values: writing one file both ways and diffing them with
`mars compare` finds no difference across 82 million peaks.

The binary encoding is read from the input and matched, per array, so a 64-bit zlib input
produces a 64-bit zlib output. Left to its own defaults pwiz writes 64-bit **uncompressed**,
which inflated a Stellar run by 61%.

`mgf` and `mzXML` print a warning at startup saying what they drop.

### Threads

`--threads` defaults to `auto`, which is one worker per logical processor. The run says which
it chose:

```
INFO  Using 16 worker threads, one per logical processor. --threads <n> to change it.
```

One number drives all three parallel stages - the mzML writer, the pwiz spectrum list, and the
histogram build inside the boosting implementation. Matching is not one of them: it streams
spectra in order on a single thread, so a full `calibrate` never speeds up in proportion to
this. Nothing about the correction depends on the count; it is a CPU-use knob, not an accuracy
one, and the output is identical at any setting.

Whether the hardware threads of a simultaneously-multithreaded CPU are worth using is usually
argued rather than measured, so it was measured. Correcting and rewriting one 1.2 GB Stellar
run on an 8-core i9-9900K with 16 logical processors, best of two passes taken in both
directions to spread thermal drift:

| threads | 2 | 4 | 6 | 8 | 10 | 12 | 16 |
|---|---|---|---|---|---|---|---|
| seconds | 150.5 | 77.4 | 52.5 | 45.4 | 42.8 | 38.7 | 36.8 |

Scaling is near-perfect to 4 and still improving at the end: **the 16 logical processors are
24% faster than the 8 physical ones**, so the default uses all of them and capping at physical
cores would give up most of a quarter of the throughput.

It is a shallow curve past 8, though - about half the ideal speedup by 16 - and the writer
emits its finished spectra in order on one thread, which has to become the limit somewhere.
Where that falls on a 64- or 128-core machine has not been measured, so MARS imposes no
ceiling: a guessed one would be worse than none. It reports what it chose instead.

`--threads` above the processor count is allowed and warned about; below 1 is refused, because
`--threads $N` with `N` unset should report a scripting mistake rather than quietly take the
whole machine.

### Speed

`--threads` applies to the pwiz writer as well as to MARS's own. Scoring the model is where a
conversion's time goes - on one Astral run, 243 s of 308 s, against 17 s reading and about
49 s encoding - and pwiz's writers pull spectra one at a time, so MARS reads a batch ahead and
corrects the batch in parallel. That run goes from 318 s on one thread to 103 s on twelve.

Reads stay sequential: they are 5% of the work and the vendor readers are not thread-safe.
What is left after the model is parallelized is mostly pwiz's encoder, which is inside pwiz.

### Byte-reproducibility

mzML and mzXML are byte-reproducible: the same input, model and version produce the same
bytes, on any number of threads. Verified by writing an mzXML on 1 thread and on 12 and
comparing hashes.

**mzMLb is not**, and not because of anything MARS does - two mzMLb writes of identical data,
at the same thread count, differ byte-wise, because the HDF5 container records things that
vary between writes. The spectra are the same; the file is not. Use mzML or mzXML where a
checksum has to match.

### What can this binary do?

`mars --version` reports what the binary in front of you actually carries, which is not a
property of MARS but of how it was built and where it is running:

```
26.1.0
reads:  .mzML, .raw, .wiff, .wiff2, .d, .tdf, .tsf, .baf
writes: mzML, mzXML, mzMLb, mgf
```

A build made without pwiz-sharp says `reads: .mzML` and `writes: mzML`. An arm64 build drops
Bruker, Sciex and mzMLb, because those need native x64 libraries while Thermo's SDK is managed.
The list is what the binary can do here, not what it recognizes the name of - a `.lcd` is
understood well enough to be refused with a reason, and is deliberately not advertised.

### Builds without pwiz

The pwiz reference is optional, because pwiz-sharp has no package feed yet. A MARS built
without it writes mzML and refuses the others with an explanatory error; nothing else about
MARS changes. To enable them, point the build at a pwiz checkout - this needs the **.NET 10
SDK** the way any MARS build does, and a full pwiz working tree rather than a sparse checkout
of `pwiz-sharp/`:

```bash
dotnet build -c Release -p:PwizSharpDir=/path/to/pwiz/pwiz-sharp
```

`mars apply --validate` checks an mzML index and its SHA-1 footer. The other formats have
neither, so it says it is skipping rather than reporting a pass it did not make.

---

## `mars qc`

Matches library fragments against the spectra and reports the mass error that is already
there. Trains nothing, writes no mzML.

**Run this first.** It answers the only question that matters before calibrating: is there
a systematic error here worth removing? On an already well-calibrated instrument the answer
is often no, and the honest outcome is to leave the files alone.

```bash
mars qc --mzml-dir runs/ --prism-report report.csv
mars qc --mzml-dir runs/ --library lib.blib --by-file
```

| Option | Meaning |
|---|---|
| `--mzml <path>` | mzML file or glob. Repeatable |
| `--mzml-dir <dir>` | Directory of mzML files |
| `--prism-report <path>` | Skyline PRISM report, `.csv` or `.parquet`. `--prism-csv` is an accepted alias |
| `--library <path>` | `.blib`, DIA-NN `report-lib.parquet`, or a Skyline PRISM report |
| `--diann-report <path>` | DIA-NN `report.parquet`, for per-run RT windows |
| `--temperature-dir <dir>` | Directory of `RFA2-`/`RFC2-` temperature CSVs |
| `--resolution <mode>` | `unit`, `hram` or `auto` (default `auto`) |
| `--tolerance <Th>` | Fragment tolerance in Th (default 0.3, or from `--resolution`) |
| `--tolerance-ppm <ppm>` | Fragment tolerance in ppm; overrides `--tolerance` |
| `--min-intensity <n>` | Minimum peak intensity to match (default 500) |
| `--max-isolation-window <Th>` | Skip spectra with wider isolation windows |
| `--output <path>` | Text report path (default `mars_qc_summary.txt`) |
| `--html-report <path>` | Figures (default `mars_qc_report.html`, beside the text report) |
| `--no-html-report` | Skip the figures |
| `--by-file` | Report each input file separately rather than pooled |

`qc` writes the same figures as `calibrate`, minus the ones that need a model: no corrected
distribution, no after-heatmap, no feature importance. What is left is the error as
measured and how it varies with each feature, which is exactly what the decision to
calibrate turns on. See [qc-report.md](qc-report.md).

With `--no-html-report` only two features are collected, which is all the numbers need.
With figures on, every feature is collected so the panels mean something; the cost is one
pass over peaks MARS has already decoded.

The text report gives the error in both Th and ppm, which is the quickest way to tell
whether your tolerance is sane before training anything. See
[choosing a tolerance](spectral-libraries.md#choosing-a-tolerance).

---

## `mars calibrate`

Matches, trains, and writes recalibrated files named `{input}-mars.mzML`.

```bash
mars calibrate --mzml-dir runs/ --prism-report report.csv --output-dir corrected/
```

### Input

| Option | Meaning |
|---|---|
| `--mzml <path>` | mzML file or glob. Repeatable |
| `--mzml-dir <dir>` | Directory of mzML files |
| `--prism-report <path>` | Skyline PRISM report, `.csv` or `.parquet` (theoretical `Product Mz`). Recommended. `--prism-csv` is an accepted alias |
| `--library <path>` | `.blib`, DIA-NN `report-lib.parquet`, or a Skyline PRISM report |
| `--diann-report <path>` | DIA-NN `report.parquet`, for per-run RT windows |
| `--temperature-dir <dir>` | Directory of `RFA2-`/`RFC2-` temperature CSVs |

Files can also be passed as bare arguments. All the input files are matched and trained on
together, which is the point: one model over the whole cohort sees more of the error
surface than one model per file.

### Matching

| Option | Default | Meaning |
|---|---|---|
| `--resolution <mode>` | `auto` | `unit`, `hram` or `auto` |
| `--tolerance <Th>` | 0.3 | Fragment tolerance in Th |
| `--tolerance-ppm <ppm>` | - | Fragment tolerance in ppm; overrides `--tolerance` |
| `--min-intensity <n>` | 500 | Minimum peak intensity to match |
| `--max-isolation-window <Th>` | - | Skip spectra with wider isolation windows |
| `--rt-window <min>` | 0.083 | RT half-window around a `.blib` entry's library RT |
| `--no-dedupe-library` | off | Keep transitions that repeat across replicates |

### Model

| Option | Default | Meaning |
|---|---|---|
| `--n-estimators <n>` | 100 | Boosting rounds |
| `--max-depth <n>` | 6 | Tree depth |
| `--learning-rate <x>` | 0.1 | Shrinkage |
| `--robust <mode>` | `trim` | Second pass over rows the first could not explain: `trim`, `huber`, or `none` |
| `--robust-sigma <x>` | 3 | Residual threshold for `--robust`, in robust sigma. 0 disables the second pass |
| `--cv-folds <n>` | 5 | Cross-validation folds, split by peptide. 0 trains a single model |
| `--validation-split <x>` | 0.2 | Held-out fraction, used only when `--cv-folds 0` |
| `--max-training-rows <n>` | no cap | Cap training rows by even stride |
| `--min-training-rows <n>` | 1000 | Refuse to fit below this many matches (exit 2) |
| `--seed <n>` | 42 | Random seed |

The defaults are XGBoost's defaults, which is not a coincidence: see
[the model](model.md#hyperparameters). There is rarely a reason to change them.

**Cross-validation does not change what gets applied.** The correction model is an
ordinary fit over every row - calibration is in-sample by nature - and the folds are a
measurement taken alongside it, answering what the same procedure would achieve on a run it
was not fitted to. So the cost is a few extra training rounds and nothing at correction
time: 52 s for `--cv-folds 0` against 66 s for the default, on one 1.47 GB Stellar file.
The report gives both numbers, labelled. See
[the model](model.md#calibration-is-in-sample-and-that-is-not-a-problem).

### Output

| Option | Meaning |
|---|---|
| `--output-dir <dir>` | Output directory (default `.`) |
| `--model-path <path>` | Where to save the model (default `mars_model.json`) |
| `--report <path>` | Text QC summary (default `mars_qc_summary.txt`) |
| `--html-report <path>` | QC figures (default `mars_qc_report.html`) |
| `--no-html-report` | Skip the figures |
| `--dump-matches <path>` | Every matched fragment as CSV, with all computed features |
| `--dump-predictions <path>` | As `--dump-matches`, plus the model's prediction and residual |
| `--no-recalibrate` | Train and report only; write no mzML |
| `--on-reorder <mode>` | `clamp` (default), `revert`, or `allow` |
| `--python-compat` | Reproduce two known Python inconsistencies, for A/B comparison |
| `--threads <n\|auto>` | Worker threads (default: auto, one per logical processor) |
| `-v, --verbose` | Verbose output |

`--on-reorder` decides what happens when a per-peak correction would put an m/z array out
of ascending order. `clamp` nudges the offending value to preserve order, `revert` leaves
that whole spectrum uncorrected, `allow` writes it anyway. Violations are counted and
reported under every mode. See
[keeping m/z ascending](algorithm.md#keeping-mz-ascending).

`--dump-matches` and `--dump-predictions` are diagnostics, and also the input to the
[parity harness](python-parity.md). A plate-scale cohort produces millions of rows.

---

## `mars apply`

Applies a model trained earlier to more files, with no rematching and no retraining.

```bash
mars apply --model corrected/mars_model.json --mzml-dir more-runs/ --output-dir corrected/
```

| Option | Meaning |
|---|---|
| `--model <path>` | Trained model. Required |
| `--mzml <path>`, `--mzml-dir <dir>` | Input files |
| `--output-dir <dir>` | Output directory (default `.`) |
| `--temperature-dir <dir>` | Temperature CSVs, if the model uses them |
| `--max-isolation-window <Th>` | Leave wider isolation windows uncorrected |
| `--on-reorder <mode>` | `clamp` (default), `revert`, or `allow` |
| `--python-compat` | Reproduce the Python inconsistencies |
| `--threads <n\|auto>` | Worker threads (default: auto, one per logical processor) |
| `--validate` | Check the index and checksum of each output |

Use this for files acquired under the same conditions as the training set. A model carries
an `absolute_time` offset, and the feature list it was trained on; loading a model whose
features do not match what the extractor produces is a hard error rather than a silent
mismatch.

A model trained with the RF temperature features, applied without `--temperature-dir`, says
so. The run still completes - the missing features are substituted the way training substitutes
a missing one - but two of them are then pinned to a value no real spectrum produced, and
nothing in the output would tell you:

```
WARNING: This model was trained with the RF temperature features, but no --temperature-dir was
given. They will be treated as missing for every spectrum.
```

The same is said per file when a directory was given but no CSV matches that run.

The judgement call is whether "the same conditions" still holds. The instrument's
calibration drifts, which is the entire premise of the tool, so a model from three months
ago is not obviously applicable today. `mars qc` on the new files will say.

---

## `mars verify`

Round-trips a file through the passthrough writer applying a **null** correction - decode
and re-encode every m/z array without changing a value - then checks the result is
equivalent to the input.

```bash
mars verify runs/one.mzML
```

| Option | Meaning |
|---|---|
| `-o, --output <path>` | Where to write the round-tripped copy (default `-verify.mzML` alongside the input) |
| `--keep` | Keep the round-tripped file (default: delete it) |
| `--threads <n\|auto>` | Worker threads (default: auto, one per logical processor) |
| `--check-offsets <n>` | Index offsets to spot check (default: all) |
| `-v, --verbose` | Verbose output |

This separates the file-format work from the science. If `verify` passes, MARS can read and
rewrite this vendor's mzML faithfully, and any problem with a calibrated file is in the
model rather than the plumbing. If it fails, nothing else is worth investigating yet.

Run it once per new instrument, conversion pipeline, or msconvert version.

---

## `mars compare`

Compares two mzML files on **decoded** m/z and intensity values.

```bash
mars compare original.mzML corrected/original-mars.mzML
```

| Option | Meaning |
|---|---|
| `--validate` | Also check each file's index and checksum |
| `--max-report <n>` | Detail lines to print (default 10) |

Use this rather than `cmp`. Byte comparison is meaningless here: two zlib implementations
produce different compressed bytes for identical data, so MARS's output is not
byte-identical across platforms even when every decoded value matches. That is
[documented behaviour](mzml-passthrough.md#binary-arrays), not a defect.

## Global

`mars --version` prints the version. `mars <command> --help` prints the options above.
