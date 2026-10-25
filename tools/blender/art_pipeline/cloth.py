"""The cloth chains in a pose as the runtime solver lays them at its Follow detail, for the review sheets,
the review files, and the checks of garments with loose parts: the Blender side of cloth_core.py, which
lays them on plain data exactly as ClothKinematics in the Unity client does. See
docs/EQUIPMENT_VISUALS_DESIGN.md and docs/ART_PIPELINE.md.

This measures what the kinematic layer needs from Blender's bodies and garments and hands it to
cloth_core.lay: the capsules around the body's bones, the legs' turns against the hips, every chain as it
hangs at rest from its posed parent, how deep each particle lies in each of the trunk's capsules at rest,
and how far a garment's cloth reaches beyond and within each particle; then it turns every bone to point
at its laid particle. A capsule's radius is the middle distance of the body's vertices that its bone moves
most. Around the trunk it is taken in each of DIRECTIONS directions around the bone, of the vertices along
its segment, with its narrow grooves filled: so a torso that is deep in front of the spine and shallow
behind it keeps a cloak on its back, and the groove along the spine does not catch the cloth and swing it
from side to side. The legs are about round and move under the cloth, which slides around them, so their
capsules are round and push it straight away. Springs, inertia, and gravity are left out: the review
shows where the cloth settles in a pose, so a robe's skirt drapes over bent legs as the game shows it.

How far a garment's cloth reaches beyond and within each particle of its chains is measured from the
garment at rest, as reaches gives it; a chain need not lie in its cloth. The trunk carries the chains,
and no capsule follows its shape exactly, such as the hips' capsule at the small of the back, so against
the capsules of the trunk a particle keeps no more than it has at rest: a body standing still wears its
garments as they were made. The legs move under the cloth, so it keeps its margin from them."""

import math

import bpy
import numpy as np
from mathutils import Matrix, Vector

from . import bodies, cloth_core

# The body bones the cloth collides with, each with the bone its capsule ends at; the head is a
# sphere around its own vertices. The legs' capsules move under the cloth; the others are the trunk's.
CAPSULE_BONES = cloth_core.CAPSULE_BONES
LEG_BONES = ("UpperLeg", "LowerLeg")
FALLBACK_RADIUS = cloth_core.FALLBACK_RADIUS
# A capsule has a radius in this many directions around its bone, from the body's front toward its
# right at rest, with grooves filled this many times over; the head is a sphere with one radius.
DIRECTIONS = cloth_core.DIRECTIONS
GROOVE_PASSES = cloth_core.GROOVE_PASSES
# The chains of each kind by the start of their bones' names.
PREFIXES = {"skirt": "Skirt", "cloak": "Cloak", "hood": "Hood"}
# The chains' angles around the hips, by name, as the skeleton's cloth section gives them.
ANGLES = cloth_core.ANGLES
# Vertices of a garment within this share of a bone's length along its chain lie at one place, such
# as a ring of a skirt.
PLACE_SPAN = cloth_core.PLACE_SPAN
# The order the kinds are laid in, so a kind's cloth is laid before the cloth that lies over it.
ORDER = cloth_core.ORDER


def worn(arm: bpy.types.Object, garments: list[bpy.types.Object]) -> tuple[list[str], dict]:
    """The kinds of chains garments worn together hang on, from the inside out, and the reaches of the
    cloth on each kind, as the runtime solver measures them when the garments go on: of the garment
    whose cloth hangs on the kind, the one with the most vertices that the kind's bones move most, not
    another that only blends into its weights, such as a hood's collar lying on a cloak."""
    kinds, hanging = [], {}
    for obj in garments:
        for kind in kinds_of(obj):
            if kind not in kinds:
                kinds.append(kind)
            count = _hanging_vertices(obj, kind)
            if kind not in hanging or count > hanging[kind][0]:
                hanging[kind] = (count, obj)
    return kinds, {kind: reaches(arm, obj, kind) for kind, (_count, obj) in hanging.items()}


def _hanging_vertices(obj: bpy.types.Object, kind: str) -> int:
    """How many of a garment's vertices the bones of a kind's chains move most."""
    names = [g.name for g in obj.vertex_groups]
    chain = [index for index, name in enumerate(names) if name.startswith(PREFIXES[kind])]
    weights = bodies.weights_of(obj, names)
    moved = weights.max(axis=1) > 0.0
    return int((moved & np.isin(weights.argmax(axis=1), chain)).sum())


def kinds_of(obj: bpy.types.Object) -> list[str]:
    """The kinds of chains a garment version hangs on: those of the bones it has weights on."""
    found = []
    for kind, prefix in PREFIXES.items():
        names = [g.name for g in obj.vertex_groups if g.name.startswith(prefix)]
        if names and (bodies.weights_of(obj, names) > 0.0).any():
            found.append(kind)
    return found


class Capsule:
    """A body bone's capsule, in the armature's space: its segment, and the body's distance from it in
    each direction around it, from the body's front toward its right, or one radius for a sphere."""

    def __init__(self, bone: str, a: Vector, b: Vector, radii: list[float], front: Vector, side: Vector):
        self.bone, self.local_a, self.local_b, self.radii = bone, a, b, radii
        self.local_front, self.local_side = front, side
        self.a, self.b, self.front, self.side = a, b, front, side
        self.trunk = not bone.startswith(LEG_BONES)
        # The middle of the radii, for reports.
        self.radius = float(np.median(radii))

    def place(self, arm: bpy.types.Object) -> None:
        """Puts the capsule where its bone is posed, in the armature's space."""
        matrix = arm.pose.bones[self.bone].matrix @ arm.data.bones[self.bone].matrix_local.inverted()
        self.a, self.b = matrix @ self.local_a, matrix @ self.local_b
        rotation = matrix.to_3x3()
        self.front, self.side = rotation @ self.local_front, rotation @ self.local_side

    def depth_at_rest(self, point: Vector) -> float:
        """How far a point at rest lies inside the capsule at rest, its radius toward the point blended
        between the two directions around it that were measured."""
        away = point - _closest(point, self.local_a, self.local_b)
        if len(self.radii) == 1:
            return self.radii[0] - away.length
        angle = math.atan2(away.dot(self.local_side), away.dot(self.local_front))
        position = (angle + math.pi) / (2.0 * math.pi) * len(self.radii) - 0.5
        lower = math.floor(position)
        share = position - lower
        radius = self.radii[lower % len(self.radii)] * (1.0 - share) + self.radii[(lower + 1) % len(self.radii)] * share
        return radius - away.length

    def case(self) -> dict:
        """The capsule as posed, as cloth_core takes it; a point on its axis leaves along the armature's
        Y axis."""
        return {"bone": self.bone, "a": _plain(self.a), "b": _plain(self.b), "front": _plain(self.front),
                "side": _plain(self.side), "axisNormal": {"x": 0.0, "y": 1.0, "z": 0.0}, "scale": 1.0,
                "radii": list(self.radii)}


def _closest(point: Vector, a: Vector, b: Vector) -> Vector:
    axis = b - a
    squared = axis.length_squared
    if squared < 1e-12:
        return a.copy()
    t = min(max((point - a).dot(axis) / squared, 0.0), 1.0)
    return a + axis * t


def _median(values: np.ndarray) -> float:
    return float(np.sort(values)[len(values) // 2])


def _filled(radii: list[float | None], fallback: float) -> list[float]:
    """The radii with every direction that no vertex lies in taken between the nearest ones that some
    do, around the bone."""
    known = [i for i, r in enumerate(radii) if r is not None]
    if not known:
        return [fallback] * len(radii)
    count = len(radii)
    found = []
    for i, r in enumerate(radii):
        if r is not None:
            found.append(r)
            continue
        before = min(known, key=lambda k: (i - k) % count)
        after = min(known, key=lambda k: (k - i) % count)
        gap = (after - before) % count or count
        share = ((i - before) % count) / gap
        found.append(radii[before] * (1.0 - share) + radii[after] * share)
    return found


def _without_grooves(radii: list[float]) -> list[float]:
    """The radii with every narrow groove between two directions filled, such as the one along the
    spine, which would catch cloth and swing it from side to side: a radius smaller than the middle of
    its neighbors' takes that middle, GROOVE_PASSES times over."""
    count = len(radii)
    for _ in range(GROOVE_PASSES):
        radii = [max(r, (radii[k - 1] + radii[(k + 1) % count]) / 2.0) for k, r in enumerate(radii)]
    return radii


def capsules(arm: bpy.types.Object, body: bpy.types.Object) -> list[Capsule]:
    """The capsules of a body at rest, in the armature's space: around each bone's segment, the
    median distance of the body vertices whose largest weight is the bone's; around the trunk, of
    those along the segment, in each of DIRECTIONS directions around it from the body's front toward
    its right, with its narrow grooves filled, so the capsule takes the shape of the body around the
    bone; round around a leg; and a sphere for the head."""
    names = [b.name for b in arm.data.bones if b.use_deform]
    weights = bodies.weights_of(body, names)
    co = bodies.world_positions(body) @ np.array(arm.matrix_world.inverted())[:3, :3].T + \
        np.array(arm.matrix_world.inverted())[:3, 3]
    dominant = np.where(weights.max(axis=1) > 0.0, weights.argmax(axis=1), -1)
    forward, right = Vector((0.0, 1.0, 0.0)), _level_right(arm)
    found = []
    for bone, end in CAPSULE_BONES:
        if bone not in arm.data.bones:
            continue
        index = names.index(bone)
        points = co[dominant == index]
        head = arm.data.bones[bone].head_local
        if end is None:
            middle = Vector(points.mean(axis=0)) if len(points) else head.copy()
            distances = np.linalg.norm(points - np.array(middle), axis=1)
            radius = _median(distances) if len(distances) else FALLBACK_RADIUS
            found.append(Capsule(bone, middle, middle.copy(), [radius], forward, right))
            continue
        a, b = head.copy(), arm.data.bones[end].head_local.copy()
        if bone.startswith(LEG_BONES):
            distances = np.array([(Vector(p) - _closest(Vector(p), a, b)).length for p in points])
            found.append(Capsule(bone, a, b, [_median(distances) if len(distances) else FALLBACK_RADIUS], forward, right))
            continue
        axis = (b - a).normalized()
        front = (forward - axis * forward.dot(axis)).normalized()
        side = (right - axis * right.dot(axis) - front * right.dot(front)).normalized()
        # Only the vertices along the segment, so the buttocks below the hips' bone do not widen its
        # capsule at the small of the back.
        along = (points - np.array(a)) @ np.array(b - a) / (b - a).length_squared if len(points) else np.empty(0)
        points = points[(along > 0.0) & (along < 1.0)]
        offsets = [Vector(p) - _closest(Vector(p), a, b) for p in points]
        distances = np.array([o.length for o in offsets])
        angles = np.array([math.atan2(o.dot(side), o.dot(front)) for o in offsets])
        bins = np.floor((angles + math.pi) / (2.0 * math.pi) * DIRECTIONS).astype(int) % DIRECTIONS
        radii = [_median(distances[bins == k]) if (bins == k).any() else None for k in range(DIRECTIONS)]
        fallback = _median(distances) if len(distances) else FALLBACK_RADIUS
        found.append(Capsule(bone, a, b, _without_grooves(_filled(radii, fallback)), front, side))
    return found


def _chains(arm: bpy.types.Object, kind: str) -> list[list[str]]:
    prefix = PREFIXES[kind]
    found = []
    for bone in arm.data.bones:
        if bone.name.startswith(prefix) and (bone.parent is None or not bone.parent.name.startswith(prefix)):
            names, current = [], bone
            while current is not None:
                names.append(current.name)
                current = next((c for c in current.children if c.name.startswith(prefix)), None)
            found.append(names)
    return found


def _plain(value: Vector) -> dict:
    return {"x": float(value.x), "y": float(value.y), "z": float(value.z)}


def _legs(arm: bpy.types.Object) -> dict:
    """How each part of each leg turns against the hips, from where it would point if it kept its rest
    pose under the posed hips to where it points, the hips' forward and right axes and where they are,
    and how far the right knee lies ahead of the left, as cloth_core takes them."""
    hips = arm.pose.bones["Hips"]
    carried = hips.matrix.to_3x3() @ hips.bone.matrix_local.to_3x3().inverted()
    along = Vector((0.0, 1.0, 0.0))
    turns = []
    for side in ("L", "R"):
        for part in ("UpperLeg", "LowerLeg"):
            name = f"{part}_{side}"
            rest = carried @ (arm.data.bones[name].matrix_local.to_3x3() @ along)
            turn = rest.rotation_difference(arm.pose.bones[name].matrix.to_3x3() @ along)
            turns.append({"x": turn.x, "y": turn.y, "z": turn.z, "w": turn.w})
    forward = carried @ Vector((0.0, 1.0, 0.0))
    ahead = (arm.pose.bones["LowerLeg_R"].head - arm.pose.bones["LowerLeg_L"].head).dot(forward)
    return {"hasLegs": True, "hips": _plain(hips.head), "forward": _plain(forward),
            "right": _plain(carried @ _level_right(arm)), "ahead": float(ahead), "legTurns": turns}


def _segment_of(arm: bpy.types.Object, names: list[str], index: int) -> tuple[Vector, Vector]:
    """A chain bone's segment at rest, in the armature's space: from its head to the next particle."""
    bone = arm.data.bones[names[index]]
    end = arm.data.bones[names[index + 1]].head_local if index + 1 < len(names) else bone.tail_local
    return bone.head_local.copy(), end.copy()


def _outward(root: str, forward: Vector, right: Vector) -> Vector:
    """The way a chain faces from the hips, by its angle around them."""
    base = root.rstrip("_LR").rstrip("0123456789")
    angle = math.radians(ANGLES[base] * (-1.0 if root.endswith("_L") else 1.0))
    return forward * math.cos(angle) + right * math.sin(angle)


def _level_right(arm: bpy.types.Object) -> Vector:
    across = arm.data.bones["UpperLeg_R"].head_local - arm.data.bones["UpperLeg_L"].head_local
    return Vector((across.x, across.y, 0.0)).normalized()


def reaches(arm: bpy.types.Object, obj: bpy.types.Object, kind: str) -> dict[str, list[tuple[float, float] | None]]:
    """How far a garment's cloth reaches beyond each particle of its chains of a kind, the way the
    chain faces from the hips, and how far within it, at rest, per chain by its first bone. Every
    vertex that a bone of the chain moves most lies at a place along the chain, the bone's number and
    its share of the bone's length, and beyond or within the bone by some distance; the vertices that
    lie at one place, such as a ring of a skirt, give the farthest out and the farthest in there, and
    a particle takes them as they run between the places on either side of it. A skirt widens down
    its chains, so the reach changes from joint to joint. The second is negative where all the cloth
    lies beyond the chain, and a particle farther than half a bone from any cloth has None.
    ClothChainSolver measures the same from the garment's model."""
    names = [g.name for g in obj.vertex_groups]
    weights = bodies.weights_of(obj, names)
    inverse = np.array(arm.matrix_world.inverted())
    co = bodies.world_positions(obj) @ inverse[:3, :3].T + inverse[:3, 3]
    dominant = np.where(weights.max(axis=1) > 0.0, weights.argmax(axis=1), -1)
    forward, right = Vector((0.0, 1.0, 0.0)), _level_right(arm)
    found = {}
    for chain in _chains(arm, kind):
        outward = np.array(_outward(chain[0], forward, right))
        places, beyond = [], []
        for index, name in enumerate(chain):
            members = co[dominant == names.index(name)] if name in names else np.empty((0, 3))
            if not len(members):
                continue
            a, b = (np.array(p) for p in _segment_of(arm, chain, index))
            axis = b - a
            t = (members - a) @ axis / max(float(axis @ axis), 1e-12)
            closest = a + np.clip(t, 0.0, 1.0)[:, None] * axis
            places.extend(index + t)
            beyond.extend((members - closest) @ outward)
        found[chain[0]] = _along_chain(np.array(places), np.array(beyond), len(chain) + 1)
    return found


def _along_chain(places: np.ndarray, beyond: np.ndarray, particles: int) -> list[tuple[float, float] | None]:
    """The farthest out and in at every particle, from the vertices at their places along a chain:
    vertices within PLACE_SPAN of each other lie at one place, and a particle takes the places on
    either side of it, linearly, or the nearest within half a bone."""
    if not len(places):
        return [None] * particles
    order = np.argsort(places)
    places, beyond = places[order], beyond[order]
    rings = []
    start = 0
    for index in range(1, len(places) + 1):
        if index == len(places) or places[index] - places[start] > PLACE_SPAN:
            rings.append((float(places[start:index].mean()), float(beyond[start:index].max()),
                          float(-beyond[start:index].min())))
            start = index
    found = []
    for particle in range(particles):
        if particle < rings[0][0] - 0.5 or particle > rings[-1][0] + 0.5:
            found.append(None)
            continue
        if particle <= rings[0][0]:
            found.append(rings[0][1:])
            continue
        if particle >= rings[-1][0]:
            found.append(rings[-1][1:])
            continue
        after = next(i for i, ring in enumerate(rings) if ring[0] >= particle)
        (p0, out0, in0), (p1, out1, in1) = rings[after - 1], rings[after]
        share = (particle - p0) / (p1 - p0) if p1 > p0 else 0.0
        found.append((out0 + (out1 - out0) * share, in0 + (in1 - in0) * share))
    return found


def follow(arm: bpy.types.Object, kinds: list[str], found: list[Capsule],
           reach: dict[str, dict[str, list[tuple[float, float] | None]]] | None = None) -> None:
    """Lays the chains of the kinds over the armature's current pose as cloth_core lays them: each hangs
    from its animated parent, follows the legs, is pushed out of the capsules, and its bones turn to
    point at its particles. With the reaches of the garments on them, a kind's chains also stay outside
    the envelope of the cloth that it lies over."""
    if not kinds:
        return
    bpy.context.view_layer.update()
    for capsule in found:
        capsule.place(arm)
    reach = reach or {}
    case = {"name": arm.name, "scale": 1.0, "up": {"x": 0.0, "y": 0.0, "z": 1.0}, "hasLegs": False,
            "capsules": [capsule.case() for capsule in found], "chains": []}
    if any(cloth_core.KINDS[kind]["beside"] for kind in kinds):
        case.update(_legs(arm))
    laid_chains = []
    for kind in sorted(kinds, key=ORDER.index):
        for names in _chains(arm, kind):
            parent = arm.data.bones[names[0]].parent
            parent_matrix = arm.pose.bones[parent.name].matrix.copy() if parent else Matrix.Identity(4)
            parent_rest = parent.matrix_local if parent else Matrix.Identity(4)
            # Every bone at rest under the posed parent, and the particles at its joints and its tip.
            matrices, matrix, rest_parent = [], parent_matrix, parent_rest
            for name in names:
                bone = arm.data.bones[name]
                matrix = matrix @ rest_parent.inverted() @ bone.matrix_local
                matrices.append(matrix)
                rest_parent = bone.matrix_local
            lengths = [arm.data.bones[n].length for n in names]
            hanging = [m.translation.copy() for m in matrices]
            hanging.append(matrices[-1] @ Vector((0.0, lengths[-1], 0.0)))
            rest = [arm.data.bones[n].head_local.copy() for n in names] + [arm.data.bones[names[-1]].tail_local.copy()]
            chain_reach = reach[kind].get(names[0]) if kind in reach else None
            base = names[0].rstrip("_LR").rstrip("0123456789")
            case["chains"].append({
                "root": names[0], "kind": kind,
                "angle": ANGLES[base] * (-1.0 if names[0].endswith("_L") else 1.0),
                "beside": list(cloth_core.KINDS[kind]["beside"]),
                "margin": cloth_core.KINDS[kind]["margin"], "slack": cloth_core.KINDS[kind]["slack"],
                "hanging": [_plain(point) for point in hanging], "lengths": lengths,
                # How deep each particle lies in each of the trunk's capsules at rest; no limit from the legs.
                "restDepths": [capsule.depth_at_rest(point) if capsule.trunk else 0.0 for point in rest for capsule in found],
                "restDepthKnown": [capsule.trunk for _point in rest for capsule in found],
                "reachKnown": chain_reach is not None,
                "hasReach": [r is not None for r in chain_reach] if chain_reach is not None else [],
                "reach": [{"x": r[0], "y": r[1]} if r is not None else {"x": 0.0, "y": 0.0} for r in chain_reach]
                if chain_reach is not None else [],
            })
            laid_chains.append((names, parent_matrix, parent_rest))
    for (names, parent_matrix, parent_rest), positions in zip(laid_chains, cloth_core.lay(case)):
        positions = [Vector(point) for point in positions]
        # Turn every bone, from the root, to point at its next particle.
        matrix, rest_parent = parent_matrix, parent_rest
        for index, name in enumerate(names):
            bone = arm.data.bones[name]
            at_rest = matrix @ rest_parent.inverted() @ bone.matrix_local
            current = at_rest.to_3x3() @ Vector((0.0, 1.0, 0.0))
            wanted = positions[index + 1] - at_rest.translation
            turned = at_rest
            if current.length > 1e-9 and wanted.length > 1e-9:
                rotation = current.rotation_difference(wanted).to_matrix().to_4x4()
                turned = Matrix.Translation(at_rest.translation) @ rotation @ \
                    Matrix.Translation(-at_rest.translation) @ at_rest
            pose_bone = arm.pose.bones[name]
            pose_bone.rotation_mode = "QUATERNION"
            pose_bone.matrix_basis = at_rest.inverted() @ turned
            matrix, rest_parent = turned, bone.matrix_local
    bpy.context.view_layer.update()
