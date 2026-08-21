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
| `--tolerance <Th>` | Fragment tolerance in Th (default 0.3) |
| `--tolerance-ppm <ppm>` | Fragment tolerance in ppm; overrides `--tolerance` |
| `--min-intensity <n>` | Minimum peak intensity to match (default 500) |
| `--max-isolation-window <Th>` | Skip spectra with wider isolation windows |
| `--output <path>` | Report path (default `mars_qc_summary.txt`) |
| `--by-file` | Report each input file separately rather than pooled |

The report gives the error in both Th and ppm, which is the quickest way to tell whether
your tolerance is sane before training anything. See
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
| `--validation-split <x>` | 0.2 | Held-out fraction. 0 trains on everything |
| `--max-training-rows <n>` | no cap | Cap training rows by even stride |
| `--min-training-rows <n>` | 1000 | Refuse to fit below this many matches (exit 2) |
| `--seed <n>` | 42 | Random seed |

The defaults are XGBoost's defaults, which is not a coincidence: see
[the model](model.md#hyperparameters). There is rarely a reason to change them.

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
