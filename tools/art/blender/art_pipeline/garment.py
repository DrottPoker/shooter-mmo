"""Garment templates: the form of a garment kind, as a mesh skinned to the shared skeleton, made on
the garment cage of its skeleton and fitted to every body of the cage. See
docs/EQUIPMENT_VISUALS_DESIGN.md.

The file holds, per body, the collection Export_<Label> with the body's armature <Label> and the
template's version <Name>_<Label> bound to it; in the Reference collection the body's source body,
eyes, teeth, and tongue, which show it and which the weights read; and in the Cage collection the
cage's version Cage_<Label> on every body, taken from the cage's file, whose content fingerprint
the scene keeps. The first body's version is the template a person shapes.

A recipe builds the first version from the first body's cage: the faces of its regions, cut at its
standard lines, its UVs unfolded along seams where clothes have them, edge loops at its splits,
which interpolate the UVs, offset into the band along the cage's normals, with more room at the
armpits, and every open edge turned inward; last, every quad's corners start so that it splits
across the diagonal it folds outward across, a quad and its mirror image alike. A turned edge
is always laid from its cloth, on every version and at every sync, and never settled on its own:
its rim lies the hem's depth inside the cloth, or as deep as the cloth lies off the cage if that is
less, and its flap runs back along the inside of the cloth at that depth, or as deep as the cloth
lies off the cage at its top if that is less. Where the
recipe says the template lies over an inner garment beyond a standard line, such as a top over the
trousers below the waistband, it is built lifted by the inner band's distance from the cage, fading
in over a few centimeters before the line, and its cloth keeps OVER_GAP outside the real surface of
the templates of lower layers it lies over there, their triangles as the game draws them. A modeled
template's first version is modeled by hand instead. Every other version is fitted from the first
through the cage: each vertex is bound to a triangle of the first body's cage, by barycentric
coordinates and an offset in the triangle's frame, and placed at the same triangle and offset of the
other body's cage, and a vertex or a face centre that then leaves the band is moved back into it.
Every body's cage has the same faces, so this needs nothing of the bodies' own topology. When the
cage changes, the first version is bound to the old cage and placed on the new one, so it follows. A
person's corrections of a fitted version are kept as the difference to the attribute "fitted", the
shape the last fitting gave, and the attribute "built" holds the shape the recipe gave the first
version, so a new build of a changed recipe carries the handmade shape over.

Every vertex takes the weights of the nearest skin of the body part its cage face lies on, the
region and side of the cage, interpolated from the corners of the skin's triangle and not smoothed,
so it moves like the skin it covers: a sleeve's inside at the armpit moves with the arm. At the
crotch the cloth follows the skin's rule instead, which holds the crotch with the pelvis: within a
band about the crotch the thighs' share of its weights moves to the pelvis by its distance across
the body, so cloth that spans the legs stays under the held skin; see CROTCH_BAND_DEPTH. The attribute
"transferred_weights" holds a checksum of the weights the last transfer gave each vertex of the first
version; a vertex whose weights differ from it was painted by hand and keeps them, also in the other
versions.

A hairstyle has the shape key UnderHeadwear, the hair pressed flat to fit under hoods and helmets,
made by hand on the first version like the hair itself. Every other version's shape key is fitted
from the first's the same way as its shape, and its corrections are kept as the difference to the
attribute "fitted_under_headwear"."""

import contextlib
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

from human_base import rig

from . import bodies, body_files, cage, cloth, hang, manifest_io, poses
from .scene import ArtPipelineError, relative_image_paths

FITTED = "fitted"
BUILT = "built"
TRANSFERRED = "transferred_weights"
CAGE_COLLECTION = "Cage"
# The content fingerprint of the cage the versions were taken from, which the PowerShell scripts pass.
CAGE_CONTENT = "cage_content"
RECIPE = "recipe"
MODELED = "modeled"
# The shape key of a hairstyle that presses it flat under headwear, and the shape a fitting gave it.
UNDER_HEADWEAR = "UnderHeadwear"
FITTED_UNDER = "fitted_under_headwear"
# The shape keys each visual slot's templates have besides the basis; every other slot has none.
SHAPE_KEYS = {"hair": (UNDER_HEADWEAR,)}
MAX_INFLUENCES = 4
# How far a fitted vertex or a weight may differ from what a sync gives before the file is stale.
FIT_TOLERANCE = 1e-5
WEIGHT_TOLERANCE = 1e-4
# The distance from the cage each band allows, in meters: the smallest and the largest. Hair lies
# close at its hairline and its parting and falls free from the head.
BANDS = {"tight": (0.0, 0.008), "mid": (0.005, 0.025), "outer": (0.010, 0.060), "loose": (0.010, None),
         "hair": (0.002, None)}
SLOTS = ("head", "body", "legs", "hands", "feet", "back", "hair")
# A recipe builds a template this far from the cage: clothes lie as close to the body as they can.
OFFSETS = {"tight": 0.002, "mid": 0.010, "outer": 0.020, "loose": 0.020, "hair": 0.010}
# The ease of a recipe fades out over this distance from the lower half of an armhole, the part of
# the border between the trunk and an arm that lies this far or more below the shoulder joint.
EASE_REACH = 0.06
ARMPIT_DROP = 0.04
# A turned edge folds back inside the cloth this far.
HEM_FLAP = 0.012
# Every rim of a turned edge lies at least this far inside the cloth it is turned from. A rim lies as
# deep as the cloth lies off the cage at most, and the cloth it is turned from keeps HEM_ROOM off the
# cage, so this stays below that room.
MIN_FOLD = 0.0003
# How far a distance, in meters, or a normal's lean may miss a limit and still keep it, so the
# rounding of float32 positions and normals never decides a rule.
ROUNDING = 1e-6
LEAN_ROUNDING = 1e-4
# A cage vertex this close to a line moves onto it before the cut, so the cut leaves no slivers and
# the edge runs straight.
LINE_SNAP = 0.008
# A split runs through a vertex this close to its plane instead of cutting a sliver beside it. A
# split moves no vertex: it comes after the UVs are unfolded, so a texture keeps its place, and the
# UVs of its new vertices are interpolated along the edges they cut.
SPLIT_SNAP = 0.001
# Where a template lies over the real surface of a template of a lower layer, as the game draws its
# triangles, its cloth keeps at least this far outside it.
OVER_GAP = 0.0015
# How far beyond a point of the template's cloth, outward from the cage, the surface of a template it
# lies over still counts as under it, so the point is pushed out over it.
OVER_REACH = 0.03
# A quad that bends less than this across its diagonals, the distance between them, is flat and
# splits along its shorter diagonal.
FLAT_BEND = 1e-5
# Two faces are mirror images of each other across the middle plane when every corner of one,
# mirrored, lies this close to a corner of the other.
MIRROR_MATCH = 0.001
# A template that lies over an inner garment beyond a line rises to it over this distance before
# the line.
OVER_FADE = 0.03
UV_MARGIN = 0.02
# A boot's sole is where its faces look this far down, the cosine of their angle to straight down.
SOLE_FACING = 0.5
# A vertex or a face centre that leaves its band moves back into it by the difference and this
# margin, a few times over, because moving one face moves the corners it shares.
SETTLE_MARGIN = 0.0005
SETTLE_PASSES = 4
# The cloth vertices a turned edge is turned from keep at least this far off the cage, where their
# band lets them come closer, so their fold has room above the cage: the settle moves one that lies
# closer, on every version and at every sync.
HEM_ROOM = SETTLE_MARGIN
# A point that a push along the nearest normal cannot settle moves along the mean of the cage's
# normals this close around it, by at most this much.
SETTLE_AROUND = 0.02
SETTLE_REACH = 0.01
ALPHA_CUT = 0.5
# A modeled template moves with a smooth field of its cage's change: a cage vertex counts by a
# Gaussian of how much farther it lies than the nearest one, this wide at the cage and growing by
# this share of the point's distance from it.
FIELD_WIDTH = 0.015
FIELD_GROWTH = 0.7
# Cloth that spans the crotch, such as trousers between the legs, lies centimeters below the skin
# there, and the skin nearest to it is the inner thighs', so a plain transfer gives it the thighs'
# weights: a leg that swings takes the cloth under the crotch along and presses it up into the skin,
# which a rebuilt body holds with the pelvis (CROTCH_MIDDLE_REBUILT in human_base/rig.py). The fixed
# cloth in a band under, behind, and just in front of the crotch follows the skin's rule instead:
# the share of its weights that it moves from the thighs to the pelvis, its hold, goes by its
# distance across the body from the middle plane, wholly within the skin's held half width and not
# at all from the skin's CROTCH_RADIUS on, smoothly between. The band lies about the crotch, the
# lowest place of the skin's middle line under the trunk within CROTCH_SEARCH of the hip joints'
# depth, as two ranges per axis, of the depth from behind to in front and of the height from below
# to above: within the first range the rule holds wholly, beyond the second not at all, and between
# them it fades smoothly. Behind, it reaches the cloth over the buttocks' cleft, where the skin the
# cover rule leaves drawn lies up to 9 cm behind the crotch; in front, the cloth under the male
# body's bulge, whose skin came through 4 cm in front, and fades out before the front of the
# thighs, which a sitting body raises into it; down, the cloth that spans the legs, down to 4 to
# 5 cm below the crotch, and fades out over the inner thighs below, whose cloth moves with them;
# up, the pelvis, whose skin has little of the thighs. The lengths are a body's of the rig's
# reference height and scale with a body's height, as the skin's do. The hold is then evened out
# over the cloth's edges CROTCH_EVENING times, each time halfway to the mean of its neighbours, as
# the rig evens the skin's weights: the cloth's faces between the legs are up to 6 cm tall, and a
# hold that changes much across one of them shears it until it turns inside out when the legs
# move. Only the hold is evened, never the weights, so no bone of the cloth's neighbours mixes in;
# evening the weights spread the hold up the buttock and opened a gap there. Three passes keep
# every face whole; more spread the hold farther down the inner thighs, where it lets the skin come
# through in Death.
CROTCH_SEARCH = 0.05
CROTCH_BAND_DEPTH = ((-0.12, 0.06), (-0.17, 0.10))
CROTCH_BAND_HEIGHT = ((-0.04, 0.03), (-0.09, 0.06))
CROTCH_EVENING = 3
# The body's middle line is the vertices this close to its middle plane, as the rig takes it.
MIDDLE_PLANE = 1e-5
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
        raise ArtPipelineError(f'The garment has no cage "{cage.version_name(label)}"; run tools/art/Sync-GarmentAsset.ps1.')
    return obj


def recipe_text(recipe) -> str:
    """A recipe as the file keeps it, with its numbers as floats, so 0 and 0.0 read alike, and
    without an empty list of what it lies over, of its loose parts, or of its splits, so a recipe
    from before such a field reads alike."""
    if isinstance(recipe, dict):
        recipe = {**recipe, "ease": float(recipe["ease"]), "hem": float(recipe["hem"])}
        if not recipe.get("over"):
            recipe.pop("over", None)
        if recipe.get("hang"):
            recipe["hang"] = [{**part, **{key: float(part[key]) for key in ("flare", "top", "width") if key in part}}
                              for part in recipe["hang"]]
        else:
            recipe.pop("hang", None)
        if recipe.get("splits"):
            recipe["splits"] = [{**split, **({"offset": float(split["offset"])} if "offset" in split else {})}
                                for split in recipe["splits"]]
        else:
            recipe.pop("splits", None)
    return json.dumps(recipe, sort_keys=True, separators=(",", ":"))


def split_planes(recipe, lines: list[dict], label: str) -> list[tuple[dict, str, Vector, Vector]]:
    """The planes of a recipe's splits on a body: per split and side the line, the side, and the
    line's plane moved by the split's offset toward its bone's head or tail along the line's normal,
    which points toward the tail."""
    by_name = {line["name"]: line for line in lines}
    planes = []
    for split in recipe.get("splits", []) if isinstance(recipe, dict) else []:
        line = by_name[split["line"]]
        offset = float(split.get("offset", 0.0)) * (1.0 if split.get("toward", "tail") == "tail" else -1.0)
        for side, point, normal in cage.line_planes(label, line):
            planes.append((line, side, point + normal * offset, normal))
    return planes


def recipe_regions(recipe) -> tuple[str, ...] | None:
    """The cage regions a template is bound to and measured from: every region but the hands and
    the feet that it does not cover, so the hem of a sleeve or a trouser leg follows its limb and not
    the hand or the foot beyond it, or None for a modeled template, which may lie on any."""
    if not isinstance(recipe, dict):
        return None
    return tuple(r for r in cage.REGIONS if r not in cage.ATTACHED or r in recipe["regions"])


def over_lift(recipe, lines: list[dict], label: str, points: np.ndarray) -> np.ndarray:
    """How far each point of a template on a body lies farther from the cage than its band, because
    the recipe says the template lies over an inner garment beyond a standard line there: the inner
    band's distance from the cage on the line's side the inner garment keeps, fading in over
    OVER_FADE before the line, and only in the line's region of the cage and the regions that hang
    from it, such as a hand from its arm."""
    lift = np.zeros(len(points))
    entries = recipe.get("over", []) if isinstance(recipe, dict) else []
    if not entries:
        return lift
    shell = cage_object(label)
    # A point is over the region of the template it lies nearest, of those the template covers. A
    # turned edge takes its band from the cloth it is turned from: it may lie under the inner
    # garment's surface, down to the cage, inside its own cloth.
    surface = Surface(shell, [], tuple(r for r in cage.REGIONS if r in recipe["regions"]))
    keys = cage_keys(shell, surface)
    nearest = [surface.tree.find_nearest(Vector(p))[2] for p in points]
    by_name = {line["name"]: line for line in lines}
    for entry in entries:
        line = by_name[entry["line"]]
        regions = {line["region"]} | {end for end, parent in cage.ATTACHED.items() if parent == line["region"]}
        for side, point, normal in cage.line_planes(label, line):
            inside = np.array([t is not None and keys[t][0] in regions and (not side or keys[t][1] == side)
                               for t in nearest])
            # The side a cut keeping the bone's head keeps lies behind the line's normal.
            signed = cage.line_offsets(line, point, normal, points)
            beyond = -signed if entry["keep"] == "head" else signed
            share = np.clip(1.0 + beyond / OVER_FADE, 0.0, 1.0)
            share = share * share * (3.0 - 2.0 * share)
            lift = np.maximum(lift, np.where(inside, share * OFFSETS[entry["band"]], 0.0))
    return lift


def band_limits(recipe, lines: list[dict], label: str, points: np.ndarray, band: str) -> tuple[np.ndarray, np.ndarray | None]:
    """The band of each point of a template on a body, from the cage: the recipe's band, and where
    the recipe says the template lies over an inner garment, a least distance that clears the real
    surface of the templates of lower layers it lies over there by OVER_GAP, and a largest distance
    lifted by the inner garment's band, as far as the recipe builds it off the cage."""
    low, high = BANDS[band]
    lift = over_lift(recipe, lines, label, points)
    over = lift > 0.0
    lows = np.where(over, np.maximum(low, under_lows(recipe, label, points, over)), low) if over.any() else \
        np.full(len(points), float(low))
    return lows, (high + lift if high is not None else None)


def lain_over(label: str) -> list[bpy.types.Object]:
    """The versions on a body of the templates of lower layers that a template is worn over, which a
    sync or a check put into the collection Pairs, by name."""
    collection = bpy.data.collections.get(PAIR_COLLECTION)
    if collection is None:
        return []
    return sorted((o for o in collection.objects if o.type == "MESH" and o.parent is not None and o.parent.name == label),
                  key=lambda o: o.name)


def under_lows(recipe, label: str, points: np.ndarray, mask: np.ndarray) -> np.ndarray:
    """Per point of a template on a body where mask is set, the least distance from the cage that
    keeps it OVER_GAP outside the real surface of every template of a lower layer it lies over: of
    their triangles as the game draws them, the farthest outer side that the straight line from the
    cage's nearest point out through the point meets within OVER_REACH beyond the point, plus the
    gap; -inf where no such surface lies under it."""
    lows = np.full(len(points), -np.inf)
    inners = lain_over(label)
    if not inners or not mask.any():
        return lows
    shell = Surface(cage_object(label), [], recipe_regions(recipe))
    trees = [BVHTree.FromPolygons([tuple(p) for p in bodies.world_positions(obj)], [t[1:] for t in _triangles(obj.data)])
             for obj in inners]
    for i in np.nonzero(mask)[0]:
        point = Vector(points[i])
        found, normal, _index, _distance = shell.tree.find_nearest(point)
        if found is None:
            continue
        offset = point - found
        direction = offset.normalized() if offset.dot(normal) > 1e-9 else normal
        reach = max(offset.dot(direction), 0.0) + OVER_REACH
        for tree in trees:
            farthest = outer_side(tree, found, direction, reach)
            if farthest is not None:
                lows[i] = max(lows[i], farthest + OVER_GAP)
    return lows


def outer_side(tree: BVHTree, origin: Vector, direction: Vector, reach: float) -> float | None:
    """How far along a ray the last outer side of a surface lies within the reach, where the ray
    leaves it toward its normal, or None when it leaves it nowhere."""
    farthest, start, travelled = None, origin, 0.0
    for _ in range(8):
        location, normal, _index, distance = tree.ray_cast(start, direction, reach - travelled)
        if location is None:
            break
        travelled += distance
        if normal.dot(direction) > 0.0:
            farthest = travelled
        start = location + direction * 1e-6
        travelled += 1e-6
    return farthest


def field_place(source: np.ndarray, target: np.ndarray, points: np.ndarray) -> np.ndarray:
    """Points moved with a cage from its shape on one body to its shape on another, each by the
    cage's displacement around it: the cage's vertices weighted by a Gaussian of how much farther they
    lie than the nearest one, wider the farther the point stands off the cage. A form that stands off
    the body, such as hair or a helmet, so keeps its shape, where binding each point to the nearest
    face of the cage would crumple it."""
    tree = KDTree(len(source))
    for i, p in enumerate(source):
        tree.insert(p, i)
    tree.balance()
    shift = np.asarray(target) - np.asarray(source)
    moved = np.empty_like(np.asarray(points, float))
    for i, p in enumerate(points):
        _co, _index, nearest = tree.find(p)
        width = FIELD_WIDTH + FIELD_GROWTH * nearest
        found = tree.find_range(p, nearest + 3.0 * width)
        index = np.array([f[1] for f in found], np.int64)
        weight = np.exp(-((np.array([f[2] for f in found]) - nearest) / width) ** 2)
        moved[i] = p + (weight[:, None] * shift[index]).sum(axis=0) / weight.sum()
    return moved


def _points(co: np.ndarray, polygons: list[tuple[int, ...]]) -> np.ndarray:
    """Every vertex and the centre of every face, the points a band holds for."""
    return np.concatenate([co, np.array([co[list(p)].mean(axis=0) for p in polygons]).reshape(-1, 3)])


def _loose(mesh: bpy.types.Mesh) -> np.ndarray:
    """Which vertices belong to loose parts, which hang on the cloth chains instead of the cage."""
    values = _attribute(mesh, hang.HANG, "INT")
    return values > 0 if values is not None else np.zeros(len(mesh.vertices), bool)


def limits(recipe, lines: list[dict], label: str, co: np.ndarray, polygons: list[tuple[int, ...]], band: str,
           loose: np.ndarray, turned: list[tuple[int, int, int]] | None = None, hem: float = 0.0,
           hems=None) -> tuple[np.ndarray, np.ndarray | None]:
    """The band of every vertex and face centre of a template on a body, as band_limits gives it,
    and none for the vertices of loose parts and the faces that touch them, which hang off the cage.
    The vertices of turned edges lie inside their cloth, as deep as it lies off the cage at most:
    their least distance is zero. The faces that touch them are flat between points that may lie on
    the cage, so over a round part of it their centres may cut under it, by less than the hem's
    depth. The largest distance of both is their cloth's, taken where the vertex they are turned
    from lies. The cloth vertices they are turned from, hems, or those of turned when hems is None,
    keep at least HEM_ROOM off the cage, short of float32 rounding, so a settle that left one there
    does not move it again."""
    anchor = np.arange(len(co))
    for cloth, rim, flap in turned or []:
        anchor[rim] = anchor[flap] = cloth
    low, high = band_limits(recipe, lines, label, _points(np.asarray(co)[anchor], polygons), band)
    count = len(co) + len(polygons)
    hems = [cloth for cloth, _rim, _flap in turned or []] if hems is None else list(hems)
    if hems:
        low = np.array(np.broadcast_to(np.asarray(low, float), (count,)))
        low[hems] = np.maximum(low[hems], HEM_ROOM - ROUNDING)
    if turned:
        inside = turned_mask(len(co), turned)
        faces = np.array([inside[list(p)].any() for p in polygons], bool)
        lowest = np.concatenate([np.zeros(len(co)), np.full(len(polygons), -hem)])
        low = np.where(np.concatenate([inside, faces]), lowest, np.broadcast_to(np.asarray(low, float), (count,)))
    if not loose.any():
        return low, high
    free = np.concatenate([loose, np.array([loose[list(p)].any() for p in polygons], bool)])
    low = np.where(free, -np.inf, np.broadcast_to(np.asarray(low, float), free.shape))
    high = np.where(free, np.inf, np.broadcast_to(np.asarray(np.inf if high is None else high, float), free.shape))
    return low, high


def _triangles(mesh: bpy.types.Mesh) -> list[tuple[int, int, int, int]]:
    """Every polygon as a fan from its first corner: (polygon, a, b, c). The order depends only on
    the topology, so meshes of the same topology give the same triangles."""
    found = []
    for polygon in mesh.polygons:
        corners = list(polygon.vertices)
        for i in range(1, len(corners) - 1):
            found.append((polygon.index, corners[0], corners[i], corners[i + 1]))
    return found


def quad_bend(corners) -> float:
    """How far a quad folds outward across its diagonal from its first corner to its third: the
    distance between its two diagonals, positive when the two triangles of that split make a ridge
    toward the quad's normal, negative when they make a valley; the other diagonal folds the other
    way by as much. A flat quad bends by nothing."""
    p0, p1, p2, p3 = (np.asarray(c, float) for c in corners)
    across = np.cross(p2 - p0, p3 - p1)
    length = float(np.linalg.norm(across))
    if length < 1e-30:
        return 0.0
    return float(-np.dot(p1 - p0, np.cross(p2 - p0, p3 - p0)) / length)


def mirror_faces(co, polygons: list[tuple[int, ...]], tolerance: float = MIRROR_MATCH) -> list:
    """For every face its mirror image across the middle plane, x = 0, as the face and, per corner of
    the face, the corner of the image it mirrors to, every corner within the tolerance; None for a
    face without one. A face in the middle plane may be its own image."""
    co = np.asarray(co, float)
    flip = np.array([-1.0, 1.0, 1.0])
    centres = np.array([co[list(p)].mean(axis=0) for p in polygons]).reshape(-1, 3)
    grid: dict[tuple[int, ...], list[int]] = {}
    for face, key in enumerate(np.floor(centres / tolerance).astype(np.int64)):
        grid.setdefault(tuple(int(k) for k in key), []).append(face)
    found = []
    for face, polygon in enumerate(polygons):
        target = centres[face] * flip
        base = np.floor(target / tolerance).astype(np.int64)
        match = None
        for offset in np.ndindex(3, 3, 3):
            for other in grid.get(tuple(int(b + o - 1) for b, o in zip(base, offset)), []):
                if match is not None or len(polygons[other]) != len(polygon) or \
                        np.abs(centres[other] - target).max() > tolerance:
                    continue
                corners = co[list(polygons[other])]
                mapping = []
                for vertex in polygon:
                    distances = np.linalg.norm(corners - co[vertex] * flip, axis=1)
                    nearest = int(np.argmin(distances))
                    if distances[nearest] > tolerance:
                        break
                    mapping.append(nearest)
                if len(mapping) == len(polygon) and len(set(mapping)) == len(mapping):
                    match = (other, mapping)
        found.append(match)
    return found


def _diagonal_key(a: np.ndarray, b: np.ndarray) -> tuple[float, ...]:
    """What decides between two diagonals of the same length, by their geometry alone and alike on
    both sides of the middle plane: the steeper one, then the one whose lower end lies farther from
    the middle, lower, and farther forward, and then the same of its upper end. Two diagonals with
    the same key are mirror images of each other across the middle plane."""
    length = max(float(np.linalg.norm(b - a)), 1e-30)
    lower, upper = sorted((a, b), key=lambda p: (float(p[2]), -abs(float(p[0])), -float(p[1])))
    return (round(abs(float(a[2] - b[2])) / length, 9),
            *(value for end in (lower, upper) for value in (round(abs(float(end[0])), 9), round(-float(end[2]), 9),
                                                            round(float(end[1]), 9))))


def _diagonals(co: np.ndarray, polygon: tuple[int, ...]) -> tuple[float, list[float], list[tuple[float, ...]]]:
    """A quad's bend across its first diagonal, and per diagonal, first to third corner and second
    to fourth, its length and its key."""
    p = co[list(polygon)]
    return (quad_bend(p), [float(np.linalg.norm(p[2] - p[0])), float(np.linalg.norm(p[3] - p[1]))],
            [_diagonal_key(p[0], p[2]), _diagonal_key(p[1], p[3])])


def split_starts(co, polygons: list[tuple[int, ...]]) -> list[int]:
    """The corner every face starts from, so that a quad splits from it to its third corner across
    the diagonal it folds outward across, 0 for its first diagonal and 1 for its second, and every
    other face from its first corner. A quad and its mirror image across the middle plane are decided
    together, from both their bends, so they split along mirrored diagonals. A flat quad, one that
    bends by less than FLAT_BEND, splits along its shorter diagonal, and two of the same length by
    their geometry."""
    co = np.asarray(co, float)
    mirrors = mirror_faces(co, polygons)
    starts = [0] * len(polygons)
    decided = [False] * len(polygons)
    for face, polygon in enumerate(polygons):
        if len(polygon) != 4 or decided[face]:
            continue
        bend, lengths, keys = _diagonals(co, polygon)
        pair = mirrors[face]
        other, same, count = None, True, 1
        if pair is not None and pair[0] != face:
            other, mapping = pair
            # Whether the image's first diagonal mirrors this one's first diagonal.
            same = mapping[0] % 2 == 0
            other_bend, other_lengths, other_keys = _diagonals(co, polygons[other])
            if not same:
                other_bend, other_lengths, other_keys = -other_bend, other_lengths[::-1], other_keys[::-1]
            bend += other_bend
            lengths = [a + b for a, b in zip(lengths, other_lengths)]
            keys = [tuple(x + y for x, y in zip(a, b)) for a, b in zip(keys, other_keys)]
            count = 2
        if bend / count > FLAT_BEND:
            diagonal = 0
        elif bend / count < -FLAT_BEND:
            diagonal = 1
        elif abs(lengths[0] - lengths[1]) > 1e-9:
            diagonal = 0 if lengths[0] < lengths[1] else 1
        else:
            diagonal = 0 if keys[0] >= keys[1] else 1
        starts[face], decided[face] = diagonal, True
        if other is not None:
            starts[other], decided[other] = (diagonal if same else 1 - diagonal), True
    return starts


def mirror_split_error(version: str, co, polygons: list[tuple[int, ...]]) -> str | None:
    """The rule of a recipe template's quads: a quad and its mirror image across the middle plane
    split along mirrored diagonals, so the cloth folds alike on both sides of the body."""
    pairs, wrong = mirror_split_faces(co, polygons)
    if not wrong:
        return None
    centre = np.asarray(co, float)[list(polygons[wrong[0]])].mean(axis=0)
    return (f'"{version}" splits {len(wrong)} of its {pairs} pairs of mirrored quads along diagonals that are not '
            f"mirror images of each other, such as the face {wrong[0]} at ({', '.join(f'{x:.3f}' for x in centre)}) m; "
            "build it again with tools/art/Sync-GarmentAsset.ps1 -Rebuild.")


def mirror_split_faces(co, polygons: list[tuple[int, ...]]) -> tuple[int, list[int]]:
    """How many pairs of mirrored quads a mesh has, and the first face of every pair whose splits,
    each from its first corner to its third, are not mirror images of each other. A quad that is its
    own mirror image, across the middle plane, is no pair: its diagonals are each their own image or
    each other's, and in the second case no split of it is mirrored."""
    pairs, wrong = 0, []
    for face, pair in enumerate(mirror_faces(co, polygons)):
        if pair is None or len(polygons[face]) != 4 or pair[0] <= face:
            continue
        pairs += 1
        if pair[1][0] % 2:
            wrong.append(face)
    return pairs, wrong


# The components and the property of a corner attribute's data, by its type.
_CORNER_DATA = {"FLOAT": ("value", 1, np.float32), "INT": ("value", 1, np.int32), "BOOLEAN": ("value", 1, bool),
                "INT8": ("value", 1, np.int32), "FLOAT2": ("vector", 2, np.float32),
                "FLOAT_VECTOR": ("vector", 3, np.float32), "FLOAT_COLOR": ("color", 4, np.float32),
                "BYTE_COLOR": ("color", 4, np.float32)}


def _turn_corners(mesh: bpy.types.Mesh, starts: list[int]) -> None:
    """Turns the corners of every face to start from the given corner, with the data of its corners,
    their UVs among them, so the face, its winding, and its look stay as they were."""
    if not any(starts):
        return
    order = np.arange(len(mesh.loops))
    for polygon, start in zip(mesh.polygons, starts):
        if start:
            first, total = polygon.loop_start, polygon.loop_total
            order[first:first + total] = first + (np.arange(total) + start) % total
    names = {attribute.name for attribute in mesh.attributes}
    if ".corner_vert" not in names:
        for name in ("vertex_index", "edge_index"):
            values = np.empty(len(mesh.loops), np.int32)
            mesh.loops.foreach_get(name, values)
            mesh.loops.foreach_set(name, values[order])
    for attribute in [a for a in mesh.attributes if a.domain == "CORNER"]:
        if attribute.data_type not in _CORNER_DATA:
            raise ArtPipelineError(f'The corner attribute "{attribute.name}" of {attribute.data_type} cannot be turned.')
        key, size, kind = _CORNER_DATA[attribute.data_type]
        values = np.empty(len(mesh.loops) * size, kind)
        attribute.data.foreach_get(key, values)
        attribute.data.foreach_set(key, values.reshape(len(mesh.loops), size)[order].reshape(-1))
    mesh.update()


class Surface:
    """A mesh's surface for fitting, such as a body or a cage: its triangles, their frames, and the
    mesh's weights over the given bones, or over its own vertex groups. A cage's surface may hold
    only the faces of some of its regions, those recipe_regions gives a template."""

    def __init__(self, obj: bpy.types.Object, names: list[str] | None = None, regions: tuple[str, ...] | None = None):
        mesh = obj.data
        self.co = bodies.world_positions(obj)
        self.triangles = _triangles(mesh)
        if regions is not None:
            values = cage.face_ints(mesh, "region", -1)
            wanted = {cage.REGIONS.index(r) for r in regions}
            self.triangles = [t for t in self.triangles if values[t[0]] in wanted]
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

    def straight_distance(self, point: Vector) -> float:
        """The distance to the nearest point of the surface, negative on the inner side of its face.
        In front of a sharp edge of the surface, such as the cage's nose, the nearest point lies on
        the edge, and the distance along either face's normal falls far short of this one."""
        found, normal, _triangle, distance = self.tree.find_nearest(point)
        if found is None:
            return float("inf")
        return distance if (point - found).dot(normal) >= 0.0 else -distance


class RegionSkin:
    """A body's skin with its weights, split by the regions and sides of the cage, so cloth takes the
    weights of the body part it lies on: a sleeve's inside at the armpit those of the arm, not of
    the trunk next to it. A hand's skin is also split by its digits, each finger and the thumb with
    the palm, so a glove's finger moves with its own finger and not with the next one."""

    def __init__(self, body: bpy.types.Object, names: list[str]):
        self.surface = Surface(body, names)
        zones = bodies.zones_of(body)
        digits = cage.face_digits(body)
        centres = np.array([self.surface.co[list(t[1:])].mean(axis=0) for t in self.surface.triangles])
        hands = bodies.ZONES.index("Hands")
        self.trees = {}

        def add(key, keep) -> None:
            found = [i for i, t in enumerate(self.surface.triangles) if keep(t[0], centres[i][0])]
            tree = BVHTree.FromPolygons([tuple(p) for p in self.surface.co], [self.surface.triangles[i][1:] for i in found])
            self.trees[key] = (tree, found)

        for region in cage.REGIONS:
            wanted = {bodies.ZONES.index(z) for z in cage.SKIN_ZONES[region]}
            for side in (("L", "R") if region in cage.SIDED else ("",)):
                add((region, side), lambda p, x, w=wanted, s=side: zones[p] in w and (not s or cage.side_of(x) == s))
        for side in ("L", "R"):
            for digit in range(1, len(cage.DIGITS) + 1):
                add(("hands", side, digit),
                    lambda p, x, s=side, d=digit: zones[p] == hands and cage.side_of(x) == s and digits[p] in (0, d))

    def weights(self, key: tuple, point: Vector) -> np.ndarray:
        """The weights of the nearest skin of a cage region and side, or of a hand's digit with the
        palm when the key also names a digit other than 0."""
        tree, found = self.trees[key if len(key) < 3 or key[2] else key[:2]]
        location, _normal, index, _distance = tree.find_nearest(point)
        if location is None:
            location, _normal, triangle, _distance = self.surface.tree.find_nearest(point)
        else:
            triangle = found[index]
        return self.surface.weights_at(triangle, poly_3d_calc(self.surface.corners(triangle), location))


def cage_keys(cage_obj: bpy.types.Object, surface: Surface) -> list[tuple[str, str, int]]:
    """The region, side, and digit of every triangle of a cage's surface."""
    regions = cage.face_ints(cage_obj.data, "region", -1)
    digits = cage.face_ints(cage_obj.data, cage.DIGIT, 0)
    keys = []
    for polygon, a, b, c in surface.triangles:
        region = cage.REGIONS[int(regions[polygon])]
        x = surface.co[[a, b, c]].mean(axis=0)[0]
        keys.append((region, cage.side_of(x) if region in cage.SIDED else "", int(digits[polygon])))
    return keys


def _settle(positions: np.ndarray, polygons: list[tuple[int, ...]], surface: Surface, low, high) -> np.ndarray:
    """The positions with every vertex, and the corners of every face whose centre, that lies closer
    to the surface than low or farther than high moved along the surface's normal there into the
    band, so the whole surface keeps it, not only its corners. low and high hold for every point, or
    per vertex and then per face, as band_limits gives them."""
    co = positions.copy()
    points = [(i,) for i in range(len(co))] + list(polygons)
    lows = np.broadcast_to(np.asarray(low, float), (len(points),))
    highs = None if high is None else np.broadcast_to(np.asarray(high, float), (len(points),))
    for _ in range(SETTLE_PASSES):
        settled = True
        for index, polygon in enumerate(points):
            corners = list(polygon)
            distance, normal, _p = surface.nearest(Vector(co[corners].mean(axis=0)))
            if distance < lows[index]:
                co[corners] += np.array(normal) * (lows[index] + SETTLE_MARGIN - distance)
                settled = False
            elif highs is not None and distance > highs[index]:
                co[corners] -= np.array(normal) * (distance - highs[index] + SETTLE_MARGIN)
                settled = False
        if settled:
            return co
    # Where the cage's normals point against each other, such as across the crotch between the
    # thighs, a push along the nearest one moves the point toward the other. Such a point moves along
    # the mean of the cage's normals around it instead, which points away from both, by at most
    # SETTLE_REACH in all.
    moved = np.zeros(len(points))
    for _ in range(SETTLE_PASSES * 2):
        settled = True
        for index, polygon in enumerate(points):
            corners = list(polygon)
            centre = Vector(co[corners].mean(axis=0))
            distance, _normal, _p = surface.nearest(centre)
            if distance < lows[index] and moved[index] < SETTLE_REACH:
                around = surface.tree.find_nearest_range(centre, SETTLE_AROUND)
                mean = sum((n for _c, n, _i, _d in around), Vector())
                if mean.length < 1e-6:
                    continue
                step = min(lows[index] + SETTLE_MARGIN - distance, SETTLE_REACH - moved[index])
                co[corners] += np.array(mean.normalized()) * step
                moved[index] += step
                settled = False
        if settled:
            break
    return co


def _settle_band(co: np.ndarray, polygons: list[tuple[int, ...]], surface: Surface, recipe, lines: list[dict],
                 label: str, band: str, loose: np.ndarray, hems=()) -> np.ndarray:
    """The positions settled into the band that limits gives where the points end up: a point that
    moves may come to lie over another region of the cage, where the template lies over an inner
    garment or does not, so the band is taken again until it holds. loose marks the vertices it
    leaves where they are, with the faces that touch them: those of loose parts, and the rims and
    flaps of turned edges, which are laid from the settled cloth. hems are the cloth vertices the
    turned edges are turned from, which keep HEM_ROOM off the cage."""
    for _ in range(SETTLE_PASSES):
        settled = _settle(co, polygons, surface, *limits(recipe, lines, label, co, polygons, band, loose, hems=hems))
        if np.array_equal(settled, co):
            break
        co = settled
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


def band_fade(values, ranges) -> np.ndarray:
    """1 within the first of two ranges, 0 outside the second, which holds the first, and a smooth
    step between them, per value."""
    (low, high), (lowest, highest) = ranges
    values = np.asarray(values, float)
    rise = np.clip((values - lowest) / max(low - lowest, 1e-12), 0.0, 1.0)
    fall = np.clip((highest - values) / max(highest - high, 1e-12), 0.0, 1.0)
    t = np.minimum(rise, fall)
    return t * t * (3.0 - 2.0 * t)


def crotch_point(co, middle, hip_depth: float, scale: float) -> np.ndarray:
    """A body's crotch: of the vertices of its middle line under the trunk, given by index, the lowest
    within CROTCH_SEARCH of the hip joints' depth, at the body's scale."""
    co, middle = np.asarray(co, float), np.asarray(middle, np.int64)
    near = middle[np.abs(co[middle, 1] - hip_depth) <= CROTCH_SEARCH * scale]
    if not len(near):
        raise ArtPipelineError("The body has no middle line under its trunk near the depth of its hip joints.")
    return co[near[np.argmin(co[near, 2])]]


def crotch_hold(points, crotch, scale: float, held: float = rig.CROTCH_MIDDLE_REBUILT[0],
                full: float = rig.CROTCH_RADIUS) -> np.ndarray:
    """How much of the thighs' share of each point's weights moves to the pelvis by the crotch rule:
    by the point's distance across the body from the middle plane, wholly within held and not at all
    from full on, the skin's own distances, within the band about the crotch and fading out with it.
    The lengths are at the rig's reference height and scale with the body's."""
    points = np.asarray(points, float)
    t = np.clip((np.abs(points[:, 0]) - held * scale) / ((full - held) * scale), 0.0, 1.0)
    across = 1.0 - t * t * (3.0 - 2.0 * t)
    offset = (points - np.asarray(crotch, float)) / scale
    return across * band_fade(offset[:, 1], CROTCH_BAND_DEPTH) * band_fade(offset[:, 2], CROTCH_BAND_HEIGHT)


def evened_hold(hold, polygons: list[tuple[int, ...]], times: int = CROTCH_EVENING) -> np.ndarray:
    """The hold evened out over the edges of the faces this many times, each time halfway to the mean
    of every vertex's neighbours, as the rig evens the skin's weights at the crotch."""
    evened = np.array(hold, float)
    if not np.any(evened > 0.0):
        return evened
    return rig.evened_out(evened, np.arange(len(evened)), polygons, times)


def held_weights(weights, hold, legs: list[int], pelvis: int) -> np.ndarray:
    """The weights with the share hold of each row's leg bones moved to its other bones, in their
    proportions, or to the pelvis where it has none, as the skin's crotch rule moves them, and
    limited to four bones again."""
    out = np.array(weights, float)
    hold = np.asarray(hold, float)
    for row in np.nonzero(hold > 0.0)[0]:
        taken = out[row, legs].sum() * hold[row]
        if taken <= 0.0:
            continue
        out[row, legs] *= 1.0 - hold[row]
        rest = out[row].copy()
        rest[legs] = 0.0
        total = rest.sum()
        if total > 1e-9:
            out[row] += rest / total * taken
        else:
            out[row, pelvis] += taken
        out[row] = _limited(out[row])
    return out


def _held_at_crotch(weights: np.ndarray, positions: np.ndarray, polygons: list[tuple[int, ...]], arm: bpy.types.Object,
                    names: list[str], fixed: np.ndarray) -> np.ndarray:
    """The weights of a version on the arm's body with the crotch rule applied to its fixed cloth,
    the vertices marked fixed, at the positions they have there, its hold evened over its faces."""
    body = bodies.source_body(arm.name)
    co = bodies.world_positions(body)
    zones = bodies.zones_of(body)
    pelvis = bodies.ZONES.index("Pelvis")
    under = np.unique([v for p in body.data.polygons if zones[p.index] == pelvis for v in p.vertices])
    middle = under[np.abs(co[under, 0]) < MIDDLE_PLANE]
    scale = float(co[:, 2].max()) / rig.REFERENCE_HEIGHT
    hip = arm.matrix_world @ arm.data.bones["UpperLeg_R"].head_local
    hold = evened_hold(crotch_hold(positions, crotch_point(co, middle, hip.y, scale), scale), polygons)
    hold = np.where(fixed, hold, 0.0)
    if not np.any(hold > 0.0):
        return weights
    legs = [names.index(f"{bone}_{side}") for bone in ("UpperLeg", "LowerLeg") for side in ("L", "R")]
    return _rounded_weights(held_weights(weights, hold, legs, names.index("Hips")))


def under_positions(obj: bpy.types.Object) -> np.ndarray | None:
    """The world positions of a version's shape key UnderHeadwear, or None when it has none."""
    keys = obj.data.shape_keys
    block = keys.key_blocks.get(UNDER_HEADWEAR) if keys is not None else None
    if block is None:
        return None
    co = np.empty(len(obj.data.vertices) * 3)
    block.data.foreach_get("co", co)
    m = np.array(obj.matrix_world)
    return co.reshape(-1, 3) @ m[:3, :3].T + m[:3, 3]


def _match_shape_keys(first: bpy.types.Object, target: bpy.types.Object) -> None:
    """Gives a fitted version the shape keys of the first version, by name, and no others."""
    wanted = [k.name for k in first.data.shape_keys.key_blocks] if first.data.shape_keys is not None else []
    have = [k.name for k in target.data.shape_keys.key_blocks] if target.data.shape_keys is not None else []
    if have == wanted:
        return
    if target.data.shape_keys is not None:
        target.shape_key_clear()
    for index, name in enumerate(wanted):
        block = target.shape_key_add(name=name, from_mix=False)
        if index:
            block.relative_key = target.data.shape_keys.key_blocks[0]


def _set_shape(obj: bpy.types.Object, positions: np.ndarray, under: np.ndarray | None = None) -> None:
    """Sets a version's positions, which its armature at the origin keeps in world space, with its
    basis, and the shape key UnderHeadwear when it has one."""
    obj.data.vertices.foreach_set("co", np.asarray(positions, float).reshape(-1))
    keys = obj.data.shape_keys
    if keys is not None:
        keys.key_blocks[0].data.foreach_set("co", np.asarray(positions, float).reshape(-1))
        if under is not None and UNDER_HEADWEAR in keys.key_blocks:
            keys.key_blocks[UNDER_HEADWEAR].data.foreach_set("co", np.asarray(under, float).reshape(-1))
    obj.data.update()


def append_cages(cage_file: str, labels: list[str], cage_content: str) -> None:
    """The cage's version on every body, taken from the cage's file into the Cage collection,
    replacing earlier ones, and the cage's content fingerprint kept on the scene."""
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
            raise ArtPipelineError(f"{cage_file} has no cage versions {missing}; run tools/art/Sync-CageAsset.ps1.")
        target.meshes = wanted
    collection = body_files.collection(CAGE_COLLECTION)
    for mesh in target.meshes:
        mesh.name = mesh.name.split(".")[0]
        obj = bpy.data.objects.new(mesh.name, mesh)
        collection.objects.link(obj)
        obj.hide_select = True
        obj.hide_render = True
        obj.hide_set(True)
    bpy.context.scene[CAGE_CONTENT] = cage_content


PAIR_COLLECTION = "Pairs"


def append_inner(template_file: str, template: str, labels: list[str], compressed: bool = False) -> list[bpy.types.Object]:
    """The versions of another template, of a lower layer, from its own file, put on the bodies of
    this file for the pair check and the review sheet: each skinned to the armature of its body
    here, in the collection Pairs, which a sync never saves because it only appears in a check. A
    hairstyle under headwear that compresses the hair takes its form UnderHeadwear."""
    wanted = [version_name(template, label) for label in labels]
    with bpy.data.libraries.load(template_file, link=False) as (source, target):
        missing = [n for n in wanted if n not in source.objects]
        if missing:
            raise ArtPipelineError(f"{template_file} has no versions {missing}.")
        target.objects = wanted
    collection = bpy.data.collections.get(PAIR_COLLECTION) or bpy.data.collections.new(PAIR_COLLECTION)
    if collection.name not in bpy.context.scene.collection.children:
        bpy.context.scene.collection.children.link(collection)
    found = []
    for obj, label in zip(target.objects, labels):
        collection.objects.link(obj)
        obj.parent = bpy.data.objects[label]
        obj.matrix_parent_inverse = Matrix.Identity(4)
        bodies.bind(obj, bpy.data.objects[label])
        keys = obj.data.shape_keys
        if compressed and keys is not None and UNDER_HEADWEAR in keys.key_blocks:
            keys.key_blocks[UNDER_HEADWEAR].value = 1.0
        found.append(obj)
    return found


def append_lain_over(pairs: list[tuple[str, str, str]], labels: list[str]) -> None:
    """The templates of lower layers a template is worn over, put on the bodies of its file, so its
    cloth clears their real surface where its recipe says it lies over one."""
    for path, inner, _band in pairs:
        append_inner(path, inner, labels)


def remove_inner() -> None:
    """Takes the templates of lower layers out of the file again, with everything that came with
    them, before a sync saves it."""
    collection = bpy.data.collections.get(PAIR_COLLECTION)
    if collection is None:
        return
    for obj in list(collection.objects):
        bpy.data.objects.remove(obj, do_unlink=True)
    bpy.data.collections.remove(collection)
    bpy.data.orphans_purge(do_recursive=True)


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


def _by_index(elements) -> list:
    """BMesh elements once each, in the order of their indices, so a build goes over them the same way
    every time and gives its new elements the same order: a set of them goes by memory address. The
    caller updates the indices first, since a new element's index is -1 until then."""
    return sorted(set(elements), key=lambda element: element.index)


def _index_update(bm: bmesh.types.BMesh) -> None:
    bm.verts.index_update()
    bm.edges.index_update()
    bm.faces.index_update()


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
            _index_update(bm)
            verts = _by_index(v for f in faces for v in f.verts)
            # Moving along the normal changes no point's distance to the side, so a snapped vertex
            # lies on a rising line's curve too.
            for v in verts:
                distance = float(cage.line_offsets(line, point, normal, np.array(v.co))[()])
                if abs(distance) < LINE_SNAP:
                    v.co -= normal * distance
            if line.get("rise"):
                _cut_curved(bm, faces, line, point, normal, cut["keep"])
                continue
            geom = _by_index(e for f in faces for e in f.edges) + verts + faces
            bmesh.ops.bisect_plane(bm, geom=geom, dist=1e-6, plane_co=point, plane_no=normal,
                                   clear_outer=cut["keep"] == "head", clear_inner=cut["keep"] == "tail")
    # A part across the back with a top trims the template to a yoke it hangs from.
    for part in hang.parts(recipe):
        if part.get("top") is not None:
            _side, point, normal = cage.line_planes(arm.name, by_name[part["line"]])[0]
            hang.trim(bm, part, arm, point, normal)
    loose = [v for v in bm.verts if not v.link_faces]
    bmesh.ops.delete(bm, geom=loose, context="VERTS")
    bm.normal_update()
    return bm


def _split(bm: bmesh.types.BMesh, recipe: dict, lines: list[dict], arm: bpy.types.Object) -> None:
    """Edge loops at the recipe's splits, each a standard line moved by its offset, through the faces
    of the line's region and side without cutting the template there, so a template of a higher
    layer that ends at that line hides the cloth beyond it face by face. A split runs through a vertex
    within SPLIT_SNAP of its plane and moves none, and the UVs of its new vertices are interpolated
    along the edges they cut, so a texture keeps its place."""
    region_layer = bm.faces.layers.int.get("region")
    for line, side, point, normal in split_planes(recipe, lines, arm.name):
        region = cage.REGIONS.index(line["region"])
        faces = [f for f in bm.faces if f[region_layer] == region and (not side or cage.side_of(f.calc_center_median().x) == side)]
        if not faces:
            continue
        _index_update(bm)
        if line.get("rise"):
            _cut_curved(bm, faces, line, point, normal, None, SPLIT_SNAP)
            continue
        geom = _by_index(e for f in faces for e in f.edges) + _by_index(v for f in faces for v in f.verts) + faces
        bmesh.ops.bisect_plane(bm, geom=geom, dist=SPLIT_SNAP, plane_co=point, plane_no=normal)


def _cut_curved(bm: bmesh.types.BMesh, faces: list, line: dict, point: Vector, normal: Vector, keep: str | None,
                near: float = 1e-6) -> None:
    """Cuts faces at a curved standard line, as bisect_plane cuts at a plane: every edge that crosses
    the line is split where it does, every face crossed joins its two points on the line, and the
    faces on the side the cut does not keep go; with no side to keep, both stay. A vertex this near
    the line counts as on it."""
    def offset(co: Vector) -> float:
        return float(cage.line_offsets(line, point, normal, np.array(co))[()])

    on = {v for f in faces for v in f.verts if abs(offset(v.co)) <= near}
    _index_update(bm)
    for edge in _by_index(e for f in faces for e in f.edges):
        a, b = edge.verts
        da, db = offset(a.co), offset(b.co)
        if abs(da) > near and abs(db) > near and (da < 0.0) != (db < 0.0):
            _edge, vert = bmesh.utils.edge_split(edge, a, da / (da - db))
            on.add(vert)
    # The faces keep their identity through the edge splits; a face split adds the other half.
    pieces = list(faces)
    for face in faces:
        corners = [v for v in face.verts if v in on]
        if len(corners) == 2 and not any(set(e.verts) == set(corners) for e in face.edges):
            other, _edge = bmesh.utils.face_split(face, corners[0], corners[1])
            pieces.append(other)
    if keep is None:
        return
    beyond = [f for f in pieces if (offset(f.calc_center_median()) > 0.0) == (keep == "head")]
    bmesh.ops.delete(bm, geom=beyond, context="FACES")


def _palm_normal(arm: bpy.types.Object, side: str) -> Vector:
    """The direction a hand's palm faces at rest: at right angles to the hand's bone and to the line
    from the index finger's base to the little finger's, on the side of the hand the thumb lies on."""
    world = arm.matrix_world
    hand = arm.data.bones[f"Hand_{side}"]
    along = (world @ hand.tail_local) - (world @ hand.head_local)
    across = (world @ arm.data.bones[f"LittleProximal_{side}"].head_local) - (world @ arm.data.bones[f"IndexProximal_{side}"].head_local)
    normal = along.cross(across).normalized()
    # The bones mirror, so the cross product turns the other way on the left hand.
    return -normal if side == "L" else normal


def _mark_seams(bm: bmesh.types.BMesh, arm: bpy.types.Object) -> None:
    """Seams where clothes have them: between the front and the back of the trunk and the neck,
    down the sides and over the shoulders, along the underside of each arm, along the inside of each
    leg, where an arm or a leg meets the trunk, and where a hand or a foot meets its limb; a glove's
    between its palm and its back, along the sides of the fingers, and a boot's around its sole and
    up the back of its heel. A neckline runs on from the trunk up the neck without a seam."""
    region_layer = bm.faces.layers.int.get("region")
    trunk, neck, arms, legs, hands, feet = (cage.REGIONS.index(r) for r in ("trunk", "neck", "arms", "legs", "hands", "feet"))
    middle = float(np.mean([v.co.y for v in bm.verts]))
    palms = {side: _palm_normal(arm, side) for side in ("L", "R")}

    def palm(face) -> bool:
        return face.normal.dot(palms[cage.side_of(face.calc_center_median().x)]) > 0.0

    def sole(face) -> bool:
        return face.normal.z < -SOLE_FACING

    def heel(face) -> float:
        """Across the foot behind its ankle, on which side of the foot's middle the face lies, or 0
        in front of the ankle."""
        side = cage.side_of(face.calc_center_median().x)
        joint = arm.matrix_world @ arm.data.bones[f"LowerLeg_{side}"].tail_local
        forward = (arm.matrix_world @ arm.data.bones[f"Toes_{side}"].tail_local) - joint
        forward.z = 0.0
        forward.normalize()
        offset = face.calc_center_median() - joint
        if offset.dot(forward) >= 0.0:
            return 0.0
        return 1.0 if offset.dot(forward.cross(Vector((0.0, 0.0, 1.0)))) > 0.0 else -1.0

    def angle(face, bone_name: str, seam_side: Vector) -> float:
        # Around a limb's bone, measured so it wraps where the seam runs.
        bone = arm.data.bones[bone_name]
        head, tail = arm.matrix_world @ bone.head_local, arm.matrix_world @ bone.tail_local
        axis = (tail - head).normalized()
        toward = (seam_side - axis * seam_side.dot(axis)).normalized()
        across = axis.cross(toward)
        offset = face.calc_center_median() - head
        return math.atan2(offset.dot(across), -offset.dot(toward))

    def limb_angle(face, region: int) -> float:
        side = cage.side_of(face.calc_center_median().x)
        if region == arms:
            # Under the upper arm, around its bone, and on to the inside of the forearm, around the
            # forearm's bone, which hangs down too far for "under" to name a side of it.
            upper = arm.data.bones[f"UpperArm_{side}"]
            head, tail = arm.matrix_world @ upper.head_local, arm.matrix_world @ upper.tail_local
            if (face.calc_center_median() - tail).dot((tail - head).normalized()) <= 0.0:
                return angle(face, f"UpperArm_{side}", Vector((0.0, 0.0, -1.0)))
            inward = Vector((1.0 if face.calc_center_median().x < 0.0 else -1.0, 0.0, -1.0))
            return angle(face, f"LowerArm_{side}", inward)
        # On the inside of the leg, toward the other one, around the bone of the thigh or the shin.
        knee = arm.matrix_world @ arm.data.bones[f"LowerLeg_{side}"].head_local
        bone = f"UpperLeg_{side}" if face.calc_center_median().z > knee.z else f"LowerLeg_{side}"
        inward = Vector((-1.0 if face.calc_center_median().x > 0.0 else 1.0, 0.0, 0.0))
        return angle(face, bone, inward)

    for edge in bm.edges:
        faces = list(edge.link_faces)
        if len(faces) != 2:
            continue
        a, b = faces
        ra, rb = a[region_layer], b[region_layer]
        if ra != rb and {ra, rb} != {trunk, neck}:
            edge.seam = True
        elif ra in (trunk, neck):
            edge.seam = (a.calc_center_median().y < middle) != (b.calc_center_median().y < middle)
        elif ra in (arms, legs):
            ta, tb = limb_angle(a, ra), limb_angle(b, ra)
            edge.seam = (ta < 0.0) != (tb < 0.0) and abs(ta) > math.pi / 2 and abs(tb) > math.pi / 2
        elif ra == hands:
            edge.seam = palm(a) != palm(b)
        elif ra == feet:
            ha, hb = heel(a), heel(b)
            edge.seam = sole(a) != sole(b) or (not sole(a) and ha * hb < 0.0)


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


def _turn_edges(mesh: bpy.types.Mesh) -> None:
    """Every open edge turned inward: a rim toward the body and a flap back inside the cloth, with
    the UVs of the edge, so a hem shows the cloth's thickness. Their vertices start where the edge
    lies; lay_turned lays them. The edge of a loose part turns with it: its rim and flap belong to
    the part."""
    bm = bmesh.new()
    bm.from_mesh(mesh)
    _index_update(bm)
    uv = bm.loops.layers.uv.active
    loose = bm.verts.layers.int.get(hang.HANG)
    loose_faces = bm.faces.layers.int.get(hang.HANG_FACE)
    border = [e for e in bm.edges if e.is_boundary]
    rim, flap = {}, {}
    for v in _by_index(v for e in border for v in e.verts):
        rim[v] = bm.verts.new(v.co)
        flap[v] = bm.verts.new(v.co)
        if loose is not None:
            rim[v][loose] = flap[v][loose] = max(v[loose], 0)
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
            if loose_faces is not None:
                new[loose_faces] = face[loose_faces]
            for loop in new.loops:
                source = a if loop.vert in (a, rim[a], flap[a]) else b
                loop[uv].uv = loops[source][uv].uv
    bm.to_mesh(mesh)
    bm.free()


def turned_edges(polygons: list[tuple[int, ...]]) -> list[tuple[int, int, int]]:
    """The turned edges of a template's mesh, read from its faces alone, so every version, a rebuild,
    and an export find the same: per vertex of the cloth's open edge, that vertex, its rim, and the
    top of its flap, in the order of the cloth's vertices. _turn_edges makes the tops of the flaps the
    border of the mesh: each joins its rim off the border, and the rim joins the cloth's vertex, the
    one of its neighbors that is neither a rim nor a flap."""
    count: dict[tuple[int, int], int] = {}
    neighbors: dict[int, set[int]] = {}
    for polygon in polygons:
        for i, a in enumerate(polygon):
            b = polygon[(i + 1) % len(polygon)]
            key = (a, b) if a < b else (b, a)
            count[key] = count.get(key, 0) + 1
            neighbors.setdefault(a, set()).add(b)
            neighbors.setdefault(b, set()).add(a)
    border = {key for key, n in count.items() if n == 1}
    flaps = {v for key in border for v in key}
    wrong = ArtPipelineError("The border of the template is not the turned edge its recipe gives it, a rim and a "
                             "flap along every open edge; build it again with tools/art/Sync-GarmentAsset.ps1 -Rebuild.")
    rims = {}
    for flap in sorted(flaps):
        off = [w for w in neighbors[flap] if ((flap, w) if flap < w else (w, flap)) not in border]
        if len(off) != 1:
            raise wrong
        rims[flap] = off[0]
    rim_set = set(rims.values())
    if len(rim_set) != len(rims) or rim_set & flaps:
        raise wrong
    found = []
    for flap, rim in rims.items():
        cloth = [w for w in neighbors[rim] if w not in rim_set and w not in flaps]
        if len(cloth) != 1:
            raise wrong
        found.append((cloth[0], rim, flap))
    if len({hem for hem, _rim, _flap in found}) != len(found):
        raise wrong
    return sorted(found)


def border_vertices(polygons: list[tuple[int, ...]]) -> list[int]:
    """The vertices on the border of a mesh, the edges of only one face, in their order."""
    count: dict[tuple[int, int], int] = {}
    for polygon in polygons:
        for i, a in enumerate(polygon):
            b = polygon[(i + 1) % len(polygon)]
            key = (a, b) if a < b else (b, a)
            count[key] = count.get(key, 0) + 1
    return sorted({v for key, n in count.items() if n == 1 for v in key})


def _turned(recipe, polygons: list[tuple[int, ...]]) -> list[tuple[int, int, int]]:
    """The turned edges of a template whose recipe turns its edges, or none."""
    if not isinstance(recipe, dict) or recipe.get("hem", 0.0) <= 0.0:
        return []
    return turned_edges(polygons)


def turned_mask(count: int, turned: list[tuple[int, int, int]]) -> np.ndarray:
    """Which of a template's vertices are the rims and flaps of its turned edges."""
    mask = np.zeros(count, bool)
    mask[[i for _hem, rim, flap in turned for i in (rim, flap)]] = True
    return mask


def cloth_normals(co: np.ndarray, polygons: list[tuple[int, ...]]) -> np.ndarray:
    """Every vertex's normal over the given faces, as Blender takes it: the faces' normals, by
    Newell's method, which holds for a face that is not flat, weighted by the angle of each face at
    the vertex; zero for a vertex of none of them."""
    co = np.asarray(co, float)
    normals = np.zeros((len(co), 3))
    for polygon in polygons:
        corners = co[list(polygon)]
        following = np.roll(corners, -1, axis=0)
        normal = np.array([np.sum((corners[:, 1] - following[:, 1]) * (corners[:, 2] + following[:, 2])),
                           np.sum((corners[:, 2] - following[:, 2]) * (corners[:, 0] + following[:, 0])),
                           np.sum((corners[:, 0] - following[:, 0]) * (corners[:, 1] + following[:, 1]))])
        length = np.linalg.norm(normal)
        if length < 1e-20:
            continue
        to_next, to_previous = following - corners, np.roll(corners, 1, axis=0) - corners
        sizes = np.linalg.norm(to_next, axis=1) * np.linalg.norm(to_previous, axis=1)
        cosine = np.einsum("ij,ij->i", to_next, to_previous) / np.maximum(sizes, 1e-30)
        np.add.at(normals, list(polygon), (normal / length)[None, :] * np.arccos(np.clip(cosine, -1.0, 1.0))[:, None])
    length = np.linalg.norm(normals, axis=1, keepdims=True)
    return np.divide(normals, length, out=np.zeros_like(normals), where=length > 0.0)


def hem_frames(co: np.ndarray, cloth: list[tuple[int, ...]], hems, normals: np.ndarray) -> np.ndarray:
    """At every vertex of the cloth's open edge, the direction into the cloth across its edge: at
    right angles to the cloth's normal and to the edge, toward the cloth's vertices off the edge, or
    toward the centres of its faces at a corner without such vertices."""
    co = np.asarray(co, float)
    count: dict[tuple[int, int], int] = {}
    neighbors: dict[int, set[int]] = {}
    faces_at: dict[int, list[tuple[int, ...]]] = {}
    for polygon in cloth:
        for i, a in enumerate(polygon):
            b = polygon[(i + 1) % len(polygon)]
            key = (a, b) if a < b else (b, a)
            count[key] = count.get(key, 0) + 1
            neighbors.setdefault(a, set()).add(b)
            neighbors.setdefault(b, set()).add(a)
            faces_at.setdefault(a, []).append(polygon)
    inward = np.zeros((len(hems), 3))
    for k, v in enumerate(hems):
        around = sorted(neighbors[v])
        on_edge = [w for w in around if count[(v, w) if v < w else (w, v)] == 1]
        along = [co[w] - co[v] for w in on_edge]
        tangent = along[0] - along[1] if len(along) == 2 else along[0]
        off = [co[w] - co[v] for w in around if w not in on_edge]
        toward = np.sum(off, axis=0) if off else np.sum([co[list(p)].mean(axis=0) - co[v] for p in faces_at[v]], axis=0)
        direction = np.cross(normals[v], tangent)
        direction /= max(np.linalg.norm(direction), 1e-30)
        inward[k] = -direction if direction @ toward < 0.0 else direction
    return inward


def _lay(co: np.ndarray, polygons: list[tuple[int, ...]], turned: list[tuple[int, int, int]], depth: float,
         shell: Surface) -> tuple[np.ndarray, list[tuple[tuple[int, ...], list[float]]]]:
    """lay_turned, and for every turned edge the cloth face its flap lies under, with the flap's
    barycentric weights on it, which turned_weights takes the flap's skin weights from."""
    if not turned:
        return np.asarray(co), []
    co = np.array(co, float)
    inside = turned_mask(len(co), turned)
    cloth = [p for p in polygons if not inside[list(p)].any()]
    hems = [hem for hem, _rim, _flap in turned]
    normals = cloth_normals(co, cloth)
    inward = hem_frames(co, cloth, hems, normals)
    tree = BVHTree.FromPolygons([tuple(p) for p in co], cloth)
    under = []
    for (hem, rim, flap), direction in zip(turned, inward):
        deep = min(depth, max(shell.straight_distance(Vector(co[hem])), 0.0))
        co[rim] = co[hem] - normals[hem] * deep
        location, _normal, index, _distance = tree.find_nearest(Vector(co[hem] + direction * HEM_FLAP))
        corners = cloth[index]
        shares = poly_3d_calc([Vector(co[v]) for v in corners], location)
        normal = np.sum([normals[v] * w for v, w in zip(corners, shares)], axis=0)
        deep = min(deep, max(shell.straight_distance(location), 0.0))
        co[flap] = np.array(location) - normal / max(np.linalg.norm(normal), 1e-30) * deep
        under.append((tuple(corners), [float(w) for w in shares]))
    return co, under


def lay_turned(co: np.ndarray, polygons: list[tuple[int, ...]], turned: list[tuple[int, int, int]], depth: float,
               shell: Surface) -> np.ndarray:
    """The positions with every turned edge laid from its cloth, which does not move: the rim inside
    the cloth under the vertex it is turned from, along the cloth's normal there, the hem's depth
    deep or as deep as the cloth lies off the cage there if that is less; and the top of the flap
    HEM_FLAP back from the edge across the cloth, at the nearest point of the cloth, the same depth
    inside along the cloth's normal there, or as deep as the cloth lies off the cage there if that
    is less. A point that moves less than its distance from the cage cannot reach it, so no rim and
    no flap lies under the cage."""
    return _lay(co, polygons, turned, depth, shell)[0]


def turned_weights(weights: np.ndarray, turned: list[tuple[int, int, int]], under) -> np.ndarray:
    """The skin weights of a template's vertices with every turned vertex's taken from its cloth, so
    a fold moves with its cloth in every pose: a rim's from the vertex it is turned from, a flap's
    interpolated on the cloth face it lies under, at most four bones adding up to one, as vertex
    groups store them. under holds, per turned edge, that face's corners and the flap's barycentric
    weights on it, as _lay gives them."""
    weights = np.array(weights, float)
    for (cloth, rim, flap), (corners, shares) in zip(turned, under):
        weights[rim] = weights[cloth]
        weights[flap] = _rounded_weights(_limited(np.sum([weights[c] * s for c, s in zip(corners, shares)], axis=0)))
    return weights


def _folds(co: np.ndarray, polygons: list[tuple[int, ...]], turned: list[tuple[int, int, int]]) -> tuple[np.ndarray, np.ndarray]:
    """How deep every rim and every top of a flap lies inside the cloth it is turned from: its
    distance from the nearest point of the cloth, along the normal of the cloth's face there,
    negative outside it."""
    inside = turned_mask(len(co), turned)
    cloth = [p for p in polygons if not inside[list(p)].any()]
    tree = BVHTree.FromPolygons([tuple(p) for p in co], cloth)

    def depth(vertex: int) -> float:
        location, normal, _index, _distance = tree.find_nearest(Vector(co[vertex]))
        return -float((Vector(co[vertex]) - location).dot(normal))

    return (np.array([depth(rim) for _hem, rim, _flap in turned]),
            np.array([depth(flap) for _hem, _rim, flap in turned]))


def fold_error(version: str, rims, flaps, turned: list[tuple[int, int, int]], co: np.ndarray, rooms) -> str | None:
    """The rule of a template's turned edges: no turned vertex lies outside the cloth it is turned
    from, and every rim lies at least MIN_FOLD inside it, so the hem shows the cloth's thickness and
    its faces face the body. rims and flaps hold how deep each lies inside its cloth, and rooms how
    far the cloth vertex each turned edge is turned from lies off the cage, in meters. A turned
    vertex cannot be edited, since every sync lays it from its cloth, so the message names the cloth
    vertex at the thinnest fold, and says when the cloth there lies too close to the cage for one,
    such as a loose part's hem, which no settle holds off the cage."""
    rims, flaps = [float(d) for d in rims], [float(d) for d in flaps]
    failing = sum(1 for d in rims if d < MIN_FOLD) + sum(1 for d in flaps if d < -ROUNDING)
    if not failing:
        return None
    worst = min(range(len(turned)), key=lambda k: min(rims[k], flaps[k]))
    cloth = turned[worst][0]
    noun = "vertex lies" if failing == 1 else "vertices lie"
    text = (f'{failing} turned {noun} outside the cloth of "{version}" or in a fold thinner than '
            f"{MIN_FOLD * 1000:.1f} mm; the thinnest fold is {min(rims[worst], flaps[worst]) * 1000:.2f} mm, at the hem "
            f"vertex {cloth} at ({', '.join(f'{x:.3f}' for x in co[cloth])}) m")
    if rooms[worst] < MIN_FOLD:
        return (text + f", where the cloth lies {float(rooms[worst]) * 1000:.2f} mm off the cage, too close to it for "
                "the fold.")
    return text + "."


def hem_normal_error(version: str, co: np.ndarray, turned: list[tuple[int, int, int]], inward: np.ndarray,
                     outward: np.ndarray, loop_vertices: np.ndarray, corner_normals: np.ndarray) -> str | None:
    """The rule of a template's hems: at every cloth vertex a turned edge is turned from, each corner
    normal the export writes leans out of the cloth, along outward, the cloth's normal, and out of
    the opening, against inward, the direction into the cloth across its edge. A normal that leans
    back into the garment lights the hem's outer edge like a face turned up, a bright strip in the
    game. inward holds one direction per turned edge, outward one per vertex. A normal that lies in
    the cloth's plane, such as where a rim has no depth, which the fold rule reports, keeps it."""
    row = {cloth: k for k, (cloth, _rim, _flap) in enumerate(turned)}
    lean = np.full(len(turned), np.inf)
    for loop, vertex in enumerate(loop_vertices):
        k = row.get(int(vertex))
        if k is not None:
            normal = corner_normals[loop]
            lean[k] = min(lean[k], float(normal @ outward[vertex]), -float(normal @ inward[k]))
    failing = np.nonzero(lean < -LEAN_ROUNDING)[0]
    if not len(failing):
        return None
    worst = int(failing[np.argmin(lean[failing])])
    vertex = turned[worst][0]
    noun = "vertex has" if len(failing) == 1 else "vertices have"
    return (f'{len(failing)} hem {noun} a normal that leans back into "{version}"; the worst, vertex {vertex} at '
            f"({', '.join(f'{x:.3f}' for x in co[vertex])}) m, leans {lean[worst]:+.2f} out of its cloth or its opening.")


def _hem_normal_error(obj: bpy.types.Object, polygons: list[tuple[int, ...]], turned: list[tuple[int, int, int]]) -> str | None:
    """hem_normal_error of a version, with the corner normals its export writes."""
    co = bodies.world_positions(obj)
    inside = turned_mask(len(co), turned)
    cloth = [p for p in polygons if not inside[list(p)].any()]
    outward = cloth_normals(co, cloth)
    inward = hem_frames(co, cloth, [cloth_vertex for cloth_vertex, _rim, _flap in turned], outward)
    loop_vertices = np.empty(len(obj.data.loops), np.int64)
    obj.data.loops.foreach_get("vertex_index", loop_vertices)
    rotation = np.array(obj.matrix_world)[:3, :3]
    corner = bodies.corner_normals(obj.data).astype(np.float64) @ rotation.T
    corner /= np.maximum(np.linalg.norm(corner, axis=1, keepdims=True), 1e-30)
    return hem_normal_error(obj.name, co, turned, inward, outward, loop_vertices, corner)


def _fold_error(obj: bpy.types.Object, polygons: list[tuple[int, ...]], turned: list[tuple[int, int, int]], label: str,
                recipe) -> str | None:
    """fold_error of a version, with how far its hem's cloth lies off its cage."""
    co = bodies.world_positions(obj)
    shell = Surface(cage_object(label), [], recipe_regions(recipe))
    rooms = [shell.straight_distance(Vector(co[cloth])) for cloth, _rim, _flap in turned]
    return fold_error(obj.name, *_folds(co, polygons, turned), turned, co, rooms)


def _laid_first(co: np.ndarray, polygons: list[tuple[int, ...]], turned: list[tuple[int, int, int]], recipe,
                lines: list[dict], label: str, band: str, loose: np.ndarray, shell: Surface):
    """The first version's positions as a sync leaves them, with where its flaps lie on its cloth: the
    cloth vertices its turned edges are turned from settled HEM_ROOM off the cage where they lie
    closer, every other cloth vertex where it is, and the turned edges laid from that cloth."""
    held = np.ones(len(co), bool)
    held[[cloth for cloth, _rim, _flap in turned]] = False
    settled = _settle_band(np.asarray(co, float), polygons, shell, recipe, lines, label, band, held | loose,
                           [cloth for cloth, _rim, _flap in turned])
    # Laid from the cloth as the file keeps it, in float32, so the next sync lays it alike.
    return _lay(settled.astype(np.float32).astype(np.float64), polygons, turned, recipe["hem"], shell)


def _lay_first(first: bpy.types.Object, recipe, lines: list[dict], label: str, band: str) -> None:
    """Lays the first version's turned edges again from its cloth, in place, after the cloth vertices
    they are turned from keep their room off the cage: only positions change, so a turned edge is
    always its cloth's and a hand edit of one does not last. The shape the recipe gave them follows,
    laid from the shape the recipe gave the cloth."""
    polygons = [tuple(p.vertices) for p in first.data.polygons]
    turned = _turned(recipe, polygons)
    if not turned:
        return
    shell = Surface(cage_object(label), [], recipe_regions(recipe))
    loose = _loose(first.data)
    _set_shape(first, _laid_first(bodies.world_positions(first), polygons, turned, recipe, lines, label, band, loose,
                                  shell)[0])
    built = _attribute(first.data, BUILT)
    if built is not None and len(built) == len(first.data.vertices):
        _store(first.data, BUILT, _laid_first(built, polygons, turned, recipe, lines, label, band, loose, shell)[0])
    first.data.update()


def _hang_line(recipe, lines: list[dict], label: str, part: dict) -> tuple[Vector, Vector]:
    """The plane of the standard line a loose part hangs from, on a body."""
    line = next(line for line in lines if line["name"] == part["line"])
    _side, point, normal = cage.line_planes(label, line)[0]
    return point, normal


def _hang_hem(lines: list[dict], label: str, part: dict) -> float | None:
    """The height a loose part hangs down to on a body: that of the standard line its recipe names
    as its hem, between the body's two sides, or None for a part that hangs to the tips of its
    chains."""
    if part.get("to") is None:
        return None
    line = next((line for line in lines if line["name"] == part["to"]), None)
    if line is None:
        raise ArtPipelineError(f'The cage has no line "{part["to"]}" for the hem of the part at {part["line"]}.')
    return float(np.mean([point.z for _side, point, _normal in cage.line_planes(label, line)]))


def _add_hangs(bm: bmesh.types.BMesh, arm: bpy.types.Object, recipe, lines: list[dict], label: str) -> None:
    """Adds the recipe's loose parts to the cut cage: per part a grid of rings below the open edge
    at its line, laid on its scaffold on the first body, with the faces marked as the part's and,
    around a skirt, a seam down the back. The edge's vertices and the rings' keep their column in
    "hang_top", and the rings' vertices their ring in "hang_ring"."""
    entries = hang.parts(recipe)
    if not entries:
        return
    loose = bm.verts.layers.int.new(hang.HANG)
    top_layer = bm.verts.layers.int.new(hang.HANG_TOP)
    ring_layer = bm.verts.layers.int.new(hang.HANG_RING)
    face_layer = bm.faces.layers.int.new(hang.HANG_FACE)
    region_layer = bm.faces.layers.int.get("region")
    body = bodies.source_body(label)
    names = [b.name for b in arm.data.bones if b.use_deform]
    for number, part in enumerate(entries):
        point, normal = _hang_line(recipe, lines, label, part)
        edge = hang.edge(bm, part, point, normal)
        closed = part["side"] == "all"
        top = np.array([tuple(v.co) for v in edge])
        hem = _hang_hem(lines, label, part)
        rings = hang.ring_count(part, arm, top, hem)
        co, _weights = hang.scaffold(part, arm, body, top, np.zeros((len(top), len(names))), names, rings, hem)
        for column, v in enumerate(edge):
            v[loose] = -(number + 1)
            v[top_layer] = column
        grid = [edge]
        for k in range(1, rings + 1):
            row = [bm.verts.new(tuple(p)) for p in co[k]]
            for column, v in enumerate(row):
                v[loose] = number + 1
                v[ring_layer] = k
                v[top_layer] = column
            grid.append(row)
        # The new faces run the edge against the faces of the template there.
        a, b = edge[0], edge[1]
        face = next(f for f in a.link_faces if b in f.verts)
        runs = next(loop for loop in face.loops if loop.vert is a).link_loop_next.vert is b
        columns = len(edge) if closed else len(edge) - 1
        for k in range(rings):
            for c in range(columns):
                n = (c + 1) % len(edge)
                corners = (grid[k][n], grid[k][c], grid[k + 1][c], grid[k + 1][n]) if runs else \
                    (grid[k][c], grid[k][n], grid[k + 1][n], grid[k + 1][c])
                new = bm.faces.new(corners)
                new[region_layer] = len(cage.REGIONS)
                new[face_layer] = number + 1
                new.smooth = True
        if closed:
            # A skirt opens for its UVs down the middle of its back.
            back = min(range(len(edge)), key=lambda c: (abs(edge[c].co.x), edge[c].co.y))
            for k in range(rings):
                between = bm.edges.get((grid[k][back], grid[k + 1][back]))
                if between is not None:
                    between.seam = True
    bm.normal_update()


def _hang_columns(mesh: bpy.types.Mesh, number: int) -> tuple[np.ndarray, int]:
    """The vertices of the edge a loose part hangs from, in its order, and its number of rings."""
    loose = _attribute(mesh, hang.HANG, "INT")
    top = _attribute(mesh, hang.HANG_TOP, "INT")
    rings = _attribute(mesh, hang.HANG_RING, "INT")
    edge = np.nonzero(loose == -(number + 1))[0]
    edge = edge[np.argsort(top[edge], kind="stable")]
    return edge, int(rings[loose == number + 1].max())


def _lay_hangs(mesh: bpy.types.Mesh, arm: bpy.types.Object, recipe, lines: list[dict], label: str) -> None:
    """Lays the rings of every loose part on its scaffold from the edge where it now lies, before
    its edges are turned."""
    entries = hang.parts(recipe)
    if not entries:
        return
    names = [b.name for b in arm.data.bones if b.use_deform]
    loose = _attribute(mesh, hang.HANG, "INT")
    columns = _attribute(mesh, hang.HANG_TOP, "INT")
    rings_of = _attribute(mesh, hang.HANG_RING, "INT")
    co = np.empty(len(mesh.vertices) * 3)
    mesh.vertices.foreach_get("co", co)
    co = co.reshape(-1, 3)
    for number, part in enumerate(entries):
        edge, rings = _hang_columns(mesh, number)
        laid, _weights = hang.scaffold(part, arm, bodies.source_body(label), co[edge], np.zeros((len(edge), len(names))),
                                       names, rings, _hang_hem(lines, label, part))
        members = np.nonzero((loose == number + 1) & (rings_of > 0))[0]
        co[members] = laid[rings_of[members], columns[members]]
    mesh.vertices.foreach_set("co", co.reshape(-1))
    mesh.update()


def build(name: str, band: str, recipe: dict, lines: list[dict], label: str) -> bpy.types.Mesh:
    """The template's first version as its recipe gives it, on the cage of the first body, with its
    loose parts hanging on their scaffolds."""
    cage_obj = cage_object(label)
    arm = bpy.data.objects[label]
    bm = _cut(cage_obj, arm, recipe, lines)
    _add_hangs(bm, arm, recipe, lines, label)
    _mark_seams(bm, arm)
    mesh = bpy.data.meshes.new(version_name(name, label))
    bm.to_mesh(mesh)
    bm.free()
    for polygon in mesh.polygons:
        polygon.use_smooth = True
    _unwrap(mesh)
    # The splits come after the UVs are unfolded, so they interpolate them and change none.
    if recipe.get("splits"):
        bm = bmesh.new()
        bm.from_mesh(mesh)
        _split(bm, recipe, lines, arm)
        bm.to_mesh(mesh)
        bm.free()
    # The cage's own attributes stay with the cage.
    for attribute in cage.OWN_ATTRIBUTES:
        if attribute in mesh.attributes:
            mesh.attributes.remove(mesh.attributes[attribute])

    # Into the band along the cage's normals, with more room at the armpits; loose parts hang
    # where their scaffold lays them.
    co = np.empty(len(mesh.vertices) * 3)
    mesh.vertices.foreach_get("co", co)
    co = co.reshape(-1, 3)
    normals = np.empty(len(mesh.vertices) * 3)
    mesh.vertex_normals.foreach_get("vector", normals)
    normals = normals.reshape(-1, 3)
    loose = _loose(mesh)
    offset = np.full(len(co), OFFSETS[band]) + over_lift(recipe, lines, label, co)
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
    co = co + normals * np.where(loose, 0.0, offset)[:, None]
    surface = Surface(cage_obj, [], recipe_regions(recipe))
    polygons = [tuple(p.vertices) for p in mesh.polygons]
    # The open edges, which the hem turns, keep room for their fold above the cage.
    hems = border_vertices(polygons) if recipe.get("hem", 0.0) > 0.0 else []
    co = _settle_band(co, polygons, surface, recipe, lines, label, band, loose, hems)
    mesh.vertices.foreach_set("co", co.reshape(-1))
    mesh.update()
    _lay_hangs(mesh, arm, recipe, lines, label)
    if recipe.get("hem", 0.0) > 0.0:
        # The turned edges are laid from the settled cloth and not settled again: a settle of their
        # own would push them out through the cloth where it leans over them.
        _turn_edges(mesh)
        co = np.empty(len(mesh.vertices) * 3)
        mesh.vertices.foreach_get("co", co)
        polygons = [tuple(p.vertices) for p in mesh.polygons]
        co = lay_turned(co.reshape(-1, 3), polygons, turned_edges(polygons), recipe["hem"], surface)
        mesh.vertices.foreach_set("co", co.reshape(-1))
        mesh.update()
    mesh.validate()
    # Every quad splits across the diagonal it folds outward across, a quad and its mirror image alike.
    co = np.empty(len(mesh.vertices) * 3)
    mesh.vertices.foreach_get("co", co)
    _turn_corners(mesh, split_starts(co.reshape(-1, 3), [tuple(p.vertices) for p in mesh.polygons]))
    return mesh


@contextlib.contextmanager
def _scaffold(part: dict, lines: list[dict], arm: bpy.types.Object, top: np.ndarray, top_weights: np.ndarray,
              names: list[str], rings: int):
    """A loose part's scaffold on a body as a surface to bind to and place on, with the chains'
    weights, for the time of a with block."""
    co, weights = hang.scaffold(part, arm, bodies.source_body(arm.name), top, top_weights, names, rings,
                                _hang_hem(lines, arm.name, part))
    obj = hang.surface_object(co, weights, names, part["side"] == "all")
    try:
        yield Surface(obj, names)
    finally:
        hang.remove(obj)


def _plan(name: str, arms: list[bpy.types.Object], band: str, recipe, lines: list[dict], transfer_all: bool = False) -> dict:
    """What a sync gives every version: its positions, its weights over the first body's bones,
    the binding of the first version to its cage, and the checksums of the transferred weights of
    the first version, with the number of vertices painted by hand."""
    labels = [a.name for a in arms]
    first = _first_version(name, labels)
    # Every deforming bone, so weights painted by hand on cloth chain bones, which the body has
    # none of, are kept too.
    names = [b.name for b in arms[0].data.bones if b.use_deform]
    regions = recipe_regions(recipe)
    first_cage = Surface(cage_object(labels[0]), [], regions)
    co = bodies.world_positions(first)
    binding = [first_cage.bind(Vector(p)) for p in co]
    # A hairstyle's form under headwear is fitted the same way.
    under_first = under_positions(first)
    under_binding = [first_cage.bind(Vector(p)) for p in under_first] if under_first is not None else None
    polygons = [tuple(p.vertices) for p in first.data.polygons]
    # Every body's cage has the same triangles, so the regions of the first body's hold for all.
    keys = cage_keys(cage_object(labels[0]), first_cage)
    # The turned edges are laid from each version's cloth, and the cloth is settled without them.
    turned = _turned(recipe, polygons)
    folded = turned_mask(len(co), turned)

    loose = _loose(first.data)

    def weights_on(skin: RegionSkin, positions: np.ndarray, arm: bpy.types.Object) -> np.ndarray:
        # The skin's weights, and at the crotch its rule, on the fixed cloth; loose parts take
        # theirs from their scaffolds below.
        found = _rounded_weights(np.array([skin.weights(keys[t], Vector(p)) for p, (t, _w, _o) in zip(positions, binding)]))
        return _held_at_crotch(found, positions, polygons, arm, names, ~loose)

    transferred = weights_on(RegionSkin(bodies.source_body(labels[0]), names), co, arms[0])
    # Loose parts hang on scaffolds instead of the cage: on the first body each of their vertices is
    # bound to the scaffold there and takes its weights, the chains', from it.
    entries = hang.parts(recipe)
    values = _attribute(first.data, hang.HANG, "INT")
    members = [np.nonzero(values == number + 1)[0] for number in range(len(entries))]
    columns = [_hang_columns(first.data, number) for number in range(len(entries))]
    hangs = []
    for number, part in enumerate(entries):
        edge, rings = columns[number]
        with _scaffold(part, lines, arms[0], co[edge], transferred[edge], names, rings) as surface:
            bound = [surface.bind(Vector(co[i])) for i in members[number]]
            transferred[members[number]] = _rounded_weights(np.array([surface.weights_at(t, w) for t, w, _o in bound]))
        hangs.append(bound)
    current = bodies.weights_of(first, names)
    stored = _attribute(first.data, TRANSFERRED, "INT")
    if transfer_all or stored is None:
        hand = np.zeros(len(co), bool)
    else:
        sums = np.array([_checksum(row, names) for row in current])
        hand = (sums != stored) & (current.sum(1) > 0)
    # A turned vertex is never painted by hand: it takes its weights from its cloth, also from cloth
    # painted by hand, so its fold moves with the cloth in every pose.
    hand &= ~folded
    first_weights = np.where(hand[:, None], current, transferred)
    checksums = np.array([_checksum(row, names) for row in transferred])
    if stored is not None and not transfer_all:
        checksums = np.where(hand, stored, checksums)
    if turned:
        first_weights = turned_weights(first_weights, turned, _lay(co, polygons, turned, recipe["hem"], first_cage)[1])
        checksums = np.where(folded, [_checksum(row, names) for row in first_weights], checksums)
    plan = {"names": names, "hand": int(hand.sum()), "binding": binding, "turned": turned, "folded": folded,
            "hem": recipe["hem"] if turned else 0.0, "regions": regions,
            "versions": {first.name: {"positions": None, "under": None, "weights": first_weights,
                                      "checksums": checksums}}}
    hems = [cloth for cloth, _rim, _flap in turned]
    # A modeled form, such as hair or a helmet, stands off the cage and moves with its smooth field;
    # a recipe's cloth lies on the cage and follows each of its faces.
    modeled = not isinstance(recipe, dict)
    first_shape = bodies.world_positions(cage_object(labels[0]))
    for arm in arms[1:]:
        own = Surface(cage_object(arm.name), [], regions)
        own_shape = bodies.world_positions(cage_object(arm.name))
        if modeled:
            placed = field_place(first_shape, own_shape, co)
        else:
            placed = np.array([own.place(t, w, o) for t, w, o in binding])
        positions = _settle_band(placed, polygons, own, recipe, lines, arm.name, band, loose | folded, hems)
        under = None
        if under_binding is not None:
            under = field_place(first_shape, own_shape, under_first) if modeled else \
                np.array([own.place(t, w, o) for t, w, o in under_binding])
            under = _settle_band(under, polygons, own, recipe, lines, arm.name, band, loose | folded)
        skin = RegionSkin(bodies.source_body(arm.name), names)
        weights = weights_on(skin, positions, arm)
        # Then the loose parts, on this body's scaffolds from where their edges now lie.
        for number, part in enumerate(entries):
            edge, rings = columns[number]
            with _scaffold(part, lines, arm, positions[edge], weights[edge], names, rings) as surface:
                positions[members[number]] = np.array([surface.place(t, w, o) for t, w, o in hangs[number]])
                weights[members[number]] = _rounded_weights(
                    np.array([surface.weights_at(t, w) for t, w, _o in hangs[number]]))
        weights = np.where(hand[:, None], first_weights, weights)
        # Last the turned edges, from the cloth where it now lies, with its weights, also those of a
        # loose part's cloth, which its scaffold gave it.
        if turned:
            positions, flaps = _lay(positions, polygons, turned, recipe["hem"], own)
            weights = turned_weights(weights, turned, flaps)
        plan["versions"][version_name(name, arm.name)] = {
            "positions": positions, "under": under, "weights": weights, "checksums": None}
    return plan


def _apply(plan: dict, name: str, arms: list[bpy.types.Object]) -> None:
    """Writes the plan: fits every other version, keeping its corrections, gives every version its
    weights and binding, and keeps the first version's binding to its cage."""
    first = _first_version(name, [a.name for a in arms])
    names = plan["names"]
    for arm in arms:
        target = bpy.data.objects.get(version_name(name, arm.name))
        entry = plan["versions"][version_name(name, arm.name)]
        weights = entry["weights"]
        if entry["positions"] is not None:
            fitted, under = entry["positions"], entry["under"]
            under_correction = np.zeros_like(fitted)
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
                previous_under, current_under = _attribute(target.data, FITTED_UNDER), under_positions(target)
                if previous_under is not None and current_under is not None:
                    under_correction = current_under - previous_under
            _match_shape_keys(first, target)
            shape = fitted + correction
            # A turned edge follows its cloth's corrections and keeps none of its own.
            if plan["turned"] and np.any(correction):
                shape, flaps = _lay(shape, [tuple(p.vertices) for p in first.data.polygons], plan["turned"], plan["hem"],
                                    Surface(cage_object(arm.name), [], plan["regions"]))
                weights = turned_weights(weights, plan["turned"], flaps)
            _set_shape(target, shape, under + under_correction if under is not None else None)
            _store(target.data, FITTED, fitted)
            if under is not None:
                _store(target.data, FITTED_UNDER, under)
            for attribute in (TRANSFERRED, BUILT) + (() if under is not None else (FITTED_UNDER,)):
                if attribute in target.data.attributes:
                    target.data.attributes.remove(target.data.attributes[attribute])
            target.data.update()
        else:
            _store(target.data, TRANSFERRED, entry["checksums"], "INT")
        target.parent = arm
        target.matrix_parent_inverse = Matrix.Identity(4)
        bodies.set_weights(target, weights, names)
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
    digest.update(str(bpy.context.scene.get(CAGE_CONTENT)).encode("utf-8"))
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
        keys = obj.data.shape_keys
        digest.update(str([k.name for k in keys.key_blocks] if keys is not None else []).encode("utf-8"))
        under = under_positions(obj)
        digest.update(b"-" if under is None else (np.round(under, 6) + 0.0).tobytes())
        for attribute, kind in ((FITTED, "FLOAT_VECTOR"), (BUILT, "FLOAT_VECTOR"), (TRANSFERRED, "INT"),
                                (FITTED_UNDER, "FLOAT_VECTOR")):
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
           cage_file: str, cage_content: str, pairs: list[tuple[str, str, str]] = ()) -> None:
    """Writes the first file of a template: the bodies, their cages, and the first version as the
    recipe builds it, fitted to every body, clear of the templates of lower layers in pairs where it
    lies over them; a modeled template gets no first version, which a person models on the first
    body."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    arms = [body_files.append_body(path, root, collection_name(root)) for path, root in body_list]
    append_cages(cage_file, [a.name for a in arms], cage_content)
    append_lain_over(list(pairs), [a.name for a in arms])
    if recipe != MODELED:
        _new_first_version(name, band, recipe, lines, arms[0])
        _apply(_plan(name, arms, band, recipe, lines, transfer_all=True), name, arms)
    remove_inner()
    _show_first(arms)
    relative_image_paths()
    bpy.ops.wm.save_as_mainfile(filepath=file, compress=True, relative_remap=False)


def carried_moves(built: np.ndarray, shaped: np.ndarray, points: np.ndarray, sources) -> np.ndarray:
    """How far each point of a new build moves to carry a handmade shape over: by the difference
    between the old version and the old build at the nearest point of the old build among sources,
    the old vertices the carry-over reads, the first of them where two lie as near."""
    built, points = np.asarray(built, float)[sources], np.asarray(points, float)
    difference = np.asarray(shaped, float)[sources] - built
    moves = np.empty((len(points), 3))
    for start in range(0, len(points), 256):
        chunk = points[start:start + 256]
        nearest = np.argmin(((chunk[:, None, :] - built[None, :, :]) ** 2).sum(axis=2), axis=1)
        moves[start:start + 256] = difference[nearest]
    return moves


def _carry_over(old: bpy.types.Object, new: bpy.types.Object, recipe) -> int:
    """Gives a new build of the first version the handmade shape of the old one: each new vertex
    takes the difference between the old version and the old build at the nearest point of the old
    build's cloth. The old turned edges are left out: they are laid from their cloth and carry no
    shape of their own, and the new build's are laid again from its cloth. Returns how many old
    vertices of the cloth had been shaped by hand."""
    built = _attribute(old.data, BUILT)
    shaped = bodies.world_positions(old)
    if built is None or len(built) != len(shaped):
        return 0
    try:
        turned = _turned(recipe, [tuple(p.vertices) for p in old.data.polygons])
    except ArtPipelineError:
        turned = []
    sources = np.nonzero(~turned_mask(len(built), turned))[0]
    co = bodies.world_positions(new)
    new.data.vertices.foreach_set("co", (co + carried_moves(built, shaped, co, sources)).reshape(-1))
    new.data.update()
    return int((np.linalg.norm((shaped - built)[sources], axis=1) > FIT_TOLERANCE).sum())


def sync(file: str, name: str, band: str, recipe, lines: list[dict], body_list: list[tuple[str, str]], cage_file: str,
         cage_content: str, transfer_all: bool = False, rebuild: bool = False,
         pairs: list[tuple[str, str, str]] = ()) -> tuple[bool, int, list[str]]:
    """Appends the bodies that changed in their own files again, and the cages when the cage's content
    changed, moving the first version with its cage, builds the first version again from a changed
    recipe when asked, keeping its handmade shape, fits every other version again from the first,
    and gives every version its weights, keeping the corrections and the weights painted by hand,
    unless every weight is transferred again. Where the recipe says it lies over an inner garment,
    its cloth clears the real surface of the templates of lower layers in pairs, which the file never
    keeps. Returns whether the file changed, the number of vertices with weights painted by hand, and
    notes for the report. An unchanged file is not saved, so its approvals stay valid."""
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
    append_lain_over(list(pairs), labels)

    first = bpy.data.objects.get(version_name(name, labels[0]))
    if bpy.context.scene.get(CAGE_CONTENT) != cage_content or any(
            bpy.data.objects.get(cage.version_name(label)) is None for label in labels):
        binding = built_binding = under_binding = old_shape = None
        if first is not None and bpy.data.objects.get(cage.version_name(labels[0])) is not None:
            old_shape = bodies.world_positions(cage_object(labels[0]))
            old = Surface(cage_object(labels[0]), [], recipe_regions(recipe))
            binding = [old.bind(Vector(p)) for p in bodies.world_positions(first)]
            under = under_positions(first)
            if under is not None:
                under_binding = [old.bind(Vector(p)) for p in under]
            # The shape the recipe gave moves with the cage too, so a later rebuild carries over
            # only the handmade shape, not the cage's change.
            built = _attribute(first.data, BUILT)
            if built is not None:
                built_binding = [old.bind(Vector(p)) for p in built]
        append_cages(cage_file, labels, cage_content)
        if binding is not None:
            own = Surface(cage_object(labels[0]), [], recipe_regions(recipe))

            # The cage's vertices it had before, where regions were added after them.
            new_shape = bodies.world_positions(cage_object(labels[0]))[:len(old_shape)]

            def follow(points: np.ndarray, bound: list) -> np.ndarray:
                # Where the cage did not move, such as when only other regions were added to it, a
                # point stays exactly where it was, so the fitting of the other bodies, which looks
                # for the nearest triangle of the cage, finds what it found before. A modeled form
                # moves with the cage's smooth field, as it is fitted to the other bodies.
                if isinstance(recipe, dict):
                    placed = np.array([tuple(own.place(t, w, o)) for t, w, o in bound])
                else:
                    placed = field_place(old_shape, new_shape, points)
                return np.where((np.linalg.norm(placed - points, axis=1) > FIT_TOLERANCE)[:, None], placed, points)

            current = bodies.world_positions(first)
            moved = follow(current, binding)
            _set_shape(first, moved, follow(under_positions(first), under_binding) if under_binding is not None else None)
            if built_binding is not None:
                _store(first.data, BUILT, follow(_attribute(first.data, BUILT), built_binding))
            first.data.update()
            if np.abs(moved - current).max(initial=0.0) > 0.0:
                notes.append(f"{first.name} moved with its changed cage.")
            else:
                notes.append(f"{first.name} stays where it is: its regions of the changed cage did not move.")

    built = recipe != MODELED and (first is None or rebuild)
    if built:
        new = _new_first_version(name, band, recipe, lines, arms[0])
        if first is not None:
            shaped = _carry_over(first, new, recipe)
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
    # The first version's turned edges come from its cloth as it now lies, also after a rebuild that
    # carried a handmade shape over, or a move with its cage.
    _lay_first(first, recipe, lines, labels[0], band)
    if built:
        # A new build splits its quads by the shape it ends with, with the handmade shape carried
        # over, so none splits along the valley that shape folds it into.
        _turn_corners(first.data, split_starts(bodies.world_positions(first), [tuple(p.vertices) for p in first.data.polygons]))

    plan = _plan(name, arms, band, recipe, lines, transfer_all)
    _apply(plan, name, arms)
    remove_inner()
    if not removed and _state(name, labels) == before:
        return False, plan["hand"], notes
    _show_first(arms)
    relative_image_paths()
    bpy.ops.wm.save_as_mainfile(filepath=file, compress=True, relative_remap=False)
    return True, plan["hand"], notes


def stale_errors(name: str, band: str, recipe, lines: list[dict], body_list: list[tuple[str, str]],
                 cage_content: str) -> list[str]:
    """A template is checked as a sync would leave it: its bodies are the current ones of their files
    and its cages of the cage's current content, its first version was built from the recipe of its
    asset.json, every fitted version is fitted to the current first version, every version has the
    weights a sync gives it, and on every version the turned edges are laid from its cloth and have
    its weights, so a hand edit of a hem fails until a sync lays it again."""
    labels = [root for _path, root in body_list]
    stale = body_files.stale_bodies(body_list)
    if stale:
        return [f"The bodies {stale} of the garment changed in their own files; run tools/art/Sync-GarmentAsset.ps1."]
    if bpy.context.scene.get(CAGE_CONTENT) != cage_content:
        return ["The garment was fitted to other content of its cage; run tools/art/Sync-GarmentAsset.ps1."]
    first = bpy.data.objects.get(version_name(name, labels[0]))
    if first is not None and recipe != MODELED and first.get(RECIPE) != recipe_text(recipe):
        return [f'"{first.name}" was built from another recipe than its asset.json names; run '
                "tools/art/Sync-GarmentAsset.ps1 -Rebuild."]
    arms = [bpy.data.objects[label] for label in labels]
    try:
        plan = _plan(name, arms, band, recipe, lines)
    except ArtPipelineError:
        # garment_errors reports the missing first version or cage, and a border that is not turned.
        return []
    errors = []
    for label in labels:
        version = version_name(name, label)
        entry = plan["versions"][version]
        obj = bpy.data.objects.get(version)
        if obj is None:
            errors.append(f'The garment has no version "{version}"; run tools/art/Sync-GarmentAsset.ps1.')
            continue
        if entry["positions"] is not None:
            fitted = _attribute(obj.data, FITTED)
            if fitted is None or fitted.shape != entry["positions"].shape or \
                    np.abs(fitted - entry["positions"]).max() > FIT_TOLERANCE:
                errors.append(f'"{version}" is not fitted to the current "{version_name(name, labels[0])}"; '
                              "run tools/art/Sync-GarmentAsset.ps1.")
                continue
            if entry["under"] is not None:
                fitted_under = _attribute(obj.data, FITTED_UNDER)
                if fitted_under is None or fitted_under.shape != entry["under"].shape or \
                        np.abs(fitted_under - entry["under"]).max() > FIT_TOLERANCE or under_positions(obj) is None:
                    errors.append(f'The shape key {UNDER_HEADWEAR} of "{version}" is not fitted to the current '
                                  f'"{version_name(name, labels[0])}"; run tools/art/Sync-GarmentAsset.ps1.')
                    continue
        current = bodies.weights_of(obj, plan["names"])
        # The weights of turned vertices follow their cloth where it lies now, below.
        cloth = ~plan["folded"]
        if np.abs(current - entry["weights"])[cloth].max(initial=0.0) > WEIGHT_TOLERANCE:
            errors.append(f'"{version}" does not have the weights its body gives it; run tools/art/Sync-GarmentAsset.ps1.')
            continue
        if plan["turned"]:
            errors += filter(None, [_turned_stale(obj, current, label, label == labels[0], plan, recipe, lines, band)])
    return errors


def _turned_stale(obj: bpy.types.Object, current: np.ndarray, label: str, is_first: bool, plan: dict, recipe,
                  lines: list[dict], band: str) -> str | None:
    """Whether a version's turned edges are as a sync leaves them: laid from its cloth, the first
    version's after its hem keeps its room off the cage, and with the weights of its cloth."""
    polygons = [tuple(p.vertices) for p in obj.data.polygons]
    co = bodies.world_positions(obj)
    shell = Surface(cage_object(label), [], plan["regions"])
    if is_first:
        laid, flaps = _laid_first(co, polygons, plan["turned"], recipe, lines, label, band, _loose(obj.data), shell)
    else:
        laid, flaps = _lay(co, polygons, plan["turned"], plan["hem"], shell)
    if np.abs(laid - co).max(initial=0.0) > FIT_TOLERANCE:
        return f'The turned edges of "{obj.name}" are not laid from its cloth; run tools/art/Sync-GarmentAsset.ps1.'
    derived = turned_weights(current, plan["turned"], flaps)
    if np.abs(derived - current)[plan["folded"]].max(initial=0.0) > WEIGHT_TOLERANCE:
        return f'The turned edges of "{obj.name}" do not have the weights of their cloth; run tools/art/Sync-GarmentAsset.ps1.'
    return None


def shape_key_errors(obj: bpy.types.Object, slot: str | None) -> list[str]:
    """A template has exactly the shape keys of its slot: a hairstyle the basis and UnderHeadwear,
    relative to the basis and at rest, so the version shows its own form, and every other slot none."""
    wanted = ["Basis", *SHAPE_KEYS[slot]] if slot in SHAPE_KEYS else []
    keys = obj.data.shape_keys
    have = [k.name for k in keys.key_blocks] if keys is not None else []
    if have != wanted:
        return [f'"{obj.name}" must have the shape keys {wanted or "none"}, not {have or "none"}.']
    if keys is not None and any(k.relative_key != keys.key_blocks[0] or k.value != 0.0 or k.mute
                                for k in keys.key_blocks[1:]):
        return [f'The shape keys of "{obj.name}" must be relative to its basis, at 0, and not muted.']
    return []


def garment_errors(name: str, labels: list[str], band: str, budget: int, recipe=None, lines: list[dict] | None = None,
                   slot: str | None = None) -> list[str]:
    """The model rules of a template: a version per body with the first version's topology and one
    material, within the triangle budget and its band from the cage, at every vertex and at the
    centre of every face, so a large flat face over a round shoulder cannot dip under the cage
    between its corners and cloth cannot sink into a pit, and UVs on the first version. Where the
    template lies over an inner garment, its band is measured from that garment's surface. A
    hairstyle keeps its band in its form under headwear too. A turned edge lies inside the cloth it
    is turned from on every version, never under the cage, its rims at least MIN_FOLD deep, and the
    normals at the outer edge of its hem lean out of the cloth and out of the opening."""
    errors = []
    try:
        first = _first_version(name, labels)
    except ArtPipelineError as error:
        return [str(error)]
    if first.data.uv_layers.active is None:
        errors.append(f'"{first.name}" needs UVs that its textures are painted in.')
    polygons = [tuple(p.vertices) for p in first.data.polygons]
    try:
        turned = _turned(recipe, polygons)
    except ArtPipelineError as error:
        errors.append(str(error))
        turned = []
    if isinstance(recipe, dict):
        errors += filter(None, [mirror_split_error(first.name, bodies.world_positions(first), polygons)])
    low, high = BANDS[band]
    for label in labels:
        obj = bpy.data.objects.get(version_name(name, label))
        if obj is None or obj.type != "MESH":
            errors.append(f'The garment has no version "{version_name(name, label)}"; run tools/art/Sync-GarmentAsset.ps1.')
            continue
        if [tuple(p.vertices) for p in obj.data.polygons] != polygons:
            errors.append(f'"{obj.name}" does not have the topology of "{first.name}".')
        if len(obj.data.materials) == 0 or any(m is None or m.name != material_name(name) for m in obj.data.materials):
            errors.append(f'"{obj.name}" must use the one material "{material_name(name)}".')
        triangles = sum(len(p.vertices) - 2 for p in obj.data.polygons)
        if triangles > budget:
            errors.append(f'"{obj.name}" has {triangles} triangles; the budget is {budget}.')
        errors += shape_key_errors(obj, slot)
        forms = [("", bodies.world_positions(obj))]
        under = under_positions(obj)
        if under is not None:
            forms.append((f" in its shape key {UNDER_HEADWEAR}", under))
        for form, co in forms:
            try:
                distances, straight, nearest = _distances(obj, label, recipe_regions(recipe), co)
            except ArtPipelineError as error:
                errors.append(str(error))
                break
            lows, highs = limits(recipe, lines or [], label, co, polygons, band, _loose(first.data), turned,
                                 recipe["hem"] if turned else 0.0)
            # Too close by the straight distance, which a sharp edge of the cage does not shorten;
            # too far by the distance along the cage's normal, as the recipes build the cloth.
            near = straight < lows - 1e-4
            far = distances > highs + 1e-4 if highs is not None else np.zeros_like(near)
            if near.any() or far.any():
                # Where they are, so a person knows where to shape the garment.
                zones = bodies.zones_of(bodies.source_body(label))
                counts = np.bincount(zones[nearest[near | far]], minlength=len(bodies.ZONES))
                places = ", ".join(f"{bodies.ZONES[z]} {counts[z]}" for z in np.argsort(-counts, kind="stable") if counts[z])
                worst = []
                if near.any():
                    worst.append(f"the closest {straight[near].min() * 1000:.1f} mm")
                if far.any():
                    worst.append(f"the farthest {distances[far].max() * 1000:.1f} mm")
                over = (f" (keeping {OVER_GAP * 1000:.1f} mm outside the templates it lies over, where it lies over one)"
                        if (lows > low + ROUNDING).any() else "")
                errors.append(f'"{obj.name}"{form} leaves its {band} band{over} of {low * 1000:.0f} to '
                              f'{"any" if high is None else f"{high * 1000:.0f}"} mm from the cage at {int((near | far).sum())} '
                              f"vertices and face centres ({int(near.sum())} too close, {int(far.sum())} too far; "
                              f"{', '.join(worst)}), over {places}.")
        if turned and len(obj.data.vertices) == len(first.data.vertices):
            errors += filter(None, [_fold_error(obj, polygons, turned, label, recipe),
                                    _hem_normal_error(obj, polygons, turned)])
    return errors


def _distances(obj: bpy.types.Object, label: str, regions: tuple[str, ...] | None,
               co: np.ndarray | None = None) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """The distance from the cage's regions a template covers of every vertex and of the centre of
    every face, along the normal of the cage's nearest face and straight, and the body polygon each
    is nearest to; of the version's own positions or of others of its topology, such as its shape
    key UnderHeadwear."""
    shell = Surface(cage_object(label), [], regions)
    skin = Surface(bodies.source_body(label), [])
    co = bodies.world_positions(obj) if co is None else co
    points = [Vector(p) for p in list(co) + [co[list(p.vertices)].mean(axis=0) for p in obj.data.polygons]]
    return (np.array([shell.signed_distance(p)[0] for p in points]),
            np.array([shell.straight_distance(p) for p in points]),
            np.array([skin.signed_distance(p)[1] for p in points], np.int64))


def report(name: str, labels: list[str], recipe) -> list[str]:
    """How far every version lies from the cage, its loose parts left out, which hang on the cloth
    chains, and its triangles."""
    lines = []
    for label in labels:
        obj = bpy.data.objects.get(version_name(name, label))
        if obj is None or bpy.data.objects.get(cage.version_name(label)) is None:
            continue
        distances, straight, _polygons = _distances(obj, label, recipe_regions(recipe))
        polygons = [tuple(p.vertices) for p in obj.data.polygons]
        loose = _loose(obj.data)
        free = np.concatenate([loose, np.array([loose[list(p)].any() for p in polygons], bool)])
        fixed = distances[~free]
        # The closest as the band's lower bound measures it, the farthest as its upper bound does.
        closest = straight[~free].min()
        triangles = sum(len(p.vertices) - 2 for p in obj.data.polygons)
        hanging = "".join(f", {int((_attribute(obj.data, hang.HANG, 'INT') == number + 1).sum())} vertices hanging on the "
                          f"{part['chains']} chains" for number, part in enumerate(hang.parts(recipe)))
        lines.append(f"{obj.name}: {triangles} triangles, {closest * 1000:.1f} to {fixed.max() * 1000:.1f} mm "
                     f"from the cage, median {np.median(fixed) * 1000:.1f} mm{hanging}")
    return lines


def export_paths(name: str, labels: list[str], folder: str) -> list[tuple[str, str]]:
    return [(label, os.path.join(folder, f"{version_name(name, label)}.fbx")) for label in labels]


def review_file(file: str, labels: list[str], output: str) -> str:
    """The garment on every body side by side, with an action RigTest_<Label> on each armature that
    steps through the test poses, for the reviewer to play in Blender. The cloth chains of its loose
    parts lie in every pose as the runtime solver lays them."""
    bpy.ops.wm.open_mainfile(filepath=file)
    scene = bpy.context.scene
    frame = 0
    for index, label in enumerate(labels):
        arm = bpy.data.objects[label]
        kinds, reach = cloth.worn(arm, [o for o in arm.children_recursive if o.type == "MESH"])
        found = cloth.capsules(arm, bodies.source_body(label)) if kinds else []
        arm.location.x = index * REVIEW_OFFSET
        for obj in [arm] + list(arm.children_recursive):
            obj.hide_set(False)
        action = bpy.data.actions.new(f"RigTest_{label}")
        arm.animation_data_create()
        arm.animation_data.action = action
        frame = 0
        for _name, _description, pose in poses.POSES[1:]:
            for f, p in ((frame, {}), (frame + POSE_FRAMES // 2, pose), (frame + POSE_FRAMES, pose)):
                _key_pose(arm, p, f, kinds, found, reach)
            frame += POSE_FRAMES + POSE_FRAMES // 2
        _key_pose(arm, {}, frame, kinds, found, reach)
    scene.frame_start, scene.frame_end = 0, frame
    scene.render.fps = POSE_FRAMES
    scene.frame_set(0)
    os.makedirs(os.path.dirname(output), exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=output, compress=True, relative_remap=False)
    return output


def _key_pose(arm: bpy.types.Object, pose: dict, frame: int, kinds: list[str], found: list, reach: dict) -> None:
    poses.apply(arm, pose)
    cloth.follow(arm, kinds, found, reach)
    for pb in arm.pose.bones:
        pb.keyframe_insert("rotation_quaternion", frame=frame)
    arm.pose.bones["Hips"].keyframe_insert("location", frame=frame)


def read_wearing(manifest: str) -> tuple[str, str | None]:
    """The visual slot of a template's asset.json and, for headwear, what it does to the hair under
    it: hide, compress, or show."""
    data = manifest_io.read_json(manifest)
    return str(data["slot"]), data.get("hair")


def read_manifests(manifest: str, cage_manifest: str) -> tuple[str, object, list[dict], str]:
    """The band and the recipe of a template's asset.json, and the standard lines and the file of
    its cage."""
    data = manifest_io.read_json(manifest)
    _figure, lines = cage.read_manifest(cage_manifest)
    folder = os.path.dirname(os.path.abspath(cage_manifest))
    return str(data["band"]), data["recipe"], lines, os.path.join(folder, f"{os.path.basename(folder)}.blend")
