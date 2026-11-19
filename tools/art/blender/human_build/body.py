"""The coarse cage of a human body's right half, designed once and laid out on each body's own
reference by measures of that reference: the trunk as rows of sixteen around from the leg holes to
the base of the neck, an arm hole of eight around each shoulder, the arms as rings of eight, and
the legs as rings of ten. Every border between two body parts is a ring of the cage."""

import math

import numpy as np

from .net import FIXED, MIRROR, PINNED, Net
from .surface import Loop, Surface

ZONES = ("Head", "Neck", "Torso", "Pelvis", "UpperArms", "Forearms", "Hands", "Thighs", "Shins", "Feet")
ZONE = {name: index for index, name in enumerate(ZONES)}
X, Y, Z = np.eye(3)

# Rows of the trunk between the leg holes and the armpits. The row at NAVEL_ROW passes through the
# navel, so the centre line has a vertex there; the rows below it lie evenly between the leg holes
# and the navel, and the row at PELVIS_ROW is the border between the pelvis and the torso. The
# rows above the navel lie at TRUNK_SHARES of the height from the navel to the armpits, twice as
# close over the chest, from the fold under it to the armpit: rows as far apart as on the belly
# cut the round of a breast by 9 mm.
NAVEL_ROW = 4
TRUNK_SHARES = (0.25, 0.5, 0.625, 0.75, 0.875, 1.0)
PELVIS_ROW = 3
# The navel is looked for between these shares of the body's height: where the belly's centre line
# lies deepest behind the skin NAVEL_BESIDE to its side. It is a dent smaller than the faces of the
# subdivided net, so the faces at its vertex make room for a pocket: a ring on the dent's edge,
# NAVEL_RIM across and up from its middle, a ring at NAVEL_INNER of that, and the vertex in its
# depth. Lengths at 1.79 m.
NAVEL_BETWEEN, NAVEL_BESIDE = (0.56, 0.65), 0.025
NAVEL_RIM, NAVEL_INNER = (0.012, 0.013), 0.5
# The valley between the breasts is flat at its bottom and rises steeply, in less than the width of
# one face: between these rows of the trunk the faces at the centre line are parted by a line of
# vertices halfway to the next column.
CHEST_LINE = (8, 11)
TRUNK_COLUMNS = 8          # per half; column 4 is the side
SIDE = 4
ARM_RING = 8
LEG_RING = 10
# FAN_RINGS rings fan from the shoulder ring to the first ring square to the arm, and pass under
# the armpit this far below each other, at a body height of 1.79 m. Four rings keep the round of
# the shoulder: with three, its outline turned 30 degrees at one vertex.
FAN_RINGS = 4
FAN_STEP = 0.020
FAN_FIRST = 0.040
# How many rows of faces each stretch of a limb has, the same on every body, so every body of the
# cage has the same faces: from the last ring of the shoulder's fan to the elbow, from the elbow to
# the wrist, from the top of the thigh to the knee, and from the knee to the ankle.
UPPER_ARM_SEGMENTS, FOREARM_SEGMENTS, THIGH_SEGMENTS, SHIN_SEGMENTS = 2, 5, 5, 8
# The arm is measured along the line from the shoulder joint to the tip of its longest finger,
# however it is held. The wrist is its slimmest section between WRIST_BETWEEN as shares of that
# line, where the forearm ends and the hand begins, and the arm's first section of its own, clear
# of the armpit, lies at ARM_CLEAR.
WRIST_BETWEEN, ARM_CLEAR = (0.62, 0.84), 0.24


def _unit(v):
    v = np.asarray(v, float)
    return v / max(np.linalg.norm(v), 1e-12)


def _bisect(low, high, test, steps=14):
    """The value between low and high at which test turns from False to True."""
    for _ in range(steps):
        middle = (low + high) / 2
        if test(middle):
            high = middle
        else:
            low = middle
    return (low + high) / 2


class Measures:
    """What the cage needs to know about a reference body, found from its own surface."""

    def __init__(self, surface: Surface, joint=None):
        """joint is the right arm's shoulder joint, where the reference names it: a reference
        whose arms were turned out knows the point they were turned about."""
        self.surface = surface
        self.joint = None if joint is None else np.asarray(joint, float)
        self._skins = None
        points = surface.points
        self.height = float(points[:, 2].max())
        s = self.height / 1.79
        self.scale = s
        mid = max(surface.section((0, 0, 0), X), key=lambda l: l.length)
        self.mid = mid.oriented(X)
        # Where the trunk divides into the legs.
        self.leg_split = _bisect(0.70 * s, 0.95 * s, lambda z: self._central(z) is not None)
        # The armpit: the height at which the arm joins the trunk.
        self.armpit_z = _bisect(1.15 * s, 1.40 * s, lambda z: self._arm(z) is None)
        below = self.armpit_z - 0.004
        self.gap_x = (self._central(below).points[:, 0].max() + self._arm(below).points[:, 0].min()) / 2
        self.gap_heights = np.linspace(self.leg_split + 0.06 * s, below, 24)
        gaps, beside = [], []
        for z in self.gap_heights:
            trunk, arm = self._central(z), self._arm(z)
            gaps.append((trunk.points[:, 0].max() + arm.points[:, 0].min()) / 2 if arm is not None
                        else trunk.points[:, 0].max() + 0.05)
            beside.append(arm is not None)
        self.gap_values = np.array(gaps)
        # Whether the arm hangs beside the hips, so the hand lies beside the thigh.
        self.arm_hangs = beside[0]
        # The ring around the shoulder: the section in the plane of the gap between arm and chest.
        loops = surface.section((self.gap_x, 0, 0), X)
        target = np.array([self.gap_x, 0.0, self.armpit_z + 0.09 * s])
        self.shoulder_ring = min(loops, key=lambda l: np.linalg.norm(l.centre - target)).oriented(X)
        self.shoulder_top = float(self.shoulder_ring.points[:, 2].max())
        # The notch above the breastbone: where the front of the centre line lies farthest back
        # between the chest and the chin.
        heights = np.arange(self.armpit_z + 0.08 * s, self.armpit_z + 0.26 * s, 0.004)
        fronts = [self.mid_point(z, front=True)[1] for z in heights]
        self.notch_z = float(heights[int(np.argmin(fronts))])
        # The navel: where the belly's centre line lies deepest behind the skin beside it.
        heights = np.arange(NAVEL_BETWEEN[0] * self.height, NAVEL_BETWEEN[1] * self.height, 0.002)
        dents = [self.front(NAVEL_BESIDE * s, z)[1] - self.front(0.0, z)[1] for z in heights]
        self.navel_z = float(heights[int(np.argmax(dents))])
        self.navel_depth = float(max(dents))

    def _loops(self, z):
        return self.surface.section((0, 0, z), Z)

    def _central(self, z):
        """The one loop around the body's middle at a height, or None where the legs are apart."""
        for loop in self._loops(z):
            low, high = loop.points.min(0), loop.points.max(0)
            if low[0] < -0.02 and high[0] > 0.02 and loop.length > 0.3:
                return loop.oriented(Z)
        return None

    def _arm(self, z):
        """The right arm's own loop at a height, or None where it has joined the trunk."""
        best = None
        for loop in self._loops(z):
            if loop.centre[0] > 0.15 * self.scale and loop.points[:, 0].min() > 0.05 and loop.length > 0.12:
                if best is None or loop.length > best.length:
                    best = loop
        return None if best is None else best.oriented(Z)

    def gap(self, z):
        """The x between the trunk and the arm at a height; above the armpit the plane of the ring
        around the shoulder."""
        z = np.asarray(z, float)
        return np.where(z >= self.armpit_z - 0.004, self.gap_x, np.interp(z, self.gap_heights, self.gap_values))

    def front(self, x, z):
        """The point of the body's front at a height, this far to the right of the centre line."""
        hit = self.surface.ray((max(x, 1e-4), 1.0, z), -Y, 2.0)
        if hit is None:
            raise ValueError(f"the body has no front at x = {x}, z = {z}")
        return hit[0]

    def mid_point(self, z, front):
        """The point of the body's centre line at a height, at the front or at the back."""
        pts = self.mid.points
        nxt = np.roll(pts, -1, axis=0)
        found = []
        for a, b in zip(pts, nxt):
            if (a[2] - z) * (b[2] - z) <= 0 and a[2] != b[2]:
                found.append(a + (b - a) * ((z - a[2]) / (b[2] - a[2])))
        if not found:
            raise ValueError(f"the centre line has no point at z = {z}")
        return max(found, key=lambda p: p[1]) if front else min(found, key=lambda p: p[1])

    def column_point(self, z, azimuth, centre_y):
        """The point of the body's outline at a height in a direction from the body's axis: the
        azimuth runs from -90 degrees at the back over 0 at the right side to 90 at the front."""
        loop = self._central(z)
        direction = np.array([math.cos(math.radians(azimuth)), math.sin(math.radians(azimuth)), 0.0])
        point = loop.start_at(direction, Z, centre=(0.0, centre_y, z)).points[0].copy()
        if abs(abs(azimuth) - 90.0) < 1e-9:
            point[0] = 0.0
        return point

    def skins(self):
        """The parts of the reference that the cage's faces are laid onto."""
        if self._skins is None:
            surface = self.surface
            margin = 0.004
            self._skins = {
                "trunk": surface.part(lambda c: (c[:, 0] > -0.03) & (c[:, 0] < self.gap(c[:, 2]) + margin)),
                "arm": surface.part(lambda c: (c[:, 0] > self.gap(c[:, 2]) - margin) & (c[:, 2] > 0.35 * self.height)),
                # A hand that hangs beside the thigh is no part of the leg; where the arms are held
                # out nothing lies beside the legs, whose feet may stand wider than the hips.
                "leg": surface.part(lambda c: (c[:, 0] > 0.002) & (c[:, 2] < self.leg_split + 0.06 * self.scale)
                                    & ((c[:, 0] < self.gap(c[:, 2]) + margin) | (not self.arm_hangs))),
            }
            # The rows under the arm are laid onto the arm's skin by its nearest point.
            self._skins["armpit"] = self._skins["arm"]
        return self._skins


def _half_ring(loop: Loop, columns=TRUNK_COLUMNS, side=SIDE, side_y=None):
    """A trunk section's right half as points from the back centre over the side to the front
    centre: the side column at the point farthest out, or where the section passes side_y from
    the back to the front, and the others at even shares between."""
    loop = loop.oriented(Z).start_at(-Y, Z, centre=(0.0, loop.centre[1], loop.centre[2]))
    cumulative = np.concatenate([[0.0], np.cumsum(loop.lengths)])
    n = len(loop.points)
    half = loop.length / 2
    right = [i for i in range(n) if cumulative[i] <= half]
    if side_y is None:
        far = max(right, key=lambda i: loop.points[i][0])
    else:
        far = next((i for i in right if loop.points[i][1] >= side_y), right[-1])
    side_share = cumulative[far] / loop.length
    shares = [side_share * k / side for k in range(side)] + \
             [side_share + (0.5 - side_share) * k / (columns - side) for k in range(columns - side + 1)]
    points = loop.resample(len(shares), shares)
    points[0][0] = 0.0
    points[-1][0] = 0.0
    return points


def build_trunk(net: Net, m: Measures):
    """The trunk from the row above the leg holes to the row over the shoulders, with the arm hole."""
    s = m.scale
    surface = m.surface
    trunk = m.skins()["trunk"]
    bottom = m.leg_split + 0.035 * s
    top = m.armpit_z
    heights = [bottom + (m.navel_z - bottom) * row / NAVEL_ROW for row in range(NAVEL_ROW)]
    heights += [m.navel_z + share * (top - m.navel_z) for share in (0.0, *TRUNK_SHARES)]
    # The ring around the shoulder: eight points from the armpit over the front and the top.
    ring = m.shoulder_ring.start_at(-Z, X)
    hole_points = ring.resample(ARM_RING)
    rows = []
    for index, z in enumerate(heights[:-1]):
        # Up to the navel the side column lies where the body is widest. From there it makes
        # for the armpit, step by step, as the side seam of a shirt does: where the body is
        # widest lies on the breast or on the back's muscle higher up, and a column there
        # leaves the faces under the armpit sheared between it and the ring around the shoulder.
        side_y = None
        if index > NAVEL_ROW:
            share = (index - NAVEL_ROW) / (len(heights) - 1 - NAVEL_ROW)
            side_y = widest_y + share * (hole_points[0][1] - widest_y)
        points = _half_ring(m._central(z), side_y=side_y)
        if index == NAVEL_ROW:
            widest_y = points[SIDE][1]
        ring = [net.add(p, *([MIRROR] if k in (0, TRUNK_COLUMNS) else [])) for k, p in enumerate(points)]
        rows.append(ring)
    net.plane("waist", (0, 0, heights[PELVIS_ROW]), Z)
    net.hold(rows[PELVIS_ROW], ("plane", "waist"))

    gap_plane = net.plane("shoulder", (m.gap_x, 0, 0), X)
    hole = [net.add(p, gap_plane) for p in hole_points]
    net.mark("arm_hole", hole)

    def laid(guess, level):
        """A point between the hole and the centre line on the trunk's skin. A level row takes
        the skin straight out from the body's middle at the point's own height, so the row
        neither climbs nor dips over a breast, where the nearest skin lies above the point."""
        if level:
            z = guess[2]
            middle = np.array([0.0, (m.mid_point(z, front=True)[1] + m.mid_point(z, front=False)[1]) / 2, z])
            hit = trunk.ray(middle, guess - middle, 0.5)
            if hit is not None:
                return hit[0]
        return trunk.project(guess, reach=0.15)

    def row_through(back_z, front_z, back_hole, side_hole, front_hole, level=False):
        """A row at and above the armpit: from the back centre to the hole and on to the front."""
        back = m.mid_point(back_z, front=False)
        front = m.mid_point(front_z, front=True)
        ring = [net.add(back, MIRROR)]
        a = net.points[back_hole]
        for k in (1, 2):
            ring.append(net.add(laid(back + (a - back) * (k / 3), level)))
        ring += [back_hole, side_hole, front_hole]
        b = net.points[front_hole]
        for k in (1, 2):
            ring.append(net.add(laid(b + (front - b) * (k / 3), level)))
        ring.append(net.add(front, MIRROR))
        return ring

    # Rows a, a + 1, a + 2: at the armpit, through the middle of the hole, and over the shoulder.
    span = m.shoulder_top - m.armpit_z
    row_a = row_through(top, top, hole[7], hole[0], hole[1], level=True)
    row_b = row_through(top + 0.36 * span, top + 0.36 * span, hole[6], None, hole[2])
    row_c = row_through(top + 0.74 * span, top + 0.70 * span, hole[5], hole[4], hole[3])
    rows += [row_a, row_b, row_c]

    for index in range(len(rows) - 1):
        lower, upper = rows[index], rows[index + 1]
        zone = ZONE["Pelvis"] if index < PELVIS_ROW else ZONE["Torso"]
        for k in range(TRUNK_COLUMNS):
            corners = (lower[k], lower[k + 1], upper[k + 1], upper[k])
            if None in corners:
                continue
            # The arm hole takes the two faces beside the side column in the two rows above the armpit.
            if lower is row_a and k in (SIDE - 1, SIDE):
                continue
            if lower is row_b and k in (SIDE - 1, SIDE):
                continue
            net.island = "back" if k < SIDE else "front"
            net.quad(*corners, zone, "trunk")
    net.mark("trunk_bottom", rows[0])
    net.mark("trunk_top", rows[-1])
    net.mark("front_centre", [row[-1] for row in rows])
    net.mark("front_beside", [row[-2] for row in rows])
    return rows


def refine_trunk(fine: Net, coarse: Net, m: Measures):
    """Gives the subdivided trunk what is finer than its faces: the navel, as a pocket at the
    vertex of the centre line that lies in it, and a line of vertices beside the centre line over
    the chest, for the valley between the breasts."""
    s = m.scale
    centre, beside = coarse.marks["front_centre"], coarse.marks["front_beside"]
    by_corners = {frozenset(face): index for index, face in enumerate(fine.faces)}

    def between(a, b):
        return fine.edge_point[(a, b) if a < b else (b, a)]

    def column(half_row):
        """The subdivided net's vertex of the centre line and the one beside it, by half rows of
        the cage: an even half row is a row of the cage, an odd one lies between two."""
        row, odd = divmod(half_row, 2)
        if not odd:
            return centre[row], between(centre[row], beside[row])
        corners = {centre[row], beside[row], centre[row + 1], beside[row + 1]}
        face = next(index for index, face in enumerate(coarse.faces) if set(face) == corners)
        return between(centre[row], centre[row + 1]), fine.face_point[face]

    def put(like, pieces, turned):
        """Faces in the place of the face like: the first takes its place, the others follow."""
        fine.island = fine.islands[like]
        for index, piece in enumerate(pieces):
            piece = tuple(reversed(piece)) if turned else tuple(piece)
            if index == 0:
                fine.faces[like] = piece
            else:
                fine.face(piece, fine.zones[like], fine.skins[like])

    # The line beside the centre line over the chest: each face at the centre line is parted in
    # two, and a triangle begins and ends the line.
    first, last = 2 * CHEST_LINE[0], 2 * CHEST_LINE[1]
    line = {}
    for half_row in range(first, last + 1):
        on, off = column(half_row)
        line[half_row] = fine.add((np.asarray(fine.points[on], float) + np.asarray(fine.points[off], float)) / 2)
    for half_row in range(first - 1, last + 1):
        (c0, m0), (c1, m1) = column(half_row), column(half_row + 1)
        like = by_corners[frozenset((c0, m0, m1, c1))]
        face = fine.faces[like]
        turned = face[(face.index(c0) + 1) % 4] != m0
        n0, n1 = line.get(half_row), line.get(half_row + 1)
        if n0 is None:
            put(like, [(c0, m0, m1, n1), (c0, n1, c1)], turned)
        elif n1 is None:
            put(like, [(n0, m0, m1, c1), (c0, n0, c1)], turned)
        else:
            put(like, [(c0, n0, n1, c1), (n0, m0, m1, n1)], turned)

    # The navel: the two faces at its vertex become rings of faces around it.
    half_row = 2 * NAVEL_ROW
    (below, below_right), (navel, right), (above, above_right) = (column(half_row + step) for step in (-1, 0, 1))
    around = [below, below_right, right, above_right, above]
    spokes = [math.radians(angle) for angle in (-90.0, -45.0, 0.0, 45.0, 90.0)]

    def ring(share):
        out = []
        for index, spoke in enumerate(spokes):
            on_centre = index in (0, len(spokes) - 1)
            x = 0.0 if on_centre else NAVEL_RIM[0] * s * share * math.cos(spoke)
            point = m.front(x, m.navel_z + NAVEL_RIM[1] * s * share * math.sin(spoke))
            if on_centre:
                point[0] = 0.0
            out.append(fine.add(point, FIXED, PINNED, *([MIRROR] if on_centre else [])))
        return out

    rim, inner = ring(1.0), ring(NAVEL_INNER)
    depth = m.front(0.0, m.navel_z)
    depth[0] = 0.0
    fine.points[navel] = depth
    fine.holds[navel].update((FIXED, PINNED))
    for like, spans in ((by_corners[frozenset((below, below_right, right, navel))], (0, 1)),
                        (by_corners[frozenset((navel, right, above_right, above))], (2, 3))):
        face = fine.faces[like]
        turned = face[(face.index(right) + 1) % 4] != (navel if spans[0] == 0 else above_right)
        pieces = []
        for k in spans:
            pieces += [(around[k], around[k + 1], rim[k + 1], rim[k]), (rim[k], rim[k + 1], inner[k + 1], inner[k]),
                       (inner[k], inner[k + 1], navel)]
        put(like, pieces, turned)


def _arm_axis(m: Measures):
    """The shoulder joint, the elbow, and the wrist of the right arm, from the arm's own sections,
    however the arm is held."""
    s = m.scale
    surface = m.surface
    arm = m.skins()["arm"]
    # The joint lies inside the round of the shoulder, level with the middle of the ring around
    # it, on a body whose arms hang; a reference whose arms were turned out names it.
    ring = m.shoulder_ring
    joint = m.joint
    if joint is None:
        joint = np.array([m.gap_x + 0.035 * s, ring.centre[1], m.armpit_z + 0.75 * (m.shoulder_top - m.armpit_z)])
    # The arm's line: from the joint to the tip of the longest finger.
    points = arm.points[np.unique(arm.triangles)]
    tip = points[int(np.argmax(np.linalg.norm(points - joint, axis=1)))]
    reach = float(np.linalg.norm(tip - joint))
    along = (tip - joint) / reach

    def section(share):
        origin = joint + along * (share * reach)
        return arm.loop(origin, along, near=origin)

    # The wrist: the arm's slimmest section before the hand.
    best = None
    for share in np.arange(WRIST_BETWEEN[0], WRIST_BETWEEN[1], 0.005):
        loop = section(share)
        if loop is not None and loop.length > 0.12 * s and (best is None or loop.length < best[0]):
            best = (loop.length, loop.centre)
    if best is None:
        raise RuntimeError("the arm has no section of its own where its wrist should be")
    wrist = best[1]
    top = section(ARM_CLEAR).centre
    # The elbow: where the line of section centres bends most away from the straight arm.
    direction = _unit(wrist - top)
    worst = None
    for share in np.linspace(0.25, 0.75, 21):
        origin = top + (wrist - top) * share
        loop = surface.loop(origin, direction, near=origin)
        if loop is None:
            continue
        away = loop.centre - origin
        away -= (away @ direction) * direction
        if worst is None or np.linalg.norm(away) > worst[0]:
            worst = (np.linalg.norm(away), loop.centre)
    elbow = worst[1]
    return joint, elbow, wrist, surface


def build_arm(net: Net, m: Measures, to_wrist=True, wrist_start=None):
    """The arm from the ring around the shoulder to the wrist: rings that fan about the armpit
    from that ring to the first one square to the arm, then rings square to the arm."""
    s = m.scale
    joint, elbow, wrist, surface = _arm_axis(m)
    hole = net.marks["arm_hole"]
    upper = _unit(elbow - joint)
    lower = _unit(wrist - elbow)
    # The fan turns about a line just below the armpit, from the plane of the shoulder ring to
    # the plane square to the upper arm. The first ring passes FAN_FIRST below the armpit: nearer,
    # its faces under the arm are slivers 3 mm wide.
    pivot = np.array([m.gap_x + 0.003 * s, 0.0, m.armpit_z - FAN_FIRST * s])
    angle = math.acos(float(np.clip(X @ upper, -1.0, 1.0)))
    turn_axis = _unit(np.cross(X, upper))

    def turned(share):
        a = angle * share
        return X * math.cos(a) + np.cross(turn_axis, X) * math.sin(a) + turn_axis * (turn_axis @ X) * (1 - math.cos(a))

    def ring_at(origin, normal, near, start, name=None):
        loop = surface.loop(origin, normal, near=near)
        loop = loop.oriented(normal).start_at(start, normal)
        holds = [net.plane(name, origin, normal)] if name else []
        return net.add_ring(loop.resample(ARM_RING), *holds), loop

    rings = [hole]
    previous_centre = m.shoulder_ring.centre
    for step in range(1, FAN_RINGS + 1):
        share = step / FAN_RINGS
        normal = turned(share)
        near = previous_centre + normal * 0.03 * s
        # Each ring of the fan passes through its own point under the armpit, a step further
        # down, so it meets the inside of the arm a step further out and the hollow of the armpit
        # keeps skin to stretch when the arm rises.
        through = pivot - Z * (FAN_STEP * (step - 1) * s)
        ring, loop = ring_at(through, normal, near, -Z if share < 1 else -X)
        rings.append(ring)
        previous_centre = loop.centre
    # Down the upper arm to the elbow, across it, and down the forearm.
    first = previous_centre
    stations = []
    count = UPPER_ARM_SEGMENTS
    for k in range(1, count):
        stations.append((first + (elbow - first) * (k / count), upper, None))
    bisector = _unit(upper + lower)
    stations.append((elbow, bisector, "elbow"))
    starts = [-X] * len(stations)
    if to_wrist:
        # Down the forearm the rings' first points turn from the side toward the body to the
        # hand's palm side, as the forearm's skin turns with the hand.
        end = -X if wrist_start is None else np.asarray(wrist_start, float)
        count = FOREARM_SEGMENTS
        for k in range(1, count):
            stations.append((elbow + (wrist - elbow) * (k / count), lower, None))
            starts.append(_unit(-X + (end + X) * (k / count)))
        stations.append((wrist, lower, "wrist"))
        starts.append(end)
    for (origin, normal, name), start in zip(stations, starts):
        ring, loop = ring_at(origin, normal, origin, start, name)
        rings.append(ring)
    elbow_index = 1 + FAN_RINGS + [name for _o, _n, name in stations].index("elbow")
    # The arm is one island of the UV map, opened along its inner line.
    net.island = "arm_R"
    net.seam([ring[0] for ring in rings])
    for index in range(len(rings) - 1):
        zone = ZONE["UpperArms"] if index < elbow_index else ZONE["Forearms"]
        # The first two rows lie under the arm, at the armpit.
        net.strip(rings[index], rings[index + 1], zone, "armpit" if index < 2 else "arm")
    net.mark("elbow", rings[elbow_index])
    net.mark("arm_end", rings[-1])
    return rings, (joint, elbow, wrist)


def build_neck(net: Net, m: Measures, top=None):
    """The rows from the one over the shoulders to the middle of the neck: the collar ring, which
    is the border between the torso and the neck, and a ring halfway from it to the ring under the
    head, whose points are given from the back centre over the right side to the front centre.
    The collar's columns lie evenly around the neck; the middle ring's lie halfway between them
    and the directions of the ring under the head, which the head gives."""
    s = m.scale
    lower = net.marks["trunk_top"]
    centre_y = 0.015 * s
    notch, side, back = m.notch_z, m.notch_z + 0.052 * s, m.notch_z + 0.045 * s
    even = [-90.0 + 180.0 * k / TRUNK_COLUMNS for k in range(TRUNK_COLUMNS + 1)]

    def heights(front_z, side_z, back_z):
        out = []
        for azimuth in even:
            turn = math.sin(math.radians(azimuth)) ** 2
            out.append(side_z + ((front_z if azimuth > 0 else back_z) - side_z) * turn)
        return out

    def ring(at, azimuths, around):
        out = []
        for k, (z, azimuth) in enumerate(zip(at, azimuths)):
            holds = [MIRROR] if k in (0, TRUNK_COLUMNS) else []
            out.append(net.add(m.column_point(z, azimuth, around), *holds))
        return out

    collar_at = heights(notch, side, back)
    if top is None:
        middle_at = heights(notch + 0.045 * s, side + 0.035 * s, back + 0.045 * s)
        middle_azimuths, middle_around = even, centre_y
    else:
        top = np.asarray(top, float)
        top_around = (top[:, 1].min() + top[:, 1].max()) / 2
        towards = [math.degrees(math.atan2(p[1] - top_around, p[0])) for p in top]
        towards[0], towards[-1] = -90.0, 90.0
        middle_at = [(z + float(point[2])) / 2 for z, point in zip(collar_at, top)]
        middle_azimuths = [(a + b) / 2 for a, b in zip(even, towards)]
        middle_around = (centre_y + top_around) / 2
    collar = ring(collar_at, even, centre_y)
    middle = ring(middle_at, middle_azimuths, middle_around)
    # A row halfway up the slope from the shoulder to the neck, so the slope and the collarbone
    # keep their curve.
    trunk = m.skins()["trunk"]
    slope = []
    for k, (a, b) in enumerate(zip(lower, collar)):
        point = trunk.project((np.asarray(net.points[a], float) + np.asarray(net.points[b], float)) / 2, reach=0.15)
        if k in (0, TRUNK_COLUMNS):
            point[0] = 0.0
        slope.append(net.add(point, *([MIRROR] if k in (0, TRUNK_COLUMNS) else [])))
    sides = ["back"] * SIDE + ["front"] * (TRUNK_COLUMNS - SIDE)
    net.strip(lower, slope, ZONE["Torso"], "trunk", closed=False, islands=sides)
    lower = slope
    net.strip(lower, collar, ZONE["Torso"], "trunk", closed=False, islands=sides)
    # The neck is one island, opened down the nape.
    net.island = "neck"
    net.seam([collar[0], middle[0]])
    net.strip(collar, middle, ZONE["Neck"], "trunk", closed=False)
    net.mark("collar", collar)
    net.mark("neck_end", middle)
    return collar, middle


def build_legs(net: Net, m: Measures):
    """The bridge under the crotch, which leaves a leg hole of ten around, and the leg from that
    hole to the ankle: rings square to the leg, with one at the top of the thigh, one at the knee,
    and one at the ankle as the borders between the body parts."""
    s = m.scale
    surface = m.surface
    trunk = m.skins()["trunk"]
    row = net.marks["trunk_bottom"]
    front, back = net.points[row[TRUNK_COLUMNS]], net.points[row[0]]
    # The centre line under the crotch, from the front of the bottom row to its back.
    line = m.mid.points
    under = line[line[:, 2] <= front[2] + 1e-6]
    under = under[(under[:, 1] <= front[1] + 1e-6) & (under[:, 1] >= back[1] - 1e-6) & (under[:, 2] > 0.5 * s)]
    under = under[np.argsort(-under[:, 1])]
    path = np.concatenate([[front], under, [back]])
    steps = np.concatenate([[0.0], np.cumsum(np.linalg.norm(np.diff(path, axis=0), axis=1))])
    centres, sides = [row[TRUNK_COLUMNS]], [row[TRUNK_COLUMNS - 1]]
    for k in (1, 2, 3):
        target = steps[-1] * k / 4
        i = int(np.searchsorted(steps, target)) - 1
        t = (target - steps[i]) / max(steps[i + 1] - steps[i], 1e-12)
        point = path[i] + t * (path[i + 1] - path[i])
        point[0] = 0.0
        centres.append(net.add(point, MIRROR))
        sides.append(net.add(trunk.project(point + np.array([0.05 * s, 0.0, 0.012 * s]), reach=0.2)))
    centres.append(row[0])
    sides.append(row[1])
    for k in range(4):
        net.island = "front" if k < 2 else "back"
        net.quad(centres[k], sides[k], sides[k + 1], centres[k + 1], ZONE["Pelvis"], "trunk")
    # The hole, from its innermost vertex over the front and the outside to the back.
    hole = [sides[2], sides[1]] + [row[k] for k in range(TRUNK_COLUMNS - 1, 0, -1)] + [sides[3]]

    def leg_centre(z):
        loops = [l for l in surface.section((0, 0, z), Z) if l.centre[0] > 0.03 and l.length > 0.15 * s
                 and l.points[:, 0].max() < m.gap(z)]
        return max(loops, key=lambda l: l.length).centre

    hip = leg_centre(m.leg_split - 0.035 * s)
    knee = leg_centre(0.28 * m.height)
    ankle = leg_centre(0.115 * s)
    upper, lower = _unit(knee - hip), _unit(ankle - knee)
    # Every ring of the leg starts on the leg's inner line.
    start = -X

    def ring_at(origin, normal, name=None):
        loop = surface.loop(origin, normal, near=origin).oriented(normal).start_at(start, normal)
        holds = [net.plane(name, origin, normal)] if name else []
        return net.add_ring(loop.resample(LEG_RING), *holds)

    rings = [hole]
    stations = [(hip, upper, "leg_top")]
    count = THIGH_SEGMENTS
    for k in range(1, count):
        stations.append((hip + (knee - hip) * (k / count), upper, None))
    stations.append((knee, _unit(upper + lower), "knee"))
    count = SHIN_SEGMENTS
    for k in range(1, count):
        stations.append((knee + (ankle - knee) * (k / count), lower, None))
    stations.append((ankle, lower, "ankle"))
    for origin, normal, name in stations:
        rings.append(ring_at(origin, normal, name))
    names = [name for _o, _n, name in stations]
    knee_index = 1 + names.index("knee")
    # The row under the trunk belongs to the trunk's front and back; the leg below it is one island
    # of the UV map, opened along its inner line.
    net.seam([ring[0] for ring in rings[1:]])
    for index in range(len(rings) - 1):
        zone = ZONE["Pelvis"] if index == 0 else ZONE["Thighs"] if index < knee_index else ZONE["Shins"]
        net.island = "leg_R"
        net.strip(rings[index], rings[index + 1], zone, "leg",
                  islands=["front"] * 5 + ["back"] * 5 if index == 0 else None)
    net.mark("leg_end", rings[-1])
    return rings, (hip, knee, ankle)
