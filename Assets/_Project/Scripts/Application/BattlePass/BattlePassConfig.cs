using System;

namespace NeonRush.Application.BattlePass
{
    /// <summary>
    /// The tunable scalars of the battle pass: how fast a run earns season XP, and what the premium
    /// pass costs. Built and clamped from Remote Config in GameConfigService, exactly like the run
    /// tuning and ad policy — the pacing of a season is the number LiveOps most wants to move without
    /// a store submission (too slow and players disengage; too fast and the pass has no runway).
    ///
    /// The reward ladder itself lives in the Domain's BattlePassTrack; only these rates are here.
    /// </summary>
    public sealed class BattlePassConfig
    {
        /// <summary>Season XP earned per metre of a completed run.</summary>
        public float XpPerMetre { get; set; } = 1f;

        /// <summary>Season XP earned per coin collected in a run. Rewards active play, not just distance.</summary>
        public int XpPerCoin { get; set; } = 2;

        /// <summary>
        /// Gem price of the premium pass. A gem sink stands in for the real-money IAP until the store
        /// SDK is wired; the unlock flow and entitlement are identical either way, so swapping the
        /// payment rail later touches only how <see cref="BattlePass.BattlePassService.TryUnlockPremium"/>
        /// is funded, never the pass itself.
        /// </summary>
        public int PremiumGemPrice { get; set; } = 500;

        public void Validate()
        {
            if (XpPerMetre < 0f) throw new ArgumentException($"{nameof(XpPerMetre)} must be >= 0.");
            if (XpPerCoin < 0) throw new ArgumentException($"{nameof(XpPerCoin)} must be >= 0.");
            if (PremiumGemPrice < 0) throw new ArgumentException($"{nameof(PremiumGemPrice)} must be >= 0.");
        }
    }
}
