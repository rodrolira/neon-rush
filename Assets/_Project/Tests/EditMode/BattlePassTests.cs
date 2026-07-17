using System.Collections.Generic;
using NeonRush.Core.Events;
using NeonRush.Domain.BattlePass;
using NUnit.Framework;

namespace NeonRush.Tests.EditMode
{
    /// <summary>
    /// Tests for the battle-pass rules: progression, the once-only claim contract, premium gating,
    /// season rollover, and the persistence round-trip. This is the code that moves real currency and
    /// paid entitlements, so the rules are asserted directly, with no Editor and no Unity.
    /// </summary>
    [TestFixture]
    public sealed class BattlePassTests
    {
        private EventBus _bus;

        [SetUp]
        public void SetUp() => _bus = new EventBus();

        [TearDown]
        public void TearDown() => _bus.Dispose();

        // A small, predictable season: 3 tiers, 100 XP each (300 XP maxes it).
        private static BattlePassTrack TestTrack(string seasonId = "s-test") =>
            new(seasonId, xpPerTier: 100, new List<BattlePassTier>
            {
                new(1, BattlePassReward.Coins(100), BattlePassReward.Gems(5)),
                new(2, BattlePassReward.None,       BattlePassReward.Coins(200)),
                new(3, BattlePassReward.Gems(10),   BattlePassReward.Item("bp.test.skin")),
            });

        private BattlePassState New(BattlePassSnapshot? loaded = null) => new(TestTrack(), _bus, loaded);

        // -------------------------------------------------------------------------------
        // Progression
        // -------------------------------------------------------------------------------

        [Test]
        public void FreshState_HasNoProgressAndNothingClaimable()
        {
            var state = New();

            Assert.That(state.Xp, Is.Zero);
            Assert.That(state.CurrentTier, Is.Zero);
            Assert.That(state.CanClaimFree(1), Is.False);
        }

        [Test]
        public void AddXp_CrossesTierBoundary()
        {
            var state = New();

            state.AddXp(100);

            Assert.That(state.CurrentTier, Is.EqualTo(1));
            Assert.That(state.CanClaimFree(1), Is.True);
            Assert.That(state.CanClaimFree(2), Is.False);
        }

        [Test]
        public void AddXp_ClampsAtTheTopOfTheLadder()
        {
            var state = New();

            state.AddXp(999_999);

            Assert.That(state.Xp, Is.EqualTo(state.MaxXp));
            Assert.That(state.Xp, Is.EqualTo(300));
            Assert.That(state.CurrentTier, Is.EqualTo(3));
            Assert.That(state.TierProgress01, Is.EqualTo(1f));
        }

        [Test]
        public void AddXp_IgnoresNonPositiveGains()
        {
            var state = New();

            state.AddXp(0);
            state.AddXp(-50);

            Assert.That(state.Xp, Is.Zero);
        }

        [Test]
        public void AddXp_PublishesProgressWithTierChangedFlag()
        {
            var events = new List<BattlePassProgressed>();
            _bus.Subscribe<BattlePassProgressed>(e => events.Add(e));

            var state = New();
            state.AddXp(50);   // no tier crossed
            state.AddXp(50);   // now crosses into tier 1

            Assert.That(events.Count, Is.EqualTo(2));
            Assert.That(events[0].TierChanged, Is.False);
            Assert.That(events[1].TierChanged, Is.True);
            Assert.That(events[1].Tier, Is.EqualTo(1));
        }

        // -------------------------------------------------------------------------------
        // Claiming
        // -------------------------------------------------------------------------------

        [Test]
        public void ClaimFree_PaysExactlyOnce()
        {
            var state = New();
            state.AddXp(100);

            var first = state.ClaimFree(1);
            var second = state.ClaimFree(1);

            Assert.That(first.Kind, Is.EqualTo(BattlePassRewardKind.Coins));
            Assert.That(first.Amount, Is.EqualTo(100));
            Assert.That(state.IsClaimedFree(1), Is.True);
            Assert.That(second.IsSomething, Is.False, "A second claim must pay nothing.");
        }

        [Test]
        public void ClaimFree_LockedTierPaysNothing()
        {
            var state = New(); // 0 XP, tier 1 not unlocked

            var reward = state.ClaimFree(1);

            Assert.That(reward.IsSomething, Is.False);
            Assert.That(state.IsClaimedFree(1), Is.False);
        }

        [Test]
        public void ClaimFree_EmptySlotPaysNothing()
        {
            var state = New();
            state.AddXp(200); // tier 2 unlocked, but its free reward is None

            Assert.That(state.CanClaimFree(2), Is.False);
            Assert.That(state.ClaimFree(2).IsSomething, Is.False);
        }

        [Test]
        public void Premium_CannotBeClaimedWithoutOwningThePass()
        {
            var state = New();
            state.AddXp(100);

            Assert.That(state.CanClaimPremium(1), Is.False);
            Assert.That(state.ClaimPremium(1).IsSomething, Is.False);
        }

        [Test]
        public void Premium_ClaimableOnceUnlocked()
        {
            var state = New();
            state.AddXp(100);
            state.UnlockPremium();

            var reward = state.ClaimPremium(1);

            Assert.That(state.PremiumOwned, Is.True);
            Assert.That(reward.Kind, Is.EqualTo(BattlePassRewardKind.Gems));
            Assert.That(reward.Amount, Is.EqualTo(5));
        }

        [Test]
        public void UnlockPremium_IsIdempotentAndPublishesOnce()
        {
            var unlocks = new List<BattlePassPremiumUnlocked>();
            _bus.Subscribe<BattlePassPremiumUnlocked>(e => unlocks.Add(e));

            var state = New();
            state.UnlockPremium();
            state.UnlockPremium();

            Assert.That(state.PremiumOwned, Is.True);
            Assert.That(unlocks.Count, Is.EqualTo(1));
        }

        [Test]
        public void ClaimAllAvailable_TakesEveryUnlockedRewardOnBothTracks()
        {
            var state = New();
            state.AddXp(200);       // tiers 1 and 2 unlocked, tier 3 not
            state.UnlockPremium();

            var rewards = state.ClaimAllAvailable();

            // tier1 free (coins) + tier1 premium (gems) + tier2 premium (coins). tier2 free is empty;
            // tier3 is locked.
            Assert.That(rewards.Count, Is.EqualTo(3));
            Assert.That(state.CanClaimFree(1), Is.False);
            Assert.That(state.CanClaimPremium(2), Is.False);
            Assert.That(state.CanClaimPremium(3), Is.False, "Tier 3 is locked and must stay claimable-later.");
        }

        // -------------------------------------------------------------------------------
        // Season rollover
        // -------------------------------------------------------------------------------

        [Test]
        public void StartSeason_NewSeasonResetsProgressAndPremium()
        {
            var started = new List<BattlePassSeasonStarted>();
            _bus.Subscribe<BattlePassSeasonStarted>(e => started.Add(e));

            var state = New();
            state.AddXp(150);
            state.UnlockPremium();
            state.ClaimFree(1);

            state.StartSeason(TestTrack("s-two"));

            Assert.That(state.SeasonId, Is.EqualTo("s-two"));
            Assert.That(state.Xp, Is.Zero);
            Assert.That(state.PremiumOwned, Is.False);
            Assert.That(state.IsClaimedFree(1), Is.False);
            Assert.That(started.Count, Is.EqualTo(1));
        }

        [Test]
        public void StartSeason_SameSeasonKeepsProgress()
        {
            var state = New();
            state.AddXp(120);

            state.StartSeason(TestTrack()); // same id, e.g. a config refetch

            Assert.That(state.Xp, Is.EqualTo(120));
        }

        // -------------------------------------------------------------------------------
        // Persistence
        // -------------------------------------------------------------------------------

        [Test]
        public void Snapshot_RoundTripsProgress()
        {
            var state = New();
            state.AddXp(150);
            state.UnlockPremium();
            state.ClaimFree(1);
            state.ClaimPremium(1);

            var restored = New(state.Snapshot());

            Assert.That(restored.Xp, Is.EqualTo(150));
            Assert.That(restored.PremiumOwned, Is.True);
            Assert.That(restored.IsClaimedFree(1), Is.True);
            Assert.That(restored.IsClaimedPremium(1), Is.True);
            Assert.That(restored.CurrentTier, Is.EqualTo(1));
        }

        [Test]
        public void Snapshot_FromAnotherSeasonIsDiscarded()
        {
            var stale = new BattlePassSnapshot("s-old", xp: 250, premiumOwned: true,
                new List<int> { 1, 2 }, new List<int> { 1 });

            // New state on the current season must ignore last season's progress entirely.
            var state = New(stale);

            Assert.That(state.Xp, Is.Zero);
            Assert.That(state.PremiumOwned, Is.False);
            Assert.That(state.IsClaimedFree(1), Is.False);
        }

        [Test]
        public void RewardClaimed_IsPublishedWithTheRightPayload()
        {
            var claims = new List<BattlePassRewardClaimed>();
            _bus.Subscribe<BattlePassRewardClaimed>(e => claims.Add(e));

            var state = New();
            state.AddXp(100);
            state.UnlockPremium();
            state.ClaimFree(1);
            state.ClaimPremium(1);

            Assert.That(claims.Count, Is.EqualTo(2));
            Assert.That(claims[0].Premium, Is.False);
            Assert.That(claims[0].Reward.Amount, Is.EqualTo(100));
            Assert.That(claims[1].Premium, Is.True);
            Assert.That(claims[1].Reward.Kind, Is.EqualTo(BattlePassRewardKind.Gems));
        }
    }
}
