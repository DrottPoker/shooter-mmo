using UnityEngine;

namespace Mmorpg.Client.Worlds
{
    // A shape that makes the surface of a water body: the World Builder writes the mesh of the
    // body's MeshFilter from the shape and the water around it, so where two bodies meet their
    // surfaces agree. It also carves its bed and bank into a World Builder terrain below it.
    [RequireComponent(typeof(WaterBody))]
    [RequireComponent(typeof(MeshRenderer))]
    public abstract class WaterSurfaceShape : WorldBuilderShape
    {
        [Tooltip("How far the surface reaches past the shore, under the bank that hides it (m).")]
        [SerializeField, Min(0f)] private float bankReach = 1.5f;

        [Tooltip("Whether the water carves its bed and bank into a World Builder terrain below it.")]
        [SerializeField] private bool carvesBed = true;

        public float BankReach
        {
            get { return bankReach; }
        }

        public bool CarvesBed
        {
            get { return carvesBed; }
        }

        // Over how far past the shore, or past the edge of a river's water, the bank rises to the
        // ground around the water (m).
        public abstract float BankWidth { get; }

        public WaterBody Body
        {
            get { return GetComponent<WaterBody>(); }
        }
    }
}
