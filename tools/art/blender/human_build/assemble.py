"""Writes the asset files of the human bodies from their built nets, at the model stage.

    blender --background --factory-startup --disable-autoexec \
        --python tools/art/blender/human_build/assemble.py -- <work folder> <art/Characters> <suffix>

The work folder holds reference.blend and the work files of build.py for both bodies. Each body's
file goes to <art/Characters>/Human_<Label><suffix>/Human_<Label><suffix>.blend, as the body files
of docs/ART_PIPELINE.md are laid out, without a rig: the root <Label>, an empty at the origin; the
editable body <Label>_Body in the Source collection, with its zones, its hidden parts, its seams,
and its UV map; its body parts, the eyeballs, the teeth, and the tongue in the Export collection;
and the reference mesh in the Reference collection.

Both bodies are written in one run, because they must be the same net: every face is cut into
triangles from the same corner on both, the corner that leaves the better triangles on the worse
of the two. Where that cut would still leave a sliver on a body, the face's vertices are evened
out on that body first, each with its mirror image.

The UV map is unwrapped island by island along the seams the cage names, never projected. Every
island is turned upright, brought to one texel density, and laid out by hand-written rules: the
skin of the body on an image twice as wide as high, the head on a square image of its own. No two
islands share texels and no island is mirrored, so paint can differ between the sides."""

import math
import os
import sys

import bmesh
import bpy
import numpy as np

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from art_pipeline import bodies  # noqa: E402
from art_pipeline.scene import EXPORT_COLLECTION  # noqa: E402

LABELS = ("Male", "Female")
# A triangle with a corner under this many degrees is a sliver, as the body checks count them.
SLIVER_ANGLE = 5.0
REFERENCE_COLLECTION = "Reference"
# The islands of the head, the faces of the zone Head, lie on the image of the face material, every
# other one on the body's.
HEAD_ZONE = bodies.ZONES.index("Head")
# The body's image is this many times as wide as high; the head's is square.
BODY_ASPECT, HEAD_ASPECT = 2.0, 1.0
# Islands lie this far apart, as a share of the image's height, and this far from its edge.
GAP = 0.006
# The flat colours of the materials until the skin is painted, as sRGB, and their roughness.
MATERIALS = {"Body": ("#C9A084", 0.62), "Face": ("#C9A084", 0.55), "Eye": ("#E9E6E0", 0.12),
             "Teeth": ("#DBD1B8", 0.35), "Tongue": ("#9E4C4F", 0.45)}
EXTRAS = (("Eye_R", "Eye"), ("Eye_L", "Eye"), ("Teeth_Upper", "Teeth"), ("Teeth_Lower", "Teeth"), ("Tongue", "Tongue"))
# Which way is up on the image for an island, by the start of its name: the body's height, the
# way from the wrist to the fingertips, or the way from the heel to the toes.
UPRIGHT = {"hand": "tips", "foot": "toes"}


def _linear(hex_colour):
    out = []
    for k in (1, 3, 5):
        c = int(hex_colour[k:k + 2], 16) / 255.0
        out.append(c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4)
    return (*out, 1.0)


def _material(part, label):
    colour, roughness = MATERIALS[part]
    material = bpy.data.materials.new(f"M_{part}_{label}")
    material.use_nodes = True
    bsdf = next(n for n in material.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = _linear(colour)
    bsdf.inputs["Roughness"].default_value = roughness
    bsdf.inputs["Metallic"].default_value = 0.0
    return material


def _load(path, names):
    with bpy.data.libraries.load(path) as (source, target):
        missing = [n for n in names if n not in source.objects]
        if missing:
            raise RuntimeError(f"{path} has no objects {missing}; build the body first")
        target.objects = list(names)
    return dict(zip(names, target.objects))


def _net(path, label):
    obj = _load(path, [f"Net_{label}"])[f"Net_{label}"]
    mesh = obj.data
    points = np.array([tuple(v.co) for v in mesh.vertices])
    faces = [tuple(p.vertices) for p in mesh.polygons]
    values = {name: [d.value for d in mesh.attributes[name].data] for name in ("zone", "part", "island")}
    names = list(mesh["islands"])
    seams = {tuple(sorted(e.vertices)) for e in mesh.edges if e.use_seam}
    pinned = np.array([d.value for d in mesh.attributes["pinned"].data], bool)
    # Every vertex's mirror image, itself on the mirror plane: the net is an exact mirror image.
    place = {tuple(np.round(p, 7)): v for v, p in enumerate(points)}
    twin = np.array([place[tuple(np.round(p * (-1.0, 1.0, 1.0), 7))] for p in points])
    return {"points": points, "faces": faces, "zone": values["zone"], "part": values["part"],
            "island": [names[i] for i in values["island"]], "seams": seams, "pinned": pinned, "twin": twin}


def _smallest_corner(points, a, b, c):
    out = 180.0
    for p, q, r in ((a, b, c), (b, c, a), (c, a, b)):
        u, w = points[q] - points[p], points[r] - points[p]
        lengths = np.linalg.norm(u) * np.linalg.norm(w)
        out = min(out, 0.0 if lengths < 1e-18 else math.degrees(math.acos(float(np.clip(u @ w / lengths, -1.0, 1.0)))))
    return out


def _cut_score(points, a, b, c, d):
    """How well a quad is cut from a to c: the smallest corner of its two triangles in degrees, or
    -1 when the cut folds the face."""
    first = np.cross(points[b] - points[a], points[c] - points[a])
    second = np.cross(points[c] - points[a], points[d] - points[a])
    if first @ second <= 0.0:
        return -1.0
    return min(_smallest_corner(points, a, b, c), _smallest_corner(points, a, c, d))


def mend(nets, limit=SLIVER_ANGLE + 1.0, rounds=12, rate=0.5):
    """Moves the vertices of the faces that are slivers on a body whichever way the two bodies'
    shared cut goes: each such vertex part of the way toward the middle of its neighbours, on that
    body, until no such face is left. A body's own build leaves no face that is a sliver cut its
    best way, but the best way may differ between the bodies. Pinned vertices stay."""
    faces = nets[LABELS[0]]["faces"]
    beside = {}
    for face in faces:
        for a, b in zip(face, face[1:] + face[:1]):
            beside.setdefault(a, set()).add(b)
            beside.setdefault(b, set()).add(a)
    for _ in range(rounds):
        cut = cut_faces(nets)
        moved = False
        for net in nets.values():
            points = net["points"]
            loose = {v for face in cut if len(face) == 4 and _cut_score(points, *face) < limit
                     for v in face if not net["pinned"][v]}
            # A vertex moves with its mirror image, and both from where the round found them, so
            # the two sides stay mirror images.
            loose = sorted(loose | {int(net["twin"][v]) for v in loose})
            fresh = {v: points[v] + rate * (points[sorted(beside[v])].mean(0) - points[v]) for v in loose}
            for v, point in fresh.items():
                points[v] = point
            moved = moved or bool(loose)
        if not moved:
            break
    left = sum(1 for face in cut_faces(nets) if len(face) == 4
               for net in nets.values() if _cut_score(net["points"], *face) < SLIVER_ANGLE)
    if left:
        raise RuntimeError(f"{left} faces stay slivers on a body whichever way they are cut for both")


def cut_faces(nets):
    """Every face starting at the corner from which it is best cut into triangles on both bodies:
    Blender cuts a quad from its first corner to its third."""
    first = nets[LABELS[0]]["faces"]
    if any(net["faces"] != first for net in nets.values()):
        raise RuntimeError("the bodies differ in topology")
    out = []
    for face in first:
        if len(face) != 4:
            out.append(face)
            continue
        # A cut that folds the face is worse than any sliver.
        scores = [min(_cut_score(net["points"], *(face[shift:] + face[:shift])) for net in nets.values()) for shift in (0, 1)]
        out.append(face[1:] + face[:1] if scores[1] > scores[0] else face)
    return out


# ------------------------------------------------------------------------------------- the UV map

def _island_frames(points, faces, islands):
    """Per island: its faces, and the direction in the body that points up on the image."""
    by_name = {}
    for index, name in enumerate(islands):
        by_name.setdefault(name, []).append(index)
    frames = {}
    for name, members in by_name.items():
        cloud = points[sorted({v for f in members for v in faces[f]})]
        kind = next((way for start, way in UPRIGHT.items() if name.startswith(start)), "height")
        if kind == "height":
            up = np.array([0.0, 0.0, 1.0])
        else:
            centred = cloud - cloud.mean(0)
            _values, vectors = np.linalg.eigh(centred.T @ centred)
            up = vectors[:, -1]
            # Fingertips lie below the wrist, and toes in front of the heel.
            if (kind == "tips" and up[2] > 0) or (kind == "toes" and up[1] < 0):
                up = -up
        frames[name] = (members, up)
    return frames


def lay_out(sizes, aspect, gap):
    """Places boxes of (width, height) in an image aspect times as wide as high, all at the
    largest scale at which they fit: the largest box first, each at the lowest free place and
    there as far left as it goes, on the outline of the boxes placed before it. Returns the scale
    and the lower left corner of every box, in units of the image's height."""
    order = sorted(range(len(sizes)), key=lambda i: -sizes[i][0] * sizes[i][1])

    def place(scale):
        spots = [None] * len(sizes)
        # The outline of what is placed: stretches of (left, right, height), left to right.
        outline = [(0.0, aspect, 0.0)]
        for i in order:
            w, h = sizes[i][0] * scale + gap, sizes[i][1] * scale + gap
            best = None
            for start in range(len(outline)):
                left = outline[start][0]
                if left + w > aspect + 1e-12:
                    break
                floor = max(height for l, r, height in outline if r > left + 1e-12 and l < left + w - 1e-12)
                if floor + h <= 1.0 + 1e-12 and (best is None or floor < best[1] - 1e-12):
                    best = (left, floor)
            if best is None:
                return None
            left, floor = best
            spots[i] = (left + gap / 2, floor + gap / 2)
            fresh = []
            for l, r, height in outline:
                if r <= left + 1e-12 or l >= left + w - 1e-12:
                    fresh.append((l, r, height))
                    continue
                if l < left:
                    fresh.append((l, left, height))
                if r > left + w:
                    fresh.append((left + w, r, height))
            fresh.append((left, left + w, floor + h))
            outline = sorted(fresh)
        return spots

    low, high = 0.0, 1.0
    while place(high) is not None:
        low, high = high, high * 2.0
    for _ in range(40):
        middle = (low + high) / 2
        if place(middle) is None:
            high = middle
        else:
            low = middle
    return low, place(low)


def unwrap(obj, points, faces, islands, head_islands):
    """Unwraps the body along its seams and lays the islands out. Returns, per image, how much of
    its height a metre of skin takes and the share of the image its islands fill."""
    mesh = obj.data
    layer = mesh.uv_layers.new(name=bodies.SKIN_UV)
    mesh.uv_layers.active = layer
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    try:
        bpy.ops.uv.unwrap(method="MINIMUM_STRETCH", fill_holes=False, correct_aspect=False, margin=0.0)
    except TypeError:
        bpy.ops.uv.unwrap(method="ANGLE_BASED", fill_holes=False, correct_aspect=False, margin=0.0)
    bpy.ops.object.mode_set(mode="OBJECT")
    starts = [p.loop_start for p in mesh.polygons]
    frames = _island_frames(points, faces, islands)

    def read():
        # Edit mode gives the mesh new layers: the one made above is gone.
        values = np.empty(len(mesh.loops) * 2)
        mesh.uv_layers[bodies.SKIN_UV].data.foreach_get("uv", values)
        return values.reshape(-1, 2)

    uv = read()
    # An island that the first method left without an area is unwrapped by angles instead.
    for name, (members, _up) in frames.items():
        loops = np.concatenate([np.arange(starts[f], starts[f] + len(faces[f])) for f in members])
        if np.ptp(uv[loops], axis=0).min() > 1e-6 and np.isfinite(uv[loops]).all():
            continue
        bpy.ops.object.mode_set(mode="EDIT")
        bm = bmesh.from_edit_mesh(mesh)
        bm.faces.ensure_lookup_table()
        chosen = set(members)
        for face in bm.faces:
            face.select_set(face.index in chosen)
        bmesh.update_edit_mesh(mesh)
        bpy.ops.uv.unwrap(method="ANGLE_BASED", fill_holes=False, correct_aspect=False, margin=0.0)
        bpy.ops.object.mode_set(mode="OBJECT")
        uv = read()
        if np.ptp(uv[loops], axis=0).min() <= 1e-6:
            raise RuntimeError(f"the island {name} of {obj.name} does not unwrap")
        print(f"HUMAN_ASSEMBLE {obj.name}: the island {name} is unwrapped by angles")
    loop_vertex = np.array([l.vertex_index for l in mesh.loops])

    def area_3d(f):
        p = points[list(faces[f])]
        return sum(np.linalg.norm(np.cross(p[k] - p[0], p[k + 1] - p[0])) for k in range(1, len(p) - 1)) / 2

    def area_uv(f):
        q = uv[starts[f]:starts[f] + len(faces[f])]
        return sum((q[k] - q[0])[0] * (q[k + 1] - q[0])[1] - (q[k] - q[0])[1] * (q[k + 1] - q[0])[0]
                   for k in range(1, len(q) - 1)) / 2

    boxes = {}
    for name, (members, up) in frames.items():
        loops = np.concatenate([np.arange(starts[f], starts[f] + len(faces[f])) for f in members])
        signed = sum(area_uv(f) for f in members)
        if signed < 0:
            uv[loops, 0] = -uv[loops, 0]
            signed = -signed
        # Up on the image is the way the island's own up direction rises in the map.
        design = np.column_stack([uv[loops], np.ones(len(loops))])
        rise = np.linalg.lstsq(design, points[loop_vertex[loops]] @ up, rcond=None)[0][:2]
        angle = math.atan2(rise[0], rise[1])
        turn = np.array([[math.cos(angle), -math.sin(angle)], [math.sin(angle), math.cos(angle)]])
        uv[loops] = uv[loops] @ turn.T
        # One texel density: the island as large on the image as on the body, in metres.
        skin = sum(area_3d(f) for f in members)
        uv[loops] *= math.sqrt(skin / max(signed, 1e-18))
        uv[loops] -= uv[loops].min(0)
        boxes[name] = (loops, uv[loops].max(0), skin)
    filled = {}
    for image, aspect, chosen in (("body", BODY_ASPECT, [n for n in boxes if n not in head_islands]),
                                  ("head", HEAD_ASPECT, [n for n in boxes if n in head_islands])):
        chosen.sort()
        scale, spots = lay_out([tuple(boxes[n][1]) for n in chosen], aspect, GAP)
        for name, (x, y) in zip(chosen, spots):
            loops = boxes[name][0]
            uv[loops] = uv[loops] * scale + (x, y)
            uv[loops, 0] /= aspect
        filled[image] = (scale, sum(boxes[n][2] for n in chosen) * scale * scale / aspect)
    mesh.uv_layers[bodies.SKIN_UV].data.foreach_set("uv", uv.ravel())
    return filled


# ------------------------------------------------------------------------------------- the file

def write(label, net, faces, work, out_path):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1.0
    collections = {}
    for name in (EXPORT_COLLECTION, bodies.SOURCE_COLLECTION, REFERENCE_COLLECTION):
        collections[name] = bpy.data.collections.new(name)
        scene.collection.children.link(collections[name])
    root = bpy.data.objects.new(label, None)
    collections[EXPORT_COLLECTION].objects.link(root)
    materials = {part: _material(part, label) for part in MATERIALS}

    points, islands = net["points"], net["island"]
    mesh = bpy.data.meshes.new(bodies.body_name(label))
    mesh.from_pydata([tuple(p) for p in points], [], [tuple(f) for f in faces])
    mesh.update()
    for name in ("zone", "part"):
        mesh.attributes.new(name, "INT", "FACE").data.foreach_set("value", np.array(net[name], np.int32))
    mesh.materials.append(materials["Body"])
    mesh.materials.append(materials["Face"])
    head_islands = {island for island, zone in zip(islands, net["zone"]) if zone == HEAD_ZONE}
    if head_islands & {island for island, zone in zip(islands, net["zone"]) if zone != HEAD_ZONE}:
        raise RuntimeError("an island of the UV map lies on the head and on the body")
    mesh.polygons.foreach_set("material_index", [1 if island in head_islands else 0 for island in islands])
    mesh.polygons.foreach_set("use_smooth", [True] * len(mesh.polygons))
    # The seams: the borders between islands, and the lines that open an island.
    island_of = {}
    for index, face in enumerate(faces):
        for a, b in zip(face, face[1:] + face[:1]):
            island_of.setdefault((a, b) if a < b else (b, a), set()).add(islands[index])
    for edge in mesh.edges:
        key = tuple(sorted(edge.vertices))
        edge.use_seam = len(island_of[key]) > 1 or key in net["seams"]
    body = bpy.data.objects.new(mesh.name, mesh)
    collections[bodies.SOURCE_COLLECTION].objects.link(body)
    body.parent = root
    scales = unwrap(body, points, faces, islands, head_islands)

    # The body parts, as the pipeline writes them from the editable body.
    normals = bodies.corner_normals(mesh)
    for index, zone in enumerate(bodies.ZONES):
        part_mesh, _vertices = bodies.split(body, index, bodies.part_name(label, zone), normals)
        part = bpy.data.objects.new(part_mesh.name, part_mesh)
        collections[EXPORT_COLLECTION].objects.link(part)
        part.parent = root
        part["zone"] = zone
        part.hide_select = True
    body.hide_render = True

    extras = _load(os.path.join(work, f"work_{label}.blend"), [f"{label}_{name}" for name, _part in EXTRAS])
    for name, part in EXTRAS:
        obj = extras[f"{label}_{name}"]
        collections[EXPORT_COLLECTION].objects.link(obj)
        obj.parent = root
        obj.data.materials.clear()
        obj.data.materials.append(materials[part])
    reference = _load(os.path.join(work, "reference.blend"), [f"Reference_{label}"])[f"Reference_{label}"]
    collections[REFERENCE_COLLECTION].objects.link(reference)
    reference.hide_render = True
    reference.hide_select = True
    reference.display_type = "WIRE"
    collections[REFERENCE_COLLECTION].hide_render = True
    bpy.context.view_layer.update()
    for part in bodies.parts_of(label):
        part.hide_set(True)
    bpy.data.orphans_purge(do_local_ids=True, do_linked_ids=True, do_recursive=True)
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=out_path, compress=True, relative_remap=False)
    triangles = sum(len(f) - 2 for f in faces) + sum(sum(len(p.vertices) - 2 for p in o.data.polygons) for o in extras.values())
    print(f"HUMAN_ASSEMBLE {label}: {len(points)} vertices, {len(faces)} faces, {triangles} triangles with the eyeballs, "
          f"the teeth, and the tongue; the body's islands fill {scales['body'][1] * 100:.0f} % of its image at "
          f"{scales['body'][0] * 2048:.0f} texels per metre for 4096 x 2048, the head's {scales['head'][1] * 100:.0f} % at "
          f"{scales['head'][0] * 2048:.0f} texels per metre for 2048 x 2048")
    print(f"HUMAN_ASSEMBLE_SAVED {out_path}")


def assemble(work, out_folder, suffix):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    # Blender reads a relative path from the folder of its own file, and an unsaved file has none.
    work, out_folder = os.path.abspath(work), os.path.abspath(out_folder)
    nets = {label: _net(os.path.join(work, f"work_{label}.blend"), label) for label in LABELS}
    mend(nets)
    faces = cut_faces(nets)
    for label in LABELS:
        name = f"Human_{label}{suffix}"
        write(label, nets[label], faces, work, os.path.join(out_folder, name, f"{name}.blend"))


if __name__ == "__main__":
    arguments = sys.argv[sys.argv.index("--") + 1:]
    assemble(arguments[0], arguments[1], arguments[2] if len(arguments) > 2 else "")
