using System.Collections.Generic;

namespace NeonRush.Domain.Ports
{
    /// <summary>
    /// The analytics pipe (Firebase Analytics in production).
    ///
    /// Behind a port for the usual testability reasons, plus one specific to analytics: tests must be
    /// able to assert <b>which events fired with which parameters</b> — "did the purchase failure
    /// report its shortfall?" — and that is only possible against a recording fake, never against a
    /// live SDK that swallows events into a dashboard you cannot read from a test.
    ///
    /// Design rules for every event sent through this port:
    ///
    ///  · <b>Names and parameters are decided once, in AnalyticsEvents, not at call sites.</b> An
    ///    analytics schema that grows ad hoc — one engineer logs "run_end", another "runEnded" —
    ///    produces a dashboard where no query can be trusted. The taxonomy is code-reviewed like any
    ///    other API.
    ///
    ///  · <b>Fire-and-forget, never blocking.</b> Analytics is a passenger. If the pipe is slow or
    ///    down, gameplay must not notice. Implementations buffer and drop; they never stall a frame.
    ///
    ///  · <b>No PII.</b> Nothing that identifies a person goes through this port: no emails, no
    ///    device contacts, no precise location. This is not just policy — it is GDPR/COPPA exposure,
    ///    and a mobile game's audience always includes children.
    /// </summary>
    public interface IAnalyticsService
    {
        /// <summary>Sends an event. Must return immediately; must never throw into the caller.</summary>
        void Track(string eventName, IReadOnlyDictionary<string, object> parameters = null);

        /// <summary>
        /// Sets a property attached to every subsequent event (player level, total runs bucket, ...).
        /// This is what lets a dashboard segment "whales vs day-one players" without every event
        /// carrying the full player state.
        /// </summary>
        void SetUserProperty(string name, string value);
    }
}
