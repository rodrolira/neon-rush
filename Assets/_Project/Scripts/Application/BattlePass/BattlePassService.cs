using System;
using System.Collections.Generic;
using NeonRush.Application.Events;
using NeonRush.Core.Events;
using NeonRush.Domain.BattlePass;
using NeonRush.Domain.Economy;
using NeonRush.Domain.Inventory;

namespace NeonRush.Application.BattlePass
{
    /// <summary>
    /// Drives the battle pass: turns finished runs into season XP, and turns claimed tier rewards
    /// into real currency and entitlements.
    ///
    /// This is the seam between the pure Domain rules (BattlePassState decides WHAT you are owed and
    /// marks it claimed exactly once) and the rest of the economy (the Wallet and Inventory, which
    /// actually pay it out). Keeping the payout here — and nowhere in the Domain — is what lets the
    /// claim rules be tested with no wallet at all, and lets the wallet stay unaware that a battle
    /// pass exists. Every credit carries <see cref="TransactionReason.SeasonPassReward"/> so the
    /// economy dashboard can attribute exactly how much currency the pass is injecting.
    /// </summary>
    public sealed class BattlePassService : IDisposable
    {
        private readonly BattlePassState _state;
        private readonly Wallet _wallet;
        private readonly Inventory _inventory;
        private readonly BattlePassConfig _config;
        private readonly IDisposable _runEndedSubscription;

        public BattlePassService(BattlePassState state, Wallet wallet, Inventory inventory,
            BattlePassConfig config, IEventBus bus)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            _config = config ?? throw new ArgumentNullException(nameof(config));

            if (bus == null) throw new ArgumentNullException(nameof(bus));

            // Season progress is earned by playing. Awarded on RunEnded — the same signal that banks
            // the run's coins — so a run contributes to the pass whether the player lives or dies.
            _runEndedSubscription = bus.Subscribe<RunEnded>(OnRunEnded);
        }

        /// <summary>The live progress, for the UI to read (tiers, current level, claimed state).</summary>
        public BattlePassState State => _state;

        /// <summary>Gem price of the premium pass, for the buy button to display.</summary>
        public int PremiumGemPrice => _config.PremiumGemPrice;

        private void OnRunEnded(RunEnded e)
        {
            var xp = (int)(e.DistanceMetres * _config.XpPerMetre) + e.CoinsCollected * _config.XpPerCoin;
            _state.AddXp(xp); // AddXp already ignores a non-positive gain
        }

        /// <summary>Claims a free-track reward and banks it. Returns what was granted (None if nothing was claimable).</summary>
        public BattlePassReward ClaimFree(int level) => Bank(_state.ClaimFree(level));

        /// <summary>Claims a premium-track reward and banks it. Returns what was granted (None if not owned/locked/taken).</summary>
        public BattlePassReward ClaimPremium(int level) => Bank(_state.ClaimPremium(level));

        /// <summary>Claims and banks every reward currently unlocked on both tracks. The "claim all" button.</summary>
        public IReadOnlyList<BattlePassReward> ClaimAll()
        {
            var rewards = _state.ClaimAllAvailable();
            foreach (var reward in rewards) Bank(reward);
            return rewards;
        }

        /// <summary>
        /// Buys the premium pass with gems. Returns false if the player cannot afford it — in which
        /// case the wallet has already published the insufficient-funds signal the store UI reacts
        /// to. In production this call is funded by a real-money IAP instead; the entitlement it
        /// grants is identical.
        /// </summary>
        public bool TryUnlockPremium()
        {
            if (_state.PremiumOwned) return true;

            if (!_wallet.TryDebit(CurrencyType.Gems, _config.PremiumGemPrice, TransactionReason.SeasonPassUnlock))
            {
                return false;
            }

            _state.UnlockPremium();
            return true;
        }

        /// <summary>Captures progress for the save file.</summary>
        public BattlePassSnapshot Snapshot() => _state.Snapshot();

        private BattlePassReward Bank(BattlePassReward reward)
        {
            switch (reward.Kind)
            {
                case BattlePassRewardKind.Coins:
                    _wallet.Credit(CurrencyType.Coins, reward.Amount, TransactionReason.SeasonPassReward);
                    break;

                case BattlePassRewardKind.Gems:
                    _wallet.Credit(CurrencyType.Gems, reward.Amount, TransactionReason.SeasonPassReward);
                    break;

                case BattlePassRewardKind.Item:
                    _inventory.Grant(reward.ItemId);
                    break;

                case BattlePassRewardKind.None:
                default:
                    break; // nothing to bank
            }

            return reward;
        }

        public void Dispose() => _runEndedSubscription.Dispose();
    }
}
