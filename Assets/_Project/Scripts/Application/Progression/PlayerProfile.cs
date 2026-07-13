using System;
using NeonRush.Application.Events;
using NeonRush.Core.Events;
using NeonRush.Domain.Save;

namespace NeonRush.Application.Progression
{
    /// <summary>
    /// The player's lifetime record: best score, runs played, distance travelled.
    ///
    /// Separate from the wallet on purpose. These are *achievements* — they only ever go up, they
    /// are never spent, and they are never a cheat target worth obscuring (nobody buys anything with
    /// a best score). Conflating them with currency would mean applying the wallet's whole
    /// anti-tamper and server-authority apparatus to numbers that do not need it.
    ///
    /// Pure C#. Feeds missions, achievements and leaderboards later.
    /// </summary>
    public sealed class PlayerProfile : IDisposable
    {
        private readonly IDisposable _subscription;

        public PlayerProfile(IEventBus bus, SaveData loaded = null)
        {
            if (bus == null) throw new ArgumentNullException(nameof(bus));

            if (loaded != null)
            {
                BestScore = loaded.BestScore;
                TotalRuns = loaded.TotalRuns;
                TotalDistance = loaded.TotalDistance;
            }

            _subscription = bus.Subscribe<RunEnded>(OnRunEnded);
        }

        public int BestScore { get; private set; }

        public int TotalRuns { get; private set; }

        /// <summary>Lifetime metres. A long, not an int: a committed player will pass two billion.</summary>
        public long TotalDistance { get; private set; }

        /// <summary>True when the run that just ended set a new personal best. Read by the HUD.</summary>
        public bool LastRunWasPersonalBest { get; private set; }

        private void OnRunEnded(RunEnded e)
        {
            TotalRuns++;
            TotalDistance += (long)e.DistanceMetres;

            LastRunWasPersonalBest = e.Score > BestScore;

            if (LastRunWasPersonalBest)
            {
                BestScore = e.Score;
            }
        }

        /// <summary>Copies this profile into <paramref name="data"/> for persistence.</summary>
        public void WriteTo(SaveData data)
        {
            data.BestScore = BestScore;
            data.TotalRuns = TotalRuns;
            data.TotalDistance = TotalDistance;
        }

        public void Dispose() => _subscription.Dispose();
    }
}
