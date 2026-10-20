"""Quality checks of the base characters. Each check returns its report lines and the rules it
found broken:

- mesh: open edges, edges shared by more than two faces, tiny and sliver faces, mirror error,
  loose parts, identical body topology, and the triangles of each character (body with its
  eyeballs, teeth and tongue). Teeth and tongue sit inside the closed mouth cavity and may be
  open at the back.
- seams: the colour difference across every UV seam of the bodies, sampled through the UVs of
  both faces that share the seam edge (sRGB, 0..255). It is a report and breaks no rule.
- parts: the split characters, where every body part of the male and the female has the same
  topology, the parts close up into one watertight body (every open edge of a part is matched
  by an edge of a neighbouring part), and the normals agree across the cuts."""

import math

import bmesh
import bpy
import numpy as np
from mathutils import Vector
from mathutils.kdtree import KDTree

BODIES = ("Male_Body", "Female_Body")
CHARACTERS = ("Male", "Female")
MAX_TRIS = 10000
TINY_AREA = 1e-6        # m^2 (1 mm^2)
TINY_AREA_HIDDEN = 2.5e-7  # m^2, eye sockets and mouth cavity (small by design, never seen)
HIDDEN_PARTS = (7, 8)   # part attribute: socket, mouth
SLIVER_ANGLE = 5.0      # degrees, smallest angle of a triangle after triangulation
OPEN_ALLOWED = ("_Teeth_", "_Tongue")
# Blender stores custom normals compressed (16 bit per component in a local frame), so the two
# sides of a cut can differ by a few hundredths of a degree; a seam shows from a few degrees.
NORMAL_TOLERANCE = 0.005  # length of the difference vector, about 0.3 degrees


# ------------------------------------------------------------------------------------- mesh

def _analyse(obj):
    me = obj.data
    bm = bmesh.new()
    bm.from_mesh(me)
    bm.edges.ensure_lookup_table()
    open_edges = sum(1 for e in bm.edges if len(e.link_faces) == 1)
    over_shared = sum(1 for e in bm.edges if len(e.link_faces) > 2)
    wire = sum(1 for e in bm.edges if len(e.link_faces) == 0)
    part = [0] * len(me.polygons)
    if "part" in me.attributes:
        me.attributes["part"].data.foreach_get("value", part)
    tiny = sum(1 for f in bm.faces if f.calc_area() < (TINY_AREA_HIDDEN if part[f.index] in HIDDEN_PARTS else TINY_AREA))
    seen, pieces = set(), 0
    for v in bm.verts:
        if v.index in seen:
            continue
        pieces += 1
        stack = [v]
        seen.add(v.index)
        while stack:
            w = stack.pop()
            for e in w.link_edges:
                o = e.other_vert(w)
                if o.index not in seen:
                    seen.add(o.index)
                    stack.append(o)
    bm.free()
    me.calc_loop_triangles()
    co = [v.co for v in me.vertices]
    min_angle, slivers = 180.0, 0
    for t in me.loop_triangles:
        a, b, c = (co[i] for i in t.vertices)
        angles = []
        for p, q, r in ((a, b, c), (b, c, a), (c, a, b)):
            u, w = q - p, r - p
            angles.append(0.0 if u.length < 1e-12 or w.length < 1e-12 else math.degrees(u.angle(w)))
        m = min(angles)
        min_angle = min(min_angle, m)
        slivers += m < SLIVER_ANGLE
    kd = KDTree(len(co))
    for i, v in enumerate(co):
        kd.insert(v, i)
    kd.balance()
    mirror = max(kd.find(Vector((-v.x, v.y, v.z)))[2] for v in co)
    return {
        "tris": len(me.loop_triangles), "open": open_edges, "over_shared": over_shared, "wire": wire,
        "tiny": tiny, "slivers": slivers, "min_angle": min_angle, "mirror": mirror, "pieces": pieces,
        "sharp": sum(1 for e in me.edges if e.use_edge_sharp),
    }


def mesh(path):
    """Checks the textured bodies with their eyeballs, teeth and tongue."""
    bpy.ops.wm.open_mainfile(filepath=path)
    lines = [f"{'object':<26}{'tris':>6}{'open':>6}{'>2':>4}{'tiny':>6}{'sliv':>6}{'minang':>8}"
             f"{'mirror mm':>11}{'pieces':>7}{'sharp':>6}"]
    failures = []
    results = {}
    for obj in sorted((o for o in bpy.data.objects if o.type == "MESH"), key=lambda o: (o.parent is not None, o.name)):
        r = _analyse(obj)
        results[obj.name] = r
        lines.append(f"{obj.name:<26}{r['tris']:>6}{r['open']:>6}{r['over_shared']:>4}{r['tiny']:>6}{r['slivers']:>6}"
                     f"{r['min_angle']:>8.1f}{r['mirror'] * 1000:>11.4f}{r['pieces']:>7}{r['sharp']:>6}")
        if obj.name in BODIES:
            for key in ("open", "over_shared", "wire", "tiny", "slivers"):
                if r[key] > 0:
                    failures.append(f"{obj.name}: {key} = {r[key]}")
            if r["mirror"] > 1e-6:
                failures.append(f"{obj.name}: mirror error {r['mirror']}")
            if r["pieces"] != 1:
                failures.append(f"{obj.name}: {r['pieces']} loose pieces")
        else:
            open_bad = r["open"] and not any(k in obj.name for k in OPEN_ALLOWED)
            if open_bad or r["over_shared"] or r["tiny"] or r["slivers"] or r["mirror"] > 1e-5:
                failures.append(f"{obj.name}: open {r['open']}, >2 {r['over_shared']}, tiny {r['tiny']}, "
                                f"slivers {r['slivers']}, mirror {r['mirror']}")
    a, b = (bpy.data.objects[n].data for n in BODIES)
    same = [tuple(p.vertices) for p in a.polygons] == [tuple(p.vertices) for p in b.polygons]
    lines += ["", f"Identical topology Male_Body / Female_Body: {'yes' if same else 'NO'}"]
    if not same:
        failures.append("bodies differ in topology")
    for body in BODIES:
        extras = sorted((o for o in bpy.data.objects if o.get("body") == body), key=lambda o: o.name)
        total = results[body]["tris"] + sum(results[o.name]["tris"] for o in extras)
        lines.append(f"{body} character: {total} triangles ({body} + {', '.join(o.name for o in extras)})")
        if total > MAX_TRIS:
            failures.append(f"{body}: character has {total} triangles (max {MAX_TRIS})")
    return lines, failures


# ------------------------------------------------------------------------------------ seams

def _texture_of(mat):
    if mat is None or not mat.use_nodes:
        return None
    return next((n.image for n in mat.node_tree.nodes if n.type == "TEX_IMAGE" and n.image is not None), None)


def _sampler(image):
    w, h = image.size
    px = np.empty(w * h * 4, np.float32)
    image.pixels.foreach_get(px)
    px = px.reshape(h, w, 4)[..., :3]

    def sample(uv):
        x = uv[0] * w - 0.5
        y = uv[1] * h - 0.5
        x0, y0 = int(np.floor(x)), int(np.floor(y))
        fx, fy = x - x0, y - y0
        x0, x1 = np.clip([x0, x0 + 1], 0, w - 1)
        y0, y1 = np.clip([y0, y0 + 1], 0, h - 1)
        top = px[y0, x0] * (1 - fx) + px[y0, x1] * fx
        bot = px[y1, x0] * (1 - fx) + px[y1, x1] * fx
        return top * (1 - fy) + bot * fy
    return sample


def _seam_differences(obj):
    me = obj.data
    uv = me.uv_layers["UVMap"].data
    samplers = [_sampler(_texture_of(m)) if _texture_of(m) else None for m in me.materials]
    edge_loops = {}
    for p in me.polygons:
        n = len(p.loop_indices)
        for k in range(n):
            la, lb = p.loop_indices[k], p.loop_indices[(k + 1) % n]
            a, b = me.loops[la].vertex_index, me.loops[lb].vertex_index
            edge_loops.setdefault(tuple(sorted((a, b))), []).append((p, {a: la, b: lb}))
    diffs = []
    worst = (0.0, None)
    for e in me.edges:
        if not e.use_seam:
            continue
        key = tuple(sorted(e.vertices))
        sides = edge_loops.get(key, [])
        if len(sides) != 2:
            continue
        a, b = key
        for t in (0.15, 0.5, 0.85):
            cols = []
            for p, loops in sides:
                ua, ub = uv[loops[a]].uv, uv[loops[b]].uv
                cols.append(samplers[p.material_index]((ua[0] + (ub[0] - ua[0]) * t, ua[1] + (ub[1] - ua[1]) * t)))
            d = float(np.abs(cols[0] - cols[1]).max() * 255)
            diffs.append(d)
            if d > worst[0]:
                mid = (me.vertices[a].co + me.vertices[b].co) / 2
                worst = (d, tuple(round(c, 3) for c in mid))
    return np.array(diffs), worst


def seams(path):
    """Reports the texture colour difference across the UV seams of both bodies."""
    bpy.ops.wm.open_mainfile(filepath=path)
    lines = []
    for name in BODIES:
        diffs, worst = _seam_differences(bpy.data.objects[name])
        lines.append(f"{name}: {len(diffs)} samples on seams, mean difference {diffs.mean():.1f}, "
                     f"95th percentile {np.percentile(diffs, 95):.1f}, max {worst[0]:.1f} at {worst[1]} (sRGB 0..255)")
    return lines, []


# ------------------------------------------------------------------------------------ parts

def _key(co):
    return tuple(int(round(c * 1e5)) for c in co)


def _check_character(label, lines, failures):
    root = bpy.data.objects[label]
    parts = [c for c in root.children if c.type == "MESH" and "zone" in c]
    extras = [c for c in root.children if c.type == "MESH" and "zone" not in c]
    tris = sum(len(p.vertices) - 2 for o in root.children if o.type == "MESH" for p in o.data.polygons)
    lines.append(f"{label}: {len(parts)} body parts, {len(extras)} eyes/teeth/tongue, {tris} triangles")
    if tris > MAX_TRIS:
        failures.append(f"{label}: {tris} triangles (max {MAX_TRIS})")
    # Open edges of all parts, by position: each must be shared by exactly two parts.
    edges = {}
    normals = {}
    for o in parts:
        me = o.data
        count = {}
        for p in me.polygons:
            for k in p.edge_keys:
                count[k] = count.get(k, 0) + 1
        co = [o.matrix_world @ v.co for v in me.vertices]
        for (a, b), c in count.items():
            if c == 1:
                edges.setdefault(tuple(sorted((_key(co[a]), _key(co[b])))), []).append(o.name)
        cn = np.empty(len(me.loops) * 3, np.float32)
        me.corner_normals.foreach_get("vector", cn)
        cn = cn.reshape(-1, 3)
        boundary = {v for (a, b), c in count.items() if c == 1 for v in (a, b)}
        for li, loop in enumerate(me.loops):
            if loop.vertex_index in boundary:
                normals.setdefault(_key(co[loop.vertex_index]), []).append(cn[li])
    unmatched = [e for e, names in edges.items() if len(names) != 2 or names[0] == names[1]]
    lines.append(f"  cuts between parts: {len(edges)} edges, unmatched {len(unmatched)}")
    if unmatched:
        failures.append(f"{label}: {len(unmatched)} part edges have no neighbour")
    worst = 0.0
    for ns in normals.values():
        ns = np.array(ns)
        worst = max(worst, float(np.max(np.linalg.norm(ns - ns.mean(0), axis=1))))
    lines.append(f"  largest normal difference across a cut: {worst:.6f}")
    if worst > NORMAL_TOLERANCE:
        failures.append(f"{label}: normals differ across a cut ({worst:.4f})")
    head = next(o for o in parts if o["zone"] == "Head")
    face_faces = sum(1 for p in head.data.polygons if head.data.materials[p.material_index].name.startswith("M_Face"))
    lines.append(f"  head faces with the face material: {face_faces}")
    if face_faces == 0:
        failures.append(f"{label}: the head has no face-material faces")
    for o in sorted(parts, key=lambda o: o.name):
        t = sum(len(p.vertices) - 2 for p in o.data.polygons)
        lines.append(f"  {o.name:<20}{t:>6} tris  {', '.join(m.name for m in o.data.materials)}")
    return {o["zone"]: o for o in parts}


def parts(path):
    """Checks the organized characters and their body parts."""
    bpy.ops.wm.open_mainfile(filepath=path)
    lines, failures = [], []
    found = {label: _check_character(label, lines, failures) for label in CHARACTERS}
    differing = [zone for zone, a in found["Male"].items()
                 if zone not in found["Female"]
                 or [tuple(p.vertices) for p in a.data.polygons] != [tuple(p.vertices) for p in found["Female"][zone].data.polygons]]
    failures += [f"{zone}: male and female parts differ in topology" for zone in differing]
    lines.append(f"Identical topology of every part, male and female: {'yes' if not differing else 'NO'}")
    return lines, failures


# ---------------------------------------------------------------------------------- compare

def _snapshot(path):
    """Everything of a character file that shows or that later stages read, by name."""
    bpy.ops.wm.open_mainfile(filepath=path)
    data = {"collections": {c.name: (sorted(o.name for o in c.objects), sorted(k.name for k in c.children))
                            for c in bpy.data.collections}}
    for o in bpy.data.objects:
        entry = {"type": o.type, "parent": o.parent.name if o.parent else None,
                 "matrix": np.array(o.matrix_world).tobytes(),
                 "props": {k: repr(o[k].to_list() if hasattr(o[k], "to_list") else o[k]) for k in o.keys()}}
        if o.type == "MESH":
            me = o.data
            arrays = {}
            for name, collection, attr, width in (("co", me.vertices, "co", 3), ("uv", me.uv_layers.active.data if me.uv_layers else [], "uv", 2),
                                                  ("normals", me.corner_normals, "vector", 3)):
                values = np.empty(len(collection) * width, np.float64)
                if len(collection):
                    collection.foreach_get(attr, values)
                arrays[name] = values.tobytes()
            entry.update(arrays, faces=[tuple(p.vertices) for p in me.polygons],
                         face_flags=[(p.material_index, p.use_smooth) for p in me.polygons],
                         edge_flags=[(tuple(e.vertices), e.use_seam, e.use_edge_sharp) for e in me.edges],
                         materials=[s.material.name if s.material else None for s in o.material_slots],
                         attributes=sorted(a.name for a in me.attributes))
        data["object " + o.name] = entry
    for m in bpy.data.materials:
        nodes, links = [], []
        if m.node_tree is not None:
            for n in m.node_tree.nodes:
                values = tuple((s.identifier, repr(tuple(s.default_value) if hasattr(s.default_value, "__len__") else s.default_value))
                               for s in n.inputs if hasattr(s, "default_value") and not s.is_linked)
                settings = tuple((p, repr(getattr(n, p))) for p in ("interpolation", "extension", "projection",
                                                                    "distribution", "subsurface_method", "target")
                                 if hasattr(n, p))
                nodes.append((n.name, n.bl_idname, getattr(getattr(n, "image", None), "name", None), values, settings))
            links = [(link.from_node.name, link.from_socket.identifier, link.to_node.name, link.to_socket.identifier)
                     for link in m.node_tree.links]
        data["material " + m.name] = (sorted(nodes), sorted(links))
    for i in bpy.data.images:
        w, h = i.size
        px = np.empty(w * h * 4, np.float32)
        i.pixels.foreach_get(px)
        # Packed or not matters: the art pipeline only accepts packed textures.
        data["image " + i.name] = (tuple(i.size), i.colorspace_settings.name, i.source, i.packed_file is not None,
                                   px.tobytes())
    return data


def compare(a, b):
    """Whether two character files hold the same characters: objects, meshes, UVs, normals,
    edge flags, materials, textures and custom properties. Files Blender saved again differ in
    their bytes but not in this content."""
    first, second = _snapshot(a), _snapshot(b)
    differences = sorted(k for k in set(first) | set(second) if first.get(k) != second.get(k))
    lines = [f"{'same content' if not differences else 'different content'}: {a} and {b}"]
    lines += [f"  differs: {k}" for k in differences[:20]]
    return lines, differences
