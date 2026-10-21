"""Independent validation. Requires Poppler (pdfinfo/pdftoppm/pdfimages) and Pillow.
Usage: python tests/verify-pdfs.py <core-test-results-directory>
"""
from pathlib import Path
import subprocess
import sys
from PIL import Image

root = Path(sys.argv[1]).resolve()
for mode, expected in [('Lineart', [0, 255, 255]), ('Gray', [0, 255, 128]), ('Color', [0, 255, 128])]:
    pdf = root / (mode + '.pdf')
    subprocess.run(['pdfinfo', str(pdf)], check=True, capture_output=True)
    prefix = root / (mode + '-independent')
    subprocess.run(['pdftoppm', '-r', '720', '-png', '-singlefile', str(pdf), str(prefix)], check=True)
    image = Image.open(str(prefix) + '.png').convert('RGB')
    assert image.size == (30, 20), (mode, image.size)
    for y in [4, 14]:
        pixels = [image.getpixel((x, y)) for x in [4, 14, 24]]
        assert pixels == [(v, v, v) for v in expected], (mode, pixels)
    print('PASS: independently rendered pixels, both rows:', mode)
prefix = root / 'monochrome-extracted'
subprocess.run(['pdfimages', '-png', str(root / 'Lineart.pdf'), str(prefix)], check=True)
image = Image.open(str(prefix) + '-000.png').convert('RGB')
assert image.size == (3, 2)
assert [image.getpixel((x, y))[0] for y in range(2) for x in range(3)] == [0, 255, 255] * 2
print('PASS: independently extracted packed monochrome has correct polarity')
