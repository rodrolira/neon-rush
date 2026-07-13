using NeonRush.Domain.Run;

namespace NeonRush.Application.Events
{
    // Every gameplay event is a readonly struct, never a class. Publishing must not allocate:
    // CoinCollected alone fires tens of times per second, and a per-event heap allocation is a
    // guaranteed GC spike on a low-end Android device, mid-run, when the player is least willing
    // to forgive a dropped frame. See Core/Events/EventBus.cs.
    //
    // These are plain structs with constructors rather than `record struct` because Unity 6
    // compiles against .NET Standard 2.1, which lacks IsExternalInit and therefore cannot use
    // init-only setters without a shim.

    /// <summary>Published the instant a run begins, before the first frame is simulated.</summary>
    public readonly struct RunStarted
    {
        /// <summary>1-based index of this run within the current session.</summary>
        public readonly int RunNumber;

        public RunStarted(int runNumber) => RunNumber = runNumber;
    }

    /// <summary>Why a run ended. Drives both the death screen and the churn funnel in analytics.</summary>
    public enum DeathCause
    {
        /// <summary>The player ran into an obstacle.</summary>
        HitObstacle = 0,

        /// <summary>The player quit deliberately (pause → quit). Not a failure; do not offer a revive.</summary>
        Quit = 1,
    }

    /// <summary>Published once, when a run ends. Carries everything needed to score, reward and report it.</summary>
    public readonly struct RunEnded
    {
        public readonly int RunNumber;
        public readonly float DistanceMetres;
        public readonly int CoinsCollected;
        public readonly int Score;
        public readonly DeathCause Cause;

        /// <summary>How long the run actually lasted. Measured on the monotonic clock, so it cannot be inflated by changing the device time.</summary>
        public readonly float DurationSeconds;

        public RunEnded(int runNumber, float distanceMetres, int coinsCollected, int score, DeathCause cause, float durationSeconds)
        {
            RunNumber = runNumber;
            DistanceMetres = distanceMetres;
            CoinsCollected = coinsCollected;
            Score = score;
            Cause = cause;
            DurationSeconds = durationSeconds;
        }
    }

    /// <summary>
    /// A coin was picked up. Consumed independently by the wallet, the mission tracker, analytics,
    /// audio and the HUD — none of which know the others are listening.
    /// </summary>
    public readonly struct CoinCollected
    {
        /// <summary>Soft-currency value of this coin (a magnet-boosted or seasonal coin may be worth more than 1).</summary>
        public readonly int Value;

        /// <summary>Running total for this run. Included so subscribers do not have to keep their own count.</summary>
        public readonly int TotalThisRun;

        public CoinCollected(int value, int totalThisRun)
        {
            Value = value;
            TotalThisRun = totalThisRun;
        }
    }

    /// <summary>The player moved between lanes. Published on the decision, not on arrival.</summary>
    public readonly struct LaneChanged
    {
        public readonly Lane From;
        public readonly Lane To;

        public LaneChanged(Lane from, Lane to)
        {
            From = from;
            To = to;
        }
    }

    /// <summary>The player left the ground.</summary>
    public readonly struct PlayerJumped
    {
    }

    /// <summary>The player started a slide.</summary>
    public readonly struct PlayerSlid
    {
    }

    /// <summary>
    /// Published every whole 100 m. Missions ("run 10 km"), audio stingers and difficulty
    /// telemetry hang off this rather than polling distance every frame.
    /// </summary>
    public readonly struct DistanceMilestone
    {
        public readonly int Metres;

        public DistanceMilestone(int metres) => Metres = metres;
    }
}
