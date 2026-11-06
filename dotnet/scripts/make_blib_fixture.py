"""Cuts a small test fixture out of a real Osprey result .blib.

usage: make_blib_fixture.py <source.blib> <theoretical-out.blib> <observed-out.blib>

The second output is the same subset with every peak m/z moved by +0.25 Th, standing in for a
library of OBSERVED unit-resolution spectra, which MARS must still refuse.
"""
import shutil
import sqlite3
import struct
import sys
import zlib

source, theoretical, observed = sys.argv[1:4]
shutil.copyfile(source, theoretical)

con = sqlite3.connect(theoretical)
con.execute('PRAGMA journal_mode=DELETE')

# A spread of charges, with and without a modification: the first 8 modified spectra and the
# first 16 unmodified ones of charge 2 or 3.
modified = [r[0] for r in con.execute(
    'SELECT DISTINCT RefSpectraID FROM Modifications ORDER BY RefSpectraID LIMIT 8')]
plain = [r[0] for r in con.execute(
    'SELECT id FROM RefSpectra WHERE id NOT IN (SELECT RefSpectraID FROM Modifications) '
    'AND precursorCharge IN (2, 3) ORDER BY id LIMIT 16')]
keep = sorted(set(modified + plain))
con.execute('CREATE TEMP TABLE keep (id INTEGER PRIMARY KEY)')
con.executemany('INSERT INTO keep VALUES (?)', [(k,) for k in keep])

tables = [r[0] for r in con.execute("SELECT name FROM sqlite_master WHERE type = 'table'")]
for table in tables:
    columns = [r[1] for r in con.execute('PRAGMA table_info(%s)' % table)]
    if 'RefSpectraID' in columns:
        con.execute('DELETE FROM %s WHERE RefSpectraID NOT IN (SELECT id FROM keep)' % table)
con.execute('DELETE FROM RefSpectra WHERE id NOT IN (SELECT id FROM keep)')
con.execute('DELETE FROM Proteins WHERE id NOT IN (SELECT ProteinID FROM RefSpectraProteins)')
con.execute("UPDATE LibInfo SET numSpecs = (SELECT count(*) FROM RefSpectra)")
con.commit()
con.execute('VACUUM')
con.close()

shutil.copyfile(theoretical, observed)
con = sqlite3.connect(observed)
for rid, blob in con.execute('SELECT RefSpectraID, peakMZ FROM RefSpectraPeaks').fetchall():
    try:
        raw = zlib.decompress(blob)
    except zlib.error:
        raw = blob
    mz = struct.unpack('<%dd' % (len(raw) // 8), raw)
    shifted = struct.pack('<%dd' % len(mz), *[m + 0.25 for m in mz])
    con.execute('UPDATE RefSpectraPeaks SET peakMZ = ? WHERE RefSpectraID = ?', (shifted, rid))
con.commit()
con.execute('VACUUM')
con.close()

for path in (theoretical, observed):
    c = sqlite3.connect(path)
    print(path, c.execute('SELECT count(*) FROM RefSpectra').fetchone()[0], 'spectra,',
          c.execute('SELECT count(*) FROM RetentionTimes').fetchone()[0], 'RT rows,',
          c.execute('SELECT count(DISTINCT RefSpectraID) FROM Modifications').fetchone()[0], 'modified')
