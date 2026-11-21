"""Render this mark's SVG rect/polyline subset to RGBA PNG with no dependencies.

Run with Python 3; reads SVG as the single geometry/color source. Rasterization
uses 8x8 coverage per output pixel and premultiplied alpha accumulation.
This is an offline asset tool, never called by Unity or at runtime.
"""
from pathlib import Path
import math
import struct
import xml.etree.ElementTree as ET
import zlib

ROOT = Path(__file__).resolve().parent.parent


def rgb(value):
    return tuple(int(value[i:i + 2], 16) for i in (1, 3, 5))


def segment_distance(x, y, a, b):
    dx, dy = b[0] - a[0], b[1] - a[1]
    t = max(0, min(1, ((x - a[0]) * dx + (y - a[1]) * dy) / (dx * dx + dy * dy)))
    return math.hypot(x - a[0] - t * dx, y - a[1] - t * dy)


def contains(element, x, y):
    tag = element.tag.rsplit('}', 1)[-1]
    if tag == 'polyline':
        pts = [tuple(map(float, p.split(','))) for p in element.attrib['points'].split()]
        radius = float(element.attrib['stroke-width']) / 2
        return any(segment_distance(x, y, a, b) <= radius for a, b in zip(pts, pts[1:]))
    if tag == 'rect':
        a = element.attrib
        left, top, w, h, r = (float(a[k]) for k in ('x', 'y', 'width', 'height', 'rx'))
        qx = abs(x - left - w / 2) - (w / 2 - r)
        qy = abs(y - top - h / 2) - (h / 2 - r)
        return math.hypot(max(qx, 0), max(qy, 0)) + min(max(qx, qy), 0) <= r
    return False


def chunk(kind, data):
    return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xffffffff)


def render(size):
    elements = list(ET.parse(ROOT / 'WhimShaderIcon.svg').getroot())
    shapes = [(e, rgb(e.attrib.get('stroke') or e.attrib['fill']))
              for e in elements if e.tag.rsplit('}', 1)[-1] in ('rect', 'polyline')]
    rows = bytearray()
    for y in range(size):
        rows.append(0)  # PNG filter: none
        for x in range(size):
            sums, coverage = [0, 0, 0], 0
            for sy in range(8):
                for sx in range(8):
                    px, py = (x + (sx + .5) / 8) * 16 / size, (y + (sy + .5) / 8) * 16 / size
                    color = None
                    for element, fill in shapes:
                        if contains(element, px, py):
                            color = fill
                    if color:
                        coverage += 1
                        for i in range(3):
                            sums[i] += color[i]
            rows.extend([round(c / coverage) if coverage else 0 for c in sums])
            rows.append(round(255 * coverage / 64))
    header = struct.pack('>2I5B', size, size, 8, 6, 0, 0, 0)
    png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', header) + chunk(b'sRGB', b'\0')
    png += chunk(b'IDAT', zlib.compress(rows, 9)) + chunk(b'IEND', b'')
    (ROOT / f'WhimShaderIcon{size}.png').write_bytes(png)
    print(f'WhimShaderIcon{size}.png: {size}x{size} RGBA, {len(png)} bytes')


if __name__ == '__main__':
    for resolution in (16, 32, 64):
        render(resolution)
