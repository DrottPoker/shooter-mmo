"""The cloth chains in a pose as the runtime solver lays them at its Follow detail, for the review sheets,
the review files, and the checks of garments with loose parts. See docs/EQUIPMENT_VISUALS_DESIGN.md.

ClothChainSolver in the Unity client is the source, and this follows it step by step: every chain
hangs from its animated parent with its bones at rest, and skirt and cloak chains follow the legs:
each bone beside a thigh or a shin turns as that part of the leg turns against the hips, but only as
far as the leg pushes the cloth outward, the way the chain faces from the hips, so a skirt lies over
raised thighs and a cloak is pushed back by a leg that kicks back, while both hang down over a leg
that moves away from them. The front chains follow the leg in front, the back chains and the cloak
the leg behind, and the side chains the leg on their side, blended by their angle around the hips.
Then every
particle, a joint or the tip, is pushed out of capsules around the body's bones at its bone's length
from the one before, with passes from the tip when it cannot leave a capsule that way, and every bone
turns to point at its next particle. A capsule's radius is the middle distance of the body's
vertices that its bone moves most. Around the trunk it is taken in each of DIRECTIONS directions
around the bone, of the vertices along its segment, with its narrow grooves filled: so a torso that
is deep in front of the spine and shallow behind it keeps a cloak on its back, and the groove along
the spine does not catch the cloth and swing it from side to side. The legs are about round and move
under the cloth, which slides around them, so their capsules are round and push it straight away. Springs, inertia, and gravity are left out: the review shows where
the cloth settles in a pose, so a robe's skirt drapes over bent legs as the game shows it.

How far a garment's cloth reaches beyond and within each particle of its chains is measured from the
garment at rest, as reaches gives it; a chain need not lie in its cloth. With the reaches, the
margin a particle keeps from the body is the margin its cloth keeps: the profile's margin plus how
far the cloth lies within the particle. The trunk carries the chains, and no capsule follows its
shape exactly, such as the hips' capsule at the small of the back, so against the capsules of the
trunk a particle keeps no more than it has at rest and may lie as deep inside one as it does there:
a body standing still wears its garments as they were made. The legs move under the cloth, so it
keeps its margin from them. A cloak worn over a robe lies over its skirt: the skirt
chains are laid first, and every cloak particle with cloth is kept outside the skirt's cloth, its
envelope, by a gap and by how far the cloak's own cloth lies within it."""

import math

import bpy
import numpy as np
from mathutils import Matrix, Vector

from . import bodies

# The body bones the cloth collides with, each with the bone its capsule ends at; the head is a
# sphere around its own vertices. The legs' capsules move under the cloth; the others are the trunk's.
CAPSULE_BONES = (("Hips", "Spine"), ("Spine", "Chest"), ("Chest", "UpperChest"), ("UpperChest", "Neck"),
                 ("Neck", "Head"), ("Head", None), ("UpperLeg_L", "LowerLeg_L"), ("LowerLeg_L", "Foot_L"),
                 ("UpperLeg_R", "LowerLeg_R"), ("LowerLeg_R", "Foot_R"))
LEG_BONES = ("UpperLeg", "LowerLeg")
RESOLVE_ITERATIONS = 8
MINIMUM_SLOPE = 0.2
CHAIN_PASSES = 3
FALLBACK_RADIUS = 0.05
# A capsule has a radius in this many directions around its bone, from the body's front toward its
# right at rest, with grooves filled this many times over; the head is a sphere with one radius.
DIRECTIONS = 16
GROOVE_PASSES = 2
# The chains of each kind by the start of their bones' names, and the margin and the slack of its
# cloth profile, as ClothProfile in the Unity client: how far a leg turns a bone's hanging direction
# outward, in the outward share of a unit direction, before the cloth follows it, so a breathing
# body leaves the cloth at rest; 0.1 is about 6 degrees of a leg's turn.
PREFIXES = {"skirt": "Skirt", "cloak": "Cloak", "hood": "Hood"}
MARGINS = {"skirt": 0.012, "cloak": 0.02, "hood": 0.01}
SLACKS = {"skirt": 0.1, "cloak": 0.05, "hood": 0.0}
# The chains' angles around the hips from the front toward the right, in degrees, by name; a chain on
# the left takes the negative angle. As ClothChains in the Unity client.
ANGLES = {"SkirtFront": 0.0, "SkirtFrontSide": 45.0, "SkirtSide": 90.0, "SkirtBackSide": 135.0, "SkirtBack": 180.0,
          "CloakCenter": 180.0, "CloakInner": 157.5, "CloakOuter": 135.0}
# The part of the legs each bone of a chain lies beside, from its root, as the rig places the chains:
# a skirt's from the hips to the ankles, a cloak's from the shoulder blades to the middle of the calves.
BESIDE = {"skirt": ("UpperLeg", "UpperLeg", "LowerLeg", "LowerLeg"), "cloak": (None, None, "UpperLeg", "UpperLeg", "LowerLeg")}
# A knee this much farther forward than the other, in meters, makes its leg the one in front.
AHEAD_BLEND = 0.05
# A bone follows its part of the leg fully this much beyond its profile's slack.
FOLLOW_REACH = 0.3
# The kinds whose cloth lies over another kind's, and the gap kept between the two cloths, in meters.
OVER = {"cloak": "skirt"}
LAYER_GAP = 0.01
# Vertices of a garment within this share of a bone's length along its chain lie at one place, such
# as a ring of a skirt.
PLACE_SPAN = 0.05
# The order the kinds are laid in, so a kind's cloth is laid before the cloth that lies over it.
ORDER = ("skirt", "cloak", "hood")


def worn(arm: bpy.types.Object, garments: list[bpy.types.Object]) -> tuple[list[str], dict]:
    """The kinds of chains garments worn together hang on, from the inside out, and the reaches of the
    cloth on each kind, of the outermost garment on it, as the runtime solver measures them when the
    garments go on."""
    kinds, reach = [], {}
    for obj in garments:
        for kind in kinds_of(obj):
            if kind not in kinds:
                kinds.append(kind)
            reach[kind] = reaches(arm, obj, kind)
    return kinds, reach


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

    def radius_toward(self, away: Vector, at_rest: bool = False) -> float:
        """The radius toward a direction from the capsule's axis, posed or at rest, blended between the
        two directions around it that were measured."""
        if len(self.radii) == 1:
            return self.radii[0]
        front, side = (self.local_front, self.local_side) if at_rest else (self.front, self.side)
        angle = math.atan2(away.dot(side), away.dot(front))
        position = (angle + math.pi) / (2.0 * math.pi) * len(self.radii) - 0.5
        lower = math.floor(position)
        share = position - lower
        return self.radii[lower % len(self.radii)] * (1.0 - share) + self.radii[(lower + 1) % len(self.radii)] * share

    def penetration(self, point: Vector, margin: float) -> tuple[float, Vector]:
        closest = _closest(point, self.a, self.b)
        away = point - closest
        length = away.length
        normal = away / length if length > 1e-6 else Vector((0.0, 1.0, 0.0))
        return self.radius_toward(away) + margin - length, normal

    def depth_at_rest(self, point: Vector) -> float:
        """How far a point at rest lies inside the capsule at rest."""
        away = point - _closest(point, self.local_a, self.local_b)
        return self.radius_toward(away, at_rest=True) - away.length


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


class Legs:
    """How each part of each leg turns against the hips, from where it would point if it kept its rest
    pose under the posed hips to where it points; the hips' forward and right axes; and how far the
    right knee lies ahead of the left."""

    def __init__(self, arm: bpy.types.Object):
        hips = arm.pose.bones["Hips"]
        carried = hips.matrix.to_3x3() @ hips.bone.matrix_local.to_3x3().inverted()
        along = Vector((0.0, 1.0, 0.0))
        self.turns = {}
        for side in ("L", "R"):
            for part in ("UpperLeg", "LowerLeg"):
                name = f"{part}_{side}"
                rest = carried @ (arm.data.bones[name].matrix_local.to_3x3() @ along)
                self.turns[(part, side)] = rest.rotation_difference(arm.pose.bones[name].matrix.to_3x3() @ along)
        self.forward = carried @ Vector((0.0, 1.0, 0.0))
        self.right = carried @ _level_right(arm)
        self.ahead = (arm.pose.bones["LowerLeg_R"].head - arm.pose.bones["LowerLeg_L"].head).dot(self.forward)

    def right_share(self, angle: float) -> float:
        """How much a chain at an angle around the hips, from the front toward the right, follows the
        right leg rather than the left: a side chain its own leg, a front chain the leg in front, and a
        back chain the leg behind, blended between."""
        side, front = math.sin(angle), math.cos(angle)
        right_ahead = min(max(0.5 + self.ahead / (2.0 * AHEAD_BLEND), 0.0), 1.0)
        by_depth = right_ahead if front > 0.0 else 1.0 - right_ahead
        return max(side, 0.0) + (1.0 - abs(side)) * by_depth

    def lay(self, kind: str, root: str, hanging: list[Vector]) -> list[Vector]:
        """The directions of a chain's bones that follow the legs, from their hanging directions."""
        base = root.rstrip("_LR").rstrip("0123456789")
        angle = math.radians(ANGLES[base] * (-1.0 if root.endswith("_L") else 1.0))
        share = self.right_share(angle)
        outward = self.forward * math.cos(angle) + self.right * math.sin(angle)
        laid = []
        for part, down in zip(BESIDE[kind], hanging):
            if part is None:
                laid.append(down)
                continue
            turned = self.turns[(part, "L")].slerp(self.turns[(part, "R")], share) @ down
            reach = min(max(((turned - down).dot(outward) - SLACKS[kind]) / FOLLOW_REACH, 0.0), 1.0)
            laid.append(down.slerp(turned, reach) if reach > 0.0 else down)
        return laid


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


class Envelope:
    """The outside of the cloth on a kind's chains that another kind's cloth lies over, such as a
    robe's skirt under a cloak: per chain, its particles moved out by how far the cloth reaches beyond
    each, the way the chain faces from the hips. At the height of a point, the cloth runs between
    the chains around the hips' upright axis, its distance from the axis blended by angle, and a point
    nearer the axis lies inside it."""

    def __init__(self, centre: Vector, forward: Vector, right: Vector, up: Vector):
        self.centre, self.forward, self.right, self.up = centre, forward, right, up
        self.chains: list[list[tuple[Vector, Vector]]] = []

    def add(self, positions: list[Vector], outward: Vector, reach: list[tuple[float, float] | None]) -> None:
        """A chain's particles moved out by the cloth's reach at each, between the particles that have
        cloth at them."""
        points = [positions[k] + outward * r[0] if r is not None else None for k, r in enumerate(reach)]
        self.chains.append([(points[k], points[k + 1]) for k in range(len(points) - 1)
                            if points[k] is not None and points[k + 1] is not None])

    def _polar(self, point: Vector) -> tuple[float, float, Vector]:
        offset = point - self.centre
        flat = offset - self.up * offset.dot(self.up)
        return math.atan2(flat.dot(self.right), flat.dot(self.forward)), flat.length, flat

    def penetration(self, point: Vector, margin: float) -> tuple[float, Vector]:
        height = point.dot(self.up)
        around = []
        for segments in self.chains:
            for a, b in segments:
                low, high = a.dot(self.up), b.dot(self.up)
                if min(low, high) <= height <= max(low, high) and abs(high - low) > 1e-6:
                    angle, radius, _flat = self._polar(a.lerp(b, (height - low) / (high - low)))
                    around.append((angle, radius))
                    break
        if len(around) < 3:
            return -1.0, self.up
        around.sort()
        angle, radius, flat = self._polar(point)
        cloth = around[0][1]
        for index in range(len(around)):
            (a0, r0), (a1, r1) = around[index - 1], around[index]
            span = (a1 - a0) % (2.0 * math.pi)
            share = ((angle - a0) % (2.0 * math.pi)) / span if span > 1e-9 else 2.0
            if share <= 1.0:
                cloth = r0 + (r1 - r0) * share
                break
        normal = flat / radius if radius > 1e-6 else -self.forward
        return cloth + margin - radius, normal


def _margins(rest: list[Vector], found: list[Capsule], margin: float,
             insets: list[float | None] | None) -> list[list[float]]:
    """The margin each particle of a chain keeps from each capsule: the margin its cloth keeps, the
    profile's and how far the cloth lies within the particle, and from the trunk's capsules no more
    than it has at rest."""
    found_margins = []
    for index, point in enumerate(rest):
        own = margin + ((insets[index] if insets is not None else None) or 0.0)
        found_margins.append([min(own, -capsule.depth_at_rest(point)) if capsule.trunk else own for capsule in found])
    return found_margins


def _resolve(positions: list[Vector], lengths: list[float], found: list[Capsule], margins: list[list[float]],
             insets: list[float | None] | None = None, envelope: Envelope | None = None) -> None:
    """ChainState.Resolve: from the root out of the capsules by each particle's margin from each, and
    of the envelope of the cloth it lies over by the gap and its cloth's reach within it at each
    particle that has cloth, and passes from the tip while it cannot."""

    def colliders(index: int, share: float = 1.0) -> list[tuple[object, float]]:
        pairs = [(capsule, margins[index][c] * share) for c, capsule in enumerate(found)]
        if envelope is not None and insets is not None and insets[index] is not None:
            pairs.append((envelope, (LAYER_GAP + insets[index]) * share))
        return pairs

    def keep_length(index: int, targets: list[Vector]) -> None:
        link = positions[index] - positions[index - 1]
        length = link.length
        positions[index] = positions[index - 1] + (link / length * lengths[index - 1] if length > 1e-6
                                                   else targets[index] - targets[index - 1])

    def from_root(targets: list[Vector]) -> None:
        for index in range(1, len(positions)):
            keep_length(index, targets)
            for _ in range(RESOLVE_ITERATIONS):
                moved = False
                for collider, gap in colliders(index):
                    depth, normal = collider.penetration(positions[index], gap)
                    if depth <= 0.0:
                        continue
                    link = (positions[index] - positions[index - 1]).normalized()
                    tangent = normal - link * normal.dot(link)
                    slope = tangent.length
                    positions[index] = positions[index] + (tangent / slope * min(depth / slope, lengths[index - 1])
                                                           if slope > MINIMUM_SLOPE else normal * depth)
                    moved = True
                keep_length(index, targets)
                if not moved:
                    break

    def penetrates() -> bool:
        return any(c.penetration(positions[i], gap)[0] > 0.0
                   for i in range(1, len(positions)) for c, gap in colliders(i, 0.5))

    def push_out(index: int) -> None:
        for collider, gap in colliders(index):
            depth, normal = collider.penetration(positions[index], gap)
            if depth > 0.0:
                positions[index] = positions[index] + normal * depth

    targets = [p.copy() for p in positions]
    from_root(targets)
    for _ in range(CHAIN_PASSES):
        if not penetrates():
            break
        last = len(positions) - 1
        push_out(last)
        for index in range(last - 1, 0, -1):
            link = positions[index] - positions[index + 1]
            if link.length > 1e-6:
                positions[index] = positions[index + 1] + link.normalized() * lengths[index]
            push_out(index)
        from_root(targets)


def follow(arm: bpy.types.Object, kinds: list[str], found: list[Capsule],
           reach: dict[str, dict[str, tuple[float, float]]] | None = None) -> None:
    """Lays the chains of the kinds over the armature's current pose: each hangs from its animated
    parent, is pushed out of the capsules, and its bones turn to point at its particles. With the
    reaches of the garments on them, a kind's chains also stay outside the envelope of the cloth that
    it lies over."""
    if not kinds:
        return
    bpy.context.view_layer.update()
    for capsule in found:
        capsule.place(arm)
    legs = Legs(arm) if any(kind in BESIDE for kind in kinds) else None
    reach = reach or {}
    envelopes: dict[str, Envelope] = {}
    for kind in sorted(kinds, key=ORDER.index):
        under = OVER.get(kind)
        envelope = envelopes.get(under) if under is not None and kind in reach else None
        if kind in reach and legs is not None and any(OVER.get(other) == kind for other in kinds):
            envelopes[kind] = Envelope(arm.pose.bones["Hips"].head.copy(), legs.forward, legs.right, Vector((0.0, 0.0, 1.0)))
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
            positions = [m.translation.copy() for m in matrices]
            positions.append(matrices[-1] @ Vector((0.0, lengths[-1], 0.0)))
            if legs is not None and kind in BESIDE:
                hanging = [m.to_3x3() @ Vector((0.0, 1.0, 0.0)) for m in matrices]
                positions = positions[:1]
                for direction, length in zip(legs.lay(kind, names[0], hanging), lengths):
                    positions.append(positions[-1] + direction.normalized() * length)
            insets = None
            if kind in reach:
                insets = [r[1] if r is not None else None for r in reach[kind].get(names[0], [None] * len(positions))]
            rest = [arm.data.bones[n].head_local.copy() for n in names] + [arm.data.bones[names[-1]].tail_local.copy()]
            _resolve(positions, lengths, found, _margins(rest, found, MARGINS[kind], insets), insets, envelope)
            if kind in envelopes and names[0] in reach[kind]:
                envelopes[kind].add(positions, _outward(names[0], legs.forward, legs.right), reach[kind][names[0]])
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
