using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mmorpg.Client.Cloth;
using Mmorpg.Client.Gameplay;
using Mmorpg.WorldData.Editor;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Mmorpg.Client.Editor
{
    // MMORPG > Tools > Art Pipeline > Write Cloth Fixtures: both bodies in a robe, a cloak, and
    // both, in frames of their clips, in leg poses set by hand, and in seeded random leg poses,
    // each captured with what ClothKinematics lays from it, written to
    // tools/art/blender/tests/fixtures/cloth_kinematics.json one case per line. The art pipeline's
    // Python tests lay every case with cloth_core.py and the Unity tests with ClothKinematics, and
    // both compare with what was laid; the Unity tests also capture the cases again, so a change to
    // the solver, the bodies, or the garments shows as a change of the fixtures. See
    // docs/ART_PIPELINE.md.
    public static class ClothKinematicFixtures
    {
        public const string MenuPath = "MMORPG/Tools/Art Pipeline/Write Cloth Fixtures";
        public const string RepositoryPath = "tools/art/blender/tests/fixtures/cloth_kinematics.json";

        private const string MaleBodyPath = "Assets/Prefabs/Player/HumanMaleBody.prefab";
        private const string FemaleBodyPath = "Assets/Prefabs/Player/HumanFemaleBody.prefab";
        private const string GarmentFolder = "Assets/Art/Models/Garments/";
        private const int RandomSeed = 20261025;

        private static readonly int SpeedId = Animator.StringToHash(HumanLocomotion.SpeedParameter);
        private static readonly int MovingId = Animator.StringToHash(HumanLocomotion.MovingParameter);
        private static readonly int RateId = Animator.StringToHash(HumanLocomotion.RateParameter);
        private static readonly int DeadId = Animator.StringToHash(HumanLocomotion.DeadParameter);

        // The cases: a body, what it wears, and its poses.
        private static readonly (string Body, string Label, string[] Wears, bool Hood, string[] Poses)[] Outfits =
        {
            ("Male", "robe_cloak_hood", new[] { "Robe", "Cloak" }, true, new[]
            {
                "rest", "idle_0.5", "walk_0", "walk_0.25", "walk_0.5", "walk_0.75", "jog_0", "jog_0.25", "jog_0.5", "jog_0.75",
                "death_0.5", "death_1", "sit", "kick_back", "high_knee", "stride", "random_0", "random_1", "random_2", "random_3",
            }),
            ("Male", "robe", new[] { "Robe" }, false, new[] { "rest", "jog_0.25", "sit", "random_4" }),
            ("Male", "cloak", new[] { "Cloak" }, false, new[] { "rest", "jog_0.5", "kick_back", "random_5" }),
            ("Female", "robe_cloak", new[] { "Robe", "Cloak" }, false, new[]
            {
                "rest", "walk_0.5", "jog_0.25", "jog_0.75", "death_1", "sit", "kick_back", "random_6", "random_7",
            }),
            ("Female", "robe", new[] { "Robe" }, false, new[] { "jog_0.5" }),
            ("Female", "cloak", new[] { "Cloak" }, false, new[] { "jog_0.5" }),
        };

        public static string FilePath => Path.Combine(RepositoryLayout.FindRoot(), RepositoryPath);

        [MenuItem(MenuPath)]
        public static void Write()
        {
            var fixture = Capture();
            File.WriteAllText(FilePath, ToJson(fixture), new UTF8Encoding(false));
            Debug.Log("Wrote " + fixture.cases.Count + " cloth kinematic cases to " + RepositoryPath + ".");
        }

        public static ClothKinematicFixture Read()
        {
            return JsonUtility.FromJson<ClothKinematicFixture>(File.ReadAllText(FilePath));
        }

        // The fixture as JSON with one case per line, so a change shows as the cases it changed.
        public static string ToJson(ClothKinematicFixture fixture)
        {
            var text = new StringBuilder();
            text.Append("{\"version\":").Append(fixture.version).Append(",\"cases\":[\n");
            for (var index = 0; index < fixture.cases.Count; index++)
            {
                text.Append(JsonUtility.ToJson(fixture.cases[index], false));
                text.Append(index + 1 < fixture.cases.Count ? ",\n" : "\n");
            }

            return text.Append("]}\n").ToString();
        }

        public static ClothKinematicFixture Capture()
        {
            var fixture = new ClothKinematicFixture();
            var random = new System.Random(RandomSeed);
            var randomPoses = new Dictionary<string, (Vector3 Thigh, float Knee)[]>();
            for (var index = 0; index < 8; index++)
            {
                randomPoses["random_" + index] = new[] { RandomLeg(random), RandomLeg(random) };
            }

            foreach (var (bodyName, label, wears, hood, poses) in Outfits)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(bodyName == "Male" ? MaleBodyPath : FemaleBodyPath);
                if (prefab == null)
                {
                    throw new InvalidOperationException("The " + bodyName.ToLowerInvariant() + " body prefab is missing.");
                }

                var body = Object.Instantiate(prefab);
                body.hideFlags = HideFlags.HideAndDontSave;
                try
                {
                    var solver = body.GetComponent<ClothChainSolver>();
                    foreach (var garment in wears)
                    {
                        var path = GarmentFolder + garment + "_" + bodyName + ".fbx";
                        var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        if (model == null)
                        {
                            throw new InvalidOperationException(path + " is missing.");
                        }

                        var kind = garment == "Robe" ? ClothChainKind.Skirt : ClothChainKind.Cloak;
                        solver.Activate(kind, ClothProfile.For(kind), model);
                    }

                    if (hood)
                    {
                        solver.Activate(ClothChainKind.Hood, ClothProfile.Hood);
                    }

                    var rest = Pose.Of(body.transform);
                    var animator = body.GetComponent<Animator>();
                    foreach (var pose in poses)
                    {
                        rest.Apply();
                        Place(animator, pose, randomPoses, body.transform);
                        solver.Simulate(1f / 60f, ClothDetail.Pose);
                        fixture.cases.Add(solver.CaptureKinematics(bodyName.ToLowerInvariant() + "/" + label + "/" + pose));
                    }
                }
                finally
                {
                    Object.DestroyImmediate(body);
                }
            }

            return fixture;
        }

        // Poses the body: at rest, a clip's frame by its share of the clip, or legs set by hand.
        private static void Place(Animator animator, string pose, Dictionary<string, (Vector3 Thigh, float Knee)[]> randomPoses,
            Transform body)
        {
            var bones = body.GetComponentsInChildren<Transform>(true).GroupBy(bone => bone.name).ToDictionary(group => group.Key, group => group.First());
            if (pose == "rest")
            {
                return;
            }

            var separator = pose.LastIndexOf('_');
            var clip = separator > 0 ? pose.Substring(0, separator) : pose;
            if (clip == "idle" || clip == "walk" || clip == "jog" || clip == "death")
            {
                var share = float.Parse(pose.Substring(separator + 1), System.Globalization.CultureInfo.InvariantCulture);
                Sample(animator, clip, share);
                return;
            }

            var forward = body.forward;
            if (randomPoses.TryGetValue(pose, out var legs))
            {
                Leg(bones, "_L", legs[0].Thigh, legs[0].Knee, body);
                Leg(bones, "_R", legs[1].Thigh, legs[1].Knee, body);
                return;
            }

            switch (pose)
            {
                case "sit":
                    // Both thighs nearly level forward, the shins hanging down from the knees.
                    foreach (var side in new[] { "_L", "_R" })
                    {
                        Turn(bones["UpperLeg" + side], bones["LowerLeg" + side], along => Vector3.Slerp(along, forward, 0.85f));
                        Turn(bones["LowerLeg" + side], bones["Foot" + side], _ => Vector3.down);
                    }

                    break;
                case "kick_back":
                    // The right thigh swings back and the shin folds up behind it, as in a jog's back kick.
                    Turn(bones["UpperLeg_R"], bones["LowerLeg_R"], along => Vector3.Slerp(along, -forward, 0.35f));
                    Turn(bones["LowerLeg_R"], bones["Foot_R"], _ => Vector3.Slerp(-forward, Vector3.down, 0.3f));
                    break;
                case "high_knee":
                    // The left knee raised to the hips, the shin hanging from it.
                    Turn(bones["UpperLeg_L"], bones["LowerLeg_L"], _ => forward);
                    Turn(bones["LowerLeg_L"], bones["Foot_L"], _ => Vector3.Slerp(Vector3.down, forward, 0.2f));
                    break;
                case "stride":
                    // A long step: the left thigh forward, the right back.
                    Turn(bones["UpperLeg_L"], bones["LowerLeg_L"], along => Vector3.Slerp(along, forward, 0.4f));
                    Turn(bones["UpperLeg_R"], bones["LowerLeg_R"], along => Vector3.Slerp(along, -forward, 0.35f));
                    Turn(bones["LowerLeg_R"], bones["Foot_R"], along => Vector3.Slerp(along, -forward, 0.3f));
                    break;
                default:
                    throw new ArgumentException("Unknown pose " + pose + ".");
            }
        }

        // A clip's frame, by its share of the clip, as the locomotion controller plays it.
        private static void Sample(Animator animator, string clip, float share)
        {
            animator.enabled = true;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.Rebind();
            var speed = clip == "walk" ? HumanLocomotion.WalkSpeed : clip == "jog" ? HumanLocomotion.JogSpeed : 0f;
            animator.SetBool(MovingId, speed > HumanLocomotion.MovingSpeed);
            animator.SetFloat(SpeedId, speed);
            animator.SetFloat(RateId, 1f);
            animator.SetBool(DeadId, clip == "death");
            var state = clip == "death" ? HumanLocomotion.DeathState : clip == "idle" ? HumanLocomotion.IdleState : HumanLocomotion.LocomotionState;
            animator.Play(state, 0, Mathf.Min(share, 0.999f));
            animator.Update(0f);
            animator.enabled = false;
        }

        // A leg from a random thigh direction in the body's space and a knee bend in degrees.
        private static void Leg(Dictionary<string, Transform> bones, string side, Vector3 thigh, float knee, Transform body)
        {
            var thighBone = bones["UpperLeg" + side];
            var kneeBone = bones["LowerLeg" + side];
            var direction = body.TransformDirection(thigh).normalized;
            Turn(thighBone, kneeBone, _ => direction);
            var backward = -Vector3.ProjectOnPlane(body.forward, direction).normalized;
            Turn(kneeBone, bones["Foot" + side], _ => Vector3.SlerpUnclamped(direction, backward, knee / 90f));
        }

        private static (Vector3 Thigh, float Knee) RandomLeg(System.Random random)
        {
            // From hanging down, up to level in front and well back, and a little to the side.
            var pitch = Mathf.Lerp(-45f, 90f, (float)random.NextDouble());
            var yaw = Mathf.Lerp(-25f, 25f, (float)random.NextDouble());
            var thigh = Quaternion.Euler(-pitch, yaw, 0f) * Vector3.down;
            return (thigh, Mathf.Lerp(0f, 110f, (float)random.NextDouble()));
        }

        // Turns a bone so the way from it to its child points where the function says.
        private static void Turn(Transform bone, Transform child, Func<Vector3, Vector3> toward)
        {
            var along = (child.position - bone.position).normalized;
            bone.rotation = Quaternion.FromToRotation(along, toward(along).normalized) * bone.rotation;
        }

        // Every transform's local pose below a body, to put it back.
        private sealed class Pose
        {
            private readonly List<(Transform Transform, Vector3 Position, Quaternion Rotation, Vector3 Scale)> transforms =
                new List<(Transform, Vector3, Quaternion, Vector3)>();

            public static Pose Of(Transform root)
            {
                var pose = new Pose();
                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                {
                    pose.transforms.Add((transform, transform.localPosition, transform.localRotation, transform.localScale));
                }

                return pose;
            }

            public void Apply()
            {
                foreach (var (transform, position, rotation, scale) in transforms)
                {
                    transform.localPosition = position;
                    transform.localRotation = rotation;
                    transform.localScale = scale;
                }
            }
        }
    }
}
