using Mmorpg.Client.Worlds;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // A waterfall's values, and a button that lets it fall into the highest water below its
    // river's end: a lake, a sea, or another river.
    [CustomEditor(typeof(WaterfallShape))]
    internal sealed class WaterfallShapeEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox(
                "The water falls over the last point of its River into its Landing. Move the river's last point to "
                + "move the lip; the fall follows, as far as the water falls at its speed.",
                MessageType.None);
            DrawDefaultInspector();
            var river = ((WaterfallShape)target).River;
            using (new EditorGUI.DisabledScope(river == null))
            {
                if (GUILayout.Button("Fall Into The Water Below"))
                {
                    serializedObject.Update();
                    serializedObject.FindProperty("landing").objectReferenceValue = WorldBuilderCreate.WaterBelow(river);
                    serializedObject.ApplyModifiedProperties();
                }
            }
        }
    }
}
