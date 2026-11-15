# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Alex Cooper
"""Build the loading screen's island art from photo-mode masters.

Run with python -I tools/build-loading-art.py [--masters DIR] [--sheet PNG]. The
16:9 PNG masters stay outside the repository, listed in loading-art.source.json
beside them with their season (the game's Season names), light (day or night) and
focal point (u from the left, v from the top). Each one gets the shared grade, is
box-filtered in linear light to 2560x1440 and 1280x720 and written as a two-level
BC7 sRGB DDS; loading-art.json lists them for the Loader. Every file is decoded
back and compared with what went into the encoder before the list is written.
"""
import argparse
import json
import struct
from pathlib import Path

import etcpak
import numpy as np
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
MASTERS = ROOT.parent / "Art/Loading/masters"
OUT = ROOT / "src/Modstoke.Loader/Art/Loading"
LEVELS = ((2560, 1440), (1280, 720))
SEASONS = ("Spring", "Summer", "Fall", "Winter")
LIGHTS = ("day", "night")
MIN_PSNR = 36.0

# Photo-mode captures leave the game's tonemapping already dark (encoded medians 0.16-0.34), so the set gets no
# exposure cut: -0.15 EV made the rain and mist shots muddy under the screen's veils. Saturation drops 8 % in
# linear light, then a soft S-curve on the encoded values, pivoted at 0.4, adds 14 % mid-tone contrast and rolls
# off the brightest sky a little.
SATURATION = 0.92
CONTRAST, PIVOT = 2.5, 0.40
LUMA = np.array([0.2126, 0.7152, 0.0722], np.float32)

DDSD_FLAGS = 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000  # caps, height, width, pixel format, mip count, linear size
DDPF_FOURCC = 0x4
DDSCAPS = 0x1000 | 0x8 | 0x400000  # texture, complex, mipmap
DXGI_FORMAT_BC7_UNORM_SRGB = 99
TEXTURE2D = 3
# Opaque input still decodes to alpha 254 in some of etcpak's mode 6 blocks (the p-bit is shared with colour),
# so the header marks alpha as unused.
ALPHA_MODE_OPAQUE = 3
HEADER = struct.Struct("<4s7I44x2I4s5I5I5I")


def to_linear(s):
    return np.where(s <= 0.04045, s / 12.92, ((s + 0.055) / 1.055) ** 2.4).astype(np.float32)


def to_srgb(linear):
    linear = np.clip(linear, 0, 1)
    return np.where(linear <= 0.0031308, linear * 12.92, 1.055 * linear ** (1 / 2.4) - 0.055).astype(np.float32)


def sigmoid(x):
    return 1 / (1 + np.exp(-x))


SRGB_TO_LINEAR = to_linear(np.arange(256, dtype=np.float32) / 255)


def grade(rgb):
    linear = SRGB_TO_LINEAR[rgb]
    luma = (linear @ LUMA)[..., None]
    encoded = to_srgb(luma + (linear - luma) * SATURATION)
    low, high = sigmoid(-CONTRAST * PIVOT), sigmoid(CONTRAST * (1 - PIVOT))
    return to_linear((sigmoid(CONTRAST * (encoded - PIVOT)) - low) / (high - low))


def downsample(linear, size):
    channels = [np.asarray(Image.fromarray(np.ascontiguousarray(linear[..., c])).resize(size, Image.BOX))
                for c in range(3)]
    return (to_srgb(np.dstack(channels)) * 255 + 0.5).astype(np.uint8)


def bc7(rgb):
    height, width, _ = rgb.shape
    rgba = np.dstack((rgb, np.full((height, width), 255, np.uint8)))
    params = etcpak.BC7CompressBlockParams()
    params.init_perceptual_weights()
    return etcpak.compress_bc7(rgba.tobytes(), width, height, params)


def level_size(width, height):
    return max(1, (width + 3) // 4) * max(1, (height + 3) // 4) * 16


def dds(levels):
    width, height = LEVELS[0]
    header = HEADER.pack(b"DDS ", 124, DDSD_FLAGS, height, width, level_size(width, height), 0, len(levels),
                         32, DDPF_FOURCC, b"DX10", 0, 0, 0, 0, 0,
                         DDSCAPS, 0, 0, 0, 0,
                         DXGI_FORMAT_BC7_UNORM_SRGB, TEXTURE2D, 0, 1, ALPHA_MODE_OPAQUE)
    return header + b"".join(levels)


def read_dds(path):
    data = path.read_bytes()
    fields = HEADER.unpack_from(data)
    magic, size, flags, height, width, linear_size, _, mips = fields[:8]
    fourcc, dxgi, dimension, array_size = fields[10], fields[21], fields[22], fields[24]
    if (magic, size, flags, fourcc, dxgi, dimension, array_size) != (b"DDS ", 124, DDSD_FLAGS, b"DX10",
                                                                     DXGI_FORMAT_BC7_UNORM_SRGB, TEXTURE2D, 1):
        raise SystemExit(f"{path.name}: unexpected DDS header")
    if (width, height) != LEVELS[0] or mips != len(LEVELS) or linear_size != level_size(width, height):
        raise SystemExit(f"{path.name}: {width}x{height}, {mips} levels, linear size {linear_size}")
    offset, decoded = HEADER.size, []
    for w, h in LEVELS:
        length = level_size(w, h)
        decoded.append(np.asarray(Image.frombytes("RGBA", (w, h), data[offset:offset + length], "bcn", (7,)))[..., :3])
        offset += length
    if offset != len(data):
        raise SystemExit(f"{path.name}: {len(data) - offset} bytes past the last level")
    # Pillow's own DDS reader must agree on the top level, so the header works outside this script too.
    with Image.open(path) as other:
        if not np.array_equal(np.asarray(other.convert("RGB")), decoded[0]):
            raise SystemExit(f"{path.name}: Pillow reads a different top level")
    return decoded


def psnr(a, b):
    mse = np.mean((a.astype(np.float64) - b) ** 2)
    return 10 * np.log10(255 ** 2 / mse) if mse else float("inf")


def load_sources(masters):
    entries = json.loads((masters / "loading-art.source.json").read_text(encoding="utf-8"))["art"]
    for entry in entries:
        name = entry["file"]
        focus = entry["focus"]
        if entry["season"] not in SEASONS or entry["light"] not in LIGHTS:
            raise SystemExit(f"{name}: season must be one of {SEASONS} and light one of {LIGHTS}")
        if len(focus) != 2 or not all(0 <= v <= 1 for v in focus):
            raise SystemExit(f"{name}: focus must be [u, v] within 0..1")
    return entries


def contact_sheet(rows, path):
    tile_w, tile_h, crop = 960, 540, 540
    font = ImageFont.load_default(20)
    sheet = Image.new("RGB", (tile_w * 2 + crop, tile_h * len(rows)), (5, 6, 6))
    for i, (entry, decoded, scores) in enumerate(rows):
        top = Image.fromarray(decoded[0])
        u, v = entry["focus"]
        view = top.resize((tile_w, tile_h), Image.BOX)
        draw = ImageDraw.Draw(view)
        x, y = u * tile_w, v * tile_h
        draw.line((x - 14, y, x + 14, y), fill=(217, 38, 44), width=2)
        draw.line((x, y - 14, x, y + 14), fill=(217, 38, 44), width=2)
        draw.text((12, 10), f"{entry['file']}  {entry['season']} {entry['light']}  focus {u:.2f}, {v:.2f}  "
                            f"PSNR {scores[0]:.1f} / {scores[1]:.1f} dB", font=font, fill=(236, 230, 214),
                  stroke_width=2, stroke_fill=(0, 0, 0))
        left = min(max(int(u * top.width) - crop // 2, 0), top.width - crop)
        upper = min(max(int(v * top.height) - crop // 2, 0), top.height - crop)
        sheet.paste(view, (0, i * tile_h))
        sheet.paste(Image.fromarray(decoded[1]).resize((tile_w, tile_h), Image.BOX), (tile_w, i * tile_h))
        sheet.paste(top.crop((left, upper, left + crop, upper + crop)), (tile_w * 2, i * tile_h))
    sheet.save(path)


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--masters", type=Path, default=MASTERS)
    parser.add_argument("--out", type=Path, default=OUT)
    parser.add_argument("--sheet", type=Path, help="also save a contact sheet of the decoded files")
    args = parser.parse_args()

    entries = load_sources(args.masters)
    args.out.mkdir(parents=True, exist_ok=True)
    art, rows = [], []
    for entry in entries:
        with Image.open(args.masters / entry["file"]) as master:
            if master.width * 9 != master.height * 16 or master.width < LEVELS[0][0]:
                raise SystemExit(f"{entry['file']}: {master.width}x{master.height} is not a 16:9 master")
            linear = grade(np.asarray(master.convert("RGB")))
        sources = [downsample(linear, size) for size in LEVELS]
        target = args.out / (Path(entry["file"]).stem + ".dds")
        target.write_bytes(dds([bc7(rgb) for rgb in sources]))
        decoded = read_dds(target)
        scores = [psnr(d, s) for d, s in zip(decoded, sources)]
        if min(scores) < MIN_PSNR:
            raise SystemExit(f"{target.name}: decoded PSNR {min(scores):.1f} dB is below {MIN_PSNR} dB")
        print(f"{target.name}: {target.stat().st_size / 2 ** 20:.2f} MiB, "
              f"PSNR {' / '.join(f'{s:.2f}' for s in scores)} dB", flush=True)
        art.append({"file": target.name, "season": entry["season"], "light": entry["light"],
                    "focus": entry["focus"]})
        rows.append((entry, decoded, scores))

    kept = {item["file"] for item in art}
    for stale in args.out.glob("*.dds"):
        if stale.name not in kept:
            stale.unlink()
            print(f"Removed {stale.name}, no longer in the source list")
    lines = ",\n".join("    " + json.dumps(item) for item in art)
    (args.out / "loading-art.json").write_text('{\n  "version": 1,\n  "art": [\n' + lines + "\n  ]\n}\n",
                                               encoding="utf-8", newline="\n")
    total = sum((args.out / item["file"]).stat().st_size for item in art)
    print(f"Wrote {len(art)} pictures, {total / 2 ** 20:.1f} MiB, to {args.out}")
    if args.sheet:
        contact_sheet(rows, args.sheet)
        print(f"Contact sheet: {args.sheet}")


if __name__ == "__main__":
    main()
