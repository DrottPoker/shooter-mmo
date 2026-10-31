using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The limbs and string of a bow a body holds, on the bones of the Bow skeleton that the body holds
    // below the bow's socket. While the body draws, the drawing hand takes the string when it comes to
    // where the string rests, the nock follows the hand, the limbs bend by how far the string comes
    // back toward the bow's draw length, as its clip Draw bends them from brace to full draw, and each
    // straight half of the string runs from its tip to the nock. When the bow looses or the body stops
    // drawing, the string springs back to brace. Clips are keyed on one body and retargeted to every
    // other, so the drawing arm reaches for the string where it rests and then draws it along the
    // arrow's line, from the bow straight back; it lets go of that line as it looses. See
    // docs/EQUIPMENT_VISUALS_DESIGN.md.
    public sealed class BowRig
    {
        public const string NockBone = "Bow_Nock";
        public const string DrawClip = "Draw";

        // The drawing hand takes the string within this distance of where it rests.
        public const float TakeDistance = 0.08f;

        // The drawing arm reaches for the resting string fully within the near distance of it and
        // not at all beyond the far one, and comes onto or off the arrow's line over this time.
        public const float ReachNear = 0.03f;
        public const float ReachFar = 0.25f;
        public const float LineSeconds = 0.1f;

        // The working limbs and the tips, which Draw bends, and the halves of the string.
        public static readonly string[] LimbBones = { "Bow_LimbUpper", "Bow_TipUpper", "Bow_LimbLower", "Bow_TipLower" };
        public static readonly string[] StringBones = { "Bow_StringUpper", "Bow_StringLower" };

        private readonly Transform socket;
        private readonly Transform nock;
        private readonly Vector3 nockRest;
        private readonly Transform[] limbs;
        private readonly Quaternion[] limbRest;
        private readonly Quaternion[] limbFull;
        private readonly Transform[] strings;
        private readonly Quaternion[] stringRest;
        private readonly Vector3[] stringScale;
        // In each half's own space, toward the resting nock, the axis it stretches along, or -1 when
        // no axis runs along it, and its length in the socket's space.
        private readonly Vector3[] stringAlong;
        private readonly int[] stringAxis;
        private readonly float[] stringLength;
        // In the socket's space: where the arrow passes the bow, the way back from there to the resting
        // nock, and how far the nock rests and comes at full draw behind the arrow pass.
        private readonly Vector3 arrowPass;
        private readonly Vector3 back;
        private readonly Vector3 restingNock;
        private readonly float brace;
        private readonly float drawLength;
        private bool posed;
        private bool loosed;
        // How firmly the drawing arm keeps to the string or the arrow's line, from 0 to 1.
        private float pin;

        private BowRig(Transform socket, Transform nock, Transform[] limbs, Transform[] strings, AnimationClip draw,
            Transform bones, Vector3 arrowPass, float drawLength)
        {
            this.socket = socket;
            this.nock = nock;
            this.limbs = limbs;
            this.strings = strings;
            this.arrowPass = arrowPass;
            this.drawLength = drawLength;
            nockRest = nock.localPosition;
            restingNock = socket.InverseTransformPoint(nock.position);
            var toNock = restingNock - arrowPass;
            brace = toNock.magnitude;
            back = brace > 0f ? toNock / brace : Vector3.back;

            limbRest = new Quaternion[limbs.Length];
            limbFull = new Quaternion[limbs.Length];
            for (var index = 0; index < limbs.Length; index++)
            {
                limbRest[index] = limbs[index].localRotation;
                limbFull[index] = limbRest[index];
            }

            stringRest = new Quaternion[strings.Length];
            stringScale = new Vector3[strings.Length];
            stringAlong = new Vector3[strings.Length];
            stringAxis = new int[strings.Length];
            stringLength = new float[strings.Length];
            for (var index = 0; index < strings.Length; index++)
            {
                var half = strings[index];
                stringRest[index] = half.localRotation;
                stringScale[index] = half.localScale;
                var toRestingNock = nock.position - half.position;
                stringAlong[index] = half.InverseTransformDirection(toRestingNock).normalized;
                stringLength[index] = socket.InverseTransformVector(toRestingNock).magnitude;
                stringAxis[index] = AxisAlong(stringAlong[index]);
            }

            if (draw != null)
            {
                FullDraw(draw, bones);
            }
        }

        // Whether the drawing hand holds the string.
        public bool IsHeld { get; private set; }

        // Whether the bow loosed the shot it draws, until the body stops drawing or starts a new shot.
        public bool HasLoosed
        {
            get { return loosed; }
        }

        // How far the bow is drawn, from 0 at brace to 1 at its draw length.
        public float Draw { get; private set; }

        // The rig of a bow whose bones the body holds below its socket, or null when they are not
        // those of the Bow skeleton or the bow names no launch point.
        public static BowRig TryCreate(Transform bones, Transform socket, BodyAppearanceCatalog.Attachment bow)
        {
            if (bones == null || socket == null || bow == null || !bow.HasLaunchPoint)
            {
                return null;
            }

            var nock = PlayerBodyMeshes.FindDescendant(bones, NockBone);
            var limbs = Find(bones, LimbBones);
            var strings = Find(bones, StringBones);
            if (nock == null || limbs == null || strings == null)
            {
                return null;
            }

            return new BowRig(socket, nock, limbs, strings, bow.ClipNamed(DrawClip), bones, bow.LaunchPoint, bow.DrawLength);
        }

        // A new shot: the drawing hand may take the string again.
        public void StartShot()
        {
            loosed = false;
        }

        // Every frame after the Animator posed the body: drawing says whether the body plays the bow's
        // attack clip, arm is the arm that draws, and deltaTime the seconds since the last frame, or
        // 0 to bring the arm onto or off the line at once.
        public void Update(bool drawing, DrawingArm arm, float deltaTime)
        {
            if (!drawing)
            {
                loosed = false;
                // A bow that rests costs a body that carries it nothing more.
                if (pin <= 0f && !posed)
                {
                    IsHeld = false;
                    return;
                }
            }

            if (arm == null)
            {
                Rest();
                return;
            }

            var hand = arm.Socket.position;
            var resting = socket.TransformPoint(restingNock);
            var takes = drawing && !loosed;
            var wanted = !takes ? 0f
                : IsHeld ? 1f
                : 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(ReachNear, ReachFar, (hand - resting).magnitude));
            pin = deltaTime > 0f ? Mathf.MoveTowards(pin, wanted, deltaTime / LineSeconds) : wanted;
            if (pin > 0f)
            {
                OffHandToolGrip.Place(arm.Shoulder, arm.UpperArm, arm.LowerArm, arm.Hand, arm.Socket,
                    takes && !IsHeld ? resting : OnLine(hand), arm.Hand.rotation, pin);
            }

            if (!takes)
            {
                Rest();
                return;
            }

            hand = arm.Socket.position;
            if (!IsHeld && (hand - resting).sqrMagnitude > TakeDistance * TakeDistance)
            {
                return;
            }

            IsHeld = true;
            Pose(hand, socket.InverseTransformPoint(hand));
        }

        // The point of the arrow's line, from the resting nock back to full draw, as far back as a
        // point is from the bow.
        private Vector3 OnLine(Vector3 point)
        {
            var pull = Mathf.Clamp(Vector3.Dot(socket.InverseTransformPoint(point) - arrowPass, back), brace, Mathf.Max(brace, drawLength));
            return socket.TransformPoint(arrowPass + (back * pull));
        }

        // The hand lets go of the string, which springs back to brace until the next shot.
        public void Loose()
        {
            if (IsHeld)
            {
                loosed = true;
                Rest();
            }
        }

        // Where the nock of the drawn string is and the way from it through the arrow pass, which an
        // arrow on the string points along; false while no hand holds the string.
        public bool TryGetNock(out Vector3 position, out Vector3 forward)
        {
            position = nock.position;
            forward = (socket.TransformPoint(arrowPass) - position).normalized;
            return IsHeld && forward.sqrMagnitude > 0f;
        }

        private void Pose(Vector3 handPosition, Vector3 held)
        {
            var pull = Vector3.Dot(held - arrowPass, back);
            Draw = drawLength > brace ? Mathf.Clamp01((pull - brace) / (drawLength - brace)) : 0f;
            for (var index = 0; index < limbs.Length; index++)
            {
                limbs[index].localRotation = Quaternion.Slerp(limbRest[index], limbFull[index], Draw);
            }

            nock.position = handPosition;
            for (var index = 0; index < strings.Length; index++)
            {
                Aim(index, handPosition);
            }

            posed = true;
        }

        // A half of the string turns from its tip to the nock and stretches to reach it.
        private void Aim(int index, Vector3 target)
        {
            var half = strings[index];
            half.localRotation = stringRest[index];
            half.localScale = stringScale[index];
            var toTarget = target - half.position;
            var along = half.TransformDirection(stringAlong[index]);
            half.rotation = Quaternion.FromToRotation(along, toTarget) * half.rotation;
            if (stringAxis[index] >= 0 && stringLength[index] > 0f)
            {
                var scale = stringScale[index];
                scale[stringAxis[index]] *= socket.InverseTransformVector(toTarget).magnitude / stringLength[index];
                half.localScale = scale;
            }
        }

        private void Rest()
        {
            IsHeld = false;
            if (!posed)
            {
                return;
            }

            for (var index = 0; index < limbs.Length; index++)
            {
                limbs[index].localRotation = limbRest[index];
            }

            nock.localPosition = nockRest;
            for (var index = 0; index < strings.Length; index++)
            {
                strings[index].localRotation = stringRest[index];
                strings[index].localScale = stringScale[index];
            }

            Draw = 0f;
            posed = false;
        }

        // The limbs' rotations at the end of Draw, sampled on the bones, which then rest again.
        private void FullDraw(AnimationClip draw, Transform bones)
        {
            var all = bones.GetComponentsInChildren<Transform>(true);
            var rest = new List<(Vector3 Position, Quaternion Rotation, Vector3 Scale)>(all.Length);
            foreach (var bone in all)
            {
                rest.Add((bone.localPosition, bone.localRotation, bone.localScale));
            }

            draw.SampleAnimation(bones.gameObject, draw.length);
            for (var index = 0; index < limbs.Length; index++)
            {
                limbFull[index] = limbs[index].localRotation;
            }

            for (var index = 0; index < all.Length; index++)
            {
                all[index].localPosition = rest[index].Position;
                all[index].localRotation = rest[index].Rotation;
                all[index].localScale = rest[index].Scale;
            }
        }

        private static Transform[] Find(Transform bones, string[] names)
        {
            var found = new Transform[names.Length];
            for (var index = 0; index < names.Length; index++)
            {
                found[index] = PlayerBodyMeshes.FindDescendant(bones, names[index]);
                if (found[index] == null)
                {
                    return null;
                }
            }

            return found;
        }

        // The local axis a direction runs along, or -1 when it runs along none.
        private static int AxisAlong(Vector3 direction)
        {
            for (var axis = 0; axis < 3; axis++)
            {
                if (Mathf.Abs(direction[axis]) > 0.999f)
                {
                    return axis;
                }
            }

            return -1;
        }

        // The arm that draws the string: its collar bone, if the body has one, its upper and lower
        // arm, its hand, and the hand's socket, whose origin holds the string.
        public sealed class DrawingArm
        {
            public Transform Shoulder;
            public Transform UpperArm;
            public Transform LowerArm;
            public Transform Hand;
            public Transform Socket;

            // The right arm of a humanoid body with its main hand's socket, or null.
            public static DrawingArm Find(Animator animator, Transform root)
            {
                if (animator == null || !animator.isHuman)
                {
                    return null;
                }

                var arm = new DrawingArm
                {
                    Shoulder = animator.GetBoneTransform(HumanBodyBones.RightShoulder),
                    UpperArm = animator.GetBoneTransform(HumanBodyBones.RightUpperArm),
                    LowerArm = animator.GetBoneTransform(HumanBodyBones.RightLowerArm),
                    Hand = animator.GetBoneTransform(HumanBodyBones.RightHand),
                    Socket = PlayerBodyMeshes.FindDescendant(root, HumanLocomotion.MainHandSocketBone),
                };
                return arm.UpperArm != null && arm.LowerArm != null && arm.Hand != null && arm.Socket != null ? arm : null;
            }
        }
    }
}
