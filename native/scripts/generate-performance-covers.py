"""Deterministic, original synthetic media. Outputs only to the explicit artifact directory."""
import argparse
import base64
import hashlib
import json
from pathlib import Path
from PIL import Image, ImageDraw

p = argparse.ArgumentParser()
p.add_argument('--output', required=True)
p.add_argument('--count', type=int, default=10000)
p.add_argument('--seed', type=int, default=7101)
a = p.parse_args()
root = Path(a.output).resolve()
if '.artifacts' not in root.parts or a.count < 1 or a.count > 10000:
    raise SystemExit('Explicit .artifacts output and count 1..10000 required')
root.mkdir(parents=True, exist_ok=True)
items = []
for i in range(a.count):
    digest = hashlib.sha256(f'{a.seed}:{i}'.encode()).digest()
    width, height = 320 + i % 5 * 32, 480 + i % 7 * 24
    img = Image.new('RGB', (width, height), tuple(digest[:3]))
    draw = ImageDraw.Draw(img)
    for band in range(16):
        color = tuple((digest[(band + c) % 32] + i * (c + 1)) % 256 for c in range(3))
        draw.rectangle((0, band * height // 16, width, (band + 1) * height // 16), fill=color)
    for shape in range(8):
        x = digest[shape] * width // 256
        y = digest[shape + 8] * height // 256
        draw.ellipse((x, y, min(width, x + 65), min(height, y + 85)), fill=tuple(digest[shape:shape + 3]))
    draw.text((12, 12), f'SYNTHETIC {a.seed} / {i:05d}', fill='white')
    ext = ('jpg', 'png', 'webp')[i % 3]
    file = root / f'cover-{i:05d}.{ext}'
    img.save(file, quality=82, **({'method': 2} if ext == 'webp' else {}))
    items.append(dict(file=file.name, width=width, height=height, sha256=hashlib.sha256(file.read_bytes()).hexdigest(), bytes=file.stat().st_size))
    if i % 1000 == 0:
        print(f'covers={i}', flush=True)
edge = root / 'edge'
edge.mkdir(exist_ok=True)
(edge / 'corrupt.png').write_bytes(b'invalid synthetic image')
# Stable repro from our original PNG fixture; WIC tolerates its incomplete IDAT.
(edge / 'truncated.png').write_bytes(base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAWAAAAH4CAIAAAD7NqA1AAATFUlEQVR4nO3de5CdZX3A8feEk6BACNdQDdGiRA=='))
(edge / 'invalid.webp').write_bytes(b'RIFF\x04\x00\x00\x00WEBP')
for name, size in [('small.png', (2, 3)), ('aspect.png', (1500, 40)), ('moderate.png', (2400, 3600)), ('封面-日本語.png', (320, 480))]:
    Image.new('RGB', size, (33, 66, 99)).save(edge / name)
exif = Image.Exif()
exif[274] = 6
Image.new('RGB', (320, 480), (111, 88, 44)).save(edge / 'orientation.jpg', exif=exif)
long_dir = edge / ('long-' + 'a' * 90) / ('b' * 90)
long_dir.mkdir(parents=True, exist_ok=True)
Image.new('RGB', (32, 48), (2, 3, 4)).save(long_dir / 'long-cover.png')
assert len({v['sha256'] for v in items}) == a.count
edges = [dict(file=f.relative_to(root).as_posix(), bytes=f.stat().st_size, sha256=hashlib.sha256(f.read_bytes()).hexdigest()) for f in sorted(edge.rglob('*')) if f.is_file()]
(root / 'dataset.json').write_text(json.dumps(dict(seed=a.seed, count=a.count, generator='Pillow 12.3.0 original synthetic bands/shapes', generatorSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), formats=['jpeg', 'png', 'webp'], items=items, edges=edges), indent=2), encoding='utf-8')
print(f'complete: {a.count} unique covers, {sum(v["bytes"] for v in items)} bytes', flush=True)
