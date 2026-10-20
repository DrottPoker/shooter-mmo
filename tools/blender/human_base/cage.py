"""Coarse quad cages and how they become the body surface: a cage whose vertices carry ray
origins inside the body, one Catmull-Clark step that blends the origins like the positions,
casting from the origins onto a reference surface, and relaxing and softening of the right
half, which is then mirrored."""

import math

from mathutils import Vector

from . import geo


class Cage:
    def __init__(self):
        self.pos = []      # guessed positions
        self.origin = []   # ray origins inside the body
        self.centre = []   # True for vertices on x = 0
        self.faces = []
        self.face_tags = []
        self.tag = "torso"
        self.face_zones = []  # body zone of each face (the pieces equipment can hide)
        self.zone = "torso"
        self.tags = {}

    def add(self, pos, origin, centre=False):
        self.pos.append(Vector(pos))
        self.origin.append(Vector(origin))
        self.centre.append(centre)
        return len(self.pos) - 1

    def quad(self, a, b, c, d):
        self.faces.append((a, b, c, d))
        self.face_tags.append(self.tag)
        self.face_zones.append(self.zone)


def dir_xy(angle_deg):
    a = math.radians(angle_deg)
    return Vector((math.sin(a), math.cos(a), 0.0))


def track_arm(surface, start, steps=20, spacing=0.02):
    """Section centres along the arm from the armpit toward the wrist."""
    p = Vector(start)
    d = Vector((0.45, 0.0, -0.89)).normalized()
    out = []
    for _ in range(steps):
        sec = geo.section(surface, p, d, iterations=3, max_dist=0.15)
        if not sec:
            break
        c = sec[0]
        out.append(c.copy())
        if len(out) > 2:
            d = (out[-1] - out[-3]).normalized()
        p = c + d * spacing
    return out


def centre_at(surface, guess, z):
    sec = geo.section(surface, Vector((guess.x, guess.y, z)), (0, 0, 1), iterations=4, max_dist=0.2)
    return sec[0] if sec else Vector((guess.x, guess.y, z))


def mirror(cg):
    """Full cage from the right half: returns positions, origins, faces, centre flags and the
    index of each vertex's mirror partner."""
    n = len(cg.pos)
    index = {}
    pos, org, centre = list(cg.pos), list(cg.origin), list(cg.centre)
    partner = list(range(n))
    for i in range(n):
        if cg.centre[i]:
            index[i] = i
            pos[i].x = 0.0
            org[i].x = 0.0
        else:
            index[i] = len(pos)
            pos.append(Vector((-cg.pos[i].x, cg.pos[i].y, cg.pos[i].z)))
            org.append(Vector((-cg.origin[i].x, cg.origin[i].y, cg.origin[i].z)))
            centre.append(False)
            partner.append(i)
            partner[i] = index[i]
    faces = list(cg.faces) + [tuple(index[v] for v in reversed(f)) for f in cg.faces]
    return pos, org, faces, list(cg.face_tags) * 2, centre, partner, index


def catmull_clark(pos, org, faces):
    """One Catmull-Clark step on a quad mesh with boundaries; origins are interpolated with
    the same weights. Returns new positions, origins, faces and per-vertex parent info."""
    pos = [Vector(p) for p in pos]
    org = [Vector(o) for o in org]
    nv = len(pos)
    edge_faces = {}
    for fi, f in enumerate(faces):
        for k in range(len(f)):
            e = tuple(sorted((f[k], f[(k + 1) % len(f)])))
            edge_faces.setdefault(e, []).append(fi)
    vert_faces = [[] for _ in range(nv)]
    vert_edges = [[] for _ in range(nv)]
    for fi, f in enumerate(faces):
        for v in f:
            vert_faces[v].append(fi)
    for e in edge_faces:
        vert_edges[e[0]].append(e)
        vert_edges[e[1]].append(e)
    fp = [sum((pos[v] for v in f), Vector()) / len(f) for f in faces]
    fo = [sum((org[v] for v in f), Vector()) / len(f) for f in faces]
    ep, eo = {}, {}
    for e, fs in edge_faces.items():
        a, b = e
        if len(fs) == 2:
            ep[e] = (pos[a] + pos[b] + fp[fs[0]] + fp[fs[1]]) / 4
            eo[e] = (org[a] + org[b] + fo[fs[0]] + fo[fs[1]]) / 4
        else:
            ep[e] = (pos[a] + pos[b]) / 2
            eo[e] = (org[a] + org[b]) / 2
    vp, vo = [], []
    for v in range(nv):
        if not vert_faces[v]:
            # A vertex no face uses keeps its place (it is dropped later).
            vp.append(pos[v].copy())
            vo.append(org[v].copy())
            continue
        boundary = [e for e in vert_edges[v] if len(edge_faces[e]) == 1]
        if boundary:
            others = [e[0] if e[1] == v else e[1] for e in boundary]
            vp.append(pos[v] * 0.75 + sum((pos[o] for o in others), Vector()) * 0.125)
            vo.append(org[v] * 0.75 + sum((org[o] for o in others), Vector()) * 0.125)
            continue
        n = len(vert_faces[v])
        q = sum((fp[f] for f in vert_faces[v]), Vector()) / n
        r = sum(((pos[e[0]] + pos[e[1]]) / 2 for e in vert_edges[v]), Vector()) / len(vert_edges[v])
        vp.append((q + 2 * r + (n - 3) * pos[v]) / n)
        qo = sum((fo[f] for f in vert_faces[v]), Vector()) / n
        ro = sum(((org[e[0]] + org[e[1]]) / 2 for e in vert_edges[v]), Vector()) / len(vert_edges[v])
        vo.append((qo + 2 * ro + (n - 3) * org[v]) / n)
    new_pos = list(vp)
    new_org = list(vo)
    eidx = {}
    for e in edge_faces:
        eidx[e] = len(new_pos)
        new_pos.append(ep[e])
        new_org.append(eo[e])
    fidx = []
    for fi in range(len(faces)):
        fidx.append(len(new_pos))
        new_pos.append(fp[fi])
        new_org.append(fo[fi])
    new_faces = []
    for fi, f in enumerate(faces):
        k = len(f)
        for i in range(k):
            a, b, prev = f[i], f[(i + 1) % k], f[(i - 1) % k]
            new_faces.append((a, eidx[tuple(sorted((a, b)))], fidx[fi], eidx[tuple(sorted((prev, a)))]))
    return new_pos, new_org, new_faces, eidx


def neighbours(nv, faces):
    nb = [set() for _ in range(nv)]
    for f in faces:
        for k in range(len(f)):
            a, b = f[k], f[(k + 1) % len(f)]
            nb[a].add(b)
            nb[b].add(a)
    return [sorted(s) for s in nb]


def boundary_vertices(faces):
    count = {}
    for f in faces:
        for k in range(len(f)):
            e = tuple(sorted((f[k], f[(k + 1) % len(f)])))
            count[e] = count.get(e, 0) + 1
    out = set()
    for e, c in count.items():
        if c == 1:
            out.update(e)
    return out


def cast(surface, pos, org, centre, max_dist=0.5):
    """Places every vertex where the ray from its origin through it meets the surface.
    Returns positions and (triangle, barycentric) hits."""
    out, hits = [], []
    for p, o, c in zip(pos, org, centre):
        d = p - o
        if c:
            d.x = 0.0
        hit = surface.first_hit(o, d, max_dist)
        if hit is None:
            loc, _n, tri, _d = surface.nearest(p)
        else:
            loc, _n, tri, _d = hit
        loc = Vector(loc)
        if c:
            loc.x = 0.0
        out.append(loc)
        hits.append((tri, surface.barycentric(loc, tri)))
    return out, hits


_PROBES = (Vector((0.31, 0.83, 0.46)).normalized(), Vector((-0.7, 0.2, -0.68)).normalized(),
           Vector((0.55, -0.6, 0.58)).normalized())


def inside(surface, p, limit=4.0):
    """True when p lies inside the closed reference surface (crossing parity, majority of three
    probe directions)."""
    votes = 0
    for d in _PROBES:
        o = Vector(p)
        count = 0
        for _ in range(64):
            hit = surface.tree.ray_cast(o, d, limit)
            if hit[0] is None:
                break
            count += 1
            o = Vector(hit[0]) + d * 1e-5
        votes += count % 2
    return votes >= 2


def fix_origins(surface, org, faces, iterations=12):
    """Moves origins that lie outside the reference (blends of inside origins can, across a
    gap such as a web) to the mean of their neighbours' inside origins."""
    org = [Vector(o) for o in org]
    nb = neighbours(len(org), faces)
    bad = {i for i, o in enumerate(org) if not inside(surface, o)}
    total = len(bad)
    for _ in range(iterations):
        if not bad:
            break
        fixed = set()
        for i in bad:
            good = [org[j] for j in nb[i] if j not in bad]
            if good:
                cand = sum(good, Vector()) / len(good)
                if not inside(surface, cand):
                    cand = min(good, key=lambda o: (o - org[i]).length)
                org[i] = cand
                fixed.add(i)
        bad -= fixed
    return org, total, len(bad)


def cast_right(surface, pos, org, centre, partner, right, max_dist=0.3):
    """Casts the right half and the centre line only and mirrors the result to the left, so
    the asymmetries of the reference (hands and feet posed a little differently) never mix."""
    idx = [i for i in range(len(pos)) if right[i] or centre[i]]
    sub, sub_hits = cast(surface, [pos[i] for i in idx], [org[i] for i in idx], [centre[i] for i in idx], max_dist)
    out = [None] * len(pos)
    hits = [None] * len(pos)
    for i, p, h in zip(idx, sub, sub_hits):
        out[i] = p
        hits[i] = h
    for i in range(len(pos)):
        if out[i] is None:
            q = out[partner[i]]
            out[i] = Vector((-q.x, q.y, q.z))
    return out, hits


def despike(surface, pos, nb, centre, partner, right, factor=0.7, rounds=4):
    """Vertices that stick out of their neighbourhood by more than factor times the mean
    length of their edges (a ray that crossed a thin web of skin to the wrong side) are put on
    the surface point nearest to their neighbours' mean. Returns the positions and count."""
    pos = list(pos)
    total = 0
    for _ in range(rounds):
        changed = 0
        for i in range(len(pos)):
            if not (right[i] or centre[i]):
                continue
            avg = sum((pos[j] for j in nb[i]), Vector()) / len(nb[i])
            edge = sum(((pos[j] - pos[i]).length for j in nb[i])) / len(nb[i])
            if (pos[i] - avg).length > factor * edge:
                loc = Vector(surface.nearest(avg)[0])
                if centre[i]:
                    loc.x = 0.0
                pos[i] = loc
                changed += 1
        for i in range(len(pos)):
            if not (right[i] or centre[i]):
                q = pos[partner[i]]
                pos[i] = Vector((-q.x, q.y, q.z))
        total += changed
        if not changed:
            break
    return pos, total


def relax_right(surface, pos, org, centre, faces, partner, right, iterations=30, strength=0.5):
    """Laplacian smoothing followed by casting the right half back onto the surface and
    mirroring it; spikes are taken out after every step."""
    nb = neighbours(len(pos), faces)
    bnd = boundary_vertices(faces)
    nb = [[j for j in ns if j in bnd] if i in bnd else ns for i, ns in enumerate(nb)]
    hits = None
    pos, _n = despike(surface, pos, nb, centre, partner, right)
    for _ in range(iterations):
        moved = []
        for i, p in enumerate(pos):
            # Tangential smoothing: only the part of the step along the surface, so bumps such
            # as the bust keep their height and only the spacing evens out.
            step = (sum((pos[j] for j in nb[i]), Vector()) / len(nb[i]) - p) * strength
            n = Vector(surface.nearest(p)[1])
            moved.append(p + step - n * step.dot(n))
        pos, hits = cast_right(surface, moved, org, centre, partner, right)
        pos, _n = despike(surface, pos, nb, centre, partner, right)
    return pos, hits


def soften_right(pos, faces, centre, partner, right, seeds, iterations=14, strength=0.5):
    """Local smoothing that does not go back to the surface: rounds pinched spots (where the
    rays of neighbouring vertices met a fold of the reference from different sides) such as
    the back of the armpit. seeds: (point, radius, weight); the right half is smoothed and
    mirrored."""
    nb = neighbours(len(pos), faces)
    w = []
    for p in pos:
        k = 0.0
        for c, r, s in seeds:
            d = (p - c).length
            t = min(max((r - d) / (r * 0.7), 0.0), 1.0)
            k = max(k, s * t * t * (3 - 2 * t))
        w.append(k)
    for _ in range(iterations):
        out = list(pos)
        for i, p in enumerate(pos):
            if w[i] <= 0.0 or not (right[i] or centre[i]):
                continue
            avg = sum((pos[j] for j in nb[i]), Vector()) / len(nb[i])
            q = p.lerp(avg, strength * w[i])
            if centre[i]:
                q.x = 0.0
            out[i] = q
        for i in range(len(out)):
            if not (right[i] or centre[i]):
                q = out[partner[i]]
                out[i] = Vector((-q.x, q.y, q.z))
        pos = out
    return pos


def orient(faces, pos, origins):
    """Makes the winding of a connected quad mesh consistent and outward (face normals away
    from the face origins)."""
    faces = [list(f) for f in faces]
    edge_faces = {}
    for i, f in enumerate(faces):
        for k in range(len(f)):
            edge_faces.setdefault(tuple(sorted((f[k], f[(k + 1) % len(f)]))), []).append(i)
    done = [False] * len(faces)
    for seed in range(len(faces)):
        if done[seed]:
            continue
        done[seed] = True
        stack = [seed]
        while stack:
            i = stack.pop()
            f = faces[i]
            for k in range(len(f)):
                a, b = f[k], f[(k + 1) % len(f)]
                for j in edge_faces[tuple(sorted((a, b)))]:
                    if j == i or done[j]:
                        continue
                    g = faces[j]
                    # Consistent neighbours run the shared edge in opposite directions.
                    same = any(g[m] == a and g[(m + 1) % len(g)] == b for m in range(len(g)))
                    if same:
                        g.reverse()
                    done[j] = True
                    stack.append(j)
    score = 0.0
    for f in faces:
        c = sum((pos[v] for v in f), Vector()) / len(f)
        o = sum((origins[v] for v in f), Vector()) / len(f)
        n = (pos[f[1]] - pos[f[0]]).cross(pos[f[2]] - pos[f[0]])
        score += n.dot(c - o)
    if score < 0:
        for f in faces:
            f.reverse()
    return [tuple(f) for f in faces]
