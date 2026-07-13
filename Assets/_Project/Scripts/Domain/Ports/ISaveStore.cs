using NeonRush.Domain.Save;

namespace NeonRush.Domain.Ports
{
    /// <summary>
    /// Where the player's progress physically lives.
    ///
    /// A port, not an implementation: the file-on-disk store, the in-memory test store, and (later)
    /// the Firestore cloud store all satisfy it. That is what lets the entire save/load/conflict
    /// logic be unit-tested with no filesystem, no device and no network.
    /// </summary>
    public interface ISaveStore
    {
        /// <summary>
        /// Reads the save.
        ///
        /// Never throws. A load failure — corrupt file, tampered signature, unreadable disk — is an
        /// ordinary condition on real devices, and it is reported through
        /// <see cref="LoadResult.Failure"/> with a usable fallback profile attached. The game must
        /// always be able to boot.
        /// </summary>
        LoadResult Load();

        /// <summary>
        /// Writes the save.
        ///
        /// Implementations MUST be atomic: the previously-good save must survive the app being
        /// killed mid-write. On Android the OS terminates backgrounded apps without warning, and a
        /// non-atomic write is how a player loses everything and leaves a one-star review.
        /// </summary>
        /// <returns>True when the write is durably committed.</returns>
        bool Save(SaveData data);

        /// <summary>Erases the save. Used by "reset progress" and by QA.</summary>
        void Delete();
    }
}
