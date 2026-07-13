using System;
using System.Collections.Generic;

namespace NeonRush.Core.Events
{
    /// <summary>
    /// Default <see cref="IEventBus"/>.
    ///
    /// Performance contract: <see cref="Publish{T}"/> allocates zero bytes in the steady state.
    /// This is not premature optimisation — coin pickups, distance ticks and lane changes publish
    /// tens of times per second during a run, and on a low-end Android device a per-event
    /// allocation is a guaranteed GC spike, which is a guaranteed frame drop, in the one moment
    /// the player is paying attention.
    ///
    /// Three design points make that possible:
    ///  · Events are constrained to <c>struct</c> and passed by <c>in</c>, so publishing never boxes.
    ///  · Handlers live in a strongly-typed <see cref="HandlerList{T}"/>, so dispatch never casts
    ///    the event or allocates an enumerator.
    ///  · Unsubscribing during a publish tombstones the slot rather than compacting the list,
    ///    so we never copy the handler array defensively.
    /// </summary>
    public sealed class EventBus : IEventBus, IDisposable
    {
        // Keyed by event type. The value is a HandlerList<T> for that exact T; the cast in
        // Publish/Subscribe is therefore always safe.
        private readonly Dictionary<Type, IHandlerList> _handlers = new();
        private bool _disposed;

        /// <summary>Non-generic handle so the dictionary can hold every HandlerList{T}.</summary>
        private interface IHandlerList
        {
            void Clear();
        }

        private sealed class HandlerList<T> : IHandlerList where T : struct
        {
            // A null entry is a tombstone: a handler that unsubscribed while a publish was in
            // flight. It is compacted away once the last nested publish finishes.
            internal readonly List<Action<T>> Handlers = new();

            /// <summary>Publish depth. Non-zero means the list is being iterated and must not be structurally modified.</summary>
            internal int PublishDepth;

            /// <summary>True when at least one tombstone is waiting to be compacted.</summary>
            internal bool HasTombstones;

            public void Clear()
            {
                Handlers.Clear();
                HasTombstones = false;
            }
        }

        public IDisposable Subscribe<T>(Action<T> handler) where T : struct
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            ThrowIfDisposed();

            var list = GetOrCreate<T>();
            list.Handlers.Add(handler);

            return new Subscription<T>(this, handler);
        }

        public void Publish<T>(in T evt) where T : struct
        {
            ThrowIfDisposed();

            if (!_handlers.TryGetValue(typeof(T), out var untyped))
            {
                return; // Nobody is listening. Publishing into the void is legal and free.
            }

            var list = (HandlerList<T>)untyped;

            list.PublishDepth++;
            try
            {
                // Index-based, and re-reading Count each iteration: a handler is allowed to
                // subscribe a new handler mid-publish, and the newcomer should receive this event.
                // foreach would throw "collection was modified" here.
                for (var i = 0; i < list.Handlers.Count; i++)
                {
                    var handler = list.Handlers[i];
                    if (handler == null) continue; // tombstoned mid-publish

                    try
                    {
                        handler(evt);
                    }
                    catch (Exception e)
                    {
                        // One bad subscriber must not stop the others. If the audio system throws,
                        // the wallet still gets its coin. Swallowing here is correct; swallowing
                        // silently is not, so it is reported.
                        System.Diagnostics.Debug.WriteLine(
                            $"[EventBus] Handler for '{typeof(T).Name}' threw and was skipped: {e}");
                    }
                }
            }
            finally
            {
                list.PublishDepth--;

                if (list.PublishDepth == 0 && list.HasTombstones)
                {
                    Compact(list);
                }
            }
        }

        private void Unsubscribe<T>(Action<T> handler) where T : struct
        {
            if (_disposed) return; // Disposing the bus already dropped every handler.

            if (!_handlers.TryGetValue(typeof(T), out var untyped)) return;

            var list = (HandlerList<T>)untyped;
            var index = list.Handlers.IndexOf(handler);
            if (index < 0) return;

            if (list.PublishDepth > 0)
            {
                // Removing an element while Publish is walking the list by index would shift
                // every subsequent handler down one slot and silently skip one of them.
                // Tombstone now, compact when the last publish unwinds.
                list.Handlers[index] = null;
                list.HasTombstones = true;
            }
            else
            {
                list.Handlers.RemoveAt(index);
            }
        }

        private static void Compact<T>(HandlerList<T> list) where T : struct
        {
            for (var i = list.Handlers.Count - 1; i >= 0; i--)
            {
                if (list.Handlers[i] == null)
                {
                    list.Handlers.RemoveAt(i);
                }
            }

            list.HasTombstones = false;
        }

        private HandlerList<T> GetOrCreate<T>() where T : struct
        {
            if (_handlers.TryGetValue(typeof(T), out var existing))
            {
                return (HandlerList<T>)existing;
            }

            var created = new HandlerList<T>();
            _handlers[typeof(T)] = created;
            return created;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var list in _handlers.Values)
            {
                list.Clear();
            }

            _handlers.Clear();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(EventBus));
        }

        /// <summary>
        /// Handle returned by Subscribe. Disposing is idempotent, because Unity will happily
        /// call OnDestroy on an object whose OnDisable already ran.
        /// </summary>
        private sealed class Subscription<T> : IDisposable where T : struct
        {
            private EventBus _bus;
            private Action<T> _handler;

            internal Subscription(EventBus bus, Action<T> handler)
            {
                _bus = bus;
                _handler = handler;
            }

            public void Dispose()
            {
                if (_bus == null) return;

                _bus.Unsubscribe(_handler);
                _bus = null;
                _handler = null;
            }
        }
    }
}
