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
  than its band allows, such as the skin at a hem, so they stay under it in motion. A drawn face is
  measured inside too, split as the game splits it, and the corners of a triangle whose inside lies
  that close move inward by what the closest point lacks.
- The hidden faces hold, per template of a lower layer worn under this one, the faces of it that
  this one hides, by the cover rule applied to that template as the surface: every ray of a face's
  cone meets this one's cloth where it is opaque, and the face lies beyond the hide margin of every
  place of an open edge of this one's cloth on every body at rest, four times the gap between the
  place and the surface under it and at least 10 mm; the recipes split the inner templates just
  beyond the margin on the first body. Cloth of the inner template that swings is never hidden. The
  list is one for every body: the faces the rest rule gives on the first body, without those at
  which a look past this one shows a hole on any body at rest or in any frame of any clip, past
  only what the game draws. The game leaves the hidden faces out, so nothing of them can come
  through.
- In the pair check, a template of a lower layer worn under this one keeps its drawn triangles: of
  the faces whose body face this one leaves visible and that this one does not hide, split as the
  game splits them. Each is followed at its corners, along its edges, inside it, and where the edges
  of this one's triangles near it cross it, at the points that this one is over at rest; one that
  lies through this one at rest breaks a rule, and the report names the deepest place per pose and
  clip. The hidden faces are followed by the cover rule that hid them, and the report names per
  phase the most at which a look past this one shows a hole, which only the test poses can have.
- In motion, visible skin under the garment can come through it: skinning blends the bones of a
  fold, such as an armpit, so the gap between the skin and the cloth there closes, whatever the
  weights. Each visible vertex, and each place inside a drawn face, split as the game splits it,
  that the garment is over at rest keeps the point of the garment over it, its cover. The body, with
  the pull-in applied as the game applies it, takes every test pose and every frame of the clips,
  and a place that lies outside the cloth at its cover, with the cloth between it and the inside,
  comes through, and so does its face. The report names the deepest place per phase, as the pair
  report does. Such skin lies at an opening that shows it, so it is reported, not hidden; the
  garment's shape or weights fix it. Skin that passes a hem's edge without the cloth over it does
  not come through, and neither does a garment that runs into another body part, such as a hem
  into a thigh when the body folds. Skin, or an inner garment's vertex, that lies through the
  garment already at rest shows in every pose, which Motion.rest counts and the checks treat as a
  broken rule."""

import json
import math
from dataclasses import dataclass, field

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
# A face of an inner garment that this one hides lies beyond the hide margin of every place of an open
# edge of this one's cloth, so a look under its hem or into a hole sees the inner garment and not a
# hole in it. The margin of a place is EDGE_GAPS times the gap between it and the surface under it,
# the inner garment's or the body's, as far as a look into the opening there reaches, and at least
# this. Recipes split the inner templates just beyond the margin inside the lines where the outer
# ones end.
HIDE_MARGIN = 0.01
# A face within the margin by less than this still counts as beyond it: a split runs through a vertex
# this close to its plane, so a face that starts at a split at the margin may come this much closer.
HIDE_ROUNDING = 0.001
# The lines where a texture is cut are found this densely for the hide margin.
HIDE_SPACING = 0.002
# Rays from a face of an inner garment start this far off it.
HIDE_OFFSET = 1e-4
# An inner garment's drawn triangle that lies through this one at rest by more than this numeric
# rounding shows in every pose and breaks the rule.
DEPTH_ROUNDING = 5e-5
# A point of an inner garment's drawn triangle lies through this one at rest only where this one's
# outer side lies this close inward of it: where the two cross, the depth starts at nothing, so the
# check finds every crossing, and farther in the outer side belongs to cloth elsewhere, such as a
# hood's brim seen from the hair in its opening.
PAIR_REST_DEPTH = 0.01
# The drawn triangles of an inner garment are followed at points this far apart along their edges
# and inside them.
TRIANGLE_SPACING = 0.003
# The name of the rest pose in the motion and pair reports.
REST = "the rest pose"
# The phase of the pair report that holds the test poses.
TEST_POSES = "the test poses"
DATA_SCHEMA = 3


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
    # Per template of a lower layer worn under it, by name, bool per face of that template it hides.
    hidden: dict[str, np.ndarray] = field(default_factory=dict)

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

    def __init__(self, obj: bpy.types.Object, positions: np.ndarray, alpha: np.ndarray | None, fixed: bool = False,
                 turned: list[tuple[int, int, int]] | None = None):
        mesh = obj.data
        # Only the cloth that does not swing when fixed, as the cover mask takes it: loose parts
        # hang on the cloth chains, which the runtime solver moves, so what lies under them at rest
        # need not lie under them in motion. The top of a loose part above where its chains hang
        # from, such as a skirt from the waist down to the hips, moves with the skin under it and
        # covers it like the rest of the garment.
        loose = hang.swinging_faces(obj, positions) if fixed else np.zeros(len(mesh.polygons), bool)
        # Without its turned edges, when given, the cloth ends where its hem shows: its rims and
        # flaps lie inside it.
        left = loose.copy()
        if turned:
            inside = garment.turned_mask(len(mesh.vertices), turned)
            left |= np.array([inside[list(p.vertices)].any() for p in mesh.polygons], bool)
        self.faces = np.nonzero(~left)[0]
        self.triangles = []
        for polygon in mesh.polygons:
            if left[polygon.index]:
                continue
            vertices, loops = list(polygon.vertices), list(polygon.loop_indices)
            for i in range(1, len(vertices) - 1):
                self.triangles.append((polygon.index, (vertices[0], vertices[i], vertices[i + 1]),
                                       (loops[0], loops[i], loops[i + 1])))
        counts: dict[tuple[int, int], int] = {}
        for polygon in mesh.polygons:
            if not left[polygon.index]:
                for key in polygon.edge_keys:
                    counts[key] = counts.get(key, 0) + 1
        # Where the top of a loose part ends and its swinging faces begin, the cloth goes on: that
        # is no open edge, since the part does not swing off its own top.
        parts = _loose_faces(mesh)
        for polygon in mesh.polygons:
            if loose[polygon.index]:
                for key in polygon.edge_keys:
                    if key in counts and any(parts[f] and not left[f] for f in self._faces_at(mesh, key)):
                        counts[key] += 1
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

    def _faces_at(self, mesh: bpy.types.Mesh, key: tuple[int, int]) -> list[int]:
        """The faces of the mesh at an edge, by its two vertices."""
        if not hasattr(self, "_edge_faces"):
            self._edge_faces: dict[tuple[int, int], list[int]] = {}
            for polygon in mesh.polygons:
                for edge in polygon.edge_keys:
                    self._edge_faces.setdefault(edge, []).append(polygon.index)
        return self._edge_faces.get(key, [])

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

    def _grid(self, spacing: float) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
        """Points every spacing over the cloth's triangles: those where it is opaque, those where it
        is not, and the opaque ones on its borders."""
        opaque, clear, border = [], [], []
        for _polygon, vertices, loops in self.triangles:
            corners = self.co[list(vertices)]
            longest = max(np.linalg.norm(corners[i] - corners[i - 1]) for i in range(3))
            steps = max(1, math.ceil(longest / spacing))
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
        empty = [np.empty((0, 3))]
        return np.concatenate(opaque + empty), np.concatenate(clear + empty), np.concatenate(border + empty)

    @staticmethod
    def _cut(opaque: np.ndarray, clear: np.ndarray, spacing: float) -> list:
        """The opaque points next to a transparent one, which lie on a line where the texture is cut."""
        if not len(clear):
            return []
        near = KDTree(len(clear))
        for index, point in enumerate(clear):
            near.insert(point, index)
        near.balance()
        return [p for p in opaque if near.find(p)[2] <= 1.5 * spacing]

    def edges(self) -> tuple[KDTree, np.ndarray] | None:
        """Points every EDGE_SPACING along the open edges of the cloth where it is opaque, the
        borders of its mesh and the lines where its texture is cut, and the points themselves. None
        when it has no edge."""
        opaque, clear, border = self._grid(EDGE_SPACING)
        found = list(border) + self._cut(opaque, clear, EDGE_SPACING)
        if not found:
            return None
        tree = KDTree(len(found))
        for index, point in enumerate(found):
            tree.insert(point, index)
        tree.balance()
        return tree, np.array(found)

    def open_edges(self, spacing: float) -> tuple[np.ndarray, np.ndarray]:
        """The open edges of the cloth: its borders as segments, each its two ends, and points every
        spacing along the lines where its texture is cut, where it is opaque."""
        keys = sorted(self.border)
        segments = self.co[np.array(keys, np.int64).reshape(-1, 2)].reshape(-1, 2, 3)
        if self.alpha is None or self.uv is None:
            return segments, np.empty((0, 3))
        opaque, clear, _border = self._grid(spacing)
        return segments, np.array(self._cut(opaque, clear, spacing)).reshape(-1, 3)


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
    cover nothing where they swing: they swing off what they hang over at rest, which would show as
    a hole, so the skin and the inner garments under them stay drawn, and the line they hang from
    is an open edge. The top of a loose part above where its chains hang from does not swing and
    covers like the rest of the garment.
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
    needs = np.zeros(len(body.data.vertices))
    for vertex in result.visible_vertices(body):
        found = cloth.tree.find_nearest(Vector(surface.co[vertex]))
        if found[0] is not None and found[3] < clearance:
            needs[vertex] = clearance - found[3]
    # Inside a drawn face, as the game splits it, the skin may lie closer to the cloth than at the
    # face's corners, such as where a hem stands over the middle of a face: there every corner of
    # its triangle moves inward by what the closest point inside lacks.
    for _face, triangle in drawn_triangles(body, covered):
        points = surface.co[list(triangle)]
        centre = points.mean(axis=0)
        radius = float(np.linalg.norm(points - centre, axis=1).max())
        found = cloth.tree.find_nearest(Vector(centre))
        if found[0] is None or found[3] >= clearance + radius:
            continue
        closest = min(cloth.tree.find_nearest(Vector(weights @ points))[3] for weights in triangle_samples(points))
        if closest < clearance:
            needs[list(triangle)] = np.maximum(needs[list(triangle)], clearance - closest)
    # A distance that rounds to nothing moves nothing.
    distances = np.round(np.minimum(PULL_IN_MAX, needs), 5)
    result.pull_vertices = np.nonzero(distances > 0.0)[0].astype(np.int64)
    result.pull_distances = distances[result.pull_vertices]
    return result


def drawn_triangles(body: bpy.types.Object, covered: np.ndarray):
    """The triangles of every body face a garment leaves drawn, as the game splits them: each face as
    a fan from its first corner, which is how Unity's import splits the exported faces, every quad
    from its first corner to its third. Yields the face and the three body vertices of each."""
    for polygon in body.data.polygons:
        if covered[polygon.index]:
            continue
        corners = list(polygon.vertices)
        for i in range(1, len(corners) - 1):
            yield polygon.index, (corners[0], corners[i], corners[i + 1])


def summary(name: str, coverage: Coverage) -> str:
    pulled = len(coverage.pull_vertices)
    most = f" by at most {coverage.pull_distances.max() * 1000:.1f} mm" if pulled else ""
    edge = int(coverage.edge.sum()) if coverage.edge is not None else 0
    return (f"{garment.version_name(name, coverage.label)}: covers {int(coverage.covered.sum())} of "
            f"{coverage.body_faces} body faces, leaves {edge} at its edges visible, and pulls in {pulled} "
            f"visible vertices{most}")


def record(name: str, coverage: Coverage) -> dict:
    """The garment's data for one body, as the export writes it next to the model, with the faces
    it hides of every template of a lower layer worn under it, in that template's faces, with the
    number of faces each has, so a model of it with other faces is refused, and the garment's own
    vertices and faces, so Unity finds the faces of its triangles."""
    version = bpy.data.objects[garment.version_name(name, coverage.label)]
    inner = sorted(coverage.hidden)
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
        "innerTemplates": inner,
        "innerFaceCounts": [len(coverage.hidden[template]) for template in inner],
        "innerHiddenCounts": [int(coverage.hidden[template].sum()) for template in inner],
        "innerHiddenFaces": [int(i) for template in inner for i in np.nonzero(coverage.hidden[template])[0]],
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
            numbers = [f"{v:.5f}" if isinstance(v, float) else json.dumps(v) if isinstance(v, str) else str(v)
                       for v in value]
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
    through: int  # drawn body faces that come through it by more than CLIP_TOLERANCE in a pose or a clip frame
    depth: float  # the deepest any of them comes through
    where: str
    zone: str
    checked: int  # poses and clip frames
    faces: int = 0  # drawn body faces under the garment at rest, at a corner or inside
    rest: int = 0  # drawn body faces that lie through it already at rest
    worst: dict = field(default_factory=dict)  # per phase, the rest pose, the test poses, and each clip: the deepest and where

    def rest_error(self, version: str, what: str) -> str | None:
        """The rule broken when something lies through the garment at rest, where it shows in every
        pose, or None."""
        if not self.rest:
            return None
        return (f'{self.rest} {what} lie through "{version}" at rest, where they show in every pose; fix its shape '
                "or the cage.")


def _clip_frames(action) -> range:
    """The frames of a clip that the checks take. The last frame of a looping clip repeats its first,
    so it is left out."""
    last = int(action.frame_range[1])
    return range(last if action.get("loop") else last + 1)


def _frames(arm: bpy.types.Object, actions: list[bpy.types.Action], kinds: list[str] | None = None,
            found: list | None = None, reach: dict | None = None, test_poses: bool = True):
    """Sets the armature to every test pose, unless left out, and to every frame of every clip,
    naming each, with the cloth chains of the kinds laid as the runtime solver lays them."""
    scene = bpy.context.scene
    for pose_name, _description, pose in poses.POSES if test_poses else ():
        poses.apply(arm, pose)
        follow(arm, kinds or [], found or [], reach)
        yield pose_name
    poses.rest(arm)
    arm.animation_data_create()
    for action in actions:
        arm.animation_data.action = action
        arm.animation_data.action_slot = action.slots[0]
        for frame in _clip_frames(action):
            scene.frame_set(frame)
            follow(arm, kinds or [], found or [], reach)
            yield f"{action.name} frame {frame}"


def frame_names(actions, test_poses: bool = True) -> list[str]:
    """The names _frames gives the test poses and the clip frames, in its order, without posing."""
    names = [pose_name for pose_name, _description, _pose in poses.POSES] if test_poses else []
    return names + [f"{action.name} frame {frame}" for action in actions for frame in _clip_frames(action)]


def skipped_frames(worst: dict, actions, test_poses: bool = True) -> int:
    """A check with nothing to follow steps through no pose and no frame: every phase still joins its
    report at nothing, as stepping through its frames adds it, and the count of poses and frames is
    that of the names _frames gives."""
    names = frame_names(actions, test_poses)
    for name_of_frame in names:
        worst.setdefault(phase_of(name_of_frame), (0.0, ""))
    return len(names)


def _chains_of(garments: list[bpy.types.Object], arm: bpy.types.Object,
               body: bpy.types.Object) -> tuple[list[str], list, dict]:
    """The kinds of cloth chains garments worn together hang on, from the inside out, the body's
    capsules they collide with, and the reaches of the garments' cloth on them, so the chains lie as
    the runtime solver lays them, a cloak over a robe's skirt."""
    kinds, reach = worn(arm, garments)
    return kinds, (capsules(arm, body) if kinds else []), reach


def check_motion(name: str, coverage: Coverage, actions: list[bpy.types.Action]) -> Motion:
    """Finds the drawn skin that comes through the garment over it at rest, in the test poses, and in
    the clips and marks its faces. The skin is followed at its visible vertices and inside the drawn
    faces, split as the game splits them, where the garment can lie deepest over a face: its
    triangles' samples and where the cloth's corners and edges lie over them. Such skin lies at an
    opening that shows it, so it is not hidden."""
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
    polygons = [tuple(p.vertices) for p in body.data.polygons]
    skin = BVHTree.FromPolygons([tuple(p) for p in pulled], polygons)
    reach_out = REACH[coverage.band]
    # A place inside a face lies through the garment at rest only where the garment's outer side
    # lies as close inward of it as the pair check takes it for an inner garment's triangle: where a
    # face crosses the cloth, the depth starts at nothing, and farther in the outer side belongs to
    # cloth elsewhere, such as a hood's side seen from behind an ear.
    depth_in = min(reach_out, REST_DEPTH)
    depth_inside = min(depth_in, PAIR_REST_DEPTH)
    # Every place followed, as body corners and barycentric weights on them, with the face it lies
    # inside, or -1 at a vertex, which every face around it shares.
    places, owners, covers, deepest = [], [], [], []

    def add(corners, weights, direction: Vector, owner: int) -> bool:
        point = Vector(np.asarray(weights) @ pulled[list(corners)])
        reach = _cage_gap(shell, point, direction) + reach_out
        cover = _rest_cover(cloth, point, direction, reach, depth_in if owner < 0 else depth_inside, skin)
        if cover is None:
            return False
        location, depth = cover
        outer = cloth.triangles[cloth.tree.find_nearest(location)[2]][1]
        places.append((corners, weights))
        owners.append(owner)
        covers.append((outer, poly_3d_calc([Vector(cloth.co[c]) for c in outer], location)))
        deepest.append(depth)
        return True

    vertices = 0
    for vertex in coverage.visible_vertices(body):
        vertices += add((vertex, vertex, vertex), (1.0, 0.0, 0.0), Vector(normals[vertex]).normalized(), -1)
    for face, triangle in drawn_triangles(body, coverage.covered):
        points = pulled[list(triangle)]
        normal = np.cross(points[1] - points[0], points[2] - points[0])
        if np.linalg.norm(normal) < 1e-18:
            continue
        centre = points.mean(axis=0)
        radius = float(np.linalg.norm(points - centre, axis=1).max())
        nearest = cloth.tree.find_nearest(Vector(centre))[3]
        if nearest is None or nearest > radius + reach_out:
            continue
        # The cloth's triangles near it, whose corners and edges, where its surface folds, may lie
        # over it, and points about TRIANGLE_SPACING apart along its edges and inside it; its corners
        # are followed as vertices.
        near = sorted({index for _co, _normal, index, _d in cloth.tree.find_nearest_range(Vector(centre), radius + reach_out)})
        around = sorted({v for t in near for v in cloth.triangles[t][1]})
        segments = sorted({(min(a, b), max(a, b)) for t in near
                           for a, b in zip(cloth.triangles[t][1], cloth.triangles[t][1][1:] + cloth.triangles[t][1][:1])})
        inside = np.vstack([triangle_samples(points),
                            overlay_weights(points, cloth.co[np.array(around, np.int64)].reshape(-1, 3),
                                            cloth.co[np.array(segments, np.int64).reshape(-1, 2)])])
        direction = Vector(normal / np.linalg.norm(normal))
        for weights in inside[inside.max(axis=1) < 1.0 - 1e-9]:
            add(triangle, tuple(weights), direction, face)
    corners = np.array([c for c, _w in places], np.int64).reshape(-1, 3)
    shares = np.array([w for _c, w in places], float).reshape(-1, 3)
    owners = np.array(owners, np.int64)
    outer_corners = np.array([c for c, _w in covers], np.int64).reshape(-1, 3)
    outer_weights = np.array([w for _c, w in covers]).reshape(-1, 3)
    deepest = np.array(deepest)
    where = [REST if depth > 0.0 else "" for depth in deepest]
    worst = {REST: (float(deepest.max(initial=0.0)), REST)}
    at_rest = deepest > CLIP_TOLERANCE
    checked = 1
    had_animation = arm.animation_data is not None
    previous = arm.animation_data.action if had_animation else None
    body.data.vertices.foreach_set("co", pulled.reshape(-1))
    body.data.update()
    try:
        frames = _frames(arm, actions, kinds, found, chains)
        if not len(deepest):
            # Nothing to follow: no pose or frame can change the result.
            checked += skipped_frames(worst, actions)
            frames = ()
        for name_of_frame in frames:
            phase = phase_of(name_of_frame)
            best = worst.setdefault(phase, (0.0, ""))
            co = (_posed(body)[0][corners] * shares[:, :, None]).sum(axis=1)
            cloth.place(_posed(version)[0])
            a, b, c = (cloth.co[outer_corners[:, k]] for k in range(3))
            cover = a * outer_weights[:, :1] + b * outer_weights[:, 1:2] + c * outer_weights[:, 2:]
            outward = np.cross(b - a, c - a)
            outward /= np.maximum(np.linalg.norm(outward, axis=1, keepdims=True), 1e-12)
            # How far the skin lies outside the cloth's plane at its cover.
            plane = np.einsum("ij,ij->i", co - cover, outward)
            for i in np.nonzero((plane > best[0]) | ((plane > CLIP_TOLERANCE) & (plane > deepest)))[0]:
                # The skin comes through only where the cloth lies between it and the inside: skin
                # that passes a hem's edge has no cloth there.
                direction = Vector(-outward[i])
                hit = _opaque_hit(cloth, Vector(co[i]), direction, float(plane[i]) + 2.0 * CLIP_TOLERANCE)
                if hit is None or hit[1].dot(direction) >= 0.0:
                    continue
                if hit[0] > best[0]:
                    best = (float(hit[0]), name_of_frame)
                if hit[0] > deepest[i]:
                    deepest[i], where[i] = hit[0], name_of_frame
            worst[phase] = best
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

    def faces_of(mask: np.ndarray) -> np.ndarray:
        """The drawn body faces of the places marked: a vertex's faces around it, or the face a
        place lies inside."""
        marked = np.zeros(coverage.body_faces, bool)
        marked[owners[mask & (owners >= 0)]] = True
        at = set(int(v) for v in corners[mask & (owners < 0), 0])
        for face, polygon in enumerate(polygons):
            if not coverage.covered[face] and at.intersection(polygon):
                marked[face] = True
        return marked

    through = deepest > CLIP_TOLERANCE
    coverage.through = faces_of(through)
    result = Motion(label, vertices, int(coverage.through.sum()), 0.0, "", "", checked,
                    faces=int(faces_of(np.ones(len(deepest), bool)).sum()), rest=int(faces_of(at_rest).sum()),
                    worst=worst)
    if through.any():
        k = int(np.argmax(deepest))
        result.depth, result.where = float(deepest[k]), where[k]
        # The body part of the face it lies inside, or of the last face around a vertex.
        face = owners[k] if owners[k] >= 0 else max(f for f, p in enumerate(polygons) if corners[k, 0] in p)
        result.zone = bodies.ZONES[zones[face]]
    return result


def phase_of(frame: str) -> str:
    """The phase of the pair report a pose or a clip frame belongs to: the rest pose, the test poses,
    or its clip."""
    if frame == REST:
        return REST
    clip, separator, _number = frame.rpartition(" frame ")
    return clip if separator else TEST_POSES


def _segment_distances(first: np.ndarray, second: np.ndarray) -> np.ndarray:
    """The distance between every segment of the first set and every segment of the second, each
    given by its two ends, as an array of the first's count by the second's."""
    p, q = first[:, 0][:, None, :], second[:, 0][None, :, :]
    d1, d2 = (first[:, 1] - first[:, 0])[:, None, :], (second[:, 1] - second[:, 0])[None, :, :]
    r = p - q
    a = np.maximum((d1 * d1).sum(axis=2), 1e-30)
    e = np.maximum((d2 * d2).sum(axis=2), 1e-30)
    b = (d1 * d2).sum(axis=2)
    c = (d1 * r).sum(axis=2)
    f = (d2 * r).sum(axis=2)
    denominator = a * e - b * b
    s = np.where(denominator > 1e-24, np.clip((b * f - c * e) / np.maximum(denominator, 1e-30), 0.0, 1.0), 0.0)
    t = (b * s + f) / e
    s = np.where(t < 0.0, np.clip(-c / a, 0.0, 1.0), np.where(t > 1.0, np.clip((b - c) / a, 0.0, 1.0), s))
    t = np.clip(t, 0.0, 1.0)
    return np.linalg.norm(p + d1 * s[:, :, None] - q - d2 * t[:, :, None], axis=2)


def _point_face_distances(points: np.ndarray, corners: np.ndarray) -> np.ndarray:
    """How far every point lies from a face, its triangles split from its first corner, inside them
    or on their edges."""
    best = np.full(len(points), np.inf)
    for i in range(1, len(corners) - 1):
        a, b, c = corners[0], corners[i], corners[i + 1]
        normal = np.cross(b - a, c - a)
        size = np.linalg.norm(normal)
        if size < 1e-18:
            continue
        normal /= size
        height = (points - a) @ normal
        flat = points - np.outer(height, normal)
        # Inside the triangle when the point's projection lies on the inner side of all three edges.
        inside = np.ones(len(points), bool)
        for start, end in ((a, b), (b, c), (c, a)):
            inside &= (np.cross(end - start, flat - start) @ normal) >= 0.0
        best = np.minimum(best, np.where(inside, np.abs(height), np.inf))
    outline = np.stack([corners, np.roll(corners, -1, axis=0)], axis=1)
    points_as_segments = np.stack([points, points], axis=1)
    return np.minimum(best, _segment_distances(points_as_segments, outline).min(axis=1))


def edge_margins(gaps) -> np.ndarray:
    """The hide margin at places of a garment's open edges that lie these gaps off the surface under
    them: a look into the opening there reaches about EDGE_GAPS times the gap, and the margin is
    never less than HIDE_MARGIN."""
    return np.maximum(HIDE_MARGIN, EDGE_GAPS * np.asarray(gaps, float))


def margin_slack(corners, segments, segment_margins, cuts, cut_margins) -> float:
    """How far a face lies beyond the hide margins of the open edges of a garment over it: the least,
    over the places of the edges, of the distance between the face and the place less the place's
    margin. The places are the segments of the edges' borders, each given by its two ends, whose
    distance is the least between the segment and the face's outline and between its ends and the
    face, inside it or on its outline, and the points along the lines where its texture is cut. A
    place farther than its margin from the box around the face cannot bring it within and is left
    out, so the slack is infinite when no place is that near."""
    corners = np.asarray(corners, float).reshape(-1, 3)
    segments = np.asarray(segments, float).reshape(-1, 2, 3)
    segment_margins = np.asarray(segment_margins, float).reshape(-1)
    cuts = np.asarray(cuts, float).reshape(-1, 3)
    cut_margins = np.asarray(cut_margins, float).reshape(-1)
    reach = max(float(segment_margins.max(initial=0.0)), float(cut_margins.max(initial=0.0)))
    low, high = corners.min(axis=0) - reach, corners.max(axis=0) + reach
    near = (segments.min(axis=1) <= high).all(axis=1) & (segments.max(axis=1) >= low).all(axis=1)
    segments, segment_margins = segments[near], segment_margins[near]
    near = ((cuts >= low) & (cuts <= high)).all(axis=1)
    cuts, cut_margins = cuts[near], cut_margins[near]
    best = np.inf
    if len(segments):
        outline = np.stack([corners, np.roll(corners, -1, axis=0)], axis=1)
        across = _segment_distances(outline, segments).min(axis=0)
        ends = _point_face_distances(segments.reshape(-1, 3), corners).reshape(-1, 2).min(axis=1)
        best = float((np.minimum(across, ends) - segment_margins).min())
    for first in range(0, len(cuts), 20000):
        distances = _point_face_distances(cuts[first:first + 20000], corners)
        best = min(best, float((distances - cut_margins[first:first + 20000]).min(initial=np.inf)))
    return best


def beyond_margin(slack: float) -> bool:
    """Whether a face that lies this far beyond the hide margins of the open edges of a garment over
    it lies beyond them: within none of them by more than HIDE_ROUNDING, so a face whose corners lie
    on a split at the margin counts as beyond it."""
    return bool(slack >= -HIDE_ROUNDING)


def turned_follow(polygons: list[tuple[int, ...]], turned: list[tuple[int, int, int]], state) -> np.ndarray:
    """A state per face, such as whether it is hidden, with every face of a turned edge, a rim's or a
    flap's, taking that of the cloth face it is turned from, the face that holds both cloth vertices
    its corners are turned from, so a hem goes and stays with its cloth."""
    cloth_of = {}
    for cloth, rim, flap in turned:
        cloth_of[rim] = cloth
        cloth_of[flap] = cloth
    by_edge: dict[tuple[int, int], list[int]] = {}
    for face, polygon in enumerate(polygons):
        if any(v in cloth_of for v in polygon):
            continue
        for i, a in enumerate(polygon):
            b = polygon[(i + 1) % len(polygon)]
            by_edge.setdefault((min(a, b), max(a, b)), []).append(face)
    out = np.array(state, bool)
    for face, polygon in enumerate(polygons):
        if not any(v in cloth_of for v in polygon):
            continue
        corners = sorted({cloth_of.get(v, v) for v in polygon})
        faces = by_edge.get((corners[0], corners[1]), []) if len(corners) == 2 else []
        out[face] = bool(faces) and all(state[f] for f in faces)
    return out


def triangle_samples(corners, spacing: float = TRIANGLE_SPACING) -> np.ndarray:
    """The barycentric weights of points of a triangle about spacing apart: its corners, points along
    its edges, and points inside it."""
    corners = np.asarray(corners, float)
    longest = max(float(np.linalg.norm(corners[i] - corners[i - 1])) for i in range(3))
    steps = max(1, math.ceil(longest / spacing))
    return np.array([(i, j, steps - i - j) for i in range(steps + 1) for j in range(steps + 1 - i)], float) / steps


def overlay_weights(corners, points, segments) -> np.ndarray:
    """The barycentric weights of the places in a triangle where another surface over it, made of the
    given vertices and of its triangles' edges as segments, can lie deepest through it, seen along
    the triangle's normal: every vertex that lies over the triangle, and every place where one of the
    segments crosses one of the triangle's edges. Between them the depth changes linearly."""
    a, b, c = np.asarray(corners, float)
    normal = np.cross(b - a, c - a)
    if np.linalg.norm(normal) < 1e-18:
        return np.empty((0, 3))
    u = (b - a) / np.linalg.norm(b - a)
    basis = np.array([u, np.cross(normal / np.linalg.norm(normal), u)]).T

    def flat(q):
        return (np.asarray(q, float).reshape(-1, 3) - a) @ basis

    frame = np.linalg.inv(np.array([flat(b)[0], flat(c)[0]]).T)

    def weights(q2):
        shares = q2 @ frame.T
        return np.column_stack([1.0 - shares.sum(axis=1), shares])

    found = []
    if len(points):
        over = weights(flat(points))
        found.append(over[(over >= -1e-12).all(axis=1)])
    segments = np.asarray(segments, float).reshape(-1, 2, 3)
    if len(segments):
        p, q = flat(segments[:, 0]), flat(segments[:, 1])
        r = q - p
        sides = flat(np.array([a, b, c]))
        for side in range(3):
            s, t = sides[side], sides[(side + 1) % 3] - sides[side]
            denominator = r[:, 0] * t[1] - r[:, 1] * t[0]
            ok = np.abs(denominator) > 1e-18
            safe = np.where(ok, denominator, 1.0)
            w = s - p
            on_segment = (w[:, 0] * t[1] - w[:, 1] * t[0]) / safe
            on_side = (w[:, 0] * r[:, 1] - w[:, 1] * r[:, 0]) / safe
            crossing = ok & (on_segment >= 0.0) & (on_segment <= 1.0) & (on_side >= 0.0) & (on_side <= 1.0)
            row = np.zeros((int(crossing.sum()), 3))
            row[:, side] = 1.0 - on_side[crossing]
            row[:, (side + 1) % 3] = on_side[crossing]
            found.append(row)
    return np.clip(np.concatenate(found + [np.empty((0, 3))]), 0.0, 1.0)


def _turned_of(obj: bpy.types.Object, recipe) -> list[tuple[int, int, int]]:
    """The turned edges of a template whose recipe turns its edges, or none."""
    if not isinstance(recipe, dict) or recipe.get("hem", 0.0) <= 0.0:
        return []
    return garment.turned_edges([tuple(p.vertices) for p in obj.data.polygons])


def _polygon_normal(co: np.ndarray, polygon: tuple[int, ...]) -> np.ndarray:
    """A face's normal by Newell's method, which holds for a face that is not flat."""
    corners = co[list(polygon)]
    following = np.roll(corners, -1, axis=0)
    normal = np.array([np.sum((corners[:, 1] - following[:, 1]) * (corners[:, 2] + following[:, 2])),
                       np.sum((corners[:, 2] - following[:, 2]) * (corners[:, 0] + following[:, 0])),
                       np.sum((corners[:, 0] - following[:, 0]) * (corners[:, 1] + following[:, 1]))])
    return normal / max(float(np.linalg.norm(normal)), 1e-30)


def _hide_ray(cloth: _Cloth, blockers: list[BVHTree], origin: Vector, direction: Vector, reach: float) -> str:
    """What a ray from a face of an inner garment meets first within the reach: this garment's cloth
    where it is opaque, the body or the inner garment itself, or nothing, so it gets out."""
    limit, blocked = reach, False
    for tree in blockers:
        hit = tree.ray_cast(origin, direction, limit)
        if hit[0] is not None and hit[3] < limit:
            limit, blocked = hit[3], True
    if _opaque_hit(cloth, origin, direction, limit) is not None:
        return GARMENT
    return BODY if blocked else OUT


def _gaps(points: np.ndarray, trees: list[BVHTree]) -> np.ndarray:
    """How far each point lies from the nearest of the surfaces."""
    gaps = np.full(len(points), np.inf)
    for index, point in enumerate(np.asarray(points, float).reshape(-1, 3)):
        for tree in trees:
            found = tree.find_nearest(Vector(point))
            if found[0] is not None:
                gaps[index] = min(gaps[index], found[3])
    return gaps


@dataclass
class Margins:
    """The open edges of a garment's version on a body, where its hem shows, with the hide margin of
    every place of them over a template of a lower layer worn under it."""

    segments: np.ndarray  # the segments of the borders of its cloth, each its two ends
    segment_margins: np.ndarray  # per segment, by the larger gap of its two ends
    cuts: np.ndarray  # points along the lines where its texture is cut
    cut_margins: np.ndarray
    segment_gaps: np.ndarray  # per segment, how far it lies from the inner template at the nearer end
    cut_gaps: np.ndarray

    def over(self) -> np.ndarray:
        """The margins of the places whose margin reaches the inner template, which decide what of it
        lies beyond them."""
        return np.concatenate([self.segment_margins[self.segment_gaps <= self.segment_margins],
                               self.cut_margins[self.cut_gaps <= self.cut_margins]])


def hide_margins(name: str, label: str, inner: bpy.types.Object, recipe=None) -> Margins:
    """The open edges of a garment's version on a body, at rest, with the hide margin of every place
    of them: the borders of its cloth without its turned edges, where its hem shows, the line where
    its loose parts begin to swing, and every line where its texture is cut. The margin of a place is
    EDGE_GAPS times its gap, how far it lies from the surface under it, the nearer of the inner
    template's version on the body and the body itself, and at least HIDE_MARGIN; a segment of a
    border takes the larger gap of its two ends."""
    arm = bpy.data.objects[label]
    version = bpy.data.objects[garment.version_name(name, label)]
    cloth = _Cloth(version, bodies.world_positions(version), _alpha(version), fixed=True, turned=_turned_of(version, recipe))
    segments, cuts = cloth.open_edges(HIDE_SPACING)
    poses.rest(arm)
    co = _posed(inner)[0]
    own = BVHTree.FromPolygons([tuple(p) for p in co], [t[1:] for t in garment._triangles(inner.data)])
    under = [own, garment.Surface(bodies.source_body(label), []).tree]
    ends = _gaps(segments.reshape(-1, 3), under).reshape(-1, 2)
    inner_ends = _gaps(segments.reshape(-1, 3), [own]).reshape(-1, 2)
    return Margins(segments, edge_margins(ends.max(axis=1)), cuts, edge_margins(_gaps(cuts, under)),
                   inner_ends.min(axis=1), _gaps(cuts, [own]))


def body_margins(name: str, labels: list[str], inners: list[bpy.types.Object],
                 recipe=None) -> list[tuple[str, bpy.types.Object, Margins]]:
    """The hide margins of a garment's open edges on every body, each measured with that body's
    versions at rest: per body its label, the inner template's version on it, and the margins."""
    return [(label, inner, hide_margins(name, label, inner, recipe)) for label, inner in zip(labels, inners)]


def hidden(name: str, label: str, band: str, inner: bpy.types.Object, recipe=None, inner_recipe=None,
           covers: bool = True, margins: list[tuple[str, bpy.types.Object, Margins]] | None = None) -> np.ndarray:
    """Which faces of a template of a lower layer, worn under this one, this one hides on a body, by
    the cover rule applied to the inner template as the surface: every ray of a face's cone meets
    this one's cloth where its texture is opaque, or the body or the inner template itself, and at
    least one meets the cloth, and the face lies beyond the hide margin of every place of the open
    edges of the cloth, as hide_margins gives them, on every body at rest. The margins are given per
    body, as body_margins gives them, each with the inner template's version on that body, whose
    positions there the face is measured at; without them they are measured on this body alone. The
    recipes lay their splits just beyond the margins on the first body; the cage spaces a split and
    the line where the outer template ends closer on a smaller body, where a face the split bounds
    may come within the margin and stays drawn. A face of the inner template's turned edges goes
    with the cloth it is turned from. The inner template's cloth that swings is never hidden, since
    it swings out from under what lies over it at rest, and neither is any face under cloth of this
    one that swings. A garment that covers nothing hides nothing at all."""
    polygons = [tuple(p.vertices) for p in inner.data.polygons]
    result = np.zeros(len(polygons), bool)
    if not covers:
        return result
    arm = bpy.data.objects[label]
    version = bpy.data.objects[garment.version_name(name, label)]
    cloth = _Cloth(version, bodies.world_positions(version), _alpha(version), fixed=True,
                   turned=_turned_of(version, recipe))
    if margins is None:
        margins = [(label, inner, hide_margins(name, label, inner, recipe))]
    measured = []
    for margin_label, margin_inner, found in margins:
        poses.rest(bpy.data.objects[margin_label])
        measured.append((_posed(margin_inner)[0], found))
    poses.rest(arm)
    co = _posed(inner)[0]
    turned = _turned_of(inner, inner_recipe)
    inside = garment.turned_mask(len(co), turned)
    swinging = hang.swinging_faces(inner, co)
    own = BVHTree.FromPolygons([tuple(p) for p in co], [t[1:] for t in garment._triangles(inner.data)])
    blockers = [own, garment.Surface(bodies.source_body(label), []).tree]
    for face, polygon in enumerate(polygons):
        if inside[list(polygon)].any() or swinging[face]:
            continue
        if not all(beyond_margin(margin_slack(margin_co[list(polygon)], found.segments, found.segment_margins,
                                              found.cuts, found.cut_margins)) for margin_co, found in measured):
            continue
        corners = co[list(polygon)]
        centre = corners.mean(axis=0)
        normal = Vector(_polygon_normal(co, polygon))
        samples = [own.find_nearest(Vector(p))[0] for p in [centre] + [centre + (c - centre) * SAMPLE_SPREAD for c in corners]]
        met = [_hide_ray(cloth, blockers, sample + normal * HIDE_OFFSET, direction, REACH[band])
               for sample in samples if sample is not None for direction in _cone(normal)]
        result[face] = OUT not in met and GARMENT in met
    return turned_follow(polygons, turned, result)


def removed_under(covered, inner_covered=None) -> np.ndarray:
    """The body faces the game removes under a garment worn over a template of a lower layer, as bool
    per face: those the garment covers, and those the inner template covers when its cover on the
    body is given."""
    removed = np.array(covered, bool)
    if inner_covered is not None:
        removed |= np.asarray(inner_covered, bool)
    return removed


class _Followed:
    """The faces of an inner garment that a garment over it hides, followed in motion by the cover rule
    that hid them: their samples, the centre and points toward the corners as hidden() takes them,
    held where they lie on the face's triangles at rest, so they move with it. Only what the game
    draws blocks a ray: the inner garment's faces but the open ones, those it may leave out, and the
    body's faces but those the game removes under the pair."""

    def __init__(self, inner: bpy.types.Object, polygons: list[tuple[int, ...]], faces: list[int], rest: np.ndarray,
                 body: bpy.types.Object, open_faces=None, removed=None):
        self.polygons = polygons
        self.faces = faces
        self.triangles = sum(len(polygons[f]) - 2 for f in faces)
        triangles = garment._triangles(inner.data)
        self.fan = [t[1:] for t in triangles]
        self.blocking = [t[1:] for t in triangles if open_faces is None or not open_faces[t[0]]]
        self.body_polygons = [tuple(p.vertices) for p in body.data.polygons
                              if removed is None or not removed[p.index]]
        self.places = [tuple(float(x) for x in rest[list(polygons[f])].mean(axis=0)) for f in faces]
        self.samples = []
        own = BVHTree.FromPolygons([tuple(p) for p in rest], self.fan) if faces else None
        for face in faces:
            corners = rest[list(polygons[face])]
            centre = corners.mean(axis=0)
            found = []
            for point in [centre] + [centre + (c - centre) * SAMPLE_SPREAD for c in corners]:
                location, _normal, index, _distance = own.find_nearest(Vector(point))
                if location is not None:
                    triangle = self.fan[index]
                    found.append((list(triangle), np.array(poly_3d_calc([Vector(rest[v]) for v in triangle], location))))
            self.samples.append(found)

    def showing(self, cloth: _Cloth, co: np.ndarray, body_co: np.ndarray, reach: float) -> list[int]:
        """The hidden faces at which a look past the garment over them shows a hole in a pose, with the
        inner garment's vertices at co, the body's at body_co, and the garment's cloth placed as the
        pose lays it, all of it as the game draws it, its turned edges too: those from a sample of
        which a ray of its cone shows one, as _hole_ray says, past the inner garment's faces and the
        body's that the game draws."""
        blockers = [BVHTree.FromPolygons([tuple(p) for p in points], polygons)
                    for points, polygons in ((co, self.blocking), (body_co, self.body_polygons)) if polygons]
        found = []
        for face, samples in zip(self.faces, self.samples):
            normal = Vector(_polygon_normal(co, self.polygons[face]))
            directions = _cone(normal)
            if any(_hole_ray(cloth, blockers, Vector(weights @ co[triangle]) + normal * HIDE_OFFSET, direction, reach)
                   for triangle, weights in samples for direction in directions):
                found.append(face)
        return found

    def shown(self, cloth: _Cloth, co: np.ndarray, body_co: np.ndarray, reach: float) -> tuple[int, tuple]:
        """How many of the hidden triangles show a hole in a pose, as showing() finds their faces, and
        where the first such face lies at rest."""
        faces = set(self.showing(cloth, co, body_co, reach))
        count = sum(len(self.polygons[f]) - 2 for f in faces)
        place = next((at for face, at in zip(self.faces, self.places) if face in faces), (0.0, 0.0, 0.0))
        return count, place


def _hole_ray(cloth: _Cloth, blockers: list[BVHTree], origin: Vector, direction: Vector, reach: float) -> bool:
    """Whether a ray of the cover rule from a sample of a hidden face shows a hole: it gets out within
    the reach, past the garment's opaque cloth and the blockers, the body's and the inner garment's
    faces that the game draws, and the look along it, which comes in from outside and passes the
    face the game leaves out, goes on past the sample without first meeting the front of the
    garment's opaque cloth within the reach, the side that faces the look. The cloth is all of it as
    the game draws it, the rims and flaps of its turned edges too, which a look under a hem meets. A
    face that has come out through the cloth has that front behind it, so the look ends on the cloth
    there and sees no hole."""
    if _hide_ray(cloth, blockers, origin, direction, reach) != OUT:
        return False
    behind = _opaque_hit(cloth, origin, -direction, reach)
    return behind is None or behind[1].dot(direction) <= 0.0


def chosen_hidden(candidates, failures, polygons: list[tuple[int, ...]], turned: list[tuple[int, int, int]]) -> np.ndarray:
    """The faces a garment hides of a template worn under it, one list for every body: the candidates
    the rest rule gives on the first body, without every face at which a look past the garment shows
    a hole on any body, at rest or in any frame of any clip, so such a face is drawn and followed like
    every other drawn face; a face of the template's turned edges goes with its cloth."""
    chosen = np.array(candidates, bool)
    for failed in failures:
        chosen &= ~np.asarray(failed, bool)
    return turned_follow(polygons, turned, chosen)


def follow_failures(name: str, coverage: Coverage, inner: bpy.types.Object, hides, actions: list[bpy.types.Action],
                    recipe=None, inner_recipe=None, inner_covered=None) -> tuple[np.ndarray, dict]:
    """Of the faces of an inner garment that this one may hide on a body, but for their turned edges,
    those at which a look past this one shows a hole at rest, with the chains of both laid, or in any
    frame of any of the clips, as bool per face, and for each the first pose or frame and its place
    at rest; the test poses are only reported, by the pair check. Every face it may hide is left out
    of what blocks a ray, as are the body faces the game removes under the pair: those this one
    covers, and those the inner garment covers when its cover on the body is given, else this one's
    alone. A face that is then drawn only blocks more, so the choice is on the safe side."""
    label = coverage.label
    arm = bpy.data.objects[label]
    version = bpy.data.objects[garment.version_name(name, label)]
    body = bodies.source_body(label)
    polygons = [tuple(p.vertices) for p in inner.data.polygons]
    inside = garment.turned_mask(len(inner.data.vertices), _turned_of(inner, inner_recipe))
    faces = [f for f in range(len(polygons)) if hides[f] and not inside[list(polygons[f])].any()]
    failed = np.zeros(len(polygons), bool)
    where: dict[int, tuple[str, tuple]] = {}
    if not faces:
        return failed, where
    kinds, found, chains = _chains_of([inner, version], arm, body)
    poses.rest(arm)
    follow(arm, kinds, found, chains)
    rest = _posed(inner)[0]
    # The cloth as the game draws it, its turned edges too.
    cloth = _Cloth(version, _posed(version)[0], _alpha(version))
    followed = _Followed(inner, polygons, faces, rest, body, hides, removed_under(coverage.covered, inner_covered))
    reach = REACH[coverage.band]
    places = dict(zip(followed.faces, followed.places))

    def note(name_of_frame: str) -> None:
        for face in followed.showing(cloth, _posed(inner)[0], _posed(body)[0], reach):
            if not failed[face]:
                failed[face] = True
                where[face] = (name_of_frame, places[face])

    note(REST)
    had_animation = arm.animation_data is not None
    previous = arm.animation_data.action if had_animation else None
    try:
        for name_of_frame in _frames(arm, actions, kinds, found, chains, test_poses=False):
            cloth.place(_posed(version)[0])
            note(name_of_frame)
    finally:
        if had_animation:
            arm.animation_data.action = previous
        else:
            arm.animation_data_clear()
        poses.rest(arm)
        bpy.context.scene.frame_set(0)
    return failed, where


def _shown_entry(shown: tuple[int, tuple], frame: str) -> tuple[int, str, tuple]:
    """A phase's entry of the hidden triangles that show: how many, in which pose or frame, and where."""
    return shown[0], frame, shown[1]


@dataclass
class Pair:
    """What the drawn triangles of an inner garment do under this one, at rest and in motion."""

    label: str
    triangles: int  # drawn triangles of the inner garment this one is over at rest, or that lie outside it
    checked: int  # poses and clip frames
    worst: dict  # per phase, the rest pose, the test poses, and each clip: the deepest place and where
    through: int = 0  # triangles that come through it by more than CLIP_TOLERANCE in a pose or a clip frame
    depth: float = 0.0
    where: str = ""
    zone: str = ""
    rest: int = 0  # triangles that lie through it at rest by more than DEPTH_ROUNDING
    rest_depth: float = 0.0
    rest_place: tuple = (0.0, 0.0, 0.0)
    faces: np.ndarray | None = None  # bool per face of the inner garment whose triangles come through
    places: dict = field(default_factory=dict)  # per phase, where its deepest place lies on the inner garment at rest
    hidden: int = 0  # triangles of the inner garment this one hides, followed like the drawn ones
    # Per phase, the most hidden triangles that show a hole in one pose or clip
    # frame of it, the frame, and where the first of them lies on the inner garment at rest.
    shown: dict = field(default_factory=dict)

    def rest_error(self, version: str, inner: str) -> str | None:
        """The rule broken when a drawn triangle of the inner garment lies through this one at rest,
        where it shows in every pose, or None."""
        if not self.rest:
            return None
        noun, verb = ("triangle", "lies") if self.rest == 1 else ("triangles", "lie")
        place = ", ".join(f"{x:.3f}" for x in self.rest_place)
        return (f'{self.rest} drawn {noun} of {inner} worn under "{version}" {verb} through it at rest, where they show in '
                f"every pose, by up to {self.rest_depth * 1000:.2f} mm at ({place}) m; shape it or its recipe so it "
                f"clears {inner}.")


def check_pair(name: str, coverage: Coverage, inner: bpy.types.Object, inner_underlay: np.ndarray, hides,
               actions: list[bpy.types.Action], recipe=None, inner_recipe=None, inner_covered=None) -> Pair:
    """Finds where the drawn triangles of an inner garment, worn under this one, come through this
    one's cloth at rest, in the test poses, and in the clips. Drawn are the triangles of the inner
    garment's faces that the game draws, neither over a body face this one covers nor among the
    faces it hides, split from their first corner as the game splits them, of its cloth that faces
    away from the cage; its turned edges lie under its cloth. Each is followed at its corners, along
    its edges, inside it, and where the edges of this one's triangles cross it, the places where this
    one's cloth can lie deepest under it, where this one is over it at rest or it already lies outside
    this one: the corners and the edges of every triangle of this one's cloth within the inner
    triangle's radius and the band's reach of its centre. This one's cloth ends where its hem shows:
    its own turned edges lie inside it. The chains of both are laid together, also at rest, so a
    cloak lies over a robe's skirt.
    The faces of the inner garment that this one hides, but for their turned edges, which lie under
    their cloth, are followed too, by the cover rule that hid them: in every pose and clip frame,
    every ray of a face's cone from its samples, as they lie on it at rest, meets this one's cloth
    where it is opaque, or the body's or the inner garment's faces that the game draws, within the
    band's reach, or the look along it shows a hole, unless it goes on past the sample to the front
    of this one's opaque cloth, as at a face that has come out through it; a face with a hole shows,
    and so do its triangles. For them this one's cloth is all of it as the game draws it, its turned
    edges too; the body faces the game removes are those this one covers and those the inner garment
    covers when its cover on the body is given. The choice of the hidden faces leaves out every face
    that shows a hole at rest or in a clip, so only the test poses can report one.
    With no place and no hidden face to follow, no pose or frame can change the result: it steps
    through none and counts them."""
    label = coverage.label
    polygons = [tuple(p.vertices) for p in inner.data.polygons]
    inside = garment.turned_mask(len(inner.data.vertices), _turned_of(inner, inner_recipe))
    arm, version, body, (kinds, found, chains), rest, cloth = _pair_rest(name, label, inner, recipe)
    shell = garment.Surface(garment.cage_object(label), []).tree
    skin = garment.Surface(body, []).tree
    reach_out = REACH[coverage.band]
    followed = _Followed(inner, polygons, [f for f in range(len(polygons)) if hides[f] and not inside[list(polygons[f])].any()],
                         rest, body, hides, removed_under(coverage.covered, inner_covered))
    triangles, owner, inner_weights, covers, deepest = [], [], [], [], []
    for face, polygon in enumerate(polygons):
        if coverage.covered[inner_underlay[face]] or hides[face] or inside[list(polygon)].any():
            continue
        for i in range(1, len(polygon) - 1):
            corners = (polygon[0], polygon[i], polygon[i + 1])
            points = rest[list(corners)]
            centre = points.mean(axis=0)
            normal = np.cross(points[1] - points[0], points[2] - points[0])
            if np.linalg.norm(normal) < 1e-18:
                continue
            normal = normal / np.linalg.norm(normal)
            _location, outward, _index, _distance = shell.find_nearest(Vector(centre))
            within = _within(cloth, points, reach_out)
            if outward is None or float(np.dot(normal, outward)) <= 0.0 or within is None:
                continue
            # The cloth's triangles near it, whose corners and edges, where its surface folds, may lie
            # over it.
            near = sorted({index for _co, _normal, index, _d in cloth.tree.find_nearest_range(Vector(centre), within)})
            around = sorted({v for t in near for v in cloth.triangles[t][1]})
            segments = sorted({(min(a, b), max(a, b)) for t in near
                               for a, b in zip(cloth.triangles[t][1], cloth.triangles[t][1][1:] + cloth.triangles[t][1][:1])})
            places = np.vstack([triangle_samples(points),
                                overlay_weights(points, cloth.co[np.array(around, np.int64)].reshape(-1, 3),
                                                cloth.co[np.array(segments, np.int64).reshape(-1, 2)])])
            direction = Vector(normal)
            index = len(triangles)
            triangles.append((face, corners))
            for weights in places:
                point = Vector(weights @ points)
                reach = _cage_gap(shell, point, direction) + reach_out
                cover = _rest_cover(cloth, point, direction, reach, PAIR_REST_DEPTH, skin)
                if cover is None:
                    continue
                location, depth = cover
                outer = cloth.triangles[cloth.tree.find_nearest(location)[2]][1]
                owner.append(index)
                inner_weights.append(weights)
                covers.append((outer, poly_3d_calc([Vector(cloth.co[c]) for c in outer], location)))
                deepest.append(depth)
    owner = np.array(owner, np.int64)
    inner_corners = np.array([triangles[t][1] for t in owner], np.int64).reshape(-1, 3)
    inner_weights = np.array(inner_weights).reshape(-1, 3)
    outer_corners = np.array([c for c, _w in covers], np.int64).reshape(-1, 3)
    outer_weights = np.array([w for _c, w in covers]).reshape(-1, 3)
    deepest = np.array(deepest)
    worst = {REST: (float(deepest.max(initial=0.0)), REST)}
    result = Pair(label, len(set(owner.tolist())), 1, worst)
    if len(deepest):
        per_triangle = np.zeros(len(triangles))
        np.maximum.at(per_triangle, owner, deepest)
        result.rest = int((per_triangle > DEPTH_ROUNDING).sum())
        if result.rest:
            k = int(np.argmax(deepest))
            result.rest_depth = float(deepest[k])
            result.rest_place = tuple(float(x) for x in inner_weights[k] @ rest[inner_corners[k]])
    where = [REST if depth > 0.0 else "" for depth in deepest]
    result.hidden = followed.triangles
    # The hidden faces are followed against the cloth as the game draws it, its turned edges too.
    drawn_cloth = _Cloth(version, cloth.co, _alpha(version)) if followed.faces else None
    if followed.faces:
        result.shown[REST] = _shown_entry(followed.shown(drawn_cloth, rest, _posed(body)[0], reach_out), REST)
    had_animation = arm.animation_data is not None
    previous = arm.animation_data.action if had_animation else None
    try:
        frames = _frames(arm, actions, kinds, found, chains)
        if not len(deepest) and not followed.faces:
            # Nothing to follow: no pose or frame can change the result.
            result.checked += skipped_frames(worst, actions)
            frames = ()
        for name_of_frame in frames:
            phase = phase_of(name_of_frame)
            best = worst.setdefault(phase, (0.0, ""))
            posed = _posed(inner)[0]
            co = (posed[inner_corners] * inner_weights[:, :, None]).sum(axis=1)
            cloth.place(_posed(version)[0])
            if followed.faces:
                drawn_cloth.place(cloth.co)
                entry = _shown_entry(followed.shown(drawn_cloth, posed, _posed(body)[0], reach_out), name_of_frame)
                if phase not in result.shown or entry[0] > result.shown[phase][0]:
                    result.shown[phase] = entry
            a, b, c = (cloth.co[outer_corners[:, k]] for k in range(3))
            cover = a * outer_weights[:, :1] + b * outer_weights[:, 1:2] + c * outer_weights[:, 2:]
            outward = np.cross(b - a, c - a)
            outward /= np.maximum(np.linalg.norm(outward, axis=1, keepdims=True), 1e-12)
            # How far each place lies outside the cloth's plane at its cover.
            plane = np.einsum("ij,ij->i", co - cover, outward)
            for i in np.nonzero((plane > best[0]) | ((plane > CLIP_TOLERANCE) & (plane > deepest)))[0]:
                # It comes through only where the cloth lies between it and the inside: a place that
                # passes a hem's edge has no cloth there.
                direction = Vector(-outward[i])
                hit = _opaque_hit(cloth, Vector(co[i]), direction, float(plane[i]) + 2.0 * CLIP_TOLERANCE)
                if hit is None or hit[1].dot(direction) >= 0.0:
                    continue
                if hit[0] > best[0]:
                    best = (float(hit[0]), name_of_frame)
                    result.places[phase] = tuple(float(x) for x in inner_weights[i] @ rest[inner_corners[i]])
                if hit[0] > deepest[i]:
                    deepest[i], where[i] = hit[0], name_of_frame
            worst[phase] = best
            result.checked += 1
    finally:
        if had_animation:
            arm.animation_data.action = previous
        else:
            arm.animation_data_clear()
        poses.rest(arm)
        bpy.context.scene.frame_set(0)
    out = deepest > CLIP_TOLERANCE
    result.through = len(set(owner[out].tolist()))
    result.faces = np.zeros(len(polygons), bool)
    result.faces[[triangles[t][0] for t in owner[out].tolist()]] = True
    if out.any():
        k = int(np.argmax(deepest))
        result.depth, result.where = float(deepest[k]), where[k]
        # The body part under the deepest place, so a person knows where to look.
        nearest = garment.Surface(body, []).signed_distance(Vector(inner_weights[k] @ rest[inner_corners[k]]))[1]
        result.zone = bodies.ZONES[bodies.zones_of(body)[nearest]] if nearest >= 0 else ""
    return result


def _pair_rest(name: str, label: str, inner: bpy.types.Object, recipe=None):
    """A pair on a body at rest as check_pair takes it: the chains of both laid together, so a cloak
    lies over a robe's skirt, the inner garment's vertices, and this one's cloth without its own
    turned edges, where its hem shows. Returns the armature, this one's version, the body, the laid
    chains (kinds, capsules, and reaches), the inner garment's vertices, and the cloth."""
    arm = bpy.data.objects[label]
    version = bpy.data.objects[garment.version_name(name, label)]
    body = bodies.source_body(label)
    kinds, found, chains = _chains_of([inner, version], arm, body)
    poses.rest(arm)
    follow(arm, kinds, found, chains)
    rest = _posed(inner)[0]
    cloth = _Cloth(version, _posed(version)[0], _alpha(version), turned=_turned_of(version, recipe))
    return arm, version, body, (kinds, found, chains), rest, cloth


def _within(cloth: _Cloth, points: np.ndarray, reach: float) -> float | None:
    """How far around a triangle of an inner garment, by its corners, the pair check looks for the
    cloth's triangles that can lie over it: its radius and the band's reach from its centre, or None
    when the cloth lies farther off, so the triangle has no place to follow."""
    centre = points.mean(axis=0)
    nearest = cloth.tree.find_nearest(Vector(centre))[3]
    radius = max(float(np.linalg.norm(p - centre)) for p in points)
    if nearest is None or nearest > radius + reach:
        return None
    return radius + reach


def meets(name: str, label: str, band: str, inner: bpy.types.Object, recipe=None) -> bool:
    """Whether a template of a lower layer can lie under this one on a body, by the test check_pair
    makes before it follows a drawn triangle: at rest, with the chains of both laid as check_pair lays
    them, some triangle of it, split from its first corner as the game splits it, has this one's cloth
    within its radius and the band's reach of its centre. Every triangle counts, also those check_pair
    leaves out as covered, hidden, turned, or facing the cage, so where this is false check_pair
    follows no place."""
    _arm, _version, _body, _laid, rest, cloth = _pair_rest(name, label, inner, recipe)
    reach = REACH[band]
    for polygon in inner.data.polygons:
        corners = list(polygon.vertices)
        for i in range(1, len(corners) - 1):
            if _within(cloth, rest[[corners[0], corners[i], corners[i + 1]]], reach) is not None:
                return True
    return False


def apart_pair(name: str, label: str, inner: bpy.types.Object, actions: list[bpy.types.Action]) -> Pair:
    """The pair check of a template of a lower layer that cannot meet this one: meets() is false on
    every body and the rest rule hides none of its faces, so check_pair would follow no place and no
    hidden face. It computes nothing of the inner template, steps through no frame, and gives what
    check_pair gives: no triangle under this one in the same count of poses and clip frames, and the
    armature at rest at frame 0, as check_pair leaves it."""
    result = Pair(label, 0, 1, {REST: (0.0, REST)})
    result.checked += skipped_frames(result.worst, actions)
    poses.rest(bpy.data.objects[label])
    bpy.context.scene.frame_set(0)
    result.faces = np.zeros(len(inner.data.polygons), bool)
    return result


def pair_summary(name: str, inner: str, result: Pair) -> str:
    """The pair check's report: how many drawn triangles of the inner garment lie under this one, how
    deep they come through it at most per phase, the rest pose, the test poses, and each clip, and
    how many come through by more than CLIP_TOLERANCE; and of the triangles this one hides, the most
    that show a hole in one pose or clip frame, per phase."""
    version = garment.version_name(name, result.label)

    def detail(phase: str, where: str) -> str:
        return f" ({'frame ' + where.rpartition(' frame ')[2] if ' frame ' in where else where})" if phase != REST else ""

    if not result.triangles:
        text = (f"{inner} under {version}: none of its drawn triangles lies under {version} in {result.checked} poses "
                "and clip frames")
    else:
        phases = [f"{phase} {depth * 1000:.2f} mm{detail(phase, where) if depth > 0.0 else ''}"
                  for phase, (depth, where) in result.worst.items()]
        text = (f"{inner} under {version}: {result.triangles} drawn triangles of it lie under {version}; the deepest they "
                f"come through it in {result.checked} poses and clip frames: {', '.join(phases)}")
        if result.through:
            text += (f"; {result.through} come through by more than {CLIP_TOLERANCE * 1000:.0f} mm, by at most "
                     f"{result.depth * 1000:.1f} mm ({result.zone} in {result.where})")
    if result.hidden:
        shown = [f"{phase} {count}{detail(phase, frame) if count else ''}" for phase, (count, frame, _place) in result.shown.items()]
        text += f"; of the {result.hidden} triangles it hides, the most that show a hole: {', '.join(shown)}"
    return text


def motion_summary(name: str, result: Motion) -> str:
    """The motion check's report: how many drawn body faces lie under the garment, how deep their skin
    comes through it at most per phase, the rest pose, the test poses, and each clip, as the pair
    report names them, and how many faces come through by more than CLIP_TOLERANCE."""
    version = garment.version_name(name, result.label)
    if not result.faces:
        return f"{version}: none of the drawn faces of the body lies under it in {result.checked} poses and clip frames"

    def detail(phase: str, where: str) -> str:
        return f" ({'frame ' + where.rpartition(' frame ')[2] if ' frame ' in where else where})" if phase != REST else ""

    phases = [f"{phase} {depth * 1000:.2f} mm{detail(phase, where) if depth > 0.0 else ''}"
              for phase, (depth, where) in result.worst.items()]
    text = (f"{version}: {result.faces} drawn faces of the body lie under it, with {result.vertices} visible vertices; "
            f"the deepest their skin comes through it in {result.checked} poses and clip frames: {', '.join(phases)}")
    if result.through:
        text += (f"; {result.through} faces come through by more than {CLIP_TOLERANCE * 1000:.0f} mm, by at most "
                 f"{result.depth * 1000:.1f} mm ({result.zone} in {result.where}), red on the review sheet")
    return text


# The units the checks cache, as check_cache.py keys and stores them: a garment's own check on one body,
# and a template of a lower layer worn under it on every body.


@dataclass
class OwnCheck:
    """A garment version's own check on one body: what it covers and, at the rig stage, what motion
    does to the skin under it."""

    coverage: Coverage
    motion: Motion | None = None


@dataclass
class UnderCheck:
    """A template of a lower layer worn under the garment, on every body in order: the hide margins of
    the garment's open edges that reach it, which a garment that covers nothing has none of, the
    faces the rest rule hides of it on the first body, and at the rig stage, per body, the faces at
    which a look past the garment shows a hole and where they first do, the faces the garment hides
    of it, one list for every body, and the pair check, and whether the two cannot meet."""

    over: list | None
    candidates: np.ndarray
    chosen: np.ndarray
    failed: list = field(default_factory=list)
    where: list = field(default_factory=list)
    pairs: list = field(default_factory=list)
    apart: bool = False


def _phases_data(phases: dict) -> list:
    return [[phase, float(depth), where] for phase, (depth, where) in phases.items()]


def _phases_from(items: list) -> dict:
    return {phase: (depth, where) for phase, depth, where in items}


def _motion_data(motion: Motion) -> dict:
    return {"label": motion.label, "vertices": int(motion.vertices), "through": int(motion.through),
            "depth": float(motion.depth), "where": motion.where, "zone": motion.zone, "checked": int(motion.checked),
            "faces": int(motion.faces), "rest": int(motion.rest), "worst": _phases_data(motion.worst)}


def _motion_from(data: dict) -> Motion:
    return Motion(data["label"], data["vertices"], data["through"], data["depth"], data["where"], data["zone"],
                  data["checked"], faces=data["faces"], rest=data["rest"], worst=_phases_from(data["worst"]))


def _pair_data(pair: Pair) -> dict:
    return {"label": pair.label, "triangles": int(pair.triangles), "checked": int(pair.checked),
            "worst": _phases_data(pair.worst), "through": int(pair.through), "depth": float(pair.depth),
            "where": pair.where, "zone": pair.zone, "rest": int(pair.rest), "restDepth": float(pair.rest_depth),
            "restPlace": [float(x) for x in pair.rest_place],
            "places": [[phase, [float(x) for x in place]] for phase, place in pair.places.items()],
            "hidden": int(pair.hidden),
            "shown": [[phase, int(count), frame, [float(x) for x in place]] for phase, (count, frame, place) in pair.shown.items()]}


def _pair_from(data: dict, faces: np.ndarray) -> Pair:
    return Pair(data["label"], data["triangles"], data["checked"], _phases_from(data["worst"]), through=data["through"],
                depth=data["depth"], where=data["where"], zone=data["zone"], rest=data["rest"], rest_depth=data["restDepth"],
                rest_place=tuple(data["restPlace"]), faces=faces,
                places={phase: tuple(place) for phase, place in data["places"]}, hidden=data["hidden"],
                shown={phase: (count, frame, tuple(place)) for phase, count, frame, place in data["shown"]})


def own_entry(check: OwnCheck) -> tuple[dict, dict]:
    """A garment's own check as a cache entry: its plain data and its arrays."""
    cover = check.coverage
    data = {"label": cover.label, "band": cover.band, "bodyFaces": cover.body_faces, "bodyVertices": cover.body_vertices,
            "garmentFaces": cover.garment_faces, "motion": _motion_data(check.motion) if check.motion is not None else None}
    arrays = {"covered": cover.covered, "underlay": cover.underlay, "pullVertices": cover.pull_vertices,
              "pullDistances": cover.pull_distances}
    for name, array in (("edge", cover.edge), ("through", cover.through)):
        if array is not None:
            arrays[name] = array
    return data, arrays


def own_from(data: dict, arrays: dict) -> OwnCheck:
    cover = Coverage(data["label"], data["band"], arrays["covered"], arrays["underlay"], arrays["pullVertices"],
                     arrays["pullDistances"], data["bodyFaces"], data["bodyVertices"], data["garmentFaces"],
                     arrays.get("edge"), arrays.get("through"))
    return OwnCheck(cover, _motion_from(data["motion"]) if data["motion"] is not None else None)


def under_entry(check: UnderCheck) -> tuple[dict, dict]:
    """A template of a lower layer under the garment as a cache entry: its plain data and its arrays."""
    data = {"apart": bool(check.apart), "over": len(check.over) if check.over is not None else None,
            "failed": len(check.failed),
            "where": [[[int(face), frame, [float(x) for x in place]] for face, (frame, place) in where.items()]
                      for where in check.where],
            "pairs": [_pair_data(pair) for pair in check.pairs]}
    arrays = {"candidates": check.candidates, "chosen": check.chosen}
    for index, over in enumerate(check.over or []):
        arrays[f"over{index}"] = over
    for index, failed in enumerate(check.failed):
        arrays[f"failed{index}"] = failed
    for index, pair in enumerate(check.pairs):
        arrays[f"faces{index}"] = pair.faces
    return data, arrays


def under_from(data: dict, arrays: dict) -> UnderCheck:
    over = [arrays[f"over{index}"] for index in range(data["over"])] if data["over"] is not None else None
    return UnderCheck(over, arrays["candidates"], arrays["chosen"],
                      [arrays[f"failed{index}"] for index in range(data["failed"])],
                      [{face: (frame, tuple(place)) for face, frame, place in where} for where in data["where"]],
                      [_pair_from(pair, arrays[f"faces{index}"]) for index, pair in enumerate(data["pairs"])],
                      data["apart"])


class Clips:
    """The clips of animation libraries, linked into this session when a check first needs them, so a
    run whose units all come from the cache links none."""

    def __init__(self, libraries: list[tuple[str, list[str]]]):
        self.libraries = libraries
        self.loaded: list[bpy.types.Action] | None = None

    def actions(self) -> list[bpy.types.Action]:
        if self.loaded is None:
            self.loaded = load_actions(self.libraries)
        return self.loaded

    def remove(self) -> None:
        for library in {a.library for a in self.loaded or [] if a.library is not None}:
            bpy.data.libraries.remove(library)


def own_cover(name: str, label: str, band: str, covers: bool, clips: Clips, units) -> Coverage:
    """What a garment version covers on its body with the skin that comes through it in motion, its own
    check at the rig stage, from the cache or computed and cached, as the review sheet shows it."""
    inputs = units.own(label, "rig")
    found = units.load(inputs)
    if found is not None:
        return own_from(*found).coverage
    result = compute(name, label, band, covers)
    motion = check_motion(name, result, clips.actions())
    units.save(inputs, *own_entry(OwnCheck(result, motion)))
    return result


def checks(name: str, labels: list[str], band: str, covers: bool, recipe, inners: list, clips: Clips, rig: bool, units,
           pair_checks: bool = True, motion_first: bool = True) -> tuple[dict[str, OwnCheck], dict[str, UnderCheck]]:
    """The checks of a garment over its bodies: per body its own check, and per template of a lower layer
    worn under it, given as its name, its band, its recipe, and a function that gives its version on
    every body, put on the bodies when first called, what the garment hides of it and the pair check,
    with the faces it hides one list for every body. A unit the cache holds is taken from it; the others
    are computed in the order the checks have always run in, the margins, the rest rule, the covers,
    the hidden faces, then the motion check before the pair checks, or with motion_first false per body
    after them, and cached. A pair that cannot meet, by meets() on every body with no face the rest
    rule hides, computes no cover of the inner template and steps through no frame. pair_checks false
    leaves the pair check out, as a texture of a template that covers nothing does. At the model stage
    only the margins, the rest rule, and the covers run. Every own check's Coverage holds the faces it
    hides of each inner template."""
    stage = "rig" if rig else "model"
    own_inputs = {label: units.own(label, stage) for label in labels}
    under_inputs = {inner: units.pair(inner, labels, stage) for inner, _band, _recipe, _objects in inners}
    own, under = {}, {}
    for label in labels:
        found = units.load(own_inputs[label])
        if found is not None:
            own[label] = own_from(*found)
    for inner, _band, _recipe, _objects in inners:
        found = units.load(under_inputs[inner])
        if found is not None:
            under[inner] = under_from(*found)
    computed_own = [label for label in labels if label not in own]
    missing = [entry for entry in inners if entry[0] not in under]
    # The hide margins of the open edges over each template on every body, which the recipes split just
    # beyond on the first body, and the faces the rest rule hides of each there beyond the margins on
    # every body.
    margins = {inner: body_margins(name, labels, objects(), recipe) for inner, _band, _recipe, objects in missing} \
        if covers else {}
    candidates = {inner: hidden(name, labels[0], band, objects()[0], recipe, inner_recipe, covers, margins.get(inner))
                  for inner, _band, inner_recipe, objects in missing}
    coverages = {label: own[label].coverage if label in own else compute(name, label, band, covers) for label in labels}
    apart, inner_covers = {}, {}
    if rig:
        # A pair that cannot meet on any body follows nothing.
        for inner, _band, _recipe, objects in missing:
            apart[inner] = pair_checks and not candidates[inner].any() and \
                not any(meets(name, label, band, obj, recipe) for label, obj in zip(labels, objects()))
        # Each template's own cover on every body, whose faces the game removes under the pair too, where a
        # hidden face or a drawn triangle of it is followed.
        inner_covers = {inner: {label: compute(inner, label, inner_band) for label in labels}
                        for inner, inner_band, _recipe, _objects in missing
                        if (covers and candidates[inner].any()) or (pair_checks and not apart[inner])}
    for inner, _band, inner_recipe, objects in missing:
        over = [found.over() for _label, _version, found in margins[inner]] if covers else None
        chosen, failed, where = candidates[inner].copy(), [], []
        if rig and covers:
            # Without the faces at which a look past it shows a hole on any body, at rest or in any frame
            # of any clip, which stay drawn and are followed like every other drawn face.
            polygons = [tuple(p.vertices) for p in objects()[0].data.polygons]
            for label, obj in zip(labels, objects()):
                inner_covered = inner_covers[inner][label].covered if inner in inner_covers else None
                found_failed, found_where = follow_failures(name, coverages[label], obj, candidates[inner], clips.actions(),
                                                            recipe, inner_recipe, inner_covered)
                failed.append(found_failed)
                where.append(found_where)
            chosen = chosen_hidden(candidates[inner], failed, polygons, _turned_of(objects()[0], inner_recipe))
        under[inner] = UnderCheck(over, candidates[inner], chosen, failed, where, [], apart.get(inner, False))

    def motion(label: str) -> None:
        if label in computed_own:
            own[label] = OwnCheck(coverages[label], check_motion(name, coverages[label], clips.actions()) if rig else None)

    def pair(entry: tuple, index: int, label: str) -> None:
        inner, _band, inner_recipe, objects = entry
        check = under[inner]
        if check.apart:
            check.pairs.append(apart_pair(name, label, objects()[index], clips.actions()))
            return
        cover = inner_covers[inner][label]
        check.pairs.append(check_pair(name, coverages[label], objects()[index], cover.underlay, check.chosen,
                                      clips.actions(), recipe, inner_recipe, cover.covered))

    checked = missing if rig and pair_checks else []
    if motion_first:
        for label in labels:
            motion(label)
        for entry in checked:
            for index, label in enumerate(labels):
                pair(entry, index, label)
    else:
        for index, label in enumerate(labels):
            for entry in checked:
                pair(entry, index, label)
            motion(label)
    for label in computed_own:
        units.save(own_inputs[label], *own_entry(own[label]))
    for inner, _band, _recipe, _objects in missing:
        units.save(under_inputs[inner], *under_entry(under[inner]))
    for label in labels:
        for inner, _band, _recipe, _objects in inners:
            own[label].coverage.hidden[inner] = under[inner].chosen.copy()
    return own, under
