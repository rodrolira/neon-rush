using System.Collections.Generic;
using NeonRush.Domain.Ports;

namespace NeonRush.Infrastructure.Analytics
{
    /// <summary>
    /// An analytics sink that keeps everything in memory.
    ///
    /// Three real jobs, none of them "placeholder":
    ///
    ///  · <b>In tests</b>, it is what lets a test assert "the purchase failure reported its
    ///    shortfall" — the recorded list IS the assertion surface.
    ///  · <b>In the Editor</b>, it logs each event so a designer watching the console can see the
    ///    funnel fire in real time while playing.
    ///  · <b>Before Firebase is integrated</b>, it proves the entire instrumentation pipeline —
    ///    so that when the SDK adapter lands, day-one data flows through a taxonomy that has
    ///    already been exercised for weeks.
    ///
    /// The buffer is capped: an unbounded event list in a soak test (or a long Editor session)
    /// is a slow memory leak wearing a lab coat.
    /// </summary>
    public sealed class RecordingAnalytics : IAnalyticsService
    {
        private const int MaxBuffered = 500;

        private readonly bool _logToConsole;
        private readonly List<RecordedEvent> _events = new();
        private readonly Dictionary<string, string> _userProperties = new();

        public RecordingAnalytics(bool logToConsole = false)
        {
            _logToConsole = logToConsole;
        }

        /// <summary>Everything tracked so far, oldest first (rolling window of the last 500).</summary>
        public IReadOnlyList<RecordedEvent> Events => _events;

        public IReadOnlyDictionary<string, string> UserProperties => _userProperties;

        public void Track(string eventName, IReadOnlyDictionary<string, object> parameters = null)
        {
            if (string.IsNullOrEmpty(eventName)) return;

            if (_events.Count >= MaxBuffered)
            {
                _events.RemoveAt(0);
            }

            // Copy the parameters: the caller may reuse or mutate its dictionary after Track returns,
            // and a recorded event that changes after the fact makes test failures unreproducible.
            var copy = parameters != null ? CopyOf(parameters) : new Dictionary<string, object>();

            _events.Add(new RecordedEvent(eventName, copy));

            if (_logToConsole)
            {
                UnityEngine.Debug.Log($"[Analytics] {eventName} {Format(copy)}");
            }
        }

        public void SetUserProperty(string name, string value)
        {
            if (string.IsNullOrEmpty(name)) return;

            _userProperties[name] = value;
        }

        /// <summary>Latest event with this name, or null. The assertion helper tests lean on.</summary>
        public RecordedEvent Find(string eventName)
        {
            for (var i = _events.Count - 1; i >= 0; i--)
            {
                if (_events[i].Name == eventName) return _events[i];
            }

            return null;
        }

        /// <summary>How many times an event fired.</summary>
        public int CountOf(string eventName)
        {
            var count = 0;
            foreach (var e in _events)
            {
                if (e.Name == eventName) count++;
            }

            return count;
        }

        private static Dictionary<string, object> CopyOf(IReadOnlyDictionary<string, object> source)
        {
            var copy = new Dictionary<string, object>(source.Count);
            foreach (var pair in source) copy[pair.Key] = pair.Value;
            return copy;
        }

        private static string Format(Dictionary<string, object> parameters)
        {
            if (parameters.Count == 0) return string.Empty;

            var parts = new List<string>(parameters.Count);
            foreach (var pair in parameters) parts.Add($"{pair.Key}={pair.Value}");
            return "{ " + string.Join(", ", parts) + " }";
        }

        public sealed class RecordedEvent
        {
            public RecordedEvent(string name, IReadOnlyDictionary<string, object> parameters)
            {
                Name = name;
                Parameters = parameters;
            }

            public string Name { get; }
            public IReadOnlyDictionary<string, object> Parameters { get; }

            /// <summary>Typed parameter access for assertions.</summary>
            public T Param<T>(string key) => Parameters.TryGetValue(key, out var value) && value is T typed ? typed : default;
        }
    }
}
