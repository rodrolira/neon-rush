using NeonRush.Domain.Ports;
using NeonRush.Domain.Save;

namespace NeonRush.Infrastructure.Save
{
    /// <summary>
    /// A save store that lives in RAM.
    ///
    /// This is a real, shipping adapter, not a stub. It is what runs in CI, where there is no
    /// writable sandbox to speak of, and it is what lets the whole save/load/conflict pipeline be
    /// tested exhaustively in milliseconds. It also gives QA a "no persistence" mode for reproducing
    /// first-launch behaviour without hunting down a file on a device.
    ///
    /// It can simulate the failure modes that matter, because the interesting bugs in a save system
    /// are all in the failure paths — and those are exactly the paths that never get exercised if
    /// the only store you can test against always works.
    /// </summary>
    public sealed class InMemorySaveStore : ISaveStore
    {
        private SaveData _data;

        /// <summary>When set, the next <see cref="Load"/> reports this failure instead of returning data.</summary>
        public LoadFailure NextLoadFailure { get; set; } = LoadFailure.None;

        /// <summary>When true, <see cref="Save"/> reports failure — simulates a full or read-only disk.</summary>
        public bool FailWrites { get; set; }

        /// <summary>How many times <see cref="Save"/> has been called. Used to assert that saves are debounced.</summary>
        public int WriteCount { get; private set; }

        public LoadResult Load()
        {
            if (NextLoadFailure != LoadFailure.None)
            {
                return LoadResult.Failed(NextLoadFailure, SaveData.NewPlayer());
            }

            if (_data == null)
            {
                return LoadResult.Failed(LoadFailure.NotFound, SaveData.NewPlayer());
            }

            // Hand back a copy. Returning the live instance would let a caller mutate the "stored"
            // data without ever writing it, which hides save bugs rather than exposing them.
            return LoadResult.Success(_data.Clone());
        }

        public bool Save(SaveData data)
        {
            if (FailWrites) return false;

            _data = data.Clone();
            WriteCount++;
            return true;
        }

        public void Delete()
        {
            _data = null;
        }
    }
}
