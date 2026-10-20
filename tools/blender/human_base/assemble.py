"""Assembles one base body: the fitted body cage with hands and feet (body), the head with eye
sockets and mouth cavity (head), and the neck strip between them, welded into one closed mesh
with exact mirror symmetry. Also collects the UV seams, the face region, the ear zone, the body
zones and the landmarks used for painting."""

import math

import numpy as np
from mathutils import Vector
from mathutils.kdtree import KDTree

from . import body, geo, head


def _match(ring_a, ring_b, pos_a, pos_b):
    """Cyclic offset and direction mapping ring_b onto ring_a with the least distance."""
    n = len(ring_a)
    best = None
    for direction in (1, -1):
        for off in range(n):
            d = sum((Vector(pos_a[ring_a[i]]) - Vector(pos_b[ring_b[(off + direction * i) % n]])).length for i in range(n))
            if best is None or d < best[0]:
                best = (d, off, direction)
    _d, off, direction = best
    return {ring_b[(off + direction * i) % n]: ring_a[i] for i in range(n)}


def _loop_edges(loop):
    return {tuple(sorted((loop[i], loop[(i + 1) % len(loop)]))) for i in range(len(loop))}


class Mesh:
    def __init__(self):
        self.verts = []
        self.faces = []
        self.part = []
        self.zone = []
        self.seams = set()
        self.face_region = set()
        self.ear_zone = set()
        self.landmarks = {}

    def add(self, verts, faces, part, remap=None):
        """Adds a part; remap maps some of its local vertex indices to existing vertices.
        Returns the local-to-mesh vertex index map and the index of its first face."""
        remap = remap or {}
        index = {}
        for i, v in enumerate(verts):
            if i in remap:
                index[i] = remap[i]
            else:
                index[i] = len(self.verts)
                self.verts.append(Vector(v))
        first = len(self.faces)
        parts = part if isinstance(part, list) else [part] * len(faces)
        for f, p in zip(faces, parts):
            self.faces.append(tuple(index[v] for v in f))
            self.part.append(p)
        return index, first

    def seams_between(self, local_set, index, faces):
        """Seams along edges of the given local faces whose two ends are both in local_set."""
        for f in faces:
            for k in range(len(f)):
                a, b = f[k], f[(k + 1) % len(f)]
                if a in local_set and b in local_set:
                    self.seams.add(tuple(sorted((index[a], index[b]))))


def quad_angles(verts, f):
    out = []
    n = len(f)
    for k in range(n):
        p, a, b = verts[f[k]], verts[f[k - 1]], verts[f[(k + 1) % n]]
        u, w = a - p, b - p
        out.append(math.degrees(u.angle(w)) if u.length > 1e-12 and w.length > 1e-12 else 0.0)
    return out


def fix_bad_quads(mesh, low=22.0, high=150.0, iterations=40, keep=frozenset()):
    """Smooths the vertices of quads with a very small or very flat corner, which would
    otherwise give sliver triangles, keeping the mesh mirror symmetric. Vertices in keep are
    never moved."""
    partner, _worst = mirror_partners(mesh.verts)
    nb = {}
    for f in mesh.faces:
        for k in range(len(f)):
            a, b = f[k], f[(k + 1) % len(f)]
            nb.setdefault(a, set()).add(b)
            nb.setdefault(b, set()).add(a)
    for _ in range(iterations):
        bad = {v for f in mesh.faces for v in f if any(x < low or x > high for x in quad_angles(mesh.verts, f))}
        if not bad:
            break

        verts = list(mesh.verts)
        for v in (bad | {partner[v] for v in bad}) - keep:
            avg = sum((verts[w] for w in nb[v]), Vector()) / len(nb[v])
            mesh.verts[v] = verts[v].lerp(avg, 0.35)
        mesh.verts = symmetrize_all(mesh.verts)
    left = {v for f in mesh.faces for v in f if any(x < low or x > high for x in quad_angles(mesh.verts, f))}
    if left:
        print("fix_bad_quads: still bad", len(left), [tuple(round(c, 3) for c in mesh.verts[v]) for v in sorted(left)[:6]])


def _neck_axis(surface, z0, z1, steps=6):
    """Neck section centres between two heights, for horizontal casting."""
    pts = []
    for k in range(steps + 1):
        z = z0 + (z1 - z0) * k / steps
        sec = geo.section(surface, Vector((0.0, 0.01, z)), (0, 0, 1), iterations=3, max_dist=0.2)
        if sec:
            pts.append((z, sec[0].y))
    return pts


def _axis_y(axis, z):
    if z <= axis[0][0]:
        return axis[0][1]
    for (za, ya), (zb, yb) in zip(axis, axis[1:]):
        if za <= z <= zb:
            t = (z - za) / (zb - za)
            return ya + (yb - ya) * t
    return axis[-1][1]


def _relax_neck(mesh, surface, tube, head_bottom, head_centre, iterations=15, strength=0.5):
    """Evens out the neck tube and the bottom of the head together, casting the tube from the
    neck axis and the head bottom from the head centre, so the head flows into the neck."""
    region = set(tube) | set(head_bottom)
    nb = {}
    for f in mesh.faces:
        for k in range(len(f)):
            a, b = f[k], f[(k + 1) % len(f)]
            nb.setdefault(a, set()).add(b)
            nb.setdefault(b, set()).add(a)
    zs = [mesh.verts[v].z for v in region]
    axis = _neck_axis(surface, min(zs) - 0.02, max(zs) + 0.01)
    tube_set = set(tube)
    kd = KDTree(len(mesh.verts))
    for i, v in enumerate(mesh.verts):
        kd.insert(v, i)
    kd.balance()
    partner = {v: kd.find(Vector((-mesh.verts[v].x, mesh.verts[v].y, mesh.verts[v].z)))[1] for v in region}
    for _ in range(iterations):
        moved = {}
        for v in region:
            avg = sum((mesh.verts[w] for w in nb[v]), Vector()) / len(nb[v])
            moved[v] = mesh.verts[v].lerp(avg, strength)
        for v, p in moved.items():
            if v in tube_set:
                c = Vector((0.0, _axis_y(axis, p.z), p.z))
                d = p - c
                d.z = 0.0
            else:
                c = head_centre
                d = p - c
            hit = surface.first_hit(c, d, 0.3)
            mesh.verts[v] = Vector(hit[0]) if hit else p
        # Keep the region mirror symmetric (the reference is not exactly symmetric).
        snapshot = {v: mesh.verts[v].copy() for v in region}
        for v in region:
            p, q = snapshot[v], snapshot[partner[v]]
            if partner[v] == v:
                mesh.verts[v] = Vector((0.0, p.y, p.z))
            else:
                mesh.verts[v] = Vector(((p.x - q.x) / 2, (p.y + q.y) / 2, (p.z + q.z) / 2))


def _compact(mesh):
    """Drops vertices that no face uses (the inside of the neck opening of the head)."""
    used = sorted({v for f in mesh.faces for v in f})
    index = {old: new for new, old in enumerate(used)}
    out = Mesh()
    out.verts = [mesh.verts[i] for i in used]
    out.faces = [tuple(index[v] for v in f) for f in mesh.faces]
    out.part = list(mesh.part)
    out.zone = list(mesh.zone)
    out.seams = {tuple(sorted((index[a], index[b]))) for a, b in mesh.seams if a in index and b in index}
    out.face_region = set(mesh.face_region)
    out.ear_zone = {index[v] for v in mesh.ear_zone if v in index}
    out.landmarks = {k: [index[v] for v in vs] for k, vs in mesh.landmarks.items()}
    return out


def mirror_partners(verts):
    kd = KDTree(len(verts))
    for i, v in enumerate(verts):
        kd.insert(v, i)
    kd.balance()
    partner = []
    worst = 0.0
    for v in verts:
        _co, j, d = kd.find(Vector((-v.x, v.y, v.z)))
        worst = max(worst, d)
        partner.append(j)
    return partner, worst


def symmetrize_all(verts):
    partner, worst = mirror_partners(verts)
    assert worst < 5e-3, f"mirror partner too far: {worst}"
    out = []
    for i, v in enumerate(verts):
        q = verts[partner[i]]
        if partner[i] == i:
            out.append(Vector((0.0, v.y, v.z)))
        else:
            out.append(Vector(((v.x - q.x) / 2, (v.y + q.y) / 2, (v.z + q.z) / 2)))
    return out


HEAD_PART = {"skin": "head", "socket": "socket", "cavity": "mouth"}


def regions(verts, faces, tags, eye, ring):
    """Faces of the face region (front of the head from the hairline to under the chin, in
    front of the ears, with the eye sockets and the mouth cavity) and of the ears. eye: the
    surface point in front of the right eyeball."""
    co = np.asarray(verts)
    ring_front = max(co[v][1] for v in ring)
    face_set, ear_set = set(), set()
    for i, f in enumerate(faces):
        if tags[i] != "skin":
            face_set.add(i)
            continue
        c = co[list(f)].mean(0)
        ax = abs(c[0])
        if ax > 0.064 and c[1] < eye[1] - 0.055 and eye[2] - 0.07 < c[2] < eye[2] + 0.04:
            ear_set.add(i)
            continue
        if c[1] > eye[1] - 0.068 and c[2] < eye[2] + 0.068 and c[1] > ring_front - 0.005:
            face_set.add(i)
    return face_set, ear_set


def _quad_bad(verts, f, low=20.0, high=155.0, aspect=8.0):
    if len(f) != 4:
        return False
    angles = quad_angles(verts, f)
    edges = [(verts[f[(k + 1) % 4]] - verts[f[k]]).length for k in range(4)]
    return min(angles) < low or max(angles) > high or max(edges) > aspect * max(min(edges), 1e-9)


def relax_bad_quads(mesh, surface, keep_out=(), iterations=15, strength=0.5):
    """Vertices of skin quads with a very sharp or flat corner or a thin shape (a projection
    squeezed two rows together) slide along the reference surface toward their neighbours'
    mean. The right half is moved and mirrored. Socket and mouth faces are hidden and keep
    their designed shape."""
    nb = {}
    for f in mesh.faces:
        for k in range(len(f)):
            a, b = f[k], f[(k + 1) % len(f)]
            nb.setdefault(a, set()).add(b)
            nb.setdefault(b, set()).add(a)
    partner, _worst = mirror_partners(mesh.verts)
    hidden = {v for f, p in zip(mesh.faces, mesh.part) if p in ("socket", "mouth") for v in f}
    # The designed eyelids and lips are not moved (keep_out: (centre, radius) spheres).
    hidden |= {i for i, p in enumerate(mesh.verts) if any((p - c).length < r for c, r in keep_out)}
    skin = [f for f, p in zip(mesh.faces, mesh.part) if p not in ("socket", "mouth")]
    for _ in range(iterations):
        bad = {v for f in skin if _quad_bad(mesh.verts, f) for v in f} - hidden
        bad = {v if mesh.verts[v].x >= 0 else partner[v] for v in bad}
        if not bad:
            break
        verts = list(mesh.verts)
        for v in bad:
            step = (sum((verts[w] for w in nb[v]), Vector()) / len(nb[v]) - verts[v]) * strength
            n = Vector(surface.nearest(verts[v])[1])
            p = Vector(surface.nearest(verts[v] + step - n * step.dot(n))[0])
            if partner[v] == v:
                p.x = 0.0
            mesh.verts[v] = p
            if partner[v] != v:
                mesh.verts[partner[v]] = Vector((-p.x, p.y, p.z))
    left = {v for f in skin if _quad_bad(mesh.verts, f) for v in f}
    print("relax_bad_quads: left", len(left))


def assemble(R, sex, relax_iterations=15, plan=None):
    """plan: topology decisions shared by all body types (filled by the first body built)."""
    surface = R.body(sex)
    plan = {} if plan is None else plan
    fitted = body.build(surface, sex, relax_iterations, plan)
    mesh = Mesh()
    mesh.add([p[:] for p in fitted["pos"]], fitted["faces"], fitted["tags"])
    mesh.zone = list(fitted["zones"])
    mesh.seams |= fitted["seams"]
    mesh.landmarks = {k: list(v) for k, v in fitted["landmarks"].items()}
    # Head with its sockets and mouth cavity.
    head_fit = head.build(R.head[sex], R.head_faces, surface, sex)
    hverts, hfaces, htags = head_fit["verts"], head_fit["faces"], head_fit["tags"]
    eye_c, eye_r = head_fit["eyes"][0]
    eye_front = Vector((eye_c.x, eye_c.y + eye_r, eye_c.z))
    face_local, _ears = regions(hverts, hfaces, htags, eye_front, head_fit["neck_ring"])
    hidx, hfirst = mesh.add(hverts, hfaces, [HEAD_PART[t] for t in htags])
    mesh.zone += ["head"] * len(hfaces)
    mesh.face_region = {hfirst + i for i in face_local}
    mesh.ear_zone = {hidx[i] for i, v in enumerate(hverts)
                     if abs(v[0]) > 0.064 and v[1] < eye_front.y - 0.045 and eye_front.z - 0.052 < v[2] < eye_front.z + 0.04}
    # Each ear is its own UV island (its folds would overlap the side of the head).
    ear_faces = [f for f in hfaces if all(hidx[v] in mesh.ear_zone for v in f)]
    ear_edges = {}
    for f in ear_faces:
        for k in range(len(f)):
            e = tuple(sorted((hidx[f[k]], hidx[f[(k + 1) % len(f)]])))
            ear_edges[e] = ear_edges.get(e, 0) + 1
    ear_border = {e for e, c in ear_edges.items() if c == 1}
    mesh.seams |= ear_border
    head_ring = [hidx[i] for i in head_fit["neck_ring"]]
    mesh.seams |= _loop_edges(head_ring)
    for k, v in head.LANDMARKS.items():
        mesh.landmarks[k] = [hidx[v]]
    mesh.landmarks["eye_rim"] = [hidx[v] for v in head_fit["landmarks"]["eye_rim"]]
    mesh.landmarks["upper_lip_line"] = [hidx[v] for v in head_fit["upper_line"]]
    mesh.landmarks["lower_lip_line"] = [hidx[v] for v in head_fit["lower_line"]]
    # Seams around the face region (its border edges).
    for i in face_local:
        f = hfaces[i]
        for k in range(len(f)):
            a, b = f[k], f[(k + 1) % len(f)]
            shared = any(j != i and j in face_local and a in hfaces[j] and b in hfaces[j] for j in face_local)
            if not shared:
                mesh.seams.add(tuple(sorted((hidx[a], hidx[b]))))
    # No face may be cut out on its own by the ear seams (it would unwrap to nothing).
    for f in hfaces:
        edges = [tuple(sorted((hidx[f[k]], hidx[f[(k + 1) % len(f)]]))) for k in range(len(f))]
        if all(e in mesh.seams for e in edges):
            mesh.seams -= set(edges) & ear_border
    # The body's upper neck ring meets the head's ring in one strip of quads.
    neck = fitted["neck"]
    if "neck" not in plan:
        plan["neck"] = _match(neck, head_ring, mesh.verts, mesh.verts)
    match = plan["neck"]
    inv = {v: k for k, v in match.items()}
    top = [inv[v] for v in neck]
    n = len(neck)
    # Wind the strip like the body: a body face runs the ring edge neck[0] -> neck[1] the other way.
    forward = any(f[k] == neck[0] and f[(k + 1) % len(f)] == neck[1] for f in mesh.faces for k in range(len(f)))
    for i in range(n):
        j = (i + 1) % n
        mesh.faces.append((neck[j], neck[i], top[i], top[j]) if forward else (neck[i], neck[j], top[j], top[i]))
        mesh.part.append("neck")
        mesh.zone.append("neck")
    _relax_neck(mesh, surface, [], [hidx[i] for i in head_fit["transition"]], head_fit["centre"])
    mesh = _compact(mesh)
    mesh.verts = symmetrize_all(mesh.verts)
    keep_out = [(c, 0.026) for c, _r in head_fit["eyes"]] + [(Vector(head_fit["mouth"]["centre"]), 0.032)]
    relax_bad_quads(mesh, surface, keep_out)
    keep = frozenset(i for i, p in enumerate(mesh.verts) if any((p - c).length < r for c, r in keep_out))
    fix_bad_quads(mesh, keep=keep)
    mesh.eyes = head_fit["eyes"]
    mesh.mouth = head_fit["mouth"]
    return mesh
