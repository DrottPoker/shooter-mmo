"""Garments: clothes and armor as meshes skinned to the shared skeleton, in an art asset of their own,
made once on the first body they fit and fitted to every other body of the same topology. See
docs/EQUIPMENT_VISUALS_DESIGN.md.

The file holds, per body, the collection Export_<Label> with the body's armature <Label> and the
garment's version <Name>_<Label> bound to it, and in the Reference collection the body's source body,
eyes, teeth, and tongue, which show it and which the fitting reads. The first body's version is the
garment a person edits. Every other version is fitted from it: each vertex is bound to a triangle of
the first body, by barycentric coordinates and an offset in the triangle's frame, and placed at the
same triangle and offset of the other body, and a vertex or a face centre that then leaves the
garment's band is moved back into it. A person's corrections of a fitted version are kept as
the difference to the attribute "fitted", the shape the last fitting gave.

Every vertex takes the weights of the body under it, interpolated from the corners of its triangle
and not smoothed, so it moves like the skin it covers. The attribute "transferred_weights" holds a
checksum of the weights the last transfer gave each vertex of the first version; a vertex whose
weights differ from it was painted by hand and keeps them, also in the other versions."""

import hashlib
import os
import zlib

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree
from mathutils.interpolate import poly_3d_calc

from . import bodies, poses, skinned
from .scene import ArtPipelineError, relative_image_paths

REFERENCE_COLLECTION = "Reference"
FITTED = "fitted"
TRANSFERRED = "transferred_weights"
MAX_INFLUENCES = 4
# How far a fitted vertex or a weight may differ from what a sync gives before the file is stale.
FIT_TOLERANCE = 1e-5
WEIGHT_TOLERANCE = 1e-4
# The distance from the skin each band allows, in meters: the smallest and the largest.
BANDS = {"tight": (0.0, 0.008), "mid": (0.005, 0.025), "outer": (0.010, 0.060), "loose": (0.010, None)}
SLOTS = ("head", "body", "legs", "hands", "feet", "back", "hair")
# The body parts a slot starts from when no paint shows where the garment goes.
SLOT_ZONES = {
    "head": ("Head",), "body": ("Neck", "Torso", "UpperArms"), "legs": ("Pelvis", "Thighs", "Shins"),
    "hands": ("Hands",), "feet": ("Feet",), "back": ("Torso",), "hair": ("Head",),
}
# A starting shell lies within its band: this far from the skin, at least the second distance and
# at most the third, and smoothed this often, so muscles and folds of the body do not show through.
SHELL_DISTANCES = {"tight": (0.004, 0.002, 0.007), "mid": (0.010, 0.0075, 0.022),
                   "outer": (0.020, 0.015, 0.050), "loose": (0.020, 0.015, None)}
SHELL_SMOOTHING = 12
# A vertex or a face centre that leaves its band moves back into it by the difference and this
# margin, a few times over, because moving one face moves the corners it shares.
SETTLE_MARGIN = 0.0005
SETTLE_PASSES = 4
ALPHA_CUT = 0.5
# The review file places the bodies side by side, this far apart.
REVIEW_OFFSET = 0.8
POSE_FRAMES = 24


def collection_name(label: str) -> str:
    return f"Export_{label}"


def version_name(name: str, label: str) -> str:
    return f"{name}_{label}"


def material_name(name: str) -> str:
    return f"M_Garment_{name}"


def image_name(name: str) -> str:
    return f"T_Garment_{name}"


def _collection(name: str) -> bpy.types.Collection:
    collection = bpy.data.collections.get(name)
    if collection is None:
        collection = bpy.data.collections.new(name)
    if collection.name not in bpy.context.scene.collection.children:
        bpy.context.scene.collection.children.link(collection)
    return collection


def _triangles(mesh: bpy.types.Mesh) -> list[tuple[int, int, int, int]]:
    """Every polygon as a fan from its first corner: (polygon, a, b, c). The order depends only on
    the topology, so bodies of the same topology give the same triangles."""
    found = []
    for polygon in mesh.polygons:
        corners = list(polygon.vertices)
        for i in range(1, len(corners) - 1):
            found.append((polygon.index, corners[0], corners[i], corners[i + 1]))
    return found


class Surface:
    """A body's surface for fitting: its triangles, their frames, and the body's weights over the
    given bones, or over its own vertex groups."""

    def __init__(self, body: bpy.types.Object, names: list[str] | None = None):
        mesh = body.data
        self.co = bodies.world_positions(body)
        self.triangles = _triangles(mesh)
        self.tree = BVHTree.FromPolygons([tuple(p) for p in self.co], [t[1:] for t in self.triangles])
        self.names = names if names is not None else [g.name for g in body.vertex_groups]
        self.weights = bodies.weights_of(body, self.names)

    def frame(self, triangle: int) -> tuple[Vector, Vector, Vector]:
        _p, a, b, c = self.triangles[triangle]
        pa, pb, pc = (Vector(self.co[i]) for i in (a, b, c))
        normal = (pb - pa).cross(pc - pa).normalized()
        tangent = (pb - pa).normalized()
        return tangent, normal.cross(tangent), normal

    def corners(self, triangle: int) -> list[Vector]:
        _p, a, b, c = self.triangles[triangle]
        return [Vector(self.co[i]) for i in (a, b, c)]

    def bind(self, point: Vector) -> tuple[int, list[float], tuple[float, float, float]]:
        """The triangle closest to the point, the point's barycentric coordinates on it, and its
        offset in the triangle's frame."""
        found, _normal, triangle, _distance = self.tree.find_nearest(point)
        if found is None:
            raise ArtPipelineError(f"No body surface near {tuple(point)}.")
        weights = poly_3d_calc(self.corners(triangle), found)
        tangent, bitangent, normal = self.frame(triangle)
        offset = point - found
        return triangle, weights, (offset.dot(tangent), offset.dot(bitangent), offset.dot(normal))

    def place(self, triangle: int, weights: list[float], offset: tuple[float, float, float]) -> Vector:
        corners = self.corners(triangle)
        point = corners[0] * weights[0] + corners[1] * weights[1] + corners[2] * weights[2]
        tangent, bitangent, normal = self.frame(triangle)
        return point + tangent * offset[0] + bitangent * offset[1] + normal * offset[2]

    def weights_at(self, triangle: int, weights: list[float]) -> np.ndarray:
        _p, a, b, c = self.triangles[triangle]
        return _limited(self.weights[a] * weights[0] + self.weights[b] * weights[1] + self.weights[c] * weights[2])

    def nearest(self, point: Vector) -> tuple[float, Vector, int]:
        """The distance from the nearest point of the surface along its normal, that normal, and the
        polygon of that point."""
        found, normal, triangle, _distance = self.tree.find_nearest(point)
        if found is None:
            return float("inf"), Vector((0.0, 0.0, 1.0)), -1
        return (point - found).dot(normal), normal, self.triangles[triangle][0]

    def signed_distance(self, point: Vector) -> tuple[float, int]:
        distance, _normal, polygon = self.nearest(point)
        return distance, polygon


def _settle(positions: np.ndarray, polygons: list[tuple[int, ...]], surface: Surface, low: float,
            high: float | None) -> np.ndarray:
    """The positions with every vertex, and the corners of every face whose centre, that lies closer
    to the skin than low or farther than high moved along the skin's normal there into the band, so
    the whole surface keeps it, not only its corners."""
    co = positions.copy()
    points = [(i,) for i in range(len(co))] + list(polygons)
    for _ in range(SETTLE_PASSES):
        settled = True
        for polygon in points:
            corners = list(polygon)
            distance, normal, _p = surface.nearest(Vector(co[corners].mean(axis=0)))
            if distance < low:
                co[corners] += np.array(normal) * (low + SETTLE_MARGIN - distance)
                settled = False
            elif high is not None and distance > high:
                co[corners] -= np.array(normal) * (distance - high + SETTLE_MARGIN)
                settled = False
        if settled:
            break
    return co


def _limited(row: np.ndarray) -> np.ndarray:
    """A weight row with its four largest weights, adding up to one."""
    keep = np.argsort(row, kind="stable")[::-1][:MAX_INFLUENCES]
    limited = np.zeros_like(row)
    limited[keep] = row[keep]
    total = limited.sum()
    return limited / total if total > 0 else limited


def _checksum(row: np.ndarray, names: list[str]) -> int:
    """A checksum of one vertex's weights, as a signed 32-bit integer attribute holds it."""
    text = ";".join(f"{names[i]}:{row[i]:.4f}" for i in np.nonzero(row > 0)[0])
    value = zlib.crc32(text.encode("utf-8"))
    return value - (1 << 32) if value >= 1 << 31 else value


def _references(arm: bpy.types.Object) -> list[bpy.types.Object]:
    """The body's meshes in the Reference collection: its source body, eyes, teeth, and tongue."""
    reference = bpy.data.collections.get(REFERENCE_COLLECTION)
    return [o for o in arm.children_recursive if reference is not None and o.name in reference.objects]


def _append_body(path: str, root: str) -> bpy.types.Object:
    """The body's armature and everything below it but its parts, appended: the source body shows
    the body in the viewport and in renders, and the fitting reads it."""
    with bpy.data.libraries.load(path, link=False) as (source, target):
        if root not in source.objects:
            raise ArtPipelineError(f'{path} has no object "{root}".')
        target.objects = list(source.objects)
        target.images = list(source.images)
    loaded = [o for o in target.objects if o is not None]
    arm = next(o for o in loaded if o.name == root)
    keep = {arm}
    stack = list(arm.children)
    while stack:
        child = stack.pop()
        if "zone" not in child:
            keep.add(child)
            stack.extend(child.children)
    for obj in loaded:
        if obj not in keep:
            bpy.data.objects.remove(obj, do_unlink=True)
    for image in target.images:
        if image is not None and image.users == 0:
            bpy.data.images.remove(image)
    if bodies.source_body(root) is None:
        raise ArtPipelineError(f'{path} has no source body "{bodies.body_name(root)}".')
    export = _collection(collection_name(root))
    reference = _collection(REFERENCE_COLLECTION)
    export.objects.link(arm)
    for obj in keep - {arm}:
        reference.objects.link(obj)
        obj.hide_render = False
        obj.hide_select = True
    return arm


def _body_fingerprint(arm: bpy.types.Object, body: bpy.types.Object) -> str:
    """SHA-256 of what the fitting reads from a body: its rig, its surface, and its weights."""
    names = sorted(g.name for g in body.vertex_groups)
    digest = hashlib.sha256(skinned.rig_fingerprint(arm).encode("utf-8"))
    digest.update(";".join(names).encode("utf-8"))
    digest.update((np.round(bodies.world_positions(body), 6) + 0.0).tobytes())
    digest.update((np.round(bodies.weights_of(body, names), 5) + 0.0).tobytes())
    return digest.hexdigest()


def _current_fingerprint(path: str, root: str) -> str:
    """The fingerprint of the body as its own file holds it now, read through a link that is
    removed again, so nothing of it stays in this file."""
    names = [root, bodies.body_name(root)]
    with bpy.data.libraries.load(path, link=True) as (source, target):
        missing = [n for n in names if n not in source.objects]
        if missing:
            raise ArtPipelineError(f"{path} has no objects {missing}.")
        target.objects = names
    library = target.objects[0].library
    try:
        return _body_fingerprint(target.objects[0], target.objects[1])
    finally:
        bpy.data.libraries.remove(library)


def _stale_bodies(body_list: list[tuple[str, str]]) -> list[str]:
    stale = []
    for path, root in body_list:
        arm = bpy.data.objects.get(root)
        body = bodies.source_body(root)
        here = _body_fingerprint(arm, body) if arm is not None and arm.type == "ARMATURE" and body is not None else None
        if here != _current_fingerprint(path, root):
            stale.append(root)
    return stale


def _replace_body(path: str, root: str) -> None:
    """Appends the body again from its file, keeping the garment's version on it."""
    old = bpy.data.objects.get(root)
    versions = []
    if old is not None:
        references = _references(old)
        versions = [o for o in old.children if o not in references]
        for obj in references:
            bpy.data.objects.remove(obj, do_unlink=True)
        old.name = f"{root}_replaced"
        if old.data is not None:
            old.data.name = f"{root}_replaced"
        bpy.data.orphans_purge(do_recursive=True)
    arm = _append_body(path, root)
    for version in versions:
        version.parent = arm
    if old is not None:
        bpy.data.objects.remove(old, do_unlink=True)
        bpy.data.orphans_purge(do_recursive=True)


def _shell(body: bpy.types.Object, keep: list[bool], band: str) -> bpy.types.Mesh:
    """The kept faces of the body, moved off the skin into the band and smoothed so the body's
    muscles and folds do not show through, with the body's UVs."""
    offset, gap, ceiling = SHELL_DISTANCES[band]
    surface = BVHTree.FromObject(body, bpy.context.evaluated_depsgraph_get())
    bm = bmesh.new()
    bm.from_mesh(body.data)
    bm.faces.ensure_lookup_table()
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if not keep[f.index]], context="FACES")
    if bm.verts.layers.deform.active is not None:
        bm.verts.layers.deform.remove(bm.verts.layers.deform.active)
    bm.normal_update()
    for v in bm.verts:
        v.co = v.co + v.normal * offset
    boundary = {v for e in bm.edges if e.is_boundary for v in e.verts}

    def smooth(factor):
        moved = {}
        for v in bm.verts:
            if v not in boundary and v.link_edges:
                moved[v] = sum((e.other_vert(v).co for e in v.link_edges), Vector()) / len(v.link_edges)
        for v, co in moved.items():
            v.co = v.co + (co - v.co) * factor

    # Taubin smoothing, a shrinking and an inflating step, keeps the shell's size.
    for _ in range(SHELL_SMOOTHING):
        smooth(0.5)
        smooth(-0.53)
        for v in bm.verts:
            found, normal, _i, _d = surface.find_nearest(v.co)
            if found is None:
                continue
            distance = (v.co - found).dot(normal)
            if distance < gap:
                v.co = found + normal * gap
            elif ceiling is not None and distance > ceiling:
                v.co = found + normal * ceiling
    # The centres of the faces keep the distances too.
    bm.verts.index_update()
    matrix = body.matrix_world
    co = np.array([tuple(matrix @ v.co) for v in bm.verts])
    co = _settle(co, [tuple(v.index for v in f.verts) for f in bm.faces], Surface(body, []), gap, ceiling)
    inverse = matrix.inverted()
    for v in bm.verts:
        v.co = inverse @ Vector(co[v.index])
    mesh = bpy.data.meshes.new("shell")
    bm.to_mesh(mesh)
    bm.free()
    # Only the UVs and the shading of the body's faces stay; its normals no longer fit the shell.
    keep_attributes = {"position", "sharp_face", "material_index"}
    if mesh.uv_layers.active is not None:
        keep_attributes.add(mesh.uv_layers.active.name)
    for attribute in [a.name for a in mesh.attributes if a.name not in keep_attributes and not a.name.startswith(".")]:
        mesh.attributes.remove(mesh.attributes[attribute])
    return mesh


def _painted_faces(body: bpy.types.Object, paint: bpy.types.Image, part: str) -> list[bool]:
    """Body faces of the painted material that the paint touches."""
    width, height = paint.size
    pixels = np.empty(width * height * 4, np.float32)
    paint.pixels.foreach_get(pixels)
    pixels = pixels.reshape(height, width, 4)
    mesh = body.data
    uv = mesh.uv_layers.active.data
    slots = {i for i, m in enumerate(mesh.materials) if m is not None and m.name.startswith(f"M_{part}_")}
    keep = []
    for polygon in mesh.polygons:
        if polygon.material_index not in slots:
            keep.append(False)
            continue
        corners = [Vector(uv[i].uv) for i in polygon.loop_indices]
        centre = sum(corners, Vector((0.0, 0.0))) / len(corners)
        samples = [centre] + [centre.lerp(c, 0.6) for c in corners]
        keep.append(any(pixels[min(max(int(s.y * height), 0), height - 1),
                               min(max(int(s.x * width), 0), width - 1), 3] >= ALPHA_CUT for s in samples))
    return keep


def _zone_faces(body: bpy.types.Object, slot: str) -> list[bool]:
    zones = {bodies.ZONES.index(z) for z in SLOT_ZONES[slot]}
    return [int(z) in zones for z in bodies.zones_of(body)]


def _material(name: str, image: bpy.types.Image | None) -> bpy.types.Material:
    """The garment's material: its image as the color, cut at its alpha, drawn from both sides like
    the game draws garments; the roughness and metallic of a paint layer's image when it has them."""
    material = bpy.data.materials.get(material_name(name)) or bpy.data.materials.new(material_name(name))
    material.use_nodes = True
    nodes, links = material.node_tree.nodes, material.node_tree.links
    bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Roughness"].default_value = 0.6
    if image is not None:
        bsdf.inputs["Roughness"].default_value = float(image.get("roughness", 0.6))
        bsdf.inputs["Metallic"].default_value = float(image.get("metallic", 0.0))
        texture = next((n for n in nodes if n.type == "TEX_IMAGE"), None) or nodes.new("ShaderNodeTexImage")
        texture.image = image
        links.new(texture.outputs["Color"], bsdf.inputs["Base Color"])
        if image.alpha_mode != "NONE":
            links.new(texture.outputs["Alpha"], bsdf.inputs["Alpha"])
            material.surface_render_method = "DITHERED"
    material.use_backface_culling = False
    return material


def create(file: str, name: str, slot: str, band: str, body_list: list[tuple[str, str]], paint_path: str | None,
           paint_name: str | None, part: str) -> None:
    """Writes the first file of a garment: the bodies, and on the first body a starting shell in its
    band of the faces its paint covers, or of the faces of its slot's body parts."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    arms = [_append_body(path, root) for path, root in body_list]
    first = arms[0]
    body = bodies.source_body(first.name)
    image = None
    if paint_path:
        with bpy.data.libraries.load(paint_path, link=False) as (source, target):
            wanted = f"T_Paint_{paint_name}_{first.name}"
            if wanted not in source.images:
                raise ArtPipelineError(f'{paint_path} has no image "{wanted}".')
            target.images = [wanted]
        image = target.images[0]
        image.name = image_name(name)
        keep = _painted_faces(body, image, part)
    else:
        keep = _zone_faces(body, slot)
    mesh = _shell(body, keep, band)
    mesh.name = version_name(name, first.name)
    obj = bpy.data.objects.new(mesh.name, mesh)
    bpy.data.collections[collection_name(first.name)].objects.link(obj)
    obj.parent = first
    mesh.materials.clear()
    mesh.materials.append(_material(name, image))
    for polygon in mesh.polygons:
        polygon.material_index = 0
    if image is not None:
        image.pack()
    _apply(_plan(name, arms, band, transfer_all=True), name, arms)
    _show_first(arms)
    relative_image_paths()
    bpy.ops.wm.save_as_mainfile(filepath=file, compress=True, relative_remap=False)


def _first_version(name: str, labels: list[str]) -> bpy.types.Object:
    obj = bpy.data.objects.get(version_name(name, labels[0]))
    if obj is None or obj.type != "MESH":
        raise ArtPipelineError(f'The garment has no version "{version_name(name, labels[0])}" on its first body.')
    return obj


def _attribute(mesh: bpy.types.Mesh, name: str, kind: str = "FLOAT_VECTOR") -> np.ndarray | None:
    attribute = mesh.attributes.get(name)
    if attribute is None or attribute.domain != "POINT" or attribute.data_type != kind:
        return None
    if kind == "INT":
        values = np.empty(len(mesh.vertices), np.int32)
        attribute.data.foreach_get("value", values)
        return values
    values = np.empty(len(mesh.vertices) * 3, np.float32)
    attribute.data.foreach_get("vector", values)
    return values.reshape(-1, 3).astype(np.float64)


def _store(mesh: bpy.types.Mesh, name: str, values: np.ndarray, kind: str = "FLOAT_VECTOR") -> None:
    attribute = mesh.attributes.get(name)
    if attribute is not None and (attribute.domain != "POINT" or attribute.data_type != kind):
        mesh.attributes.remove(attribute)
        attribute = None
    attribute = attribute or mesh.attributes.new(name, kind, "POINT")
    if kind == "INT":
        attribute.data.foreach_set("value", np.asarray(values, np.int32))
    else:
        attribute.data.foreach_set("vector", np.asarray(values, np.float32).reshape(-1))


def _rounded_weights(weights: np.ndarray) -> np.ndarray:
    # As vertex groups store them.
    return weights.astype(np.float32).astype(np.float64)


def _plan(name: str, arms: list[bpy.types.Object], band: str, transfer_all: bool = False) -> dict:
    """What a sync gives every version: its positions, its weights over the first body's bones,
    and the checksums of the transferred weights of the first version, with the number of vertices
    painted by hand."""
    labels = [a.name for a in arms]
    first = _first_version(name, labels)
    # Every deforming bone, so weights painted by hand on cloth chain bones, which the body has
    # none of, are kept too.
    names = [b.name for b in arms[0].data.bones if b.use_deform]
    surface = Surface(bodies.source_body(labels[0]), names)
    co = bodies.world_positions(first)
    binding = [surface.bind(Vector(p)) for p in co]
    transferred = _rounded_weights(np.array([surface.weights_at(t, w) for t, w, _o in binding]))
    current = bodies.weights_of(first, names)
    stored = _attribute(first.data, TRANSFERRED, "INT")
    if transfer_all or stored is None:
        hand = np.zeros(len(co), bool)
    else:
        sums = np.array([_checksum(row, names) for row in current])
        hand = (sums != stored) & (current.sum(1) > 0)
    first_weights = np.where(hand[:, None], current, transferred)
    checksums = np.array([_checksum(row, names) for row in transferred])
    if stored is not None and not transfer_all:
        checksums = np.where(hand, stored, checksums)
    plan = {"names": names, "hand": int(hand.sum()),
            "versions": {first.name: {"positions": None, "weights": first_weights, "checksums": checksums}}}
    for arm in arms[1:]:
        own = Surface(bodies.source_body(arm.name), names)
        positions = _settle(np.array([own.place(t, w, o) for t, w, o in binding]),
                            [tuple(p.vertices) for p in first.data.polygons], own, *BANDS[band])
        weights = _rounded_weights(np.array([own.weights_at(t, w) for t, w, _o in binding]))
        plan["versions"][version_name(name, arm.name)] = {
            "positions": positions, "weights": np.where(hand[:, None], first_weights, weights), "checksums": None}
    return plan


def _apply(plan: dict, name: str, arms: list[bpy.types.Object]) -> None:
    """Writes the plan: fits every other version, keeping its corrections, and gives every version
    its weights and binding."""
    first = _first_version(name, [a.name for a in arms])
    names = plan["names"]
    for arm in arms:
        target = bpy.data.objects.get(version_name(name, arm.name))
        entry = plan["versions"][version_name(name, arm.name)]
        if entry["positions"] is not None:
            fitted = entry["positions"]
            if target is None:
                mesh = first.data.copy()
                mesh.name = version_name(name, arm.name)
                target = bpy.data.objects.new(mesh.name, mesh)
                bpy.data.collections[collection_name(arm.name)].objects.link(target)
                correction = np.zeros_like(fitted)
            else:
                if len(target.data.vertices) != len(first.data.vertices):
                    raise ArtPipelineError(f'"{target.name}" no longer has the topology of "{first.name}"; delete it so '
                                           "the next sync fits it again.")
                previous = _attribute(target.data, FITTED)
                correction = bodies.world_positions(target) - previous if previous is not None else np.zeros_like(fitted)
            target.data.vertices.foreach_set("co", (fitted + correction).reshape(-1))
            _store(target.data, FITTED, fitted)
            if TRANSFERRED in target.data.attributes:
                target.data.attributes.remove(target.data.attributes[TRANSFERRED])
            target.data.update()
        else:
            _store(target.data, TRANSFERRED, entry["checksums"], "INT")
        target.parent = arm
        target.matrix_parent_inverse = Matrix.Identity(4)
        bodies.set_weights(target, entry["weights"], names)
        bodies.bind(target, arm)


def _show_first(arms: list[bpy.types.Object]) -> None:
    """Shows the first body and its version in the viewport, the one a person edits, and hides the
    others, which stand at the same place."""
    for index, arm in enumerate(arms):
        for obj in [arm] + list(arm.children_recursive):
            obj.hide_set(index > 0)


def _state(name: str, labels: list[str]) -> str:
    """SHA-256 of everything a sync writes, so a sync that changes nothing leaves the file as it is."""
    digest = hashlib.sha256()
    for label in labels:
        arm = bpy.data.objects.get(label)
        body = bodies.source_body(label)
        digest.update(label.encode("utf-8"))
        if arm is not None and arm.type == "ARMATURE" and body is not None:
            digest.update(_body_fingerprint(arm, body).encode("utf-8"))
        obj = bpy.data.objects.get(version_name(name, label))
        if obj is None or obj.type != "MESH":
            continue
        groups = sorted(g.name for g in obj.vertex_groups)
        digest.update(f"{obj.parent.name if obj.parent else ''}|{[m.type for m in obj.modifiers]}|{groups}".encode())
        digest.update((np.round(bodies.world_positions(obj), 6) + 0.0).tobytes())
        digest.update((np.round(bodies.weights_of(obj, groups), 5) + 0.0).tobytes())
        for attribute, kind in ((FITTED, "FLOAT_VECTOR"), (TRANSFERRED, "INT")):
            values = _attribute(obj.data, attribute, kind)
            digest.update(b"-" if values is None else (np.round(values, 6) + 0).tobytes())
    return digest.hexdigest()


def sync(file: str, name: str, band: str, body_list: list[tuple[str, str]], transfer_all: bool = False) -> tuple[bool, int]:
    """Appends the bodies again that changed in their own files, fits every other version of the
    garment again from the first, and gives every version its weights, keeping the corrections and
    the weights painted by hand, unless every weight is transferred again. Returns whether the file
    changed, and the number of vertices with weights painted by hand. An unchanged file is not
    saved, so its approvals stay valid."""
    bpy.ops.wm.open_mainfile(filepath=file)
    labels = [root for _path, root in body_list]
    before = _state(name, labels)
    stale = set(_stale_bodies(body_list))
    for path, root in body_list:
        if root in stale:
            _replace_body(path, root)
    arms = [bpy.data.objects[label] for label in labels]
    for obj in [o for o in bpy.data.objects if o.type == "ARMATURE" and o.name not in labels]:
        for child in list(obj.children_recursive):
            bpy.data.objects.remove(child, do_unlink=True)
        bpy.data.objects.remove(obj, do_unlink=True)
    plan = _plan(name, arms, band, transfer_all)
    _apply(plan, name, arms)
    if _state(name, labels) == before:
        return False, plan["hand"]
    if stale:
        _show_first(arms)
    relative_image_paths()
    bpy.ops.wm.save_as_mainfile(filepath=file, compress=True, relative_remap=False)
    return True, plan["hand"]


def stale_errors(name: str, band: str, body_list: list[tuple[str, str]]) -> list[str]:
    """A garment is checked as a sync would leave it: its bodies are the current ones of their
    files, every fitted version is fitted to the current first version, and every version has the
    weights a sync gives it."""
    labels = [root for _path, root in body_list]
    stale = _stale_bodies(body_list)
    if stale:
        return [f"The bodies {stale} of the garment changed in their own files; run tools/Sync-GarmentAsset.ps1."]
    arms = [bpy.data.objects[label] for label in labels]
    try:
        plan = _plan(name, arms, band)
    except ArtPipelineError:
        # garment_errors reports the missing first version.
        return []
    errors = []
    for version, entry in plan["versions"].items():
        obj = bpy.data.objects.get(version)
        if obj is None:
            errors.append(f'The garment has no version "{version}"; run tools/Sync-GarmentAsset.ps1.')
            continue
        if entry["positions"] is not None:
            fitted = _attribute(obj.data, FITTED)
            if fitted is None or fitted.shape != entry["positions"].shape or \
                    np.abs(fitted - entry["positions"]).max() > FIT_TOLERANCE:
                errors.append(f'"{version}" is not fitted to the current "{version_name(name, labels[0])}"; '
                              "run tools/Sync-GarmentAsset.ps1.")
                continue
        if np.abs(bodies.weights_of(obj, plan["names"]) - entry["weights"]).max(initial=0.0) > WEIGHT_TOLERANCE:
            errors.append(f'"{version}" does not have the weights its body gives it; run tools/Sync-GarmentAsset.ps1.')
    return errors


def garment_errors(name: str, labels: list[str], band: str, budget: int) -> list[str]:
    """The model rules of a garment: a version per body with the first version's topology, within the
    triangle budget and its band from the skin, at every vertex and at the centre of every face, so
    a large flat face over a round shoulder cannot dip under the skin between its corners."""
    errors = []
    try:
        first = _first_version(name, labels)
    except ArtPipelineError as error:
        return [str(error)]
    polygons = [tuple(p.vertices) for p in first.data.polygons]
    low, high = BANDS[band]
    for label in labels:
        obj = bpy.data.objects.get(version_name(name, label))
        if obj is None or obj.type != "MESH":
            errors.append(f'The garment has no version "{version_name(name, label)}"; run tools/Sync-GarmentAsset.ps1.')
            continue
        if [tuple(p.vertices) for p in obj.data.polygons] != polygons:
            errors.append(f'"{obj.name}" does not have the topology of "{first.name}".')
        if len(obj.data.materials) == 0 or any(m is None or m.name != material_name(name) for m in obj.data.materials):
            errors.append(f'"{obj.name}" must use the one material "{material_name(name)}".')
        triangles = sum(len(p.vertices) - 2 for p in obj.data.polygons)
        if triangles > budget:
            errors.append(f'"{obj.name}" has {triangles} triangles; the budget is {budget}.')
        distances, nearest = _distances(obj, label)
        near = distances < low - 1e-4
        far = distances > high + 1e-4 if high is not None else np.zeros_like(near)
        if near.any() or far.any():
            # Where they are, so a person knows where to shape the garment.
            zones = bodies.zones_of(bodies.source_body(label))
            counts = np.bincount(zones[nearest[near | far]], minlength=len(bodies.ZONES))
            places = ", ".join(f"{bodies.ZONES[z]} {counts[z]}" for z in np.argsort(-counts, kind="stable") if counts[z])
            worst = []
            if near.any():
                worst.append(f"the closest {distances[near].min() * 1000:.1f} mm")
            if far.any():
                worst.append(f"the farthest {distances[far].max() * 1000:.1f} mm")
            errors.append(f'"{obj.name}" leaves its {band} band of {low * 1000:.0f} to '
                          f'{"any" if high is None else f"{high * 1000:.0f}"} mm from the skin at {int((near | far).sum())} '
                          f"vertices and face centres ({int(near.sum())} too close, {int(far.sum())} too far; "
                          f"{', '.join(worst)}), over {places}.")
    return errors


def _distances(obj: bpy.types.Object, label: str) -> tuple[np.ndarray, np.ndarray]:
    """The distance from the skin of every vertex and of the centre of every face, and the body
    polygon each is nearest to."""
    surface = Surface(bodies.source_body(label), [])
    co = bodies.world_positions(obj)
    points = list(co) + [co[list(p.vertices)].mean(axis=0) for p in obj.data.polygons]
    found = [surface.signed_distance(Vector(p)) for p in points]
    return np.array([d for d, _p in found]), np.array([p for _d, p in found], np.int64)


def report(name: str, labels: list[str]) -> list[str]:
    """How far every version lies from the skin, and its triangles."""
    lines = []
    for label in labels:
        obj = bpy.data.objects.get(version_name(name, label))
        if obj is None:
            continue
        distances, _polygons = _distances(obj, label)
        triangles = sum(len(p.vertices) - 2 for p in obj.data.polygons)
        lines.append(f"{obj.name}: {triangles} triangles, {distances.min() * 1000:.1f} to {distances.max() * 1000:.1f} mm "
                     f"from the skin, median {np.median(distances) * 1000:.1f} mm")
    return lines


def export_paths(name: str, labels: list[str], folder: str) -> list[tuple[str, str]]:
    return [(label, os.path.join(folder, f"{version_name(name, label)}.fbx")) for label in labels]


def review_file(file: str, labels: list[str], output: str) -> str:
    """The garment on every body side by side, with an action RigTest_<Label> on each armature that
    steps through the test poses, for the reviewer to play in Blender."""
    bpy.ops.wm.open_mainfile(filepath=file)
    scene = bpy.context.scene
    frame = 0
    for index, label in enumerate(labels):
        arm = bpy.data.objects[label]
        arm.location.x = index * REVIEW_OFFSET
        for obj in [arm] + list(arm.children_recursive):
            obj.hide_set(False)
        action = bpy.data.actions.new(f"RigTest_{label}")
        arm.animation_data_create()
        arm.animation_data.action = action
        frame = 0
        for _name, _description, pose in poses.POSES[1:]:
            for f, p in ((frame, {}), (frame + POSE_FRAMES // 2, pose), (frame + POSE_FRAMES, pose)):
                _key_pose(arm, p, f)
            frame += POSE_FRAMES + POSE_FRAMES // 2
        _key_pose(arm, {}, frame)
    scene.frame_start, scene.frame_end = 0, frame
    scene.render.fps = POSE_FRAMES
    scene.frame_set(0)
    os.makedirs(os.path.dirname(output), exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=output, compress=True, relative_remap=False)
    return output


def _key_pose(arm: bpy.types.Object, pose: dict, frame: int) -> None:
    poses.apply(arm, pose)
    for pb in arm.pose.bones:
        pb.keyframe_insert("rotation_quaternion", frame=frame)
    arm.pose.bones["Hips"].keyframe_insert("location", frame=frame)
