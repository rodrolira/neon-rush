using System;
using System.Collections.Generic;
using NeonRush.Application.Events;
using NeonRush.Core.Events;
using NeonRush.Domain.Economy;
using NeonRush.Domain.Ports;

namespace NeonRush.Application.Missions
{
    /// <summary>What a mission counts.</summary>
    public enum MissionMetric
    {
        /// <summary>Coins collected, cumulative across runs.</summary>
        CollectCoins = 0,

        /// <summary>Metres travelled, cumulative across runs.</summary>
        TravelDistance = 1,

        /// <summary>Jumps performed, cumulative.</summary>
        Jump = 2,

        /// <summary>Slides performed, cumulative.</summary>
        Slide = 3,

        /// <summary>Runs finished (any distance).</summary>
        CompleteRuns = 4,

        /// <summary>Best single-run distance. Progress is a high-water mark, not a sum.</summary>
        SingleRunDistance = 5,
    }

    /// <summary>An immutable mission template from the pool.</summary>
    public sealed class MissionDefinition
    {
        public MissionDefinition(string id, string description, MissionMetric metric, int target, int rewardCoins)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Id required.", nameof(id));
            if (target <= 0) throw new ArgumentOutOfRangeException(nameof(target));
            if (rewardCoins <= 0) throw new ArgumentOutOfRangeException(nameof(rewardCoins));

            Id = id;
            Description = description ?? id;
            Metric = metric;
            Target = target;
            RewardCoins = rewardCoins;
        }

        public string Id { get; }
        public string Description { get; }
        public MissionMetric Metric { get; }
        public int Target { get; }
        public int RewardCoins { get; }
    }

    /// <summary>A live mission: a definition plus today's progress.</summary>
    public sealed class MissionState
    {
        public MissionState(MissionDefinition definition, int progress = 0, bool rewarded = false)
        {
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            Progress = Math.Max(0, progress);
            Rewarded = rewarded;
        }

        public MissionDefinition Definition { get; }

        public int Progress { get; internal set; }

        /// <summary>True once the reward has been paid. Guards against double-crediting on reload.</summary>
        public bool Rewarded { get; internal set; }

        public bool IsComplete => Progress >= Definition.Target;
    }

    /// <summary>A mission hit its target. Analytics and the UI listen.</summary>
    public readonly struct MissionCompleted
    {
        public readonly string MissionId;
        public readonly int RewardCoins;

        public MissionCompleted(string missionId, int rewardCoins)
        {
            MissionId = missionId;
            RewardCoins = rewardCoins;
        }
    }

    /// <summary>
    /// The daily missions: three per day, drawn from a pool, progress driven entirely by events the
    /// game already publishes.
    ///
    /// That last part is the architectural payoff worth naming: this class adds mission tracking to
    /// the game without touching a single line of gameplay code. PlayerMotor does not know missions
    /// exist; it publishes PlayerJumped exactly as it did before. If mission tracking ever needs a
    /// new metric, the question is "does the bus already carry it?" — and so far the answer has been
    /// yes every time, which is how you know the event vocabulary was cut at the right joints.
    ///
    /// Design decisions:
    ///
    ///  · <b>Selection is deterministic from the UTC day.</b> Everyone gets the same three missions
    ///    on the same day, which makes them a shared, social thing ("did you finish the jump one?")
    ///    and — the practical reason — makes any player's mission state reproducible from their save
    ///    date alone when a support ticket arrives.
    ///
    ///  · <b>A rolled-back clock never rerolls missions.</b> Refreshing on "day changed" rather than
    ///    "day advanced" would let a player reroll a hard mission set by moving their clock, and
    ///    reroll-until-easy is the classic mission-system exploit.
    ///
    ///  · <b>Rewards auto-credit on completion.</b> A claim button drives re-engagement, but a claim
    ///    button that does not exist yet (no main-menu UI) would make every reward silently
    ///    unclaimable. Auto-credit now; the explicit claim ritual arrives with the menu.
    /// </summary>
    public sealed class MissionService : IDisposable
    {
        /// <summary>Missions active per day.</summary>
        public const int ActiveCount = 3;

        private readonly Wallet _wallet;
        private readonly IClock _clock;
        private readonly IEventBus _bus;
        private readonly List<IDisposable> _subscriptions = new();
        private readonly List<MissionState> _active = new();
        private readonly IReadOnlyList<MissionDefinition> _pool;

        /// <summary>UTC date stamp (days since year 1) the active set was generated for.</summary>
        public int MissionDay { get; private set; }

        public MissionService(Wallet wallet, IClock clock, IEventBus bus,
            IReadOnlyList<MissionDefinition> pool = null)
        {
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
            _pool = pool ?? DefaultPool();

            if (_pool.Count < ActiveCount)
            {
                throw new ArgumentException($"The mission pool needs at least {ActiveCount} entries.", nameof(pool));
            }

            _subscriptions.Add(_bus.Subscribe<CoinCollected>(e => Advance(MissionMetric.CollectCoins, e.Value)));
            _subscriptions.Add(_bus.Subscribe<PlayerJumped>(_ => Advance(MissionMetric.Jump, 1)));
            _subscriptions.Add(_bus.Subscribe<PlayerSlid>(_ => Advance(MissionMetric.Slide, 1)));
            _subscriptions.Add(_bus.Subscribe<DistanceMilestone>(_ => Advance(MissionMetric.TravelDistance, 100)));
            _subscriptions.Add(_bus.Subscribe<RunEnded>(OnRunEnded));
        }

        public IReadOnlyList<MissionState> Active => _active;

        /// <summary>
        /// Regenerates the daily set if a NEW day has arrived. Called at boot and after resume.
        ///
        /// Strictly "day advanced", never "day changed": a clock that moved backwards keeps the
        /// current set, so rerolling-by-clock is structurally impossible rather than discouraged.
        /// </summary>
        public void RefreshIfNewDay()
        {
            var today = DayStamp(_clock.UtcNow);

            if (today <= MissionDay && _active.Count == ActiveCount) return;

            MissionDay = today;
            _active.Clear();

            // Deterministic selection: the day IS the seed. System.Random with a fixed seed is
            // stable for a given runtime, and reproducibility-per-day is all we need.
            var random = new Random(today);
            var indices = new HashSet<int>();

            while (indices.Count < ActiveCount)
            {
                indices.Add(random.Next(_pool.Count));
            }

            foreach (var index in indices)
            {
                _active.Add(new MissionState(_pool[index]));
            }
        }

        /// <summary>Restores persisted progress onto today's missions (matched by id; stale ids are dropped).</summary>
        public void RestoreProgress(int missionDay, IReadOnlyList<(string id, int progress, bool rewarded)> saved)
        {
            if (saved == null) return;

            // Progress only survives within its own day. Yesterday's half-finished mission does not
            // carry into today's fresh set.
            if (missionDay != DayStamp(_clock.UtcNow)) return;

            MissionDay = missionDay;

            foreach (var (id, progress, rewarded) in saved)
            {
                foreach (var mission in _active)
                {
                    if (mission.Definition.Id != id) continue;

                    mission.Progress = Math.Max(0, progress);
                    mission.Rewarded = rewarded;
                }
            }
        }

        private void OnRunEnded(RunEnded e)
        {
            Advance(MissionMetric.CompleteRuns, 1);
            AdvanceHighWater(MissionMetric.SingleRunDistance, (int)e.DistanceMetres);
        }

        private void Advance(MissionMetric metric, int amount)
        {
            if (amount <= 0) return;

            foreach (var mission in _active)
            {
                if (mission.Definition.Metric != metric) continue;
                if (mission.IsComplete) continue;

                mission.Progress = Math.Min(mission.Definition.Target, mission.Progress + amount);

                PayIfComplete(mission);
            }
        }

        /// <summary>High-water metrics record the best single value rather than a sum.</summary>
        private void AdvanceHighWater(MissionMetric metric, int value)
        {
            foreach (var mission in _active)
            {
                if (mission.Definition.Metric != metric) continue;
                if (mission.IsComplete) continue;

                if (value > mission.Progress)
                {
                    mission.Progress = Math.Min(mission.Definition.Target, value);
                }

                PayIfComplete(mission);
            }
        }

        private void PayIfComplete(MissionState mission)
        {
            if (!mission.IsComplete || mission.Rewarded) return;

            // Rewarded is set BEFORE the credit publishes: the credit fires CurrencyChanged, and a
            // subscriber reacting to that must never observe a completed-but-unrewarded mission and
            // pay it a second time.
            mission.Rewarded = true;

            _wallet.Credit(CurrencyType.Coins, mission.Definition.RewardCoins, TransactionReason.MissionReward);
            _bus.Publish(new MissionCompleted(mission.Definition.Id, mission.Definition.RewardCoins));
        }

        /// <summary>Days since 0001-01-01 UTC. A compact, timezone-proof day identity.</summary>
        public static int DayStamp(DateTime utc) => (int)(utc.Date - DateTime.MinValue).TotalDays;

        /// <summary>The shipped pool. Remote Config will extend this per-season later.</summary>
        private static IReadOnlyList<MissionDefinition> DefaultPool() => new[]
        {
            new MissionDefinition("coins_150", "Collect 150 coins", MissionMetric.CollectCoins, 150, 300),
            new MissionDefinition("coins_400", "Collect 400 coins", MissionMetric.CollectCoins, 400, 700),
            new MissionDefinition("distance_2k", "Travel 2,000 m", MissionMetric.TravelDistance, 2_000, 400),
            new MissionDefinition("distance_5k", "Travel 5,000 m", MissionMetric.TravelDistance, 5_000, 900),
            new MissionDefinition("jump_50", "Jump 50 times", MissionMetric.Jump, 50, 300),
            new MissionDefinition("slide_30", "Slide 30 times", MissionMetric.Slide, 30, 300),
            new MissionDefinition("runs_5", "Finish 5 runs", MissionMetric.CompleteRuns, 5, 350),
            new MissionDefinition("sprint_600", "Run 600 m in one run", MissionMetric.SingleRunDistance, 600, 500),
        };

        public void Dispose()
        {
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
        }
    }
}
