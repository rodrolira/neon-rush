using System;
using System.Collections.Generic;
using NeonRush.Core.Events;

namespace NeonRush.Domain.BattlePass
{
    /// <summary>
    /// A flat snapshot of a player's battle-pass progress, for persistence and cloud sync. Dumb by
    /// design — it is a wire format, mapped to and from <see cref="BattlePassState"/> at the edges.
    /// </summary>
    public readonly struct BattlePassSnapshot
    {
        public readonly string SeasonId;
        public readonly int Xp;
        public readonly bool PremiumOwned;
        public readonly IReadOnlyList<int> ClaimedFree;
        public readonly IReadOnlyList<int> ClaimedPremium;

        public BattlePassSnapshot(string seasonId, int xp, bool premiumOwned,
            IReadOnlyList<int> claimedFree, IReadOnlyList<int> claimedPremium)
        {
            SeasonId = seasonId;
            Xp = xp;
            PremiumOwned = premiumOwned;
            ClaimedFree = claimedFree ?? Array.Empty<int>();
            ClaimedPremium = claimedPremium ?? Array.Empty<int>();
        }
    }

    /// <summary>
    /// A player's live progress through the current season, and the rules that govern it.
    ///
    /// The reward-granting itself lives one layer up (BattlePassService applies a claimed reward to
    /// the wallet or inventory): this type only decides <b>what</b> a player is entitled to and marks
    /// it claimed, exactly once. Keeping the "what" pure and Unity-free is what lets the claim rules —
    /// the part that moves real currency and paid entitlements, and so the part most costly to get
    /// wrong — be exhaustively unit-tested with no Editor and no network.
    ///
    /// Two invariants carry the design:
    ///  · A reward is claimable only once. Every claim path routes through the claimed-sets, so a
    ///    double-tap, a replayed input, or a UI that re-fires cannot pay a reward twice.
    ///  · A new season resets progress but NEVER touches owned cosmetics. Those live in the Inventory,
    ///    which this type does not even reference; taking away something a player earned or bought is
    ///    the fastest way to a refund and a one-star review.
    /// </summary>
    public sealed class BattlePassState
    {
        private readonly IEventBus _bus;
        private readonly HashSet<int> _claimedFree = new();
        private readonly HashSet<int> _claimedPremium = new();

        private BattlePassTrack _track;
        private int _xp;
        private bool _premiumOwned;

        public BattlePassState(BattlePassTrack track, IEventBus bus, BattlePassSnapshot? loaded = null)
        {
            _track = track ?? throw new ArgumentNullException(nameof(track));
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));

            // A snapshot from a DIFFERENT season is stale: the player has progress banked against a
            // season that is over. We drop it and start the new season clean — but the cosmetics they
            // unlocked are already in the Inventory and are never in scope here, so nothing is lost.
            if (loaded.HasValue && loaded.Value.SeasonId == _track.SeasonId)
            {
                _xp = Clamp(loaded.Value.Xp);
                _premiumOwned = loaded.Value.PremiumOwned;
                Restore(_claimedFree, loaded.Value.ClaimedFree);
                Restore(_claimedPremium, loaded.Value.ClaimedPremium);
            }
        }

        public BattlePassTrack Track => _track;

        public string SeasonId => _track.SeasonId;

        public int Xp => _xp;

        public bool PremiumOwned => _premiumOwned;

        /// <summary>Total XP that maxes the pass. XP is clamped here so it cannot run away past the top tier.</summary>
        public int MaxXp => _track.TierCount * _track.XpPerTier;

        /// <summary>
        /// The highest tier the player has fully earned, 0..TierCount. Tier N is unlocked (its rewards
        /// claimable) once <see cref="CurrentTier"/> reaches N.
        /// </summary>
        public int CurrentTier
        {
            get
            {
                var tier = _xp / _track.XpPerTier;
                return tier > _track.TierCount ? _track.TierCount : tier;
            }
        }

        /// <summary>Progress into the current tier, 0..1. Drives the UI bar; 1 when the pass is maxed.</summary>
        public float TierProgress01
        {
            get
            {
                if (CurrentTier >= _track.TierCount) return 1f;
                var into = _xp - CurrentTier * _track.XpPerTier;
                return (float)into / _track.XpPerTier;
            }
        }

        // -------------------------------------------------------------------------------
        // Progress
        // -------------------------------------------------------------------------------

        /// <summary>
        /// Adds season XP, clamped so it can never exceed the top of the ladder. Non-positive gains
        /// are ignored (a defensive no-op, not an error). Publishes progress, flagging whether the
        /// gain crossed a tier boundary so the UI and audio can celebrate only when it matters.
        /// </summary>
        public void AddXp(int amount)
        {
            if (amount <= 0) return;

            var tierBefore = CurrentTier;
            _xp = Clamp(_xp + amount);
            var tierAfter = CurrentTier;

            _bus.Publish(new BattlePassProgressed(_xp, tierAfter, tierChanged: tierAfter != tierBefore));
        }

        /// <summary>Unlocks the premium track (an IAP purchase). Idempotent: a re-grant from a cloud sync is a no-op.</summary>
        public void UnlockPremium()
        {
            if (_premiumOwned) return;

            _premiumOwned = true;
            _bus.Publish(new BattlePassPremiumUnlocked(_track.SeasonId));
        }

        // -------------------------------------------------------------------------------
        // Claiming
        // -------------------------------------------------------------------------------

        public bool IsClaimedFree(int level) => _claimedFree.Contains(level);

        public bool IsClaimedPremium(int level) => _claimedPremium.Contains(level);

        /// <summary>True when the free reward at <paramref name="level"/> can be claimed right now.</summary>
        public bool CanClaimFree(int level) =>
            IsUnlocked(level) && !_claimedFree.Contains(level) && _track.TierAt(level).Free.IsSomething;

        /// <summary>True when the premium reward at <paramref name="level"/> can be claimed right now (needs the paid pass).</summary>
        public bool CanClaimPremium(int level) =>
            _premiumOwned && IsUnlocked(level) && !_claimedPremium.Contains(level) && _track.TierAt(level).Premium.IsSomething;

        /// <summary>
        /// Claims the free reward at a level. Returns the reward on success, or
        /// <see cref="BattlePassReward.None"/> if it was not claimable (locked, already taken, or
        /// empty) — so calling it is always safe and can never pay twice. The caller applies the
        /// returned reward to the wallet/inventory.
        /// </summary>
        public BattlePassReward ClaimFree(int level)
        {
            if (!CanClaimFree(level)) return BattlePassReward.None;

            _claimedFree.Add(level);
            var reward = _track.TierAt(level).Free;
            _bus.Publish(new BattlePassRewardClaimed(level, premium: false, reward));
            return reward;
        }

        /// <summary>Claims the premium reward at a level. Same once-only, safe-to-call contract as <see cref="ClaimFree"/>.</summary>
        public BattlePassReward ClaimPremium(int level)
        {
            if (!CanClaimPremium(level)) return BattlePassReward.None;

            _claimedPremium.Add(level);
            var reward = _track.TierAt(level).Premium;
            _bus.Publish(new BattlePassRewardClaimed(level, premium: true, reward));
            return reward;
        }

        /// <summary>
        /// Claims every reward currently unlocked and unclaimed, across both tracks (premium only if
        /// owned). The "claim all" button. Returns them for the service to bank in one pass.
        /// </summary>
        public IReadOnlyList<BattlePassReward> ClaimAllAvailable()
        {
            var granted = new List<BattlePassReward>();

            for (var level = 1; level <= CurrentTier; level++)
            {
                var free = ClaimFree(level);
                if (free.IsSomething) granted.Add(free);

                var premium = ClaimPremium(level);
                if (premium.IsSomething) granted.Add(premium);
            }

            return granted;
        }

        // -------------------------------------------------------------------------------
        // Season rollover
        // -------------------------------------------------------------------------------

        /// <summary>
        /// Swaps in a season definition. If it is the SAME season (a config refetch mid-season) the
        /// track is replaced but progress is kept. If it is a NEW season, progress, premium ownership
        /// and claim history all reset — cosmetics already granted are elsewhere and survive.
        /// </summary>
        public void StartSeason(BattlePassTrack track)
        {
            if (track == null) throw new ArgumentNullException(nameof(track));

            if (track.SeasonId == _track.SeasonId)
            {
                _track = track;
                return;
            }

            _track = track;
            _xp = 0;
            _premiumOwned = false;
            _claimedFree.Clear();
            _claimedPremium.Clear();

            _bus.Publish(new BattlePassSeasonStarted(track.SeasonId));
        }

        // -------------------------------------------------------------------------------
        // Persistence
        // -------------------------------------------------------------------------------

        /// <summary>Captures the current progress for saving. Sorted lists, so saves are stable and diffable.</summary>
        public BattlePassSnapshot Snapshot()
        {
            var free = new List<int>(_claimedFree);
            var premium = new List<int>(_claimedPremium);
            free.Sort();
            premium.Sort();

            return new BattlePassSnapshot(_track.SeasonId, _xp, _premiumOwned, free, premium);
        }

        // -------------------------------------------------------------------------------

        private bool IsUnlocked(int level) => level >= 1 && level <= CurrentTier;

        private int Clamp(int xp) => xp < 0 ? 0 : xp > MaxXp ? MaxXp : xp;

        private static void Restore(HashSet<int> set, IReadOnlyList<int> levels)
        {
            if (levels == null) return;
            foreach (var level in levels) set.Add(level);
        }
    }
}
