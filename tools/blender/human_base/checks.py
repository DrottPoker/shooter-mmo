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
  by an edge of a neighbouring part), and the normals agree across the cuts.
- rig: a rigged character against the model it was rigged from: the meshes at rest are the
  model's meshes, the art pipeline's rig rules hold, and the weights are mirror symmetric.
- animation: the clips of an animated character follow the art pipeline's animation rules, and a
  foot on the ground keeps its place when the character moves at the clip's speed."""

import math

import bmesh
import bpy
import numpy as np
from bpy_extras import anim_utils
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
REST_POSITION_TOLERANCE = 1e-6  # m, the rigged mesh at rest against the model
# A foot point is on the ground while it is at most this much above its height in the rest pose;
# on the ground it may slide this much (m), measured while the character moves at the clip's speed.
# The heel point sits this far behind the ankle.
GROUND = 0.004
SLIP_TOLERANCE = 0.015
HEEL_BEHIND = 0.045


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


# -------------------------------------------------------------------------------------- rig

def _rest_meshes(label):
    """Evaluated meshes of a character's children by name: positions, corner normals and UVs."""
    depsgraph = bpy.context.evaluated_depsgraph_get()
    found = {}
    for o in bpy.data.objects[label].children:
        if o.type != "MESH":
            continue
        evaluated = o.evaluated_get(depsgraph)
        me = evaluated.to_mesh()
        try:
            co = np.array([o.matrix_world @ v.co for v in me.vertices])
            normals = np.empty(len(me.loops) * 3)
            me.corner_normals.foreach_get("vector", normals)
            uv = np.empty(len(me.loops) * 2)
            if me.uv_layers.active:
                me.uv_layers.active.data.foreach_get("uv", uv)
            found[o.name] = (co, normals, uv, [tuple(p.vertices) for p in me.polygons])
        finally:
            evaluated.to_mesh_clear()
    return found


def _mirror_name(name):
    return name[:-2] + {"_L": "_R", "_R": "_L"}[name[-2:]] if name[-2:] in ("_L", "_R") else name


def rig(model_path, rigged_path, label, skeleton_path):
    """Checks a rigged character against the model file it was rigged from."""
    from art_pipeline import skeleton as skeleton_definition
    from art_pipeline import skinned
    from . import rig as rigging

    lines, failures = [], []
    bpy.ops.wm.open_mainfile(filepath=model_path)
    rigging.normalize(label)
    before = _rest_meshes(label)
    bpy.ops.wm.open_mainfile(filepath=rigged_path)
    after = _rest_meshes(label)
    if sorted(before) != sorted(after):
        failures.append(f"{label}: the rigged meshes {sorted(after)} are not the model's {sorted(before)}")
    moved = turned = 0.0
    for name in sorted(set(before) & set(after)):
        (co0, n0, uv0, f0), (co1, n1, uv1, f1) = before[name], after[name]
        if f0 != f1 or len(co0) != len(co1) or not np.array_equal(uv0, uv1):
            failures.append(f"{name}: the rig changed the faces or UVs of the model")
            continue
        moved = max(moved, float(np.abs(co0 - co1).max()))
        turned = max(turned, float(np.abs(n0 - n1).max()))
    lines.append(f"{label}: at rest, largest change of a position {moved:.2e} m and of a normal {turned:.2e}")
    if moved > REST_POSITION_TOLERANCE or turned > NORMAL_TOLERANCE:
        failures.append(f"{label}: the rig changes the model at rest ({moved:.2e} m, normals {turned:.2e})")

    skeleton = skeleton_definition.load(skeleton_path)
    exported = [o for o in bpy.data.objects if o == bpy.data.objects[label] or o.parent == bpy.data.objects[label]]
    failures += [f"{label}: {e}" for e in skinned.rig_errors(exported, [bpy.data.objects[label]], skeleton)]

    # Weights: influences per vertex and mirror symmetry.
    influences = {}
    asymmetry = 0.0
    for o in bpy.data.objects[label].children:
        if o.type != "MESH" or "zone" not in o:
            continue
        names = {g.index: g.name for g in o.vertex_groups}
        keyed = {}
        for v in o.data.vertices:
            w = {names[g.group]: g.weight for g in v.groups if g.weight > 0}
            influences[len(w)] = influences.get(len(w), 0) + 1
            keyed[_key(v.co)] = w
        for k, w in keyed.items():
            m = keyed.get((-k[0], k[1], k[2]))
            if m is None:
                failures.append(f"{o.name}: a vertex has no mirror partner")
                break
            mirrored = {_mirror_name(b): x for b, x in m.items()}
            for b in set(w) | set(mirrored):
                asymmetry = max(asymmetry, abs(w.get(b, 0.0) - mirrored.get(b, 0.0)))
    lines.append(f"{label}: vertices by bone count {dict(sorted(influences.items()))}")
    lines.append(f"{label}: largest weight difference to the mirror side {asymmetry:.2e}")
    if asymmetry > 1e-4:
        failures.append(f"{label}: the weights are not mirror symmetric ({asymmetry:.2e})")
    bones = bpy.data.objects[label].data.bones
    lines.append(f"{label}: {len(bones)} bones, {sum(b.use_deform for b in bones)} deforming")
    return lines, failures


# -------------------------------------------------------------------------------- animation

def animation(path, label, skeleton_path):
    """Checks the clips of an animated character: the art pipeline's animation rules, and that a
    foot on the ground keeps its place when the character moves at the clip's speed."""
    from mathutils import Vector

    from art_pipeline import skeleton as skeleton_definition
    from art_pipeline import skinned
    from . import animate

    bpy.ops.wm.open_mainfile(filepath=path)
    lines, failures = [], []
    arm = bpy.data.objects[label]
    skeleton = skeleton_definition.load(skeleton_path)
    failures += [f"{label}: {e}" for e in skinned.animation_errors(arm, list(animate.CLIPS), skeleton)]
    scene = bpy.context.scene
    arm.animation_data_create()
    heels, ground = {}, {}
    for side in ("R", "L"):
        bone = arm.data.bones[f"Foot_{side}"]
        ankle = bone.head_local
        heels[side] = bone.matrix_local.inverted() @ Vector((ankle.x, ankle.y - HEEL_BEHIND, 0.012))
        ground[(side, "heel")] = 0.012 + GROUND
        ground[(side, "ball")] = arm.data.bones[f"Toes_{side}"].head_local.z + GROUND
    for name, speed in animate.CLIPS.items():
        action = bpy.data.actions.get(name)
        if action is None:
            continue
        arm.animation_data.action = action
        frames = int(action.frame_range[1])
        tracks = {}
        hips = []
        for f in range(frames + 1):
            scene.frame_set(f)
            travelled = speed * f / animate.FPS
            hips.append((arm.matrix_world @ arm.pose.bones["Hips"].head).z)
            for side in ("R", "L"):
                heel = arm.pose.bones[f"Foot_{side}"].matrix @ heels[side]
                for point, where in (("ball", arm.pose.bones[f"Toes_{side}"].head), ("heel", heel)):
                    p = arm.matrix_world @ where
                    tracks.setdefault((side, point), []).append((p.z < ground[(side, point)], p.y + travelled))
        slip = 0.0
        for track in tracks.values():
            run = []
            for on_ground, y in track + [(False, 0.0)]:
                if on_ground:
                    run.append(y)
                    continue
                if len(run) > 2:
                    slip = max(slip, max(run) - min(run))
                run = []
        lines.append(f"{label} {name}: {frames} frames, {speed:.1f} m/s, hips {min(hips):.3f} to {max(hips):.3f} m, "
                     f"largest slip of a foot on the ground {slip * 1000:.1f} mm")
        if slip > SLIP_TOLERANCE:
            failures.append(f"{label} {name}: a foot slides {slip * 1000:.1f} mm on the ground")
    arm.animation_data.action = None
    return lines, failures


# ---------------------------------------------------------------------------------- compare

def _mesh_snapshot(me):
    arrays = {}
    for name, collection, attr, width in (("co", me.vertices, "co", 3),
                                          ("uv", me.uv_layers.active.data if me.uv_layers.active else [], "uv", 2),
                                          ("normals", me.corner_normals, "vector", 3)):
        values = np.empty(len(collection) * width, np.float64)
        if len(collection):
            collection.foreach_get(attr, values)
        arrays[name] = values.tobytes()
    return dict(arrays, faces=[tuple(p.vertices) for p in me.polygons],
                face_flags=[(p.material_index, p.use_smooth) for p in me.polygons],
                edge_flags=[(tuple(e.vertices), e.use_seam, e.use_edge_sharp) for e in me.edges],
                uv_maps=[(u.name, u.active, u.active_render) for u in me.uv_layers],
                attributes=sorted(a.name for a in me.attributes))


def _snapshot(path):
    """Everything of a character file that shows or that later stages read, by name: also the
    armature's bones and pose, the skin weights, and the animation clips."""
    bpy.ops.wm.open_mainfile(filepath=path)
    data = {"collections": {c.name: (sorted(o.name for o in c.objects), sorted(k.name for k in c.children))
                            for c in bpy.data.collections}}
    depsgraph = bpy.context.evaluated_depsgraph_get()
    for o in bpy.data.objects:
        entry = {"type": o.type, "data": o.data.name if o.data else None, "parent": o.parent.name if o.parent else None,
                 "matrix": np.array(o.evaluated_get(depsgraph).matrix_world).tobytes(),
                 "constraints": [(c.type, c.name, c.enabled) for c in o.constraints],
                 "visibility": (o.hide_viewport, o.hide_render, o.hide_get()),
                 "modifiers": [(m.type, m.name, m.show_viewport, m.show_render) for m in o.modifiers],
                 "props": {k: repr(o[k].to_list() if hasattr(o[k], "to_list") else o[k]) for k in o.keys()}}
        if o.type == "MESH":
            # The evaluated mesh, with modifiers and shape keys applied, as validation and export see it.
            evaluated = o.evaluated_get(depsgraph)
            me = evaluated.to_mesh()
            try:
                entry.update(_mesh_snapshot(me), materials=[s.material.name if s.material else None for s in o.material_slots])
            finally:
                evaluated.to_mesh_clear()
            names = [g.name for g in o.vertex_groups]
            entry["weights"] = (names, [tuple((names[g.group], g.weight) for g in v.groups) for v in o.data.vertices],
                                [(m.type, getattr(m, "object", None) and m.object.name,
                                  getattr(m, "use_deform_preserve_volume", None)) for m in o.modifiers])
        if o.type == "ARMATURE":
            entry["bones"] = [(b.name, b.parent.name if b.parent else None, b.use_deform, b.use_connect,
                               np.array(b.matrix_local).tobytes(), b.length, sorted(c.name for c in b.collections))
                              for b in o.data.bones]
            entry["pose"] = [(pb.name, pb.rotation_mode, tuple(pb.location), tuple(pb.rotation_quaternion), tuple(pb.scale))
                             for pb in o.pose.bones]
            entry["armature"] = (o.data.display_type, o.data.pose_position, o.show_in_front)
            ad = o.animation_data
            entry["animation"] = None if ad is None else (ad.action.name if ad.action else None, len(ad.nla_tracks))
        data["object " + o.name] = entry
    for a in bpy.data.actions:
        curves = []
        for slot in a.slots:
            channelbag = anim_utils.action_get_channelbag_for_slot(a, slot)
            for fc in (channelbag.fcurves if channelbag else []):
                points = np.empty(len(fc.keyframe_points) * 6)
                fc.keyframe_points.foreach_get("co", points[:len(fc.keyframe_points) * 2])
                fc.keyframe_points.foreach_get("handle_left", points[len(fc.keyframe_points) * 2:len(fc.keyframe_points) * 4])
                fc.keyframe_points.foreach_get("handle_right", points[len(fc.keyframe_points) * 4:])
                curves.append((slot.identifier, fc.data_path, fc.array_index, points.tobytes(),
                               [k.interpolation for k in fc.keyframe_points], fc.extrapolation))
        data["action " + a.name] = (tuple(a.frame_range), a.use_frame_range, a.use_fake_user,
                                    {k: repr(a[k]) for k in a.keys()}, sorted(curves))
    scene = bpy.context.scene
    data["scene"] = (scene.render.fps, scene.render.fps_base, scene.frame_start, scene.frame_end)
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
