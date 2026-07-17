using System;
using NeonRush.Domain.Inventory;
using NeonRush.Domain.Ports;
using NeonRush.Domain.Store;

namespace NeonRush.Application.Store
{
    /// <summary>
    /// Decides when the starter pack should be put in front of the player, and tracks its one-shot
    /// window.
    ///
    /// The purchase itself goes through the ordinary <see cref="StoreService"/> — the pack is just the
    /// catalogue's <c>bundle_starter</c>, so it gets the same real-money flow, server receipt
    /// validation and grant as everything else. This service adds only the two things that turn a
    /// buried catalogue row into the highest-converting product in the game: <b>ownership gating</b>
    /// (offer it only to players who have not bought it) and <b>urgency</b> (a window that starts the
    /// moment they first see it and never reopens).
    /// </summary>
    public sealed class StarterPackService
    {
        /// <summary>The catalogue id of the starter bundle. Also the entitlement that marks it owned.</summary>
        public const string BundleItemId = "bundle_starter";

        private readonly StarterPackOffer _offer;
        private readonly Inventory _inventory;
        private readonly IClock _clock;
        private readonly bool _enabled;

        private DateTime _firstSeenUtc;
        private bool _shownThisSession;

        public StarterPackService(StarterPackOffer offer, Inventory inventory, IClock clock, bool enabled, DateTime firstSeenUtc)
        {
            _offer = offer ?? throw new ArgumentNullException(nameof(offer));
            _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _enabled = enabled;
            _firstSeenUtc = firstSeenUtc;
        }

        /// <summary>True once the pack is bought — from this or any past device (it lives in the Inventory).</summary>
        public bool Owned => _inventory.Has(BundleItemId);

        /// <summary>When the player first saw the offer, for persistence. MinValue = never shown.</summary>
        public DateTime FirstSeenUtc => _firstSeenUtc;

        /// <summary>Guards against re-popping the offer repeatedly within one play session.</summary>
        public bool ShownThisSession => _shownThisSession;

        /// <summary>Seconds left in the window, for the countdown label.</summary>
        public double RemainingSeconds() => _offer.RemainingSeconds(_firstSeenUtc, _clock.UtcNow);

        /// <summary>
        /// True when the offer should be presented right now: enabled remotely, not already owned, and
        /// still inside its window. Ownership and expiry are permanent gates; a live game can also kill
        /// the whole offer instantly by flipping the remote flag.
        /// </summary>
        public bool ShouldOffer()
        {
            if (!_enabled || Owned) return false;

            return _offer.StateAt(_firstSeenUtc, _clock.UtcNow) != StarterPackState.Expired;
        }

        /// <summary>
        /// Records that the offer was shown. Starts the countdown on the first-ever showing (stamped
        /// from the trusted clock, never the device clock) and marks it shown for this session.
        /// </summary>
        public void MarkSeen()
        {
            if (_firstSeenUtc == DateTime.MinValue) _firstSeenUtc = _clock.UtcNow;

            _shownThisSession = true;
        }
    }
}
