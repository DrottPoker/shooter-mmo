"""The head: the Generic Topology head of the Human Base Meshes bundle in its realistic male or
female shape, aligned to the body's head (ICP), opened at the eyes, the mouth and the neck,
subdivided once and moved onto the body's realistic head.

- Eyes: the closed lid faces are removed; the lid margin rests on a separate eyeball and a
  closed pocket behind it keeps the head watertight.
- Mouth: the line between the lips is split so the lips part slightly; a closed mouth cavity
  goes back from the inner lips, ready for an animated lower jaw. Teeth and tongue are built
  separately.
- Neck: a 32-vertex ring (16 before subdivision) that the body's neck tube meets.

Faces keep a region tag: skin, socket (eye pocket) or cavity (mouth)."""

import math

import numpy as np
from mathutils import Vector
from mathutils.kdtree import KDTree

from . import cage, geo


# Right eye-opening vertices of the Generic Topology head (the closed lid slit and its corners).
EYE_SLIT = (199, 200, 201, 202, 203, 204, 205, 206, 207, 208, 209)
# Centre-line vertices: bottom of the chin and front of the chin.
CHIN_BOTTOM = 285
# Face landmarks of the Generic Topology head (right side where it matters).
LANDMARKS = {
    "mouth_centre": 286, "mouth_corner": 153, "upper_lip": 279, "lower_lip": 276,
    "subnasale": 278, "nose_tip": 288, "sellion": 274, "chin_front": 280, "chin_bottom": 285,
    "forehead": 289,
}


def mirror_map(co):
    """Index of each vertex's mirror partner across x = 0."""
    co = np.asarray(co)
    mirrored = co * np.array([-1.0, 1.0, 1.0])
    out = []
    for p in mirrored:
        d = ((co - p) ** 2).sum(1)
        out.append(int(np.argmin(d)))
    return np.array(out)


def symmetrize(co, mirror):
    co = np.asarray(co, np.float64)
    partner = co[mirror] * np.array([-1.0, 1.0, 1.0])
    out = (co + partner) / 2
    out[mirror == np.arange(len(co)), 0] = 0.0
    return out


def align(head_co, surface):
    """Scales and moves the head onto the body's head with ICP (similarity transform)."""
    co = np.asarray(head_co, np.float64).copy()
    top = surface.co[:, 2].max()
    body_head = surface.co[surface.co[:, 2] > top - 0.24]
    co *= 0.235 / (co[:, 2].max() - co[:, 2].min())
    co[:, 2] += top - co[:, 2].max()
    co[:, 1] += (body_head[:, 1].max() + body_head[:, 1].min()) / 2 - (co[:, 1].max() + co[:, 1].min()) / 2
    z0, z1 = co[:, 2].min(), co[:, 2].max()
    use = (co[:, 2] > z0 + 0.25 * (z1 - z0)) & (np.abs(co[:, 0]) < 0.068)
    _pts, mat = geo.icp(co[use], surface, iterations=25)
    co = (mat[:3, :3] @ co.T).T + mat[:3, 3]
    return symmetrize(co, mirror_map(head_co))


# Faces at the bottom of the head that open into the neck; their border is a 16-vertex ring.
NECK_HOLE_FACES = (128, 129, 134, 135, 150, 151, 286, 287, 292, 293, 308, 309)


def ordered_ring(faces):
    """The ordered border loop of a set of faces given as vertex tuples."""
    edges = {}
    for f in faces:
        for k in range(len(f)):
            a, b = f[k], f[(k + 1) % len(f)]
            edges[(a, b)] = True
    border = {a: b for (a, b) in edges if (b, a) not in edges}
    start = min(border)
    loop = [start]
    while border[loop[-1]] != start:
        loop.append(border[loop[-1]])
    return loop


def loops_from(ring, faces, depth):
    """Vertices by edge distance from the ring, up to the given depth."""
    nb = {}
    for f in faces:
        for k in range(len(f)):
            a, b = f[k], f[(k + 1) % len(f)]
            nb.setdefault(a, set()).add(b)
            nb.setdefault(b, set()).add(a)
    level = {v: 0 for v in ring}
    frontier = list(ring)
    for d in range(1, depth + 1):
        nxt = []
        for v in frontier:
            for w in nb.get(v, ()):
                if w not in level:
                    level[w] = d
                    nxt.append(w)
        frontier = nxt
    return level


# Closed eyelid faces of the right eye; the left eye uses their mirror partners.
EYE_FACES_RIGHT = (217, 218, 219, 220, 221, 222, 223, 224)
LOWER_LIP_FACES = (199, 216, 41, 58)
MOUTH_LINE = (183, 286, 40)
MOUTH_ENDS = (271, 128)
EYE_RADIUS = 0.0118
# Eye opening (seen from the front, relative to the iris centre, metres): half width, upper
# and lower lid height at the middle and the rise of the outer corner per metre of width.
# The upper lid covers the top of the iris a little (a relaxed, slightly hooded look).
EYE_OPENING = {"male": (0.0142, 0.0040, 0.0049, 0.06), "female": (0.0146, 0.0044, 0.0051, 0.08)}
LIP_PART = 0.0016          # gap between the lips at the centre of the mouth
PROJECTION = 0.85
TRANSITION_LOOPS = 4       # on the subdivided head
RING_DROP = 0.03


def _smooth(e0, e1, x):
    t = min(max((x - e0) / (e1 - e0), 0.0), 1.0)
    return t * t * (3.0 - 2.0 * t)


def _mirror_face(face, mirror, faces_by_set):
    return faces_by_set[frozenset(mirror[v] for v in face)]


def _cap(ring_pos, add_vert, faces, tags, tag, n_side):
    """Closes a ring of 4n vertices (indices) with an n x n Coons patch of quads."""
    m = len(ring_pos)
    n = m // 4
    border = [(k, 0) for k in range(n)] + [(n, k) for k in range(n)] + [(n - k, n) for k in range(n)] + [(0, n - k) for k in range(n)]
    grid, pos = {}, {}
    for k, ij in enumerate(border):
        grid[ij] = ring_pos[k][0]
        pos[ij] = Vector(ring_pos[k][1])
    for i in range(1, n):
        for j in range(1, n):
            s, t = i / n, j / n
            p = (1 - s) * pos[(0, j)] + s * pos[(n, j)] + (1 - t) * pos[(i, 0)] + t * pos[(i, n)]
            p -= (1 - s) * (1 - t) * pos[(0, 0)] + s * (1 - t) * pos[(n, 0)] + (1 - s) * t * pos[(0, n)] + s * t * pos[(n, n)]
            pos[(i, j)] = p
    for _ in range(30):
        for i in range(1, n):
            for j in range(1, n):
                pos[(i, j)] = pos[(i, j)].lerp((pos[(i - 1, j)] + pos[(i + 1, j)] + pos[(i, j - 1)] + pos[(i, j + 1)]) / 4, 0.6)
    for i in range(1, n):
        for j in range(1, n):
            grid[(i, j)] = add_vert(pos[(i, j)])
    for i in range(n):
        for j in range(n):
            cell = [(i, j), (i + 1, j), (i + 1, j + 1), (i, j + 1)]
            corner = [k for k, (a, b) in enumerate(cell) if a in (0, n) and b in (0, n)]
            if corner:
                k = corner[0]
                o = [cell[(k + q) % 4] for q in range(4)]
                for tri in ((o[0], o[1], o[2]), (o[0], o[2], o[3])):
                    faces.append(tuple(grid[c] for c in tri))
                    tags.append(tag)
            else:
                faces.append(tuple(grid[c] for c in cell))
                tags.append(tag)


def _ordered_loop(boundary_edges, start):
    nxt = {}
    for a, b in boundary_edges:
        nxt.setdefault(a, []).append(b)
        nxt.setdefault(b, []).append(a)
    loop = [start]
    prev = None
    while True:
        cands = [v for v in nxt[loop[-1]] if v != prev]
        if not cands or cands[0] == start:
            break
        prev = loop[-1]
        loop.append(cands[0])
    return loop


def build(head_co, faces, surface, sex):
    """Returns a dict: verts, faces, tags, neck_ring (ordered), transition (vertex list),
    centre (cast origin for the transition), eyes (centre, radius), mouth (frame for teeth),
    landmarks (vertex indices)."""
    co = align(head_co, surface)
    mirror = mirror_map(head_co)
    faces_by_set = {frozenset(f): i for i, f in enumerate(faces)}
    eye_faces = set(EYE_FACES_RIGHT) | {_mirror_face(faces[i], mirror, faces_by_set) for i in EYE_FACES_RIGHT}
    hole = set(NECK_HOLE_FACES) | eye_faces
    # Split the line between the lips: the lower lip gets copies of its vertices.
    verts = [Vector(p) for p in co]
    mirror = list(mirror)
    dup = {}
    for v in MOUTH_LINE:
        dup[v] = len(verts)
        verts.append(Vector(co[v]))
        mirror.append(None)
    for v in MOUTH_LINE:
        mirror[dup[v]] = dup[mirror[v]]
    new_faces, tags = [], []
    for i, f in enumerate(faces):
        if i in hole:
            continue
        if i in LOWER_LIP_FACES:
            f = tuple(dup.get(v, v) for v in f)
        new_faces.append(tuple(f))
        tags.append("skin")
    ring_coarse = ordered_ring([faces[i] for i in NECK_HOLE_FACES])
    # Subdivide once (Catmull-Clark; open borders keep their curves).
    pos, _org, sub_faces, eidx = cage.catmull_clark(verts, [Vector()] * len(verts), new_faces)
    sub_tags = [t for t in tags for _ in range(4)]
    pos = [Vector(p) for p in pos]
    # Mirror map of the subdivided head from the coarse one (positions cannot tell the two
    # coinciding copies of the line between the lips apart).
    n_edges = len(eidx)
    face_by_set = {frozenset(f): fi for fi, f in enumerate(new_faces)}
    sub_mirror = list(mirror) + [None] * (len(pos) - len(verts))
    for (a, b), e in eidx.items():
        sub_mirror[e] = eidx[tuple(sorted((mirror[a], mirror[b])))]
    for fi, f in enumerate(new_faces):
        sub_mirror[len(verts) + n_edges + fi] = len(verts) + n_edges + face_by_set[frozenset(mirror[v] for v in f)]
    # The mouth: part the lips (copies of the line were made before subdividing, so the
    # subdivided line has an upper and a lower copy).
    corner_r = verts[LANDMARKS["mouth_corner"]]
    upper_line = _line_after(MOUTH_ENDS, MOUTH_LINE, eidx, upper=True, dup=dup)
    lower_line = _line_after(MOUTH_ENDS, MOUTH_LINE, eidx, upper=False, dup=dup)
    # Move most of the head onto the body's head with smooth weights.
    normals = geo.vertex_normals([p[:] for p in pos], sub_faces)
    chin = pos[CHIN_BOTTOM].z
    eye_c0 = sum((pos[v] for v in EYE_SLIT), Vector()) / len(EYE_SLIT)
    eyes0 = (eye_c0, Vector((-eye_c0.x, eye_c0.y, eye_c0.z)))
    neck_ring = []
    for k, v in enumerate(ring_coarse):
        a, b = v, ring_coarse[(k + 1) % len(ring_coarse)]
        neck_ring += [a, eidx[tuple(sorted((a, b)))]]
    level = loops_from(neck_ring, sub_faces, TRANSITION_LOOPS)
    out = [p.copy() for p in pos]
    for i, p in enumerate(pos):
        if i in level:
            continue
        n = Vector(normals[i])
        de = min((p - e).length for e in eyes0)
        under = _smooth(-0.15, -0.55, n.z) * _smooth(chin + 0.035, chin + 0.012, p.z)
        w = (PROJECTION + (1.0 - PROJECTION) * under) * _smooth(0.074, 0.058, abs(p.x))
        # The eye margins and the lips keep their designed shape; the skin around them follows.
        w *= 0.15 + 0.85 * _smooth(0.010, 0.026, de)
        target = geo.project_along(surface, p, n, 0.02)
        out[i] = p.lerp(target, w)
    ring_centre = sum((pos[v] for v in neck_ring), Vector()) / len(neck_ring)
    centre = Vector((0.0, ring_centre.y, chin + 0.04))
    for i, lv in level.items():
        share = 1.0 - lv / (TRANSITION_LOOPS + 1)
        target = pos[i] + Vector((0.0, 0.0, -RING_DROP * share))
        hit = surface.first_hit(centre, target - centre, 0.4)
        if hit:
            proj = geo.project_along(surface, out[i], Vector(normals[i]), 0.02)
            out[i] = proj.lerp(Vector(hit[0]), min(1.0, share * 1.5))
    out = _symmetrize(out, sub_mirror)
    # Even out the line between the lips (it alternates between original and new vertices).
    for _ in range(6):
        for line in (upper_line, lower_line):
            moved = {}
            for a, v, b in zip(line, line[1:], line[2:]):
                avg = (out[a] + out[b]) / 2
                moved[v] = Vector((out[v].x, out[v].y * 0.5 + avg.y * 0.5, out[v].z * 0.4 + avg.z * 0.6))
            for v, p in moved.items():
                out[v] = p
        out = _symmetrize(out, sub_mirror)
    # The closed lips share one line: average the upper and lower copies.
    for a, b in zip(upper_line, lower_line):
        m = (out[a] + out[b]) / 2
        out[a], out[b] = m.copy(), m.copy()
    # Part the lips symmetrically about the closed line.
    for line, sign in ((upper_line, 1.0), (lower_line, -1.0)):
        for v in line[1:-1]:
            t = 1.0 - (abs(out[v].x) / max(abs(corner_r.x), 1e-6)) ** 2
            out[v] = out[v] + Vector((0.0, -0.0006 * t, sign * LIP_PART / 2 * max(t, 0.0)))
    out = _symmetrize(out, sub_mirror)
    # The two rows of each lip next to the opening are spaced evenly again (parting pushes the
    # line into the first row), keeping their x.
    nb = {}
    for f in sub_faces:
        for k in range(len(f)):
            a, b = f[k], f[(k + 1) % len(f)]
            nb.setdefault(a, set()).add(b)
            nb.setdefault(b, set()).add(a)
    lines = set(upper_line) | set(lower_line)
    for line in (upper_line, lower_line):
        row1 = {w for v in line for w in nb[v] if w not in lines}
        row2 = {w for v in row1 for w in nb[v] if w not in lines and w not in row1}
        for _ in range(4):
            moved = {}
            for v in row1:
                inner = [w for w in nb[v] if w in lines]
                outer = [w for w in nb[v] if w in row2]
                if inner and outer:
                    a = sum((out[w] for w in inner), Vector()) / len(inner)
                    b = sum((out[w] for w in outer), Vector()) / len(outer)
                    mid = (a + b) / 2
                    moved[v] = Vector((out[v].x, out[v].y * 0.5 + mid.y * 0.5, out[v].z * 0.3 + mid.z * 0.7))
            for v, p in moved.items():
                out[v] = p
            out = _symmetrize(out, sub_mirror)
    # Eyes: the boundary loops of the eye holes, an eyeball fitted to each margin and a pocket.
    faces_out, tags_out = list(sub_faces), list(sub_tags)
    edges = {}
    for f in faces_out:
        for k in range(len(f)):
            e = tuple(sorted((f[k], f[(k + 1) % len(f)])))
            edges[e] = edges.get(e, 0) + 1
    boundary = [e for e, c in edges.items() if c == 1]
    in_neck = set(neck_ring)
    eye_edges = [e for e in boundary if not (e[0] in in_neck and e[1] in in_neck)
                 and min((out[e[0]] - eo).length for eo in eyes0) < 0.03]
    mouth_edges = [e for e in boundary if e not in set(eye_edges) and not (e[0] in in_neck and e[1] in in_neck)]
    # Loops start at a fixed vertex (the right eye's lowest index and its mirror partner) and
    # run so that the new faces wind like their neighbours, so both body types get the same
    # topology whatever their shape.
    right_edges = [e for e in eye_edges if out[e[0]].x > 0]
    left_edges = [e for e in eye_edges if out[e[0]].x < 0]
    start_r = min({v for e in right_edges for v in e})
    eyes = [_oriented_loop(right_edges, start_r, faces_out), _oriented_loop(left_edges, sub_mirror[start_r], faces_out)]

    def add_vert(p):
        out.append(Vector(p))
        return len(out) - 1

    eye_info = []
    # The eyeball sits just behind the closed-lid surface of the Generic Topology head.
    c_r = Vector((eye_c0.x, eye_c0.y - EYE_RADIUS - 0.0005, eye_c0.z))
    for side, loop in zip((1.0, -1.0), eyes):
        c = Vector((c_r.x * side, c_r.y, c_r.z))
        eye_info.append((c, EYE_RADIUS))
        _eye_pocket(out, loop, c, side, EYE_OPENING[sex], faces_out, tags_out, add_vert)
    # Mouth cavity from the opening between the lips.
    mouth_loop = _oriented_loop(mouth_edges, min({v for e in mouth_edges for v in e}), faces_out)
    mouth_frame = _mouth_cavity(out, mouth_loop, faces_out, tags_out, add_vert)
    # Exact mirror symmetry of everything built after the last symmetrize step.
    kd = KDTree(len(out))
    for i, p in enumerate(out):
        kd.insert(p, i)
    kd.balance()
    partner = []
    for p in out:
        _co, j, dist = kd.find(Vector((-p.x, p.y, p.z)))
        assert dist < 2e-3, f"head vertex without mirror partner: {p} ({dist})"
        partner.append(j)
    out = _symmetrize(out, partner)
    landmarks = {k: [v] for k, v in LANDMARKS.items()}
    landmarks["eye"] = list(EYE_SLIT)
    landmarks["eye_rim"] = list(eyes[0])
    return {
        "verts": [p[:] for p in out], "faces": faces_out, "tags": tags_out, "neck_ring": neck_ring,
        "transition": sorted(level), "centre": centre, "eyes": eye_info, "mouth": mouth_frame,
        "landmarks": landmarks, "upper_line": upper_line, "lower_line": lower_line,
    }


def _oriented_loop(boundary_edges, start, faces):
    """Boundary loop from start, running so that a face (loop[k], loop[k + 1], ...) added
    against it winds like the existing face on the other side of that edge."""
    loop = _ordered_loop(boundary_edges, start)
    a, b = loop[0], loop[1]
    for f in faces:
        n = len(f)
        for k in range(n):
            if f[k] == a and f[(k + 1) % n] == b:
                return [loop[0]] + loop[1:][::-1]
            if f[k] == b and f[(k + 1) % n] == a:
                return loop
    raise ValueError("loop edge without a face")


def _line_after(ends, line, eidx, upper, dup):
    """Ordered vertices of the line between the lips after subdivision (one copy)."""
    path = [ends[0]] + [v if upper else dup[v] for v in line] + [ends[1]]
    out = []
    for a, b in zip(path, path[1:]):
        out += [a, eidx[tuple(sorted((a, b)))]]
    out.append(path[-1])
    return out


def _symmetrize(pos, mirror):
    out = []
    for i, p in enumerate(pos):
        q = pos[mirror[i]]
        if mirror[i] == i:
            out.append(Vector((0.0, p.y, p.z)))
        else:
            out.append(Vector(((p.x - q.x) / 2, (p.y + q.y) / 2, (p.z + q.z) / 2)))
    return out


def _almond(u, W, up, low, tilt, upper):
    t = max(1.0 - (u / W) ** 2, 0.0)
    return tilt * u + (up * t ** 0.7 if upper else -low * t ** 0.85)


def _on_eyeball(c, u, v, radius):
    """Point on the front of a sphere around c at frontal offsets (u, v); past the silhouette
    the point stays on the equator."""
    d2 = radius * radius - u * u - v * v
    return Vector((c.x + u, c.y + math.sqrt(max(d2, 0.0)), c.z + v))


def _eye_pocket(out, loop, c, side, opening, faces, tags, add_vert):
    """Places the lid margin as an almond on the eyeball, rolls it 1.2 mm onto the eyeball as
    lid thickness, and closes a pocket behind the eyeball."""
    W, up, low, tilt = opening
    r = EYE_RADIUS
    n = len(loop)
    canon = list(loop)  # new vertices and faces follow the given (canonical) order
    # Corners: the loop vertices farthest to the inner and outer side.
    us = [(out[v].x - c.x) * side for v in loop]
    inner_k = min(range(n), key=lambda k: us[k])
    outer_k = max(range(n), key=lambda k: us[k])
    loop = loop[inner_k:] + loop[:inner_k]
    outer_k = (outer_k - inner_k) % n
    chain_a, chain_b = loop[:outer_k + 1], loop[outer_k:] + loop[:1]
    za = sum(out[v].z for v in chain_a) / len(chain_a)
    zb = sum(out[v].z for v in chain_b) / len(chain_b)
    upper_chain, lower_chain = (chain_a, chain_b) if za > zb else (chain_b, chain_a)
    if upper_chain[0] != loop[0]:
        upper_chain = list(reversed(upper_chain))
    if lower_chain[0] != loop[0]:
        lower_chain = list(reversed(lower_chain))
    target = {}
    for chain, is_upper in ((upper_chain, True), (lower_chain, False)):
        m = len(chain) - 1
        for k, v in enumerate(chain):
            # Even spacing from the inner corner (-W) to the outer corner (+W), denser at the ends.
            s = k / m
            u = -W * math.cos(math.pi * s)
            vv = _almond(u, W, up, low, tilt, is_upper)
            target[v] = (u, vv)
    for v, (u, vv) in target.items():
        p = _on_eyeball(c, u * side, vv, r + 0.0006)
        if abs(u) >= W * 0.999:
            p.y = c.y + (0.003 if u < 0 else -0.002)
        out[v] = p
    loop = canon
    inner = []
    for v in loop:
        u, vv = target[v]
        k = 1.0 - 0.0012 / max(math.hypot(u, vv), 0.002)
        inner.append(add_vert(_on_eyeball(c, u * k * side, vv * k, r + 0.0003)))
    back = []
    for v in loop:
        d = out[v] - c
        flat = Vector((d.x, 0.0, d.z)).normalized()
        back.append(add_vert(c + (flat * 0.95 + Vector((0.0, -0.45, 0.0))).normalized() * (r + 0.0012)))
    for ring_a, ring_b, tag in ((loop, inner, "skin"), (inner, back, "socket")):
        for k in range(n):
            a, b = ring_a[k], ring_a[(k + 1) % n]
            faces.append((a, b, ring_b[(k + 1) % n], ring_b[k]))
            tags.append(tag)
    first = len(out)
    _cap([(v, out[v]) for v in back], add_vert, faces, tags, "socket", n // 4)
    # Bulge the pocket's back over the back of the eyeball.
    for i in range(first, len(out)):
        d = out[i] - c
        d.y = min(d.y, -0.2 * d.length)
        out[i] = c + d.normalized() * (r + 0.0012)
    # The lid skin next to the margin wraps the eyeball (about 2 mm thick).
    ring = set(loop)
    nb = {}
    for f in faces:
        for k in range(len(f)):
            a, b = f[k], f[(k + 1) % len(f)]
            nb.setdefault(a, set()).add(b)
            nb.setdefault(b, set()).add(a)
    first_loop = {w for v in loop for w in nb[v] if w not in ring and w not in set(inner)}
    second_loop = {w for v in first_loop for w in nb[v] if w not in ring and w not in first_loop and w not in set(inner)}
    for verts, share, thick in ((first_loop, 0.75, 0.0024), (second_loop, 0.35, 0.0036)):
        for v in verts:
            d = (out[v] - c).normalized()
            out[v] = out[v].lerp(c + d * (r + thick), share)


def _mouth_cavity(out, loop, faces, tags, add_vert):
    """A closed cavity behind the lips: inner lips, behind the teeth, back of the mouth."""
    # Start the loop on the centre line below, so the cap's patch corners are mirror pairs.
    start = min((k for k in range(len(loop)) if abs(out[loop[k]].x) < 1e-6), key=lambda k: out[loop[k]].z)
    loop = loop[start:] + loop[:start]
    pts = [out[v] for v in loop]
    centre = sum(pts, Vector()) / len(pts)
    width = max(abs(p.x) for p in pts)
    rings = [loop]
    for back, lift_up, lift_down, scale in ((0.005, 0.002, 0.002, 0.97), (0.016, 0.012, 0.013, 1.0), (0.042, 0.012, 0.011, 0.82)):
        ring = []
        for p in pts:
            up = 1.0 if p.z >= centre.z - 1e-6 else -1.0
            q = Vector((p.x * scale, p.y - back, p.z + (lift_up if up > 0 else -lift_down) * (1.0 - (abs(p.x) / width) ** 2) ** 0.5))
            ring.append(add_vert(q))
        rings.append(ring)
    n = len(loop)
    for ra, rb in zip(rings, rings[1:]):
        for k in range(n):
            faces.append((ra[k], ra[(k + 1) % n], rb[(k + 1) % n], rb[k]))
            tags.append("cavity")
    _cap([(v, out[v]) for v in rings[-1]], add_vert, faces, tags, "cavity", n // 4)
    return {"centre": centre, "width": width, "front_y": max(p.y for p in pts)}
