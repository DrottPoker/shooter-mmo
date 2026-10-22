"""What a garment covers on each body it fits, in the body's faces and vertices, which every body of
the shared topology numbers alike: the cover mask, the underlay, the pull-in, and the faces that
motion in the test poses and the clips hides as well. See docs/EQUIPMENT_VISUALS_DESIGN.md.

- The cover mask holds the body faces the garment hides. From sample points on a face, rays go out
  in a cone around the face normal; a face is covered when every ray hits the garment where its
  texture is opaque, or the body itself, before it gets out of the garment's reach, and at least one
  hits the garment. A ray that leaves through a hole, an open hem, or a transparent part of the
  texture leaves the face visible, and so does a face that only the body hides, such as the inside
  of the mouth.
- The underlay holds, for every garment face, the body face under it.
- The pull-in holds how far to move visible body vertices inward that lie closer to the garment
  than its band allows, such as the skin at a hem, so they stay under it in motion.
- In motion, the mask also hides the visible skin that comes out of the garment over it. Each
  visible vertex that the garment is over at rest keeps the point of the garment over it, its
  cover. The body, with the pull-in applied as the game applies it, takes every test pose and
  sampled frames of the clips, and every face of a vertex that comes out of its cover by more than
  the tolerance is hidden too. Skinning blends the bones of a fold, such as an armpit, so the gap
  between the skin and the cloth there closes in motion, whatever the weights; the hidden faces
  lie under the garment, so only a look into an opening sees past them, at the garment's inside.
  A garment that runs into another body part, such as a hem into a thigh when the body folds, is
  not skin coming out of its cover."""

import math
from dataclasses import dataclass

import bpy
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.interpolate import poly_3d_calc

from . import bodies, garment, poses
from .scene import ArtPipelineError

# How far rays look for the garment from the skin, per band, in meters.
REACH = {"tight": 0.03, "mid": 0.05, "outer": 0.10, "loose": 0.30}
CONE_ANGLE = math.radians(25.0)
CONE_RAYS = 6
SAMPLE_SPREAD = 0.6
ALPHA_CUT = 0.5
# Transparent layers a ray passes through before it counts as getting out.
MAX_LAYERS = 4
RAY_OFFSET = 1e-4
# Rays from the skin start this far off the face they sample, which need not be flat.
SKIN_OFFSET = 0.001
PULL_IN_MAX = 0.003
PULL_IN_CLEARANCE = 0.003
# Visible skin may come this far out of the garment that covers it in motion, in meters.
CLIP_TOLERANCE = 0.001
CLIP_FRAMES = 8
DATA_SCHEMA = 1


@dataclass
class Coverage:
    """What one version of a garment covers on its body."""

    label: str
    band: str
    covered: np.ndarray  # bool per body face, at rest and in motion
    underlay: np.ndarray  # body face per garment face
    pull_vertices: np.ndarray  # body vertices to move inward
    pull_distances: np.ndarray  # meters, per pulled vertex
    body_faces: int
    body_vertices: int
    garment_faces: int
    moved: np.ndarray | None = None  # bool per body face hidden for motion

    def visible_vertices(self, body: bpy.types.Object) -> np.ndarray:
        """The body vertices of every face the garment leaves visible."""
        visible = np.zeros(self.body_vertices, bool)
        for polygon in body.data.polygons:
            if not self.covered[polygon.index]:
                visible[list(polygon.vertices)] = True
        return np.nonzero(visible)[0]


class _Cloth:
    """A garment version's surface for rays: its triangles, the UVs of their corners, and the
    alpha of its texture."""

    def __init__(self, obj: bpy.types.Object, positions: np.ndarray, alpha: np.ndarray | None):
        mesh = obj.data
        self.triangles = []
        for polygon in mesh.polygons:
            vertices, loops = list(polygon.vertices), list(polygon.loop_indices)
            for i in range(1, len(vertices) - 1):
                self.triangles.append((polygon.index, (vertices[0], vertices[i], vertices[i + 1]),
                                       (loops[0], loops[i], loops[i + 1])))
        self.co = positions
        self.tree = BVHTree.FromPolygons([tuple(c) for c in positions], [t[1] for t in self.triangles])
        self.uv = None
        if mesh.uv_layers.active is not None:
            uv = np.empty(len(mesh.loops) * 2, np.float32)
            mesh.uv_layers.active.data.foreach_get("uv", uv)
            self.uv = uv.reshape(-1, 2)
        self.alpha = alpha

    def opaque(self, triangle: int, location: Vector) -> bool:
        if self.alpha is None or self.uv is None:
            return True
        _polygon, vertices, loops = self.triangles[triangle]
        weights = poly_3d_calc([Vector(self.co[v]) for v in vertices], location)
        u, v = sum(self.uv[loop] * w for loop, w in zip(loops, weights))
        height, width = self.alpha.shape
        x = min(max(int(u * width), 0), width - 1)
        y = min(max(int(v * height), 0), height - 1)
        return bool(self.alpha[y, x] >= ALPHA_CUT)


def _alpha(obj: bpy.types.Object) -> np.ndarray | None:
    """The alpha of the image that cuts the garment's material, or None when nothing cuts it."""
    material = obj.data.materials[0] if obj.data.materials else None
    if material is None or not material.use_nodes:
        return None
    for link in material.node_tree.links:
        if link.to_socket.name == "Alpha" and link.from_node.type == "TEX_IMAGE" and link.from_node.image is not None:
            image = link.from_node.image
            width, height = image.size
            pixels = np.empty(width * height * 4, np.float32)
            image.pixels.foreach_get(pixels)
            return pixels.reshape(height, width, 4)[:, :, 3].copy()
    return None


def _cone(normal: Vector) -> list[Vector]:
    tangent = normal.orthogonal().normalized()
    bitangent = normal.cross(tangent)
    directions = [normal]
    for k in range(CONE_RAYS):
        angle = 2.0 * math.pi * k / CONE_RAYS
        side = tangent * math.cos(angle) + bitangent * math.sin(angle)
        directions.append((normal * math.cos(CONE_ANGLE) + side * math.sin(CONE_ANGLE)).normalized())
    return directions


def _opaque_hit(cloth: _Cloth, origin: Vector, direction: Vector, distance: float) -> tuple[float, Vector] | None:
    """The distance and normal of the first place a ray hits the garment where it is opaque, through
    its transparent parts."""
    start, travelled = origin, 0.0
    for _ in range(MAX_LAYERS):
        location, normal, triangle, hit = cloth.tree.ray_cast(start, direction, distance - travelled)
        if location is None:
            return None
        if cloth.opaque(triangle, location):
            return travelled + hit, normal
        travelled += hit + RAY_OFFSET
        start = location + direction * RAY_OFFSET
    return None


def _body_hit(surface: "garment.Surface", polygon: int, origin: Vector, direction: Vector, reach: float) -> float | None:
    """How far a ray from a body face goes before it meets the body again, past the face itself."""
    start, travelled = origin, 0.0
    for _ in range(MAX_LAYERS):
        location, _normal, triangle, distance = surface.tree.ray_cast(start, direction, reach - travelled)
        if location is None:
            return None
        travelled += distance
        if surface.triangles[triangle][0] != polygon:
            return travelled
        travelled += RAY_OFFSET
        start = location + direction * RAY_OFFSET
    return None


OUT, GARMENT, BODY = "out", "garment", "body"


def _ray(cloth: _Cloth, surface: "garment.Surface", polygon: int, origin: Vector, direction: Vector, reach: float) -> str:
    """What a ray from the skin meets first within the garment's reach: the garment where it is
    opaque, the body, or nothing, so it gets out."""
    blocked = _body_hit(surface, polygon, origin, direction, reach)
    limit = blocked if blocked is not None else reach
    start, travelled = origin, 0.0
    for _ in range(MAX_LAYERS):
        location, _normal, triangle, distance = cloth.tree.ray_cast(start, direction, limit - travelled)
        if location is None:
            return OUT if blocked is None else BODY
        if cloth.opaque(triangle, location):
            return GARMENT
        travelled += distance + RAY_OFFSET
        start = location + direction * RAY_OFFSET
    return OUT


def _covers(cloth: _Cloth, surface: "garment.Surface", polygon: int, rays: list[tuple[Vector, Vector]],
            reach: float) -> bool:
    hits_garment = False
    for origin, direction in rays:
        met = _ray(cloth, surface, polygon, origin, direction, reach)
        if met == OUT:
            return False
        hits_garment = hits_garment or met == GARMENT
    return hits_garment


def compute(name: str, label: str, band: str) -> Coverage:
    """The cover mask, underlay, and pull-in of one version of the garment, at rest."""
    body = bodies.source_body(label)
    version = bpy.data.objects.get(garment.version_name(name, label))
    if body is None or version is None:
        raise ArtPipelineError(f'The garment has no version "{garment.version_name(name, label)}" on a body.')
    reach = REACH[band]
    surface = garment.Surface(body, [])
    cloth = _Cloth(version, bodies.world_positions(version), _alpha(version))
    rotation = body.matrix_world.to_3x3()
    covered = np.zeros(len(body.data.polygons), bool)
    for polygon in body.data.polygons:
        corners = [Vector(surface.co[v]) for v in polygon.vertices]
        centre = sum(corners, Vector()) / len(corners)
        normal = (rotation @ polygon.normal).normalized()
        # The samples lie on the face's triangles, which a face that is not flat need not share
        # with its centre.
        samples = [surface.tree.find_nearest(p)[0] for p in [centre] + [centre.lerp(c, SAMPLE_SPREAD) for c in corners]]
        rays = [(sample + normal * SKIN_OFFSET, d) for sample in samples for d in _cone(normal)]
        covered[polygon.index] = _covers(cloth, surface, polygon.index, rays, reach)

    garment_rotation = version.matrix_world.to_3x3()
    underlay = np.empty(len(version.data.polygons), np.int64)
    for polygon in version.data.polygons:
        centre = Vector(np.mean([cloth.co[v] for v in polygon.vertices], axis=0))
        inward = -(garment_rotation @ polygon.normal).normalized()
        hit = surface.tree.ray_cast(centre, inward, 2.0 * reach)
        if hit[0] is None:
            hit = surface.tree.find_nearest(centre)
        underlay[polygon.index] = surface.triangles[hit[2]][0]

    low = garment.BANDS[band][0]
    clearance = max(low, PULL_IN_CLEARANCE)
    result = Coverage(label, band, covered, underlay, np.empty(0, np.int64), np.empty(0), len(body.data.polygons),
                      len(body.data.vertices), len(version.data.polygons))
    pulled, distances = [], []
    for vertex in result.visible_vertices(body):
        found = cloth.tree.find_nearest(Vector(surface.co[vertex]))
        if found[0] is not None and found[3] < clearance:
            pulled.append(int(vertex))
            distances.append(round(min(PULL_IN_MAX, clearance - found[3]), 5))
    result.pull_vertices = np.array(pulled, np.int64)
    result.pull_distances = np.array(distances)
    return result


def summary(name: str, coverage: Coverage) -> str:
    pulled = len(coverage.pull_vertices)
    most = f" by at most {coverage.pull_distances.max() * 1000:.1f} mm" if pulled else ""
    moved = f", {int(coverage.moved.sum())} of them for motion," if coverage.moved is not None else ""
    return (f"{garment.version_name(name, coverage.label)}: covers {int(coverage.covered.sum())} of "
            f"{coverage.body_faces} body faces{moved} and pulls in {pulled} visible vertices{most}")


def record(name: str, coverage: Coverage) -> dict:
    """The garment's data for one body, as the export writes it next to the model."""
    return {
        "schemaVersion": DATA_SCHEMA,
        "garment": name,
        "body": coverage.label,
        "bodyFaces": coverage.body_faces,
        "bodyVertices": coverage.body_vertices,
        "garmentFaces": coverage.garment_faces,
        "covered": [int(i) for i in np.nonzero(coverage.covered)[0]],
        "underlay": [int(i) for i in coverage.underlay],
        "pullInVertices": [int(i) for i in coverage.pull_vertices],
        "pullInDistances": [float(d) for d in coverage.pull_distances],
    }


def write_record(path: str, data: dict) -> None:
    """Writes the record as JSON with the numbers 16 to a line, so a change shows in a diff."""
    lines = ["{"]
    keys = list(data)
    for index, key in enumerate(keys):
        comma = "," if index < len(keys) - 1 else ""
        value = data[key]
        if isinstance(value, list):
            numbers = [f"{v:.5f}" if isinstance(v, float) else str(v) for v in value]
            if not numbers:
                lines.append(f'    "{key}": []{comma}')
                continue
            lines.append(f'    "{key}": [')
            for start in range(0, len(numbers), 16):
                last = start + 16 >= len(numbers)
                lines.append("        " + ", ".join(numbers[start:start + 16]) + ("" if last else ","))
            lines.append(f"    ]{comma}")
        elif isinstance(value, str):
            lines.append(f'    "{key}": "{value}"{comma}')
        else:
            lines.append(f'    "{key}": {value}{comma}')
    lines.append("}")
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(lines) + "\n")


def load_actions(libraries: list[tuple[str, list[str]]]) -> list[bpy.types.Action]:
    """The named clips of animation libraries, linked into this session."""
    actions = []
    for path, names in libraries:
        with bpy.data.libraries.load(path, link=True) as (source, target):
            missing = [n for n in names if n not in source.actions]
            if missing:
                raise ArtPipelineError(f"{path} has no clips {missing}.")
            target.actions = list(names)
        actions += [a for a in target.actions if a is not None]
    return actions


def _posed(obj: bpy.types.Object) -> tuple[np.ndarray, np.ndarray]:
    """The positions and vertex normals of a mesh as its armature poses it, in world space."""
    depsgraph = bpy.context.evaluated_depsgraph_get()
    evaluated = obj.evaluated_get(depsgraph)
    mesh = evaluated.to_mesh()
    try:
        co = np.empty(len(mesh.vertices) * 3)
        mesh.vertices.foreach_get("co", co)
        normals = np.empty(len(mesh.vertices) * 3)
        mesh.vertex_normals.foreach_get("vector", normals)
    finally:
        evaluated.to_mesh_clear()
    m = np.array(obj.matrix_world)
    rotation = m[:3, :3]
    return co.reshape(-1, 3) @ rotation.T + m[:3, 3], normals.reshape(-1, 3) @ rotation.T


@dataclass
class Motion:
    """What motion does to the skin under a garment version."""

    label: str
    vertices: int  # visible vertices under the garment at rest
    hidden: int  # of them, how many come out of their cover
    depth: float  # the deepest any of them comes out
    where: str
    zone: str
    checked: int  # poses and clip frames


def _frames(arm: bpy.types.Object, actions: list[bpy.types.Action]):
    """Sets the armature to every test pose and to sampled frames of every clip, naming each."""
    scene = bpy.context.scene
    for pose_name, _description, pose in poses.POSES:
        poses.apply(arm, pose)
        yield pose_name
    poses.rest(arm)
    arm.animation_data_create()
    for action in actions:
        arm.animation_data.action = action
        arm.animation_data.action_slot = action.slots[0]
        last = int(action.frame_range[1])
        for step in range(CLIP_FRAMES):
            frame = round(step * last / CLIP_FRAMES)
            scene.frame_set(frame)
            yield f"{action.name} frame {frame}"


def settle_motion(name: str, coverage: Coverage, actions: list[bpy.types.Action]) -> Motion:
    """Hides every face of the visible skin that comes out of the garment over it in the test poses
    and the clips, and drops the pull-in of vertices that are no longer visible."""
    label = coverage.label
    arm = bpy.data.objects[label]
    body = bodies.source_body(label)
    version = bpy.data.objects[garment.version_name(name, label)]
    zones = bodies.zones_of(body)
    rest = np.empty(len(body.data.vertices) * 3)
    body.data.vertices.foreach_get("co", rest)
    normals = np.empty(len(body.data.vertices) * 3)
    body.data.vertex_normals.foreach_get("vector", normals)
    normals = normals.reshape(-1, 3)
    pulled = rest.reshape(-1, 3).copy()
    pulled[coverage.pull_vertices] -= normals[coverage.pull_vertices] * coverage.pull_distances[:, None]
    # The visible skin the garment is over at rest: going out from it, a ray meets the garment's
    # inner side where it is opaque. That point of the garment is the skin's cover.
    rest_cloth = _Cloth(version, bodies.world_positions(version), _alpha(version))
    vertices, corners, weights = [], [], []
    for vertex in coverage.visible_vertices(body):
        direction = Vector(normals[vertex]).normalized()
        hit = _opaque_hit(rest_cloth, Vector(pulled[vertex]), direction, REACH[coverage.band])
        if hit is not None and hit[1].dot(direction) > 0.0:
            location = Vector(pulled[vertex]) + direction * hit[0]
            triangle = rest_cloth.triangles[rest_cloth.tree.find_nearest(location)[2]][1]
            vertices.append(int(vertex))
            corners.append(triangle)
            weights.append(poly_3d_calc([Vector(rest_cloth.co[c]) for c in triangle], location))
    vertices = np.array(vertices, np.int64)
    corners = np.array(corners, np.int64).reshape(-1, 3)
    weights = np.array(weights).reshape(-1, 3)
    deepest = np.full(len(vertices), -np.inf)
    where = [""] * len(vertices)
    checked = 0
    had_animation = arm.animation_data is not None
    previous = arm.animation_data.action if had_animation else None
    body.data.vertices.foreach_set("co", pulled.reshape(-1))
    body.data.update()
    try:
        for name_of_frame in _frames(arm, actions):
            co = _posed(body)[0][vertices]
            cloth = _posed(version)[0]
            a, b, c = cloth[corners[:, 0]], cloth[corners[:, 1]], cloth[corners[:, 2]]
            cover = a * weights[:, :1] + b * weights[:, 1:2] + c * weights[:, 2:]
            outward = np.cross(b - a, c - a)
            outward /= np.maximum(np.linalg.norm(outward, axis=1, keepdims=True), 1e-12)
            # How far the skin lies outside its cover, along the garment's outer side there.
            depth = np.einsum("ij,ij->i", co - cover, outward)
            for i in np.nonzero(depth > deepest)[0]:
                where[i] = name_of_frame
            deepest = np.maximum(deepest, depth)
            checked += 1
    finally:
        if had_animation:
            arm.animation_data.action = previous
        else:
            arm.animation_data_clear()
        poses.rest(arm)
        bpy.context.scene.frame_set(0)
        body.data.vertices.foreach_set("co", rest)
        body.data.update()

    out = set(int(v) for v in vertices[deepest > CLIP_TOLERANCE])
    moved = np.zeros(coverage.body_faces, bool)
    for polygon in body.data.polygons:
        if not coverage.covered[polygon.index] and out.intersection(polygon.vertices):
            moved[polygon.index] = True
    coverage.covered = coverage.covered | moved
    coverage.moved = moved
    visible = set(int(v) for v in coverage.visible_vertices(body))
    keep = np.array([int(v) in visible for v in coverage.pull_vertices], bool)
    coverage.pull_vertices = coverage.pull_vertices[keep]
    coverage.pull_distances = coverage.pull_distances[keep]
    vertex_zone = np.zeros(coverage.body_vertices, np.int64)
    for polygon in body.data.polygons:
        vertex_zone[list(polygon.vertices)] = zones[polygon.index]
    result = Motion(label, len(vertices), len(out), 0.0, "", "", checked)
    if len(vertices):
        worst = int(np.argmax(deepest))
        result.depth, result.where = float(max(deepest[worst], 0.0)), where[worst]
        result.zone = bodies.ZONES[vertex_zone[vertices[worst]]]
    return result


def motion_summary(name: str, result: Motion) -> str:
    worst = f", at most {result.depth * 1000:.1f} mm ({result.zone} in {result.where})" if result.hidden else ""
    return (f"{garment.version_name(name, result.label)}: of the {result.vertices} visible vertices under it, "
            f"{result.hidden} come out of it in {result.checked} poses and clip frames{worst}, so their faces are "
            f"hidden too")
