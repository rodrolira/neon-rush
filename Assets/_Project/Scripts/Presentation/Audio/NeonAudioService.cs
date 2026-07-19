using System;
using System.Collections.Generic;
using NeonRush.Domain.Audio;
using NeonRush.Domain.Ports;
using UnityEngine;

namespace NeonRush.Presentation.Audio
{
    /// <summary>
    /// The Unity end of <see cref="IAudioService"/>. It bakes every <see cref="SoundId"/> into an
    /// <see cref="AudioClip"/> once, at construction, from the procedural samples <see cref="ToneSynth"/>
    /// produces — so the game ships with a full sound palette and <b>zero audio asset files</b>, the
    /// same "everything from code" discipline the greybox visuals follow. No .wav to import, to review
    /// as an opaque binary, or to have silently stripped from a build.
    ///
    /// One-shots play through a small round-robin of 2D AudioSources via PlayOneShot, which mixes
    /// overlapping clips without cutting each other off — a coin during a jump plays both. A separate
    /// looping source carries a soft, seamless ambient pad. Mute is global and instant.
    /// </summary>
    public sealed class NeonAudioService : IAudioService, IDisposable
    {
        private const int SampleRate = 44100;
        private const int VoiceCount = 6;

        private readonly GameObject _root;
        private readonly AudioSource[] _voices = new AudioSource[VoiceCount];
        private readonly Dictionary<SoundId, AudioClip> _clips = new();
        private readonly AudioSource _music;
        private readonly List<AudioClip> _ownedClips = new();

        private int _nextVoice;
        private bool _muted;

        public NeonAudioService(Transform parent)
        {
            _root = new GameObject("Audio");
            _root.transform.SetParent(parent, worldPositionStays: false);

            // Sound needs a listener; the camera in this project is created bare, so guarantee one
            // exists rather than depending on scene authoring that may not be there.
            if (UnityEngine.Object.FindFirstObjectByType<AudioListener>() == null)
            {
                _root.AddComponent<AudioListener>();
            }

            BakeClips();

            for (var i = 0; i < VoiceCount; i++)
            {
                _voices[i] = CreateSource("SfxVoice", loop: false, volume: 1f);
            }

            _music = CreateSource("Music", loop: true, volume: 0.14f);
            _music.clip = BuildAmbientLoop();
            _music.Play();
        }

        /// <summary>Plays a one-shot. No-op while muted, so a silenced game also spends no audio CPU.</summary>
        public void Play(SoundId sound)
        {
            if (_muted) return;
            if (!_clips.TryGetValue(sound, out var clip) || clip == null) return;

            var voice = _voices[_nextVoice];
            _nextVoice = (_nextVoice + 1) % VoiceCount;

            voice.PlayOneShot(clip);
        }

        public void SetMuted(bool muted)
        {
            _muted = muted;

            // Global and immediate: silences the ambient pad and any in-flight one-shots at once. The
            // early-out in Play keeps future SFX from even being scheduled.
            AudioListener.volume = muted ? 0f : 1f;
        }

        public bool IsMuted => _muted;

        private void BakeClips()
        {
            foreach (SoundId id in Enum.GetValues(typeof(SoundId)))
            {
                var samples = ToneSynth.Render(SoundBank.For(id), SampleRate);

                var clip = AudioClip.Create(id.ToString(), samples.Length, 1, SampleRate, false);
                clip.SetData(samples, 0);

                _clips[id] = clip;
                _ownedClips.Add(clip);
            }
        }

        private AudioSource CreateSource(string name, bool loop, float volume)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root.transform, worldPositionStays: false);

            var source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = loop;
            source.volume = volume;
            source.spatialBlend = 0f; // 2D — no positional attenuation for UI/gameplay feedback

            return source;
        }

        /// <summary>
        /// Builds a soft, seamless ambient chord loop. The frequencies and the amplitude LFO are all
        /// chosen to complete a whole number of cycles across the loop length, so the end joins the
        /// start with no click — a generated loop that is even slightly non-integer clicks once per
        /// bar, which is worse than silence.
        /// </summary>
        private AudioClip BuildAmbientLoop()
        {
            const float duration = 4f; // 110·4, 165·4, 220·4 and LFO 0.5·4 are all whole cycles
            var count = (int)(duration * SampleRate);
            var samples = new float[count];

            // A low, open chord (root, fifth, octave) — calm, spacious, non-melodic so it never
            // competes with the gameplay sounds for attention.
            float[] partials = { 110f, 165f, 220f };
            float[] gains = { 0.6f, 0.3f, 0.25f };

            for (var i = 0; i < count; i++)
            {
                var t = i / (float)SampleRate;

                var value = 0f;
                for (var p = 0; p < partials.Length; p++)
                {
                    value += gains[p] * Mathf.Sin(2f * Mathf.PI * partials[p] * t);
                }

                // A slow breath so the pad is not static. 0.5 Hz → two full cycles across the 4 s loop.
                var lfo = 0.75f + 0.25f * Mathf.Sin(2f * Mathf.PI * 0.5f * t);

                samples[i] = Mathf.Clamp(value * lfo * 0.3f, -1f, 1f);
            }

            var clip = AudioClip.Create("Ambient", count, 1, SampleRate, false);
            clip.SetData(samples, 0);
            _ownedClips.Add(clip);

            return clip;
        }

        public void Dispose()
        {
            // Restore global volume: a service torn down while muted would otherwise leave whatever
            // loads next silent.
            AudioListener.volume = 1f;

            foreach (var clip in _ownedClips)
            {
                if (clip != null) UnityEngine.Object.Destroy(clip);
            }

            _ownedClips.Clear();
            _clips.Clear();

            if (_root != null) UnityEngine.Object.Destroy(_root);
        }
    }
}
