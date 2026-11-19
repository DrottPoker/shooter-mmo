"""The lids of the head's eye, laid onto the reference's own lids.

The head cage closes each eye with a ring at the edge of its opening and one band of faces out to
the crease and the cheek, which subdividing makes two rings: too few for a lid that closes, and a
lid without the thickness of its margin, so closed lids leave a slit. That band leaves the cage,
and the lids are built in its place on the subdivided net, ring by ring, on spokes: half planes
through the eyeball's centre that hold the line of sight. The opening's outline is measured first
in OUTLINE_COUNT directions around the line of sight, as the spokes see it, and its corners are
where it reaches furthest around the eyeball, on the side of the nose and on the outer side; the
spokes stand at SPOKE_STEPS steps of even length along each lid from one corner to the other. On
each spoke the reference's lid is the skin that rays from outside toward the eyeball's centre meet
first, and the rings lie on it:

- the lids' inner edge, on the eyeball where the opening ends, LID_CLEARANCE off its surface;
- the lash line LASH_ANGLE further out, where the reference's margin turns into the front of the
  lid, so the margin between the two has the reference's thickness; it thins out toward the
  corners, where the lids meet;
- LID_RINGS rings evenly on the lid between the lash line and the crease;
- the crease, where the lid's skin has risen CREASE_RISE above the lash line: below the fold of the
  brow over the upper lid, and where the lower lid meets the cheek, with the lid's width to it
  evened out with the neighbouring spokes', so the rings on the lid do not wave;
- a ring between the crease and the ring around the hole, laid onto the skin and evened out with
  the rest of the net.

The ring around the hole has two vertices fewer on each lid than the spokes: the band from the
crease takes them in with a triangle near each corner, where the lids move least. A pocket behind
the inner edge holds the eyeball and is hidden under the lids. Turned about the eyeball's centre,
closing lids meet margin to margin."""

import math

import numpy as np

from .body import ZONE, _unit
from .net import FIXED, PINNED, RIGID

SOCKET = "socket"
# Steps of each lid's edge between the two corners: SPOKE_STEPS - 1 spokes on each lid and one at
# each corner.
SPOKE_STEPS = 12
# The lids' inner edge and the pocket lie this far off the eyeball's surface, at any height.
LID_CLEARANCE = 0.0004
# The pocket's ring continues this far around the eyeball beyond the inner edge, in radians, so the
# pocket stays hidden under the lids also at the corners.
LID_TUCK = 0.32
LID_RINGS = 2
# The lash line lies LASH_ANGLE degrees beyond the inner edge, where the reference's margin turns
# into the front of the lid, on the lid there: at least MARGIN_LEAST above the inner edge, and no
# more than MARGIN_MOST off the eyeball, which it reaches sooner beside the corners. At 1.79 m.
LASH_ANGLE = 1.5
MARGIN_LEAST, MARGIN_MOST = 0.0005, 0.0025
# Toward each corner, where the two lids meet, the margin thins out over this many spokes to
# MARGIN_LEAST at the corner itself, so the corner is a slope and not a wall.
CORNER_SPOKES = 2
# The crease lies where the lid has risen this far above the lash line, at least LID_LEAST and at
# most LID_MOST degrees beyond the lash line, at 1.79 m: at the corners the lid is narrow.
CREASE_RISE = 0.0012
LID_LEAST, LID_MOST = 8.0, 30.0
# The widths of the lid to the crease are evened out around the eye this many rounds.
EVEN_ROUNDS = 2
# The opening's outline is measured in OUTLINE_COUNT directions around the line of sight, each
# scanned in steps of OUTLINE_STEP degrees out to OUTLINE_MOST. Each spoke's lid is measured in
# steps of PROFILE_STEP degrees around the eyeball's centre from PROFILE_BEFORE degrees short of
# the opening's edge, from rays that start PROFILE_REACH from the centre, at 1.79 m.
OUTLINE_COUNT = 96
OUTLINE_STEP = 1.0
OUTLINE_MOST = 100.0
PROFILE_STEP = 0.25
PROFILE_REACH = 0.025
PROFILE_BEFORE = 12.0
# A ray that meets no skin farther from the eyeball's centre than its radius and this passes through
# the opening.
OPEN_ABOVE = 0.0002
# The pocket's second ring lies this many degrees further around the eyeball than its first, with
# a vertex on every other spoke, and its bottom this far behind the eyeball, at 1.79 m: its faces
# stay wide also where the spokes crowd at the corners.
POCKET_TURN = 40.0
POCKET_DEPTH = 0.003


def first_on_lid(rhos):
    """The index of a spoke's first sample on the lid: the profile holds, per angle from the line
    of sight outward, the distance of the outermost skin from the eyeball's centre, NaN where a ray
    passes through the opening. The profile must start in the opening."""
    on_lid = ~np.isnan(np.asarray(rhos, float))
    if not len(on_lid) or on_lid[0]:
        raise RuntimeError("a spoke of the eye starts on the lid, not in the opening")
    if not on_lid.any():
        raise RuntimeError("a spoke of the eye meets no lid")
    return int(np.argmax(on_lid))


def crease_angle(thetas, rhos, edge, rise, least=LID_LEAST, most=LID_MOST):
    """The angle where the lid has first risen rise above the lash line, which lies at the edge
    index: interpolated between the two samples around it, at least least and at most most degrees
    beyond the edge. Where the skin turns away from the eye before that, as beside the nose, so
    that rays from outside no longer meet it, the lid ends where they last did."""
    thetas = np.asarray(thetas, float)
    rhos = np.asarray(rhos, float)
    level = rhos[edge] + rise
    start, stop = thetas[edge] + least, thetas[edge] + most
    previous = edge
    on_lid = False
    for k in range(edge + 1, len(thetas)):
        if thetas[k] > stop:
            break
        if np.isnan(rhos[k]):
            # Rays may pass through the opening beside the lash line where the edge was evened
            # out beside a corner; once they have met the lid, a gap is its end.
            if on_lid:
                return float(thetas[previous])
            continue
        on_lid = True
        if rhos[k] >= level and thetas[k] >= start:
            if thetas[previous] < start:
                return float(start)
            t = (level - rhos[previous]) / max(rhos[k] - rhos[previous], 1e-12)
            return float(thetas[previous] + t * (thetas[k] - thetas[previous]))
        previous = k
    if not on_lid:
        raise RuntimeError("the eye's lid ends at its lash line")
    return float(min(stop, max(thetas[previous], start)))


def filled(edges):
    """The opening's edge in every direction around the line of sight, where a direction without one,
    None, takes the mean of the nearest measured directions on both sides: deep in a corner the
    rays of a single direction may pass between the lids all the way round."""
    count = len(edges)
    if all(edge is None for edge in edges):
        raise RuntimeError("the eye's opening has no edge in any direction")
    out = list(edges)
    for k, edge in enumerate(edges):
        if edge is None:
            before = next(edges[(k - d) % count] for d in range(1, count) if edges[(k - d) % count] is not None)
            after = next(edges[(k + d) % count] for d in range(1, count) if edges[(k + d) % count] is not None)
            out[k] = (before + after) / 2
    return out


def evened_edges(edges, corners):
    """The angles of the inner edge, spoke by spoke around the eye, with a spoke that lies further
    from the line of sight than both its neighbours, other than a corner, laid between them: beside
    a corner the lids may part around the eyeball's side, and the edge would stand out there."""
    edges = list(edges)
    count = len(edges)
    out = list(edges)
    for k in range(count):
        if k in corners:
            continue
        before, after = edges[k - 1], edges[(k + 1) % count]
        if edges[k] > max(before, after):
            out[k] = (before + after) / 2
    return out


def lid_end(thetas, rhos):
    """The last angle on a spoke's lid from its lash line, which lies at the first sample: before
    the first gap that follows the lid, as beside the nose, or the last sample."""
    rhos = np.asarray(rhos, float)
    seen = False
    for k in range(1, len(rhos)):
        if np.isnan(rhos[k]):
            if seen:
                return float(thetas[k - 1])
            continue
        seen = True
    return float(thetas[-1])


def evened_widths(widths, rounds=EVEN_ROUNDS):
    """The widths of the lid from the lash line to the crease, spoke by spoke around the eye, evened
    out: each round every spoke takes half its own width and a quarter of each neighbour's."""
    widths = np.asarray(widths, float)
    for _ in range(rounds):
        widths = 0.5 * widths + 0.25 * (np.roll(widths, 1) + np.roll(widths, -1))
    return [float(w) for w in widths]


def lid_rings(edge, crease, rings=LID_RINGS):
    """The angles of the rings on the lid, evenly between the inner edge and the crease."""
    return [edge + (crease - edge) * k / (rings + 1) for k in range(1, rings + 1)]


def corner_share(spoke, steps=SPOKE_STEPS, corner=CORNER_SPOKES):
    """How much of its margin a spoke keeps: none at the two corners, at spokes 0 and steps, all of
    it from corner spokes away from both on, and evenly between."""
    k = spoke % (2 * steps)
    away = min(k, abs(steps - k), 2 * steps - k)
    return min(1.0, away / corner)


def merged(spokes, around):
    """Which vertex of the ring around the hole each spoke of a lid goes to: spokes counts the
    spokes between the two corners, around the ring's vertices there, no more than the spokes.
    The spokes beyond the ring's count share a vertex with their neighbour near a corner, half at
    the first corner and the rest at the second, so the band between them has its triangles where
    the lid moves least. Returns, per spoke from the first corner, the index of its vertex, from 1
    up to around."""
    extra = spokes - around
    if extra < 0:
        raise ValueError(f"{spokes} spokes cannot take a ring of {around}")
    first = (extra + 1) // 2
    out = []
    for k in range(spokes):
        if k <= first:
            out.append(1)
        else:
            out.append(min(k - first + 1, around))
    return out


def band(spokes_per_lid, around_per_lid):
    """The faces between the ring of spokes and the ring around the hole, as pairs of corner lists:
    (spoke indices, ring indices), each face from two neighbouring spokes to the ring's vertices
    they go to. Both rings run from the first corner over the first lid to the second corner and
    back over the other lid; the corners go to each other."""
    lid = merged(spokes_per_lid, around_per_lid)
    spokes = 2 * (spokes_per_lid + 1)
    around = 2 * (around_per_lid + 1)
    target = [0] + lid + [around_per_lid + 1] + [around_per_lid + 1 + m for m in lid]
    faces = []
    for k in range(spokes):
        after = (k + 1) % spokes
        a, b = target[k], target[after]
        if a == b:
            faces.append(((k, after), (a,)))
        else:
            if (b - a) % around != 1:
                raise ValueError("the band's spokes skip a vertex of the ring around the hole")
            faces.append(((k, after), (b, a)))
    return faces


def _outermost(surface, centre, way, reach):
    """Where a ray from outside toward the eyeball's centre first meets skin seen from outside: way
    is the direction from the centre out along the ray. A ray that starts inside the body, such as
    in the nose beside the inner corner, goes on past the faces it leaves through."""
    origin = centre + way * reach
    toward = -way
    left = reach
    for _ in range(8):
        hit = surface.ray(origin, toward, left)
        if hit is None:
            return None
        point, normal, distance = hit
        if normal @ toward < 0.0:
            return point
        origin = point + toward * 1e-6
        left -= distance + 1e-6
        if left <= 0.0:
            return None
    return None


def stations(phis, points, inner, outer, steps=SPOKE_STEPS):
    """The angles around the line of sight of the spokes: the outline of the opening is sampled at
    the angles phis (degrees, evenly around and growing counter-clockwise, as the eye is seen from in
    front, from its outer side over the top), as points; inner and outer are the samples of the two
    corners. Each lid runs from the inner corner the way the angles fall, over the upper lid to the
    outer corner and on under the lower lid back to the inner corner, and is cut into steps of
    even length. Returns the angles from the inner corner over the upper lid first, without
    repeating the inner corner at the end."""
    count = len(phis)
    points = np.asarray(points, float)
    out = []
    for start, stop in ((inner, outer), (outer, inner)):
        chain = [start]
        while chain[-1] != stop:
            chain.append((chain[-1] - 1) % count)
        angles = [phis[chain[0]]]
        for j in chain[1:]:
            angle = phis[j]
            while angle > angles[-1]:
                angle -= 360.0
            angles.append(angle)
        lengths = np.concatenate([[0.0], np.cumsum(np.linalg.norm(np.diff(points[chain], axis=0), axis=1))])
        for k in range(steps):
            out.append(float(np.interp(lengths[-1] * k / steps, lengths, angles)) % 360.0)
    return out


class Lids:
    """The lids of the right eye, measured on the reference: per spoke from the corner beside the
    nose over the upper lid to the outer corner and back under the lower lid, the points of the
    inner edge, the lash line, the rings on the lid, and the crease, and the pocket's ring.

    The opening is first measured around the line of sight at OUTLINE_COUNT angles, as the spokes
    see it: its corners are where it reaches furthest around the eyeball on the nose's side and on
    the outer side, which beside the outer corner is further around than the eye seen from in front
    shows."""

    def __init__(self, surface, centre, radius, forward, scale):
        self.surface = surface
        self.centre = np.asarray(centre, float)
        self.radius = float(radius)
        self.forward = _unit(forward)
        self.scale = scale
        up = _unit(np.array([0.0, 0.0, 1.0]) - self.forward[2] * self.forward)
        self.across = (_unit(np.cross(self.forward, up)), up)
        phis = np.arange(OUTLINE_COUNT) * 360.0 / OUTLINE_COUNT
        measured = [self._outline_edge(self._out(phi)) for phi in phis]
        around = filled(measured)
        points = [self._at(self._out(phi), edge, self.radius + LID_CLEARANCE) for phi, edge in zip(phis, around)]
        found = [j for j, edge in enumerate(measured) if edge is not None]
        inner = max((j for j in found if 90.0 < phis[j] < 270.0), key=lambda j: around[j])
        outer = max((j for j in found if phis[j] < 90.0 or phis[j] > 270.0), key=lambda j: around[j])
        self.spoke_angles = stations(phis, points, inner, outer)
        inner, lash, rings, crease, under, deep, widths = [], [], [], [], [], [], []
        least = LID_CLEARANCE + MARGIN_LEAST * scale
        profiles = []
        for phi in self.spoke_angles:
            out = self._out(phi)
            first = max(0.0, self._outline_edge(out) - PROFILE_BEFORE)
            thetas = np.arange(first, first + PROFILE_BEFORE + LID_MOST + 15.0, PROFILE_STEP)
            rhos = np.array([self._rho(out, t) for t in thetas])
            edge_index = first_on_lid(rhos)
            profiles.append((out, thetas, rhos, self._edge(out, thetas[edge_index - 1], thetas[edge_index])))
        edges = evened_edges([p[3] for p in profiles], {0, len(self.spoke_angles) // 2})
        stations_ = []
        for index, ((out, thetas, rhos, _measured), edge) in enumerate(zip(profiles, edges)):
            at, depth = self._lash(out, edge)
            # Toward a corner the margin thins out, and the lid rises from it to the crease as a
            # ramp under the reference's skin, which beside a corner stands up as a wall.
            share = corner_share(index)
            at = edge + (at - edge) * share
            depth = least + (depth - least) * share
            # The profile from the lash line on, with the lash line's own distance first.
            keep = thetas > at
            profile_t = np.concatenate([[at], thetas[keep]])
            profile_r = np.concatenate([[self.radius + depth], rhos[keep]])
            fold = crease_angle(profile_t, profile_r, 0, CREASE_RISE * scale)
            stations_.append((at, depth, share, fold, lid_end(profile_t, profile_r)))
        # The crease runs evenly around the eye: where the brow's fold or the cheek is measured
        # nearer on one spoke than on its neighbours, the lid's rings would wave.
        widths_ = evened_widths([fold - at for at, _depth, _share, fold, _end in stations_])
        for index, ((out, thetas, rhos, _measured), edge, (at, depth, share, _fold, end), width) in enumerate(
                zip(profiles, edges, stations_, widths_)):
            fold = min(at + width, end)
            # Where the evened crease falls where rays pass through the opening beside an evened
            # edge, it lies its rise above the lash line.
            high = self._rho(out, fold) - self.radius
            if np.isnan(high):
                high = depth + CREASE_RISE * scale
            top = self._at(out, fold, self.radius + high)
            spoke_rings = []
            for t in lid_rings(at, fold):
                ramp = depth + (high - depth) * (t - at) / max(fold - at, 1e-9)
                # Where rays still pass through the opening beside an evened edge, the ramp is the lid.
                on_skin = self._rho(out, t) - self.radius
                if np.isnan(on_skin):
                    on_skin = ramp
                spoke_rings.append(self._at(out, t, self.radius + ramp + (on_skin - ramp) * share))
            inner.append(self._at(out, edge, self.radius + LID_CLEARANCE))
            lash.append(self._at(out, at, self.radius + depth))
            rings.append(spoke_rings)
            crease.append(top)
            under.append(self._at(out, edge + math.degrees(LID_TUCK), self.radius + LID_CLEARANCE))
            deep.append(self._at(out, edge + math.degrees(LID_TUCK) + POCKET_TURN, self.radius + LID_CLEARANCE))
            widths.append((edge, at, fold, depth))
        self.inner = np.array(inner)
        self.lash = np.array(lash)
        self.rings = [np.array([spoke[k] for spoke in rings]) for k in range(LID_RINGS)]
        self.crease = np.array(crease)
        self.under = np.array(under)
        self.deep = np.array(deep[0::2])
        self.back = self.centre - self.forward * (self.radius + POCKET_DEPTH * scale)
        self.measures = widths

    def _lash(self, out, edge):
        """The angle of the lash line and its height over the eyeball's surface."""
        s = self.scale
        least = LID_CLEARANCE + MARGIN_LEAST * s
        at = edge + LASH_ANGLE
        rho = self._rho(out, at)
        if np.isnan(rho) or rho - self.radius <= MARGIN_MOST * s:
            return at, max(least, 0.0 if np.isnan(rho) else rho - self.radius)
        # The lid rises steeply beside a corner: the lash line lies where it is MARGIN_MOST high.
        low, high = edge, at
        if not np.isnan(self._rho(out, low)) and self._rho(out, low) - self.radius >= MARGIN_MOST * s:
            return low, MARGIN_MOST * s
        for _ in range(8):
            middle = (low + high) / 2
            rho = self._rho(out, middle)
            if np.isnan(rho) or rho - self.radius < MARGIN_MOST * s:
                low = middle
            else:
                high = middle
        return high, MARGIN_MOST * s

    def _out(self, phi):
        """The direction away from the line of sight at an angle around it: 0 toward the outer side
        of the eye, 90 up."""
        p = math.radians(phi)
        return self.across[0] * math.cos(p) + self.across[1] * math.sin(p)

    def _outline_edge(self, out):
        """The angle where the opening ends in a direction, scanned from the line of sight, or None
        where rays pass between the lids into the corner all the way round."""
        previous = 0.0
        if not np.isnan(self._rho(out, previous)):
            raise RuntimeError("the eye's line of sight meets a lid")
        theta = OUTLINE_STEP
        while theta <= OUTLINE_MOST:
            if not np.isnan(self._rho(out, theta)):
                return self._edge(out, previous, theta)
            previous, theta = theta, theta + OUTLINE_STEP
        return None

    def _way(self, out, theta):
        t = math.radians(theta)
        return self.forward * math.cos(t) + out * math.sin(t)

    def _at(self, out, theta, distance):
        return self.centre + self._way(out, theta) * distance

    def _rho(self, out, theta):
        """The distance from the centre of the outermost skin at an angle, NaN in the opening."""
        hit = _outermost(self.surface, self.centre, self._way(out, theta), PROFILE_REACH * self.scale)
        if hit is None:
            return float("nan")
        distance = float(np.linalg.norm(hit - self.centre))
        return float("nan") if distance < self.radius + OPEN_ABOVE else distance

    def _on_lid(self, out, theta):
        distance = self._rho(out, theta)
        if np.isnan(distance):
            raise RuntimeError(f"the eye's lid has a gap {theta:.1f} degrees from the line of sight")
        return self._at(out, theta, distance)

    def _edge(self, out, opening, lid, rounds=8):
        """The angle where the opening ends, between an angle in it and one on the lid."""
        for _ in range(rounds):
            middle = (opening + lid) / 2
            if np.isnan(self._rho(out, middle)):
                opening = middle
            else:
                lid = middle
        return lid

    def report(self):
        """The margin's depth and the lid's height on the middle spoke of each lid and at the corners."""
        count = len(self.measures)
        lines = []
        for name, k in (("inner corner", 0), ("upper lid", SPOKE_STEPS // 2), ("outer corner", SPOKE_STEPS),
                        ("lower lid", SPOKE_STEPS + SPOKE_STEPS // 2)):
            edge, at, fold, depth = self.measures[k % count]
            margin = float(np.linalg.norm(self.lash[k] - self.inner[k]))
            lid = float(np.linalg.norm(self.crease[k] - self.lash[k]))
            lines.append(f"eye {name}: edge {edge:.1f} degrees from the line of sight, lash line {depth * 1000:.1f} mm over "
                         f"the eyeball, margin {margin * 1000:.1f} mm, lid {lid * 1000:.1f} mm to the crease {fold - at:.1f} "
                         f"degrees on")
        return lines


def graft(fine, coarse, lids):
    """Builds the lids into the hole the cage's band around the eye left in the subdivided net: the
    rings of the lids, the band from the crease to the ring around the hole, and the pocket."""
    hole = coarse.marks["eye_hole"]
    outline = []
    for vertex, after in zip(hole, hole[1:] + hole[:1]):
        outline += [vertex, fine.edge_point[(vertex, after) if vertex < after else (after, vertex)]]
    zone = ZONE["Head"]
    inner = [fine.add(p, FIXED, PINNED) for p in lids.inner]
    lash = [fine.add(p, FIXED, PINNED) for p in lids.lash]
    rings = [[fine.add(p, FIXED) for p in ring] for ring in lids.rings]
    crease = [fine.add(p, FIXED) for p in lids.crease]
    spokes = len(crease)
    per_lid = spokes // 2 - 1
    around = len(outline) // 2 - 1
    faces = band(per_lid, around)
    # Each vertex of the ring between the crease and the hole starts between the spokes it takes
    # and its vertex of the ring around the hole, and is laid onto the skin with the rest.
    taken = [[] for _ in outline]
    for (k, after), targets in faces:
        for j in targets:
            taken[j] += [k, after]
    between = []
    for j, vertex in enumerate(outline):
        mine = np.mean([fine.points[crease[k]] for k in set(taken[j])], axis=0)
        between.append(fine.add((mine + np.asarray(fine.points[vertex], float)) / 2))
    # The rings run from the corner beside the nose over the upper lid, which for the right eye is
    # toward +X over the top: faces from an inner ring to an outer one look outward when they are
    # wound against that way, and the pocket's faces, which look at the eyeball, with it.
    fine.island = "head"
    rows = [inner, lash] + rings + [crease]
    for near, far in zip(rows[:-1], rows[1:]):
        fine.strip(near, far, zone, "head", flip=True)
    for (k, after), targets in faces:
        if len(targets) == 1:
            fine.face((crease[after], crease[k], between[targets[0]]), zone, "head")
        else:
            fine.face((crease[after], crease[k], between[targets[1]], between[targets[0]]), zone, "head")
    fine.strip(between, outline, zone, "head", flip=True)
    _look_outward(fine, lash[spokes // 4], rings[0][spokes // 4], lids.centre)
    fine.island = "socket_R"
    under = [fine.add(p, RIGID) for p in lids.under]
    deep = [fine.add(p, RIGID) for p in lids.deep]
    fine.strip(inner, under, zone, SOCKET)
    # Every other spoke of the second ring: a triangle and a quad per pair of spokes.
    half = len(deep)
    for k in range(half):
        a, b, c = under[2 * k], under[2 * k + 1], under[(2 * k + 2) % spokes]
        fine.face((a, b, deep[k]), zone, SOCKET)
        fine.face((b, c, deep[(k + 1) % half], deep[k]), zone, SOCKET)
    back = fine.add(lids.back, RIGID)
    for k in range(half):
        fine.face((deep[k], deep[(k + 1) % half], back), zone, SOCKET)
    fine.mark("lid_inner", inner)
    fine.mark("lid_lash", lash)
    fine.mark("lid_crease", crease)


def _look_outward(fine, a, b, centre):
    """Makes sure the face on the lid that has the edge from a to b looks away from the eyeball."""
    for face in fine.faces:
        if a in face and b in face:
            p = np.array([fine.points[v] for v in face])
            way = np.cross(p, np.roll(p, -1, axis=0)).sum(0)
            if way @ (p.mean(0) - np.asarray(centre, float)) <= 0.0:
                raise RuntimeError("the lids' faces look into the head; their winding is the wrong way")
            return
    raise RuntimeError("the lids have no face between the lash line and the lid")
