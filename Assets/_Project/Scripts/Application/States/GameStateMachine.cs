using System;
using System.Collections.Generic;

namespace NeonRush.Application.States
{
    /// <summary>High-level game states. Exactly one is active at a time.</summary>
    public enum GameState
    {
        Boot = 0,
        MainMenu = 1,
        Playing = 2,
        Paused = 3,
        GameOver = 4,
    }

    /// <summary>
    /// Guards the legal transitions between <see cref="GameState"/>s.
    ///
    /// This exists because implicit state — a handful of booleans like <c>isPaused</c>,
    /// <c>isDead</c>, <c>isInMenu</c> scattered across MonoBehaviours — is where runner games go
    /// to die. It permits nonsense combinations (dead AND paused AND in a menu), and every one of
    /// those combinations is a bug report: the revive offer that appears over the main menu, the
    /// interstitial that plays during gameplay, the input that still moves a corpse.
    ///
    /// An illegal transition throws rather than being silently ignored, because it is always a
    /// programmer error, and a silently-swallowed one becomes a heisenbug three months later.
    /// </summary>
    public sealed class GameStateMachine
    {
        private static readonly Dictionary<GameState, GameState[]> Allowed = new()
        {
            [GameState.Boot] = new[] { GameState.MainMenu },

            // Playing -> MainMenu is absent on purpose. Leaving a run always goes through
            // GameOver, which is where the run is scored, coins are banked, missions are
            // credited and the revive/interstitial decision is made. A path that bypassed it
            // would silently drop the player's coins on the floor.
            [GameState.MainMenu] = new[] { GameState.Playing },

            [GameState.Playing] = new[] { GameState.Paused, GameState.GameOver },

            [GameState.Paused] = new[] { GameState.Playing, GameState.GameOver },

            // GameOver -> Playing is the revive path (rewarded ad or gem spend); it resumes the
            // same run. GameOver -> MainMenu is the ordinary exit.
            [GameState.GameOver] = new[] { GameState.MainMenu, GameState.Playing },
        };

        /// <summary>Raised after a successful transition, with (previous, current).</summary>
        public event Action<GameState, GameState> Changed;

        public GameState Current { get; private set; } = GameState.Boot;

        /// <summary>True when <paramref name="next"/> is a legal transition from the current state.</summary>
        public bool CanTransitionTo(GameState next) =>
            Allowed.TryGetValue(Current, out var targets) && Array.IndexOf(targets, next) >= 0;

        /// <summary>Transitions to <paramref name="next"/>. Throws on an illegal transition.</summary>
        public void TransitionTo(GameState next)
        {
            if (next == Current)
            {
                // A no-op transition is almost always a double-fire (two colliders reporting the
                // same death, a button pressed twice). Ignoring it is correct and safe.
                return;
            }

            if (!CanTransitionTo(next))
            {
                throw new InvalidOperationException(
                    $"Illegal state transition {Current} -> {next}. " +
                    $"Legal targets from {Current}: {string.Join(", ", Allowed[Current])}.");
            }

            var previous = Current;
            Current = next;
            Changed?.Invoke(previous, next);
        }
    }
}
