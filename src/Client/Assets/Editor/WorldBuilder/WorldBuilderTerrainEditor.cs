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
                + "when the water moves away, the ground it had before comes back. Sculpt and paint elsewhere as usual.",
                MessageType.None);
            DrawDefaultInspector();
            if (GUILayout.Button("Give The Ground Back"))
            {
                var shaped = (WorldBuilderTerrain)target;
                if (EditorUtility.DisplayDialog(
                        "Give The Ground Back",
                        "Give " + shaped.name + " back the ground it had before the water shaped it, and stop the water "
                        + "from shaping it? Undo lets the water shape it again.",
                        "Give It Back",
                        "Cancel"))
                {
                    TerrainCarver.Restore(shaped.GetComponent<Terrain>());
                    Undo.DestroyObjectImmediate(shaped);
                    GUIUtility.ExitGUI();
                }
            }
        }
    }
}
