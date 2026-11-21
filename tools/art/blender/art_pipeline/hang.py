"""Loose parts of garment templates: cloth that hangs from a standard line on the cloth chains instead
of lying on the garment cage, such as the skirt of a robe or the back of a cloak. See
docs/EQUIPMENT_VISUALS_DESIGN.md.

A part hangs from the template's open edge at its line, all around for a skirt or across the back for
a cloak. Per body, a scaffold of rings is laid from that edge down to the tips of the chains, or to
the height of the line the part's recipe names as its hem, such as a skirt that ends above the ankle
so the shoes show, and the cloth does not follow the figure under it: every column is stretched like a string from the edge
over the body's outline, the margin of the chains' cloth profile outside it, to where the body
reaches out farthest, and hangs straight down from there, spanning the hollows. A skirt's columns
lie around the hips between the chains and widen by the part's flare toward the hem, so it falls
from the widest of the hips and the legs and never narrows; a cloak's columns lie across the back
and grow wider by the flare toward the hem. A part across the back with a top hangs high, from
the nape: the template keeps only its middle that wide, so a cloak's yoke lies on the trapezius
and not on the shoulders, and its back only above the part's line, where the part hangs from. With
a width the part fans out to that width by the first joint of its chains. Top and width are shares
of how far the shoulder joints lie from the middle. Every scaffold point takes the weights of the
chain bones around it, those of the two chains beside it and of the two bones along them, blended
from the edge's own weights at the top down to where the chains hang from, so the cloth moves with
the chains that the runtime solver swings. A skirt above where its chains hang from, such as one
from the waist, blends from the weights of the skin under it instead, so it moves with the belly,
the hips, and the top of the thighs there, and a thigh that rises carries the cloth along.

The template's first version takes the scaffold's points as its loose vertices. Every other version
binds them to the scaffold of the first body and places them on its own, as the rest of a template is
placed on the cage, so a part made once hangs on every body of the skeleton, and a shape given to it by
hand follows."""

import math

import bmesh
import bpy
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.interpolate import poly_3d_calc

from . import bodies, cage
from .scene import ArtPipelineError

# The point attribute "hang" holds 1 + the number of the part a loose vertex belongs to, minus that
# for a vertex of the edge a part hangs from, and 0 for the rest; "hang_top" the place of an edge
# vertex in the part's order, and "hang_ring" the ring of a vertex of a part. The face attribute
# "hang_face" marks the faces of the parts like the vertices; a mesh's attributes need names of
# their own across domains.
HANG = "hang"
HANG_TOP = "hang_top"
HANG_RING = "hang_ring"
HANG_FACE = "hang_face"
# The chains a part hangs on, by the start of their bones' names.
PREFIXES = {"skirt": "Skirt", "cloak": "Cloak"}
SIDES = ("all", "back")
# How far the cloth keeps from the body's outline: a skirt enough that the thighs of a jog stay
# under it between its chains, a cloak the margin the runtime solver gives its chains.
MARGINS = {"skirt": 0.03, "cloak": 0.02}
# The rings of a part, about this far apart, counted on the first body, so every body has as many.
# They lie closer near the edge it hangs from, where the hips and the buttocks or the shoulder blades
# turn fastest, and farther apart toward the hem, where the cloth hangs straight: ring k of n lies at
# the share (k / n) ** RING_EASING of the way down.
RING_SPACING = 0.09
RING_EASING = 1.4
# The scaffold's weights blend from the edge it hangs from into the chains over this many rings, so
# the cloth right below a hip line already follows a raised thigh; from an edge above where the
# chains hang from, they blend over the height down to there instead.
TOP_BLEND = 1
# A vertex of a template this close to where a part's trim cuts it moves onto the cut first, as at
# the template's lines.
TRIM_SNAP = 0.008
# The body is measured at heights this far apart. Right below the edge it hangs from, cloth keeps the
# edge's own gap from the body and leaves the body by this much per meter down until it keeps the
# margin. A skirt whose edge lies above where its chains hang from, such as one from the waist,
# leaves the body evenly over that height instead when that is slower: cloth that stood off the
# belly and the hips a few centimeters below a fitted waist would puff out like a bell there.
STRING_STEP = 0.01
EDGE_GAP = 0.004
LEAVE = 0.5
# The zones whose outline a part keeps outside of; a skirt from the waist spans the belly too.
OUTLINE_ZONES = {"skirt": ("Torso", "Pelvis", "Thighs", "Shins", "Feet"),
                 "cloak": ("Torso", "UpperArms", "Pelvis", "Thighs", "Shins")}
# Above where its chains hang from, a skirt moves with the skin of these zones under it, and
# takes the chains' weights only over this height above their roots: a chain bone turns about its
# root, so cloth above the root that turned with it would swing in where the bone swings out.
UNDER_SKIRT_ZONES = ("Torso", "Pelvis", "Thighs")
ROOT_BLEND = 0.03
# A cloak keeps clear of a skirt that may be under it, by this much more than the skirt keeps from the
# hips and the legs, as the runtime solver lays a cloak over a skirt: a skirt from the waist bulges
# over the hips above its chains, where the solver does not lay the cloak over it.
SKIRT_ZONES = ("Pelvis", "Thighs", "Shins", "Feet")
SKIRT_CLEARANCE = 0.01


def parts(recipe) -> list[dict]:
    return list(recipe.get("hang", [])) if isinstance(recipe, dict) else []


def swinging_faces(obj: bpy.types.Object, positions: np.ndarray) -> np.ndarray:
    """Which faces of a garment version swing on the cloth chains: the faces of its loose parts
    that reach down to where the part's chains hang from. A part's faces above that, such as the
    top of a skirt from the waist, move with the skin under them and do not swing."""
    mesh = obj.data
    attribute = mesh.attributes.get(HANG_FACE)
    if attribute is None or attribute.domain != "FACE" or attribute.data_type != "INT":
        return np.zeros(len(mesh.polygons), bool)
    parts = np.empty(len(mesh.polygons), np.int32)
    attribute.data.foreach_get("value", parts)
    arm = obj.find_armature() or obj.parent
    # The height each part's chains hang from, by the chains its vertices weigh.
    roots: dict[int, float] = {}
    for kind, prefix in PREFIXES.items():
        names = [group.name for group in obj.vertex_groups if group.name.startswith(prefix)]
        if not names or arm is None or arm.type != "ARMATURE":
            continue
        carried = bodies.weights_of(obj, names).sum(axis=1) > 0.0
        height = float(np.mean([joints[0, 2] for _names, joints in chains(arm, kind)]))
        for polygon in mesh.polygons:
            if parts[polygon.index] > 0 and carried[list(polygon.vertices)].any():
                roots.setdefault(int(parts[polygon.index]), height)
    swinging = np.zeros(len(mesh.polygons), bool)
    for polygon in mesh.polygons:
        part = int(parts[polygon.index])
        if part > 0:
            # A part whose chains are unknown swings as a whole.
            swinging[polygon.index] = part not in roots or positions[list(polygon.vertices), 2].min() <= roots[part] + 1e-4
    return swinging


def shoulder_half(arm: bpy.types.Object) -> float:
    """How far the shoulder joints lie from the middle at rest, which a part's top and width are
    shares of."""
    return float(np.mean([abs((arm.matrix_world @ arm.data.bones[f"UpperArm_{side}"].head_local).x)
                          for side in ("L", "R")]))


def top_half(part: dict, arm: bpy.types.Object) -> float | None:
    """How far from the middle the edge of a part across the back reaches, or None for one that
    hangs from the whole back of its line."""
    return float(part["top"]) * shoulder_half(arm) if part.get("top") is not None else None


def chains(arm: bpy.types.Object, kind: str) -> list[tuple[list[str], np.ndarray]]:
    """The cloth chains of a kind on an armature at rest: per chain its bones from the root and its
    joints in world space, sorted around the hips from the front for a skirt, or across the back
    from the left for a cloak."""
    prefix = PREFIXES[kind]
    found = []
    for bone in arm.data.bones:
        if not bone.name.startswith(prefix) or (bone.parent is not None and bone.parent.name.startswith(prefix)):
            continue
        names, current = [], bone
        while current is not None:
            names.append(current.name)
            current = next((c for c in current.children if c.name.startswith(prefix)), None)
        heads = [arm.matrix_world @ arm.data.bones[names[0]].head_local]
        joints = np.array(heads + [arm.matrix_world @ arm.data.bones[n].tail_local for n in names])
        found.append((names, joints))
    if not found:
        raise ArtPipelineError(f'"{arm.name}" has no {kind} chains; run tools/art/Sync-HumanBase.ps1.')
    return found


def _down(joints: np.ndarray, z: float) -> tuple[np.ndarray, float]:
    """The point of a hanging chain at a height and how far down the chain it lies, in bones: below
    the root and above the tip along the chain, above the root straight above it, and below the tip
    straight below it."""
    zs = joints[:, 2]
    if z >= zs[0]:
        return np.array((joints[0, 0], joints[0, 1], z)), -(z - zs[0]) / max(zs[0] - zs[1], 1e-6)
    for b in range(len(joints) - 1):
        if zs[b] >= z >= zs[b + 1]:
            t = (zs[b] - z) / max(zs[b] - zs[b + 1], 1e-9)
            return joints[b] + (joints[b + 1] - joints[b]) * t, b + t
    return np.array((joints[-1, 0], joints[-1, 1], z)), float(len(joints) - 1)


def _tent(u: float, count: int) -> np.ndarray:
    """How much each bone of a chain carries a point that far down it, in bones: the bone it lies on,
    blended with the next one toward the joint between them."""
    centres = np.arange(count) + 0.5
    weights = np.maximum(0.0, 1.0 - np.abs(np.clip(u, 0.5, count - 0.5) - centres))
    return weights / weights.sum()


def _smooth(t: float) -> float:
    t = min(max(t, 0.0), 1.0)
    return t * t * (3.0 - 2.0 * t)


class Outline:
    """A body's outline at a height: where a level plane cuts its skin of some zones."""

    def __init__(self, body: bpy.types.Object, zones: tuple[str, ...]):
        co = bodies.world_positions(body)
        zone_of = bodies.zones_of(body)
        wanted = {bodies.ZONES.index(z) for z in zones}
        polygons = [tuple(p.vertices) for p in body.data.polygons if zone_of[p.index] in wanted]
        self.triangles = np.array([co[[p[0], p[i], p[i + 1]]] for p in polygons for i in range(1, len(p) - 1)])

    def section(self, z: float) -> np.ndarray:
        """The points where the plane at the height cuts the skin, in X and Y."""
        signed = self.triangles[:, :, 2] - z
        found = []
        for i, j in ((0, 1), (1, 2), (2, 0)):
            crossing = (signed[:, i] < 0.0) != (signed[:, j] < 0.0)
            a, b = self.triangles[crossing, i], self.triangles[crossing, j]
            t = signed[crossing, i] / (signed[crossing, i] - signed[crossing, j])
            found.append(a + (b - a) * t[:, None])
        return np.concatenate(found)[:, :2] if found else np.zeros((0, 2))


class SkinWeights:
    """A body's weights over some bones at the nearest skin of some zones to a point."""

    def __init__(self, body: bpy.types.Object, names: list[str], zones: tuple[str, ...]):
        self.co = bodies.world_positions(body)
        zone_of = bodies.zones_of(body)
        wanted = {bodies.ZONES.index(z) for z in zones}
        self.polygons = [tuple(p.vertices) for p in body.data.polygons if zone_of[p.index] in wanted]
        self.tree = BVHTree.FromPolygons([tuple(c) for c in self.co], self.polygons)
        self.weights = bodies.weights_of(body, names)

    def at(self, point) -> np.ndarray:
        location, _normal, index, _distance = self.tree.find_nearest(Vector(point))
        corners = self.polygons[index]
        shares = poly_3d_calc([Vector(self.co[v]) for v in corners], location)
        return sum(self.weights[v] * share for v, share in zip(corners, shares))


def _reach(convex: np.ndarray | None, centre: np.ndarray, angle: float) -> float:
    """How far from a centre a convex outline reaches toward an angle from the front (+Y) toward +X,
    so a skirt spans the gap between the legs: where a ray leaves the outline, also from a centre
    outside it, such as in front of the calves of legs that lean back; 0 where the ray misses it or
    there is no outline."""
    if convex is None:
        return 0.0
    direction = np.array((math.sin(angle), math.cos(angle)))
    farthest = 0.0
    for a, b in zip(convex, np.roll(convex, -1, axis=0)):
        edge, start = b - a, a - centre
        determinant = edge[0] * direction[1] - edge[1] * direction[0]
        if abs(determinant) < 1e-15:
            continue
        t = (edge[0] * start[1] - edge[1] * start[0]) / determinant
        u = (direction[0] * start[1] - direction[1] * start[0]) / determinant
        if -1e-9 <= u <= 1.0 + 1e-9:
            farthest = max(farthest, t)
    return float(farthest)


def _string(depth: np.ndarray, needed: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """A column's profile stretched like a string from its edge over what it must pass, by depth
    below the edge: never less lower down, then the least concave curve over that, so the cloth runs
    straight to where the body reaches out farthest and straight down from there, spanning hollows."""
    needed = np.maximum.accumulate(needed)
    hull = [0]
    for i in range(1, len(depth)):
        while len(hull) >= 2:
            a, b = hull[-2], hull[-1]
            if (depth[b] - depth[a]) * (needed[i] - needed[a]) - (needed[b] - needed[a]) * (depth[i] - depth[a]) >= 0.0:
                hull.pop()
            else:
                break
        hull.append(i)
    return depth[hull], needed[hull]


def _gap(depth: float, margin: float, over: float = 0.0) -> float:
    """How far the cloth keeps from the body this far below the edge it hangs from: the edge's own gap
    right below it, growing to the margin, by LEAVE per meter down or, where that is slower, evenly
    over the height over."""
    leave = min(LEAVE, (margin - EDGE_GAP) / over) if over > 0.0 else LEAVE
    return min(margin, EDGE_GAP + depth * leave)


def _back(convex: np.ndarray | None, x: float) -> float | None:
    """How far back a convex outline reaches at an X, the least Y where its border crosses it, or
    None where it does not reach that X."""
    if convex is None:
        return None
    found = []
    for a, b in zip(convex, np.roll(convex, -1, axis=0)):
        if min(a[0], b[0]) <= x <= max(a[0], b[0]) and abs(b[0] - a[0]) > 1e-12:
            found.append(a[1] + (b[1] - a[1]) * (x - a[0]) / (b[0] - a[0]))
    return min(found) if found else None


def _spread(depth: float, fan: float) -> float:
    """How far a part across the back has fanned out to its width this far below its edge: from
    none at the edge to all of it at the depth of the first joint of its chains, easing in and out."""
    return _smooth(depth / fan) if fan > 0.0 else 1.0


def _half(part: dict, top_half: float, width_half: float, fan: float, drop: float, depth: float) -> float:
    """How far from the middle the outermost column of a part across the back lies this far below
    its edge: fanned out from its top to its width, and wider by its flare toward the hem."""
    return top_half + (width_half - top_half) * _spread(depth, fan) + float(part["flare"]) * depth / drop


def _taut(outlines: list[tuple[Outline, float]], top: np.ndarray, heights: np.ndarray, across) -> list:
    """How far behind its top every cloak column lies below it: its string over the back of the body,
    measured from the body's convex outlines at each height, each with the margin the cloak keeps
    from it, so it spans the hollow along the spine. across(c, depth) is the X of column c that far
    below the edge."""
    sections = [[cage.hull(s) if len(s) >= 3 else None for s in (outline.section(z) for z in heights)]
                for outline, _margin in outlines]
    depth = heights[0] - heights
    profiles = []
    for c in range(len(top)):
        needed = np.zeros(len(heights))
        for (_outline, margin), hulls in zip(outlines, sections):
            for i, hull in enumerate(hulls):
                back = _back(hull, across(c, depth[i]))
                if back is not None:
                    needed[i] = max(needed[i], top[c, 1] - (back - _gap(depth[i], margin)))
        needed[0] = 0.0
        profiles.append(_string(depth, needed))
    return profiles


def _flared(outline: Outline, starts: np.ndarray, chain_radii: np.ndarray, angles: np.ndarray, centre: np.ndarray,
            heights: np.ndarray, z_root: float, margin: float) -> list:
    """How far from the hips' centre every skirt column lies below its edge: its string over the
    body's outline, and below where the chains hang from over them, so the skirt falls from the
    widest of the hips and the legs and never narrows."""
    hulls = [cage.hull(s) if len(s) >= 3 else None for s in (outline.section(z) for z in heights)]
    depth = heights[0] - heights
    # From an edge above where the chains hang from, the cloth leaves the body over the height down
    # to there.
    over = max(float(heights[0]) - z_root, 0.0)
    profiles = []
    for c in range(len(starts)):
        needed = np.array([max(chain_radii[c] if z <= z_root else 0.0,
                               _reach(hull, centre, angles[c]) + _gap(d, margin, over))
                           for hull, d, z in zip(hulls, depth, heights)])
        needed[0] = starts[c]
        profiles.append(_string(depth, needed))
    return profiles


def hem_height(part: dict, tips: float, hem: float | None) -> float:
    """The height a part hangs down to: the tips of its chains, or its hem where its recipe names a
    line for it. The hem may not lie below the tips, where no chain carries the cloth."""
    if hem is None:
        return tips
    if hem < tips:
        raise ArtPipelineError(f'The hem of the part at {part["line"]}, the line {part["to"]}, lies '
                               f'{(tips - hem) * 100:.1f} cm below the tips of its {part["chains"]} chains.')
    return hem


def ring_count(part: dict, arm: bpy.types.Object, top: np.ndarray, hem: float | None = None) -> int:
    """How many rings a part has below the edge it hangs from, on the first body."""
    joints = [j for _names, j in chains(arm, part["chains"])]
    drop = float(top[:, 2].mean() - hem_height(part, float(np.mean([j[-1, 2] for j in joints])), hem))
    if drop <= RING_SPACING:
        raise ArtPipelineError(f"The edge a part hangs from lies below its hem or the tips of its {part['chains']} chains.")
    return max(2, round(drop / RING_SPACING))


def scaffold(part: dict, arm: bpy.types.Object, body: bpy.types.Object, top: np.ndarray, top_weights: np.ndarray,
             names: list[str], rings: int, hem: float | None = None) -> tuple[np.ndarray, np.ndarray]:
    """A part's scaffold on a body: points (rings + 1, columns, 3), its first ring the edge it hangs
    from, and their weights over the bones of names. It reaches down to the tips of the chains, or
    to the height hem."""
    kind = part["chains"]
    found = chains(arm, kind)
    margin = MARGINS[kind]
    outline = Outline(body, OUTLINE_ZONES[kind])
    column = {name: i for i, name in enumerate(names)}
    z_top = float(top[:, 2].mean())
    z_tip = hem_height(part, float(np.mean([j[-1, 2] for _n, j in found])), hem)
    # Above where the chains hang from, the cloth moves with the body, as its edge does.
    z_root = float(np.mean([j[0, 2] for _n, j in found]))
    co = np.zeros((rings + 1, len(top), 3))
    weights = np.zeros((rings + 1, len(top), len(names)))
    co[0] = top
    weights[0] = top_weights
    # A skirt from above where its chains hang from, such as one from the waist, moves there with
    # the skin under it, not with its edge: a thigh that rises carries the cloth in front of the
    # hip along, so the cloth can lie close to the body there without the thigh coming through.
    under = SkinWeights(body, names, UNDER_SKIRT_ZONES) if kind == "skirt" and z_top > z_root and top_weights.any() else None
    heights = np.append(np.arange(z_top, z_tip, -STRING_STEP), z_tip)
    if kind == "skirt":
        centre = np.mean([j[0, :2] for _n, j in found], axis=0)
        angles = np.array([math.atan2(j[0, 0] - centre[0], j[0, 1] - centre[1]) for _n, j in found])
        order = np.argsort(angles)
        found = [found[i] for i in order]
        angles = angles[order]
        # Every column between the two chains around it, by its angle.
        columns = np.array([math.atan2(p[0] - centre[0], p[1] - centre[1]) for p in top])
        pairs = []
        for angle in columns:
            after = int(np.searchsorted(angles, angle) % len(angles))
            before = (after - 1) % len(angles)
            span = (angles[after] - angles[before]) % (2.0 * math.pi) or 2.0 * math.pi
            t = ((angle - angles[before]) % (2.0 * math.pi)) / span
            pairs.append(((before, 1.0 - t), (after, t)))
        chain_radii = np.array([sum(w * np.linalg.norm(found[i][1][0, :2] - centre) for i, w in pair) for pair in pairs])
        strings = _flared(outline, np.linalg.norm(top[:, :2] - centre, axis=1), chain_radii, columns, centre, heights,
                          z_root, margin)
    else:
        across = np.array([j[0, 0] for _n, j in found])
        order = np.argsort(across)
        found = [found[i] for i in order]
        across = across[order]
        # Every column keeps its share of the outermost one's distance from the middle.
        reach = max(float(np.abs(top[:, 0]).max()), 1e-6)
        width_half = float(part["width"]) * shoulder_half(arm) if part.get("width") is not None else reach
        fan = z_top - float(np.mean([j[1, 2] for _n, j in found]))
        drop = z_top - z_tip

        def column_x(c: int, depth: float) -> float:
            return float(top[c, 0]) / reach * _half(part, reach, width_half, fan, drop, depth)

        under = Outline(body, SKIRT_ZONES)
        strings = _taut([(outline, margin), (under, MARGINS["skirt"] + SKIRT_CLEARANCE)], top, heights, column_x)
    for k in range(1, rings + 1):
        down = (k / rings) ** RING_EASING
        z = z_top + (z_tip - z_top) * down
        if z <= z_root:
            share = _smooth(k / TOP_BLEND)
        elif under is not None:
            share = _smooth(1.0 - (z - z_root) / ROOT_BLEND)
        else:
            share = _smooth((z_top - z) / (z_top - z_root))
        points = [_down(j, z) for _n, j in found]
        for c in range(len(top)):
            chain_weights = np.zeros(len(names))
            if kind == "skirt":
                pair = pairs[c]
                # On the column's string, wider by the flare toward the hem.
                radius = float(np.interp(z_top - z, *strings[c])) + float(part["flare"]) * down
                co[k, c] = (centre[0] + radius * math.sin(columns[c]), centre[1] + radius * math.cos(columns[c]), z)
            else:
                x = column_x(c, z_top - z)
                after = int(np.clip(np.searchsorted(across, x), 1, len(across) - 1))
                t = float(np.clip((x - across[after - 1]) / max(across[after] - across[after - 1], 1e-9), 0.0, 1.0))
                pair = ((after - 1, 1.0 - t), (after, t))
                # On the column's string, fanned out and wider by the flare.
                behind = float(np.interp(z_top - z, *strings[c]))
                co[k, c] = (x, top[c, 1] - behind, z)
            for i, w in pair:
                bones, _joints = found[i]
                for bone, carried in zip(bones, _tent(points[i][1], len(bones))):
                    if bone not in column:
                        raise ArtPipelineError(f'The chain bone "{bone}" is not a deforming bone of "{arm.name}".')
                    chain_weights[column[bone]] += w * carried
            above = under.at(co[k, c]) if under is not None and z > z_root else top_weights[c]
            weights[k, c] = above * (1.0 - share) + chain_weights * share
    return co, weights


def surface_object(co: np.ndarray, weights: np.ndarray, names: list[str], closed: bool) -> bpy.types.Object:
    """A scaffold as a mesh object with its weights in vertex groups, for binding to it. The caller
    removes it."""
    rings, columns = co.shape[0], co.shape[1]
    faces = []
    for k in range(rings - 1):
        for c in range(columns if closed else columns - 1):
            n = (c + 1) % columns
            faces.append((k * columns + c, k * columns + n, (k + 1) * columns + n, (k + 1) * columns + c))
    mesh = bpy.data.meshes.new("_scaffold")
    mesh.from_pydata([tuple(p) for p in co.reshape(-1, 3)], [], faces)
    obj = bpy.data.objects.new("_scaffold", mesh)
    bodies.set_weights(obj, weights.reshape(-1, len(names)), names)
    return obj


def remove(obj: bpy.types.Object) -> None:
    mesh = obj.data
    bpy.data.objects.remove(obj, do_unlink=True)
    bpy.data.meshes.remove(mesh)


def _bisect(bm: bmesh.types.BMesh, faces: list, point, normal, clear_outer: bool = False,
            clear_inner: bool = False) -> None:
    """Cuts faces at a plane, as a template is cut at its lines: their vertices this close to it
    first move onto it, so the cut leaves no slivers."""
    faces = [f for f in faces if f.is_valid]
    point, normal = Vector(point), Vector(normal).normalized()
    # By index, not by memory address as a set goes, so a rebuild gives its new elements the same order.
    bm.verts.index_update()
    bm.edges.index_update()
    verts = sorted({v for f in faces for v in f.verts}, key=lambda v: v.index)
    for v in verts:
        distance = (v.co - point).dot(normal)
        if abs(distance) < TRIM_SNAP:
            v.co -= normal * distance
    geom = sorted({e for f in faces for e in f.edges}, key=lambda e: e.index) + verts + faces
    bmesh.ops.bisect_plane(bm, geom=geom, dist=1e-6, plane_co=point, plane_no=normal, clear_outer=clear_outer,
                           clear_inner=clear_inner)


def trim(bm: bmesh.types.BMesh, part: dict, arm: bpy.types.Object, line_point: Vector,
         line_normal: Vector) -> None:
    """Trims a template for a part across the back with a top: its back, behind the bone at the
    part's line, goes below the line, where the part hangs from, and it keeps only its middle within
    the top, cut straight down there, so it lies on nothing beyond, such as the shoulders."""
    half = top_half(part, arm)
    _bisect(bm, [f for f in bm.faces if f.calc_center_median().y < line_point.y], line_point, line_normal,
            clear_inner=True)
    for sign in (1.0, -1.0):
        _bisect(bm, list(bm.faces), (sign * half, 0.0, 0.0), (sign, 0.0, 0.0), clear_outer=True)
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context="VERTS")


def edge(bm: bmesh.types.BMesh, part: dict, line_point: Vector, line_normal: Vector) -> list:
    """The open edge a part hangs from: the border vertices of the template on the plane of its line,
    those behind their middle for a cloak, or behind the bone at the line for one with a top, in their
    order along the border, from the left across the back for a cloak and around the hips from the
    front for a skirt."""
    on = {v for e in bm.edges if e.is_boundary for v in e.verts if abs((v.co - line_point).dot(line_normal)) < 1e-4}
    if not on:
        raise ArtPipelineError(f"The template has no open edge at {part['line']} for a part to hang from.")
    if part["side"] == "back":
        # A part with a top has a yoke that comes over to the front, beside the neck.
        if part.get("top") is not None:
            middle = line_point.y
        else:
            middle = (max(v.co.y for v in on) + min(v.co.y for v in on)) / 2.0
        on = {v for v in on if v.co.y < middle}
    neighbors = {v: [w for e in v.link_edges if e.is_boundary for w in e.verts if w is not v and w in on] for v in on}
    ends = [v for v, n in neighbors.items() if len(n) < 2]
    if part["side"] == "back":
        if len(ends) != 2:
            raise ArtPipelineError(f"The edge at {part['line']} across the back is not one line of the template's border.")
        start = min(ends, key=lambda v: v.co.x)
    else:
        if ends:
            raise ArtPipelineError(f"The edge at {part['line']} is not one loop of the template's border.")
        start = min(on, key=lambda v: (abs(v.co.x), -v.co.y))
    ordered, previous = [start], None
    while True:
        following = [w for w in neighbors[ordered[-1]] if w is not previous and w not in ordered]
        if not following:
            break
        previous = ordered[-1]
        ordered.append(min(following, key=lambda w: w.co.x) if len(following) > 1 else following[0])
    if len(ordered) != len(on):
        raise ArtPipelineError(f"The edge at {part['line']} is not one connected line of the template's border.")
    return ordered
