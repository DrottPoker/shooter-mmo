"""Game views: a dressed body drawn as the game draws it, with the pixels counted that must not show.
See docs/ART_PIPELINE.md, Game Views.

The geometry, the skeleton, the clips, the images, and the paint come from the current .blend files:
the body from its asset, each garment's version for the body appended from its template's file as the
pair check appends it, with its texture on in its natural dyes, and the paint layers on the skin as the
paint sheet lays them. What the game leaves out comes from data in the format the export writes, one
file per garment and body: the template's, or a masked texture's own, as its last export wrote it,
or from another folder the caller names.

The selection of what is drawn mirrors the client's PlayerBodyMeshPlan rule for rule, over plain data,
so the tests run it without Blender; the C# names are in the comments:

- PlayerBodyMeshes.Drawn: a worn garment is left out when a worn garment of a higher layer hides its
  slot, and hair when any worn headwear hides the hair; the others are drawn in the order of their
  layer, then their name, the texture's.
- PlayerBodyMeshes.ShapeOf: hair takes its shape key UnderHeadwear in full when a drawn garment presses
  the hair flat.
- Plan.Compute: a body face is covered at the highest layer of the drawn garments that cover it, and a
  body vertex is pulled in by the largest distance of the drawn garments. The client pulls along the
  normals of its body parts, the body's corner normals; the views pull along Blender's vertex normals,
  as the checks do, which differs by less than the 3 mm of the largest pull-in at a sharp edge.
- Plan.AddTriangles and Plan.FindHiding: a body triangle is left out when a drawn garment covers its
  face, and a garment triangle when a drawn garment of a higher layer covers the body face under it or
  a drawn garment of a higher layer hides it among the faces of its template.
- Every face is split from its first corner, as Unity's import splits the exported faces.
- BodyAppearanceCatalog.ShownPaints: of each category and slot the first paint layer the look names,
  the body's first underwear of a slot when the look names none, and no underwear of a slot whose
  drawn garment hides it; at most PlayerBodyMaterials.MaximumPaintLayers on a material.

A body that is not the rig its clip library is keyed on takes the clips as the game retargets them,
through the reference poses of the two avatars, by retarget_pose.py; the rig itself and the rest pose
play as they are. Every view's numbers hold where the pose put each bone's head, which the capture
from the game holds against the game's.

The views count three classes of pixels, decided per drawn triangle in the pose of the view: skin over
which the cloth of a drawn garment lies, where the cloth should cover it; a garment over which the
cloth of a garment drawn after it lies; and contact, either of them where that garment's cloth did not
cover it at rest, so motion brought it into the cloth, such as a forearm pressed into the shirt over
the chest."""

from __future__ import annotations

import json
import math
import os
from dataclasses import dataclass, field

import numpy as np

# The version of the format of the views' keys and numbers, which the committed key of every view
# holds and art/Views/baseline.json names: raise it, and $script:ArtViewVersion in
# tools/art/ArtViews.ps1, when either format changes. A change of the renderer's code changes every key
# by itself, through the fingerprint of the code that the key holds.
VIEW_VERSION = 4

# BodyAppearanceCatalog.Garment: the hair's slot, the headwear's hair forms, and the shape key the hair
# takes under headwear that presses it flat; AppearanceRules: the paint category a body always shows,
# one per slot, compared without case as the import parses it; and PlayerBodyMaterials: the paint
# layers the skin shader draws on one material.
HAIR_SLOT = "hair"
HIDE_HAIR = "hide"
COMPRESS_HAIR = "compress"
UNDER_HEADWEAR = "UnderHeadwear"
UNDERWEAR = "underwear"
REQUIRED_CATEGORIES = (UNDERWEAR,)
MAX_PAINT_LAYERS = 4

# A drawn triangle lies over cloth when a ray from one of its samples, into the body or under the
# garment, meets opaque cloth within this reach before the body's surface. The samples are its centre
# and its corners pulled this share of the way toward the centre.
THROUGH_REACH = 0.03
SAMPLE_PULL = 0.2
# What lies_under finds for a triangle: no cloth, cloth that covered it at rest too, or only cloth that
# did not, contact.
NONE, THROUGH, CONTACT = 0, 1, 2
# A triangle whose rays passed a hole of cloth is followed again at points this far apart, the spacing at
# which the pair check follows a triangle.
DENSE_SPACING = 0.003
# A ray starts this far inside its triangle, and goes on this far past a transparent place of cloth.
RAY_START = 1e-5
RAY_STEP = 1e-5
# Transparent places a ray passes before it counts as missing the cloth, and the alpha the game's
# garment shader clips at.
MAX_LAYERS = 4
ALPHA_CUT = 0.5

# The pictures: their size, the game camera's vertical field of view and its distance range, the share
# of the frame left free around a whole figure, and a close view's distance, the game's least.
WIDTH, HEIGHT = 512, 768
FIELD_OF_VIEW = 55.0
NEAREST, FARTHEST = 0.6, 15.0
FRAME_MARGIN = 0.06
CLOSE_DISTANCE = 0.6
# What a close view looks at, by the posed bones of the skeleton: the bones and how far along them. The
# feet are framed whole instead, with what is drawn up to this height above their bones' heads.
CLOSE_TARGETS = {"head": (("Head",), 0.4), "waist": (("Hips",), 0.0)}
FEET = ("Foot_L", "Foot_R")
FEET_ABOVE = 0.15

# Development World 2's light: a white sun of intensity 2, 50 degrees above the horizon, which Blender
# gives as pi times the intensity, and the ambient light of its sky, here a mid grey that is also the
# plain background. The sun shines from 30 degrees beside the camera, so every view is lit alike.
SUN_INTENSITY = 2.0
SUN_ELEVATION = 50.0
SUN_SIDE = 30.0
GREY_LINEAR = 0.2140
LOOK_SAMPLES = 16
# The classes' colours, exact in the PNG, by the class of a drawn triangle: everything else, skin
# through, a garment through, and contact.
OTHER = (0.0, 0.0, 0.0, 1.0)
SKIN_THROUGH = (1.0, 0.0, 0.0, 1.0)
INNER_THROUGH = (0.0, 1.0, 0.0, 1.0)
CONTACT_COLOUR = (0.0, 0.0, 1.0, 1.0)
CLASS_COLOURS = (OTHER, SKIN_THROUGH, INNER_THROUGH, CONTACT_COLOUR)
OTHER_CLASS, SKIN_CLASS, INNER_CLASS, CONTACT_CLASS = 0, 1, 2, 3
CLASS_ATTRIBUTE = "view_class"


@dataclass
class Worn:
    """A garment a body wears as the client's catalog holds it (BodyAppearanceCatalog.Garment): named
    after its texture, with its template's slot, layer, the slots it hides, whether it hides the
    underwear of its slot, and its hair form, and the data for the body: the body faces it covers, the
    body face under each of its faces, the body vertices it pulls in, and per template of a lower layer
    the faces of it that it hides."""

    name: str
    template: str
    slot: str
    layer: int
    hides: tuple[str, ...] = ()
    hides_underwear: bool = False
    hair: str | None = None
    covered: tuple[int, ...] = ()
    underlay: tuple[int, ...] = ()
    pull_vertices: tuple[int, ...] = ()
    pull_distances: tuple[float, ...] = ()
    hidden: dict[str, tuple[int, ...]] = field(default_factory=dict)


def hidden_faces(data: dict) -> dict[str, tuple[int, ...]]:
    """The faces a garment's data hides of each template of a lower layer, by template, from its
    innerTemplates, innerHiddenCounts, and innerHiddenFaces, one template after the other."""
    found, start = {}, 0
    faces = [int(f) for f in data.get("innerHiddenFaces", [])]
    for template, count in zip(data.get("innerTemplates", []), data.get("innerHiddenCounts", [])):
        found[template] = tuple(faces[start:start + int(count)])
        start += int(count)
    if start != len(faces):
        raise ValueError("The data names other hidden faces than its counts say.")
    return found


def worn_from(texture: str, template: str, fields: dict, data: dict) -> Worn:
    """A worn garment from its template's fields of asset.json and its data for the body."""
    return Worn(texture, template, str(fields["slot"]), int(fields["layer"]), tuple(fields.get("hides") or ()),
                bool(fields.get("hidesUnderwear") or False), fields.get("hair"),
                tuple(int(f) for f in data.get("covered", [])), tuple(int(f) for f in data.get("underlay", [])),
                tuple(int(v) for v in data.get("pullInVertices", [])),
                tuple(float(d) for d in data.get("pullInDistances", [])), hidden_faces(data))


def drawn(garments: list[Worn]) -> list[Worn]:
    """PlayerBodyMeshes.Drawn: the garments a body draws, from the lowest layer up, then by name, without
    those of a slot that a worn garment of a higher layer hides, such as trousers under a floor-length
    robe, and without the hair under headwear that hides it. Every worn garment hides, also one that is
    not drawn itself."""
    found = []
    for garment in garments:
        if garment is None or not garment.name:
            continue
        hidden = any(other is not None and other is not garment
                     and ((other.layer > garment.layer and garment.slot in other.hides)
                          or (garment.slot == HAIR_SLOT and other.hair == HIDE_HAIR))
                     for other in garments)
        if not hidden:
            found.append(garment)
    # string.CompareOrdinal compares code units, as Python compares the names.
    return sorted(found, key=lambda g: (g.layer, g.name))


def shape_of(garment: Worn, drawn_garments: list[Worn]) -> str | None:
    """PlayerBodyMeshes.ShapeOf: the shape key a drawn garment takes in full, UnderHeadwear for hair
    under headwear that presses it flat, or None."""
    if garment.slot != HAIR_SLOT:
        return None
    return UNDER_HEADWEAR if any(other is not None and other.hair == COMPRESS_HAIR for other in drawn_garments) else None


def cover_layers(drawn_garments: list[Worn]) -> dict[int, int]:
    """Plan.Compute: every body face a drawn garment covers, with the highest layer that covers it."""
    found: dict[int, int] = {}
    for garment in drawn_garments:
        for face in garment.covered:
            found[face] = max(found[face], garment.layer) if face in found else garment.layer
    return found


def pull_in(drawn_garments: list[Worn]) -> dict[int, float]:
    """Plan.Compute: every body vertex a drawn garment pulls in, with the largest distance."""
    found: dict[int, float] = {}
    for garment in drawn_garments:
        for vertex, distance in zip(garment.pull_vertices, garment.pull_distances):
            found[vertex] = max(found[vertex], distance) if vertex in found else distance
    return found


def kept_body_faces(face_count: int, covers: dict[int, int]) -> np.ndarray:
    """Plan.AddTriangles for a body part: a body face is drawn unless a drawn garment covers it."""
    kept = np.ones(face_count, bool)
    kept[[face for face in covers if 0 <= face < face_count]] = False
    return kept


def kept_garment_faces(garment: Worn, drawn_garments: list[Worn], covers: dict[int, int], face_count: int) -> np.ndarray:
    """Plan.AddTriangles and Plan.FindHiding for a garment: a face is drawn unless a drawn garment of a
    higher layer covers the body face under it, or a drawn garment of a higher layer hides it among the
    faces of its template."""
    kept = np.ones(face_count, bool)
    for face in range(min(face_count, len(garment.underlay))):
        if covers.get(garment.underlay[face], garment.layer) > garment.layer:
            kept[face] = False
    for outer in drawn_garments:
        if outer is None or outer.layer <= garment.layer:
            continue
        kept[[f for f in outer.hidden.get(garment.template, ()) if 0 <= f < face_count]] = False
    return kept


def _category(paint: dict) -> str:
    return str(paint["category"]).lower()


def shown_paints(catalog: list[dict], named, drawn_garments: list[Worn]) -> list[dict]:
    """BodyAppearanceCatalog.ShownPaints over the paint layers that fit the body, each with its name,
    category, slot, and layer, in the catalog's order, the layer, then the name: of each category and
    slot the first the look names, then the body's first of a required category, the underwear, of a
    slot the look names none of, since underwear is always worn, and no underwear of a slot whose drawn
    garment hides it. The categories are compared without case."""
    ordered = sorted(catalog, key=lambda p: (int(p["layer"]), p["name"]))
    named = set(named)
    taken, shown = set(), set()
    for by_name in (True, False):
        for paint in ordered:
            key = (_category(paint), paint.get("slot") or "")
            if key in taken:
                continue
            if (paint["name"] in named) if by_name else (_category(paint) in REQUIRED_CATEGORIES):
                taken.add(key)
                shown.add(paint["name"])
    return [paint for paint in ordered if paint["name"] in shown and not (
        _category(paint) == UNDERWEAR and any(g.hides_underwear and g.slot == (paint.get("slot") or "") for g in drawn_garments))]


def paints_over_limit(paints: list[dict]) -> dict[str, int]:
    """The parts, each a material of the body, that would show more paint layers than the skin shader
    draws, with how many."""
    counts: dict[str, int] = {}
    for paint in paints:
        counts[paint["part"]] = counts.get(paint["part"], 0) + 1
    return {part: count for part, count in sorted(counts.items()) if count > MAX_PAINT_LAYERS}


def shape_problem(mesh: str, shape: str | None, keys) -> str | None:
    """PlayerBodyMeshPlan: a mesh that takes a shape key in full must have it, or the body cannot be
    drawn; None when it has it or takes none."""
    if shape is None or shape in (keys or ()):
        return None
    return f"{mesh} has no shape key {shape}, which it takes under headwear that presses the hair flat."


def garment_triangles(kept, turned, triangles) -> tuple[np.ndarray, np.ndarray]:
    """The triangles of a drawn garment, by its triangles' vertices and which vertices are the rims and
    flaps of its turned edges: those that can be marked, every drawn one, also of its turned edges, and
    its cloth for the rays of the others, the drawn ones without the turned edges, whose rims and flaps
    lie inside the cloth by design."""
    kept = np.asarray(kept, bool)
    inside = np.asarray(turned, bool)[np.asarray(triangles, np.int64).reshape(-1, 3)].any(axis=1)
    return kept.copy(), kept & ~inside


def fan(polygons: list[tuple[int, ...]]) -> list[tuple[int, int, int, int]]:
    """Every face as the game splits it, a fan from its first corner, so a quad from its first corner to
    its third: (face, a, b, c), in the order of the faces."""
    found = []
    for face, polygon in enumerate(polygons):
        for i in range(1, len(polygon) - 1):
            found.append((face, polygon[0], polygon[i], polygon[i + 1]))
    return found


def triangle_samples(corners) -> np.ndarray:
    """The samples of each triangle, given as (T, 3, 3) corners: its centre, then each corner pulled
    SAMPLE_PULL of the way toward the centre, as (T, 4, 3)."""
    corners = np.asarray(corners, float).reshape(-1, 3, 3)
    centre = corners.mean(axis=1, keepdims=True)
    return np.concatenate([centre, corners + (centre - corners) * SAMPLE_PULL], axis=1)


def dense_samples(corners, spacing: float = DENSE_SPACING) -> np.ndarray:
    """Points of one triangle about spacing apart, over the triangle with its corners pulled SAMPLE_PULL
    of the way toward its centre, as (N, 3)."""
    corners = np.asarray(corners, float).reshape(3, 3)
    centre = corners.mean(axis=0)
    inside = corners + (centre - corners) * SAMPLE_PULL
    longest = max(float(np.linalg.norm(inside[i] - inside[i - 1])) for i in range(3))
    steps = max(1, math.ceil(longest / spacing))
    grid = np.array([(i, j, steps - i - j) for i in range(steps + 1) for j in range(steps + 1 - i)], float) / steps
    return grid @ inside


def triangle_normals(corners) -> np.ndarray:
    """The unit normal of each triangle by its winding, the outward side, or zero for a triangle
    without area."""
    corners = np.asarray(corners, float).reshape(-1, 3, 3)
    normal = np.cross(corners[:, 1] - corners[:, 0], corners[:, 2] - corners[:, 0])
    length = np.linalg.norm(normal, axis=1, keepdims=True)
    return np.where(length > 1e-18, normal / np.maximum(length, 1e-30), 0.0)


def _hit(tree, origin, direction, distance: float):
    """A ray cast as mathutils.bvhtree.BVHTree.ray_cast casts it, with plain numbers in and out: the
    location as an array, the normal of the triangle met by its winding, the triangle, and the distance,
    or None."""
    if distance <= 0.0:
        return None
    location, normal, index, travelled = tree.ray_cast(tuple(float(x) for x in origin), tuple(float(x) for x in direction),
                                                      float(distance))
    if location is None:
        return None
    return (np.array([location[0], location[1], location[2]], float), np.array([normal[0], normal[1], normal[2]], float),
            int(index), float(travelled))


def _ray(point: np.ndarray, direction: np.ndarray, cloth, body, reach: float) -> tuple[int | None, bool]:
    """What one ray meets: the triangle of opaque cloth it meets on its outer side within the reach
    before the body's surface, or None, and whether it passed a transparent place of cloth on its outer
    side on the way."""
    start = point + direction * RAY_START
    met = _hit(body, start, direction, reach) if body is not None else None
    limit = met[3] if met is not None else reach
    travelled, passed = 0.0, False
    for _ in range(MAX_LAYERS):
        met = _hit(cloth, start, direction, limit - travelled)
        if met is None:
            break
        location, normal, triangle, distance = met
        if float(normal @ direction) < 0.0:
            if cloth.opaque(triangle, location):
                return triangle, passed
            passed = True
        travelled += distance + RAY_STEP
        start = location + direction * RAY_STEP
    return None, passed


def _rays(points, direction: np.ndarray, cloth, body, reach: float, owned) -> tuple[int, bool]:
    """What the rays from the points find: THROUGH at the first that meets cloth that covered the
    triangle at rest, by owned(cloth triangle), else CONTACT when one met cloth that did not, else NONE;
    and whether a ray passed a transparent place of cloth."""
    found, passed = NONE, False
    for point in points:
        triangle, through_hole = _ray(point, direction, cloth, body, reach)
        passed = passed or through_hole
        if triangle is None:
            continue
        if owned is not None and not owned(triangle):
            found = CONTACT
            continue
        return THROUGH, passed
    return found, passed


def lies_under(corners, cloth, body, reach: float = THROUGH_REACH, owned=None) -> np.ndarray:
    """What cloth lies under each triangle, given as (T, 3, 3) corners: THROUGH when a ray into the body
    or under the garment, against the triangle's outward side, from one of its samples meets opaque
    cloth on its outer side within the reach before it meets the body's surface, cloth whose garment
    covered the triangle at rest, by owned(index, cloth triangle); CONTACT when every such cloth is of a
    garment that did not, so motion brought the triangle into it; else NONE. Without owned nothing is
    contact. cloth casts rays as a BVHTree does and says with opaque(triangle, location) whether its
    texture is opaque there; body casts rays as a BVHTree does. A ray passes up to MAX_LAYERS places of
    cloth that are transparent or that it meets from their inner side, such as the lining of a hood's
    brim behind the hair in its opening, which lies there by design, as the pair check passes them. A
    triangle whose rays passed a transparent place of cloth on its outer side, such as a hole of a net,
    and found no cloth that covered it at rest is followed again at points DENSE_SPACING apart, so thin
    opaque strands under it are found. A ray starts RAY_START inside its triangle, so the triangle
    itself is not met."""
    corners = np.asarray(corners, float).reshape(-1, 3, 3)
    directions = -triangle_normals(corners)
    found = np.zeros(len(corners), np.int8)
    for index, (points, direction) in enumerate(zip(triangle_samples(corners), directions)):
        if not np.any(direction):
            continue
        own = None if owned is None else (lambda triangle, index=index: owned(index, triangle))
        result, passed = _rays(points, direction, cloth, body, reach, own)
        if result != THROUGH and passed:
            dense, _passed = _rays(dense_samples(corners[index]), direction, cloth, body, reach, own)
            result = THROUGH if dense == THROUGH else (CONTACT if CONTACT in (result, dense) else NONE)
        found[index] = result
    return found


def cloth_met(corners, cloth, body, reach: float = THROUGH_REACH) -> list[set[int]]:
    """For each triangle, given as (T, 3, 3) corners, every cloth triangle that the rays of the class
    rule meet first where it is opaque, on its outer side, within the reach before the body's surface:
    from all its samples, and from the points DENSE_SPACING apart when a ray passed a transparent place,
    so it names every cloth the rule can find under the triangle in the same place."""
    corners = np.asarray(corners, float).reshape(-1, 3, 3)
    found = []
    for index, (points, direction) in enumerate(zip(triangle_samples(corners), -triangle_normals(corners))):
        met: set[int] = set()
        if np.any(direction):
            passed = False
            for point in points:
                triangle, through_hole = _ray(point, direction, cloth, body, reach)
                passed = passed or through_hole
                if triangle is not None:
                    met.add(int(triangle))
            if passed:
                for point in dense_samples(corners[index]):
                    triangle, _passed = _ray(point, direction, cloth, body, reach)
                    if triangle is not None:
                        met.add(int(triangle))
        found.append(met)
    return found


def covered_at_rest(corners, cloth, body, reach: float, rest_cover, depth: float, vector=np.asarray) -> np.ndarray:
    """Which triangles, given as (T, 3, 3) corners in the rest pose, one garment's cloth covers at rest,
    as the pair check takes a place of an inner garment: rest_cover, coverage._rest_cover, finds from one
    of the triangle's samples, along its outward normal, the cloth's inner side where it is opaque within
    the reach of the garment's band, or the triangle already through the cloth by at most depth. A
    triangle none of whose samples finds it under cloth with holes, such as a net, is followed again at
    points DENSE_SPACING apart. vector makes the points and the direction what rest_cover takes."""
    corners = np.asarray(corners, float).reshape(-1, 3, 3)
    found = np.zeros(len(corners), bool)
    for index, (points, normal) in enumerate(zip(triangle_samples(corners), triangle_normals(corners))):
        if not np.any(normal):
            continue
        direction = vector(normal)

        def under(point) -> bool:
            return rest_cover(cloth, vector(point), direction, reach, depth, body) is not None

        found[index] = any(under(p) for p in points) or (
            bool(getattr(cloth, "holes", False)) and any(under(p) for p in dense_samples(corners[index])))
    return found


def toward_camera(yaw: float, pitch: float) -> np.ndarray:
    """The direction from what a camera looks at toward the camera, by yaw and pitch in degrees: an asset
    faces +Y, so a yaw of 0 sees its back and 180 its front."""
    p, y = math.radians(pitch), math.radians(yaw)
    return np.array([math.sin(y) * math.cos(p), -math.cos(y) * math.cos(p), math.sin(p)])


def aim(points, yaw: float, pitch: float, aspect: float = WIDTH / HEIGHT) -> tuple[np.ndarray, np.ndarray, float]:
    """The camera of a view that frames the points whole: the direction toward it, the point it looks at,
    and its distance, which leaves FRAME_MARGIN of the frame free around them at the game's field of
    view, within the game's distance range."""
    points = np.asarray(points, float).reshape(-1, 3)
    toward = toward_camera(yaw, pitch)
    right = np.cross(-toward, (0.0, 0.0, 1.0))
    right /= np.linalg.norm(right)
    up = np.cross(right, -toward)
    tan_y = math.tan(math.radians(FIELD_OF_VIEW) / 2.0) * (1.0 - FRAME_MARGIN)
    tan_x = tan_y * aspect
    centre = (points.min(axis=0) + points.max(axis=0)) / 2.0
    distance = NEAREST
    for _ in range(4):
        relative = points - centre
        x, z, depth = relative @ right, relative @ up, relative @ toward
        distance = float(np.clip(max((np.abs(x) / tan_x + depth).max(), (np.abs(z) / tan_y + depth).max()), NEAREST, FARTHEST))
        # The figure in the middle of the picture as the camera sees it.
        span = distance - depth
        sx, sz = x / span, z / span
        centre = centre + right * (sx.max() + sx.min()) / 2.0 * distance + up * (sz.max() + sz.min()) / 2.0 * distance
    return toward, centre, distance


def class_counts(pixels) -> dict[str, int]:
    """The numbers of a class picture, RGBA in 0..1: the pixels of skin through, of a garment through,
    of contact, and of the whole figure. Every pixel must be one of the classes' colours or the empty
    background, so the counts are exact."""
    rgba = np.rint(np.asarray(pixels, float) * 255.0).astype(np.int64).reshape(-1, 4)
    background = (rgba == 0).all(axis=1)
    known = background.copy()
    counts = {}
    for name, colour in (("skin", SKIN_THROUGH), ("inner", INNER_THROUGH), ("contact", CONTACT_COLOUR), ("other", OTHER)):
        exact = (rgba == np.rint(np.array(colour) * 255.0).astype(np.int64)).all(axis=1)
        counts[name] = int(exact.sum())
        known |= exact
    if not known.all():
        raise ValueError(f"{int((~known).sum())} pixels of the class picture have no class colour.")
    return {"skin": counts["skin"], "inner": counts["inner"], "contact": counts["contact"], "figure": int((~background).sum())}


# ----------------------------------------------------------------------------------------------------
# Blender: one outfit on one body put together from the files, its drawn meshes, the classes, and the
# pictures. The tests read the plain rules above without it.

try:
    import bmesh
    import bpy
    from mathutils import Matrix, Vector
    from mathutils.bvhtree import BVHTree
    from mathutils.interpolate import poly_3d_calc
except ImportError:
    bpy = None

if bpy is not None:
    from . import (bodies, body_files, cloth, coverage, garment as garment_module, manifest_io, paint, poses, retarget_pose,
                   skeleton, texture)
    from .scene import ArtPipelineError


def _cut(obj, kept: np.ndarray, triangles: list[tuple[int, int, int, int]]) -> np.ndarray:
    """Leaves the drawn faces of a mesh, each split from its first corner as the game splits it, with its
    vertices in their order, and returns the triangle of every face of the new mesh by its index among
    the triangles given. A split that is not the game's stops the view."""
    mesh = obj.data
    by_face: dict[int, dict[frozenset, int]] = {}
    for index, (face, a, b, c) in enumerate(triangles):
        by_face.setdefault(face, {})[frozenset((a, b, c))] = index
    bm = bmesh.new()
    try:
        bm.from_mesh(mesh)
        layer = bm.faces.layers.int.new("view_face")
        for face in bm.faces:
            face[layer] = face.index
        bm.verts.ensure_lookup_table()
        first = [polygon.vertices[0] for polygon in mesh.polygons]
        bmesh.ops.delete(bm, geom=[f for f in bm.faces if not kept[f.index]], context="FACES_ONLY")
        # A face of more than four corners is split off triangle by triangle from its first corner; a
        # quad, which starts at its first corner, is split from it to its third by the triangulation.
        for face in [f for f in bm.faces if len(f.verts) > 4]:
            corner = bm.verts[first[face[layer]]]
            while len(face.verts) > 3:
                loop = next(item for item in face.loops if item.vert == corner)
                split, _loop = bmesh.utils.face_split(face, corner, loop.link_loop_next.link_loop_next.vert)
                face = split if len(split.verts) > len(face.verts) else face
        bmesh.ops.triangulate(bm, faces=list(bm.faces), quad_method="FIXED", ngon_method="EAR_CLIP")
        order = []
        for face in bm.faces:
            found = by_face.get(face[layer], {}).get(frozenset(v.index for v in face.verts))
            if found is None:
                raise ArtPipelineError(f"{obj.name}: face {face[layer]} does not split from its first corner as the game "
                                       "splits it.")
            order.append(found)
        bm.faces.layers.int.remove(layer)
        bm.to_mesh(mesh)
    finally:
        bm.free()
    mesh.update()
    mesh.attributes.new(CLASS_ATTRIBUTE, "FLOAT_COLOR", "FACE")
    return np.array(order, np.int64)


class _Piece:
    """A mesh the view draws: its object, its faces as the game splits them, which of them it draws, the
    UVs of their corners, the triangle of every face of its drawn mesh, and the class of every
    triangle."""

    def __init__(self, obj, kept_faces: np.ndarray):
        mesh = obj.data
        self.obj = obj
        self.polygons = [tuple(p.vertices) for p in mesh.polygons]
        self.fan = fan(self.polygons)
        self.faces = np.array([face for face, _a, _b, _c in self.fan], np.int64)
        self.kept = np.asarray(kept_faces, bool)[self.faces] if len(self.faces) else np.zeros(0, bool)
        self.triangles = np.array([t[1:] for t in self.fan], np.int64).reshape(-1, 3)
        loops = [list(p.loop_indices) for p in mesh.polygons]
        self.loops = np.array([(loops[face][0], loops[face][k], loops[face][k + 1])
                               for face, polygon in enumerate(self.polygons) for k in range(1, len(polygon) - 1)],
                              np.int64).reshape(-1, 3)
        uv = np.zeros(len(mesh.loops) * 2)
        if mesh.uv_layers.active is not None:
            mesh.uv_layers.active.data.foreach_get("uv", uv)
        self.uv = uv.reshape(-1, 2)[self.loops] if len(self.loops) else np.zeros((0, 3, 2))
        self.used = np.unique(self.triangles[self.kept].reshape(-1))
        self.order = _cut(obj, np.asarray(kept_faces, bool), self.fan)
        self.classes = np.zeros(len(self.fan), np.int8)
        self.co = np.zeros((len(mesh.vertices), 3))

    def colours(self) -> np.ndarray:
        return np.array(CLASS_COLOURS, np.float32)[self.classes[self.order]]


class _Cloth:
    """The opaque cloth of drawn garments for rays, as the pair check takes a garment's cloth: their drawn
    triangles without the turned edges, whose rims and flaps lie inside the cloth, each with the UVs of
    its corners, the alpha of its garment's texture, which cuts it at ALPHA_CUT, and its garment, by its
    index among the drawn garments."""

    def __init__(self, parts: list[tuple[_Piece, np.ndarray | None, np.ndarray, int]]):
        co, triangles, owner, uv, garments, self.alpha = [], [], [], [], [], []
        offset = 0
        for piece, alpha, chosen, garment_index in parts:
            co.append(piece.co)
            triangles.append(piece.triangles[chosen] + offset)
            owner.append(np.full(int(chosen.sum()), len(self.alpha), np.int64))
            garments.append(np.full(int(chosen.sum()), garment_index, np.int64))
            uv.append(piece.uv[chosen])
            self.alpha.append(alpha)
            offset += len(piece.co)
        self.holes = any(alpha is not None for alpha in self.alpha)
        self.tree = None
        if not parts or not sum(len(t) for t in triangles):
            return
        points = np.concatenate(co)
        triangles = np.concatenate(triangles)
        self.corners = points[triangles]
        self.uv = np.concatenate(uv)
        self.owner = np.concatenate(owner)
        self.garments = np.concatenate(garments)
        self.tree = BVHTree.FromPolygons(points.tolist(), triangles.tolist())

    def ray_cast(self, origin, direction, distance):
        return self.tree.ray_cast(origin, direction, distance)

    def garment(self, triangle: int) -> int:
        return int(self.garments[triangle])

    def near(self, point: np.ndarray, distance: float) -> bool:
        return self.tree is not None and self.tree.find_nearest(point.tolist(), distance)[0] is not None

    def opaque(self, triangle: int, location: np.ndarray) -> bool:
        alpha = self.alpha[self.owner[triangle]]
        if alpha is None:
            return True
        weights = poly_3d_calc([Vector(c) for c in self.corners[triangle]], Vector(location))
        u, v = np.asarray(weights) @ self.uv[triangle]
        height, width = alpha.shape
        x = min(max(int(u * width), 0), width - 1)
        y = min(max(int(v * height), 0), height - 1)
        return bool(alpha[y, x] >= ALPHA_CUT)


def _over(piece: _Piece, chosen: np.ndarray, cloth: _Cloth, body, rest: "_Rest") -> np.ndarray:
    """What cloth lies under each chosen triangle of a drawn mesh, NONE, THROUGH, or CONTACT, as
    lies_under decides it, against each triangle's outward side, with the cloth each garment covered at
    rest; a triangle without cloth within its size and the reach of its centre has none."""
    marked = np.zeros(len(piece.fan), np.int8)
    if cloth.tree is None:
        return marked
    candidates = np.nonzero(chosen)[0]
    corners = piece.co[piece.triangles[candidates]]
    centres = corners.mean(axis=1)
    radii = np.linalg.norm(corners - centres[:, None, :], axis=2).max(axis=1)
    near = np.array([cloth.near(c, float(r) + THROUGH_REACH) for c, r in zip(centres, radii)], bool)
    if near.any():
        candidates, corners = candidates[near], corners[near]
        marked[candidates] = lies_under(corners, cloth, body, owned=lambda index, triangle: rest.covers(
            cloth.garment(triangle), piece, int(candidates[index])))
    return marked


class _Rest:
    """The outfit in the rest pose, with the cloth chains of every drawn garment laid together: the drawn
    meshes, the body's surface, every garment's cloth alone with the reach of its band, as the pair check
    takes them, and the cloth the class rule looks for under the body and under each garment; and which
    triangles of the body and of the garments under it each garment's cloth covers at rest, each found
    once, when a pose first asks for it."""

    def __init__(self, outfit: "Outfit"):
        outfit.pose({})
        self.co = {id(piece): _posed(piece.obj) for piece in [outfit.body_piece] + [g[0] for g in outfit.garments]}
        body = outfit.body_piece
        self.body = BVHTree.FromPolygons(self.co[id(body)].tolist(), body.triangles.tolist())
        self.cloths, self.reach, parts = [], [], []
        for index, ((piece, alpha, _marked, cloth_triangles), band) in enumerate(zip(outfit.garments, outfit.bands)):
            piece.co = self.co[id(piece)]
            self.cloths.append(_Cloth([(piece, alpha, cloth_triangles, index)]))
            self.reach.append(coverage.REACH[band])
            parts.append((piece, alpha, cloth_triangles, index))
        # The cloth the class rule looks for under each drawn mesh, as classify takes it.
        self.under = {id(body): _Cloth(parts)}
        for index, (piece, _alpha, _marked, _cloth) in enumerate(outfit.garments):
            self.under[id(piece)] = _Cloth(parts[index + 1:])
        self.known: dict[tuple[int, int], np.ndarray] = {}
        self.met: dict[tuple[int, int], set[int]] = {}

    def through(self, piece: _Piece, triangle: int) -> set[int]:
        """The garments whose cloth the class rule finds under a triangle in the rest pose."""
        key = (id(piece), triangle)
        if key not in self.met:
            cloth = self.under[id(piece)]
            corners = self.co[id(piece)][piece.triangles[triangle]]
            self.met[key] = set() if cloth.tree is None else {cloth.garment(t) for t in cloth_met(corners, cloth, self.body)[0]}
        return self.met[key]

    def covers(self, garment_index: int, piece: _Piece, triangle: int) -> bool:
        """Whether the garment's cloth covers a triangle of a drawn mesh at rest: the class rule finds it
        under the triangle in the rest pose, so nothing that shows through at rest is contact, or
        covered_at_rest finds it over the triangle with the pair check's coverage._rest_cover, the reach
        of the garment's band, and its depth at rest."""
        found = self.known.setdefault((garment_index, id(piece)), np.full(len(piece.fan), -1, np.int8))
        if found[triangle] < 0:
            corners = self.co[id(piece)][piece.triangles[triangle]]
            found[triangle] = int(garment_index in self.through(piece, triangle) or covered_at_rest(
                corners, self.cloths[garment_index], self.body, self.reach[garment_index], coverage._rest_cover,
                coverage.PAIR_REST_DEPTH, vector=Vector)[0])
        return bool(found[triangle])


def _posed(obj) -> np.ndarray:
    """The world positions of a mesh's vertices as its armature poses it."""
    return coverage._posed(obj)[0]


def _image_alpha(image) -> np.ndarray:
    width, height = image.size
    pixels = np.empty(width * height * 4, np.float32)
    image.pixels.foreach_get(pixels)
    return pixels.reshape(height, width, 4)[:, :, 3].copy()


def _take_shape(obj, key: str | None) -> None:
    """Takes a shape key in full, or else the basis, into the mesh and drops the shape keys, as the game
    draws a blend shape at its full weight. A mesh without the shape key it takes cannot be drawn, as in
    the game."""
    keys = obj.data.shape_keys
    problem = shape_problem(obj.name, key, [block.name for block in keys.key_blocks] if keys is not None else [])
    if problem is not None:
        raise ArtPipelineError(problem)
    if keys is None:
        return
    block = keys.key_blocks[key] if key is not None else keys.key_blocks[0]
    co = np.empty(len(obj.data.vertices) * 3)
    block.data.foreach_get("co", co)
    obj.shape_key_clear()
    obj.data.vertices.foreach_set("co", co)
    obj.data.update()


def _skin_material(source):
    """A material of the body, its eyes, teeth, or tongue as the game draws it: with its back faces
    culled, as their materials' _Cull of 2 has the skin shader and URP Lit cull them."""
    material = source.copy()
    material.name = f"View_{source.name}"
    material.use_backface_culling = True
    return material


def _look_garment_material(source):
    """A garment's material as the game's garment shader draws it: both faces, and its texture's alpha
    clipped at ALPHA_CUT."""
    material = source.copy()
    material.name = f"View_{source.name}"
    material.use_backface_culling = False
    material.surface_render_method = "DITHERED"
    tree = material.node_tree
    for link in list(tree.links):
        if link.to_socket.name == "Alpha" and link.from_node.type == "TEX_IMAGE":
            clip = tree.nodes.new("ShaderNodeMath")
            clip.operation = "GREATER_THAN"
            clip.inputs[1].default_value = ALPHA_CUT - 1e-4
            source_socket, target = link.from_socket, link.to_socket
            tree.links.remove(link)
            tree.links.new(source_socket, clip.inputs[0])
            tree.links.new(clip.outputs[0], target)
    return material


def _class_material(name: str, cull: bool, image=None, uv_map: str | None = None):
    """A flat material that shows the class colour of every face without light, opaque, or cut by an
    image's alpha at ALPHA_CUT."""
    material = bpy.data.materials.new(name)
    material.use_nodes = True
    material.use_backface_culling = cull
    material.surface_render_method = "DITHERED"
    tree = material.node_tree
    tree.nodes.clear()
    output = tree.nodes.new("ShaderNodeOutputMaterial")
    colour = tree.nodes.new("ShaderNodeAttribute")
    colour.attribute_type = "GEOMETRY"
    colour.attribute_name = CLASS_ATTRIBUTE
    emission = tree.nodes.new("ShaderNodeEmission")
    emission.inputs["Strength"].default_value = 1.0
    tree.links.new(colour.outputs["Color"], emission.inputs["Color"])
    if image is None:
        tree.links.new(emission.outputs["Emission"], output.inputs["Surface"])
        return material
    coordinates = tree.nodes.new("ShaderNodeUVMap")
    coordinates.uv_map = uv_map or ""
    sampled = tree.nodes.new("ShaderNodeTexImage")
    sampled.image = image
    tree.links.new(coordinates.outputs["UV"], sampled.inputs["Vector"])
    clip = tree.nodes.new("ShaderNodeMath")
    clip.operation = "GREATER_THAN"
    clip.inputs[1].default_value = ALPHA_CUT - 1e-4
    tree.links.new(sampled.outputs["Alpha"], clip.inputs[0])
    clear = tree.nodes.new("ShaderNodeBsdfTransparent")
    mix = tree.nodes.new("ShaderNodeMixShader")
    tree.links.new(clip.outputs[0], mix.inputs["Fac"])
    tree.links.new(clear.outputs["BSDF"], mix.inputs[1])
    tree.links.new(emission.outputs["Emission"], mix.inputs[2])
    tree.links.new(mix.outputs["Shader"], output.inputs["Surface"])
    return material


def _world(name: str, colour: float):
    world = bpy.data.worlds.new(name)
    world.use_nodes = True
    background = next(n for n in world.node_tree.nodes if n.type == "BACKGROUND")
    background.inputs["Color"].default_value = (colour, colour, colour, 1.0)
    background.inputs["Strength"].default_value = 1.0
    return world


def _append_rig(rig: dict):
    """The armature of the library's rig alone, appended from its file without its meshes and linked to
    the scene, so a clip poses it; it draws nothing."""
    with bpy.data.libraries.load(rig["file"], link=False) as (source, target):
        if rig["root"] not in source.objects:
            raise ArtPipelineError(f'{rig["file"]} has no object "{rig["root"]}".')
        target.objects = [rig["root"]]
    armature = target.objects[0]
    if armature is None or armature.type != "ARMATURE":
        raise ArtPipelineError(f'"{rig["root"]}" in {rig["file"]} is not an armature.')
    bpy.context.scene.collection.objects.link(armature)
    armature.hide_render = True
    return armature


def _reference(path: str, armature) -> dict:
    """The reference pose of an armature from the avatar skeleton of its model's .fbx.meta."""
    parents = {bone.name: (bone.parent.name if bone.parent else None) for bone in armature.data.bones}
    rest = {bone.name: np.array(bone.matrix_local) for bone in armature.data.bones}
    try:
        with open(path, encoding="utf-8") as handle:
            lines = retarget_pose.avatar_skeleton_lines(handle.read())
        return retarget_pose.reference_of(lines, rest, parents)
    except OSError as error:
        problem = f"cannot be read ({error.strerror})"
    except ValueError as error:
        problem = str(error)
    model = os.path.basename(path)[:-len(".meta")] if path.endswith(".meta") else os.path.basename(path)
    raise ArtPipelineError(f"{path} {problem}; import {model} in Unity first, so its avatar's reference pose is current, "
                           "then render again.")


def _retarget(arm, rig, job_rig: dict, body_reference: str):
    """The model that poses the body after the rig, from the two avatars' reference poses."""
    try:
        return retarget_pose.Retarget(
            {bone.name: (bone.parent.name if bone.parent else None) for bone in arm.data.bones},
            {bone.name: bone.human for bone in skeleton.load(job_rig["skeleton"]).bones if bone.human},
            {bone.name: np.array(bone.matrix_local) for bone in arm.data.bones}, _reference(body_reference, arm),
            {bone.name: np.array(bone.matrix_local) for bone in rig.data.bones}, _reference(job_rig["reference"], rig))
    except ValueError as error:
        raise ArtPipelineError(f"{arm.name} cannot follow the rig {rig.name}: {error}") from error


def _reset(arm) -> None:
    """Every bone at rest, its scale too, so a pose does not depend on the poses before it: laying the
    cloth chains leaves the float rounding of their turn in their bones' scale."""
    for bone in arm.pose.bones:
        bone.rotation_mode = "QUATERNION"
        bone.rotation_quaternion = (1.0, 0.0, 0.0, 0.0)
        bone.location = (0.0, 0.0, 0.0)
        bone.scale = (1.0, 1.0, 1.0)


class Outfit:
    """One outfit on one body, put together once from the files, then posed and drawn for every view."""

    def __init__(self, job: dict):
        for obj in list(bpy.data.objects):
            bpy.data.objects.remove(obj, do_unlink=True)
        scene = bpy.context.scene
        self.label = job["body"]["root"]
        self.arm = body_files.append_body(job["body"]["file"], self.label, "Body")
        self.body = bodies.source_body(self.label)
        self.others = sorted((o for o in self.arm.children_recursive if o.type == "MESH" and o is not self.body),
                             key=lambda o: o.name)
        body_faces = len(self.body.data.polygons)
        # Every worn garment with its template's fields and its data; only the drawn ones are put on.
        worn, entries = [], {}
        for entry in job["garments"]:
            fields = manifest_io.read_json(os.path.join(os.path.dirname(entry["templateFile"]), "asset.json"))
            data = manifest_io.read_json(entry["data"])
            if int(data.get("bodyFaces", -1)) != body_faces:
                raise ArtPipelineError(f"{entry['data']} names {data.get('bodyFaces')} faces of the body {self.label}, which "
                                       f"has {body_faces}; export {entry['template']} again.")
            item = worn_from(entry["texture"], entry["template"], fields, data)
            worn.append(item)
            entries[item.name] = (entry, fields, data)
        self.drawn = drawn(worn)
        self.covers = cover_layers(self.drawn)
        objects = []
        for item in self.drawn:
            entry, _fields, data = entries[item.name]
            shape = shape_of(item, self.drawn)
            obj = garment_module.append_inner(entry["templateFile"], item.template, [self.label], shape == UNDER_HEADWEAR)[0]
            _take_shape(obj, shape)
            if int(data.get("garmentFaces", -1)) != len(obj.data.polygons):
                raise ArtPipelineError(f"{entry['data']} names {data.get('garmentFaces')} faces of {item.template}, whose "
                                       f"version for {self.label} has {len(obj.data.polygons)}; export it again.")
            texture.dress(item.template, entry["textureFile"], item.name, [self.label])
            objects.append(obj)
        # PlayerBodyAppearance: the hidden faces name an inner template's faces by the count it had.
        for item in self.drawn:
            _entry, _fields, data = entries[item.name]
            for inner, count in zip(data.get("innerTemplates", []), data.get("innerFaceCounts", [])):
                other = next((o for o, g in zip(objects, self.drawn) if g.template == inner), None)
                if other is not None and int(count) != len(other.data.polygons):
                    raise ArtPipelineError(f"{entries[item.name][0]['data']} hides faces of {inner} with {count} faces, but "
                                           f"its version for {self.label} has {len(other.data.polygons)}; export "
                                           f"{item.template} again.")
        # The cloth chains of every drawn garment, laid together, measured before the pull-in.
        self.kinds, self.capsules, self.reach = coverage._chains_of(objects, self.arm, self.body)
        self._paint(job.get("paints", []))
        self._pull(pull_in(self.drawn))
        clips = sorted({view["pose"]["clip"] for view in job["views"] if view["pose"].get("clip")})
        self.actions = {a.name: a for a in coverage.load_actions([(job["library"], clips)])} if clips else {}
        # A body that is not the library's rig takes the clips as the game retargets them: they play on
        # the rig, appended apart and never drawn, and retarget_pose poses the body after it. A reference
        # pose that cannot be had stops the clip poses, not the rest pose.
        self.rig, self.retarget, self.retarget_problem = None, None, None
        if clips and job.get("rig"):
            self.rig = _append_rig(job["rig"])
            try:
                self.retarget = _retarget(self.arm, self.rig, job["rig"], job["body"]["reference"])
            except ArtPipelineError as error:
                self.retarget_problem = str(error)
        # The drawn meshes, and the cloth of every garment for the rays with the band its template names.
        self.body_piece = _Piece(self.body, kept_body_faces(body_faces, self.covers))
        self.garments, self.bands = [], []
        for item, obj in zip(self.drawn, objects):
            fields = entries[item.name][1]
            image = bpy.data.images.get(texture.image_name(item.name))
            alpha = _image_alpha(image) if image is not None and texture.has_holes(image) else None
            inside = garment_module.turned_mask(len(obj.data.vertices), coverage._turned_of(obj, fields.get("recipe")))
            piece = _Piece(obj, kept_garment_faces(item, self.drawn, self.covers, len(obj.data.polygons)))
            marked, cloth_triangles = garment_triangles(piece.kept, inside, piece.triangles)
            self.garments.append((piece, alpha, marked, cloth_triangles))
            self.bands.append(str(fields["band"]))
        self.other_pieces = [_Piece(obj, np.ones(len(obj.data.polygons), bool)) for obj in self.others]
        self._materials()
        self._studio(scene)
        # The outfit at rest, which decides contact.
        self.rest = _Rest(self)

    def _paint(self, paints: list[dict]) -> None:
        """The paint layers the body shows with the drawn garments on their painted material, from the
        lowest layer up, as the paint sheet lays them: of the paint layers that fit the body, those the
        look names and the underwear it is always shown in. More than the skin shader draws on one
        material stops the view."""
        shown = shown_paints(paints, [p["name"] for p in paints if p.get("named")], self.drawn)
        over = paints_over_limit(shown)
        if over:
            raise ArtPipelineError(f"The look shows {', '.join(f'{count} paint layers on {part}' for part, count in over.items())} "
                                   f"of {self.label}; the skin shader draws at most {MAX_PAINT_LAYERS} on a material.")
        if not shown:
            return
        bodies.write_paint_uvs(self.body.data)
        by_part: dict[str, list] = {}
        for entry in shown:
            wanted = paint.image_name(entry["name"], self.label)
            with bpy.data.libraries.load(entry["file"], link=False) as (source, target):
                if wanted not in source.images:
                    raise ArtPipelineError(f"{entry['file']} has no image {wanted}; run tools/art/Sync-PaintAsset.ps1.")
                target.images = [wanted]
            by_part.setdefault(entry["part"], []).append(target.images[0])
        for part, images in by_part.items():
            index = paint._painted_index(self.body, part, self.label)
            paint.preview_material(self.body, index, f"Preview_{part}_{self.label}", images)

    def _pull(self, pulls: dict[int, float]) -> None:
        """Plan.AddSkinned: a pulled-in vertex moves inward along its normal at rest, by the largest
        distance of the drawn garments."""
        mesh = self.body.data
        co = np.empty(len(mesh.vertices) * 3)
        mesh.vertices.foreach_get("co", co)
        normals = np.empty(len(mesh.vertices) * 3)
        mesh.vertex_normals.foreach_get("vector", normals)
        co, normals = co.reshape(-1, 3), normals.reshape(-1, 3)
        for vertex, distance in pulls.items():
            if 0 <= vertex < len(co) and distance > 0.0:
                co[vertex] -= normals[vertex] / max(float(np.linalg.norm(normals[vertex])), 1e-12) * distance
        mesh.vertices.foreach_set("co", co.reshape(-1))
        mesh.update()

    def _materials(self) -> None:
        """The materials of the look, the body with its eyes, teeth, and tongue with their back faces
        culled and the garments drawn from both sides and cut at ALPHA_CUT, and the flat ones of the
        classes, culled alike, per drawn mesh."""
        self.look, self.flat = [], []
        skin = _class_material("ViewClass_Skin", True)
        for obj in [self.body] + self.others:
            materials = list(obj.data.materials)
            self.look.append((obj, [_skin_material(m) for m in materials]))
            self.flat.append((obj, [skin for _m in materials]))
        for (piece, alpha, _marked, _cloth), item in zip(self.garments, self.drawn):
            obj = piece.obj
            self.look.append((obj, [_look_garment_material(m) for m in obj.data.materials]))
            image = bpy.data.images.get(texture.image_name(item.name)) if alpha is not None else None
            uv_map = obj.data.uv_layers.active.name if obj.data.uv_layers.active is not None else None
            self.flat.append((obj, [_class_material(f"ViewClass_{item.name}", False, image, uv_map)
                                    for _m in obj.data.materials]))

    def _studio(self, scene) -> None:
        scene.render.engine = "BLENDER_EEVEE"
        scene.render.resolution_x, scene.render.resolution_y = WIDTH, HEIGHT
        scene.render.resolution_percentage = 100
        scene.render.image_settings.file_format = "PNG"
        scene.render.image_settings.color_mode = "RGBA"
        scene.render.image_settings.color_depth = "8"
        scene.render.dither_intensity = 0.0
        scene.render.use_motion_blur = False
        scene.view_settings.view_transform = "Standard"
        scene.view_settings.look = "None"
        scene.view_settings.exposure = 0.0
        scene.view_settings.gamma = 1.0
        camera = bpy.data.cameras.new("ViewCamera")
        camera.sensor_fit = "VERTICAL"
        camera.angle_y = math.radians(FIELD_OF_VIEW)
        camera.clip_start = 0.01
        camera.clip_end = 100.0
        self.camera = bpy.data.objects.new("ViewCamera", camera)
        scene.collection.objects.link(self.camera)
        scene.camera = self.camera
        sun = bpy.data.lights.new("ViewSun", "SUN")
        sun.energy = SUN_INTENSITY * math.pi
        sun.angle = math.radians(1.0)
        self.sun = bpy.data.objects.new("ViewSun", sun)
        scene.collection.objects.link(self.sun)
        self.look_world = _world("ViewLook", GREY_LINEAR)
        self.flat_world = _world("ViewClasses", 0.0)

    def _pass(self, classes: bool) -> None:
        scene = bpy.context.scene
        for obj, materials in (self.flat if classes else self.look):
            for slot, material in enumerate(materials):
                obj.data.materials[slot] = material
        scene.world = self.flat_world if classes else self.look_world
        scene.render.film_transparent = classes
        scene.eevee.taa_render_samples = 1 if classes else LOOK_SAMPLES
        scene.render.filter_size = 0.0 if classes else 1.5
        self.sun.hide_render = classes

    def pose(self, pose: dict) -> None:
        """The armature in the rest pose or a frame of a clip, with the cloth chains of every drawn garment
        laid together as the game's target pose lays them."""
        _reset(self.arm)
        if pose.get("clip"):
            if self.retarget_problem is not None:
                raise ArtPipelineError(self.retarget_problem)
            played = self.rig if self.rig is not None else self.arm
            action = self.actions[pose["clip"]]
            played.animation_data_create()
            played.animation_data.action = action
            played.animation_data.action_slot = action.slots[0]
            bpy.context.scene.frame_set(int(pose["frame"]))
            if self.retarget is not None:
                self._follow_rig()
        else:
            if self.arm.animation_data is not None:
                self.arm.animation_data.action = None
            bpy.context.scene.frame_set(0)
        bpy.context.view_layer.update()
        cloth.follow(self.arm, self.kinds, self.capsules, self.reach)
        bpy.context.view_layer.update()

    def _follow_rig(self) -> None:
        """Gives every bone of the body the local pose that puts it where the retargeting of the rig's
        evaluated pose puts it."""
        model = self.retarget
        posed = model.pose({bone.name: np.array(bone.matrix) for bone in self.rig.pose.bones})
        for name, basis in retarget_pose.bases(model.parents, model.body_rest, posed).items():
            bone = self.arm.pose.bones[name]
            bone.rotation_mode = "QUATERNION"
            bone.location = basis[:3, 3]
            bone.rotation_quaternion = Matrix(basis[:3, :3].tolist()).to_quaternion()
            bone.scale = (1.0, 1.0, 1.0)

    def bone_heads(self) -> dict[str, list[float]]:
        """Where the head of every bone lies in the current pose, in the body's own frame and the game's
        axes, x right, y up, z front, in meters, as the capture from the game records the game's."""
        heads = {}
        for bone in self.arm.pose.bones:
            head = self.arm.matrix_world @ bone.head
            heads[bone.name] = [round(head.x, 5), round(head.z, 5), round(head.y, 5)]
        return heads

    def classify(self) -> None:
        """The classes of the drawn triangles in the current pose: skin under which the cloth of a drawn
        garment lies, a garment under which the cloth of a garment drawn after it lies, and contact,
        either where that garment's cloth did not cover it at rest."""
        pieces = [self.body_piece] + [g[0] for g in self.garments] + self.other_pieces
        for piece in pieces:
            piece.co = _posed(piece.obj)
            piece.classes[:] = OTHER_CLASS
        # The body's own surface, all of it, pulled in, ends a ray.
        body = BVHTree.FromPolygons(self.body_piece.co.tolist(), self.body_piece.triangles.tolist())
        cloth = [(piece, alpha, cloth_triangles, index) for index, (piece, alpha, _marked, cloth_triangles) in enumerate(self.garments)]
        found = _over(self.body_piece, self.body_piece.kept, _Cloth(cloth), body, self.rest)
        self.body_piece.classes[found == THROUGH] = SKIN_CLASS
        self.body_piece.classes[found == CONTACT] = CONTACT_CLASS
        for index, (piece, _alpha, marked, _cloth) in enumerate(self.garments):
            found = _over(piece, marked, _Cloth(cloth[index + 1:]), body, self.rest)
            piece.classes[found == THROUGH] = INNER_CLASS
            piece.classes[found == CONTACT] = CONTACT_CLASS
        for piece in pieces:
            piece.obj.data.attributes[CLASS_ATTRIBUTE].data.foreach_set("color", piece.colours().reshape(-1))
            piece.obj.data.update()

    def figure(self) -> np.ndarray:
        """The drawn vertices of every mesh in the current pose."""
        pieces = [self.body_piece] + [g[0] for g in self.garments] + self.other_pieces
        return np.concatenate([p.co[p.used] for p in pieces])

    def _bone_point(self, name: str, share: float) -> np.ndarray:
        posed = self.arm.pose.bones[name]
        head, tail = self.arm.matrix_world @ posed.head, self.arm.matrix_world @ posed.tail
        return np.array(head.lerp(tail, share))

    def shoot(self, camera: dict, look: str, classes: str) -> dict[str, int]:
        """Both pictures of the current pose from one camera, the look and the classes, each written to its
        file, and the numbers of the classes: the whole figure, the feet whole, or a close view at the
        game's least distance from a point of the skeleton."""
        yaw, pitch = float(camera["yaw"]), float(camera["pitch"])
        if camera["frame"] == "figure":
            toward, centre, distance = aim(self.figure(), yaw, pitch)
        elif camera["frame"] == "feet":
            points = self.figure()
            top = max(self._bone_point(name, 0.0)[2] for name in FEET) + FEET_ABOVE
            toward, centre, distance = aim(points[points[:, 2] <= top], yaw, pitch)
        else:
            names, share = CLOSE_TARGETS[camera["frame"]]
            centre = np.mean([self._bone_point(name, share) for name in names], axis=0)
            toward, distance = toward_camera(yaw, pitch), CLOSE_DISTANCE
        self.camera.location = Vector(centre + toward * distance)
        self.camera.rotation_euler = (Vector(centre) - self.camera.location).to_track_quat("-Z", "Y").to_euler()
        self.sun.rotation_euler = (math.radians(90.0 - SUN_ELEVATION), 0.0, math.radians(yaw - SUN_SIDE))
        self._pass(False)
        _render(look)
        self._pass(True)
        return class_counts(_render(classes, read=True))


def _render(path: str, read: bool = False) -> np.ndarray | None:
    """Renders the scene to a PNG at path, written under a temporary name and moved into place, and
    returns its pixels when asked."""
    folder, name = os.path.split(path)
    temporary = os.path.join(folder, f".{name}.{os.getpid()}.tmp.png")
    bpy.context.scene.render.filepath = temporary
    bpy.ops.render.render(write_still=True)
    pixels = None
    if read:
        image = bpy.data.images.load(temporary)
        try:
            width, height = image.size
            pixels = np.empty(width * height * 4, np.float32)
            image.pixels.foreach_get(pixels)
            pixels = pixels.reshape(height, width, 4)
        finally:
            bpy.data.images.remove(image)
    os.replace(temporary, path)
    return pixels


def write_numbers(path: str, numbers: dict) -> None:
    """A view's numbers as JSON, written under a temporary name and moved into place."""
    folder, name = os.path.split(path)
    temporary = os.path.join(folder, f".{name}.{os.getpid()}.tmp")
    with open(temporary, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(json.dumps(numbers, sort_keys=True) + "\n")
    os.replace(temporary, path)


def render_job(job: dict):
    """Renders every view of a job, one outfit on one body, pose by pose, and yields each view's numbers
    once its look picture, its class picture, and its numbers are written."""
    outfit = Outfit(job)
    by_pose: dict[str, list[dict]] = {}
    for view in job["views"]:
        by_pose.setdefault(json.dumps(view["pose"], sort_keys=True), []).append(view)
    for views in by_pose.values():
        outfit.pose(views[0]["pose"])
        outfit.classify()
        bones = outfit.bone_heads()
        for view in views:
            numbers = dict(outfit.shoot(view["camera"], view["look"], view["classes"]), name=view["name"], key=view["key"],
                           committed=view.get("committed", ""), bones=bones)
            write_numbers(view["numbers"], numbers)
            yield numbers
