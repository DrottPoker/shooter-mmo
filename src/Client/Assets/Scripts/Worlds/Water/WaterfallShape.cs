using UnityEngine;

namespace Mmorpg.Client.Worlds
{
    // A waterfall: the water of a river falls over its last point, the lip, as wide and as deep as
    // the river is there, along the path water takes at the waterfall's speed, into the water body
    // below, which churns and foams where it lands and has a pool dug into the ground there.
    // White water boils up there, mist rises, and droplets fly. The falling water only shows; it
    // has no collision and no water body.
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    public sealed class WaterfallShape : WorldBuilderShape
    {
        [Tooltip("The river whose last point is the lip.")]
        [SerializeField] private RiverShape river;

        [Tooltip("The water body the waterfall falls into.")]
        [SerializeField] private WaterBody landing;

        [Tooltip("How fast the water leaves the lip (m/s).")]
        [SerializeField, Min(0.1f)] private float speed = 1.6f;

        [Tooltip("How far up the river the falling water fades in over its surface (m).")]
        [SerializeField, Min(0f)] private float approach = 0.6f;

        [Tooltip("How far past the lip the river's surface reaches, under the falling water's top (m).")]
        [SerializeField, Min(0f)] private float pastLip = 0.05f;

        [Tooltip("How far inside the falling water's sides the river's surface keeps past the lip (m).")]
        [SerializeField, Min(0f)] private float insideFall = 0.02f;

        [Tooltip("How much wider the water grows on each side as it falls (m).")]
        [SerializeField, Min(0f)] private float spreadWidth = 0.35f;

        [Tooltip("Half the thickness of the falling water at its foot (m).")]
        [SerializeField, Min(0.01f)] private float footHalfThickness = 0.45f;

        [Tooltip("How much the river foams where it tips over the lip, from 0 to 1.")]
        [SerializeField, Range(0f, 1f)] private float lipFoam = 0.8f;

        [Tooltip("How far up the river the foam at the lip reaches (m).")]
        [SerializeField, Min(0f)] private float lipFoamLength = 1.5f;

        [Tooltip("How far from where the water lands the water below churns fully (m).")]
        [SerializeField, Min(0f)] private float churnRadius = 1f;

        [Tooltip("Over how much water the churn fades out beyond that (m).")]
        [SerializeField, Min(0.1f)] private float churnFade = 3f;

        [Tooltip("How fast the water below spreads out from where the water lands (m/s).")]
        [SerializeField, Min(0f)] private float spreadSpeed = 0.7f;

        [Tooltip("How far from where the water lands it digs a pool into a World Builder terrain below (m).")]
        [SerializeField, Min(0f)] private float poolRadius = 3f;

        [Tooltip("How deep the pool lies below the surface of the water where the water lands (m).")]
        [SerializeField, Min(0f)] private float poolDepth = 1f;

        public RiverShape River
        {
            get { return river; }
        }

        public WaterBody Landing
        {
            get { return landing; }
        }

        // The speed, the foot's thickness, and the churn's fade are at least as large as the
        // Inspector allows, also when a script sets them.
        public float Speed
        {
            get { return Mathf.Max(0.1f, speed); }
        }

        public float Approach
        {
            get { return approach; }
        }

        public float PastLip
        {
            get { return pastLip; }
        }

        public float InsideFall
        {
            get { return insideFall; }
        }

        public float SpreadWidth
        {
            get { return spreadWidth; }
        }

        public float FootHalfThickness
        {
            get { return Mathf.Max(0.01f, footHalfThickness); }
        }

        public float LipFoam
        {
            get { return lipFoam; }
        }

        public float LipFoamLength
        {
            get { return lipFoamLength; }
        }

        public float ChurnRadius
        {
            get { return churnRadius; }
        }

        public float ChurnFade
        {
            get { return Mathf.Max(0.1f, churnFade); }
        }

        public float SpreadSpeed
        {
            get { return spreadSpeed; }
        }

        public float PoolRadius
        {
            get { return Mathf.Max(0f, poolRadius); }
        }

        public float PoolDepth
        {
            get { return Mathf.Max(0f, poolDepth); }
        }
    }
}
