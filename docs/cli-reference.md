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
mars qc --mzml-dir runs/ --prism-csv report.csv        # is there anything to correct?
mars verify runs/one.mzML                              # can MARS handle this file at all?
mars calibrate --mzml-dir runs/ --prism-csv report.csv --output-dir corrected/
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

### Which vendors

Only Thermo today. The others are recognized well enough to say what is missing rather than
"unrecognized file":

| Extension | Vendor | Status |
|---|---|---|
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

### Builds without pwiz

The pwiz reference is optional, because pwiz-sharp has no package feed yet. A MARS built
without it writes mzML and refuses the others with an explanatory error; nothing else about
MARS changes. To enable them, point the build at a pwiz checkout:

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
mars qc --mzml-dir runs/ --prism-csv report.csv
mars qc --mzml-dir runs/ --library lib.blib --by-file
```

| Option | Meaning |
|---|---|
| `--mzml <path>` | mzML file or glob. Repeatable |
| `--mzml-dir <dir>` | Directory of mzML files |
| `--prism-csv <path>` | Skyline PRISM report |
| `--library <path>` | `.blib`, DIA-NN `report-lib.parquet`, or a PRISM `.csv` |
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
mars calibrate --mzml-dir runs/ --prism-csv report.csv --output-dir corrected/
```

### Input

| Option | Meaning |
|---|---|
| `--mzml <path>` | mzML file or glob. Repeatable |
| `--mzml-dir <dir>` | Directory of mzML files |
| `--prism-csv <path>` | Skyline PRISM report CSV (theoretical `Product Mz`). Recommended |
| `--library <path>` | `.blib`, DIA-NN `report-lib.parquet`, or a PRISM `.csv` |
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
| `--threads <n>` | Worker threads |
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
| `--threads <n>` | Worker threads |
| `--validate` | Check the index and checksum of each output |

Use this for files acquired under the same conditions as the training set. A model carries
an `absolute_time` offset, and the feature list it was trained on; loading a model whose
features do not match what the extractor produces is a hard error rather than a silent
mismatch.

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
| `--threads <n>` | Worker threads |
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
