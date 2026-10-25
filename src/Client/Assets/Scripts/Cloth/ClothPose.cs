using Unity.Collections;
using Unity.Mathematics;

namespace Mmorpg.Client.Cloth
{
    // Where a body's cloth chains hang in a pose, without memory, so the same pose always lays
    // them alike: the target pose of docs/EQUIPMENT_VISUALS_DESIGN.md, which every visible body
    // shows and the art pipeline's reviews lay with cloth_core.py, step by step the same.
    //
    // Every chain hangs from its posed parent with its bones at rest, and its bones beside a thigh
    // or a shin turn as that part of each leg turns against the hips, as far as that leg pushes the
    // cloth outward beyond the profile's slack, toward the legs in the share each pushes. Then each
    // bone, from the root, swings outward in the plane of its direction and the way its chain
    // faces, by the smallest angle that leaves its samples outside what its kind collides with: its
    // end, its middle, and the cloth toward the neighbouring chains. The legs are always inside a
    // skirt and in front of a cloak, so the cloth is only ever pushed outward and cannot end on the
    // wrong side of a leg. The angle is found by steps that never pass where a sample first leaves
    // a collider and a last secant step, so it changes smoothly with the pose, and the cloth starts
    // to give way a little before a collider reaches it, so it starts moving without a jolt. Each sample keeps from each capsule
    // the margin its cloth keeps, the profile's and how far the cloth lies within the chain, but no
    // more than it has in the bind pose, so a body in its bind pose wears its garments as they were
    // made. The cloth toward a neighbour lies half way to where the neighbour hangs as it follows
    // the legs, before it swings, and moves with the bone as if the neighbour swung alike, so every
    // chain is laid on its own, the same pose always lays it alike, and two neighbours that clear
    // the cloth between them each swing only as far as it needs. A cloak keeps outside the outer
    // side of a robe's skirt under it, a gap and its own cloth's reach within it away.
    public static class ClothPose
    {
        // The samples of a bone: its end, its middle, half way to the left and right neighbour at its
        // end, and the middle of the cloth between it and each neighbour's bone beside it.
        public const int Samples = 6;
        public const int EndSample = 0;
        public const int MiddleSample = 1;
        public const int LeftSample = 2;
        public const int RightSample = 3;
        public const int LeftMiddleSample = 4;
        public const int RightMiddleSample = 5;

        // A bone swings by at most this many degrees; a sample it does not take out of a collider by
        // then, it does not try to, and a swing toward that is taken less and less from this many
        // degrees before.
        public const float MaxSwingDegrees = 165f;
        public const float SwingFadeDegrees = 30f;

        // The swing toward where a sample leaves a collider advances this many times, each time by
        // the sample's depth over this many times the farthest the sample moves per radian, which
        // never passes where it first leaves even where the collider is wider one way than another.
        public const int AdvanceSteps = 8;
        public const float AdvanceSafety = 1.5f;

        // A bone follows its part of the leg once that pushes it farther outward than hanging by its
        // profile's slack, and fully this much beyond it, in the outward share of a unit direction.
        public const float FollowReach = 0.3f;

        // The gap a cloak's cloth keeps from the cloth of a robe's skirt under it, in meters.
        public const float LayerGap = 0.01f;

        // An arm moves a sample of a cloak out by at most about this far, in meters, as far as it
        // comes into it at first and less and less beyond; farther, the arm comes through the edge
        // rather than throw the chain.
        public const float ArmPush = 0.08f;

        // A sample leaves a collider at its full angle when the swing moves it this straight out of
        // the collider where it leaves, as a cosine, and at none when the swing moves it along it:
        // between, the angle is taken in part, so it changes smoothly where the swing stops helping.
        public const float ExitAlong = 0.3f;

        // The cloth starts to give way when a collider comes this close to a sample, in meters, and
        // lies on it once the collider is as far into it: between, it keeps a little more clearance,
        // so its motion starts and ends without a jolt.
        public const float ContactBand = 0.02f;

        // A bone this close to the way its chain faces has no plane to swing in.
        private const float SwingSideMinimum = 1e-4f;

        private const float NoDepth = -1e30f;

        // The way a chain faces: from the hips at its angle, or from the capsule its kind's frame is,
        // such as the head's for a hood.
        public static float3 Outward(in ClothChainData chain, in ClothLegs legs, NativeArray<ClothCapsule> capsules, int capsuleStart,
            int frameCapsule)
        {
            math.sincos(math.radians(chain.Angle), out var sin, out var cos);
            if (frameCapsule >= 0)
            {
                var frame = capsules[capsuleStart + frameCapsule];
                return frame.Front * cos + frame.Side * sin;
            }

            return legs.Valid ? legs.Forward * cos + legs.Right * sin : float3.zero;
        }

        // The direction of every bone of a chain, from where it hangs: beside a thigh or a shin turned
        // as that part of the legs turns, as far as it pushes the bone outward beyond the slack, the
        // way the chain faces from the hips. Each leg pushes on its own, in full on its own side and
        // the front and back and less toward the other side, where it does not reach: a side chain
        // follows only its own leg. The bone turns toward both legs in the share of the squares of
        // their pushes, so a front chain follows the leg that pushes it forward and a back chain the
        // leg that kicks it back, and nothing changes at once when the legs pass each other.
        public static void FollowLegs(in ClothChainData chain, in ClothLegs legs, NativeArray<ClothBone> bones,
            NativeArray<float3> hanging, NativeArray<float3> directions)
        {
            math.sincos(math.radians(chain.Angle), out var sin, out var cos);
            var outward = legs.Forward * cos + legs.Right * sin;
            var leftSide = math.min(1f, 1f - sin);
            var rightSide = math.min(1f, 1f + sin);
            for (var index = 0; index < chain.BoneCount; index++)
            {
                var particle = chain.ParticleStart + index;
                var hangingDirection = ClothMath.Normalized(hanging[particle + 1] - hanging[particle]);
                var direction = hangingDirection;
                var part = bones[chain.BoneStart + index].Beside;
                if (legs.Valid && part != ClothLegPart.None)
                {
                    var left = ClothMath.Rotate(part == ClothLegPart.Thigh ? legs.ThighL : legs.ShinL, hangingDirection);
                    var right = ClothMath.Rotate(part == ClothLegPart.Thigh ? legs.ThighR : legs.ShinR, hangingDirection);
                    var leftReach = leftSide * math.saturate((math.dot(left - hangingDirection, outward) - chain.Slack) / FollowReach);
                    var rightReach = rightSide * math.saturate((math.dot(right - hangingDirection, outward) - chain.Slack) / FollowReach);
                    var reach = math.max(leftReach, rightReach);
                    if (reach > 0f)
                    {
                        var share = rightReach * rightReach / (leftReach * leftReach + rightReach * rightReach);
                        direction = ClothMath.SlerpDirections(hangingDirection, ClothMath.SlerpDirections(left, right, share), reach);
                    }
                }

                directions[chain.BoneStart + index] = ClothMath.Normalized(direction);
            }
        }

        // How far a point lies within a capsule and a margin: positive inside.
        public static float Depth(in ClothCapsule capsule, NativeArray<float> radii, float3 point, float margin)
        {
            var away = point - ClothMath.Closest(point, capsule.A, capsule.B);
            return RadiusToward(capsule, radii, away) + margin - math.length(away);
        }

        // The radius toward a direction from a capsule's axis, blended between the two directions
        // around it that were measured.
        public static float RadiusToward(in ClothCapsule capsule, NativeArray<float> radii, float3 away)
        {
            var count = capsule.RadiiCount;
            if (count == 1)
            {
                return radii[capsule.RadiiStart] * capsule.Scale;
            }

            var angle = math.atan2(math.dot(away, capsule.Side), math.dot(away, capsule.Front));
            var position = (angle + math.PI) / (2f * math.PI) * count - 0.5f;
            var lower = (int)math.floor(position);
            var share = position - lower;
            var below = ((lower % count) + count) % count;
            var above = (((lower + 1) % count) + count) % count;
            return (radii[capsule.RadiiStart + below] * (1f - share) + radii[capsule.RadiiStart + above] * share) * capsule.Scale;
        }

        // How far a chain's garment's cloth lies within a particle, scaled with the body.
        public static float Within(NativeArray<ClothParticle> particles, int particle, float scale)
        {
            var found = particles[particle];
            return found.HasCloth ? found.Within * scale : 0f;
        }

        // The margin every sample of every bone of a body keeps from every capsule, laid in the body's
        // bind pose: the one its cloth keeps, the profile's and how far the cloth lies within it, but
        // no more than it has there less the contact band, so nothing gives way in the bind pose. By
        // bone of the body, sample, then capsule.
        public static void Margins(int chainStart, int chainCount, int boneStart, int capsuleStart, int capsuleCount, float scale,
            NativeArray<ClothChainData> chains, NativeArray<ClothParticle> particles, NativeArray<ClothCapsule> bindCapsules,
            NativeArray<float> radii, NativeArray<float3> bindHanging, NativeArray<float> margins, int marginStart)
        {
            for (var chainIndex = 0; chainIndex < chainCount; chainIndex++)
            {
                var chain = chains[chainStart + chainIndex];
                for (var index = 0; index < chain.BoneCount; index++)
                {
                    var particle = chain.ParticleStart + index;
                    var end = bindHanging[particle + 1];
                    var row = marginStart + ((chain.BoneStart - boneStart + index) * Samples) * capsuleCount;
                    for (var sample = 0; sample < Samples; sample++)
                    {
                        var exists = SampleAt(chainStart, chain, index, sample, bindHanging[particle], end, chains, bindHanging, 0, particles,
                            scale, out var point, out var within);
                        var own = chain.Margin + within;
                        for (var capsule = 0; capsule < capsuleCount; capsule++)
                        {
                            var depth = exists ? Depth(bindCapsules[capsuleStart + capsule], radii, point, 0f) : NoDepth;
                            margins[row + sample * capsuleCount + capsule] = math.min(own, -depth - ContactBand);
                        }
                    }
                }
            }
        }

        // The neighbour a sample of a bone lies toward, and -1 for the edge of an open strip of
        // chains, where it lies as far beyond the bone as the cloth toward the neighbour on the
        // other side; false for a bone with no neighbour beside it.
        private static bool Toward(int chainStart, in ClothChainData chain, int index, int sample, NativeArray<ClothChainData> chains,
            out int neighbour, out float side)
        {
            var left = sample == LeftSample || sample == LeftMiddleSample;
            neighbour = left ? chain.Left : chain.Right;
            side = 1f;
            if (neighbour >= 0 && index + 1 <= chains[chainStart + neighbour].BoneCount)
            {
                return true;
            }

            neighbour = left ? chain.Right : chain.Left;
            side = -1f;
            return neighbour >= 0 && index + 1 <= chains[chainStart + neighbour].BoneCount;
        }

        // Where a sample of a bone lies when the bone ends at a point, and how far its cloth lies
        // within it; false for a bone with no neighbour beside it. The cloth toward a neighbour lies
        // half way to it as the chains follow the legs, in followed from the particle at its offset
        // on, and moves with the bone.
        private static bool SampleAt(int chainStart, in ClothChainData chain, int index, int sample, float3 start, float3 end,
            NativeArray<ClothChainData> chains, NativeArray<float3> followed, int followedOffset, NativeArray<ClothParticle> particles,
            float scale, out float3 point, out float within)
        {
            var particle = chain.ParticleStart + index;
            switch (sample)
            {
                case EndSample:
                    point = end;
                    within = Within(particles, particle + 1, scale);
                    return true;
                case MiddleSample:
                    point = math.lerp(start, end, 0.5f);
                    within = 0.5f * (Within(particles, particle, scale) + Within(particles, particle + 1, scale));
                    return true;
                default:
                    if (!Toward(chainStart, chain, index, sample, chains, out var neighbour, out var side))
                    {
                        point = float3.zero;
                        within = 0f;
                        return false;
                    }

                    // Beyond the edge the cloth is the bone's own.
                    var other = chains[chainStart + neighbour].ParticleStart + index + 1;
                    var otherWithin = side > 0f ? Within(particles, other, scale) : Within(particles, particle + 1, scale);
                    var otherStartWithin = side > 0f ? Within(particles, other - 1, scale) : Within(particles, particle, scale);
                    var toEnd = side * (followed[other - followedOffset] - followed[particle + 1 - followedOffset]);
                    if (sample == LeftSample || sample == RightSample)
                    {
                        point = end + 0.5f * toEnd;
                        within = 0.5f * (Within(particles, particle + 1, scale) + otherWithin);
                        return true;
                    }

                    var toStart = side * (followed[other - 1 - followedOffset] - followed[particle - followedOffset]);
                    point = math.lerp(start, end, 0.5f) + 0.25f * (toStart + toEnd);
                    within = 0.25f * (Within(particles, particle, scale) + Within(particles, particle + 1, scale) + otherStartWithin
                        + otherWithin);
                    return true;
            }
        }

        // Lays every chain of a body in a pose: the kinds in the order their cloth lies on the body,
        // and the outer side of a robe's skirt before a cloak over it.
        // The body's chains are in that order, its capsules posed, and hanging holds where every
        // particle hangs from its posed parent; positions takes where they are laid, and directions,
        // by bone, the way each bone follows the legs.
        public static void Lay(int chainStart, int chainCount, int boneStart, int capsuleStart, int capsuleCount, int marginStart,
            float scale, in ClothLegs legs, NativeArray<int> frameCapsules, NativeArray<ClothChainData> chains,
            NativeArray<ClothBone> bones, NativeArray<ClothParticle> particles, NativeArray<ClothCapsule> capsules,
            NativeArray<float> radii, NativeArray<float> margins, NativeArray<float3> hanging, NativeArray<float3> positions,
            NativeArray<float3> directions)
        {
            // Every chain as it follows the legs, before it swings: what the cloth toward a neighbour
            // is sampled toward.
            var first = chainCount > 0 ? chains[chainStart].ParticleStart : 0;
            var followed = new NativeArray<float3>(math.max(ParticleCount(chainStart, chainCount, chains), 1), Allocator.Temp);
            for (var chainIndex = 0; chainIndex < chainCount; chainIndex++)
            {
                var chain = chains[chainStart + chainIndex];
                FollowLegs(chain, legs, bones, hanging, directions);
                var point = hanging[chain.ParticleStart];
                followed[chain.ParticleStart - first] = point;
                for (var index = 0; index < chain.BoneCount; index++)
                {
                    var length = math.length(hanging[chain.ParticleStart + index + 1] - hanging[chain.ParticleStart + index]);
                    point += directions[chain.BoneStart + index] * length;
                    followed[chain.ParticleStart + index + 1 - first] = point;
                }
            }

            Collide(chainStart, chainCount, boneStart, capsuleStart, capsuleCount, marginStart, scale, legs, frameCapsules, chains,
                particles, capsules, radii, margins, hanging, followed, first, positions, directions, ClothRoles.None, true, default);
            followed.Dispose();
        }

        // How many particles a body's chains have.
        public static int ParticleCount(int chainStart, int chainCount, NativeArray<ClothChainData> chains)
        {
            var count = 0;
            for (var chainIndex = 0; chainIndex < chainCount; chainIndex++)
            {
                count += chains[chainStart + chainIndex].BoneCount + 1;
            }

            return count;
        }

        // Lays every chain of a body from its root along the directions its bones were given, each
        // bone swung out of what its kind collides with, but for the roles skipped: the last pass of
        // the target pose, and of the springs on top of it. Followed holds where every chain lies
        // before it swings, from its first particle at the offset on, which the cloth toward a
        // neighbour is sampled toward. With the contact band the cloth gives way before a collider
        // reaches it, as the target pose lays it; without, it is only taken out of the colliders,
        // so laying cloth again that lies outside them leaves it where it is. The springs lay their
        // cloth without the band and the arms, which lift a cloak by a share of how far they come
        // into it rather than out of it; and they let every sample lie as deep in a collider as it
        // lies where the target pose lays the chains, at the targets given, so the target pose stays
        // where it is. Without targets, not created, no sample lies deeper than its margin allows.
        public static void Collide(int chainStart, int chainCount, int boneStart, int capsuleStart, int capsuleCount, int marginStart,
            float scale, in ClothLegs legs, NativeArray<int> frameCapsules, NativeArray<ClothChainData> chains,
            NativeArray<ClothParticle> particles, NativeArray<ClothCapsule> capsules, NativeArray<float> radii, NativeArray<float> margins,
            NativeArray<float3> hanging, NativeArray<float3> followed, int first, NativeArray<float3> positions, NativeArray<float3> directions,
            ClothRoles skip, bool banded, NativeArray<float3> targets)
        {
            for (var chainIndex = 0; chainIndex < chainCount; chainIndex++)
            {
                var chain = chains[chainStart + chainIndex];
                for (var index = 0; index <= chain.BoneCount; index++)
                {
                    positions[chain.ParticleStart + index] = hanging[chain.ParticleStart + index];
                }
            }

            // The hips' upright axis, which a skirt hangs around however the body lies.
            var up = legs.Valid ? ClothMath.Normalized(math.cross(legs.Forward, legs.Right)) : new float3(0f, 1f, 0f);
            var surface = new NativeList<float3>(Allocator.Temp);
            var targetSurface = new NativeList<float3>(Allocator.Temp);
            var surfaceBuilt = false;
            for (var kind = ClothKind.Skirt; kind <= ClothKind.Hood; kind++)
            {
                for (var chainIndex = 0; chainIndex < chainCount; chainIndex++)
                {
                    var chain = chains[chainStart + chainIndex];
                    if (chain.Kind != kind)
                    {
                        continue;
                    }

                    var outward = Outward(chain, legs, capsules, capsuleStart, frameCapsules[chainStart + chainIndex]);
                    var under = kind == ClothKind.Cloak && surfaceBuilt;
                    for (var index = 0; index < chain.BoneCount; index++)
                    {
                        LayBone(chainStart, chain, index, outward, boneStart, capsuleStart, capsuleCount, marginStart, scale, up, legs,
                            under, surface, targetSurface, chains, particles, capsules, radii, margins, hanging, followed, first, positions,
                            directions, chain.Collides & ~skip, banded, targets);
                    }
                }

                if (kind == ClothKind.Skirt && legs.Valid && Covers(chainStart, chainCount, chains, ClothKind.Skirt)
                    && Covers(chainStart, chainCount, chains, ClothKind.Cloak))
                {
                    BuildSurface(chainStart, chainCount, scale, legs, capsules, capsuleStart, frameCapsules, chains, particles, positions, surface);
                    surfaceBuilt = surface.Length > 0;
                    if (targets.IsCreated)
                    {
                        BuildSurface(chainStart, chainCount, scale, legs, capsules, capsuleStart, frameCapsules, chains, particles, targets,
                            targetSurface);
                    }
                }
            }

            surface.Dispose();
            targetSurface.Dispose();
        }

        private static bool Covers(int chainStart, int chainCount, NativeArray<ClothChainData> chains, ClothKind kind)
        {
            for (var chainIndex = 0; chainIndex < chainCount; chainIndex++)
            {
                var chain = chains[chainStart + chainIndex];
                if (chain.Kind == kind && chain.ReachKnown)
                {
                    return true;
                }
            }

            return false;
        }

        // Swings one bone outward from where its chain's particle before it lies, by the smallest
        // angle that clears its samples, and lays its end there.
        private static void LayBone(int chainStart, in ClothChainData chain, int index, float3 outward, int boneStart, int capsuleStart,
            int capsuleCount, int marginStart, float scale, float3 up, in ClothLegs legs, bool under, NativeList<float3> surface,
            NativeList<float3> targetSurface, NativeArray<ClothChainData> chains, NativeArray<ClothParticle> particles, NativeArray<ClothCapsule> capsules,
            NativeArray<float> radii, NativeArray<float> margins, NativeArray<float3> hanging, NativeArray<float3> followed,
            int followedOffset, NativeArray<float3> positions, NativeArray<float3> directions, ClothRoles collides, bool banded,
            NativeArray<float3> targets)
        {
            var particle = chain.ParticleStart + index;
            var start = positions[particle];
            var direction = directions[chain.BoneStart + index];
            var length = math.length(hanging[particle + 1] - hanging[particle]);
            var side = outward - direction * math.dot(outward, direction);
            var sideLength = math.length(side);
            if (length < 1e-6f)
            {
                return;
            }

            if (sideLength < SwingSideMinimum)
            {
                positions[particle + 1] = start + direction * length;
                return;
            }

            side /= sideLength;
            var row = marginStart + ((chain.BoneStart - boneStart + index) * Samples) * capsuleCount;
            var swing = new Swing
            {
                ChainStart = chainStart,
                Chain = chain,
                Index = index,
                Start = start,
                Direction = direction,
                Side = side,
                Length = length,
                Row = row,
                CapsuleStart = capsuleStart,
                CapsuleCount = capsuleCount,
                Scale = scale,
                Band = banded ? ContactBand * scale : 0f,
                Up = up,
                Hips = legs.Hips,
                FollowedOffset = followedOffset,
                Targets = targets,
                TargetSurface = targetSurface,
            };

            // A capsule farther from the bone's start than the farthest its samples lie from there in
            // any swing, its widest radius, the largest margin a sample keeps from it, and the contact
            // band never reaches a sample.
            var reach = swing.SampleReach(chains, followed) + swing.Band;
            var candidates = new FixedList64Bytes<byte>();
            for (var capsule = 0; capsule < capsuleCount && candidates.Length < candidates.Capacity; capsule++)
            {
                var found = capsules[capsuleStart + capsule];
                if ((found.Role & collides) != 0
                    && math.distance(start, ClothMath.Closest(start, found.A, found.B)) - found.Widest
                    <= reach + swing.LargestMargin(capsule, margins))
                {
                    candidates.Add((byte)capsule);
                }
            }

            // Each sample leaves each collider at its own angle, which changes smoothly with the pose;
            // the bone swings to the largest of them.
            var angle = 0f;
            var hangingEnd = swing.End(0f);
            for (var sample = 0; sample < Samples; sample++)
            {
                if (!SampleAt(chainStart, chain, index, sample, start, hangingEnd, chains, followed, followedOffset, particles, scale,
                    out var hangingPoint, out var hangingWithin))
                {
                    continue;
                }

                for (var candidate = 0; candidate < candidates.Length; candidate++)
                {
                    var capsule = candidates[candidate];
                    var exit = (capsules[capsuleStart + capsule].Role & ClothRoles.Arm) != 0
                        ? swing.ArmAngle(sample, capsule, hangingPoint, capsules, radii, margins)
                        : swing.ExitAngle(sample, capsule, hangingPoint, hangingWithin, chains, particles, capsules, radii, margins, followed,
                            surface);
                    angle = math.max(angle, exit);
                }

                if (under && sample <= MiddleSample)
                {
                    angle = math.max(angle, swing.ExitAngle(sample, SurfaceCollider, hangingPoint, hangingWithin, chains, particles, capsules,
                        radii, margins, followed, surface));
                }
            }

            positions[particle + 1] = swing.End(angle);
        }

        // The collider of a sample that is the outer side of a skirt rather than a capsule.
        private const int SurfaceCollider = -1;

        // One bone's swing: where its end lies at an angle, and the angle at which a sample leaves a
        // collider.
        private struct Swing
        {
            public int ChainStart;
            public ClothChainData Chain;
            public int Index;
            public float3 Start;
            public float3 Direction;
            public float3 Side;
            public float Length;
            public int Row;
            public int CapsuleStart;
            public int CapsuleCount;
            public float Scale;
            public float Band;
            public NativeArray<float3> Targets;
            public NativeList<float3> TargetSurface;
            public float3 Up;
            public float3 Hips;
            public int FollowedOffset;

            public float3 End(float angle)
            {
                math.sincos(angle, out var sin, out var cos);
                return Start + (Direction * cos + Side * sin) * Length;
            }

            // The farthest any sample of the bone lies from its start in any swing: the end at the
            // bone's length, the middle at half of it, and the cloth toward a neighbour as far again
            // as its share of the way to the neighbour.
            public float SampleReach(NativeArray<ClothChainData> chains, NativeArray<float3> followed)
            {
                var reach = Length;
                var particle = Chain.ParticleStart + Index;
                for (var sample = LeftSample; sample < Samples; sample++)
                {
                    if (!Toward(ChainStart, Chain, Index, sample, chains, out var neighbour, out _))
                    {
                        continue;
                    }

                    var other = chains[ChainStart + neighbour].ParticleStart + Index + 1;
                    var toEnd = math.length(followed[other - FollowedOffset] - followed[particle + 1 - FollowedOffset]);
                    var toStart = math.length(followed[other - 1 - FollowedOffset] - followed[particle - FollowedOffset]);
                    reach = math.max(reach, sample == LeftSample || sample == RightSample
                        ? Length + 0.5f * toEnd
                        : 0.5f * Length + 0.25f * (toStart + toEnd));
                }

                return reach;
            }

            // The largest margin a sample of the bone keeps from a capsule.
            public float LargestMargin(int capsule, NativeArray<float> margins)
            {
                var largest = margins[Row + capsule];
                for (var sample = 1; sample < Samples; sample++)
                {
                    largest = math.max(largest, margins[Row + sample * CapsuleCount + capsule]);
                }

                return largest * Scale;
            }

            // How far a sample moves per radian of the swing: as the end, or the middle of the bone.
            public float Moves(int sample)
            {
                return (sample == MiddleSample || sample == LeftMiddleSample || sample == RightMiddleSample ? 0.5f : 1f) * Length;
            }

            // How deep a sample lies in a collider as the bone hangs, with the clearance the contact
            // band keeps: none outside the band, its depth once the collider is the band deep into it,
            // and between, a little more.
            public float BandDepth(float depth)
            {
                var band = Band;
                return depth <= -band ? 0f : depth >= band ? depth : (depth + band) * (depth + band) / (4f * band);
            }

            // The angle an arm swings a bone by: its sample moved out as far as the arm comes into it,
            // saturated at the arm's push.
            public float ArmAngle(int sample, int collider, float3 hangingPoint, NativeArray<ClothCapsule> capsules, NativeArray<float> radii,
                NativeArray<float> margins)
            {
                var depth = BandDepth(HangingDepth(sample, collider, hangingPoint, 0f, capsules, radii, margins, default));
                var push = ArmPush * Scale;
                return push * math.tanh(depth / push) / Moves(sample);
            }

            // The smallest angle at which a sample leaves a collider, the capsule of the body or the
            // outer side of the skirt, and the clearance the contact band keeps: none for a sample
            // outside the band as the bone hangs, else advanced toward where it first leaves and
            // found there by a secant step, taken in the share the swing moves it out of the collider
            // there and less and less toward the largest swing. The sample lies at a point as the bone
            // hangs, its cloth within it by a share.
            public float ExitAngle(int sample, int collider, float3 hangingPoint, float hangingWithin, NativeArray<ClothChainData> chains,
                NativeArray<ClothParticle> particles, NativeArray<ClothCapsule> capsules, NativeArray<float> radii, NativeArray<float> margins,
                NativeArray<float3> followed, NativeList<float3> surface)
            {
                var hangingDepth = HangingDepth(sample, collider, hangingPoint, hangingWithin, capsules, radii, margins, surface);

                // The springs let a sample lie as deep as it lies in the target pose.
                var allowed = Targets.IsCreated && hangingDepth > -Band ? TargetDepth(sample, collider, chains, particles, capsules, radii, margins) : 0f;
                hangingDepth -= allowed;
                var bandDepth = BandDepth(hangingDepth);
                if (bandDepth <= 0f)
                {
                    return 0f;
                }

                var moves = Moves(sample);
                var clearance = bandDepth - hangingDepth;
                var largest = math.radians(MaxSwingDegrees);
                var before = 0f;
                var beforeDepth = bandDepth;
                var angle = 0f;
                var depth = bandDepth;
                for (var step = 0; step < AdvanceSteps && angle < largest; step++)
                {
                    before = angle;
                    beforeDepth = depth;
                    angle = math.min(angle + math.max(depth, 0f) / (AdvanceSafety * moves), largest);
                    depth = DepthAt(angle, sample, collider, chains, particles, capsules, radii, margins, followed, surface) - allowed + clearance;
                }

                // The last steps fall short where the sample leaves; a secant step through them reaches
                // it, but no farther than a few of them.
                if (depth > 0f && beforeDepth > depth)
                {
                    angle = math.min(angle + math.min(depth * (angle - before) / (beforeDepth - depth), 4f * (angle - before)), largest);
                }

                math.sincos(angle, out var sin, out var cos);
                var along = math.saturate(math.dot(OutOf(angle, sample, collider, chains, particles, capsules, followed, surface),
                    Side * cos - Direction * sin) / ExitAlong);
                var fade = math.saturate((largest - angle) / math.radians(SwingFadeDegrees));
                return angle * along * along * (3f - 2f * along) * fade * fade * (3f - 2f * fade);
            }

            // How deep a sample lies in a collider where the target pose lays the chains, or 0 outside.
            private float TargetDepth(int sample, int collider, NativeArray<ClothChainData> chains, NativeArray<ClothParticle> particles,
                NativeArray<ClothCapsule> capsules, NativeArray<float> radii, NativeArray<float> margins)
            {
                var particle = Chain.ParticleStart + Index;
                if (!SampleAt(ChainStart, Chain, Index, sample, Targets[particle], Targets[particle + 1], chains, Targets, 0, particles, Scale,
                        out var point, out var within))
                {
                    return 0f;
                }

                var depth = collider == SurfaceCollider
                    ? SurfaceDepth(TargetSurface, Hips, Up, point, LayerGap * Scale + within)
                    : Depth(capsules[CapsuleStart + collider], radii, point, margins[Row + sample * CapsuleCount + collider] * Scale);
                return math.max(depth, 0f);
            }

            // The way out of a collider from a sample when the bone swings by an angle: away from a
            // capsule's axis, or away from the hips' upright axis for the outer side of a skirt.
            private float3 OutOf(float angle, int sample, int collider, NativeArray<ClothChainData> chains,
                NativeArray<ClothParticle> particles, NativeArray<ClothCapsule> capsules, NativeArray<float3> followed,
                NativeList<float3> surface)
            {
                SampleAt(ChainStart, Chain, Index, sample, Start, End(angle), chains, followed, FollowedOffset, particles, Scale, out var point,
                    out _);
                if (collider == SurfaceCollider)
                {
                    var flat = point - Hips;
                    return ClothMath.Normalized(flat - Up * math.dot(flat, Up));
                }

                var capsule = capsules[CapsuleStart + collider];
                var away = point - ClothMath.Closest(point, capsule.A, capsule.B);
                return math.length(away) > 1e-6f ? away / math.length(away) : capsule.AxisNormal;
            }

            // How deep a sample lies in a collider as the bone hangs, at a point with its cloth within it
            // by a share; from a capsule whose widest radius keeps it out of the contact band, no
            // deeper than that, found without the capsule's radius toward it.
            private float HangingDepth(int sample, int collider, float3 point, float within, NativeArray<ClothCapsule> capsules,
                NativeArray<float> radii, NativeArray<float> margins, NativeList<float3> surface)
            {
                if (collider == SurfaceCollider)
                {
                    return SurfaceDepth(surface, Hips, Up, point, LayerGap * Scale + within);
                }

                var capsule = capsules[CapsuleStart + collider];
                var margin = margins[Row + sample * CapsuleCount + collider] * Scale;
                var away = point - ClothMath.Closest(point, capsule.A, capsule.B);
                var distance = math.length(away);
                var widest = capsule.Widest + margin - distance;
                return widest <= -Band ? widest : RadiusToward(capsule, radii, away) + margin - distance;
            }

            // How deep a sample lies in a collider when the bone swings by an angle.
            private float DepthAt(float angle, int sample, int collider, NativeArray<ClothChainData> chains,
                NativeArray<ClothParticle> particles, NativeArray<ClothCapsule> capsules, NativeArray<float> radii,
                NativeArray<float> margins, NativeArray<float3> followed, NativeList<float3> surface)
            {
                SampleAt(ChainStart, Chain, Index, sample, Start, End(angle), chains, followed, FollowedOffset, particles, Scale, out var point,
                    out var within);
                if (collider == SurfaceCollider)
                {
                    return SurfaceDepth(surface, Hips, Up, point, LayerGap * Scale + within);
                }

                return Depth(capsules[CapsuleStart + collider], radii, point, margins[Row + sample * CapsuleCount + collider] * Scale);
            }
        }

        // The outer side of a robe's skirt: its chains' particles moved out by the cloth's reach
        // beyond them, the way each chain faces, joined in triangles to the next chain around.
        private static void BuildSurface(int chainStart, int chainCount, float scale, in ClothLegs legs,
            NativeArray<ClothCapsule> capsules, int capsuleStart, NativeArray<int> frameCapsules, NativeArray<ClothChainData> chains,
            NativeArray<ClothParticle> particles, NativeArray<float3> positions, NativeList<float3> surface)
        {
            surface.Clear();
            for (var chainIndex = 0; chainIndex < chainCount; chainIndex++)
            {
                var chain = chains[chainStart + chainIndex];
                if (chain.Kind != ClothKind.Skirt || !chain.ReachKnown || chain.Right < 0)
                {
                    continue;
                }

                var next = chains[chainStart + chain.Right];
                if (!next.ReachKnown)
                {
                    continue;
                }

                var outward = Outward(chain, legs, capsules, capsuleStart, frameCapsules[chainStart + chainIndex]);
                var nextOutward = Outward(next, legs, capsules, capsuleStart, frameCapsules[chainStart + chain.Right]);
                var count = math.min(chain.BoneCount, next.BoneCount);
                for (var index = 0; index < count; index++)
                {
                    var a = chain.ParticleStart + index;
                    var b = next.ParticleStart + index;
                    if (!particles[a].HasCloth || !particles[a + 1].HasCloth || !particles[b].HasCloth || !particles[b + 1].HasCloth)
                    {
                        continue;
                    }

                    var a0 = positions[a] + outward * (particles[a].Beyond * scale);
                    var a1 = positions[a + 1] + outward * (particles[a + 1].Beyond * scale);
                    var b0 = positions[b] + nextOutward * (particles[b].Beyond * scale);
                    var b1 = positions[b + 1] + nextOutward * (particles[b + 1].Beyond * scale);
                    surface.Add(a0);
                    surface.Add(b0);
                    surface.Add(b1);
                    surface.Add(a0);
                    surface.Add(b1);
                    surface.Add(a1);
                }
            }
        }

        // How far a point lies inside the outer side of a skirt and a gap, seen from the hips'
        // upright axis at its height: the farthest crossing of the way out through it beyond the
        // point; no depth where nothing crosses.
        public static float SurfaceDepth(NativeList<float3> surface, float3 hips, float3 up, float3 point, float gap)
        {
            var axis = hips + up * math.dot(point - hips, up);
            var way = point - axis;
            var radius = math.length(way);
            if (radius < 1e-6f)
            {
                return NoDepth;
            }

            var outward = way / radius;
            var farthest = NoDepth;
            for (var index = 0; index + 2 < surface.Length; index += 3)
            {
                if (Crossing(axis, outward, surface[index], surface[index + 1], surface[index + 2], out var distance))
                {
                    farthest = math.max(farthest, distance);
                }
            }

            return farthest > NoDepth ? farthest + gap - radius : NoDepth;
        }

        // A ray that passes this close to a triangle's edge, in its share of the triangle, crosses it,
        // so a ray along the seam of two triangles crosses both rather than slip between them.
        private const float CrossingSlack = 1e-4f;

        // Where a ray from an origin crosses a triangle, as a distance along its unit direction.
        private static bool Crossing(float3 origin, float3 direction, float3 a, float3 b, float3 c, out float distance)
        {
            distance = 0f;
            var edge1 = b - a;
            var edge2 = c - a;
            var p = math.cross(direction, edge2);
            var determinant = math.dot(edge1, p);
            if (math.abs(determinant) < 1e-12f)
            {
                return false;
            }

            var t = origin - a;
            var u = math.dot(t, p) / determinant;
            if (u < -CrossingSlack || u > 1f + CrossingSlack)
            {
                return false;
            }

            var q = math.cross(t, edge1);
            var v = math.dot(direction, q) / determinant;
            if (v < -CrossingSlack || u + v > 1f + CrossingSlack)
            {
                return false;
            }

            distance = math.dot(edge2, q) / determinant;
            return distance > 0f;
        }
    }
}
