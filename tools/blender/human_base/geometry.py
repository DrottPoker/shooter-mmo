"""Builds the base bodies Male_Body and Female_Body with identical topology, exact mirror
symmetry, eye sockets and a mouth cavity, plus per body two eyeballs, upper and lower teeth and
a tongue as separate objects (for gaze and jaw animation). Smooth shading with hard ear rims,
the UV layout (mirrored body, own face island) and the materials."""

import math

import bmesh
import bpy
import numpy as np
from mathutils import Vector

from . import assemble, proportions, references


EAR_SHARP_DEG = 55.0
EAR_MIN_X = 0.066


def material(name):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    return mat


def make_object(name, verts, faces, collection):
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata([tuple(v) for v in verts], [], [tuple(f) for f in faces])
    mesh.validate()
    obj = bpy.data.objects.new(name, mesh)
    collection.objects.link(obj)
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    bm.to_mesh(mesh)
    bm.free()
    return obj


def edge_lookup(mesh):
    return {tuple(sorted(e.vertices)): e.index for e in mesh.edges}


def mark_seams_and_sharp(obj, m):
    mesh = obj.data
    lookup = edge_lookup(mesh)
    face_region = m.face_region
    # Centre line: edges on x = 0 outside the face region, which is one unmirrored island.
    centre = {i for i, v in enumerate(mesh.vertices) if abs(v.co.x) < 1e-7}
    region_edges = set()
    for fi in face_region:
        vs = mesh.polygons[fi].vertices
        for k in range(len(vs)):
            region_edges.add(tuple(sorted((vs[k], vs[(k + 1) % len(vs)]))))
    for key, ei in lookup.items():
        e = mesh.edges[ei]
        seam = key in m.seams or (key[0] in centre and key[1] in centre and key not in region_edges)
        # Face region edges on the centre line stay joined; its border is in m.seams.
        if key in region_edges and key not in m.seams:
            seam = False
        e.use_seam = seam
    # Hard ear rims: sharp edges by angle in the ear zone.
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bm.edges.ensure_lookup_table()
    # Only edges between two faces of the ear itself (its rims), never where the ear meets
    # the head, so the skin around the ear stays smooth.
    ear_faces = {f.index for f in bm.faces
                 if all(v.index in m.ear_zone for v in f.verts) and min(abs(v.co.x) for v in f.verts) > EAR_MIN_X}
    sharp = set()
    for e in bm.edges:
        if len(e.link_faces) == 2 and all(f.index in ear_faces for f in e.link_faces):
            if math.degrees(e.calc_face_angle()) > EAR_SHARP_DEG:
                sharp.add(e.index)
    bm.free()
    for e in mesh.edges:
        e.use_edge_sharp = e.index in sharp
    for p in mesh.polygons:
        p.use_smooth = True
    return len(sharp)


def _select_faces(obj, wanted):
    bm = bmesh.from_edit_mesh(obj.data)
    for f in bm.faces:
        f.select = f.index in wanted
    bmesh.update_edit_mesh(obj.data)


def _orient_face_island(mesh, face_region, uv):
    """Turns the face island upright (chin down, forehead up, nose centred) and fits it into
    the texture with a small border."""
    loops = [li for fi in face_region for li in mesh.polygons[fi].loop_indices]
    pts = np.array([uv[li].uv[:] for li in loops])
    co = np.array([mesh.vertices[mesh.loops[li].vertex_index].co[:] for li in loops])
    # Fit a 2D similarity from 3D (x, z) to UV; its rotation tells how the island is turned.
    a = np.c_[co[:, 0], co[:, 2], np.ones(len(co))]
    sol, *_ = np.linalg.lstsq(a, pts, rcond=None)
    ux, uz = sol[0], sol[1]
    angle = math.atan2(uz[0], uz[1])  # direction of +z (up) in UV
    # The character's right side (-x) goes to the left of the image, as when facing the face.
    flip = (ux[0] * uz[1] - ux[1] * uz[0]) > 0
    c, s = math.cos(angle), math.sin(angle)
    rot = np.array([[c, -s], [s, c]])
    pts = pts @ rot.T
    if flip:
        pts[:, 0] = -pts[:, 0]
    lo, hi = pts.min(0), pts.max(0)
    size = (hi - lo).max()
    pts = (pts - (lo + hi) / 2) / size * 0.94 + 0.5
    for li, p in zip(loops, pts):
        uv[li].uv = p


BODIES = (("male", "Male_Body", 0.45), ("female", "Female_Body", -0.45))
PARTS = ("torso", "arm", "leg", "neck", "head", "hand", "foot", "socket", "mouth")
# Body zones: the pieces each character is split into, so equipment can hide them.
ZONES = ("head", "neck", "torso", "pelvis", "upper_arms", "forearms", "hands", "thighs", "shins", "feet")
# Eyeball rings: angle from the front pole (degrees); the iris edge sits at about 29 degrees.
EYE_RINGS = (14.0, 29.0, 45.0, 72.0, 108.0, 148.0)
EYE_SEGMENTS = 12
# Share of the body texture the packed right half must cover (a good pack covers about 0.6).
MIN_BODY_UV_COVER = 0.45


def eyeball_mesh(name, radius):
    """A sphere around the origin with its front pole on +Y, UVs in a polar projection around
    the front pole (the iris is a disc in the middle of the texture)."""
    verts = [(0.0, radius, 0.0)]
    for ang in EYE_RINGS:
        t = math.radians(ang)
        for k in range(EYE_SEGMENTS):
            phi = 2 * math.pi * k / EYE_SEGMENTS
            verts.append((radius * math.sin(t) * math.cos(phi), radius * math.cos(t), radius * math.sin(t) * math.sin(phi)))
    verts.append((0.0, -radius, 0.0))
    faces = []
    n = EYE_SEGMENTS
    for k in range(n):
        faces.append((0, 1 + (k + 1) % n, 1 + k))
    for r in range(len(EYE_RINGS) - 1):
        a, b = 1 + r * n, 1 + (r + 1) * n
        for k in range(n):
            faces.append((a + k, a + (k + 1) % n, b + (k + 1) % n, b + k))
    last, back = 1 + (len(EYE_RINGS) - 1) * n, len(verts) - 1
    for k in range(n):
        faces.append((last + k, last + (k + 1) % n, back))
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], faces)
    mesh.validate()
    uv = mesh.uv_layers.new(name="UVMap").data
    for p in mesh.polygons:
        centre = sum((mesh.vertices[v].co for v in p.vertices), Vector()) / len(p.vertices)
        for li in p.loop_indices:
            co = Vector(mesh.vertices[mesh.loops[li].vertex_index].co)
            if abs(co.x) < 1e-9 and abs(co.z) < 1e-9:
                co = Vector((centre.x, co.y, centre.z))  # a pole takes the direction of its face
            theta = math.acos(max(-1.0, min(1.0, co.y / radius)))
            phi = math.atan2(co.z, co.x)
            rho = theta / math.pi * 0.49
            uv[li].uv = (0.5 + rho * math.cos(phi), 0.5 + rho * math.sin(phi))
    for p in mesh.polygons:
        p.use_smooth = True
    return mesh


def arch_mesh(name, centre, half_width, depth, height, thickness, upper, segments=10):
    """A tooth row: a U-shaped band of box section, open at the back."""
    verts, faces = [], []
    for k in range(segments + 1):
        s = -1.0 + 2.0 * k / segments
        ang = s * math.pi * 0.5
        x = math.sin(ang) * half_width
        y = -(1.0 - math.cos(ang)) * depth
        out = Vector((math.sin(ang), math.cos(ang) * depth / half_width, 0.0)).normalized()
        top = height if upper else 0.0
        bot = 0.0 if upper else -height
        base = Vector((centre.x + x, centre.y + y, centre.z))
        for off, z in ((0.0, top), (0.0, bot), (-thickness, bot), (-thickness, top)):
            p = base + out * off + Vector((0.0, 0.0, z))
            verts.append(p[:])
    for k in range(segments):
        a, b = 4 * k, 4 * (k + 1)
        for j in range(4):
            faces.append((a + j, a + (j + 1) % 4, b + (j + 1) % 4, b + j))
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], faces)
    mesh.validate()
    mesh.uv_layers.new(name="UVMap")
    for p in mesh.polygons:
        p.use_smooth = True
    return mesh


def tongue_mesh(name, centre, half_width, length, height, segments=8, rings=4):
    """A flattened half ellipsoid lying on the floor of the mouth, tip forward."""
    verts, faces = [], []
    for r in range(rings + 1):
        t = r / rings
        y = centre.y - length * t
        w = half_width * math.sqrt(max(1.0 - (1.0 - t) ** 2 * 0.85, 0.15))
        for k in range(segments):
            ang = math.pi * k / (segments - 1)
            verts.append((centre.x + math.cos(ang) * w, y, centre.z + math.sin(ang) * height * (0.6 + 0.4 * t)))
    for r in range(rings):
        a, b = r * segments, (r + 1) * segments
        for k in range(segments - 1):
            faces.append((a + k, b + k, b + k + 1, a + k + 1))
    faces.append(tuple(range(segments - 1, -1, -1)))  # tip
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], faces)
    mesh.validate()
    mesh.uv_layers.new(name="UVMap")
    for p in mesh.polygons:
        p.use_smooth = True
    return mesh


def warp_object(obj, sex, surface, origin_point=None):
    """Moves an extra object's vertices through the body's proportion warp (in world space at
    the rest place), and keeps its origin at the warped origin_point."""
    me = obj.data
    co = np.array([obj.matrix_world @ v.co for v in me.vertices])
    w = proportions.warp(co, sex, surface)
    if origin_point is not None:
        obj.location = Vector(proportions.warp(np.array([origin_point[:]]), sex, surface)[0])
    # The objects are not rotated or scaled: local coordinates are offsets from the location.
    for v, p in zip(me.vertices, w):
        v.co = Vector(p) - obj.location
    me.update()


def build_extras(m, sex, label, surface, coll, x_offset):
    """Eyeballs, teeth and tongue of one body, in rest coordinates warped like the body."""
    objs = []
    eye_mat = material(f"M_Eye_{label}")
    for c, r in m.eyes:
        # The asset faces +Y, so the character's right eye is at +x.
        name = f"{label}_Eye_{'R' if c.x > 0 else 'L'}"
        mesh = eyeball_mesh(name, r)
        mesh.materials.append(eye_mat)
        obj = bpy.data.objects.new(name, mesh)
        obj.location = c
        coll.objects.link(obj)
        bpy.context.view_layer.update()
        warp_object(obj, sex, surface, c)
        objs.append(obj)
    mouth = m.mouth
    c = Vector(mouth["centre"])
    half = mouth["width"] * 0.82
    teeth_mat = material(f"M_Teeth_{label}")
    tongue_mat = material(f"M_Tongue_{label}")
    front = Vector((0.0, mouth["front_y"] - 0.0075, c.z))
    for name, upper, z in ((f"{label}_Teeth_Upper", True, c.z + 0.0012), (f"{label}_Teeth_Lower", False, c.z - 0.0012)):
        mesh = arch_mesh(name, Vector((0.0, front.y, z)), half, 0.026, 0.0085, 0.006, upper)
        mesh.materials.append(teeth_mat)
        obj = bpy.data.objects.new(name, mesh)
        coll.objects.link(obj)
        warp_object(obj, sex, surface)
        objs.append(obj)
    mesh = tongue_mesh(f"{label}_Tongue", Vector((0.0, front.y - 0.008, c.z - 0.010)), half * 0.78, 0.034, 0.008)
    mesh.materials.append(tongue_mat)
    obj = bpy.data.objects.new(f"{label}_Tongue", mesh)
    coll.objects.link(obj)
    warp_object(obj, sex, surface)
    objs.append(obj)
    for o in objs:
        o.location.x += x_offset
        o["body"] = f"{label}_Body"
    return objs


def unwrap(obj, m, partner):
    """UV layout: the right half of the body is unwrapped along its seams (hands and feet cut
    like a glove, the ears as their own islands) and packed; the face region is its own layout.
    Left faces copy the UVs of their mirror partners."""
    mesh = obj.data
    if not mesh.uv_layers:
        mesh.uv_layers.new(name="UVMap")
    part = [0] * len(mesh.polygons)
    mesh.attributes["part"].data.foreach_get("value", part)
    centroids = [sum((mesh.vertices[v].co for v in p.vertices), Vector()) / len(p.vertices) for p in mesh.polygons]
    face = set(m.face_region)
    right = {p.index for p in mesh.polygons if p.index not in face and centroids[p.index].x > 1e-6}
    left = {p.index for p in mesh.polygons if p.index not in face and centroids[p.index].x < -1e-6}
    assert len(right) + len(left) + len(face) == len(mesh.polygons), "a face straddles the centre"
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    uv = mesh.uv_layers["UVMap"].data

    def loops_of(group):
        return [li for fi in group for li in mesh.polygons[fi].loop_indices]

    def park(group, offset):
        # The packer keeps clear of every other UV in the tile, so the faces that are not being
        # packed wait in another tile meanwhile.
        for li in loops_of(group):
            uv[li].uv = (uv[li].uv[0] + offset, uv[li].uv[1])

    def unwrap_and_pack(group, margin):
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.select_mode(type="FACE")
        _select_faces(obj, group)
        bpy.ops.uv.unwrap(method="ANGLE_BASED", margin=0.0)
        # With sync selection, selecting all UVs selects every face, and the pack would mix the
        # rest of the body into this layout. Without it the UV operators see only the group.
        tool = bpy.context.scene.tool_settings
        sync = tool.use_uv_select_sync
        tool.use_uv_select_sync = False
        bpy.ops.uv.select_all(action="SELECT")
        bpy.ops.uv.average_islands_scale()
        bpy.ops.uv.pack_islands(rotate=True, margin_method="FRACTION", margin=margin)
        tool.use_uv_select_sync = sync
        bpy.ops.object.mode_set(mode="OBJECT")

    for li in loops_of(face | left):
        uv[li].uv = (10.5, 0.5)
    # Islands are packed far enough apart that the bake margins of neighbours never meet.
    unwrap_and_pack(right, 0.012)
    uv = mesh.uv_layers["UVMap"].data
    park(right, 20.0)
    unwrap_and_pack(face, 0.0)
    uv = mesh.uv_layers["UVMap"].data
    park(right, -20.0)
    # Fill the square with the body layout.
    loops = loops_of(right)
    pts = np.array([uv[li].uv[:] for li in loops])
    lo, hi = pts.min(0), pts.max(0)
    s = 0.996 / (hi - lo).max()
    for li, p in zip(loops, pts):
        uv[li].uv = (p - lo) * s + 0.002
    cover = 0.0
    for fi in right:
        q = np.array([uv[li].uv[:] for li in mesh.polygons[fi].loop_indices])
        cover += 0.5 * abs(np.dot(q[:, 0], np.roll(q[:, 1], -1)) - np.dot(q[:, 1], np.roll(q[:, 0], -1)))
    assert cover > MIN_BODY_UV_COVER, f"{obj.name}: the body layout covers only {cover:.2f} of its texture"
    _orient_face_island(mesh, face, uv)
    by_verts = {frozenset(p.vertices): p.index for p in mesh.polygons}
    for fi in left:
        p = mesh.polygons[fi]
        q = mesh.polygons[by_verts[frozenset(partner[v] for v in p.vertices)]]
        quv = {mesh.loops[li].vertex_index: uv[li].uv.copy() for li in q.loop_indices}
        for li in p.loop_indices:
            uv[li].uv = quv[partner[mesh.loops[li].vertex_index]]
    return cover


def build(refs_path, out_path):
    """Builds both bodies with their eyeballs, teeth and tongue from the reference file and saves
    them side by side (male at +x, female at -x)."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    R = references.References(refs_path)
    coll = bpy.data.collections.new("Characters")
    bpy.context.scene.collection.children.link(coll)
    built = []
    layout = None
    plan = {}
    for sex, name, x in BODIES:
        m = assemble.assemble(R, sex, plan=plan)
        if layout is None:
            layout = (m.seams, m.face_region, m.ear_zone)
        else:
            m.seams, m.face_region, m.ear_zone = layout
        surface = R.body(sex)
        rest = np.array([v[:] for v in m.verts])
        warped = proportions.warp(rest, sex, surface)
        partner, worst = assemble.mirror_partners([Vector(v) for v in warped])
        assert worst < 1e-6, f"{name}: not mirror symmetric ({worst})"
        obj = make_object(name, warped, m.faces, coll)
        label = sex.capitalize()
        obj.data.materials.append(material(f"M_Body_{label}"))
        obj.data.materials.append(material(f"M_Face_{label}"))
        for fi in m.face_region:
            obj.data.polygons[fi].material_index = 1
        sharp = mark_seams_and_sharp(obj, m)
        mesh = obj.data
        attr = mesh.attributes.new("rest_position", "FLOAT_VECTOR", "POINT")
        attr.data.foreach_set("vector", np.asarray(rest, np.float32).ravel())
        part = mesh.attributes.new("part", "INT", "FACE")
        part.data.foreach_set("value", np.array([PARTS.index(p) for p in m.part], np.int32))
        assert len(m.zone) == len(m.faces), "every face needs a zone"
        zone = mesh.attributes.new("zone", "INT", "FACE")
        zone.data.foreach_set("value", np.array([ZONES.index(z) for z in m.zone], np.int32))
        uv_cover = unwrap(obj, m, partner)
        for key, verts in m.landmarks.items():
            obj["lm_" + key] = [float(c) for v in verts for c in rest[v]]
            obj["lmw_" + key] = [float(c) for v in verts for c in warped[v]]
        for k, (c, r) in enumerate(m.eyes):
            obj[f"eye_{k}"] = [float(v) for v in c[:]] + [float(r)]
        obj["mouth"] = [float(v) for v in m.mouth["centre"][:]] + [float(m.mouth["width"]), float(m.mouth["front_y"])]
        extras = build_extras(m, sex, label, surface, coll, x)
        obj.location.x = x
        tris = sum(len(p.vertices) - 2 for p in obj.data.polygons)
        extra_tris = sum(len(p.vertices) - 2 for o in extras for p in o.data.polygons)
        print(f"BUILT {name}: verts {len(obj.data.vertices)} faces {len(obj.data.polygons)} tris {tris} "
              f"(+{extra_tris} eyes, teeth, tongue = {tris + extra_tris}) seams {sum(e.use_seam for e in obj.data.edges)} sharp {sharp} "
              f"body uv cover {uv_cover:.2f}")
        built.append(obj)
    a, b = (o.data for o in built)
    assert [tuple(p.vertices) for p in a.polygons] == [tuple(p.vertices) for p in b.polygons], "topology differs"
    bpy.ops.wm.save_as_mainfile(filepath=out_path, compress=True)
    print("SAVED", out_path)
