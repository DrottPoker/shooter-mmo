"""The cloth chains laid by the legs and pushed out of the body, without springs, on plain data: the
kinematic layer of the runtime cloth, for the review sheets, the review files, and the checks of
garments with loose parts. See docs/EQUIPMENT_VISUALS_DESIGN.md and docs/ART_PIPELINE.md.

ClothKinematics in the Unity client is the source, and this follows it step by step, with Unity's own
vector and rotation arithmetic. Every chain hangs as given from its posed parent; its bones beside a
thigh or a shin turn as that part of the leg turns against the hips, but only as far as the leg pushes
the cloth outward beyond the profile's slack, the way the chain faces from the hips; then every
particle is pushed out of the capsules around the body's bones at its bone's length from the one
before, with passes from the tip when it cannot leave a capsule that way. A cloak worn over a robe lies
over its skirt: the skirt chains are laid first, and every cloak particle with cloth is kept outside
the skirt's cloth, its envelope, by a gap and by how far the cloak's own cloth lies within it.

The constants are the cloth section of art/Skeletons/Humanoid.json, which the Unity client checks
against its own. The data is a case of tools/blender/tests/fixtures/cloth_kinematics.json, which the
Unity client writes and both test against: a dict with the scale of the body, its up, its legs (the
hips' position, forward and right, how far the right knee lies ahead of the left, and how each thigh
and shin turns, as quaternions x, y, z, w), its posed capsules, and its chains as they hang. Vectors
are dicts with x, y, z. Only the standard library, so the tests run without Blender."""

import json
import math
import pathlib

SKELETON = pathlib.Path(__file__).resolve().parents[3] / "art" / "Skeletons" / "Humanoid.json"


def _cloth() -> dict:
    with open(SKELETON, encoding="utf-8") as file:
        return json.load(file)["cloth"]


CLOTH = _cloth()
CAPSULE_BONES = tuple((capsule["bone"], capsule["end"]) for capsule in CLOTH["capsules"])
DIRECTIONS = CLOTH["directions"]
GROOVE_PASSES = CLOTH["groovePasses"]
FALLBACK_RADIUS = CLOTH["fallbackRadius"]
PLACE_SPAN = CLOTH["placeSpan"]
RESOLVE_ITERATIONS = CLOTH["resolveIterations"]
MINIMUM_SLOPE = CLOTH["minimumSlope"]
CHAIN_PASSES = CLOTH["chainPasses"]
AHEAD_BLEND = CLOTH["aheadBlend"]
FOLLOW_REACH = CLOTH["followReach"]
LAYER_GAP = CLOTH["layerGap"]
# The kinds in the order their cloth lies on the body, each with its profile's margin and slack, the
# part of the legs beside each of its bones, the bone it hangs from, and the kind it lies over.
KINDS = {kind["kind"]: kind for kind in CLOTH["kinds"]}
ORDER = tuple(kind["kind"] for kind in CLOTH["kinds"])
# The chains' angles around the hips from the front toward the right, in degrees, by name; a chain on
# the left takes the negative angle.
ANGLES = {chain["name"]: chain["angle"] for kind in CLOTH["kinds"] for chain in kind["chains"]}

# Unity's Vector3.kEpsilon, below which a vector has no direction.
_EPSILON = 1e-5


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


def _clamp01(value):
    return min(max(value, 0.0), 1.0)


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


def _slerp_vectors(a, b, t):
    """Vector3.Slerp: the direction turned from a toward b by a share of the angle between them,
    clamped, and the length taken between theirs."""
    t = _clamp01(t)
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


def _slerp_rotations(a, b, t):
    """Quaternion.Slerp: the shorter way from a to b by a clamped share, blended straight and
    normalized when they lie close, as Unity does above a dot of 0.95."""
    t = _clamp01(t)
    dot = a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3]
    if dot < 0.0:
        dot, b = -dot, (-b[0], -b[1], -b[2], -b[3])
    if dot > 0.95:
        mixed = tuple(a[k] + (b[k] - a[k]) * t for k in range(4))
    else:
        angle = math.acos(min(dot, 1.0))
        sin = math.sin(angle)
        wa, wb = math.sin((1.0 - t) * angle) / sin, math.sin(t * angle) / sin
        mixed = tuple(a[k] * wa + b[k] * wb for k in range(4))
    norm = math.sqrt(sum(c * c for c in mixed))
    return tuple(c / norm for c in mixed)


def _repeat(value, length):
    return value - math.floor(value / length) * length


class Legs:
    """Where the legs are: how each thigh and shin turns against the posed hips from where it points
    at rest (left thigh, left shin, right thigh, right shin), the hips' forward and right axes, where
    the hips are, and how far the right knee lies ahead of the left."""

    THIGH_L, SHIN_L, THIGH_R, SHIN_R = range(4)

    def __init__(self, turns, forward, right, hips, ahead: float):
        self.turns, self.forward, self.right, self.hips, self.ahead = list(turns), forward, right, hips, ahead

    def right_share(self, angle: float) -> float:
        """How much a chain at an angle around the hips follows the right leg rather than the left: a
        side chain its own leg, a front chain the leg in front, and a back chain the leg behind,
        blended between."""
        radians = math.radians(angle)
        side = math.sin(radians)
        right_ahead = _clamp01(0.5 + self.ahead / (2.0 * AHEAD_BLEND))
        by_depth = right_ahead if math.cos(radians) > 0.0 else 1.0 - right_ahead
        return max(side, 0.0) + (1.0 - abs(side)) * by_depth


def outward(legs: Legs, angle: float):
    """The way a chain faces from the hips, by its angle around them in degrees."""
    radians = math.radians(angle)
    return _add(_mul(legs.forward, math.cos(radians)), _mul(legs.right, math.sin(radians)))


def follow_legs(targets: list, lengths: list, beside: list, angle: float, legs: Legs, slack: float) -> None:
    """Turns every bone beside a thigh or a shin as that part of the legs turns, as far as it pushes
    the bone outward beyond the slack, the way the chain faces from the hips; the targets hang from the
    chain's parent at rest and are laid again from the root."""
    share = legs.right_share(angle)
    out = outward(legs, angle)
    hanging_from = targets[0]
    for index, length in enumerate(lengths):
        hanging_to = targets[index + 1]
        hanging = _normalized(_sub(hanging_to, hanging_from))
        hanging_from = hanging_to
        part = beside[index] if index < len(beside) else "none"
        direction = hanging
        if part != "none":
            left = Legs.THIGH_L if part == "thigh" else Legs.SHIN_L
            right = Legs.THIGH_R if part == "thigh" else Legs.SHIN_R
            turned = _rotate(_slerp_rotations(legs.turns[left], legs.turns[right], share), hanging)
            reach = _clamp01((_dot(_sub(turned, hanging), out) - slack) / FOLLOW_REACH)
            direction = _slerp_vectors(hanging, turned, reach) if reach > 0.0 else hanging
        targets[index + 1] = _add(targets[index], _mul(_normalized(direction), length))


def within(reach: list | None, index: int, scale: float) -> float:
    """How far the garment's cloth lies within a particle, or zero without the garment or cloth."""
    return reach[index][1] * scale if reach is not None and index < len(reach) and reach[index] is not None else 0.0


def margins(margin: float, reach: list | None, rest_depths: list | None, capsules: int, particles: int, scale: float) -> list:
    """The margin each particle keeps from each capsule, by particle, then capsule: the one its cloth
    keeps, the profile's and how far the cloth lies within the particle, and from a capsule no more
    than it has in the bind pose, where rest_depths gives how far it lies within it there (None for no
    limit, as for the legs), all scaled with the body."""
    found = []
    for index in range(particles):
        own = margin + within(reach, index, scale)
        for capsule in range(capsules):
            depth = rest_depths[index * capsules + capsule] if rest_depths is not None else None
            found.append(own if depth is None else min(own, -depth * scale))
    return found


class Capsule:
    """A capsule as posed: its segment, the body's front and side around it, its radii from the front
    toward the side, how much it is scaled, and the way out of a point on its axis."""

    def __init__(self, bone: str, a, b, front, side, radii: list, scale: float = 1.0, axis_normal=(0.0, 1.0, 0.0)):
        self.bone, self.a, self.b, self.front, self.side = bone, a, b, front, side
        self.radii, self.scale, self.axis_normal = list(radii), scale, axis_normal
        self.widest = max(self.radii) * scale if self.radii else 0.0
        self.axis = _sub(b, a)
        self.axis_squared = _dot(self.axis, self.axis)

    @staticmethod
    def of(case: dict) -> "Capsule":
        return Capsule(case["bone"], vector(case["a"]), vector(case["b"]), vector(case["front"]), vector(case["side"]),
                       case["radii"], case["scale"], vector(case["axisNormal"]))

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

    def penetration(self, point, margin: float):
        """How far a point lies within the capsule and the margin, and the way out of it."""
        away = _sub(point, closest(point, self.a, self.b))
        length = _length(away)
        normal = _mul(away, 1.0 / length) if length > 1e-6 else self.axis_normal
        return self.radius_toward(away) + margin - length, normal

    def penetration_if_near(self, point, margin: float):
        """penetration, or zero without the way out for a point beyond the capsule's widest radius and
        the margin, which lies outside for certain."""
        reach = self.widest + margin
        if reach <= 0.0:
            return 0.0, (0.0, 0.0, 0.0)
        offset = _sub(point, self.a)
        t = 0.0 if self.axis_squared < 1e-12 else _clamp01(_dot(offset, self.axis) / self.axis_squared)
        away = _sub(offset, _mul(self.axis, t))
        if _dot(away, away) >= reach * reach:
            return 0.0, (0.0, 0.0, 0.0)
        return self.penetration(point, margin)


def closest(point, a, b):
    axis = _sub(b, a)
    squared = _dot(axis, axis)
    if squared < 1e-12:
        return a
    return _add(a, _mul(axis, _clamp01(_dot(_sub(point, a), axis) / squared)))


class Envelope:
    """The outside of the cloth on the skirt chains that a cloak lies over: per chain, its particles
    moved out by how far the skirt's cloth reaches beyond each, the way the chain faces from the hips.
    At the height of a point, the cloth runs between the chains around the hips' upright axis, its
    distance from the axis blended by angle, and a point nearer the axis lies inside it."""

    def __init__(self, centre, forward, right, up):
        self.centre, self.forward, self.right, self.up = centre, forward, right, up
        self.lines: list[list[tuple]] = []
        self.lowest, self.highest = math.inf, -math.inf

    @property
    def built(self) -> bool:
        return bool(self.lines)

    def add(self, positions: list, out, reach: list | None, scale: float) -> None:
        """A chain's particles moved out by the cloth's reach at each, between the particles that have
        cloth at them."""
        line = []
        for index, position in enumerate(positions):
            has = reach is not None and index < len(reach) and reach[index] is not None
            point = _add(position, _mul(out, reach[index][0] * scale)) if has else position
            height = _dot(point, self.up)
            line.append((point, has, height))
            if has:
                self.lowest, self.highest = min(self.lowest, height), max(self.highest, height)
        self.lines.append(line)

    def _polar(self, point):
        offset = _sub(point, self.centre)
        flat = _sub(offset, _mul(self.up, _dot(offset, self.up)))
        return math.atan2(_dot(flat, self.right), _dot(flat, self.forward)), _length(flat), flat

    def penetration(self, point, margin: float):
        height = _dot(point, self.up)
        if height < self.lowest or height > self.highest:
            return -1.0, (0.0, 0.0, 0.0)
        around = []
        for line in self.lines:
            for (a, has_a, low), (b, has_b, high) in zip(line, line[1:]):
                if not has_a or not has_b:
                    continue
                if min(low, high) <= height <= max(low, high) and abs(high - low) > 1e-6:
                    angle, radius, _flat = self._polar(_lerp(a, b, (height - low) / (high - low)))
                    # Insertion by angle, as the solver keeps them: a later equal angle after the earlier.
                    at = len(around)
                    while at > 0 and around[at - 1][0] > angle:
                        at -= 1
                    around.insert(at, (angle, radius))
                    break
        point_angle, point_radius, flat = self._polar(point)
        normal = _mul(flat, 1.0 / point_radius) if point_radius > 1e-6 else _mul(self.forward, -1.0)
        if len(around) < 3:
            return -1.0, normal
        cloth = around[0][1]
        for index in range(len(around)):
            (a0, r0), (a1, r1) = around[index - 1], around[index]
            span = _repeat(a1 - a0, 2.0 * math.pi)
            share = _repeat(point_angle - a0, 2.0 * math.pi) / span if span > 1e-9 else 2.0
            if share <= 1.0:
                cloth = r0 + (r1 - r0) * share
                break
        return cloth + margin - point_radius, normal


def resolve(positions: list, targets: list, lengths: list, capsules: list, found_margins: list, reach: list | None,
            under: Envelope | None, scale: float) -> None:
    """Moves every particle out of the capsules at its bone's length from the one before, so the bones
    that point at the particles end exactly where the particles are, and out of the cloth under it,
    such as a cloak out of a robe's skirt. A pass from the root sets each particle in turn; when a
    particle cannot leave a capsule that way, such as behind a thigh that swings back, a pass from the
    tip pulls the chain toward where the tip is free, and the next pass from the root bends the chain
    around the leg. The targets are where the chain hangs without the body, for a link that has no
    direction."""
    count = len(capsules)

    def depth(index: int, collider: int, share: float):
        if collider < count:
            return capsules[collider].penetration_if_near(positions[index], found_margins[index * count + collider] * share)
        if under is None or reach is None or index >= len(reach) or reach[index] is None:
            return -1.0, (0.0, 0.0, 0.0)
        return under.penetration(positions[index], (LAYER_GAP + reach[index][1]) * scale * share)

    def keep_length(index: int) -> None:
        link = _sub(positions[index], positions[index - 1])
        length = _length(link)
        positions[index] = _add(positions[index - 1], _mul(link, lengths[index - 1] / length) if length > 1e-6
                                else _sub(targets[index], targets[index - 1]))

    def from_root() -> None:
        for index in range(1, len(positions)):
            keep_length(index)
            for _ in range(RESOLVE_ITERATIONS):
                moved = False
                for collider in range(count + 1):
                    found, normal = depth(index, collider, 1.0)
                    if found <= 0.0:
                        continue
                    # Slide around the joint before it, on the bone's reach, out of the collider;
                    # straight out when the bone points into it head on.
                    link = _normalized(_sub(positions[index], positions[index - 1]))
                    tangent = _sub(normal, _mul(link, _dot(normal, link)))
                    slope = _length(tangent)
                    positions[index] = _add(positions[index], _mul(tangent, min(found / slope, lengths[index - 1]) / slope)
                                            if slope > MINIMUM_SLOPE else _mul(normal, found))
                    moved = True
                keep_length(index)
                if not moved:
                    break

    def push_out(index: int) -> None:
        for collider in range(count + 1):
            found, normal = depth(index, collider, 1.0)
            if found > 0.0:
                positions[index] = _add(positions[index], _mul(normal, found))

    def penetrates() -> bool:
        # A little inside the margin is still outside.
        return any(depth(index, collider, 0.5)[0] > 0.0 for index in range(1, len(positions)) for collider in range(count + 1))

    from_root()
    for _ in range(CHAIN_PASSES):
        if not penetrates():
            break
        last = len(positions) - 1
        push_out(last)
        for index in range(last - 1, 0, -1):
            link = _sub(positions[index], positions[index + 1])
            length = _length(link)
            if length > 1e-6:
                positions[index] = _add(positions[index + 1], _mul(link, lengths[index] / length))
            push_out(index)
        from_root()


def chain_reach(chain: dict) -> list | None:
    """A chain's reaches: per particle, how far the cloth reaches beyond and within it, None where it
    has no cloth; None without the garment."""
    if not chain["reachKnown"]:
        return None
    return [(r["x"], r["y"]) if has else None for r, has in zip(chain["reach"], chain["hasReach"])]


def chain_rest_depths(chain: dict) -> list | None:
    if not chain["restDepths"]:
        return None
    return [depth if known else None for depth, known in zip(chain["restDepths"], chain["restDepthKnown"])]


def legs_of(case: dict) -> Legs | None:
    if not case["hasLegs"] or len(case["legTurns"]) != 4:
        return None
    return Legs([quaternion(q) for q in case["legTurns"]], vector(case["forward"]), vector(case["right"]),
                vector(case["hips"]), case["ahead"])


def lay(case: dict) -> list[list[tuple]]:
    """Lays every chain of a case as the solver's Follow detail lays them: the kinds in the order their
    cloth lies on the body, the skirt's envelope built before a cloak that lies over it. Returns each
    chain's particles in the order of the case."""
    legs = legs_of(case)
    capsules = [Capsule.of(capsule) for capsule in case["capsules"]]
    scale = case["scale"]
    chains = case["chains"]
    laid: list = [None] * len(chains)
    envelope = None
    covered = {kind: any(chain["kind"] == kind and chain["reachKnown"] for chain in chains) for kind in ORDER}
    for kind in ORDER:
        under = envelope if KINDS[kind]["over"] is not None and envelope is not None and envelope.built else None
        for index, chain in enumerate(chains):
            if chain["kind"] != kind:
                continue
            targets = [vector(point) for point in chain["hanging"]]
            if legs is not None and chain["beside"]:
                follow_legs(targets, chain["lengths"], chain["beside"], chain["angle"], legs, chain["slack"])
            positions = list(targets)
            reach = chain_reach(chain)
            found = margins(chain["margin"], reach, chain_rest_depths(chain), len(capsules), len(positions), scale)
            resolve(positions, targets, chain["lengths"], capsules, found, reach, under, scale)
            laid[index] = positions
        lies_over = [other for other in ORDER if KINDS[other]["over"] == kind]
        if legs is not None and covered[kind] and any(covered[other] for other in lies_over):
            envelope = Envelope(legs.hips, legs.forward, legs.right, vector(case["up"]))
            for index, chain in enumerate(chains):
                if chain["kind"] == kind:
                    envelope.add(laid[index], outward(legs, chain["angle"]), chain_reach(chain), scale)
    return laid
