"""Verify the source ZIP and its complete SHA256 manifest using Python stdlib.
Usage: python tests/check-package.py <downloaded-zip>
"""
from pathlib import Path, PurePosixPath
import hashlib
import sys
import zipfile

path = Path(sys.argv[1])
with path.open('rb') as stream:
    assert stream.read(4) == b'PK\x03\x04', 'Expected ZIP, not RAR or a renamed file'
with zipfile.ZipFile(path) as archive:
    assert archive.testzip() is None, 'CRC failure'
    names = [n for n in archive.namelist() if not n.endswith('/')]
    assert len(names) == len(set(names)), 'Duplicate archive entries'
    roots = {PurePosixPath(n).parts[0] for n in names}
    assert len(roots) == 1, 'Expected a single package root'
    root = next(iter(roots))
    for name in names:
        parts = PurePosixPath(name).parts
        assert '..' not in parts and not name.startswith('/'), 'Unsafe archive path'
        assert not {'app', 'bin', 'obj', '__pycache__'}.intersection(parts), 'Generated output in source archive'
        assert not name.lower().endswith(('.exe', '.dll', '.pdb')), 'Binary in source archive'
    manifest = root + '/PACKAGE-MANIFEST.sha256'
    expected = {}
    for line in archive.read(manifest).decode('utf-8').splitlines():
        digest, relative = line.split('  ', 1)
        expected[root + '/' + relative] = digest
    assert set(expected) == set(names) - {manifest}, 'Manifest does not cover the archive exactly'
    for name, digest in expected.items():
        assert hashlib.sha256(archive.read(name)).hexdigest() == digest, 'Hash mismatch: ' + name
print('PASS: ZIP signature, CRC, source-only contents and all SHA256 hashes')
