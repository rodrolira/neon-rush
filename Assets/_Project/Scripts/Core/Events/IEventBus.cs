using System;

namespace NeonRush.Core.Events
{
    /// <summary>
    /// Synchronous, in-process publish/subscribe.
    ///
    /// Systems communicate through this bus instead of holding references to each other.
    /// A coin pickup is published once by gameplay and consumed independently by the wallet,
    /// the mission tracker, analytics, audio and the HUD — none of which know the others exist.
    ///
    /// Delivery is deliberately synchronous. Asynchronous delivery would make handler ordering
    /// non-deterministic, and non-deterministic ordering makes the mission and anti-cheat
    /// systems untestable: "did the wallet update before the mission checked it?" must have a
    /// single, reproducible answer.
    /// </summary>
    public interface IEventBus
    {
        /// <summary>
        /// Subscribes to <typeparamref name="T"/>. Dispose the returned handle to unsubscribe.
        ///
        /// The handle MUST be disposed when the subscriber dies. A run-scoped system that
        /// subscribes and never unsubscribes is the classic Unity leak: it is kept alive by the
        /// bus, it keeps handling events from runs it no longer belongs to, and its scores get
        /// double-counted.
        /// </summary>
        IDisposable Subscribe<T>(Action<T> handler) where T : struct;

        /// <summary>Delivers <paramref name="evt"/> to every current subscriber, in subscription order.</summary>
        void Publish<T>(in T evt) where T : struct;
    }
}
