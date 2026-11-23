using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // Where the camera of a game view stands, as tools/art/blender/art_pipeline/game_view.py places
    // Blender's, rule for rule: the picture's size, the game camera's vertical field of view, the
    // margin around a whole figure, the distance range, and the close distance, the direction by the
    // view's yaw and pitch, and what it frames: the whole figure, the feet whole, or the head or the
    // waist up close. It works in the body's own frame: Unity's axes of a body that faces +Z with its
    // right at +X and its up at +Y are Blender's axes of a body that faces +Y with its right at +X and
    // its up at +Z, with y and z swapped, and the dot products the framing takes are the same in both.
    // It computes in doubles, as numpy does. See docs/ART_PIPELINE.md, Capturing The Views From The
    // Game.
    public static class GameViewFraming
    {
        public const int Width = 512;
        public const int Height = 768;
        public const float FieldOfView = 55f;
        public const double Nearest = 0.6;
        public const double Farthest = 15.0;
        public const double FrameMargin = 0.06;
        public const double CloseDistance = 0.6;
        // A close view of the head looks at this share of the way along the head bone, from its head
        // at the mouth to its tail at the top of the skull; one of the waist at the hips bone's head.
        public const double HeadShare = 0.4;
        // The feet are framed whole with what is drawn up to this height above their bones' heads.
        public const double FeetAbove = 0.15;
        // How often the figure is moved to the middle of the picture and measured again.
        public const int Rounds = 4;

        public const string Figure = "figure";
        public const string Head = "head";
        public const string Waist = "waist";
        public const string Feet = "feet";

        // Whether a view frames this, one of the four frames of the standard set.
        public static bool IsFrame(string frame)
        {
            return frame == Figure || frame == Head || frame == Waist || frame == Feet;
        }

        // The direction from what the camera looks at toward the camera, in the body's frame, by yaw
        // and pitch in degrees: a yaw of 0 sees the body's back and 180 its front, and a positive
        // pitch looks down on it.
        public static Vector3 TowardCamera(double yaw, double pitch)
        {
            return ToVector(Toward(yaw, pitch));
        }

        // The camera that frames the points whole, in the body's frame: the direction toward it, the
        // point it looks at, and its distance, which leaves FrameMargin of the frame free around them
        // at the game's field of view, within the game's distance range.
        public static Placement Aim(IReadOnlyList<Vector3> points, double yaw, double pitch, double aspect = (double)Width / Height)
        {
            if (points == null || points.Count == 0)
            {
                throw new ArgumentException("A view frames at least one point.", nameof(points));
            }

            var toward = Toward(yaw, pitch);
            var look = -toward;
            var right = Normalized(Cross(new Double3(0.0, 1.0, 0.0), look));
            var up = Cross(look, right);
            var tanY = Math.Tan(FieldOfView * Math.PI / 180.0 / 2.0) * (1.0 - FrameMargin);
            var tanX = tanY * aspect;
            var low = new Double3(double.MaxValue, double.MaxValue, double.MaxValue);
            var high = new Double3(double.MinValue, double.MinValue, double.MinValue);
            for (var index = 0; index < points.Count; index++)
            {
                var point = new Double3(points[index]);
                low = new Double3(Math.Min(low.X, point.X), Math.Min(low.Y, point.Y), Math.Min(low.Z, point.Z));
                high = new Double3(Math.Max(high.X, point.X), Math.Max(high.Y, point.Y), Math.Max(high.Z, point.Z));
            }

            var centre = (low + high) * 0.5;
            var distance = Nearest;
            var across = new double[points.Count];
            var upward = new double[points.Count];
            var depths = new double[points.Count];
            for (var round = 0; round < Rounds; round++)
            {
                var needed = double.MinValue;
                for (var index = 0; index < points.Count; index++)
                {
                    var relative = new Double3(points[index]) - centre;
                    across[index] = Dot(relative, right);
                    upward[index] = Dot(relative, up);
                    depths[index] = Dot(relative, toward);
                    needed = Math.Max(needed, Math.Max(Math.Abs(across[index]) / tanX + depths[index],
                        Math.Abs(upward[index]) / tanY + depths[index]));
                }

                distance = Math.Min(Math.Max(needed, Nearest), Farthest);

                // The figure in the middle of the picture as the camera sees it.
                double leftmost = double.MaxValue, rightmost = double.MinValue, lowest = double.MaxValue, highest = double.MinValue;
                for (var index = 0; index < points.Count; index++)
                {
                    var span = distance - depths[index];
                    var sideways = across[index] / span;
                    var lifted = upward[index] / span;
                    leftmost = Math.Min(leftmost, sideways);
                    rightmost = Math.Max(rightmost, sideways);
                    lowest = Math.Min(lowest, lifted);
                    highest = Math.Max(highest, lifted);
                }

                centre = centre + right * ((rightmost + leftmost) / 2.0 * distance) + up * ((highest + lowest) / 2.0 * distance);
            }

            return new Placement(ToVector(toward), ToVector(centre), (float)distance);
        }

        // A close view, in the body's frame: the camera at the game's least distance from the point.
        public static Placement Close(Vector3 point, double yaw, double pitch)
        {
            return new Placement(TowardCamera(yaw, pitch), point, (float)CloseDistance);
        }

        // The points of the feet frame: what is drawn up to FeetAbove above the higher foot bone's
        // head, in the body's frame.
        public static List<Vector3> FeetPoints(IReadOnlyList<Vector3> points, float leftFootHeight, float rightFootHeight)
        {
            var top = Math.Max(leftFootHeight, rightFootHeight) + FeetAbove;
            var found = new List<Vector3>();
            foreach (var point in points)
            {
                if (point.y <= top)
                {
                    found.Add(point);
                }
            }

            return found;
        }

        // How far the two silhouettes of a picture overlap, each a mask of its pixels: the pixels in
        // both over the pixels in either.
        public static Overlap Compare(bool[] first, bool[] second)
        {
            if (first == null || second == null || first.Length != second.Length)
            {
                throw new ArgumentException("Two silhouettes of one picture size are compared.");
            }

            int both = 0, either = 0, ofFirst = 0, ofSecond = 0;
            for (var index = 0; index < first.Length; index++)
            {
                both += first[index] && second[index] ? 1 : 0;
                either += first[index] || second[index] ? 1 : 0;
                ofFirst += first[index] ? 1 : 0;
                ofSecond += second[index] ? 1 : 0;
            }

            return new Overlap(both, either, ofFirst, ofSecond);
        }

        // The pixels of a picture whose alpha is at least the given one.
        public static bool[] Silhouette(Color32[] pixels, byte alpha)
        {
            var mask = new bool[pixels.Length];
            for (var index = 0; index < pixels.Length; index++)
            {
                mask[index] = pixels[index].a >= alpha;
            }

            return mask;
        }

        private static Double3 Toward(double yaw, double pitch)
        {
            var p = pitch * Math.PI / 180.0;
            var y = yaw * Math.PI / 180.0;
            return new Double3(Math.Sin(y) * Math.Cos(p), Math.Sin(p), -Math.Cos(y) * Math.Cos(p));
        }

        // Unity's cross product, of its left-handed axes: Cross(up, forward) is the right.
        private static Double3 Cross(Double3 a, Double3 b)
        {
            return new Double3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }

        private static double Dot(Double3 a, Double3 b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        private static Double3 Normalized(Double3 value)
        {
            var length = Math.Sqrt(Dot(value, value));
            return value * (1.0 / length);
        }

        private static Vector3 ToVector(Double3 value)
        {
            return new Vector3((float)value.X, (float)value.Y, (float)value.Z);
        }

        // Where a camera stands, in the body's frame: the direction from the point it looks at
        // toward it, that point, and its distance.
        public readonly struct Placement
        {
            public Placement(Vector3 toward, Vector3 centre, float distance)
            {
                Toward = toward;
                Centre = centre;
                Distance = distance;
            }

            public Vector3 Toward { get; }

            public Vector3 Centre { get; }

            public float Distance { get; }

            public Vector3 Camera
            {
                get { return Centre + Toward * Distance; }
            }
        }

        // The overlap of two silhouettes: the pixels in both, in either, and in each.
        public readonly struct Overlap
        {
            public Overlap(int both, int either, int first, int second)
            {
                Both = both;
                Either = either;
                First = first;
                Second = second;
            }

            public int Both { get; }

            public int Either { get; }

            public int First { get; }

            public int Second { get; }

            // The intersection over the union, 1 when both are empty.
            public float Ratio
            {
                get { return Either > 0 ? (float)Both / Either : 1f; }
            }
        }

        private readonly struct Double3
        {
            public Double3(double x, double y, double z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public Double3(Vector3 value)
                : this(value.x, value.y, value.z)
            {
            }

            public double X { get; }

            public double Y { get; }

            public double Z { get; }

            public static Double3 operator +(Double3 a, Double3 b)
            {
                return new Double3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
            }

            public static Double3 operator -(Double3 a, Double3 b)
            {
                return new Double3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
            }

            public static Double3 operator -(Double3 a)
            {
                return new Double3(-a.X, -a.Y, -a.Z);
            }

            public static Double3 operator *(Double3 a, double scale)
            {
                return new Double3(a.X * scale, a.Y * scale, a.Z * scale);
            }
        }
    }
}
