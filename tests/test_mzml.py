"""Tests for mars mzml module."""

import re

import numpy as np
import pytest

from mars.mzml import (
    _extract_injection_time,
    _extract_isolation_window,
    _parse_iso8601_timestamp,
    DIASpectrum,
    write_calibrated_mzml,
)


class TestParseISO8601Timestamp:
    """Tests for ISO 8601 timestamp parsing."""

    def test_parse_iso8601_with_z(self):
        """Test parsing ISO 8601 timestamp with Z suffix."""
        timestamp_str = "2023-01-15T10:30:45Z"
        result = _parse_iso8601_timestamp(timestamp_str)

        assert result is not None
        assert isinstance(result, float)
        # Should be a valid Unix timestamp
        assert result > 0

    def test_parse_iso8601_with_timezone(self):
        """Test parsing ISO 8601 timestamp with timezone offset."""
        timestamp_str = "2023-01-15T10:30:45+00:00"
        result = _parse_iso8601_timestamp(timestamp_str)

        assert result is not None
        assert isinstance(result, float)

    def test_parse_iso8601_invalid(self):
        """Test parsing invalid timestamp returns None."""
        timestamp_str = "not-a-timestamp"
        result = _parse_iso8601_timestamp(timestamp_str)

        assert result is None

    def test_parse_iso8601_empty(self):
        """Test parsing empty string returns None."""
        result = _parse_iso8601_timestamp("")
        assert result is None


class TestExtractInjectionTime:
    """Tests for injection time extraction."""

    def test_extract_injection_time_from_precursor(self):
        """Test extracting injection time from precursor metadata."""
        spectrum = {
            "precursorList": {
                "precursor": [
                    {
                        "ion injection time": 50.0,  # milliseconds
                    }
                ]
            }
        }

        result = _extract_injection_time(spectrum)

        assert result is not None
        assert result == 0.05  # Should be converted to seconds

    def test_extract_injection_time_from_scan(self):
        """Test extracting injection time from scan metadata."""
        spectrum = {
            "precursorList": {"precursor": [{}]},
            "scanList": {
                "scan": [
                    {
                        "ion injection time": 75.5,  # milliseconds
                    }
                ]
            },
        }

        result = _extract_injection_time(spectrum)

        assert result is not None
        assert result == 0.0755  # Should be converted to seconds

    def test_extract_injection_time_missing(self):
        """Test that None is returned when injection time is missing."""
        spectrum = {
            "precursorList": {"precursor": [{}]},
            "scanList": {"scan": [{}]},
        }

        result = _extract_injection_time(spectrum)

        assert result is None

    def test_extract_injection_time_empty_spectrum(self):
        """Test with empty spectrum dict."""
        result = _extract_injection_time({})

        assert result is None


class TestDIASpectrum:
    """Tests for DIASpectrum dataclass."""

    def test_dia_spectrum_creation(self):
        """Test creating a DIASpectrum with new fields."""
        spectrum = DIASpectrum(
            scan_number=1,
            rt=10.5,
            precursor_mz_low=400.0,
            precursor_mz_high=401.0,
            precursor_mz_center=400.5,
            tic=1e7,
            mz_array=np.array([100.0, 200.0]),
            intensity_array=np.array([1000.0, 2000.0]),
            injection_time=0.05,
            acquisition_start_time=1673779845.0,
            absolute_time=630.0,
        )

        assert spectrum.scan_number == 1
        assert spectrum.rt == 10.5
        assert spectrum.injection_time == 0.05
        assert spectrum.acquisition_start_time == 1673779845.0
        assert spectrum.absolute_time == 630.0
        assert spectrum.n_peaks == 2

    def test_dia_spectrum_optional_fields(self):
        """Test DIASpectrum with optional fields as None."""
        spectrum = DIASpectrum(
            scan_number=1,
            rt=10.5,
            precursor_mz_low=400.0,
            precursor_mz_high=401.0,
            precursor_mz_center=400.5,
            tic=1e7,
            mz_array=np.array([100.0, 200.0]),
            intensity_array=np.array([1000.0, 2000.0]),
            injection_time=None,
            acquisition_start_time=None,
            absolute_time=None,
        )

        assert spectrum.injection_time is None
        assert spectrum.acquisition_start_time is None
        assert spectrum.absolute_time is None


def _build_indexed_mzml(path):
    """Write a minimal indexed mzML (1 MS1 + 1 MS2) with correct byte offsets."""
    import base64
    import hashlib

    def binary_arrays(mzs, ints):
        out = ""
        for name, acc, arr in (
            ("m/z array", "MS:1000514", mzs),
            ("intensity array", "MS:1000515", ints),
        ):
            enc = base64.b64encode(np.asarray(arr, dtype=np.float64).tobytes()).decode("ascii")
            out += (
                f'<binaryDataArray encodedLength="{len(enc)}">'
                '<cvParam cvRef="MS" accession="MS:1000523" name="64-bit float" value=""/>'
                '<cvParam cvRef="MS" accession="MS:1000576" name="no compression" value=""/>'
                f'<cvParam cvRef="MS" accession="{acc}" name="{name}" value=""/>'
                f"<binary>{enc}</binary></binaryDataArray>\n"
            )
        return out

    ms1 = (
        '<spectrum index="0" id="controllerType=0 controllerNumber=1 scan=1" defaultArrayLength="3">\n'
        '<cvParam cvRef="MS" accession="MS:1000511" name="ms level" value="1"/>\n'
        '<cvParam cvRef="MS" accession="MS:1000285" name="total ion current" value="100.0"/>\n'
        '<scanList count="1"><scan><cvParam cvRef="MS" accession="MS:1000016" '
        'name="scan start time" value="1.0" unitAccession="UO:0000031" unitName="minute"/></scan></scanList>\n'
        f'<binaryDataArrayList count="2">\n{binary_arrays([100.0, 200.0, 300.0], [1.0, 2.0, 3.0])}'
        "</binaryDataArrayList>\n</spectrum>\n"
    )
    ms2 = (
        '<spectrum index="1" id="controllerType=0 controllerNumber=1 scan=2" defaultArrayLength="3">\n'
        '<cvParam cvRef="MS" accession="MS:1000511" name="ms level" value="2"/>\n'
        '<cvParam cvRef="MS" accession="MS:1000285" name="total ion current" value="200.0"/>\n'
        '<scanList count="1"><scan><cvParam cvRef="MS" accession="MS:1000016" '
        'name="scan start time" value="1.1" unitAccession="UO:0000031" unitName="minute"/></scan></scanList>\n'
        '<precursorList count="1"><precursor><isolationWindow>'
        '<cvParam cvRef="MS" accession="MS:1000827" name="isolation window target m/z" value="450.0"/>'
        '<cvParam cvRef="MS" accession="MS:1000828" name="isolation window lower offset" value="0.5"/>'
        '<cvParam cvRef="MS" accession="MS:1000829" name="isolation window upper offset" value="0.5"/>'
        "</isolationWindow></precursor></precursorList>\n"
        f'<binaryDataArrayList count="2">\n{binary_arrays([110.0, 220.0, 330.0], [4.0, 5.0, 6.0])}'
        "</binaryDataArrayList>\n</spectrum>\n"
    )

    header = (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<indexedmzML xmlns="http://psi.hupo.org/ms/mzml">\n'
    )
    body = (
        '<mzML xmlns="http://psi.hupo.org/ms/mzml" id="test" version="1.1.0">\n'
        '<cvList count="1"><cv id="MS" fullName="PSI-MS" version="4.1.0" URI="https://example.org/psi-ms.obo"/></cvList>\n'
        '<run id="r1" startTimeStamp="2026-08-12T10:00:00Z">\n'
        f'<spectrumList count="2" defaultDataProcessingRef="dp1">\n{ms1}{ms2}</spectrumList>\n'
        "</run>\n</mzML>\n"
    )
    main = header + body
    main_bytes = main.encode("utf-8")

    offsets = [
        (m.group(1).decode(), m.start())
        for m in re.finditer(rb'<spectrum[^>]+id="([^"]+)"', main_bytes)
    ]
    index_xml = '<indexList count="1">\n<index name="spectrum">\n'
    index_xml += "".join(f'<offset idRef="{i}">{o}</offset>\n' for i, o in offsets)
    index_xml += "</index>\n</indexList>\n"
    offset_line = f"<indexListOffset>{len(main_bytes)}</indexListOffset>\n"
    sha1 = hashlib.sha1(main_bytes + index_xml.encode() + offset_line.encode()).hexdigest()

    path.write_bytes(
        (main + index_xml + offset_line + f"<fileChecksum>{sha1}</fileChecksum>\n</indexedmzML>").encode("utf-8")
    )


class TestWriteCalibratedMzML:
    """Regression tests for index integrity of the written mzML."""

    def test_index_offsets_are_valid_byte_positions(self, tmp_path):
        """Offsets must be byte positions into the file as actually written.

        Regression: writing in text mode on Windows translated every '\n' to
        '\r\n' after the offsets had been computed, shifting each one by the
        number of preceding lines and breaking readers (DIA-NN/Carafe/pwiz
        failed with "parseOffset() 4: Syntax error parsing XML").
        """
        src = tmp_path / "in.mzML"
        dst = tmp_path / "out.mzML"
        _build_indexed_mzml(src)

        write_calibrated_mzml(src, dst, lambda meta, mz, inten: mz + 0.01)

        data = dst.read_bytes()
        assert b"\r\n" not in data, "output must not contain CRLF; it invalidates the index"

        ilo = int(re.search(rb"<indexListOffset>(\d+)</indexListOffset>", data).group(1))
        assert data[ilo:].lstrip().startswith(b"<indexList"), "indexListOffset must point at <indexList"

        found = re.findall(rb'<offset idRef="([^"]+)">(\d+)</offset>', data[ilo:])
        assert len(found) == 2
        for id_ref, offset in found:
            assert data[int(offset):].startswith(b"<spectrum "), (
                f"offset for {id_ref.decode()} does not land on a <spectrum> tag"
            )

    def test_written_file_is_readable_by_pyteomics(self, tmp_path):
        """The regenerated index must support random access, not just iteration."""
        from pyteomics import mzml as pyt_mzml

        src = tmp_path / "in.mzML"
        dst = tmp_path / "out.mzML"
        _build_indexed_mzml(src)

        write_calibrated_mzml(src, dst, lambda meta, mz, inten: mz + 0.01)

        with pyt_mzml.MzML(str(dst), use_index=True) as reader:
            spec = reader.get_by_id("controllerType=0 controllerNumber=1 scan=2")
        assert spec["ms level"] == 2
        # MS2 m/z values were shifted by the calibration function
        np.testing.assert_allclose(spec["m/z array"], [110.01, 220.01, 330.01])
