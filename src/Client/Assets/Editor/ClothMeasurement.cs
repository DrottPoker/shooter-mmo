using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Mmorpg.Client.Gameplay;
using Mmorpg.WorldData.Editor;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // MMORPG > Tools > Art Pipeline > Measure Cloth: both bodies in a robe, a cloak, and both, in
    // every cloth stress motion, measured by ClothMotionMeter, and how far the cloth's course
    // depends on the frame time: a zig-zag stepped at 1/30, 1/60, and 1/144 s and in jittery frames,
    // against the same at 1/240 s, playing the clips and with the body's pose held, so only the
    // springs and not the clips' sampling differ. Writes a table to artifacts/cloth-measurements.md,
    // which Git ignores; docs/EQUIPMENT_VISUALS_DESIGN.md keeps the numbers decisions rest on.
    public static class ClothMeasurement
    {
        public const string MenuPath = "MMORPG/Tools/Art Pipeline/Measure Cloth";
        public const string ReportPath = "artifacts/cloth-measurements.md";

        // The camera stands this far behind the body's chest, near enough for the springs.
        private static readonly Vector3 CameraOffset = new Vector3(0f, 0.3f, -3.2f);

        public static IReadOnlyList<(string Label, string[] Garments)> Outfits { get; } = new[]
        {
            ("robe", new[] { "WoolRobe" }),
            ("cloak", new[] { "WoolCloak" }),
            ("robe+cloak", new[] { "WoolRobe", "WoolCloak" }),
        };

        [MenuItem(MenuPath)]
        public static void Measure()
        {
            var text = new StringBuilder();
            text.Append("# Cloth Measurements\n\nWritten by ").Append(MenuPath).Append(" on ")
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(".\n\n");
            text.Append("| Body | Outfit | Motion | Through cloth (vertices, mm) | Legs through cloth (mm) | Robe through cloak (vertices, mm) | ")
                .Append("Jerk RMS (m/s^3) | Flare (cm) | Settle (s) | Order breaks | Jump move (mm) |\n");
            text.Append("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\n");
            var deviations = new StringBuilder();
            deviations.Append("\n| Body | Outfit | Pose | 1/30 s (cm) | 1/60 s (cm) | 1/144 s (cm) | Jittery (cm) |\n")
                .Append("| --- | --- | --- | --- | --- | --- | --- |\n");
            try
            {
                foreach (var body in WardrobePreview.Bodies())
                {
                    foreach (var (label, garments) in Outfits)
                    {
                        using (var preview = new WardrobePreview(body))
                        {
                            preview.Wear(garments);
                            foreach (var motion in ClothStressMotion.All)
                            {
                                EditorUtility.DisplayProgressBar("Measure Cloth", body.Name + ", " + label + ", " + motion.Name, 0f);
                                var report = Run(preview, motion);
                                text.Append(Row(body.Name, label, motion.Name, report));
                            }

                            foreach (var frozen in new[] { false, true })
                            {
                                var deviation = FrameTimeDeviations(preview, ClothStressMotion.Named("Zig-zag"), frozen);
                                deviations.Append("| ").Append(body.Name).Append(" | ").Append(label).Append(" | ")
                                    .Append(frozen ? "held" : "clips").Append(" | ")
                                    .Append(string.Join(" | ", deviation.Select(each => Number(each.Largest * 100f) + " / " + Number(each.Rms * 100f))))
                                    .Append(" |\n");
                            }
                        }
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            text.Append("\nHow far the chains' tips in a zig-zag lie from where they lie stepped at 1/240 s, in the frame of the bone they hang ")
                .Append("from, at most and as RMS over the frames:\n");
            text.Append(deviations);
            var path = Path.Combine(RepositoryLayout.FindRoot(), ReportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
            Debug.Log("Wrote the cloth measurements to " + ReportPath + ".");
        }

        // Plays a motion on a dressed preview from its start and measures every frame, the
        // vertices that come through the cloth every meshEvery frames.
        public static ClothMotionReport Run(WardrobePreview preview, ClothStressMotion motion, float frameSeconds = 1f / 60f,
            int meshEvery = 3)
        {
            using (var meter = new ClothMotionMeter(preview))
            {
                preview.Play(motion);
                meter.Reset();
                var frame = 0;
                foreach (var step in motion.Frames(frameSeconds))
                {
                    if (preview.StressTime + step > motion.Seconds)
                    {
                        break;
                    }

                    preview.Step(step, CameraOffset);
                    meter.Sample(motion, preview.StressTime, step, frame % meshEvery == 0);
                    frame++;
                }

                return meter.Report;
            }
        }

        // How far the chains' tips lie, in the frame of the bone they hang from, from where they lie
        // when the motion is stepped at 1/240 s, at 1/30, 1/60, and 1/144 s and in jittery frames:
        // the largest distance at any frame, and the RMS over the frames. With the pose held only the
        // body's root moves, so the clips' own sampling does not count.
        public static (float Largest, float Rms)[] FrameTimeDeviations(WardrobePreview preview, ClothStressMotion motion, bool frozen = false)
        {
            var wasFrozen = preview.Frozen;
            preview.Frozen = frozen;
            try
            {
                var reference = Course(preview, motion, 1f / 240f, false);
                return new[] { 1f / 30f, 1f / 60f, 1f / 144f, -1f }
                    .Select(step => Deviation(reference, Course(preview, motion, Mathf.Abs(step), step < 0f)))
                    .ToArray();
            }
            finally
            {
                preview.Frozen = wasFrozen;
            }
        }

        private static List<(float Time, Vector3[] Tips)> Course(WardrobePreview preview, ClothStressMotion motion, float frameSeconds,
            bool jittery)
        {
            var anchors = ClothChains.All.Select(chain => preview.Body.GetComponentsInChildren<Transform>(true)
                .First(bone => bone.name == chain.Parent)).ToArray();
            var course = new List<(float, Vector3[])>();
            preview.Play(motion);
            var random = new System.Random(7);
            while (true)
            {
                var step = jittery ? Mathf.Lerp(1f / 240f, 1f / 20f, (float)random.NextDouble()) : frameSeconds;
                if (preview.StressTime + step > motion.Seconds)
                {
                    break;
                }

                preview.Step(step, CameraOffset);
                var tips = new Vector3[ClothChains.All.Count];
                for (var index = 0; index < tips.Length; index++)
                {
                    var particles = preview.Solver.ParticlesOf(ClothChains.All[index]);
                    tips[index] = particles.Count > 0 ? anchors[index].InverseTransformPoint(particles[particles.Count - 1]) : Vector3.zero;
                }

                course.Add((preview.StressTime, tips));
            }

            return course;
        }

        private static (float Largest, float Rms) Deviation(List<(float Time, Vector3[] Tips)> reference,
            List<(float Time, Vector3[] Tips)> course)
        {
            var largest = 0f;
            var squares = 0.0;
            var count = 0;
            var at = 0;
            foreach (var (time, tips) in course)
            {
                while (at + 1 < reference.Count && reference[at + 1].Time < time)
                {
                    at++;
                }

                if (at + 1 >= reference.Count)
                {
                    break;
                }

                var (t0, before) = reference[at];
                var (t1, after) = reference[at + 1];
                var share = t1 > t0 ? Mathf.Clamp01((time - t0) / (t1 - t0)) : 0f;
                for (var index = 0; index < tips.Length; index++)
                {
                    if (tips[index] == Vector3.zero)
                    {
                        // A chain without cloth.
                        continue;
                    }

                    var distance = Vector3.Distance(tips[index], Vector3.Lerp(before[index], after[index], share));
                    largest = Mathf.Max(largest, distance);
                    squares += distance * distance;
                    count++;
                }
            }

            return (largest, count > 0 ? (float)Math.Sqrt(squares / count) : 0f);
        }

        private static string Row(string body, string outfit, string motion, ClothMotionReport report)
        {
            return "| " + body + " | " + outfit + " | " + motion + " | "
                + report.Poked + ", " + Number(report.PokeDepth * 1000f) + " | " + Number(report.LegPokeDepth * 1000f) + " | "
                + report.LayerPoked + ", " + Number(report.LayerPokeDepth * 1000f)
                + " | " + Number(report.JerkRms) + " | " + Number(report.Flare * 100f) + " | " + Number(report.SettleSeconds) + " | "
                + report.OrderBreaks + " | " + Number(report.JumpMove * 1000f) + " |\n";
        }

        private static string Number(float value)
        {
            return value.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }
}
