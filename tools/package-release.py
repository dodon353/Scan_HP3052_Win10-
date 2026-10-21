"""Build the novice-facing release ZIP from a successful Windows publish."""
from pathlib import Path
import hashlib
import os
import shutil
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[1]
DIST = ROOT / 'dist'
PACKAGE = DIST / 'HP3052Scanner_0.7.0'
EXE = DIST / 'publish' / 'HP3052Scanner.exe'
if not EXE.is_file() or EXE.stat().st_size < 10_000_000:
    raise RuntimeError('A standalone EXE must be published before packaging.')
if PACKAGE.exists():
    shutil.rmtree(PACKAGE)
(PACKAGE / 'app').mkdir(parents=True)
EXTRA = PACKAGE / 'Дополнительно'
(EXTRA / 'Лицензии').mkdir(parents=True)
shutil.copyfile(EXE, PACKAGE / 'app' / EXE.name)
for name in ('START.cmd', 'INSTALL.cmd', 'INSTALL-UBUNTU.cmd'):
    shutil.copyfile(ROOT / name, PACKAGE / name)
shutil.copytree(ROOT / 'installer', PACKAGE / 'installer')
shutil.copyfile(ROOT / 'docs' / 'Инструкция.txt', PACKAGE / 'ИНСТРУКЦИЯ.txt')

# Preserve the exact tracked source commit without build output or private files.
tracked = subprocess.check_output(['git', 'ls-files', '-z'], cwd=ROOT).decode('utf-8').split('\0')
with zipfile.ZipFile(EXTRA / 'Исходники.zip', 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
    hashes = []
    for name in sorted(n for n in tracked if n):
        file = ROOT / name
        archive.write(file, 'HP3052Scanner_Source_0.7.0/' + name)
        hashes.append(hashlib.sha256(file.read_bytes()).hexdigest() + '  ' + name + '\n')
    archive.writestr('HP3052Scanner_Source_0.7.0/PACKAGE-MANIFEST.sha256', ''.join(hashes))

nuget = Path(os.environ.get('NUGET_PACKAGES', Path.home() / '.nuget/packages'))
# The pinned 8.0.32 runtime and host packages carry identical LICENSE and
# THIRD-PARTY-NOTICES files. One copy covers both. On Windows the host pack
# is supplied by the SDK and need not exist in the NuGet cache.
for package, label in (
    ('microsoft.netcore.app.runtime.win-x64', 'NET-Runtime-and-Host'),
    ('microsoft.windowsdesktop.app.runtime.win-x64', 'WindowsDesktop'),
):
    versions = sorted((nuget / package).glob('*'), key=lambda p: tuple(int(n) for n in p.name.split('.')))
    if not versions:
        raise RuntimeError('Missing license package: ' + package)
    found_license = False
    for name in ('LICENSE', 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT'):
        source = versions[-1] / name
        if source.is_file():
            shutil.copyfile(source, EXTRA / 'Лицензии' / (label + '-' + name + ('.txt' if '.' not in name else '')))
            found_license |= name.startswith('LICENSE')
    if not found_license:
        raise RuntimeError('Missing component license: ' + package)

sha = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
(EXTRA / 'О_сборке.txt').write_text(
    'HP 3052 Scanner 0.7.0\nWindows x64, standalone single-file with bundled .NET.\n'
    'Built on Windows in GitHub Actions after Core, Windows image and PowerShell checks.\n'
    'Source commit: ' + sha + '\n'
    'WSL1/Ubuntu/HPLIP are required for scanner access. No hardware scan was run in CI.\n',
    encoding='utf-8-sig',
)
files = sorted(p for p in PACKAGE.rglob('*') if p.is_file())
manifest = EXTRA / 'Контрольные_суммы.sha256'
manifest.write_text(''.join(hashlib.sha256(p.read_bytes()).hexdigest() + '  ' + p.relative_to(PACKAGE).as_posix() + '\n' for p in files), encoding='utf-8')
output = DIST / 'HP3052Scanner_v0.7.0_Windows_x64.zip'
with zipfile.ZipFile(output, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
    for file in files + [manifest]:
        archive.write(file, PACKAGE.name + '/' + file.relative_to(PACKAGE).as_posix())
with zipfile.ZipFile(output) as archive:
    if archive.testzip() is not None:
        raise RuntimeError('Release ZIP CRC check failed.')
    names = archive.namelist()
    assert sum(n.endswith('.exe') for n in names) == 1
    assert not any(n.endswith(('.dll', '.pdb', '.log')) for n in names)
(DIST / 'SHA256SUMS.txt').write_text(
    hashlib.sha256(output.read_bytes()).hexdigest() + '  ' + output.name + '\n'
    + hashlib.sha256(EXE.read_bytes()).hexdigest() + '  HP3052Scanner.exe\n', encoding='utf-8'
)
print('Release ZIP:', output.name, output.stat().st_size, 'bytes')
print('Source commit:', sha)
