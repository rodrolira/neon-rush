using System;
using System.Collections.Generic;

namespace NeonRush.Core.DI
{
    /// <summary>
    /// A small, explicit, reflection-free DI container.
    ///
    /// Bindings are registered as factory delegates rather than discovered by reflecting over
    /// constructors. That is a deliberate trade: it costs a line of wiring per service, and it
    /// buys three things that matter on mobile.
    ///
    ///  1. IL2CPP/AOT safety by construction. Reflection-based containers can fail at runtime
    ///     on iOS when the AOT compiler has stripped a constructor nothing statically referenced.
    ///     A factory delegate is a static reference, so the code is always there.
    ///  2. No reflection cost at boot. Reflection-heavy containers spend real milliseconds
    ///     scanning types on a low-end Android device, on the critical path to first frame.
    ///  3. Errors surface at the composition root, in one file, instead of as a runtime
    ///     resolution failure somewhere deep in a scene.
    ///
    /// Not thread-safe. Composition happens on the main thread at boot; resolution afterwards
    /// is read-mostly. Guarding every resolve with a lock would tax the hot path to protect
    /// against a scenario the design does not permit.
    /// </summary>
    public sealed class Container : IContainer, IDisposable
    {
        private readonly Dictionary<Type, Binding> _bindings = new();

        /// <summary>Singletons we constructed and therefore own. Disposed in reverse creation order.</summary>
        private readonly List<IDisposable> _ownedDisposables = new();

        /// <summary>Guards against A → B → A cycles, which would otherwise stack-overflow.</summary>
        private readonly HashSet<Type> _resolving = new();

        private bool _disposed;

        private sealed class Binding
        {
            public Func<IContainer, object> Factory;
            public Lifetime Lifetime;
            public object Instance;
            public bool HasInstance;
        }

        // -------------------------------------------------------------------------------
        // Registration
        // -------------------------------------------------------------------------------

        /// <summary>
        /// Registers an already-constructed instance as a singleton.
        /// The container does NOT take ownership: an instance created by the caller is disposed
        /// by the caller. This prevents double-dispose when the same object is shared between
        /// two containers (e.g. a run-scoped child and the root).
        /// </summary>
        public Container RegisterInstance<T>(T instance)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            ThrowIfDisposed();

            Bind(typeof(T), new Binding
            {
                Lifetime = Lifetime.Singleton,
                Instance = instance,
                HasInstance = true,
                Factory = null,
            });

            return this;
        }

        /// <summary>
        /// Registers a factory. The container owns anything the factory produces, and will
        /// dispose it if it is <see cref="IDisposable"/> and the lifetime is singleton.
        /// </summary>
        public Container Register<T>(Func<IContainer, T> factory, Lifetime lifetime = Lifetime.Singleton)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            ThrowIfDisposed();

            Bind(typeof(T), new Binding
            {
                Factory = c => factory(c),
                Lifetime = lifetime,
                HasInstance = false,
            });

            return this;
        }

        private void Bind(Type type, Binding binding)
        {
            if (_bindings.ContainsKey(type))
            {
                // Silently replacing a binding is how you get a bug that takes a day to find:
                // half the graph holds the old instance, half holds the new one.
                throw new ContainerException(
                    $"'{type.Name}' is already registered. Duplicate registration is almost always a " +
                    "copy-paste error in the composition root. If you intend to replace a binding for a " +
                    "test, build a separate container instead.");
            }

            _bindings[type] = binding;
        }

        // -------------------------------------------------------------------------------
        // Resolution
        // -------------------------------------------------------------------------------

        public T Resolve<T>()
        {
            ThrowIfDisposed();

            if (!_bindings.TryGetValue(typeof(T), out var binding))
            {
                throw new ContainerException(
                    $"No binding registered for '{typeof(T).Name}'. Register it in the composition root " +
                    "(GameBootstrap) before anything resolves it.");
            }

            return (T)Instantiate(typeof(T), binding);
        }

        public bool TryResolve<T>(out T service)
        {
            ThrowIfDisposed();

            if (_bindings.TryGetValue(typeof(T), out var binding))
            {
                service = (T)Instantiate(typeof(T), binding);
                return true;
            }

            service = default;
            return false;
        }

        public bool IsRegistered<T>() => _bindings.ContainsKey(typeof(T));

        private object Instantiate(Type type, Binding binding)
        {
            if (binding.Lifetime == Lifetime.Singleton && binding.HasInstance)
            {
                return binding.Instance;
            }

            if (!_resolving.Add(type))
            {
                throw new ContainerException(
                    $"Circular dependency detected while resolving '{type.Name}'. " +
                    $"Resolution chain: {string.Join(" -> ", _resolving)} -> {type.Name}. " +
                    "Break the cycle by depending on an event or an interface instead of the concrete type.");
            }

            try
            {
                var instance = binding.Factory(this)
                    ?? throw new ContainerException($"The factory for '{type.Name}' returned null.");

                if (binding.Lifetime == Lifetime.Singleton)
                {
                    binding.Instance = instance;
                    binding.HasInstance = true;

                    // Only track what we created. Caller-supplied instances (RegisterInstance)
                    // never reach this branch, so they are never double-disposed.
                    if (instance is IDisposable disposable)
                    {
                        _ownedDisposables.Add(disposable);
                    }
                }

                return instance;
            }
            finally
            {
                _resolving.Remove(type);
            }
        }

        // -------------------------------------------------------------------------------
        // Teardown
        // -------------------------------------------------------------------------------

        /// <summary>
        /// Disposes every singleton the container created, in reverse creation order, so that a
        /// dependent is always torn down before the thing it depends on.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            for (var i = _ownedDisposables.Count - 1; i >= 0; i--)
            {
                try
                {
                    _ownedDisposables[i].Dispose();
                }
                catch (Exception e)
                {
                    // One service throwing on teardown must not strand the rest of the graph
                    // in a half-disposed state (leaked sockets, unflushed analytics, live timers).
                    System.Diagnostics.Debug.WriteLine($"[Container] Error disposing service: {e}");
                }
            }

            _ownedDisposables.Clear();
            _bindings.Clear();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(Container));
            }
        }
    }
}
