using System;
using NUnit.Framework;
using Sub = NeonRush.Domain.Subscription.Subscription;

namespace NeonRush.Tests.EditMode
{
    /// <summary>
    /// Tests for the subscription's time logic: active/lapsed, renewal that stacks rather than
    /// truncates, and the once-per-UTC-day daily perk. Pure arithmetic, no clock or store.
    /// </summary>
    [TestFixture]
    public sealed class SubscriptionTests
    {
        private static readonly DateTime Now = new(2026, 7, 16, 10, 0, 0, DateTimeKind.Utc);

        [Test]
        public void FreshSubscription_IsInactive()
        {
            var sub = new Sub();
            Assert.That(sub.IsActiveAt(Now), Is.False);
            Assert.That(sub.RemainingAt(Now), Is.EqualTo(TimeSpan.Zero));
        }

        [Test]
        public void Extend_FromInactive_StartsFromNow()
        {
            var sub = new Sub();
            sub.Extend(Now, days: 30);

            Assert.That(sub.IsActiveAt(Now), Is.True);
            Assert.That(sub.ExpiryUtc, Is.EqualTo(Now.AddDays(30)));
        }

        [Test]
        public void Extend_WhileActive_StacksOntoRemainingTime()
        {
            var sub = new Sub();
            sub.Extend(Now, days: 30);          // expires Now+30
            sub.Extend(Now.AddDays(5), days: 30); // renews 5 days in — must add, not reset

            Assert.That(sub.ExpiryUtc, Is.EqualTo(Now.AddDays(60)));
        }

        [Test]
        public void IsActive_FlipsAtExpiry()
        {
            var sub = new Sub();
            sub.Extend(Now, days: 7);

            Assert.That(sub.IsActiveAt(Now.AddDays(7).AddSeconds(-1)), Is.True);
            Assert.That(sub.IsActiveAt(Now.AddDays(7)), Is.False);
        }

        [Test]
        public void DailyGrant_AvailableWhileActiveOnANewDay()
        {
            var sub = new Sub();
            sub.Extend(Now, days: 30);

            Assert.That(sub.DailyGrantAvailableAt(Now), Is.True);
        }

        [Test]
        public void DailyGrant_NotAvailableTwiceInTheSameUtcDay()
        {
            var sub = new Sub();
            sub.Extend(Now, days: 30);

            sub.MarkDailyGranted(Now);

            Assert.That(sub.DailyGrantAvailableAt(Now.AddHours(6)), Is.False);
            Assert.That(sub.DailyGrantAvailableAt(Now.AddDays(1)), Is.True, "A new UTC day re-opens the grant.");
        }

        [Test]
        public void DailyGrant_NeverAvailableWhenLapsed()
        {
            var sub = new Sub();
            sub.Extend(Now, days: 1);

            Assert.That(sub.DailyGrantAvailableAt(Now.AddDays(2)), Is.False);
        }
    }
}
