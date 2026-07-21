"""
neonlib — shared geometry + material toolkit for the Neon Rush art pass.

Design rules encoded here (see ART_README.md for the reasoning):
  * glTF convention: Y up, forward = -Z. Unity's importers flip Z, so a model
    authored facing -Z here arrives in Unity facing +Z (the run direction).
  * One metre = one unit. Every dimension matches the gameplay constants in
    RunTuning / ObstacleArchetype exactly.
  * Materials are shared instances, created once and reused across every model,
    so an importer that merges by material name collapses them into one.
  * Chamfers instead of bevels: three extra tris per corner buys the specular
    highlight that makes a low-poly shape read as solid under bloom.
"""

import numpy as np
import trimesh
from trimesh.visual.material import PBRMaterial

# ---------------------------------------------------------------------------
# Palette — lifted verbatim from NeonMaterials.cs / TrackStreamer.cs so the
# models cannot drift from the code-driven greybox they replace.
# ---------------------------------------------------------------------------

PALETTE = {
    "cyan":     (0.20, 1.00, 0.85),   # NeonMaterials.Player, Magnet pickup
    "magenta":  (1.00, 0.18, 0.45),   # NeonMaterials.Obstacle — danger
    "gold":     (1.00, 0.82, 0.25),   # NeonMaterials.Coin, DoubleScore
    "violet":   (0.35, 0.25, 0.85),   # NeonMaterials.LaneLine
    "asphalt":  (0.06, 0.06, 0.10),   # NeonMaterials.Road
    "blue":     (0.35, 0.60, 1.00),   # Shield pickup
    "pink":     (1.00, 0.25, 0.75),   # skyline accent
    "lilac":    (0.55, 0.35, 1.00),   # skyline accent
    "chassis":  (0.05, 0.04, 0.11),   # building body, unlit structure
    "steel":    (0.22, 0.23, 0.30),   # armour plate, non-emissive
    "void":     (0.10, 0.02, 0.20),   # Nova — Void skin base
    "white":    (0.92, 0.96, 1.00),
}


def _mat(name, colour, emission=0.0, metallic=0.0, roughness=0.55, alpha=1.0):
    """A PBR material. `emission` is the multiplier applied to the base colour.

    Emissive strength above 1 is what pushes a surface past the bloom threshold
    (0.9 in NeonAtmosphere). glTF clamps emissiveFactor to [0,1], so anything
    hotter than that is carried as a KHR_materials_emissive_strength extension
    value recorded in the manifest for the Unity-side material to apply.
    """
    c = np.array(colour, dtype=float)
    emissive = np.clip(c * min(emission, 1.0), 0, 1) if emission > 0 else np.zeros(3)
    m = PBRMaterial(
        name=name,
        baseColorFactor=np.concatenate([c, [alpha]]),
        emissiveFactor=emissive,
        metallicFactor=metallic,
        roughnessFactor=roughness,
        alphaMode="BLEND" if alpha < 1.0 else "OPAQUE",
        doubleSided=False,
    )
    m.neon_emission = emission  # carried into the manifest, not into the glTF
    return m


# --- The shared material library. Twelve materials cover all 27 models. ------

MATERIALS = {
    "MAT_Neon_Cyan":      _mat("MAT_Neon_Cyan", PALETTE["cyan"], emission=1.6, roughness=0.35),
    "MAT_Neon_Magenta":   _mat("MAT_Neon_Magenta", PALETTE["magenta"], emission=1.6, roughness=0.35),
    "MAT_Neon_Gold":      _mat("MAT_Neon_Gold", PALETTE["gold"], emission=1.6, metallic=0.85, roughness=0.25),
    "MAT_Neon_Violet":    _mat("MAT_Neon_Violet", PALETTE["violet"], emission=1.4, roughness=0.40),
    "MAT_Neon_Blue":      _mat("MAT_Neon_Blue", PALETTE["blue"], emission=2.2, roughness=0.30),
    "MAT_Neon_Pink":      _mat("MAT_Neon_Pink", PALETTE["pink"], emission=1.8, roughness=0.35),
    "MAT_Neon_Lilac":     _mat("MAT_Neon_Lilac", PALETTE["lilac"], emission=1.8, roughness=0.35),
    "MAT_Neon_White":     _mat("MAT_Neon_White", PALETTE["white"], emission=1.2, roughness=0.30),
    "MAT_Dark_Asphalt":   _mat("MAT_Dark_Asphalt", PALETTE["asphalt"], emission=0.0, roughness=0.85),
    "MAT_Dark_Chassis":   _mat("MAT_Dark_Chassis", PALETTE["chassis"], emission=0.0, roughness=0.75),
    "MAT_Metal_Steel":    _mat("MAT_Metal_Steel", PALETTE["steel"], emission=0.0, metallic=0.75, roughness=0.40),
    "MAT_Dark_Void":      _mat("MAT_Dark_Void", PALETTE["void"], emission=0.35, roughness=0.30),
    "MAT_Glass_Tint":     _mat("MAT_Glass_Tint", PALETTE["blue"], emission=0.9, roughness=0.10, alpha=0.45),
}


def M(key):
    return MATERIALS[key]


def base_rgb(material):
    """Base colour as float RGB in 0..1.

    trimesh stores baseColorFactor as uint8 RGBA regardless of what it was
    constructed with. Reading it back as though it were the 0..1 float that
    went in yields values around 255, which clip to white — silently, and only
    visibly once something tries to render or report the colour.
    """
    c = np.asarray(material.baseColorFactor)
    c = c.astype(float)
    if c.max() > 1.0:
        c = c / 255.0
    return c[:3]


def base_alpha(material):
    c = np.asarray(material.baseColorFactor).astype(float)
    return float(c[3] / 255.0 if c.max() > 1.0 else c[3])


# ---------------------------------------------------------------------------
# Geometry primitives
# ---------------------------------------------------------------------------

def box(size, centre=(0, 0, 0)):
    """Axis-aligned box. 12 tris. The workhorse."""
    m = trimesh.creation.box(extents=np.asarray(size, dtype=float))
    m.apply_translation(np.asarray(centre, dtype=float))
    return m


def chamfer_box(size, chamfer=0.04, centre=(0, 0, 0)):
    """Box with its corners cut back — the convex hull of every corner vertex
    pulled inward along each of the three axes in turn.

    Twenty-four points, hulled, gives 44 tris versus the box's 12. That is a
    deliberate spend: a hard-edged cube under strong bloom reads as a flat
    silhouette, whereas the chamfer catches a rim highlight and the shape reads
    as an object with volume. Applied only where the player looks: obstacles,
    characters, pickups. Never on the road or the skyline.
    """
    sx, sy, sz = (np.asarray(size, dtype=float) * 0.5)
    c = min(chamfer, sx * 0.49, sy * 0.49, sz * 0.49)
    pts = []
    for ix in (-1, 1):
        for iy in (-1, 1):
            for iz in (-1, 1):
                p = np.array([ix * sx, iy * sy, iz * sz])
                for axis in range(3):
                    q = p.copy()
                    q[axis] -= np.sign(q[axis]) * c
                    pts.append(q)
    m = trimesh.Trimesh(vertices=np.array(pts)).convex_hull
    m.apply_translation(np.asarray(centre, dtype=float))
    return m


def _align(mesh, axis):
    """Reorients a Z-aligned solid onto `axis`.

    trimesh builds cylinders along Z, not Y. Getting this backwards silently
    produces a coin lying flat on the road instead of facing the camera, which
    is exactly the kind of error that survives to the device, so the conversion
    lives in one place and every caller goes through it.
    """
    if axis == "y":
        mesh.apply_transform(trimesh.transformations.rotation_matrix(np.pi / 2, [1, 0, 0]))
    elif axis == "x":
        mesh.apply_transform(trimesh.transformations.rotation_matrix(np.pi / 2, [0, 1, 0]))
    return mesh


def cyl(radius, height, sections=16, axis="y", centre=(0, 0, 0)):
    """Cylinder. `axis` names the axis the cylinder runs along."""
    m = trimesh.creation.cylinder(radius=radius, height=height, sections=sections)
    _align(m, axis)
    m.apply_translation(np.asarray(centre, dtype=float))
    return m


def beveled_disc(radius, thickness, bevel=0.25, sections=20, axis="z", centre=(0, 0, 0)):
    """A disc with a chamfered rim: the hull of a wide-thin and a narrow-thick
    cylinder. This is the coin. A flat cylinder spins into an invisible edge-on
    line; the bevel keeps a lit sliver visible through the whole rotation, which
    is what makes a spinning coin read as a coin rather than as a flicker.
    """
    a = trimesh.creation.cylinder(radius=radius, height=thickness * (1 - bevel), sections=sections)
    b = trimesh.creation.cylinder(radius=radius * (1 - bevel * 0.35), height=thickness, sections=sections)
    m = trimesh.util.concatenate([a, b]).convex_hull
    _align(m, axis)
    m.apply_translation(np.asarray(centre, dtype=float))
    return m


def prism(radius, height, sides=6, axis="y", centre=(0, 0, 0), twist=0.0):
    """Regular n-gon prism. Hexagons everywhere: cheapest shape that stops
    reading as 'a cylinder Unity gave me for free'."""
    m = trimesh.creation.cylinder(radius=radius, height=height, sections=sides)
    if twist:
        m.apply_transform(trimesh.transformations.rotation_matrix(twist, [0, 0, 1]))
    _align(m, axis)
    m.apply_translation(np.asarray(centre, dtype=float))
    return m


def octahedron(size, centre=(0, 0, 0)):
    """Eight tris. The gem, and the core of the shield pickup."""
    sx, sy, sz = np.asarray(size, dtype=float) * 0.5
    v = np.array([[sx, 0, 0], [-sx, 0, 0], [0, sy, 0], [0, -sy, 0], [0, 0, sz], [0, 0, -sz]])
    f = np.array([[0, 2, 4], [2, 1, 4], [1, 3, 4], [3, 0, 4],
                  [2, 0, 5], [1, 2, 5], [3, 1, 5], [0, 3, 5]])
    m = trimesh.Trimesh(vertices=v, faces=f)
    m.apply_translation(np.asarray(centre, dtype=float))
    return m


def pyramid(base, height, centre=(0, 0, 0)):
    """Square-based pyramid, base sitting on the local XZ plane."""
    b = base * 0.5
    v = np.array([[-b, 0, -b], [b, 0, -b], [b, 0, b], [-b, 0, b], [0, height, 0]])
    f = np.array([[0, 1, 4], [1, 2, 4], [2, 3, 4], [3, 0, 4], [0, 3, 2], [0, 2, 1]])
    m = trimesh.Trimesh(vertices=v, faces=f)
    m.apply_translation(np.asarray(centre, dtype=float))
    return m


def arrow_plate(width, height, thickness, centre=(0, 0, 0), point="+x"):
    """A flat triangular plate lying in the signage plane, apex along `point`.

    A rotated pyramid was the obvious way to get an arrow and it was wrong: a
    pyramid is deep along the axis it points, so laying one on a 2.5 cm signage
    plane either buries it inside the faceplate or pushes it out past the
    obstacle's depth limit. A plate is 8 tris, sits flush, and cannot overflow.
    """
    w, h, t = width * 0.5, height * 0.5, thickness * 0.5
    if point in ("+x", "-x"):
        s_ = 1.0 if point == "+x" else -1.0
        tip = np.array([s_ * w, 0.0])
        a, b = np.array([-s_ * w, h]), np.array([-s_ * w, -h])
    else:
        s_ = 1.0 if point == "+y" else -1.0
        tip = np.array([0.0, s_ * h])
        a, b = np.array([-w, -s_ * h]), np.array([w, -s_ * h])
    front = [np.array([p[0], p[1], t]) for p in (tip, a, b)]
    back = [np.array([p[0], p[1], -t]) for p in (tip, a, b)]
    v = np.array(front + back)
    f = np.array([[0, 1, 2], [5, 4, 3],
                  [0, 3, 4], [0, 4, 1],
                  [1, 4, 5], [1, 5, 2],
                  [2, 5, 3], [2, 3, 0]])
    m = trimesh.Trimesh(vertices=v, faces=f)
    m.apply_translation(np.asarray(centre, dtype=float))
    return m


def wedge(size, centre=(0, 0, 0)):
    """A box with the top -Z edge sliced off: the angled fairing used on the
    characters' shoulders and the boards' nose."""
    sx, sy, sz = np.asarray(size, dtype=float) * 0.5
    v = np.array([
        [-sx, -sy, -sz], [sx, -sy, -sz], [sx, -sy, sz], [-sx, -sy, sz],
        [-sx, sy, sz * 0.2], [sx, sy, sz * 0.2], [sx, sy, sz], [-sx, sy, sz],
    ])
    f = np.array([[0, 1, 2], [0, 2, 3], [4, 6, 5], [4, 7, 6], [0, 4, 5], [0, 5, 1],
                  [3, 2, 6], [3, 6, 7], [0, 3, 7], [0, 7, 4], [1, 5, 6], [1, 6, 2]])
    m = trimesh.Trimesh(vertices=v, faces=f)
    m.apply_translation(np.asarray(centre, dtype=float))
    return m


def mirror_x(mesh):
    """Mirrored copy across the YZ plane. Every limb is modelled once."""
    m = mesh.copy()
    m.apply_transform(np.diag([-1.0, 1.0, 1.0, 1.0]))
    m.invert()
    return m


def rot(mesh, degrees, axis, about=(0, 0, 0)):
    m = mesh.copy()
    m.apply_transform(trimesh.transformations.rotation_matrix(
        np.radians(degrees), axis, about))
    return m


# ---------------------------------------------------------------------------
# Scene assembly
# ---------------------------------------------------------------------------

class Model:
    """One deliverable: a named scene of (mesh, material) parts."""

    def __init__(self, name, category, function, pivot, notes=""):
        self.name = name
        self.category = category
        self.function = function
        self.pivot = pivot
        self.notes = notes
        self.parts = []

    def add(self, mesh, material_key, part_name=None):
        mesh = mesh.copy()
        mesh.visual = trimesh.visual.TextureVisuals(material=M(material_key))
        self.parts.append((part_name or f"{self.name}_p{len(self.parts)}", mesh, material_key))
        return self

    def add_uv(self, mesh, material_key, part_name=None, uv_scale=1.0):
        """Adds a part with planar box UVs, so a tiling texture lands on it."""
        mesh = mesh.copy()
        v = mesh.vertices
        n = mesh.face_normals[mesh.faces[:, 0] * 0] if len(mesh.faces) else None
        # Cheap triplanar-by-dominant-axis UV: adequate for tiling panel detail
        # on boxy shapes, which is all the textured models are.
        uv = np.zeros((len(v), 2))
        ext = mesh.extents
        dom = int(np.argmin(ext))  # project along the thinnest axis
        axes = [a for a in range(3) if a != dom]
        uv[:, 0] = v[:, axes[0]] * uv_scale
        uv[:, 1] = v[:, axes[1]] * uv_scale
        mat = M(material_key)
        mesh.visual = trimesh.visual.TextureVisuals(uv=uv, material=mat)
        self.parts.append((part_name or f"{self.name}_p{len(self.parts)}", mesh, material_key))
        return self

    def merged(self):
        """All parts concatenated — used for the bounds/tri-count report."""
        return trimesh.util.concatenate([p[1] for p in self.parts])

    def scene(self):
        s = trimesh.Scene()
        for part_name, mesh, _ in self.parts:
            s.add_geometry(mesh, node_name=part_name, geom_name=part_name)
        return s

    @property
    def triangles(self):
        return sum(len(p[1].faces) for p in self.parts)

    @property
    def bounds(self):
        return self.merged().bounds

    @property
    def extents(self):
        b = self.bounds
        return b[1] - b[0]

    @property
    def materials_used(self):
        return sorted({p[2] for p in self.parts})
