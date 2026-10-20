"""The right foot as a coarse quad cage fitted to a reference foot, built like the hand: the
toes are traced on the reference and every cage vertex carries a ray origin inside the foot.

Coarse topology: the ankle ring of 8 (the leg's last ring) is an opening on top of the foot
between two slices, the heel slice R1 and the instep slice R2. R1 is closed behind by a heel
cap of three quads. Slices R3 (mid foot) and R4 (ball) run forward to the toe base ring of 12,
where the five 4-sided toes share their side edges at the webs; two 1-to-3 blocks (top and sole)
widen R4 to the toe ring. Each toe has two segments and an end cap.

Slice layout (8 vertices): 0 lateral-top, 1 top-lateral, 2 top-medial, 3 medial-top,
4 medial-bottom, 5 sole-medial, 6 sole-lateral, 7 lateral-bottom."""

import math

import numpy as np
from mathutils import Vector

from . import digits, geo


ANKLE_Z = 0.095
# Ankle ring directions (degrees from forward toward lateral) of R1[0..3] and R2[0..3].
HEEL_ANGLES = (112.0, 157.0, -157.0, -112.0)
INSTEP_ANGLES = (62.0, 20.0, -20.0, -62.0)
SLICE = ((0.9, 0.55), (0.35, 1.0), (-0.35, 1.0), (-0.9, 0.55), (-0.9, -0.6), (-0.35, -1.0), (0.35, -1.0),
         (0.9, -0.6))
# Distances along the foot from the ankle centre: heel bottom, instep bottom, mid foot, ball.
HEEL_ALONG = -0.046
INSTEP_ALONG = 0.03
MID_ALONG = 0.082
BALL_ALONG = 0.125
TOE_RINGS = (0.12, 0.5)
BASE_BACK = 0.004
COARSE = 1.3


def _ortho(v, axis):
    return (v - axis * v.dot(axis)).normalized()


def frame(surface):
    """Ankle centre and the foot directions (forward along the sole, lateral, up)."""
    sole = surface.co[(surface.co[:, 0] > 0.0) & (surface.co[:, 2] < 0.015)]
    c = sole.mean(0)
    _u, _s, vt = np.linalg.svd(sole[:, :2] - c[:2], full_matrices=False)
    ax = vt[0] if vt[0][1] > 0 else -vt[0]
    fwd = Vector((ax[0], ax[1], 0.0)).normalized()
    guess = Vector((c[0], c[1], ANKLE_Z)) - fwd * 0.10
    sec = geo.section(surface, guess, (0, 0, 1), iterations=4, max_dist=0.2)
    ankle = Vector(sec[0])
    up = Vector((0.0, 0.0, 1.0))
    return ankle, sec[1], fwd, fwd.cross(up).normalized(), up


def _slice(surface, ankle, fwd, lat, along):
    """Centre (on the foot's mid plane), half-width and the heights above and below it of the
    foot's cross-section at the given distance along the foot."""
    p = Vector((ankle.x, ankle.y, 0.03)) + fwd * along
    sec = geo.section(surface, p, fwd, rays=48, iterations=3, max_dist=0.15)
    c = Vector(sec[0]) if sec else p

    def reach(d):
        hit = surface.first_hit(c, d, 0.2)
        return hit[3] if hit else 0.02

    up = Vector((0, 0, 1))
    c = c + lat * (reach(lat) - reach(-lat)) / 2
    return c, (reach(lat) + reach(-lat)) / 2, reach(up), reach(-up)


def build(cage, surface, ankle_ring=None):
    """Adds the right foot to a cage.Cage. ankle_ring: the leg's last ring of 8 cage
    vertices in the order R1[0..3], R2[3..0] (made here when None). Returns the ankle ring,
    the foot frame and the toe tips."""
    ankle, ankle_r, fwd, lat, up = frame(surface)
    tag, zone = cage.tag, cage.zone
    cage.tag, cage.zone = "foot", "feet"

    def ring_dir(deg):
        a = math.radians(deg)
        return fwd * math.cos(a) + lat * math.sin(a)

    ank = Vector((ankle.x, ankle.y, ANKLE_Z))
    if ankle_ring is None:
        r = ankle_r * 1.15
        top_back = [cage.add(ank + ring_dir(a) * r, ank) for a in HEEL_ANGLES]
        top_front = [cage.add(ank + ring_dir(a) * r, ank) for a in INSTEP_ANGLES]
        ankle_ring = top_back + list(reversed(top_front))
    top_back = ankle_ring[:4]
    top_front = list(reversed(ankle_ring[4:]))

    def bottom(along, origin):
        c, w, _h_up, h_dn = _slice(surface, ankle, fwd, lat, along)
        return [cage.add(c + lat * (u * w * COARSE) + up * (v * h_dn * COARSE), origin) for u, v in SLICE[4:]]

    heel_origin = Vector((ankle.x, ankle.y, 0.04)) + fwd * (HEEL_ALONG + 0.035)
    instep_origin = Vector((ankle.x, ankle.y, 0.035)) + fwd * INSTEP_ALONG
    R1 = top_back + bottom(HEEL_ALONG, heel_origin)
    R2 = top_front + bottom(INSTEP_ALONG, instep_origin)
    for v in top_back:
        cage.origin[v] = (cage.origin[v] + heel_origin) / 2
    # Heel cap behind R1 and the strip R1 -> R2 below the ankle opening.
    cage.quad(R1[0], R1[1], R1[6], R1[7])
    cage.quad(R1[1], R1[2], R1[5], R1[6])
    cage.quad(R1[2], R1[3], R1[4], R1[5])
    for k in range(3, 8):
        cage.quad(R1[k], R1[(k + 1) % 8], R2[(k + 1) % 8], R2[k])

    def full_slice(along):
        c, w, h_up, h_dn = _slice(surface, ankle, fwd, lat, along)
        return [cage.add(c + lat * (u * w * COARSE) + up * (v * (h_up if v > 0 else h_dn) * COARSE), c) for u, v in SLICE]

    R3 = full_slice(MID_ALONG)
    R4 = full_slice(BALL_ALONG)
    for a, b in ((R2, R3), (R3, R4)):
        for k in range(8):
            cage.quad(a[k], a[(k + 1) % 8], b[(k + 1) % 8], b[k])

    # Toes, big toe (medial) first.
    traced = digits.trace(surface, Vector((ankle.x, ankle.y, 0.03)) + fwd * 0.10, fwd, lat, 5, reach=0.2)
    toes = sorted((digits.Digit(t) for t in traced), key=lambda d: d.base.dot(lat))
    corners, centres = [], []
    for d in toes:
        c, r, t = d.at(d.length + BASE_BACK)
        side = _ortho(lat, t)
        dor = t.cross(side).normalized()
        if dor.dot(up) < 0:
            dor = -dor
        ca = r * COARSE
        # medial-bottom, medial-top, lateral-top, lateral-bottom
        corners.append([c + (-side - dor) * ca, c + (-side + dor) * ca, c + (side + dor) * ca, c + (side - dor) * ca])
        centres.append(c)
    top, bot = [None] * 6, [None] * 6
    top[0] = cage.add(corners[0][1], centres[0])
    bot[0] = cage.add(corners[0][0], centres[0])
    for k in range(4):
        web = (centres[k] + centres[k + 1]) / 2 - fwd * 0.006
        top[k + 1] = cage.add((corners[k][2] + corners[k + 1][1]) / 2, web)
        bot[k + 1] = cage.add((corners[k][3] + corners[k + 1][0]) / 2, web)
    top[5] = cage.add(corners[4][2], centres[4])
    bot[5] = cage.add(corners[4][3], centres[4])
    P, O = cage.pos, cage.origin

    def block(t, b):
        m1 = cage.add((P[t[1]] * 2 + P[b[0]]) / 3, (O[t[1]] + O[b[0]]) / 2)
        m2 = cage.add((P[t[2]] * 2 + P[b[1]]) / 3, (O[t[2]] + O[b[1]]) / 2)
        cage.quad(t[0], t[1], m1, b[0])
        cage.quad(t[1], t[2], m2, m1)
        cage.quad(t[2], t[3], b[1], m2)
        cage.quad(b[0], m1, m2, b[1])

    cage.quad(R4[3], R4[2], top[1], top[0])
    block([top[1], top[2], top[3], top[4]], [R4[2], R4[1]])
    cage.quad(R4[1], R4[0], top[5], top[4])
    cage.quad(R4[0], R4[7], bot[5], top[5])
    cage.quad(R4[7], R4[6], bot[4], bot[5])
    block([bot[4], bot[3], bot[2], bot[1]], [R4[6], R4[5]])
    cage.quad(R4[5], R4[4], bot[0], bot[1])
    cage.quad(R4[4], R4[3], top[0], bot[0])

    tips, caps, cap_faces, digit_rings = [], [], [], []
    for k, d in enumerate(toes):
        base = [bot[k], top[k], top[k + 1], bot[k + 1]]
        base_c = centres[k]
        offsets = [cage.pos[v] - base_c for v in base]
        _c, _r, t0 = d.at(d.length)
        normal = Vector()
        for i in range(4):
            normal += offsets[i].cross(offsets[(i + 1) % 4])
        normal.normalize()
        if normal.dot(t0) < 0:
            normal = -normal
        prev = base
        rings = [base]
        for i, frac in enumerate(sorted(TOE_RINGS, reverse=True)):
            c, r, t = d.at(frac * d.length)
            turn = normal.rotation_difference(t)
            dirs = [_ortho(turn @ off, t) for off in offsets]
            o = c - t * (r * 1.2) if i == len(TOE_RINGS) - 1 else c
            ring = [cage.add(c + u * (r * COARSE), o) for u in dirs]
            for j in range(4):
                cage.quad(prev[j], prev[(j + 1) % 4], ring[(j + 1) % 4], ring[j])
            prev = ring
            rings.append(ring)
        cap_faces.append(len(cage.faces))
        cage.quad(prev[3], prev[2], prev[1], prev[0])
        tips.append(d.tip)
        caps.append(prev)
        digit_rings.append(rings)
    cage.tag, cage.zone = tag, zone
    return {"ankle": ankle_ring, "frame": (ankle, fwd, lat, up), "tips": tips, "caps": caps, "cap_faces": cap_faces,
            "digit_rings": digit_rings, "heel": (R1[1], R1[2], R1[5], R1[6]), "slices": (R1, R2, R3, R4),
            "toe_ring": (top, bot)}
