# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Alex Cooper
"""Author periodic material data for field equipment, independent of scene lighting.

Run with Blender --background --python tools/device-surfaces.py. Each 1024-square
tile represents 64 mm. Base color is neutral sRGB modulation; RGB normals are
linear tangent-space +Y. HDRP masks contain metallic weight / AO / detail mask /
smoothness variation. Runtime remaps metal and smoothness per material.
"""
from pathlib import Path
import importlib.util
import numpy as np

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "src/Modstoke.Client/Assets"
spec = importlib.util.spec_from_file_location("surface_tools", Path(__file__).with_name("aircraft-kestrel-surfaces.py"))
surface = importlib.util.module_from_spec(spec)
spec.loader.exec_module(surface)
surface.SIZE = SIZE = 1024
surface.RNG = np.random.default_rng(3482026)
noise, png, rgba = surface.noise, surface.png, surface.rgba
PERIOD_MM = 64.0


def channels(kind):
    broad, fine = noise(12), noise(512)
    if kind == "polymer":
        grain = .7 * noise(170) + .3 * noise(380)
        pores = np.maximum(-noise(330) - .55, 0) / .45
        height = .006 * grain - .002 * pores + .0008 * fine
        tone = .968 + .016 * grain + .009 * broad - .008 * pores
        smooth = .5 + .16 * grain + .08 * broad - .12 * pores
        ao = 1 - .025 * pores
    elif kind == "rubber":
        grain = noise(240)
        valleys = np.maximum(-noise(90) - .3, 0)
        height = .006 * grain - .003 * valleys + .001 * fine
        tone = .962 + .014 * grain - .016 * valleys + .006 * broad
        smooth = .5 + .13 * grain - .18 * valleys + .07 * broad
        ao = 1 - .028 * valleys
    elif kind == "metal":
        brush = .72 * noise(6, 640) + .28 * noise(17, 330)
        height = .004 * brush + .0006 * fine
        tone = .97 + .020 * brush + .004 * broad
        smooth = .5 + .26 * brush + .06 * broad
        ao = np.ones_like(tone)
    elif kind == "composite":
        u = np.arange(SIZE, dtype=np.float32)[None, :] / SIZE
        v = u.T
        # Fine sealed twill beneath the coating; no fake pits or glowing fibres.
        x, y = u * 64, v * 64
        over = ((np.floor(x) + np.floor(y)) % 4 < 2)
        weave = np.where(over, np.sin(np.pi * (y % 1)), np.sin(np.pi * (x % 1)))
        height = .005 * weave + .003 * noise(280) + .0007 * fine
        tone = .957 + .020 * weave + .007 * broad + .004 * fine
        smooth = .5 + .14 * weave + .06 * broad + .04 * fine
        ao = 1 - .018 * (1 - weave)
    else:
        # Cell bus bars are geometry; this is the smooth protective coating.
        height = .00035 * noise(70) + .0001 * fine
        tone = .985 + .006 * broad + .002 * fine
        smooth = .5 + .045 * broad + .015 * fine
        ao = np.ones_like(tone)
    step = PERIOD_MM / SIZE
    dx = (np.roll(height, -1, 1) - np.roll(height, 1, 1)) / (2 * step)
    dy = (np.roll(height, -1, 0) - np.roll(height, 1, 0)) / (2 * step)
    normal = np.stack((-dx, -dy, np.ones_like(dx)), axis=2)
    normal /= np.linalg.norm(normal, axis=2, keepdims=True)
    return rgba(tone), rgba(normal * .5 + .5), rgba(np.stack((np.ones_like(ao), ao, np.ones_like(ao)), axis=2), smooth)


def main():
    for kind in ("polymer", "rubber", "metal", "composite", "solar"):
        for channel, data in zip(("base", "normal", "mask"), channels(kind)):
            png(OUT / f"device-{kind}-{channel}.png", data, srgb=channel == "base")
        print(f"Authored {kind}: 1024px / 64mm, sRGB base, linear normal and mask", flush=True)


if __name__ == "__main__":
    main()
