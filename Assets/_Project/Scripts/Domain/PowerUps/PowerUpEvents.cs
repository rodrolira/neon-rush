namespace NeonRush.Domain.PowerUps
{
    // Readonly structs, never classes: these fire mid-run and must not allocate. Same discipline as
    // the gameplay events in Application/Events/GameplayEvents.cs.

    /// <summary>
    /// A pickup was grabbed off the track. Published before the effect is applied, so audio and
    /// analytics see the grab itself. The HUD listens to <see cref="PowerUpActivated"/> instead,
    /// because what it shows is the running effect, not the instant of collection.
    /// </summary>
    public readonly struct PowerUpCollected
    {
        public readonly PowerUpType Type;

        public PowerUpCollected(PowerUpType type) => Type = type;
    }

    /// <summary>
    /// A power-up effect started (or was refreshed). Carries the duration so the HUD can draw a
    /// countdown without knowing the config. Shield reports a duration of 0 — it is a charge, not a
    /// timer — and <see cref="Charges"/> instead.
    /// </summary>
    public readonly struct PowerUpActivated
    {
        public readonly PowerUpType Type;

        /// <summary>Seconds the effect will last. Zero for the shield, which is charge-based.</summary>
        public readonly float DurationSeconds;

        /// <summary>Shield charges now banked. Zero for the timed effects.</summary>
        public readonly int Charges;

        public PowerUpActivated(PowerUpType type, float durationSeconds, int charges)
        {
            Type = type;
            DurationSeconds = durationSeconds;
            Charges = charges;
        }
    }

    /// <summary>A timed power-up ran out. The HUD clears its indicator; audio can play a fade stinger.</summary>
    public readonly struct PowerUpExpired
    {
        public readonly PowerUpType Type;

        public PowerUpExpired(PowerUpType type) => Type = type;
    }

    /// <summary>
    /// A shield charge absorbed an obstacle hit the player would otherwise have died to. Drives the
    /// break-flash, the sound, and the HUD charge count going down.
    /// </summary>
    public readonly struct ShieldConsumed
    {
        /// <summary>Charges still banked after this one was spent.</summary>
        public readonly int ChargesRemaining;

        public ShieldConsumed(int chargesRemaining) => ChargesRemaining = chargesRemaining;
    }
}
