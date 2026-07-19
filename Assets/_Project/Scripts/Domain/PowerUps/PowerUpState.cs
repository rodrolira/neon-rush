using System;

namespace NeonRush.Domain.PowerUps
{
    /// <summary>
    /// The live state of the player's power-ups during a run: which timed effects are active and for
    /// how much longer, and how many shield charges are banked.
    ///
    /// Pure, tickable, and unit-tested. The timers and the shield count are the kind of thing that is
    /// tedious to verify by hand in a running game ("did the magnet really last exactly six seconds?",
    /// "did picking up a second shield stack or replace the first?") and trivial to verify in a test.
    /// So the rules live here, with no Unity dependency, and the presentation layer only reads them.
    ///
    /// Two deliberately different mechanics share this one type, because they ARE different:
    ///  · Magnet and DoubleScore are <b>timed</b> — they run down and expire.
    ///  · Shield is a <b>charge</b> — it does not tick down; it waits until a hit consumes it.
    /// </summary>
    public sealed class PowerUpState
    {
        private float _magnetRemaining;
        private float _doubleScoreRemaining;
        private int _shieldCharges;

        /// <summary>True while the coin magnet is pulling.</summary>
        public bool IsMagnetActive => _magnetRemaining > 0f;

        /// <summary>True while the score multiplier is running.</summary>
        public bool IsDoubleScoreActive => _doubleScoreRemaining > 0f;

        /// <summary>True while at least one shield charge is banked.</summary>
        public bool HasShield => _shieldCharges > 0;

        /// <summary>Banked shield charges. Each absorbs one obstacle hit.</summary>
        public int ShieldCharges => _shieldCharges;

        /// <summary>Seconds of magnet left. For the HUD countdown.</summary>
        public float MagnetRemaining => _magnetRemaining;

        /// <summary>Seconds of score multiplier left. For the HUD countdown.</summary>
        public float DoubleScoreRemaining => _doubleScoreRemaining;

        /// <summary>
        /// Starts or refreshes the magnet. Picking up a second magnet never shortens the first — it
        /// extends to whichever leaves the player better off — so a pickup is never a downgrade.
        /// </summary>
        public void ActivateMagnet(float durationSeconds)
        {
            RequirePositive(durationSeconds, nameof(durationSeconds));
            _magnetRemaining = Math.Max(_magnetRemaining, durationSeconds);
        }

        /// <summary>Starts or refreshes the score multiplier. Same never-a-downgrade rule as the magnet.</summary>
        public void ActivateDoubleScore(float durationSeconds)
        {
            RequirePositive(durationSeconds, nameof(durationSeconds));
            _doubleScoreRemaining = Math.Max(_doubleScoreRemaining, durationSeconds);
        }

        /// <summary>Banks shield charges. Charges stack — a second shield means two hits absorbed.</summary>
        public void AddShield(int charges = 1)
        {
            if (charges <= 0) throw new ArgumentOutOfRangeException(nameof(charges), "Must add at least one charge.");
            _shieldCharges += charges;
        }

        /// <summary>
        /// Spends one shield charge if any are banked. Returns true when a charge was consumed (the
        /// hit is absorbed), false when there was none (the hit is fatal). This is the whole contract
        /// the collision system leans on to decide life or death.
        /// </summary>
        public bool TryConsumeShield()
        {
            if (_shieldCharges <= 0) return false;

            _shieldCharges--;
            return true;
        }

        /// <summary>Runs the timed effects down by one frame. Charges are untouched — they are not timed.</summary>
        public void Tick(float deltaTime)
        {
            if (deltaTime <= 0f) return;

            if (_magnetRemaining > 0f)
            {
                _magnetRemaining -= deltaTime;
                if (_magnetRemaining < 0f) _magnetRemaining = 0f;
            }

            if (_doubleScoreRemaining > 0f)
            {
                _doubleScoreRemaining -= deltaTime;
                if (_doubleScoreRemaining < 0f) _doubleScoreRemaining = 0f;
            }
        }

        /// <summary>Clears everything. Called at the start and end of every run so nothing leaks between runs.</summary>
        public void Reset()
        {
            _magnetRemaining = 0f;
            _doubleScoreRemaining = 0f;
            _shieldCharges = 0;
        }

        private static void RequirePositive(float value, string name)
        {
            if (value <= 0f) throw new ArgumentOutOfRangeException(name, "Duration must be positive.");
        }
    }
}
