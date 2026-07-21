using System;
using System.Collections.Generic;
using NeonRush.Application.Events;
using NeonRush.Application.Missions;
using NeonRush.Core.Events;
using NeonRush.Domain.Economy;

namespace NeonRush.Application.Stages
{
    /// <summary>
    /// The stage campaign: a ladder of numbered stages, each a set of objectives that must all be
    /// completed to clear it, pay its reward, and advance to the next.
    ///
    /// Like the daily missions, progress is driven entirely by events the game already publishes —
    /// this class adds a whole progression system without touching a line of gameplay code. Unlike the
    /// daily missions, stage progress is <b>cumulative and permanent</b>: it never resets on a new day,
    /// it only advances, and it persists until a stage is cleared. That is the difference in feel —
    /// dailies are a today thing, the campaign is the long road.
    ///
    /// No Unity types, so the advancement and reward rules are unit-tested by pushing events through a
    /// bus, exactly the way MissionService is.
    /// </summary>
    public sealed class StageService : IDisposable
    {
        private readonly Wallet _wallet;
        private readonly IEventBus _bus;
        private readonly IReadOnlyList<Stage> _ladder;
        private readonly List<IDisposable> _subscriptions = new();

        /// <summary>0-based index of the current stage. Equal to the ladder length once every stage is cleared.</summary>
        private int _index;

        /// <summary>Progress per objective of the current stage. Empty once the campaign is complete.</summary>
        private int[] _progress;

        public StageService(Wallet wallet, IEventBus bus, IReadOnlyList<Stage> ladder = null)
        {
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
            _ladder = ladder ?? StageLadder.Default();

            if (_ladder.Count == 0) throw new ArgumentException("The stage ladder is empty.", nameof(ladder));

            _progress = new int[_ladder[0].Objectives.Count];

            _subscriptions.Add(_bus.Subscribe<CoinCollected>(e => Advance(MissionMetric.CollectCoins, e.Value)));
            _subscriptions.Add(_bus.Subscribe<PlayerJumped>(_ => Advance(MissionMetric.Jump, 1)));
            _subscriptions.Add(_bus.Subscribe<PlayerSlid>(_ => Advance(MissionMetric.Slide, 1)));
            _subscriptions.Add(_bus.Subscribe<DistanceMilestone>(_ => Advance(MissionMetric.TravelDistance, 100)));
            _subscriptions.Add(_bus.Subscribe<RunEnded>(OnRunEnded));
        }

        /// <summary>True once every stage has been cleared.</summary>
        public bool IsAllComplete => _index >= _ladder.Count;

        /// <summary>The stage the player is working on, or null once the whole campaign is complete.</summary>
        public Stage CurrentStage => IsAllComplete ? null : _ladder[_index];

        /// <summary>1-based number of the current stage. One past the last stage number when all are complete.</summary>
        public int CurrentStageNumber => _index + 1;

        /// <summary>Progress on the current stage's objective at <paramref name="objectiveIndex"/>.</summary>
        public int ProgressAt(int objectiveIndex) =>
            objectiveIndex >= 0 && objectiveIndex < _progress.Length ? _progress[objectiveIndex] : 0;

        /// <summary>Snapshot of the current stage's per-objective progress, for the save.</summary>
        public IReadOnlyList<int> ProgressSnapshot() => (int[])_progress.Clone();

        /// <summary>
        /// Restores a saved stage and its progress. Defensive against a ladder that changed shape since
        /// the save was written: a saved stage past the (new) end means the campaign is complete, and a
        /// progress array of the wrong length is clamped rather than trusted.
        /// </summary>
        public void Restore(int stageNumber, IReadOnlyList<int> savedProgress)
        {
            _index = Math.Max(0, stageNumber - 1);

            if (_index >= _ladder.Count)
            {
                _index = _ladder.Count; // all complete
                _progress = Array.Empty<int>();
                return;
            }

            var objectives = _ladder[_index].Objectives;
            _progress = new int[objectives.Count];

            if (savedProgress == null) return;

            for (var i = 0; i < _progress.Length && i < savedProgress.Count; i++)
            {
                _progress[i] = Math.Clamp(savedProgress[i], 0, objectives[i].Target);
            }
        }

        private void OnRunEnded(RunEnded e)
        {
            Advance(MissionMetric.CompleteRuns, 1);
            AdvanceHighWater(MissionMetric.SingleRunDistance, (int)e.DistanceMetres);
        }

        private void Advance(MissionMetric metric, int amount)
        {
            if (IsAllComplete || amount <= 0) return;

            var moved = false;
            var objectives = _ladder[_index].Objectives;

            for (var i = 0; i < objectives.Count; i++)
            {
                if (objectives[i].Metric != metric || objectives[i].IsHighWater) continue;
                if (_progress[i] >= objectives[i].Target) continue;

                _progress[i] = Math.Min(objectives[i].Target, _progress[i] + amount);
                moved = true;
            }

            if (moved) AfterAdvance();
        }

        private void AdvanceHighWater(MissionMetric metric, int value)
        {
            if (IsAllComplete) return;

            var moved = false;
            var objectives = _ladder[_index].Objectives;

            for (var i = 0; i < objectives.Count; i++)
            {
                if (objectives[i].Metric != metric || !objectives[i].IsHighWater) continue;
                if (_progress[i] >= objectives[i].Target) continue;

                if (value > _progress[i])
                {
                    _progress[i] = Math.Min(objectives[i].Target, value);
                    moved = true;
                }
            }

            if (moved) AfterAdvance();
        }

        private void AfterAdvance()
        {
            _bus.Publish(new StageProgressed(CurrentStageNumber));

            if (!AllObjectivesComplete()) return;

            var stage = _ladder[_index];

            // Reward BEFORE advancing the index, so a subscriber reacting to the credit reads the
            // stage that was actually cleared. Coins and gems both route through the wallet, tagged as
            // a progression reward so the economy dashboard can attribute them.
            if (stage.RewardCoins > 0)
            {
                _wallet.Credit(CurrencyType.Coins, stage.RewardCoins, TransactionReason.MissionReward);
            }

            if (stage.RewardGems > 0)
            {
                _wallet.Credit(CurrencyType.Gems, stage.RewardGems, TransactionReason.MissionReward);
            }

            _bus.Publish(new StageCompleted(stage.Number, stage.RewardCoins, stage.RewardGems));

            _index++;
            _progress = IsAllComplete ? Array.Empty<int>() : new int[_ladder[_index].Objectives.Count];

            // Announce the new current stage (or the completed campaign) so the menu redraws.
            _bus.Publish(new StageProgressed(CurrentStageNumber));
        }

        private bool AllObjectivesComplete()
        {
            var objectives = _ladder[_index].Objectives;

            for (var i = 0; i < objectives.Count; i++)
            {
                if (_progress[i] < objectives[i].Target) return false;
            }

            return true;
        }

        public void Dispose()
        {
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
        }
    }
}
