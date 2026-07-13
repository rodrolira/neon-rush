using System;
using System.Diagnostics;
using NeonRush.Domain.Ports;

namespace NeonRush.Infrastructure.Time
{
    /// <summary>
    /// Production <see cref="IClock"/>.
    ///
    /// Two independent time sources, used for different jobs:
    ///
    ///  · A <see cref="Stopwatch"/> for <see cref="ElapsedRealtime"/>. It is monotonic — it cannot
    ///    be moved by the user, it never runs backwards, and it is what run durations, cooldowns
    ///    and powerup timers are measured against.
    ///
    ///  · A server-anchored wall clock for <see cref="UtcNow"/>. We take one trusted timestamp
    ///    from the backend at boot, remember the monotonic reading at that instant, and from then
    ///    on derive the current time as <c>anchor + (monotonic_now - monotonic_at_anchor)</c>.
    ///
    /// The consequence of that second point is the important one: once synced, moving the device
    /// clock does nothing at all. The player can set their phone forward a year and
    /// <see cref="UtcNow"/> will not budge, because it is no longer derived from the device clock.
    /// This is what protects daily rewards, streaks, season boundaries and flash-sale timers.
    ///
    /// Before the first sync we have no choice but to fall back to the device clock, and we say so
    /// through <see cref="IsServerTimeAuthoritative"/>. Callers must not grant time-gated rewards
    /// while that is false — show them, grant on resync.
    /// </summary>
    public sealed class SystemClock : IClock
    {
        /// <summary>Monotonic, starts at process start, immune to wall-clock changes.</summary>
        private readonly Stopwatch _monotonic = Stopwatch.StartNew();

        /// <summary>Trusted server time at the moment of the last sync. Null until first sync.</summary>
        private DateTime? _serverAnchorUtc;

        /// <summary>Monotonic reading captured at the same instant as <see cref="_serverAnchorUtc"/>.</summary>
        private TimeSpan _monotonicAtAnchor;

        /// <summary>Device wall-clock reading captured at the same instant, used to compute drift.</summary>
        private DateTime _deviceAtAnchorUtc;

        public TimeSpan ElapsedRealtime => _monotonic.Elapsed;

        public bool IsServerTimeAuthoritative => _serverAnchorUtc.HasValue;

        public DateTime UtcNow
        {
            get
            {
                if (_serverAnchorUtc is not { } anchor)
                {
                    // Not yet synced — the honest answer is the device clock, and callers are told
                    // it is untrusted via IsServerTimeAuthoritative.
                    return DateTime.UtcNow;
                }

                // Derived from the monotonic clock, NOT from DateTime.UtcNow. This is the line that
                // makes clock-tampering pointless.
                return anchor + (_monotonic.Elapsed - _monotonicAtAnchor);
            }
        }

        public TimeSpan DeviceClockDrift
        {
            get
            {
                if (_serverAnchorUtc is not { } anchor) return TimeSpan.Zero;

                // Drift measured at the moment of sync: how far ahead (+) or behind (-) the device
                // clock was relative to the server. Recomputed against the live device clock so a
                // player who changes their clock *after* sync is still detected.
                var deviceElapsed = DateTime.UtcNow - _deviceAtAnchorUtc;
                var trueElapsed = _monotonic.Elapsed - _monotonicAtAnchor;

                return deviceElapsed - trueElapsed + (_deviceAtAnchorUtc - anchor);
            }
        }

        /// <summary>
        /// Anchors the clock to a trusted server timestamp. Called at boot once the backend
        /// responds, and again on each successful resync.
        /// </summary>
        /// <param name="serverUtcNow">Authoritative UTC time from the backend.</param>
        public void SyncToServer(DateTime serverUtcNow)
        {
            if (serverUtcNow.Kind == DateTimeKind.Local)
            {
                // A local-kind timestamp here means someone passed DateTime.Now instead of a server
                // value. Converting silently would bake the device's timezone offset into every
                // future deadline in the game.
                throw new ArgumentException(
                    "Server time must be UTC. A Local DateTime here indicates the device clock is " +
                    "being passed in by mistake, which would defeat the entire purpose of this class.",
                    nameof(serverUtcNow));
            }

            _serverAnchorUtc = DateTime.SpecifyKind(serverUtcNow, DateTimeKind.Utc);
            _monotonicAtAnchor = _monotonic.Elapsed;
            _deviceAtAnchorUtc = DateTime.UtcNow;
        }
    }
}
