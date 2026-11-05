# MARS vNEXT Release Notes

MARS now finds a run's coarse m/z offset before matching, so a badly calibrated run is
matched on its fragments' monoisotopic peaks rather than on an isotope of them.

## New Features

### The matching window is anchored on the monoisotopic peak

MARS centres its matching window on the library m/z and takes the most intense peak inside.
That is sound while the instrument's error is small against the window, and stops being
sound before the error reaches the window width - because what replaces the monoisotopic
peak inside the window is often an isotope of the fragment.

Before matching a run, MARS now scans for the coarse m/z offset that puts the most library
fragments on a peak, checks that it landed on the monoisotope rather than the M+1, refines
it to the median of the peaks it found, and centres the window there. The shift moves the
window and the features that describe where a peak sits; it does not move the training
label, which stays the whole error from the library m/z. The offset therefore travels
inside the trained model, and correcting a file needs no new information.

The shift is applied only when it exceeds half the matching tolerance, so a well-calibrated
run is matched exactly as it was before. On the frozen Stellar parity reference the match
dump is unchanged, column for column.

New options on `calibrate` and `qc`:

- `--mz-shift <Th>` matches at a stated offset instead of looking for one.
- `--no-anchor` turns the step off.

This runs for absolute (`--tolerance`) windows only; automatic anchoring is not supported for
ppm windows. A ppm window is far narrower than any isotope spacing, so an offset that grows
gradually costs the matches rather than drifting onto an isotope. But an offset within the
window of a whole isotope spacing - -1.00335/z Th - lands that charge's M+1 on the library
m/z while the monoisotope sits outside, and MARS does not detect it: the matches are tight
and centred, so the placement check below can pass them. `--mz-shift` works with
`--tolerance-ppm` for an offset you know. The
[algorithm doc](../docs/algorithm.md#anchoring-the-window-on-the-monoisotopic-peak) has the
numbers.

### A check that the matching window was in the right place

After matching each run, MARS measures where the matched errors sit inside the window they
were matched in, and warns when that is not where a real fragment population would put
them - either a distribution still climbing as it reaches a window edge, or one spread
flatly across the whole window because nothing in it was ever the fragment. Both statistics
appear in the run log and the QC report whether or not they are suspect.

This is the backstop for an offset the anchor could not find, and it runs for ppm
tolerances as well. On a ppm window it judges each row against its own window, which is a
different width in Th at every m/z. It cannot see the ppm isotope case above, because those
matches sit centred in the window.

## Bug Fixes

### Fragments were matched to the wrong isotope on badly calibrated runs

A fragment's isotopes sit `1.00335 / z` Th apart, so an isotope enters a `+/-0.3` Th window
at an offset of -1.30 to -0.70 Th for a singly charged fragment, **-0.80 to -0.20 Th for a
doubly charged one**, and -0.63 to -0.03 Th for a triply charged one. Multiply charged
products are not rare - the Stellar reference library is 38% 2+ and 16% 3+ - so this did
not need an extreme offset to start happening.

An isotope match is the dangerous kind of wrong: its residual is as tight as a real match's,
so the model trained on it, every accuracy figure stayed good, and the correction came out a
whole isotope spacing wrong. Measured on the reference run with the library shifted to
simulate an instrument offset:

Each row is the same reference file, matched against a library shifted to put its fragments
where that instrument offset would. "On the M+1" is the share of matches that landed on the
fragment's first isotope instead of its monoisotope.

| Instrument offset | Matches | On the M+1 (1+) | On the M+1 (2+) | MAD reported |
|---|---|---|---|---|
| as acquired | 146,515 | 0.0% | 0.0% | 0.0442 Th, 44.9% reduction |
| -0.25 Th, before | 98,976 | 0.0% | **18.5%** | 0.0377 Th, 31.8% reduction |
| -0.50 Th, before | 23,184 | 0.0%, and no monoisotopes either - all noise | **42.2%** | 0.0982 Th, 43.7% reduction |
| -0.75 Th, before | 58,412 | **65.8%** | 22.0% | 0.0599 Th, 28.6% reduction |

Not one match in the three shifted runs landed on the monoisotope.

At -0.75 Th the median matched error was +0.153 Th where the truth was -0.750: MARS learned
a small positive correction, wrote it into the corrected mzML, and reported a 41.4%
reduction in RMS.

With anchoring, all three shifted runs recover the unshifted run's own numbers. The anchor
finds -0.250, -0.500 and -0.750 Th; matching then yields 146,513 rows against 146,515, with
79.9% of 1+ and 59.5% of 2+ matches on the monoisotope and **0.0% on the M+1 at every
charge** - the unshifted run's percentages to the decimal. The model comes out at MAD
0.0442 Th, a 44.9% reduction, against 0.0442 Th and 44.9% unshifted.

**If you have corrected files from a run whose MS2 m/z were more than about 0.2 Th off,
recorrect them.** The affected runs are identifiable: the new window-placement check flags
them, and so does a QC report whose median delta sits far from zero relative to the
tolerance.

### Windows builds carried two Microsoft libraries with security advisories

Windows builds of MARS include pwiz's Sciex reader, and with it two Microsoft libraries:
`System.Security.Cryptography.Xml` 8.0.4, which pwiz pins, and
`System.Security.Cryptography.Pkcs` 8.0.1, which that version pulls in. Microsoft's .NET
security release of 2026-10-13 published six advisories against them - four against the
first (CVE-2026-96958, 96959, 96960 and 96222), two against the second (CVE-2026-84008 and
70162) - covering denial of service and spoofing.

Both now come from the patched .NET 10 line, 10.0.14. pwiz's pin is a .NET 8-line version
left over from before pwiz-sharp moved to .NET 10, and MARS cannot change it, so MARS now
overrides it for what it builds and ships. Only the Windows artifacts are affected: the Sciex
reader is the only part of pwiz that brings either library in, and MARS includes it only on
Windows.

MARS uses neither library itself; they serve the Sciex reader's configuration plumbing.
They are fixed rather
than argued about because a release should not ship a known advisory, whether or not
MARS ever reaches the affected code.

**How this was found, and what now stops it recurring.** The advisories first appeared as a
build failure: pwiz treats an advisory against a package it declares as an error, so the
vendor CI job and the release publish both began failing on 2026-10-13 with nothing in MARS
changed. That check could only ever see half of the problem. pwiz audits the packages it
names directly, and the Pkcs library arrives one step removed, so its two advisories never
appeared there at all.

The vendor builds now stop pwiz's error over a pin MARS has overridden, and audit instead
what MARS actually ships: every package version that reaches the binary, whoever declared
it. That check is fatal, and it is the one that found the Pkcs advisories.

This changes which library versions the Windows binaries bundle. Neither library is used by
the calibration or by any of MARS's writers.

## Performance

Anchoring reads a bounded sample of each run before the matching pass, sized by what it
collects rather than by what it reads: it stops at 20,000 fragment targets, or at 60,000
spectra for a run too sparse to reach that. On the Stellar reference cohort that is 37,700
spectra, a third of the run, and about 1.5 s per file - inside the run-to-run noise of a
31 s calibrate.

Sizing the sample by spectra alone was tried first and was a mistake worth recording: the
head of a run is the worst part of it to sample. The first 20,000 MS2 spectra of the
reference run span RT 0 to 3.8 minutes while its earliest library peptide elutes at 2.3, so
most of that budget bought nothing, the sample came to 2,143 targets, and the offset it
found was 0.007 Th out. Collecting to a target count instead costs the same order of time
and lands the offset exactly.

## Breaking Changes

None. The model file format, the correction path and the mzML passthrough contract are
unchanged, and a well-calibrated run produces the same output as before.
