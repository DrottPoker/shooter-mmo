"""The right hand as a coarse quad cage fitted to a reference hand: the digits are traced on
the reference (digits), and the cage is laid around their centre lines and the palm sections.
Every cage vertex carries a ray origin inside the hand (the digit centre line or the palm
centre), so after one Catmull-Clark step each vertex can be cast onto the reference.

Coarse topology: wrist ring 8 (the arm's last ring), two palm rings of 8 with the thumb hole
on the radial side between them, the knuckle ring of 10 where the four fingers share their side
edges at the webs, a 1-to-3 block that feeds the little finger from the ulnar side of the palm,
and 4-sided digits with three segments and an end cap. After subdivision the fingers are
8-sided with rings at every joint and between them."""

from mathutils import Vector

from . import digits, geo


# Finger rings as fractions of the traced digit length from the tip: end cap ring, distal joint,
# proximal joint. The base ring sits just proximal of the traced base (the web).
FINGER_RINGS = (0.07, 0.34, 0.63)
THUMB_RINGS = (0.06, 0.22, 0.47)  # end ring, middle of the end joint, joint
BASE_BACK = 0.006
# Palm rings (distance along the palm axis from the wrist) and the knuckle ring offset.
# The two palm rings around the thumb hole sit this far before and after the thumb's base
# (along the palm axis), clamped between the wrist and the knuckles.
THUMB_HOLE = (-0.026, 0.012)
# Palm ring layout in units of half-width (radial +) and half-thickness (dorsal +):
# 0 ulnar-palmar, 1 ulnar-dorsal, 2 and 3 dorsal, 4 radial-dorsal, 5 radial-palmar, 6 and 7 palmar.
PALM_RING = ((-0.95, -0.55), (-0.95, 0.55), (-0.35, 1.0), (0.35, 1.0), (0.95, 0.55), (0.95, -0.55),
             (0.35, -1.0), (-0.35, -1.0))
COARSE = 1.35  # coarse ring radius over the digit radius (subdivision pulls rings inward)


def _ortho(v, axis):
    return (v - axis * v.dot(axis)).normalized()


def _palm_section(surface, centre, e_len, e_rad, e_dor):
    """Refined centre, half-width and half-thickness of the palm section at centre."""
    sec = geo.section(surface, centre, e_len, rays=48, iterations=3, max_dist=0.12)
    c = Vector(sec[0]) if sec else Vector(centre)

    def reach(d):
        hit = surface.first_hit(c, d, 0.12)
        return hit[3] if hit else 0.02

    # Centre between the two sides along each axis, then the half extents.
    for axis in (e_rad, e_dor):
        a, b = reach(axis), reach(-axis)
        c = c + axis * (a - b) / 2
    return c, (reach(e_rad) + reach(-e_rad)) / 2, (reach(e_dor) + reach(-e_dor)) / 2


def trace(surface, wrist, direction):
    """Traces the reference hand beyond the wrist plane. Returns the four fingers (little to
    index), the thumb, and the hand frame (palm axis, radial and dorsal directions)."""
    traced = digits.trace(surface, wrist, direction, Vector((0, 1, 0)), 5)
    found = [digits.Digit(t) for t in traced]
    thumb = min(found, key=lambda d: (d.tip - wrist).dot(direction))
    fingers = [d for d in found if d is not thumb]
    knuckles = sum((d.base for d in fingers), Vector()) / 4
    e_len = (knuckles - wrist).normalized()
    side = _ortho(thumb.base - wrist, e_len)
    fingers.sort(key=lambda d: d.base.dot(side))
    e_rad = _ortho(fingers[-1].base - fingers[0].base, e_len)
    e_dor = e_len.cross(e_rad).normalized()
    if e_dor.x < 0:  # the back of the right hand faces outward in the A-pose
        e_dor = -e_dor
    return fingers, thumb, (e_len, e_rad, e_dor)


def build(cage, surface, wrist, direction, wrist_ring=None):
    """Adds the right hand to a cage.Cage. wrist_ring: the arm's last ring of 8 cage
    vertices ordered like PALM_RING (made here when None). Returns the wrist ring, the digit
    tip directions and the indices of the cap rings (for the nails and landmarks)."""
    fingers, thumb, (e_len, e_rad, e_dor) = trace(surface, wrist, direction)
    knuckle_along = sum(((d.base - wrist).dot(e_len) for d in fingers)) / 4
    tag, zone = cage.tag, cage.zone
    cage.tag, cage.zone = "hand", "hands"

    def ring8(centre, a, b):
        return [cage.add(centre + e_rad * (u * a) + e_dor * (v * b), centre) for u, v in PALM_RING]

    if wrist_ring is None:
        c, a, b = _palm_section(surface, wrist, e_len, e_rad, e_dor)
        wrist_ring = ring8(c, a * COARSE, b * COARSE)
    rows = [wrist_ring]
    mcp_along = (thumb.at(thumb.length)[0] - wrist).dot(e_len)
    hole_rows = (max(mcp_along + THUMB_HOLE[0], 0.012), min(mcp_along + THUMB_HOLE[1], knuckle_along - 0.03))
    for along in hole_rows:
        c, a, b = _palm_section(surface, wrist + e_len * along, e_len, e_rad, e_dor)
        rows.append(ring8(c, a * COARSE, b * COARSE))
    W, A, B = rows
    for k in range(8):
        cage.quad(W[k], W[(k + 1) % 8], A[(k + 1) % 8], A[k])
    for k in range(8):
        if k != 4:  # thumb hole between A4, A5, B5, B4
            cage.quad(A[k], A[(k + 1) % 8], B[(k + 1) % 8], B[k])

    # Finger base rings: [ulnar-palmar, ulnar-dorsal, radial-dorsal, radial-palmar] of each
    # finger; neighbouring fingers share their side edge at the web.
    def frame(t):
        side = _ortho(e_rad, t)
        return side, t.cross(side).normalized() * (1 if t.cross(side).dot(e_dor) > 0 else -1)

    corners = []
    centres = []
    for d in fingers:
        c, r, t = d.at(d.length + BASE_BACK)
        side, dor = frame(t)
        ca = r * COARSE
        corners.append([c + (-side - dor) * ca, c + (-side + dor) * ca, c + (side + dor) * ca, c + (side - dor) * ca])
        centres.append(c)
    K = [None] * 10
    K[0] = cage.add(corners[0][0], centres[0])
    K[1] = cage.add(corners[0][1], centres[0])
    for f in range(3):
        web_c = (centres[f] + centres[f + 1]) / 2 - e_len * 0.006
        K[2 + f] = cage.add((corners[f][2] + corners[f + 1][1]) / 2, web_c)
        K[9 - f] = cage.add((corners[f][3] + corners[f + 1][0]) / 2, web_c)
    K[5] = cage.add(corners[3][2], centres[3])
    K[6] = cage.add(corners[3][3], centres[3])
    base_rings = [[K[0], K[1], K[2], K[9]], [K[9], K[2], K[3], K[8]], [K[8], K[3], K[4], K[7]], [K[7], K[4], K[5], K[6]]]
    # Palm to knuckles: straight quads over the ring, middle and index fingers, and a 1-to-3
    # block from the ulnar edge B0-B1 to the little finger's palmar, ulnar and dorsal edges.
    cage.quad(B[1], B[2], K[3], K[2])
    cage.quad(B[2], B[3], K[4], K[3])
    cage.quad(B[3], B[4], K[5], K[4])
    cage.quad(B[4], B[5], K[6], K[5])
    cage.quad(B[5], B[6], K[7], K[6])
    cage.quad(B[6], B[7], K[8], K[7])
    cage.quad(B[7], B[0], K[9], K[8])
    P = cage.pos
    O = cage.origin
    m1 = cage.add((P[K[0]] * 2 + P[B[0]]) / 3, (O[K[0]] + O[B[0]]) / 2)
    m2 = cage.add((P[K[1]] * 2 + P[B[1]]) / 3, (O[K[1]] + O[B[1]]) / 2)
    cage.quad(K[9], K[0], m1, B[0])
    cage.quad(K[0], K[1], m2, m1)
    cage.quad(K[1], K[2], B[1], m2)
    cage.quad(B[0], m1, m2, B[1])

    tips = []
    caps = []
    cap_faces = []
    digit_rings = []

    def digit_tube(d, base, fractions, base_centre):
        """Rings along a digit from its base ring to the cap. Each ring's vertices keep the
        angular places of the base ring's vertices around the centre line."""
        offsets = [cage.pos[v] - base_centre for v in base]
        normal = Vector()
        for k in range(4):
            normal += offsets[k].cross(offsets[(k + 1) % 4])
        normal.normalize()
        _c, _r, t0 = d.at(d.length)
        if normal.dot(t0) < 0:
            normal = -normal
        prev = base
        rings = [base]
        dirs = [_ortho(off, normal) for off in offsets]
        axis = normal
        for i, frac in enumerate(sorted(fractions, reverse=True)):
            c, r, t = d.at(frac * d.length)
            # The previous ring's directions carried along the bending centre line (parallel
            # transport, no twist); the first ring picks the turn with the shortest edges to the
            # base ring, as a strongly bent thumb would otherwise start with a quarter twist.
            turn = axis.rotation_difference(t)
            dirs = [_ortho(turn @ u, t) for u in dirs]
            axis = t
            if i == 0:
                shift = min(range(4), key=lambda s: sum(
                    (cage.pos[base[k]] - (c + dirs[(k + s) % 4] * (r * COARSE))).length for k in range(4)))
                dirs = [dirs[(k + shift) % 4] for k in range(4)]
            # The cap ring's origin sits further back, so the cap centre is cast to the tip.
            o = c - t * (r * 1.2) if i == len(fractions) - 1 else c
            ring = [cage.add(c + u * (r * COARSE), o) for u in dirs]
            for k in range(4):
                cage.quad(prev[k], prev[(k + 1) % 4], ring[(k + 1) % 4], ring[k])
            prev = ring
            rings.append(ring)
        cap_faces.append(len(cage.faces))
        cage.quad(prev[3], prev[2], prev[1], prev[0])
        tips.append(d.tip)
        caps.append(prev)
        digit_rings.append(rings)

    for d, base, c in zip(fingers, base_rings, centres):
        digit_tube(d, base, FINGER_RINGS, c)
    # Thumb: from the hole on the radial side of the palm through the base, middle and end rings.
    hole = [A[4], B[4], B[5], A[5]]
    hole_c = sum((cage.pos[v] for v in hole), Vector()) / 4
    mcp, _r, _t = thumb.at(thumb.length)
    for v in hole:
        cage.origin[v] = (cage.origin[v] + mcp) / 2
    digit_tube(thumb, hole, (0.9,) + THUMB_RINGS, hole_c)
    cage.tag, cage.zone = tag, zone
    return {
        "wrist": wrist_ring, "tips": tips, "caps": caps, "frame": (e_len, e_rad, e_dor),
        "knuckle_along": knuckle_along, "cap_faces": cap_faces, "digit_rings": digit_rings,
        "palm_rows": (W, A, B), "knuckles": K, "block": (m1, m2),
    }
