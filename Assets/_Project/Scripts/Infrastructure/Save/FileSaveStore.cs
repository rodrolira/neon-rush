using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NeonRush.Domain.Ports;
using NeonRush.Domain.Save;
using UnityEngine;

namespace NeonRush.Infrastructure.Save
{
    /// <summary>
    /// The player's save, on the device's disk.
    ///
    /// Three properties matter here, and each one is a bug report that never happens:
    ///
    /// <b>1. Writes are atomic.</b> Android kills backgrounded apps with no warning and no chance to
    /// finish what you were doing. If that happens in the middle of writing the save file, a naive
    /// implementation leaves a half-written file and the player loses everything. Here, the new save
    /// is written to a temporary file first, and only once it is complete and flushed to disk is the
    /// old save rotated to a backup and the new one moved into place. There is no moment at which
    /// the only copy of the player's progress is a partially-written file.
    ///
    /// <b>2. There is always a backup.</b> If the primary is somehow unreadable, the previous good
    /// save is still there. Losing one run is survivable; losing the account is not.
    ///
    /// <b>3. The file is signed.</b> A save file is a plaintext JSON blob in a folder the player can
    /// reach on a rooted device. Without a signature, editing <c>"Gems": 5</c> to <c>"Gems": 99999</c>
    /// is a 10-second exercise. The signature does not *prevent* that — see the honesty note below —
    /// but it detects it, and the detection is what feeds the server-side review.
    ///
    /// <b>Honesty about the signature.</b> The key is compiled into the binary, so anyone willing to
    /// decompile the APK can extract it and forge a valid signature. This is inherent: a client
    /// cannot keep a secret from the machine it runs on. It stops the casual save-file editor, which
    /// is the overwhelming majority of cheating, and it produces a tamper signal for the rest. Real
    /// protection is the server holding the authoritative balance (ARCHITECTURE.md §8). Nothing here
    /// should ever be mistaken for that.
    /// </summary>
    public sealed class FileSaveStore : ISaveStore
    {
        private const string FileName = "neonrush.save";
        private const string BackupName = "neonrush.save.bak";
        private const string TempName = "neonrush.save.tmp";

        /// <summary>
        /// Signing key. Obfuscated only in the sense that it is not a string literal shouting
        /// "SIGNING KEY" in the binary's string table. It is not, and cannot be, a real secret on a
        /// client. See the class remarks.
        /// </summary>
        private static readonly byte[] SigningKey =
        {
            0x4E, 0x65, 0x6F, 0x6E, 0x52, 0x75, 0x73, 0x68,
            0x9A, 0x3C, 0x71, 0xE8, 0x05, 0xBD, 0x42, 0x17,
            0x6D, 0xF0, 0x28, 0xC4, 0x93, 0x5A, 0xE1, 0x7B,
        };

        private readonly string _path;
        private readonly string _backupPath;
        private readonly string _tempPath;

        public FileSaveStore(string directory)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("Directory required.", nameof(directory));

            Directory.CreateDirectory(directory);

            _path = Path.Combine(directory, FileName);
            _backupPath = Path.Combine(directory, BackupName);
            _tempPath = Path.Combine(directory, TempName);
        }

        /// <summary>The correct save directory on every platform Unity ships to.</summary>
        public static FileSaveStore Default() => new(UnityEngine.Application.persistentDataPath);

        public LoadResult Load()
        {
            // Primary first, backup second. The backup is not a nicety: a device that lost power
            // mid-rotation can leave the primary missing, and the backup is the difference between
            // a player losing one run and losing their account.
            if (TryLoadFrom(_path, out var result)) return result;

            if (TryLoadFrom(_backupPath, out var fromBackup))
            {
                Debug.LogWarning("[FileSaveStore] Primary save was unusable; recovered from backup.");
                return fromBackup;
            }

            if (!File.Exists(_path) && !File.Exists(_backupPath))
            {
                // A brand-new install. Not an error, and it must not be reported as one.
                return LoadResult.Failed(LoadFailure.NotFound, SaveData.NewPlayer());
            }

            // Both copies exist and both are unusable. Report it loudly — this is data loss, and we
            // want to know how often it happens — but still boot the game.
            Debug.LogError("[FileSaveStore] Both the save and its backup are unusable. Starting a fresh profile.");
            return LoadResult.Failed(LoadFailure.Corrupt, SaveData.NewPlayer());
        }

        private bool TryLoadFrom(string path, out LoadResult result)
        {
            result = default;

            if (!File.Exists(path)) return false;

            string raw;
            try
            {
                raw = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FileSaveStore] Could not read '{path}': {e.Message}");
                return false;
            }

            SaveEnvelope envelope;
            try
            {
                envelope = JsonUtility.FromJson<SaveEnvelope>(raw);
            }
            catch (Exception)
            {
                // Almost always a write that the OS interrupted. Fall through to the backup.
                return false;
            }

            if (envelope?.payload == null || string.IsNullOrEmpty(envelope.signature)) return false;

            if (!SignatureMatches(envelope.payload, envelope.signature))
            {
                // The file was edited outside the game. Report it, refuse it, and let the backup or
                // the server provide the truth.
                Debug.LogWarning("[FileSaveStore] Save signature mismatch — the file was modified externally.");
                result = LoadResult.Failed(LoadFailure.SignatureMismatch, SaveData.NewPlayer());
                return true;
            }

            SaveDto dto;
            try
            {
                dto = JsonUtility.FromJson<SaveDto>(envelope.payload);
            }
            catch (Exception)
            {
                return false;
            }

            if (dto == null) return false;

            if (dto.version > SaveData.CurrentVersion)
            {
                // Written by a newer build — a cloud backup restored onto an older client. Guessing
                // at a schema we do not know is how you silently destroy a paying player's account.
                Debug.LogWarning(
                    $"[FileSaveStore] Save is version {dto.version}, this build understands {SaveData.CurrentVersion}. Refusing it.");

                result = LoadResult.Failed(LoadFailure.FutureVersion, SaveData.NewPlayer());
                return true;
            }

            result = LoadResult.Success(dto.ToDomain());
            return true;
        }

        public bool Save(SaveData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            try
            {
                var payload = JsonUtility.ToJson(SaveDto.FromDomain(data));

                var envelope = new SaveEnvelope
                {
                    payload = payload,
                    signature = Sign(payload),
                };

                var json = JsonUtility.ToJson(envelope);

                // --- The atomic write -------------------------------------------------------
                //
                // Step 1: write the new save to a scratch file. If the app dies here, the real save
                // is untouched and the player loses nothing.
                using (var stream = new FileStream(_tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, Encoding.UTF8))
                {
                    writer.Write(json);
                    writer.Flush();

                    // Force the bytes out of the OS cache and onto the actual storage. Without this,
                    // the write can still be sitting in a buffer when the device loses power, and
                    // the file that survives is a zero-byte husk.
                    stream.Flush(flushToDisk: true);
                }

                // Step 2: rotate the current save to the backup slot.
                if (File.Exists(_path))
                {
                    if (File.Exists(_backupPath)) File.Delete(_backupPath);
                    File.Move(_path, _backupPath);
                }

                // Step 3: promote the scratch file. A move is atomic on every platform we ship to,
                // so the save is either the old one or the new one — never a mixture.
                File.Move(_tempPath, _path);

                return true;
            }
            catch (Exception e)
            {
                // Disk full, permissions revoked, sandbox weirdness. Never crash the game over it,
                // but never pretend it worked either.
                Debug.LogError($"[FileSaveStore] Save failed: {e}");
                return false;
            }
        }

        public void Delete()
        {
            TryDelete(_path);
            TryDelete(_backupPath);
            TryDelete(_tempPath);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FileSaveStore] Could not delete '{path}': {e.Message}");
            }
        }

        private static string Sign(string payload)
        {
            using var hmac = new HMACSHA256(SigningKey);
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            return Convert.ToBase64String(hash);
        }

        private static bool SignatureMatches(string payload, string signature)
        {
            var expected = Sign(payload);

            // Constant-time compare. The timing-attack angle is admittedly theoretical on a phone,
            // but a short-circuiting string compare here is a free thing to get right.
            if (expected.Length != signature.Length) return false;

            var diff = 0;
            for (var i = 0; i < expected.Length; i++)
            {
                diff |= expected[i] ^ signature[i];
            }

            return diff == 0;
        }

        // ---------------------------------------------------------------------------------
        // Wire format. Kept in Infrastructure so the Domain is never deformed by what a
        // serialiser happens to support — JsonUtility, for instance, cannot serialise DateTime
        // or auto-properties, which would otherwise leak straight into SaveData's shape.
        // ---------------------------------------------------------------------------------

        [Serializable]
        private sealed class SaveEnvelope
        {
            public string payload;
            public string signature;
        }

        [Serializable]
        private sealed class SaveDto
        {
            public int version;
            public int coins;
            public int gems;
            public int bestScore;
            public int totalRuns;
            public long totalDistance;
            public string[] ownedItems;
            public bool adsRemoved;
            public long lastDailyClaimUtcTicks;
            public int dailyStreakDays;
            public int missionDay;
            public MissionDto[] missions;

            [Serializable]
            public sealed class MissionDto
            {
                public string id;
                public int progress;
                public bool rewarded;
            }

            /// <summary>DateTime as UTC ticks: JsonUtility cannot serialise DateTime, and ticks are unambiguous across time zones.</summary>
            public long savedAtUtcTicks;

            public static SaveDto FromDomain(SaveData data) => new()
            {
                version = data.Version,
                coins = data.Coins,
                gems = data.Gems,
                bestScore = data.BestScore,
                totalRuns = data.TotalRuns,
                totalDistance = data.TotalDistance,
                ownedItems = data.OwnedItems?.ToArray() ?? Array.Empty<string>(),
                adsRemoved = data.AdsRemoved,
                lastDailyClaimUtcTicks = data.LastDailyClaimUtc.Ticks,
                dailyStreakDays = data.DailyStreakDays,
                missionDay = data.MissionDay,
                missions = ToMissionDtos(data),
                savedAtUtcTicks = data.SavedAtUtc.Ticks,
            };

            private static MissionDto[] ToMissionDtos(SaveData data)
            {
                if (data.Missions == null || data.Missions.Count == 0) return Array.Empty<MissionDto>();

                var dtos = new MissionDto[data.Missions.Count];
                for (var i = 0; i < data.Missions.Count; i++)
                {
                    var m = data.Missions[i];
                    dtos[i] = new MissionDto { id = m.Id, progress = m.Progress, rewarded = m.Rewarded };
                }

                return dtos;
            }

            public SaveData ToDomain() => new()
            {
                Version = version,
                Coins = Math.Max(0, coins),
                Gems = Math.Max(0, gems),
                BestScore = Math.Max(0, bestScore),
                TotalRuns = Math.Max(0, totalRuns),
                TotalDistance = Math.Max(0, totalDistance),

                // A save written by an older build has no ownedItems array at all, and JsonUtility
                // leaves it null rather than empty. Every field read here must tolerate its own
                // absence — that is what makes a schema migration possible instead of a crash.
                OwnedItems = ownedItems != null
                    ? new System.Collections.Generic.List<string>(ownedItems)
                    : new System.Collections.Generic.List<string>(),

                AdsRemoved = adsRemoved,
                DailyStreakDays = Math.Max(0, dailyStreakDays),
                LastDailyClaimUtc = TicksToUtc(lastDailyClaimUtcTicks),
                MissionDay = Math.Max(0, missionDay),
                Missions = FromMissionDtos(missions),

                // Clamp rather than trust: a corrupted or hand-edited tick count outside DateTime's
                // legal range throws inside the DateTime constructor, which would turn a bad save
                // into a hard crash on boot.
                SavedAtUtc = TicksToUtc(savedAtUtcTicks),
            };

            private static DateTime TicksToUtc(long ticks) =>
                ticks > 0 && ticks <= DateTime.MaxValue.Ticks
                    ? new DateTime(ticks, DateTimeKind.Utc)
                    : DateTime.MinValue;

            private static System.Collections.Generic.List<SaveData.MissionSave> FromMissionDtos(MissionDto[] dtos)
            {
                var list = new System.Collections.Generic.List<SaveData.MissionSave>();
                if (dtos == null) return list; // Older save with no missions field: JsonUtility leaves it null.

                foreach (var dto in dtos)
                {
                    if (dto == null || string.IsNullOrEmpty(dto.id)) continue;

                    list.Add(new SaveData.MissionSave
                    {
                        Id = dto.id,
                        Progress = Math.Max(0, dto.progress),
                        Rewarded = dto.rewarded,
                    });
                }

                return list;
            }
        }
    }
}
