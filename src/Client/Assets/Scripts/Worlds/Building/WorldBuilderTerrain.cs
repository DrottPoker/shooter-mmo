using UnityEngine;

namespace Mmorpg.Client.Worlds
{
    // A terrain that the World Builder shapes: the water above it carves its beds and banks into
    // it and paints them with the bed layer, and what was there before comes back when the water
    // moves away. In the game it does nothing; the terrain keeps what the World Builder made.
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Terrain))]
    public sealed class WorldBuilderTerrain : WorldBuilderShape
    {
        [Tooltip("The terrain layer that the beds and banks of the water are painted with, such as sand; none paints nothing.")]
        [SerializeField] private TerrainLayer bedLayer;

        public TerrainLayer BedLayer
        {
            get { return bedLayer; }
        }
    }
}
