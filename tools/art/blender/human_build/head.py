"""The cage of the head's right half with the centre line, joined to the neck.

The head starts from the head cage of the Human Base Meshes bundle, a coarse cage of quads with
loops around the eyes, the mouth, and the nose. It is brought to the body's own head in three steps:
scaled and moved so its features lie near the reference's, warped so the loops lie on the features
themselves, the rim of the eye's opening, the line between the lips, the nose, the chin, the ear,
and the top of the neck, and then laid onto the reference's skin. The faces that close its eyes and
its underside are taken out: the eye becomes an opening with a pocket behind it for the eyeball,
the line between the lips is parted into an upper and a lower lip with a mouth cavity behind them,
and the opening under the head takes the neck's ring of sixteen."""

import math

import numpy as np

from .body import TRUNK_COLUMNS, ZONE, _unit
from .face import Face
from .net import FIXED, MIRROR, RIGID, Net

X, Y, Z = np.eye(3)

# The bundle's head cage, by its vertex and face numbers; the right side is the one at +x once
# the cage faces +Y.
NECK_FACES = (128, 129, 134, 135, 150, 151, 286, 287, 292, 293, 308, 309)
NECK_INNER = (295, 299, 301, 310, 312)
# The opening under the head, from the back centre over the right side to the front centre.
NECK_RING = (297, 256, 313, 232, 311, 265, 270, 269, 300)
# The right eye: the faces that close it, the vertices of its closed slit, and the ring around
# it from the corner beside the nose over the upper lid to the outer corner and back underneath.
EYE_FACES = (217, 218, 219, 220, 221, 222, 223, 224)
EYE_SLIT = (206, 207, 208, 209)
EYE_RING = (203, 181, 182, 198, 199, 205, 200, 201, 204, 202)
# The line between the lips from the centre to the right, its last vertex before the corner, and
# the lower lip's faces at it.
LIP_LINE = (286, 183)
LIP_END = 271
LIP_CORNER = 153
LOWER_LIP_FACES = (199, 216)
# Centre line vertices at the features of the profile.
PROFILE = {"nasion": 274, "nose_tip": 277, "subnasale": 278, "upper_lip": 279, "lower_lip": 276,
           "chin_dip": 275, "chin": 280, "chin_bottom": 285}
TOP = 291
EYEBALL_RADIUS = 0.0120
LID_CLEARANCE = 0.0004
# The neck's top ring lies at these heights above the notch over the breastbone, at 1.79 m.
NECK_TOP = (0.085, 0.118, 0.140)
# In the cage's own measures: the face lies in front of this, and the ears beyond this to the side.
FACE_FRONT = 0.085
EAR_SIDE = 0.072
LIP_SHARES = (0.0, 0.45, 0.90)
EAR_SURROUND = 0.014
# The cage's ear stands out more than a real one: it is laid this much flatter against the head.
EAR_FLATTEN = 0.62
# The lid's inner edge continues this far around the eyeball, in radians.
LID_TUCK = 0.32
# The mouth cavity's rings behind the line between the lips, at 1.79 m: how far back, how wide as a
# share of the mouth's half width, and how far above and below the line. It holds the teeth.
MOUTH_ROWS = ((0.005, 0.80, 0.0025, 0.0025), (0.022, 1.35, 0.014, 0.016), (0.054, 1.30, 0.012, 0.014))
# The teeth and the tongue behind the lips, at 1.79 m.
TEETH_BEHIND_LIPS, TEETH_HALF_WIDTH, TEETH_DEPTH, TEETH_THICKNESS = 0.008, 0.024, 0.040, 0.005
UPPER_TEETH_HEIGHT, LOWER_TEETH_HEIGHT = 0.009, 0.008
TONGUE_BEHIND_LIPS, TONGUE_BELOW_LIPS, TONGUE_LENGTH, TONGUE_HALF_WIDTH, TONGUE_THICKNESS = 0.014, 0.007, 0.042, 0.016, 0.006
# The right ear: the vertices that stand out from the head, the ring of the head's side it stands
# on, and its faces. The ear keeps the cage's plain form and is moved as one stiff piece.
EAR_FACES = (265, 266, 267, 271, 272, 273, 274, 275, 276, 298, 299, 303, 304, 305, 306, 307)
EAR_INNER = (247, 251, 253, 254, 255, 272, 273, 306, 307, 308, 309)
EAR_LOOP = (223, 243, 241, 239, 252, 249, 305, 250, 248, 193, 176, 302)


def _warp(points, anchors, moves, reach):
    """Moves points with the anchors near them: each anchor carries its move to the points within
    its reach, fading with distance, and points far from every anchor stay."""
    out = points.copy()
    anchors, moves = np.asarray(anchors, float), np.asarray(moves, float)
    for i, p in enumerate(points):
        w = np.exp(-((anchors - p) ** 2).sum(1) / (2 * reach * reach))
        out[i] = p + (w[:, None] * moves).sum(0) / max(w.sum(), 1.0)
    return out


class Head:
    def __init__(self, m, cage_points, cage_faces, eye_centres, eye_radius):
        self.m = m
        s = m.scale
        self.scale = s
        self.faces = [tuple(f) for f in cage_faces]
        cage = np.asarray(cage_points, float)
        cage[:, 0] -= (cage[:, 0].min() + cage[:, 0].max()) / 2
        cage[np.abs(cage[:, 0]) < 1e-4, 0] = 0.0
        self.face = Face(m.surface, eye_centres, eye_radius, s)
        face = self.face
        self.eye_centre = face.eye_centre
        rim, _axis = face.eye_rim(96)
        # Ten points of the rim: the corner beside the nose, four over the upper lid, the outer
        # corner, four under the lower lid.
        inner, outer = int(np.argmin(rim[:, 0])), int(np.argmax(rim[:, 0]))
        rim = np.roll(rim, -inner, axis=0)
        outer = (outer - inner) % len(rim)
        steps = np.linalg.norm(np.diff(np.concatenate([rim, rim[:1]]), axis=0), axis=1)
        along = np.concatenate([[0.0], np.cumsum(steps)])

        def at(distance):
            i = int(np.clip(np.searchsorted(along, distance, side="right") - 1, 0, len(rim) - 1))
            t = (distance - along[i]) / max(steps[i], 1e-12)
            return rim[i] + t * (rim[(i + 1) % len(rim)] - rim[i])

        upper, lower = along[outer], along[-1] - along[outer]
        self.rim_stations = [upper * k / 5 for k in range(6)] + [upper + lower * k / 5 for k in range(1, 5)]
        self.rim_length = float(along[-1])

        def on_rim(distance):
            """A point of the rim, kept clear of the eyeball."""
            offset = at(distance % along[-1]) - self.eye_centre
            reach = max(np.linalg.norm(offset), EYEBALL_RADIUS * s + LID_CLEARANCE)
            return self.eye_centre + _unit(offset) * reach

        self.on_rim = on_rim
        self.eye_ring = [on_rim(distance) for distance in self.rim_stations]
        half_mouth = face.mouth_corner()
        self.half_mouth = half_mouth
        # The line between the lips as a smooth curve from the centre to the corner.
        across = np.arange(0.0, half_mouth + 1e-9, 0.001)
        found = [face.lip_line_at(x) for x in across]
        known = [i for i, f in enumerate(found) if f is not None]
        line = np.array([found[i][0] for i in known])
        kernel = np.ones(5) / 5
        self._lip = (across[known], np.convolve(np.pad(line[:, 1], 2, mode="edge"), kernel, mode="valid"),
                     np.convolve(np.pad(line[:, 2], 2, mode="edge"), kernel, mode="valid"))
        lip = {v: self.lip_point(share) for v, share in zip(LIP_LINE + (LIP_END,), LIP_SHARES)}
        corner = self.lip_point(LIP_SHARES[-1]).copy()
        corner[0] = half_mouth + 0.003 * s
        lip[LIP_CORNER] = m.surface.project(corner, reach=0.02)

        # Step 1: scale and move the cage so its features lie near the reference's.
        targets = {PROFILE[name]: getattr(face, name) for name in PROFILE}
        targets[TOP] = face.mid[int(np.argmax(face.mid[:, 2]))] * (0.0, 1.0, 1.0)
        pairs = [(cage[v], q) for v, q in targets.items()]
        pairs.append((cage[list(EYE_RING)].mean(0), np.mean(self.eye_ring, axis=0)))
        ear = int(np.argmax(cage[:, 0]))
        ear_low, ear_high = face.ear_box()
        ear_target = np.array([ear_high[0], (ear_low[1] + ear_high[1]) / 2, (ear_low[2] + ear_high[2]) / 2])
        pairs.append((cage[ear], ear_target))
        mirrored = [(p * (-1, 1, 1), q * (-1, 1, 1)) for p, q in pairs[-2:]]
        source = np.array([p for p, _q in pairs + mirrored])
        goal = np.array([q for _p, q in pairs + mirrored])
        centre_s, centre_g = source.mean(0), goal.mean(0)
        size = float(((source - centre_s) * (goal - centre_g)).sum() / ((source - centre_s) ** 2).sum())
        placed = (cage - centre_s) * size + centre_g
        placed[cage[:, 0] == 0.0, 0] = 0.0
        self.size = size

        # Step 2: warp the loops onto the features.
        anchors = dict(targets)
        for v, q in zip(EYE_RING, self.eye_ring):
            anchors[v] = q
        for v, q in lip.items():
            anchors[v] = q
        # The ear moves as one stiff piece to the reference's ear and is scaled evenly to its height.
        outer = list(EAR_INNER)
        low, high = placed[outer].min(0), placed[outer].max(0)
        grow = float(np.clip((ear_high[2] - ear_low[2]) / (high[2] - low[2]), 0.9, 1.2))
        middle = np.array([high[0], (low[1] + high[1]) / 2, (low[2] + high[2]) / 2])
        for v in EAR_INNER + EAR_LOOP:
            offset = (placed[v] - middle) * grow
            anchors[v] = ear_target + offset
        inner = low[0] - middle[0]
        for v in EAR_INNER:
            # Flatter: nearer to the plane the ear stands on.
            offset = (placed[v] - middle) * grow
            anchors[v] = ear_target + offset * (1.0, 1.0, 1.0) + X * ((1.0 - EAR_FLATTEN) * (inner * grow - offset[0]))
        neck = self.neck_ring()
        for v, q in zip(NECK_RING, neck):
            anchors[v] = q
        index, goal = list(anchors), np.array([anchors[v] for v in anchors])
        # Both sides carry their moves, so the centre line stays on the mirror plane.
        mirror_index = {tuple(np.round(p * (-1, 1, 1), 5)): i for i, p in enumerate(cage)}
        anchor_points, anchor_moves = [], []
        for v, q in zip(index, goal):
            anchor_points.append(placed[v])
            anchor_moves.append(q - placed[v])
            twin = mirror_index.get(tuple(np.round(cage[v], 5)))
            if twin is not None and twin != v:
                anchor_points.append(placed[twin])
                anchor_moves.append((q - placed[v]) * (-1, 1, 1))
        warped = _warp(placed, anchor_points, anchor_moves, 0.022 * s)
        for v, q in anchors.items():
            warped[v] = q
        warped[cage[:, 0] == 0.0, 0] = 0.0
        self.points = warped
        # The neck's ring is placed here but relaxes with the neck.
        # The ring the ear stands on is laid onto the head's side with the rest of the head.
        self.pinned = set(anchors) - {TOP} - set(NECK_RING) - set(EAR_LOOP)
        self.ear_box = (ear_low, ear_high)
        # The head's side right around the ear keeps the warped cage's shape: the reference's ear
        # stands over it, so it cannot be laid onto the reference there.
        reach = EAR_SURROUND * s
        self.near_ear = {i for i in range(len(cage)) if cage[i, 0] > 0
                         and warped[i][0] > ear_low[0] - 0.03 * s
                         and ear_low[1] - reach < warped[i][1] < ear_high[1] + reach
                         and ear_low[2] - reach < warped[i][2] < ear_high[2] + reach}
        # The face and the ears keep the places the cage's maker gave their vertices; the back of
        # the head, its top, and its underside may relax.
        self.kept = {i for i in range(len(cage)) if cage[i, 1] > FACE_FRONT or abs(cage[i, 0]) > EAR_SIDE}
        self.right = [i for i in range(len(cage)) if cage[i, 0] >= 0.0]
        self.centre = {i for i in range(len(cage)) if cage[i, 0] == 0.0}

    def lip_point(self, share):
        """The point of the line between the lips at a share of the mouth's half width."""
        x = share * self.half_mouth
        across, forward, height = self._lip
        return np.array([x, np.interp(x, across, forward), np.interp(x, across, height)])

    def neck_ring(self):
        """The top ring of the neck: under the chin in front, under the ear at the side, and at
        the nape, from the back centre over the right side to the front centre."""
        m, s = self.m, self.scale
        front, side, back = (m.notch_z + h * s for h in NECK_TOP)
        out = []
        for k in range(TRUNK_COLUMNS + 1):
            azimuth = -90.0 + 180.0 * k / TRUNK_COLUMNS
            turn = math.sin(math.radians(azimuth)) ** 2
            z = side + ((front if azimuth > 0 else back) - side) * turn
            out.append(m.column_point(z, azimuth, 0.03 * s))
        return out

    def inner_parts(self):
        """The eyeballs, the teeth, and the tongue as (name, points, faces, uvs)."""
        from . import parts
        s = self.scale
        lips = self.lip_point(0.0)
        out = []
        for side, sign in (("R", 1.0), ("L", -1.0)):
            centre = self.eye_centre * (sign, 1.0, 1.0)
            points, faces, uvs = parts.eyeball(centre, EYEBALL_RADIUS * s)
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
        """The head's skin without the ear that stands out from it: the net's own ear takes the
        ear's place, and the head's side around it must not be laid onto the reference's ear."""
        m = self.m
        low, high = self.ear_box
        margin = 0.006 * m.scale

        def keep(c):
            ear = ((c[:, 0] > low[0] + 0.001) & (c[:, 1] > low[1] - margin) & (c[:, 1] < high[1] + margin)
                   & (c[:, 2] > low[2] - margin) & (c[:, 2] < high[2] + margin))
            return (c[:, 2] > m.notch_z + 0.02 * m.scale) & (c[:, 0] > -0.03) & ~ear

        return {"head": m.surface.part(keep)}


def build_head(net: Net, head: Head):
    s = head.scale
    zone = ZONE["Head"]
    skin = head.skins()["head"]
    drop = set(NECK_INNER) | set(EYE_SLIT)
    vertex = {}
    for v in head.right:
        if v in drop:
            continue
        point = head.points[v]
        # The cage's own vertices keep the places its maker gave them on the features; the vertices
        # that subdividing adds are the ones that relax.
        on_neck = v in NECK_RING
        stiff = v in head.pinned or v in head.near_ear
        holds = ([FIXED] if (stiff or v in head.kept) and not on_neck else []) + ([MIRROR] if v in head.centre else [])
        if not stiff and not on_neck:
            laid = skin.project(point, reach=0.03)
            point = point if laid is None else laid
            if v in head.centre:
                point = np.array([0.0, point[1], point[2]])
        vertex[v] = net.add(point, *holds)

    # The lower lip takes its own vertices along the line between the lips.
    lower = {v: net.add(head.points[v], FIXED, *([MIRROR] if v in head.centre else [])) for v in LIP_LINE}
    skip = set(NECK_FACES) | set(EYE_FACES)
    for index, corners in enumerate(head.faces):
        if index in skip or any(v not in vertex for v in corners):
            continue
        if index in LOWER_LIP_FACES:
            net.face([lower.get(v, vertex[v]) for v in corners], zone, "head")
        else:
            # The ear's faces are not laid onto the reference's ear, whose folds are finer than the net.
            beside_ear = index in EAR_FACES or all(v in head.near_ear for v in corners)
            net.face([vertex[v] for v in corners], zone, None if beside_ear else "head")

    # The neck's top ring is the opening under the head.
    neck_top = [vertex[v] for v in NECK_RING]
    net.strip(net.marks["neck_end"], neck_top, ZONE["Neck"], "trunk", closed=False)
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
        under.append(net.add(centre + tucked * (EYEBALL_RADIUS * s + LID_CLEARANCE), RIGID))
    # Seen from in front the ring runs clockwise for the right eye, so the pocket's faces look
    # into the opening.
    net.strip(ring, under, zone, None)
    back = net.add(centre - forward * (EYEBALL_RADIUS * s + 0.003 * s), RIGID)
    for k in range(len(under)):
        net.face((under[k], under[(k + 1) % len(under)], back), zone, None)
    net.mark("eye_ring", ring)

    # The mouth: the inner lips behind the line between the lips, and the cavity behind them.
    upper = [vertex[286], vertex[183], vertex[LIP_END], lower[183], lower[286]]
    line = np.asarray(net.points[vertex[286]], float)
    half = head.half_mouth
    rows = [upper]
    for depth, width, up, down in MOUTH_ROWS:
        back_y = line[1] - depth * s
        z = line[2]
        row = [
            net.add((0.0, back_y, z + up * s), RIGID, MIRROR),
            net.add((half * width * 0.5, back_y, z + up * s * 0.9), RIGID),
            net.add((half * width, back_y + 0.003 * s, z), RIGID),
            net.add((half * width * 0.5, back_y, z - down * s * 0.9), RIGID),
            net.add((0.0, back_y, z - down * s), RIGID, MIRROR),
        ]
        rows.append(row)
    for near, far in zip(rows[:-1], rows[1:]):
        net.strip(near, far, zone, None, closed=False)
    last = rows[-1]
    net.face((last[0], last[1], last[3], last[4]), zone, None)
    net.face((last[1], last[2], last[3]), zone, None)
    net.mark("lip_upper", [vertex[286], vertex[183], vertex[LIP_END]])
    net.mark("lip_lower", [lower[286], lower[183], vertex[LIP_END]])


def refine_head(fine: Net, coarse: Net, head: Head):
    """Places the vertices that subdividing put on the two feature lines the reference cannot
    give a vertex by itself: the rim of the eye's opening, and the line between the lips, where
    the upper and the lower lip must meet point for point."""
    def between(a, b):
        return fine.edge_point[(a, b) if a < b else (b, a)]

    ring = coarse.marks["eye_ring"]
    stations = head.rim_stations + [head.rim_length]
    for k in range(len(ring)):
        vertex = between(ring[k], ring[(k + 1) % len(ring)])
        fine.points[vertex] = head.on_rim((stations[k] + stations[k + 1]) / 2)
        fine.holds[vertex].add(FIXED)
    for name in ("lip_upper", "lip_lower"):
        line = coarse.marks[name]
        for k in range(len(line) - 1):
            vertex = between(line[k], line[k + 1])
            fine.points[vertex] = head.lip_point((LIP_SHARES[k] + LIP_SHARES[k + 1]) / 2)
            fine.holds[vertex].add(FIXED)
