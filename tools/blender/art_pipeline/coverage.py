"""What a garment covers on each body it fits, in the body's faces and vertices, which every body of
the shared topology numbers alike: the cover mask, the underlay, the pull-in, and the skin that
comes through the garment in the test poses and the clips. See docs/EQUIPMENT_VISUALS_DESIGN.md.

- The cover mask holds the body faces the garment hides. From sample points on a face, rays go out
  in a cone around the face normal; a face is covered when every ray hits the garment where its
  texture is opaque, or the body itself, before it gets out of the garment's reach, and at least one
  hits the garment. The reach is the band's above the garment cage, so a ray from the bottom of a
  pit that the cage spans reaches the cloth over it. A ray that leaves through a hole, an open hem, or a transparent part of the
  texture leaves the face visible, and so does a face that only the body hides, such as the inside
  of the mouth. The skin at the garment's edges stays visible as well: a look into a sleeve or under
  a hem sees past the cone, so a face within the edge reach of an open edge of the garment, a
  border of its mesh or a line where its texture is cut, is never hidden. The edge reach is the
  band's, or four times the gap between the skin and the edge where the cloth hangs farther off.
- The underlay holds, for every garment face, the body face under it.
- The pull-in holds how far to move visible body vertices inward that lie closer to the garment
  than its band allows, such as the skin at a hem, so they stay under it in motion.
- In the pair check, a template of a lower layer worn under this one keeps the faces whose body face
  this one leaves visible, at this one's openings. Each of their vertices that this one is over at
  rest keeps the point of this one over it, and a vertex that lies outside this one at its cover in
  a test pose or a clip frame comes through it, which the report names.
- In motion, visible skin under the garment can come through it: skinning blends the bones of a
  fold, such as an armpit, so the gap between the skin and the cloth there closes, whatever the
  weights. Each visible vertex that the garment is over at rest keeps the point of the garment over
  it, its cover. The body, with the pull-in applied as the game applies it, takes every test pose
  and every frame of the clips, and a vertex that lies outside the cloth at its cover, with the
  cloth between it and the inside, comes through. Such skin lies at an opening that shows it, so
  it is reported, not hidden; the garment's shape or weights fix it. Skin that passes a hem's edge
  without the cloth over it does not come through, and neither does a garment that runs into
  another body part, such as a hem into a thigh when the body folds. Skin, or an inner garment's
  vertex, that lies through the garment already at rest shows in every pose, which Motion.rest
  counts and the checks treat as a broken rule."""

import math
from dataclasses import dataclass

import bpy
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.interpolate import poly_3d_calc
from mathutils.kdtree import KDTree

from . import bodies, garment, hang, poses
from .cloth import capsules, follow, worn
from .scene import ArtPipelineError

# How far rays look for the garment from the skin, per band, in meters.
REACH = {"tight": 0.03, "mid": 0.05, "outer": 0.10, "loose": 0.30, "hair": 0.10}
# How far from an open edge of the garment the skin stays visible, per band, in meters: a look
# into an opening reaches about four times the band's gap between skin and cloth, or four times the
# gap at the edge where the cloth hangs farther off the skin, up to the largest band's reach.
EDGE_REACH = {"tight": 0.02, "mid": 0.04, "outer": 0.08, "loose": 0.12, "hair": 0.04}
EDGE_GAPS = 4.0
# How far from the skin a ray looks for the garment cage over it.
CAGE_REACH = 0.1
# The open edges are sampled this densely.
EDGE_SPACING = 0.005
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
# Visible skin may come this far through the garment over it in motion, in meters.
CLIP_TOLERANCE = 0.001
# A point lies through a garment at rest only where the garment's outer side lies this close inward
# of it: farther in, the outer side belongs to cloth elsewhere, such as a hood's side seen from the
# top of a shoulder.
REST_DEPTH = 0.05
# The name of the rest pose in the motion and pair reports.
REST = "the rest pose"
DATA_SCHEMA = 2


@dataclass
class Coverage:
    """What one version of a garment covers on its body."""

    label: str
    band: str
    covered: np.ndarray  # bool per body face
    underlay: np.ndarray  # body face per garment face
    pull_vertices: np.ndarray  # body vertices to move inward
    pull_distances: np.ndarray  # meters, per pulled vertex
    body_faces: int
    body_vertices: int
    garment_faces: int
    edge: np.ndarray | None = None  # bool per body face the rays cover but an open edge shows
    through: np.ndarray | None = None  # bool per body face whose skin comes through it in motion

    def visible_vertices(self, body: bpy.types.Object) -> np.ndarray:
        """The body vertices of every face the garment leaves visible."""
        visible = np.zeros(self.body_vertices, bool)
        for polygon in body.data.polygons:
            if not self.covered[polygon.index]:
                visible[list(polygon.vertices)] = True
        return np.nonzero(visible)[0]


def _loose_faces(mesh: bpy.types.Mesh) -> np.ndarray:
    """Which faces of a garment belong to loose parts, which hang on the cloth chains."""
    attribute = mesh.attributes.get(hang.HANG_FACE)
    if attribute is None or attribute.domain != "FACE" or attribute.data_type != "INT":
        return np.zeros(len(mesh.polygons), bool)
    values = np.empty(len(mesh.polygons), np.int32)
    attribute.data.foreach_get("value", values)
    return values > 0


class _Cloth:
    """A garment version's surface for rays: its triangles, the UVs of their corners, and the
    alpha of its texture."""

    def __init__(self, obj: bpy.types.Object, positions: np.ndarray, alpha: np.ndarray | None, fixed: bool = False):
        mesh = obj.data
        # Only the cloth that lies on the cage when fixed, as the cover mask takes it: loose parts
        # hang on the cloth chains, which the runtime solver moves, so what lies under them at rest
        # need not lie under them in motion.
        loose = _loose_faces(mesh) if fixed else np.zeros(len(mesh.polygons), bool)
        self.triangles = []
        for polygon in mesh.polygons:
            if loose[polygon.index]:
                continue
            vertices, loops = list(polygon.vertices), list(polygon.loop_indices)
            for i in range(1, len(vertices) - 1):
                self.triangles.append((polygon.index, (vertices[0], vertices[i], vertices[i + 1]),
                                       (loops[0], loops[i], loops[i + 1])))
        counts: dict[tuple[int, int], int] = {}
        for polygon in mesh.polygons:
            if not loose[polygon.index]:
                for key in polygon.edge_keys:
                    counts[key] = counts.get(key, 0) + 1
        # The edges of only one face: the borders of the mesh, and of the fixed cloth where a loose
        # part hangs from it.
        self.border = {key for key, count in counts.items() if count == 1}
        self.uv = None
        if mesh.uv_layers.active is not None:
            uv = np.empty(len(mesh.loops) * 2, np.float32)
            mesh.uv_layers.active.data.foreach_get("uv", uv)
            self.uv = uv.reshape(-1, 2)
        self.alpha = alpha
        self.place(positions)

    def place(self, positions: np.ndarray) -> "_Cloth":
        """Moves the cloth to these positions of its vertices, as a pose does."""
        self.co = positions
        self.tree = BVHTree.FromPolygons([tuple(c) for c in positions], [t[1] for t in self.triangles])
        return self

    def opaque_uv(self, uv: np.ndarray) -> np.ndarray:
        """Whether the texture is opaque at these UV coordinates."""
        if self.alpha is None:
            return np.ones(len(uv), bool)
        height, width = self.alpha.shape
        x = np.clip((uv[:, 0] * width).astype(np.int64), 0, width - 1)
        y = np.clip((uv[:, 1] * height).astype(np.int64), 0, height - 1)
        return self.alpha[y, x] >= ALPHA_CUT

    def opaque(self, triangle: int, location: Vector) -> bool:
        if self.alpha is None or self.uv is None:
            return True
        _polygon, vertices, loops = self.triangles[triangle]
        weights = poly_3d_calc([Vector(self.co[v]) for v in vertices], location)
        uv = sum(self.uv[loop] * w for loop, w in zip(loops, weights))
        return bool(self.opaque_uv(np.array([uv]))[0])

    def edges(self) -> tuple[KDTree, np.ndarray] | None:
        """Points every EDGE_SPACING along the open edges of the cloth where it is opaque, the
        borders of its mesh and the lines where its texture is cut, and the points themselves. None
        when it has no edge."""
        opaque, clear, border = [], [], []
        for _polygon, vertices, loops in self.triangles:
            corners = self.co[list(vertices)]
            longest = max(np.linalg.norm(corners[i] - corners[i - 1]) for i in range(3))
            steps = max(1, math.ceil(longest / EDGE_SPACING))
            # Barycentric weights of a grid over the triangle, with the side each point lies on:
            # side 0 joins corners 0 and 1, side 1 corners 1 and 2, side 2 corners 2 and 0.
            grid = np.array([(i, j, steps - i - j) for i in range(steps + 1) for j in range(steps + 1 - i)],
                            np.float64) / steps
            points = grid @ corners
            shown = (self.opaque_uv(grid @ self.uv[list(loops)]) if self.uv is not None
                     else np.ones(len(grid), bool))
            on_border = np.zeros(len(grid), bool)
            for side, weight in ((0, 2), (1, 0), (2, 1)):
                key = tuple(sorted((vertices[side], vertices[(side + 1) % 3])))
                if key in self.border:
                    on_border |= grid[:, weight] == 0.0
            opaque.append(points[shown])
            clear.append(points[~shown])
            border.append(points[shown & on_border])
        opaque, clear, border = np.concatenate(opaque), np.concatenate(clear), np.concatenate(border)
        found = list(border)
        if len(clear):
            near = KDTree(len(clear))
            for index, point in enumerate(clear):
                near.insert(point, index)
            near.balance()
            # An opaque point next to a transparent one lies on a line where the texture is cut.
            found += [p for p in opaque if near.find(p)[2] <= 1.5 * EDGE_SPACING]
        if not found:
            return None
        tree = KDTree(len(found))
        for index, point in enumerate(found):
            tree.insert(point, index)
        tree.balance()
        return tree, np.array(found)


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


def _blocked(body: BVHTree | None, point: Vector, direction: Vector, distance: float) -> bool:
    """Whether the body's surface lies between a point on it and a place this far along a direction."""
    if body is None:
        return False
    return body.ray_cast(point + direction * RAY_OFFSET, direction, max(distance - 2.0 * RAY_OFFSET, 0.0))[0] is not None


def _rest_cover(cloth: _Cloth, point: Vector, direction: Vector, reach: float, depth: float,
                body: BVHTree | None = None) -> tuple[Vector, float] | None:
    """Where the garment covers a point at rest, and how deep the point already lies through it: going
    out along the point's normal, the first place a ray meets the garment's inner side where it is
    opaque, or, for a point that lies outside the garment, the place on its outer side inward of it
    within depth. A point lies through the garment only where the garment's outer side lies inside
    the body there; one that the ray meets after it leaves the body again, such as the hair beside a
    face seen from the inside of an eye socket, lies on the body's other side."""
    hit = _opaque_hit(cloth, point, direction, reach)
    if hit is not None and hit[1].dot(direction) > 0.0 and not _blocked(body, point, direction, hit[0]):
        return point + direction * hit[0], 0.0
    hit = _opaque_hit(cloth, point, -direction, depth)
    if hit is not None and hit[1].dot(direction) > 0.0 and not _blocked(body, point, -direction, hit[0]):
        return point - direction * hit[0], hit[0]
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


def _cage_gap(shell: BVHTree, point: Vector, normal: Vector) -> float:
    """How far the garment cage lies off the skin at a point, along the skin's normal."""
    hit = shell.ray_cast(point, normal, CAGE_REACH)
    if hit[0] is not None:
        return hit[3]
    _location, _normal, _index, distance = shell.find_nearest(point)
    return min(distance if distance is not None else 0.0, CAGE_REACH)


def _edge_reach(edges, surface: "garment.Surface", band: str) -> np.ndarray:
    """The edge reach at every edge point: the band's, or four times the gap between the skin and
    the edge where the cloth hangs farther off."""
    gaps = np.array([max(surface.nearest(Vector(p))[0], 0.0) for p in edges[1]])
    return np.clip(gaps * EDGE_GAPS, EDGE_REACH[band], max(EDGE_REACH.values()))


def compute(name: str, label: str, band: str, covers: bool = True) -> Coverage:
    """The cover mask, underlay, and pull-in of one version of the garment, at rest. Loose parts
    cover nothing: they swing off what they hang over at rest, which would show as a hole, so the
    skin and the inner garments under them stay drawn, and the line they hang from is an open edge.
    A garment that covers nothing, such as a cloak that lies on top and may swing open, leaves its
    cover mask empty."""
    body = bodies.source_body(label)
    version = bpy.data.objects.get(garment.version_name(name, label))
    if body is None or version is None:
        raise ArtPipelineError(f'The garment has no version "{garment.version_name(name, label)}" on a body.')
    reach = REACH[band]
    surface = garment.Surface(body, [])
    shell = garment.Surface(garment.cage_object(label), []).tree
    cloth = _Cloth(version, bodies.world_positions(version), _alpha(version), fixed=True)
    edges = cloth.edges()
    edge_reach = _edge_reach(edges, surface, band) if edges is not None else None
    rotation = body.matrix_world.to_3x3()
    covered = np.zeros(len(body.data.polygons), bool)
    edge = np.zeros(len(body.data.polygons), bool)
    for polygon in body.data.polygons if covers else []:
        corners = [Vector(surface.co[v]) for v in polygon.vertices]
        centre = sum(corners, Vector()) / len(corners)
        normal = (rotation @ polygon.normal).normalized()
        # The samples lie on the face's triangles, which a face that is not flat need not share
        # with its centre.
        samples = [surface.tree.find_nearest(p)[0] for p in [centre] + [centre.lerp(c, SAMPLE_SPREAD) for c in corners]]
        rays = [(sample + normal * SKIN_OFFSET, d) for sample in samples for d in _cone(normal)]
        if not _covers(cloth, surface, polygon.index, rays, _cage_gap(shell, samples[0], normal) + reach):
            continue
        near_edge = edges is not None and any(
            distance <= edge_reach[index] for p in [centre] + corners
            for _co, index, distance in edges[0].find_range(p, float(edge_reach.max())))
        if near_edge:
            edge[polygon.index] = True
        else:
            covered[polygon.index] = True

    garment_rotation = version.matrix_world.to_3x3()
    underlay = np.empty(len(version.data.polygons), np.int64)
    for polygon in version.data.polygons:
        centre = Vector(np.mean([cloth.co[v] for v in polygon.vertices], axis=0))
        inward = -(garment_rotation @ polygon.normal).normalized()
        hit = surface.tree.ray_cast(centre, inward, CAGE_REACH + 2.0 * reach)
        if hit[0] is None:
            hit = surface.tree.find_nearest(centre)
        underlay[polygon.index] = surface.triangles[hit[2]][0]

    low = garment.BANDS[band][0]
    clearance = max(low, PULL_IN_CLEARANCE)
    result = Coverage(label, band, covered, underlay, np.empty(0, np.int64), np.empty(0), len(body.data.polygons),
                      len(body.data.vertices), len(version.data.polygons), edge)
    pulled, distances = [], []
    for vertex in result.visible_vertices(body):
        found = cloth.tree.find_nearest(Vector(surface.co[vertex]))
        if found[0] is None or found[3] >= clearance:
            continue
        # A distance that rounds to nothing moves nothing.
        distance = round(min(PULL_IN_MAX, clearance - found[3]), 5)
        if distance > 0.0:
            pulled.append(int(vertex))
            distances.append(distance)
    result.pull_vertices = np.array(pulled, np.int64)
    result.pull_distances = np.array(distances)
    return result


def summary(name: str, coverage: Coverage) -> str:
    pulled = len(coverage.pull_vertices)
    most = f" by at most {coverage.pull_distances.max() * 1000:.1f} mm" if pulled else ""
    edge = int(coverage.edge.sum()) if coverage.edge is not None else 0
    return (f"{garment.version_name(name, coverage.label)}: covers {int(coverage.covered.sum())} of "
            f"{coverage.body_faces} body faces, leaves {edge} at its edges visible, and pulls in {pulled} "
            f"visible vertices{most}")


def record(name: str, coverage: Coverage) -> dict:
    """The garment's data for one body, as the export writes it next to the model, with the
    garment's own vertices and faces, so Unity finds the faces of its triangles."""
    version = bpy.data.objects[garment.version_name(name, coverage.label)]
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
        "garmentVertices": [round(float(c), 5) for c in bodies.world_positions(version).reshape(-1)],
        "garmentFaceSizes": [len(p.vertices) for p in version.data.polygons],
        "garmentFaceVertices": [int(v) for p in version.data.polygons for v in p.vertices],
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
    through: int  # of them, how many come through it
    depth: float  # the deepest any of them comes through
    where: str
    zone: str
    checked: int  # poses and clip frames
    faces: np.ndarray | None = None  # bool per face of an inner garment whose vertices come through
    rest: int = 0  # of them, how many lie through it already at rest

    def rest_error(self, version: str, what: str) -> str | None:
        """The rule broken when something lies through the garment at rest, where it shows in every
        pose, or None."""
        if not self.rest:
            return None
        return (f'{self.rest} {what} lie through "{version}" at rest, where they show in every pose; fix its shape '
                "or the cage.")


def _frames(arm: bpy.types.Object, actions: list[bpy.types.Action], kinds: list[str] | None = None,
            found: list | None = None, reach: dict | None = None):
    """Sets the armature to every test pose and to every frame of every clip, naming each, with the
    cloth chains of the kinds laid as the runtime solver lays them. The last frame of a looping clip
    repeats its first, so it is left out."""
    scene = bpy.context.scene
    for pose_name, _description, pose in poses.POSES:
        poses.apply(arm, pose)
        follow(arm, kinds or [], found or [], reach)
        yield pose_name
    poses.rest(arm)
    arm.animation_data_create()
    for action in actions:
        arm.animation_data.action = action
        arm.animation_data.action_slot = action.slots[0]
        last = int(action.frame_range[1])
        for frame in range(last if action.get("loop") else last + 1):
            scene.frame_set(frame)
            follow(arm, kinds or [], found or [], reach)
            yield f"{action.name} frame {frame}"


def _chains_of(garments: list[bpy.types.Object], arm: bpy.types.Object,
               body: bpy.types.Object) -> tuple[list[str], list, dict]:
    """The kinds of cloth chains garments worn together hang on, from the inside out, the body's
    capsules they collide with, and the reaches of the garments' cloth on them, so the chains lie as
    the runtime solver lays them, a cloak over a robe's skirt."""
    kinds, reach = worn(arm, garments)
    return kinds, (capsules(arm, body) if kinds else []), reach


def check_motion(name: str, coverage: Coverage, actions: list[bpy.types.Action]) -> Motion:
    """Finds the visible skin that comes through the garment over it at rest, in the test poses, and
    in the clips and marks its faces. Such skin lies at an opening that shows it, so it is not
    hidden."""
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
    # inner side where it is opaque, or the skin already lies outside it. That point of the garment
    # is the skin's cover. Loose parts count with their chains laid as the runtime solver lays them.
    kinds, found, chains = _chains_of([version], arm, body)
    # The garment at rest with its chains laid as the runtime solver lays them.
    poses.rest(arm)
    follow(arm, kinds, found, chains)
    cloth = _Cloth(version, _posed(version)[0], _alpha(version))
    shell = garment.Surface(garment.cage_object(label), []).tree
    skin = BVHTree.FromPolygons([tuple(p) for p in pulled], [tuple(p.vertices) for p in body.data.polygons])
    vertices, corners, weights, deepest = [], [], [], []
    for vertex in coverage.visible_vertices(body):
        direction = Vector(normals[vertex]).normalized()
        reach = _cage_gap(shell, Vector(pulled[vertex]), direction) + REACH[coverage.band]
        cover = _rest_cover(cloth, Vector(pulled[vertex]), direction, reach, min(REACH[coverage.band], REST_DEPTH), skin)
        if cover is not None:
            location, depth = cover
            triangle = cloth.triangles[cloth.tree.find_nearest(location)[2]][1]
            vertices.append(int(vertex))
            corners.append(triangle)
            weights.append(poly_3d_calc([Vector(cloth.co[c]) for c in triangle], location))
            deepest.append(depth)
    vertices = np.array(vertices, np.int64)
    corners = np.array(corners, np.int64).reshape(-1, 3)
    weights = np.array(weights).reshape(-1, 3)
    deepest = np.array(deepest)
    where = [REST if depth > 0.0 else "" for depth in deepest]
    at_rest = int((deepest > CLIP_TOLERANCE).sum())
    checked = 1
    had_animation = arm.animation_data is not None
    previous = arm.animation_data.action if had_animation else None
    body.data.vertices.foreach_set("co", pulled.reshape(-1))
    body.data.update()
    try:
        for name_of_frame in _frames(arm, actions, kinds, found, chains):
            co = _posed(body)[0][vertices]
            cloth.place(_posed(version)[0])
            a, b, c = (cloth.co[corners[:, k]] for k in range(3))
            cover = a * weights[:, :1] + b * weights[:, 1:2] + c * weights[:, 2:]
            outward = np.cross(b - a, c - a)
            outward /= np.maximum(np.linalg.norm(outward, axis=1, keepdims=True), 1e-12)
            # How far the skin lies outside the cloth's plane at its cover.
            plane = np.einsum("ij,ij->i", co - cover, outward)
            for i in np.nonzero(plane > deepest)[0]:
                # The skin comes through only where the cloth lies between it and the inside: skin
                # that passes a hem's edge has no cloth there.
                direction = Vector(-outward[i])
                hit = _opaque_hit(cloth, Vector(co[i]), direction, float(plane[i]) + 2.0 * CLIP_TOLERANCE)
                if hit is not None and hit[1].dot(direction) < 0.0 and hit[0] > deepest[i]:
                    deepest[i], where[i] = hit[0], name_of_frame
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

    through = deepest > CLIP_TOLERANCE
    out = set(int(v) for v in vertices[through])
    coverage.through = np.zeros(coverage.body_faces, bool)
    for polygon in body.data.polygons:
        if out.intersection(polygon.vertices):
            coverage.through[polygon.index] = True
    vertex_zone = np.zeros(coverage.body_vertices, np.int64)
    for polygon in body.data.polygons:
        vertex_zone[list(polygon.vertices)] = zones[polygon.index]
    result = Motion(label, len(vertices), len(out), 0.0, "", "", checked, rest=at_rest)
    if out:
        worst = int(np.argmax(deepest))
        result.depth, result.where = float(deepest[worst]), where[worst]
        result.zone = bodies.ZONES[vertex_zone[vertices[worst]]]
    return result


def check_pair(name: str, coverage: Coverage, inner: bpy.types.Object, inner_underlay: np.ndarray,
               actions: list[bpy.types.Action]) -> Motion:
    """Finds the vertices of an inner garment, worn under this one, that come through this one at
    rest, in the test poses, and in the clips: of the inner garment's faces that stay drawn, those
    whose body face this one leaves visible, every vertex that faces away from the cage and that
    this one is over at rest, or that already lies outside it. A turned edge's rim and flap face
    the body and lie under the inner garment's outer side, which is checked instead. The chains of
    both are laid together, also at rest, so a cloak lies over a robe's skirt."""
    label = coverage.label
    arm = bpy.data.objects[label]
    version = bpy.data.objects[garment.version_name(name, label)]
    drawn = [p for p in inner.data.polygons if not coverage.covered[inner_underlay[p.index]]]
    kinds, found, chains = _chains_of([inner, version], arm, bodies.source_body(label))
    # Both at rest with their chains laid together, as the runtime solver lays a cloak over a robe's
    # skirt.
    poses.rest(arm)
    follow(arm, kinds, found, chains)
    rest, normals = _posed(inner)
    cloth = _Cloth(version, _posed(version)[0], _alpha(version))
    shell = garment.Surface(garment.cage_object(label), []).tree
    skin = garment.Surface(bodies.source_body(label), []).tree
    vertices, corners, weights, deepest = [], [], [], []
    for vertex in sorted({v for p in drawn for v in p.vertices}):
        direction = Vector(normals[vertex]).normalized()
        _location, outward, _index, _distance = shell.find_nearest(Vector(rest[vertex]))
        if outward is None or direction.dot(outward) <= 0.0:
            continue
        reach = _cage_gap(shell, Vector(rest[vertex]), direction) + REACH[coverage.band]
        cover = _rest_cover(cloth, Vector(rest[vertex]), direction, reach, min(REACH[coverage.band], REST_DEPTH), skin)
        if cover is not None:
            location, depth = cover
            triangle = cloth.triangles[cloth.tree.find_nearest(location)[2]][1]
            vertices.append(int(vertex))
            corners.append(triangle)
            weights.append(poly_3d_calc([Vector(cloth.co[c]) for c in triangle], location))
            deepest.append(depth)
    vertices = np.array(vertices, np.int64)
    corners = np.array(corners, np.int64).reshape(-1, 3)
    weights = np.array(weights).reshape(-1, 3)
    deepest = np.array(deepest)
    where = [REST if depth > 0.0 else "" for depth in deepest]
    at_rest = int((deepest > CLIP_TOLERANCE).sum())
    checked = 1
    had_animation = arm.animation_data is not None
    previous = arm.animation_data.action if had_animation else None
    try:
        for name_of_frame in _frames(arm, actions, kinds, found, chains):
            co = _posed(inner)[0][vertices]
            cloth.place(_posed(version)[0])
            a, b, c = (cloth.co[corners[:, k]] for k in range(3))
            cover = a * weights[:, :1] + b * weights[:, 1:2] + c * weights[:, 2:]
            outward = np.cross(b - a, c - a)
            outward /= np.maximum(np.linalg.norm(outward, axis=1, keepdims=True), 1e-12)
            plane = np.einsum("ij,ij->i", co - cover, outward)
            for i in np.nonzero(plane > deepest)[0]:
                direction = Vector(-outward[i])
                hit = _opaque_hit(cloth, Vector(co[i]), direction, float(plane[i]) + 2.0 * CLIP_TOLERANCE)
                if hit is not None and hit[1].dot(direction) < 0.0 and hit[0] > deepest[i]:
                    deepest[i], where[i] = hit[0], name_of_frame
            checked += 1
    finally:
        if had_animation:
            arm.animation_data.action = previous
        else:
            arm.animation_data_clear()
        poses.rest(arm)
        bpy.context.scene.frame_set(0)
    through = deepest > CLIP_TOLERANCE
    out = set(int(v) for v in vertices[through])
    body = bodies.source_body(label)
    zones = bodies.zones_of(body)
    result = Motion(label, len(vertices), len(out), 0.0, "", "", checked, rest=at_rest)
    if out:
        worst = int(np.argmax(deepest))
        result.depth, result.where = float(deepest[worst]), where[worst]
        # The body part under the deepest place, so a person knows where to look.
        nearest = garment.Surface(body, []).signed_distance(Vector(rest[vertices[worst]]))[1]
        result.zone = bodies.ZONES[zones[nearest]] if nearest >= 0 else ""
    result.faces = np.array([any(v in out for v in p.vertices) for p in inner.data.polygons])
    return result


def pair_summary(name: str, inner: str, result: Motion) -> str:
    version = garment.version_name(name, result.label)
    if not result.through:
        return (f"{inner} under {version}: none of the {result.vertices} vertices of it that stay drawn under {version} "
                f"comes through it in {result.checked} poses and clip frames")
    return (f"{inner} under {version}: of the {result.vertices} vertices of it that stay drawn under {version}, "
            f"{result.through} come through it in {result.checked} poses and clip frames, by at most "
            f"{result.depth * 1000:.1f} mm ({result.zone} in {result.where})")


def motion_summary(name: str, result: Motion) -> str:
    version = garment.version_name(name, result.label)
    if not result.through:
        return (f"{version}: none of the {result.vertices} visible vertices under it comes through it in "
                f"{result.checked} poses and clip frames")
    return (f"{version}: of the {result.vertices} visible vertices under it, {result.through} come through it in "
            f"{result.checked} poses and clip frames, by at most {result.depth * 1000:.1f} mm ({result.zone} in "
            f"{result.where}), red on the review sheet")
