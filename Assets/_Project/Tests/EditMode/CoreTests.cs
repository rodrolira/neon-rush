using System;
using System.Collections.Generic;
using NeonRush.Application.States;
using NeonRush.Core.DI;
using NeonRush.Core.Events;
using NeonRush.Domain.Run;
using NUnit.Framework;

namespace NeonRush.Tests.EditMode
{
    /// <summary>Tests for the event bus. Most of these guard against subtle dispatch bugs that only appear under load.</summary>
    [TestFixture]
    public sealed class EventBusTests
    {
        private struct Ping
        {
            public int Value;
        }

        private struct Pong
        {
        }

        private EventBus _bus;

        [SetUp]
        public void SetUp() => _bus = new EventBus();

        [TearDown]
        public void TearDown() => _bus.Dispose();

        [Test]
        public void Publish_ReachesEverySubscriber_InSubscriptionOrder()
        {
            var order = new List<int>();

            using var a = _bus.Subscribe<Ping>(_ => order.Add(1));
            using var b = _bus.Subscribe<Ping>(_ => order.Add(2));
            using var c = _bus.Subscribe<Ping>(_ => order.Add(3));

            _bus.Publish(new Ping());

            Assert.That(order, Is.EqualTo(new[] { 1, 2, 3 }));
        }

        [Test]
        public void Publish_WithNoSubscribers_IsSafe()
        {
            Assert.DoesNotThrow(() => _bus.Publish(new Pong()));
        }

        [Test]
        public void Dispose_Unsubscribes()
        {
            var count = 0;
            var subscription = _bus.Subscribe<Ping>(_ => count++);

            _bus.Publish(new Ping());
            subscription.Dispose();
            _bus.Publish(new Ping());

            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public void Dispose_IsIdempotent()
        {
            // Unity calls OnDisable and then OnDestroy. A handle disposed in both must not throw
            // or corrupt the handler list.
            var subscription = _bus.Subscribe<Ping>(_ => { });

            subscription.Dispose();
            Assert.DoesNotThrow(() => subscription.Dispose());
        }

        [Test]
        public void UnsubscribingDuringPublish_DoesNotSkipTheNextHandler()
        {
            // The bug this guards is nasty and realistic: a handler unsubscribes itself while the
            // bus is iterating the list by index. If the bus removes the element immediately, every
            // later handler shifts down a slot and exactly one of them is silently never called.
            // In production that is "the mission tracker sometimes misses a coin", which is
            // maddening to reproduce.
            var calls = new List<string>();
            IDisposable first = null;

            first = _bus.Subscribe<Ping>(_ =>
            {
                calls.Add("first");
                // ReSharper disable once AccessToModifiedClosure
                first.Dispose();
            });

            using var second = _bus.Subscribe<Ping>(_ => calls.Add("second"));
            using var third = _bus.Subscribe<Ping>(_ => calls.Add("third"));

            _bus.Publish(new Ping());

            Assert.That(calls, Is.EqualTo(new[] { "first", "second", "third" }),
                "Removing a handler mid-publish must not cause a later handler to be skipped.");
        }

        [Test]
        public void SubscribingDuringPublish_DoesNotThrow()
        {
            // foreach over a List would throw "collection was modified" here.
            using var _ = _bus.Subscribe<Ping>(__ => _bus.Subscribe<Ping>(___ => { }));

            Assert.DoesNotThrow(() => _bus.Publish(new Ping()));
        }

        [Test]
        public void AThrowingHandler_DoesNotStopTheOthers()
        {
            // If the audio system throws, the wallet must still get its coin.
            var reached = false;

            using var bad = _bus.Subscribe<Ping>(_ => throw new InvalidOperationException("boom"));
            using var good = _bus.Subscribe<Ping>(_ => reached = true);

            Assert.DoesNotThrow(() => _bus.Publish(new Ping()));
            Assert.That(reached, Is.True);
        }

        [Test]
        public void EventsAreRoutedByType()
        {
            var pings = 0;
            var pongs = 0;

            using var a = _bus.Subscribe<Ping>(_ => pings++);
            using var b = _bus.Subscribe<Pong>(_ => pongs++);

            _bus.Publish(new Ping());

            Assert.That(pings, Is.EqualTo(1));
            Assert.That(pongs, Is.Zero);
        }

        [Test]
        public void Publish_CarriesThePayload()
        {
            var seen = 0;
            using var _ = _bus.Subscribe<Ping>(e => seen = e.Value);

            _bus.Publish(new Ping { Value = 42 });

            Assert.That(seen, Is.EqualTo(42));
        }
    }

    [TestFixture]
    public sealed class ContainerTests
    {
        private interface IService
        {
        }

        private sealed class Service : IService, IDisposable
        {
            public bool Disposed;
            public void Dispose() => Disposed = true;
        }

        [Test]
        public void Resolve_ReturnsTheRegisteredInstance()
        {
            using var container = new Container();
            var service = new Service();

            container.RegisterInstance<IService>(service);

            Assert.That(container.Resolve<IService>(), Is.SameAs(service));
        }

        [Test]
        public void Singleton_IsConstructedOnce()
        {
            using var container = new Container();
            var built = 0;

            container.Register<IService>(_ =>
            {
                built++;
                return new Service();
            });

            var a = container.Resolve<IService>();
            var b = container.Resolve<IService>();

            Assert.That(built, Is.EqualTo(1));
            Assert.That(a, Is.SameAs(b));
        }

        [Test]
        public void Transient_IsConstructedEveryTime()
        {
            using var container = new Container();
            container.Register<IService>(_ => new Service(), Lifetime.Transient);

            Assert.That(container.Resolve<IService>(), Is.Not.SameAs(container.Resolve<IService>()));
        }

        [Test]
        public void Resolve_Unregistered_Throws()
        {
            using var container = new Container();
            Assert.Throws<ContainerException>(() => container.Resolve<IService>());
        }

        [Test]
        public void DuplicateRegistration_Throws()
        {
            // Silently replacing a binding leaves half the object graph holding the old instance and
            // half holding the new one — a bug that takes a day to find.
            using var container = new Container();
            container.RegisterInstance<IService>(new Service());

            Assert.Throws<ContainerException>(() => container.RegisterInstance<IService>(new Service()));
        }

        [Test]
        public void Dispose_DisposesSingletonsItCreated()
        {
            var container = new Container();
            container.Register<IService>(_ => new Service());

            var service = (Service)container.Resolve<IService>();
            container.Dispose();

            Assert.That(service.Disposed, Is.True);
        }

        [Test]
        public void Dispose_DoesNotDisposeInstancesItDidNotCreate()
        {
            // The container does not own what the caller handed it. Disposing a caller-supplied
            // instance is how you get a double-dispose when the same object is shared between two
            // containers.
            var container = new Container();
            var service = new Service();
            container.RegisterInstance<IService>(service);

            container.Dispose();

            Assert.That(service.Disposed, Is.False);
        }

        [Test]
        public void CircularDependency_ThrowsInsteadOfStackOverflowing()
        {
            using var container = new Container();
            container.Register<IService>(c => c.Resolve<IService>());

            Assert.Throws<ContainerException>(() => container.Resolve<IService>());
        }
    }

    [TestFixture]
    public sealed class GameStateMachineTests
    {
        [Test]
        public void StartsInBoot()
        {
            Assert.That(new GameStateMachine().Current, Is.EqualTo(GameState.Boot));
        }

        [Test]
        public void LegalTransition_Succeeds()
        {
            var machine = new GameStateMachine();
            machine.TransitionTo(GameState.MainMenu);
            machine.TransitionTo(GameState.Playing);

            Assert.That(machine.Current, Is.EqualTo(GameState.Playing));
        }

        [Test]
        public void IllegalTransition_Throws()
        {
            var machine = new GameStateMachine();

            // Boot -> Playing skips the menu, and with it every piece of setup the menu is
            // responsible for.
            Assert.Throws<InvalidOperationException>(() => machine.TransitionTo(GameState.Playing));
        }

        [Test]
        public void PlayingCannotJumpStraightToMainMenu()
        {
            // Leaving a run must go through GameOver, which is where the run is scored, coins are
            // banked and missions are credited. A path that bypassed it would drop the player's
            // coins on the floor.
            var machine = new GameStateMachine();
            machine.TransitionTo(GameState.MainMenu);
            machine.TransitionTo(GameState.Playing);

            Assert.Throws<InvalidOperationException>(() => machine.TransitionTo(GameState.MainMenu));
        }

        [Test]
        public void GameOverCanReturnToPlaying_ForTheRevivePath()
        {
            var machine = new GameStateMachine();
            machine.TransitionTo(GameState.MainMenu);
            machine.TransitionTo(GameState.Playing);
            machine.TransitionTo(GameState.GameOver);

            Assert.DoesNotThrow(() => machine.TransitionTo(GameState.Playing),
                "Revive (rewarded ad / gem spend) resumes the same run from GameOver.");
        }

        [Test]
        public void TransitionToSelf_IsIgnored()
        {
            // Two colliders reporting the same death in one frame must not throw.
            var machine = new GameStateMachine();
            machine.TransitionTo(GameState.MainMenu);

            Assert.DoesNotThrow(() => machine.TransitionTo(GameState.MainMenu));
            Assert.That(machine.Current, Is.EqualTo(GameState.MainMenu));
        }

        [Test]
        public void Changed_ReportsPreviousAndCurrent()
        {
            var machine = new GameStateMachine();
            GameState from = default, to = default;

            machine.Changed += (previous, current) =>
            {
                from = previous;
                to = current;
            };

            machine.TransitionTo(GameState.MainMenu);

            Assert.That(from, Is.EqualTo(GameState.Boot));
            Assert.That(to, Is.EqualTo(GameState.MainMenu));
        }
    }

    [TestFixture]
    public sealed class LaneTests
    {
        [Test]
        public void StepsClampAtTheEdges()
        {
            // Clamping rather than wrapping is a design decision: a swipe that teleports the player
            // from the left lane to the right lane reads as a bug and kills them on an obstacle they
            // never saw.
            Assert.That(Lane.Left.StepLeft(), Is.EqualTo(Lane.Left));
            Assert.That(Lane.Right.StepRight(), Is.EqualTo(Lane.Right));
        }

        [Test]
        public void StepsMoveOneLane()
        {
            Assert.That(Lane.Centre.StepLeft(), Is.EqualTo(Lane.Left));
            Assert.That(Lane.Centre.StepRight(), Is.EqualTo(Lane.Right));
            Assert.That(Lane.Left.StepRight(), Is.EqualTo(Lane.Centre));
            Assert.That(Lane.Right.StepLeft(), Is.EqualTo(Lane.Centre));
        }

        [Test]
        public void OffsetIsSymmetricAboutTheCentre()
        {
            const float width = 2.6f;

            Assert.That(Lane.Centre.OffsetFor(width), Is.Zero);
            Assert.That(Lane.Left.OffsetFor(width), Is.EqualTo(-width));
            Assert.That(Lane.Right.OffsetFor(width), Is.EqualTo(width));
        }

        [Test]
        public void CanStep_IsFalseAtTheEdge()
        {
            Assert.That(Lane.Left.CanStep(-1), Is.False);
            Assert.That(Lane.Left.CanStep(1), Is.True);
            Assert.That(Lane.Right.CanStep(1), Is.False);
            Assert.That(Lane.Centre.CanStep(-1), Is.True);
        }
    }

    [TestFixture]
    public sealed class RunTuningTests
    {
        [Test]
        public void Defaults_AreValid()
        {
            Assert.DoesNotThrow(() => new RunTuning().Validate());
        }

        [Test]
        public void MaxSpeedBelowBaseSpeed_IsRejected()
        {
            var tuning = new RunTuning { BaseSpeed = 20f, MaxSpeed = 10f };

            Assert.Throws<ArgumentException>(() => tuning.Validate(),
                "A max speed below the base speed would make the game slow down as it progresses.");
        }

        [Test]
        public void ZeroLaneWidth_IsRejected()
        {
            Assert.Throws<ArgumentException>(() => new RunTuning { LaneWidth = 0f }.Validate());
        }
    }
}
