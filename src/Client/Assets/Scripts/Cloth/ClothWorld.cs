using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace Mmorpg.Client.Cloth
{
    // The cloth of many bodies, laid every frame on the other processor cores: Burst jobs read the
    // bones that every body's cloth hangs from and collides with, lay every visible body's chains in
    // its target pose with ClothPose, and turn the chain bones to point along them. Every body has a
    // block of the same size in the world's arrays, set by the layout, so adding or removing a body
    // touches only its block. A body at rest has its chain bones put back to rest, and a body out
    // of view keeps its pose. The world owns native memory: dispose it. See
    // docs/EQUIPMENT_VISUALS_DESIGN.md.
    public sealed class ClothWorld : IDisposable
    {
        private const int ReadBatch = 32;
        private const int WriteBatch = 32;

        private readonly ClothBodyLayout layout;
        private readonly List<BodyDescription> bodies = new List<BodyDescription>();
        private readonly Stack<int> free = new Stack<int>();
        private Transform[] inputTransforms = new Transform[0];
        private Transform[] boneTransforms = new Transform[0];
        private TransformAccessArray inputAccess;
        private TransformAccessArray boneAccess;
        private bool transformsDirty;
        private int capacity;

        private NativeArray<ClothBodySlot> slots;
        private NativeArray<float4x4> matrices;
        private NativeArray<ClothCapsuleShape> capsuleShapes;
        private NativeArray<ClothCapsule> capsules;
        private NativeArray<float> radii;
        private NativeArray<ClothChainData> chains;
        private NativeArray<int> parentSlots;
        private NativeArray<int> frameCapsules;
        private NativeArray<ClothBone> bones;
        private NativeArray<ClothBoneRest> rests;
        private NativeArray<ClothParticle> particles;
        private NativeArray<float> margins;
        private NativeArray<float3> hanging;
        private NativeArray<float3> positions;
        private NativeArray<float3> directions;
        private NativeArray<quaternion> rotations;
        private NativeArray<bool> writes;
        private NativeArray<ClothLegs> legs;
        private NativeArray<float> scales;
        private NativeArray<ClothCapsule> bindCapsules;
        private NativeArray<float3> bindHanging;

        public ClothWorld(ClothBodyLayout layout, int initialCapacity = 8)
        {
            this.layout = layout;
            Allocate(Math.Max(1, initialCapacity));
        }

        public ClothBodyLayout Layout => layout;

        // How many bodies the world holds.
        public int Count => bodies.Count - free.Count;

        // Adds a body and returns its handle.
        public int Add(BodyDescription body)
        {
            Validate(body);
            int handle;
            if (free.Count > 0)
            {
                handle = free.Pop();
                bodies[handle] = body;
            }
            else
            {
                handle = bodies.Count;
                bodies.Add(body);
                if (handle >= capacity)
                {
                    Grow(capacity * 2);
                }
            }

            Write(handle, body);
            return handle;
        }

        // Replaces a body's description, as when its garments change, keeping its handle.
        public void Replace(int handle, BodyDescription body)
        {
            Validate(body);
            bodies[handle] = body;
            Write(handle, body);
        }

        public void Remove(int handle)
        {
            if (handle < 0 || handle >= bodies.Count || bodies[handle] == null)
            {
                return;
            }

            bodies[handle] = null;
            free.Push(handle);
            var slot = slots[handle];
            slot.Active = false;
            slots[handle] = slot;
            ClearTransforms(handle);
        }

        public void SetDetail(int handle, ClothDetail detail)
        {
            var slot = slots[handle];
            slot.Detail = detail;
            slots[handle] = slot;
        }

        public ClothDetail DetailOf(int handle) => slots[handle].Detail;

        // Reads every body's bones, lays every body in view in its target pose, and turns the chain
        // bones; done when it returns.
        public void Step()
        {
            if (transformsDirty)
            {
                inputAccess.SetTransforms(inputTransforms);
                boneAccess.SetTransforms(boneTransforms);
                transformsDirty = false;
            }

            var read = new ReadJob { Matrices = matrices }.ScheduleReadOnly(inputAccess, ReadBatch);
            var solve = new SolveJob
            {
                Layout = layout,
                Slots = slots,
                Matrices = matrices,
                CapsuleShapes = capsuleShapes,
                Capsules = capsules,
                Radii = radii,
                Chains = chains,
                ParentSlots = parentSlots,
                FrameCapsules = frameCapsules,
                Bones = bones,
                Rests = rests,
                Particles = particles,
                Margins = margins,
                Hanging = hanging,
                Positions = positions,
                Directions = directions,
                Rotations = rotations,
                Writes = writes,
                Legs = legs,
                Scales = scales,
            }.Schedule(bodies.Count, 1, read);
            new WriteJob { Rotations = rotations, Writes = writes }.Schedule(boneAccess, solve).Complete();
            for (var handle = 0; handle < bodies.Count; handle++)
            {
                var slot = slots[handle];
                if (slot.Active && slot.Detail == ClothDetail.Rest && !slot.Resting)
                {
                    slot.Resting = true;
                    slots[handle] = slot;
                }
                else if (slot.Active && slot.Detail != ClothDetail.Rest && slot.Resting)
                {
                    slot.Resting = false;
                    slots[handle] = slot;
                }
            }
        }

        // The laid particles of a body's chain, from its root to its tip, in world space.
        public void ParticlesOf(int handle, int chain, List<Vector3> into)
        {
            into.Clear();
            var slot = slots[handle];
            if (!slot.Active || chain < 0 || chain >= slot.ChainCount)
            {
                return;
            }

            var found = chains[slot.ChainStart + chain];
            for (var index = 0; index <= found.BoneCount; index++)
            {
                into.Add(positions[found.ParticleStart + index]);
            }
        }

        // What the last step laid a body from and what it laid, for the kinematic fixtures and the
        // tests: its capsules as posed and in the bind pose with their radii, its legs, its scale,
        // and for every chain where its particles hung from the posed parent and in the bind
        // pose and where they were laid.
        public ClothSnapshot Snapshot(int handle)
        {
            var slot = slots[handle];
            var snapshot = new ClothSnapshot
            {
                Legs = legs[handle],
                Scale = scales[handle],
                Capsules = new ClothCapsule[slot.CapsuleCount],
                BindCapsules = new ClothCapsule[slot.CapsuleCount],
                Radii = new float[slot.CapsuleCount][],
                Chains = new ClothChainData[slot.ChainCount],
                FrameCapsules = new int[slot.ChainCount],
                Beside = new ClothLegPart[slot.ChainCount][],
                Particles = new ClothParticle[slot.ChainCount][],
                Hanging = new float3[slot.ChainCount][],
                BindHanging = new float3[slot.ChainCount][],
                Positions = new float3[slot.ChainCount][],
            };
            for (var index = 0; index < slot.CapsuleCount; index++)
            {
                var capsule = capsules[slot.CapsuleStart + index];
                snapshot.Capsules[index] = capsule;
                snapshot.BindCapsules[index] = bindCapsules[slot.CapsuleStart + index];
                snapshot.Radii[index] = new float[capsule.RadiiCount];
                for (var radius = 0; radius < capsule.RadiiCount; radius++)
                {
                    snapshot.Radii[index][radius] = radii[capsule.RadiiStart + radius];
                }
            }

            for (var chainIndex = 0; chainIndex < slot.ChainCount; chainIndex++)
            {
                var chain = chains[slot.ChainStart + chainIndex];
                snapshot.Chains[chainIndex] = chain;
                snapshot.FrameCapsules[chainIndex] = frameCapsules[slot.ChainStart + chainIndex];
                snapshot.Beside[chainIndex] = new ClothLegPart[chain.BoneCount];
                for (var index = 0; index < chain.BoneCount; index++)
                {
                    snapshot.Beside[chainIndex][index] = bones[chain.BoneStart + index].Beside;
                }

                var count = chain.BoneCount + 1;
                snapshot.Particles[chainIndex] = new ClothParticle[count];
                snapshot.Hanging[chainIndex] = new float3[count];
                snapshot.BindHanging[chainIndex] = new float3[count];
                snapshot.Positions[chainIndex] = new float3[count];
                for (var index = 0; index < count; index++)
                {
                    snapshot.Particles[chainIndex][index] = particles[chain.ParticleStart + index];
                    snapshot.Hanging[chainIndex][index] = hanging[chain.ParticleStart + index];
                    snapshot.BindHanging[chainIndex][index] = bindHanging[chain.ParticleStart + index];
                    snapshot.Positions[chainIndex][index] = positions[chain.ParticleStart + index];
                }
            }

            return snapshot;
        }

        public void Dispose()
        {
            if (inputAccess.isCreated)
            {
                inputAccess.Dispose();
            }

            if (boneAccess.isCreated)
            {
                boneAccess.Dispose();
            }

            DisposeArrays();
        }

        private void Validate(BodyDescription body)
        {
            if (body.Inputs.Length > layout.Slots || body.Capsules.Length > layout.Capsules || body.Chains.Length > layout.Chains)
            {
                throw new ArgumentException("The body does not fit the cloth world's layout.");
            }

            var boneCount = 0;
            foreach (var chain in body.Chains)
            {
                boneCount += chain.Bones.Length;
            }

            if (boneCount > layout.Bones)
            {
                throw new ArgumentException("The body's chains have more bones than the cloth world's layout.");
            }
        }

        // Writes a body's description into its block, and lays its margins in its bind pose.
        private void Write(int handle, BodyDescription body)
        {
            var capsuleStart = handle * layout.Capsules;
            var radiiStart = handle * layout.Capsules * layout.Radii;
            for (var index = 0; index < body.Capsules.Length; index++)
            {
                var shape = body.Capsules[index];
                var start = radiiStart + index * layout.Radii;
                for (var radius = 0; radius < shape.Radii.Length && radius < layout.Radii; radius++)
                {
                    radii[start + radius] = shape.Radii[radius];
                }

                var widest = 0f;
                foreach (var each in shape.Radii)
                {
                    widest = math.max(widest, each);
                }

                capsuleShapes[capsuleStart + index] = new ClothCapsuleShape
                {
                    Slot = shape.Slot,
                    A = shape.A,
                    B = shape.B,
                    Front = shape.Front,
                    Side = shape.Side,
                    RadiiStart = start,
                    RadiiCount = Math.Min(shape.Radii.Length, layout.Radii),
                    Widest = widest,
                    Role = shape.Role,
                };
            }

            var chainStart = handle * layout.Chains;
            var boneStart = handle * layout.Bones;
            var particleStart = handle * layout.Particles;
            var bone = boneStart;
            var particle = particleStart;
            for (var index = 0; index < body.Chains.Length; index++)
            {
                var chain = body.Chains[index];
                chains[chainStart + index] = new ClothChainData
                {
                    Kind = chain.Kind,
                    Angle = chain.Angle,
                    BoneStart = bone,
                    BoneCount = chain.Bones.Length,
                    ParticleStart = particle,
                    Left = chain.Left,
                    Right = chain.Right,
                    Margin = chain.Margin,
                    Slack = chain.Slack,
                    ReachKnown = chain.Particles != null,
                    Collides = chain.Collides,
                };
                parentSlots[chainStart + index] = chain.ParentSlot;
                frameCapsules[chainStart + index] = chain.FrameCapsule;
                for (var b = 0; b < chain.Bones.Length; b++)
                {
                    var rest = chain.Bones[b];
                    bones[bone + b] = new ClothBone { Beside = rest.Beside };
                    rests[bone + b] = new ClothBoneRest
                    {
                        LocalPosition = rest.LocalPosition,
                        LocalRotation = rest.LocalRotation,
                        LocalScale = rest.LocalScale,
                        ChildOffset = rest.ChildOffset,
                    };
                }

                for (var p = 0; p <= chain.Bones.Length; p++)
                {
                    particles[particle + p] = chain.Particles != null && p < chain.Particles.Length
                        ? chain.Particles[p]
                        : new ClothParticle();
                }

                bone += chain.Bones.Length;
                particle += chain.Bones.Length + 1;
            }

            var slot = new ClothBodySlot
            {
                Active = true,
                Detail = slots[handle].Active ? slots[handle].Detail : ClothDetail.Pose,
                Resting = false,
                InputStart = handle * layout.Slots,
                CapsuleStart = capsuleStart,
                CapsuleCount = body.Capsules.Length,
                ChainStart = chainStart,
                ChainCount = body.Chains.Length,
                BoneStart = boneStart,
                ParticleStart = particleStart,
                MarginStart = handle * layout.Margins,
                HipsSlot = body.HipsSlot,
                RootSlot = body.RootSlot,
                LegPartSlots = new int4(body.LegPartSlots[0], body.LegPartSlots[1], body.LegPartSlots[2], body.LegPartSlots[3]),
                LegEndSlots = new int4(body.LegEndSlots[0], body.LegEndSlots[1], body.LegEndSlots[2], body.LegEndSlots[3]),
                RestThighL = body.LegsRestInHips[0],
                RestShinL = body.LegsRestInHips[1],
                RestThighR = body.LegsRestInHips[2],
                RestShinR = body.LegsRestInHips[3],
                ForwardInHips = body.ForwardInHips,
                RightInHips = body.RightInHips,
                HasLegs = body.HasLegs,
            };
            slots[handle] = slot;

            // The margins come from the body laid in its bind pose, in the body's own space.
            for (var index = 0; index < body.BindMatrices.Length; index++)
            {
                matrices[slot.InputStart + index] = body.BindMatrices[index];
            }

            Pose(slot, matrices, capsuleShapes, bindCapsules, chains, parentSlots, rests, bindHanging, legs, handle, out _);
            ClothPose.Margins(slot.ChainStart, slot.ChainCount, slot.BoneStart, slot.CapsuleStart, slot.CapsuleCount, 1f, chains,
                particles, bindCapsules, radii, bindHanging, margins, slot.MarginStart);

            for (var index = 0; index < layout.Slots; index++)
            {
                inputTransforms[slot.InputStart + index] = index < body.Inputs.Length ? body.Inputs[index] : null;
            }

            var boneIndex = boneStart;
            for (var index = 0; index < layout.Bones; index++)
            {
                boneTransforms[boneStart + index] = null;
                writes[boneStart + index] = false;
            }

            foreach (var chain in body.Chains)
            {
                foreach (var rest in chain.Bones)
                {
                    boneTransforms[boneIndex++] = rest.Transform;
                }
            }

            transformsDirty = true;
        }

        private void ClearTransforms(int handle)
        {
            for (var index = 0; index < layout.Slots; index++)
            {
                inputTransforms[handle * layout.Slots + index] = null;
            }

            for (var index = 0; index < layout.Bones; index++)
            {
                boneTransforms[handle * layout.Bones + index] = null;
                writes[handle * layout.Bones + index] = false;
            }

            transformsDirty = true;
        }

        // Poses a body from its bones' matrices: its capsules, its legs, and where every particle
        // hangs from its chain's posed parent with the bones at rest. The scale is the root's largest.
        private static void Pose(in ClothBodySlot slot, NativeArray<float4x4> matrices, NativeArray<ClothCapsuleShape> shapes,
            NativeArray<ClothCapsule> capsules, NativeArray<ClothChainData> chains, NativeArray<int> parentSlots,
            NativeArray<ClothBoneRest> rests, NativeArray<float3> hanging, NativeArray<ClothLegs> legs, int handle, out float scale)
        {
            scale = slot.RootSlot >= 0 ? LargestScale(matrices[slot.InputStart + slot.RootSlot]) : 1f;
            for (var index = 0; index < slot.CapsuleCount; index++)
            {
                var shape = shapes[slot.CapsuleStart + index];
                var matrix = matrices[slot.InputStart + shape.Slot];
                var rotation = RotationOf(matrix);
                capsules[slot.CapsuleStart + index] = new ClothCapsule
                {
                    A = math.transform(matrix, shape.A),
                    B = math.transform(matrix, shape.B),
                    Front = ClothMath.Rotate(rotation, shape.Front),
                    Side = ClothMath.Rotate(rotation, shape.Side),
                    AxisNormal = ClothMath.Rotate(rotation, new float3(0f, 0f, 1f)),
                    Scale = LargestScale(matrix),
                    Widest = shape.Widest * LargestScale(matrix),
                    RadiiStart = shape.RadiiStart,
                    RadiiCount = shape.RadiiCount,
                    Role = shape.Role,
                };
            }

            var found = new ClothLegs();
            if (slot.HasLegs)
            {
                var hips = matrices[slot.InputStart + slot.HipsSlot];
                var hipsRotation = RotationOf(hips);
                found.ThighL = Turn(hipsRotation, slot.RestThighL, matrices, slot, 0);
                found.ShinL = Turn(hipsRotation, slot.RestShinL, matrices, slot, 1);
                found.ThighR = Turn(hipsRotation, slot.RestThighR, matrices, slot, 2);
                found.ShinR = Turn(hipsRotation, slot.RestShinR, matrices, slot, 3);
                found.Forward = ClothMath.Rotate(hipsRotation, slot.ForwardInHips);
                found.Right = ClothMath.Rotate(hipsRotation, slot.RightInHips);
                found.Hips = hips.c3.xyz;
                found.Valid = true;
            }

            legs[handle] = found;
            for (var chainIndex = 0; chainIndex < slot.ChainCount; chainIndex++)
            {
                var chain = chains[slot.ChainStart + chainIndex];
                var matrix = matrices[slot.InputStart + parentSlots[slot.ChainStart + chainIndex]];
                for (var index = 0; index < chain.BoneCount; index++)
                {
                    var rest = rests[chain.BoneStart + index];
                    matrix = math.mul(matrix, float4x4.TRS(rest.LocalPosition, rest.LocalRotation, rest.LocalScale));
                    hanging[chain.ParticleStart + index] = matrix.c3.xyz;
                    if (index == chain.BoneCount - 1)
                    {
                        hanging[chain.ParticleStart + index + 1] = math.transform(matrix, rest.ChildOffset);
                    }
                }
            }
        }

        // How a leg part turns against the hips: from where it points at rest under the posed hips to
        // where it points.
        private static quaternion Turn(quaternion hipsRotation, float3 restInHips, NativeArray<float4x4> matrices, in ClothBodySlot slot,
            int part)
        {
            var from = matrices[slot.InputStart + slot.LegPartSlots[part]].c3.xyz;
            var to = matrices[slot.InputStart + slot.LegEndSlots[part]].c3.xyz;
            return ClothMath.FromTo(ClothMath.Rotate(hipsRotation, restInHips), to - from);
        }

        private static quaternion RotationOf(float4x4 matrix)
        {
            var x = math.normalize(matrix.c0.xyz);
            var y = math.normalize(matrix.c1.xyz);
            var z = math.normalize(matrix.c2.xyz);
            return new quaternion(new float3x3(x, y, z));
        }

        private static float LargestScale(float4x4 matrix)
        {
            return math.max(math.length(matrix.c0.xyz), math.max(math.length(matrix.c1.xyz), math.length(matrix.c2.xyz)));
        }

        // The local rotation of every bone of a chain that points it at its laid particle, from the
        // chain's posed parent and the bones' rest rotations.
        private static void PointBones(in ClothChainData chain, float4x4 parent, NativeArray<ClothBoneRest> rests, NativeArray<float3> positions,
            NativeArray<quaternion> rotations)
        {
            var parentRotation = RotationOf(parent);
            for (var index = 0; index < chain.BoneCount; index++)
            {
                var rest = rests[chain.BoneStart + index];
                var atRest = math.mul(parentRotation, rest.LocalRotation);
                var current = ClothMath.Rotate(atRest, rest.ChildOffset);
                var wanted = positions[chain.ParticleStart + index + 1] - positions[chain.ParticleStart + index];
                var turned = math.lengthsq(current) > 1e-12f && math.lengthsq(wanted) > 1e-12f
                    ? math.mul(ClothMath.FromTo(current, wanted), atRest)
                    : atRest;
                rotations[chain.BoneStart + index] = math.normalize(math.mul(math.inverse(parentRotation), turned));
                parentRotation = turned;
            }
        }

        private void Allocate(int bodyCapacity)
        {
            capacity = bodyCapacity;
            var persistent = Allocator.Persistent;
            slots = new NativeArray<ClothBodySlot>(capacity, persistent);
            matrices = new NativeArray<float4x4>(capacity * layout.Slots, persistent);
            capsuleShapes = new NativeArray<ClothCapsuleShape>(capacity * layout.Capsules, persistent);
            capsules = new NativeArray<ClothCapsule>(capacity * layout.Capsules, persistent);
            radii = new NativeArray<float>(capacity * layout.Capsules * layout.Radii, persistent);
            chains = new NativeArray<ClothChainData>(capacity * layout.Chains, persistent);
            parentSlots = new NativeArray<int>(capacity * layout.Chains, persistent);
            frameCapsules = new NativeArray<int>(capacity * layout.Chains, persistent);
            bones = new NativeArray<ClothBone>(capacity * layout.Bones, persistent);
            rests = new NativeArray<ClothBoneRest>(capacity * layout.Bones, persistent);
            particles = new NativeArray<ClothParticle>(capacity * layout.Particles, persistent);
            margins = new NativeArray<float>(capacity * layout.Margins, persistent);
            hanging = new NativeArray<float3>(capacity * layout.Particles, persistent);
            positions = new NativeArray<float3>(capacity * layout.Particles, persistent);
            directions = new NativeArray<float3>(capacity * layout.Bones, persistent);
            rotations = new NativeArray<quaternion>(capacity * layout.Bones, persistent);
            writes = new NativeArray<bool>(capacity * layout.Bones, persistent);
            legs = new NativeArray<ClothLegs>(capacity, persistent);
            scales = new NativeArray<float>(capacity, persistent);
            bindCapsules = new NativeArray<ClothCapsule>(capacity * layout.Capsules, persistent);
            bindHanging = new NativeArray<float3>(capacity * layout.Particles, persistent);
            Array.Resize(ref inputTransforms, capacity * layout.Slots);
            Array.Resize(ref boneTransforms, capacity * layout.Bones);
            if (inputAccess.isCreated)
            {
                inputAccess.Dispose();
                boneAccess.Dispose();
            }

            inputAccess = new TransformAccessArray(inputTransforms);
            boneAccess = new TransformAccessArray(boneTransforms);
            transformsDirty = false;
        }

        // A world for more bodies: the bodies are written again into the new blocks.
        private void Grow(int bodyCapacity)
        {
            DisposeArrays();
            Allocate(bodyCapacity);
            for (var handle = 0; handle < bodies.Count; handle++)
            {
                if (bodies[handle] != null)
                {
                    Write(handle, bodies[handle]);
                }
            }
        }

        private void DisposeArrays()
        {
            if (!slots.IsCreated)
            {
                return;
            }

            slots.Dispose();
            matrices.Dispose();
            capsuleShapes.Dispose();
            capsules.Dispose();
            radii.Dispose();
            chains.Dispose();
            parentSlots.Dispose();
            frameCapsules.Dispose();
            bones.Dispose();
            rests.Dispose();
            particles.Dispose();
            margins.Dispose();
            hanging.Dispose();
            positions.Dispose();
            directions.Dispose();
            rotations.Dispose();
            writes.Dispose();
            legs.Dispose();
            scales.Dispose();
            bindCapsules.Dispose();
            bindHanging.Dispose();
        }

        [BurstCompile]
        private struct ReadJob : IJobParallelForTransform
        {
            [WriteOnly] public NativeArray<float4x4> Matrices;

            public void Execute(int index, TransformAccess transform)
            {
                if (transform.isValid)
                {
                    Matrices[index] = transform.localToWorldMatrix;
                }
            }
        }

        [BurstCompile]
        private struct SolveJob : IJobParallelFor
        {
            public ClothBodyLayout Layout;
            [ReadOnly] public NativeArray<ClothBodySlot> Slots;
            [ReadOnly] public NativeArray<float4x4> Matrices;
            [ReadOnly] public NativeArray<ClothCapsuleShape> CapsuleShapes;
            [ReadOnly] public NativeArray<float> Radii;
            [ReadOnly] public NativeArray<ClothChainData> Chains;
            [ReadOnly] public NativeArray<int> ParentSlots;
            [ReadOnly] public NativeArray<int> FrameCapsules;
            [ReadOnly] public NativeArray<ClothBone> Bones;
            [ReadOnly] public NativeArray<ClothBoneRest> Rests;
            [ReadOnly] public NativeArray<ClothParticle> Particles;
            [ReadOnly] public NativeArray<float> Margins;

            [NativeDisableParallelForRestriction] public NativeArray<ClothCapsule> Capsules;
            [NativeDisableParallelForRestriction] public NativeArray<float3> Hanging;
            [NativeDisableParallelForRestriction] public NativeArray<float3> Positions;
            [NativeDisableParallelForRestriction] public NativeArray<float3> Directions;
            [NativeDisableParallelForRestriction] public NativeArray<quaternion> Rotations;
            [NativeDisableParallelForRestriction] public NativeArray<bool> Writes;
            [NativeDisableParallelForRestriction] public NativeArray<ClothLegs> Legs;
            [NativeDisableParallelForRestriction] public NativeArray<float> Scales;

            public void Execute(int handle)
            {
                var slot = Slots[handle];
                if (!slot.Active)
                {
                    return;
                }

                if (slot.Detail == ClothDetail.Hidden || (slot.Detail == ClothDetail.Rest && slot.Resting))
                {
                    for (var index = 0; index < Layout.Bones; index++)
                    {
                        Writes[slot.BoneStart + index] = false;
                    }

                    return;
                }

                if (slot.Detail == ClothDetail.Rest)
                {
                    for (var chainIndex = 0; chainIndex < slot.ChainCount; chainIndex++)
                    {
                        var chain = Chains[slot.ChainStart + chainIndex];
                        for (var index = 0; index < chain.BoneCount; index++)
                        {
                            Rotations[chain.BoneStart + index] = Rests[chain.BoneStart + index].LocalRotation;
                            Writes[chain.BoneStart + index] = true;
                        }
                    }

                    return;
                }

                Pose(slot, Matrices, CapsuleShapes, Capsules, Chains, ParentSlots, Rests, Hanging, Legs, handle, out var scale);
                Scales[handle] = scale;
                ClothPose.Lay(slot.ChainStart, slot.ChainCount, slot.BoneStart, slot.CapsuleStart, slot.CapsuleCount, slot.MarginStart, scale,
                    Legs[handle], FrameCapsules, Chains, Bones, Particles, Capsules, Radii, Margins, Hanging, Positions, Directions);
                for (var chainIndex = 0; chainIndex < slot.ChainCount; chainIndex++)
                {
                    var chain = Chains[slot.ChainStart + chainIndex];
                    PointBones(chain, Matrices[slot.InputStart + ParentSlots[slot.ChainStart + chainIndex]], Rests, Positions, Rotations);
                    for (var index = 0; index < chain.BoneCount; index++)
                    {
                        Writes[chain.BoneStart + index] = true;
                    }
                }
            }
        }

        [BurstCompile]
        private struct WriteJob : IJobParallelForTransform
        {
            [ReadOnly] public NativeArray<quaternion> Rotations;
            [ReadOnly] public NativeArray<bool> Writes;

            public void Execute(int index, TransformAccess transform)
            {
                if (Writes[index] && transform.isValid)
                {
                    transform.localRotation = Rotations[index];
                }
            }
        }

        // A body as the world takes it: the transforms it reads, by slot, with their matrices in the
        // bind pose in the body's own space; the slots of the root, the hips, and the legs; where the
        // legs point against the hips at rest; its capsules; and its active chains in the order they
        // are laid.
        public sealed class BodyDescription
        {
            public Transform[] Inputs = new Transform[0];
            public float4x4[] BindMatrices = new float4x4[0];
            public int RootSlot = -1;
            public int HipsSlot = -1;
            public bool HasLegs;
            // Left thigh, left shin, right thigh, right shin: the slots of each part and of its end.
            public int[] LegPartSlots = new int[4];
            public int[] LegEndSlots = new int[4];
            public float3[] LegsRestInHips = new float3[4];
            public float3 ForwardInHips;
            public float3 RightInHips;
            public CapsuleDescription[] Capsules = new CapsuleDescription[0];
            public ChainDescription[] Chains = new ChainDescription[0];
        }

        // A capsule in its bone's own space.
        public struct CapsuleDescription
        {
            public int Slot;
            public float3 A;
            public float3 B;
            public float3 Front;
            public float3 Side;
            public float[] Radii;
            public ClothRoles Role;
        }

        // A chain: its kind, angle, parent's slot, the capsule of its frame or -1 for the hips', its
        // neighbours in the body's chains, its profile's margin and slack, what it collides with, its
        // bones, and its garment's reach at each particle, or null without the garment.
        public sealed class ChainDescription
        {
            public ClothKind Kind;
            public float Angle;
            public int ParentSlot;
            public int FrameCapsule = -1;
            public int Left = -1;
            public int Right = -1;
            public float Margin;
            public float Slack;
            public ClothRoles Collides;
            public BoneDescription[] Bones = new BoneDescription[0];
            public ClothParticle[] Particles;
        }

        // A chain bone: its transform, its rest pose under its parent, the way to the next particle
        // in its own space, and the part of the legs it hangs beside.
        public struct BoneDescription
        {
            public Transform Transform;
            public float3 LocalPosition;
            public quaternion LocalRotation;
            public float3 LocalScale;
            public float3 ChildOffset;
            public ClothLegPart Beside;
        }
    }

    // A body as the world last laid it; see ClothWorld.Snapshot.
    public sealed class ClothSnapshot
    {
        public ClothLegs Legs;
        public float Scale;
        public ClothCapsule[] Capsules;
        public ClothCapsule[] BindCapsules;
        public float[][] Radii;
        public ClothChainData[] Chains;
        public int[] FrameCapsules;
        public ClothLegPart[][] Beside;
        public ClothParticle[][] Particles;
        public float3[][] Hanging;
        public float3[][] BindHanging;
        public float3[][] Positions;
    }

    // How a body's cloth moves this frame: laid in its target pose, put back to rest, or left as it
    // is while it is out of view.
    public enum ClothDetail : byte
    {
        Pose,
        Rest,
        Hidden,
    }

    // The block every body of a world takes: its transforms read, capsules, radii per capsule,
    // chains, bones, and particles.
    public struct ClothBodyLayout
    {
        public int Slots;
        public int Capsules;
        public int Radii;
        public int Chains;
        public int Bones;
        public int Particles;

        public int Margins => Bones * ClothPose.Samples * Capsules;
    }

    // A capsule as measured, in its bone's own space.
    public struct ClothCapsuleShape
    {
        public int Slot;
        public float3 A;
        public float3 B;
        public float3 Front;
        public float3 Side;
        public int RadiiStart;
        public int RadiiCount;
        public float Widest;
        public ClothRoles Role;
    }

    // A chain bone's rest pose under its parent and the way to the next particle in its own space.
    public struct ClothBoneRest
    {
        public float3 LocalPosition;
        public quaternion LocalRotation;
        public float3 LocalScale;
        public float3 ChildOffset;
    }

    // A body's block in a world and how it moves.
    public struct ClothBodySlot
    {
        public bool Active;
        public ClothDetail Detail;
        public bool Resting;
        public int InputStart;
        public int CapsuleStart;
        public int CapsuleCount;
        public int ChainStart;
        public int ChainCount;
        public int BoneStart;
        public int ParticleStart;
        public int MarginStart;
        public int RootSlot;
        public int HipsSlot;
        public bool HasLegs;
        public int4 LegPartSlots;
        public int4 LegEndSlots;
        public float3 RestThighL;
        public float3 RestShinL;
        public float3 RestThighR;
        public float3 RestShinR;
        public float3 ForwardInHips;
        public float3 RightInHips;
    }
}
