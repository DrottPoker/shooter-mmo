"""The body cage: torso, arms, legs and the base of the neck as a designed coarse quad cage,
joined at the coarse level with the fitted hand (hands) and foot (feet) cages, subdivided once
(Catmull-Clark) and cast onto one reference body.

Every vertex carries a ray origin, and origins are chosen on an inner skeleton of the body
(torso axis, shoulder joint, arm and leg axes, digit centre lines) so that the origins of
subdivided vertices, which are blends of their neighbours' origins, stay inside the body. This
keeps the armpit and crotch from webbing across the gap between limbs and torso.

Coarse ring sizes (full body): torso and neck base 16, arm opening and arm 8, leg opening 10
reducing to 8 on the thigh. After subdivision: torso 32, neck base 32 (the head's neck ring),
arm and wrist 16, thigh 20 then 16."""

import math

import numpy as np
from mathutils import Vector
from mathutils.kdtree import KDTree

from . import cage, feet, geo, hands


HALF_ANGLES = tuple(22.5 * k for k in range(9))  # front centre (0) over the side (90) to the back centre (180)
# Per body type. torso_z: hip, belly, waist, lower chest (under the bust), armpit. l45_z: the
# ring between the armpit and the shoulder top. shoulder: rays (origin, direction) of the
# shoulder-top ring from the front centre over the shoulder to the back centre; None as origin
# means the shoulder joint. arm_steps: arm rings at these steps of the arm track (2 cm apart),
# the last one is the wrist. leg_z: thigh rings down to the lower calf.
LANDMARKS = {
    "male": {
        "torso_z": (0.86, 0.94, 1.03, 1.10, 1.155, 1.205),
        "l45_z": 1.27,
        "shoulder_joint": (0.192, -0.012, 1.285),
        "shoulder": (((0.0, 0.0, 1.335), (0.0, 1.0, 0.05)), ((0.04, 0.0, 1.34), (0.15, 1.0, 0.25)),
                     ((0.09, 0.0, 1.335), (0.25, 1.0, 0.3)), (None, (0.3, 1.0, 0.25)), (None, (0.25, 0.0, 1.0)),
                     (None, (0.3, -1.0, 0.25)), ((0.09, -0.01, 1.335), (0.2, -1.0, 0.3)),
                     ((0.04, -0.01, 1.34), (0.1, -1.0, 0.25)), ((0.0, -0.01, 1.335), (0.0, -1.0, 0.1))),
        "neck": (0.0, -0.005, 1.395),
        "neck_top_z": 1.43,
        "arm_start": (0.227, -0.015, 1.208),
        "arm_steps": (3, 6, 8, 11, 15, 19),
        "deltoid_r": 0.055,
        "crotch": ((0.0, 0.01, 0.74), (0.0, 0.01, 0.86)),
        "thigh": (0.105, 0.022, 0.70),
        "leg_z": (0.70, 0.60, 0.52, 0.465, 0.41, 0.32, 0.20),
    },
    "female": {
        "torso_z": (0.865, 0.945, 1.00, 1.04, 1.10, 1.17),
        "l45_z": 1.225,
        "shoulder_joint": (0.176, -0.018, 1.25),
        "shoulder": (((0.0, 0.0, 1.30), (0.0, 1.0, 0.05)), ((0.037, 0.0, 1.305), (0.15, 1.0, 0.25)),
                     ((0.083, -0.003, 1.30), (0.25, 1.0, 0.3)), (None, (0.3, 1.0, 0.25)), (None, (0.25, 0.0, 1.0)),
                     (None, (0.3, -1.0, 0.25)), ((0.083, -0.017, 1.30), (0.2, -1.0, 0.3)),
                     ((0.037, -0.015, 1.305), (0.1, -1.0, 0.25)), ((0.0, -0.015, 1.30), (0.0, -1.0, 0.1))),
        "neck": (0.0, 0.0, 1.36),
        "neck_top_z": 1.395,
        "arm_start": (0.198, -0.023, 1.169),
        "arm_steps": (2, 4, 6, 9, 13, 17),
        "deltoid_r": 0.045,
        "crotch": ((0.0, 0.0, 0.75), (0.0, 0.0, 0.86)),
        "thigh": (0.10, 0.01, 0.70),
        "leg_z": (0.70, 0.60, 0.52, 0.465, 0.41, 0.32, 0.20),
    },
}
NECK_TILT = (-0.4, -0.35, -0.22, -0.08, 0.0, 0.08, 0.16, 0.22, 0.25)
NECK_TOP_TILT = (-0.15, -0.12, -0.08, -0.03, 0.0, 0.05, 0.1, 0.14, 0.16)
COARSE = 1.3


def _ortho(v, axis):
    return (v - axis * v.dot(axis)).normalized()


def _ring_dirs(cg, ring, centre, axis):
    return [_ortho(cg.pos[v] - centre, axis) for v in ring]


def _turn(dirs, axis_from, axis_to, twist=0.0):
    """Directions turned from one ring axis onto another, then twisted about the new axis."""
    q = axis_from.rotation_difference(axis_to)
    out = []
    for d in dirs:
        u = _ortho(q @ d, axis_to)
        if twist:
            u = (u * math.cos(twist) + axis_to.cross(u) * math.sin(twist)).normalized()
        out.append(u)
    return out


def _match_twist(dirs, target_dirs, axis):
    """Cyclic offset and mean signed angle (about axis) from dirs to target_dirs, for rings of
    equal size and the same winding."""
    n = len(dirs)
    best = None
    for off in range(n):
        ang = []
        for i in range(n):
            a, b = dirs[i], target_dirs[(i + off) % n]
            ang.append(math.atan2(axis.dot(a.cross(b)), a.dot(b)))
        cost = sum(abs(x) for x in ang)
        if best is None or cost < best[0]:
            best = (cost, off, sum(ang) / n)
    return best[1], best[2]


def _winding(dirs, axis):
    s = 0.0
    for i in range(len(dirs)):
        s += axis.dot(dirs[i].cross(dirs[(i + 1) % len(dirs)]))
    return 1 if s > 0 else -1


def _tube(cg, surface, top, top_centre, top_axis, centres, end, end_centre, end_axis, plan=None, key=None,
          zones=None):
    """Rings along a limb from the opening ring top through the given centres to the end ring
    (an existing ring, e.g. the wrist of the hand cage). Each ring direction turns gradually
    from its place on the opening to the matched place on the end ring, so the rings meet the
    end ring without a jump. Returns the rings, the last one in the order of top."""
    top_dirs = _ring_dirs(cg, top, top_centre, top_axis)
    end_dirs = _ring_dirs(cg, end, end_centre, end_axis)
    carried = _turn(top_dirs, top_axis, end_axis)
    # How the end ring is matched to the top ring is a topology decision: it is made once (on
    # the first body built) and reused, so all body types share one topology.
    if plan is not None and key in plan:
        flip, off = plan[key]
    else:
        flip = _winding(end_dirs, end_axis) != _winding(carried, end_axis)
        off = _match_twist(carried, list(reversed(end_dirs)) if flip else end_dirs, end_axis)[0]
        if plan is not None:
            plan[key] = (flip, off)
    if flip:
        end = list(reversed(end))
        end_dirs = list(reversed(end_dirs))
    n = len(top)
    end = [end[(i + off) % n] for i in range(n)]
    end_dirs = [end_dirs[(i + off) % n] for i in range(n)]
    deltas = [math.atan2(end_axis.dot(a.cross(b)), a.dot(b)) for a, b in zip(carried, end_dirs)]
    rings = [top]
    pts = [top_centre] + list(centres) + [end_centre]
    radii = [None] * len(pts)
    for j, c in enumerate(centres, start=1):
        if isinstance(c, tuple):
            pts[j], radii[j] = c
    for j in range(1, len(pts) - 1):
        c = pts[j]
        axis = (pts[j + 1] - pts[j - 1]).normalized()
        if radii[j] is None:
            # A ring with a given radius keeps its centre (its plane would cut the torso).
            sec = geo.section(surface, c, axis, rays=32, iterations=2, max_dist=0.2)
            r = sec[1] if sec else 0.04
            c = Vector(sec[0]) if sec else c
        else:
            r = radii[j]
        t = j / (len(centres) + 1)
        dirs = []
        for d, delta in zip(_turn(top_dirs, top_axis, axis), deltas):
            a = delta * t
            dirs.append((d * math.cos(a) + axis.cross(d) * math.sin(a)).normalized())
        rings.append([cg.add(c + d * (r * COARSE), c) for d in dirs])
    rings.append(end)
    for j, (a, b) in enumerate(zip(rings, rings[1:])):
        if zones:
            cg.zone = zones[j]
        for k in range(n):
            cg.quad(a[k], a[(k + 1) % n], b[(k + 1) % n], b[k])
    return rings


def build_half(surface, lm, plan=None):
    """The right half of the body cage with the right hand and foot. Returns the cage, named
    rings and the hand and foot info."""
    cg = cage.Cage()
    rings = {}
    S = Vector(lm["shoulder_joint"])

    def centre_at(z):
        c = cage.centre_at(surface, Vector((0.0, 0.0, z)), z)
        return Vector((0.0, c.y, z))

    # Torso rings L0 (hip) .. L4 (armpit). The armpit corners L4[3..5] are cast from the
    # shoulder joint into the hollow of the armpit.
    torso = []
    for z in lm["torso_z"]:
        c = centre_at(z)
        torso.append([cg.add(c + cage.dir_xy(a) * 0.16, c, centre=(k in (0, 8))) for k, a in enumerate(HALF_ANGLES)])
    L4 = torso[-1]
    for k in (3, 4, 5):
        cg.origin[L4[k]] = S.copy()
        p = cg.pos[L4[k]]
        cg.pos[L4[k]] = Vector((p.x * 0.75, p.y * 0.8 + S.y * 0.2, p.z))
    for r in range(len(torso) - 1):
        lo, hi = torso[r], torso[r + 1]
        cg.zone = "pelvis" if r == 0 else "torso"
        for k in range(8):
            cg.quad(lo[k], lo[k + 1], hi[k + 1], hi[k])
    cg.zone = "torso"
    # L45 between the armpit and the shoulder top, without its vertex inside the arm opening.
    c45 = centre_at(lm["l45_z"])
    L45 = {}
    for k, a in enumerate(HALF_ANGLES):
        if k == 4:
            continue
        o = S.copy() if k in (3, 5) else c45
        pos = c45 + cage.dir_xy(a) * 0.16
        if k in (3, 5):
            pos = Vector((S.x * 0.85, S.y + (0.06 if k == 3 else -0.06), lm["l45_z"] - 0.01))
        L45[k] = cg.add(pos, o, centre=(k in (0, 8)))
    # L5 over the shoulder tops.
    L5 = []
    for k, (o, d) in enumerate(lm["shoulder"]):
        o = S.copy() if o is None else Vector(o)
        L5.append(cg.add(o + Vector(d).normalized() * 0.12, o, centre=(k in (0, 8))))
    for k in range(8):
        if k in (3, 4):
            continue
        cg.quad(L4[k], L4[k + 1], L45[k + 1], L45[k])
        cg.quad(L45[k], L45[k + 1], L5[k + 1], L5[k])
    # Neck base ring.
    neck_c = Vector(lm["neck"])
    N = []
    for k, (a, tilt) in enumerate(zip(HALF_ANGLES, NECK_TILT)):
        d = cage.dir_xy(a) + Vector((0, 0, tilt))
        N.append(cg.add(neck_c + d.normalized() * 0.07, neck_c, centre=(k in (0, 8))))
    for k in range(8):
        cg.quad(L5[k], L5[k + 1], N[k + 1], N[k])
    # Upper neck ring, cast level from the neck axis; the head's ring joins it.
    z2 = lm["neck_top_z"]
    c2 = cage.centre_at(surface, Vector((0.0, neck_c.y, z2)), z2)
    c2 = Vector((0.0, c2.y, z2))
    N2 = []
    for k, (a, tilt) in enumerate(zip(HALF_ANGLES, NECK_TOP_TILT)):
        d = cage.dir_xy(a) + Vector((0, 0, tilt))
        N2.append(cg.add(c2 + d.normalized() * 0.065, c2, centre=(k in (0, 8))))
    cg.zone = "neck"
    for k in range(8):
        cg.quad(N[k], N[k + 1], N2[k + 1], N2[k])
    rings["neck_half"] = N2

    # Arm: the opening, rings along the arm track and the hand's wrist ring.
    cg.tag = "arm"
    opening = [L4[3], L4[4], L4[5], L45[5], L5[5], L5[4], L5[3], L45[3]]
    track = cage.track_arm(surface, Vector(lm["arm_start"]), steps=max(lm["arm_steps"]) + 3)
    steps = lm["arm_steps"]
    wrist = track[steps[-1]]
    w_dir = (track[steps[-1]] - track[steps[-1] - 3]).normalized()
    hand = hands.build(cg, surface, wrist, w_dir)
    wring = hand["wrist"]
    o_centre = sum((cg.pos[v] for v in opening), Vector()) / 8
    a1 = track[steps[0]]
    o_axis = (a1 - S).normalized()
    w_centre = sum((cg.pos[v] for v in wring), Vector()) / 8
    deltoid = (S.lerp(a1, 0.4), lm["deltoid_r"])
    # Upper arms down to the elbow ring (the third track ring), forearms from there.
    arm_centres = [deltoid] + [track[s] for s in steps[:-1]]
    arm_zones = ["upper_arms"] * 4 + ["forearms"] * (len(arm_centres) + 1 - 4)
    arm_rings = _tube(cg, surface, opening, o_centre, o_axis, arm_centres, wring, w_centre, hand["frame"][0], plan,
                      "arm", arm_zones)
    rings["arm"] = arm_rings
    rings["opening"] = opening

    # Leg: the opening around the hip and crotch, the thigh rings and the foot's ankle ring.
    cg.tag = "leg"
    L0 = torso[0]
    crotch = cg.add(Vector(lm["crotch"][0]), Vector(lm["crotch"][1]), centre=True)
    gop = list(L0) + [crotch]
    thigh = Vector(lm["thigh"])
    zs = lm["leg_z"]
    c1 = cage.centre_at(surface, thigh, zs[0])
    up = Vector((0, 0, -1))  # leg axis points down
    g1_dirs = [_ortho(cg.pos[v] - c1, up) for v in gop]
    sec = geo.section(surface, c1, (0, 0, 1), rays=32, iterations=3, max_dist=0.2)
    r1 = sec[1] if sec else 0.08
    G1 = [cg.add(c1 + d * r1 * COARSE, c1) for d in g1_dirs]
    cg.zone = "pelvis"
    for k in range(10):
        cg.quad(gop[k], gop[(k + 1) % 10], G1[(k + 1) % 10], G1[k])
    # 10 to 8 on the inner thigh: G1 edges 7-8, 8-9, 9-0 become G2 edge 7-0.
    c2 = cage.centre_at(surface, c1, zs[1])
    sec = geo.section(surface, c2, (0, 0, 1), rays=32, iterations=3, max_dist=0.2)
    r2 = sec[1] if sec else 0.07
    g2_dirs = [g1_dirs[k] for k in range(8)]
    G2 = [cg.add(c2 + d * r2 * COARSE, c2) for d in g2_dirs]
    cg.zone = "thighs"
    for k in range(7):
        cg.quad(G1[k], G1[k + 1], G2[k + 1], G2[k])
    P, O = cg.pos, cg.origin
    m1 = cg.add((P[G1[8]] * 2 + P[G2[7]]) / 3, (O[G1[8]] + O[G2[7]]) / 2)
    m2 = cg.add((P[G1[9]] * 2 + P[G2[0]]) / 3, (O[G1[9]] + O[G2[0]]) / 2)
    cg.quad(G1[7], G1[8], m1, G2[7])
    cg.quad(G1[8], G1[9], m2, m1)
    cg.quad(G1[9], G1[0], G2[0], m2)
    cg.quad(G2[7], m1, m2, G2[0])
    foot = feet.build(cg, surface)
    aring = foot["ankle"]
    ankle_c = sum((cg.pos[v] for v in aring), Vector()) / 8
    centres = []
    guess = c2
    for z in zs[2:]:
        guess = cage.centre_at(surface, guess, z)
        centres.append(guess)
    # Thighs down to the knee ring (the second ring below G2), shins from there.
    leg_zones = ["thighs"] * 2 + ["shins"] * (len(centres) + 1 - 2)
    leg_rings = _tube(cg, surface, G2, c2, up, centres, aring, ankle_c, up, plan, "leg", leg_zones)
    rings["leg"] = [gop, G1] + leg_rings

    # UV seams of the half cage as coarse vertex chains (closed rings repeat their start).
    def closed(ring):
        return list(ring) + [ring[0]]

    chains = [list(N2), closed(opening), [r[1] for r in arm_rings], closed(arm_rings[-1]), closed(gop),
              [gop[8], G1[8], m1, leg_rings[0][7]] + [r[7] for r in leg_rings[1:]], closed(leg_rings[-1])]
    # Hands and feet are cut like a glove: the hand into its back and its palm side along the
    # ulnar and radial edges, the foot into top (with the heel) and sole; every finger, the thumb
    # and every toe gives a strip of its palm (sole) side, cut free at the tip. Each piece then
    # unwraps flat without folding over another.
    def digit_strip(rs, a, b):
        chains.append([r[a] for r in rs])
        chains.append([r[b] for r in rs])
        chains.append([rs[-1][a], rs[-1][b]])
        chains.append(closed(rs[0]))  # the digit is cut off at its base

    W, A, B = hand["palm_rows"]
    K = hand["knuckles"]
    hm1, _hm2 = hand["block"]
    chains.append([W[0], A[0], B[0], hm1, K[0]])     # ulnar edge of the palm to the little finger
    chains.append([W[5], A[5], B[5], K[6]])          # radial edge, along the thumb hole, to the index
    for k, rs in enumerate(hand["digit_rings"]):
        if k == 4:
            digit_strip(rs, 2, 3)                    # thumb: [near-back, far-back, far-palm, near-palm]
        else:
            digit_strip(rs, 0, 3)                    # fingers: [ulnar-palm, ulnar-back, radial-back, radial-palm]
    R1, R2, R3, R4 = foot["slices"]
    _top, bot = foot["toe_ring"]
    chains.append([R1[4], R2[4], R3[4], R4[4], bot[0]])   # medial sole edge to the big toe
    chains.append([R1[7], R2[7], R3[7], R4[7], bot[5]])   # lateral sole edge to the little toe
    chains.append([R1[4], R1[5], R1[6], R1[7]])           # back edge of the sole under the heel
    heel = foot["heel"]
    chains.append([heel[0], heel[3]])                     # up the back of the heel to the ankle
    for rs in foot["digit_rings"]:
        digit_strip(rs, 0, 3)                             # toes: [medial-sole, medial-top, lateral-top, lateral-sole]
    rings["seam_chains"] = chains
    # Spots that are rounded after the fit: the back and front corners and the hollow of the
    # armpit, and the web between thumb and index finger.
    deltoid_ring = arm_rings[1]  # in the order of the opening: [3], [4], [5] start the ring
    L3 = torso[-2]  # the ring below the armpit: the back fold turns under the arm there
    rings["soften"] = [(L4[5], 0.055, 1.0), (L4[4], 0.05, 1.0), (L4[3], 0.045, 0.7),
                       (L3[5], 0.045, 1.0), (L3[4], 0.04, 0.8),
                       (deltoid_ring[0], 0.05, 1.0), (deltoid_ring[1], 0.05, 1.0), (deltoid_ring[2], 0.05, 1.0),
                       (B[4], 0.016, 1.0), (B[5], 0.016, 1.0)]
    return cg, rings, hand, foot


def build(surface, sex, relax_iterations=30, plan=None):
    """The subdivided body (without head) fitted to one reference body. Both body types get
    the same topology and vertex order when they share one plan; only the landmarks differ."""
    half, rings, hand, foot = build_half(surface, LANDMARKS[sex], plan)
    half.faces = cage.orient(half.faces, half.pos, half.origin)
    pos, org, faces, tags, _centre, _partner, index = cage.mirror(half)
    org, n_out, n_left = cage.fix_origins(surface, org, faces)
    print("coarse origins outside", n_out, "left", n_left)
    sub_pos, sub_org, sub_faces, eidx = cage.catmull_clark(pos, org, faces)
    sub_org, n_out, n_left = cage.fix_origins(surface, sub_org, sub_faces)
    print("fine origins outside", n_out, "left", n_left)
    sym = np.array([p[:] for p in sub_pos])
    kd = KDTree(len(sym))
    for i, p in enumerate(sym):
        kd.insert(p, i)
    kd.balance()
    sub_partner = [kd.find(Vector(p))[1] for p in sym * np.array([-1.0, 1.0, 1.0])]
    sub_centre = [sub_partner[i] == i for i in range(len(sym))]
    right = [p[0] > 1e-6 for p in sym]
    sub_pos, hits = cage.cast_right(surface, sub_pos, sub_org, sub_centre, sub_partner, right)
    if relax_iterations:
        sub_pos, hits = cage.relax_right(surface, sub_pos, sub_org, sub_centre, sub_faces, sub_partner, right,
                                       iterations=relax_iterations)
    seeds = [(sub_pos[v].copy(), r, s) for v, r, s in rings["soften"]]
    sub_pos = cage.soften_right(sub_pos, sub_faces, sub_centre, sub_partner, right, seeds)

    def sub_ring(closed_full):
        out = []
        k = len(closed_full)
        for i in range(k):
            a, b = closed_full[i], closed_full[(i + 1) % k]
            out.append(a)
            out.append(eidx[tuple(sorted((a, b)))])
        return out

    nk = rings["neck_half"]
    neck_full = nk + [index[v] for v in reversed(nk[1:-1])]
    n_coarse, n_edges, n_half_faces = len(pos), len(eidx), len(half.faces)
    seams = set()
    for chain in rings["seam_chains"]:
        for a, b in zip(chain, chain[1:]):
            for x, y in ((a, b), (index[a], index[b])):
                e = eidx[tuple(sorted((x, y)))]
                seams.add(tuple(sorted((x, e))))
                seams.add(tuple(sorted((e, y))))

    def face_point(fi):
        return n_coarse + n_edges + fi

    def digit_landmarks(info, sides):
        """Tip, dorsal and palm-side vertices of every digit's end, right side then left.
        sides(k) gives the dorsal and palmar edges of digit k's end ring."""
        tips, backs, fronts = [], [], []
        for offset, remap in ((0, lambda v: v), (n_half_faces, lambda v: index[v])):
            for k, (fi, rs) in enumerate(zip(info["cap_faces"], info["digit_rings"])):
                dorsal, palmar = sides(k)
                cap = [remap(v) for v in rs[-1]]
                tips.append(face_point(fi + offset))
                backs.append(eidx[tuple(sorted((cap[dorsal[0]], cap[dorsal[1]])))])
                fronts.append(eidx[tuple(sorted((cap[palmar[0]], cap[palmar[1]])))])
        return tips, backs, fronts

    # Finger ends: [ulnar-palm, ulnar-back, radial-back, radial-palm]; the thumb's end keeps
    # the hole order [near-back, far-back, far-palm, near-palm]; toes [medial-sole, medial-top,
    # lateral-top, lateral-sole].
    f_tips, f_backs, f_fronts = digit_landmarks(hand, lambda k: ((0, 1), (2, 3)) if k == 4 else ((1, 2), (3, 0)))
    t_tips, t_backs, t_fronts = digit_landmarks(foot, lambda k: ((1, 2), (3, 0)))
    landmarks = {
        "finger_tips": f_tips, "finger_back": f_backs, "finger_front": f_fronts,
        "toe_tips": t_tips, "toe_back": t_backs, "toe_front": t_fronts,
        "wrist": sub_ring(rings["arm"][-1]),
    }
    return {
        "pos": sub_pos, "faces": sub_faces, "tags": [t for t in tags for _ in range(4)], "hits": hits,
        "zones": [z for z in list(half.face_zones) * 2 for _ in range(4)],
        "centre": sub_centre, "partner": sub_partner, "origin": sub_org, "neck": sub_ring(neck_full),
        "coarse_index": index, "eidx": eidx, "rings": rings, "hand": hand, "foot": foot,
        "coarse_count": len(pos), "seams": seams, "landmarks": landmarks,
    }
