using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The eyelids of one body: the four lid bones of EyelidRig with the turns they rest in, found
    // once by name when the body is combined. A share from 0, open, the rest, to 1, closed by the
    // skeleton's closed turns, turns all four lids at once. No clip keys the lids, so the Animator
    // leaves them as they were last turned, also after Rebind, and a share holds until the next one.
    // See docs/UNITY_CLIENT_ARCHITECTURE.md, Eyelids.
    public sealed class BodyEyelids
    {
        private readonly Transform[] bones;
        private readonly Quaternion[] open;
        private readonly float[] closedDegrees;

        private BodyEyelids(Transform[] bones, float[] closedDegrees, Renderer renderer)
        {
            this.bones = bones;
            this.closedDegrees = closedDegrees;
            Renderer = renderer;
            open = new Quaternion[bones.Length];
            for (var index = 0; index < bones.Length; index++)
            {
                open[index] = bones[index].localRotation;
            }
        }

        // The lid bones, each eye's upper lid and then its lower lid, in the order of EyelidRig.Eyes.
        public IReadOnlyList<Transform> Bones
        {
            get { return bones; }
        }

        // The renderer that draws the lids, the body's combined renderer, or null.
        public Renderer Renderer { get; }

        // How far the lids are closed, from 0, open, to 1, closed.
        public float Share { get; private set; }

        // Where the eyes are, at the first lid bone, the centre of the left eye.
        public Vector3 Position
        {
            get { return bones[0].position; }
        }

        // The lids below the body's root as they rest now, drawn by the renderer; null for a body
        // without every lid bone of EyelidRig, such as a creature.
        public static BodyEyelids Find(Transform root, Renderer renderer)
        {
            var eyes = EyelidRig.Eyes;
            var bones = new Transform[eyes.Count * 2];
            var degrees = new float[bones.Length];
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
            {
                for (var eye = 0; eye < eyes.Count; eye++)
                {
                    if (child.name == eyes[eye].Upper)
                    {
                        bones[eye * 2] = child;
                        degrees[eye * 2] = EyelidRig.UpperClosedDegrees;
                    }
                    else if (child.name == eyes[eye].Lower)
                    {
                        bones[eye * 2 + 1] = child;
                        degrees[eye * 2 + 1] = EyelidRig.LowerClosedDegrees;
                    }
                }
            }

            foreach (var bone in bones)
            {
                if (bone == null)
                {
                    return null;
                }
            }

            return new BodyEyelids(bones, degrees, renderer);
        }

        // Turns every lid from its rest by the share of its closed turn.
        public void Show(float share)
        {
            Share = Mathf.Clamp01(share);
            for (var index = 0; index < bones.Length; index++)
            {
                bones[index].localRotation = RotationAt(index, Share);
            }
        }

        // The local rotation of a lid bone of Bones at a share.
        public Quaternion RotationAt(int bone, float share)
        {
            return open[bone] * EyelidRig.Turn(closedDegrees[bone], Mathf.Clamp01(share));
        }
    }
}
