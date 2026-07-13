using System;
using System.Collections.Generic;
using NeonRush.Core.Events;

namespace NeonRush.Domain.Inventory
{
    /// <summary>An item entered the player's collection.</summary>
    public readonly struct ItemGranted
    {
        public readonly string ItemId;

        public ItemGranted(string itemId) => ItemId = itemId;
    }

    /// <summary>
    /// What the player owns.
    ///
    /// A set of item ids and nothing more. That minimalism is the point: entitlements are the data
    /// most likely to be restored from a cloud backup, merged across devices, or reconciled against
    /// a server, and every extra field is another thing that can conflict. Ownership is boolean —
    /// you have the skin or you do not — so the merge rule is trivially correct: the union always
    /// wins. A player who bought a skin on their old phone must never lose it, and taking a
    /// purchased item away from someone is the single fastest way to earn a chargeback.
    /// </summary>
    public sealed class Inventory
    {
        private readonly HashSet<string> _owned;
        private readonly IEventBus _bus;

        public Inventory(IEventBus bus, IEnumerable<string> owned = null)
        {
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
            _owned = owned != null ? new HashSet<string>(owned) : new HashSet<string>();
        }

        /// <summary>Everything the player owns. Stable order is not guaranteed.</summary>
        public IReadOnlyCollection<string> Owned => _owned;

        public bool Has(string itemId) => itemId != null && _owned.Contains(itemId);

        /// <summary>
        /// Grants an item. Idempotent: granting something twice is a no-op, not an error.
        ///
        /// Idempotency is not politeness here, it is a requirement. Grants arrive from several
        /// independent sources — a purchase, a cloud sync, a season-pass reward, a support grant —
        /// and they will overlap. A grant that threw on a duplicate would turn a routine cloud
        /// re-sync into a crash.
        /// </summary>
        /// <returns>True if the item was newly added.</returns>
        public bool Grant(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId)) throw new ArgumentException("Item id required.", nameof(itemId));

            if (!_owned.Add(itemId)) return false;

            _bus.Publish(new ItemGranted(itemId));
            return true;
        }

        /// <summary>
        /// Merges the server's entitlements into the local set.
        ///
        /// Union, never replace. The server is authoritative on what the player *has bought*, but a
        /// purchase made offline a moment ago is real too and has not reached the server yet.
        /// Replacing would silently delete it. The union is the only rule that cannot lose a
        /// purchase, and losing a purchase is unforgivable.
        /// </summary>
        public void MergeFromServer(IEnumerable<string> serverOwned)
        {
            if (serverOwned == null) return;

            foreach (var id in serverOwned)
            {
                Grant(id);
            }
        }
    }
}
