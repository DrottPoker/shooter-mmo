"""The editable body of a skinned character: the whole body as one mesh, <Root>_Body, in the Source
collection, bound to the character's armature. It is what a person edits in Blender: one surface
without cuts, with smooth shading, sharp edges where the shading should break, the face attribute
"zone" that names the body part of every face, and the skin weights. The eyeballs, the teeth, the
tongue, and the armature are edited where they are, in the Export collection.

The body parts <Root>_<Zone> in the Export collection are written from the source body and never
edited by hand: each part holds the faces of one zone, the whole body's corner normals as custom
normals, so the parts meet without visible seams, and the whole body's weights. Equipment can hide
whole parts. In the viewport the source body shows and the parts are hidden; renders show the parts,
which are what the game gets."""

import bmesh
import bpy
import numpy as np

SOURCE_COLLECTION = "Source"
# The body parts in the order of the zone attribute's values.
ZONES = ("Head", "Neck", "Torso", "Pelvis", "UpperArms", "Forearms", "Hands", "Thighs", "Shins", "Feet")
# A part must match a fresh split of the source body this closely. Blender keeps custom normals
# in 16 bits per component, which moves a normal by up to about 1e-4.
NORMAL_TOLERANCE = 1e-3
UV_TOLERANCE = 1e-6
WEIGHT_TOLERANCE = 1e-4


def body_name(root: str) -> str:
    return f"{root}_Body"


def part_name(root: str, zone: str) -> str:
    return f"{root}_{zone}"


def source_body(root: str) -> bpy.types.Object | None:
    body = bpy.data.objects.get(body_name(root))
    return body if body is not None and body.type == "MESH" else None


TOPOLOGY_SCHEMA = 1


def topology(root: str) -> dict:
    """The editable body's vertices in Blender's world space and its faces with their body parts,
    as the export writes it next to the model. Garments name body faces and vertices by these
    indices, and Unity finds the faces of its own triangles by the positions."""
    body = source_body(root)
    co = world_positions(body)
    return {
        "schemaVersion": TOPOLOGY_SCHEMA,
        "body": root,
        "vertices": [round(float(c), 5) for c in co.reshape(-1)],
        "faceSizes": [len(p.vertices) for p in body.data.polygons],
        "faceVertices": [int(v) for p in body.data.polygons for v in p.vertices],
        "faceZones": [int(z) for z in zones_of(body)],
    }


def is_source_body(obj: bpy.types.Object) -> bool:
    """Whether the object is the editable body of the armature it belongs to."""
    return obj.type == "MESH" and obj.parent is not None and obj.name == body_name(obj.parent.name)


def parts_of(root: str) -> list[bpy.types.Object]:
    return [bpy.data.objects[part_name(root, zone)] for zone in ZONES if part_name(root, zone) in bpy.data.objects]


def _key(co) -> tuple[int, int, int]:
    return tuple(int(round(c * 1e5)) for c in co)


def world_positions(obj: bpy.types.Object) -> np.ndarray:
    co = np.empty(len(obj.data.vertices) * 3)
    obj.data.vertices.foreach_get("co", co)
    m = np.array(obj.matrix_world)
    return co.reshape(-1, 3) @ m[:3, :3].T + m[:3, 3]


def corner_normals(mesh: bpy.types.Mesh) -> np.ndarray:
    out = np.empty(len(mesh.loops) * 3, np.float32)
    mesh.corner_normals.foreach_get("vector", out)
    return out.reshape(-1, 3)


def _uvs(mesh: bpy.types.Mesh) -> np.ndarray:
    out = np.empty(len(mesh.loops) * 2)
    if mesh.uv_layers.active is not None:
        mesh.uv_layers.active.data.foreach_get("uv", out)
    return out.reshape(-1, 2)


def bind(obj: bpy.types.Object, arm: bpy.types.Object) -> None:
    """Binds a mesh to the armature by its vertex groups, replacing an earlier binding."""
    for md in [m for m in obj.modifiers if m.type == "ARMATURE"]:
        obj.modifiers.remove(md)
    md = obj.modifiers.new("Armature", "ARMATURE")
    md.object = arm
    md.use_vertex_groups = True
    md.use_bone_envelopes = False
    md.use_deform_preserve_volume = False


def weights_of(obj: bpy.types.Object, names: list[str]) -> np.ndarray:
    """The weights of a mesh as an (N, bones) array over the given bone names."""
    column = {g.index: names.index(g.name) for g in obj.vertex_groups if g.name in names}
    weights = np.zeros((len(obj.data.vertices), len(names)))
    for v in obj.data.vertices:
        for g in v.groups:
            if g.group in column:
                weights[v.index, column[g.group]] = g.weight
    return weights


def set_weights(obj: bpy.types.Object, weights: np.ndarray, names: list[str]) -> None:
    """Replaces a mesh's vertex groups with one group per bone that carries weight, in bone order."""
    obj.vertex_groups.clear()
    for i in np.where(weights.max(0) > 0)[0]:
        group = obj.vertex_groups.new(name=names[i])
        for v in np.where(weights[:, i] > 0)[0]:
            group.add([int(v)], float(weights[v, i]), "REPLACE")


def _drop_unused_materials(mesh: bpy.types.Mesh) -> None:
    used = sorted({p.material_index for p in mesh.polygons})
    materials = [mesh.materials[i] for i in used]
    remap = {old: new for new, old in enumerate(used)}
    indices = [remap[p.material_index] for p in mesh.polygons]
    # Clearing the slots resets every face to slot 0, so the indices are set afterwards.
    mesh.materials.clear()
    for material in materials:
        mesh.materials.append(material)
    for polygon, index in zip(mesh.polygons, indices):
        polygon.material_index = index


def zones_of(body: bpy.types.Object) -> np.ndarray:
    if "zone" not in body.data.attributes or body.data.attributes["zone"].domain != "FACE":
        raise RuntimeError(f'{body.name} needs the face attribute "zone" with the body part of every face.')
    zone = np.empty(len(body.data.polygons), np.int32)
    body.data.attributes["zone"].data.foreach_get("value", zone)
    return zone


def split(body: bpy.types.Object, zone_index: int, name: str, normals: np.ndarray) -> tuple[bpy.types.Mesh, np.ndarray]:
    """A copy of the body's mesh with only the faces of one zone, in their order, and the body's
    corner normals, and the body's index of each of its vertices."""
    mesh = body.data.copy()
    mesh.name = name
    zone = zones_of(body)
    first_loop = np.array([p.loop_start for p in mesh.polygons])
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bm.faces.ensure_lookup_table()
    origin_face = bm.faces.layers.int.new("orig_face")
    origin_vertex = bm.verts.layers.int.new("orig_vertex")
    for f in bm.faces:
        f[origin_face] = f.index
    for v in bm.verts:
        v[origin_vertex] = v.index
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if zone[f.index] != zone_index], context="FACES")
    custom = []
    for f in bm.faces:
        start = first_loop[f[origin_face]]
        custom += [normals[start + k] for k in range(len(f.loops))]
    vertices = np.array([v[origin_vertex] for v in bm.verts])
    bm.faces.layers.int.remove(origin_face)
    bm.verts.layers.int.remove(origin_vertex)
    # The weights refer to the body's vertex groups; the part gets groups of its own.
    if bm.verts.layers.deform.active is not None:
        bm.verts.layers.deform.remove(bm.verts.layers.deform.active)
    bm.to_mesh(mesh)
    bm.free()
    mesh.normals_split_custom_set([tuple(n) for n in custom])
    _drop_unused_materials(mesh)
    return mesh, vertices


def write_parts(root: str, bone_names: list[str]) -> list[bpy.types.Object]:
    """Writes the body parts of a character from its source body, replacing earlier parts."""
    body = source_body(root)
    if body is None:
        raise RuntimeError(f'The file has no source body "{body_name(root)}".')
    arm = bpy.data.objects[root]
    collection = next(c for c in arm.users_collection if c.name != SOURCE_COLLECTION)
    for obj in parts_of(root):
        mesh = obj.data
        bpy.data.objects.remove(obj)
        bpy.data.meshes.remove(mesh)
    normals = corner_normals(body.data)
    weights = weights_of(body, bone_names)
    zone = zones_of(body)
    parts = []
    for index, zone_name in enumerate(ZONES):
        if not (zone == index).any():
            raise RuntimeError(f"{body.name} has no faces of the zone {zone_name}.")
        name = part_name(root, zone_name)
        mesh, vertices = split(body, index, name, normals)
        obj = bpy.data.objects.new(name, mesh)
        collection.objects.link(obj)
        obj.parent = arm
        obj.matrix_world = body.matrix_world
        obj["zone"] = zone_name
        set_weights(obj, weights[vertices], bone_names)
        bind(obj, arm)
        obj.hide_select = True
        parts.append(obj)
    body.hide_render = True
    bpy.context.view_layer.update()
    for obj in parts:
        obj.hide_set(True)
    return parts


def body_weights_from_parts(root: str, bone_names: list[str]) -> None:
    """Gives the source body the weights of its parts, vertex by vertex at the same place, after a
    script solved them on the parts."""
    body = source_body(root)
    found = {}
    for obj in parts_of(root):
        for co, row in zip(world_positions(obj), weights_of(obj, bone_names)):
            found[_key(co)] = row
    rows = []
    for co in world_positions(body):
        key = _key(co)
        if key not in found:
            raise RuntimeError(f"{body.name} has a vertex at {tuple(co)} that no part has.")
        rows.append(found[key])
    set_weights(body, np.array(rows), bone_names)


def part_errors(root: str) -> list[str]:
    """The parts must be what writing them from the source body gives now, so the export is the
    edited body. A character without a source body has nothing to compare."""
    body = source_body(root)
    if body is None:
        return []
    try:
        zone = zones_of(body)
    except RuntimeError as error:
        return [str(error)]
    missing = [part_name(root, z) for i, z in enumerate(ZONES) if (zone == i).any() and part_name(root, z) not in bpy.data.objects]
    if missing:
        return [f"The parts {missing} of {body.name} are missing; write them with tools/Sync-HumanBase.ps1."]
    names = sorted({g.name for o in [body] + parts_of(root) for g in o.vertex_groups})
    body_weights = weights_of(body, names)
    normals = corner_normals(body.data)
    stale = []
    for index, zone_name in enumerate(ZONES):
        if not (zone == index).any():
            continue
        part = bpy.data.objects[part_name(root, zone_name)]
        mesh, vertices = split(body, index, f"_check_{zone_name}", normals)
        try:
            fresh_co = world_positions(body)[vertices]
            part_co = world_positions(part)
            same = (len(part_co) == len(fresh_co) and len(part.data.polygons) == len(mesh.polygons)
                    and [tuple(_key(part_co[v]) for v in p.vertices) for p in part.data.polygons]
                    == [tuple(_key(fresh_co[v]) for v in p.vertices) for p in mesh.polygons]
                    and [part.data.materials[p.material_index].name for p in part.data.polygons]
                    == [mesh.materials[p.material_index].name for p in mesh.polygons])
            if same:
                same = (float(np.abs(corner_normals(part.data) - corner_normals(mesh)).max()) <= NORMAL_TOLERANCE
                        and float(np.abs(_uvs(part.data) - _uvs(mesh)).max(initial=0.0)) <= UV_TOLERANCE)
            if same:
                order = {_key(co): i for i, co in enumerate(part_co)}
                rows = [order.get(_key(co)) for co in fresh_co]
                same = None not in rows and float(np.abs(
                    weights_of(part, names)[rows] - body_weights[vertices]).max(initial=0.0)) <= WEIGHT_TOLERANCE
            if not same:
                stale.append(part.name)
        finally:
            bpy.data.meshes.remove(mesh)
    if stale:
        return [f"The parts {stale} differ from the source body {body.name}; write them again with "
                "tools/Sync-HumanBase.ps1."]
    return []
