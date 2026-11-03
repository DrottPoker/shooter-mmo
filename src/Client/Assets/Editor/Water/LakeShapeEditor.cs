using Mmorpg.Client.Worlds;
using UnityEditor;

namespace Mmorpg.Client.Editor
{
    // Shapes a lake's shore with handles in the Scene view.
    [CustomEditor(typeof(LakeShape))]
    internal sealed class LakeShapeEditor : OutlineShapeEditor
    {
        protected override string OutlineProperty
        {
            get { return "shore"; }
        }

        protected override string Hint
        {
            get { return "The lake lies at the height of its object: move the object up or down to change its level."; }
        }
    }
}
