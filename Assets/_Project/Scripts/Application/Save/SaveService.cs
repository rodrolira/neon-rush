using System;
using System.Collections.Generic;
using NeonRush.Application.Events;
using NeonRush.Application.Progression;
using NeonRush.Application.Store;
using NeonRush.Core.Events;
using NeonRush.Domain.Economy;
using NeonRush.Domain.Inventory;
using NeonRush.Domain.Ports;
using NeonRush.Domain.Save;

namespace NeonRush.Application.Save
{
    /// <summary>
    /// Decides when the game is written to disk.
    ///
    /// The interesting question in a save system is not *how* to write — it is *when*, and both
    /// obvious answers are wrong:
    ///
    ///  · <b>Save on every change.</b> A coin pickup fires several times per second. Writing the
    ///    file each time means hundreds of disk writes per run: it burns battery, it wears the flash
    ///    storage, and on a budget Android device the I/O stall is visible as a frame hitch — during
    ///    gameplay, which is the worst possible moment.
    ///
    ///  · <b>Save only when the run ends.</b> This loses data constantly, because the run ending is
    ///    not when the app dies. Android kills backgrounded apps without warning and without running
    ///    any of your code afterwards. A player who alt-tabs mid-run and never comes back loses
    ///    whatever happened since the last write.
    ///
    /// So: mark dirty on change, flush on a debounce (never mid-frame-critical work), and — the part
    /// that actually saves the data — <b>flush immediately and synchronously when the app is
    /// backgrounded</b>. <c>OnApplicationPause</c> is the last moment the OS guarantees you get. If
    /// the write does not happen there, it may never happen at all.
    ///
    /// Pure C#: the platform lifecycle callback is delivered by the composition root, so all of this
    /// logic is unit-testable without an app, a device, or a filesystem.
    /// </summary>
    public sealed class SaveService : IDisposable
    {
        /// <summary>
        /// Seconds between debounced writes. Long enough that a run's worth of coin pickups collapse
        /// into a single write; short enough that a crash costs seconds, not a session.
        /// </summary>
        private const float DebounceSeconds = 5f;

        private readonly ISaveStore _store;
        private readonly Wallet _wallet;
        private readonly PlayerProfile _profile;
        private readonly Inventory _inventory;
        private readonly IClock _clock;

        /// <summary>Optional: set after construction because the reward service needs the loaded save first.</summary>
        public Domain.Retention.DailyRewardService DailyRewards { get; set; }

        /// <summary>Optional, same pattern as <see cref="DailyRewards"/>.</summary>
        public Missions.MissionService Missions { get; set; }

        /// <summary>Optional, same pattern. Its snapshot (season XP, premium, claimed tiers) is persisted.</summary>
        public BattlePass.BattlePassService BattlePass { get; set; }

        /// <summary>Optional, same pattern. Only its first-seen timestamp needs persisting (the window's start).</summary>
        public Store.StarterPackService StarterPack { get; set; }

        /// <summary>Optional, same pattern. Its expiry and last daily-grant timestamps are persisted.</summary>
        public Subscription.SubscriptionService Vip { get; set; }

        /// <summary>Optional, same pattern. Its current stage number and per-objective progress are persisted.</summary>
        public Stages.StageService Stages { get; set; }

        private readonly List<IDisposable> _subscriptions = new();

        private bool _dirty;
        private float _sinceLastWrite;

        /// <summary>Set once ad removal is bought. Persisted so it survives a reinstall.</summary>
        private bool _adsRemoved;

        /// <summary>
        /// The player's mute setting. A plain persisted flag, set by the composition root when the
        /// mute toggle flips (which also marks the save dirty). Mirrors <see cref="_adsRemoved"/>: a
        /// value the save owns rather than reading off another service.
        /// </summary>
        public bool AudioMuted { get; set; }

        public SaveService(
            ISaveStore store,
            Wallet wallet,
            PlayerProfile profile,
            Inventory inventory,
            IEventBus bus,
            IClock clock,
            bool adsRemoved = false)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _adsRemoved = adsRemoved;

            if (bus == null) throw new ArgumentNullException(nameof(bus));

            _subscriptions.Add(bus.Subscribe<CurrencyChanged>(_ => MarkDirty()));
            _subscriptions.Add(bus.Subscribe<RunEnded>(_ => MarkDirty()));

            // A purchase must reach the disk. An item the player PAID for that is lost because the
            // app was killed before the next debounce tick is a refund request and a lost customer.
            _subscriptions.Add(bus.Subscribe<ItemGranted>(_ => Flush()));

            _subscriptions.Add(bus.Subscribe<PurchaseCompleted>(e =>
            {
                if (e.ItemId == AdRemovalItemId) _adsRemoved = true;
                Flush();
            }));

            // A claimed tier changes the pass's claimed-set, which must persist or a reload would let
            // the reward be taken again. Coin/gem/item grants already mark dirty on their own, but the
            // claimed-set is the authoritative "already paid" record, so mark on the claim itself too.
            _subscriptions.Add(bus.Subscribe<NeonRush.Domain.BattlePass.BattlePassRewardClaimed>(_ => MarkDirty()));

            // Buying the premium pass is a paid entitlement — flush it to disk at once, exactly like
            // a store purchase, so a kill immediately afterwards can never lose it.
            _subscriptions.Add(bus.Subscribe<NeonRush.Domain.BattlePass.BattlePassPremiumUnlocked>(_ => Flush()));

            // The VIP subscription is a paid, time-limited entitlement — flush its new expiry at once.
            _subscriptions.Add(bus.Subscribe<NeonRush.Domain.Subscription.SubscriptionActivated>(_ => Flush()));

            // Stage progress advances constantly; a debounced write is enough. But clearing a stage
            // pays a reward and moves the campaign forward — flush that at once so a kill right after
            // can never lose it or, worse, let it be earned twice.
            _subscriptions.Add(bus.Subscribe<NeonRush.Application.Stages.StageProgressed>(_ => MarkDirty()));
            _subscriptions.Add(bus.Subscribe<NeonRush.Application.Stages.StageCompleted>(_ => Flush()));
        }

        /// <summary>The catalogue id of the ad-removal product.</summary>
        public const string AdRemovalItemId = "no_ads";

        /// <summary>Number of writes actually committed. Exposed for tests and for the debug overlay.</summary>
        public int WritesCommitted { get; private set; }

        /// <summary>True when there are unsaved changes.</summary>
        public bool HasUnsavedChanges => _dirty;

        /// <summary>Marks the state as needing a write. Cheap — does not touch the disk.</summary>
        public void MarkDirty() => _dirty = true;

        /// <summary>
        /// Drives the debounce. Call once per frame with the frame's delta.
        /// </summary>
        public void Tick(float deltaTime)
        {
            if (!_dirty) return;

            _sinceLastWrite += deltaTime;

            if (_sinceLastWrite < DebounceSeconds) return;

            Flush();
        }

        /// <summary>
        /// Writes now, if there is anything to write.
        ///
        /// Call this from <c>OnApplicationPause</c> and <c>OnApplicationQuit</c>. It must be
        /// synchronous: an async write started as the app is being killed does not finish.
        /// </summary>
        /// <returns>True when a write was committed (or there was nothing to do).</returns>
        public bool Flush()
        {
            if (!_dirty) return true;

            var data = Capture();

            if (!_store.Save(data))
            {
                // Disk full, permissions gone. Stay dirty so the next flush tries again rather than
                // silently dropping the player's progress on the floor.
                return false;
            }

            _dirty = false;
            _sinceLastWrite = 0f;
            WritesCommitted++;

            return true;
        }

        /// <summary>Snapshots the current game state into a persistable form.</summary>
        public SaveData Capture()
        {
            var data = SaveData.NewPlayer();

            data.Coins = _wallet.Balance(CurrencyType.Coins);
            data.Gems = _wallet.Balance(CurrencyType.Gems);

            data.OwnedItems = new List<string>(_inventory.Owned);
            data.AdsRemoved = _adsRemoved;
            data.AudioMuted = AudioMuted;

            if (DailyRewards != null)
            {
                data.LastDailyClaimUtc = DailyRewards.LastClaimUtc;
                data.DailyStreakDays = DailyRewards.StreakDays;
            }

            if (Missions != null)
            {
                data.MissionDay = Missions.MissionDay;
                data.Missions.Clear();

                foreach (var mission in Missions.Active)
                {
                    data.Missions.Add(new SaveData.MissionSave
                    {
                        Id = mission.Definition.Id,
                        Progress = mission.Progress,
                        Rewarded = mission.Rewarded,
                    });
                }
            }

            if (BattlePass != null)
            {
                var bp = BattlePass.Snapshot();
                data.BattlePassSeasonId = bp.SeasonId;
                data.BattlePassXp = bp.Xp;
                data.BattlePassPremiumOwned = bp.PremiumOwned;
                data.BattlePassClaimedFree = new List<int>(bp.ClaimedFree);
                data.BattlePassClaimedPremium = new List<int>(bp.ClaimedPremium);
            }

            if (StarterPack != null)
            {
                data.StarterPackFirstSeenUtc = StarterPack.FirstSeenUtc;
            }

            if (Vip != null)
            {
                data.SubscriptionExpiryUtc = Vip.ExpiryUtc;
                data.SubscriptionLastDailyGrantUtc = Vip.LastDailyGrantUtc;
            }

            if (Stages != null)
            {
                data.StageNumber = Stages.CurrentStageNumber;
                data.StageProgress = new List<int>(Stages.ProgressSnapshot());
                data.StagePrestige = Stages.Prestige;
            }

            _profile.WriteTo(data);

            // Server-anchored time, never DateTime.Now. This timestamp decides cloud-save merge
            // conflicts, so a player who could set it by changing their phone clock could make their
            // local (cheated) save always win the merge against the server's.
            data.SavedAtUtc = _clock.UtcNow;

            return data;
        }

        public void Dispose()
        {
            // One last write on the way out. Disposing without flushing would discard whatever
            // happened since the last debounce tick.
            Flush();

            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }

            _subscriptions.Clear();
        }
    }
}
