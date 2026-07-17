using System;
using NeonRush.Domain.Store;
using NUnit.Framework;

namespace NeonRush.Tests.EditMode
{
    /// <summary>
    /// Tests for the starter-pack availability window: when the offer is live, when it has expired,
    /// and how a wound-back clock is handled. Pure time arithmetic, no clock or store required.
    /// </summary>
    [TestFixture]
    public sealed class StarterPackTests
    {
        private static readonly DateTime Seen = new(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);

        private readonly StarterPackOffer _offer = new(windowHours: 48);

        [Test]
        public void BeforeFirstSeen_IsNotStarted()
        {
            Assert.That(_offer.StateAt(DateTime.MinValue, Seen), Is.EqualTo(StarterPackState.NotStarted));
        }

        [Test]
        public void WithinWindow_IsActive()
        {
            var now = Seen.AddHours(47);
            Assert.That(_offer.StateAt(Seen, now), Is.EqualTo(StarterPackState.Active));
        }

        [Test]
        public void AtAndPastExpiry_IsExpired()
        {
            Assert.That(_offer.StateAt(Seen, Seen.AddHours(48)), Is.EqualTo(StarterPackState.Expired));
            Assert.That(_offer.StateAt(Seen, Seen.AddHours(72)), Is.EqualTo(StarterPackState.Expired));
        }

        [Test]
        public void RemainingSeconds_CountsDownWithinTheWindow()
        {
            var now = Seen.AddHours(2);
            Assert.That(_offer.RemainingSeconds(Seen, now), Is.EqualTo(46 * 3600d).Within(0.5));
        }

        [Test]
        public void RemainingSeconds_IsZeroOnceExpired()
        {
            Assert.That(_offer.RemainingSeconds(Seen, Seen.AddHours(60)), Is.EqualTo(0d));
        }

        [Test]
        public void RemainingSeconds_IsTheFullWindowBeforeItStarts()
        {
            Assert.That(_offer.RemainingSeconds(DateTime.MinValue, Seen), Is.EqualTo(48 * 3600d).Within(0.5));
        }

        [Test]
        public void AClockWoundBackwards_DoesNotExpireTheOfferEarly()
        {
            // now is BEFORE firstSeen — a device whose date was moved back. The offer is a gift, so
            // it must stay Active rather than being revoked.
            var now = Seen.AddHours(-5);
            Assert.That(_offer.StateAt(Seen, now), Is.EqualTo(StarterPackState.Active));
            Assert.That(_offer.RemainingSeconds(Seen, now), Is.GreaterThan(48 * 3600d));
        }
    }
}
