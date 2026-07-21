"""
Assembles one in-game scene from the shipped models and renders it from the
chase camera. This is the check that no per-model test can make: whether the
whole set is consistent in scale and reads as one world.
"""
import numpy as np, trimesh, build
from neonlib import MATERIALS
build.register_textured_materials('/tmp/art/Textures')
import models_a, models_b, preview
from neonlib import base_rgb

LANE = 2.6
by = {m.name: m for m in (models_a.characters() + models_a.obstacles()
                          + models_a.pickups() + models_b.track() + models_b.environment())}

UNITY_IMPORT = np.diag([1.0, 1.0, -1.0, 1.0])

parts = []
def place(model, pos, yaw=0.0):
    """Places a model exactly as Unity will, Z-flip included.

    Without this the preview lies in the one way that matters: it shows the
    authored -Z face toward the camera, when the importer's handedness
    conversion means Unity shows the +Z face. Everything about which side the
    signage lands on, and which way the runner faces, is decided right here.
    """
    for _, mesh, key in model.parts:
        mm = mesh.copy()
        mm.apply_transform(UNITY_IMPORT)
        mm.invert()                      # the mirror reverses winding
        if yaw:
            mm.apply_transform(trimesh.transformations.rotation_matrix(np.radians(yaw), [0, 1, 0]))
        mm.apply_translation(np.asarray(pos, float))
        mat = MATERIALS[key]
        parts.append((mm, base_rgb(mat), float(getattr(mat, 'neon_emission', 0.0))))

# Road: two chunks butted end to end, exactly as TrackStreamer streams them.
for c in range(2):
    z = c * 30.0 + 15.0
    place(by['TRK_RoadSlab_30m'], (0, -0.10, z))
    for sgn in (-1, 1):
        place(by['TRK_LaneMarker_30m'], (sgn * LANE * 0.5, 0.005, z))
        place(by['TRK_EdgeRail_30m'], (sgn * 4.5, 0.0, z))

# Skyline down both sides.
rng = np.random.default_rng(3)
for side in (-1, 1):
    for i in range(9):
        b = by[['ENV_Building_Low', 'ENV_Building_Mid', 'ENV_Building_Tall'][rng.integers(0, 3)]]
        place(b, (side * (6.5 + rng.random() * 2.5), 0, 4 + i * 6.5), yaw=90 * rng.integers(0, 4))

place(by['ENV_ArchGate'], (0, 0, 34))
place(by['ENV_Billboard_Sign'], (-8.0, 0, 20))

# The player, plus the sequence of decisions ahead of them.
place(by['CHR_Runner_Default'], (0, 0, 0))
place(by['OBS_LowJump_Barrier'],  (-LANE, 0.35, 11))
place(by['OBS_FullBlock_Wall'],   (0.0,   0.80, 11))
place(by['OBS_HighSlide_Gate'],   (LANE,  1.60, 11))
place(by['OBS_FullBlock_Wall'],   (-LANE, 0.80, 24))
place(by['OBS_LowJump_Barrier'],  (LANE,  0.35, 24))
for i in range(7):
    place(by['PU_Coin'], (0.0, 0.9, 5 + i * 1.4))
for i in range(4):
    place(by['PU_Coin'], (LANE, 0.9, 16 + i * 1.4))
place(by['PU_Shield'], (-LANE, 1.0, 18))
place(by['PU_Magnet'], (LANE, 1.0, 29))

img = preview.render_camera(parts, size=1000, eye=(0, 3.1, -6.2), target=(0, 1.5, 14), fov=46)
from PIL import Image
Image.fromarray(img).save('/sessions/vibrant-vigilant-allen/mnt/outputs/_preview/scene.png')
print('scene rendered')
