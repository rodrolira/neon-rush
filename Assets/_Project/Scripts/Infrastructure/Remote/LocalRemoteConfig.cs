using System;
using System.Collections.Generic;
using NeonRush.Domain.Ports;

namespace NeonRush.Infrastructure.Remote
{
    /// <summary>
    /// A remote-config source that is entirely local.
    ///
    /// A real, shipping adapter — not a stub. It is what runs before Firebase is integrated, what
    /// runs in CI (no network), and what runs on any launch where the fetch has not completed or the
    /// player is offline. In every one of those cases it returns the compiled-in defaults, which are
    /// a complete and balanced game. That is the whole point of the defaults-first design: the
    /// absence of a backend is not a degraded experience, it is the baseline experience.
    ///
    /// It is also seedable, which makes it the test double: hand it a value for a key and assert that
    /// the game reconfigures itself correctly, all without a network or the Firebase SDK.
    /// </summary>
    public sealed class LocalRemoteConfig : IRemoteConfigService
    {
        private readonly Dictionary<string, string> _values;

        public LocalRemoteConfig(IReadOnlyDictionary<string, string> seed = null)
        {
            _values = seed != null
                ? new Dictionary<string, string>((IDictionary<string, string>)seed)
                : new Dictionary<string, string>();
        }

        /// <summary>Always true: local values are available immediately, with no fetch to wait for.</summary>
        public bool IsReady => true;

        /// <summary>Seeds or overrides a value at runtime. Used by tests and by QA tooling.</summary>
        public void Set(string key, string value) => _values[key] = value;

        public void Fetch(Action<bool> onComplete)
        {
            // Nothing to fetch. Report success synchronously — there is a complete config already.
            onComplete?.Invoke(true);
        }

        public int GetInt(string key, int defaultValue) =>
            _values.TryGetValue(key, out var raw) && int.TryParse(raw, out var value) ? value : defaultValue;

        public float GetFloat(string key, float defaultValue) =>
            _values.TryGetValue(key, out var raw)
            && float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value
                : defaultValue;

        public bool GetBool(string key, bool defaultValue) =>
            _values.TryGetValue(key, out var raw) && bool.TryParse(raw, out var value) ? value : defaultValue;

        public string GetString(string key, string defaultValue) =>
            _values.TryGetValue(key, out var raw) ? raw : defaultValue;
    }
}
