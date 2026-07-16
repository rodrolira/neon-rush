using NeonRush.Application.Analytics;
using NeonRush.Application.Economy;
using NeonRush.Application.Events;
using NeonRush.Application.Run;
using NeonRush.Core.Events;
using NeonRush.Domain.Economy;
using NeonRush.Domain.Run;
using NeonRush.Infrastructure.Analytics;
using NUnit.Framework;

namespace NeonRush.Tests.EditMode
{
    /// <summary>
    /// Tests for the analytics funnel.
    ///
    /// These assert which events fire and with which parameters — because a dashboard is only as
    /// trustworthy as its schema, and the schema is only trustworthy if a renamed parameter or a
    /// dropped field fails a test instead of silently emptying a dashboard column three weeks after
    /// it shipped.
    /// </summary>
    [TestFixture]
    public sealed class AnalyticsReporterTests
    {
        private EventBus _bus;
        private RecordingAnalytics _analytics;
        private AnalyticsReporter _reporter;

        [SetUp]
        public void SetUp()
        {
            _bus = new EventBus();
            _analytics = new RecordingAnalytics();
            _reporter = new AnalyticsReporter(_analytics, _bus);
        }

        [TearDown]
        public void TearDown()
        {
            _reporter.Dispose();
            _bus.Dispose();
        }

        [Test]
        public void RunEnd_CarriesTheFullRunSummary()
        {
            _bus.Publish(new RunEnded(3, 512.7f, 21, 730, DeathCause.HitObstacle, 47.2f));

            var e = _analytics.Find(AnalyticsEvents.RunEnd);

            Assert.That(e, Is.Not.Null);
            Assert.That(e.Param<int>(AnalyticsEvents.Params.RunNumber), Is.EqualTo(3));
            Assert.That(e.Param<int>(AnalyticsEvents.Params.DistanceMetres), Is.EqualTo(512));
            Assert.That(e.Param<int>(AnalyticsEvents.Params.DurationSeconds), Is.EqualTo(47));
            Assert.That(e.Param<int>(AnalyticsEvents.Params.Coins), Is.EqualTo(21));
            Assert.That(e.Param<int>(AnalyticsEvents.Params.Score), Is.EqualTo(730));
            Assert.That(e.Param<string>(AnalyticsEvents.Params.DeathCause), Is.EqualTo("HitObstacle"));
        }

        [Test]
        public void FaucetsAndSinks_AreSeparateEvents()
        {
            // The first question every economy dashboard asks is "faucets vs sinks over time".
            // Separate events answer it directly; a single signed event forces a computed column
            // into every query.
            var wallet = new Wallet(_bus, startingCoins: 100);

            wallet.Credit(CurrencyType.Coins, 50, TransactionReason.RunReward);
            wallet.TryDebit(CurrencyType.Coins, 30, TransactionReason.StorePurchase);

            var earned = _analytics.Find(AnalyticsEvents.CurrencyEarned);
            var spent = _analytics.Find(AnalyticsEvents.CurrencySpent);

            Assert.That(earned, Is.Not.Null);
            Assert.That(earned.Param<int>(AnalyticsEvents.Params.Amount), Is.EqualTo(50));
            Assert.That(earned.Param<string>(AnalyticsEvents.Params.Reason), Is.EqualTo("RunReward"));

            Assert.That(spent, Is.Not.Null);
            Assert.That(spent.Param<int>(AnalyticsEvents.Params.Amount), Is.EqualTo(30),
                "Spend amounts are reported positive; the event name carries the direction.");
        }

        [Test]
        public void PurchaseBlocked_ReportsTheShortfall()
        {
            // The offer-targeting signal. If this ever loses its shortfall parameter, the most
            // valuable dashboard in the game silently goes blank.
            var wallet = new Wallet(_bus, startingGems: 10);

            wallet.TryDebit(CurrencyType.Gems, 250, TransactionReason.StorePurchase);

            var e = _analytics.Find(AnalyticsEvents.PurchaseBlocked);

            Assert.That(e, Is.Not.Null);
            Assert.That(e.Param<int>(AnalyticsEvents.Params.Price), Is.EqualTo(250));
            Assert.That(e.Param<int>(AnalyticsEvents.Params.Balance), Is.EqualTo(10));
            Assert.That(e.Param<int>(AnalyticsEvents.Params.Shortfall), Is.EqualTo(240));
        }

        [Test]
        public void CoinPickups_AreNotTrackedIndividually()
        {
            // Per-coin events would be nearly all of our volume with nearly none of the information —
            // the run summary already carries the total. Track decisions, not keystrokes.
            var tuning = new RunTuning();
            var session = new RunSession(tuning, _bus);

            session.Begin();
            for (var i = 0; i < 25; i++) session.CollectCoin();
            session.End(DeathCause.HitObstacle);

            Assert.That(_analytics.CountOf("coin_collected"), Is.Zero,
                "Individual coin pickups must not be analytics events.");

            var runEnd = _analytics.Find(AnalyticsEvents.RunEnd);
            Assert.That(runEnd.Param<int>(AnalyticsEvents.Params.Coins), Is.EqualTo(25),
                "The run summary carries the coin total instead.");
        }

        [Test]
        public void Revive_IsTracked()
        {
            _bus.Publish(new RunResumed(2, 1));

            var e = _analytics.Find(AnalyticsEvents.RunRevive);

            Assert.That(e, Is.Not.Null);
            Assert.That(e.Param<int>(AnalyticsEvents.Params.RevivesUsed), Is.EqualTo(1));
        }

        [Test]
        public void WalletTampering_IsReported()
        {
            _bus.Publish(new WalletTamperDetected(CurrencyType.Gems));

            Assert.That(_analytics.Find(AnalyticsEvents.WalletTampered), Is.Not.Null,
                "A tamper signal that never reaches analytics is a cheat nobody investigates.");
        }

        [Test]
        public void Dispose_StopsTracking()
        {
            _reporter.Dispose();

            _bus.Publish(new RunStarted(1));

            Assert.That(_analytics.CountOf(AnalyticsEvents.RunStart), Is.Zero,
                "A disposed reporter must not keep tracking from beyond the grave.");
        }

        [Test]
        public void RunsBucket_SegmentsSensibly()
        {
            Assert.That(AnalyticsEvents.BucketRuns(1), Is.EqualTo("1-5"));
            Assert.That(AnalyticsEvents.BucketRuns(5), Is.EqualTo("1-5"));
            Assert.That(AnalyticsEvents.BucketRuns(6), Is.EqualTo("6-20"));
            Assert.That(AnalyticsEvents.BucketRuns(100), Is.EqualTo("21-100"));
            Assert.That(AnalyticsEvents.BucketRuns(10_000), Is.EqualTo("500+"));
        }
    }

    [TestFixture]
    public sealed class RecordingAnalyticsTests
    {
        [Test]
        public void TheBufferIsCapped()
        {
            // An unbounded event list in a long Editor session is a slow memory leak in a lab coat.
            var analytics = new RecordingAnalytics();

            for (var i = 0; i < 1000; i++) analytics.Track("e");

            Assert.That(analytics.Events.Count, Is.LessThanOrEqualTo(500));
        }

        [Test]
        public void RecordedParameters_AreACopy()
        {
            // The caller may reuse its dictionary; the recording must not change after the fact.
            var analytics = new RecordingAnalytics();
            var parameters = new System.Collections.Generic.Dictionary<string, object> { ["k"] = 1 };

            analytics.Track("e", parameters);
            parameters["k"] = 999;

            Assert.That(analytics.Find("e").Param<int>("k"), Is.EqualTo(1));
        }

        [Test]
        public void NullAndEmptyNames_AreIgnored()
        {
            var analytics = new RecordingAnalytics();

            Assert.DoesNotThrow(() => analytics.Track(null));
            Assert.DoesNotThrow(() => analytics.Track(string.Empty));
            Assert.That(analytics.Events, Is.Empty);
        }

        [Test]
        public void UserPropertiesAreStored()
        {
            var analytics = new RecordingAnalytics();
            analytics.SetUserProperty("runs_bucket", "6-20");

            Assert.That(analytics.UserProperties["runs_bucket"], Is.EqualTo("6-20"));
        }
    }
}
