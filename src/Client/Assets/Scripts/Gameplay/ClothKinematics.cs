using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The cloth chains laid by the legs and pushed out of the body, without springs: the part of
    // ClothChainSolver that tools/blender/art_pipeline/cloth_core.py follows for the art pipeline's
    // reviews and checks. It works on plain data, so the kinematic fixtures pin both to the same
    // results. Every chain hangs as given, its bones beside a thigh or a shin turn as that part of
    // the leg turns beyond the profile's slack, and its particles are pushed out of the capsules and
    // out of the cloth under it at their bones' lengths. Its constants are the cloth section of
    // art/Skeletons/Humanoid.json. See docs/EQUIPMENT_VISUALS_DESIGN.md.
    public static class ClothKinematics
    {
        // Each particle alternates this often between leaving the capsules and its bone length.
        public const int ResolveIterations = 8;
        // Below this share of a capsule's push across the bone, a particle is pushed straight out.
        public const float MinimumSlope = 0.2f;
        // At most this many passes from the tip and back when a chain cannot leave the body otherwise.
        public const int ChainPasses = 3;
        // A knee this much farther forward than the other, in meters, makes its leg the one in front.
        public const float AheadBlend = 0.05f;
        // A bone follows its part of the leg once that pushes it farther outward than hanging by its
        // profile's slack, so a breathing body leaves the cloth at rest, and fully this much beyond
        // it, in the outward share of a unit direction.
        public const float FollowReach = 0.3f;
        // The gap a cloak's cloth keeps from the cloth of a robe's skirt under it, in meters.
        public const float LayerGap = 0.01f;

        // The way a chain faces from the hips, by its angle around them in degrees.
        public static Vector3 Outward(LegPose legs, float angle)
        {
            var radians = angle * Mathf.Deg2Rad;
            return legs.Forward * Mathf.Cos(radians) + legs.Right * Mathf.Sin(radians);
        }

        // Turns every bone beside a thigh or a shin as that part of the legs turns, as far as it
        // pushes the bone outward beyond the slack, the way the chain faces from the hips. The
        // targets hang from the chain's parent at rest and are laid again from the root.
        public static void FollowLegs(Vector3[] targets, float[] lengths, IReadOnlyList<LegPart> beside, float angle, LegPose legs,
            float slack)
        {
            var share = legs.RightShare(angle);
            var outward = Outward(legs, angle);
            var hangingFrom = targets[0];
            for (var index = 0; index < lengths.Length; index++)
            {
                var hangingTo = targets[index + 1];
                var hanging = (hangingTo - hangingFrom).normalized;
                hangingFrom = hangingTo;
                var part = index < beside.Count ? beside[index] : LegPart.None;
                var direction = hanging;
                if (part != LegPart.None)
                {
                    var left = part == LegPart.Thigh ? LegPose.ThighL : LegPose.ShinL;
                    var right = part == LegPart.Thigh ? LegPose.ThighR : LegPose.ShinR;
                    var turned = Quaternion.Slerp(legs.Turns[left], legs.Turns[right], share) * hanging;
                    var reach = Mathf.Clamp01((Vector3.Dot(turned - hanging, outward) - slack) / FollowReach);
                    direction = reach > 0f ? Vector3.Slerp(hanging, turned, reach) : hanging;
                }

                targets[index + 1] = targets[index] + direction.normalized * lengths[index];
            }
        }

        // The margin each particle keeps from each capsule, by particle, then capsule: the one its
        // cloth keeps, the profile's and how far the cloth lies within the particle, and from a
        // capsule no more than it has in the bind pose, where restDepths gives how far it lies within
        // it there (negative infinity for no limit, as for the legs), all scaled with the body.
        public static void Margins(float margin, Vector2[] reach, float[] restDepths, int capsules, float scale, float[] into)
        {
            var particles = into.Length / Math.Max(capsules, 1);
            for (var index = 0; index < particles; index++)
            {
                var own = margin + Within(reach, index, scale);
                for (var capsule = 0; capsule < capsules; capsule++)
                {
                    into[index * capsules + capsule] = restDepths == null
                        ? own
                        : Mathf.Min(own, -restDepths[index * capsules + capsule] * scale);
                }
            }
        }

        // How far the garment's cloth lies within a particle, or zero without the garment or cloth.
        public static float Within(Vector2[] reach, int index, float scale)
        {
            return reach != null && index < reach.Length && !float.IsNaN(reach[index].y) ? reach[index].y * scale : 0f;
        }

        // Moves every particle out of the capsules at its bone's length from the one before, so
        // the bones that point at the particles end exactly where the particles are, and out of
        // the cloth under it, such as a cloak out of a robe's skirt. A pass from the root sets
        // each particle in turn; when a particle cannot leave a capsule that way, such as behind a
        // thigh that swings back, a pass from the tip pulls the chain toward where the tip is
        // free, and the next pass from the root bends the chain around the leg. The targets are
        // where the chain hangs without the body, for a link that has no direction.
        public static void Resolve(Vector3[] positions, Vector3[] targets, float[] lengths, IReadOnlyList<ClothChainSolver.Capsule> capsules,
            float[] margins, Vector2[] reach, Envelope under, float scale)
        {
            var chain = new Chain(positions, targets, lengths, capsules, margins, reach, under, scale);
            chain.FromRoot();
            for (var pass = 0; pass < ChainPasses && chain.Penetrates(); pass++)
            {
                var last = positions.Length - 1;
                chain.PushOut(last);
                for (var index = last - 1; index >= 1; index--)
                {
                    var link = positions[index] - positions[index + 1];
                    var length = link.magnitude;
                    if (length > 1e-6f)
                    {
                        positions[index] = positions[index + 1] + link / length * lengths[index];
                    }

                    chain.PushOut(index);
                }

                chain.FromRoot();
            }
        }

        // Lays every chain of a captured body as ClothChainSolver lays them at its Follow detail:
        // the kinds in the order their cloth lies on the body, the skirt's envelope built before a
        // cloak that lies over it. Returns each chain's particles, in the order of the case.
        public static Vector3[][] Lay(ClothKinematicCase body)
        {
            var legs = body.HasLegs ? body.Legs() : null;
            var capsules = new List<ClothChainSolver.Capsule>();
            foreach (var capsule in body.capsules)
            {
                capsules.Add(capsule.Posed());
            }

            var laid = new Vector3[body.chains.Count][];
            var envelope = new Envelope();
            var skirtReaches = body.chains.Exists(chain => chain.Kind == ClothChainKind.Skirt && chain.HasAnyReach);
            var cloakReaches = body.chains.Exists(chain => chain.Kind == ClothChainKind.Cloak && chain.HasAnyReach);
            for (var kind = ClothChainKind.Skirt; kind <= ClothChainKind.Hood; kind++)
            {
                var under = kind == ClothChainKind.Cloak && envelope.IsBuilt ? envelope : null;
                for (var index = 0; index < body.chains.Count; index++)
                {
                    var chain = body.chains[index];
                    if (chain.Kind != kind)
                    {
                        continue;
                    }

                    var targets = (Vector3[])chain.hanging.Clone();
                    var beside = chain.Beside;
                    if (legs != null && beside.Length > 0)
                    {
                        FollowLegs(targets, chain.lengths, beside, chain.angle, legs, chain.slack);
                    }

                    var positions = (Vector3[])targets.Clone();
                    var reach = chain.Reach();
                    var margins = new float[positions.Length * capsules.Count];
                    Margins(chain.margin, reach, chain.RestDepths(), capsules.Count, body.scale, margins);
                    Resolve(positions, targets, chain.lengths, capsules, margins, reach, under, body.scale);
                    laid[index] = positions;
                }

                if (kind == ClothChainKind.Skirt && legs != null && skirtReaches && cloakReaches)
                {
                    envelope.Begin(legs.Hips, legs.Forward, legs.Right, body.up);
                    for (var index = 0; index < body.chains.Count; index++)
                    {
                        var chain = body.chains[index];
                        if (chain.Kind == ClothChainKind.Skirt)
                        {
                            envelope.Add(laid[index], Outward(legs, chain.angle), chain.Reach(), body.scale);
                        }
                    }
                }
            }

            return laid;
        }

        // One chain while it is resolved.
        private readonly struct Chain
        {
            private readonly Vector3[] positions;
            private readonly Vector3[] targets;
            private readonly float[] lengths;
            private readonly IReadOnlyList<ClothChainSolver.Capsule> capsules;
            private readonly float[] margins;
            private readonly Vector2[] reach;
            private readonly Envelope under;
            private readonly float scale;

            public Chain(Vector3[] positions, Vector3[] targets, float[] lengths, IReadOnlyList<ClothChainSolver.Capsule> capsules,
                float[] margins, Vector2[] reach, Envelope under, float scale)
            {
                this.positions = positions;
                this.targets = targets;
                this.lengths = lengths;
                this.capsules = capsules;
                this.margins = margins;
                this.reach = reach;
                this.under = under;
                this.scale = scale;
            }

            public void FromRoot()
            {
                var colliders = capsules.Count + 1;
                for (var index = 1; index < positions.Length; index++)
                {
                    KeepLength(index);
                    for (var iteration = 0; iteration < ResolveIterations; iteration++)
                    {
                        var moved = false;
                        for (var collider = 0; collider < colliders; collider++)
                        {
                            var depth = Depth(index, collider, 1f, out var normal);
                            if (depth <= 0f)
                            {
                                continue;
                            }

                            // Slide around the joint before it, on the bone's reach, out of the collider;
                            // straight out when the bone points into it head on.
                            var link = (positions[index] - positions[index - 1]).normalized;
                            var tangent = normal - link * Vector3.Dot(normal, link);
                            var slope = tangent.magnitude;
                            positions[index] += slope > MinimumSlope
                                ? tangent / slope * Mathf.Min(depth / slope, lengths[index - 1])
                                : normal * depth;
                            moved = true;
                        }

                        KeepLength(index);
                        if (!moved)
                        {
                            break;
                        }
                    }
                }
            }

            public void PushOut(int index)
            {
                for (var collider = 0; collider <= capsules.Count; collider++)
                {
                    var depth = Depth(index, collider, 1f, out var normal);
                    if (depth > 0f)
                    {
                        positions[index] += normal * depth;
                    }
                }
            }

            public bool Penetrates()
            {
                for (var index = 1; index < positions.Length; index++)
                {
                    for (var collider = 0; collider <= capsules.Count; collider++)
                    {
                        // A little inside the margin is still outside.
                        if (Depth(index, collider, 0.5f, out _) > 0f)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            // How far a particle lies inside one of the colliders, the capsules and then the cloth
            // under it, with a share of the margins, and the way out.
            private float Depth(int index, int collider, float share, out Vector3 normal)
            {
                if (collider < capsules.Count)
                {
                    return capsules[collider].PenetrationIfNear(positions[index], margins[index * capsules.Count + collider] * share,
                        out normal);
                }

                normal = Vector3.zero;
                if (under == null || reach == null || index >= reach.Length || float.IsNaN(reach[index].y))
                {
                    return -1f;
                }

                return under.Penetration(positions[index], (LayerGap + reach[index].y) * scale * share, out normal);
            }

            private void KeepLength(int index)
            {
                var link = positions[index] - positions[index - 1];
                var length = link.magnitude;
                positions[index] = positions[index - 1] + (length > 1e-6f
                    ? link / length * lengths[index - 1]
                    : targets[index] - targets[index - 1]);
            }
        }

        // Where the legs are: how each thigh and shin turns against the posed hips from where it
        // points at rest, the hips' forward and right axes, where the hips are, and how far the right
        // knee lies ahead of the left.
        public sealed class LegPose
        {
            public const int ThighL = 0;
            public const int ShinL = 1;
            public const int ThighR = 2;
            public const int ShinR = 3;

            public Quaternion[] Turns { get; } = new Quaternion[4];

            public Vector3 Forward { get; set; }

            public Vector3 Right { get; set; }

            public Vector3 Hips { get; set; }

            public float Ahead { get; set; }

            // How much a chain at an angle around the hips follows the right leg rather than the
            // left: a side chain its own leg, a front chain the leg in front, and a back chain the
            // leg behind, blended between.
            public float RightShare(float angle)
            {
                var radians = angle * Mathf.Deg2Rad;
                var side = Mathf.Sin(radians);
                var rightAhead = Mathf.Clamp01(0.5f + Ahead / (2f * AheadBlend));
                var byDepth = Mathf.Cos(radians) > 0f ? rightAhead : 1f - rightAhead;
                return Mathf.Max(side, 0f) + (1f - Mathf.Abs(side)) * byDepth;
            }
        }

        // The outside of the cloth on the skirt chains that a cloak lies over: per chain, its
        // particles moved out by how far the skirt's cloth reaches beyond each, the way the chain
        // faces from the hips. At the height of a point, the cloth runs between the chains around the
        // hips' upright axis, its distance from the axis blended by angle, and a point nearer the
        // axis lies inside it.
        public sealed class Envelope
        {
            private readonly List<Vector3[]> lines = new List<Vector3[]>();
            private readonly List<bool[]> cloth = new List<bool[]>();
            // The height of every point of each line along the body's up.
            private readonly List<float[]> heights = new List<float[]>();
            // The cloth around the axis at the height of a point, one entry per line, by angle.
            private float[] aroundAngles = new float[0];
            private float[] aroundRadii = new float[0];
            private int count;
            // The lowest and highest point with cloth: a point outside lies outside the envelope.
            private float lowest;
            private float highest;
            private Vector3 centre;
            private Vector3 forward;
            private Vector3 right;
            private Vector3 up;

            public bool IsBuilt => count > 0;

            public void Clear()
            {
                count = 0;
            }

            public void Begin(Vector3 hips, Vector3 hipsForward, Vector3 hipsRight, Vector3 bodyUp)
            {
                centre = hips;
                forward = hipsForward;
                right = hipsRight;
                up = bodyUp;
                count = 0;
                lowest = float.PositiveInfinity;
                highest = float.NegativeInfinity;
            }

            // A chain's particles moved out by the cloth's reach at each, between the particles that
            // have cloth at them.
            public void Add(Vector3[] positions, Vector3 outward, Vector2[] reach, float scale)
            {
                if (lines.Count <= count)
                {
                    lines.Add(null);
                    cloth.Add(null);
                    heights.Add(null);
                    aroundAngles = new float[lines.Count];
                    aroundRadii = new float[lines.Count];
                }

                if (lines[count] == null || lines[count].Length != positions.Length)
                {
                    lines[count] = new Vector3[positions.Length];
                    cloth[count] = new bool[positions.Length];
                    heights[count] = new float[positions.Length];
                }

                var line = lines[count];
                var has = cloth[count];
                var height = heights[count];
                for (var index = 0; index < positions.Length; index++)
                {
                    has[index] = reach != null && index < reach.Length && !float.IsNaN(reach[index].x);
                    line[index] = has[index] ? positions[index] + outward * (reach[index].x * scale) : positions[index];
                    height[index] = Vector3.Dot(line[index], up);
                    if (has[index])
                    {
                        lowest = Mathf.Min(lowest, height[index]);
                        highest = Mathf.Max(highest, height[index]);
                    }
                }

                count++;
            }

            public float Penetration(Vector3 point, float margin, out Vector3 normal)
            {
                var height = Vector3.Dot(point, up);
                normal = Vector3.zero;
                if (height < lowest || height > highest)
                {
                    return -1f;
                }

                var around = 0;
                for (var chain = 0; chain < count; chain++)
                {
                    var line = lines[chain];
                    var has = cloth[chain];
                    var heightsOf = heights[chain];
                    for (var index = 0; index + 1 < line.Length; index++)
                    {
                        if (!has[index] || !has[index + 1])
                        {
                            continue;
                        }

                        var low = heightsOf[index];
                        var high = heightsOf[index + 1];
                        if (height >= Mathf.Min(low, high) && height <= Mathf.Max(low, high) && Mathf.Abs(high - low) > 1e-6f)
                        {
                            var (angle, radius, _) = Polar(Vector3.Lerp(line[index], line[index + 1], (height - low) / (high - low)));
                            // Insertion by angle: a handful of entries, and no allocation.
                            var at = around++;
                            while (at > 0 && aroundAngles[at - 1] > angle)
                            {
                                aroundAngles[at] = aroundAngles[at - 1];
                                aroundRadii[at] = aroundRadii[at - 1];
                                at--;
                            }

                            aroundAngles[at] = angle;
                            aroundRadii[at] = radius;
                            break;
                        }
                    }
                }

                var (pointAngle, pointRadius, flat) = Polar(point);
                normal = pointRadius > 1e-6f ? flat / pointRadius : -forward;
                if (around < 3)
                {
                    return -1f;
                }

                var clothRadius = aroundRadii[0];
                for (var index = 0; index < around; index++)
                {
                    var before = (index - 1 + around) % around;
                    var (a0, r0) = (aroundAngles[before], aroundRadii[before]);
                    var (a1, r1) = (aroundAngles[index], aroundRadii[index]);
                    var span = Mathf.Repeat(a1 - a0, 2f * Mathf.PI);
                    var share = span > 1e-9f ? Mathf.Repeat(pointAngle - a0, 2f * Mathf.PI) / span : 2f;
                    if (share <= 1f)
                    {
                        clothRadius = r0 + (r1 - r0) * share;
                        break;
                    }
                }

                return clothRadius + margin - pointRadius;
            }

            private (float Angle, float Radius, Vector3 Flat) Polar(Vector3 point)
            {
                var offset = point - centre;
                var flat = offset - up * Vector3.Dot(offset, up);
                return (Mathf.Atan2(Vector3.Dot(flat, right), Vector3.Dot(flat, forward)), flat.magnitude, flat);
            }
        }
    }
}
