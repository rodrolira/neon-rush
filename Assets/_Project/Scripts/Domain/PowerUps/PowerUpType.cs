namespace NeonRush.Domain.PowerUps
{
    /// <summary>
    /// The pickups a player can grab during a run. Each is a distinct verb, not just a different
    /// colour: a magnet changes how you collect, a shield changes how you fail, a score boost changes
    /// what a stretch of track is worth. Three mechanics, not three timers.
    /// </summary>
    public enum PowerUpType
    {
        /// <summary>Pulls nearby coins to the player for a few seconds. A timed effect.</summary>
        Magnet = 0,

        /// <summary>Absorbs the next obstacle hit instead of dying. A charge, consumed on impact — not timed.</summary>
        Shield = 1,

        /// <summary>Multiplies the distance score for a few seconds. A timed effect.</summary>
        DoubleScore = 2,
    }
}
