"""The cage of the head's right half with the centre line, joined to the neck.

The head is the head cage of the Human Base Meshes bundle, a coarse cage of quads with loops around
the eyes, the mouth, the nose, and the ear. The reference tool has laid it onto the body's own
head, every vertex on the feature it belongs to, so the cage's vertices stay where they are and
only the vertices that subdividing adds are laid onto the reference. The faces that close the
cage's eyes and its underside are taken out: the eye becomes an opening with a pocket behind it for
the eyeball, the line between the lips is parted into an upper and a lower lip with a mouth cavity
behind them, and the ring under the head is the top of the neck."""

import math

import numpy as np

from .body import ZONE, _unit
from .net import FIXED, MIRROR, RIGID, Net

X, Y, Z = np.eye(3)

# The bundle's head cage, by its vertex and face numbers; the right side is the one at +x once
# the cage faces +Y.
NECK_FACES = (128, 129, 134, 135, 150, 151, 286, 287, 292, 293, 308, 309)
NECK_INNER = (295, 299, 301, 310, 312)
# The opening under the head, from the back centre over the right side to the front centre.
NECK_RING = (297, 256, 313, 232, 311, 265, 270, 269, 300)
NECK_CORNER, NECK_CORNER_DROP = 313, 0.006
# The right eye: the faces that close it, the vertices of its closed slit, and the ring around
# it from the corner beside the nose over the upper lid to the outer corner and back underneath.
EYE_FACES = (217, 218, 219, 220, 221, 222, 223, 224)
EYE_SLIT = (206, 207, 208, 209)
EYE_RING = (203, 181, 182, 198, 199, 205, 200, 201, 204, 202)
# The line between the lips from the centre to the right, its last vertex before the corner, the
# corner, and the lower lip's faces at the line.
LIP_LINE = (286, 183)
LIP_END = 271
LIP_CORNER = 153
LOWER_LIP_FACES = (199, 216)
LID_CLEARANCE = 0.0004
# The vertices that subdividing adds to the head lie between vertices on the reference already:
# the reference's skin is looked for this near them, at 1.79 m.
HEAD_REACH = 0.005
# The faces from the neck up to the ring under the head span the hollow under the jaw: the
# reference's skin is looked for this near their vertices.
THROAT_REACH = 0.02
# The line between the lips is looked for this far above and below a vertex of the cage's own
# line, which lies inside the mouth, and lies at most this far behind the lips, at 1.79 m.
LIP_SEARCH, LIP_DEPTH = 0.007, 0.002
# The lid's inner edge continues this far around the eyeball, in radians.
LID_TUCK = 0.32
# The mouth cavity behind the line between the lips, at 1.79 m. Its first ring follows the lips'
# own curve this far behind them, and parts this far above and below the line. Its other rings
# hold the teeth: how far behind the middle of the line, their half width, and how far above and
# below the line.
MOUTH_LIPS = (0.005, 0.0025, 0.0025)
MOUTH_ROWS = ((0.022, 0.027, 0.014, 0.016), (0.054, 0.026, 0.012, 0.014))
# The right ear: its faces, which stand out from the head. Its folds are finer than the net, so
# its vertices are laid onto the reference's ear and then evened out in this many rounds, which
# leaves the ear's outline and its hollow and takes out the crumples.
EAR_FACES = (265, 266, 267, 271, 272, 273, 274, 275, 276, 298, 299, 303, 304, 305, 306, 307)
EAR_SMOOTHING = 3
# The teeth and the tongue behind the lips, at 1.79 m.
TEETH_BEHIND_LIPS, TEETH_HALF_WIDTH, TEETH_DEPTH, TEETH_THICKNESS = 0.008, 0.024, 0.040, 0.005
UPPER_TEETH_HEIGHT, LOWER_TEETH_HEIGHT = 0.009, 0.008
TONGUE_BEHIND_LIPS, TONGUE_BELOW_LIPS, TONGUE_LENGTH, TONGUE_HALF_WIDTH, TONGUE_THICKNESS = 0.014, 0.007, 0.042, 0.016, 0.006


class Head:
    def __init__(self, m, cage_points, cage_faces, eye_centre, eye_radius):
        self.m = m
        s = m.scale
        self.scale = s
        self.faces = [tuple(f) for f in cage_faces]
        self.points = np.asarray(cage_points, float).copy()
        # The eyeball is the reference's own, so the lids of the reference lie on it.
        self.eye_centre = np.asarray(eye_centre, float)
        self.eye_radius = float(eye_radius)
        self.right = [i for i in range(len(self.points)) if self.points[i, 0] >= 0.0]
        self.centre = {i for i in range(len(self.points)) if self.points[i, 0] == 0.0}
        # The ring around the eye takes the lids' edge of the reference, as the eye is seen from in
        # front: the corner beside the nose, four points over the upper lid, the outer corner, and
        # four points under the lower lid.
        self.rim = self.lid_edge()
        steps = np.linalg.norm(np.diff(np.concatenate([self.rim, self.rim[:1]]), axis=0), axis=1)
        self._rim_steps = steps
        self._rim_along = np.concatenate([[0.0], np.cumsum(steps)])
        outer = int(np.argmax(self.rim[:, 0]))
        upper, whole = self._rim_along[outer], self._rim_along[-1]
        self.rim_stations = [upper * k / 5 for k in range(6)] + [upper + (whole - upper) * k / 5 for k in range(1, 5)]
        self.rim_length = float(whole)
        for v, distance in zip(EYE_RING, self.rim_stations):
            self.points[v] = self.on_rim(distance)
        # One face of the cage has two of its sides on the ring under the head, and the corner
        # between them lies 6 mm from the next vertex of the ring: the corner moves to the middle
        # between its neighbours on the ring and a little below their line, so the ring's vertices
        # lie evenly and the face has the shape of a kite.
        k = NECK_RING.index(NECK_CORNER)
        before, after = self.points[NECK_RING[k - 1]], self.points[NECK_RING[k + 1]]
        found = m.surface.nearest((before + after) / 2 - Z * (NECK_CORNER_DROP * s), 0.02)
        if found is not None:
            self.points[NECK_CORNER] = found[0]
        # The cage's line between the lips lies inside the mouth: its vertices move forward to
        # the line where the lips meet.
        for v in LIP_LINE + (LIP_END,):
            self.points[v] = self.between_lips(self.points[v])
        self.half_mouth = float(self.points[LIP_CORNER][0])
        self.neck_top = [self.points[v] for v in NECK_RING]

    def on_lid(self, point):
        """A point of the lids' edge, kept clear of the eyeball."""
        offset = np.asarray(point, float) - self.eye_centre
        reach = max(np.linalg.norm(offset), self.eye_radius + LID_CLEARANCE)
        return self.eye_centre + _unit(offset) * reach

    def lid_edge(self, count=96, step=0.00025):
        """The edge of the eye's opening as the eye is seen from straight in front, as points in
        order around it: from the corner beside the nose over the upper lid to the outer corner and
        back under the lower lid. A ray from in front that reaches the eyeball before any skin lies
        in the opening; the edge is the skin just outside the outermost such ray in each direction,
        or the eyeball's own outline where the opening reaches it."""
        surface, centre, radius = self.m.surface, self.eye_centre, self.eye_radius
        far = 0.05
        span = np.arange(-radius, radius + 1e-9, step)

        def skin(a, b):
            return surface.ray(centre + X * a + Z * b + Y * far, -Y, far + radius)

        is_open = np.zeros((len(span), len(span)), bool)
        for i, a in enumerate(span):
            for j, b in enumerate(span):
                within = radius * radius - a * a - b * b
                if within > 0.0:
                    hit = skin(a, b)
                    is_open[i, j] = hit is None or hit[2] > far - math.sqrt(within) + 0.0002
        # The opening is the open patch nearest the middle.
        cells = np.argwhere(is_open)
        seed = tuple(cells[int(np.argmin(((span[cells] ** 2).sum(1))))])
        patch = np.zeros_like(is_open)
        patch[seed] = True
        todo = [seed]
        while todo:
            i, j = todo.pop()
            for di, dj in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                k = (i + di, j + dj)
                if 0 <= k[0] < len(span) and 0 <= k[1] < len(span) and is_open[k] and not patch[k]:
                    patch[k] = True
                    todo.append(k)
        middle = span[np.argwhere(patch)].mean(0)
        rim = []
        for k in range(count):
            angle = 2.0 * math.pi * k / count
            way = np.array([math.cos(angle), math.sin(angle)])
            reach = 0.0
            while True:
                spot = middle + way * (reach + step)
                cell = np.round((spot - span[0]) / step).astype(int)
                if not (0 <= cell[0] < len(span) and 0 <= cell[1] < len(span)) or not patch[cell[0], cell[1]]:
                    break
                reach += step
            spot = middle + way * (reach + step)
            within = radius * radius - spot @ spot
            hit = skin(spot[0], spot[1]) if within > 0.0 else None
            if hit is not None and hit[2] < far - math.sqrt(within) + 0.0002:
                rim.append(hit[0])
            else:
                spot = middle + way * reach
                rim.append(centre + X * spot[0] + Z * spot[1] + Y * math.sqrt(max(radius * radius - spot @ spot, 0.0)))
        rim = np.array(rim)
        # From the corner beside the nose, over the upper lid first.
        rim = np.roll(rim, -int(np.argmin(rim[:, 0])), axis=0)
        outer = int(np.argmax(rim[:, 0]))
        if rim[1:outer, 2].mean() < rim[outer + 1:, 2].mean():
            rim = np.concatenate([rim[:1], rim[:0:-1]])
        return rim

    def on_rim(self, distance):
        """The point of the lids' edge at a distance along it, kept clear of the eyeball."""
        along, steps, rim = self._rim_along, self._rim_steps, self.rim
        distance = distance % along[-1]
        i = int(np.clip(np.searchsorted(along, distance, side="right") - 1, 0, len(rim) - 1))
        t = (distance - along[i]) / max(steps[i], 1e-12)
        return self.on_lid(rim[i] + t * (rim[(i + 1) % len(rim)] - rim[i]))

    def between_lips(self, point):
        """The point of the line where the lips meet in front of a point inside the mouth: seen
        from in front, the deepest point between the upper and the lower lip, or, where the lips
        stand apart, a point a little behind them."""
        s, surface = self.scale, self.m.surface
        x, z0 = float(point[0]), float(point[2])
        heights = z0 + np.arange(-LIP_SEARCH, LIP_SEARCH + 1e-9, 0.00025) * s
        fronts = []
        for z in heights:
            hit = surface.ray((x, point[1] + 0.05, z), -Y, 0.12)
            fronts.append(point[1] if hit is None else hit[0][1])
        fronts = np.array(fronts)
        middle = len(heights) // 2
        lower = int(np.argmax(fronts[:middle]))
        upper = middle + int(np.argmax(fronts[middle:]))
        deepest = lower + int(np.argmin(fronts[lower:upper + 1]))
        forward = max(fronts[deepest], min(fronts[lower], fronts[upper]) - LIP_DEPTH * s)
        return np.array([x, forward, heights[deepest]])

    def report(self):
        s, notch = self.scale, self.m.notch_z
        above = [round((p[2] - notch) / s * 1000) for p in self.neck_top]
        lips = self.points[LIP_LINE[0]]
        return [f"mouth half width {self.half_mouth * 1000:.0f} mm, lips meet at forward {lips[1]:.4f}, height {lips[2]:.4f}",
                f"eye centre {np.round(self.eye_centre, 4).tolist()}, radius {self.eye_radius * 1000:.1f} mm, opening "
                f"{(self.rim[:, 0].max() - self.rim[:, 0].min()) * 1000:.1f} mm wide and "
                f"{(self.rim[:, 2].max() - self.rim[:, 2].min()) * 1000:.1f} mm high",
                f"neck top ring {above} mm above the notch, from the back to the front, at a height of 1.79 m"]

    def inner_parts(self):
        """The eyeballs, the teeth, and the tongue as (name, points, faces, uvs)."""
        from . import parts
        s = self.scale
        lips = self.points[LIP_LINE[0]]
        out = []
        for side, sign in (("R", 1.0), ("L", -1.0)):
            centre = self.eye_centre * (sign, 1.0, 1.0)
            points, faces, uvs = parts.eyeball(centre, self.eye_radius)
            out.append((f"Eye_{side}", points, faces, uvs))
        front = lips - Y * (TEETH_BEHIND_LIPS * s)
        points, faces = parts.teeth(front - Z * (0.0015 * s), TEETH_HALF_WIDTH * s, TEETH_DEPTH * s,
                                    UPPER_TEETH_HEIGHT * s, TEETH_THICKNESS * s, upper=True)
        out.append(("Teeth_Upper", points, faces, None))
        points, faces = parts.teeth(front - Y * (0.0015 * s) - Z * (0.001 * s), (TEETH_HALF_WIDTH - 0.001) * s,
                                    (TEETH_DEPTH - 0.001) * s, LOWER_TEETH_HEIGHT * s, TEETH_THICKNESS * s, upper=False)
        out.append(("Teeth_Lower", points, faces, None))
        tip = lips - Y * (TONGUE_BEHIND_LIPS * s) - Z * (TONGUE_BELOW_LIPS * s)
        points, faces = parts.tongue(tip, TONGUE_LENGTH * s, TONGUE_HALF_WIDTH * s, TONGUE_THICKNESS * s)
        out.append(("Tongue", points, faces, None))
        return out

    def skins(self):
        """The head's skin: the reference above the base of the neck. The ear's faces and the
        faces under the jaw are laid onto the same skin under names of their own."""
        m = self.m
        skin = m.surface.part(lambda c: (c[:, 2] > m.notch_z + 0.02 * m.scale) & (c[:, 0] > -0.03))
        return {"head": skin, "ear": skin, "throat": skin}

    def reaches(self):
        """How near each of the head's skins is looked for."""
        s = self.scale
        return {"head": HEAD_REACH * s, "ear": HEAD_REACH * s, "throat": THROAT_REACH * s}


def build_head(net: Net, head: Head):
    s = head.scale
    zone = ZONE["Head"]
    drop = set(NECK_INNER) | set(EYE_SLIT)
    beside_ear = {v for index, face in enumerate(head.faces) if index not in EAR_FACES for v in face}
    vertex = {}
    for v in head.right:
        if v in drop:
            continue
        # The cage's vertices keep their places on the features; the vertices that subdividing
        # adds are laid onto the reference. The ear's own vertices are evened out with it.
        vertex[v] = net.add(head.points[v], *([FIXED] if v in beside_ear else []), *([MIRROR] if v in head.centre else []))

    # The lower lip takes its own vertices along the line between the lips.
    lower = {v: net.add(head.points[v], FIXED, *([MIRROR] if v in head.centre else [])) for v in LIP_LINE}
    skip = set(NECK_FACES) | set(EYE_FACES)
    for index, corners in enumerate(head.faces):
        if index in skip or any(v not in vertex for v in corners):
            continue
        if index in LOWER_LIP_FACES:
            net.face([lower.get(v, vertex[v]) for v in corners], zone, "head")
        else:
            # The faces at the ring under the head span the hollow under the jaw like the neck's.
            under = bool(set(corners) & set(NECK_RING))
            net.face([vertex[v] for v in corners], zone, "ear" if index in EAR_FACES else "throat" if under else "head")

    # The neck's top ring is the opening under the head.
    neck_top = [vertex[v] for v in NECK_RING]
    net.strip(net.marks["neck_end"], neck_top, ZONE["Neck"], "throat", closed=False)
    net.mark("neck_top", neck_top)

    # The eye: the lids' inner edge a little under the lid on the eyeball, and a pocket behind it.
    centre = head.eye_centre
    ring = [vertex[v] for v in EYE_RING]
    middle = np.mean([net.points[v] for v in ring], axis=0)
    forward = _unit(middle - centre)
    under = []
    for v in ring:
        # Each point of the lid's edge continues around the eyeball, away from the line of sight,
        # so the pocket stays hidden under the lid also at the corners.
        direction = _unit(np.asarray(net.points[v], float) - centre)
        polar = math.acos(float(np.clip(direction @ forward, -1.0, 1.0))) + LID_TUCK
        radial = _unit(direction - (direction @ forward) * forward)
        tucked = forward * math.cos(polar) + radial * math.sin(polar)
        under.append(net.add(centre + tucked * (head.eye_radius + LID_CLEARANCE), RIGID))
    # Seen from in front the ring runs clockwise for the right eye, so the pocket's faces look
    # into the opening.
    net.strip(ring, under, zone, None)
    back = net.add(centre - forward * (head.eye_radius + 0.003 * s), RIGID)
    for k in range(len(under)):
        net.face((under[k], under[(k + 1) % len(under)], back), zone, None)
    net.mark("eye_ring", ring)

    # The mouth: the inner lips behind the line between the lips, and the cavity behind them.
    upper = [vertex[286], vertex[183], vertex[LIP_END], lower[183], lower[286]]
    middle, beside, last = (np.asarray(head.points[v], float) for v in (286, 183, LIP_END))
    depth, up, down = MOUTH_LIPS
    rows = [upper, [
        net.add((0.0, middle[1] - depth * s, middle[2] + up * s), RIGID, MIRROR),
        net.add((beside[0], beside[1] - depth * s, beside[2] + up * s * 0.9), RIGID),
        net.add((last[0] * 0.92, last[1] - depth * s * 0.7, last[2]), RIGID),
        net.add((beside[0], beside[1] - depth * s, beside[2] - down * s * 0.9), RIGID),
        net.add((0.0, middle[1] - depth * s, middle[2] - down * s), RIGID, MIRROR),
    ]]
    for depth, half, up, down in MOUTH_ROWS:
        back_y, z = middle[1] - depth * s, middle[2]
        rows.append([
            net.add((0.0, back_y, z + up * s), RIGID, MIRROR),
            net.add((half * s * 0.5, back_y, z + up * s * 0.9), RIGID),
            net.add((half * s, back_y + 0.003 * s, z), RIGID),
            net.add((half * s * 0.5, back_y, z - down * s * 0.9), RIGID),
            net.add((0.0, back_y, z - down * s), RIGID, MIRROR),
        ])
    for near, far in zip(rows[:-1], rows[1:]):
        net.strip(near, far, zone, None, closed=False)
    last = rows[-1]
    net.face((last[0], last[1], last[3], last[4]), zone, None)
    net.face((last[1], last[2], last[3]), zone, None)
    net.mark("lip_upper", [vertex[286], vertex[183], vertex[LIP_END]])
    net.mark("lip_lower", [lower[286], lower[183], vertex[LIP_END]])
    net.mark("lip_corner", [vertex[LIP_END], vertex[LIP_CORNER]])


def refine_head(fine: Net, coarse: Net, head: Head):
    """Places the vertices that subdividing put on the two feature lines: the lids' edge, and the
    line between the lips, where the upper and the lower lip must meet point for point."""
    def between(a, b):
        return fine.edge_point[(a, b) if a < b else (b, a)]

    ring = coarse.marks["eye_ring"]
    stations = head.rim_stations + [head.rim_length]
    for k in range(len(ring)):
        vertex = between(ring[k], ring[(k + 1) % len(ring)])
        fine.points[vertex] = head.on_rim((stations[k] + stations[k + 1]) / 2)
        fine.holds[vertex].add(FIXED)
    upper, lower = coarse.marks["lip_upper"], coarse.marks["lip_lower"]
    for k in range(len(upper) - 1):
        point = head.between_lips(fine.points[between(upper[k], upper[k + 1])])
        for line in (upper, lower):
            vertex = between(line[k], line[k + 1])
            fine.points[vertex] = point
            fine.holds[vertex].add(FIXED)
    # One face closes the mouth's corner, from the end of the line between the lips to the corner
    # with the upper lip above and the lower lip below: its middle lies in the fold between them.
    end, corner = coarse.marks["lip_corner"]
    for index, face in enumerate(coarse.faces):
        if end in face and corner in face:
            vertex = fine.face_point[index]
            fine.points[vertex] = head.between_lips(fine.points[vertex])
            fine.holds[vertex].add(FIXED)
