"""The coarse cage of a human body's right half, designed once and laid out on each body's own
reference by measures of that reference: the trunk as rows of sixteen around from the leg holes to
the base of the neck, an arm hole of eight around each shoulder, the arms as rings of eight, and
the legs as rings of ten. Every border between two body parts is a ring of the cage."""

import math

import numpy as np

from .net import MIRROR, Net
from .surface import Loop, Surface

ZONES = ("Head", "Neck", "Torso", "Pelvis", "UpperArms", "Forearms", "Hands", "Thighs", "Shins", "Feet")
ZONE = {name: index for index, name in enumerate(ZONES)}
X, Y, Z = np.eye(3)

# Rows of the trunk between the leg holes and the armpits, as shares of that height; the row at
# PELVIS_ROW is the border between the pelvis and the torso. The rows lie twice as close over the
# chest, from the fold under it to the armpit: rows as far apart as on the belly cut the round of
# a breast by 9 mm.
TRUNK_SHARES = (0.0, 0.125, 0.25, 0.375, 0.5, 0.625, 0.75, 0.8125, 0.875, 0.9375, 1.0)
PELVIS_ROW = 3
TRUNK_COLUMNS = 8          # per half; column 4 is the side
SIDE = 4
ARM_RING = 8
LEG_RING = 10
# FAN_RINGS rings fan from the shoulder ring to the first ring square to the arm, and cross the gap
# beside the chest this far below each other, at a body height of 1.79 m. Four rings keep the
# round of the shoulder: with three, its outline turned 30 degrees at one vertex.
FAN_RINGS = 4
FAN_STEP = 0.020
# How many rows of faces each stretch of a limb has, the same on every body, so every body of the
# cage has the same faces: from the last ring of the shoulder's fan to the elbow, from the elbow to
# the wrist, from the top of the thigh to the knee, and from the knee to the ankle.
UPPER_ARM_SEGMENTS, FOREARM_SEGMENTS, THIGH_SEGMENTS, SHIN_SEGMENTS = 2, 5, 5, 8


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

    def __init__(self, surface: Surface):
        self.surface = surface
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
        gaps = []
        for z in self.gap_heights:
            trunk, arm = self._central(z), self._arm(z)
            gaps.append((trunk.points[:, 0].max() + arm.points[:, 0].min()) / 2 if arm is not None
                        else trunk.points[:, 0].max() + 0.05)
        self.gap_values = np.array(gaps)
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
                "leg": surface.part(lambda c: (c[:, 0] > 0.002) & (c[:, 0] < self.gap(c[:, 2]) + margin)
                                    & (c[:, 2] < self.leg_split + 0.06 * self.scale)),
            }
        return self._skins


def _half_ring(loop: Loop, columns=TRUNK_COLUMNS, side=SIDE):
    """A trunk section's right half as points from the back centre over the side to the front
    centre: the side column at the point farthest out, the others at even shares between."""
    loop = loop.oriented(Z).start_at(-Y, Z, centre=(0.0, loop.centre[1], loop.centre[2]))
    cumulative = np.concatenate([[0.0], np.cumsum(loop.lengths)])
    n = len(loop.points)
    half = loop.length / 2
    right = [i for i in range(n) if cumulative[i] <= half]
    far = max(right, key=lambda i: loop.points[i][0])
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
    heights = [bottom + share * (top - bottom) for share in TRUNK_SHARES]
    rows = []
    for index, z in enumerate(heights[:-1]):
        points = _half_ring(m._central(z))
        ring = [net.add(p, *([MIRROR] if k in (0, TRUNK_COLUMNS) else [])) for k, p in enumerate(points)]
        rows.append(ring)
    net.plane("waist", (0, 0, heights[PELVIS_ROW]), Z)
    net.hold(rows[PELVIS_ROW], ("plane", "waist"))

    # The ring around the shoulder: eight points from the armpit over the front and the top.
    ring = m.shoulder_ring.start_at(-Z, X)
    hole_points = ring.resample(ARM_RING)
    gap_plane = net.plane("shoulder", (m.gap_x, 0, 0), X)
    hole = [net.add(p, gap_plane) for p in hole_points]
    net.mark("arm_hole", hole)

    def row_through(back_z, front_z, back_hole, side_hole, front_hole):
        """A row at and above the armpit: from the back centre to the hole and on to the front."""
        back = m.mid_point(back_z, front=False)
        front = m.mid_point(front_z, front=True)
        ring = [net.add(back, MIRROR)]
        a = net.points[back_hole]
        for k in (1, 2):
            guess = back + (a - back) * (k / 3)
            ring.append(net.add(trunk.project(guess, reach=0.15)))
        ring += [back_hole, side_hole, front_hole]
        b = net.points[front_hole]
        for k in (1, 2):
            guess = b + (front - b) * (k / 3)
            ring.append(net.add(trunk.project(guess, reach=0.15)))
        ring.append(net.add(front, MIRROR))
        return ring

    # Rows a, a + 1, a + 2: at the armpit, through the middle of the hole, and over the shoulder.
    span = m.shoulder_top - m.armpit_z
    row_a = row_through(top, top, hole[7], hole[0], hole[1])
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
            net.quad(*corners, zone, "trunk")
    net.mark("trunk_bottom", rows[0])
    net.mark("trunk_top", rows[-1])
    return rows


def _arm_axis(m: Measures):
    """The shoulder joint, the elbow, and the wrist of the right arm, from the arm's own sections."""
    s = m.scale
    surface = m.surface
    # The wrist: the slimmest horizontal section above the hand.
    best = None
    for z in np.arange(0.86 * s, 1.02 * s, 0.005):
        loop = m._arm(z)
        if loop is not None and loop.length > 0.12 * s and (best is None or loop.length < best[0]):
            best = (loop.length, loop.centre)
    wrist = best[1]
    top = m._arm(m.armpit_z - 0.02 * s).centre
    # The joint lies inside the round of the shoulder, level with the middle of the ring around it.
    ring = m.shoulder_ring
    joint = np.array([m.gap_x + 0.035 * s, ring.centre[1], m.armpit_z + 0.75 * (m.shoulder_top - m.armpit_z)])
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
    # The fan turns about a line through the gap just below the armpit, from the plane of the
    # shoulder ring to the plane square to the upper arm.
    pivot = np.array([m.gap_x + 0.003 * s, 0.0, m.armpit_z - 0.022 * s])
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
        # Each ring of the fan passes through its own point of the gap, a step further down the
        # inside of the arm, so the hollow of the armpit keeps skin to stretch when the arm rises.
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
    for index in range(len(rings) - 1):
        zone = ZONE["UpperArms"] if index < elbow_index else ZONE["Forearms"]
        net.strip(rings[index], rings[index + 1], zone, "arm")
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
    net.strip(lower, slope, ZONE["Torso"], "trunk", closed=False)
    lower = slope
    net.strip(lower, collar, ZONE["Torso"], "trunk", closed=False)
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
    for index in range(len(rings) - 1):
        zone = ZONE["Pelvis"] if index == 0 else ZONE["Thighs"] if index < knee_index else ZONE["Shins"]
        net.strip(rings[index], rings[index + 1], zone, "leg")
    net.mark("leg_end", rings[-1])
    return rings, (hip, knee, ankle)
