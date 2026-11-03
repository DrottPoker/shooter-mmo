using UnityEngine;

namespace Mmorpg.Client.Worlds
{
    // A shape that makes the surface of a water body: the World Builder writes the mesh of the
    // body's MeshFilter from the shape and the water around it, so where two bodies meet their
    // surfaces agree.
    [RequireComponent(typeof(WaterBody))]
    [RequireComponent(typeof(MeshRenderer))]
    public abstract class WaterSurfaceShape : WorldBuilderShape
    {
        [Tooltip("How far the surface reaches past the shore, under the bank that hides it (m).")]
        [SerializeField, Min(0f)] private float bankReach = 1.5f;

        public float BankReach
        {
            get { return bankReach; }
        }

        public WaterBody Body
        {
            get { return GetComponent<WaterBody>(); }
        }
    }
}
