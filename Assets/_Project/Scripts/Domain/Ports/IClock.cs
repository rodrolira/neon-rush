using System;

namespace NeonRush.Domain.Ports
{
    /// <summary>
    /// The single source of time for all gameplay and economy logic.
    ///
    /// Every deadline in Neon Rush is a place a player can gain value by changing the device
    /// clock: daily rewards, login streaks, energy refills, season end, flash-sale countdowns,
    /// timed boosts, offer expiry. A player who sets their phone forward a day and claims the
    /// daily reward, then sets it back, has minted currency out of nothing.
    ///
    /// Therefore economy and gameplay code MUST NOT call <c>DateTime.Now</c>, <c>DateTime.UtcNow</c>,
    /// <c>Time.time</c>, <c>Time.realtimeSinceStartup</c> or <c>Environment.TickCount</c> directly.
    /// Everything goes through this port. That gives us two things:
    ///
    ///  · Production safety — the implementation prefers server time and detects tampering.
    ///  · Testability — "does the streak reset correctly across a season boundary in UTC-11?"
    ///    becomes a one-second unit test with a FakeClock, not a QA ticket and a phone with
    ///    its date changed by hand.
    /// </summary>
    public interface IClock
    {
        /// <summary>
        /// Current UTC wall-clock time, server-authoritative where possible.
        ///
        /// Use this for anything a player could profit from shifting: reward eligibility,
        /// streak evaluation, offer expiry, season boundaries.
        ///
        /// Never assume this is monotonic — it can jump backwards when a server resync corrects
        /// a drifted device. For measuring elapsed time, use <see cref="ElapsedRealtime"/>.
        /// </summary>
        DateTime UtcNow { get; }

        /// <summary>
        /// Monotonic time since process start. Never jumps, never goes backwards, unaffected by
        /// the user changing the device clock.
        ///
        /// Use this for durations — run length, powerup remaining time, ad cooldowns — where
        /// what matters is "how much time has passed", not "what time is it".
        /// </summary>
        TimeSpan ElapsedRealtime { get; }

        /// <summary>
        /// True when <see cref="UtcNow"/> is backed by server time rather than an unverified
        /// device clock.
        ///
        /// While false, the game is running on the player's own clock, which is not trustworthy.
        /// Time-gated rewards should be shown but not granted until this becomes true — grant on
        /// resync instead. Refusing to *display* them would punish an honest offline player;
        /// refusing to *grant* them is what stops the cheat.
        /// </summary>
        bool IsServerTimeAuthoritative { get; }

        /// <summary>
        /// Signed difference between the device's own wall clock and trusted server time
        /// (positive = device runs ahead). Zero when no server sample has been taken yet.
        ///
        /// A large magnitude is a tamper signal worth reporting to analytics. It is evidence,
        /// not proof: travellers cross time zones and phones with dead batteries lose their
        /// clocks. Use it to flag accounts for server-side review, never to punish on device.
        /// </summary>
        TimeSpan DeviceClockDrift { get; }
    }
}
