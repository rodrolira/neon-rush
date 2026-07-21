"""
Procedural PBR textures for Neon Rush.

Three sets, all tiling, all generated rather than painted so they can be
regenerated at any resolution without an artist round-trip:

  TEX_Road_*      the asphalt panel under the track
  TEX_Facade_*    the window grid on the skyline buildings
  TEX_CoinFace_*  the struck face of the coin

Channel packing follows the glTF spec (ORM: occlusion=R, roughness=G,
metallic=B) so Unity's glTF importers wire them up with no manual remapping.
Sizes are deliberately small — 512 for surfaces the player never gets closer
than a few metres to, 256 for the coin. On a low-end Android GPU the texture
cache is the scarce resource, not the disk.
"""

import numpy as np
from PIL import Image


def _save(arr, path):
    Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8)).save(path, optimize=True)


def _grid_mask(size, cells, line_px):
    """A tiling grid of lines. Seamless because the modulo wraps."""
    step = size // cells
    xs = np.arange(size)
    line = ((xs % step) < line_px).astype(float)
    return np.maximum(line[None, :], line[:, None])


def road(out_dir, size=512):
    """Dark asphalt with a faint hex-panel seam and a scatter of specks.

    The seam is what stops a 30 m slab from reading as a flat black rectangle
    at speed; it gives the eye something to measure motion against without
    competing with the lane markers for attention.
    """
    rng = np.random.default_rng(7)

    base = np.full((size, size, 3), 16.0)
    base += rng.normal(0, 4.0, (size, size, 1))          # fine grain
    seam = _grid_mask(size, 4, 3) * 14.0                 # panel seams
    base += seam[..., None] * np.array([0.6, 0.5, 1.0])

    specks = (rng.random((size, size)) > 0.9985).astype(float) * 60.0
    base += specks[..., None] * np.array([0.5, 1.0, 0.9])  # cyan glints

    _save(base, f"{out_dir}/TEX_Road_BaseColor.png")

    # Occlusion flat, roughness high and slightly varied, metallic zero.
    orm = np.zeros((size, size, 3))
    orm[..., 0] = 255                                     # AO
    orm[..., 1] = 205 + rng.normal(0, 8, (size, size))    # roughness
    orm[..., 2] = 0                                       # metallic
    orm[..., 1] -= seam * 40                              # seams read wetter
    _save(orm, f"{out_dir}/TEX_Road_ORM.png")

    # Only the specks and seams emit, and only faintly.
    em = seam[..., None] * np.array([0.10, 0.08, 0.30]) * 255
    em += specks[..., None] * np.array([0.2, 1.0, 0.85]) * 255
    _save(em, f"{out_dir}/TEX_Road_Emissive.png")


def facade(out_dir, size=512):
    """Building facade: a dark slab with lit windows.

    Windows are lit stochastically at ~35% with three tints drawn from the
    skyline palette. A fully lit facade looks like a spreadsheet; a sparsely
    lit one reads as a city at night from two hundred metres away, which is
    the only distance the player ever sees it from.
    """
    rng = np.random.default_rng(19)
    cols, rows = 8, 16
    cw, ch = size // cols, size // rows

    base = np.full((size, size, 3), 12.0)
    base[..., 2] += 6.0                                   # the violet cast
    em = np.zeros((size, size, 3))

    tints = np.array([[0.20, 1.00, 0.85],                 # cyan
                      [1.00, 0.25, 0.75],                 # pink
                      [0.55, 0.35, 1.00]])                # lilac

    for r in range(rows):
        for c in range(cols):
            y0, x0 = r * ch + ch // 4, c * cw + cw // 4
            y1, x1 = y0 + ch // 2, x0 + cw // 2
            if rng.random() < 0.35:
                tint = tints[rng.integers(0, 3)]
                gain = 0.45 + rng.random() * 0.55
                base[y0:y1, x0:x1] = tint * 90 * gain
                em[y0:y1, x0:x1] = tint * 255 * gain
            else:
                base[y0:y1, x0:x1] = 22.0                 # dark glass

    _save(base, f"{out_dir}/TEX_Facade_BaseColor.png")
    _save(em, f"{out_dir}/TEX_Facade_Emissive.png")

    orm = np.zeros((size, size, 3))
    orm[..., 0] = 255
    orm[..., 1] = 150                                     # glassy-ish concrete
    orm[..., 2] = 30
    _save(orm, f"{out_dir}/TEX_Facade_ORM.png")


def coin_face(out_dir, size=256):
    """The struck face of the coin: a rim ring and a centred bolt glyph.

    Radial, so it survives the coin's bob and any spin without a seam showing.
    """
    yy, xx = np.mgrid[0:size, 0:size]
    cx = cy = (size - 1) / 2
    r = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2) / (size / 2)

    gold = np.array([1.00, 0.82, 0.25])
    base = np.ones((size, size, 3)) * gold * 210

    rim = ((r > 0.80) & (r < 0.94)).astype(float)         # raised outer ring
    inner = (r < 0.62).astype(float)                      # recessed field
    base -= inner[..., None] * gold * 55
    base += rim[..., None] * gold * 45

    # A lightning-bolt glyph: two mirrored slanted bars. Neon Rush's mark.
    u, v = (xx - cx) / (size / 2), (yy - cy) / (size / 2)
    bolt = ((np.abs(u - v * 0.55) < 0.13) & (np.abs(v) < 0.42)).astype(float)
    bolt *= (r < 0.60)
    base = base * (1 - bolt[..., None]) + bolt[..., None] * np.array([255, 250, 230])

    outside = (r > 1.0)[..., None]
    base = base * (1 - outside)
    _save(base, f"{out_dir}/TEX_CoinFace_BaseColor.png")

    em = (rim[..., None] * gold * 255) + (bolt[..., None] * np.array([255, 245, 215]))
    _save(em, f"{out_dir}/TEX_CoinFace_Emissive.png")

    orm = np.zeros((size, size, 3))
    orm[..., 0] = 255
    orm[..., 1] = 60 + inner * 40                          # polished metal
    orm[..., 2] = 235                                      # metallic
    _save(orm, f"{out_dir}/TEX_CoinFace_ORM.png")


def build_all(out_dir):
    road(out_dir)
    facade(out_dir)
    coin_face(out_dir)
    return [
        "TEX_Road_BaseColor.png", "TEX_Road_ORM.png", "TEX_Road_Emissive.png",
        "TEX_Facade_BaseColor.png", "TEX_Facade_ORM.png", "TEX_Facade_Emissive.png",
        "TEX_CoinFace_BaseColor.png", "TEX_CoinFace_ORM.png", "TEX_CoinFace_Emissive.png",
    ]
