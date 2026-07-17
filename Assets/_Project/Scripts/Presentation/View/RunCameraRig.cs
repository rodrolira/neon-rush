using UnityEngine;

namespace NeonRush.Presentation.View
{
    /// <summary>
    /// The chase camera.
    ///
    /// It follows the player's lane changes, but <b>lazily</b> — it lags behind by design, at a
    /// fraction of the player's lateral speed. Two reasons, and the second is the one that actually
    /// matters:
    ///
    ///  · A camera locked rigidly to the player makes the world appear to jerk sideways on every
    ///    swipe, which is a well-known trigger for motion sickness in runners.
    ///  · A trailing camera keeps the lanes the player is *not* in visible at the edges of the
    ///    frame. In a game where every decision is "which lane do I move to", hiding the
    ///    alternatives is hiding the gameplay.
    ///
    /// The camera never follows the jump. Pitching up and down with the player is nauseating and it
    /// costs the player their view of the upcoming track precisely when they are airborne and most
    /// need it.
    /// </summary>
    public sealed class RunCameraRig
    {
        private readonly Transform _camera;
        private readonly Transform _target;
        private readonly Camera _lens;

        /// <summary>Offset from the player, in metres: behind and above, looking forward.</summary>
        private static readonly Vector3 Offset = new(0f, 4.2f, -7.5f);

        /// <summary>
        /// How much of the player's lateral position the camera adopts. Below 1 the camera trails,
        /// which is what keeps the neighbouring lanes on screen.
        /// </summary>
        private const float LateralFollow = 0.55f;

        /// <summary>Approximate seconds for the camera to close the remaining distance. Higher = snappier.</summary>
        private const float FollowSharpness = 9f;

        // --- Speed FOV ---------------------------------------------------------------------

        /// <summary>Field of view at base speed.</summary>
        private const float BaseFov = 62f;

        /// <summary>
        /// Extra field of view at the speed cap.
        ///
        /// This is doing real work, not decoration. Forward speed in a chase camera is almost
        /// invisible: the player runs in place and the world scrolls, so 26 m/s looks very much
        /// like 9 m/s. Widening the lens pushes more of the world into the frame and pulls the
        /// edges outward as you accelerate, and *that* is the cue the eye reads as speed. Without
        /// it, the difficulty ramp is something the player feels only as "I keep dying" rather than
        /// "I am going terrifyingly fast", and those two experiences retain very differently.
        /// </summary>
        private const float FovKick = 18f;

        /// <summary>How quickly the lens responds to speed changes. Slow, so it breathes rather than snaps.</summary>
        private const float FovSharpness = 2.5f;

        /// <summary>Smoothed speed-driven FOV, kept separate from the punch below.</summary>
        private float _fov;

        /// <summary>
        /// A transient FOV bulge, added straight onto the lens and shed over a fraction of a second.
        /// Held apart from <see cref="_fov"/> on purpose: the speed-FOV smoothing is deliberately
        /// slow (it breathes), and routing a punch through it would swallow the punch entirely.
        /// </summary>
        private float _fovPunch;

        /// <summary>Degrees of punch shed per second. ~0.4s to clear a typical surge.</summary>
        private const float FovPunchDecay = 16f;

        // --- Death shake -------------------------------------------------------------------

        private float _shakeRemaining;
        private float _shakeMagnitude;
        private float _shakeDuration = 0.4f;

        /// <summary>Deterministic shake: no Random, so a replayed run shakes identically.</summary>
        private float _shakeTime;

        public RunCameraRig(Transform camera, Transform target, Camera lens)
        {
            _camera = camera;
            _target = target;
            _lens = lens;

            _camera.position = Offset;
            _camera.rotation = Quaternion.Euler(12f, 0f, 0f);

            _fov = BaseFov;
            if (_lens != null) _lens.fieldOfView = BaseFov;
        }

        /// <summary>Snaps the camera to its resting pose. Called at the start of a run so it does not glide in from wherever the last run ended.</summary>
        public void Reset()
        {
            _camera.position = Offset;
            _shakeRemaining = 0f;
            _shakeTime = 0f;

            _fov = BaseFov;
            _fovPunch = 0f;

            if (_lens != null) _lens.fieldOfView = BaseFov;
        }

        /// <summary>
        /// Kicks off a short camera shake. Called on death.
        ///
        /// A crash with no physical feedback reads as the game simply stopping. The shake is the
        /// punctuation that tells the player *they* hit something, which is the difference between
        /// "I made a mistake" and "the game ended for no reason".
        /// </summary>
        public void Shake(float duration = 0.4f, float magnitude = 0.5f)
        {
            _shakeRemaining = duration;
            _shakeDuration = duration;
            _shakeMagnitude = magnitude;
        }

        /// <summary>
        /// A quick outward FOV bulge, added on top of the speed-driven FOV and shed over ~0.4s.
        /// Fired on each distance milestone to punctuate the "changing gear" of the difficulty ramp
        /// — the moment the run gets faster should be something the player sees, not only survives.
        /// </summary>
        public void PunchFov(float degrees = 6f)
        {
            _fovPunch = Mathf.Max(_fovPunch, degrees);
        }

        /// <param name="normalisedSpeed">0 at base speed, 1 at the speed cap.</param>
        public void Tick(float deltaTime, float normalisedSpeed)
        {
            var desiredX = _target.localPosition.x * LateralFollow;

            var position = _camera.position;

            // Exponential smoothing that is framerate-independent. The naive
            // `Lerp(current, target, k * dt)` is NOT: it converges faster at high frame rates, so
            // the camera would feel different on a 120 Hz phone than on a 30 fps one. This form
            // gives identical motion at any frame rate.
            var t = 1f - Mathf.Exp(-FollowSharpness * deltaTime);

            position.x = Mathf.Lerp(position.x, desiredX, t);
            position.y = Offset.y;
            position.z = Offset.z;

            position += TickShake(deltaTime);

            _camera.position = position;

            TickFov(deltaTime, normalisedSpeed);
        }

        private void TickFov(float deltaTime, float normalisedSpeed)
        {
            if (_lens == null) return;

            var target = BaseFov + FovKick * Mathf.Clamp01(normalisedSpeed);
            var t = 1f - Mathf.Exp(-FovSharpness * deltaTime);
            _fov = Mathf.Lerp(_fov, target, t);

            _fovPunch = Mathf.Max(0f, _fovPunch - FovPunchDecay * deltaTime);

            _lens.fieldOfView = _fov + _fovPunch;
        }

        private Vector3 TickShake(float deltaTime)
        {
            if (_shakeRemaining <= 0f) return Vector3.zero;

            _shakeRemaining -= deltaTime;
            _shakeTime += deltaTime;

            if (_shakeRemaining <= 0f)
            {
                _shakeRemaining = 0f;
                return Vector3.zero;
            }

            // Decays to nothing rather than stopping dead — a shake that cuts off abruptly looks
            // like a bug. Two different frequencies per axis so it never reads as a clean sine.
            var decay = _shakeRemaining / _shakeDuration;
            var amplitude = _shakeMagnitude * Mathf.Clamp01(decay);

            return new Vector3(
                Mathf.Sin(_shakeTime * 47f) * amplitude,
                Mathf.Sin(_shakeTime * 61f) * amplitude * 0.6f,
                0f);
        }
    }
}
