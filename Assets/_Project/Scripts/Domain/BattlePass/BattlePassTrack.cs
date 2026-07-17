using System;
using System.Collections.Generic;

namespace NeonRush.Domain.BattlePass
{
    /// <summary>What a battle-pass reward pays out.</summary>
    public enum BattlePassRewardKind
    {
        /// <summary>This slot on this track is empty (e.g. a free tier with no free reward).</summary>
        None = 0,
        Coins = 1,
        Gems = 2,

        /// <summary>A cosmetic entitlement, granted into the Inventory by <see cref="ItemId"/>.</summary>
        Item = 3,
    }

    /// <summary>
    /// One reward on one track at one tier. A value type: rewards are pure data shuffled between the
    /// season definition, the claim logic, the UI and analytics, and none of them should be able to
    /// mutate a shared instance.
    /// </summary>
    public readonly struct BattlePassReward
    {
        public readonly BattlePassRewardKind Kind;

        /// <summary>Coin or gem amount. Zero for <see cref="BattlePassRewardKind.Item"/> and None.</summary>
        public readonly int Amount;

        /// <summary>Entitlement id for an <see cref="BattlePassRewardKind.Item"/>; null otherwise.</summary>
        public readonly string ItemId;

        private BattlePassReward(BattlePassRewardKind kind, int amount, string itemId)
        {
            Kind = kind;
            Amount = amount;
            ItemId = itemId;
        }

        public bool IsSomething => Kind != BattlePassRewardKind.None;

        public static readonly BattlePassReward None = new(BattlePassRewardKind.None, 0, null);

        public static BattlePassReward Coins(int amount) => new(BattlePassRewardKind.Coins, Require(amount), null);
        public static BattlePassReward Gems(int amount) => new(BattlePassRewardKind.Gems, Require(amount), null);

        public static BattlePassReward Item(string itemId) =>
            string.IsNullOrWhiteSpace(itemId)
                ? throw new ArgumentException("Item reward needs an id.", nameof(itemId))
                : new BattlePassReward(BattlePassRewardKind.Item, 0, itemId);

        private static int Require(int amount) =>
            amount > 0 ? amount : throw new ArgumentOutOfRangeException(nameof(amount), "Currency reward must be positive.");
    }

    /// <summary>
    /// One rung of the ladder: a free reward everyone earns and a premium reward gated behind the
    /// paid pass. Either may be <see cref="BattlePassReward.None"/> — a common, deliberate pattern is
    /// a sparse free track next to a dense premium one, which is most of what makes the pass worth
    /// buying.
    /// </summary>
    public sealed class BattlePassTier
    {
        public BattlePassTier(int level, BattlePassReward free, BattlePassReward premium)
        {
            if (level < 1) throw new ArgumentOutOfRangeException(nameof(level), "Tiers are 1-based.");

            Level = level;
            Free = free;
            Premium = premium;
        }

        /// <summary>1-based rung number.</summary>
        public int Level { get; }

        public BattlePassReward Free { get; }
        public BattlePassReward Premium { get; }
    }

    /// <summary>
    /// A season's definition: its id, how much XP buys a tier, and the ladder of rewards.
    ///
    /// This is content, not code. In production it is populated from Remote Config so a new season
    /// ships without an app update — the whole reason a battle pass drives a live game. The compiled
    /// <see cref="Default"/> below is a complete, balanced season so the feature works fully offline
    /// and in tests, exactly like every other tunable in the game (see IRemoteConfigService).
    /// </summary>
    public sealed class BattlePassTrack
    {
        public BattlePassTrack(string seasonId, int xpPerTier, IReadOnlyList<BattlePassTier> tiers)
        {
            SeasonId = string.IsNullOrWhiteSpace(seasonId)
                ? throw new ArgumentException("Season id required.", nameof(seasonId))
                : seasonId;

            if (xpPerTier <= 0) throw new ArgumentOutOfRangeException(nameof(xpPerTier), "XP per tier must be > 0.");
            if (tiers == null || tiers.Count == 0) throw new ArgumentException("A season needs at least one tier.", nameof(tiers));

            XpPerTier = xpPerTier;
            Tiers = tiers;
        }

        /// <summary>Identifies the season. When it changes, progress resets but owned cosmetics never do.</summary>
        public string SeasonId { get; }

        /// <summary>XP required to climb one tier. Total XP for the whole pass is this times <see cref="TierCount"/>.</summary>
        public int XpPerTier { get; }

        public IReadOnlyList<BattlePassTier> Tiers { get; }

        public int TierCount => Tiers.Count;

        /// <summary>The tier at a 1-based level, or null if out of range.</summary>
        public BattlePassTier TierAt(int level) =>
            level >= 1 && level <= Tiers.Count ? Tiers[level - 1] : null;

        /// <summary>
        /// The compiled default season: 30 tiers, 100 XP each. The free track pays a steady trickle
        /// of coins with a gem drop every fifth tier; the premium track pays more, drops gems more
        /// often, and hands out a cosmetic at each 10-tier milestone plus a capstone skin at the top.
        /// Deliberately generated rather than hand-listed, so the shape is easy to read and retune.
        /// </summary>
        public static BattlePassTrack Default()
        {
            const int tierCount = 30;
            var tiers = new List<BattlePassTier>(tierCount);

            for (var level = 1; level <= tierCount; level++)
            {
                var free = (level % 5 == 0)
                    ? BattlePassReward.Gems(10)
                    : BattlePassReward.Coins(100 + level * 10);

                BattlePassReward premium;
                if (level == tierCount)
                {
                    premium = BattlePassReward.Item("bp.s1.skin.apex");         // capstone cosmetic
                }
                else if (level % 10 == 0)
                {
                    premium = BattlePassReward.Item($"bp.s1.skin.tier{level}"); // milestone cosmetics
                }
                else if (level % 3 == 0)
                {
                    premium = BattlePassReward.Gems(25);
                }
                else
                {
                    premium = BattlePassReward.Coins(250 + level * 20);
                }

                tiers.Add(new BattlePassTier(level, free, premium));
            }

            return new BattlePassTrack("season-1", xpPerTier: 100, tiers);
        }
    }
}
