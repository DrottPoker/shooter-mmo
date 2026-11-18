"""The cage of the right hand, from the wrist ring of eight to the finger tips.

The palm has two rings of ten: at the knuckles and at the middle of the palm, laid out as four
faces across the palm, four across the back, and one at each side. Two triangles beside the wrist
take the ring of eight to ten there, where the hand bends least. The ring at the webs is a ladder:
each finger's first ring is one rung to the next, so neighbouring fingers share the edge through
their web. The thumb grows from the face at the palm's thumb side between the two palm rings.
Each finger has rings of four at its web, on both sides of its two joints, and before its tip, so
the subdivided net has a ring exactly at each joint and one on either side of it."""

import numpy as np

from . import digits
from .body import ZONE, _arm_axis, _unit
from .net import Net

FINGERS = ("Index", "Middle", "Ring", "Little")
# Rings of a digit at shares of its free length, measured from the tip (0) to the web (1).
TIP_RING = 0.14
WEB_RING = 0.97
JOINT_HALF = 0.065
FINGER_JOINTS = ((0.30, (0.22, 0.38)), (0.675, (0.58, 0.76)))
THUMB_JOINT = (0.50, (0.40, 0.62))
# The knuckle ring lies this far before the webs and the mid palm ring at this share of the way
# from the wrist to it, at a body height of 1.79 m.
KNUCKLES_BEFORE_WEB = 0.022
MID_PALM = 0.46
# The thumb's root: the four corners of its section at the web, moved this far into the palm.
THUMB_ROOT = 0.018


def _distance_to_path(points, path):
    best = np.full(len(points), np.inf)
    for a, b in zip(path[:-1], path[1:]):
        ab = b - a
        t = np.clip(((points - a) @ ab) / max(ab @ ab, 1e-18), 0.0, 1.0)
        best = np.minimum(best, np.linalg.norm(points - (a + t[:, None] * ab), axis=1))
    return best


class Hand:
    """The right hand of a reference body: its fingers and thumb traced, and its own directions."""

    def __init__(self, m):
        s = m.scale
        self.scale = s
        _joint, elbow, wrist, _surface = _arm_axis(m)
        forearm = _unit(wrist - elbow)
        self.wrist = wrist
        self.surface = m.skins()["arm"].part(lambda c: (c - wrist) @ forearm > -0.03 * s)
        points = self.surface.points[np.unique(self.surface.triangles)]
        lowest = points[int(np.argmax((points - wrist) @ forearm))]
        axis = _unit(lowest - wrist)
        found = sorted(digits.tips(self.surface, axis, wrist), key=lambda d: -d["height"])
        if len(found) < 5:
            raise RuntimeError(f"the hand shows {len(found)} digits, expected five")
        fingers, rest = found[:4], found[4:]
        thumb = max(rest, key=lambda d: d["count"])
        radial = thumb["first"] - np.mean([f["first"] for f in fingers], axis=0)
        radial = _unit(radial - (radial @ axis) * axis)
        fingers.sort(key=lambda d: -(d["first"] @ radial))
        self.fingers = []
        for finger in fingers:
            tip = digits.farthest(self.surface, finger["first"], finger["direction"], 0.011 * s)
            self.fingers.append(digits.march(self.surface, tip, finger["direction"]))
        guess = digits.farthest(self.surface, thumb["first"], 0.8 * radial + 0.6 * axis, 0.016 * s)
        base = wrist + axis * (0.03 * s) + radial * (0.02 * s)
        direction = _unit(guess - base)
        tip = digits.farthest(self.surface, guess, direction, 0.012 * s)
        self.thumb = digits.march(self.surface, tip, direction)
        webs = [finger.path[-1] for finger in self.fingers]
        self.axis = _unit(np.mean(webs, axis=0) - wrist)
        across = webs[0] - webs[3]
        self.radial = _unit(across - (across @ self.axis) * self.axis)
        self.palmar = np.cross(self.radial, self.axis)
        self.web_height = float(np.mean([(w - wrist) @ self.axis for w in webs]))

    def report(self):
        lines = [f"hand axis {np.round(self.axis, 3).tolist()}, radial {np.round(self.radial, 3).tolist()}, "
                 f"palmar {np.round(self.palmar, 3).tolist()}, webs {self.web_height * 1000:.0f} mm from the wrist"]
        for name, digit in zip(FINGERS + ("Thumb",), self.fingers + [self.thumb]):
            lines.append(f"{name}: tip {np.round(digit.tip, 3).tolist()}, free length {digit.length * 1000:.0f} mm, "
                         f"girth at the web {digit.girths[-1] * 1000:.0f} mm")
        return lines

    def skins(self):
        """The whole hand for the palm, and for each digit the skin around its own line."""
        triangles = self.surface.triangles
        centres = self.surface.points[triangles].mean(1)
        all_digits = self.fingers + [self.thumb]
        distances = np.array([_distance_to_path(centres, d.path) for d in all_digits])
        nearest = distances.argmin(0)
        out = {"palm": self.surface}
        for index, (name, digit) in enumerate(zip(FINGERS + ("Thumb",), all_digits)):
            inward = digit.direction(1.0, window=0.2)
            beyond = (centres - digit.path[-1]) @ inward > -0.008 * self.scale
            keep = (nearest == index) & (distances[index] < 0.018 * self.scale) & beyond
            out[f"digit_{name}"] = type(self.surface)(self.surface.points, triangles[keep])
        return out


def _ring_of_four(digit, share, palmar, radial):
    """A digit's section at a share of its length as four points: the palmar corner on the little
    finger's side first, then the palmar corner on the thumb's side, and on around the back."""
    loop, _origin, normal = digit.loop(share)
    start = palmar - radial
    return loop.start_at(start - (start @ normal) * normal, normal).resample(4)


def _thumb_ring(thumb, share, start):
    """The thumb's section at a share of its length as four points, the first toward a direction."""
    loop, _origin, normal = thumb.loop(share)
    return loop.start_at(start - (start @ normal) * normal, normal).resample(4)


def _tip(net, ring, digit, zone, skin, name):
    """Closes a digit with four triangles from its last ring to its tip; subdivided, they round it."""
    tip = net.add(digit.tip)
    for k in range(4):
        net.face((ring[k], ring[(k + 1) % 4], tip), zone, skin)
    net.mark(f"{name}_tip", [tip])


def build_hand(net: Net, hand: Hand):
    s = hand.scale
    zone = ZONE["Hands"]
    wrist_ring = net.marks["arm_end"]
    axis, radial, palmar = hand.axis, hand.radial, hand.palmar

    # Every finger's rings from its web to its tip.
    finger_rings = []
    for name, digit in zip(FINGERS, hand.fingers):
        shares = [WEB_RING]
        for default, (low, high) in reversed(FINGER_JOINTS):
            peak = digit.girth_peak(low, high)
            joint = peak if low + 0.01 < peak < high - 0.01 else default
            shares += [joint + JOINT_HALF, joint - JOINT_HALF]
        shares.append(TIP_RING)
        finger_rings.append([_ring_of_four(digit, share, palmar, radial) for share in shares])

    # The ladder at the webs: neighbouring fingers share the edge through their web.
    palm = hand.surface
    front, back = [], []
    for i in range(5):
        if i == 0:
            p, d = finger_rings[0][0][1], finger_rings[0][0][2]
        elif i == 4:
            p, d = finger_rings[3][0][0], finger_rings[3][0][3]
        else:
            p = palm.project((finger_rings[i - 1][0][0] + finger_rings[i][0][1]) / 2, reach=0.03)
            d = palm.project((finger_rings[i - 1][0][3] + finger_rings[i][0][2]) / 2, reach=0.03)
        front.append(net.add(p))
        back.append(net.add(d))

    def order(p, d):
        return [p[2], p[1], p[0], d[0], d[1], d[2], d[3], d[4], p[4], p[3]]

    def palm_ring(height, toward):
        loop = palm.loop(hand.wrist + axis * height, axis, largest=True).oriented(axis)
        targets = [np.asarray(net.points[v], float) for v in toward]
        return [net.add(loop.nearest_point(q - axis * ((q - hand.wrist) @ axis - height))) for q in targets]

    knuckle_height = hand.web_height - KNUCKLES_BEFORE_WEB * s
    web_ring = order(front, back)
    knuckles = palm_ring(knuckle_height, web_ring)
    middle = palm_ring(MID_PALM * knuckle_height, knuckles)
    # At the thumb's side both palm rings follow the thumb's root instead of their heights: the
    # corners of the thumb's section at its web, moved into the palm. The two corners beside the
    # palm belong to the knuckle ring and the two away from it to the mid palm ring.
    thumb = hand.thumb
    thumb_axis = thumb.direction(1.0, window=0.25)
    across = _unit(radial - (radial @ thumb_axis) * thumb_axis)
    thumb_start = across - np.cross(thumb_axis, across)
    turned = np.cross(thumb_axis, across)
    outward = (across - turned, across + turned, -across + turned, -across - turned)
    root = _thumb_ring(thumb, WEB_RING, thumb_start) - thumb_axis * (THUMB_ROOT * s)
    # Each corner is laid onto the skin that faces its own way, so the two corners beside the
    # palm land on the two faces of the web between thumb and forefinger, not in its fold.
    root = [palm.project(point, normal=_unit(direction), reach=0.04) for point, direction in zip(root, outward)]
    for vertex, point in zip((middle[2], middle[3], knuckles[3], knuckles[2]), root):
        net.points[vertex] = np.asarray(point, float)
    net.strip(knuckles, web_ring, zone, "palm")
    for k in range(10):
        if k == 2:
            continue  # the thumb grows from the face at the thumb's side
        j = (k + 1) % 10
        net.quad(middle[k], middle[j], knuckles[j], knuckles[k], zone, "palm")
    w = wrist_ring
    m2 = middle
    for a, b, c, d in ((0, 1, 1, 0), (1, 2, 2, 1), (2, 3, 4, 3), (3, 4, 5, 4), (4, 5, 6, 5), (5, 6, 7, 6), (6, 7, 9, 8), (7, 0, 0, 9)):
        net.quad(w[a], w[b], m2[c], m2[d], zone, "palm")
    net.face((w[2], m2[3], m2[2]), zone, "palm")
    net.face((w[6], m2[8], m2[7]), zone, "palm")

    # The fingers: from the ladder to the tip ring, closed by one face.
    for index, (name, digit, rings) in enumerate(zip(FINGERS, hand.fingers, finger_rings)):
        skin = f"digit_{name}"
        previous = [front[index + 1], front[index], back[index], back[index + 1]]
        marks = {}
        for step, points in enumerate(rings[1:], start=1):
            ring = net.add_ring(points)
            net.strip(previous, ring, zone, skin)
            previous = ring
        _tip(net, previous, digit, zone, skin, name)

    # The thumb, from the hole between the two palm rings at the thumb's side.
    hole = [middle[2], middle[3], knuckles[3], knuckles[2]]
    peak = thumb.girth_peak(*THUMB_JOINT[1])
    joint = peak if THUMB_JOINT[1][0] + 0.01 < peak < THUMB_JOINT[1][1] - 0.01 else THUMB_JOINT[0]
    previous = hole
    for share in (WEB_RING, joint + JOINT_HALF, joint - JOINT_HALF, TIP_RING):
        ring = net.add_ring(_thumb_ring(thumb, share, thumb_start))
        net.strip(previous, ring, zone, "digit_Thumb")
        previous = ring
    _tip(net, previous, thumb, zone, "digit_Thumb", "Thumb")
