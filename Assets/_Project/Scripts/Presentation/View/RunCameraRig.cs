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

        /// <summary>Offset from the player, in metres: behind and above, looking forward.</summary>
        private static readonly Vector3 Offset = new(0f, 4.2f, -7.5f);

        /// <summary>
        /// How much of the player's lateral position the camera adopts. Below 1 the camera trails,
        /// which is what keeps the neighbouring lanes on screen.
        /// </summary>
        private const float LateralFollow = 0.55f;

        /// <summary>Approximate seconds for the camera to close the remaining distance. Higher = snappier.</summary>
        private const float FollowSharpness = 9f;

        public RunCameraRig(Transform camera, Transform target)
        {
            _camera = camera;
            _target = target;

            _camera.position = Offset;
            _camera.rotation = Quaternion.Euler(12f, 0f, 0f);
        }

        /// <summary>Snaps the camera to its resting pose. Called at the start of a run so it does not glide in from wherever the last run ended.</summary>
        public void Reset()
        {
            _camera.position = Offset;
        }

        public void Tick(float deltaTime)
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

            _camera.position = position;
        }
    }
}
