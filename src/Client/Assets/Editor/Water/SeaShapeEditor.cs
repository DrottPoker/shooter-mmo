using Mmorpg.Client.Worlds;
using UnityEditor;

namespace Mmorpg.Client.Editor
{
    // Shapes a sea's outline inside the World with handles in the Scene view.
    [CustomEditor(typeof(SeaShape))]
    internal sealed class SeaShapeEditor : OutlineShapeEditor
    {
        protected override string OutlineProperty
        {
            get { return "outline"; }
        }

        protected override string Hint
        {
            get
            {
                return "The sea lies at the height of its object. On its Horizon sides it runs on beyond the World's "
                    + "ground, so draw its outline out to that edge there.";
            }
        }
    }
}
