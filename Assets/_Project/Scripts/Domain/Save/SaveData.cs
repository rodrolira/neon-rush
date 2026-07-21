using System;
using System.Collections.Generic;

namespace NeonRush.Domain.Save
{
    /// <summary>
    /// Everything that survives closing the app.
    ///
    /// Deliberately small and flat. A save file is the one artefact you can never change
    /// retroactively — every version you ship is out there on real devices forever, and a player who
    /// installs an update must be able to load a file written by a build from a year ago. Complex,
    /// deeply-nested save schemas are how studios end up unable to change a data structure without
    /// breaking a fraction of their install base.
    ///
    /// Pure C#: no Unity, no serialisation attributes. The mapping to a concrete wire format lives
    /// in Infrastructure, so the domain never gets deformed by what a serialiser happens to support.
    /// </summary>
    public sealed class SaveData
    {
        /// <summary>
        /// Schema version. Bumped whenever the shape changes.
        ///
        /// This field is why migrations are possible at all. Read it FIRST, before trusting any
        /// other field — a file from a future version (the player downgraded, or restored a cloud
        /// backup from a newer device) must be rejected rather than misread.
        /// </summary>
        public int Version { get; set; } = CurrentVersion;

        /// <summary>The schema version this build writes. v2 added the battle-pass block; v3 added the audio-muted flag; v4 added the stage campaign.</summary>
        public const int CurrentVersion = 4;

        public int Coins { get; set; }

        public int Gems { get; set; }

        public int BestScore { get; set; }

        public int TotalRuns { get; set; }

        /// <summary>Lifetime distance, in metres. Drives long-term missions and achievements.</summary>
        public long TotalDistance { get; set; }

        /// <summary>
        /// Item ids the player owns.
        ///
        /// This is the most valuable field in the file — it is what they PAID for. It is also the one
        /// that must never be lost, which is why the cloud merge is a union and never a replace (see
        /// Inventory.MergeFromServer). Taking away a purchased item is the fastest route to a
        /// chargeback.
        /// </summary>
        public List<string> OwnedItems { get; set; } = new();

        /// <summary>True once the player has bought ad removal.</summary>
        public bool AdsRemoved { get; set; }

        /// <summary>UTC instant of the last daily-reward claim. MinValue = never claimed.</summary>
        public DateTime LastDailyClaimUtc { get; set; }

        /// <summary>Consecutive daily-reward days. The streak IS the retention loop; losing it to a save bug would be unforgivable.</summary>
        public int DailyStreakDays { get; set; }

        /// <summary>Day stamp the saved mission set belongs to. Progress never crosses days.</summary>
        public int MissionDay { get; set; }

        /// <summary>Progress of today's missions. One entry per active mission.</summary>
        public List<MissionSave> Missions { get; set; } = new();

        // --- Battle pass (schema v2) ---

        /// <summary>The season the saved pass progress belongs to. A different live season discards it (see BattlePassState).</summary>
        public string BattlePassSeasonId { get; set; } = string.Empty;

        /// <summary>Season XP accumulated on the current pass.</summary>
        public int BattlePassXp { get; set; }

        /// <summary>True once the premium pass is owned for this season. A paid entitlement — never dropped.</summary>
        public bool BattlePassPremiumOwned { get; set; }

        /// <summary>Free-track tier levels already claimed, so a reload can never re-pay them.</summary>
        public List<int> BattlePassClaimedFree { get; set; } = new();

        /// <summary>Premium-track tier levels already claimed.</summary>
        public List<int> BattlePassClaimedPremium { get; set; } = new();

        // --- Starter pack (schema v2) ---

        /// <summary>UTC instant the starter-pack offer was first shown, which starts its one-shot window. MinValue = never shown.</summary>
        public DateTime StarterPackFirstSeenUtc { get; set; }

        // --- VIP subscription (schema v2) ---

        /// <summary>When the VIP subscription lapses. MinValue = never subscribed. A paid entitlement — restored before the first ad decision.</summary>
        public DateTime SubscriptionExpiryUtc { get; set; }

        /// <summary>UTC instant of the last VIP daily-gem grant, so it pays at most once per UTC day across restarts.</summary>
        public DateTime SubscriptionLastDailyGrantUtc { get; set; }

        // --- Settings (schema v3) ---

        /// <summary>True when the player has muted the game. Persisted so a silenced game stays silent across launches.</summary>
        public bool AudioMuted { get; set; }

        // --- Stage campaign (schema v4) ---

        /// <summary>1-based number of the stage the player is working on. 0 or 1 both mean the first stage.</summary>
        public int StageNumber { get; set; } = 1;

        /// <summary>Per-objective progress of the current stage. Cumulative and permanent until the stage is cleared.</summary>
        public List<int> StageProgress { get; set; } = new();

        /// <summary>One mission's persisted state. Flat and dumb on purpose — it is a wire format.</summary>
        public sealed class MissionSave
        {
            public string Id;
            public int Progress;
            public bool Rewarded;
        }

        /// <summary>
        /// When this file was written, in UTC, according to the trusted clock (<see cref="Ports.IClock"/>).
        ///
        /// Used for cloud-save conflict resolution: when the device and the cloud disagree, the
        /// newer write usually wins. Written from server-anchored time, not the device clock, so a
        /// player cannot win a merge by setting their phone forward.
        /// </summary>
        public DateTime SavedAtUtc { get; set; }

        public SaveData Clone() => new()
        {
            Version = Version,
            Coins = Coins,
            Gems = Gems,
            BestScore = BestScore,
            TotalRuns = TotalRuns,
            TotalDistance = TotalDistance,
            SavedAtUtc = SavedAtUtc,

            // A deep copy. Sharing the list would let a caller mutate the "stored" save without
            // writing it — which is exactly the class of bug Clone() exists to prevent.
            OwnedItems = new List<string>(OwnedItems),
            AdsRemoved = AdsRemoved,
            LastDailyClaimUtc = LastDailyClaimUtc,
            DailyStreakDays = DailyStreakDays,
            MissionDay = MissionDay,
            Missions = CloneMissions(),

            BattlePassSeasonId = BattlePassSeasonId,
            BattlePassXp = BattlePassXp,
            BattlePassPremiumOwned = BattlePassPremiumOwned,
            BattlePassClaimedFree = new List<int>(BattlePassClaimedFree),
            BattlePassClaimedPremium = new List<int>(BattlePassClaimedPremium),
            StarterPackFirstSeenUtc = StarterPackFirstSeenUtc,
            SubscriptionExpiryUtc = SubscriptionExpiryUtc,
            SubscriptionLastDailyGrantUtc = SubscriptionLastDailyGrantUtc,
            AudioMuted = AudioMuted,
            StageNumber = StageNumber,
            StageProgress = new List<int>(StageProgress),
        };

        private List<MissionSave> CloneMissions()
        {
            var copy = new List<MissionSave>(Missions.Count);
            foreach (var m in Missions)
            {
                copy.Add(new MissionSave { Id = m.Id, Progress = m.Progress, Rewarded = m.Rewarded });
            }

            return copy;
        }

        /// <summary>A fresh profile. This is what a brand-new install loads.</summary>
        public static SaveData NewPlayer() => new()
        {
            Version = CurrentVersion,
            Coins = 0,
            Gems = 0,
            BestScore = 0,
            TotalRuns = 0,
            TotalDistance = 0,
            SavedAtUtc = DateTime.MinValue,
            OwnedItems = new List<string>(),
            AdsRemoved = false,
            LastDailyClaimUtc = DateTime.MinValue,
            DailyStreakDays = 0,
            MissionDay = 0,
            Missions = new List<MissionSave>(),
            BattlePassSeasonId = string.Empty,
            BattlePassXp = 0,
            BattlePassPremiumOwned = false,
            BattlePassClaimedFree = new List<int>(),
            BattlePassClaimedPremium = new List<int>(),
            StarterPackFirstSeenUtc = DateTime.MinValue,
            SubscriptionExpiryUtc = DateTime.MinValue,
            SubscriptionLastDailyGrantUtc = DateTime.MinValue,
            AudioMuted = false,
            StageNumber = 1,
            StageProgress = new List<int>(),
        };
    }

    /// <summary>Why a load did not produce usable data. Every one of these is a real thing that happens on real devices.</summary>
    public enum LoadFailure
    {
        /// <summary>Loaded fine.</summary>
        None = 0,

        /// <summary>No save exists. A new install — not an error.</summary>
        NotFound = 1,

        /// <summary>The file exists but could not be parsed. Usually a write interrupted by the OS killing the app.</summary>
        Corrupt = 2,

        /// <summary>
        /// The file's integrity signature does not match its contents — it was edited outside the
        /// game. A cheat signal, and one worth reporting.
        /// </summary>
        SignatureMismatch = 3,

        /// <summary>Written by a newer build than this one. Must be refused, never guessed at.</summary>
        FutureVersion = 4,

        /// <summary>The storage medium itself failed (disk full, permissions, sandbox).</summary>
        IoError = 5,
    }

    /// <summary>Result of a load attempt.</summary>
    public readonly struct LoadResult
    {
        public readonly SaveData Data;
        public readonly LoadFailure Failure;

        /// <summary>True when the data came from a real, valid save (as opposed to a fresh profile).</summary>
        public readonly bool Restored;

        private LoadResult(SaveData data, LoadFailure failure, bool restored)
        {
            Data = data;
            Failure = failure;
            Restored = restored;
        }

        public static LoadResult Success(SaveData data) => new(data, LoadFailure.None, restored: true);

        /// <summary>
        /// A load that failed, carrying a usable fallback profile.
        ///
        /// The game must ALWAYS boot. A corrupt save is a bad day for one player; a game that
        /// refuses to start is a one-star review and an uninstall. The failure reason is carried
        /// alongside so it can be reported and, where the backup survived, recovered.
        /// </summary>
        public static LoadResult Failed(LoadFailure failure, SaveData fallback) =>
            new(fallback, failure, restored: false);
    }
}
