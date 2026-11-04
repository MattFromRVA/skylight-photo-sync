#!/usr/bin/env bash
# Regenerates the image fixtures under tests/SkylightPhotoSync.Tests/Fixtures/Images.
#
# macOS only: every image is produced by the built-in `sips` tool (ImageIO), so no image
# library is needed. The seed is a hand-written 64x48 24-bit BMP gradient; EXIF segments are
# built byte-by-byte with Python's standard library (no third-party modules).
#
# Outputs (all 64x48):
#   tiny.jpg        JPEG with EXIF DateTimeOriginal 2020:01:02 03:04:05, orientation 1
#   tiny.png        PNG (no EXIF)
#   tiny.gif        GIF (no EXIF)
#   tiny_rot6.heic  HEIC whose EXIF orientation is 6 (rotate 90 CW), same DateTimeOriginal
#
# tiny.mp4 is NOT produced here; it is a static fixture made once with
# scripts/make-tiny-mp4.swift (AVFoundation, 2 frames of H.264 at 64x48, under 1 KB).
#
# Usage: scripts/make-fixtures.sh    (from anywhere; writes into the repo's fixture folder)
set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "make-fixtures.sh needs macOS (sips)." >&2
  exit 1
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="$root/tests/SkylightPhotoSync.Tests/Fixtures/Images"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$out"

captured="2020:01:02 03:04:05"

# 1. Seed: 64x48 bottom-up 24-bit BMP with a simple gradient.
python3 -I - "$tmp/seed.bmp" <<'PY'
import struct, sys
w, h = 64, 48
pixels = bytearray()
for y in range(h - 1, -1, -1):          # BMP rows are stored bottom-up
    for x in range(w):
        pixels += bytes((x * 4 % 256, y * 5 % 256, 128))   # B, G, R
file_header = b"BM" + struct.pack("<IHHI", 54 + len(pixels), 0, 0, 54)
info_header = struct.pack("<IiiHHIIiiII", 40, w, h, 1, 24, 0, len(pixels), 2835, 2835, 0, 0)
with open(sys.argv[1], "wb") as f:
    f.write(file_header + info_header + pixels)
PY

# 2. Baseline JPEG via sips.
sips -s format jpeg "$tmp/seed.bmp" --out "$tmp/plain.jpg" > /dev/null

# 3. Replace the EXIF APP1 segment with one we control: orientation + DateTimeOriginal.
#    Any Exif APP1 segment sips wrote is dropped so the file carries exactly one.
inject_exif() {   # src dst orientation datetime
  python3 -I - "$1" "$2" "$3" "$4" <<'PY'
import struct, sys
src, dst, orientation, captured = sys.argv[1], sys.argv[2], int(sys.argv[3]), sys.argv[4]
data = open(src, "rb").read()
assert data[:2] == b"\xff\xd8", "not a JPEG"

# Walk the marker segments after SOI and drop existing Exif APP1 segments.
segments = []
pos = 2
while pos < len(data):
    assert data[pos] == 0xFF
    marker = data[pos + 1]
    if marker == 0xDA:                          # SOS: entropy-coded data follows to EOI
        segments.append(data[pos:])
        break
    length = struct.unpack(">H", data[pos + 2:pos + 4])[0]
    seg = data[pos:pos + 2 + length]
    if not (marker == 0xE1 and seg[4:10] == b"Exif\x00\x00"):
        segments.append(seg)
    pos += 2 + length

date_bytes = captured.encode("ascii") + b"\x00"       # ASCII, NUL-terminated (20 bytes)
ifd0_offset = 8
exif_ifd_offset = ifd0_offset + 2 + 12 * 2 + 4
date_offset = exif_ifd_offset + 2 + 12 * 1 + 4
ifd0 = struct.pack("<H", 2)
ifd0 += struct.pack("<HHIHH", 0x0112, 3, 1, orientation, 0)   # Orientation, SHORT
ifd0 += struct.pack("<HHII", 0x8769, 4, 1, exif_ifd_offset)   # ExifIFD pointer, LONG
ifd0 += struct.pack("<I", 0)
exif_ifd = struct.pack("<H", 1)
exif_ifd += struct.pack("<HHII", 0x9003, 2, len(date_bytes), date_offset)   # DateTimeOriginal, ASCII
exif_ifd += struct.pack("<I", 0)
tiff = b"II*\x00" + struct.pack("<I", ifd0_offset) + ifd0 + exif_ifd + date_bytes
app1 = b"Exif\x00\x00" + tiff
segment = b"\xff\xe1" + struct.pack(">H", len(app1) + 2) + app1

with open(dst, "wb") as f:
    f.write(data[:2] + segment + b"".join(segments))
PY
}

inject_exif "$tmp/plain.jpg" "$out/tiny.jpg" 1 "$captured"
inject_exif "$tmp/plain.jpg" "$tmp/rot6.jpg" 6 "$captured"

# 4. The other formats come from sips. ImageIO carries the EXIF (including orientation 6)
#    across the JPEG -> HEIC conversion.
sips -s format png "$tmp/plain.jpg" --out "$out/tiny.png" > /dev/null
sips -s format gif "$tmp/plain.jpg" --out "$out/tiny.gif" > /dev/null
sips -s format heic "$tmp/rot6.jpg" --out "$out/tiny_rot6.heic" > /dev/null

# 5. Verify. `sips -g all` shows size/format; orientation and DateTimeOriginal are read
#    straight from the embedded EXIF because sips does not report them.
for f in tiny.jpg tiny.png tiny.gif tiny_rot6.heic; do
  sips -g all "$out/$f"
done

python3 -I - "$out/tiny.jpg" 1 "$out/tiny_rot6.heic" 6 <<'PY'
import struct, sys

def exif_fields(path):
    data = open(path, "rb").read()
    # The first "Exif" string in a HEIC is the item type name; take the one that is
    # followed by a TIFF header.
    tiff = -1
    start = data.find(b"Exif\x00\x00")
    while start >= 0:
        if data[start + 6:start + 10] in (b"II*\x00", b"MM\x00*"):
            tiff = start + 6
            break
        start = data.find(b"Exif\x00\x00", start + 1)
    assert tiff >= 0, f"{path}: no EXIF block"
    endian = "<" if data[tiff:tiff + 2] == b"II" else ">"
    fields = {}

    def read_ifd(offset):
        count = struct.unpack(endian + "H", data[tiff + offset:tiff + offset + 2])[0]
        for i in range(count):
            e = tiff + offset + 2 + 12 * i
            tag, typ, n = struct.unpack(endian + "HHI", data[e:e + 8])
            if tag == 0x0112:
                fields["orientation"] = struct.unpack(endian + "H", data[e + 8:e + 10])[0]
            elif tag == 0x9003:
                off = struct.unpack(endian + "I", data[e + 8:e + 12])[0]
                fields["captured"] = data[tiff + off:tiff + off + n - 1].decode("ascii")
            elif tag == 0x8769:
                read_ifd(struct.unpack(endian + "I", data[e + 8:e + 12])[0])

    read_ifd(struct.unpack(endian + "I", data[tiff + 4:tiff + 8])[0])
    return fields

args = sys.argv[1:]
for path, expected in zip(args[::2], args[1::2]):
    f = exif_fields(path)
    print(f"{path}: orientation={f.get('orientation')} DateTimeOriginal={f.get('captured')}")
    assert f.get("orientation") == int(expected), f"{path}: orientation is not {expected}"
    assert f.get("captured") == "2020:01:02 03:04:05", f"{path}: unexpected DateTimeOriginal"
PY

ls -la "$out"
