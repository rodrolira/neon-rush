using System;
using System.Collections.Generic;
using UnityEngine;

namespace NeonRush.Presentation.Pooling
{
    /// <summary>
    /// A pre-warmed pool of GameObjects.
    ///
    /// The rule this enforces: <b>nothing is instantiated or destroyed during a run.</b>
    /// <c>Instantiate</c> allocates and touches the scene graph; <c>Destroy</c> queues managed
    /// objects for collection. Doing either while the player is running produces a GC spike, and a
    /// GC spike on a budget Android device is a dropped frame at exactly the moment the player is
    /// reacting to an obstacle. They will read that dropped frame as an unfair death.
    ///
    /// Growth is therefore deliberate and visible: the pool grows when asked to, and it reports
    /// when it was forced to grow mid-run so that the pre-warm count can be corrected. A pool that
    /// silently grows on demand is just <c>Instantiate</c> with extra steps.
    /// </summary>
    public sealed class GameObjectPool : IDisposable
    {
        private readonly Func<GameObject> _factory;
        private readonly Stack<GameObject> _idle;
        private readonly Transform _parent;

        private int _live;
        private bool _disposed;

        /// <param name="factory">Creates one new instance. Called only during pre-warm and growth.</param>
        /// <param name="parent">Inactive instances are parented here to keep the hierarchy tidy.</param>
        /// <param name="prewarm">How many instances to build up front, before the first run.</param>
        public GameObjectPool(Func<GameObject> factory, Transform parent, int prewarm)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _parent = parent;
            _idle = new Stack<GameObject>(Mathf.Max(prewarm, 4));

            for (var i = 0; i < prewarm; i++)
            {
                var instance = Create();
                instance.SetActive(false);
                _idle.Push(instance);
            }
        }

        /// <summary>Total instances this pool has ever created (idle + rented).</summary>
        public int Capacity { get; private set; }

        /// <summary>Instances currently rented out.</summary>
        public int Live => _live;

        /// <summary>
        /// True when the pool ran dry and had to allocate mid-run. Surfaced by the bootstrap in
        /// development builds: it means the pre-warm count is too low and the player just paid for
        /// it in frame time.
        /// </summary>
        public bool GrewUnderLoad { get; private set; }

        /// <summary>Rents an instance, activating it. Grows the pool if empty.</summary>
        public GameObject Rent()
        {
            ThrowIfDisposed();

            GameObject instance;

            if (_idle.Count > 0)
            {
                instance = _idle.Pop();
            }
            else
            {
                instance = Create();
                GrewUnderLoad = true;
            }

            _live++;
            instance.SetActive(true);
            return instance;
        }

        /// <summary>Returns an instance to the pool and deactivates it.</summary>
        public void Return(GameObject instance)
        {
            if (instance == null) return;
            if (_disposed)
            {
                // The pool is gone (scene teardown); the object is about to be destroyed with the
                // scene anyway. Returning it here would resurrect a dead Stack.
                return;
            }

            instance.SetActive(false);

            // Re-parenting on return keeps rented objects from being orphaned under a chunk that
            // is itself about to be recycled, which would take the child down with it.
            if (_parent != null)
            {
                instance.transform.SetParent(_parent, worldPositionStays: false);
            }

            _idle.Push(instance);
            _live--;
        }

        private GameObject Create()
        {
            var instance = _factory();

            if (instance == null)
            {
                throw new InvalidOperationException("Pool factory returned null.");
            }

            if (_parent != null)
            {
                instance.transform.SetParent(_parent, worldPositionStays: false);
            }

            Capacity++;
            return instance;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var instance in _idle)
            {
                if (instance != null)
                {
                    UnityEngine.Object.Destroy(instance);
                }
            }

            _idle.Clear();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(GameObjectPool));
        }
    }
}
