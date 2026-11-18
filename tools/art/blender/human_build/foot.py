"""The cage of the right foot, from the ankle ring of ten to the toe tips.

Rings fan from the ankle ring about the instep until they stand across the foot, so the heel gets
the outside of the fan and the instep its fold. Every ring has a vertex on the foot's inner line
and one on its outer line, with four over the top and four under the sole, so the foot unwraps
into a top and a sole. Two triangles at the sides of the middle of the foot, which does not bend,
take the ring to twelve for the five toes.

The toes lie against each other, so they are no tubes of their own: six lines run forward over the
top and six under the sole, the inner four of each in the creases between the toes, and the rings
of twelve cross them behind the toes, at the toes' roots, at their middles, and at the notches
where their tips part. Each toe ends in four triangles to its tip, which subdividing rounds."""

import numpy as np

from .body import ZONE, Z, _unit
from .net import Net
from .surface import Loop

TOES = ("Big", "Second", "Third", "Fourth", "Little")
# The rings of the fan cross the instep this far in front of the ankle ring's front, at a body
# height of 1.79 m; the last one stands across the foot.
FAN_STEPS = (0.012, 0.026, 0.042)
# How far behind its notch each of the six lines reaches the toes' roots, from the big toe's
# inner side to the little toe's outer side.
ROOT_BEHIND_NOTCH = (0.040, 0.036, 0.030, 0.028, 0.026, 0.024)
# The outer lines' notches lie this far behind the tip of the big and the little toe.
SIDE_NOTCH = (0.014, 0.010)
# The outer lines lie this far inside the foot's edge.
SIDE_INSET = 0.007
BALL_BEHIND_ROOTS = 0.022
# The creases are looked for this far behind the toes' front outline.
CREASE_BEHIND_OUTLINE = 0.012
BIN = 0.002


class Foot:
    """The right foot of a reference body: its directions, its toe tips, and the creases between
    its toes."""

    def __init__(self, m, ankle, shin):
        s = m.scale
        self.scale = s
        self.ankle = np.asarray(ankle, float)
        self.shin = _unit(shin)
        self.body = m.surface
        self.surface = m.skins()["leg"].part(lambda c: c[:, 2] < ankle[2] + 0.03 * s)
        points = self.surface.points[np.unique(self.surface.triangles)]
        low = points[points[:, 2] < ankle[2] - 0.02 * s][:, :2]
        centred = low - low.mean(0)
        _values, vectors = np.linalg.eigh(centred.T @ centred)
        forward = np.array([vectors[0, -1], vectors[1, -1], 0.0])
        self.forward = _unit(forward if forward[1] > 0 else -forward)
        self.lateral = np.cross(self.forward, Z)
        rel = points - self.ankle
        self.u, self.v, self.points = rel @ self.lateral, rel @ self.forward, points
        # The front outline of the toes: per strip across the foot, how far forward the foot reaches.
        front = self.v > self.v.max() - 0.075 * s
        low_u, high_u = self.u[front].min(), self.u[front].max()
        edges = np.arange(low_u, high_u + BIN, BIN)
        reach = np.full(len(edges) - 1, -np.inf)
        index = np.clip(((self.u[front] - low_u) / BIN).astype(int), 0, len(reach) - 1)
        np.maximum.at(reach, index, self.v[front])
        reach = np.where(np.isfinite(reach), reach, np.nanmin(reach[np.isfinite(reach)]))
        smooth = np.convolve(np.pad(reach, 2, mode="edge"), np.ones(5) / 5, mode="valid")
        centres = (edges[:-1] + edges[1:]) / 2
        self._outline = (centres, smooth)
        # The creases between the toes: the four deepest valleys of the foot's top along a line
        # that follows the front outline a little behind it, since the toes stand in a stair.
        across = np.arange(low_u + 0.006 * s, high_u - 0.006 * s, 0.001)
        heights = []
        for u in across:
            point = self.top(u, self.reach(u) - CREASE_BEHIND_OUTLINE * s)
            heights.append(np.nan if point is None else point[2])
        heights = np.array(heights)
        known = np.isfinite(heights)
        heights = np.interp(across, across[known], heights[known])
        heights = np.convolve(np.pad(heights, 1, mode="edge"), np.ones(3) / 3, mode="valid")
        valleys = []
        for i in range(2, len(heights) - 2):
            if heights[i] <= heights[i - 1] and heights[i] < heights[i + 1]:
                depth = min(heights[max(0, i - 9):i].max(), heights[i + 1:i + 10].max()) - heights[i]
                valleys.append((depth, i))
        chosen = []
        for depth, i in sorted(valleys, reverse=True):
            if all(abs(i - j) >= 8 for j in chosen):
                chosen.append(i)
            if len(chosen) == 4:
                break
        if len(chosen) != 4:
            profile = " ".join(f"{u * 1000:.0f}:{z * 1000:.1f}" for u, z in zip(across, heights))
            raise RuntimeError(f"the foot's top shows {len(chosen)} creases between the toes, expected four; "
                               f"top across:height in mm: {profile}")
        self.notch_u = [float(across[i]) for i in sorted(chosen)]
        self.notch_v = [self.reach(u) for u in self.notch_u]
        bounds = [float(low_u)] + self.notch_u + [float(high_u)]
        self.tip_u, self.tip_v = [], []
        for inner, outer in zip(bounds[:-1], bounds[1:]):
            inside = (centres > inner + 0.003) & (centres < outer - 0.003)
            peak = centres[inside][int(np.argmax(smooth[inside]))]
            at_edge = peak <= centres[inside].min() + 1e-9 or peak >= centres[inside].max() - 1e-9
            u = float((inner + outer) / 2 if at_edge else peak)
            self.tip_u.append(u)
            self.tip_v.append(self.reach(u))

    def reach(self, u):
        """How far forward the toes reach at a place across the foot."""
        centres, smooth = self._outline
        return float(np.interp(u, centres, smooth))

    def report(self):
        return [f"foot forward {np.round(self.forward, 3).tolist()}; toe tips across {np.round(np.array(self.tip_u) * 1000).astype(int).tolist()} mm, "
                f"forward {np.round(np.array(self.tip_v) * 1000).astype(int).tolist()} mm; notches across "
                f"{np.round(np.array(self.notch_u) * 1000).astype(int).tolist()} mm, forward {np.round(np.array(self.notch_v) * 1000).astype(int).tolist()} mm"]

    def at(self, u, v, z=0.0):
        return self.ankle + self.lateral * u + self.forward * v + Z * (z - self.ankle[2])

    def top(self, u, v):
        """The point on top of the foot at a place across and forward, or None beside the foot."""
        hit = self.surface.ray(self.at(u, v, 0.25 * self.scale), -Z, 0.4)
        return None if hit is None else hit[0]

    def under(self, u, v):
        hit = self.surface.ray(self.at(u, v, -0.05), Z, 0.4)
        return None if hit is None else hit[0]

    def crease(self, u, v, top=True, reach=0.004):
        """The point of the crease between two toes near a place: the lowest point of the top, or
        the highest of the underside, within a few millimetres across."""
        best = None
        for du in np.linspace(-reach, reach, 9):
            point = self.top(u + du, v) if top else self.under(u + du, v)
            if point is None:
                continue
            depth = point[2] if top else -point[2]
            if best is None or depth < best[0]:
                best = (depth, point)
        return None if best is None else best[1]

    def edges(self, v):
        """The foot's inner and outer edge across at a place forward."""
        near = np.abs(self.v - v) < 0.003
        return float(self.u[near].min()), float(self.u[near].max())

    def tip(self, index):
        """The tip of a toe: the point farthest forward near its place across."""
        near = np.abs(self.u - self.tip_u[index]) < 0.004
        return self.points[np.where(near)[0][int(np.argmax(self.v[near]))]]

    def skins(self):
        return {"foot": self.surface}


def _from_inner_line(loop: Loop, normal, lateral):
    """The loop starting at its point on the foot's inner line, and the share of its length at
    which it reaches the outer line."""
    loop = loop.oriented(normal)
    along = loop.points @ lateral
    loop = Loop(np.roll(loop.points, -int(np.argmin(along)), axis=0))
    cumulative = np.concatenate([[0.0], np.cumsum(loop.lengths)])
    outer = float(cumulative[int(np.argmax(loop.points @ lateral))] / loop.length)
    return loop, outer


def _ring_of_ten(loop: Loop, normal, lateral):
    """Inner line, four over the top, outer line, four under the sole."""
    loop, outer = _from_inner_line(loop, normal, lateral)
    shares = [outer * k / 5 for k in range(6)] + [outer + (1 - outer) * k / 5 for k in range(1, 5)]
    return loop.resample(10, shares)


def build_foot(net: Net, foot: Foot):
    s = foot.scale
    zone = ZONE["Feet"]
    body = foot.body
    forward, lateral = foot.forward, foot.lateral
    ankle_ring = net.marks["leg_end"]

    # The fan about the instep, from the ankle ring to the ring across the foot.
    ankle_loop = body.loop(foot.ankle, foot.shin, near=foot.ankle)
    front = float(((ankle_loop.points - foot.ankle) @ forward).max())
    angle = np.arccos(float(np.clip(foot.shin @ forward, -1.0, 1.0)))
    turn_axis = _unit(np.cross(foot.shin, forward))
    rings = [ankle_ring]
    for step, ahead in enumerate(FAN_STEPS, start=1):
        a = angle * step / len(FAN_STEPS)
        normal = (foot.shin * np.cos(a) + np.cross(turn_axis, foot.shin) * np.sin(a)
                  + turn_axis * (turn_axis @ foot.shin) * (1 - np.cos(a)))
        through = foot.top(0.0, front + ahead * s)
        near = through - Z * 0.03 * s - forward * 0.02 * s * (len(FAN_STEPS) - step)
        loop = body.loop(through, normal, near=near)
        rings.append(net.add_ring(_ring_of_ten(loop, normal, lateral)))
    for lower, upper in zip(rings[:-1], rings[1:]):
        net.strip(lower, upper, zone, "foot")
    across = rings[-1]

    # The six lines over the top and under the sole, at the notches, the middles, and the roots.
    notch_v = [foot.tip_v[0] - SIDE_NOTCH[0] * s] + list(foot.notch_v) + [foot.tip_v[4] - SIDE_NOTCH[1] * s]
    notch_v = [v - 0.0015 for v in notch_v]
    root_v = [v - behind * s for v, behind in zip(notch_v, ROOT_BEHIND_NOTCH)]
    # The ring behind the toes follows their roots, which lie farther back toward the little toe.
    ball_v = [v - BALL_BEHIND_ROOTS * s for v in root_v]

    def ring_at(depths, creased):
        top, sole = [], []
        for i, v in enumerate(depths):
            if i in (0, 5):
                inner, outer = foot.edges(v)
                u = inner + SIDE_INSET * s if i == 0 else outer - SIDE_INSET * s
                t, b = foot.top(u, v), foot.under(u, v)
            else:
                u = foot.notch_u[i - 1]
                t = foot.crease(u, v, top=True) if creased else foot.top(u, v)
                b = foot.crease(u, v, top=False) if creased else foot.under(u, v)
            if t is None or b is None:
                raise RuntimeError(f"the foot has no skin at line {i}, {v * 1000:.0f} mm in front of the ankle")
            top.append(net.add(t))
            sole.append(net.add(b))
        return top + sole[::-1]

    ball = ring_at(ball_v, creased=False)
    roots = ring_at(root_v, creased=True)
    middles = ring_at([(a + b) / 2 for a, b in zip(notch_v, root_v)], creased=True)
    notches = ring_at(notch_v, creased=True)

    # From the ring of ten across the foot to the ring of twelve, with a triangle at each side.
    for i in range(5):
        net.quad(across[i], across[i + 1], ball[i + 1], ball[i], zone, "foot")
    net.face((across[5], ball[6], ball[5]), zone, "foot")
    for i in range(5, 10):
        net.quad(across[i], across[(i + 1) % 10], ball[i + 2], ball[i + 1], zone, "foot")
    net.face((across[0], ball[0], ball[11]), zone, "foot")
    net.strip(ball, roots, zone, "foot")
    net.strip(roots, middles, zone, "foot")
    net.strip(middles, notches, zone, "foot")

    # Each toe ends in four triangles to its tip.
    for index, name in enumerate(TOES):
        ring = [notches[11 - index], notches[index], notches[index + 1], notches[10 - index]]
        tip = net.add(foot.tip(index))
        for k in range(4):
            net.face((ring[k], ring[(k + 1) % 4], tip), zone, "foot")
        net.mark(f"{name}_toe_tip", [tip])
