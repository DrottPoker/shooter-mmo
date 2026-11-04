using Mmorpg.Client.Worlds;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // A World Builder terrain's bed layer, and a button that gives the terrain back the ground it
    // had before the water shaped it.
    [CustomEditor(typeof(WorldBuilderTerrain))]
    internal sealed class WorldBuilderTerrainEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox(
                "The water above this terrain carves its beds and banks into it and paints them with the bed layer. "
                + "Where water lies, the World Builder shapes the ground anew whenever the water changes, and "
                + "when the water moves away or this component is removed, the ground it had before comes back. "
                + "Sculpt and paint elsewhere as usual.",
                MessageType.None);
            DrawDefaultInspector();
            if (GUILayout.Button("Give The Ground Back"))
            {
                var shaped = (WorldBuilderTerrain)target;
                var terrain = shaped.GetComponent<Terrain>();
                if (terrain.terrainData != null && !TerrainCarver.MayShape(terrain))
                {
                    EditorUtility.DisplayDialog(
                        "Give The Ground Back",
                        "Another terrain uses the terrain data of " + shaped.name + " too, so the World Builder leaves it as it is. "
                        + "Give this terrain terrain data of its own first.",
                        "OK");
                }
                else if (EditorUtility.DisplayDialog(
                        "Give The Ground Back",
                        "Give " + shaped.name + " back the ground it had before the water shaped it, and stop the water "
                        + "from shaping it? Undo lets the water shape it again.",
                        "Give It Back",
                        "Cancel"))
                {
                    TerrainCarver.Restore(terrain);
                    Undo.DestroyObjectImmediate(shaped);
                    GUIUtility.ExitGUI();
                }
            }
        }
    }
}
