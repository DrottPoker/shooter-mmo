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
    // its target pose with ClothPose, swing the chains of the bodies given springs on it with
    // ClothSprings, and turn the chain bones to point along them. Every body has a block of the same
    // size in the world's arrays, set by the layout, so adding or removing a body touches only its
    // block. A body at rest has its chain bones put back to rest, and a body out of view keeps its
    // pose. The world owns native memory: dispose it. See docs/EQUIPMENT_VISUALS_DESIGN.md.
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
        private NativeArray<float3> targets;
        private NativeArray<ClothSpringChain> springChains;
        private NativeArray<ClothSpringBody> springBodies;
        private NativeArray<ClothAnchor> anchors;
        private NativeArray<float4x4> previousMatrices;
        private NativeArray<float3> states;
        private NativeArray<float3> previousStates;
        private NativeArray<float3> velocities;
        private NativeArray<float3> targetStates;
        private NativeArray<float> pushes;

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

        // Tells the world that a body jumped since its last step, as in a teleport, a respawn, or a
        // hard correction: its springs carry its cloth along rigidly in the next step.
        public void NotifyDiscontinuity(int handle)
        {
            if (handle < 0 || handle >= bodies.Count || bodies[handle] == null)
            {
                return;
            }

            var body = springBodies[handle];
            body.Jumped = true;
            springBodies[handle] = body;
        }

        // How far a body's springs are faded in, from 0 to 1.
        public float SpringWeightOf(int handle) => springBodies[handle].Weight;

        // Starts a body's springs again on its target in its next step, as faded in as they are, as
        // when a motion starts again from its beginning.
        public void Restart(int handle)
        {
            if (handle < 0 || handle >= bodies.Count || bodies[handle] == null)
            {
                return;
            }

            springBodies[handle] = new ClothSpringBody { Weight = springBodies[handle].Weight };
        }

        // Whether a body's cloth sleeps on its target.
        public bool SleepsOf(int handle) => springBodies[handle].Sleeping;

        // How far the collision swung a bone of a body's chain out of the body last step, the most
        // of the chain's bones from a bone on, in radians: where the legs push the cloth.
        public float PushOf(int handle, int chain, int fromBone = 0)
        {
            var slot = slots[handle];
            if (!slot.Active || chain < 0 || chain >= slot.ChainCount)
            {
                return 0f;
            }

            var found = chains[slot.ChainStart + chain];
            var largest = 0f;
            for (var index = math.max(fromBone, 0); index < found.BoneCount; index++)
            {
                largest = math.max(largest, pushes[found.BoneStart + index]);
            }

            return largest;
        }

        // Reads every body's bones, lays every body in view in its target pose, swings the chains of
        // the bodies with springs on it over a frame of this many seconds, and turns the chain bones;
        // done when it returns.
        public void Step(float deltaTime)
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
                DeltaTime = math.max(deltaTime, 0f),
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
                Targets = targets,
                SpringChains = springChains,
                SpringBodies = springBodies,
                Anchors = anchors,
                PreviousMatrices = previousMatrices,
                States = states,
                PreviousStates = previousStates,
                Velocities = velocities,
                TargetStates = targetStates,
                Pushes = pushes,
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

        // Where the last step laid a body's chain in its target pose, from its root to its tip, in
        // world space: what its springs swing about.
        public void TargetsOf(int handle, int chain, List<Vector3> into)
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
                into.Add(targets[found.ParticleStart + index]);
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
                // The springs run in the frame of the chain's parent, which knows the body's upright
                // axis from the bind pose.
                springChains[chainStart + index] = new ClothSpringChain
                {
                    Spring = chain.Spring,
                    AnchorSlot = chain.ParentSlot,
                    Anchor = (int)chain.Kind,
                    Up = math.mul(math.conjugate(RotationOf(body.BindMatrices[chain.ParentSlot])), new float3(0f, 1f, 0f)),
                };
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

            // Other cloth starts its springs again from the target.
            springBodies[handle] = default;
            for (var anchor = 0; anchor < ClothSprings.Anchors; anchor++)
            {
                anchors[handle * ClothSprings.Anchors + anchor] = default;
            }

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
            targets = new NativeArray<float3>(capacity * layout.Particles, persistent);
            springChains = new NativeArray<ClothSpringChain>(capacity * layout.Chains, persistent);
            springBodies = new NativeArray<ClothSpringBody>(capacity, persistent);
            anchors = new NativeArray<ClothAnchor>(capacity * ClothSprings.Anchors, persistent);
            previousMatrices = new NativeArray<float4x4>(capacity * layout.Slots, persistent);
            states = new NativeArray<float3>(capacity * layout.Particles, persistent);
            previousStates = new NativeArray<float3>(capacity * layout.Particles, persistent);
            velocities = new NativeArray<float3>(capacity * layout.Particles, persistent);
            targetStates = new NativeArray<float3>(capacity * layout.Particles, persistent);
            pushes = new NativeArray<float>(capacity * layout.Bones, persistent);
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

        // A world for more bodies: the bodies are written again into the new blocks, and what moves
        // with time, their springs, the frames of their anchors, and last frame's bones, is kept, so
        // growing the world neither starts their springs again nor jolts their cloth. A body keeps
        // its block, which starts at its handle times the layout's size whatever the capacity.
        private void Grow(int bodyCapacity)
        {
            var keptSlots = new NativeArray<ClothBodySlot>(slots, Allocator.Temp);
            var keptSpringBodies = new NativeArray<ClothSpringBody>(springBodies, Allocator.Temp);
            var keptAnchors = new NativeArray<ClothAnchor>(anchors, Allocator.Temp);
            var keptMatrices = new NativeArray<float4x4>(previousMatrices, Allocator.Temp);
            var keptStates = new NativeArray<float3>(states, Allocator.Temp);
            var keptPreviousStates = new NativeArray<float3>(previousStates, Allocator.Temp);
            var keptVelocities = new NativeArray<float3>(velocities, Allocator.Temp);
            var keptTargetStates = new NativeArray<float3>(targetStates, Allocator.Temp);
            var keptTargets = new NativeArray<float3>(targets, Allocator.Temp);
            DisposeArrays();
            Allocate(bodyCapacity);
            for (var handle = 0; handle < bodies.Count; handle++)
            {
                if (bodies[handle] != null)
                {
                    Write(handle, bodies[handle]);
                }
            }

            NativeArray<ClothSpringBody>.Copy(keptSpringBodies, springBodies, keptSpringBodies.Length);
            NativeArray<ClothAnchor>.Copy(keptAnchors, anchors, keptAnchors.Length);
            NativeArray<float4x4>.Copy(keptMatrices, previousMatrices, keptMatrices.Length);
            NativeArray<float3>.Copy(keptStates, states, keptStates.Length);
            NativeArray<float3>.Copy(keptPreviousStates, previousStates, keptPreviousStates.Length);
            NativeArray<float3>.Copy(keptVelocities, velocities, keptVelocities.Length);
            NativeArray<float3>.Copy(keptTargetStates, targetStates, keptTargetStates.Length);
            NativeArray<float3>.Copy(keptTargets, targets, keptTargets.Length);
            for (var handle = 0; handle < keptSlots.Length; handle++)
            {
                var slot = slots[handle];
                if (!slot.Active)
                {
                    continue;
                }

                slot.Detail = keptSlots[handle].Detail;
                slot.Resting = keptSlots[handle].Resting;
                slots[handle] = slot;
            }

            keptSlots.Dispose();
            keptSpringBodies.Dispose();
            keptAnchors.Dispose();
            keptMatrices.Dispose();
            keptStates.Dispose();
            keptPreviousStates.Dispose();
            keptVelocities.Dispose();
            keptTargetStates.Dispose();
            keptTargets.Dispose();
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
            targets.Dispose();
            springChains.Dispose();
            springBodies.Dispose();
            anchors.Dispose();
            previousMatrices.Dispose();
            states.Dispose();
            previousStates.Dispose();
            velocities.Dispose();
            targetStates.Dispose();
            pushes.Dispose();
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

        // The springs' collision skips the arms, which lift a cloak by a share of how far they come
        // into it rather than out of it, and would lift it again on every step.
        private const ClothRoles SpringSkips = ClothRoles.Arm;

        [BurstCompile]
        private struct SolveJob : IJobParallelFor
        {
            public ClothBodyLayout Layout;
            public float DeltaTime;
            [ReadOnly] public NativeArray<ClothBodySlot> Slots;
            [ReadOnly] public NativeArray<ClothSpringChain> SpringChains;
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
            [NativeDisableParallelForRestriction] public NativeArray<float3> Targets;
            [NativeDisableParallelForRestriction] public NativeArray<ClothSpringBody> SpringBodies;
            [NativeDisableParallelForRestriction] public NativeArray<ClothAnchor> Anchors;
            [NativeDisableParallelForRestriction] public NativeArray<float4x4> PreviousMatrices;
            [NativeDisableParallelForRestriction] public NativeArray<float3> States;
            [NativeDisableParallelForRestriction] public NativeArray<float3> PreviousStates;
            [NativeDisableParallelForRestriction] public NativeArray<float3> Velocities;
            [NativeDisableParallelForRestriction] public NativeArray<float3> TargetStates;
            [NativeDisableParallelForRestriction] public NativeArray<float> Pushes;

            public void Execute(int handle)
            {
                var slot = Slots[handle];
                if (!slot.Active)
                {
                    return;
                }

                if (slot.Detail == ClothDetail.Hidden || slot.Detail == ClothDetail.Rest)
                {
                    // Seen again or near again, the springs start from the target.
                    SpringBodies[handle] = default;
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
                    Legs[handle], FrameCapsules, Chains, Bones, Particles, Capsules, Radii, Margins, Hanging, Targets, Directions);
                MeasurePushes(slot, Hanging, 0, Targets);
                if (slot.Detail == ClothDetail.Spring || SpringBodies[handle].Started)
                {
                    Swing(handle, slot, scale);
                }
                else
                {
                    // Springs given again start faded out.
                    SpringBodies[handle] = default;
                    ShowTargets(slot);
                }

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

            // The way a chain of a body faces in the world, as the body stands at the step posed last.
            private float3 OutwardOf(int handle, in ClothBodySlot slot, int chainIndex)
            {
                return ClothPose.Outward(Chains[slot.ChainStart + chainIndex], Legs[handle], Capsules, slot.CapsuleStart,
                    FrameCapsules[slot.ChainStart + chainIndex]);
            }

            // How far the body turned every bone of a body's chains from where it would lie without the
            // body, as it hangs or as its springs swing it, from the body's particle at an offset, to
            // where it was laid.
            private void MeasurePushes(in ClothBodySlot slot, NativeArray<float3> free, int offset, NativeArray<float3> laid)
            {
                for (var chainIndex = 0; chainIndex < slot.ChainCount; chainIndex++)
                {
                    var chain = Chains[slot.ChainStart + chainIndex];
                    for (var index = 0; index < chain.BoneCount; index++)
                    {
                        var particle = chain.ParticleStart + index;
                        var before = ClothMath.Normalized(free[particle + 1 - offset] - free[particle - offset]);
                        var after = ClothMath.Normalized(laid[particle + 1] - laid[particle]);
                        Pushes[chain.BoneStart + index] = math.acos(math.clamp(math.dot(before, after), -1f, 1f));
                    }
                }
            }

            private void ShowTargets(in ClothBodySlot slot)
            {
                var count = ClothPose.ParticleCount(slot.ChainStart, slot.ChainCount, Chains);
                for (var index = 0; index < count; index++)
                {
                    Positions[slot.ParticleStart + index] = Targets[slot.ParticleStart + index];
                }
            }

            // Swings a body's chains on their target pose for a frame, fading the springs in while
            // the body has them and out after, and lays what is shown into the positions.
            private void Swing(int handle, in ClothBodySlot slot, float scale)
            {
                var body = SpringBodies[handle];
                var wanted = slot.Detail == ClothDetail.Spring;
                var first = slot.ParticleStart;
                var count = ClothPose.ParticleCount(slot.ChainStart, slot.ChainCount, Chains);
                if (!body.Started)
                {
                    // From the target, faded in from zero unless started again.
                    Start(handle, slot);
                    SpringBodies[handle] = new ClothSpringBody { Started = true, Weight = body.Weight };
                    ShowTargets(slot);
                    return;
                }

                body.Weight = math.saturate(body.Weight + (wanted ? DeltaTime : -DeltaTime) / ClothSprings.FadeSeconds);
                if (!wanted && body.Weight <= 0f)
                {
                    SpringBodies[handle] = default;
                    ShowTargets(slot);
                    return;
                }

                // Where the target lies this frame in the frame of each chain's anchor.
                var now = new NativeArray<float3>(count, Allocator.Temp);
                ToAnchors(slot, Matrices, slot.InputStart, Targets, now, first);

                // A jump carries the cloth along: nothing of it reaches the springs. Last frame's bones
                // move with the body's own jump, so the steps see the clips move and not the jump; a
                // pose that still jumped on the body, as one that changed at once, is taken as it is.
                var jumped = body.Jumped || JumpedSince(slot);
                body.Jumped = false;
                MoveAnchors(handle, slot, jumped);
                if (jumped)
                {
                    Rebase(slot);
                    if (JumpedSince(slot))
                    {
                        for (var index = 0; index < count; index++)
                        {
                            TargetStates[first + index] = now[index];
                        }

                        for (var index = 0; index < Layout.Slots; index++)
                        {
                            PreviousMatrices[slot.InputStart + index] = Matrices[slot.InputStart + index];
                        }
                    }
                }

                // Every step of the fixed rate this frame reaches, at most so many, the body posed
                // and the target laid between the last frame and this one.
                var step = ClothSprings.StepSeconds;
                var added = math.max(0f, math.min(DeltaTime, ClothSprings.MaxSteps * step - body.Accumulator));
                var time = body.Accumulator + added;
                var steps = math.min((int)math.floor(time / step + 1e-4f), ClothSprings.MaxSteps);
                if (steps > 0)
                {
                    var stepMatrices = new NativeArray<float4x4>(Layout.Slots, Allocator.Temp);
                    var stepTargets = new NativeArray<float3>(count, Allocator.Temp);
                    var places = new NativeArray<float3>(count, Allocator.Temp);
                    var speeds = new NativeArray<float3>(count, Allocator.Temp);
                    var starts = new NativeArray<float3>(count, Allocator.Temp);
                    var frees = new NativeArray<float3>(count, Allocator.Temp);
                    var stepSlot = slot;
                    stepSlot.InputStart = 0;
                    for (var taken = 1; taken <= steps; taken++)
                    {
                        var share = added > 0f ? math.saturate((taken * step - body.Accumulator) / added) : 1f;
                        Between(slot, share, stepMatrices);
                        Pose(stepSlot, stepMatrices, CapsuleShapes, Capsules, Chains, ParentSlots, Rests, Hanging, Legs, handle, out _);
                        for (var index = 0; index < count; index++)
                        {
                            stepTargets[index] = math.lerp(TargetStates[first + index], now[index], share);
                        }

                        StepSprings(handle, slot, scale, share, ref body, stepMatrices, stepTargets, places, speeds, starts, frees);
                    }

                    stepMatrices.Dispose();
                    stepTargets.Dispose();
                    places.Dispose();
                    speeds.Dispose();
                    starts.Dispose();
                    frees.Dispose();

                    // The body as it stands this frame again, for what is shown.
                    Pose(slot, Matrices, CapsuleShapes, Capsules, Chains, ParentSlots, Rests, Hanging, Legs, handle, out _);
                }

                body.Accumulator = math.max(0f, time - steps * step);
                if (body.Sleeping || body.Weight <= 0f)
                {
                    ShowTargets(slot);
                }
                else
                {
                    Show(handle, slot, scale, body);
                }

                for (var index = 0; index < count; index++)
                {
                    TargetStates[first + index] = now[index];
                }

                for (var index = 0; index < Layout.Slots; index++)
                {
                    PreviousMatrices[slot.InputStart + index] = Matrices[slot.InputStart + index];
                }

                now.Dispose();
                SpringBodies[handle] = body;
            }

            // Starts a body's springs on its target, at rest against its anchors.
            private void Start(int handle, in ClothBodySlot slot)
            {
                var first = slot.ParticleStart;
                var count = ClothPose.ParticleCount(slot.ChainStart, slot.ChainCount, Chains);
                var now = new NativeArray<float3>(count, Allocator.Temp);
                ToAnchors(slot, Matrices, slot.InputStart, Targets, now, first);
                for (var index = 0; index < count; index++)
                {
                    States[first + index] = now[index];
                    PreviousStates[first + index] = now[index];
                    TargetStates[first + index] = now[index];
                    Velocities[first + index] = float3.zero;
                }

                now.Dispose();
                for (var index = 0; index < Layout.Slots; index++)
                {
                    PreviousMatrices[slot.InputStart + index] = Matrices[slot.InputStart + index];
                }

                for (var anchor = 0; anchor < ClothSprings.Anchors; anchor++)
                {
                    Anchors[handle * ClothSprings.Anchors + anchor] = default;
                }

                MoveAnchors(handle, slot, false);
            }

            // Moves the anchors of a body's kinds of chain to where they are this frame.
            private void MoveAnchors(int handle, in ClothBodySlot slot, bool jumped)
            {
                var moved = 0;
                for (var chainIndex = 0; chainIndex < slot.ChainCount; chainIndex++)
                {
                    var springChain = SpringChains[slot.ChainStart + chainIndex];
                    if ((moved & (1 << springChain.Anchor)) != 0)
                    {
                        continue;
                    }

                    moved |= 1 << springChain.Anchor;
                    var matrix = Matrices[slot.InputStart + springChain.AnchorSlot];
                    var index = handle * ClothSprings.Anchors + springChain.Anchor;
                    var anchor = Anchors[index];
                    if (jumped)
                    {
                        ClothSprings.Carry(ref anchor, matrix.c3.xyz, RotationOf(matrix));
                    }
                    else
                    {
                        ClothSprings.Move(ref anchor, matrix.c3.xyz, RotationOf(matrix), DeltaTime);
                    }

                    Anchors[index] = anchor;
                }
            }

            // Moves a body's bones of last frame with the body's own move since, as if the body had
            // stood where it stands now.
            private void Rebase(in ClothBodySlot slot)
            {
                var root = slot.InputStart + (slot.RootSlot >= 0 ? slot.RootSlot : 0);
                var move = math.mul(Matrices[root], math.inverse(PreviousMatrices[root]));
                for (var index = 0; index < Layout.Slots; index++)
                {
                    PreviousMatrices[slot.InputStart + index] = math.mul(move, PreviousMatrices[slot.InputStart + index]);
                }
            }

            // Whether the body or an anchor of its chains jumped since the last frame.
            private bool JumpedSince(in ClothBodySlot slot)
            {
                if (slot.RootSlot >= 0 && Jumped(slot.InputStart + slot.RootSlot))
                {
                    return true;
                }

                for (var chainIndex = 0; chainIndex < slot.ChainCount; chainIndex++)
                {
                    if (Jumped(slot.InputStart + SpringChains[slot.ChainStart + chainIndex].AnchorSlot))
                    {
                        return true;
                    }
                }

                return false;
            }

            private bool Jumped(int input)
            {
                var from = PreviousMatrices[input];
                var to = Matrices[input];
                return ClothSprings.Jumped(from.c3.xyz, RotationOf(from), to.c3.xyz, RotationOf(to), DeltaTime);
            }

            // The bones a body reads, between where they were last frame and where they are, by a
            // share: the body itself, and every other bone as it lies on the body.
            private void Between(in ClothBodySlot slot, float share, NativeArray<float4x4> into)
            {
                var root = slot.RootSlot >= 0 ? slot.RootSlot : 0;
                var rootFrom = PreviousMatrices[slot.InputStart + root];
                var rootTo = Matrices[slot.InputStart + root];
                var rootBetween = Blend(rootFrom, rootTo, share);
                var fromInverse = math.inverse(rootFrom);
                var toInverse = math.inverse(rootTo);
                for (var index = 0; index < Layout.Slots; index++)
                {
                    into[index] = index == root
                        ? rootBetween
                        : math.mul(rootBetween, Blend(math.mul(fromInverse, PreviousMatrices[slot.InputStart + index]),
                            math.mul(toInverse, Matrices[slot.InputStart + index]), share));
                }
            }

            // One step of a body's springs: every particle springs toward its target under what it
            // feels of its anchor's motion, the constraints are held, and the body pushes the cloth
            // out no nearer than the target, which also takes the speed into the body out of the
            // particles it moved.
            private void StepSprings(int handle, in ClothBodySlot slot, float scale, float share, ref ClothSpringBody body,
                NativeArray<float4x4> stepMatrices, NativeArray<float3> stepTargets, NativeArray<float3> places, NativeArray<float3> speeds,
                NativeArray<float3> starts, NativeArray<float3> frees)
            {
                var step = ClothSprings.StepSeconds;
                var first = slot.ParticleStart;
                var count = places.Length;
                for (var index = 0; index < count; index++)
                {
                    places[index] = States[first + index];
                    speeds[index] = Velocities[first + index];
                    starts[index] = places[index];
                }

                for (var chainIndex = 0; chainIndex < slot.ChainCount; chainIndex++)
                {
                    var chain = Chains[slot.ChainStart + chainIndex];
                    var springChain = SpringChains[slot.ChainStart + chainIndex];
                    var spring = springChain.Spring;
                    var anchor = ClothSprings.Between(Anchors[handle * ClothSprings.Anchors + springChain.Anchor], share);
                    var rotation = RotationOf(stepMatrices[springChain.AnchorSlot]);
                    var start = chain.ParticleStart - first;
                    places[start] = stepTargets[start];
                    speeds[start] = float3.zero;
                    for (var index = 1; index <= chain.BoneCount; index++)
                    {
                        var place = places[start + index];
                        var speed = speeds[start + index];
                        var felt = ClothSprings.Felt(spring, anchor, rotation, springChain.Up, place, speed, index / (float)chain.BoneCount);
                        ClothSprings.Spring(ref place, ref speed, stepTargets[start + index], felt,
                            ClothSprings.Along(spring.FrequencyRoot, spring.FrequencyTip, index - 1, chain.BoneCount),
                            ClothSprings.Along(spring.DampingRoot, spring.DampingTip, index - 1, chain.BoneCount), step);
                        places[start + index] = place;
                        speeds[start + index] = speed;
                    }
                }

                for (var pass = 0; pass < ClothSprings.Passes; pass++)
                {
                    // Every chain is linked to the one on its right, which closes a skirt's ring.
                    for (var chainIndex = 0; chainIndex < slot.ChainCount; chainIndex++)
                    {
                        var chain = Chains[slot.ChainStart + chainIndex];
                        if (chain.Right >= 0)
                        {
                            var other = Chains[slot.ChainStart + chain.Right];
                            ClothSprings.Link(places, stepTargets, chain.ParticleStart - first, other.ParticleStart - first,
                                math.min(chain.BoneCount, other.BoneCount) + 1);
                        }
                    }

                    for (var chainIndex = 0; chainIndex < slot.ChainCount; chainIndex++)
                    {
                        var chain = Chains[slot.ChainStart + chainIndex];
                        var springChain = SpringChains[slot.ChainStart + chainIndex];
                        var start = chain.ParticleStart - first;
                        var left = chain.Left >= 0 ? Chains[slot.ChainStart + chain.Left] : default;
                        var right = chain.Right >= 0 ? Chains[slot.ChainStart + chain.Right] : default;
                        ClothSprings.LimitTwist(places, stepTargets, start, chain.BoneCount, springChain.Up, springChain.Spring.Twist,
                            chain.Left >= 0 ? left.ParticleStart - first : -1, left.BoneCount,
                            chain.Right >= 0 ? right.ParticleStart - first : -1, right.BoneCount);
                        ClothSprings.HoldBones(places, stepTargets, start, chain.BoneCount, springChain.Spring.ConeRoot,
                            springChain.Spring.ConeTip);
                    }
                }

                // The speed limit, and a chain with a broken value starts again on its target.
                var farthest = 0f;
                for (var chainIndex = 0; chainIndex < slot.ChainCount; chainIndex++)
                {
                    var chain = Chains[slot.ChainStart + chainIndex];
                    var start = chain.ParticleStart - first;
                    var broken = false;
                    for (var index = 0; index <= chain.BoneCount; index++)
                    {
                        var place = starts[start + index] + ClothSprings.ClampLength(places[start + index] - starts[start + index],
                            ClothSprings.MaxSpeed * step);
                        places[start + index] = place;
                        broken |= !ClothSprings.IsFinite(place) || !ClothSprings.IsFinite(speeds[start + index]);
                    }

                    for (var index = 0; index <= chain.BoneCount; index++)
                    {
                        if (broken)
                        {
                            places[start + index] = stepTargets[start + index];
                            starts[start + index] = stepTargets[start + index];
                            speeds[start + index] = float3.zero;
                        }

                        farthest = math.max(farthest, math.distance(places[start + index], stepTargets[start + index]));
                    }
                }

                // Sleeping cloth wakes only when a step would move it off its target.
                var sleepDistance = ClothSprings.SleepDistance * scale;
                if (body.Sleeping && farthest < sleepDistance)
                {
                    for (var index = 0; index < count; index++)
                    {
                        PreviousStates[first + index] = States[first + index];
                        States[first + index] = stepTargets[index];
                        Velocities[first + index] = float3.zero;
                    }

                    return;
                }

                body.Sleeping = false;

                // The body pushes the cloth out last, no nearer than the target. The cloth goes where
                // it is pushed, its speed into the body goes, it slides with a little friction, and it
                // takes only a little of the push's speed, so a jolt of a clip does not fling it.
                for (var index = 0; index < count; index++)
                {
                    frees[index] = places[index];
                }

                for (var chainIndex = 0; chainIndex < slot.ChainCount; chainIndex++)
                {
                    var chain = Chains[slot.ChainStart + chainIndex];
                    var rotation = RotationOf(stepMatrices[SpringChains[slot.ChainStart + chainIndex].AnchorSlot]);
                    ClothSprings.Floor(places, stepTargets, chain.ParticleStart - first, chain.BoneCount,
                        math.mul(math.conjugate(rotation), OutwardOf(handle, slot, chainIndex)));
                }

                // A cloak over a robe is pushed out of the robe's swinging skirt in every step, so the
                // pushes add up even where one takes it out only in part.
                if (Layered(slot))
                {
                    KeepOutOfSkirt(handle, slot, scale, stepMatrices, places);
                }

                farthest = 0f;
                for (var index = 0; index < count; index++)
                {
                    var speed = (frees[index] - starts[index]) / step;
                    var push = places[index] - frees[index];
                    var pushed = math.length(push);
                    if (pushed > 1e-6f)
                    {
                        var normal = push / pushed;
                        var into = math.dot(speed, normal);
                        if (into < 0f)
                        {
                            speed -= normal * into;
                        }

                        speed -= (speed - normal * math.dot(speed, normal)) * ClothSprings.Friction;
                        speed += normal * math.min(pushed / step, ClothSprings.PushSpeed);
                    }

                    speeds[index] = ClothSprings.ClampLength(speed, ClothSprings.MaxSpeed);
                    farthest = math.max(farthest, math.distance(places[index], stepTargets[index]));
                }

                for (var chainIndex = 0; chainIndex < slot.ChainCount; chainIndex++)
                {
                    speeds[Chains[slot.ChainStart + chainIndex].ParticleStart - first] = float3.zero;
                }

                body.SleepTime = farthest < sleepDistance ? body.SleepTime + step : 0f;
                body.Sleeping = body.SleepTime >= ClothSprings.SleepSeconds;
                for (var index = 0; index < count; index++)
                {
                    PreviousStates[first + index] = States[first + index];
                    States[first + index] = body.Sleeping ? stepTargets[index] : places[index];
                    Velocities[first + index] = body.Sleeping ? float3.zero : speeds[index];
                }
            }

            // Whether a body wears a cloak with its cloth's reach over a skirt with its own.
            private bool Layered(in ClothBodySlot slot)
            {
                var skirt = false;
                var cloak = false;
                for (var chainIndex = 0; chainIndex < slot.ChainCount; chainIndex++)
                {
                    var chain = Chains[slot.ChainStart + chainIndex];
                    skirt |= chain.Kind == ClothKind.Skirt && chain.ReachKnown;
                    cloak |= chain.Kind == ClothKind.Cloak && chain.ReachKnown;
                }

                return skirt && cloak;
            }

            // Lays a body's sprung chains, in the frames of their anchors as the body stands at a
            // step, out of the outer side of its skirt alone, and takes them back.
            private void KeepOutOfSkirt(int handle, in ClothBodySlot slot, float scale, NativeArray<float4x4> stepMatrices,
                NativeArray<float3> places)
            {
                var first = slot.ParticleStart;
                var followed = new NativeArray<float3>(places.Length, Allocator.Temp);
                ToWorld(slot, stepMatrices, 0, places, followed);
                PointAlong(slot, followed, first);
                ClothPose.Collide(slot.ChainStart, slot.ChainCount, slot.BoneStart, slot.CapsuleStart, slot.CapsuleCount, slot.MarginStart,
                    scale, Legs[handle], FrameCapsules, Chains, Particles, Capsules, Radii, Margins, Hanging, followed, first, Positions,
                    Directions, ClothRoles.Trunk | ClothRoles.Leg | ClothRoles.Arm, false, Targets);
                for (var chainIndex = 0; chainIndex < slot.ChainCount; chainIndex++)
                {
                    var chain = Chains[slot.ChainStart + chainIndex];
                    var matrix = stepMatrices[SpringChains[slot.ChainStart + chainIndex].AnchorSlot];
                    var inverse = math.conjugate(RotationOf(matrix));
                    for (var index = 0; index <= chain.BoneCount; index++)
                    {
                        places[chain.ParticleStart - first + index] = math.mul(inverse, Positions[chain.ParticleStart + index] - matrix.c3.xyz);
                    }
                }

                followed.Dispose();
            }

            // Shows a body's springs between their last two steps, faded in over its target, no
            // nearer the body than the target, and laid with the target pose's collision as the body
            // stands this frame, which leaves the target pose where it is, so a body in its target
            // pose shows it.
            private void Show(int handle, in ClothBodySlot slot, float scale, in ClothSpringBody body)
            {
                var first = slot.ParticleStart;
                var count = ClothPose.ParticleCount(slot.ChainStart, slot.ChainCount, Chains);
                var shown = new NativeArray<float3>(count, Allocator.Temp);
                var share = math.saturate(body.Accumulator / ClothSprings.StepSeconds);
                for (var index = 0; index < count; index++)
                {
                    shown[index] = math.lerp(PreviousStates[first + index], States[first + index], share);
                }

                var followed = new NativeArray<float3>(count, Allocator.Temp);
                var targets = new NativeArray<float3>(count, Allocator.Temp);
                ToWorld(slot, Matrices, slot.InputStart, shown, followed);
                for (var index = 0; index < count; index++)
                {
                    targets[index] = Targets[first + index];
                    followed[index] = math.lerp(targets[index], followed[index], body.Weight);
                }

                var free = new NativeArray<float3>(count, Allocator.Temp);
                free.CopyFrom(followed);
                for (var chainIndex = 0; chainIndex < slot.ChainCount; chainIndex++)
                {
                    var chain = Chains[slot.ChainStart + chainIndex];
                    ClothSprings.Floor(followed, targets, chain.ParticleStart - first, chain.BoneCount, OutwardOf(handle, slot, chainIndex));
                }

                targets.Dispose();
                PointAlong(slot, followed, first);
                ClothPose.Collide(slot.ChainStart, slot.ChainCount, slot.BoneStart, slot.CapsuleStart, slot.CapsuleCount, slot.MarginStart,
                    scale, Legs[handle], FrameCapsules, Chains, Particles, Capsules, Radii, Margins, Hanging, followed, first, Positions,
                    Directions, SpringSkips, false, Targets);
                MeasurePushes(slot, free, first, Positions);
                free.Dispose();
                shown.Dispose();
                followed.Dispose();
            }

            // The direction of every bone of a body's chains along where they lie, from the body's
            // first particle at an offset; along its target where they fold onto a point.
            private void PointAlong(in ClothBodySlot slot, NativeArray<float3> laid, int first)
            {
                for (var chainIndex = 0; chainIndex < slot.ChainCount; chainIndex++)
                {
                    var chain = Chains[slot.ChainStart + chainIndex];
                    for (var index = 0; index < chain.BoneCount; index++)
                    {
                        var particle = chain.ParticleStart + index;
                        var direction = ClothMath.Normalized(laid[particle + 1 - first] - laid[particle - first]);
                        Directions[chain.BoneStart + index] = math.lengthsq(direction) > 0.5f
                            ? direction
                            : ClothMath.Normalized(Targets[particle + 1] - Targets[particle]);
                    }
                }
            }

            // World places of a body's particles, from the body's first particle at an offset, in the
            // frames of their chains' anchors as given by matrices from an input start.
            private void ToAnchors(in ClothBodySlot slot, NativeArray<float4x4> matrices, int inputStart, NativeArray<float3> world,
                NativeArray<float3> local, int first)
            {
                for (var chainIndex = 0; chainIndex < slot.ChainCount; chainIndex++)
                {
                    var chain = Chains[slot.ChainStart + chainIndex];
                    var matrix = matrices[inputStart + SpringChains[slot.ChainStart + chainIndex].AnchorSlot];
                    var inverse = math.conjugate(RotationOf(matrix));
                    for (var index = 0; index <= chain.BoneCount; index++)
                    {
                        local[chain.ParticleStart + index - first] = math.mul(inverse, world[chain.ParticleStart + index] - matrix.c3.xyz);
                    }
                }
            }

            // The world places of a body's particles from where they lie in the frames of their
            // chains' anchors, both from the body's first particle.
            private void ToWorld(in ClothBodySlot slot, NativeArray<float4x4> matrices, int inputStart, NativeArray<float3> local,
                NativeArray<float3> world)
            {
                var first = slot.ParticleStart;
                for (var chainIndex = 0; chainIndex < slot.ChainCount; chainIndex++)
                {
                    var chain = Chains[slot.ChainStart + chainIndex];
                    var matrix = matrices[inputStart + SpringChains[slot.ChainStart + chainIndex].AnchorSlot];
                    var rotation = RotationOf(matrix);
                    for (var index = 0; index <= chain.BoneCount; index++)
                    {
                        var particle = chain.ParticleStart + index - first;
                        world[particle] = matrix.c3.xyz + math.mul(rotation, local[particle]);
                    }
                }
            }
        }

        // A matrix between two by a share: places and scales taken straight, rotations the shorter
        // way; the second for a matrix that is not set.
        private static float4x4 Blend(float4x4 from, float4x4 to, float share)
        {
            var fromScale = new float3(math.length(from.c0.xyz), math.length(from.c1.xyz), math.length(from.c2.xyz));
            var toScale = new float3(math.length(to.c0.xyz), math.length(to.c1.xyz), math.length(to.c2.xyz));
            if (math.cmin(fromScale) < 1e-6f || math.cmin(toScale) < 1e-6f)
            {
                return to;
            }

            return float4x4.TRS(math.lerp(from.c3.xyz, to.c3.xyz, share), math.slerp(RotationOf(from), RotationOf(to), share),
                math.lerp(fromScale, toScale, share));
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
        // neighbours in the body's chains, its profile's margin, slack, and springs, what it collides
        // with, its bones, and its garment's reach at each particle, or null without the garment.
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
            public ClothSpring Spring;
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

    // How a body's cloth moves this frame: laid in its target pose, swung by springs on it, put back
    // to rest, or left as it is while it is out of view.
    public enum ClothDetail : byte
    {
        Pose,
        Spring,
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
