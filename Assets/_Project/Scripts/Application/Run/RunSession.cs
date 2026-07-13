using System;
using NeonRush.Application.Events;
using NeonRush.Core.Events;
using NeonRush.Domain.Run;

namespace NeonRush.Application.Run
{
    /// <summary>
    /// The authoritative state of a single run: how far, how fast, how many coins, alive or dead.
    ///
    /// This class contains no Unity types. That is the entire point — the rules that decide how
    /// difficulty ramps, when milestones fire, and what a run is finally worth are the rules most
    /// likely to be retuned and most expensive to get wrong, so they must be testable in
    /// milliseconds without an Editor, a device or a GPU. The MonoBehaviour that drives this
    /// (RunController) does nothing but feed it a delta time.
    ///
    /// Not thread-safe: driven from Unity's main loop only.
    /// </summary>
    public sealed class RunSession
    {
        /// <summary>Distance between <see cref="DistanceMilestone"/> events, in metres.</summary>
        private const int MilestoneIntervalMetres = 100;

        private readonly RunTuning _tuning;
        private readonly IEventBus _bus;

        /// <summary>The last milestone already published, so each one fires exactly once.</summary>
        private int _lastMilestone;

        public RunSession(RunTuning tuning, IEventBus bus)
        {
            _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));

            _tuning.Validate();
        }

        /// <summary>1-based index of the current run within this session.</summary>
        public int RunNumber { get; private set; }

        /// <summary>Metres travelled in the current run.</summary>
        public float Distance { get; private set; }

        /// <summary>Current forward speed, in metres/second.</summary>
        public float Speed { get; private set; }

        /// <summary>Coins collected in the current run.</summary>
        public int Coins { get; private set; }

        /// <summary>Score accumulated in the current run.</summary>
        public int Score { get; private set; }

        /// <summary>Seconds elapsed in the current run, on the monotonic clock.</summary>
        public float Duration { get; private set; }

        /// <summary>True between <see cref="Begin"/> and <see cref="End"/>.</summary>
        public bool IsRunning { get; private set; }

        /// <summary>
        /// True while the player is still inside the opening grace period, during which no
        /// obstacles spawn. The spawner reads this rather than duplicating the distance check.
        /// </summary>
        public bool InSafeStart => Distance < _tuning.SafeStartDistance;

        /// <summary>
        /// Score accumulates as a float internally and is exposed as an int. Without this,
        /// a run at 1 score/metre and 60 fps would add ~0.15 per frame, truncate to 0, and the
        /// player's score would stay at zero forever.
        /// </summary>
        private float _scoreAccumulator;

        /// <summary>Starts a new run and resets all per-run state.</summary>
        public void Begin()
        {
            if (IsRunning)
            {
                throw new InvalidOperationException(
                    "Begin() called while a run is already in progress. End() it first.");
            }

            RunNumber++;
            Distance = 0f;
            Coins = 0;
            Score = 0;
            Duration = 0f;
            Speed = _tuning.BaseSpeed;
            _scoreAccumulator = 0f;
            _lastMilestone = 0;
            IsRunning = true;

            _bus.Publish(new RunStarted(RunNumber));
        }

        /// <summary>
        /// Advances the run by <paramref name="deltaTime"/> seconds.
        /// </summary>
        /// <param name="deltaTime">
        /// Frame delta, in seconds. Must come from a monotonic source. It is clamped defensively:
        /// a device that hitches (or is suspended and resumed) can hand Unity a delta of several
        /// seconds, which would teleport the player through a wall of obstacles and produce a
        /// death the player could not possibly have avoided.
        /// </param>
        public void Tick(float deltaTime)
        {
            if (!IsRunning) return;

            if (deltaTime <= 0f) return;
            if (deltaTime > MaxDeltaTime) deltaTime = MaxDeltaTime;

            Duration += deltaTime;

            // Speed is a function of distance, not of time, so the difficulty curve is identical
            // whether the player is on a 120 Hz flagship or a 30 fps budget phone. Ramping on time
            // would make the game measurably harder on faster hardware.
            Speed = SpeedAt(Distance);

            Distance += Speed * deltaTime;

            _scoreAccumulator += Speed * deltaTime * _tuning.ScorePerMetre;
            Score = (int)_scoreAccumulator + Coins * _tuning.ScorePerCoin;

            PublishMilestonesUpTo(Distance);
        }

        /// <summary>
        /// Forward speed at a given distance: linear ramp from base speed, hard-clamped at max.
        /// Pure and static, so the difficulty curve can be asserted in a unit test without
        /// constructing a session.
        /// </summary>
        public float SpeedAt(float distance)
        {
            var speed = _tuning.BaseSpeed + distance * _tuning.SpeedGainPerMetre;
            return speed > _tuning.MaxSpeed ? _tuning.MaxSpeed : speed;
        }

        /// <summary>Records a collected coin and publishes it.</summary>
        public void CollectCoin(int value = 1)
        {
            if (!IsRunning) return;
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value), "Coin value must be positive.");

            Coins += value;
            Score = (int)_scoreAccumulator + Coins * _tuning.ScorePerCoin;

            _bus.Publish(new CoinCollected(value, Coins));
        }

        /// <summary>Ends the run and publishes <see cref="RunEnded"/>. Idempotent: a second call is ignored.</summary>
        public void End(DeathCause cause)
        {
            // Idempotent on purpose. A player can plausibly clip two obstacles in the same physics
            // step; without this guard that fires RunEnded twice, which double-credits coins,
            // double-counts the run in analytics, and shows the death screen on top of itself.
            if (!IsRunning) return;

            IsRunning = false;

            _bus.Publish(new RunEnded(
                runNumber: RunNumber,
                distanceMetres: Distance,
                coinsCollected: Coins,
                score: Score,
                cause: cause,
                durationSeconds: Duration));
        }

        /// <summary>
        /// Fires one <see cref="DistanceMilestone"/> per interval crossed.
        ///
        /// The loop matters: at 26 m/s a single long frame can cross more than one 100 m boundary,
        /// and a mission that counts milestones would silently under-count if we only ever fired
        /// the most recent one.
        /// </summary>
        private void PublishMilestonesUpTo(float distance)
        {
            var reached = (int)(distance / MilestoneIntervalMetres) * MilestoneIntervalMetres;

            while (_lastMilestone < reached)
            {
                _lastMilestone += MilestoneIntervalMetres;
                _bus.Publish(new DistanceMilestone(_lastMilestone));
            }
        }

        /// <summary>
        /// Upper bound on a single simulation step, in seconds (~6 frames at 60 fps).
        /// Anything larger is a hitch or an app resume, not real gameplay time.
        /// </summary>
        private const float MaxDeltaTime = 0.1f;
    }
}
