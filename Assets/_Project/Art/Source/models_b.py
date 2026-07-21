"""
Track, environment and store/economy props.

Track dimensions derive from RunTuning:
  LaneWidth 2.6 · 3 lanes + 1.2 margin = 9.0 m road width
  ChunkLength 30 m
  Lane markers at +/- 1.3 m, 0.08 x 0.02 x 30
"""

import numpy as np
from neonlib import Model, box, chamfer_box, cyl, beveled_disc, prism, \
    octahedron, pyramid, wedge, mirror_x, rot

LANE_WIDTH = 2.6
CHUNK = 30.0
ROAD_WIDTH = LANE_WIDTH * 3 + 1.2          # 9.0


# ===========================================================================
# TRACK
# ===========================================================================

def track():
    out = []

    m = Model(
        "TRK_RoadSlab_30m", "Track",
        "One 30 m road chunk. Replaces the road cube built in TrackStreamer.BuildChunkShell.",
        "Slab centre. Matches localPosition (0, -0.1, ChunkLength*0.5).",
        "Modelled at exactly one ChunkLength so chunks butt together with no "
        "seam and no scaling. The UVs tile once per 3 m, which is short enough "
        "that the panel seams give a speed cue and long enough that they do not "
        "strobe at 30 m/s.")
    m.add_uv(box((ROAD_WIDTH, 0.20, CHUNK)), "MAT_Road_Panel", "slab", uv_scale=1.0 / 3.0)
    # Kerbs: a dark lip that stops the slab's edge from aliasing against the fog.
    for sign in (-1, 1):
        m.add(box((0.30, 0.12, CHUNK), (sign * (ROAD_WIDTH / 2 - 0.15), 0.14, 0)),
              "MAT_Dark_Chassis", f"kerb_{sign}")
    out.append(m)

    m = Model(
        "TRK_LaneMarker_30m", "Track",
        "One lane divider. Replaces the lane marker cubes in TrackStreamer.",
        "Strip centre. Place at x = +/- LaneWidth*0.5, y = 0.005.",
        "Kept as a continuous strip rather than dashes: dashes at 30 m/s "
        "alias into a strobe on a 60 Hz panel, and the marker's job is to be "
        "a stable reference the eye can track, not decoration.")
    m.add(box((0.08, 0.02, CHUNK)), "MAT_Neon_Violet", "strip")
    out.append(m)

    m = Model(
        "TRK_EdgeRail_30m", "Track",
        "Neon guard rail along both road edges. New — no greybox equivalent.",
        "Rail centre at its base. Place at x = +/- ROAD_WIDTH/2, y = 0.",
        "Inferred addition. The greybox road just stops at its edge and the "
        "eye has nothing to measure lateral speed against; a lit rail in the "
        "periphery is the cheapest possible speed cue and costs 2 draw calls "
        "per chunk.")
    m.add(box((0.10, 0.16, CHUNK), (0, 0.42, 0)), "MAT_Neon_Violet", "rail")
    m.add(box((0.14, 0.34, 0.14), (0, 0.17, 0)), "MAT_Dark_Chassis", "post")
    out.append(m)

    return out


# ===========================================================================
# ENVIRONMENT
# ===========================================================================

def _building(name, w, h, d, crown_mat, function, notes):
    """A skyline block: dark body, lit facade, neon crown.

    Pivot at the base rather than the centre, unlike the greybox, which places
    the cube at height*0.5. A base pivot means a building is positioned at
    ground level with no per-instance height maths, and the crown strip becomes
    part of the same mesh instead of a second GameObject — halving the skyline's
    object count, which matters because TrackStreamer spawns these per chunk.
    """
    m = Model(name, "Environment", function, "Base centre, y = 0 at ground level", notes)
    m.add_uv(box((w, h, d), (0, h * 0.5, 0)), "MAT_Facade_Lit", "body",
             uv_scale=1.0 / 4.0)
    m.add(box((w + 0.15, 0.15, d + 0.15), (0, h + 0.08, 0)), crown_mat, "crown")
    m.add(box((w * 0.10, h * 0.92, 0.06), (w * 0.5, h * 0.5, -d * 0.5 - 0.03)),
          crown_mat, "edge_light")
    return m


def environment():
    out = []

    out.append(_building(
        "ENV_Building_Low", 3.0, 9.0, 3.2, "MAT_Neon_Cyan",
        "Short skyline block. Replaces the low end of TrackStreamer's random building range.",
        "Three fixed heights replace the greybox's continuous random height. "
        "A fixed set can share one mesh and one material, so the whole skyline "
        "GPU-instances; a continuously random scale cannot batch and would put "
        "dozens of draw calls into scenery nobody looks at."))

    out.append(_building(
        "ENV_Building_Mid", 3.8, 15.0, 3.6, "MAT_Neon_Pink",
        "Mid skyline block.",
        "Mid tier. Vary the skyline by mixing the three prefabs and rotating "
        "them 90 degrees, not by scaling — non-uniform scale would stretch the "
        "facade texture and break the crown's proportions."))

    out.append(_building(
        "ENV_Building_Tall", 4.6, 24.0, 4.0, "MAT_Neon_Lilac",
        "Tall skyline block; the one that reads above the fog line.",
        "Tall enough to break the fog end distance, which is what gives the "
        "horizon depth instead of a flat wall of haze."))

    m = Model(
        "ENV_NeonStrip_Crown", "Environment",
        "Standalone rooftop light band, for dressing buildings not in the set.",
        "Strip centre. Place at building top + half its own height.",
        "Extracted so the crown can be reused on any block; matches the "
        "greybox strip in TrackStreamer (body width + 0.15, 0.15 tall).")
    m.add(box((3.15, 0.15, 3.35)), "MAT_Neon_Cyan", "strip")
    out.append(m)

    m = Model(
        "ENV_Billboard_Sign", "Environment",
        "Roadside neon billboard. New — inferred set dressing.",
        "Base of the post, y = 0.",
        "Inferred. The skyline is all vertical blocks, so it reads as a "
        "repeating comb; a billboard is a horizontal element that breaks that "
        "rhythm. Frame is lit, panel is dark, so it silhouettes against the fog "
        "rather than adding another bloom source competing with the coins.")
    m.add(box((0.22, 4.20, 0.22), (0, 2.10, 0)), "MAT_Dark_Chassis", "post")
    m.add(box((3.40, 1.90, 0.10), (0, 5.10, 0)), "MAT_Dark_Chassis", "panel")
    m.add(box((3.60, 0.12, 0.16), (0, 6.11, 0)), "MAT_Neon_Pink", "frame_top")
    m.add(box((3.60, 0.12, 0.16), (0, 4.09, 0)), "MAT_Neon_Pink", "frame_bottom")
    for sign in (-1, 1):
        m.add(box((0.12, 2.14, 0.16), (sign * 1.74, 5.10, 0)), "MAT_Neon_Pink", f"frame_side_{sign}")
    for i in range(3):
        m.add(box((2.60, 0.14, 0.06), (0, 4.60 + i * 0.50, -0.08)), "MAT_Neon_Cyan", f"glyph_{i}")
    out.append(m)

    m = Model(
        "ENV_ArchGate", "Environment",
        "Neon arch spanning the full road width. New — inferred milestone marker.",
        "Centre of the road at ground level, y = 0.",
        "Inferred, with a gameplay job: StageService advances the run through "
        "named stages but nothing in the world marks the transition. An arch "
        "the player runs through is a free, diegetic stage boundary. Its clear "
        "height is 5.2 m — far above the 2.1 m ceiling of any obstacle — so it "
        "can never be mistaken for something that must be dodged.")
    for sign in (-1, 1):
        x = sign * (ROAD_WIDTH / 2 + 0.35)
        m.add(box((0.45, 5.40, 0.60), (x, 2.70, 0)), "MAT_Dark_Chassis", f"pillar_{sign}")
        m.add(box((0.14, 5.00, 0.10), (x - sign * 0.24, 2.70, -0.32)), "MAT_Neon_Cyan", f"pillar_light_{sign}")
    m.add(box((ROAD_WIDTH + 1.6, 0.60, 0.60), (0, 5.70, 0)), "MAT_Dark_Chassis", "lintel")
    m.add(box((ROAD_WIDTH + 1.6, 0.14, 0.10), (0, 5.42, -0.32)), "MAT_Neon_Magenta", "lintel_light_lo")
    m.add(box((ROAD_WIDTH + 1.6, 0.14, 0.10), (0, 5.98, -0.32)), "MAT_Neon_Magenta", "lintel_light_hi")
    out.append(m)

    return out


# ===========================================================================
# STORE / ECONOMY
# ===========================================================================

def store():
    out = []

    # --- Boards. ItemKind.Board in StoreItem.cs -------------------------------
    def _board(name, deck_mat, glow_mat, function, notes, prism_nose=False):
        m = Model(name, "Store", function,
                  "Deck centre, y = 0 at the deck's mid-plane", notes)
        m.add(chamfer_box((0.44, 0.09, 1.16), 0.035), deck_mat, "deck")
        m.add(wedge((0.40, 0.08, 0.28), (0, 0.01, -0.65)), deck_mat, "nose")
        m.add(wedge((0.40, 0.08, 0.28), (0, 0.01, 0.65)), deck_mat, "tail")
        # Underside thrusters: the light source that sells 'hover' when the
        # board is lit from below in the store's turntable view.
        for z in (-0.42, 0.0, 0.42):
            m.add(box((0.34, 0.05, 0.20), (0, -0.065, z)), glow_mat, f"thruster_{z}")
        for sign in (-1, 1):
            m.add(box((0.04, 0.05, 1.04), (sign * 0.215, 0.0, 0)), glow_mat, f"rail_{sign}")
        if prism_nose:
            m.add(octahedron((0.22, 0.14, 0.32), (0, 0.06, -0.50)), "MAT_Neon_White", "prism")
        return m

    out.append(_board(
        "STR_Board_Pulse", "MAT_Metal_Steel", "MAT_Neon_Cyan",
        "Store item 'board_pulse' (4,000 coins).",
        "Boards are pure cosmetics with no gameplay effect, so they are modelled "
        "as a display prop first: readable from the store's three-quarter angle, "
        "with the lit surfaces on the underside where a turntable shows them off."))

    out.append(_board(
        "STR_Board_Prism", "MAT_Dark_Void", "MAT_Neon_Lilac",
        "Store item 'board_prism' (500 gems).",
        "The premium tier gets a refractive nose crystal — a silhouette change, "
        "not just a recolour, because a gem-priced item that differs only in hue "
        "reads as a bad deal next to a coin-priced one.",
        prism_nose=True))

    # --- Currency icons -------------------------------------------------------
    m = Model(
        "STR_Gem", "Store",
        "The premium currency (CurrencyType.Gems). Used in the store, HUD and reward popups.",
        "Gem centre",
        "A cut octahedron with a crown of facets: eight tris for the core plus "
        "the girdle. At HUD size this is fewer triangles than the quad a sprite "
        "would need, and it can spin, which a sprite cannot.")
    m.add(octahedron((0.46, 0.62, 0.46)), "MAT_Glass_Tint", "core")
    m.add(prism(0.24, 0.07, sides=8, axis="y", centre=(0, 0.05, 0)), "MAT_Neon_Blue", "girdle")
    m.add(octahedron((0.20, 0.28, 0.20), (0, 0.02, 0)), "MAT_Neon_White", "fire")
    out.append(m)

    m = Model(
        "STR_CoinStack", "Store",
        "Soft-currency icon for CurrencyPack items (gems_100 ... gems_5000) and reward popups.",
        "Base of the stack, y = 0",
        "Four coins, not one: a pack is a quantity and the stack says so "
        "without a number. Reuses PU_Coin's exact profile so the shop icon and "
        "the thing picked up in a run are visibly the same object.")
    for i in range(4):
        d = beveled_disc(0.35, 0.06, bevel=0.28, sections=20, axis="y",
                         centre=(0.02 * (i % 2) - 0.01, 0.030 + i * 0.062, 0.015 * (i % 3) - 0.01))
        m.add(rot(d, i * 17, [0, 1, 0]), "MAT_Neon_Gold", f"coin_{i}")
    out.append(m)

    m = Model(
        "STR_Chest_Bundle", "Store",
        "Bundle icon for 'bundle_starter' and the StarterPackOffer screen.",
        "Base centre, y = 0",
        "A chest with the lid cracked and light escaping. The offer screen has "
        "one job — communicate 'more than one thing inside' — and an open lid "
        "does it in the 300 ms before the player reads any text.")
    m.add(chamfer_box((0.80, 0.44, 0.56), 0.04, (0, 0.22, 0)), "MAT_Dark_Chassis", "base")
    m.add(box((0.84, 0.05, 0.60), (0, 0.445, 0)), "MAT_Neon_Gold", "rim")
    m.add(box((0.16, 0.46, 0.62), (0, 0.22, 0)), "MAT_Neon_Gold", "strap")

    # The lid hinges on the back top edge. The first attempt rotated a lid whose
    # own centre sat 30 cm away from that hinge, so it swung through the box and
    # landed flat on top — the chest read as a table with a gold tablecloth.
    # Modelling the lid closed, directly above the rim, then rotating about the
    # actual hinge point is both correct and far easier to reason about.
    hinge = (0, 0.44, 0.28)
    lid = chamfer_box((0.82, 0.10, 0.58), 0.03, (0, 0.50, 0))
    m.add(rot(lid, 55, [1, 0, 0], hinge), "MAT_Dark_Chassis", "lid")
    m.add(rot(box((0.84, 0.04, 0.60), (0, 0.555, 0)), 55, [1, 0, 0], hinge),
          "MAT_Neon_Gold", "lid_trim")

    # The light escaping the opening, set just below the rim so it reads as
    # coming from inside rather than as a slab resting on top.
    m.add(box((0.62, 0.05, 0.40), (0, 0.425, -0.02)), "MAT_Neon_White", "spill")
    out.append(m)

    m = Model(
        "STR_Crown_Vip", "Store",
        "Icon for the VIP subscription ('vip_monthly') and the VipScreen.",
        "Base centre, y = 0",
        "Crown rather than a badge: subscription is status, and the item sits "
        "next to consumables in the same list where it needs to look categorically "
        "different at a glance.")
    m.add(prism(0.30, 0.10, sides=8, axis="y", centre=(0, 0.05, 0)), "MAT_Neon_Gold", "band")
    for i in range(8):
        a = i * np.pi / 4
        x, z = np.cos(a) * 0.27, np.sin(a) * 0.27
        m.add(pyramid(0.16, 0.24, (x, 0.10, z)), "MAT_Neon_Gold", f"point_{i}")
    m.add(octahedron((0.16, 0.20, 0.16), (0, 0.16, 0)), "MAT_Neon_Magenta", "jewel")
    out.append(m)

    m = Model(
        "STR_Badge_NoAds", "Store",
        "Icon for the 'no_ads' IAP (ItemKind.AdRemoval).",
        "Badge centre; disc axis along Z, square to the camera",
        "A struck disc with a bar across it. Reads as a prohibition sign at "
        "icon size, where a literal 'no advertisement' illustration would not.")
    m.add(beveled_disc(0.34, 0.10, bevel=0.30, sections=20, axis="z"), "MAT_Metal_Steel", "disc")
    m.add(beveled_disc(0.27, 0.12, bevel=0.30, sections=20, axis="z"), "MAT_Dark_Chassis", "field")
    m.add(prism(0.22, 0.09, sides=4, axis="z", centre=(0, 0, -0.05)), "MAT_Neon_White", "screen")
    m.add(rot(box((0.62, 0.09, 0.06), (0, 0, -0.10)), 38, [0, 0, 1]), "MAT_Neon_Magenta", "slash")
    out.append(m)

    return out
