"""
Characters, obstacles and pickups.

Every dimension in this file is traceable to a constant in the codebase:
  * Player 0.8 x 1.6 x 0.8  -> GameBootstrap.playerHeight, PlayerMotor.Bounds
  * Obstacles               -> ObstacleArchetype.For(kind)
  * Coin radius 0.35        -> TrackStreamer.CoinRadius
  * PowerUp 0.7             -> TrackStreamer.PowerUpSize
"""

import numpy as np
from neonlib import Model, box, chamfer_box, cyl, beveled_disc, prism, \
    octahedron, pyramid, wedge, arrow_plate, mirror_x, rot


# ===========================================================================
# CHARACTERS
# ===========================================================================

def _runner(name, function, body_mat, accent_mat, trim_mat, build="slim", notes=""):
    """A stylised humanoid runner inside the 0.8 x 1.6 x 0.8 gameplay box.

    Pivot sits between the feet at y=0, which mirrors what GameBootstrap already
    does by hand: it parents the visual cube to a 'PlayerPivot' and pushes the
    mesh up by half its height so lane maths, jump height and the collision AABB
    can all treat y=0 as 'feet on the floor'. Baking that offset into the model
    lets that child GameObject and its half-height fudge disappear entirely.

    'build' widens the silhouette without changing the skeleton proportions, so
    every character reads at the same size from behind at speed — a heavier
    character that is actually harder to fit through a gap would be pay-to-lose.
    """
    m = Model(name, "Characters", function, "Between the feet, y = 0, ground level", notes)

    wide = {"slim": 1.00, "medium": 1.08, "heavy": 1.20}[build]
    hip_x = 0.13
    arm_x = 0.29 * wide

    # --- Legs. Modelled once on +X and mirrored, so left and right can never
    # --- drift apart and the vertex cache sees the same data twice.
    boot = chamfer_box((0.20 * wide, 0.12, 0.34), 0.03, (hip_x, 0.06, -0.03))
    shin = chamfer_box((0.15 * wide, 0.42, 0.16), 0.03, (hip_x, 0.32, 0.0))
    thigh = chamfer_box((0.19 * wide, 0.34, 0.20), 0.03, (hip_x + 0.01, 0.72, 0.0))
    knee = box((0.17 * wide, 0.035, 0.18), (hip_x, 0.535, 0.0))

    for part in (boot, shin, thigh):
        m.add(part, body_mat)
        m.add(mirror_x(part), body_mat)
    m.add(knee, accent_mat)
    m.add(mirror_x(knee), accent_mat)

    # --- Torso
    m.add(chamfer_box((0.40 * wide, 0.16, 0.24), 0.03, (0, 0.95, 0)), body_mat, "pelvis")
    m.add(chamfer_box((0.46 * wide, 0.34, 0.26), 0.04, (0, 1.18, 0)), body_mat, "chest")
    m.add(box((0.44 * wide, 0.035, 0.27), (0, 1.02, 0)), trim_mat, "belt")

    # The back core is the read. The chase camera sits behind the runner, so the
    # +Z side (which the Z-flip on import turns into the side facing the camera)
    # is the only surface the player ever looks at for the whole run. A single
    # hot emissive point there keeps the runner legible against a lit skyline.
    m.add(octahedron((0.17, 0.17, 0.12), (0, 1.21, 0.13)), accent_mat, "back_core")
    m.add(box((0.26, 0.045, 0.04), (0, 1.06, 0.13)), accent_mat, "back_bar")
    # A smaller emblem on the chest, for the store and menu views where the
    # character is shown front-on and the back is invisible.
    m.add(octahedron((0.11, 0.11, 0.08), (0, 1.20, -0.13)), accent_mat, "chest_emblem")

    # --- Arms
    pauldron = wedge((0.16 * wide, 0.15, 0.24), (arm_x, 1.30, 0))
    upper = chamfer_box((0.11, 0.28, 0.13), 0.025, (arm_x, 1.10, 0.0))
    fore = chamfer_box((0.10, 0.26, 0.12), 0.025, (arm_x, 0.86, 0.04))
    fist = chamfer_box((0.11, 0.11, 0.12), 0.03, (arm_x, 0.70, 0.06))

    for part, mat in ((pauldron, trim_mat), (upper, body_mat), (fore, body_mat), (fist, accent_mat)):
        m.add(part, mat)
        m.add(mirror_x(part), mat)

    # --- Head. Top lands at 1.595 m: just inside the 1.6 m standing box, so the
    # --- silhouette fills the collision volume without poking out of it.
    m.add(box((0.12, 0.06, 0.12), (0, 1.375, 0)), trim_mat, "neck")
    m.add(chamfer_box((0.24, 0.22, 0.26), 0.04, (0, 1.485, 0)), body_mat, "head")
    m.add(box((0.20, 0.075, 0.035), (0, 1.49, -0.135)), accent_mat, "visor")

    return m


def characters():
    out = []

    out.append(_runner(
        "CHR_Runner_Default",
        "Default playable character. Replaces the cyan cube built in GameBootstrap.",
        "MAT_Metal_Steel", "MAT_Neon_Cyan", "MAT_Dark_Chassis", "slim",
        "The free starter. Deliberately the plainest silhouette in the set so "
        "every purchasable character is a visible upgrade."))

    nova = _runner(
        "CHR_Runner_Nova",
        "Purchasable character 'char_nova' (2,500 coins).",
        "MAT_Dark_Chassis", "MAT_Neon_Cyan", "MAT_Neon_Violet", "slim",
        "Aero crest and calf fins mark it as the speed-read character.")
    # Crest: a swept dorsal fin. Reads instantly from behind, which is the only
    # angle that matters in a runner.
    nova.add(wedge((0.035, 0.13, 0.30), (0, 1.60, 0.02)), "MAT_Neon_Violet", "crest")
    nova.add(wedge((0.03, 0.10, 0.20), (0.155, 0.36, 0.08)), "MAT_Neon_Cyan", "calf_fin_r")
    nova.add(mirror_x(wedge((0.03, 0.10, 0.20), (0.155, 0.36, 0.08))), "MAT_Neon_Cyan", "calf_fin_l")
    out.append(nova)

    void = _runner(
        "CHR_Runner_Nova_Void",
        "Skin 'skin_nova_void' (350 gems). Nova's geometry, Void materials.",
        "MAT_Dark_Void", "MAT_Neon_Lilac", "MAT_Neon_Pink", "slim",
        "Shares Nova's mesh exactly — a skin must never change the silhouette, "
        "or players read it as a hitbox change. Only the material set differs, "
        "so at runtime this can be a material swap on the Nova mesh rather than "
        "a second mesh in memory.")
    void.add(wedge((0.035, 0.13, 0.30), (0, 1.60, 0.02)), "MAT_Neon_Pink", "crest")
    void.add(wedge((0.03, 0.10, 0.20), (0.155, 0.36, 0.08)), "MAT_Neon_Lilac", "calf_fin_r")
    void.add(mirror_x(wedge((0.03, 0.10, 0.20), (0.155, 0.36, 0.08))), "MAT_Neon_Lilac", "calf_fin_l")
    out.append(void)

    rook = _runner(
        "CHR_Runner_Rook",
        "Purchasable character 'char_rook' (7,500 coins).",
        "MAT_Metal_Steel", "MAT_Neon_Magenta", "MAT_Dark_Chassis", "heavy",
        "Armoured and blocky — the prestige tier. Heavier build, identical "
        "collision footprint: cosmetics never touch balance (StoreItem.cs).")
    rook.add(chamfer_box((0.50, 0.20, 0.05), 0.02, (0, 1.30, 0.15)), "MAT_Neon_Magenta", "backplate")
    rook.add(box((0.30, 0.04, 0.05), (0, 1.12, 0.15)), "MAT_Neon_Magenta", "back_bar_2")
    rook.add(chamfer_box((0.09, 0.09, 0.28), 0.02, (0.36, 1.30, 0)), "MAT_Neon_Magenta", "spike_r")
    rook.add(mirror_x(chamfer_box((0.09, 0.09, 0.28), 0.02, (0.36, 1.30, 0))), "MAT_Neon_Magenta", "spike_l")
    out.append(rook)

    return out


# ===========================================================================
# OBSTACLES — sizes are locked to ObstacleArchetype and must not be exceeded.
# ===========================================================================
#
# WHICH FACE THE PLAYER SEES
#
# In Unity the runner sits at z = 0 and the track extends toward +Z, so an
# obstacle rushing at the player presents its -Z face. glTF is right-handed and
# Unity is left-handed, and every glTF importer bridges that by negating Z on
# import. So a face authored at +Z *here* is the face at -Z *there* — the one
# the player actually reads.
#
# All the signage therefore lives on +Z. Getting this backwards is not a subtle
# art bug: it puts every chevron and arrow on the side of the obstacle that is
# already past the camera by the time it could be read, leaving the player a
# featureless black slab and 250 ms to guess.
#
# DEPTH BUDGET (total must be exactly 1.20 m)
#   body       z in [-0.60, +0.56]   (depth 1.16, centred at -0.02)
#   faceplate  z in [+0.56, +0.59]
#   signage    z in [+0.575, +0.60]
# The body is pulled back so the signage has somewhere to sit *proud* of the
# plate. Layering it flush, as the first pass did, buried the ribs inside the
# faceplate and the FullBlock rendered as a plain black box.

BODY_D = 1.16          # body depth
BODY_Z = -0.02         # body centre, so its back lands on the -0.60 boundary
PLATE_Z = 0.575        # faceplate centre
SIGN_Z = 0.5875        # signage centre — 2.5 cm thick, ending exactly on +0.60


def obstacles():
    out = []

    # --- LowJump: 1.8 x 0.7 x 1.2, spans y 0.0 -> 0.7 -------------------------
    m = Model(
        "OBS_LowJump_Barrier", "Obstacles",
        "ObstacleKind.LowJump — a grounded block cleared only by jumping.",
        "Box centre (0,0,0). Place at ObstacleArchetype.CentreY = 0.35 m.",
        "Chevrons point up because the required input is up. The player has "
        "~250 ms to read this at 30 m/s; an arrow is faster to parse than a "
        "colour convention they have to learn.")
    m.add(chamfer_box((1.80, 0.70, BODY_D), 0.05, (0, 0, BODY_Z)), "MAT_Dark_Chassis", "body")
    m.add(box((1.70, 0.60, 0.03), (0, 0, PLATE_Z)), "MAT_Metal_Steel", "faceplate")
    for i, x in enumerate((-0.55, 0.0, 0.55)):
        # Two bars per chevron, angled into a caret pointing up.
        for sign in (-1, 1):
            bar = box((0.30, 0.07, 0.025), (x + sign * 0.075, 0.0, SIGN_Z))
            # -sign puts the outer end of each bar low and the inner end high: a
            # caret pointing UP, matching the jump the obstacle demands.
            m.add(rot(bar, -sign * 30, [0, 0, 1], (x, 0, 0)), "MAT_Neon_Magenta", f"chevron_{i}_{sign}")
    m.add(box((1.80, 0.04, 1.20), (0, 0.330, 0)), "MAT_Neon_Magenta", "top_strip")
    for sign in (-1, 1):
        m.add(box((0.05, 0.70, 0.05), (sign * 0.875, 0, 0.575)), "MAT_Neon_Magenta", f"post_{sign}")
    out.append(m)

    # --- HighSlide: 1.8 x 1.0 x 1.2, spans y 1.1 -> 2.1 ----------------------
    m = Model(
        "OBS_HighSlide_Gate", "Obstacles",
        "ObstacleKind.HighSlide — an overhead barrier cleared only by sliding.",
        "Box centre (0,0,0). Place at ObstacleArchetype.CentreY = 1.6 m.",
        "Nothing in this mesh drops below the box's own bottom edge. That is "
        "the hard constraint: the gap beneath is the entire mechanic, and a "
        "decorative strut hanging 5 cm too low would kill a correct slide.")
    m.add(chamfer_box((1.80, 1.00, BODY_D), 0.05, (0, 0, BODY_Z)), "MAT_Dark_Chassis", "body")
    m.add(box((1.70, 0.90, 0.03), (0, 0, PLATE_Z)), "MAT_Metal_Steel", "faceplate")
    # Downward chevrons: the input is down.
    for i, x in enumerate((-0.55, 0.0, 0.55)):
        for sign in (-1, 1):
            bar = box((0.30, 0.07, 0.025), (x + sign * 0.075, -0.05, SIGN_Z))
            # Mirrored against LowJump's: a caret pointing DOWN, for the slide.
            m.add(rot(bar, sign * 30, [0, 0, 1], (x, -0.05, 0)), "MAT_Neon_Magenta", f"chevron_{i}_{sign}")
    # Hazard teeth along the lower lip — flush with the bottom, never past it.
    for i in range(6):
        x = -0.75 + i * 0.30
        m.add(pyramid(0.22, 0.16, (x, -0.50, 0.30)), "MAT_Neon_Magenta", f"tooth_{i}")
    m.add(box((1.80, 0.06, 1.20), (0, -0.47, 0)), "MAT_Neon_Magenta", "lip_strip")
    m.add(box((0.08, 1.00, 0.08), (0.86, 0, 0)), "MAT_Metal_Steel", "hanger_r")
    m.add(box((0.08, 1.00, 0.08), (-0.86, 0, 0)), "MAT_Metal_Steel", "hanger_l")
    out.append(m)

    # --- FullBlock: 1.8 x 1.6 x 1.2, spans y 0.0 -> 1.6 ----------------------
    m = Model(
        "OBS_FullBlock_Wall", "Obstacles",
        "ObstacleKind.FullBlock — a full-height wall; the only answer is a lane change.",
        "Box centre (0,0,0). Place at ObstacleArchetype.CentreY = 0.8 m.",
        "Reads as unambiguously solid: a filled grid rather than the open "
        "framing of the other two. The lateral arrows tell the player the "
        "answer is sideways, since there is no vertical solution to find.")
    m.add(chamfer_box((1.80, 1.60, BODY_D), 0.06, (0, 0, BODY_Z)), "MAT_Dark_Chassis", "body")
    m.add(box((1.72, 1.52, 0.03), (0, 0, PLATE_Z)), "MAT_Metal_Steel", "faceplate")
    for i in range(3):                                    # horizontal ribs
        m.add(box((1.76, 0.06, 0.025), (0, -0.50 + i * 0.50, SIGN_Z)), "MAT_Neon_Magenta", f"rib_{i}")
    for i in range(2):                                    # vertical stiles
        m.add(box((0.06, 1.52, 0.025), (-0.45 + i * 0.90, 0, SIGN_Z)), "MAT_Neon_Magenta", f"stile_{i}")
    # Lateral arrows: "go around". Sat in the widest empty bay of the grid so
    # they are not competing with a rib for the same pixels.
    for sign in (-1, 1):
        m.add(arrow_plate(0.30, 0.42, 0.025, (sign * 0.66, 0.25, SIGN_Z),
                          point="+x" if sign > 0 else "-x"),
              "MAT_Neon_Magenta", f"arrow_{sign}")
    m.add(box((1.80, 0.05, 1.20), (0, 0.775, 0)), "MAT_Neon_Magenta", "cap_strip")
    out.append(m)

    return out


# ===========================================================================
# PICKUPS
# ===========================================================================

def pickups():
    out = []

    # --- Coin: radius 0.35, faces the camera (disc axis along Z) --------------
    m = Model(
        "PU_Coin", "Pickups",
        "The run currency. Replaces PrimitiveFactory.Coin (radius 0.35 m).",
        "Disc centre. Place at TrackStreamer.CoinHeight = 0.9 m.",
        "Disc axis runs along Z so the face is square to the camera, matching "
        "the 90-degrees-on-X rotation PrimitiveFactory.Coin already applies. "
        "The bevelled rim keeps a lit sliver visible while it bobs; a flat "
        "cylinder vanishes edge-on and the coin appears to flicker out.")
    m.add_uv(beveled_disc(0.35, 0.06, bevel=0.28, sections=20, axis="z"),
             "MAT_Coin_Face", "disc", uv_scale=1.0 / 0.7)
    out.append(m)

    # --- Magnet: 0.7 cube -----------------------------------------------------
    m = Model(
        "PU_Magnet", "Pickups",
        "PowerUpType.Magnet — pulls nearby coins in for a few seconds.",
        "Bounding-box centre. Place at TrackStreamer.PowerUpHeight = 1.0 m.",
        "A literal horseshoe magnet with lit pole tips. Silhouette over colour: "
        "the three pickups are told apart while tumbling, when colour is "
        "washing out into the bloom and only the outline survives.")
    yoke = chamfer_box((0.46, 0.16, 0.44), 0.03, (0, 0.20, 0))
    m.add(yoke, "MAT_Neon_Cyan", "yoke")
    for sign in (-1, 1):
        m.add(chamfer_box((0.15, 0.32, 0.44), 0.03, (sign * 0.155, -0.02, 0)), "MAT_Neon_Cyan", f"prong_{sign}")
        m.add(box((0.15, 0.11, 0.45), (sign * 0.155, -0.245, 0)), "MAT_Neon_White", f"pole_{sign}")
    m.add(box((0.30, 0.05, 0.46), (0, 0.265, 0)), "MAT_Neon_White", "band")
    out.append(m)

    # --- Shield: 0.7 cube -----------------------------------------------------
    m = Model(
        "PU_Shield", "Pickups",
        "PowerUpType.Shield — absorbs the next hit. A charge, not a timer.",
        "Bounding-box centre. Place at TrackStreamer.PowerUpHeight = 1.0 m.",
        "Faceted crystal core inside an open hex ring. The ring is the visual "
        "promise of a barrier; the translucent core is the one place in the "
        "project a blended material earns its overdraw, because the pickup is "
        "on screen for under a second and never in bulk.")
    # prism() returns a SOLID hexagon, not a ring. The first pass treated it as
    # a frame and centred the crystal inside it, which simply buried the crystal
    # in opaque blue. The plate is now the shield's face and everything that
    # needs to be seen is stacked proud of it, toward +Z.
    m.add(prism(0.34, 0.14, sides=6, axis="z"), "MAT_Neon_Blue", "plate")
    m.add(prism(0.26, 0.16, sides=6, axis="z", centre=(0, 0, 0.06)), "MAT_Dark_Chassis", "field")
    m.add(octahedron((0.34, 0.40, 0.46), (0, 0, 0.14)), "MAT_Glass_Tint", "core")
    m.add(octahedron((0.17, 0.20, 0.24), (0, 0, 0.16)), "MAT_Neon_White", "core_hot")
    out.append(m)

    # --- DoubleScore: 0.7 cube -----------------------------------------------
    m = Model(
        "PU_DoubleScore", "Pickups",
        "PowerUpType.DoubleScore — multiplies distance score for a few seconds.",
        "Bounding-box centre. Place at TrackStreamer.PowerUpHeight = 1.0 m.",
        "A raised x2 glyph on a gold tablet. Numerals beat abstraction here: "
        "the multiplier is the only pickup whose value is a quantity, and the "
        "player should not have to remember which colour meant how much.")
    m.add(prism(0.33, 0.40, sides=8, axis="z", twist=np.pi / 8), "MAT_Neon_Gold", "tablet")
    m.add(prism(0.27, 0.42, sides=8, axis="z", twist=np.pi / 8), "MAT_Dark_Chassis", "inset")
    # The 'x'
    for face in (-0.21, 0.21):
        for sign in (-1, 1):
            m.add(rot(box((0.055, 0.24, 0.05), (-0.10, 0, face)), sign * 40, [0, 0, 1], (-0.10, 0, 0)),
                  "MAT_Neon_Gold", f"x_{sign}_{face}")
    # The '2': three horizontal bars plus two half-height risers.
    m.add(box((0.16, 0.045, 0.46), (0.10, 0.095, 0.0)), "MAT_Neon_Gold", "two_top")
    m.add(box((0.16, 0.045, 0.46), (0.10, 0.000, 0.0)), "MAT_Neon_Gold", "two_mid")
    m.add(box((0.16, 0.045, 0.46), (0.10, -0.095, 0.0)), "MAT_Neon_Gold", "two_bot")
    m.add(box((0.045, 0.07, 0.46), (0.165, 0.050, 0.0)), "MAT_Neon_Gold", "two_riser_hi")
    m.add(box((0.045, 0.07, 0.46), (0.035, -0.048, 0.0)), "MAT_Neon_Gold", "two_riser_lo")
    out.append(m)

    return out
