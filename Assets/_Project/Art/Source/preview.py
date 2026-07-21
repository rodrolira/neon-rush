"""
Software renderer for the contact sheet.

A z-buffered triangle rasteriser in numpy. There is no GPU in this sandbox and
no display, so this exists purely so the models can be *looked at* rather than
signed off on bounding-box numbers alone — a mesh can pass every dimensional
check and still be an unrecognisable tangle.

Shading approximates what the game does: a key light, a rim light, and an
emissive term added flat, viewed against the NeonAtmosphere horizon colour.
"""

import os
import sys

import numpy as np
import trimesh
from PIL import Image, ImageDraw

from neonlib import base_rgb

HORIZON = np.array([0.03, 0.015, 0.08])       # NeonAtmosphere.Horizon


def look_at(eye, target, up=(0, 1, 0)):
    eye, target, up = map(lambda v: np.asarray(v, float), (eye, target, up))
    f = target - eye
    f /= np.linalg.norm(f)
    s = np.cross(f, up)
    s /= np.linalg.norm(s)
    u = np.cross(s, f)
    m = np.eye(4)
    m[0, :3], m[1, :3], m[2, :3] = s, u, -f
    m[:3, 3] = -m[:3, :3] @ eye
    return m


def render_camera(parts, size=700, eye=(0, 3, -6), target=(0, 1.5, 12), fov=46.0):
    """Renders from an explicit eye/target, for the assembled scene check."""
    return _raster(parts, size, fov, look_at(eye, target))


def render(parts, size=340, fov=32.0, azimuth=35.0, elevation=22.0):
    """parts: list of (trimesh, base_colour, emission). Returns an RGB array."""
    tris, cols, ems, norms = [], [], [], []
    for mesh, colour, emission in parts:
        v = mesh.vertices[mesh.faces]
        tris.append(v)
        cols.append(np.repeat(np.array(colour)[None], len(v), axis=0))
        ems.append(np.full(len(v), emission))
        norms.append(mesh.face_normals)
    tris = np.concatenate(tris)
    cols = np.concatenate(cols)
    ems = np.concatenate(ems)
    norms = np.concatenate(norms)

    allv = tris.reshape(-1, 3)
    centre = (allv.min(0) + allv.max(0)) / 2
    radius = np.linalg.norm(allv.max(0) - allv.min(0)) / 2 or 1.0

    a, e = np.radians(azimuth), np.radians(elevation)
    dist = radius / np.tan(np.radians(fov / 2)) * 1.30
    eye = centre + dist * np.array([np.cos(e) * np.sin(a), np.sin(e), np.cos(e) * np.cos(a)])

    return _raster(parts, size, fov, look_at(eye, centre))


def _raster(parts, size, fov, view):
    tris, cols, ems, norms = [], [], [], []
    for mesh, colour, emission in parts:
        v = mesh.vertices[mesh.faces]
        tris.append(v)
        cols.append(np.repeat(np.array(colour)[None], len(v), axis=0))
        ems.append(np.full(len(v), emission))
        norms.append(mesh.face_normals)
    tris = np.concatenate(tris); cols = np.concatenate(cols)
    ems = np.concatenate(ems); norms = np.concatenate(norms)

    vt = (view[:3, :3] @ tris.reshape(-1, 3).T).T + view[:3, 3]
    vt = vt.reshape(-1, 3, 3)

    f = 1.0 / np.tan(np.radians(fov / 2))
    z = -vt[..., 2]
    z = np.maximum(z, 1e-4)
    px = (vt[..., 0] * f / z * 0.5 + 0.5) * size
    py = (1 - (vt[..., 1] * f / z * 0.5 + 0.5)) * size

    # Lighting in view space so the key follows the camera, as a rig light would.
    n = (view[:3, :3] @ norms.T).T
    key = np.clip(n @ np.array([0.45, 0.75, 0.50]), 0, 1)
    rim = np.clip(1.0 - np.abs(n[:, 2]), 0, 1) ** 2.5
    shade = 0.14 + 0.62 * key[:, None] + 0.30 * rim[:, None]
    # Emission is tone-mapped rather than added raw: the in-engine value goes up
    # to 2.2, which added flat would clip every lit surface to white and hide
    # exactly the shape detail this preview exists to check.
    em_term = (ems[:, None] / (1.0 + ems[:, None])) * 0.55
    rgb = np.clip(cols * shade + cols * em_term, 0, 1)

    buf = np.tile(HORIZON, (size, size, 1))
    zbuf = np.full((size, size), np.inf)

    order = np.argsort(-z.mean(1))                      # far to near
    for i in order:
        if n[i, 2] <= 0 and ems[i] < 0.05:
            continue                                     # cheap backface cull
        x0, x1 = px[i], py[i]
        minx, maxx = int(max(0, np.floor(x0.min()))), int(min(size - 1, np.ceil(x0.max())))
        miny, maxy = int(max(0, np.floor(x1.min()))), int(min(size - 1, np.ceil(x1.max())))
        if minx > maxx or miny > maxy:
            continue
        xs = np.arange(minx, maxx + 1)
        ys = np.arange(miny, maxy + 1)
        gx, gy = np.meshgrid(xs, ys)
        ax, ay = x0[0], x1[0]
        bx, by = x0[1], x1[1]
        cx, cy = x0[2], x1[2]
        den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy)
        if abs(den) < 1e-9:
            continue
        w0 = ((by - cy) * (gx - cx) + (cx - bx) * (gy - cy)) / den
        w1 = ((cy - ay) * (gx - cx) + (ax - cx) * (gy - cy)) / den
        w2 = 1 - w0 - w1
        inside = (w0 >= 0) & (w1 >= 0) & (w2 >= 0)
        if not inside.any():
            continue
        depth = w0 * z[i, 0] + w1 * z[i, 1] + w2 * z[i, 2]
        sub = zbuf[miny:maxy + 1, minx:maxx + 1]
        win = inside & (depth < sub)
        if not win.any():
            continue
        sub[win] = depth[win]
        buf[miny:maxy + 1, minx:maxx + 1][win] = rgb[i]

    # A soft bloom pass, matching the project's threshold-0.9 URP bloom.
    img = Image.fromarray((np.clip(buf, 0, 1) * 255).astype(np.uint8))
    bright = np.clip((np.asarray(img, float) / 255 - 0.72) * 1.6, 0, 1)
    from PIL import ImageFilter
    glow = Image.fromarray((bright * 255).astype(np.uint8)).filter(
        ImageFilter.GaussianBlur(size / 55))
    out = np.clip(np.asarray(img, float) + np.asarray(glow, float) * 0.40, 0, 255)
    return out.astype(np.uint8)


def parts_of(model, materials):
    out = []
    for _, mesh, mat_key in model.parts:
        mat = materials[mat_key]
        colour = base_rgb(mat)
        out.append((mesh, colour, float(getattr(mat, "neon_emission", 0.0))))
    return out


def sheet(models, materials, path, cols=5, cell=340, azimuth=35, elevation=22, label=True):
    rows = (len(models) + cols - 1) // cols
    pad = 8
    sheet_img = Image.new("RGB", (cols * (cell + pad) + pad, rows * (cell + pad + 22) + pad),
                          tuple((HORIZON * 255 * 0.6).astype(int)))
    draw = ImageDraw.Draw(sheet_img)
    for i, m in enumerate(models):
        r, c = divmod(i, cols)
        arr = render(parts_of(m, materials), size=cell, azimuth=azimuth, elevation=elevation)
        x = pad + c * (cell + pad)
        y = pad + r * (cell + pad + 22)
        sheet_img.paste(Image.fromarray(arr), (x, y))
        if label:
            e = m.extents
            draw.text((x + 4, y + cell + 4),
                      f"{m.name}   {e[0]:.2f}x{e[1]:.2f}x{e[2]:.2f}m  {m.triangles}t",
                      fill=(170, 190, 220))
    sheet_img.save(path)
    return path
