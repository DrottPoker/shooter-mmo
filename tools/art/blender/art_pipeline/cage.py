"""The garment cage: a sparse net of fixed topology that the pipeline lays over every body of a
skeleton, smooth over the body's pits and following its figure. Garments are made and fitted on it
instead of on a body's own faces, so no garment depends on the topology of a body. See
docs/EQUIPMENT_VISUALS_DESIGN.md.

The file holds the reference in the collection CageReference: the armature CageRig, a copy of the
first body's rig when the cage was made, and on it the mesh Cage, that body's faces of the neck, the
trunk, the arms, and the legs, with its weights, the face attribute "region", and the figure scale
and the standard lines the cage was last fitted with as custom properties. The reference never
changes after it is made: a new base model changes the bodies, not the cage. Regions are only added
to it, the hands and the feet when the first gloves and boots came and the head with the first hair
and headwear, with their vertices and faces after the others, so every garment made before keeps its
faces. The hands are the first body's faces of its hands, with the face attribute "digit" for the
finger or thumb each face belongs to. The feet are a net of the cage's own, since a boot never shows
the toes: rings that sweep from the ankle around the heel and forward to the toe tip, with the point
attributes "sweep_ring" and "sweep_column" that place each vertex on them. The head is a net of the
cage's own too, since hair, hoods, and helmets lie over the ears and never in the eye sockets: rings
that sweep from the neck's top up to the crown. Per body, the collection Fit_<Label> holds the body's
armature with the cage's version Cage_<Label> on it, and the Reference collection the body's source
body, eyes, teeth, and tongue.

A version is fitted from the reference alone, so a body of any topology gets one. The neck, the
trunk, the arms, and the legs are fitted first, together:

1. The reference moves with the rig from CageRig to the body's armature, every bone with its
   length, blended by the reference's weights.
2. Every vertex is drawn to the skin of its region: to the nearest skin where it lies close to it,
   otherwise along the cage's normal, toward whichever side meets the skin first, or to the nearest
   skin where no ray meets it at about the nearest skin's distance. Where a limb stands off the
   trunk, as an arm does at the armpit, the border between the limb and the trunk then moves into
   the fold where their skin meets, so the net runs along the limb on one side of the fold and
   along the trunk on the other and no face hangs across the hollow like a web.
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
5. The cage is faired, and no vertex or face centre stays inside the skin, nor does the skin come
   out through a face between its corners, where a large face cuts across a curve of the skin, or
   past an edge of an open end, such as an ankle ring, which a later stage hangs its faces from.
   The fairing leaves the vertices in a fold where they are and fades in from them, since rounding
   the net off would lift it out of the fold again.

A body is the mirror image of itself across its middle plane, and its two halves face each other in
its clefts, such as the crotch and the buttock cleft. A vertex that moved out along its normal there
would cross to the other half, so the vertices of the neck and the trunk keep to their side of the
plane: one in the plane stays in it, one beside it is drawn to the skin of its own half, and neither
the ball nor the tension moves it toward the plane. Where the body and the reference are mirrored,
the two sides are also evened out after every step, so a mirrored body gets a mirrored cage: what
would differ comes from the order of the vertices and from points that lie as near to one face of
the skin as to the next.

The hands, the feet, and the head are fitted after them and keep the rings at the wrists, the
ankles, and the top of the neck as they lie, so adding them changed nothing of the rest:

- A hand's net is drawn to the skin as a whole, each vertex toward the nearest skin of its own
  finger or thumb and of the palm, so a finger is never drawn to the next one, and the gaps between
  the fingers stay open. The moves are evened out between neighbors first and ever less, so the net
  slides into place in one piece before every vertex takes its place on the skin: a hand of
  another build than the reference's has its thumb and fingers at other angles, and vertices that
  went to the nearest skin one by one would fold the net over itself. The vertices of the palm and
  between the digits then spread evenly over the skin.
- A foot is laid on the outline of its cross sections with their hollows spanned, their convex
  hulls: first half-planes that turn about an axis in front of the ankle, from the plane of the
  ankle ring down until they stand across the foot, so their rings run around the heel, then planes
  across the foot toward the toe tip. Each ring's vertices follow its outline at the shares at which
  the ankle ring's lie, and the last ring closes at the toe tip. The toes and the arch are spanned,
  and the sole is flat.
- The head is laid on the convex hulls of its cross sections the same way: planes that turn from
  the plane of the neck's top ring until they lie across the head's bone, then planes across it up
  to the crown, where the last ring closes. The ears, the nose, the lips, and the eye sockets are
  spanned.
- The feet and the head are faired; the hands already lie on their skin. All are kept outside the
  skin like the rest.

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

from . import bodies, body_files, manifest_io
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
REGIONS = ("neck", "trunk", "arms", "legs", "hands", "feet", "head")
# The regions fitted together, stage by stage: a later stage keeps the vertices it shares with an
# earlier one, so the regions added to a cage change nothing of the cage that was there. Within a
# stage, a vertex on the border of two regions belongs to the later one, so the limbs keep the
# vertices they share with the trunk. The first stage's regions are made with the cage; the others
# are added to it.
STAGES = (("neck", "trunk", "arms", "legs"), ("hands", "feet", "head"))
# The region each extremity hangs from, at the ring they share.
ATTACHED = {"hands": "arms", "feet": "legs", "head": "neck"}
# The body parts each region is made of, and the parts whose skin a region's vertices are drawn to
# and kept outside of. The neck and the trunk meet without a border of skin between them.
REGION_ZONES = {"neck": ("Neck",), "trunk": ("Torso", "Pelvis"), "arms": ("UpperArms", "Forearms"),
                "legs": ("Thighs", "Shins"), "hands": ("Hands",), "feet": ("Feet",), "head": ("Head",)}
SKIN_ZONES = {"neck": ("Neck", "Torso"), "trunk": ("Torso", "Pelvis", "Neck"), "arms": ("UpperArms", "Forearms"),
              "legs": ("Thighs", "Shins"), "hands": ("Hands",), "feet": ("Feet",), "head": ("Head",)}
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
# The rings of the head: planes at these shares of the way from the centre of the neck's top ring
# to the top of the head along the head's bone, evenly up the neck and the face and closer together
# over the crown, so its faces keep their size over the dome. The first HEAD_TURN planes turn from
# the plane of the neck's ring until they lie across the bone; the last ring closes at the crown.
HEAD_RINGS = (0.071, 0.142, 0.213, 0.284, 0.355, 0.426, 0.497, 0.568, 0.638,
              0.709, 0.777, 0.840, 0.894, 0.939, 0.973, 0.993)
HEAD_TURN = 3
# How far from the skin the cage keeps at least, in meters.
CLEARANCE = 0.001
# How far in from the skin a ray looks for a face of the cage that the skin comes out through, and
# the least share of the point the skin meets that a triangle's free corners must carry for the
# triangle to move.
POKE_REACH = 0.02
POKE_FREE_SHARE = 0.5
# A vertex where a limb meets the trunk lies in the fold between their skin once the middle of an
# edge from it into the trunk lies this far outside the skin, and part of the way there before.
FOLD_SPAN = 0.01
# A vertex that moved this far into a fold lies in it, and the fairing leaves it there.
FOLD_HELD = 0.001
# The points of an edge of an open end that are kept outside the skin, as shares of the edge.
END_EDGE_SHARES = (0.25, 0.5, 0.75)
# How far inside the skin a ray through the body starts, so it does not meet the skin it starts on.
SKIN_DEPTH = 1e-5
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
# A vertex of the reference this close to the body's middle plane lies in it.
MIDDLE = 1e-4
# A point's mirror image across the body's middle plane. Two vertices of the reference are each
# other's mirror images when they lie this close to them, and two of a body's skin this close.
MIRROR = np.array((-1.0, 1.0, 1.0))
TWIN_REACH = 1e-3
SKIN_TWIN_REACH = 1e-5
MIRROR_BLOCK = 256
# A net is drawn to the skin in rounds: in each, every vertex's move to the skin is evened out with
# its neighbors' this often before the net moves, so whole stretches of the net move together at
# first, and the last round lays every vertex on the skin.
DRAW_ROUNDS = (24, 12, 6, 3, 1, 0)
# How often the vertices of a hand between its digits and on its palm move toward the middle of
# their neighbors and back onto the skin.
SPREAD_STEPS = 30


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

    def limb_roots(self) -> np.ndarray:
        """Whether each vertex lies where a limb meets the trunk: on the border between a region
        with sides and the trunk."""
        seen: dict[int, set[str]] = {}
        for face, region in zip(self.faces, self.regions):
            for v in face:
                seen.setdefault(v, set()).add(REGIONS[region])
        return np.array(["trunk" in seen.get(v, ()) and any(region in SIDED for region in seen.get(v, ()))
                         for v in range(self.net.count)], bool)

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
    thumb with the palm. A foot's skin and the head's with the neck's are kept as triangles too, for
    their cross sections."""

    def __init__(self, body: bpy.types.Object):
        co = bodies.world_positions(body)
        zones = bodies.zones_of(body)
        polygons = [tuple(p.vertices) for p in body.data.polygons]
        centres = np.array([co[list(p)].mean(axis=0) for p in polygons])
        self.all = BVHTree.FromPolygons([tuple(c) for c in co], polygons)
        # The skin's vertices with their normals and the body parts of the faces around them, for
        # finding skin that comes out through the cage between its corners.
        self.co = co
        # A body is mirrored when every vertex of its skin has a mirror image; vertices may share a
        # place, as the lips of a closed mouth do.
        self.mirrored = bool(mirror_images(co)[1].max() <= SKIN_TWIN_REACH)
        normals = np.empty(len(body.data.vertices) * 3)
        body.data.vertex_normals.foreach_get("vector", normals)
        normals = normals.reshape(-1, 3) @ np.array(body.matrix_world.to_3x3()).T
        self.normals = normals / np.maximum(np.linalg.norm(normals, axis=1, keepdims=True), 1e-12)
        self.vertex_zones = np.zeros((len(co), len(bodies.ZONES)), bool)
        for polygon, zone in zip(polygons, zones):
            self.vertex_zones[list(polygon), zone] = True
        self.trees = {}
        # A region without sides also has its skin per half of the body, each with the faces that
        # reach the middle plane, for the vertices that keep to their side of it.
        self.halves = {}
        for region in REGIONS:
            wanted = {bodies.ZONES.index(z) for z in SKIN_ZONES[region]}
            for side in (("L", "R") if region in SIDED else ("",)):
                keep = [p for p, z, c in zip(polygons, zones, centres)
                        if z in wanted and (not side or side_of(c[0]) == side)]
                self.trees[(region, side)] = BVHTree.FromPolygons([tuple(c) for c in co], keep)
            if region in SIDED:
                continue
            for half in (1.0, -1.0):
                keep = [p for p, z in zip(polygons, zones) if z in wanted and (co[list(p), 0] * half).max() > -MIDDLE]
                self.halves[(region, half)] = BVHTree.FromPolygons([tuple(c) for c in co], keep)
        # The folds where a limb's skin meets the trunk's, such as the armpit and the groin: per limb
        # and side, the edges between a face of the limb and a face of the trunk.
        around: dict[tuple[int, int], list[int]] = {}
        for index, polygon in enumerate(polygons):
            for i in range(len(polygon)):
                around.setdefault(tuple(sorted((polygon[i], polygon[(i + 1) % len(polygon)]))), []).append(index)
        trunk = {bodies.ZONES.index(z) for z in REGION_ZONES["trunk"]}
        self.folds = {}
        for region in STAGES[0]:
            if region not in SIDED:
                continue
            limb = {bodies.ZONES.index(z) for z in REGION_ZONES[region]}
            for side in ("L", "R"):
                edges = [edge for edge, pair in around.items()
                         if len(pair) == 2 and {zones[pair[0]] in limb, zones[pair[1]] in limb} == {True, False}
                         and {zones[pair[0]] in trunk, zones[pair[1]] in trunk} == {True, False}
                         and side_of((co[edge[0]][0] + co[edge[1]][0]) / 2.0) == side]
                self.folds[(region, side)] = co[np.array(edges, np.int64).reshape(-1, 2)]
        hands, feet = bodies.ZONES.index("Hands"), bodies.ZONES.index("Feet")
        # A plane just above the neck's top ring may dip under it at its low side, so the head's
        # cross sections cut the neck's skin too.
        head = {bodies.ZONES.index("Head"), bodies.ZONES.index("Neck")}
        self.head = np.array([co[[p[0], p[i], p[i + 1]]] for p, z in zip(polygons, zones) if z in head
                              for i in range(1, len(p) - 1)])
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

    def thickness(self, point: Vector, normal: Vector, reach: float) -> float:
        """How far a ray that goes in from a point of the skin, against its normal, runs through
        the body before it comes out of it again, up to reach."""
        # The ray starts just inside the skin, so the faces around the point itself do not stop it.
        start = SKIN_DEPTH
        hit = self.all.ray_cast(point - normal * start, -normal, max(reach - start, 0.0))
        return reach if hit[0] is None else start + hit[3]

    def tree_of(self, key: tuple[str, str], aside: float) -> BVHTree:
        """The skin a vertex is drawn to: its region's, and for a vertex beside the middle plane
        that keeps to its side of it, its region's skin on that side."""
        return self.halves[(key[0], aside)] if aside in (1.0, -1.0) and (key[0], aside) in self.halves else self.trees[key]

    def vertices_of(self, regions) -> np.ndarray:
        """The skin vertices that a cage of these regions is kept from coming through: those of the
        body parts it is kept outside of, and of the extremities that hang from it, whose skin next
        to a wrist or an ankle may come through the ring that a later stage keeps."""
        regions = set(regions) | {end for end, limb in ATTACHED.items() if limb in regions}
        wanted = [bodies.ZONES.index(z) for region in regions for z in SKIN_ZONES[region]]
        return np.nonzero(self.vertex_zones[:, wanted].any(axis=1))[0]


def poking(triangles: np.ndarray, x: np.ndarray, skin: Skin, vertices: np.ndarray,
           clearance: float) -> list[tuple[int, int, Vector, float]]:
    """The skin vertices that come out through the cage between its corners, or come closer to it
    than the clearance from inside: per vertex, the cage triangle it meets, where, and how far that
    point of the triangle must move out. A ray goes in from the skin along its normal; where it meets
    a face of the cage from outside, before it leaves the body again, the face lies inside the skin.
    Through a thin part, such as an ear or a finger, the ray leaves the body first, and the face it
    meets then lies beyond the part's other side, not in it. Every vertex and face centre of the
    cage lies outside the skin, so only a face that cuts across a curve of the skin between its
    corners can, by little: the ray looks no farther than POKE_REACH."""
    tree = BVHTree.FromPolygons([tuple(c) for c in x], [tuple(t) for t in triangles])
    found = []
    for vertex in vertices:
        point, normal = Vector(skin.co[vertex]), Vector(skin.normals[vertex])
        location, face_normal, triangle, distance = tree.ray_cast(point, -normal, POKE_REACH)
        if location is not None and face_normal.dot(normal) > 0.0 and skin.thickness(point, normal, distance) >= distance:
            found.append((int(vertex), triangle, location, distance + clearance))
            continue
        if clearance <= 0.0:
            continue
        location, face_normal, triangle, distance = tree.ray_cast(point, normal, clearance)
        if location is not None and face_normal.dot(normal) > 0.0:
            found.append((int(vertex), triangle, location, clearance - distance))
    return found


def _lift_faces(triangles: np.ndarray, x: np.ndarray, found: list[tuple[int, int, Vector, float]],
                held: np.ndarray | None) -> np.ndarray:
    """The cage with every triangle that skin comes through moved out along its normal, all its free
    corners alike, so the point the skin meets moves out as far as it must. Where held corners carry
    most of that point, such as next to a wrist ring, the free ones would have to move far, so the
    triangle stays. A corner that several triangles move takes the largest move."""
    lift = np.zeros(len(x))
    direction = np.zeros_like(x)
    for _vertex, triangle, location, need in found:
        corners = triangles[triangle]
        weights = np.array(poly_3d_calc([Vector(x[c]) for c in corners], location))
        free = np.ones(3, bool) if held is None else ~held[corners]
        share = float(weights[free].sum())
        if share < POKE_FREE_SHARE:
            continue
        a, b, c = (x[k] for k in corners)
        normal = np.cross(b - a, c - a)
        normal /= max(np.linalg.norm(normal), 1e-12)
        step = (need + SETTLE_MARGIN) / share
        for corner in corners[free]:
            if step > lift[corner]:
                lift[corner], direction[corner] = step, normal
    return x + direction * lift[:, None]


def nearest_on_segments(point: np.ndarray, segments: np.ndarray) -> np.ndarray:
    """The point nearest to a point on any of the segments, each a pair of end points."""
    start, along = segments[:, 0], segments[:, 1] - segments[:, 0]
    share = np.einsum("ij,ij->i", point - start, along) / np.maximum(np.einsum("ij,ij->i", along, along), 1e-18)
    points = start + along * np.clip(share, 0.0, 1.0)[:, None]
    return points[np.argmin(np.linalg.norm(points - point, axis=1))]


def mirror_images(points: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """Per point, the point nearest to its mirror image across the body's middle plane, and how far
    from that image it lies."""
    points = np.asarray(points, float)
    nearest = np.empty(len(points), np.int64)
    squares = np.einsum("ij,ij->i", points, points)
    # A block of points at a time, so the table of distances stays small. The table is good enough
    # to tell which point is nearest, and the distance to that one is then measured exactly.
    for start in range(0, len(points), MIRROR_BLOCK):
        images = points[start:start + MIRROR_BLOCK] * MIRROR
        nearest[start:start + len(images)] = np.argmin(squares[None, :] - 2.0 * images @ points.T, axis=1)
    return nearest, np.linalg.norm(points * MIRROR - points[nearest], axis=1)


def twins_of(points: np.ndarray, reach: float) -> np.ndarray | None:
    """The mirror image of every point among the points, a point in the middle plane being its
    own; None when some point has none within reach, or two share one."""
    twins, distances = mirror_images(points)
    if distances.max() > reach or (twins[twins] != np.arange(len(points))).any():
        return None
    return twins


def evened(x: np.ndarray, twins: np.ndarray | None, moving: np.ndarray | None = None) -> np.ndarray:
    """The two sides of a net evened out: every moving vertex halfway between where it lies and
    the mirror image of its twin, and one that is its own twin in the middle plane. A body that is
    the mirror image of itself gets a cage that is too; what differs between the sides after a step
    comes from the order of the vertices and from points that lie as near to one face of the skin
    as to the next."""
    if twins is None:
        return x
    middle = (x + x[twins] * MIRROR) / 2.0
    if moving is None:
        return middle
    x = x.copy()
    x[moving] = middle[moving]
    return x


def middle_sides(keys: list[tuple[str, str]], reference: np.ndarray) -> np.ndarray:
    """Where each vertex of the neck and the trunk lies in the reference: 1 beside the body's
    middle plane on the body's right, -1 on its left, and 0 in the plane. A body is the mirror
    image of itself across that plane, so such a vertex keeps to its side of it, and one in the
    plane stays in it. A vertex of a limb has a side of its own and no such rule: nan."""
    x = reference[:, 0]
    aside = np.where(np.abs(x) < MIDDLE, 0.0, np.sign(x))
    return np.where([key[0] in SIDED for key in keys], np.nan, aside)


def keep_aside(x: np.ndarray, aside: np.ndarray) -> np.ndarray:
    """The vertices with those that crossed the middle plane, or left it, back in it."""
    x = x.copy()
    ruled = ~np.isnan(aside)
    x[ruled, 0] = aside[ruled] * np.maximum(aside[ruled] * x[ruled, 0], 0.0)
    return x


def away_from_middle(directions: np.ndarray, aside: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """Directions without their parts toward the middle plane, and for a vertex in the plane
    without any part across it: unit vectors, and how much of each direction is left. The two
    halves of a body face each other in its clefts, such as the crotch and the buttock cleft, so a
    vertex that moved out along its normal there would cross to the other half."""
    directions = np.array(directions, float)
    toward = (aside == 0.0) | (directions[:, 0] * aside < 0.0)
    directions[toward, 0] = 0.0
    left = np.linalg.norm(directions, axis=1)
    return directions / np.maximum(left, 1e-12)[:, None], left


def drawn(net: Net, x: np.ndarray, target, moving: np.ndarray, rounds: tuple[int, ...] = DRAW_ROUNDS) -> np.ndarray:
    """A net drawn to the skin as a whole. target gives the place on the skin each vertex is drawn
    to from where the net lies. A vertex that went there by itself would cross its neighbors where
    the net lies askew over the skin, and two stretches of the net would land on the same skin, so
    the moves are evened out between neighbors first: the net slides into place in one piece, then
    in ever smaller stretches, and in the last round every moving vertex takes its place on the
    skin. The others stay."""
    x = x.copy()
    for smoothing in rounds:
        move = np.zeros_like(x)
        move[moving] = target(x)[moving] - x[moving]
        for _ in range(smoothing):
            move[moving] = (move + 0.5 * (net.mean_of_neighbors(move) - move))[moving]
        x += move
    return x


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
        # A mirrored body's cage is fitted alike on both sides, where the reference is mirrored too.
        twins = twins_of(reference[part.vertices], TWIN_REACH) if skin.mirrored else None
        if number == 0:
            y = _fit_body(part, x[part.vertices], keys, middle_sides(keys, reference[part.vertices]), twins,
                          weights[part.vertices], names, arm, skin, figure)
        else:
            chosen = [i for i, region in enumerate(regions) if REGIONS[region] in stage]
            y = _fit_ends(part, x[part.vertices], keys, held, twins, digits[chosen], rings[part.vertices],
                          columns[part.vertices], arm, skin)
        x[part.vertices[~held]] = y[~held]
        placed[part.vertices] = True
    return x


def _fit_body(part: Part, x: np.ndarray, keys: list[tuple[str, str]], aside: np.ndarray, twins: np.ndarray | None,
              weights: np.ndarray, names: list[str], arm: bpy.types.Object, skin: Skin, figure: float) -> np.ndarray:
    """The neck, the trunk, the arms, and the legs: drawn to the skin, lifted out of the pits by the
    ball, drawn taut along their rings, faired, and kept outside the skin. The vertices of the neck
    and the trunk keep to their side of the body's middle plane all the way, and the two sides of a
    mirrored body are evened out after every step."""
    net = part.net

    def sided(y: np.ndarray) -> np.ndarray:
        return keep_aside(evened(y, twins), aside)

    x = sided(x)
    normals = net.normals(x)
    for i, key in enumerate(keys):
        tree = skin.tree_of(key, aside[i])
        nearest, _normal, _index, distance = tree.find_nearest(Vector(x[i]))
        best = None
        if distance > NEAR:
            for sign in (1.0, -1.0):
                hit = tree.ray_cast(Vector(x[i]), Vector(normals[i] * sign), REACH)
                if hit[0] is not None and hit[3] <= distance + RAY_SLACK and (best is None or hit[3] < best[3]):
                    best = hit
        x[i] = np.array(best[0] if best is not None else nearest)
    folded = _into_folds(part, x, keys, skin)
    in_fold = np.linalg.norm(folded - x, axis=1) > FOLD_HELD
    x = sided(folded)

    # The ball rolls over the skin along normals that point out of the pits.
    normals = net.smoothed_normals(x, NORMAL_SMOOTHING)
    fade = np.minimum(net.hops_from(part.borders(), LIFT_FADE) / LIFT_FADE, 1.0)
    directions = normals.copy()
    for i, key in enumerate(keys):
        if fade[i] > 0.0:
            _location, skin_normal, _d = skin.nearest(key, x[i])
            if Vector(normals[i]).dot(skin_normal) < NORMAL_AGREEMENT:
                directions[i] = skin_normal
    directions, left = away_from_middle(directions, aside)
    for i, key in enumerate(keys):
        if fade[i] <= 0.0 or left[i] <= 0.0:
            continue
        direction = Vector(directions[i])
        centre = Vector(x[i]) + direction * figure
        tree = skin.trees[key]
        for _ in range(LIFT_STEPS):
            _l, _n, _index, distance = tree.find_nearest(centre)
            if distance >= figure - 1e-4:
                break
            centre += direction * (figure - distance)
        lift = min((centre - Vector(x[i])).dot(direction) - figure, LIFT_SHARE * figure)
        x[i] = x[i] + directions[i] * max(lift, 0.0) * fade[i] * left[i]
    x = sided(x)

    # A ring stops at a border between regions by itself, so the tension fades in from the open ends only.
    ends = np.minimum(net.hops_from(np.nonzero(net.on_border)[0], LIFT_FADE) / LIFT_FADE, 1.0)
    x = sided(_tension(net, x, keys, aside, _axes(weights, keys, names, arm), ends))
    # The fairing rounds a net off, which would lift it out of a fold again, into a web between the
    # arm and the trunk: the vertices that were moved into a fold stay, and the fairing fades in
    # from them as the lift does from a border.
    faired = net.faired(x, FAIRING)
    if in_fold.any():
        hold = np.minimum(net.hops_from(np.nonzero(in_fold)[0], LIFT_FADE) / LIFT_FADE, 1.0)
        faired = x + (faired - x) * hold[:, None]
    x = sided(faired)
    return _settle(part, x, skin, aside=aside, twins=twins)


def _into_folds(part: Part, x: np.ndarray, keys: list[tuple[str, str]], skin: Skin) -> np.ndarray:
    """The net with its borders between a limb and the trunk moved into the folds where their skin
    meets, wherever it would span a hollow. A vertex of such a border lies on the limb's skin and
    its neighbors in the trunk on the trunk's; where the limb stands off the trunk, as an arm does
    at the armpit, the edges between them pass through the air of the hollow, and the faces would
    hang there like a web between the arm and the trunk. The vertex moves toward the nearest point
    of the fold by the share of FOLD_SPAN that the middle of such an edge lies outside the skin, so
    the net runs along the limb on one side of the fold and along the trunk on the other. Over a
    shoulder or a hip, where the skin does not fold, the border stays where the rig carried it."""
    roots = part.limb_roots()
    span = np.zeros(part.net.count)
    for a, b in part.net.edges:
        for root, other in ((a, b), (b, a)):
            if roots[root] and not roots[other] and keys[other][0] == "trunk":
                span[root] = max(span[root], skin.nearest(None, (x[root] + x[other]) / 2.0)[2])
    x = x.copy()
    for i in np.nonzero(span > 0.0)[0]:
        fold = skin.folds.get(keys[i], ())
        if len(fold):
            x[i] += (nearest_on_segments(x[i], fold) - x[i]) * min(span[i] / FOLD_SPAN, 1.0)
    return x


def _fit_ends(part: Part, x: np.ndarray, keys: list[tuple[str, str]], held: np.ndarray, twins: np.ndarray | None,
              digits: np.ndarray, rings: np.ndarray, columns: np.ndarray, arm: bpy.types.Object, skin: Skin) -> np.ndarray:
    """The hands, the feet, and the head, from the rings they share with the limbs and the neck,
    which stay: a hand's vertices drawn to the skin of their own digit and the palm, a foot and the
    head laid on their rings, and all faired and kept outside the skin."""
    x = x.copy()
    # A vertex whose faces all belong to one finger or the thumb is drawn to its skin; one at the
    # palm or between them to the whole hand's.
    around: dict[int, set[int]] = {}
    for face, digit in zip(part.faces, digits):
        for v in face:
            around.setdefault(v, set()).add(int(digit))
    hands = np.array([not held[i] and key[0] == "hands" for i, key in enumerate(keys)])
    trees = {}
    for i in np.nonzero(hands)[0]:
        own = around.get(i, {0})
        trees[i] = skin.trees[("hands", keys[i][1], next(iter(own)))] if len(own) == 1 and 0 not in own else skin.trees[keys[i]]

    def on_skin(y: np.ndarray) -> np.ndarray:
        found = y.copy()
        for i, tree in trees.items():
            found[i] = tree.find_nearest(Vector(y[i]))[0]
        return found

    x = drawn(part.net, x, on_skin, hands)
    # The palm and the webs between the digits differ most from hand to hand: their vertices spread
    # evenly over the skin between the digits, which lie on theirs, and the wrist ring.
    between = np.array([hands[i] and (len(around.get(i, {0})) > 1 or 0 in around.get(i, {0})) for i in range(len(x))])
    for _ in range(SPREAD_STEPS):
        x[between] = on_skin(x + 0.5 * (part.net.mean_of_neighbors(x) - x))[between]
    x = evened(x, twins, ~held)
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
    members = np.array([i for i, key in enumerate(keys) if key[0] == "head"], np.int64)
    if len(members):
        ring, column = rings[members], columns[members]
        if ring.min() < 0 or ring.max() != len(HEAD_RINGS) + 1:
            raise ArtPipelineError(f"The head of the cage was not made with the {len(HEAD_RINGS)} rings of the fitting; "
                                   "make its head region again.")
        neck = np.zeros((int(column[ring == 0].max()) + 1, 3))
        neck[column[ring == 0]] = x[members[ring == 0]]
        laid, pole = _head_rings(arm, neck, skin.head)
        for i, r, c in zip(members, ring, column):
            if not held[i]:
                x[i] = pole if r == len(HEAD_RINGS) + 1 else laid[r - 1][c]
    # The feet and the head are faired, which spreads the feet's rings where they crowd at the front
    # of the ankle; the hands lie on their skin already, and fairing would only draw in the fingertips.
    x = evened(x, twins, ~held)
    x = part.net.faired(x, FAIRING, held | np.array([key[0] not in ("feet", "head") for key in keys]))
    return _settle(part, evened(x, twins, ~held), skin, held, SETTLE_PASSES * ENDS_SETTLE_FACTOR, twins=twins)


def hull(points: np.ndarray) -> np.ndarray:
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


def centroid(polygon: np.ndarray) -> np.ndarray:
    """The centre of a polygon's area."""
    x, y = polygon[:, 0], polygon[:, 1]
    xn, yn = np.roll(x, -1), np.roll(y, -1)
    cross = x * yn - xn * y
    return np.array((((x + xn) * cross).sum(), ((y + yn) * cross).sum())) / (3.0 * cross.sum())


def toward(polygon: np.ndarray, centre: np.ndarray, angle: float) -> tuple[int, np.ndarray]:
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
        raise ArtPipelineError("A ring of a foot or the head of the cage found no outline around its centre.")
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


def _plane_section(triangles: np.ndarray, origin: np.ndarray, first: np.ndarray, second: np.ndarray) -> np.ndarray:
    """Where a plane cuts the triangles, in the plane's coordinates along its axes first and second."""
    normal = np.cross(first, second)
    signed = (triangles - origin) @ normal
    found = []
    for i, j in ((0, 1), (1, 2), (2, 0)):
        crossing = (signed[:, i] < 0.0) != (signed[:, j] < 0.0)
        a, b = triangles[crossing, i], triangles[crossing, j]
        t = signed[crossing, i] / (signed[crossing, i] - signed[crossing, j])
        found.append(a + (b - a) * t[:, None])
    points = np.concatenate(found) - origin
    return np.stack([points @ first, points @ second], axis=1)


def _section(triangles: np.ndarray, origin: np.ndarray, across: np.ndarray, down: np.ndarray) -> np.ndarray:
    """Where a half-plane cuts the triangles, in the half-plane's coordinates: along its axis across,
    and away from the axis along down."""
    flat = _plane_section(triangles, origin, across, down)
    return flat[flat[:, 1] > 0.0]


def _ring_along(convex: np.ndarray, first: float, shares: np.ndarray, turning: float) -> tuple[np.ndarray, np.ndarray]:
    """The points of a ring on a convex outline: the first where the ray from the outline's centre
    at the angle first meets it, and the others around it in the direction of turning at the shares
    of the outline. Returns them and the outline's centre."""
    middle = centroid(convex)
    edge, point = toward(convex, middle, first)
    outline = np.concatenate([[point], convex[edge + 1:], convex[:edge + 1]])
    # The hulls run counterclockwise; a ring that runs the other way walks them backward.
    if turning < 0.0:
        outline = np.concatenate([outline[:1], outline[1:][::-1]])
    return _along(outline, shares), middle


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
    middle = centroid(start)
    first = math.atan2(start[0, 1] - middle[1], start[0, 0] - middle[0])
    shares = _shares(start)
    turning = _turning(start)
    tip = float(((triangles.reshape(-1, 3) - pivot) @ forward).max())
    rings = []
    for kind, share in FOOT_RINGS:
        turn = full * share if kind == "turn" else full
        down = back * math.cos(turn) + normal * math.sin(turn)
        origin = pivot if kind == "turn" else pivot + forward * tip * share
        convex = hull(_section(triangles, origin, across, down))
        if len(convex) < 3:
            raise ArtPipelineError(f"A ring of the foot on the {side} side cuts too little of its skin.")
        flat, middle = _ring_along(convex, first, shares, turning)
        rings.append([origin + across * p[0] + down * p[1] for p in flat])
    pole = pivot + forward * tip + across * middle[0] + down * middle[1]
    return np.array(rings), pole


def _turning(polygon: np.ndarray) -> float:
    """1 for a polygon that runs counterclockwise, -1 for one that runs clockwise."""
    return float(np.sign(np.sum(polygon[:, 0] * np.roll(polygon[:, 1], -1) - np.roll(polygon[:, 0], -1) * polygon[:, 1])))


def _head_rings(arm: bpy.types.Object, neck: np.ndarray, triangles: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """The head's rings on a body, one row of points per ring of HEAD_RINGS, and the point that
    closes the last ring at the crown. The planes stand at the rings' shares of the way from the
    centre of the neck's top ring to the top of the head along the head's bone; over the first
    HEAD_TURN rings they turn from the plane of the neck's ring until they lie across the bone. Each
    ring follows the convex hull of its cross section of the head's and the neck's skin, which spans
    the ears, the nose, the lips, and the eye sockets: its first point lies where the ray from the
    hull's centre at the angle of the neck ring's first point meets the hull, and the others follow
    around the hull at the shares of the outline that the neck ring's points lie at."""
    if not len(triangles):
        raise ArtPipelineError("The body has no skin of the head.")
    world = arm.matrix_world
    bone = arm.data.bones["Head"]
    up = np.array((world @ bone.tail_local) - (world @ bone.head_local))
    up /= np.linalg.norm(up)
    centre = neck.mean(axis=0)
    normal = np.linalg.svd(neck - centre)[2][2]
    if normal @ up < 0.0:
        normal = -normal

    def frame(n: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
        # The body's right across the plane, and its front.
        right = np.array((1.0, 0.0, 0.0)) - n[0] * n
        right /= np.linalg.norm(right)
        return right, np.cross(n, right)

    right, front = frame(normal)
    start = np.stack([(neck - centre) @ right, (neck - centre) @ front], axis=1)
    middle = centroid(start)
    first = math.atan2(start[0, 1] - middle[1], start[0, 0] - middle[0])
    shares = _shares(start)
    turning = _turning(start)
    tip = float(((triangles.reshape(-1, 3) - centre) @ up).max())
    rings = []
    for index, share in enumerate(HEAD_RINGS):
        blend = min((index + 1) / HEAD_TURN, 1.0)
        plane = normal * (1.0 - blend) + up * blend
        right, front = frame(plane / np.linalg.norm(plane))
        origin = centre + up * tip * share
        convex = hull(_plane_section(triangles, origin, right, front))
        if len(convex) < 3:
            raise ArtPipelineError("A ring of the head cuts too little of its skin.")
        flat, middle = _ring_along(convex, first, shares, turning)
        rings.append([origin + right * p[0] + front * p[1] for p in flat])
    pole = centre + up * tip + right * middle[0] + front * middle[1]
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


def _tension(net: Net, x: np.ndarray, keys: list[tuple[str, str]], aside: np.ndarray, axes: np.ndarray,
             fade: np.ndarray) -> np.ndarray:
    """The rings drawn taut over the cage, outward only, each vertex along the part of its smoothed
    normal across its ring's axis and not toward the body's middle plane. A band pulls in its own
    plane, so a vertex at the bottom of the buttock cleft, whose normal points down, still moves
    back across the cleft, and one whose normal points along the axis, such as the top of a
    shoulder, hardly moves."""
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
    normals, size = away_from_middle(normals - np.einsum("ij,ij->i", normals, axes)[:, None] * axes, aside)
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


def _settle(part: Part, x: np.ndarray, skin: Skin, held: np.ndarray | None = None, passes: int = SETTLE_PASSES,
            aside: np.ndarray | None = None, twins: np.ndarray | None = None) -> np.ndarray:
    """The part kept outside the skin. Every vertex that lies inside the skin or closer than the
    clearance moves out along the skin's normal, and so do the corners of every face whose centre
    does and of every edge of an open end whose middle or quarter points do: an edge of an open
    end, such as an ankle ring, cuts across the curve of the skin like a face does, and skin that
    came out past it would come out through the faces a later stage hangs from it. Every face that
    the skin of the part's body parts comes out through between its corners moves out along its
    own normal. This is done a few times over, because moving one face moves the corners it
    shares. In each pass all moves are found first and every vertex takes the largest one asked of
    it, so the result does not depend on the order of the vertices. With twins, a vertex also takes
    its twin's move, mirrored, when that is the larger one: skin in the middle plane comes out
    between two faces, and the ray that finds it names one of them. Held vertices stay where they
    are, and the vertices of the neck and the trunk keep to their side of the middle plane."""
    net = part.net
    x = x.copy()
    aside = np.full(net.count, np.nan) if aside is None else aside
    # The points kept outside the skin, each by its corners and their shares of it.
    points = [((i,), (1.0,)) for i in range(net.count)] + [(face, (1.0 / len(face),) * len(face)) for face in net.faces]
    points += [((int(a), int(b)), (share, 1.0 - share)) for a, b in net.border_edges for share in END_EDGE_SHARES]
    vertices = skin.vertices_of({REGIONS[r] for r in part.regions})
    for _ in range(passes):
        before = x.copy()
        size = np.zeros(net.count)
        move = np.zeros_like(x)
        for corners, shares in points:
            _location, normal, distance = skin.nearest(None, np.array(shares) @ x[list(corners)])
            if distance >= CLEARANCE:
                continue
            step = CLEARANCE + SETTLE_MARGIN - distance
            for corner in corners:
                if (held is None or not held[corner]) and step > size[corner]:
                    size[corner], move[corner] = step, np.array(normal) * step
        settled = not size.any()
        x += move
        found = poking(net.triangles, x, skin, vertices, CLEARANCE)
        if found:
            lifted = _lift_faces(net.triangles, x, found, held)
            settled = settled and np.array_equal(lifted, x)
            x = lifted
        if twins is not None:
            moved = x - before
            mirrored = moved[twins] * MIRROR
            larger = np.linalg.norm(mirrored, axis=1) > np.linalg.norm(moved, axis=1)
            moved[larger] = mirrored[larger]
            # A vertex in the middle plane is its own twin and keeps the part of its move in the plane.
            x = evened(before + moved, twins, None if held is None else ~held)
        x = keep_aside(x, aside)
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


def _add_head(label: str, figure: float) -> None:
    """The head region: the rings of the head from the cage's ring at the top of the neck to the
    crown, laid on the body as the fitting lays them, each ring a quad strip and the last one closed
    by a fan at the crown. The rings start at the middle of the nape."""
    cage, rig = _reference()
    arm, body = bpy.data.objects[label], bodies.source_body(label)
    x = fit(label, figure)
    faces = [tuple(p.vertices) for p in cage.data.polygons]
    stage = Part(faces, _face_regions(cage.data), STAGES[0])
    keys = stage.keys(bodies.world_positions(cage)[stage.vertices])
    loops = [loop for loop in stage.net.loops() if all(keys[v] == ("neck", "") for v in loop)]
    if len(loops) != 1:
        raise ArtPipelineError(f"The cage has {len(loops)} open ends of the neck, not one ring at its top.")
    ring = [int(stage.vertices[v]) for v in loops[0]]
    # The new faces run along the ring against the neck's faces there.
    neck = next(f for f in faces if ring[0] in f and ring[1] in f)
    if neck[(neck.index(ring[0]) + 1) % len(neck)] == ring[1]:
        ring.reverse()
    back = min(range(len(ring)), key=lambda i: (round(float(x[ring[i]][1]), 4), abs(float(x[ring[i]][0]))))
    ring = ring[back:] + ring[:back]
    laid, pole = _head_rings(arm, x[ring], Skin(body).head)
    count = len(x)
    points, rows_of_head, sweep = [], [ring], {}
    for index, row in enumerate(laid):
        numbers = list(range(count + len(points), count + len(points) + len(row)))
        points.extend(row)
        rows_of_head.append(numbers)
        for column, vertex in enumerate(numbers):
            sweep[vertex] = (index + 1, column)
    crown = count + len(points)
    points.append(pole)
    sweep[crown] = (len(HEAD_RINGS) + 1, 0)
    for column, vertex in enumerate(ring):
        sweep[vertex] = (0, column)
    size = len(ring)
    new_faces = []
    for upper, lower in zip(rows_of_head, rows_of_head[1:]):
        for j in range(size):
            new_faces.append((upper[j], upper[(j + 1) % size], lower[(j + 1) % size], lower[j]))
    last = rows_of_head[-1]
    for j in range(size):
        new_faces.append((last[j], last[(j + 1) % size], crown))
    bones = [b.name for b in arm.data.bones if b.use_deform]
    weights = bodies.weights_of(body, bones)
    zones = bodies.zones_of(body)
    head = bodies.ZONES.index("Head")
    polygons = [tuple(p.vertices) for p in body.data.polygons if zones[p.index] == head]
    rows = _skin_rows(body, polygons, weights, points)
    moved = _transfer(np.array(points), np.array(rows), bones, arm, rig)
    _append(list(moved), rows, bones, new_faces, "head", sweep=sweep)


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
        elif region == "head":
            _add_head(label, figure)
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
        return [f"The bodies {stale} of the cage changed in their own files; run tools/art/Sync-CageAsset.ps1."]
    missing = missing_regions()
    if missing:
        return [f"The cage lacks the regions {missing}; run tools/art/Sync-CageAsset.ps1, which adds them."]
    cage, _rig = _reference()
    recorded = json.dumps(lines, sort_keys=True, separators=(",", ":"))
    if cage.get("figure") != float(figure) or cage.get("lines") != recorded:
        return ["The cage was fitted with another figure scale or other lines than its asset.json names; run "
                "tools/art/Sync-CageAsset.ps1."]
    errors = []
    for _path, label in body_list:
        version = bpy.data.objects.get(version_name(label))
        if version is None:
            errors.append(f'The cage has no version "{version_name(label)}"; run tools/art/Sync-CageAsset.ps1.')
            continue
        fitted = _attribute(version.data, FITTED)
        fresh = fit(label, figure)
        if fitted is None or fitted.shape != fresh.shape or np.abs(fitted - fresh).max() > FIT_TOLERANCE:
            errors.append(f'"{version.name}" is not fitted to its body as the fitting gives it now; run '
                          "tools/art/Sync-CageAsset.ps1.")
    return errors


def line_planes(label: str, line: dict) -> list[tuple[str, Vector, Vector]]:
    """A standard line on a body: per side its plane, a point and a normal, at the line's share of
    its bone's length, at right angles to the bone, or level across the trunk, which stands upright
    at rest while its bones lean. A line with a rise curves away from its plane; line_offsets gives
    how far points lie beyond it."""
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


def line_offsets(line: dict, point: Vector, normal: Vector, co: np.ndarray) -> np.ndarray:
    """How far points lie beyond a standard line's surface along its normal, toward its bone's tail:
    from its plane, and for a line with a rise from the plane raised toward the sides by the rise
    times the square of the distance to the side, so a neckline runs low at the throat, high at the
    sides of the neck where the shoulders meet it, and between at the nape, round all the way."""
    offsets = (np.asarray(co, float) - np.array(point)) @ np.array(normal)
    rise = float(line.get("rise", 0.0))
    if rise:
        offsets = offsets - rise * (np.asarray(co, float)[..., 0] - point.x) ** 2
    return offsets


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
        signed = line_offsets(line, point, normal, co)
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
    no face centre inside the skin, no skin that comes out through a face, no face that crosses
    another, and every line crossing its region on every body."""
    errors = []
    try:
        cage, _rig = _reference()
    except ArtPipelineError as error:
        return [str(error)]
    faces = [tuple(p.vertices) for p in cage.data.polygons]
    regions = _face_regions(cage.data)
    for label in labels:
        version = bpy.data.objects.get(version_name(label))
        if version is None or version.type != "MESH":
            errors.append(f'The cage has no version "{version_name(label)}"; run tools/art/Sync-CageAsset.ps1.')
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
                          f"{len(inside)} vertices and face centres; run tools/art/Sync-CageAsset.ps1, or correct it outward.")
        # The fitting keeps the skin the clearance inside the cage where it can; the skin next to a
        # ring that an earlier stage keeps may come closer, but never out.
        through = 0
        for stage in STAGES:
            part = Part(faces, regions, stage)
            if len(part.vertices):
                through += len(poking(part.net.triangles, co[part.vertices], skin,
                                      skin.vertices_of({REGIONS[r] for r in part.regions}), 0.0))
        if through:
            errors.append(f'The skin comes out through "{version.name}" between its corners at {through} vertices; '
                          "run tools/art/Sync-CageAsset.ps1, or correct it outward.")
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
    data = manifest_io.read_json(path)
    figure = float(data["figure"])
    lines = [{"name": str(line["name"]), "region": str(line["region"]), "bone": str(line["bone"]),
              "share": float(line["share"]), **({"rise": float(line["rise"])} if "rise" in line else {})}
             for line in data["lines"]]
    if not math.isfinite(figure) or figure <= 0.0:
        raise ArtPipelineError("The figure scale of a cage must be a positive number of meters.")
    return figure, lines
