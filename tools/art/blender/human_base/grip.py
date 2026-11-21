"""The hand pose Grip of an animation library on the Humanoid skeleton: the fingers and the thumb
of each hand closed around a handle on the hand's socket, as the game plays it over the other clips
on a hand that holds something. The handle is a cylinder HANDLE thick through the socket's origin
along the socket's Y axis, which every held model's grip lies on (docs/ART_PIPELINE.md, the axes of
a hand's socket).

The pose is fitted to the hand of the body the library shows, so it suits the hand it is played on.

- The fingers of a hand at rest are spread and each turned a little about itself. In a grip every
  finger first turns at its knuckle, to the side and about itself, until the axis it closes about
  lies along the handle: it then closes in a plane across the handle, beside its neighbours.
- A finger closes joint by joint from its knuckle outward: each joint turns until the pad of its
  segment touches the handle, or as near to it as it comes. A segment's pad is the skin along the
  middle of the outer half of its palm side: the handle lies in the palm at the fingers' roots and
  presses into the skin there, and the skin at a joint lies on the handle with the segment before
  it.
- Fingers that close across the handle may overlap, or stand apart, where their knuckles do not
  lie a finger's width apart along it: the fingers beside the middle finger, and then the little
  finger beside the ring finger, turn to the side until each lies against its neighbour, and the
  fingers close again.
- The thumb closes over the handle and the fingers: its base turns toward the palm and to the side
  and its outer joints close until the pads of its two outer segments lie on them and not in them.

Both hands take the right hand's pose, mirrored.

The geometry needs numpy and not Blender; reading the hand from the library and writing the clip
need Blender."""

import numpy as np

CLIP = "Grip"
HANDLE = 0.029
FINGERS = ("Index", "Middle", "Ring", "Little")
SEGMENTS = ("Proximal", "Intermediate", "Distal")
DIGITS = ("Thumb",) + FINGERS
# How far a finger's knuckle, its middle joint, and its last joint may close, in degrees.
CLOSED = (95.0, 115.0, 85.0)
# Skin nearer to the handle than this touches it, in metres.
TOUCH = 0.0001
# A segment's pad: the skin on its palm side within this share of its thickness of its middle
# line, and of a finger's segment from this share of the segment's length on.
PAD_WIDTH, PAD = 0.5, 0.5
# Each finger and the neighbour it lies against, toward the middle finger, in the order they are
# laid together. A finger turns at most BESIDE degrees to the side from where it closes across the
# handle, fingers that overlap count OVERLAPPING times as much as fingers as far apart, and the
# fingers are closed and laid together PASSES times over.
NEIGHBOURS = (("Index", "Middle"), ("Ring", "Middle"), ("Little", "Ring"))
BESIDE, OVERLAPPING, PASSES = 20.0, 25.0, 2
# The thumb's base turns toward the palm about X and to the side about Z, between these degrees,
# and its middle joint closes by at most THUMB_MIDDLE_CLOSED, its last joint by THUMB_LAST of it.
# Its skin inside the handle or a finger counts THUMB_PRESSED times as much as skin as far off them.
THUMB_TOWARD, THUMB_ASIDE, THUMB_MIDDLE_CLOSED, THUMB_LAST = (0.0, 70.0), (-15.0, 45.0), 80.0, 0.5
THUMB_PRESSED = 400.0
# The thumb is fitted over this many values of each of its three turns, around the best values of
# the round before, this many rounds.
GRID, ROUNDS = 9, 6
# A grip is refused when the pad of a finger's segment lies further inside the handle than INSIDE or
# further off it than OFF, when two neighbouring fingers lie further apart than APART or overlap by
# more than OVERLAP, or when the thumb lies further inside the handle or a finger than
# THUMB_INSIDE, in metres.
INSIDE, OFF, APART, OVERLAP, THUMB_INSIDE = 0.002, 0.004, 0.003, 0.002, 0.001


def cylinder_gap(points, origin, axis, radius):
    """How far each point lies outside a cylinder around a line: negative inside it."""
    off = np.asarray(points, float) - np.asarray(origin, float)
    axis = np.asarray(axis, float) / np.linalg.norm(axis)
    return np.linalg.norm(off - np.outer(off @ axis, axis), axis=1) - radius


def capsule_gap(points, a, b, radius):
    """How far each point lies outside a capsule around the segment from a to b."""
    points, a, b = np.asarray(points, float), np.asarray(a, float), np.asarray(b, float)
    along = b - a
    share = np.clip(((points - a) @ along) / max(float(along @ along), 1e-18), 0.0, 1.0)
    return np.linalg.norm(points - (a + share[:, None] * along), axis=1) - radius


def first_contact(gap, low, high, steps=64, halvings=14):
    """The smallest value between low and high at which gap is no longer positive, looked for in
    steps and then between two of them, or, when it stays positive, the value at which it is
    smallest: a joint closes until its pad touches, or as near as it comes."""
    values = np.linspace(low, high, steps + 1)
    gaps = [gap(float(value)) for value in values]
    for index, found in enumerate(gaps):
        if found > 0.0:
            continue
        if index == 0:
            return float(low)
        before, after = float(values[index - 1]), float(values[index])
        for _ in range(halvings):
            middle = (before + after) / 2.0
            if gap(middle) > 0.0:
                before = middle
            else:
                after = middle
        return after
    return float(values[int(np.argmin(gaps))])


def lowest(cost, ranges, grid=GRID, rounds=ROUNDS):
    """The values, one per range of (low, high), with the lowest cost, searched on a grid that
    narrows around the best values of the round before."""
    ranges = [tuple(map(float, r)) for r in ranges]
    best = None
    for _ in range(rounds):
        axes = [np.linspace(low, high, grid) for low, high in ranges]
        for values in np.stack(np.meshgrid(*axes, indexing="ij"), axis=-1).reshape(-1, len(ranges)):
            found = cost(*values)
            if best is None or found < best[0]:
                best = (found, tuple(float(v) for v in values))
        steps = [(high - low) / (grid - 1) for low, high in ranges]
        ranges = [(max(r[0], v - s), min(r[1], v + s)) for r, v, s in zip(ranges, best[1], steps)]
    return best[1]


def thumb_cost(pads):
    """The cost of a pose of the thumb from the pads of its two outer segments, each given as how
    far its nearest skin and its skin on average lie outside the handle and the fingers: the
    squares of the averages, and of how far the nearest skin has gone in, that THUMB_PRESSED times
    over."""
    return float(sum(mean ** 2 + THUMB_PRESSED * max(-nearest, 0.0) ** 2 for nearest, mean in pads))


def finger_turns(angles, across):
    """The turns of a hand's finger bones from each digit's closing: per finger the degrees of its
    knuckle, its middle joint, and its last joint, after the degrees across gives it to the side
    and about itself, and for the thumb the degrees its base turns toward the palm and to the side
    and those of its middle joint."""
    turns = {}
    for finger in FINGERS:
        knuckle, middle, last = angles[finger]
        aside, about = across[finger]
        turns[f"{finger}Proximal"] = (("Z", aside), ("Y", about), ("X", -knuckle))
        turns[f"{finger}Intermediate"] = (("X", -middle),)
        turns[f"{finger}Distal"] = (("X", -last),)
    toward, aside, middle = angles["Thumb"]
    turns["ThumbProximal"] = (("X", -toward), ("Z", aside))
    turns["ThumbIntermediate"] = (("X", -middle),)
    turns["ThumbDistal"] = (("X", -middle * THUMB_LAST),)
    return turns


class Hand:
    """The skin of a right hand with its bones' rests, which poses it by the turns of its finger
    bones, named without their side: points, (n, 3), in the space of the bones; weights,
    (n, bones), over names; rests, the 4x4 rest matrix of every named bone, whose Y axis runs along
    the bone and whose Z axis from a finger's pad to its nail; parents, the parent of every finger
    bone; the socket's origin and Y axis; and turn, which makes a bone's turns its 3x3 rotation. A
    pose may also give a bone's rotation itself, as a 3x3 array."""

    def __init__(self, points, weights, names, rests, parents, origin, axis, turn):
        self.points = np.asarray(points, float)
        self.weights = np.asarray(weights, float)
        self.names = list(names)
        self.rests = {name: np.asarray(rest, float) for name, rest in rests.items()}
        self.inverse = {name: np.linalg.inv(rest) for name, rest in self.rests.items()}
        self.parents, self.turn = dict(parents), turn
        self.origin, self.axis = np.asarray(origin, float), np.asarray(axis, float)
        dominant = np.array([self.names[i] for i in np.argmax(self.weights, axis=1)])
        # Per finger bone: its pad, the skin it moves most along the middle of its palm side, of a
        # finger's segment on the outer half only; the bone's end; and how thick the skin lies
        # around it.
        self.pad, self.tail, self.radius = {}, {}, {}
        for digit in DIGITS:
            for segment, following in zip(SEGMENTS, SEGMENTS[1:] + (None,)):
                name = f"{digit}{segment}_R"
                rows = dominant == name
                if not rows.any():
                    raise ValueError(f"the hand has no skin on {name}")
                rest = self.rests[name]
                off = self.points - rest[:3, 3]
                along = off @ rest[:3, 1]
                if following:
                    self.tail[name] = self.rests[f"{digit}{following}_R"][:3, 3]
                else:
                    # A last segment ends where its skin does.
                    self.tail[name] = rest[:3, 3] + rest[:3, 1] * float(along[rows].max())
                self.radius[name] = float(np.median(capsule_gap(self.points[rows], rest[:3, 3], self.tail[name], 0.0)))
                pad = rows & (off @ rest[:3, 2] < 0.0) & (np.abs(off @ rest[:3, 0]) <= PAD_WIDTH * self.radius[name])
                if digit in FINGERS:
                    pad &= along >= PAD * float(np.linalg.norm(self.tail[name] - rest[:3, 3]))
                self.pad[name] = pad if pad.any() else rows

    def across(self):
        """Per finger, the degrees it turns at its knuckle to the side, about its Z axis, and then
        about itself, about its Y axis, so the X axis it closes about lies along the handle."""
        axis = self.axis / np.linalg.norm(self.axis)
        found = {}
        for finger in FINGERS:
            wanted = self.rests[f"{finger}Proximal_R"][:3, :3].T @ axis
            if wanted[0] < 0.0:
                wanted = -wanted
            found[finger] = (float(np.degrees(np.arctan2(wanted[1], wanted[0]))), float(-np.degrees(np.arcsin(wanted[2]))))
        return found

    def poses(self, turns):
        """The 4x4 matrix of every finger bone posed by the turns, the hand at rest."""
        posed = {}
        for digit in DIGITS:
            for segment in SEGMENTS:
                name = f"{digit}{segment}_R"
                parent = self.parents[name]
                base = (posed[parent] @ self.inverse[parent] if parent in posed else np.eye(4)) @ self.rests[name]
                rotation = np.eye(4)
                # A bone's turns, or its rotation as it is.
                value = turns.get(name[:-2], ())
                rotation[:3, :3] = value if isinstance(value, np.ndarray) else self.turn(value)
                posed[name] = base @ rotation
        return posed

    def skin(self, posed, rows=None):
        """The hand's skin with its finger bones posed, or the given rows of it: each point moved
        by its bones, by their weights."""
        points = self.points if rows is None else self.points[rows]
        weights = self.weights if rows is None else self.weights[rows]
        out = np.zeros_like(points)
        homogeneous = np.column_stack([points, np.ones(len(points))])
        for column, name in enumerate(self.names):
            weight = weights[:, column]
            if not weight.any():
                continue
            move = posed[name] @ self.inverse[name] if name in posed else np.eye(4)
            out += weight[:, None] * (homogeneous @ move.T)[:, :3]
        return out

    def _ends(self, posed, bone):
        return posed[bone][:3, 3], (posed[bone] @ self.inverse[bone] @ np.append(self.tail[bone], 1.0))[:3]

    def pads(self, turns, digits=DIGITS):
        """Per finger bone of the given digits, how far the nearest skin of its pad and its pad's
        skin on average lie outside the handle, and for the thumb's two outer segments outside the
        handle and the fingers, each finger segment a capsule as thick as its skin; negative
        inside."""
        posed = self.poses(turns)
        found = {}
        for finger in FINGERS:
            if finger not in digits:
                continue
            for segment in SEGMENTS:
                name = f"{finger}{segment}_R"
                gap = cylinder_gap(self.skin(posed, self.pad[name]), self.origin, self.axis, HANDLE / 2.0)
                found[name] = (float(gap.min()), float(gap.mean()))
        for segment in SEGMENTS[1:] if "Thumb" in digits else ():
            name = f"Thumb{segment}_R"
            points = self.skin(posed, self.pad[name])
            gap = cylinder_gap(points, self.origin, self.axis, HANDLE / 2.0)
            for finger in FINGERS:
                for part in SEGMENTS:
                    bone = f"{finger}{part}_R"
                    gap = np.minimum(gap, capsule_gap(points, *self._ends(posed, bone), self.radius[bone]))
            found[name] = (float(gap.min()), float(gap.mean()))
        return found

    def between(self, turns, finger, neighbour):
        """How far apart two fingers lie, each segment a capsule as thick as its skin, from the
        outer two segments of the one to the nearest of the other: negative where they overlap."""
        posed = self.poses(turns)
        gap = np.inf
        for segment in SEGMENTS[1:]:
            bone = f"{finger}{segment}_R"
            head, tail = self._ends(posed, bone)
            points = head + np.linspace(0.0, 1.0, 9)[:, None] * (tail - head)
            for part in SEGMENTS:
                other = f"{neighbour}{part}_R"
                gap = min(gap, float(capsule_gap(points, *self._ends(posed, other), self.radius[other]).min()) - self.radius[bone])
        return gap


def measure(hand, turns):
    """How the pads of a posed hand lie on what it holds, in metres: per finger the nearest skin
    and the average skin of the pad of each of its three segments from the handle, for the thumb
    those of its two outer segments from the handle and the fingers, and under "between" how far
    each finger lies from its neighbour toward the middle finger."""
    pads = hand.pads(turns)
    found = {finger: [pads[f"{finger}{segment}_R"] for segment in SEGMENTS] for finger in FINGERS}
    found["Thumb"] = [pads[f"Thumb{segment}_R"] for segment in SEGMENTS[1:]]
    found["between"] = {finger: hand.between(turns, finger, neighbour) for finger, neighbour in NEIGHBOURS}
    return found


def fit(hand):
    """Each digit's closing around the handle, and each finger's turns at its knuckle to close
    across it and lie against its neighbour, as finger_turns takes them."""
    across = hand.across()
    square = dict(across)
    angles = {finger: (0.0, 0.0, 0.0) for finger in FINGERS}
    angles["Thumb"] = (0.0, 0.0, 0.0)

    def close(finger):
        closing = [0.0, 0.0, 0.0]
        for joint, segment in enumerate(SEGMENTS):
            def gap(value, joint=joint, segment=segment):
                trial = dict(angles, **{finger: tuple(closing[:joint] + [value] + closing[joint + 1:])})
                return hand.pads(finger_turns(trial, across), (finger,))[f"{finger}{segment}_R"][0] - TOUCH

            closing[joint] = first_contact(gap, 0.0, CLOSED[joint])
        return tuple(closing)

    for _ in range(PASSES):
        for finger in FINGERS:
            angles[finger] = close(finger)
        for finger, neighbour in NEIGHBOURS:
            def apart(aside, finger=finger, neighbour=neighbour):
                beside = dict(across, **{finger: (aside, across[finger][1])})
                gap = hand.between(finger_turns(angles, beside), finger, neighbour)
                return max(gap, 0.0) ** 2 + OVERLAPPING * max(-gap, 0.0) ** 2

            low, high = square[finger][0] - BESIDE, square[finger][0] + BESIDE
            across[finger] = (lowest(apart, ((low, high),), grid=21, rounds=4)[0], across[finger][1])
    for finger in FINGERS:
        angles[finger] = close(finger)

    def cost(toward, aside, middle):
        pads = hand.pads(finger_turns(dict(angles, Thumb=(toward, aside, middle)), across), ("Thumb",))
        return thumb_cost([pads[f"Thumb{segment}_R"] for segment in SEGMENTS[1:]])

    angles["Thumb"] = lowest(cost, (THUMB_TOWARD, THUMB_ASIDE, (0.0, THUMB_MIDDLE_CLOSED)))
    return angles, across


def report(measured, angles=None, across=None):
    """One line per digit: how far the nearest skin and the average skin of each of its pads lie
    from the handle, the thumb's from the handle and the fingers, and a finger from its neighbour,
    after its joints' turns when the pose was fitted here."""
    lines = []
    for digit in FINGERS + ("Thumb",):
        turned = ""
        if angles is not None and digit == "Thumb":
            toward, aside, middle = angles[digit]
            turned = (f"base {toward:.1f} toward the palm and {aside:.1f} to the side, middle {middle:.1f}, "
                      f"last {middle * THUMB_LAST:.1f} degrees; ")
        elif angles is not None:
            turned = ("to the side {:.1f} and about itself {:.1f}, knuckle {:.1f}, middle {:.1f}, last {:.1f} degrees; "
                      .format(*across[digit], *angles[digit]))
        pads = ", ".join(f"{nearest * 1000:+.1f} ({mean * 1000:+.1f} on average)" for nearest, mean in measured[digit])
        held = "the handle and the fingers" if digit == "Thumb" else "the handle"
        beside = f", {measured['between'][digit] * 1000:+.1f} mm from its neighbour" if digit in measured["between"] else ""
        lines.append(f"{CLIP} {digit}: {turned}pads {pads} mm from {held}{beside}")
    return lines


def problems(measured):
    """What is wrong with a grip: a finger with a pad in the handle or off it, fingers that overlap
    or stand apart, and a thumb that has gone into the handle or a finger."""
    found = []
    for finger in FINGERS:
        nearest = [near for near, _mean in measured[finger]]
        if max(nearest) > OFF:
            found.append(f"{CLIP}: a pad of the {finger.lower()} finger stays {max(nearest) * 1000:.1f} mm off the handle")
        if min(nearest) < -INSIDE:
            found.append(f"{CLIP}: the {finger.lower()} finger lies {-min(nearest) * 1000:.1f} mm inside the handle")
        apart = measured["between"].get(finger, 0.0)
        if apart > APART or apart < -OVERLAP:
            found.append(f"{CLIP}: the {finger.lower()} finger lies {abs(apart) * 1000:.1f} mm "
                         f"{'from' if apart > 0 else 'inside'} its neighbour")
    nearest = min(near for near, _mean in measured["Thumb"])
    if nearest < -THUMB_INSIDE:
        found.append(f"{CLIP}: the thumb lies {-nearest * 1000:.1f} mm inside the handle or a finger")
    return found


def read_hand(arm):
    """The right hand of the body that an animation library's armature moves."""
    from art_pipeline import poses

    bones = arm.data.bones
    fingers = [f"{digit}{segment}_R" for digit in DIGITS for segment in SEGMENTS]
    missing = [name for name in fingers + ["Hand_R", "Socket_MainHand"] if name not in bones]
    if missing:
        raise RuntimeError(f"{arm.name} lacks the bones {missing} for the {CLIP} pose")
    to_arm = arm.matrix_world.inverted()
    points, rows = [], []
    for obj in arm.children:
        if obj.type != "MESH" or not any(group.name in fingers for group in obj.vertex_groups):
            continue
        names = {group.index: group.name for group in obj.vertex_groups}
        place = to_arm @ obj.matrix_world
        for vertex in obj.data.vertices:
            weights = {names[g.group]: g.weight for g in vertex.groups if g.weight > 0.0}
            # The right hand: skin that a finger of it or the hand itself moves.
            if any(name in weights for name in fingers) or weights.get("Hand_R", 0.0) > 0.5:
                points.append(tuple(place @ vertex.co))
                rows.append(weights)
    if not points:
        raise RuntimeError(f"no body of {arm.name} has skin on the fingers of its right hand")
    names = sorted({name for row in rows for name in row})
    weights = np.array([[row.get(name, 0.0) for name in names] for row in rows])
    weights /= weights.sum(axis=1, keepdims=True)
    rests = {name: np.array(bones[name].matrix_local) for name in set(names) | set(fingers) | {"Hand_R"}}
    parents = {name: bones[name].parent.name for name in fingers}
    socket = np.array(bones["Socket_MainHand"].matrix_local)

    def turn(turns):
        return np.array(poses._turns("", turns, "R").to_matrix()) if turns else np.eye(3)

    return Hand(points, weights, names, rests, parents, socket[:3, 3], socket[:3, 1], turn)


def frame(angles, across):
    """The local rotations of the finger bones of both hands, the left hand's mirrored, and the
    hips at rest, as animate.bake takes a frame."""
    from art_pipeline import poses

    local = {}
    for bone, turns in finger_turns(angles, across).items():
        for side in ("R", "L"):
            local[f"{bone}_{side}"] = poses._turns(bone, turns, side)
    return local, (0.0, 0.0, 0.0)


def keyed_bones():
    """The bones the pose keys: the finger bones of both hands."""
    return {f"{digit}{segment}_{side}" for digit in DIGITS for segment in SEGMENTS for side in ("R", "L")}
