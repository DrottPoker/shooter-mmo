"""Garment templates: the form of a garment kind, as a mesh skinned to the shared skeleton, made on
the garment cage of its skeleton and fitted to every body of the cage. See
docs/EQUIPMENT_VISUALS_DESIGN.md.

The file holds, per body, the collection Export_<Label> with the body's armature <Label> and the
template's version <Name>_<Label> bound to it; in the Reference collection the body's source body,
eyes, teeth, and tongue, which show it and which the weights read; and in the Cage collection the
cage's version Cage_<Label> on every body, taken from the cage's file, whose SHA-256 the scene
keeps. The first body's version is the template a person shapes.

A recipe builds the first version from the first body's cage: the faces of its regions, cut at its
standard lines, offset into the band along the cage's normals, with more room at the armpits, its
UVs unfolded along seams where clothes have them, and every open edge turned inward. A modeled
template's first version is modeled by hand instead. Every other version is fitted from the first
through the cage: each vertex is bound to a triangle of the first body's cage, by barycentric
coordinates and an offset in the triangle's frame, and placed at the same triangle and offset of the
other body's cage, and a vertex or a face centre that then leaves the band is moved back into it.
Every body's cage has the same faces, so this needs nothing of the bodies' own topology. When the
cage changes, the first version is bound to the old cage and placed on the new one, so it follows. A
person's corrections of a fitted version are kept as the difference to the attribute "fitted", the
shape the last fitting gave, and the attribute "built" holds the shape the recipe gave the first
version, so a new build of a changed recipe carries the handmade shape over.

Every vertex takes the weights of the body where a ray from it inward along the cage's normal meets
the skin, interpolated from the corners of the skin's triangle and not smoothed, so it moves like
the skin it covers, and cloth that spans a pit moves with the pit's bottom. The attribute
"transferred_weights" holds a checksum of the weights the last transfer gave each vertex of the first
version; a vertex whose weights differ from it was painted by hand and keeps them, also in the other
versions."""

import hashlib
import json
import math
import os
import zlib

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree
from mathutils.interpolate import poly_3d_calc
from mathutils.kdtree import KDTree

from . import bodies, body_files, cage, poses
from .scene import ArtPipelineError, relative_image_paths

FITTED = "fitted"
BUILT = "built"
TRANSFERRED = "transferred_weights"
CAGE_COLLECTION = "Cage"
CAGE_SHA = "cage_sha256"
RECIPE = "recipe"
MODELED = "modeled"
MAX_INFLUENCES = 4
# How far a fitted vertex or a weight may differ from what a sync gives before the file is stale.
FIT_TOLERANCE = 1e-5
WEIGHT_TOLERANCE = 1e-4
# The distance from the cage each band allows, in meters: the smallest and the largest.
BANDS = {"tight": (0.0, 0.008), "mid": (0.005, 0.025), "outer": (0.010, 0.060), "loose": (0.010, None)}
SLOTS = ("head", "body", "legs", "hands", "feet", "back", "hair")
# A recipe builds a template this far from the cage.
OFFSETS = {"tight": 0.004, "mid": 0.010, "outer": 0.020, "loose": 0.020}
# The ease of a recipe fades out over this distance from the lower half of an armhole, the part of
# the border between the trunk and an arm that lies this far or more below the shoulder joint.
EASE_REACH = 0.06
ARMPIT_DROP = 0.04
# A turned edge folds back inside the cloth this far.
HEM_FLAP = 0.012
# A cage vertex this close to a line moves onto it before the cut, so the cut leaves no slivers and
# the edge runs straight.
LINE_SNAP = 0.008
UV_MARGIN = 0.02
# A vertex or a face centre that leaves its band moves back into it by the difference and this
# margin, a few times over, because moving one face moves the corners it shares.
SETTLE_MARGIN = 0.0005
SETTLE_PASSES = 4
# A ray for the weights looks this far into the body.
WEIGHT_REACH = 0.2
ALPHA_CUT = 0.5
REVIEW_GREY = (0.62, 0.62, 0.62, 1.0)
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


def cage_object(label: str) -> bpy.types.Object:
    obj = bpy.data.objects.get(cage.version_name(label))
    if obj is None or obj.type != "MESH":
        raise ArtPipelineError(f'The garment has no cage "{cage.version_name(label)}"; run tools/Sync-GarmentAsset.ps1.')
    return obj


def file_sha256(path: str) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def recipe_text(recipe) -> str:
    """A recipe as the file keeps it, with its numbers as floats, so 0 and 0.0 read alike."""
    if isinstance(recipe, dict):
        recipe = {**recipe, "ease": float(recipe["ease"]), "hem": float(recipe["hem"])}
    return json.dumps(recipe, sort_keys=True, separators=(",", ":"))


def _triangles(mesh: bpy.types.Mesh) -> list[tuple[int, int, int, int]]:
    """Every polygon as a fan from its first corner: (polygon, a, b, c). The order depends only on
    the topology, so meshes of the same topology give the same triangles."""
    found = []
    for polygon in mesh.polygons:
        corners = list(polygon.vertices)
        for i in range(1, len(corners) - 1):
            found.append((polygon.index, corners[0], corners[i], corners[i + 1]))
    return found


class Surface:
    """A mesh's surface for fitting, such as a body or a cage: its triangles, their frames, and the
    mesh's weights over the given bones, or over its own vertex groups."""

    def __init__(self, obj: bpy.types.Object, names: list[str] | None = None):
        mesh = obj.data
        self.co = bodies.world_positions(obj)
        self.triangles = _triangles(mesh)
        self.tree = BVHTree.FromPolygons([tuple(p) for p in self.co], [t[1:] for t in self.triangles])
        self.names = names if names is not None else [g.name for g in obj.vertex_groups]
        self.weights = bodies.weights_of(obj, self.names) if self.names else np.zeros((len(self.co), 0))

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
            raise ArtPipelineError(f"No surface near {tuple(point)}.")
        weights = poly_3d_calc(self.corners(triangle), found)
        tangent, bitangent, normal = self.frame(triangle)
        offset = point - found
        return triangle, weights, (offset.dot(tangent), offset.dot(bitangent), offset.dot(normal))

    def place(self, triangle: int, weights, offset) -> Vector:
        corners = self.corners(triangle)
        point = corners[0] * weights[0] + corners[1] * weights[1] + corners[2] * weights[2]
        tangent, bitangent, normal = self.frame(triangle)
        return point + tangent * offset[0] + bitangent * offset[1] + normal * offset[2]

    def weights_at(self, triangle: int, weights) -> np.ndarray:
        _p, a, b, c = self.triangles[triangle]
        return _limited(self.weights[a] * weights[0] + self.weights[b] * weights[1] + self.weights[c] * weights[2])

    def weights_under(self, point: Vector, direction: Vector) -> np.ndarray:
        """The weights where a ray from the point meets the surface, or at the nearest point when
        it meets nothing."""
        location, _normal, triangle, _distance = self.tree.ray_cast(point, direction, WEIGHT_REACH)
        if location is None:
            location, _normal, triangle, _distance = self.tree.find_nearest(point)
        return self.weights_at(triangle, poly_3d_calc(self.corners(triangle), location))

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


class RegionSkin:
    """A body's skin with its weights, split by the regions and sides of the cage, so cloth takes the
    weights of the body part it lies on: a sleeve's inside at the armpit those of the arm, not of
    the trunk next to it."""

    def __init__(self, body: bpy.types.Object, names: list[str]):
        self.surface = Surface(body, names)
        zones = bodies.zones_of(body)
        centres = np.array([self.surface.co[list(t[1:])].mean(axis=0) for t in self.surface.triangles])
        self.trees = {}
        for region in cage.REGIONS:
            wanted = {bodies.ZONES.index(z) for z in cage.SKIN_ZONES[region]}
            for side in (("L", "R") if region in cage.SIDED else ("",)):
                found = [i for i, t in enumerate(self.surface.triangles)
                         if zones[t[0]] in wanted and (not side or cage.side_of(centres[i][0]) == side)]
                tree = BVHTree.FromPolygons([tuple(p) for p in self.surface.co],
                                            [self.surface.triangles[i][1:] for i in found])
                self.trees[(region, side)] = (tree, found)

    def weights(self, key: tuple[str, str], point: Vector) -> np.ndarray:
        tree, found = self.trees[key]
        location, _normal, index, _distance = tree.find_nearest(point)
        if location is None:
            location, _normal, triangle, _distance = self.surface.tree.find_nearest(point)
        else:
            triangle = found[index]
        return self.surface.weights_at(triangle, poly_3d_calc(self.surface.corners(triangle), location))


def cage_keys(cage_obj: bpy.types.Object, surface: Surface) -> list[tuple[str, str]]:
    """The region and side of every triangle of a cage's surface."""
    regions = np.empty(len(cage_obj.data.polygons), np.int32)
    cage_obj.data.attributes["region"].data.foreach_get("value", regions)
    keys = []
    for polygon, a, b, c in surface.triangles:
        region = cage.REGIONS[int(regions[polygon])]
        x = surface.co[[a, b, c]].mean(axis=0)[0]
        keys.append((region, cage.side_of(x) if region in cage.SIDED else ""))
    return keys


def _settle(positions: np.ndarray, polygons: list[tuple[int, ...]], surface: Surface, low: float,
            high: float | None) -> np.ndarray:
    """The positions with every vertex, and the corners of every face whose centre, that lies closer
    to the surface than low or farther than high moved along the surface's normal there into the
    band, so the whole surface keeps it, not only its corners."""
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


def append_cages(cage_file: str, labels: list[str]) -> None:
    """The cage's version on every body, taken from the cage's file into the Cage collection,
    replacing earlier ones, and the file's SHA-256 kept on the scene."""
    wanted = [cage.version_name(label) for label in labels]
    for name in wanted:
        old = bpy.data.objects.get(name)
        if old is not None:
            mesh = old.data
            bpy.data.objects.remove(old, do_unlink=True)
            if mesh is not None and mesh.users == 0:
                bpy.data.meshes.remove(mesh)
    with bpy.data.libraries.load(cage_file, link=False) as (source, target):
        missing = [n for n in wanted if n not in source.meshes]
        if missing:
            raise ArtPipelineError(f"{cage_file} has no cage versions {missing}; run tools/Sync-CageAsset.ps1.")
        target.meshes = wanted
    collection = body_files.collection(CAGE_COLLECTION)
    for mesh in target.meshes:
        mesh.name = mesh.name.split(".")[0]
        obj = bpy.data.objects.new(mesh.name, mesh)
        collection.objects.link(obj)
        obj.hide_select = True
        obj.hide_render = True
        obj.hide_set(True)
    bpy.context.scene[CAGE_SHA] = file_sha256(cage_file)


def _first_version(name: str, labels: list[str]) -> bpy.types.Object:
    obj = bpy.data.objects.get(version_name(name, labels[0]))
    if obj is None or obj.type != "MESH":
        raise ArtPipelineError(f'The garment has no version "{version_name(name, labels[0])}" on its first body.')
    return obj


def _material(name: str) -> bpy.types.Material:
    """The template's material: a neutral grey without an image, drawn from both sides like the
    game draws garments. Its textures bring their own materials."""
    material = bpy.data.materials.get(material_name(name)) or bpy.data.materials.new(material_name(name))
    material.use_nodes = True
    bsdf = next(n for n in material.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = REVIEW_GREY
    bsdf.inputs["Roughness"].default_value = 0.7
    bsdf.inputs["Metallic"].default_value = 0.0
    material.use_backface_culling = False
    return material


def _armpits(cage_obj: bpy.types.Object, arm: bpy.types.Object) -> np.ndarray:
    """The points of the cage on the lower half of each armhole, the border between the trunk and an
    arm, where the skin folds when the arms swing."""
    mesh = cage_obj.data
    regions = np.empty(len(mesh.polygons), np.int32)
    mesh.attributes["region"].data.foreach_get("value", regions)
    seen: dict[int, set[int]] = {}
    for polygon, region in zip(mesh.polygons, regions):
        for v in polygon.vertices:
            seen.setdefault(v, set()).add(int(region))
    trunk, arms = cage.REGIONS.index("trunk"), cage.REGIONS.index("arms")
    co = bodies.world_positions(cage_obj)
    points = []
    for v, found in seen.items():
        if trunk in found and arms in found:
            bone = arm.data.bones[f"UpperArm_{cage.side_of(co[v][0])}"]
            if co[v][2] <= (arm.matrix_world @ bone.head_local).z - ARMPIT_DROP:
                points.append(co[v])
    return np.array(points).reshape(-1, 3)


def _cut(cage_obj: bpy.types.Object, arm: bpy.types.Object, recipe: dict, lines: list[dict]) -> bmesh.types.BMesh:
    """The first body's cage with the faces of the recipe's regions, cut at its lines, each keeping
    the side toward its bone's head or its tail."""
    bm = bmesh.new()
    bm.from_mesh(cage_obj.data)
    region_layer = bm.faces.layers.int.get("region")
    wanted = {cage.REGIONS.index(r) for r in recipe["regions"]}
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if f[region_layer] not in wanted], context="FACES")
    by_name = {line["name"]: line for line in lines}
    for cut in recipe["cuts"]:
        line = by_name[cut["line"]]
        region = cage.REGIONS.index(line["region"])
        for side, point, normal in cage.line_planes(arm.name, line):
            faces = [f for f in bm.faces if f[region_layer] == region and (not side or cage.side_of(f.calc_center_median().x) == side)]
            if not faces:
                continue
            verts = list({v for f in faces for v in f.verts})
            for v in verts:
                distance = (v.co - point).dot(normal)
                if abs(distance) < LINE_SNAP:
                    v.co -= normal * distance
            geom = list({e for f in faces for e in f.edges}) + verts + faces
            bmesh.ops.bisect_plane(bm, geom=geom, dist=1e-6, plane_co=point, plane_no=normal,
                                   clear_outer=cut["keep"] == "head", clear_inner=cut["keep"] == "tail")
    loose = [v for v in bm.verts if not v.link_faces]
    bmesh.ops.delete(bm, geom=loose, context="VERTS")
    bm.normal_update()
    return bm


def _mark_seams(bm: bmesh.types.BMesh, arm: bpy.types.Object) -> None:
    """Seams where clothes have them: between the front and the back of the trunk and the neck,
    down the sides and over the shoulders, along the underside of each arm, and where an arm or the
    neck meets the trunk."""
    region_layer = bm.faces.layers.int.get("region")
    trunk, neck, arms = (cage.REGIONS.index(r) for r in ("trunk", "neck", "arms"))
    middle = float(np.mean([v.co.y for v in bm.verts]))

    def angle(face) -> float:
        # Around the arm's upper bone, measured so it wraps at the underside of the arm.
        side = cage.side_of(face.calc_center_median().x)
        bone = arm.data.bones[f"UpperArm_{side}"]
        head, tail = arm.matrix_world @ bone.head_local, arm.matrix_world @ bone.tail_local
        axis = (tail - head).normalized()
        down = Vector((0.0, 0.0, -1.0))
        under = (down - axis * down.dot(axis)).normalized()
        across = axis.cross(under)
        offset = face.calc_center_median() - head
        return math.atan2(offset.dot(across), -offset.dot(under))

    for edge in bm.edges:
        faces = list(edge.link_faces)
        if len(faces) != 2:
            continue
        a, b = faces
        ra, rb = a[region_layer], b[region_layer]
        if ra != rb:
            edge.seam = True
        elif ra in (trunk, neck):
            edge.seam = (a.calc_center_median().y < middle) != (b.calc_center_median().y < middle)
        elif ra == arms:
            ta, tb = angle(a), angle(b)
            edge.seam = (ta < 0.0) != (tb < 0.0) and abs(ta) > math.pi / 2 and abs(tb) > math.pi / 2


def _unwrap(mesh: bpy.types.Mesh) -> None:
    """UVs unfolded along the mesh's seams and borders and packed into the image."""
    obj = bpy.data.objects.new("_unwrap", mesh)
    bpy.context.scene.collection.objects.link(obj)
    try:
        for other in bpy.context.view_layer.objects:
            other.select_set(False)
        bpy.context.view_layer.objects.active = obj
        obj.select_set(True)
        if not mesh.uv_layers:
            mesh.uv_layers.new(name="UVMap")
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.select_all(action="SELECT")
        bpy.ops.uv.unwrap(method="ANGLE_BASED", margin=UV_MARGIN)
        bpy.ops.object.mode_set(mode="OBJECT")
    finally:
        bpy.data.objects.remove(obj, do_unlink=True)


def _turn_edges(mesh: bpy.types.Mesh, depth: float) -> None:
    """Every open edge turned inward: a rim of the given depth toward the body and a flap back
    inside the cloth, with the UVs of the edge, so a hem shows the cloth's thickness."""
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bm.normal_update()
    uv = bm.loops.layers.uv.active
    border = [e for e in bm.edges if e.is_boundary]
    rim, flap = {}, {}
    for v in {v for e in border for v in e.verts}:
        along = [e.other_vert(v).co - v.co for e in v.link_edges if e.is_boundary]
        tangent = (along[0] - along[1]).normalized() if len(along) == 2 else along[0].normalized()
        inward = v.normal.cross(tangent).normalized()
        # Toward the cloth: the side of the vertex's neighbors off the edge.
        inside = [e.other_vert(v).co - v.co for e in v.link_edges if not e.is_boundary]
        toward = sum(inside, Vector()) if inside else sum((f.calc_center_median() - v.co for f in v.link_faces), Vector())
        if inward.dot(toward) < 0.0:
            inward = -inward
        rim[v] = bm.verts.new(v.co - v.normal * depth)
        flap[v] = bm.verts.new(rim[v].co + inward * HEM_FLAP)
    for edge in border:
        face = edge.link_faces[0]
        loops = {loop.vert: loop for loop in face.loops}
        a, b = edge.verts
        # The new faces run the shared edge against the face's own direction.
        if loops[a].link_loop_next.vert != b:
            a, b = b, a
        for corners in ((b, a, rim[a], rim[b]), (rim[b], rim[a], flap[a], flap[b])):
            new = bm.faces.new(corners)
            new.material_index = face.material_index
            new.smooth = True
            for loop in new.loops:
                source = a if loop.vert in (a, rim[a], flap[a]) else b
                loop[uv].uv = loops[source][uv].uv
    bm.to_mesh(mesh)
    bm.free()


def build(name: str, band: str, recipe: dict, lines: list[dict], label: str) -> bpy.types.Mesh:
    """The template's first version as its recipe gives it, on the cage of the first body."""
    cage_obj = cage_object(label)
    arm = bpy.data.objects[label]
    bm = _cut(cage_obj, arm, recipe, lines)
    _mark_seams(bm, arm)
    mesh = bpy.data.meshes.new(version_name(name, label))
    bm.to_mesh(mesh)
    bm.free()
    # The cage's own attributes stay with the cage.
    for attribute in ("region", cage.FITTED):
        if attribute in mesh.attributes:
            mesh.attributes.remove(mesh.attributes[attribute])
    for polygon in mesh.polygons:
        polygon.use_smooth = True
    _unwrap(mesh)

    # Into the band along the cage's normals, with more room at the armpits.
    co = np.empty(len(mesh.vertices) * 3)
    mesh.vertices.foreach_get("co", co)
    co = co.reshape(-1, 3)
    normals = np.empty(len(mesh.vertices) * 3)
    mesh.vertex_normals.foreach_get("vector", normals)
    normals = normals.reshape(-1, 3)
    offset = np.full(len(co), OFFSETS[band])
    armpits = _armpits(cage_obj, arm)
    if recipe.get("ease", 0.0) > 0.0 and len(armpits):
        near = KDTree(len(armpits))
        for i, point in enumerate(armpits):
            near.insert(point, i)
        near.balance()
        for i, point in enumerate(co):
            distance = near.find(point)[2]
            share = max(0.0, 1.0 - distance / EASE_REACH)
            offset[i] += recipe["ease"] * share * share * (3.0 - 2.0 * share)
    co = co + normals * offset[:, None]
    surface = Surface(cage_obj, [])
    co = _settle(co, [tuple(p.vertices) for p in mesh.polygons], surface, *BANDS[band])
    mesh.vertices.foreach_set("co", co.reshape(-1))
    mesh.update()
    if recipe.get("hem", 0.0) > 0.0:
        _turn_edges(mesh, recipe["hem"])
        # The turned edges keep the band too.
        co = np.empty(len(mesh.vertices) * 3)
        mesh.vertices.foreach_get("co", co)
        co = _settle(co.reshape(-1, 3), [tuple(p.vertices) for p in mesh.polygons], surface, *BANDS[band])
        mesh.vertices.foreach_set("co", co.reshape(-1))
        mesh.update()
    mesh.validate()
    return mesh


def _plan(name: str, arms: list[bpy.types.Object], band: str, transfer_all: bool = False) -> dict:
    """What a sync gives every version: its positions, its weights over the first body's bones,
    the binding of the first version to its cage, and the checksums of the transferred weights of
    the first version, with the number of vertices painted by hand."""
    labels = [a.name for a in arms]
    first = _first_version(name, labels)
    # Every deforming bone, so weights painted by hand on cloth chain bones, which the body has
    # none of, are kept too.
    names = [b.name for b in arms[0].data.bones if b.use_deform]
    first_cage = Surface(cage_object(labels[0]), [])
    co = bodies.world_positions(first)
    binding = [first_cage.bind(Vector(p)) for p in co]
    polygons = [tuple(p.vertices) for p in first.data.polygons]
    # Every body's cage has the same triangles, so the regions of the first body's hold for all.
    keys = cage_keys(cage_object(labels[0]), first_cage)

    def weights_on(label: str, positions: np.ndarray, surface: Surface) -> np.ndarray:
        skin = RegionSkin(bodies.source_body(label), names)
        return _rounded_weights(np.array([skin.weights(keys[t], Vector(p)) for p, (t, _w, _o) in zip(positions, binding)]))

    transferred = weights_on(labels[0], co, first_cage)
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
    plan = {"names": names, "hand": int(hand.sum()), "binding": binding,
            "versions": {first.name: {"positions": None, "weights": first_weights, "checksums": checksums}}}
    for arm in arms[1:]:
        own = Surface(cage_object(arm.name), [])
        positions = _settle(np.array([own.place(t, w, o) for t, w, o in binding]), polygons, own, *BANDS[band])
        weights = weights_on(arm.name, positions, own)
        plan["versions"][version_name(name, arm.name)] = {
            "positions": positions, "weights": np.where(hand[:, None], first_weights, weights), "checksums": None}
    return plan


def _apply(plan: dict, name: str, arms: list[bpy.types.Object]) -> None:
    """Writes the plan: fits every other version, keeping its corrections, gives every version its
    weights and binding, and keeps the first version's binding to its cage."""
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
            for attribute in (TRANSFERRED, BUILT):
                if attribute in target.data.attributes:
                    target.data.attributes.remove(target.data.attributes[attribute])
            target.data.update()
        else:
            _store(target.data, TRANSFERRED, entry["checksums"], "INT")
        target.parent = arm
        target.matrix_parent_inverse = Matrix.Identity(4)
        bodies.set_weights(target, entry["weights"], names)
        bodies.bind(target, arm)


def _show_first(arms: list[bpy.types.Object]) -> None:
    """Shows the first body and its version in the viewport, the one a person edits, and hides the
    others, which stand at the same place, and the cages."""
    for index, arm in enumerate(arms):
        for obj in [arm] + list(arm.children_recursive):
            obj.hide_set(index > 0)
    collection = bpy.data.collections.get(CAGE_COLLECTION)
    for obj in (collection.objects if collection is not None else []):
        obj.hide_set(True)


def _state(name: str, labels: list[str]) -> str:
    """SHA-256 of everything a sync writes, so a sync that changes nothing leaves the file as it is."""
    digest = hashlib.sha256()
    digest.update(str(bpy.context.scene.get(CAGE_SHA)).encode("utf-8"))
    for label in labels:
        arm = bpy.data.objects.get(label)
        body = bodies.source_body(label)
        digest.update(label.encode("utf-8"))
        if arm is not None and arm.type == "ARMATURE" and body is not None:
            digest.update(body_files.fingerprint(arm, body).encode("utf-8"))
        shell = bpy.data.objects.get(cage.version_name(label))
        if shell is not None and shell.type == "MESH":
            digest.update((np.round(bodies.world_positions(shell), 6) + 0.0).tobytes())
        obj = bpy.data.objects.get(version_name(name, label))
        if obj is None or obj.type != "MESH":
            continue
        groups = sorted(g.name for g in obj.vertex_groups)
        digest.update(f"{obj.parent.name if obj.parent else ''}|{[m.type for m in obj.modifiers]}|{groups}".encode())
        digest.update(str(obj.get(RECIPE)).encode("utf-8"))
        digest.update((np.round(bodies.world_positions(obj), 6) + 0.0).tobytes())
        digest.update((np.round(bodies.weights_of(obj, groups), 5) + 0.0).tobytes())
        for attribute, kind in ((FITTED, "FLOAT_VECTOR"), (BUILT, "FLOAT_VECTOR"), (TRANSFERRED, "INT")):
            values = _attribute(obj.data, attribute, kind)
            digest.update(b"-" if values is None else (np.round(values, 6) + 0).tobytes())
    return digest.hexdigest()


def _new_first_version(name: str, band: str, recipe, lines: list[dict], first: bpy.types.Object) -> bpy.types.Object:
    """The first version built from the recipe and placed on the first body."""
    mesh = build(name, band, recipe, lines, first.name)
    obj = bpy.data.objects.new(mesh.name, mesh)
    bpy.data.collections[collection_name(first.name)].objects.link(obj)
    obj.parent = first
    mesh.materials.clear()
    mesh.materials.append(_material(name))
    for polygon in mesh.polygons:
        polygon.material_index = 0
    _store(mesh, BUILT, bodies.world_positions(obj))
    obj[RECIPE] = recipe_text(recipe)
    return obj


def create(file: str, name: str, band: str, recipe, lines: list[dict], body_list: list[tuple[str, str]],
           cage_file: str) -> None:
    """Writes the first file of a template: the bodies, their cages, and the first version as the
    recipe builds it, fitted to every body; a modeled template gets no first version, which a person
    models on the first body."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    arms = [body_files.append_body(path, root, collection_name(root)) for path, root in body_list]
    append_cages(cage_file, [a.name for a in arms])
    if recipe != MODELED:
        _new_first_version(name, band, recipe, lines, arms[0])
        _apply(_plan(name, arms, band, transfer_all=True), name, arms)
    _show_first(arms)
    relative_image_paths()
    bpy.ops.wm.save_as_mainfile(filepath=file, compress=True, relative_remap=False)


def _carry_over(old: bpy.types.Object, new: bpy.types.Object) -> int:
    """Gives a new build of the first version the handmade shape of the old one: each new vertex
    takes the difference between the old version and the old build at the nearest point of the old
    build. Returns how many old vertices had been shaped by hand."""
    built = _attribute(old.data, BUILT)
    shaped = bodies.world_positions(old)
    if built is None or len(built) != len(shaped):
        return 0
    difference = shaped - built
    near = KDTree(len(built))
    for i, point in enumerate(built):
        near.insert(point, i)
    near.balance()
    co = bodies.world_positions(new)
    moved = np.array([difference[near.find(point)[1]] for point in co])
    new.data.vertices.foreach_set("co", (co + moved).reshape(-1))
    new.data.update()
    return int((np.linalg.norm(difference, axis=1) > FIT_TOLERANCE).sum())


def sync(file: str, name: str, band: str, recipe, lines: list[dict], body_list: list[tuple[str, str]], cage_file: str,
         transfer_all: bool = False, rebuild: bool = False) -> tuple[bool, int, list[str]]:
    """Appends the bodies and the cages again that changed in their own files, moving the first
    version with its cage, builds the first version again from a changed recipe when asked, keeping
    its handmade shape, fits every other version again from the first, and gives every version its
    weights, keeping the corrections and the weights painted by hand, unless every weight is
    transferred again. Returns whether the file changed, the number of vertices with weights painted
    by hand, and notes for the report. An unchanged file is not saved, so its approvals stay valid."""
    bpy.ops.wm.open_mainfile(filepath=file)
    labels = [root for _path, root in body_list]
    notes = []
    before = _state(name, labels)
    stale = set(body_files.stale_bodies(body_list))
    for path, root in body_list:
        if root in stale:
            body_files.replace_body(path, root, collection_name(root))
    arms = [bpy.data.objects[label] for label in labels]
    # A body no longer of the cage leaves the file with its version, which is a change too.
    gone = [o for o in bpy.data.objects if o.type == "ARMATURE" and o.name not in labels]
    gone += [o for o in bpy.data.objects if o.name.startswith(cage.version_name("")) and
             o.name[len(cage.version_name("")):] not in labels]
    removed = len(gone)
    for obj in gone:
        for child in list(obj.children_recursive):
            bpy.data.objects.remove(child, do_unlink=True)
        bpy.data.objects.remove(obj, do_unlink=True)

    first = bpy.data.objects.get(version_name(name, labels[0]))
    if bpy.context.scene.get(CAGE_SHA) != file_sha256(cage_file) or any(
            bpy.data.objects.get(cage.version_name(label)) is None for label in labels):
        binding = None
        if first is not None and bpy.data.objects.get(cage.version_name(labels[0])) is not None:
            old = Surface(cage_object(labels[0]), [])
            binding = [old.bind(Vector(p)) for p in bodies.world_positions(first)]
        append_cages(cage_file, labels)
        if binding is not None:
            own = Surface(cage_object(labels[0]), [])
            first.data.vertices.foreach_set("co", np.array([own.place(t, w, o) for t, w, o in binding]).reshape(-1))
            first.data.update()
            notes.append(f"{first.name} moved with its changed cage.")

    if recipe != MODELED and (first is None or rebuild):
        new = _new_first_version(name, band, recipe, lines, arms[0])
        if first is not None:
            shaped = _carry_over(first, new)
            notes.append(f"{version_name(name, labels[0])} was built again from its recipe; {shaped} vertices keep "
                         "their handmade shape.")
            for obj in [bpy.data.objects.get(version_name(name, arm.name)) for arm in arms[1:]] + [first]:
                if obj is not None:
                    mesh = obj.data
                    bpy.data.objects.remove(obj, do_unlink=True)
                    if mesh.users == 0:
                        bpy.data.meshes.remove(mesh)
            new.name = new.data.name = version_name(name, labels[0])
        first = new
    if first is None:
        raise ArtPipelineError(f'Model the template "{version_name(name, labels[0])}" on the first body first.')

    plan = _plan(name, arms, band, transfer_all)
    _apply(plan, name, arms)
    if not removed and _state(name, labels) == before:
        return False, plan["hand"], notes
    _show_first(arms)
    relative_image_paths()
    bpy.ops.wm.save_as_mainfile(filepath=file, compress=True, relative_remap=False)
    return True, plan["hand"], notes


def stale_errors(name: str, band: str, recipe, body_list: list[tuple[str, str]], cage_file: str) -> list[str]:
    """A template is checked as a sync would leave it: its bodies and its cages are the current ones
    of their files, its first version was built from the recipe of its asset.json, every fitted
    version is fitted to the current first version, and every version has the weights a sync gives
    it."""
    labels = [root for _path, root in body_list]
    stale = body_files.stale_bodies(body_list)
    if stale:
        return [f"The bodies {stale} of the garment changed in their own files; run tools/Sync-GarmentAsset.ps1."]
    if bpy.context.scene.get(CAGE_SHA) != file_sha256(cage_file):
        return ["The garment's cage changed in its own file; run tools/Sync-GarmentAsset.ps1."]
    first = bpy.data.objects.get(version_name(name, labels[0]))
    if first is not None and recipe != MODELED and first.get(RECIPE) != recipe_text(recipe):
        return [f'"{first.name}" was built from another recipe than its asset.json names; run '
                "tools/Sync-GarmentAsset.ps1 -Rebuild."]
    arms = [bpy.data.objects[label] for label in labels]
    try:
        plan = _plan(name, arms, band)
    except ArtPipelineError:
        # garment_errors reports the missing first version or cage.
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
    """The model rules of a template: a version per body with the first version's topology and one
    material, within the triangle budget and its band from the cage, at every vertex and at the
    centre of every face, so a large flat face over a round shoulder cannot dip under the cage
    between its corners and cloth cannot sink into a pit, and UVs on the first version."""
    errors = []
    try:
        first = _first_version(name, labels)
    except ArtPipelineError as error:
        return [str(error)]
    if first.data.uv_layers.active is None:
        errors.append(f'"{first.name}" needs UVs that its textures are painted in.')
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
        try:
            distances, nearest = _distances(obj, label)
        except ArtPipelineError as error:
            errors.append(str(error))
            continue
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
                          f'{"any" if high is None else f"{high * 1000:.0f}"} mm from the cage at {int((near | far).sum())} '
                          f"vertices and face centres ({int(near.sum())} too close, {int(far.sum())} too far; "
                          f"{', '.join(worst)}), over {places}.")
    return errors


def _distances(obj: bpy.types.Object, label: str) -> tuple[np.ndarray, np.ndarray]:
    """The distance from the cage of every vertex and of the centre of every face, and the body
    polygon each is nearest to."""
    shell = Surface(cage_object(label), [])
    skin = Surface(bodies.source_body(label), [])
    co = bodies.world_positions(obj)
    points = [Vector(p) for p in list(co) + [co[list(p.vertices)].mean(axis=0) for p in obj.data.polygons]]
    return (np.array([shell.signed_distance(p)[0] for p in points]),
            np.array([skin.signed_distance(p)[1] for p in points], np.int64))


def report(name: str, labels: list[str]) -> list[str]:
    """How far every version lies from the cage, and its triangles."""
    lines = []
    for label in labels:
        obj = bpy.data.objects.get(version_name(name, label))
        if obj is None or bpy.data.objects.get(cage.version_name(label)) is None:
            continue
        distances, _polygons = _distances(obj, label)
        triangles = sum(len(p.vertices) - 2 for p in obj.data.polygons)
        lines.append(f"{obj.name}: {triangles} triangles, {distances.min() * 1000:.1f} to {distances.max() * 1000:.1f} mm "
                     f"from the cage, median {np.median(distances) * 1000:.1f} mm")
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


def read_manifests(manifest: str, cage_manifest: str) -> tuple[str, object, list[dict], str]:
    """The band and the recipe of a template's asset.json, and the standard lines and the file of
    its cage."""
    with open(manifest, encoding="utf-8") as handle:
        data = json.load(handle)
    _figure, lines = cage.read_manifest(cage_manifest)
    folder = os.path.dirname(os.path.abspath(cage_manifest))
    return str(data["band"]), data["recipe"], lines, os.path.join(folder, f"{os.path.basename(folder)}.blend")
