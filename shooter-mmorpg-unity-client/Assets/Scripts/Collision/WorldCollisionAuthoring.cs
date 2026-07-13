using ShooterMmo.GameSimulation;
using UnityEngine;

namespace ShooterMmo.Collision
{
    [DisallowMultipleComponent]
    public sealed class WorldCollisionAuthoring : MonoBehaviour
    {
        [SerializeField] private string worldId = "local-world-1";
        [SerializeField, Min(1f)] private float chunkSize = 32f;
        [SerializeField] private Transform collisionRoot;
        [SerializeField] private uint layerMask =
            CollisionLayers.CharacterMovement | CollisionLayers.CombatQueries;

        public string WorldId
        {
            get { return worldId; }
        }

        public float ChunkSize
        {
            get { return chunkSize; }
        }

        public Transform CollisionRoot
        {
            get { return collisionRoot != null ? collisionRoot : transform; }
        }

        public uint LayerMask
        {
            get { return layerMask; }
        }

        public bool TryValidate(out string error)
        {
            if (string.IsNullOrWhiteSpace(worldId))
            {
                error = "World id is required.";
                return false;
            }

            if (float.IsNaN(chunkSize) || float.IsInfinity(chunkSize) || chunkSize <= 0f)
            {
                error = "Chunk size must be a positive finite number.";
                return false;
            }

            if (layerMask == 0)
            {
                error = "At least one collision layer is required.";
                return false;
            }

            error = string.Empty;
            return true;
        }
    }
}
