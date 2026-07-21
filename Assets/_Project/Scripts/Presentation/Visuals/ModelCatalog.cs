using UnityEngine;

namespace NeonRush.Presentation.Visuals
{
    /// <summary>
    /// The one place authored art is wired to the game.
    ///
    /// Every prefab here is optional. A field left empty falls back to the greybox for that object
    /// and nothing else changes, which is what lets the art land piecemeal — obstacles this week,
    /// characters next — without the game ever being in a broken state.
    ///
    /// <b>Why a ScriptableObject in Resources rather than scene references.</b> This project builds
    /// its entire scene from code (see <c>GameBootstrap</c>); there is no hand-authored hierarchy to
    /// drag references into, so a scene reference has nowhere to live. Resources.Load is the
    /// simplest thing that survives that constraint. It is also the natural seam to replace with
    /// Addressables later: only <see cref="CatalogMeshProvider"/> would change.
    ///
    /// Create with: <c>Neon Rush/Setup/Create Model Catalog</c>.
    /// </summary>
    [CreateAssetMenu(menuName = "Neon Rush/Model Catalog", fileName = "ModelCatalog")]
    public sealed class ModelCatalog : ScriptableObject
    {
        /// <summary>The path under a Resources folder where the bootstrap looks for this asset.</summary>
        public const string ResourcePath = "NeonRush/ModelCatalog";

        [Header("Characters")]
        [Tooltip("CHR_Runner_Default. Origin at the feet; must stand 1.6 m tall.")]
        public GameObject PlayerDefault;

        [Header("Obstacles — must match ObstacleArchetype dimensions exactly")]
        [Tooltip("OBS_LowJump_Barrier. 1.80 x 0.70 x 1.20 m, origin at the box centre.")]
        public GameObject ObstacleLowJump;

        [Tooltip("OBS_HighSlide_Gate. 1.80 x 1.00 x 1.20 m, origin at the box centre.")]
        public GameObject ObstacleHighSlide;

        [Tooltip("OBS_FullBlock_Wall. 1.80 x 1.60 x 1.20 m, origin at the box centre.")]
        public GameObject ObstacleFullBlock;

        [Header("Pickups")]
        [Tooltip("PU_Coin. 0.70 m across, disc facing the camera.")]
        public GameObject Coin;

        [Tooltip("PU_Magnet. Inside a 0.70 m cube.")]
        public GameObject PowerUpMagnet;

        [Tooltip("PU_Shield. Inside a 0.70 m cube.")]
        public GameObject PowerUpShield;

        [Tooltip("PU_DoubleScore. Inside a 0.70 m cube.")]
        public GameObject PowerUpDoubleScore;

        [Header("Track")]
        [Tooltip("TRK_RoadSlab_30m. Authored at exactly one ChunkLength; do not scale.")]
        public GameObject RoadSlab;

        [Tooltip("TRK_LaneMarker_30m. Authored at exactly one ChunkLength.")]
        public GameObject LaneMarker;

        [Header("Environment")]
        [Tooltip("ENV_Building_Low / Mid / Tall. Origin at the base. Varied by picking and rotating, never by scaling.")]
        public GameObject[] Buildings;

        /// <summary>
        /// True when at least one prefab is assigned. The bootstrap uses this to decide whether the
        /// catalog is worth using at all: an empty catalog would defer every single object back to
        /// the greybox, which is exactly what the primitive provider already does more directly.
        /// </summary>
        public bool HasAnyArt =>
            PlayerDefault != null ||
            ObstacleLowJump != null || ObstacleHighSlide != null || ObstacleFullBlock != null ||
            Coin != null ||
            PowerUpMagnet != null || PowerUpShield != null || PowerUpDoubleScore != null ||
            RoadSlab != null || LaneMarker != null ||
            (Buildings != null && Buildings.Length > 0);
    }
}
