using System;

namespace NeonRush.Domain.Economy
{
    /// <summary>
    /// An int that is never stored in plaintext in memory, and that knows when it has been tampered
    /// with.
    ///
    /// <b>Read this before trusting it.</b> This is not security. A determined attacker with a
    /// debugger will defeat it, and that is fine, because it is not what it is for. It does exactly
    /// two useful things:
    ///
    ///  1. <b>It defeats the memory scanner.</b> The overwhelmingly common cheat is not a debugger,
    ///     it is a tool like GameGuardian: search memory for the number 250 (your coin count),
    ///     spend some, search for 180, intersect the candidates, overwrite. That entire workflow
    ///     depends on the value existing somewhere in memory as a plain int. Here it does not — what
    ///     is stored is <c>value XOR key</c>, with a per-instance random key, so the number the
    ///     player sees on screen appears nowhere in the process. This alone stops the great majority
    ///     of casual cheating, which is the majority of cheating.
    ///
    ///  2. <b>It reports tampering.</b> If someone does overwrite the obscured bytes, the stored
    ///     checksum no longer matches the decrypted value, and reading it throws. That converts a
    ///     silent economy exploit into a loud, attributable telemetry signal — we learn which
    ///     accounts are doing it.
    ///
    /// What actually protects the economy is the server (see ARCHITECTURE.md §8): balances are
    /// authoritative in Firestore, currency is granted by Cloud Functions, and IAP receipts are
    /// validated server-side. This type raises the cost of casual cheating and generates evidence.
    /// It is a lock on a garden gate, not a bank vault, and no code anywhere should treat it as one.
    /// </summary>
    public struct ObscuredInt : IEquatable<ObscuredInt>
    {
        // A shared PRNG for key generation. Not cryptographic — it does not need to be. The key only
        // has to be unpredictable enough that the obscured value is not trivially derivable, and
        // different per instance so two wallets with the same balance do not hold the same bytes
        // (which would let a scanner find them by correlation).
        [ThreadStatic] private static Random _random;

        private int _key;
        private int _obscured;
        private int _checksum;

        /// <summary>True once this instance has been initialised. A default(ObscuredInt) is zero, not corrupt.</summary>
        private bool _initialised;

        public ObscuredInt(int value)
        {
            _random ??= new Random(Environment.TickCount ^ Environment.CurrentManagedThreadId);

            _key = _random.Next(int.MinValue, int.MaxValue);
            _obscured = value ^ _key;
            _checksum = Checksum(value, _key);
            _initialised = true;
        }

        /// <summary>
        /// The real value.
        /// </summary>
        /// <exception cref="TamperDetectedException">
        /// The obscured bytes no longer agree with the checksum — something outside this type wrote
        /// to them. The caller must treat this as a cheat signal: refuse the operation, report it,
        /// and resynchronise the balance from the server.
        /// </exception>
        public int Value
        {
            get
            {
                // A zero-initialised struct (e.g. an array element) is legitimately zero, not corrupt.
                if (!_initialised) return 0;

                var value = _obscured ^ _key;

                if (Checksum(value, _key) != _checksum)
                {
                    throw new TamperDetectedException(
                        "An obscured value failed its integrity check. Its memory was modified " +
                        "externally — this is a cheat attempt, not a bug.");
                }

                return value;
            }
        }

        /// <summary>
        /// Reads the value without throwing. Returns false when tampering is detected.
        /// Use this on any path where throwing would crash the game in front of an honest player
        /// whose device simply glitched.
        /// </summary>
        public bool TryGetValue(out int value)
        {
            if (!_initialised)
            {
                value = 0;
                return true;
            }

            value = _obscured ^ _key;

            if (Checksum(value, _key) == _checksum) return true;

            value = 0;
            return false;
        }

        /// <summary>
        /// Re-keys and rewrites the value.
        ///
        /// Re-keying on every write is what stops the classic "search, change, search again"
        /// differential attack: after each write the obscured bytes change completely, even if the
        /// underlying value did not, so the intersection of two memory scans is empty.
        /// </summary>
        public void Set(int value)
        {
            _random ??= new Random(Environment.TickCount ^ Environment.CurrentManagedThreadId);

            _key = _random.Next(int.MinValue, int.MaxValue);
            _obscured = value ^ _key;
            _checksum = Checksum(value, _key);
            _initialised = true;
        }

        /// <summary>
        /// A cheap integrity hash over (value, key). Not a cryptographic MAC — an attacker who
        /// reverse-engineers the binary can recompute it. It exists to catch a blind memory
        /// overwrite, which is what a memory scanner does, and it catches that reliably.
        /// </summary>
        private static int Checksum(int value, int key)
        {
            unchecked
            {
                var h = (uint)value * 2654435761u;   // Knuth's multiplicative hash
                h ^= (uint)key * 2246822519u;
                h = (h << 13) | (h >> 19);           // rotate, so adjacent values differ wildly
                h *= 3266489917u;
                return (int)h;
            }
        }

        public static implicit operator int(ObscuredInt obscured) => obscured.Value;

        public static implicit operator ObscuredInt(int value) => new(value);

        public bool Equals(ObscuredInt other) => Value == other.Value;

        public override bool Equals(object obj) => obj is ObscuredInt other && Equals(other);

        public override int GetHashCode() => Value.GetHashCode();

        public override string ToString() => Value.ToString();
    }

    /// <summary>
    /// Raised when an obscured value's memory has been modified from outside.
    ///
    /// This is never a programmer error and never a legitimate runtime condition — it means someone
    /// attached a memory editor. Callers should report it and resynchronise from the server, not
    /// swallow it.
    /// </summary>
    public sealed class TamperDetectedException : Exception
    {
        public TamperDetectedException(string message) : base(message) { }
    }
}
