"""Paint layers: transparent paint over the skin of bodies, such as a tattoo, in an art asset of its
own, independent of the bodies' topology. See docs/ART_PIPELINE.md.

The paint is kept on the layer's canvas: in the collection Canvas, the mesh Canvas, a copy of the
body it is painted on, the body's parts joined in its rest pose, with the body's UV maps, its rig's
landmarks, and its cage version Canvas_Cage; and the image T_Paint_<Name> in the canvas's paint UV
map, UVMap_Paint, which lays both sides of a mirrored body apart, so paint can differ between the
sides. A person paints on the canvas. The canvas is the layer's own and never changes with the
bodies, so a body of any topology, such as a new base model, gets the paint by projection, and
the canvas can be moved to such a body when its paint needs a hand there.

Per body, the Reference collection holds Paint_<Label>, the body's parts joined in its rest pose,
and the image T_Paint_<Name>_<Label>, the canvas's paint moved into the body's own paint UV map,
cropped to the texels it paints, with its place in the full layout in the custom properties "rect"
and "layout". Every texel of the body takes the paint at its point of the canvas:

- A body with the canvas's topology finds the point at the same place of the same face.
- Any other body finds it through the garment cage: every canvas vertex is bound to the canvas's
  cage version and placed at the same place of the body's cage version, Cage_<Label>, moved further
  by the difference between the rigs' landmarks, such as the eyes, the lips, and the finger tips,
  and laid onto the body's skin; each texel then takes the nearest point of the canvas so fitted.
  Both stay on the same region and side of the cage, and a hand on the same finger.

The faces of a body other than its skin, such as the eyes, have the zone -1 in the joined mesh."""

import hashlib
import json
import os
import tempfile

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree
from mathutils.interpolate import poly_3d_calc

from . import bodies, cage
from .scene import ArtPipelineError, relative_image_paths

REFERENCE_COLLECTION = "Reference"
CANVAS_COLLECTION = "Canvas"
CAGE_COLLECTION = "Cage"
CANVAS = "Canvas"
CANVAS_CAGE = "Canvas_Cage"
# Custom properties: the label of the body a canvas was copied from, the rig's landmarks of a
# canvas or a body, the fingerprint of what the move reads from a body, a body image's place in its
# full layout, and the SHA-256 of the cage file a body's cage version was taken from.
CANVAS_BODY = "canvas_body"
LANDMARKS = "landmarks"
LAYOUT = "paint_layout"
RECT = "rect"
FULL = "layout"
CAGE_SHA = "cage_sha256"
DIGIT = "digit"
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
BODY_SPACING = 1.2
# The roughness and metallic of the paint, kept as custom properties of the canvas image.
DEFAULT_ROUGHNESS = 0.6
# A moved texel may differ this much from a fresh move before the file counts as stale.
MOVE_TOLERANCE = 1.5 / 255.0
# The texels a body image extends its painted edges by, so filtering at a UV seam takes paint.
MARGIN = 16
# A body image is cropped to whole blocks of this many texels around its paint.
CROP_BLOCK = 4
# The landmark move: the width of each landmark's influence and the smoothing of the fit, in
# meters, and the distance below which two landmarks count as one.
LANDMARK_WIDTH = 0.02
LANDMARK_SMOOTHING = 0.01
LANDMARK_MERGE = 0.001
# Landmarks of one side only, which also hold for the mirrored side.
MIRROR_MARGIN = 1e-3
# A subdivided copy of the canvas's own body, reached through the cage, takes the paint this close
# to where the exact move puts it, in millimeters over the painted texels: the median and the 95th
# percentile.
PROJECTION_OWN_MEDIAN = 0.5
PROJECTION_OWN_P95 = 1.0
ZONE_REGIONS = {bodies.ZONES.index(zone): region for region, zones in cage.REGION_ZONES.items() for zone in zones}


def image_name(name: str, label: str | None = None) -> str:
    """The canvas image T_Paint_<Name>, or the image of a body T_Paint_<Name>_<Label>."""
    return f"T_Paint_{name}" if label is None else f"T_Paint_{name}_{label}"


def _collection(name: str) -> bpy.types.Collection:
    collection = bpy.data.collections.get(name)
    if collection is None:
        collection = bpy.data.collections.new(name)
    if collection.name not in bpy.context.scene.collection.children:
        bpy.context.scene.collection.children.link(collection)
    return collection


def _suffix(obj: bpy.types.Object, label: str) -> str:
    return obj.name[len(label) + 1:] if obj.name.startswith(label + "_") else obj.name


def _landmarks(arm: bpy.types.Object) -> dict[str, list[float]]:
    """The landmarks the rig keeps in Blender's world space, lmw_<name>, as flat lists."""
    return {key: [float(v) for v in arm[key]] for key in sorted(arm.keys()) if key.startswith("lmw_")}


def _body_meshes(objects: list[bpy.types.Object], label: str) -> list[bpy.types.Object]:
    """A body's meshes but its editable source body, in the order of their names without the label
    and without a number Blender added, such as Torso for Male_Torso.001."""
    return sorted((o for o in objects if o.type == "MESH" and not bodies.is_source_body(o)),
                  key=lambda o: _suffix(o, label).split(".")[0])


def _mark_faces(obj: bpy.types.Object) -> None:
    """Gives a body mesh the face attributes the move reads: "zone", -1 outside the skin's parts, and
    "digit", the finger or thumb of every face of the hands, from the mesh's weights."""
    skin = "zone" in obj.data.attributes and obj.data.attributes["zone"].domain == "FACE"
    if not skin:
        obj.data.attributes.new("zone", "INT", "FACE").data.foreach_set("value", [-1] * len(obj.data.polygons))
    digits = cage.face_digits(obj) if skin else np.zeros(len(obj.data.polygons), np.int64)
    attribute = obj.data.attributes.get(DIGIT) or obj.data.attributes.new(DIGIT, "INT", "FACE")
    attribute.data.foreach_set("value", digits.astype(np.int32))


def layout_fingerprint(arm: bpy.types.Object, meshes: list[bpy.types.Object]) -> str:
    """SHA-256 of what the move reads from a body, mesh by mesh in their order before they are
    joined, so it does not depend on the order Blender joins them in: each mesh's vertices in world
    space, its faces with their materials, zones, and digits, and its paint UV map, and the rig's
    landmarks."""
    digest = hashlib.sha256()
    for obj in meshes:
        mesh = obj.data
        co = _positions(mesh) @ np.array(obj.matrix_world)[:3, :3].T + np.array(obj.matrix_world)[:3, 3]
        digest.update((np.round(co, 5) + 0.0).tobytes())
        digest.update(np.array([len(p.vertices) for p in mesh.polygons], np.int64).tobytes())
        digest.update(np.array([v for p in mesh.polygons for v in p.vertices], np.int64).tobytes())
        digest.update(";".join(_skin_material_name(mesh.materials[p.material_index]) for p in mesh.polygons).encode("utf-8"))
        digest.update(_face_ints(mesh, "zone").tobytes())
        digest.update(_face_ints(mesh, DIGIT).tobytes())
        digest.update((np.round(bodies._uvs(mesh, bodies.PAINT_UV), 6) + 0.0).tobytes())
    digest.update(json.dumps(_landmarks(arm), sort_keys=True).encode("utf-8"))
    return digest.hexdigest()


def join(arm: bpy.types.Object, objects: list[bpy.types.Object], label: str, name: str) -> bpy.types.Object:
    """The body's meshes joined in the order of their names without the label, so bodies of the same
    topology join into meshes of the same topology, in their rest pose and without weights, with the
    face attributes of _mark_faces, the rig's landmarks, and the fingerprint of what the move reads
    from the body. The objects are used up."""
    meshes = _body_meshes(objects, label)
    if not meshes:
        raise ArtPipelineError(f"The body {label} has no meshes.")
    for obj in meshes:
        _mark_faces(obj)
    fingerprint = layout_fingerprint(arm, meshes)
    for obj in meshes:
        obj.modifiers.clear()
        obj.hide_select = False
        world = obj.matrix_world.copy()
        obj.parent = None
        obj.matrix_world = world
    bpy.ops.object.select_all(action="DESELECT")
    for obj in meshes:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    bpy.ops.object.join()
    body = bpy.context.view_layer.objects.active
    # The join keeps the frame of its first mesh, which may be an eye with a pivot of its own; the
    # body's mesh lies in Blender's world space at its rest pose.
    body.data.transform(body.matrix_world)
    body.matrix_world = Matrix.Identity(4)
    body.name = name
    body.data.name = name
    for group in list(body.vertex_groups):
        body.vertex_groups.remove(group)
    body[LANDMARKS] = _landmarks(arm)
    body[LAYOUT] = fingerprint
    return body


def _load_body(path: str, root: str, collection: bpy.types.Collection, name: str) -> bpy.types.Object:
    """The body of another file, joined, in the collection."""
    with bpy.data.libraries.load(path, link=False) as (source, target):
        if root not in source.objects:
            raise ArtPipelineError(f'{path} has no object "{root}".')
        target.objects = list(source.objects)
    loaded = [o for o in target.objects if o is not None]
    for obj in loaded:
        collection.objects.link(obj)
    # A loaded object's world matrix is evaluated only with the view layer, and an eye has a pivot
    # of its own.
    bpy.context.view_layer.update()
    arm = next(o for o in loaded if o.name == root)
    if arm.type != "ARMATURE":
        raise ArtPipelineError(f'"{root}" in {path} is not an armature.')
    meshes = [o for o in loaded if o.type == "MESH" and not bodies.is_source_body(o)]
    body = join(arm, meshes, root, name)
    for obj in loaded:
        if obj not in meshes:
            bpy.data.objects.remove(obj, do_unlink=True)
    return body


def body_fingerprint(root: str) -> str:
    """The fingerprint of what a paint layer's move reads from a body in the open file, from copies
    of its meshes, which take the face attributes a paint layer gives them."""
    arm = bpy.data.objects[root]
    copies = []
    for obj in _body_meshes([o for o in bpy.data.objects if o.parent == arm], root):
        copy = obj.copy()
        copy.data = obj.data.copy()
        copies.append(copy)
    try:
        for copy in copies:
            _mark_faces(copy)
        return layout_fingerprint(arm, copies)
    finally:
        for copy in copies:
            mesh = copy.data
            bpy.data.objects.remove(copy, do_unlink=True)
            bpy.data.meshes.remove(mesh)


def _skin_material_name(material: bpy.types.Material | None) -> str:
    """The name of the body's material that a material of the file stands for: M_Body_Male for
    itself, for its preview Preview_Body_Male, for the canvas's Canvas_Body_Male, and for a copy that
    a second load of the body named M_Body_Male.001."""
    if material is None:
        return ""
    name = material.name.split(".")[0]
    for prefix in ("Preview_", "Canvas_"):
        if name.startswith(prefix):
            return "M_" + name[len(prefix):]
    return name


def _face_ints(mesh: bpy.types.Mesh, name: str) -> np.ndarray:
    attribute = mesh.attributes.get(name)
    values = np.zeros(len(mesh.polygons), np.int32)
    if attribute is not None and attribute.domain == "FACE":
        attribute.data.foreach_get("value", values)
    return values.astype(np.int64)


def _marks(obj: bpy.types.Object) -> dict[str, list[float]]:
    found = obj.get(LANDMARKS)
    return {} if found is None else {key: [float(v) for v in found[key]] for key in found.keys()}


def _painted_index(obj: bpy.types.Object, part: str, label: str) -> int:
    for index, slot in enumerate(obj.material_slots):
        if slot.material is not None and _skin_material_name(slot.material) == f"M_{part}_{label}":
            return index
    raise ArtPipelineError(f'"{obj.name}" has no material M_{part}_{label}.')


def _skin_image(material: bpy.types.Material) -> bpy.types.Image:
    images = [n.image for n in material.node_tree.nodes if n.type == "TEX_IMAGE" and n.image is not None
              and not n.image.name.startswith("T_Paint_")]
    if len(images) != 1:
        raise ArtPipelineError(f'Material "{material.name}" must have one skin image.')
    return images[0]


def paint_size(obj: bpy.types.Object, index: int) -> tuple[int, int]:
    """The size of the paint of a material: that of its skin image, twice as wide when the body
    mirrors it, so both sides have texels of their own."""
    material = obj.material_slots[index].material
    width, height = _skin_image(material).size
    return (width * 2 if material.name in bodies.mirrored_materials(obj.data) else width), height


def _loop_triangles(mesh: bpy.types.Mesh, index: int | None = None) -> tuple[np.ndarray, np.ndarray]:
    """Every face, or every face of one material, as a fan of triangles from its first corner: the
    face of each triangle and its three loops. The order depends only on the topology."""
    faces, loops = [], []
    for polygon in mesh.polygons:
        if index is not None and polygon.material_index != index:
            continue
        corners = list(polygon.loop_indices)
        for i in range(1, len(corners) - 1):
            faces.append(polygon.index)
            loops.append((corners[0], corners[i], corners[i + 1]))
    return np.array(faces, np.int64), np.array(loops, np.int64).reshape(-1, 3)


def _loop_vertices(mesh: bpy.types.Mesh) -> np.ndarray:
    out = np.empty(len(mesh.loops), np.int32)
    mesh.loops.foreach_get("vertex_index", out)
    return out.astype(np.int64)


def _positions(mesh: bpy.types.Mesh) -> np.ndarray:
    co = np.empty(len(mesh.vertices) * 3)
    mesh.vertices.foreach_get("co", co)
    return co.reshape(-1, 3)


class Texels:
    """The texels of a material's faces in a mesh's paint UV map, at the image size: each texel's
    place in the image, the triangle of _loop_triangles it lies on, and its barycentric
    coordinates there. A texel on an edge belongs to one triangle."""

    def __init__(self, mesh: bpy.types.Mesh, index: int, size: tuple[int, int]):
        width, height = size
        self.size = size
        self.faces, self.loops = _loop_triangles(mesh, index)
        uv = bodies._uvs(mesh, bodies.PAINT_UV) * (width, height)
        taken = np.zeros(width * height, bool)
        places, triangles, weights = [], [], []
        for t, (a, b, c) in enumerate(uv[self.loops]):
            area = (b[0] - a[0]) * (c[1] - a[1]) - (c[0] - a[0]) * (b[1] - a[1])
            if abs(area) < 1e-12:
                continue
            low = np.floor(np.minimum(np.minimum(a, b), c) - 0.5).astype(int).clip(0, (width - 1, height - 1))
            high = np.ceil(np.maximum(np.maximum(a, b), c) - 0.5).astype(int).clip(0, (width - 1, height - 1))
            xs, ys = np.meshgrid(np.arange(low[0], high[0] + 1), np.arange(low[1], high[1] + 1))
            px, py = xs + 0.5, ys + 0.5
            w0 = ((b[0] - px) * (c[1] - py) - (c[0] - px) * (b[1] - py)) / area
            w1 = ((c[0] - px) * (a[1] - py) - (a[0] - px) * (c[1] - py)) / area
            w2 = 1.0 - w0 - w1
            inside = (w0 >= -1e-7) & (w1 >= -1e-7) & (w2 >= -1e-7)
            flat = (ys * width + xs)[inside]
            free = ~taken[flat]
            flat = flat[free]
            taken[flat] = True
            places.append(flat)
            triangles.append(np.full(len(flat), t, np.int64))
            weights.append(np.stack([w0[inside][free], w1[inside][free], w2[inside][free]], axis=1))
        self.places = np.concatenate(places) if places else np.zeros(0, np.int64)
        self.triangles = np.concatenate(triangles) if triangles else np.zeros(0, np.int64)
        self.weights = np.concatenate(weights).clip(0.0, 1.0) if weights else np.zeros((0, 3))

    def points(self, mesh: bpy.types.Mesh, co: np.ndarray | None = None) -> np.ndarray:
        """Each texel's point on the mesh, at its rest pose or at the given vertex positions."""
        co = _positions(mesh) if co is None else co
        vertices = _loop_vertices(mesh)[self.loops[self.triangles]]
        return (co[vertices] * self.weights[:, :, None]).sum(axis=1)


def _keys(mesh: bpy.types.Mesh) -> list[tuple | None]:
    """The cage region, side, and digit of every face of a joined body, or None outside its skin."""
    zones = _face_ints(mesh, "zone")
    digits = _face_ints(mesh, DIGIT)
    co = _positions(mesh)
    keys = []
    for polygon in mesh.polygons:
        region = ZONE_REGIONS.get(int(zones[polygon.index]))
        if region is None:
            keys.append(None)
            continue
        side = cage.side_of(float(co[list(polygon.vertices), 0].mean())) if region in cage.SIDED else ""
        keys.append((region, side, int(digits[polygon.index]) if region == "hands" else 0))
    return keys


def _cage_keys(mesh: bpy.types.Mesh) -> list[tuple]:
    regions = cage.face_ints(mesh, "region", -1)
    digits = cage.face_ints(mesh, cage.DIGIT, 0)
    co = _positions(mesh)
    keys = []
    for polygon in mesh.polygons:
        region = cage.REGIONS[int(regions[polygon.index])]
        side = cage.side_of(float(co[list(polygon.vertices), 0].mean())) if region in cage.SIDED else ""
        keys.append((region, side, int(digits[polygon.index]) if region == "hands" else 0))
    return keys


def _matches(key: tuple, other: tuple) -> bool:
    """Whether a face of the other key may serve a point of this key: the same region and side, and
    on a hand the same finger or the palm."""
    return key[:2] == other[:2] and (key[0] != "hands" or other[2] in (0, key[2]))


class _Nearest:
    """The nearest point of a mesh's triangles at given positions, among the triangles whose face's
    key matches the asking point's key, or among all when none does."""

    def __init__(self, co: np.ndarray, faces: np.ndarray, corners: np.ndarray, face_keys: list):
        self.co, self.faces, self.corners = co, faces, corners
        self.face_keys = face_keys
        self.trees = {}
        self.all = self._tree(np.arange(len(faces)))

    def _tree(self, chosen: np.ndarray):
        if len(chosen) == 0:
            return None
        return BVHTree.FromPolygons([tuple(p) for p in self.co], [tuple(c) for c in self.corners[chosen]]), chosen

    def tree(self, key):
        if key not in self.trees:
            chosen = np.array([i for i, f in enumerate(self.faces)
                               if key is not None and self.face_keys[f] is not None and _matches(key, self.face_keys[f])],
                              np.int64)
            self.trees[key] = self._tree(chosen) or self.all
        return self.trees[key]

    def find(self, points: np.ndarray, keys: list) -> tuple[np.ndarray, np.ndarray]:
        """Per point the triangle and its barycentric coordinates of the nearest point."""
        triangles = np.zeros(len(points), np.int64)
        weights = np.zeros((len(points), 3))
        groups: dict = {}
        for i, key in enumerate(keys):
            groups.setdefault(key, []).append(i)
        for key, members in groups.items():
            tree, chosen = self.tree(key)
            for i in members:
                location, _normal, local, _distance = tree.find_nearest(Vector(points[i]))
                t = chosen[local]
                triangles[i] = t
                weights[i] = poly_3d_calc([Vector(self.co[v]) for v in self.corners[t]], location)
        return triangles, weights


def _landmark_pairs(canvas_marks: dict, body_marks: dict) -> tuple[np.ndarray, np.ndarray]:
    """The landmarks both rigs have, one point each, with the mirror of those the rigs keep for one
    side only, and without points closer together on the canvas than LANDMARK_MERGE."""
    source, target = [], []
    for key in sorted(set(canvas_marks) & set(body_marks)):
        a = np.array(canvas_marks[key]).reshape(-1, 3)
        b = np.array(body_marks[key]).reshape(-1, 3)
        if len(a) != len(b):
            continue
        for p, q in zip(a, b):
            source.append(p)
            target.append(q)
            if abs(p[0]) > MIRROR_MARGIN and not np.any(np.all(np.abs(a * (-1, 1, 1) - p) < 1e-4, axis=1)):
                source.append(p * (-1, 1, 1))
                target.append(q * (-1, 1, 1))
    kept = []
    for i, p in enumerate(source):
        if all(np.linalg.norm(p - source[j]) > LANDMARK_MERGE for j in kept):
            kept.append(i)
    return np.array(source).reshape(-1, 3)[kept], np.array(target).reshape(-1, 3)[kept]


def fit(canvas: bpy.types.Object, canvas_cage: bpy.types.Object, body: bpy.types.Object,
        body_cage: bpy.types.Object) -> np.ndarray:
    """The canvas's vertices fitted onto a body of another topology: bound to the canvas's cage
    version, placed at the same place of the body's, moved by the difference of the landmarks, and
    laid onto the body's skin, each on its own region, side, and finger."""
    if len(canvas_cage.data.polygons) > len(body_cage.data.polygons):
        raise ArtPipelineError(f'"{body_cage.name}" has fewer faces than the canvas\'s cage; run tools/art/Sync-CageAsset.ps1.')
    cage_faces, cage_corners = _loop_triangles(canvas_cage.data)
    cage_corners = _loop_vertices(canvas_cage.data)[cage_corners]
    source_co, target_co = _positions(canvas_cage.data), _positions(body_cage.data)
    on_cage = _Nearest(source_co, cage_faces, cage_corners, _cage_keys(canvas_cage.data))

    mesh = canvas.data
    co = _positions(mesh)
    face_keys = _keys(mesh)
    vertex_keys = [None] * len(co)
    for polygon in mesh.polygons:
        for v in polygon.vertices:
            if vertex_keys[v] is None:
                vertex_keys[v] = face_keys[polygon.index]

    def place(points: np.ndarray, keys: list) -> np.ndarray:
        triangles, weights = on_cage.find(points, keys)
        placed = np.empty_like(points)
        for i, (t, w) in enumerate(zip(triangles, weights)):
            a, b, c = (source_co[v] for v in cage_corners[t])
            normal = np.cross(b - a, c - a)
            normal /= np.linalg.norm(normal)
            offset = points[i] - (a * w[0] + b * w[1] + c * w[2])
            ta, tb, tc = (target_co[v] for v in cage_corners[t])
            target_normal = np.cross(tb - ta, tc - ta)
            target_normal /= np.linalg.norm(target_normal)
            # The offset keeps its height over the cage and turns with the cage's face.
            along = offset - normal * offset.dot(normal)
            placed[i] = ta * w[0] + tb * w[1] + tc * w[2] + target_normal * offset.dot(normal) + _turn(along, normal, target_normal)
        return placed

    skin = [k is not None for k in vertex_keys]
    placed = co.copy()
    chosen = np.nonzero(skin)[0]
    placed[chosen] = place(co[chosen], [vertex_keys[i] for i in chosen])

    source, target = _landmark_pairs(_marks(canvas), _marks(body))
    if len(source):
        nearest = [int(np.argmin(np.linalg.norm(co[chosen] - p, axis=1))) for p in source]
        moved = place(source, [vertex_keys[chosen[i]] for i in nearest])
        kernel = np.exp(-np.sum((moved[:, None] - moved[None]) ** 2, axis=-1) / LANDMARK_WIDTH ** 2)
        weights = np.linalg.solve(kernel + LANDMARK_SMOOTHING * np.eye(len(kernel)), target - moved)
        reach = np.exp(-np.sum((placed[chosen][:, None] - moved[None]) ** 2, axis=-1) / LANDMARK_WIDTH ** 2)
        placed[chosen] += reach @ weights

    faces, corners = _loop_triangles(body.data)
    corners = _loop_vertices(body.data)[corners]
    on_skin = _Nearest(_positions(body.data), faces, corners, _keys(body.data))
    triangles, weights = on_skin.find(placed[chosen], [vertex_keys[i] for i in chosen])
    body_co = _positions(body.data)
    placed[chosen] = (body_co[corners[triangles]] * weights[:, :, None]).sum(axis=1)
    return placed


def _turn(vector: np.ndarray, source: np.ndarray, target: np.ndarray) -> np.ndarray:
    """The vector turned by the rotation that takes the unit vector source to target."""
    axis = np.cross(source, target)
    sine, cosine = np.linalg.norm(axis), float(source.dot(target))
    if sine < 1e-9:
        return vector
    axis /= sine
    return vector * cosine + np.cross(axis, vector) * sine + axis * axis.dot(vector) * (1.0 - cosine)


def same_topology(a: bpy.types.Mesh, b: bpy.types.Mesh) -> bool:
    return (len(a.vertices) == len(b.vertices) and len(a.loops) == len(b.loops) and len(a.polygons) == len(b.polygons)
            and np.array_equal(_loop_vertices(a), _loop_vertices(b))
            and all(pa.loop_start == pb.loop_start for pa, pb in zip(a.polygons, b.polygons)))


def _pixels(image: bpy.types.Image) -> np.ndarray:
    width, height = image.size
    out = np.empty(width * height * 4, np.float32)
    image.pixels.foreach_get(out)
    return out.reshape(height, width, 4)


def _sample(rgba: np.ndarray, uv: np.ndarray) -> np.ndarray:
    """The image at the UVs, filtered between its four nearest texels with the colors weighted by
    their alpha, so transparent texels never darken an edge."""
    height, width = rgba.shape[:2]
    x = uv[:, 0] * width - 0.5
    y = uv[:, 1] * height - 0.5
    x0, y0 = np.floor(x).astype(np.int64), np.floor(y).astype(np.int64)
    fx, fy = (x - x0)[:, None], (y - y0)[:, None]
    premultiplied = np.concatenate([rgba[..., :3] * rgba[..., 3:], rgba[..., 3:]], axis=-1)

    def at(ix, iy):
        return premultiplied[iy.clip(0, height - 1), ix.clip(0, width - 1)]

    value = (at(x0, y0) * (1 - fx) * (1 - fy) + at(x0 + 1, y0) * fx * (1 - fy)
             + at(x0, y0 + 1) * (1 - fx) * fy + at(x0 + 1, y0 + 1) * fx * fy)
    alpha = value[:, 3:]
    color = np.where(alpha > 1e-6, value[:, :3] / np.maximum(alpha, 1e-6), 0.0)
    return np.concatenate([color, alpha], axis=1)


def _extend(rgba: np.ndarray, filled: np.ndarray) -> np.ndarray:
    """The image with its painted texels extended over the empty texels around them by MARGIN, each
    taking the alpha-weighted mean of its filled neighbours, so filtering at a UV seam takes paint
    and not the empty texels beyond it."""
    out = rgba.copy()
    have = filled & (rgba[..., 3] > 0.0)
    rows, columns = np.nonzero(have)
    if len(rows) == 0:
        return out
    height, width = have.shape
    top, bottom = max(rows.min() - MARGIN, 0), min(rows.max() + MARGIN + 1, height)
    left, right = max(columns.min() - MARGIN, 0), min(columns.max() + MARGIN + 1, width)
    window = out[top:bottom, left:right]
    known = have[top:bottom, left:right].copy()
    empty = ~filled[top:bottom, left:right]
    for _ in range(MARGIN):
        weighted = np.zeros(window.shape[:2] + (3,), np.float32)
        alpha = np.zeros(window.shape[:2], np.float32)
        count = np.zeros(window.shape[:2], np.float32)
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                if dy == 0 and dx == 0:
                    continue
                source = (slice(max(dy, 0), window.shape[0] + min(dy, 0)), slice(max(dx, 0), window.shape[1] + min(dx, 0)))
                target = (slice(max(-dy, 0), window.shape[0] + min(-dy, 0)), slice(max(-dx, 0), window.shape[1] + min(-dx, 0)))
                k = known[source]
                a = window[source][..., 3] * k
                weighted[target] += window[source][..., :3] * a[..., None]
                alpha[target] += a
                count[target] += k
        grow = empty & ~known & (count > 0)
        if not grow.any():
            break
        window[grow, :3] = weighted[grow] / np.maximum(alpha[grow], 1e-6)[:, None]
        window[grow, 3] = alpha[grow] / count[grow]
        known |= grow
    return out


def correspondence(canvas: bpy.types.Object, body: bpy.types.Object, part: str, label: str,
                   canvas_cage: bpy.types.Object | None, body_cage: bpy.types.Object | None) -> tuple[Texels, np.ndarray, np.ndarray]:
    """The body's texels of the painted material in its full paint layout, and per texel its point
    of the canvas: the canvas's paint UVs there and the point on the canvas in its rest pose."""
    index = _painted_index(body, part, label)
    texels = Texels(body.data, index, paint_size(body, index))
    canvas_mesh = canvas.data
    canvas_uv = bodies._uvs(canvas_mesh, bodies.PAINT_UV)
    canvas_co = _positions(canvas_mesh)[_loop_vertices(canvas_mesh)]
    if same_topology(canvas_mesh, body.data):
        loops, weights = texels.loops[texels.triangles], texels.weights
    else:
        if canvas_cage is None or body_cage is None:
            raise ArtPipelineError(f'"{body.name}" has another topology than the canvas and needs its cage version.')
        fitted = fit(canvas, canvas_cage, body, body_cage)
        faces, all_loops = _loop_triangles(canvas_mesh, _painted_index(canvas, part, canvas[CANVAS_BODY]))
        nearest = _Nearest(fitted, faces, _loop_vertices(canvas_mesh)[all_loops], _keys(canvas_mesh))
        body_keys = _keys(body.data)
        triangles, weights = nearest.find(texels.points(body.data), [body_keys[f] for f in texels.faces[texels.triangles]])
        loops = all_loops[triangles]
    uv = (canvas_uv[loops] * weights[:, :, None]).sum(axis=1)
    points = (canvas_co[loops] * weights[:, :, None]).sum(axis=1)
    return texels, uv, points


def move(canvas: bpy.types.Object, body: bpy.types.Object, part: str, label: str,
         canvas_cage: bpy.types.Object | None, body_cage: bpy.types.Object | None) -> np.ndarray:
    """The canvas's paint in the body's full paint layout, as straight RGBA rows bottom up."""
    texels, uv, _points = correspondence(canvas, body, part, label, canvas_cage, body_cage)
    width, height = texels.size
    rgba = np.zeros((height * width, 4), np.float32)
    filled = np.zeros(height * width, bool)
    rgba[texels.places] = _sample(_pixels(bpy.data.images[image_name(canvas["paint"])]), uv)
    filled[texels.places] = True
    rgba = _extend(rgba.reshape(height, width, 4), filled.reshape(height, width))
    return np.round(rgba.clip(0.0, 1.0) * 255.0) / 255.0


def subdivided(body: bpy.types.Object) -> bpy.types.Object:
    """A copy of a joined body with every face cut in four: another topology of the same surface,
    with the same paint UV map, zones, digits, and landmarks."""
    copy = body.copy()
    copy.data = body.data.copy()
    copy.name = copy.data.name = f"{body.name}_Subdivided"
    bpy.context.scene.collection.objects.link(copy)
    bm = bmesh.new()
    bm.from_mesh(copy.data)
    bmesh.ops.subdivide_edges(bm, edges=bm.edges[:], cuts=1, use_grid_fill=True)
    bm.to_mesh(copy.data)
    bm.free()
    return copy


def projection_report(name: str, part: str, labels: list[str], cage_file: str) -> tuple[list[tuple[str, dict]], list[str]]:
    """How the move through the cage, the one a body of another topology gets, compares with the
    body's own move: per body, its paint moved through the cage to a subdivided copy of it, against
    the body's own move, exact when it has the canvas's topology and through the cage otherwise, in
    the same paint layout, as the distance on the canvas between the points both moves take, over
    every texel and over the painted ones, in millimeters. The canvas's own body must come out where
    the exact move puts it, within PROJECTION_OWN_MEDIAN and PROJECTION_OWN_P95 over the painted
    texels. For another body of the canvas's topology the distances are the part of the bodies'
    difference in form that the cage and the landmarks place otherwise than the shared topology
    does, and for a body of another topology how much the move through the cage depends on the
    body's own faces."""
    canvas, canvas_cage, _image_ = canvas_in_file(name)
    collection = _collection("_Projection")
    found, errors = [], []
    try:
        for label in labels:
            body = bpy.data.objects[f"Paint_{label}"]
            body_cage = _cage_version(cage_file, label, f"_Projection_Cage_{label}", collection)
            test = subdivided(body)
            exact = same_topology(canvas.data, body.data)
            own = (None, None) if exact else (canvas_cage, body_cage)
            exact_texels, _uv, exact_points = correspondence(canvas, body, part, label, *own)
            test_texels, _uv, test_points = correspondence(canvas, test, part, label, canvas_cage, body_cage)
            exact_image = move(canvas, body, part, label, *own)
            shared, exact_at, test_at = np.intersect1d(exact_texels.places, test_texels.places, return_indices=True)
            distance = np.linalg.norm(exact_points[exact_at] - test_points[test_at], axis=1) * 1000.0
            painted = exact_image.reshape(-1, 4)[shared, 3] > 0.1
            report = {"move": "topology" if exact else "cage", "texels": int(len(shared)), "painted": int(painted.sum()),
                      "medianMm": round(float(np.median(distance)), 2), "p95Mm": round(float(np.percentile(distance, 95)), 2)}
            if painted.any():
                report.update({"paintedMedianMm": round(float(np.median(distance[painted])), 2),
                               "paintedP95Mm": round(float(np.percentile(distance[painted], 95)), 2)})
                if label == canvas[CANVAS_BODY] and (report["paintedMedianMm"] > PROJECTION_OWN_MEDIAN
                                                     or report["paintedP95Mm"] > PROJECTION_OWN_P95):
                    errors.append(f"Through the cage, the paint lands {report['paintedMedianMm']} mm (median) and "
                                  f"{report['paintedP95Mm']} mm (95%) from its place on the canvas's own body {label}; "
                                  f"at most {PROJECTION_OWN_MEDIAN} and {PROJECTION_OWN_P95} mm.")
            found.append((label, report))
            _remove(test)
    finally:
        for obj in list(collection.objects):
            _remove(obj)
        bpy.data.collections.remove(collection)
    return found, errors


def crop(rgba: np.ndarray) -> tuple[np.ndarray, list[int]]:
    """The image cropped to whole blocks around its paint with a block of empty texels around it,
    so the clamped edge of the crop is transparent, and the crop's place in it as x, y, width, and
    height in texels; a block at the corner when nothing is painted."""
    rows, columns = np.nonzero(rgba[..., 3] > 0.0)
    if len(rows) == 0:
        return rgba[:CROP_BLOCK, :CROP_BLOCK].copy(), [0, 0, CROP_BLOCK, CROP_BLOCK]
    height, width = rgba.shape[:2]
    x0 = max(columns.min() // CROP_BLOCK - 1, 0) * CROP_BLOCK
    y0 = max(rows.min() // CROP_BLOCK - 1, 0) * CROP_BLOCK
    x1 = min((-(-(columns.max() + 1) // CROP_BLOCK) + 1) * CROP_BLOCK, width)
    y1 = min((-(-(rows.max() + 1) // CROP_BLOCK) + 1) * CROP_BLOCK, height)
    return rgba[y0:y1, x0:x1].copy(), [int(x0), int(y0), int(x1 - x0), int(y1 - y0)]


def store(image: bpy.types.Image, rgba: np.ndarray) -> None:
    """Writes straight RGBA pixels (rows bottom up, 0..1) into the image, resized to them, and packs
    it as a PNG. The image is packed from memory with a path relative to the file, so no file records
    the folder it was made in."""
    height, width = rgba.shape[:2]
    if tuple(image.size) != (width, height):
        image.scale(width, height)
    image.pixels.foreach_set(np.ascontiguousarray(rgba, dtype=np.float32).ravel())
    image.update()
    with tempfile.TemporaryDirectory() as scratch:
        path = os.path.join(scratch, f"{image.name}.png")
        image.filepath_raw = path
        image.file_format = "PNG"
        image.save()
        with open(path, "rb") as f:
            png = f.read()
    image.filepath_raw = f"//{image.name}.png"
    image.pack(data=png, data_len=len(png))
    image.source = "FILE"


def _image(name: str, size: tuple[int, int]) -> bpy.types.Image:
    image = bpy.data.images.get(name)
    if image is None:
        image = bpy.data.images.new(name, size[0], size[1], alpha=True)
        image.generated_color = (0.0, 0.0, 0.0, 0.0)
        image.alpha_mode = "STRAIGHT"
        store(image, np.zeros((size[1], size[0], 4), dtype=np.float32))
    image.use_fake_user = True
    return image


def place_crop(image: bpy.types.Image) -> tuple[list[int], list[int]]:
    """A body image's crop, x, y, width, and height, and its full layout, width and height."""
    rect, full = image.get(RECT), image.get(FULL)
    if rect is None or full is None:
        raise ArtPipelineError(f'Paint "{image.name}" lacks its custom properties "{RECT}" and "{FULL}".')
    return [int(v) for v in rect], [int(v) for v in full]


def _skin_of(material: bpy.types.Material) -> tuple[bpy.types.Image, float, float]:
    """The skin image, roughness, and metallic of a body's material or of its preview."""
    bsdf = next(n for n in material.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    roughness = material.get("skin_roughness", bsdf.inputs["Roughness"].default_value)
    return _skin_image(material), float(roughness), float(bsdf.inputs["Metallic"].default_value)


def layered_material(name: str, skin: tuple[bpy.types.Image, float, float], layers: list[bpy.types.Image],
                     live: bool = False) -> bpy.types.Material:
    """A material that shows the skin with the layers laid over it in order by their alpha, with their
    roughness, each read through the paint UV map at its crop and transparent outside it. A live
    material reads each layer's roughness from the image's custom property through a driver, so a
    painter sees a change of it at once."""
    image, skin_roughness, metallic = skin
    material = bpy.data.materials.new(name)
    if material.node_tree is None:
        material.use_nodes = True
    material["skin_roughness"] = skin_roughness
    nodes, links = material.node_tree.nodes, material.node_tree.links
    bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Metallic"].default_value = metallic
    bsdf.inputs["Roughness"].default_value = skin_roughness
    skin_uv = nodes.new("ShaderNodeUVMap")
    skin_uv.uv_map = bodies.SKIN_UV
    texture = nodes.new("ShaderNodeTexImage")
    texture.image = image
    links.new(skin_uv.outputs["UV"], texture.inputs["Vector"])
    paint_uv = nodes.new("ShaderNodeUVMap")
    paint_uv.uv_map = bodies.PAINT_UV
    color, roughness = texture.outputs["Color"], None
    for layer in layers:
        paint = nodes.new("ShaderNodeTexImage")
        paint.image = layer
        paint.extension = "CLIP"
        vector = paint_uv.outputs["UV"]
        if layer.get(RECT) is not None:
            rect, full = place_crop(layer)
            mapping = nodes.new("ShaderNodeMapping")
            mapping.vector_type = "POINT"
            mapping.inputs["Scale"].default_value = (full[0] / rect[2], full[1] / rect[3], 1.0)
            mapping.inputs["Location"].default_value = (-rect[0] / rect[2], -rect[1] / rect[3], 0.0)
            links.new(vector, mapping.inputs["Vector"])
            vector = mapping.outputs["Vector"]
        links.new(vector, paint.inputs["Vector"])
        mix = nodes.new("ShaderNodeMix")
        mix.data_type = "RGBA"
        links.new(paint.outputs["Alpha"], mix.inputs["Factor"])
        links.new(color, mix.inputs["A"])
        links.new(paint.outputs["Color"], mix.inputs["B"])
        color = mix.outputs["Result"]
        rough = nodes.new("ShaderNodeMix")
        rough.data_type = "FLOAT"
        links.new(paint.outputs["Alpha"], rough.inputs["Factor"])
        if roughness is None:
            rough.inputs["A"].default_value = skin_roughness
        else:
            links.new(roughness, rough.inputs["A"])
        rough.inputs["B"].default_value = float(layer.get("roughness", DEFAULT_ROUGHNESS))
        if live:
            driver = rough.inputs["B"].driver_add("default_value").driver
            variable = driver.variables.new()
            variable.targets[0].id_type = "IMAGE"
            variable.targets[0].id = layer
            variable.targets[0].data_path = '["roughness"]'
            driver.expression = variable.name
        roughness = rough.outputs["Result"]
    links.new(color, bsdf.inputs["Base Color"])
    if roughness is not None:
        links.new(roughness, bsdf.inputs["Roughness"])
    return material


def preview_material(obj: bpy.types.Object, index: int, name: str, layers: list[bpy.types.Image],
                     live: bool = False) -> None:
    """Gives the slot a material of that name that shows its skin with the layers, live as
    layered_material says, replacing an earlier preview."""
    old = obj.material_slots[index].material
    material = layered_material("_preview", _skin_of(old), layers, live)
    obj.material_slots[index].material = material
    if old.users == 0 and old.name.split(".")[0].startswith(("Preview_", "Canvas_")):
        bpy.data.materials.remove(old)
    material.name = name


def _shows_crop(material: bpy.types.Material, image: bpy.types.Image) -> bool:
    """Whether a preview material reads the image at the crop its custom properties give."""
    try:
        rect, full = place_crop(image)
    except ArtPipelineError:
        return False
    for node in material.node_tree.nodes:
        if node.type != "TEX_IMAGE" or node.image != image:
            continue
        link = next(iter(node.inputs["Vector"].links), None)
        mapping = link.from_node if link is not None else None
        if mapping is None or mapping.type != "MAPPING":
            return False
        scale = tuple(mapping.inputs["Scale"].default_value)[:2]
        location = tuple(mapping.inputs["Location"].default_value)[:2]
        expected_scale = (full[0] / rect[2], full[1] / rect[3])
        expected_location = (-rect[0] / rect[2], -rect[1] / rect[3])
        return all(abs(a - b) < 1e-5 for a, b in zip(scale + location, expected_scale + expected_location))
    return False


def bodies_in_file(name: str, part: str, labels: list[str]) -> list[tuple[bpy.types.Object, int, bpy.types.Image]]:
    """Each body's mesh, the slot of its painted material, and its paint image, first body first."""
    result = []
    for label in labels:
        body = bpy.data.objects.get(f"Paint_{label}")
        image = bpy.data.images.get(image_name(name, label))
        if body is None or image is None:
            raise ArtPipelineError(f"The file lacks Paint_{label} or {image_name(name, label)}; run tools/art/Sync-PaintAsset.ps1.")
        result.append((body, _painted_index(body, part, label), image))
    return result


def canvas_in_file(name: str) -> tuple[bpy.types.Object, bpy.types.Object, bpy.types.Image]:
    """The canvas, its cage version, and its image."""
    canvas = bpy.data.objects.get(CANVAS)
    canvas_cage = bpy.data.objects.get(CANVAS_CAGE)
    image = bpy.data.images.get(image_name(name))
    if canvas is None or canvas_cage is None or image is None or canvas.get(CANVAS_BODY) is None:
        raise ArtPipelineError(f"The file lacks the canvas {CANVAS}, {CANVAS_CAGE}, or {image_name(name)}; run "
                               "tools/art/Sync-PaintAsset.ps1.")
    return canvas, canvas_cage, image


def _body_cage(label: str) -> bpy.types.Object | None:
    return bpy.data.objects.get(cage.version_name(label))


def _moved_crop(name: str, part: str, canvas: tuple, body: tuple, label: str) -> tuple[np.ndarray, list[int], list[int]]:
    canvas_obj, canvas_cage, _image_ = canvas
    obj, index, _paint = body
    full = move(canvas_obj, obj, part, label, canvas_cage, _body_cage(label))
    cropped, rect = crop(full)
    return cropped, rect, [full.shape[1], full.shape[0]]


def refresh(name: str, part: str, labels: list[str]) -> list[str]:
    """Moves the canvas's paint to every body, cropped, with the canvas's roughness and metallic."""
    canvas = canvas_in_file(name)
    for label, body in zip(labels, bodies_in_file(name, part, labels)):
        cropped, rect, full = _moved_crop(name, part, canvas, body, label)
        image = body[2]
        store(image, cropped)
        image[RECT] = rect
        image[FULL] = full
        image["roughness"] = canvas[2].get("roughness", DEFAULT_ROUGHNESS)
        image["metallic"] = canvas[2].get("metallic", 0.0)
    return [image_name(name, label) for label in labels]


def _packed_errors(image: bpy.types.Image) -> list[str]:
    errors = []
    data = bytes(image.packed_file.data) if image.packed_file is not None else b""
    if not data.startswith(PNG_SIGNATURE):
        errors.append(f'Paint "{image.name}" must be packed into the file as a PNG.')
    for prop in ("roughness", "metallic"):
        value = image.get(prop)
        if not isinstance(value, (int, float)) or not 0.0 <= value <= 1.0:
            errors.append(f'Paint "{image.name}" needs the custom property "{prop}" from 0 to 1.')
    return errors


def paint_errors(name: str, part: str, labels: list[str]) -> list[str]:
    """The rules of a paint asset: the canvas with its cage version and its image, a packed RGBA PNG
    of its paint size with a roughness and metallic from 0 to 1; every body with its image, the
    canvas's paint moved to the body and cropped, with the canvas's roughness and metallic, so the
    file shows what the game will."""
    try:
        canvas = canvas_in_file(name)
        found = bodies_in_file(name, part, labels)
    except ArtPipelineError as error:
        return [str(error)]
    canvas_obj, _canvas_cage, canvas_image = canvas
    errors = _packed_errors(canvas_image)
    try:
        size = paint_size(canvas_obj, _painted_index(canvas_obj, part, canvas_obj[CANVAS_BODY]))
    except ArtPipelineError as error:
        return errors + [str(error)]
    if tuple(canvas_image.size) != size:
        errors.append(f'Paint "{canvas_image.name}" is {tuple(canvas_image.size)} pixels; its paint size is {size}.')
    if canvas_obj.data.uv_layers.get(bodies.PAINT_UV) is None:
        errors.append(f'The canvas lacks the UV map "{bodies.PAINT_UV}".')
    for body, index, image in found:
        errors += _packed_errors(image)
        if body.data.uv_layers.get(bodies.PAINT_UV) is None:
            errors.append(f'"{body.name}" lacks the UV map "{bodies.PAINT_UV}"; run tools/art/Sync-HumanBase.ps1 for its body.')
        elif not _shows_crop(body.material_slots[index].material, image):
            errors.append(f'"{body.name}" shows "{image.name}" at another crop than the image has; run tools/art/Sync-PaintAsset.ps1.')
    if errors:
        return errors
    for label, body in zip(labels, found):
        image = body[2]
        try:
            rect, full = place_crop(image)
            cropped, fresh_rect, fresh_full = _moved_crop(name, part, canvas, body, label)
        except ArtPipelineError as error:
            errors.append(str(error))
            continue
        width, height = image.size
        if rect != fresh_rect or full != fresh_full or (width, height) != (rect[2], rect[3]):
            errors.append(f'Paint "{image.name}" is not the move of "{canvas_image.name}"; run tools/art/Sync-PaintAsset.ps1.')
            continue
        difference = np.abs(_pixels(image) - cropped)
        if float(difference.max(initial=0.0)) > MOVE_TOLERANCE:
            errors.append(f'Paint "{image.name}" is not the move of "{canvas_image.name}"; run tools/art/Sync-PaintAsset.ps1.')
        for prop in ("roughness", "metallic"):
            if abs(float(image.get(prop, -1.0)) - float(canvas_image.get(prop, -2.0))) > 1e-6:
                errors.append(f'Paint "{image.name}" must have the {prop} of "{canvas_image.name}".')
    return errors


def paint_record(name: str) -> dict[str, float]:
    canvas = bpy.data.images[image_name(name)]
    return {"smoothness": round(1.0 - float(canvas["roughness"]), 2), "metallic": round(float(canvas["metallic"]), 2)}


def export_images(name: str, labels: list[str], folder: str) -> list[dict]:
    """Writes every body's image, byte for byte as it is packed, and returns its record: its name,
    path, crop, full layout, the fingerprint of the body it was moved to, and how it was moved."""
    written = []
    os.makedirs(folder, exist_ok=True)
    canvas = bpy.data.objects[CANVAS]
    for label in labels:
        image = bpy.data.images[image_name(name, label)]
        body = bpy.data.objects[f"Paint_{label}"]
        path = os.path.join(folder, f"{image.name}.png")
        with open(path, "wb") as f:
            f.write(bytes(image.packed_file.data))
        rect, full = place_crop(image)
        written.append({"name": image.name, "path": path, "rect": rect, "layout": full,
                        "bodyPaintSha256": body[LAYOUT],
                        "move": "topology" if same_topology(canvas.data, body.data) else "cage"})
    return written


def cage_record() -> str | None:
    """The SHA-256 of the cage file the bodies' cage versions came from, when a body was moved
    through the cage."""
    return bpy.context.scene.get(CAGE_SHA)


def _ground(collection: bpy.types.Collection, low: float, high: float) -> None:
    mesh = bpy.data.meshes.new("Reference_Ground")
    half = max(high - low, 3.0) / 2.0 + 1.0
    mesh.from_pydata([(-half, -2.0, 0.0), (half, -2.0, 0.0), (half, 2.0, 0.0), (-half, 2.0, 0.0)], [], [(0, 1, 2, 3)])
    material = bpy.data.materials.new("Reference_Ground")
    material.use_nodes = True
    bsdf = next(n for n in material.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = (0.08, 0.11, 0.08, 1.0)
    bsdf.inputs["Roughness"].default_value = 0.9
    mesh.materials.append(material)
    ground = bpy.data.objects.new("Reference_Ground", mesh)
    ground.location.x = (low + high) / 2.0
    ground.hide_select = True
    collection.objects.link(ground)


def _cage_version(cage_file: str, label: str, name: str, collection: bpy.types.Collection) -> bpy.types.Object:
    """A body's version of the garment cage, from the cage's file, as a hidden mesh."""
    with bpy.data.libraries.load(cage_file, link=False) as (source, target):
        wanted = cage.version_name(label)
        if wanted not in source.meshes:
            raise ArtPipelineError(f"{cage_file} has no cage version {wanted}; run tools/art/Sync-CageAsset.ps1.")
        target.meshes = [wanted]
    mesh = target.meshes[0]
    mesh.name = name
    obj = bpy.data.objects.new(name, mesh)
    collection.objects.link(obj)
    obj.hide_select = True
    obj.hide_render = True
    obj.hide_set(True)
    return obj


def _file_sha256(path: str) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def _remove(obj: bpy.types.Object | None) -> None:
    if obj is None:
        return
    mesh = obj.data if obj.type == "MESH" else None
    bpy.data.objects.remove(obj, do_unlink=True)
    if mesh is not None and mesh.users == 0:
        bpy.data.meshes.remove(mesh)


def _merge_images() -> None:
    """A skin image that a body brought again, such as T_Body_Male.001 next to the canvas's
    T_Body_Male, takes the place and the name of the older one, so the canvas shows the current skin
    and the file keeps each image once."""
    for image in list(bpy.data.images):
        base, dot, number = image.name.rpartition(".")
        if not dot or not number.isdigit() or base.startswith("T_Paint_"):
            continue
        old = bpy.data.images.get(base)
        if old is None:
            continue
        old.user_remap(image)
        bpy.data.images.remove(old)
        image.name = base


def _new_canvas(name: str, part: str, body: bpy.types.Object, label: str, cage_file: str | None,
                rgba: np.ndarray | None) -> None:
    """Makes the canvas a copy of a body of the file, with materials of its own, Canvas_<Part>_<Label>,
    that body's cage version, and the paint, or an empty image, replacing an earlier canvas."""
    if cage_file is None:
        raise ArtPipelineError("A canvas needs the garment cage of its body's skeleton.")
    _remove(bpy.data.objects.get(CANVAS))
    _remove(bpy.data.objects.get(CANVAS_CAGE))
    for material in [m for m in bpy.data.materials if m.name.startswith("Canvas_") and m.users == 0]:
        bpy.data.materials.remove(material)
    collection = _collection(CANVAS_COLLECTION)
    canvas = body.copy()
    canvas.data = body.data.copy()
    canvas.name = CANVAS
    canvas.data.name = CANVAS
    canvas.location = (-BODY_SPACING, 0.0, 0.0)
    for c in list(canvas.users_collection):
        c.objects.unlink(canvas)
    collection.objects.link(canvas)
    for key in (LAYOUT,):
        if key in canvas:
            del canvas[key]
    canvas[CANVAS_BODY] = label
    canvas["paint"] = name
    for slot in canvas.material_slots:
        if slot.material is not None:
            own = slot.material.copy()
            own.name = "Canvas_" + _skin_material_name(slot.material)[len("M_"):]
            slot.material = own
    # The canvas is painted in its paint UV map.
    canvas.data.uv_layers.active = canvas.data.uv_layers[bodies.PAINT_UV]
    _cage_version(cage_file, label, CANVAS_CAGE, collection)
    index = _painted_index(canvas, part, label)
    size = paint_size(canvas, index)
    image = bpy.data.images.get(image_name(name))
    kept = {"roughness": DEFAULT_ROUGHNESS, "metallic": 0.0}
    if image is not None:
        kept = {prop: image.get(prop, kept[prop]) for prop in kept}
        bpy.data.images.remove(image)
    image = _image(image_name(name), size)
    if rgba is not None:
        store(image, rgba)
    image["roughness"], image["metallic"] = kept["roughness"], kept["metallic"]
    preview_material(canvas, index, f"Canvas_{part}_{label}", [image], live=True)


def _keep_cages(canvas: bpy.types.Object, joined: list[bpy.types.Object], labels: list[str], cage_file: str | None) -> None:
    """Gives every body of another topology than the canvas its cage version, which the move through
    the cage reads, and takes it from every body of the canvas's topology."""
    for body, root in zip(joined, labels):
        if same_topology(canvas.data, body.data):
            _remove(_body_cage(root))
        elif _body_cage(root) is None:
            if cage_file is None:
                raise ArtPipelineError(f"The body {root} has another topology than the canvas and needs the garment cage.")
            _cage_version(cage_file, root, cage.version_name(root), _collection(CAGE_COLLECTION))
    if any(_body_cage(root) for root in labels):
        bpy.context.scene[CAGE_SHA] = _file_sha256(cage_file)
    elif CAGE_SHA in bpy.context.scene:
        del bpy.context.scene[CAGE_SHA]


def stale_errors(name: str, part: str, body_list: list[tuple[str, str]], cage_file: str | None) -> list[str]:
    """Why the open file is not what a sync gives now: a broken rule, a body that changed in its own
    file, or a cage version that changed in the cage's file."""
    labels = [root for _path, root in body_list]
    errors = paint_errors(name, part, labels)
    if errors:
        return errors
    scratch = _collection("_Stale")
    try:
        for path, root in body_list:
            fresh = _load_body(path, root, scratch, f"_stale_{root}")
            try:
                if fresh.get(LAYOUT) != bpy.data.objects[f"Paint_{root}"].get(LAYOUT):
                    errors.append(f"The body {root} changed in its file.")
            finally:
                _remove(fresh)
    finally:
        bpy.data.collections.remove(scratch)
    if cage_file is not None and bpy.context.scene.get(CAGE_SHA) not in (None, _file_sha256(cage_file)):
        errors.append("The cage changed in its file.")
    return errors


def sync(path: str, name: str, part: str, body_list: list[tuple[str, str]], cage_file: str | None,
         canvas_label: str | None = None, force: bool = False) -> bool:
    """Builds the paint file from the bodies (file, armature), or rebuilds its body meshes, keeps the
    canvas and its paint, moves the paint to every body, and saves. A new file's canvas is a copy of
    the first body; canvas_label moves the canvas to that body, with the paint moved to it. Returns
    False and leaves an existing file as it is when it is what a sync gives now."""
    labels = [root for _path, root in body_list]
    if canvas_label is not None and canvas_label not in labels:
        raise ArtPipelineError(f"The canvas can move only to a body of the layer: {', '.join(labels)}.")
    if os.path.isfile(path):
        bpy.ops.wm.open_mainfile(filepath=path)
        if not force and canvas_label is None and not stale_errors(name, part, body_list, cage_file):
            return False
    else:
        bpy.ops.wm.read_factory_settings(use_empty=True)
    for obj in [o for o in bpy.data.objects if o.name not in (CANVAS, CANVAS_CAGE)]:
        _remove(obj)
    if CAGE_SHA in bpy.context.scene:
        del bpy.context.scene[CAGE_SHA]
    bpy.data.orphans_purge(do_local_ids=True, do_linked_ids=True, do_recursive=True)
    reference = _collection(REFERENCE_COLLECTION)
    joined = []
    for index, (body_path, root) in enumerate(body_list):
        body = _load_body(body_path, root, reference, f"Paint_{root}")
        body.location.x = BODY_SPACING * index
        joined.append(body)
    _merge_images()
    canvas = bpy.data.objects.get(CANVAS)
    if canvas is None:
        _new_canvas(name, part, joined[0], labels[0], cage_file, None)
        canvas = bpy.data.objects[CANVAS]
    _keep_cages(canvas, joined, labels, cage_file)
    if canvas_label is not None:
        body = joined[labels.index(canvas_label)]
        rgba = move(canvas, body, part, canvas_label, bpy.data.objects[CANVAS_CAGE], _body_cage(canvas_label))
        _new_canvas(name, part, body, canvas_label, cage_file, rgba)
        canvas = bpy.data.objects[CANVAS]
        # Bodies that shared the old canvas's topology may not share the new one's.
        _keep_cages(canvas, joined, labels, cage_file)
    for root in labels:
        _image(image_name(name, root), (CROP_BLOCK, CROP_BLOCK))
    refresh(name, part, labels)
    # The previews read the images at their new crops, and the canvas the paint as it is painted.
    for body, root in zip(joined, labels):
        preview_material(body, _painted_index(body, part, root), f"Preview_{part}_{root}", [bpy.data.images[image_name(name, root)]])
    canvas_label = canvas[CANVAS_BODY]
    preview_material(canvas, _painted_index(canvas, part, canvas_label), f"Canvas_{part}_{canvas_label}",
                     [bpy.data.images[image_name(name)]], live=True)
    _ground(reference, -BODY_SPACING, BODY_SPACING * (len(body_list) - 1))
    relative_image_paths()
    for screen in bpy.data.screens:
        for area in screen.areas:
            for space in area.spaces:
                if space.type == "VIEW_3D":
                    space.shading.type = "MATERIAL"
    bpy.data.orphans_purge(do_local_ids=True, do_linked_ids=True, do_recursive=True)
    bpy.ops.wm.save_as_mainfile(filepath=path, compress=True, relative_remap=False)
    return True
