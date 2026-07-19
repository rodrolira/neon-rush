namespace NeonRush.Domain.Run
{
    /// <summary>
    /// The physical size and placement of one obstacle kind, in metres.
    ///
    /// None of these numbers are arbitrary. Each is tuned against the player's jump and slide (see
    /// <see cref="RunTuning"/>) so the kind is passable by exactly one action and no other. Working
    /// from the defaults — jump apex ≈ 1.13 m above the feet, slide box top 0.72 m, standing box top
    /// 1.6 m — the geometry is chosen so that:
    ///
    ///  · <b>LowJump</b> sits on the ground, low enough that the jump arc lifts the player's box clear
    ///    over it, but not so low that a slide (which also hugs the ground) can pass it. Jump only.
    ///  · <b>HighSlide</b> hangs in the air with a gap beneath tall enough for a slide, yet low enough
    ///    that running or jumping drives the player's box into it. Slide only.
    ///  · <b>FullBlock</b> is taller than the jump apex and reaches the floor, so neither jump nor
    ///    slide clears it. Dodge only.
    ///
    /// This struct is also the contract the real 3D models must honour. A replacement mesh for a kind
    /// has to fit inside this box: the greybox collision is tuned to these dimensions, so a model that
    /// spills outside them produces deaths to a hitbox the player cannot see — the exact "the game
    /// cheated me" feeling the shrunk hitbox in CollisionSystem exists to prevent.
    /// </summary>
    public readonly struct ObstacleArchetype
    {
        public readonly ObstacleKind Kind;

        /// <summary>Full box size in metres: X across lanes, Y vertical, Z along the track.</summary>
        public readonly float Width;
        public readonly float Height;
        public readonly float Depth;

        /// <summary>World-space Y of the box centre. Grounded kinds sit at Height/2; hanging kinds float higher.</summary>
        public readonly float CentreY;

        private ObstacleArchetype(ObstacleKind kind, float width, float height, float depth, float centreY)
        {
            Kind = kind;
            Width = width;
            Height = height;
            Depth = depth;
            CentreY = centreY;
        }

        /// <summary>Bottom edge of the box, in metres above the ground.</summary>
        public float Bottom => CentreY - Height * 0.5f;

        /// <summary>Top edge of the box, in metres above the ground.</summary>
        public float Top => CentreY + Height * 0.5f;

        /// <summary>Returns the tuned geometry for a kind.</summary>
        public static ObstacleArchetype For(ObstacleKind kind) => kind switch
        {
            // Short and grounded: spans 0.0 m to 0.7 m. The jump apex (feet ≈ 1.13 m) lifts the
            // player's box clear over the top; a slide (box top 0.72 m) still overlaps it and fails —
            // so the only move that works is a jump.
            ObstacleKind.LowJump => new ObstacleArchetype(kind, 1.8f, 0.7f, 1.2f, 0.35f),

            // Floats from 1.1 m to 2.1 m, leaving a gap beneath. A slide (box top 0.72 m) passes under
            // with room to spare; standing (box top 1.6 m) or jumping (the box rises whole, its top
            // higher still) both drive the player into it — so the only move that works is a slide.
            ObstacleKind.HighSlide => new ObstacleArchetype(kind, 1.8f, 1.0f, 1.2f, 1.6f),

            // Full wall from the floor to 1.6 m: taller than the jump apex and grounded, so neither a
            // jump nor a slide clears it. The only escape is sideways.
            ObstacleKind.FullBlock => new ObstacleArchetype(kind, 1.8f, 1.6f, 1.2f, 0.8f),

            _ => new ObstacleArchetype(ObstacleKind.FullBlock, 1.8f, 1.6f, 1.2f, 0.8f),
        };
    }
}
