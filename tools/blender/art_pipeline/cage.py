"""The garment cage: a sparse net of fixed topology that the pipeline lays over every body of a
skeleton, smooth over the body's pits and following its figure. Garments are made and fitted on it
instead of on a body's own faces, so no garment depends on the topology of a body. See
docs/EQUIPMENT_VISUALS_DESIGN.md.

The file holds the reference in the collection CageReference: the armature CageRig, a copy of the
first body's rig when the cage was made, and on it the mesh Cage, that body's faces of the neck, the
trunk, the arms, and the legs, with its weights, the face attribute "region", and the figure scale
and the standard lines the cage was last fitted with as custom properties. The reference never
changes after it is made: a new base model changes the bodies, not the cage. Regions are only added
to it, the hands and the feet when the first gloves and boots came, with their vertices and faces
after the others, so every garment made before keeps its faces. The hands are the first body's
faces of its hands, with the face attribute "digit" for the finger or thumb each face belongs to.
The feet are a net of the cage's own, since a boot never shows the toes: rings that sweep from the
ankle around the heel and forward to the toe tip, with the point attributes "sweep_ring" and
"sweep_column" that place each vertex on them. Per body, the collection Fit_<Label> holds the body's
armature with the cage's version Cage_<Label> on it, and the Reference collection the body's source
body, eyes, teeth, and tongue.

A version is fitted from the reference alone, so a body of any topology gets one. The neck, the
trunk, the arms, and the legs are fitted first, together:

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

The hands and the feet are fitted after them and keep the rings at the wrists and the ankles as they
lie, so adding them changed nothing of the rest:

- A hand's vertex is drawn to the nearest skin of its own finger or thumb and of the palm, so a
  finger is never drawn to the next one, and the gaps between the fingers stay open.
- A foot is laid on the outline of its cross sections with their hollows spanned, their convex
  hulls: first half-planes that turn about an axis in front of the ankle, from the plane of the
  ankle ring down until they stand across the foot, so their rings run around the heel, then planes
  across the foot toward the toe tip. Each ring's vertices follow its outline at the shares at which
  the ankle ring's lie, and the last ring closes at the toe tip. The toes and the arch are spanned,
  and the sole is flat.
- The feet are faired; the hands already lie on their skin. Both are kept outside the skin like the
  rest.

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
from mathutils.interpolate import poly_3d_calc
from mathutils.kdtree import KDTree

from . import bodies, body_files
from .scene import ArtPipelineError, relative_image_paths

REFERENCE = "Cage"
REFERENCE_RIG = "CageRig"
REFERENCE_COLLECTION = "CageReference"
FITTED = "fitted"
DIGIT = "digit"
SWEEP_RING = "sweep_ring"
SWEEP_COLUMN = "sweep_column"
# The attributes that belong to the cage and not to what is made on it.
OWN_ATTRIBUTES = ("region", FITTED, DIGIT, SWEEP_RING, SWEEP_COLUMN)
# The regions in the order of the face attribute's values.
REGIONS = ("neck", "trunk", "arms", "legs", "hands", "feet")
# The regions fitted together, stage by stage: a later stage keeps the vertices it shares with an
# earlier one, so the regions added to a cage change nothing of the cage that was there. Within a
# stage, a vertex on the border of two regions belongs to the later one, so the limbs keep the
# vertices they share with the trunk. The first stage's regions are made with the cage; the others
# are added to it.
STAGES = (("neck", "trunk", "arms", "legs"), ("hands", "feet"))
# The region each extremity hangs from, at the ring they share.
ATTACHED = {"hands": "arms", "feet": "legs"}
# The body parts each region is made of, and the parts whose skin a region's vertices are drawn to
# and kept outside of. The neck and the trunk meet without a border of skin between them.
REGION_ZONES = {"neck": ("Neck",), "trunk": ("Torso", "Pelvis"), "arms": ("UpperArms", "Forearms"),
                "legs": ("Thighs", "Shins"), "hands": ("Hands",), "feet": ("Feet",)}
SKIN_ZONES = {"neck": ("Neck", "Torso"), "trunk": ("Torso", "Pelvis", "Neck"), "arms": ("UpperArms", "Forearms"),
              "legs": ("Thighs", "Shins"), "hands": ("Hands",), "feet": ("Feet",)}
SIDED = ("arms", "legs", "hands", "feet")
# The digits of a hand, by the start of their bones' names; the face attribute "digit" holds 1 + the
# index here, and 0 for the palm and every face outside the hands.
DIGITS = ("Thumb", "Index", "Middle", "Ring", "Little")
# A body's hand vertex on the border with its forearm must lie this close to the cage's wrist ring.
WRIST_MATCH = 1e-4
# The rings of a foot: half-planes turned by these shares of the turn from the ankle ring's plane
# until they stand across the foot, then planes across the foot at these shares of the way to the
# toe tip; the last ring closes at the tip. The axis the half-planes turn about lies this far in
# front of the ankle ring, so their rings keep apart at the front of the ankle.
FOOT_RINGS = tuple(("turn", step / 8) for step in range(1, 9)) + tuple(
    ("reach", share) for share in (0.15, 0.3, 0.45, 0.6, 0.72, 0.83, 0.92, 0.97))
PIVOT_MARGIN = 0.025
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
# The hands and the feet settle in more passes: around a slender fingertip, moving one face moves
# the next one back toward the skin.
ENDS_SETTLE_FACTOR = 4
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

    def __init__(self, faces: list[tuple[int, ...]], count: int):
        self.faces = faces
        self.count = count
        counts: dict[tuple[int, int], int] = {}
        for face in self.faces:
            for i in range(len(face)):
                key = tuple(sorted((face[i], face[(i + 1) % len(face)])))
                counts[key] = counts.get(key, 0) + 1
        edges = np.array(sorted(counts), np.int64).reshape(-1, 2)
        border = np.array([counts[tuple(e)] == 1 for e in edges], bool)
        self.border_edges = edges[border]
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

    @classmethod
    def of(cls, mesh: bpy.types.Mesh) -> "Net":
        return cls([tuple(p.vertices) for p in mesh.polygons], len(mesh.vertices))

    def loops(self) -> list[list[int]]:
        """The open ends, each a loop of its vertices in the order they follow each other."""
        neighbors: dict[int, list[int]] = {}
        for a, b in self.border_edges:
            neighbors.setdefault(int(a), []).append(int(b))
            neighbors.setdefault(int(b), []).append(int(a))
        seen, found = set(), []
        for start in sorted(neighbors):
            if start in seen:
                continue
            loop, previous, current = [start], None, start
            seen.add(start)
            while True:
                following = [n for n in neighbors[current] if n != previous and n not in seen]
                if not following:
                    break
                previous, current = current, following[0]
                loop.append(current)
                seen.add(current)
            found.append(loop)
        return found

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

    def faired(self, x: np.ndarray, iterations: int, held: np.ndarray | None = None) -> np.ndarray:
        # Taubin smoothing, a shrinking and an inflating step, keeps the cage's size. Held vertices
        # stay where they are.
        start = x
        for _ in range(iterations):
            x = x + 0.5 * (self.mean_of_neighbors(x) - x)
            x = x - 0.53 * (self.mean_of_neighbors(x) - x)
            if held is not None:
                x[held] = start[held]
        return x


class Part:
    """The faces of some regions of a cage, numbered among their own vertices: vertices holds each
    one's number in the cage."""

    def __init__(self, faces: list[tuple[int, ...]], regions: np.ndarray, wanted: tuple[str, ...]):
        chosen = [i for i, region in enumerate(regions) if REGIONS[region] in wanted]
        self.vertices = np.array(sorted({v for i in chosen for v in faces[i]}), np.int64)
        local = {int(v): i for i, v in enumerate(self.vertices)}
        self.faces = [tuple(local[v] for v in faces[i]) for i in chosen]
        self.regions = np.asarray(regions)[chosen]
        self.net = Net(self.faces, len(self.vertices))

    def keys(self, co: np.ndarray) -> list[tuple[str, str]]:
        """Every vertex's region, with its side for a limb or an extremity: of two regions whose
        border it lies on, the later one."""
        best = np.full(len(self.vertices), -1, np.int64)
        for face, region in zip(self.faces, self.regions):
            for v in face:
                best[v] = max(best[v], region)
        return [(REGIONS[r], side_of(co[i][0]) if REGIONS[r] in SIDED else "") for i, r in enumerate(best)]

    def borders(self) -> np.ndarray:
        """The vertices on an open end of the part or on a border between two of its regions."""
        seen: dict[int, set[int]] = {}
        for face, region in zip(self.faces, self.regions):
            for v in face:
                seen.setdefault(v, set()).add(int(region))
        return np.nonzero(self.net.on_border | np.array([len(seen.get(v, ())) > 1 for v in range(self.net.count)]))[0]


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


def _ints(mesh: bpy.types.Mesh, name: str, domain: str, default: int) -> np.ndarray:
    """An integer attribute of the mesh, or the default everywhere when it has none."""
    count = len(mesh.polygons) if domain == "FACE" else len(mesh.vertices)
    attribute = mesh.attributes.get(name)
    if attribute is None or attribute.domain != domain or attribute.data_type != "INT":
        return np.full(count, default, np.int64)
    values = np.empty(count, np.int32)
    attribute.data.foreach_get("value", values)
    return values.astype(np.int64)


def face_ints(mesh: bpy.types.Mesh, name: str, default: int) -> np.ndarray:
    """An integer face attribute of a cage's mesh, such as "region" or "digit"."""
    return _ints(mesh, name, "FACE", default)


def digit_of(bone: str) -> int:
    """1 + the index in DIGITS of the digit a bone belongs to, or 0."""
    return next((i + 1 for i, digit in enumerate(DIGITS) if bone.startswith(digit)), 0)


def face_digits(obj: bpy.types.Object) -> np.ndarray:
    """The digit of every face of a skinned mesh: that of the bone its corners weigh most together,
    or 0 for the palm and every face that no finger or thumb moves most."""
    names = [g.name for g in obj.vertex_groups]
    weights = bodies.weights_of(obj, names)
    of_name = np.array([digit_of(name) for name in names] or [0])
    found = np.zeros(len(obj.data.polygons), np.int64)
    for polygon in obj.data.polygons:
        total = weights[list(polygon.vertices)].sum(axis=0)
        if total.any():
            found[polygon.index] = of_name[int(np.argmax(total))]
    return found


def missing_regions() -> list[str]:
    """The regions the reference does not have yet, in their order."""
    cage, _rig = _reference()
    present = {REGIONS[r] for r in np.unique(_face_regions(cage.data))}
    return [region for region in REGIONS if region not in present]


class Skin:
    """A body's skin per cage region and side, for the fitting, and the whole body's skin, for
    keeping the cage outside and for the checks; with the hands and feet in it, the skin has no
    open ends at the cage's ends. A hand's skin is also split by its digits: each finger and the
    thumb with the palm. A foot's skin is kept as triangles too, for its cross sections."""

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
        hands, feet = bodies.ZONES.index("Hands"), bodies.ZONES.index("Feet")
        digits = face_digits(body)
        self.feet = {}
        for side in ("L", "R"):
            for digit in range(1, len(DIGITS) + 1):
                keep = [p for p, z, c, d in zip(polygons, zones, centres, digits)
                        if z == hands and side_of(c[0]) == side and d in (0, digit)]
                self.trees[("hands", side, digit)] = BVHTree.FromPolygons([tuple(c) for c in co], keep)
            self.feet[side] = np.array([co[[p[0], p[i], p[i + 1]]] for p, z, c in zip(polygons, zones, centres)
                                        if z == feet and side_of(c[0]) == side for i in range(1, len(p) - 1)])

    def nearest(self, key: tuple | None, point) -> tuple[Vector, Vector, float]:
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
    """The cage's version on a body, as the fitting gives it, in world space: stage by stage, each
    keeping the vertices an earlier stage placed."""
    cage, rig = _reference()
    arm = bpy.data.objects.get(label)
    body = bodies.source_body(label)
    if arm is None or arm.type != "ARMATURE" or body is None:
        raise ArtPipelineError(f'The file has no body "{label}" with its source body.')
    mesh = cage.data
    faces = [tuple(p.vertices) for p in mesh.polygons]
    regions = _face_regions(mesh)
    names = [g.name for g in cage.vertex_groups]
    weights = bodies.weights_of(cage, names)
    reference = bodies.world_positions(cage)
    x = _transfer(reference, weights, names, rig, arm)
    skin = Skin(body)
    digits = _ints(mesh, DIGIT, "FACE", 0)
    rings, columns = _ints(mesh, SWEEP_RING, "POINT", -1), _ints(mesh, SWEEP_COLUMN, "POINT", -1)
    placed = np.zeros(len(x), bool)
    for number, stage in enumerate(STAGES):
        part = Part(faces, regions, stage)
        if not len(part.vertices):
            continue
        held = placed[part.vertices]
        keys = part.keys(reference[part.vertices])
        if number == 0:
            y = _fit_body(part, x[part.vertices], keys, weights[part.vertices], names, arm, skin, figure)
        else:
            chosen = [i for i, region in enumerate(regions) if REGIONS[region] in stage]
            y = _fit_ends(part, x[part.vertices], keys, held, digits[chosen], rings[part.vertices],
                          columns[part.vertices], arm, skin)
        x[part.vertices[~held]] = y[~held]
        placed[part.vertices] = True
    return x


def _fit_body(part: Part, x: np.ndarray, keys: list[tuple[str, str]], weights: np.ndarray, names: list[str],
              arm: bpy.types.Object, skin: Skin, figure: float) -> np.ndarray:
    """The neck, the trunk, the arms, and the legs: drawn to the skin, lifted out of the pits by the
    ball, drawn taut along their rings, faired, and kept outside the skin."""
    net = part.net
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
    fade = np.minimum(net.hops_from(part.borders(), LIFT_FADE) / LIFT_FADE, 1.0)
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
    x = _tension(net, x, keys, _axes(weights, keys, names, arm), ends)
    x = net.faired(x, FAIRING)
    return _settle(net, x, skin)


def _fit_ends(part: Part, x: np.ndarray, keys: list[tuple[str, str]], held: np.ndarray, digits: np.ndarray,
              rings: np.ndarray, columns: np.ndarray, arm: bpy.types.Object, skin: Skin) -> np.ndarray:
    """The hands and the feet, from the rings they share with the limbs, which stay: a hand's
    vertices drawn to the skin of their own digit and the palm, a foot laid on its rings, and both
    faired and kept outside the skin."""
    x = x.copy()
    # A vertex whose faces all belong to one finger or the thumb is drawn to its skin; one at the
    # palm or between them to the whole hand's.
    around: dict[int, set[int]] = {}
    for face, digit in zip(part.faces, digits):
        for v in face:
            around.setdefault(v, set()).add(int(digit))
    for i, key in enumerate(keys):
        if held[i] or key[0] != "hands":
            continue
        own = around.get(i, {0})
        tree = skin.trees[("hands", key[1], next(iter(own)))] if len(own) == 1 and 0 not in own else skin.trees[key]
        location, _normal, _index, _distance = tree.find_nearest(Vector(x[i]))
        x[i] = np.array(location)
    for side in ("L", "R"):
        members = np.array([i for i, key in enumerate(keys) if key == ("feet", side)], np.int64)
        if not len(members):
            continue
        ring, column = rings[members], columns[members]
        if ring.min() < 0 or ring.max() != len(FOOT_RINGS) + 1:
            raise ArtPipelineError(f"The foot of the cage on the {side} side was not made with the {len(FOOT_RINGS)} rings "
                                   "of the fitting; make its feet region again.")
        ankle = np.zeros((int(column[ring == 0].max()) + 1, 3))
        ankle[column[ring == 0]] = x[members[ring == 0]]
        laid, pole = _foot_rings(arm, side, ankle, skin.feet[side])
        for i, r, c in zip(members, ring, column):
            if not held[i]:
                x[i] = pole if r == len(FOOT_RINGS) + 1 else laid[r - 1][c]
    # The feet are faired, which spreads their rings where they crowd at the front of the ankle; the
    # hands lie on their skin already, and fairing would only draw in the fingertips.
    x = part.net.faired(x, FAIRING, held | np.array([key[0] != "feet" for key in keys]))
    return _settle(part.net, x, skin, held, SETTLE_PASSES * ENDS_SETTLE_FACTOR)


def _hull(points: np.ndarray) -> np.ndarray:
    """The convex hull of points in a plane, counterclockwise."""
    unique = sorted({(float(p[0]), float(p[1])) for p in np.round(points, 9)})
    if len(unique) < 3:
        return np.array(unique).reshape(-1, 2)

    def turn(o, a, b) -> float:
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])

    lower, upper = [], []
    for p in unique:
        while len(lower) >= 2 and turn(lower[-2], lower[-1], p) <= 0.0:
            lower.pop()
        lower.append(p)
    for p in reversed(unique):
        while len(upper) >= 2 and turn(upper[-2], upper[-1], p) <= 0.0:
            upper.pop()
        upper.append(p)
    return np.array(lower[:-1] + upper[:-1])


def _centroid(polygon: np.ndarray) -> np.ndarray:
    """The centre of a polygon's area."""
    x, y = polygon[:, 0], polygon[:, 1]
    xn, yn = np.roll(x, -1), np.roll(y, -1)
    cross = x * yn - xn * y
    return np.array((((x + xn) * cross).sum(), ((y + yn) * cross).sum())) / (3.0 * cross.sum())


def _toward(polygon: np.ndarray, centre: np.ndarray, angle: float) -> tuple[int, np.ndarray]:
    """Where a ray from a point inside a convex polygon, at an angle, meets its outline: the edge
    from the polygon's corner of that number, and the point."""
    direction = np.array((math.cos(angle), math.sin(angle)))
    best = None
    for index, (a, b) in enumerate(zip(polygon, np.roll(polygon, -1, axis=0))):
        edge, start = b - a, a - centre
        determinant = edge[0] * direction[1] - edge[1] * direction[0]
        if abs(determinant) < 1e-15:
            continue
        t = (edge[0] * start[1] - edge[1] * start[0]) / determinant
        u = (direction[0] * start[1] - direction[1] * start[0]) / determinant
        if t > 0.0 and -1e-9 <= u <= 1.0 + 1e-9 and (best is None or t < best[0]):
            best = (t, index)
    if best is None:
        raise ArtPipelineError("A ring of a foot of the cage found no outline around its centre.")
    return best[1], centre + direction * best[0]


def _shares(polygon: np.ndarray) -> np.ndarray:
    """How far along a closed polygon's outline each of its corners lies, as shares of the outline."""
    lengths = np.linalg.norm(np.roll(polygon, -1, axis=0) - polygon, axis=1)
    return np.concatenate([[0.0], np.cumsum(lengths)[:-1]]) / lengths.sum()


def _along(polygon: np.ndarray, shares: np.ndarray) -> np.ndarray:
    """The points at shares of a closed polygon's outline, from its first corner."""
    closed = np.concatenate([polygon, polygon[:1]])
    lengths = np.linalg.norm(closed[1:] - closed[:-1], axis=1)
    reach = np.concatenate([[0.0], np.cumsum(lengths)])
    distance = shares * reach[-1]
    return np.stack([np.interp(distance, reach, closed[:, k]) for k in range(2)], axis=1)


def _section(triangles: np.ndarray, origin: np.ndarray, across: np.ndarray, down: np.ndarray) -> np.ndarray:
    """Where a half-plane cuts the triangles, in the half-plane's coordinates: along its axis across,
    and away from the axis along down."""
    normal = np.cross(across, down)
    signed = (triangles - origin) @ normal
    found = []
    for i, j in ((0, 1), (1, 2), (2, 0)):
        crossing = (signed[:, i] < 0.0) != (signed[:, j] < 0.0)
        a, b = triangles[crossing, i], triangles[crossing, j]
        t = signed[crossing, i] / (signed[crossing, i] - signed[crossing, j])
        found.append(a + (b - a) * t[:, None])
    points = np.concatenate(found) - origin
    flat = np.stack([points @ across, points @ down], axis=1)
    return flat[flat[:, 1] > 0.0]


def _foot_rings(arm: bpy.types.Object, side: str, ankle: np.ndarray, triangles: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """A foot's rings on a body, one row of points per ring of FOOT_RINGS, and the point that closes
    the last ring at the toe tip. The half-planes turn about an axis across the foot, in the ankle
    ring's plane, in front of the ankle, from that plane down until they stand across the foot's
    forward direction; the planes after them stand across it. Each ring follows the convex hull of
    its cross section of the foot's skin: its first point lies where the ray from the hull's centre
    at the angle of the ankle ring's first point meets the hull, the back of the ankle on the heel
    and the middle of the sole further on, and the others follow around the hull in the ankle ring's
    direction at the shares of the outline that the ankle ring's points lie at, so the rings keep
    their points spread evenly and do not cut the corners of a flat section."""
    if not len(triangles):
        raise ArtPipelineError(f"The body has no skin of the foot on the {side} side.")
    world = arm.matrix_world
    joint = np.array(world @ arm.data.bones[f"LowerLeg_{side}"].tail_local)
    toe = np.array(world @ arm.data.bones[f"Toes_{side}"].tail_local)
    forward = toe - joint
    forward[2] = 0.0
    forward /= np.linalg.norm(forward)
    centre = ankle.mean(axis=0)
    normal = np.linalg.svd(ankle - centre)[2][2]
    if normal[2] > 0.0:
        normal = -normal
    back = -forward - np.dot(-forward, normal) * normal
    back /= np.linalg.norm(back)
    across = np.cross(back, normal)
    pivot = centre - back * (((ankle - centre) @ -back).max() + PIVOT_MARGIN)
    # The turn after which a half-plane stands across the forward direction.
    full = math.atan2(-np.dot(back, forward), np.dot(normal, forward))
    start = np.stack([(ankle - pivot) @ across, (ankle - pivot) @ back], axis=1)
    middle = _centroid(start)
    first = math.atan2(start[0, 1] - middle[1], start[0, 0] - middle[0])
    shares = _shares(start)
    # The hulls run counterclockwise; a ring that runs the other way walks them backward.
    turning = np.sign(np.sum(start[:, 0] * np.roll(start[:, 1], -1) - np.roll(start[:, 0], -1) * start[:, 1]))
    tip = float(((triangles.reshape(-1, 3) - pivot) @ forward).max())
    rings = []
    for kind, share in FOOT_RINGS:
        turn = full * share if kind == "turn" else full
        down = back * math.cos(turn) + normal * math.sin(turn)
        origin = pivot if kind == "turn" else pivot + forward * tip * share
        hull = _hull(_section(triangles, origin, across, down))
        if len(hull) < 3:
            raise ArtPipelineError(f"A ring of the foot on the {side} side cuts too little of its skin.")
        middle = _centroid(hull)
        edge, point = _toward(hull, middle, first)
        outline = np.concatenate([[point], hull[edge + 1:], hull[:edge + 1]])
        if turning < 0.0:
            outline = np.concatenate([outline[:1], outline[1:][::-1]])
        rings.append([origin + across * p[0] + down * p[1] for p in _along(outline, shares)])
    pole = pivot + forward * tip + across * middle[0] + down * middle[1]
    return np.array(rings), pole


def _axes(weights: np.ndarray, keys: list[tuple[str, str]], names: list[str], arm: bpy.types.Object) -> np.ndarray:
    """The axis each vertex's ring runs around: up for the trunk, which stands upright at rest, and
    for the neck and the limbs the bone of the armature that the vertex follows most in the
    reference."""
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


def _settle(net: Net, x: np.ndarray, skin: Skin, held: np.ndarray | None = None, passes: int = SETTLE_PASSES) -> np.ndarray:
    """Every vertex, and the corners of every face whose centre, lies inside the skin or closer
    than the clearance moved out along the skin's normal, a few times over, because moving one face
    moves the corners it shares. Held vertices stay where they are."""
    x = x.copy()
    points = [(i,) for i in range(net.count)] + net.faces
    for _ in range(passes):
        settled = True
        for corners in points:
            corners = list(corners)
            centre = x[corners].mean(axis=0)
            _location, normal, distance = skin.nearest(None, centre)
            if distance < CLEARANCE:
                moving = corners if held is None else [c for c in corners if not held[c]]
                if not moving:
                    continue
                x[moving] += np.array(normal) * (CLEARANCE + SETTLE_MARGIN - distance)
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
        own = [tuple(p.vertices) for p in version.data.polygons]
        faces = [tuple(p.vertices) for p in cage.data.polygons]
        if own != faces[:len(own)]:
            raise ArtPipelineError(f'"{version.name}" no longer has the topology of the cage; delete it so the next '
                                   "sync fits it again.")
        previous = _attribute(version.data, FITTED)
        correction = np.zeros_like(fitted)
        if previous is not None:
            correction[:len(previous)] = bodies.world_positions(version) - previous
        if len(own) < len(faces):
            # Regions were added to the cage after its versions: each gets them, and its vertices
            # keep their numbers and corrections.
            old = version.data
            version.data = cage.data.copy()
            bpy.data.meshes.remove(old)
            version.data.name = version_name(label)
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
        digest.update(f"{cage.get('figure')}|{cage.get('lines')}|{len(cage.data.polygons)}".encode("utf-8"))
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


def _append(points: list, rows: list, bones: list[str], faces: list[tuple[int, ...]], region: str,
            digits: list[int] | None = None, sweep: dict[int, tuple[int, int]] | None = None) -> None:
    """Adds vertices, at points in world space with their weights over the bones, and faces of a
    region to the reference, after its own, so every vertex and face it had keeps its number. sweep
    gives the vertices of a foot's rings their ring and column."""
    cage, _rig = _reference()
    carried = np.array(rows).max(axis=0) > 0.0 if rows else np.zeros(len(bones), bool)
    for column in np.nonzero(carried)[0]:
        if bones[column] not in cage.vertex_groups:
            cage.vertex_groups.new(name=bones[column])
    groups = {g.name: g.index for g in cage.vertex_groups}
    inverse = cage.matrix_world.inverted()
    bm = bmesh.new()
    bm.from_mesh(cage.data)
    deform = bm.verts.layers.deform.verify()
    region_layer = bm.faces.layers.int.get("region")
    digit_layer = bm.faces.layers.int.get(DIGIT) or bm.faces.layers.int.new(DIGIT)
    sweep_layers = []
    for name in (SWEEP_RING, SWEEP_COLUMN):
        layer = bm.verts.layers.int.get(name)
        if layer is None:
            layer = bm.verts.layers.int.new(name)
            for v in bm.verts:
                v[layer] = -1
        sweep_layers.append(layer)
    for point, row in zip(points, rows):
        v = bm.verts.new(inverse @ Vector(point))
        for column, weight in enumerate(row):
            if weight > 0.0:
                v[deform][groups[bones[column]]] = float(weight)
        for layer in sweep_layers:
            v[layer] = -1
    bm.verts.ensure_lookup_table()
    for index, corners in enumerate(faces):
        face = bm.faces.new([bm.verts[c] for c in corners])
        face[region_layer] = REGIONS.index(region)
        face[digit_layer] = int(digits[index]) if digits is not None else 0
        face.smooth = True
    for vertex, (ring, column) in (sweep or {}).items():
        bm.verts[vertex][sweep_layers[0]] = ring
        bm.verts[vertex][sweep_layers[1]] = column
    bm.to_mesh(cage.data)
    bm.free()
    cage.data.update()


def _skin_rows(body: bpy.types.Object, polygons: list[tuple[int, ...]], weights: np.ndarray, points) -> list[np.ndarray]:
    """The body's weights at points on its skin of the given faces, from the corners of the nearest
    face."""
    co = bodies.world_positions(body)
    tree = BVHTree.FromPolygons([tuple(c) for c in co], polygons)
    rows = []
    for point in points:
        location, _normal, index, _distance = tree.find_nearest(Vector(point))
        corners = polygons[index]
        shares = poly_3d_calc([Vector(co[v]) for v in corners], location)
        rows.append(sum(weights[v] * share for v, share in zip(corners, shares)))
    return rows


def _add_hands(label: str) -> None:
    """The hands region from the body's faces of its hands, met at the cage's wrist rings, with the
    digit of every face."""
    cage, rig = _reference()
    arm, body = bpy.data.objects[label], bodies.source_body(label)
    zones = bodies.zones_of(body)
    hands, forearms = bodies.ZONES.index("Hands"), bodies.ZONES.index("Forearms")
    chosen = [p for p in body.data.polygons if zones[p.index] == hands]
    wrists = {v for p in body.data.polygons if zones[p.index] == forearms for v in p.vertices}
    used = sorted({v for p in chosen for v in p.vertices})
    bones = [b.name for b in arm.data.bones if b.use_deform]
    weights = bodies.weights_of(body, bones)
    moved = _transfer(bodies.world_positions(body)[used], weights[used], bones, arm, rig)
    reference = bodies.world_positions(cage)
    stage = Part([tuple(p.vertices) for p in cage.data.polygons], _face_regions(cage.data), STAGES[0])
    ends = stage.vertices[np.unique(stage.net.border_edges)]
    near = KDTree(len(ends))
    for i, v in enumerate(ends):
        near.insert(reference[v], i)
    near.balance()
    number, points, rows = {}, [], []
    for v, point in zip(used, moved):
        if v in wrists:
            _co, index, distance = near.find(point)
            if distance > WRIST_MATCH:
                raise ArtPipelineError(f"The hands of {label} do not meet the cage's wrists, which were made from another "
                                       "body; the hands region is made from the body the cage was made from.")
            number[v] = int(ends[index])
        else:
            number[v] = len(reference) + len(points)
            points.append(point)
            rows.append(weights[v])
    digits = face_digits(body)
    _append(points, rows, bones, [tuple(number[v] for v in p.vertices) for p in chosen], "hands",
            [int(digits[p.index]) for p in chosen])


def _add_feet(label: str, figure: float) -> None:
    """The feet region: on each side the rings of the foot from the cage's ankle ring to the toe
    tip, laid on the body as the fitting lays them, each ring a quad strip and the last one closed by
    a fan at the tip."""
    cage, rig = _reference()
    arm, body = bpy.data.objects[label], bodies.source_body(label)
    x = fit(label, figure)
    faces = [tuple(p.vertices) for p in cage.data.polygons]
    regions = _face_regions(cage.data)
    stage = Part(faces, regions, STAGES[0])
    keys = stage.keys(bodies.world_positions(cage)[stage.vertices])
    skin = Skin(body)
    zones = bodies.zones_of(body)
    bones = [b.name for b in arm.data.bones if b.use_deform]
    weights = bodies.weights_of(body, bones)
    count = len(x)
    points, rows, new_faces, sweep = [], [], [], {}
    for side in ("L", "R"):
        loops = [loop for loop in stage.net.loops() if all(keys[v] == ("legs", side) for v in loop)]
        if len(loops) != 1:
            raise ArtPipelineError(f"The cage has {len(loops)} open ends of the leg on the {side} side, not one ankle ring.")
        ring = [int(stage.vertices[v]) for v in loops[0]]
        # The new faces run along the ankle ring against the leg's faces there.
        leg = next(f for f in faces if ring[0] in f and ring[1] in f)
        if leg[(leg.index(ring[0]) + 1) % len(leg)] == ring[1]:
            ring.reverse()
        forward = np.array(arm.matrix_world @ arm.data.bones[f"Toes_{side}"].tail_local) - x[ring].mean(axis=0)
        back = int(np.argmin(x[ring] @ forward))
        ring = ring[back:] + ring[:back]
        laid, pole = _foot_rings(arm, side, x[ring], skin.feet[side])
        rows_of_side = [ring]
        for index, row in enumerate(laid):
            numbers = list(range(count + len(points), count + len(points) + len(row)))
            points.extend(row)
            rows_of_side.append(numbers)
            for column, vertex in enumerate(numbers):
                sweep[vertex] = (index + 1, column)
        tip = count + len(points)
        points.append(pole)
        sweep[tip] = (len(FOOT_RINGS) + 1, 0)
        for column, vertex in enumerate(ring):
            sweep[vertex] = (0, column)
        size = len(ring)
        for upper, lower in zip(rows_of_side, rows_of_side[1:]):
            for j in range(size):
                new_faces.append((upper[j], upper[(j + 1) % size], lower[(j + 1) % size], lower[j]))
        last = rows_of_side[-1]
        for j in range(size):
            new_faces.append((last[j], last[(j + 1) % size], tip))
    feet = bodies.ZONES.index("Feet")
    polygons = [tuple(p.vertices) for p in body.data.polygons if zones[p.index] == feet]
    rows = _skin_rows(body, polygons, weights, points)
    moved = _transfer(np.array(points), np.array(rows), bones, arm, rig)
    _append(list(moved), rows, bones, new_faces, "feet", sweep=sweep)


def _add_regions(label: str, figure: float) -> list[str]:
    """Adds the regions the reference lacks, built on its first body, with their vertices and faces
    after its own. Returns the regions it added."""
    missing = missing_regions()
    early = [region for region in missing if region in STAGES[0]]
    if early:
        raise ArtPipelineError(f"The reference lacks the regions {early}, which are made with the cage; make the cage again.")
    for region in missing:
        if region == "hands":
            _add_hands(label)
        elif region == "feet":
            _add_feet(label, figure)
    return missing


def create(file: str, body_list: list[tuple[str, str]], figure: float, lines: list[dict]) -> None:
    """Writes the first file of a cage: the bodies, and the reference made from the first body, its
    first stage's regions from the body's faces and the others added to them, then the cage's version
    on every body."""
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
    regions = {bodies.ZONES.index(z): REGIONS.index(r) for r in STAGES[0] for z in REGION_ZONES[r]}
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
    _add_regions(first.name, figure)
    for arm in arms:
        _apply(arm.name, fit(arm.name, figure))
    _show_first([a.name for a in arms])
    relative_image_paths()
    bpy.ops.wm.save_as_mainfile(filepath=file, compress=True, relative_remap=False)


def sync(file: str, body_list: list[tuple[str, str]], figure: float, lines: list[dict]) -> tuple[bool, list[str]]:
    """Appends the bodies again that changed in their own files, adds the regions the reference
    lacks, and fits the cage's version on every body again from the reference, keeping the
    corrections. Returns whether the file changed and the regions it added; an unchanged file is
    not saved, so its approval stays valid."""
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
    added = _add_regions(labels[0], figure)
    for label in labels:
        _apply(label, fit(label, figure))
    if not removed and not added and _state(labels) == before:
        return False, added
    if stale or added:
        _show_first(labels)
    relative_image_paths()
    bpy.ops.wm.save_as_mainfile(filepath=file, compress=True, relative_remap=False)
    return True, added


def stale_errors(body_list: list[tuple[str, str]], figure: float, lines: list[dict]) -> list[str]:
    """A cage is checked as a sync would leave it: its bodies are the current ones of their files,
    it was fitted with the figure scale and the lines of its asset.json, and every version is
    fitted to its body as the fitting gives it now."""
    stale = body_files.stale_bodies(body_list)
    if stale:
        return [f"The bodies {stale} of the cage changed in their own files; run tools/Sync-CageAsset.ps1."]
    missing = missing_regions()
    if missing:
        return [f"The cage lacks the regions {missing}; run tools/Sync-CageAsset.ps1, which adds them."]
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
