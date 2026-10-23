"""The garment cage: a sparse net of fixed topology that the pipeline lays over every body of a
skeleton, smooth over the body's pits and following its figure. Garments are made and fitted on it
instead of on a body's own faces, so no garment depends on the topology of a body. See
docs/EQUIPMENT_VISUALS_DESIGN.md.

The file holds the reference in the collection CageReference: the armature CageRig, a copy of the
first body's rig when the cage was made, and on it the mesh Cage, that body's faces of the neck, the
trunk, the arms, and the legs, with its weights, the face attribute "region", and the figure scale
and the standard lines the cage was last fitted with as custom properties. The reference never
changes after it is made: a new base model changes the bodies, not the cage. Per body, the
collection Fit_<Label> holds the body's armature with the cage's version Cage_<Label> on it, and the
Reference collection the body's source body, eyes, teeth, and tongue.

A version is fitted from the reference alone, so a body of any topology gets one:

1. The reference moves with the rig from CageRig to the body's armature, every bone with its
   length, blended by the reference's weights.
2. Every vertex is drawn to the skin of its region: to the nearest skin where it lies close to it,
   otherwise along the cage's normal, toward whichever side meets the skin first, or to the nearest
   skin where no ray meets it at about the nearest skin's distance.
3. A ball whose radius is the figure scale rolls over the skin of the region: a vertex where the
   ball does not fit, in a pit narrower than the ball, is lifted until it does, by at most a share of
   the radius. Larger forms, such as the waist, keep the ball touching the skin and are followed.
   The lift fades in from the cage's open ends and from the borders between its regions, where the
   skin of the next region, such as the hand at a wrist or the other leg at the crotch, is not the
   ball's to roll on.
4. The rings around the trunk, the neck, the arms, and the legs are drawn taut like a band around
   the body, outward only and in the ring's own plane: a vertex moves out, away from the ring's
   axis, until its ring runs straight past it, so every groove across a ring is spanned, such as the buttock cleft, the spine groove,
   the navel, and the cleavage, while the figure from top to bottom, such as the waist and the
   small of the back, stays as the ball left it. A ring runs around the trunk's upright axis or
   around the bone a vertex follows most, ends at a border between regions, and its tension fades
   in from the cage's open ends.
5. The cage is faired, and no vertex or face centre stays inside the skin.

A person's corrections of a version are kept as the difference to the attribute "fitted", the shape
the last fitting gave."""

import hashlib
import json
import math

import bpy
import bmesh
import numpy as np
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

from . import bodies, body_files
from .scene import ArtPipelineError, relative_image_paths

REFERENCE = "Cage"
REFERENCE_RIG = "CageRig"
REFERENCE_COLLECTION = "CageReference"
FITTED = "fitted"
# The regions in the order of the face attribute's values. A vertex on the border of two regions
# belongs to the later one, so the limbs keep the vertices they share with the trunk.
REGIONS = ("neck", "trunk", "arms", "legs")
# The body parts each region is made of, and the parts whose skin a region's vertices are drawn to
# and kept outside of. The neck and the trunk meet without a border of skin between them.
REGION_ZONES = {"neck": ("Neck",), "trunk": ("Torso", "Pelvis"), "arms": ("UpperArms", "Forearms"),
                "legs": ("Thighs", "Shins")}
SKIN_ZONES = {"neck": ("Neck", "Torso"), "trunk": ("Torso", "Pelvis", "Neck"), "arms": ("UpperArms", "Forearms"),
              "legs": ("Thighs", "Shins")}
SIDED = ("arms", "legs")
# How far from the skin the cage keeps at least, in meters.
CLEARANCE = 0.001
# How far along the cage's normal a vertex looks for the skin of its region, how close to the skin
# it takes the nearest point instead, and how much farther than the nearest point a ray's hit may
# lie, so a ray from the skin itself does not cross a limb to its far side.
REACH = 0.08
NEAR = 0.002
RAY_SLACK = 0.01
# A pit lifts a vertex by at most this share of the figure scale, so a deep fold, such as the
# crotch, is spanned without a web hanging into it.
LIFT_SHARE = 0.6
LIFT_STEPS = 30
# The lift reaches its full size this many edges from an open end or a border between regions.
LIFT_FADE = 3
# The normals that the ball rolls along come from a copy smoothed this often, so they point out of
# a pit instead of at its walls; a smoothed normal that turns away from the skin's is replaced by it.
NORMAL_SMOOTHING = 25
NORMAL_AGREEMENT = 0.5
# An edge counts toward a ring by the fourth power of the sine of its angle to the ring's axis: fully
# across the axis, a quarter at 45 degrees, and not along it, so a pole of the net with slanting
# edges is drawn too. A vertex is drawn when its edges across weigh at least this much together;
# the tension takes this many steps of this size.
RING_WEIGHT = 1.0
TENSION_STEPS = 200
TENSION_RATE = 0.5
FAIRING = 8
SETTLE_PASSES = 4
SETTLE_MARGIN = 0.0002
# How far a fitted vertex may differ from what a sync gives before the file is stale.
FIT_TOLERANCE = 1e-5


def version_name(label: str) -> str:
    return f"Cage_{label}"


def collection_name(label: str) -> str:
    return f"Fit_{label}"


def side_of(x: float) -> str:
    # The bodies face +Y, so a body's left side lies at negative X.
    return "L" if x < 0.0 else "R"


class Net:
    """The cage's topology: its faces, edges, borders, and the neighbors each vertex is smoothed
    toward. A vertex on a border, such as the ring at a wrist, is smoothed along the border only,
    so the cage keeps its length."""

    def __init__(self, mesh: bpy.types.Mesh):
        self.faces = [tuple(p.vertices) for p in mesh.polygons]
        self.count = len(mesh.vertices)
        counts: dict[tuple[int, int], int] = {}
        for face in self.faces:
            for i in range(len(face)):
                key = tuple(sorted((face[i], face[(i + 1) % len(face)])))
                counts[key] = counts.get(key, 0) + 1
        edges = np.array(sorted(counts), np.int64)
        border = np.array([counts[tuple(e)] == 1 for e in edges])
        self.on_border = np.zeros(self.count, bool)
        self.on_border[edges[border].reshape(-1)] = True
        # A vertex inside the cage counts every neighbor, a border vertex only those along the border.
        a, b = edges[:, 0], edges[:, 1]
        toward_a = ~self.on_border[a] | border
        toward_b = ~self.on_border[b] | border
        self.targets = np.concatenate([a[toward_a], b[toward_b]])
        self.sources = np.concatenate([b[toward_a], a[toward_b]])
        self.degree = np.bincount(self.targets, minlength=self.count).astype(float)
        triangles = [(f[0], f[i], f[i + 1]) for f in self.faces for i in range(1, len(f) - 1)]
        self.triangles = np.array(triangles, np.int64)
        self.edges = edges

    def hops_from(self, start: np.ndarray, limit: int) -> np.ndarray:
        """How many edges each vertex lies from the nearest vertex of start, up to limit."""
        hops = np.full(self.count, limit)
        hops[start] = 0
        frontier = start
        for step in range(1, limit):
            reached = np.zeros(self.count, bool)
            reached[frontier] = True
            a, b = self.edges[:, 0], self.edges[:, 1]
            near = np.concatenate([b[reached[a]], a[reached[b]]])
            near = np.unique(near[hops[near] > step])
            hops[near] = step
            frontier = near
        return hops

    def mean_of_neighbors(self, x: np.ndarray) -> np.ndarray:
        total = np.zeros_like(x)
        np.add.at(total, self.targets, x[self.sources])
        return total / np.maximum(self.degree, 1.0)[:, None]

    def normals(self, x: np.ndarray) -> np.ndarray:
        a, b, c = (x[self.triangles[:, k]] for k in range(3))
        face = np.cross(b - a, c - a)
        out = np.zeros_like(x)
        for k in range(3):
            np.add.at(out, self.triangles[:, k], face)
        return out / np.maximum(np.linalg.norm(out, axis=1, keepdims=True), 1e-12)

    def smoothed_normals(self, x: np.ndarray, iterations: int) -> np.ndarray:
        y = x.copy()
        for _ in range(iterations):
            y = y + 0.5 * (self.mean_of_neighbors(y) - y)
        return self.normals(y)

    def faired(self, x: np.ndarray, iterations: int) -> np.ndarray:
        # Taubin smoothing, a shrinking and an inflating step, keeps the cage's size.
        for _ in range(iterations):
            x = x + 0.5 * (self.mean_of_neighbors(x) - x)
            x = x - 0.53 * (self.mean_of_neighbors(x) - x)
        return x


def _reference() -> tuple[bpy.types.Object, bpy.types.Object]:
    cage = bpy.data.objects.get(REFERENCE)
    rig = bpy.data.objects.get(REFERENCE_RIG)
    if cage is None or cage.type != "MESH" or rig is None or rig.type != "ARMATURE":
        raise ArtPipelineError(f'The file has no reference cage "{REFERENCE}" on its rig "{REFERENCE_RIG}".')
    return cage, rig


def _face_regions(mesh: bpy.types.Mesh) -> np.ndarray:
    attribute = mesh.attributes.get("region")
    if attribute is None or attribute.domain != "FACE" or attribute.data_type != "INT":
        raise ArtPipelineError('The reference cage needs the face attribute "region".')
    values = np.empty(len(mesh.polygons), np.int32)
    attribute.data.foreach_get("value", values)
    return values.astype(np.int64)


def vertex_keys(cage: bpy.types.Object) -> list[tuple[str, str]]:
    """Every reference vertex's region, with its side for an arm or a leg."""
    mesh = cage.data
    regions = _face_regions(mesh)
    best = np.full(len(mesh.vertices), -1, np.int64)
    for polygon, region in zip(mesh.polygons, regions):
        for v in polygon.vertices:
            best[v] = max(best[v], region)
    co = bodies.world_positions(cage)
    return [(REGIONS[r], side_of(co[i][0]) if REGIONS[r] in SIDED else "") for i, r in enumerate(best)]


def region_borders(cage: bpy.types.Object) -> np.ndarray:
    """The reference vertices on an open end of the cage or on a border between two regions."""
    regions = _face_regions(cage.data)
    seen: dict[int, set[int]] = {}
    for polygon, region in zip(cage.data.polygons, regions):
        for v in polygon.vertices:
            seen.setdefault(v, set()).add(int(region))
    net = Net(cage.data)
    return np.nonzero(net.on_border | np.array([len(seen.get(v, ())) > 1 for v in range(net.count)]))[0]


class Skin:
    """A body's skin per cage region and side, for the fitting, and the whole body's skin, for
    keeping the cage outside and for the checks; with the hands and feet in it, the skin has no
    open ends at the cage's ends."""

    def __init__(self, body: bpy.types.Object):
        co = bodies.world_positions(body)
        zones = bodies.zones_of(body)
        polygons = [tuple(p.vertices) for p in body.data.polygons]
        centres = np.array([co[list(p)].mean(axis=0) for p in polygons])
        self.all = BVHTree.FromPolygons([tuple(c) for c in co], polygons)
        self.trees = {}
        for region in REGIONS:
            wanted = {bodies.ZONES.index(z) for z in SKIN_ZONES[region]}
            for side in (("L", "R") if region in SIDED else ("",)):
                keep = [p for p, z, c in zip(polygons, zones, centres)
                        if z in wanted and (not side or side_of(c[0]) == side)]
                self.trees[(region, side)] = BVHTree.FromPolygons([tuple(c) for c in co], keep)

    def nearest(self, key: tuple[str, str] | None, point) -> tuple[Vector, Vector, float]:
        """The nearest point of the region's skin, or of the whole skin without a region, its normal,
        and the point's distance outside the skin."""
        tree = self.all if key is None else self.trees[key]
        location, normal, _index, _distance = tree.find_nearest(Vector(point))
        if location is None:
            raise ArtPipelineError(f"The body has no skin of the cage region {key}.")
        return location, normal, (Vector(point) - location).dot(normal)


def _transfer(reference: np.ndarray, weights: np.ndarray, names: list[str], rig: bpy.types.Object,
              arm: bpy.types.Object) -> np.ndarray:
    """The reference moved with the rig from CageRig to the armature: each bone carries the vertices
    it weighs from its rest place in the reference rig to its rest place in the armature, stretched
    along its length, and the vertices blend the bones by their weights."""
    moved = np.zeros_like(reference)
    for column, name in enumerate(names):
        share = weights[:, column]
        if not share.any():
            continue
        source, target = rig.data.bones.get(name), arm.data.bones.get(name)
        if source is None or target is None:
            raise ArtPipelineError(f'The bone "{name}" of the cage is missing in "{rig.name}" or "{arm.name}".')
        stretch = Matrix.Diagonal((1.0, target.length / source.length, 1.0, 1.0))
        matrix = np.array(arm.matrix_world @ target.matrix_local @ stretch @ source.matrix_local.inverted()
                          @ rig.matrix_world.inverted())
        moved += (reference @ matrix[:3, :3].T + matrix[:3, 3]) * share[:, None]
    return moved / np.maximum(weights.sum(axis=1), 1e-9)[:, None]


def fit(label: str, figure: float) -> np.ndarray:
    """The cage's version on a body, as the fitting gives it, in world space."""
    cage, rig = _reference()
    arm = bpy.data.objects.get(label)
    body = bodies.source_body(label)
    if arm is None or arm.type != "ARMATURE" or body is None:
        raise ArtPipelineError(f'The file has no body "{label}" with its source body.')
    net = Net(cage.data)
    keys = vertex_keys(cage)
    names = [g.name for g in cage.vertex_groups]
    x = _transfer(bodies.world_positions(cage), bodies.weights_of(cage, names), names, rig, arm)
    skin = Skin(body)

    normals = net.normals(x)
    for i, key in enumerate(keys):
        tree = skin.trees[key]
        nearest, _normal, _index, distance = tree.find_nearest(Vector(x[i]))
        best = None
        if distance > NEAR:
            for sign in (1.0, -1.0):
                hit = tree.ray_cast(Vector(x[i]), Vector(normals[i] * sign), REACH)
                if hit[0] is not None and hit[3] <= distance + RAY_SLACK and (best is None or hit[3] < best[3]):
                    best = hit
        x[i] = np.array(best[0] if best is not None else nearest)

    # The ball rolls over the skin along normals that point out of the pits.
    normals = net.smoothed_normals(x, NORMAL_SMOOTHING)
    fade = np.minimum(net.hops_from(region_borders(cage), LIFT_FADE) / LIFT_FADE, 1.0)
    for i, key in enumerate(keys):
        if fade[i] <= 0.0:
            continue
        _location, skin_normal, _d = skin.nearest(key, x[i])
        direction = Vector(normals[i])
        if direction.dot(skin_normal) < NORMAL_AGREEMENT:
            direction = skin_normal
        centre = Vector(x[i]) + direction * figure
        tree = skin.trees[key]
        for _ in range(LIFT_STEPS):
            _l, _n, _index, distance = tree.find_nearest(centre)
            if distance >= figure - 1e-4:
                break
            centre += direction * (figure - distance)
        lift = min((centre - Vector(x[i])).dot(direction) - figure, LIFT_SHARE * figure)
        x[i] = x[i] + np.array(direction) * max(lift, 0.0) * fade[i]

    # A ring stops at a border between regions by itself, so the tension fades in from the open ends only.
    ends = np.minimum(net.hops_from(np.nonzero(net.on_border)[0], LIFT_FADE) / LIFT_FADE, 1.0)
    x = _tension(net, x, keys, _axes(cage, keys, names, arm), ends)
    x = net.faired(x, FAIRING)
    return _settle(net, x, skin)


def _axes(cage: bpy.types.Object, keys: list[tuple[str, str]], names: list[str], arm: bpy.types.Object) -> np.ndarray:
    """The axis each vertex's ring runs around: up for the trunk, which stands upright at rest, and
    for the neck and the limbs the bone of the armature that the vertex follows most in the
    reference."""
    weights = bodies.weights_of(cage, names)
    directions = []
    for name in names:
        bone = arm.data.bones.get(name)
        head, tail = arm.matrix_world @ bone.head_local, arm.matrix_world @ bone.tail_local
        directions.append(np.array((tail - head).normalized()))
    axes = np.array(directions)[np.argmax(weights, axis=1)]
    axes[[key[0] == "trunk" for key in keys]] = (0.0, 0.0, 1.0)
    return axes


def _tension(net: Net, x: np.ndarray, keys: list[tuple[str, str]], axes: np.ndarray, fade: np.ndarray) -> np.ndarray:
    """The rings drawn taut over the cage, outward only, each vertex along the part of its smoothed
    normal across its ring's axis. A band pulls in its own plane, so a vertex at the bottom of the
    buttock cleft, whose normal points down, still moves back across the cleft, and one whose
    normal points along the axis, such as the top of a shoulder, hardly moves."""
    a, b = net.edges[:, 0], net.edges[:, 1]
    along = x[b] - x[a]
    along /= np.maximum(np.linalg.norm(along, axis=1, keepdims=True), 1e-12)
    same = np.array([keys[i][0] == keys[j][0] for i, j in zip(a, b)])
    targets = np.concatenate([a[same], b[same]])
    sources = np.concatenate([b[same], a[same]])
    cosines = np.abs(np.einsum("ij,ij->i", np.concatenate([along[same], along[same]]), axes[targets]))
    weights = (1.0 - cosines ** 2) ** 2
    total = np.bincount(targets, weights, minlength=net.count)
    strength = np.where(total >= RING_WEIGHT, fade, 0.0)
    normals = net.smoothed_normals(x, NORMAL_SMOOTHING)
    across = normals - np.einsum("ij,ij->i", normals, axes)[:, None] * axes
    size = np.linalg.norm(across, axis=1)
    normals = across / np.maximum(size, 1e-12)[:, None]
    strength = strength * size
    offset = np.zeros(net.count)
    for _ in range(TENSION_STEPS):
        y = x + normals * offset[:, None]
        mean = np.zeros_like(y)
        np.add.at(mean, targets, y[sources] * weights[:, None])
        mean /= np.maximum(total, 1e-9)[:, None]
        pull = np.einsum("ij,ij->i", mean - y, normals)
        offset = np.maximum(offset + TENSION_RATE * pull * strength, 0.0)
    return x + normals * offset[:, None]


def _settle(net: Net, x: np.ndarray, skin: Skin) -> np.ndarray:
    """Every vertex, and the corners of every face whose centre, lies inside the skin or closer
    than the clearance moved out along the skin's normal, a few times over, because moving one face
    moves the corners it shares."""
    x = x.copy()
    points = [(i,) for i in range(net.count)] + net.faces
    for _ in range(SETTLE_PASSES):
        settled = True
        for corners in points:
            corners = list(corners)
            centre = x[corners].mean(axis=0)
            _location, normal, distance = skin.nearest(None, centre)
            if distance < CLEARANCE:
                x[corners] += np.array(normal) * (CLEARANCE + SETTLE_MARGIN - distance)
                settled = False
        if settled:
            break
    return x


def _attribute(mesh: bpy.types.Mesh, name: str) -> np.ndarray | None:
    attribute = mesh.attributes.get(name)
    if attribute is None or attribute.domain != "POINT" or attribute.data_type != "FLOAT_VECTOR":
        return None
    values = np.empty(len(mesh.vertices) * 3, np.float32)
    attribute.data.foreach_get("vector", values)
    return values.reshape(-1, 3).astype(np.float64)


def _store(mesh: bpy.types.Mesh, name: str, values: np.ndarray) -> None:
    attribute = mesh.attributes.get(name)
    if attribute is not None and (attribute.domain != "POINT" or attribute.data_type != "FLOAT_VECTOR"):
        mesh.attributes.remove(attribute)
        attribute = None
    attribute = attribute or mesh.attributes.new(name, "FLOAT_VECTOR", "POINT")
    attribute.data.foreach_set("vector", np.asarray(values, np.float32).reshape(-1))


def _apply(label: str, fitted: np.ndarray) -> None:
    """Gives the body's version the new fitting, keeping its corrections."""
    cage, _rig = _reference()
    arm = bpy.data.objects[label]
    version = bpy.data.objects.get(version_name(label))
    if version is None:
        mesh = cage.data.copy()
        mesh.name = version_name(label)
        version = bpy.data.objects.new(mesh.name, mesh)
        body_files.collection(collection_name(label)).objects.link(version)
        correction = np.zeros_like(fitted)
    else:
        if [tuple(p.vertices) for p in version.data.polygons] != [tuple(p.vertices) for p in cage.data.polygons]:
            raise ArtPipelineError(f'"{version.name}" no longer has the topology of the cage; delete it so the next '
                                   "sync fits it again.")
        previous = _attribute(version.data, FITTED)
        correction = bodies.world_positions(version) - previous if previous is not None else np.zeros_like(fitted)
    version.parent = arm
    version.matrix_parent_inverse = arm.matrix_world.inverted()
    version.matrix_world = Matrix.Identity(4)
    version.vertex_groups.clear()
    version.data.vertices.foreach_set("co", (fitted + correction).reshape(-1))
    _store(version.data, FITTED, fitted)
    version.data.update()


def _record(figure: float, lines: list[dict]) -> None:
    cage, _rig = _reference()
    cage["figure"] = float(figure)
    cage["lines"] = json.dumps(lines, sort_keys=True, separators=(",", ":"))


def _state(labels: list[str]) -> str:
    """SHA-256 of everything a sync writes, so a sync that changes nothing leaves the file as it is."""
    digest = hashlib.sha256()
    cage = bpy.data.objects.get(REFERENCE)
    if cage is not None:
        digest.update(f"{cage.get('figure')}|{cage.get('lines')}".encode("utf-8"))
    for label in labels:
        digest.update(label.encode("utf-8"))
        arm = bpy.data.objects.get(label)
        body = bodies.source_body(label)
        if arm is not None and arm.type == "ARMATURE" and body is not None:
            digest.update(body_files.fingerprint(arm, body).encode("utf-8"))
        version = bpy.data.objects.get(version_name(label))
        if version is None or version.type != "MESH":
            continue
        digest.update(f"{version.parent.name if version.parent else ''}".encode("utf-8"))
        digest.update((np.round(bodies.world_positions(version), 6) + 0.0).tobytes())
        fitted = _attribute(version.data, FITTED)
        digest.update(b"-" if fitted is None else (np.round(fitted, 6) + 0.0).tobytes())
    return digest.hexdigest()


def _show_first(labels: list[str]) -> None:
    """Shows the first body with its cage, the one a person corrects, and hides the others, which
    stand at the same place, and the reference."""
    for index, label in enumerate(labels):
        arm = bpy.data.objects[label]
        for obj in [arm] + list(arm.children_recursive):
            obj.hide_set(index > 0)
    cage, rig = _reference()
    cage.hide_set(True)
    rig.hide_set(True)


def create(file: str, body_list: list[tuple[str, str]], figure: float, lines: list[dict]) -> None:
    """Writes the first file of a cage: the bodies, and the reference made from the first body,
    then the cage's version on every body."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    arms = [body_files.append_body(path, root, collection_name(root)) for path, root in body_list]
    first = arms[0]
    body = bodies.source_body(first.name)
    reference = body_files.collection(REFERENCE_COLLECTION)

    rig = first.copy()
    rig.data = first.data.copy()
    rig.name = rig.data.name = REFERENCE_RIG
    rig.animation_data_clear()
    reference.objects.link(rig)
    for collection in list(rig.users_collection):
        if collection != reference:
            collection.objects.unlink(rig)

    zones = bodies.zones_of(body)
    regions = {bodies.ZONES.index(z): REGIONS.index(r) for r, zs in REGION_ZONES.items() for z in zs}
    bm = bmesh.new()
    bm.from_mesh(body.data)
    bm.faces.ensure_lookup_table()
    origin = bm.verts.layers.int.new("origin")
    for v in bm.verts:
        v[origin] = v.index
    region_layer = bm.faces.layers.int.new("region")
    for face in bm.faces:
        face[region_layer] = regions.get(int(zones[face.index]), -1)
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if f[region_layer] < 0], context="FACES")
    if bm.verts.layers.deform.active is not None:
        bm.verts.layers.deform.remove(bm.verts.layers.deform.active)
    sources = np.array([v[origin] for v in bm.verts])
    bm.verts.layers.int.remove(origin)
    mesh = bpy.data.meshes.new(REFERENCE)
    bm.to_mesh(mesh)
    bm.free()
    keep = {"position", "region", "sharp_face"}
    for name in [a.name for a in mesh.attributes if a.name not in keep and not a.name.startswith(".")]:
        mesh.attributes.remove(mesh.attributes[name])
    for layer in list(mesh.uv_layers):
        mesh.uv_layers.remove(layer)
    mesh.materials.clear()
    for polygon in mesh.polygons:
        polygon.use_smooth = True
    matrix = body.matrix_world
    mesh.transform(matrix)
    cage = bpy.data.objects.new(REFERENCE, mesh)
    reference.objects.link(cage)
    cage.parent = rig
    cage.matrix_parent_inverse = rig.matrix_world.inverted()
    names = [b.name for b in first.data.bones if b.use_deform]
    bodies.set_weights(cage, bodies.weights_of(body, names)[sources], names)

    _record(figure, lines)
    for arm in arms:
        _apply(arm.name, fit(arm.name, figure))
    _show_first([a.name for a in arms])
    relative_image_paths()
    bpy.ops.wm.save_as_mainfile(filepath=file, compress=True, relative_remap=False)


def sync(file: str, body_list: list[tuple[str, str]], figure: float, lines: list[dict]) -> bool:
    """Appends the bodies again that changed in their own files and fits the cage's version on
    every body again from the reference, keeping the corrections. Returns whether the file changed;
    an unchanged file is not saved, so its approval stays valid."""
    bpy.ops.wm.open_mainfile(filepath=file)
    labels = [root for _path, root in body_list]
    before = _state(labels)
    stale = set(body_files.stale_bodies(body_list))
    for path, root in body_list:
        if root in stale:
            body_files.replace_body(path, root, collection_name(root))
    # A body no longer in the manifest leaves the file with its version, which is a change too.
    removed = [o for o in bpy.data.objects if o.type == "ARMATURE" and o.name not in labels and o.name != REFERENCE_RIG]
    for obj in removed:
        for child in list(obj.children_recursive):
            bpy.data.objects.remove(child, do_unlink=True)
        bpy.data.objects.remove(obj, do_unlink=True)
    _record(figure, lines)
    for label in labels:
        _apply(label, fit(label, figure))
    if not removed and _state(labels) == before:
        return False
    if stale:
        _show_first(labels)
    relative_image_paths()
    bpy.ops.wm.save_as_mainfile(filepath=file, compress=True, relative_remap=False)
    return True


def stale_errors(body_list: list[tuple[str, str]], figure: float, lines: list[dict]) -> list[str]:
    """A cage is checked as a sync would leave it: its bodies are the current ones of their files,
    it was fitted with the figure scale and the lines of its asset.json, and every version is
    fitted to its body as the fitting gives it now."""
    stale = body_files.stale_bodies(body_list)
    if stale:
        return [f"The bodies {stale} of the cage changed in their own files; run tools/Sync-CageAsset.ps1."]
    cage, _rig = _reference()
    recorded = json.dumps(lines, sort_keys=True, separators=(",", ":"))
    if cage.get("figure") != float(figure) or cage.get("lines") != recorded:
        return ["The cage was fitted with another figure scale or other lines than its asset.json names; run "
                "tools/Sync-CageAsset.ps1."]
    errors = []
    for _path, label in body_list:
        version = bpy.data.objects.get(version_name(label))
        if version is None:
            errors.append(f'The cage has no version "{version_name(label)}"; run tools/Sync-CageAsset.ps1.')
            continue
        fitted = _attribute(version.data, FITTED)
        fresh = fit(label, figure)
        if fitted is None or fitted.shape != fresh.shape or np.abs(fitted - fresh).max() > FIT_TOLERANCE:
            errors.append(f'"{version.name}" is not fitted to its body as the fitting gives it now; run '
                          "tools/Sync-CageAsset.ps1.")
    return errors


def line_planes(label: str, line: dict) -> list[tuple[str, Vector, Vector]]:
    """A standard line on a body: per side its plane, a point and a normal, at the line's share of
    its bone's length, at right angles to the bone, or level across the trunk, which stands upright
    at rest while its bones lean."""
    arm = bpy.data.objects[label]
    planes = []
    for side in (("L", "R") if line["region"] in SIDED else ("",)):
        name = f"{line['bone']}_{side}" if side else line["bone"]
        bone = arm.data.bones.get(name)
        if bone is None:
            raise ArtPipelineError(f'The line {line["name"]} names the bone "{name}", which "{label}" does not have.')
        head = arm.matrix_world @ bone.head_local
        tail = arm.matrix_world @ bone.tail_local
        normal = Vector((0.0, 0.0, 1.0)) if line["region"] == "trunk" else (tail - head).normalized()
        planes.append((side, head.lerp(tail, float(line["share"])), normal))
    return planes


def line_segments(label: str, line: dict) -> list[tuple[str, list[tuple[Vector, Vector]]]]:
    """Where a standard line crosses its region of the cage's version on a body: per side the
    segments of the plane through the region's faces."""
    cage, _rig = _reference()
    version = bpy.data.objects[version_name(label)]
    co = bodies.world_positions(version)
    regions = _face_regions(cage.data)
    reference = bodies.world_positions(cage)
    wanted = REGIONS.index(line["region"])
    found = []
    for side, point, normal in line_planes(label, line):
        signed = (co - np.array(point)) @ np.array(normal)
        segments = []
        for polygon, region in zip(cage.data.polygons, regions):
            corners = list(polygon.vertices)
            if region != wanted or (side and side_of(reference[corners].mean(axis=0)[0]) != side):
                continue
            crossings = []
            for i in range(len(corners)):
                a, b = corners[i], corners[(i + 1) % len(corners)]
                if (signed[a] < 0.0) != (signed[b] < 0.0):
                    t = signed[a] / (signed[a] - signed[b])
                    crossings.append(Vector(co[a] + (co[b] - co[a]) * t))
            if len(crossings) == 2:
                segments.append((crossings[0], crossings[1]))
        found.append((side, segments))
    return found


def cage_errors(labels: list[str], lines: list[dict]) -> list[str]:
    """The model rules of a cage: a version per body with the reference's topology, no vertex and
    no face centre inside the skin, no face that crosses another, and every line crossing its
    region on every body."""
    errors = []
    try:
        cage, _rig = _reference()
    except ArtPipelineError as error:
        return [str(error)]
    faces = [tuple(p.vertices) for p in cage.data.polygons]
    for label in labels:
        version = bpy.data.objects.get(version_name(label))
        if version is None or version.type != "MESH":
            errors.append(f'The cage has no version "{version_name(label)}"; run tools/Sync-CageAsset.ps1.')
            continue
        if [tuple(p.vertices) for p in version.data.polygons] != faces:
            errors.append(f'"{version.name}" does not have the topology of the cage.')
            continue
        co = bodies.world_positions(version)
        skin = Skin(bodies.source_body(label))
        inside = [i for i, f in enumerate([(i,) for i in range(len(co))] + faces)
                  if skin.nearest(None, co[list(f)].mean(axis=0))[2] < CLEARANCE - 5e-4]
        if inside:
            errors.append(f'"{version.name}" lies inside the skin or closer than {CLEARANCE * 1000:.0f} mm at '
                          f"{len(inside)} vertices and face centres; run tools/Sync-CageAsset.ps1, or correct it outward.")
        tree = BVHTree.FromPolygons([tuple(c) for c in co], faces)
        crossing = [(a, b) for a, b in tree.overlap(tree) if a < b and not set(faces[a]) & set(faces[b])]
        if crossing:
            errors.append(f'"{version.name}" has {len(crossing)} pairs of faces that cross each other, such as faces '
                          f"{crossing[0][0]} and {crossing[0][1]}.")
        for line in lines:
            for side, segments in line_segments(label, line):
                if not segments:
                    where = f" on the {side} side" if side else ""
                    errors.append(f'The line {line["name"]} does not cross the {line["region"]} of "{version.name}"{where}; '
                                  "change its share.")
    return errors


def report(labels: list[str]) -> list[str]:
    """How far every version lies from the skin, and how much of it spans pits."""
    lines = []
    try:
        _reference()
    except ArtPipelineError:
        return lines
    for label in labels:
        version = bpy.data.objects.get(version_name(label))
        if version is None:
            continue
        gaps = distances(label)
        lines.append(f"{version.name}: {len(version.data.polygons)} faces, {gaps.min() * 1000:.1f} to "
                     f"{gaps.max() * 1000:.1f} mm from the skin, median {np.median(gaps) * 1000:.1f} mm, "
                     f"{int((gaps > 0.005).sum())} vertices span pits more than 5 mm deep")
    return lines


def distances(label: str) -> np.ndarray:
    """How far each vertex of the body's version lies from the skin."""
    skin = Skin(bodies.source_body(label))
    return np.array([skin.nearest(None, p)[2] for p in bodies.world_positions(bpy.data.objects[version_name(label)])])


def read_manifest(path: str) -> tuple[float, list[dict]]:
    """The figure scale and the standard lines of a cage's asset.json."""
    with open(path, encoding="utf-8") as handle:
        data = json.load(handle)
    figure = float(data["figure"])
    lines = [{"name": str(line["name"]), "region": str(line["region"]), "bone": str(line["bone"]),
              "share": float(line["share"])} for line in data["lines"]]
    if not math.isfinite(figure) or figure <= 0.0:
        raise ArtPipelineError("The figure scale of a cage must be a positive number of meters.")
    return figure, lines
