using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The eyelids of the Humanoid skeleton, as its section eyelids in art/Skeletons/Humanoid.json
    // names them: per eye its eye bone and two lid bones, children of Head at the eye's centre and
    // outside the Humanoid avatar, which no clip keys, and the turns about each lid bone's own X axis
    // in Blender that close the lids, which the rig weights each body's lids to meet at. EyelidTests
    // holds these constants to the skeleton. See docs/ART_PIPELINE.md, Skeletons.
    public static class EyelidRig
    {
        public const float UpperClosedDegrees = -46f;
        public const float LowerClosedDegrees = 11f;

        // The X axis of a lid bone in Blender is the bone's local -X in Unity, since the export
        // turns Blender's right-handed axes into Unity's left-handed ones. A turn by the closed
        // degrees about it closes the lids; EyelidTests measures them meet on every body model.
        public static readonly Vector3 TurnAxis = Vector3.left;

        // Each eye with its lid bones, left first, as the skeleton lists them.
        public static IReadOnlyList<Eye> Eyes { get; } = new[]
        {
            new Eye("Eye_L", "EyelidUpper_L", "EyelidLower_L"),
            new Eye("Eye_R", "EyelidUpper_R", "EyelidLower_R"),
        };

        // The turn of a lid bone from its rest that closes it by a share, from 0, open, to 1, closed.
        public static Quaternion Turn(float closedDegrees, float share)
        {
            return Quaternion.AngleAxis(closedDegrees * share, TurnAxis);
        }

        public readonly struct Eye
        {
            public Eye(string bone, string upper, string lower)
            {
                Bone = bone;
                Upper = upper;
                Lower = lower;
            }

            // The eye's own bone, which the lids turn around.
            public string Bone { get; }

            public string Upper { get; }

            public string Lower { get; }
        }
    }
}
