"""The target pose of the cloth chains on plain data: where every chain hangs in a pose, without memory,
for the review sheets, the review files, and the checks of garments with loose parts. See
docs/EQUIPMENT_VISUALS_DESIGN.md and docs/ART_PIPELINE.md.

ClothPose in the Unity client is the source, and this follows it step by step, with Unity's own vector
and rotation arithmetic. Every chain hangs as given from its posed parent; its bones beside a thigh or a
shin turn as that part of each leg turns against the hips, as far as that leg pushes the cloth outward
beyond the profile's slack, toward the legs in the share each pushes. Then each bone, from the root,
swings outward in the plane of its direction and the way its chain faces, by the smallest angle that
leaves its samples outside what its kind collides with: its end, its middle, and the cloth toward the
neighbouring chains. An arm only lifts a cloak's cloth a little, and a cloak worn over a robe keeps
outside the outer side of the robe's skirt.

The constants are the cloth section of art/Skeletons/Humanoid.json, which the Unity client checks
against its own. The data is a case of tools/blender/tests/fixtures/cloth_kinematics.json, which the
Unity client writes and both test against: a dict with the scale of the body, its legs (the hips'
position, forward and right, and how each thigh and shin turns, as quaternions x, y, z, w), its capsules
as posed and in the bind pose, and its chains in the order they are laid, as they hang from their posed
parents at rest and in the bind pose. Vectors are dicts with x, y, z. Only the standard library, so the
tests run without Blender."""

import json
import math
import pathlib

SKELETON = pathlib.Path(__file__).resolve().parents[3] / "art" / "Skeletons" / "Humanoid.json"


def _cloth() -> dict:
    with open(SKELETON, encoding="utf-8") as file:
        return json.load(file)["cloth"]


CLOTH = _cloth()
CAPSULE_BONES = tuple((capsule["bone"], capsule["end"]) for capsule in CLOTH["capsules"])
# What each capsule is around: the trunk carries the chains, and the legs and arms move under the cloth.
ROLES = {capsule["bone"]: capsule["role"] for capsule in CLOTH["capsules"]}
DIRECTIONS = CLOTH["directions"]
GROOVE_PASSES = CLOTH["groovePasses"]
FALLBACK_RADIUS = CLOTH["fallbackRadius"]
PLACE_SPAN = CLOTH["placeSpan"]
LIMB_SHARE = CLOTH["limbShare"]
FOLLOW_REACH = CLOTH["followReach"]
LAYER_GAP = CLOTH["layerGap"]
SAMPLES = CLOTH["samples"]
MAX_SWING_DEGREES = CLOTH["maxSwingDegrees"]
SWING_FADE_DEGREES = CLOTH["swingFadeDegrees"]
ADVANCE_STEPS = CLOTH["advanceSteps"]
ADVANCE_SAFETY = CLOTH["advanceSafety"]
EXIT_ALONG = CLOTH["exitAlong"]
CONTACT_BAND = CLOTH["contactBand"]
ARM_PUSH = CLOTH["armPush"]
STRAIGHT_BLEND_DOT = CLOTH["straightBlendDot"]
# The kinds in the order their cloth lies on the body, each with its profile's margin and slack, the
# part of the legs beside each of its bones, the bone it hangs from, the kind it lies over, what it
# collides with, and the capsule its frame is.
KINDS = {kind["kind"]: kind for kind in CLOTH["kinds"]}
ORDER = tuple(kind["kind"] for kind in CLOTH["kinds"])
# The chains' angles around the hips from the front toward the right, in degrees, by name; a chain on
# the left takes the negative angle.
ANGLES = {chain["name"]: chain["angle"] for kind in CLOTH["kinds"] for chain in kind["chains"]}

# The samples of a bone: its end, its middle, half way to the left and right neighbour at its end, and
# the middle of the cloth between it and each neighbour's bone beside it.
END, MIDDLE, LEFT, RIGHT, LEFT_MIDDLE, RIGHT_MIDDLE = range(6)
assert SAMPLES == 6
# The roles as ClothRoles flags.
_ROLE_FLAGS = {"trunk": 1, "leg": 2, "arm": 4}
_ARM = _ROLE_FLAGS["arm"]

# Unity's Vector3.kEpsilon, below which a vector has no direction.
_EPSILON = 1e-5
# A bone this close to the way its chain faces has no plane to swing in.
_SWING_SIDE_MINIMUM = 1e-4
_NO_DEPTH = -1e30
# The collider of a sample that is the outer side of a skirt rather than a capsule.
_SURFACE = -1
# A ray that passes this close to a triangle's edge, in its share of the triangle, crosses it, so a ray
# along the seam of two triangles crosses both rather than slip between them.
_CROSSING_SLACK = 1e-4


def vector(value: dict) -> tuple[float, float, float]:
    return (value["x"], value["y"], value["z"])


def quaternion(value: dict) -> tuple[float, float, float, float]:
    return (value["x"], value["y"], value["z"], value["w"])


def _add(a, b):
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])


def _sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def _mul(a, s):
    return (a[0] * s, a[1] * s, a[2] * s)


def _dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def _cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def _length(a):
    return math.sqrt(_dot(a, a))


def _normalized(a):
    """Vector3.normalized: the zero vector for one too short to have a direction."""
    length = _length(a)
    return _mul(a, 1.0 / length) if length > _EPSILON else (0.0, 0.0, 0.0)


def _lerp(a, b, t):
    return (a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t)


def _saturate(value):
    return min(max(value, 0.0), 1.0)


def _smooth(value):
    return value * value * (3.0 - 2.0 * value)


def _rotate(q, v):
    """Quaternion * Vector3, as Unity computes it."""
    x, y, z = q[0] * 2.0, q[1] * 2.0, q[2] * 2.0
    xx, yy, zz = q[0] * x, q[1] * y, q[2] * z
    xy, xz, yz = q[0] * y, q[0] * z, q[1] * z
    wx, wy, wz = q[3] * x, q[3] * y, q[3] * z
    return ((1.0 - (yy + zz)) * v[0] + (xy - wz) * v[1] + (xz + wy) * v[2],
            (xy + wz) * v[0] + (1.0 - (xx + zz)) * v[1] + (yz - wx) * v[2],
            (xz - wy) * v[0] + (yz + wx) * v[1] + (1.0 - (xx + yy)) * v[2])


def _axis_angle(axis, angle, v):
    """A vector turned by an angle around a unit axis."""
    cos, sin = math.cos(angle), math.sin(angle)
    return _add(_add(_mul(v, cos), _mul(_cross(axis, v), sin)), _mul(axis, _dot(axis, v) * (1.0 - cos)))


def _orthonormal(v):
    """A unit vector at right angles to a unit vector, for turning it half way round."""
    if abs(v[2]) > 0.70710678:
        k = 1.0 / math.sqrt(v[1] * v[1] + v[2] * v[2])
        return (0.0, -v[2] * k, v[1] * k)
    k = 1.0 / math.sqrt(v[0] * v[0] + v[1] * v[1])
    return (-v[1] * k, v[0] * k, 0.0)


def _slerp_directions(a, b, t):
    """Vector3.Slerp: the direction turned from a toward b by a share of the angle between them,
    clamped, and the length taken between theirs."""
    t = _saturate(t)
    length_a, length_b = _length(a), _length(b)
    if length_a < _EPSILON or length_b < _EPSILON:
        return _lerp(a, b, t)
    length = length_a + (length_b - length_a) * t
    dot = _dot(a, b) / (length_a * length_b)
    if dot > 1.0 - _EPSILON:
        return _lerp(a, b, t)
    unit = _mul(a, 1.0 / length_a)
    if dot < -1.0 + _EPSILON:
        return _mul(_axis_angle(_orthonormal(unit), math.pi * t, unit), length)
    axis = _normalized(_cross(a, b))
    return _mul(_axis_angle(axis, math.acos(dot) * t, unit), length)


def closest(point, a, b):
    """The point of a segment nearest a point."""
    axis = _sub(b, a)
    squared = _dot(axis, axis)
    if squared < 1e-12:
        return a
    return _add(a, _mul(axis, _saturate(_dot(_sub(point, a), axis) / squared)))


class Legs:
    """Where the legs are: how each thigh and shin turns against the posed hips from where it points
    at rest (left thigh, left shin, right thigh, right shin), the hips' forward and right axes, and
    where the hips are."""

    THIGH_L, SHIN_L, THIGH_R, SHIN_R = range(4)

    def __init__(self, turns, forward, right, hips):
        self.turns, self.forward, self.right, self.hips = list(turns), forward, right, hips


class Capsule:
    """A capsule as posed: its segment, the body's front and side around it, the way out of a point on
    its axis, how much it is scaled, its radii from the front toward the side, and what it is around."""

    def __init__(self, a, b, front, side, axis_normal, scale: float, radii: list, role: str):
        self.a, self.b, self.front, self.side, self.axis_normal = a, b, front, side, axis_normal
        self.scale, self.radii, self.role = scale, list(radii), _ROLE_FLAGS[role]
        self.widest = (max(self.radii) if self.radii else 0.0) * scale

    @staticmethod
    def of(case: dict) -> "Capsule":
        return Capsule(vector(case["a"]), vector(case["b"]), vector(case["front"]), vector(case["side"]),
                       vector(case["axisNormal"]), case["scale"], case["radii"], case["role"])

    def radius_toward(self, away) -> float:
        """The radius toward a direction from the capsule's axis, blended between the two directions
        around it that were measured."""
        count = len(self.radii)
        if count == 1:
            return self.radii[0] * self.scale
        angle = math.atan2(_dot(away, self.side), _dot(away, self.front))
        position = (angle + math.pi) / (2.0 * math.pi) * count - 0.5
        lower = math.floor(position)
        share = position - lower
        return (self.radii[lower % count] * (1.0 - share) + self.radii[(lower + 1) % count] * share) * self.scale

    def depth(self, point, margin: float) -> float:
        """How far a point lies within the capsule and a margin: positive inside."""
        away = _sub(point, closest(point, self.a, self.b))
        return self.radius_toward(away) + margin - _length(away)


class Chain:
    """A chain of a case, in the order the chains are laid: its kind, its angle around the hips, its
    bones and particles in the case's, its neighbours among the case's chains (-1 for none), its
    profile's margin and slack, whether its garment's reach is known, what it collides with, and the
    capsule of its frame."""

    def __init__(self, case: dict, bone_start: int, particle_start: int):
        self.kind = case["kind"]
        self.angle = case["angle"]
        self.beside = list(case["beside"])
        self.bone_count = len(self.beside)
        self.bone_start, self.particle_start = bone_start, particle_start
        self.left, self.right = case["left"], case["right"]
        self.margin, self.slack = case["margin"], case["slack"]
        self.reach_known = case["reachKnown"]
        self.collides = sum(_ROLE_FLAGS[role] for role in case["collides"])
        self.frame_capsule = case["frameCapsule"]


class Particle:
    """A chain's particle: how far the garment's cloth reaches beyond it, the way the chain faces, and
    within it, and whether there is cloth at it."""

    def __init__(self, beyond: float = 0.0, inside: float = 0.0, has_cloth: bool = False):
        self.beyond, self.within, self.has_cloth = beyond, inside, has_cloth


class Body:
    """A case laid out as ClothPose takes it: its chains, bones, particles, and capsules, every
    particle by its place among all of the case's."""

    def __init__(self, case: dict):
        self.scale = case["scale"]
        self.legs = legs_of(case)
        self.capsules = [Capsule.of(capsule) for capsule in case["capsules"]]
        self.bind_capsules = [Capsule.of(capsule) for capsule in case["bindCapsules"]]
        self.chains, self.beside, self.particles = [], [], []
        self.hanging, self.bind_hanging = [], []
        for found in case["chains"]:
            chain = Chain(found, len(self.beside), len(self.particles))
            self.chains.append(chain)
            self.beside.extend(found["beside"])
            for index in range(chain.bone_count + 1):
                has = chain.reach_known and found["hasReach"][index]
                reach = found["reach"][index] if has else {"x": 0.0, "y": 0.0}
                self.particles.append(Particle(reach["x"], reach["y"], has))
                self.hanging.append(vector(found["hanging"][index]))
                self.bind_hanging.append(vector(found["bindHanging"][index]))
        self.margins = [0.0] * (len(self.beside) * SAMPLES * len(self.capsules))

    def within(self, particle: int, scale: float) -> float:
        """How far a chain's garment's cloth lies within a particle, scaled with the body."""
        found = self.particles[particle]
        return found.within * scale if found.has_cloth else 0.0


def legs_of(case: dict) -> Legs | None:
    if not case["hasLegs"] or len(case["legTurns"]) != 4:
        return None
    return Legs([quaternion(q) for q in case["legTurns"]], vector(case["forward"]), vector(case["right"]), vector(case["hips"]))


def outward(chain: Chain, legs: Legs | None, capsules: list) -> tuple:
    """The way a chain faces: from the hips at its angle, or from the capsule its kind's frame is, such
    as the head's for a hood."""
    radians = math.radians(chain.angle)
    sin, cos = math.sin(radians), math.cos(radians)
    if chain.frame_capsule >= 0:
        frame = capsules[chain.frame_capsule]
        return _add(_mul(frame.front, cos), _mul(frame.side, sin))
    return _add(_mul(legs.forward, cos), _mul(legs.right, sin)) if legs is not None else (0.0, 0.0, 0.0)


def follow_legs(chain: Chain, legs: Legs | None, beside: list, hanging: list, directions: list) -> None:
    """The direction of every bone of a chain, from where it hangs: beside a thigh or a shin turned as
    that part of the legs turns, as far as it pushes the bone outward beyond the slack, the way the chain
    faces from the hips. Each leg pushes on its own, in full on its own side and the front and back and
    less toward the other side, and the bone turns toward both legs in the share of the squares of their
    pushes."""
    radians = math.radians(chain.angle)
    sin, cos = math.sin(radians), math.cos(radians)
    out = _add(_mul(legs.forward, cos), _mul(legs.right, sin)) if legs is not None else (0.0, 0.0, 0.0)
    left_side = min(1.0, 1.0 - sin)
    right_side = min(1.0, 1.0 + sin)
    for index in range(chain.bone_count):
        particle = chain.particle_start + index
        hanging_direction = _normalized(_sub(hanging[particle + 1], hanging[particle]))
        direction = hanging_direction
        part = beside[chain.bone_start + index]
        if legs is not None and part != "none":
            left = _rotate(legs.turns[Legs.THIGH_L if part == "thigh" else Legs.SHIN_L], hanging_direction)
            right = _rotate(legs.turns[Legs.THIGH_R if part == "thigh" else Legs.SHIN_R], hanging_direction)
            left_reach = left_side * _saturate((_dot(_sub(left, hanging_direction), out) - chain.slack) / FOLLOW_REACH)
            right_reach = right_side * _saturate((_dot(_sub(right, hanging_direction), out) - chain.slack) / FOLLOW_REACH)
            reach = max(left_reach, right_reach)
            if reach > 0.0:
                share = right_reach * right_reach / (left_reach * left_reach + right_reach * right_reach)
                direction = _slerp_directions(hanging_direction, _slerp_directions(left, right, share), reach)
        directions[chain.bone_start + index] = _normalized(direction)


def _toward(body: Body, chain: Chain, index: int, sample: int):
    """The neighbour a sample of a bone lies toward, and -1 for the edge of an open strip of chains,
    where it lies as far beyond the bone as the cloth toward the neighbour on the other side; None for a
    bone with no neighbour beside it."""
    left = sample in (LEFT, LEFT_MIDDLE)
    neighbour = chain.left if left else chain.right
    if neighbour >= 0 and index + 1 <= body.chains[neighbour].bone_count:
        return neighbour, 1.0
    neighbour = chain.right if left else chain.left
    if neighbour >= 0 and index + 1 <= body.chains[neighbour].bone_count:
        return neighbour, -1.0
    return None


def _sample_at(body: Body, chain: Chain, index: int, sample: int, start, end, followed: list, scale: float):
    """Where a sample of a bone lies when the bone ends at a point, and how far its cloth lies within
    it; None for a bone with no neighbour beside it. The cloth toward a neighbour lies half way to it as
    the chains follow the legs, in followed, and moves with the bone."""
    particle = chain.particle_start + index
    if sample == END:
        return end, body.within(particle + 1, scale)
    if sample == MIDDLE:
        return _lerp(start, end, 0.5), 0.5 * (body.within(particle, scale) + body.within(particle + 1, scale))
    toward = _toward(body, chain, index, sample)
    if toward is None:
        return None
    neighbour, side = toward
    # Beyond the edge the cloth is the bone's own.
    other = body.chains[neighbour].particle_start + index + 1
    other_within = body.within(other, scale) if side > 0.0 else body.within(particle + 1, scale)
    other_start_within = body.within(other - 1, scale) if side > 0.0 else body.within(particle, scale)
    to_end = _mul(_sub(followed[other], followed[particle + 1]), side)
    if sample in (LEFT, RIGHT):
        return _add(end, _mul(to_end, 0.5)), 0.5 * (body.within(particle + 1, scale) + other_within)
    to_start = _mul(_sub(followed[other - 1], followed[particle]), side)
    point = _add(_lerp(start, end, 0.5), _mul(_add(to_start, to_end), 0.25))
    return point, 0.25 * (body.within(particle, scale) + body.within(particle + 1, scale) + other_start_within + other_within)


def margins(body: Body) -> None:
    """The margin every sample of every bone keeps from every capsule, laid in the body's bind pose: the
    one its cloth keeps, the profile's and how far the cloth lies within it, but no more than it has
    there less the contact band, so nothing gives way in the bind pose. By bone, sample, then capsule."""
    count = len(body.capsules)
    for chain in body.chains:
        for index in range(chain.bone_count):
            particle = chain.particle_start + index
            row = (chain.bone_start + index) * SAMPLES * count
            for sample in range(SAMPLES):
                found = _sample_at(body, chain, index, sample, body.bind_hanging[particle], body.bind_hanging[particle + 1],
                                   body.bind_hanging, 1.0)
                own = chain.margin + (found[1] if found is not None else 0.0)
                for capsule in range(count):
                    depth = body.bind_capsules[capsule].depth(found[0], 0.0) if found is not None else _NO_DEPTH
                    body.margins[row + sample * count + capsule] = min(own, -depth - CONTACT_BAND)


class _Swing:
    """One bone's swing: where its end lies at an angle, and the angle at which a sample leaves a
    collider."""

    def __init__(self, body: Body, chain: Chain, index: int, start, direction, side, length: float, up, hips, followed: list,
                 surface: list):
        self.body, self.chain, self.index = body, chain, index
        self.start, self.direction, self.side, self.length = start, direction, side, length
        self.row = (chain.bone_start + index) * SAMPLES * len(body.capsules)
        self.up, self.hips, self.followed, self.surface = up, hips, followed, surface

    def end(self, angle: float):
        sin, cos = math.sin(angle), math.cos(angle)
        return _add(self.start, _mul(_add(_mul(self.direction, cos), _mul(self.side, sin)), self.length))

    def sample_reach(self) -> float:
        """The farthest any sample of the bone lies from its start in any swing: the end at the bone's
        length, the middle at half of it, and the cloth toward a neighbour as far again as its share of
        the way to the neighbour."""
        reach = self.length
        particle = self.chain.particle_start + self.index
        for sample in range(LEFT, SAMPLES):
            toward = _toward(self.body, self.chain, self.index, sample)
            if toward is None:
                continue
            other = self.body.chains[toward[0]].particle_start + self.index + 1
            to_end = _length(_sub(self.followed[other], self.followed[particle + 1]))
            to_start = _length(_sub(self.followed[other - 1], self.followed[particle]))
            reach = max(reach, self.length + 0.5 * to_end if sample in (LEFT, RIGHT) else 0.5 * self.length + 0.25 * (to_start + to_end))
        return reach

    def largest_margin(self, capsule: int) -> float:
        """The largest margin a sample of the bone keeps from a capsule."""
        count = len(self.body.capsules)
        return max(self.body.margins[self.row + sample * count + capsule] for sample in range(SAMPLES)) * self.body.scale

    def moves(self, sample: int) -> float:
        """How far a sample moves per radian of the swing: as the end, or the middle of the bone."""
        return (0.5 if sample in (MIDDLE, LEFT_MIDDLE, RIGHT_MIDDLE) else 1.0) * self.length

    def band_depth(self, depth: float) -> float:
        """How deep a sample lies in a collider as the bone hangs, with the clearance the contact band
        keeps: none outside the band, its depth once the collider is the band deep into it, and between,
        a little more."""
        band = CONTACT_BAND * self.body.scale
        if depth <= -band:
            return 0.0
        if depth >= band:
            return depth
        return (depth + band) * (depth + band) / (4.0 * band)

    def arm_angle(self, sample: int, collider: int, hanging_point) -> float:
        """The angle an arm swings a bone by: its sample moved out as far as the arm comes into it,
        saturated at the arm's push."""
        depth = self.band_depth(self._hanging_depth(sample, collider, hanging_point, 0.0))
        push = ARM_PUSH * self.body.scale
        return push * math.tanh(depth / push) / self.moves(sample)

    def exit_angle(self, sample: int, collider: int, hanging_point, hanging_within: float) -> float:
        """The smallest angle at which a sample leaves a collider, the capsule of the body or the outer
        side of the skirt, and the clearance the contact band keeps: none for a sample outside the band
        as the bone hangs, else advanced toward where it first leaves and found there by a secant step,
        taken in the share the swing moves it out of the collider there and less and less toward the
        largest swing. The sample lies at a point as the bone hangs, its cloth within it by a share."""
        hanging_depth = self._hanging_depth(sample, collider, hanging_point, hanging_within)
        band_depth = self.band_depth(hanging_depth)
        if band_depth <= 0.0:
            return 0.0
        moves = self.moves(sample)
        clearance = band_depth - hanging_depth
        largest = math.radians(MAX_SWING_DEGREES)
        before, before_depth = 0.0, band_depth
        angle, depth = 0.0, band_depth
        for _ in range(ADVANCE_STEPS):
            if not angle < largest:
                break
            before, before_depth = angle, depth
            angle = min(angle + max(depth, 0.0) / (ADVANCE_SAFETY * moves), largest)
            depth = self._depth_at(angle, sample, collider) + clearance
        # The last steps fall short where the sample leaves; a secant step through them reaches it, but
        # no farther than a few of them.
        if depth > 0.0 and before_depth > depth:
            angle = min(angle + min(depth * (angle - before) / (before_depth - depth), 4.0 * (angle - before)), largest)
        sin, cos = math.sin(angle), math.cos(angle)
        tangent = _sub(_mul(self.side, cos), _mul(self.direction, sin))
        along = _saturate(_dot(self._out_of(angle, sample, collider), tangent) / EXIT_ALONG)
        fade = _saturate((largest - angle) / math.radians(SWING_FADE_DEGREES))
        return angle * along * along * (3.0 - 2.0 * along) * fade * fade * (3.0 - 2.0 * fade)

    def _point(self, angle: float, sample: int):
        return _sample_at(self.body, self.chain, self.index, sample, self.start, self.end(angle), self.followed, self.body.scale)

    def _out_of(self, angle: float, sample: int, collider: int):
        """The way out of a collider from a sample when the bone swings by an angle: away from a
        capsule's axis, or away from the hips' upright axis for the outer side of a skirt."""
        point, _within = self._point(angle, sample)
        if collider == _SURFACE:
            flat = _sub(point, self.hips)
            return _normalized(_sub(flat, _mul(self.up, _dot(flat, self.up))))
        capsule = self.body.capsules[collider]
        away = _sub(point, closest(point, capsule.a, capsule.b))
        length = _length(away)
        return _mul(away, 1.0 / length) if length > 1e-6 else capsule.axis_normal

    def _hanging_depth(self, sample: int, collider: int, point, inside: float) -> float:
        """How deep a sample lies in a collider as the bone hangs, at a point with its cloth within it by
        a share; from a capsule whose widest radius keeps it out of the contact band, no deeper than that,
        found without the capsule's radius toward it."""
        if collider == _SURFACE:
            return surface_depth(self.surface, self.hips, self.up, point, LAYER_GAP * self.body.scale + inside)
        capsule = self.body.capsules[collider]
        margin = self.body.margins[self.row + sample * len(self.body.capsules) + collider] * self.body.scale
        away = _sub(point, closest(point, capsule.a, capsule.b))
        distance = _length(away)
        widest = capsule.widest + margin - distance
        return widest if widest <= -CONTACT_BAND * self.body.scale else capsule.radius_toward(away) + margin - distance

    def _depth_at(self, angle: float, sample: int, collider: int) -> float:
        """How deep a sample lies in a collider when the bone swings by an angle."""
        point, inside = self._point(angle, sample)
        if collider == _SURFACE:
            return surface_depth(self.surface, self.hips, self.up, point, LAYER_GAP * self.body.scale + inside)
        count = len(self.body.capsules)
        return self.body.capsules[collider].depth(point, self.body.margins[self.row + sample * count + collider] * self.body.scale)


def _lay_bone(body: Body, chain: Chain, index: int, out, up, under: bool, surface: list, followed: list, positions: list,
              directions: list) -> None:
    """Swings one bone outward from where its chain's particle before it lies, by the smallest angle that
    clears its samples, and lays its end there."""
    particle = chain.particle_start + index
    start = positions[particle]
    direction = directions[chain.bone_start + index]
    length = _length(_sub(body.hanging[particle + 1], body.hanging[particle]))
    side = _sub(out, _mul(direction, _dot(out, direction)))
    side_length = _length(side)
    if length < 1e-6:
        return
    if side_length < _SWING_SIDE_MINIMUM:
        positions[particle + 1] = _add(start, _mul(direction, length))
        return
    side = _mul(side, 1.0 / side_length)
    hips = body.legs.hips if body.legs is not None else (0.0, 0.0, 0.0)
    swing = _Swing(body, chain, index, start, direction, side, length, up, hips, followed, surface)
    # A capsule farther from the bone's start than the farthest its samples lie from there in any swing,
    # its widest radius, the largest margin a sample keeps from it, and the contact band never reaches a
    # sample.
    reach = swing.sample_reach() + CONTACT_BAND * body.scale
    candidates = [index_ for index_, capsule in enumerate(body.capsules)
                  if capsule.role & chain.collides
                  and _length(_sub(start, closest(start, capsule.a, capsule.b))) - capsule.widest <= reach + swing.largest_margin(index_)]
    # Each sample leaves each collider at its own angle, which changes smoothly with the pose; the bone
    # swings to the largest of them.
    angle = 0.0
    hanging_end = swing.end(0.0)
    for sample in range(SAMPLES):
        found = _sample_at(body, chain, index, sample, start, hanging_end, followed, body.scale)
        if found is None:
            continue
        hanging_point, hanging_within = found
        for capsule in candidates:
            exit_ = swing.arm_angle(sample, capsule, hanging_point) if body.capsules[capsule].role & _ARM \
                else swing.exit_angle(sample, capsule, hanging_point, hanging_within)
            angle = max(angle, exit_)
        if under and sample <= MIDDLE:
            angle = max(angle, swing.exit_angle(sample, _SURFACE, hanging_point, hanging_within))
    positions[particle + 1] = swing.end(angle)


def _build_surface(body: Body, positions: list) -> list:
    """The outer side of a robe's skirt: its chains' particles moved out by the cloth's reach beyond
    them, the way each chain faces, joined in triangles to the next chain around."""
    surface = []
    for chain in body.chains:
        if chain.kind != "skirt" or not chain.reach_known or chain.right < 0:
            continue
        following = body.chains[chain.right]
        if not following.reach_known:
            continue
        out = outward(chain, body.legs, body.capsules)
        following_out = outward(following, body.legs, body.capsules)
        for index in range(min(chain.bone_count, following.bone_count)):
            a, b = chain.particle_start + index, following.particle_start + index
            if not all(body.particles[p].has_cloth for p in (a, a + 1, b, b + 1)):
                continue
            a0 = _add(positions[a], _mul(out, body.particles[a].beyond * body.scale))
            a1 = _add(positions[a + 1], _mul(out, body.particles[a + 1].beyond * body.scale))
            b0 = _add(positions[b], _mul(following_out, body.particles[b].beyond * body.scale))
            b1 = _add(positions[b + 1], _mul(following_out, body.particles[b + 1].beyond * body.scale))
            surface.extend((a0, b0, b1, a0, b1, a1))
    return surface


def surface_depth(surface: list, hips, up, point, gap: float) -> float:
    """How far a point lies inside the outer side of a skirt and a gap, seen from the hips' upright axis
    at its height: the farthest crossing of the way out through it beyond the point; no depth where
    nothing crosses."""
    axis = _add(hips, _mul(up, _dot(_sub(point, hips), up)))
    way = _sub(point, axis)
    radius = _length(way)
    if radius < 1e-6:
        return _NO_DEPTH
    out = _mul(way, 1.0 / radius)
    farthest = _NO_DEPTH
    for index in range(0, len(surface) - 2, 3):
        distance = _crossing(axis, out, surface[index], surface[index + 1], surface[index + 2])
        if distance is not None:
            farthest = max(farthest, distance)
    return farthest + gap - radius if farthest > _NO_DEPTH else _NO_DEPTH


def _crossing(origin, direction, a, b, c):
    """Where a ray from an origin crosses a triangle, as a distance along its unit direction."""
    edge1, edge2 = _sub(b, a), _sub(c, a)
    p = _cross(direction, edge2)
    determinant = _dot(edge1, p)
    if abs(determinant) < 1e-12:
        return None
    t = _sub(origin, a)
    u = _dot(t, p) / determinant
    if u < -_CROSSING_SLACK or u > 1.0 + _CROSSING_SLACK:
        return None
    q = _cross(t, edge1)
    v = _dot(direction, q) / determinant
    if v < -_CROSSING_SLACK or u + v > 1.0 + _CROSSING_SLACK:
        return None
    distance = _dot(edge2, q) / determinant
    return distance if distance > 0.0 else None


def lay(case: dict) -> list[list[tuple]]:
    """Lays every chain of a case as ClothPose lays a body: its margins from its bind pose, then the
    kinds in the order their cloth lies on the body, and the outer side of a robe's skirt before a cloak
    over it. Returns each chain's particles in the order of the case."""
    body = Body(case)
    margins(body)
    legs = body.legs
    directions = [(0.0, 0.0, 0.0)] * len(body.beside)
    # Every chain as it follows the legs, before it swings: what the cloth toward a neighbour is sampled
    # toward.
    followed = list(body.hanging)
    for chain in body.chains:
        follow_legs(chain, legs, body.beside, body.hanging, directions)
        point = body.hanging[chain.particle_start]
        for index in range(chain.bone_count):
            particle = chain.particle_start + index
            point = _add(point, _mul(directions[chain.bone_start + index], _length(_sub(body.hanging[particle + 1], body.hanging[particle]))))
            followed[particle + 1] = point
    positions = list(body.hanging)
    # The hips' upright axis, which a skirt hangs around however the body lies, either way along it.
    up = _normalized(_cross(legs.forward, legs.right)) if legs is not None else (0.0, 1.0, 0.0)
    surface = []
    for kind in ORDER:
        for chain in body.chains:
            if chain.kind != kind:
                continue
            out = outward(chain, legs, body.capsules)
            under = kind == "cloak" and bool(surface)
            for index in range(chain.bone_count):
                _lay_bone(body, chain, index, out, up, under, surface, followed, positions, directions)
        if kind == "skirt" and legs is not None and any(c.kind == "skirt" and c.reach_known for c in body.chains) \
                and any(c.kind == "cloak" and c.reach_known for c in body.chains):
            surface = _build_surface(body, positions)
    return [positions[chain.particle_start:chain.particle_start + chain.bone_count + 1] for chain in body.chains]
