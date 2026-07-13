using System;

namespace NeonRush.Core.DI
{
    /// <summary>
    /// Resolution surface of the dependency-injection container.
    /// Consumers receive this, never the concrete <see cref="Container"/>, so that
    /// gameplay code cannot register new bindings at runtime. Composition happens once,
    /// at boot, in the composition root.
    /// </summary>
    public interface IContainer
    {
        /// <summary>Resolves a binding. Throws if <typeparamref name="T"/> is not registered.</summary>
        T Resolve<T>();

        /// <summary>Resolves a binding, or returns false when it is not registered.</summary>
        bool TryResolve<T>(out T service);

        /// <summary>True when a binding for <typeparamref name="T"/> exists.</summary>
        bool IsRegistered<T>();
    }

    /// <summary>How long a resolved instance lives.</summary>
    public enum Lifetime
    {
        /// <summary>Created once, on first resolve, and reused for the process lifetime.</summary>
        Singleton,

        /// <summary>Created fresh on every resolve. The container does not track or dispose these.</summary>
        Transient,
    }

    /// <summary>Thrown when the object graph cannot be satisfied. Always a programmer error.</summary>
    public sealed class ContainerException : Exception
    {
        public ContainerException(string message) : base(message) { }
    }
}
