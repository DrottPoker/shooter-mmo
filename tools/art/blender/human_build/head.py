"""The cage of the head's right half with the centre line, joined to the neck.

The head is the head cage of the Human Base Meshes bundle, a coarse cage of quads with loops around
the eyes, the mouth, the nose, and the ear. The reference tool has laid it onto the body's own
head, every vertex on the feature it belongs to, so the cage's vertices stay where they are and
only the vertices that subdividing adds are laid onto the reference. The faces that close the
cage's eyes and its underside are taken out: each eye leaves the cage with the band of faces around
it, and its lids are built in their place on the subdivided net (eye.py), the line between the lips
is parted into an upper and a lower lip with a mouth cavity behind them, and the ring under the
head is the top of the neck. The cage has no nostrils, which
are finer than its faces: the two faces under the nose over each nostril are opened into a ring on
the nostril's edge with a pocket behind it. The cage's ear is a box that leaves the cage with the
faces around it: the reference tool has taken the ear of the body's own control cage, which is
built for an ear's folds, with the skin around it, and it is set into the hole of the subdivided
net with a band of triangles on plain skin."""

import math

import numpy as np

from . import eye as eyes
from .body import ZONE, _unit
from .eye import SOCKET
from .net import FIXED, MIRROR, PINNED, RIGID, Net

X, Y, Z = np.eye(3)
# The faces inside the head, which are laid onto no skin: the pocket behind the eye's opening and
# the mouth cavity. They are the hidden parts 7 and 8 of the body files' face attribute "part".
MOUTH = "mouth"
HIDDEN_PARTS = {SOCKET: 7, MOUTH: 8}
# The pocket inside a nostril is laid onto no skin either, but it is seen from below: it is skin
# like any other, an island of its own in the UV map.
NOSTRIL = "nostril"

# The bundle's head cage, by its vertex and face numbers; the right side is the one at +x once
# the cage faces +Y.
NECK_FACES = (128, 129, 134, 135, 150, 151, 286, 287, 292, 293, 308, 309)
NECK_INNER = (295, 299, 301, 310, 312)
# The opening under the head, from the back centre over the right side to the front centre.
NECK_RING = (297, 256, 313, 232, 311, 265, 270, 269, 300)
NECK_CORNER, NECK_CORNER_DROP = 313, 0.006
# The right eye: the faces that close it, the vertices of its closed slit, and the ring around
# it from the corner beside the nose over the upper lid to the outer corner and back underneath.
# These faces and the band of faces around the ring leave the cage: the lids are built in their
# place on the subdivided net (eye.py).
EYE_FACES = (217, 218, 219, 220, 221, 222, 223, 224)
EYE_SLIT = (206, 207, 208, 209)
EYE_RING = (203, 181, 182, 198, 199, 205, 200, 201, 204, 202)
# The line between the lips from the centre to the right, the mouth's corner where it ends, and
# the lower lip's faces at the line. One face of the cage closes the corner, with the last vertex
# of the line and the corner across from each other: it is parted along the line into a face of the
# upper lip and one of the lower lip, so the line runs along edges all the way to the corner.
LIP_LINE = (286, 183, 271)
LIP_CORNER = 153
LOWER_LIP_FACES = (199, 216)
LIP_CORNER_FACE = 297
# The two faces under the nose that the right nostril lies across: the one under the tip, and the
# one at the base of the nose behind it. The nostril's opening is found as it is seen from below
# and in front, along a line NOSTRIL_VIEW degrees from straight up: the skin that lies more than
# NOSTRIL_DEEP behind the plane of the first face, at 1.79 m. Its edge is the first skin within
# NOSTRIL_RIM outside the opening that looks toward the eye, by NOSTRIL_FACING as the cosine of the
# angle, or the skin there that looks toward it most: the sharp edge where the skin under the nose
# turns into the nostril, not the round of the wing outside it. The cage has no nostril, and on
# the nose's wing its two faces end where the nostril's edge lies: an edge of their outline that
# comes nearer to the nostril's edge than NOSTRIL_INSIDE moves out along the skin, with its two
# vertices, as far as it lacks. The pocket behind the opening runs NOSTRIL_DEPTH along that line,
# nearly straight in, as a nostril does, so its wall is not seen from in front: it narrows to the
# share NOSTRIL_NARROWS of the opening.
NOSTRIL_FACES = (212, 167)
NOSTRIL_VIEW, NOSTRIL_DEEP = 40.0, 0.0025
NOSTRIL_RIM, NOSTRIL_FACING, NOSTRIL_INSIDE = 0.0025, 0.3, 0.0008
NOSTRIL_DEPTH, NOSTRIL_NARROWS = 0.010, 0.8
# The vertices that subdividing adds to the head lie between vertices on the reference already:
# the reference's skin is looked for this near them, at 1.79 m.
HEAD_REACH = 0.005
# The faces from the neck up to the ring under the head span the hollow under the jaw: the
# reference's skin is looked for this near their vertices.
THROAT_REACH = 0.02
# The line between the lips is looked for this far above and below a vertex of the cage's own
# line, which lies inside the mouth, and lies at most this far behind the lips, at 1.79 m.
LIP_SEARCH, LIP_DEPTH = 0.007, 0.002
# The mouth cavity behind the line between the lips, at 1.79 m. Its first ring follows the lips'
# own curve this far behind them, and parts this far above and below the line. Its other rings
# hold the teeth: how far behind the middle of the line, their half width, and how far above and
# below the line. Toward the corner a ring parts by the shares of MOUTH_TAPER of that, one share
# per vertex of the line, and reaches the shares of MOUTH_ACROSS of its half width. The line runs
# back around the teeth to the corner, so every vertex of a ring lies at least MOUTH_STEP behind
# its vertex of the ring in front of it.
MOUTH_LIPS = (0.005, 0.0025, 0.0025)
MOUTH_TAPER = (1.0, 0.9, 0.55)
MOUTH_ACROSS = (0.0, 0.45, 0.85)
MOUTH_STEP = 0.006
MOUTH_ROWS = ((0.022, 0.027, 0.014, 0.016), (0.054, 0.026, 0.012, 0.014))
# The right ear: the cage's faces of it, which stand out from the head as a box on a ring of the
# cage's vertices. They leave the cage with the faces that touch that ring, which the reference
# tool names, for the ear of the body's own control cage and the skin around it.
EAR_FACES = (265, 266, 267, 271, 272, 273, 274, 275, 276, 298, 299, 303, 304, 305, 306, 307)
# The teeth and the tongue behind the lips, at 1.79 m.
TEETH_BEHIND_LIPS, TEETH_HALF_WIDTH, TEETH_DEPTH, TEETH_THICKNESS = 0.008, 0.024, 0.040, 0.005
UPPER_TEETH_HEIGHT, LOWER_TEETH_HEIGHT = 0.009, 0.008
TONGUE_BEHIND_LIPS, TONGUE_BELOW_LIPS, TONGUE_LENGTH, TONGUE_HALF_WIDTH, TONGUE_THICKNESS = 0.014, 0.007, 0.042, 0.016, 0.006


class Head:
    def __init__(self, m, cage_points, cage_faces, eye, ear):
        """ear is the ear the reference tool took from the control cage, with the skin around it:
        its points, its faces by those points, the cage's faces that leave the cage for it, the
        cage's vertices around that hole, in order, the points of the patch's edge in order, and
        the triangles of the band between the two: each three of a vertex of the hole's outline
        in the subdivided net, by its place there, the cage's own at the even places and the
        middles of its edges at the odd ones, or, below zero, a point of the patch's edge as -1
        minus its place in that edge."""
        self.m = m
        self.ear_points, self.ear_faces, self.ear_cut, self.ear_hole, self.ear_rim, self.ear_band = ear
        s = m.scale
        self.scale = s
        self.faces = [tuple(f) for f in cage_faces]
        self.points = np.asarray(cage_points, float).copy()
        # The eyeball is a ball of the size and at the place of the reference's own, which has a
        # bulge for the cornea in front, so the lids of the reference lie on it: its radius is
        # half its width.
        self.eye = eye
        low, high = eye.points.min(0), eye.points.max(0)
        self.eye_centre = (low + high) / 2
        self.eye_radius = float(high[0] - low[0]) / 2
        self.right = [i for i in range(len(self.points)) if self.points[i, 0] >= 0.0]
        self.centre = {i for i in range(len(self.points)) if self.points[i, 0] == 0.0}
        # The edge of the reference's opening as the eye is seen from in front; the lids look along
        # its middle.
        self.rim = self.lid_edge()
        forward = _unit(self.rim.mean(0) - self.eye_centre)
        self.lids = eyes.Lids(m.surface, self.eye_centre, self.eye_radius, forward, s)
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
        # the line where the lips meet. The corner lies on the skin where the cage has it.
        for v in LIP_LINE:
            self.points[v] = self.between_lips(self.points[v])
        self.half_mouth = float(self.points[LIP_CORNER][0])
        self.neck_top = [self.points[v] for v in NECK_RING]
        # The nostril: the outline of the cage's faces over it, its edge, and the places on the
        # edge that the outline's corners open to.
        self.nostril_outline = outline([self.faces[index] for index in NOSTRIL_FACES])
        self.nostril_into, self.nostril_rim, self.nostril_moved = self.nostril_edge()
        self.nostril_room = max([float(np.linalg.norm(point - self.points[v])) for v, point in self.nostril_moved.items()], default=0.0)
        for v, point in self.nostril_moved.items():
            self.points[v] = point
        steps = np.linalg.norm(np.diff(np.concatenate([self.nostril_rim, self.nostril_rim[:1]]), axis=0), axis=1)
        self._nostril_along = np.concatenate([[0.0], np.cumsum(steps)])
        self.nostril_start = self._nostril_start()

    def back_line(self):
        """The cage's vertices on the middle of the back of the head, from the ring under the head
        up to the crown."""
        beside = {v: set() for v in self.centre}
        for face in self.faces:
            for a, b in zip(face, face[1:] + face[:1]):
                if a in self.centre and b in self.centre:
                    beside[a].add(b)
                    beside[b].add(a)
        crown = max(self.centre, key=lambda v: self.points[v][2])
        line = [NECK_RING[0]]
        while line[-1] != crown:
            onward = [v for v in beside[line[-1]] if v not in line and v not in NECK_INNER]
            if len(onward) != 1:
                raise RuntimeError(f"the middle line of the head's back divides at vertex {line[-1]}")
            line.append(onward[0])
        return line

    def eye_hole(self):
        """The cage's band around the eye and the vertices around the hole it leaves, from the
        corner beside the nose over the upper lid, as the lids' spokes run."""
        return eye_hole(self.faces, self.points, self.lids.crease[0])

    def lid_edge(self, count=96, step=0.00025):
        """The edge of the eye's opening as the eye is seen from straight in front, as points in
        order around it: from the corner beside the nose over the upper lid to the outer corner and
        back under the lower lid. A ray from in front that reaches the reference's eyeball before
        any skin lies in the opening; the edge is the skin just outside the outermost such ray in
        each direction, or the eyeball's own outline where the opening reaches it."""
        surface, centre, radius = self.m.surface, self.eye_centre, self.eye_radius
        far = 0.05
        span = np.arange(-radius, radius + 1e-9, step)

        def skin(a, b):
            return surface.ray(centre + X * a + Z * b + Y * far, -Y, far + radius)

        def ball(a, b):
            hit = self.eye.ray(centre + X * a + Z * b + Y * far, -Y, far + radius)
            return None if hit is None else hit[2]

        is_open = np.zeros((len(span), len(span)), bool)
        for i, a in enumerate(span):
            for j, b in enumerate(span):
                reach = ball(a, b)
                if reach is not None:
                    hit = skin(a, b)
                    is_open[i, j] = hit is None or hit[2] > reach
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
            hit = skin(spot[0], spot[1])
            behind = ball(spot[0], spot[1])
            if hit is not None and behind is not None and hit[2] <= behind:
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

    def nostril_edge(self, count=64, step=0.00025, half=0.012):
        """The way into the right nostril and the edge of its opening, as points in order around
        it, running the way the outline of the cage's faces over it does, and the new places of
        the outline's vertices that make room for it. Seen from below and in front, the opening is
        the skin that lies well behind the plane of the first of those faces. Its edge is the skin
        just outside the opening that looks toward the eye: the rim of the nose's wing, the strip
        between the nostrils, and the sill, and not the wall of the nostril that rises from them.
        The edge is evened out around the opening, and the outline keeps clear of it, so the skin
        between the two has a width everywhere."""
        s, surface = self.scale, self.m.surface
        tilt = math.radians(NOSTRIL_VIEW)
        into = -Y * math.sin(tilt) + Z * math.cos(tilt)
        across, up = X, np.cross(into, X)
        corners = self.points[list(self.faces[NOSTRIL_FACES[0]])]
        normal = np.cross(corners[2] - corners[0], corners[3] - corners[1])
        on_plane = corners.mean(0)
        centre = self.points[self.nostril_outline].mean(0)
        far = 0.03 * s
        span = np.arange(-half * s, half * s + 1e-9, step * s)

        def skin(a, b):
            origin = centre + across * a + up * b - into * far
            hit = surface.ray(origin, into, far + 0.03 * s)
            plane = ((on_plane - origin) @ normal) / (into @ normal)
            return hit, plane

        is_open = np.zeros((len(span), len(span)), bool)
        for i, a in enumerate(span):
            for j, b in enumerate(span):
                hit, plane = skin(a, b)
                is_open[i, j] = hit is None or hit[2] - plane > NOSTRIL_DEEP * s
        cells = np.argwhere(is_open)
        if not len(cells):
            raise RuntimeError("the reference has no nostril behind the head cage's faces over it")
        seed = tuple(cells[int(np.argmin((span[cells] ** 2).sum(1)))])
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
        if patch[0].any() or patch[-1].any() or patch[:, 0].any() or patch[:, -1].any():
            raise RuntimeError("the nostril's opening has no edge around it as it is seen from below")
        middle = span[np.argwhere(patch)].mean(0)
        ways = [np.array([math.cos(angle), math.sin(angle)]) for angle in 2.0 * math.pi * np.arange(count) / count]
        radii, depths = [], []
        for way in ways:
            reach = 0.0
            while True:
                spot = middle + way * (reach + step * s)
                cell = np.round((spot - span[0]) / (step * s)).astype(int)
                if not patch[cell[0], cell[1]]:
                    break
                reach += step * s
            # Onward from the opening: the first skin that looks toward the eye, or the skin that
            # looks toward it most.
            best = None
            for onward in np.arange(step * s, NOSTRIL_RIM * s + 1e-9, step * s):
                spot = middle + way * (reach + onward)
                hit, _plane = skin(spot[0], spot[1])
                if hit is None:
                    continue
                facing = -float(hit[1] @ into)
                if best is None or facing > best[0]:
                    best = (facing, reach + onward, hit[2])
                if facing >= NOSTRIL_FACING:
                    break
            if best is None:
                raise RuntimeError("the nostril's opening has no skin beside it as it is seen from below")
            radii.append(best[1])
            depths.append(best[2])
        # Evened out over the two neighbours on either side.
        radii = sum(np.roll(radii, shift) for shift in (-2, -1, 0, 1, 2)) / 5
        depths = sum(np.roll(depths, shift) for shift in (-2, -1, 0, 1, 2)) / 5
        # The outline as it is seen along the way in: a patch without a hollow in its edge, so a
        # point lies inside it when it lies on the inner side of every edge.
        flat = (self.points[self.nostril_outline] - centre) @ np.stack([across, up], axis=1)
        turns = np.cross(flat, np.roll(flat, -1, axis=0)).sum() >= 0.0
        ordered = flat if turns else flat[::-1]
        # An edge of the outline that comes too near to the nostril's edge moves out.
        start = ordered
        ordered = with_room(ordered, [middle + way * radius for way, radius in zip(ways, radii)], NOSTRIL_INSIDE * s)
        edges = np.roll(ordered, -1, axis=0) - ordered
        edges /= np.linalg.norm(edges, axis=1)[:, None]
        moved = {}
        vertices = self.nostril_outline if turns else self.nostril_outline[::-1]
        for v, before, after in zip(vertices, start, ordered):
            if np.linalg.norm(after - before) > 1e-6:
                hit, _plane = skin(after[0], after[1])
                if hit is None:
                    raise RuntimeError("the head has no skin where the outline around the nostril makes room for it")
                moved[v] = hit[0]
        rim = []
        for way, radius, depth in zip(ways, radii, depths):
            while radius > 0.0:
                away = middle + way * radius - ordered
                if (edges[:, 0] * away[:, 1] - edges[:, 1] * away[:, 0] >= NOSTRIL_INSIDE * s).all():
                    break
                radius -= step * s
            if radius <= 0.0:
                raise RuntimeError("the nostril's opening lies outside the head cage's faces over it")
            spot = middle + way * radius
            rim.append(centre + across * spot[0] + up * spot[1] + into * (depth - far))
        rim = np.array(rim)
        # The rim runs counter-clockwise seen along the way in; the outline may run the other way.
        return into, (rim if turns else rim[::-1]), moved

    def on_nostril(self, distance):
        """The point of the nostril's edge at a distance along it."""
        along, rim = self._nostril_along, self.nostril_rim
        distance = distance % along[-1]
        i = int(np.clip(np.searchsorted(along, distance, side="right") - 1, 0, len(rim) - 1))
        t = (distance - along[i]) / max(along[i + 1] - along[i], 1e-12)
        return rim[i] + t * (rim[(i + 1) % len(rim)] - rim[i])

    def nostril_ring(self, half=False):
        """Points of the nostril's edge at even distances around it, one for each corner of the
        outline of the cage's faces over it, or the points halfway between them."""
        count = len(self.nostril_outline)
        step = self._nostril_along[-1] / count
        return [self.on_nostril(self.nostril_start + (k + (0.5 if half else 0.0)) * step) for k in range(count)]

    def nostril_end(self, point):
        """The point of the end of the nostril's pocket behind a point of the nostril's edge. The
        end is flat, straight across the way in, however the edge rises."""
        into = self.nostril_into
        middle = np.mean(self.nostril_ring(), axis=0)
        out = np.asarray(point, float) - middle
        return middle + (out - into * (out @ into)) * NOSTRIL_NARROWS + into * (NOSTRIL_DEPTH * self.scale)

    def _nostril_start(self):
        """Where on the nostril's edge the ring starts, so its points lie nearest to the corners
        of the outline."""
        corners = self.points[self.nostril_outline]
        whole = self._nostril_along[-1]
        best = None
        for start in np.arange(0.0, whole, whole / 96):
            ring = [self.on_nostril(start + k * whole / len(corners)) for k in range(len(corners))]
            away = sum(float(np.sum((p - c) ** 2)) for p, c in zip(ring, corners))
            if best is None or away < best[0]:
                best = (away, start)
        return best[1]

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
        rim = self.nostril_rim
        return [f"mouth half width {self.half_mouth * 1000:.0f} mm, lips meet at forward {lips[1]:.4f}, height {lips[2]:.4f}",
                f"nostril {(rim[:, 0].max() - rim[:, 0].min()) * 1000:.1f} mm wide and "
                f"{(rim[:, 1].max() - rim[:, 1].min()) * 1000:.1f} mm long, {rim[:, 0].min() * 1000:.1f} mm from the middle; "
                f"{len(self.nostril_moved)} vertices of the cage around it made room, by up to {self.nostril_room * 1000:.1f} mm",
                f"eye centre {np.round(self.eye_centre, 4).tolist()}, radius {self.eye_radius * 1000:.1f} mm, opening "
                f"{(self.rim[:, 0].max() - self.rim[:, 0].min()) * 1000:.1f} mm wide and "
                f"{(self.rim[:, 2].max() - self.rim[:, 2].min()) * 1000:.1f} mm high",
                *self.lids.report(),
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
        """The head's skin: the reference above the base of the neck. The faces under the jaw are
        laid onto the same skin under a name of their own, and the ear's faces have theirs."""
        m = self.m
        skin = m.surface.part(lambda c: (c[:, 2] > m.notch_z + 0.02 * m.scale) & (c[:, 0] > -0.03))
        return {"head": skin, "ear": skin, "throat": skin}

    def reaches(self):
        """How near each of the head's skins is looked for."""
        s = self.scale
        return {"head": HEAD_REACH * s, "ear": HEAD_REACH * s, "throat": THROAT_REACH * s}


def with_room(corners, inside, margin, rounds=8):
    """The corners of an outline in a plane, counter-clockwise, with room for the points inside
    it: an edge that comes nearer to a point than the margin moves out, with its two corners, as
    far as it lacks. A corner that two edges move goes with both."""
    corners, inside = np.array(corners, float), np.asarray(inside, float)
    for _ in range(rounds):
        edges = np.roll(corners, -1, axis=0) - corners
        edges /= np.linalg.norm(edges, axis=1)[:, None]
        shift = np.zeros_like(corners)
        for k in range(len(corners)):
            away = inside - corners[k]
            lacks = margin - float((edges[k, 0] * away[:, 1] - edges[k, 1] * away[:, 0]).min())
            if lacks > 1e-6:
                out = np.array([edges[k, 1], -edges[k, 0]]) * lacks
                shift[k] += out
                shift[(k + 1) % len(corners)] += out
        if not shift.any():
            break
        corners += shift
    return corners


def outline(faces):
    """The vertices around a patch of faces that share edges, in the order the faces run."""
    onward = {}
    for face in faces:
        for a, b in zip(face, face[1:] + face[:1]):
            onward[a, b] = True
    onward = {a: b for a, b in onward if (b, a) not in onward}
    first = next(iter(onward))
    loop = [first]
    while onward[loop[-1]] != first:
        loop.append(onward[loop[-1]])
        if len(loop) > len(onward):
            raise RuntimeError("the faces do not make one patch with one edge around it")
    if len(loop) != len(onward):
        raise RuntimeError("the faces do not make one patch with one edge around it")
    return loop


def eye_hole(faces, points, corner, eye_faces=EYE_FACES, ring=EYE_RING):
    """The faces of the band around the eye's ring, which leave the cage with the faces that close
    the eye, and the cage's vertices around the hole they leave, in order: from the one nearest the
    corner beside the nose, which is a point, over the upper lid first."""
    band = [index for index, corners in enumerate(faces) if index not in eye_faces and set(corners) & set(ring)]
    hole = outline([faces[index] for index in list(eye_faces) + band])
    points = np.asarray(points, float)
    first = min(range(len(hole)), key=lambda k: float(np.linalg.norm(points[hole[k]] - corner)))
    hole = hole[first:] + hole[:first]
    if points[hole[-1]][2] > points[hole[1]][2]:
        hole = hole[:1] + hole[:0:-1]
    return band, hole


def build_head(net: Net, head: Head):
    s = head.scale
    zone = ZONE["Head"]
    # The band around the eye leaves the cage with the faces that close the eye, and so do the
    # vertices of the eye's ring and slit, which only they use.
    band, hole = head.eye_hole()
    drop = set(NECK_INNER) | set(EYE_SLIT) | set(EYE_RING)
    # The cage's vertices keep their places on the features; the vertices that subdividing adds are
    # laid onto the reference. The ring under the head may still move along the skin: it lies on
    # the plain skin of the neck and gives way where a face would fold. The vertices that only the
    # faces use which leave the cage for the ear are left out with them.
    loose = set(NECK_RING)
    around = {v for index, corners in enumerate(head.faces) if index not in head.ear_cut for v in corners}
    drop |= {v for index in head.ear_cut for v in head.faces[index]} - around
    # The line between the lips stays exactly where it is put.
    lines = set(LIP_LINE) | {LIP_CORNER}
    vertex = {}
    for v in head.right:
        if v in drop:
            continue
        vertex[v] = net.add(head.points[v], *([] if v in loose else [FIXED]), *([PINNED] if v in lines else []),
                            *([MIRROR] if v in head.centre else []))

    # The head is one island of the UV map, with the face in its middle, opened up the back of the
    # head, where the hair lies.
    net.island = "head"
    net.seam([vertex[v] for v in head.back_line()])
    # The lower lip takes its own vertices along the line between the lips.
    lower = {v: net.add(head.points[v], FIXED, PINNED, *([MIRROR] if v in head.centre else [])) for v in LIP_LINE}
    skip = set(NECK_FACES) | set(EYE_FACES) | set(band) | set(NOSTRIL_FACES) | set(head.ear_cut)
    net.mark("ear_hole", [vertex[v] for v in head.ear_hole])
    net.mark("eye_hole", [vertex[v] for v in hole])
    for index, corners in enumerate(head.faces):
        if index in skip or any(v not in vertex for v in corners):
            continue
        if index == LIP_CORNER_FACE:
            # From the line's last vertex around: the lip on one side, the corner, the other lip.
            k = corners.index(LIP_LINE[-1])
            end, first, corner, second = corners[k:] + corners[:k]
            if corner != LIP_CORNER:
                raise RuntimeError("the head cage's face at the mouth's corner is not the one the build knows")
            for beside, face in ((first, (end, first, corner)), (second, (corner, second, end))):
                under = head.points[beside][2] < head.points[end][2]
                net.face([lower[v] if under and v == end else vertex[v] for v in face], zone, "head")
        elif index in LOWER_LIP_FACES:
            net.face([lower.get(v, vertex[v]) for v in corners], zone, "head")
        else:
            # The faces at the ring under the head span the hollow under the jaw like the neck's.
            under = bool(set(corners) & set(NECK_RING))
            net.face([vertex[v] for v in corners], zone, "throat" if under else "head")

    # The neck's top ring is the opening under the head.
    neck_top = [vertex[v] for v in NECK_RING]
    net.island = "neck"
    net.seam([net.marks["neck_end"][0], neck_top[0]])
    net.strip(net.marks["neck_end"], neck_top, ZONE["Neck"], "throat", closed=False)
    net.mark("neck_top", neck_top)

    # The nostril: the cage's faces over it open into a ring on the nostril's edge, with the skin
    # under the nose around it and a pocket behind it.
    outer = [vertex[v] for v in head.nostril_outline]
    ring = [net.add(point, FIXED, PINNED) for point in head.nostril_ring()]
    count = len(ring)
    if count % 2:
        raise RuntimeError("the outline of the faces over the nostril has an odd number of vertices")
    net.island = "head"
    for k in range(count):
        net.face((outer[k], outer[(k + 1) % count], ring[(k + 1) % count], ring[k]), zone, "head")
    deep = [net.add(head.nostril_end(net.points[v]), RIGID) for v in ring]
    net.island = "nostril_R"
    for k in range(count):
        net.face((ring[k], ring[(k + 1) % count], deep[(k + 1) % count], deep[k]), zone, NOSTRIL)
    # The pocket's end: faces of four around a vertex in its middle.
    end = net.add(np.mean([net.points[v] for v in deep], axis=0), RIGID)
    for k in range(0, count, 2):
        net.face((end, deep[k], deep[(k + 1) % count], deep[(k + 2) % count]), zone, NOSTRIL)
    net.mark("nostril_ring", ring)
    net.mark("nostril_deep", deep)

    # The mouth: the inner lips behind the line between the lips, and the cavity behind them. Each
    # ring runs from the middle of the upper lip over the corner to the middle of the lower lip.
    line = [np.asarray(head.points[v], float) for v in LIP_LINE]
    corner = np.asarray(head.points[LIP_CORNER], float)
    middle = line[0]
    depth, up, down = MOUTH_LIPS
    net.island = "mouth"

    def ring_of(above, across, below):
        points = list(above) + [across] + list(reversed(below))
        if len(rows) > 1:
            # Behind the ring in front of it, vertex for vertex.
            points = [(p[0], min(p[1], net.points[before][1] - MOUTH_STEP * s), p[2]) for p, before in zip(points, rows[-1])]
        return [net.add(p, RIGID, *([MIRROR] if p[0] == 0.0 else [])) for p in points]

    rows = [[vertex[v] for v in LIP_LINE] + [vertex[LIP_CORNER]] + [lower[v] for v in reversed(LIP_LINE)]]
    rows.append(ring_of([(p[0], p[1] - depth * s, p[2] + up * s * share) for p, share in zip(line, MOUTH_TAPER)],
                        (corner[0] * 0.94, corner[1] - depth * s * 0.7, corner[2]),
                        [(p[0], p[1] - depth * s, p[2] - down * s * share) for p, share in zip(line, MOUTH_TAPER)]))
    for depth, half, up, down in MOUTH_ROWS:
        back_y, z = middle[1] - depth * s, middle[2]
        rows.append(ring_of([(half * s * share, back_y + 0.003 * s * share, z + up * s * taper)
                             for share, taper in zip(MOUTH_ACROSS, MOUTH_TAPER)],
                            (half * s, back_y + 0.003 * s, z),
                            [(half * s * share, back_y + 0.003 * s * share, z - down * s * taper)
                             for share, taper in zip(MOUTH_ACROSS, MOUTH_TAPER)]))
    for near, far in zip(rows[:-1], rows[1:]):
        net.strip(near, far, zone, MOUTH, closed=False)
    last = rows[-1]
    net.face((last[0], last[1], last[5], last[6]), zone, MOUTH)
    net.face((last[1], last[2], last[4], last[5]), zone, MOUTH)
    net.face((last[2], last[3], last[4]), zone, MOUTH)
    net.mark("lip_upper", [vertex[v] for v in LIP_LINE] + [vertex[LIP_CORNER]])
    net.mark("lip_lower", [lower[v] for v in LIP_LINE] + [vertex[LIP_CORNER]])
    net.mark("lip_inner", rows[1])


def graft_ear(fine: Net, coarse: Net, head: Head):
    """Sets the ear into the hole of the subdivided net: the control cage's faces, whose vertices
    keep their places while the net is laid onto the skin, and the triangles of the band around
    them, which reach the vertices around the hole: the cage's own and the ones that subdividing
    put between them. The ear's vertices are fixed and not pinned: where a face of the control
    cage is twisted so far on a body that no cut leaves it unfolded, they give way."""
    hole = coarse.marks["ear_hole"]
    around = []
    for vertex, after in zip(hole, hole[1:] + hole[:1]):
        around += [vertex, fine.edge_point[(vertex, after) if vertex < after else (after, vertex)]]
    ids = [fine.add(point, FIXED) for point in head.ear_points]
    fine.island = "head"
    for face in head.ear_faces:
        fine.face([ids[v] for v in face], ZONE["Head"], "ear")
    rim = [ids[v] for v in head.ear_rim]
    for triple in zip(head.ear_band[0::3], head.ear_band[1::3], head.ear_band[2::3]):
        fine.face([around[k] if k >= 0 else rim[-1 - k] for k in triple], ZONE["Head"], "head")


def graft_eye(fine: Net, coarse: Net, head: Head):
    """Builds the lids into the hole the cage's band around the eye left; see eye.py."""
    eyes.graft(fine, coarse, head.lids)


def refine_head(fine: Net, coarse: Net, head: Head):
    """Places the vertices that subdividing put on the feature lines: the nostril's edge, and the
    line between the lips, where the upper and the lower lip must meet point for point all the way
    to the mouth's corner."""
    def between(a, b):
        return fine.edge_point[(a, b) if a < b else (b, a)]

    upper, lower = coarse.marks["lip_upper"], coarse.marks["lip_lower"]
    # The ring of the inner lips behind the line runs over the corner, as the two lips together do.
    inner = coarse.marks["lip_inner"]
    behind = {"upper": inner[:len(upper)], "lower": inner[len(upper) - 1:][::-1]}
    for k in range(len(upper) - 1):
        point = head.between_lips(fine.points[between(upper[k], upper[k + 1])])
        for name, line in (("upper", upper), ("lower", lower)):
            vertex = between(line[k], line[k + 1])
            fine.points[vertex] = point
            fine.holds[vertex].update((FIXED, PINNED))
            # The line's new vertex has moved into the fold, toward the middle of the inner lip's
            # face behind it: that middle lies halfway to the ring behind the line.
            back = (np.asarray(coarse.points[behind[name][k]], float) + np.asarray(coarse.points[behind[name][k + 1]], float)) / 2
            for index, face in enumerate(coarse.faces):
                if {line[k], line[k + 1], behind[name][k], behind[name][k + 1]} == set(face):
                    fine.points[fine.face_point[index]] = (point + back) / 2
    # The nostril's edge and the end of its pocket are as round between the cage's vertices as at
    # them, and so is the pocket's wall between the two.
    ring, deep = coarse.marks["nostril_ring"], coarse.marks["nostril_deep"]
    for k, point in enumerate(head.nostril_ring(half=True)):
        after = (k + 1) % len(ring)
        vertex = between(ring[k], ring[after])
        fine.points[vertex] = point
        fine.holds[vertex].update((FIXED, PINNED))
        end = head.nostril_end(point)
        fine.points[between(deep[k], deep[after])] = end
        for index, face in enumerate(coarse.faces):
            if {ring[k], ring[after], deep[k], deep[after]} == set(face):
                fine.points[fine.face_point[index]] = (np.asarray(point, float) + end) / 2
