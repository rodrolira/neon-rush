"""
Build the whole Neon Rush art set: generate textures, assemble every model,
export GLB + glTF, validate against the gameplay constants, write the manifest.

Run:  python3 build.py <output_root>
"""

import json
import os
import shutil
import sys

import numpy as np
import trimesh
from PIL import Image

import neonlib
from neonlib import MATERIALS, _mat, PALETTE, base_rgb, base_alpha
import textures


# ---------------------------------------------------------------------------
# Textured material variants. Registered before the model modules are imported
# so their MATERIALS lookups resolve.
# ---------------------------------------------------------------------------

def register_textured_materials(tex_dir):
    def img(name):
        return Image.open(os.path.join(tex_dir, name)).convert("RGB")

    road = _mat("MAT_Road_Panel", PALETTE["asphalt"], emission=0.25, roughness=0.85)
    road.baseColorTexture = img("TEX_Road_BaseColor.png")
    road.metallicRoughnessTexture = img("TEX_Road_ORM.png")
    road.emissiveTexture = img("TEX_Road_Emissive.png")
    road.emissiveFactor = np.array([1.0, 1.0, 1.0])
    MATERIALS["MAT_Road_Panel"] = road

    fac = _mat("MAT_Facade_Lit", PALETTE["chassis"], emission=0.6, roughness=0.60)
    fac.baseColorTexture = img("TEX_Facade_BaseColor.png")
    fac.metallicRoughnessTexture = img("TEX_Facade_ORM.png")
    fac.emissiveTexture = img("TEX_Facade_Emissive.png")
    fac.emissiveFactor = np.array([1.0, 1.0, 1.0])
    MATERIALS["MAT_Facade_Lit"] = fac

    coin = _mat("MAT_Coin_Face", PALETTE["gold"], emission=1.6, metallic=0.85, roughness=0.25)
    coin.baseColorTexture = img("TEX_CoinFace_BaseColor.png")
    coin.metallicRoughnessTexture = img("TEX_CoinFace_ORM.png")
    coin.emissiveTexture = img("TEX_CoinFace_Emissive.png")
    coin.emissiveFactor = np.array([1.0, 1.0, 1.0])
    MATERIALS["MAT_Coin_Face"] = coin


# ---------------------------------------------------------------------------
# Expected bounds, straight out of the C#. A model that fails one of these is a
# model that will kill players on a hitbox they cannot see.
# ---------------------------------------------------------------------------

# Two kinds of constraint, and the difference matters:
#
#   "exact" — the mesh IS the hitbox. Obstacles and the road are authored at the
#             size the collision code assumes, so any deviation is a bug the
#             player experiences as an invisible wall.
#   "fit"   — the mesh lives INSIDE a gameplay volume it need not fill. A runner
#             is a person, not a 0.8 m cube; a pickup is a shape inside a 0.7 m
#             cube. Overflowing is a bug, underfilling is art direction. A
#             minimum fill is still enforced so nothing ends up too small to see.
EXPECTED = {
    # name: (volume (x,y,z), mode, tolerance, expected y_min or None, min fill)
    "CHR_Runner_Default":   ((0.80, 1.60, 0.80), "fit", 0.005, 0.0, 0.0),
    "CHR_Runner_Nova":      ((0.80, 1.70, 0.80), "fit", 0.005, 0.0, 0.0),
    "CHR_Runner_Nova_Void": ((0.80, 1.70, 0.80), "fit", 0.005, 0.0, 0.0),
    "CHR_Runner_Rook":      ((0.95, 1.60, 0.80), "fit", 0.005, 0.0, 0.0),
    "OBS_LowJump_Barrier":  ((1.80, 0.70, 1.20), "exact", 0.001, None, 0.0),
    "OBS_HighSlide_Gate":   ((1.80, 1.00, 1.20), "exact", 0.001, None, 0.0),
    "OBS_FullBlock_Wall":   ((1.80, 1.60, 1.20), "exact", 0.001, None, 0.0),
    "PU_Coin":              ((0.70, 0.70, 0.08), "fit", 0.005, None, 0.0),
    "PU_Magnet":            ((0.70, 0.70, 0.70), "fit", 0.005, None, 0.55),
    "PU_Shield":            ((0.70, 0.70, 0.70), "fit", 0.005, None, 0.55),
    "PU_DoubleScore":       ((0.70, 0.70, 0.70), "fit", 0.005, None, 0.55),
    "TRK_RoadSlab_30m":     ((9.00, 0.40, 30.0), "fit", 0.005, None, 0.0),
    "TRK_LaneMarker_30m":   ((0.08, 0.02, 30.0), "exact", 0.001, None, 0.0),
    "TRK_EdgeRail_30m":     ((0.30, 0.60, 30.0), "fit", 0.005, 0.0, 0.0),
}

# Triangle budget per category. The binding constraint is 60 fps on low-end
# Android (README), and the road/skyline are on screen in bulk while a
# character is on screen once.
TRI_BUDGET = {
    "Characters": 1800,
    "Obstacles": 900,
    "Pickups": 500,
    "Track": 200,
    "Environment": 400,
    "Store": 1200,
}


def check(model):
    """Returns a list of problems. Empty list means the model is shippable."""
    problems = []
    ext = model.extents
    bounds = model.bounds

    if model.name in EXPECTED:
        want, mode, tol, y_min, fill = EXPECTED[model.name]
        for axis, (got, exp) in enumerate(zip(ext, want)):
            label = "XYZ"[axis]
            if mode == "exact" and abs(got - exp) > tol:
                problems.append(f"extent {label} = {got:.4f} m, must equal {exp:.4f} +/- {tol}")
            elif mode == "fit":
                if got > exp + tol:
                    problems.append(
                        f"extent {label} = {got:.4f} m, overflows the {exp:.4f} m volume")
                elif fill and got < exp * fill:
                    problems.append(
                        f"extent {label} = {got:.4f} m, under the {exp * fill:.4f} m "
                        f"minimum fill — too small to read at speed")
        if y_min is not None and abs(bounds[0][1] - y_min) > 0.005:
            problems.append(f"y_min = {bounds[0][1]:.4f}, expected {y_min} (pivot on the ground)")

    budget = TRI_BUDGET.get(model.category)
    if budget and model.triangles > budget:
        problems.append(f"{model.triangles} tris exceeds the {model.category} budget of {budget}")

    for _, mesh, _ in model.parts:
        if len(mesh.faces) == 0:
            problems.append("a part has zero faces")
        if not np.isfinite(mesh.vertices).all():
            problems.append("a part has non-finite vertices")

    return problems


def obstacle_rules(models):
    """The three gameplay invariants that no obstacle mesh may break.

    These are the reason the archetype struct exists (see its docstring): a mesh
    that spills outside its box produces a death the player cannot see coming.
    """
    by = {m.name: m for m in models}
    issues = []

    low = by["OBS_LowJump_Barrier"]
    # Placed at CentreY 0.35 -> spans 0.0 .. 0.70. Must stay under the jump apex.
    if low.bounds[1][1] + 0.35 > 0.70 + 1e-6:
        issues.append("LowJump reaches above 0.70 m; a correct jump would clip it")

    high = by["OBS_HighSlide_Gate"]
    # Placed at CentreY 1.6 -> spans 1.10 .. 2.10. Nothing may hang below 1.10,
    # because the slide box top is 0.72 m and the gap is the whole mechanic.
    if high.bounds[0][1] + 1.60 < 1.10 - 1e-6:
        issues.append("HighSlide hangs below 1.10 m; a correct slide would clip it")

    full = by["OBS_FullBlock_Wall"]
    if abs(full.extents[1] - 1.60) > 1e-3:
        issues.append("FullBlock is not exactly 1.60 m tall")

    for m in (low, high, full):
        if m.extents[0] > 1.80 + 1e-3:
            issues.append(f"{m.name} is wider than 1.80 m and would overlap the next lane")

    return issues


def export(model, root):
    """Writes GLB (primary) and glTF+bin (inspectable/diffable) for one model."""
    cat_dir = os.path.join(root, "Models", model.category)
    os.makedirs(cat_dir, exist_ok=True)
    scene = model.scene()

    glb_path = os.path.join(cat_dir, f"{model.name}.glb")
    with open(glb_path, "wb") as fh:
        fh.write(trimesh.exchange.gltf.export_glb(scene, include_normals=True))

    gltf_dir = os.path.join(root, "Models", model.category, "gltf")
    os.makedirs(gltf_dir, exist_ok=True)
    files = trimesh.exchange.gltf.export_gltf(
        scene, include_normals=True, merge_buffers=True)
    for fname, data in files.items():
        # trimesh names the root 'model.gltf'; rename it after the model so a
        # folder of glTFs is browsable.
        out_name = f"{model.name}.gltf" if fname.endswith(".gltf") else f"{model.name}_{fname}"
        if fname.endswith(".gltf"):
            data = data.replace(b'"model.bin"', f'"{model.name}_model.bin"'.encode())
        with open(os.path.join(gltf_dir, out_name), "wb") as fh:
            fh.write(data)

    return os.path.getsize(glb_path)


def main():
    root = sys.argv[1]
    if os.path.isdir(root):
        shutil.rmtree(root)
    tex_dir = os.path.join(root, "Textures")
    os.makedirs(tex_dir, exist_ok=True)

    tex_files = textures.build_all(tex_dir)
    register_textured_materials(tex_dir)

    import models_a
    import models_b

    all_models = (models_a.characters() + models_a.obstacles() + models_a.pickups()
                  + models_b.track() + models_b.environment() + models_b.store())

    manifest = {
        "project": "Neon Rush",
        "studio": "MoonCat Studio",
        "unit": "1 unit = 1 metre",
        "convention": {
            "up": "+Y",
            "forward": "-Z (glTF). Unity's glTF importers flip Z, so these "
                       "arrive facing +Z, which is the run direction.",
            "handedness": "right-handed (glTF); Unity converts on import",
        },
        "formats": ["glb (primary)", "gltf + bin", "png textures"],
        "textures": tex_files,
        "materials": {},
        "models": [],
        "validation": {"failures": []},
    }

    for key, mat in MATERIALS.items():
        manifest["materials"][key] = {
            "baseColor": [round(float(c), 4) for c in base_rgb(mat)],
            "alpha": round(base_alpha(mat), 3),
            "metallic": round(float(mat.metallicFactor), 3),
            "roughness": round(float(mat.roughnessFactor), 3),
            "emissionStrength": round(float(getattr(mat, "neon_emission", 0.0)), 3),
            "textured": mat.baseColorTexture is not None,
        }

    total_tris = 0
    failures = []

    for m in all_models:
        problems = check(m)
        size = export(m, root)
        b = m.bounds
        total_tris += m.triangles
        manifest["models"].append({
            "name": m.name,
            "category": m.category,
            "file": f"Models/{m.category}/{m.name}.glb",
            "function": m.function,
            "pivot": m.pivot,
            "triangles": m.triangles,
            "parts": len(m.parts),
            "extents_m": [round(float(x), 4) for x in m.extents],
            "bounds_min_m": [round(float(x), 4) for x in b[0]],
            "bounds_max_m": [round(float(x), 4) for x in b[1]],
            "materials": m.materials_used,
            "glb_bytes": size,
            "notes": m.notes,
        })
        if problems:
            failures.append({"model": m.name, "problems": problems})

    rule_issues = obstacle_rules(all_models)
    if rule_issues:
        failures.append({"model": "<obstacle gameplay invariants>", "problems": rule_issues})

    manifest["validation"]["failures"] = failures
    manifest["validation"]["passed"] = len(failures) == 0
    manifest["totals"] = {
        "models": len(all_models),
        "triangles": total_tris,
        "materials": len(MATERIALS),
        "textures": len(tex_files),
    }

    with open(os.path.join(root, "MODEL_MANIFEST.json"), "w", encoding="utf-8") as fh:
        json.dump(manifest, fh, indent=2, ensure_ascii=False)

    print(f"{len(all_models)} models, {total_tris} tris, {len(MATERIALS)} materials")
    for m in all_models:
        e = m.extents
        print(f"  {m.category:<12} {m.name:<26} {m.triangles:>5} tris  "
              f"{e[0]:.2f} x {e[1]:.2f} x {e[2]:.2f} m")
    if failures:
        print("\nVALIDATION FAILURES:")
        for f in failures:
            print(f"  {f['model']}: {'; '.join(f['problems'])}")
        sys.exit(1)
    print("\nvalidation: all checks passed")


if __name__ == "__main__":
    main()
