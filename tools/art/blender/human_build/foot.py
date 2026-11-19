"""The cage of the right foot, from the ankle ring of ten to the toe tips.

Rings fan from the ankle ring about the instep until they stand across the foot, so the heel gets
the outside of the fan and the instep its fold. Every ring has a vertex on the foot's inner line
and one on its outer line, with four over the top and four under the sole, so the foot unwraps
into a top and a sole. Two triangles at the sides of the middle of the foot, which does not bend,
take the ring to twelve for the five toes.

The toes are digits of their own, as the fingers are: each is traced from its tip inward to where
it joins the toe beside it or the foot. The ring at the toes' webs is a ladder of six vertices over
the top and six under the sole, so two toes share the edge through their web, and each toe has two
rings of four of its own and ends in four triangles to its tip, which subdividing rounds."""

import numpy as np

from . import digits
from .body import ZONE, Z, _unit
from .net import Net
from .surface import Loop

TOES = ("Big", "Second", "Third", "Fourth", "Little")
# The island the toes' faces carry until the build tool parts them into top and sole.
TOE_ISLAND = "toes_R"
# The rings of the fan: each has turned this share of the way from the ankle ring to a ring across
# the foot, and crosses the instep this far in front of the ankle ring's front, at a body height of
# 1.79 m. A quarter of the way the ring meets the back of the heel at half its height and half of
# the way it lies under the heel, so two rings between them go around the heel's corner, which is
# round and would else be one face.
FAN_RINGS = ((0.25, 0.010), (0.34, 0.0135), (0.43, 0.017), (0.50, 0.020), (0.75, 0.031), (1.0, 0.042))
# The ring behind the toes lies this far behind their webs, and its vertices on the foot's two
# sides at least this far inside the foot's edge, at 1.79 m. One more ring of twelve lies halfway
# from it to the ring across the foot, each vertex at its share of the foot's width there.
BALL_BEHIND_WEBS = 0.020
SIDE_INSET = 0.006
# The creases are looked for this far behind the toes' front outline.
CREASE_BEHIND_OUTLINE = 0.012
BIN = 0.002
# The skin of the web between two toes is at least this thick, and it is looked for up to this
# far behind the place where the toes' rings at their webs end, at 1.79 m.
WEB_THICKNESS, WEB_BEHIND = 0.003, 0.025
# A toe is traced from its tip in sections straight across the foot, which a toe lies along, and
# where a section is TOE_JOINED times as long as the longest before it, and the trace has left it
# where a section is shorter than the share TOE_LEFT of that; both are looked for from TOE_DOME
# behind the tip on, at 1.79 m. Its rings lie at these shares of its free length, from the
# tip (0) to the web (1): the ring at the web, and its own two.
TOE_STEP, TOE_DOME, TOE_JOINED, TOE_LEFT = 0.002, 0.006, 1.35, 0.5
TOE_WEB_RING, TOE_RINGS = 0.97, (0.56, 0.20)
# A toe's own skin lies within this of its line, at 1.79 m.
TOE_SKIN = 0.021


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
        # Each toe from its tip to its web.
        self.toes = [digits.march(self.surface, self.tip(index), self.forward, step=TOE_STEP * s, reach=0.08 * s,
                                  dome=TOE_DOME * s, jump=TOE_JOINED, least=TOE_LEFT, straight=True) for index in range(5)]
        for name, toe in zip(TOES, self.toes):
            if toe.length < 0.006 * s:
                raise RuntimeError(f"the {name} toe is free for {toe.length * 1000:.0f} mm only")

    def reach(self, u):
        """How far forward the toes reach at a place across the foot."""
        centres, smooth = self._outline
        return float(np.interp(u, centres, smooth))

    def report(self):
        return [f"foot forward {np.round(self.forward, 3).tolist()}; toe tips across {np.round(np.array(self.tip_u) * 1000).astype(int).tolist()} mm, "
                f"forward {np.round(np.array(self.tip_v) * 1000).astype(int).tolist()} mm; notches across "
                f"{np.round(np.array(self.notch_u) * 1000).astype(int).tolist()} mm, forward {np.round(np.array(self.notch_v) * 1000).astype(int).tolist()} mm",
                "toes free for " + ", ".join(f"{toe.length * 1000:.0f}" for toe in self.toes) + " mm, around at the web "
                + ", ".join(f"{toe.girths[-1] * 1000:.0f}" for toe in self.toes) + " mm"]

    def at(self, u, v, z=0.0):
        return self.ankle + self.lateral * u + self.forward * v + Z * (z - self.ankle[2])

    def across_forward(self, point):
        """How far across the foot and how far forward a point lies."""
        rel = np.asarray(point, float) - self.ankle
        return float(rel @ self.lateral), float(rel @ self.forward)

    def top(self, u, v):
        """The point on top of the foot at a place across and forward, or None beside the foot."""
        hit = self.surface.ray(self.at(u, v, 0.25 * self.scale), -Z, 0.4)
        return None if hit is None else hit[0]

    def under(self, u, v):
        hit = self.surface.ray(self.at(u, v, -0.05), Z, 0.4)
        return None if hit is None else hit[0]

    def web(self, over, under):
        """The skin of the web between two toes, over it and under it, at a place between the
        toes' corners over and under them. Where the toes still stand apart there, a ray from above
        and one from below pass between them: the web is the first place behind it where both meet
        skin with the web's thickness between."""
        s = self.scale
        u, v = self.across_forward((np.asarray(over, float) + np.asarray(under, float)) / 2)
        for behind in np.arange(0.0, WEB_BEHIND * s + 1e-9, 0.001 * s):
            above, below = self.top(u, v - behind), self.under(u, v - behind)
            if above is not None and below is not None and above[2] - below[2] >= WEB_THICKNESS * s:
                return above, below
        raise RuntimeError(f"the foot has no web between its toes {u * 1000:.0f} mm across, {v * 1000:.0f} mm in front of the ankle")

    def edges(self, v):
        """The foot's inner and outer edge across at a place forward."""
        near = np.abs(self.v - v) < 0.003
        return float(self.u[near].min()), float(self.u[near].max())

    def tip(self, index):
        """The tip of a toe: the point farthest forward near its place across."""
        near = np.abs(self.u - self.tip_u[index]) < 0.004
        return self.points[np.where(near)[0][int(np.argmax(self.v[near]))]]

    def skins(self):
        """The whole foot, and for each toe the skin around its own line."""
        out = {"foot": self.surface}
        out.update(digits.own_skins(self.surface, self.toes, [f"toe_{name}" for name in TOES], self.scale, TOE_SKIN))
        return out


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


def _toe_ring(foot: Foot, toe, share):
    """A toe's section straight across the foot at a share of its length, as four points: the
    corner under the toe on the foot's inner side first, then the corner over it on that side, and
    on around the outer side."""
    origin, normal = toe.point(share), foot.forward
    loop = foot.surface.loop(origin, normal, near=origin).oriented(normal)
    start = -foot.lateral - Z
    return loop.start_at(start - (start @ normal) * normal, normal).resample(4)


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
    for share, ahead in FAN_RINGS:
        a = angle * share
        normal = (foot.shin * np.cos(a) + np.cross(turn_axis, foot.shin) * np.sin(a)
                  + turn_axis * (turn_axis @ foot.shin) * (1 - np.cos(a)))
        through = foot.top(0.0, front + ahead * s)
        near = through - Z * 0.03 * s - forward * 0.08 * s * (1.0 - share)
        loop = body.loop(through, normal, near=near)
        rings.append(net.add_ring(_ring_of_ten(loop, normal, lateral)))
    # The foot opens into a top, with its sides, and a sole: along its inner and its outer line.
    top, sole = "foot_top_R", "foot_sole_R"
    for lower, upper in zip(rings[:-1], rings[1:]):
        net.strip(lower, upper, zone, "foot", islands=[top] * 5 + [sole] * 5)
    across = rings[-1]

    # The toes' rings, and the ladder at their webs: the big toe's inner corners, the little toe's
    # outer ones, and between two toes the skin of their web, over the top and under the sole.
    toe_rings = [[_toe_ring(foot, toe, share) for share in (TOE_WEB_RING,) + TOE_RINGS] for toe in foot.toes]
    over, under = [toe_rings[0][0][1]], [toe_rings[0][0][0]]
    for inner, outer in zip(toe_rings[:-1], toe_rings[1:]):
        above, below = foot.web((inner[0][2] + outer[0][1]) / 2, (inner[0][3] + outer[0][0]) / 2)
        over.append(above)
        under.append(below)
    over.append(toe_rings[4][0][2])
    under.append(toe_rings[4][0][3])

    # The ring behind the toes, a step behind each vertex of the ladder.
    ball_over, ball_under = [], []
    for index, (a, b) in enumerate(zip(over, under)):
        row = []
        for point, ray in ((a, foot.top), (b, foot.under)):
            u, v = foot.across_forward(point)
            v -= BALL_BEHIND_WEBS * s
            inner, outer = foot.edges(v)
            u = float(np.clip(u, inner + SIDE_INSET * s, outer - SIDE_INSET * s))
            found = ray(u, v)
            if found is None:
                raise RuntimeError(f"the foot has no skin behind the toes at line {index}, {v * 1000:.0f} mm in front of the ankle")
            row.append(found)
        ball_over.append(row[0])
        ball_under.append(row[1])
    # The ring across the middle of the foot, halfway from the ring of ten to the ring behind the
    # toes: every vertex at the share of the foot's width that its vertex behind the toes has.
    back = float(np.mean([foot.across_forward(net.points[v])[1] for v in across]))
    middle_over, middle_under = [], []
    for index, (a, b) in enumerate(zip(ball_over, ball_under)):
        row = []
        for point, ray in ((a, foot.top), (b, foot.under)):
            u, v = foot.across_forward(point)
            inner, outer = foot.edges(v)
            share = (u - inner) / (outer - inner)
            v = (v + back) / 2
            inner, outer = foot.edges(v)
            found = ray(inner + share * (outer - inner), v)
            if found is None:
                raise RuntimeError(f"the foot has no skin across its middle at line {index}, {v * 1000:.0f} mm in front of the ankle")
            row.append(found)
        middle_over.append(row[0])
        middle_under.append(row[1])
    middle = net.add_ring(middle_over) + net.add_ring(middle_under)[::-1]
    ball = net.add_ring(ball_over) + net.add_ring(ball_under)[::-1]
    over, under = net.add_ring(over), net.add_ring(under)
    webs = over + under[::-1]

    # From the ring of ten across the foot to the ring of twelve, with a triangle at each side.
    net.island = top
    for i in range(5):
        net.quad(across[i], across[i + 1], middle[i + 1], middle[i], zone, "foot")
    net.face((across[5], middle[6], middle[5]), zone, "foot")
    net.island = sole
    for i in range(5, 10):
        net.quad(across[i], across[(i + 1) % 10], middle[i + 2], middle[i + 1], zone, "foot")
    net.island = top
    net.face((across[0], middle[0], middle[11]), zone, "foot")
    # Of the rings of twelve, the faces at the foot's two edges belong to the top.
    twelve = [top] * 6 + [sole] * 5 + [top]
    net.strip(middle, ball, zone, "foot", islands=twelve)
    net.strip(ball, webs, zone, "foot", islands=twelve)

    # Each toe from the ladder to its last ring, closed by four triangles to its tip. The build
    # tool parts the toes' faces into the foot's top and its sole by the way they look.
    net.island = TOE_ISLAND
    for index, (name, toe) in enumerate(zip(TOES, foot.toes)):
        skin = f"toe_{name}"
        previous = [under[index], over[index], over[index + 1], under[index + 1]]
        for points in toe_rings[index][1:]:
            ring = net.add_ring(points)
            net.strip(previous, ring, zone, skin)
            previous = ring
        tip = net.add(toe.tip)
        for k in range(4):
            net.face((previous[k], previous[(k + 1) % 4], tip), zone, skin)
        net.mark(f"{name}_toe_tip", [tip])
