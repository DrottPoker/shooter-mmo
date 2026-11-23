"""Garment textures: the look of a garment template, its painted image in the template's UVs, in an
art asset of its own, so a new texture never changes the template's file. See
docs/EQUIPMENT_VISUALS_DESIGN.md.

The file holds the image T_Garment_<Name>, packed, with the custom properties roughness and
metallic, and the mesh Preview, a copy of the template's first version with the material
M_Garment_<Name> that shows the image, for painting in Texture Paint mode. Preview keeps the UVs the
image is painted in, and the property template_uvs the fingerprint of the template's UVs then, so a
template whose faces or UVs change is noticed and its texture moved to the new UVs. The file also
keeps Preview_Cage, the version of the garment cage that Preview lies on.

The move goes by correspondence, not by rays: a body may come to rest in another pose, and a
template built again from its recipe has other faces. Preview first follows the template: vertex by
vertex when the template still has its faces, else with the cage from Preview_Cage to the template's
cage as it is now, as a template's first version moves with its cage, and then by its feature
curves, its open edges and the lines its loose parts hang from, each drawn onto the template's
nearest curve of its kind with that move spread evenly over the cloth between, since the standard
lines a template is cut at are measured on the bones and the cage's net may slide along the body by
centimeters from one fit to the next. Every texel of the new UVs then takes the paint of the nearest
point of the moved Preview on a face that faces its way. A texture may also start from the look of
another mesh by projection, by rays, such as a garment made before templates.

A texture with transparent parts, such as a laced V, shows the skin through them, so it has a cover
mask, edges, and pull-in of its own per body, computed in the template's file with the texture on
every version, by the same rules as the template's, and its skin is checked in motion like the
template's. An opaque texture uses the template's masks.

A dyeable texture also has the dye mask T_Garment_<Name>_Dye, whose red, green, and blue channels
mark the regions that a dye color tints, and the custom property dye_colors on it, the natural color
of each channel, which the undyed garment wears. The garment shader multiplies the painted color by
the dye color as far as a channel reaches, so a dyeable region is painted in a light neutral, with its
middle tone at DYE_LEVEL, and its natural color gives back its painted look."""

import hashlib
import os

import bpy
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.interpolate import poly_3d_calc

from . import bodies, cage, coverage, garment, hang, paint
from .export import PNG_SIGNATURE
from .scene import ArtPipelineError, relative_image_paths

PREVIEW = "Preview"
PREVIEW_CAGE = "Preview_Cage"
TEMPLATE_UVS = "template_uvs"
SIZES = (512, 1024, 2048)
# A projection looks this far off the target's surface, both ways, for the source's surface.
PROJECTION_REACH = 0.06
BAKE_MARGIN = 8
# How far a feature curve of the preview may lie from the template's, on average, to be drawn onto
# it: the cage's net slides along the body by a few centimeters between two fits.
CURVE_REACH = 0.10
# How far beyond the nearest face a texel looks for one that faces its way, so the inside of a
# turned edge, a few millimeters under the outside, never paints the outside.
SIDE_REACH = 0.03
ALPHA_CUT = 0.5
DYE_SUFFIX = "_Dye"
DYE_COLORS = "dye_colors"
MAX_DYES = 3
# The linear level of a dyeable region's middle tone in the painted image, the same on every dyeable
# texture, so one dye color gives the same cloth on every garment: its middle tone is the level times
# the dye color. A texel too light to be dyed in full keeps part of its painted color instead.
DYE_LEVEL = 0.7
# A dye mask channel no texel reaches beyond this is unused.
DYE_UNUSED = 1.0 / 255.0


def image_name(name: str) -> str:
    return garment.image_name(name)


def dye_image_name(name: str) -> str:
    return f"{image_name(name)}{DYE_SUFFIX}"


def srgb_to_linear(c: np.ndarray) -> np.ndarray:
    return np.where(c <= 0.04045, c / 12.92, ((np.maximum(c, 0.0) + 0.055) / 1.055) ** 2.4)


def linear_to_srgb(c: np.ndarray) -> np.ndarray:
    c = np.clip(c, 0.0, 1.0)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * c ** (1.0 / 2.4) - 0.055)


def parse_dye_colors(value) -> list[str] | None:
    """The natural colors of a dye mask's channels, "#RRGGBB;#RRGGBB", or None when malformed."""
    if not isinstance(value, str):
        return None
    colors = value.split(";")
    if not 1 <= len(colors) <= MAX_DYES or any(
            len(c) != 7 or c[0] != "#" or any(ch not in "0123456789ABCDEF" for ch in c[1:]) for c in colors):
        return None
    return colors


def color_linear(hex_color: str) -> np.ndarray:
    return srgb_to_linear(np.array([int(hex_color[i:i + 2], 16) / 255.0 for i in (1, 3, 5)], np.float64))


def _hex(linear: np.ndarray) -> str:
    return "#" + "".join(f"{int(round(v * 255.0)):02X}" for v in linear_to_srgb(linear))


def _pixels(image: bpy.types.Image) -> np.ndarray:
    """The image's stored values, height by width by four, sRGB-encoded for a color image."""
    width, height = image.size
    pixels = np.empty(width * height * 4, np.float32)
    image.pixels.foreach_get(pixels)
    return pixels.reshape(height, width, 4).astype(np.float64)


def dye_factor(mask: np.ndarray, colors: list[str]) -> np.ndarray:
    """What the garment shader multiplies the painted color by: per channel of the mask, the dye
    color as far as the channel reaches, height by width by three."""
    factor = np.ones(mask.shape[:2] + (3,), np.float64)
    for channel, color in enumerate(colors):
        reach = mask[..., channel:channel + 1]
        factor *= 1.0 - reach + reach * color_linear(color)
    return factor


def material_name(name: str) -> str:
    return garment.material_name(name)


def uv_fingerprint(mesh: bpy.types.Mesh) -> str:
    """SHA-256 of a mesh's faces and UVs, which a texture is painted in."""
    digest = hashlib.sha256()
    digest.update(np.array([v for p in mesh.polygons for v in p.vertices], np.int64).tobytes())
    if mesh.uv_layers.active is not None:
        uv = np.empty(len(mesh.loops) * 2, np.float32)
        mesh.uv_layers.active.data.foreach_get("uv", uv)
        digest.update((np.round(uv, 5) + 0.0).tobytes())
    return digest.hexdigest()


def _template_first(template_file: str, template: str, label: str) -> bpy.types.Mesh:
    """The template's first version as its own file holds it now, appended."""
    wanted = garment.version_name(template, label)
    with bpy.data.libraries.load(template_file, link=False) as (source, target):
        if wanted not in source.meshes:
            raise ArtPipelineError(f'{template_file} has no first version "{wanted}".')
        target.meshes = [wanted]
    return target.meshes[0]


def current_template_uvs(template_file: str, template: str, label: str) -> str:
    mesh = _template_first(template_file, template, label)
    try:
        return uv_fingerprint(mesh)
    finally:
        bpy.data.meshes.remove(mesh)


def _template_cage(template_file: str, label: str) -> bpy.types.Mesh | None:
    """The cage's version on the template's first body as the template's file holds it now,
    appended, or None for a file without one."""
    wanted = cage.version_name(label)
    with bpy.data.libraries.load(template_file, link=False) as (source, target):
        if wanted not in source.meshes:
            return None
        target.meshes = [wanted]
    return target.meshes[0]


def _keep_cage(mesh: bpy.types.Mesh | None) -> None:
    """Keeps the cage version the preview lies on in the file, hidden, in place of an earlier one."""
    old = bpy.data.objects.get(PREVIEW_CAGE)
    if old is not None:
        old_mesh = old.data
        bpy.data.objects.remove(old, do_unlink=True)
        if old_mesh.users == 0:
            bpy.data.meshes.remove(old_mesh)
    if mesh is None:
        return
    mesh.name = PREVIEW_CAGE
    obj = bpy.data.objects.new(PREVIEW_CAGE, mesh)
    bpy.context.scene.collection.objects.link(obj)
    obj.hide_select = True
    obj.hide_render = True
    obj.hide_viewport = True


def _faces(mesh: bpy.types.Mesh) -> list[tuple[int, ...]]:
    return [tuple(p.vertices) for p in mesh.polygons]


def _corner_uvs(mesh: bpy.types.Mesh) -> list[list[tuple[float, float]]]:
    """The UVs of every face's corners, in their order."""
    uv = bodies._uvs(mesh)
    return [[tuple(float(x) for x in uv[loop]) for loop in p.loop_indices] for p in mesh.polygons]


def _uv_area(face: np.ndarray) -> float:
    """The area of a face in UV space, by the shoelace formula."""
    return 0.5 * abs(float(np.dot(face[:, 0], np.roll(face[:, 1], -1)) - np.dot(face[:, 1], np.roll(face[:, 0], -1))))


def in_uv_face(points: np.ndarray, face: np.ndarray, tolerance: float = 1e-5) -> np.ndarray:
    """Whether each point lies inside a face in UV space or within the tolerance of its outline."""
    points = np.asarray(points, float).reshape(-1, 2)
    start, end = face, np.roll(face, -1, axis=0)
    along = end - start
    t = np.clip(((points[:, None] - start[None]) * along[None]).sum(axis=2) / np.maximum((along ** 2).sum(axis=1), 1e-30)[None],
                0.0, 1.0)
    on = np.linalg.norm(points[:, None] - start[None] - t[:, :, None] * along[None], axis=2).min(axis=1) <= tolerance
    # Inside where a ray toward +u crosses the outline an odd number of times.
    y = points[:, 1:2]
    crosses = (start[None, :, 1] > y) != (end[None, :, 1] > y)
    height = np.where(crosses, end[None, :, 1] - start[None, :, 1], 1.0)
    at = start[None, :, 0] + (y - start[None, :, 1]) * along[None, :, 0] / height
    inside = (crosses & (points[:, 0:1] < at)).sum(axis=1) % 2 == 1
    return on | inside


def refines(uvs, other_uvs, tolerance: float = 1e-5) -> bool:
    """Whether the faces of other UVs refine the faces of the first, as edge loops that cut faces and
    move no vertex leave them: every other face lies in UV space inside one of the first faces, with
    its corners in it or on its outline, every corner UV of the first faces is one of the others',
    and together the other faces cover as much as the first. An image painted in the first UVs then
    lies in the second where it lay."""
    old_faces = [np.array(face, float).reshape(-1, 2) for face in uvs]
    new_faces = [np.array(face, float).reshape(-1, 2) for face in other_uvs]
    if not old_faces or not new_faces:
        return False
    old = np.concatenate(old_faces)
    new = np.concatenate(new_faces)
    for first in range(0, len(old), 256):
        if (np.linalg.norm(old[first:first + 256, None] - new[None], axis=2).min(axis=1) > tolerance).any():
            return False
    old_area = sum(_uv_area(face) for face in old_faces)
    if abs(sum(_uv_area(face) for face in new_faces) - old_area) > 1e-4 * max(old_area, 1e-12):
        return False
    low = np.array([face.min(axis=0) for face in old_faces]) - tolerance
    high = np.array([face.max(axis=0) for face in old_faces]) + tolerance
    for face in new_faces:
        centre = face.mean(axis=0)
        candidates = np.nonzero(((low <= centre) & (centre <= high)).all(axis=1))[0]
        if not any(in_uv_face(face, old_faces[k], tolerance).all() and in_uv_face(centre, old_faces[k], tolerance).all()
                   for k in candidates):
            return False
    return True


def _uv_weights(point: np.ndarray, face: np.ndarray) -> tuple[int, np.ndarray] | None:
    """The triangle of a face's fan from its first corner that holds a point in UV space, as the index
    of its middle corner, and the point's barycentric weights in it, or None."""
    for i in range(1, len(face) - 1):
        a, b, c = face[0], face[i], face[i + 1]
        frame = np.array([b - a, c - a]).T
        if abs(np.linalg.det(frame)) < 1e-18:
            continue
        u, v = np.linalg.solve(frame, point - a)
        if u >= -1e-6 and v >= -1e-6 and u + v <= 1.0 + 1e-6:
            return i, np.array([1.0 - u - v, u, v])
    return None


def places_match(uvs, co, faces, other_uvs, other_co, other_faces, reach: float = PROJECTION_REACH,
                 tolerance: float = 1e-5) -> bool:
    """Whether every other face lies on the garment where the first face that holds it in UV space
    lay: the point of that face at the other face's UV centre, by the corners of the fan triangle
    that holds it at their old positions, lies within the reach of the other face's centre at its
    new positions. UVs alone do not tell a face that kept its place from one whose UV island another
    part of the garment now has, such as the other glove's."""
    old_faces = [np.array(face, float).reshape(-1, 2) for face in uvs]
    co, other_co = np.asarray(co, float), np.asarray(other_co, float)
    low = np.array([face.min(axis=0) for face in old_faces]) - tolerance
    high = np.array([face.max(axis=0) for face in old_faces]) + tolerance
    for face_uvs, face in zip(other_uvs, other_faces):
        centre = np.array(face_uvs, float).reshape(-1, 2).mean(axis=0)
        here = other_co[list(face)].mean(axis=0)
        found = False
        for k in np.nonzero(((low <= centre) & (centre <= high)).all(axis=1))[0]:
            held = _uv_weights(centre, old_faces[k])
            if held is None:
                continue
            i, weights = held
            corners = [faces[k][0], faces[k][i], faces[k][i + 1]]
            if np.linalg.norm(weights @ co[corners] - here) <= reach:
                found = True
                break
        if not found:
            return False
    return True


def same_corners(faces, uvs, other_faces, other_uvs, tolerance: float = 1e-5) -> bool:
    """Whether two meshes have the same faces in the same UVs, each face's corners at most turned to
    start from another of them: the same vertices in the same winding, every corner with its UV."""
    if len(faces) != len(other_faces):
        return False
    for face, uv, other, other_uv in zip(faces, uvs, other_faces, other_uvs):
        if len(face) != len(other) or not face or face[0] not in other:
            return False
        turn = list(other).index(face[0])
        order = [(turn + k) % len(other) for k in range(len(other))]
        if [other[k] for k in order] != list(face):
            return False
        if any(abs(a - b) > tolerance for k, corner in zip(order, uv) for a, b in zip(corner, other_uv[k])):
            return False
    return True


def open_loops(faces: list[tuple[int, ...]]) -> list[tuple[list[int], bool]]:
    """The open edges of faces as chains of vertices in their order, and whether each closes."""
    count: dict[tuple[int, int], int] = {}
    for face in faces:
        for i in range(len(face)):
            key = (min(face[i], face[i - 1]), max(face[i], face[i - 1]))
            count[key] = count.get(key, 0) + 1
    around: dict[int, list[int]] = {}
    for (a, b), n in sorted(count.items()):
        if n == 1:
            around.setdefault(a, []).append(b)
            around.setdefault(b, []).append(a)
    found, seen = [], set()
    for start in sorted(around):
        if start in seen:
            continue
        loop, current = [start], start
        seen.add(start)
        while True:
            ahead = [n for n in around[current] if n not in seen]
            if not ahead:
                break
            current = ahead[0]
            loop.append(current)
            seen.add(current)
        found.append((loop, len(loop) > 2 and loop[0] in around[loop[-1]]))
    return found


def hang_lines(faces: list[tuple[int, ...]], loose: np.ndarray | None,
               top: np.ndarray | None) -> list[tuple[list[int], bool]]:
    """The edge every loose part hangs from, in its order along the edge, and whether it closes."""
    if loose is None or top is None or not (loose < 0).any():
        return []
    edges = {(min(f[i], f[i - 1]), max(f[i], f[i - 1])) for f in faces for i in range(len(f))}
    found = []
    for value in sorted({int(v) for v in loose[loose < 0]}, reverse=True):
        members = np.nonzero(loose == value)[0]
        members = [int(m) for m in members[np.argsort(top[members], kind="stable")]]
        found.append((members, len(members) > 2 and (min(members[0], members[-1]), max(members[0], members[-1])) in edges))
    return found


def nearest_on(points: np.ndarray, line: np.ndarray, closed: bool) -> tuple[np.ndarray, np.ndarray]:
    """For each point the nearest point of a line through the given points, and its distance."""
    a = line if closed else line[:-1]
    b = np.roll(line, -1, axis=0) if closed else line[1:]
    ab = b - a
    lengths = np.maximum((ab ** 2).sum(axis=1), 1e-18)
    out = np.empty_like(np.asarray(points, float))
    far = np.empty(len(points))
    for i, p in enumerate(points):
        t = np.clip(((p - a) * ab).sum(axis=1) / lengths, 0.0, 1.0)
        candidates = a + ab * t[:, None]
        d = ((candidates - p) ** 2).sum(axis=1)
        k = int(np.argmin(d))
        out[i], far[i] = candidates[k], np.sqrt(d[k])
    return out, far


def spread(count: int, faces: list[tuple[int, ...]], fixed: np.ndarray, moves: np.ndarray) -> np.ndarray:
    """The moves of the fixed vertices spread evenly over the others: every free vertex takes the
    mean of its neighbours' moves, and one that no fixed vertex is joined to stays where it is."""
    laplace = np.zeros((count, count))
    for face in faces:
        for i in range(len(face)):
            a, b = face[i], face[i - 1]
            if a != b:
                laplace[a, b] = laplace[b, a] = -1.0
    np.fill_diagonal(laplace, -laplace.sum(axis=1))
    out = np.where(fixed[:, None], moves, 0.0)
    free = ~fixed
    if fixed.any() and free.any():
        # The small term keeps the system solvable for a piece that no fixed vertex is joined to.
        system = laplace[np.ix_(free, free)] + np.eye(int(free.sum())) * 1e-9
        out[free] = np.linalg.solve(system, -laplace[np.ix_(free, fixed)] @ out[fixed])
    return out


def drawn(co: np.ndarray, faces: list[tuple[int, ...]], curves: list[tuple[str, list[int], bool]],
          theirs: list[tuple[str, np.ndarray, bool]], reach: float = CURVE_REACH) -> tuple[np.ndarray, list[str]]:
    """The positions with each feature curve, as its kind, its vertices, and whether it closes, drawn
    onto the nearest curve of its kind among theirs, and that move spread evenly over the vertices
    between the curves, with a note on each curve. An open edge goes before the line a loose part
    hangs from where a vertex lies on both. A curve farther than the reach from every one of theirs,
    on average, stays, and so does what it holds."""
    fixed = np.zeros(len(co), bool)
    moves = np.zeros_like(np.asarray(co, float))
    notes = []
    for kind, loop, _closed in sorted(curves, key=lambda c: c[0] != "hang"):
        best = None
        for other_kind, line, other_closed in theirs:
            if other_kind != kind or len(line) < 2:
                continue
            onto, far = nearest_on(co[loop], line, other_closed)
            if best is None or far.mean() < best[1].mean():
                best = (onto, far)
        if best is None or best[1].mean() > reach:
            notes.append(f"one of its {kind} curves, of {len(loop)} vertices, finds none of the template within "
                         f"{reach * 1000:.0f} mm and stays")
            moves[loop] = 0.0
            fixed[loop] = True
            continue
        notes.append(f"one of its {kind} curves, of {len(loop)} vertices, lay {best[1].mean() * 1000:.1f} mm from the "
                     f"template's on average, at most {best[1].max() * 1000:.1f} mm")
        moves[loop] = best[0] - co[loop]
        fixed[loop] = True
    return co + spread(len(co), faces, fixed, moves), notes


def _curves(mesh: bpy.types.Mesh) -> list[tuple[str, list[int], bool]]:
    """A template's feature curves: its open edges and the lines its loose parts hang from."""
    faces = _faces(mesh)
    lines = hang_lines(faces, garment._attribute(mesh, hang.HANG, "INT"), garment._attribute(mesh, hang.HANG_TOP, "INT"))
    edges = [("edge", loop, closed) for loop, closed in open_loops(faces)]
    return edges + [("hang", loop, closed) for loop, closed in lines]


def followed(preview: bpy.types.Object, first: bpy.types.Mesh, recipe, cage_before: bpy.types.Object | None,
             cage_now: bpy.types.Object | None) -> tuple[np.ndarray, list[str]]:
    """Where the preview's vertices lie on the template as it is now, and notes on how they came
    there: on the template's own vertices when it still has the preview's faces; else moved with
    the cage, from the version the preview lay on to the version of now, cloth on the cage face by
    face, a loose part as a whole with the edge it hangs from, and a modeled form with the cage's
    smooth field, and then drawn onto the template's feature curves."""
    points = bodies.world_positions(preview)
    mesh = preview.data
    target = paint._positions(first)
    if _faces(mesh) == _faces(first):
        return target.copy(), ["Its preview takes the template's shape vertex by vertex: the template has its faces."]
    moved = points.copy()
    notes = []
    if cage_before is None or cage_now is None:
        notes.append("The file has no record of the cage its preview lay on, so the preview is taken to lie on the "
                     "template's cage as it is now.")
    else:
        before, now = bodies.world_positions(cage_before), bodies.world_positions(cage_now)
        if len(before) != len(now) or _faces(cage_before.data) != _faces(cage_now.data):
            notes.append("The cage its preview lay on has other faces than the template's cage, so the preview does not "
                         "move with it.")
        elif np.abs(before - now).max(initial=0.0) > garment.FIT_TOLERANCE:
            if isinstance(recipe, dict):
                regions = garment.recipe_regions(recipe)
                old, new = garment.Surface(cage_before, [], regions), garment.Surface(cage_now, [], regions)
                loose = garment._attribute(mesh, hang.HANG, "INT")
                hanging = loose > 0 if loose is not None else np.zeros(len(points), bool)
                for i in np.nonzero(~hanging)[0]:
                    moved[i] = tuple(new.place(*old.bind(Vector(points[i]))))
                for value in sorted({int(v) for v in loose[hanging]}) if hanging.any() else []:
                    edge = loose == -value
                    if edge.any():
                        moved[loose == value] += (moved[edge] - points[edge]).mean(axis=0)
            else:
                moved = garment.field_place(before, now, points)
            shift = np.linalg.norm(moved - points, axis=1)
            notes.append(f"Its preview moved with the cage by {shift.mean() * 1000:.1f} mm on average, at most "
                         f"{shift.max() * 1000:.1f} mm.")
    theirs = [(kind, target[loop], closed) for kind, loop, closed in _curves(first)]
    moved, curve_notes = drawn(moved, _faces(mesh), _curves(mesh), theirs)
    return moved, notes + [f"Drawn onto the template: {note}." for note in curve_notes]


def _sources(texels: paint.Texels, first: bpy.types.Mesh, old_mesh: bpy.types.Mesh,
             old_co: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """Per texel of the template the UVs of the old mesh at the nearest point of its surface at the
    given positions, and how far away that point lies: the nearest of the faces that face the
    texel's way within SIDE_REACH beyond the nearest of all, else the nearest of all."""
    def normals(co: np.ndarray, corners: np.ndarray) -> np.ndarray:
        value = np.cross(co[corners[:, 1]] - co[corners[:, 0]], co[corners[:, 2]] - co[corners[:, 0]])
        return value / np.maximum(np.linalg.norm(value, axis=1), 1e-18)[:, None]

    _old_faces, old_loops = paint._loop_triangles(old_mesh)
    old_uv = bodies._uvs(old_mesh)
    old_corners = paint._loop_vertices(old_mesh)[old_loops]
    old_normals = normals(old_co, old_corners)
    tree = BVHTree.FromPolygons([tuple(p) for p in old_co], [tuple(int(v) for v in c) for c in old_corners])
    co = paint._positions(first)
    corners = paint._loop_vertices(first)[texels.loops]
    ways = normals(co, corners)[texels.triangles]
    points = (co[corners[texels.triangles]] * texels.weights[:, :, None]).sum(axis=1)
    uv = np.zeros((len(points), 2))
    far = np.zeros(len(points))
    for i, point in enumerate(points):
        vector = Vector(point)
        location, _normal, index, distance = tree.find_nearest(vector)
        if float(old_normals[index] @ ways[i]) <= 0.0:
            facing = [(d, found, k) for found, _n, k, d in tree.find_nearest_range(vector, distance + SIDE_REACH)
                      if float(old_normals[k] @ ways[i]) > 0.0]
            if facing:
                distance, location, index = min(facing, key=lambda item: item[0])
        weights = poly_3d_calc([Vector(old_co[v]) for v in old_corners[index]], location)
        uv[i] = (old_uv[old_loops[index]] * np.array(weights)[:, None]).sum(axis=0)
        far[i] = distance
    return uv, far


def sampled(rgba: np.ndarray, uv: np.ndarray) -> np.ndarray:
    """The image at the UVs, every channel filtered between its four nearest texels on its own, so
    the color under a hole stays what it was painted and never darkens the hole's edge."""
    height, width = rgba.shape[:2]
    x = uv[:, 0] * width - 0.5
    y = uv[:, 1] * height - 0.5
    x0, y0 = np.floor(x).astype(np.int64), np.floor(y).astype(np.int64)
    fx, fy = (x - x0)[:, None], (y - y0)[:, None]

    def at(ix, iy):
        return rgba[iy.clip(0, height - 1), ix.clip(0, width - 1)]

    return (at(x0, y0) * (1 - fx) * (1 - fy) + at(x0 + 1, y0) * fx * (1 - fy)
            + at(x0, y0 + 1) * (1 - fx) * fy + at(x0 + 1, y0 + 1) * fx * fy)


def grown(rgba: np.ndarray, filled: np.ndarray, margin: int = BAKE_MARGIN) -> np.ndarray:
    """The image with its filled texels grown over the empty ones around them by the margin, each
    taking the mean of its filled neighbours, so filtering at a UV seam takes paint, and the texels
    beyond the margin black and opaque, as a bake leaves them."""
    out = np.array(rgba, np.float32)
    known = filled.copy()
    for _ in range(margin):
        total = np.zeros_like(out)
        number = np.zeros(out.shape[:2], np.float32)
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                if dy == 0 and dx == 0:
                    continue
                source = (slice(max(dy, 0), out.shape[0] + min(dy, 0)), slice(max(dx, 0), out.shape[1] + min(dx, 0)))
                target = (slice(max(-dy, 0), out.shape[0] + min(-dy, 0)), slice(max(-dx, 0), out.shape[1] + min(-dx, 0)))
                total[target] += out[source] * known[source][..., None]
                number[target] += known[source]
        grow = ~known & (number > 0)
        if not grow.any():
            break
        out[grow] = total[grow] / number[grow][:, None]
        known |= grow
    out[~known] = (0.0, 0.0, 0.0, 1.0)
    return out


def _move_image(image: bpy.types.Image, texels: paint.Texels, uv: np.ndarray, opaque: bool) -> None:
    """Writes the image again in the texels' UVs, each texel from the image at its old UVs."""
    width, height = image.size
    rgba = np.zeros((height * width, 4), np.float32)
    filled = np.zeros(height * width, bool)
    rgba[texels.places] = sampled(_pixels(image), uv)
    filled[texels.places] = True
    rgba = grown(rgba.reshape(height, width, 4), filled.reshape(height, width))
    if opaque:
        rgba[..., 3] = 1.0
    image.pixels.foreach_set(rgba.clip(0.0, 1.0).reshape(-1))
    image.update()


def has_holes(image: bpy.types.Image) -> bool:
    """Whether the image has transparent parts, where the garment shader cuts the garment."""
    pixels = np.empty(image.size[0] * image.size[1] * 4, np.float32)
    image.pixels.foreach_get(pixels)
    return bool((pixels[3::4] < ALPHA_CUT).any())


def _socket(sockets, identifier: str):
    return next(s for s in sockets if s.identifier == identifier)


def _material(name: str, image: bpy.types.Image, dye: bpy.types.Image | None = None) -> bpy.types.Material:
    """The texture's material: its image as the color, tinted by the natural colors of its dye mask
    as the garment shader tints it, with its roughness and metallic, and its image's alpha where it
    has transparent parts, drawn from both sides like the game draws garments."""
    material = bpy.data.materials.get(material_name(name)) or bpy.data.materials.new(material_name(name))
    material.use_nodes = True
    nodes, links = material.node_tree.nodes, material.node_tree.links
    bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
    for node in [n for n in nodes if n.type not in ("BSDF_PRINCIPLED", "OUTPUT_MATERIAL")]:
        nodes.remove(node)
    texture = nodes.new("ShaderNodeTexImage")
    texture.image = image
    color = texture.outputs["Color"]
    colors = parse_dye_colors(dye.get(DYE_COLORS)) if dye is not None else None
    if colors:
        dye.colorspace_settings.name = "Non-Color"
        mask = nodes.new("ShaderNodeTexImage")
        mask.name = "DyeMask"
        mask.image = dye
        channels = nodes.new("ShaderNodeSeparateColor")
        links.new(mask.outputs["Color"], channels.inputs["Color"])
        for channel, hex_color in enumerate(colors):
            tint = nodes.new("ShaderNodeMix")
            tint.data_type = "RGBA"
            tint.blend_type = "MULTIPLY"
            links.new(channels.outputs[channel], _socket(tint.inputs, "Factor_Float"))
            links.new(color, _socket(tint.inputs, "A_Color"))
            _socket(tint.inputs, "B_Color").default_value = (*color_linear(hex_color), 1.0)
            color = _socket(tint.outputs, "Result_Color")
    links.new(color, bsdf.inputs["Base Color"])
    for link in [link for link in links if link.to_socket == bsdf.inputs["Alpha"]]:
        links.remove(link)
    if has_holes(image):
        # The masks read the alpha through this link, as they read a garment's.
        links.new(texture.outputs["Alpha"], bsdf.inputs["Alpha"])
    bsdf.inputs["Roughness"].default_value = float(image.get("roughness", 0.6))
    bsdf.inputs["Metallic"].default_value = float(image.get("metallic", 0.0))
    material.use_backface_culling = False
    return material


def _preview(name: str, mesh: bpy.types.Mesh, image: bpy.types.Image, dye: bpy.types.Image | None = None) -> bpy.types.Object:
    old = bpy.data.objects.get(PREVIEW)
    if old is not None:
        old_mesh = old.data
        bpy.data.objects.remove(old, do_unlink=True)
        if old_mesh.users == 0:
            bpy.data.meshes.remove(old_mesh)
    mesh.name = PREVIEW
    for attribute in [a.name for a in mesh.attributes if a.name in (garment.FITTED, garment.BUILT, garment.TRANSFERRED)]:
        mesh.attributes.remove(mesh.attributes[attribute])
    obj = bpy.data.objects.new(PREVIEW, mesh)
    bpy.context.scene.collection.objects.link(obj)
    mesh.materials.clear()
    mesh.materials.append(_material(name, image, dye))
    for polygon in mesh.polygons:
        polygon.material_index = 0
    return obj


def _alpha_link(obj: bpy.types.Object):
    """The link that cuts the object's material by the alpha of its image, or None."""
    material = obj.data.materials[0] if obj.data.materials else None
    if material is None or not material.use_nodes:
        return None
    return next((link for link in material.node_tree.links if link.to_socket.name == "Alpha"
                 and link.from_node.type == "TEX_IMAGE" and link.from_node.image is not None), None)


def _bake_pass(source: bpy.types.Object, target: bpy.types.Object, image: bpy.types.Image, kind: str) -> None:
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.samples = 1
    scene.cycles.device = "CPU"
    bake_target = target.data.materials[0].node_tree
    node = bake_target.nodes.new("ShaderNodeTexImage")
    node.image = image
    bake_target.nodes.active = node
    for obj in scene.objects:
        obj.select_set(False)
    source.hide_set(False)
    target.hide_set(False)
    source.select_set(True)
    target.select_set(True)
    bpy.context.view_layer.objects.active = target
    try:
        if kind == "COLOR":
            bpy.ops.object.bake(type="DIFFUSE", pass_filter={"COLOR"}, use_selected_to_active=True,
                                cage_extrusion=PROJECTION_REACH, max_ray_distance=2.0 * PROJECTION_REACH,
                                margin=BAKE_MARGIN, use_clear=True)
        else:
            bpy.ops.object.bake(type="EMIT", use_selected_to_active=True, cage_extrusion=PROJECTION_REACH,
                                max_ray_distance=2.0 * PROJECTION_REACH, margin=BAKE_MARGIN, use_clear=True)
    finally:
        bake_target.nodes.remove(node)


def _bake(source: bpy.types.Object, target: bpy.types.Object, image: bpy.types.Image) -> None:
    """The look of the source, its material's base color where it shows on its surface, baked into
    the image in the target's UVs by rays from the target's surface, and the alpha of the source's
    image too where it cuts the source, so its holes move with it. The color is baked with the
    source opaque, so a transparent part does not darken its edge."""
    link = _alpha_link(source)
    if link is None:
        _bake_pass(source, target, image, "COLOR")
        # An opaque look stays opaque: the bake leaves the texels outside the UV islands clear.
        width, height = image.size
        pixels = np.empty(width * height * 4, np.float32)
        image.pixels.foreach_get(pixels)
        pixels[3::4] = 1.0
        image.pixels.foreach_set(pixels)
        image.update()
        return
    links = source.data.materials[0].node_tree.links
    from_node, to_socket = link.from_node, link.to_socket
    alpha_image = from_node.image
    links.remove(link)
    try:
        _bake_pass(source, target, image, "COLOR")
    finally:
        links.new(from_node.outputs["Alpha"], to_socket)
    # The alpha as the emission of a material of its own, baked the same way.
    shown = bpy.data.materials.new("AlphaShown")
    shown.use_nodes = True
    nodes = shown.node_tree.nodes
    for node in list(nodes):
        nodes.remove(node)
    texture = nodes.new("ShaderNodeTexImage")
    texture.image = alpha_image
    emission = nodes.new("ShaderNodeEmission")
    output = nodes.new("ShaderNodeOutputMaterial")
    shown.node_tree.links.new(texture.outputs["Alpha"], emission.inputs["Color"])
    shown.node_tree.links.new(emission.outputs["Emission"], output.inputs["Surface"])
    width, height = image.size
    baked = bpy.data.images.new("AlphaBaked", width, height, alpha=True, float_buffer=True)
    kept = list(source.data.materials)
    try:
        for slot in range(len(source.data.materials)):
            source.data.materials[slot] = shown
        _bake_pass(source, target, baked, "ALPHA")
    finally:
        for slot, original in enumerate(kept):
            source.data.materials[slot] = original
        bpy.data.materials.remove(shown)
    alpha = np.empty(width * height * 4, np.float32)
    baked.pixels.foreach_get(alpha)
    bpy.data.images.remove(baked)
    pixels = np.empty(width * height * 4, np.float32)
    image.pixels.foreach_get(pixels)
    # A texel no ray reached stays opaque.
    reached = alpha[3::4] > 0.5
    pixels[3::4] = np.where(reached, np.clip(alpha[0::4], 0.0, 1.0), 1.0)
    image.pixels.foreach_set(pixels)
    image.update()


def _set_linear(image: bpy.types.Image, color: np.ndarray, alpha: np.ndarray) -> None:
    """Stores linear colors in an sRGB image with its alpha."""
    pixels = np.concatenate([linear_to_srgb(color), alpha[..., None]], axis=2)
    image.pixels.foreach_set(pixels.astype(np.float32).ravel())
    image.update()


def _new_dye_image(name: str, width: int, height: int) -> bpy.types.Image:
    dye = bpy.data.images.new(dye_image_name(name), width, height, alpha=False)
    dye.colorspace_settings.name = "Non-Color"
    dye.file_format = "PNG"
    return dye


def set_dyes(file: str, name: str, regions_file: str | None, level: float) -> list[str]:
    """Makes a texture dyeable, or sets its dyes again: the regions image marks per channel, in red,
    green, and blue, the texels a dye color tints, or every texel is in the first channel when there
    is none. A channel's natural color is its region's middle tone divided by the level, so its
    middle tone lies at the level in the neutral image, and the image is divided by what the shader
    multiplies it by, so the texture undyed looks exactly as it was painted. A texel too light to be
    divided so keeps part of its painted color: its mask reaches only as far as its light allows.
    Returns the natural colors and saves the file."""
    if not 0.05 <= level <= 1.0:
        raise ArtPipelineError(f"The dye level {level} must lie from 0.05 to 1.")
    bpy.ops.wm.open_mainfile(filepath=file)
    image = bpy.data.images.get(image_name(name))
    if image is None:
        raise ArtPipelineError(f'The texture needs its image "{image_name(name)}".')
    height, width = image.size[1], image.size[0]
    pixels = _pixels(image)
    painted = srgb_to_linear(pixels[..., :3])
    alpha = pixels[..., 3]
    old = bpy.data.images.get(dye_image_name(name))
    old_colors = parse_dye_colors(old.get(DYE_COLORS)) if old is not None else None
    if old is not None and old_colors:
        # The look as painted: the neutral image times its natural colors.
        old.colorspace_settings.name = "Non-Color"
        painted = painted * dye_factor(_pixels(old)[..., :3], old_colors)
    if regions_file:
        regions_image = bpy.data.images.load(regions_file)
        regions_image.colorspace_settings.name = "Non-Color"
        if tuple(regions_image.size) != (width, height):
            raise ArtPipelineError(f"The regions image is {tuple(regions_image.size)} pixels; the texture is {(width, height)}.")
        regions = np.clip(_pixels(regions_image)[..., :3], 0.0, 1.0)
        bpy.data.images.remove(regions_image)
    else:
        regions = np.zeros((height, width, 3), np.float64)
        regions[..., 0] = 1.0
    used = [channel for channel in range(MAX_DYES) if regions[..., channel].max() > DYE_UNUSED]
    if not used or used != list(range(len(used))):
        raise ArtPipelineError("The regions must use the red channel, then green, then blue, without a gap.")
    if (regions.sum(axis=2) > 1.0 + DYE_UNUSED).any():
        raise ArtPipelineError("The regions of the dye channels must not overlap.")
    opaque = alpha >= ALPHA_CUT
    colors, mask = [], np.zeros((height, width, 3), np.float64)
    for channel in used:
        region = regions[..., channel]
        middle = np.median(painted[(region > 0.5) & opaque], axis=0)
        natural = middle / level
        if (natural > 1.0).any():
            raise ArtPipelineError(f"Channel {channel + 1} is too light for the level {level}: its middle tone is "
                                   f"{_hex(middle)}. Use a higher level.")
        hex_color = _hex(natural)
        colors.append(hex_color)
        natural = color_linear(hex_color)
        # The neutral texel is the painted one over 1 - m + m * natural, and stays at most 1.
        room = np.min((1.0 - painted) / np.maximum(1.0 - natural, 1e-6), axis=2)
        mask[..., channel] = region * np.clip(room, 0.0, 1.0)
    # The mask as it is stored, so the image is divided by what the shader reads.
    mask = np.round(mask * 255.0) / 255.0
    neutral = np.clip(painted / dye_factor(mask, colors), 0.0, 1.0)
    _set_linear(image, neutral, alpha)
    image.pack()
    if old is not None:
        bpy.data.images.remove(old)
    dye = _new_dye_image(name, width, height)
    dye.pixels.foreach_set(np.concatenate([mask, np.ones((height, width, 1))], axis=2).astype(np.float32).ravel())
    dye.update()
    dye.pack()
    dye[DYE_COLORS] = ";".join(colors)
    preview = bpy.data.objects.get(PREVIEW)
    if preview is not None and preview.type == "MESH":
        preview.data.materials[0] = _material(name, image, dye)
    partial = int(((mask < 0.999) & (regions > 0.5) & opaque[..., None]).any(axis=2).sum())
    print(f"ART_REPORT {name}: natural colors {', '.join(colors)}; {partial} texels too light to be dyed in full.")
    bpy.data.orphans_purge(do_recursive=True)
    relative_image_paths()
    bpy.ops.wm.save_as_mainfile(filepath=file, compress=True, relative_remap=False)
    return colors


def create(file: str, name: str, template_file: str, template: str, label: str, size: int,
           source_file: str | None, source_object: str | None) -> None:
    """Writes the first file of a texture: its image, a neutral light grey or the look of another
    mesh moved over by projection, the preview on the template's first version, and the cage
    version the preview lies on."""
    if size not in SIZES:
        raise ArtPipelineError(f"A garment texture is {', '.join(str(s) for s in SIZES)} pixels square.")
    bpy.ops.wm.read_factory_settings(use_empty=True)
    # With an alpha channel, for the holes a projected look or a person may give it.
    image = bpy.data.images.new(image_name(name), size, size, alpha=True)
    image.generated_color = (0.8, 0.8, 0.8, 1.0)
    image["roughness"] = 0.6
    image["metallic"] = 0.0
    mesh = _template_first(template_file, template, label)
    preview = _preview(name, mesh, image)
    preview[TEMPLATE_UVS] = uv_fingerprint(mesh)
    _keep_cage(_template_cage(template_file, label))
    if source_file:
        with bpy.data.libraries.load(source_file, link=False) as (source, target):
            if source_object not in source.objects:
                raise ArtPipelineError(f'{source_file} has no object "{source_object}".')
            target.objects = [source_object]
        obj = target.objects[0]
        bpy.context.scene.collection.objects.link(obj)
        obj.parent = None
        obj.modifiers.clear()
        _bake(obj, preview, image)
        material = obj.data.materials[0] if obj.data.materials else None
        for node in (material.node_tree.nodes if material is not None and material.use_nodes else []):
            if node.type == "BSDF_PRINCIPLED":
                image["roughness"] = float(node.inputs["Roughness"].default_value)
                image["metallic"] = float(node.inputs["Metallic"].default_value)
        bpy.data.objects.remove(obj, do_unlink=True)
        _material(name, image)
    image.pack()
    bpy.data.orphans_purge(do_recursive=True)
    relative_image_paths()
    bpy.ops.wm.save_as_mainfile(filepath=file, compress=True, relative_remap=False)


def sync(file: str, name: str, template_file: str, template: str, label: str, recipe) -> tuple[bool, list[str]]:
    """Moves the texture to the template's UVs when the template's faces or UVs changed, or when
    the preview's own faces or UVs were edited, so the image is always in the UVs of the model Unity
    draws: the preview follows the template, and every texel of the new UVs takes the paint of the
    nearest point of the moved preview, the dye mask the same way. Returns whether the file changed,
    and notes for the report; an unchanged file is not saved, so its approval stays valid."""
    bpy.ops.wm.open_mainfile(filepath=file)
    preview = bpy.data.objects.get(PREVIEW)
    image = bpy.data.images.get(image_name(name))
    if preview is None or image is None:
        raise ArtPipelineError(f'The texture needs its image "{image_name(name)}" and its "{PREVIEW}".')
    current = current_template_uvs(template_file, template, label)
    dye = bpy.data.images.get(dye_image_name(name))
    colors = parse_dye_colors(dye.get(DYE_COLORS)) if dye is not None else None
    first = _template_first(template_file, template, label)
    cage_mesh = _template_cage(template_file, label)
    # The template has the preview's faces and UVs, with their corners turned at most, or the
    # preview's UV faces, cut by edge loops that moved no vertex or with its vertices numbered anew,
    # as a rebuild on a changed cage numbers them, each where it lay on the garment: the image is
    # painted in its UVs and stays as it is, and the preview takes the template's faces and shape.
    same = preview.get(TEMPLATE_UVS) == current and uv_fingerprint(preview.data) == current
    old_uvs, new_uvs = _corner_uvs(preview.data), _corner_uvs(first)
    turned = not same and same_corners(_faces(preview.data), old_uvs, _faces(first), new_uvs)
    split = not same and not turned and refines(old_uvs, new_uvs) and places_match(
        old_uvs, bodies.world_positions(preview), _faces(preview.data), new_uvs, paint._positions(first), _faces(first))
    renumbered = split and len(old_uvs) == len(new_uvs)
    if same or turned or split:
        moved = len(first.vertices) != len(preview.data.vertices) or \
            np.abs(paint._positions(first) - bodies.world_positions(preview)).max(initial=0.0) > garment.FIT_TOLERANCE
        if same and not moved:
            bpy.data.meshes.remove(first)
            if cage_mesh is not None:
                bpy.data.meshes.remove(cage_mesh)
            return False, []
        new = _preview(name, first, image, dye if colors else None)
        new[TEMPLATE_UVS] = current
        _keep_cage(cage_mesh)
        bpy.data.orphans_purge(do_recursive=True)
        relative_image_paths()
        bpy.ops.wm.save_as_mainfile(filepath=file, compress=True, relative_remap=False)
        why = ("keeps its UV faces, each where it lay, with its vertices numbered anew" if renumbered else
               "cuts its UVs with edge loops that moved none of them, each face where it lay" if split else
               "keeps its faces and UVs with the corners of its faces turned" if turned else "keeps its faces and UVs")
        return True, [f"Its preview takes the template's faces and shape; the template {why}, so the image stays as "
                      "it is."]
    cage_now = bpy.data.objects.new("TemplateCage", cage_mesh) if cage_mesh is not None else None
    moved, notes = followed(preview, first, recipe, bpy.data.objects.get(PREVIEW_CAGE), cage_now)
    if cage_now is not None:
        bpy.data.objects.remove(cage_now)
    texels = paint.Texels(first, None, tuple(image.size), None)
    uv, far = _sources(texels, first, preview.data, moved)
    notes.append(f"{len(texels.places)} texels lie on the template and took their paint from "
                 f"{np.median(far) * 1000:.1f} mm away at the median, {np.percentile(far, 99) * 1000:.1f} mm at 99 %, at most {far.max() * 1000:.1f} mm.")
    _move_image(image, texels, uv, not has_holes(image))
    if colors:
        # The image is the neutral cloth and the mask says where a dye reaches: each moves as it is.
        _move_image(dye, texels, uv, True)
        dye.pack()
    new = _preview(name, first, image, dye if colors else None)
    new[TEMPLATE_UVS] = current
    _keep_cage(cage_mesh)
    image.pack()
    bpy.data.orphans_purge(do_recursive=True)
    relative_image_paths()
    bpy.ops.wm.save_as_mainfile(filepath=file, compress=True, relative_remap=False)
    return True, notes


def dye_errors(name: str, dyes: int) -> list[str]:
    """The rules of a dye mask: packed as a PNG, the size of the image, read as data, with a natural
    color for each channel it uses, no more channels than the template's dyes, and a preview that
    shows it."""
    dye = bpy.data.images.get(dye_image_name(name))
    if dye is None:
        return []
    errors = []
    if dyes == 0:
        errors.append(f'"{dye.name}" dyes the texture, but its template has no dye channels ("dyes": 0).')
    if dye.packed_file is None or not bytes(dye.packed_file.data).startswith(PNG_SIGNATURE):
        errors.append(f'"{dye.name}" must be packed into the file as a PNG.')
    image = bpy.data.images.get(image_name(name))
    if image is not None and tuple(dye.size) != tuple(image.size):
        errors.append(f'"{dye.name}" is {tuple(dye.size)} pixels; it must be the size of "{image.name}".')
    if dye.colorspace_settings.name != "Non-Color":
        errors.append(f'"{dye.name}" must be read as Non-Color data.')
    colors = parse_dye_colors(dye.get(DYE_COLORS))
    if colors is None:
        errors.append(f'"{dye.name}" needs the custom property {DYE_COLORS}, the natural color of each channel it '
                      f'uses, "#RRGGBB" joined by ";", one to {MAX_DYES}.')
        return errors
    if len(colors) > dyes:
        errors.append(f'"{dye.name}" uses {len(colors)} dye channels; its template has {dyes}.')
    mask = _pixels(dye)
    for channel in range(len(colors), MAX_DYES):
        if mask[..., channel].max() > DYE_UNUSED:
            errors.append(f'"{dye.name}" marks texels in channel {channel + 1}, which has no natural color.')
    preview = bpy.data.objects.get(PREVIEW)
    material = preview.data.materials[0] if preview is not None and preview.type == "MESH" and preview.data.materials else None
    shown = material is not None and material.use_nodes and any(
        n.type == "TEX_IMAGE" and n.image == dye for n in material.node_tree.nodes)
    if not shown:
        errors.append(f'"{PREVIEW}" must show "{dye.name}"; run tools/art/Set-TextureDyes.ps1 again.')
    return errors


def texture_errors(name: str, template_file: str, template: str, label: str, dyes: int = 0) -> list[str]:
    """The rules of a texture: its packed square image of a known size with its roughness and
    metallic, its preview showing it, the template's current UVs, and its dye mask, if it has one."""
    errors = dye_errors(name, dyes)
    image = bpy.data.images.get(image_name(name))
    preview = bpy.data.objects.get(PREVIEW)
    if image is None or image.packed_file is None:
        return [f'The texture needs its image "{image_name(name)}" packed into the file.']
    if not bytes(image.packed_file.data).startswith(PNG_SIGNATURE):
        errors.append(f'"{image.name}" must be packed as a PNG, since the export writes it as it is packed.')
    width, height = image.size
    if width != height or width not in SIZES:
        errors.append(f'"{image.name}" is {width} by {height} pixels; a garment texture is '
                      f"{', '.join(str(s) for s in SIZES)} pixels square.")
    for key in ("roughness", "metallic"):
        value = image.get(key)
        if not isinstance(value, (int, float)) or not 0.0 <= value <= 1.0:
            errors.append(f'"{image.name}" needs the custom property {key} from 0 to 1.')
    if preview is None or preview.type != "MESH" or not preview.data.materials or \
            preview.data.materials[0] is None or preview.data.materials[0].name != material_name(name):
        errors.append(f'The texture needs its "{PREVIEW}" with the material "{material_name(name)}".')
    else:
        # The image is painted in the preview's UVs, which must be the template's as they are now.
        current = current_template_uvs(template_file, template, label)
        if preview.get(TEMPLATE_UVS) != current:
            errors.append(f"The template {template} has other UVs than the texture was painted in; run tools/art/Sync-TextureAsset.ps1.")
        elif uv_fingerprint(preview.data) != current:
            errors.append(f'"{PREVIEW}" has other faces or UVs than the template {template}; run tools/art/Sync-TextureAsset.ps1, '
                          "which moves the texture to the template's UVs.")
    return errors


def exported_image(name: str) -> tuple[dict[str, bytes], dict]:
    """The PNGs of the image and of its dye mask, if it has one, by image name, and the material's
    record, read from the open texture file, so they can be written after the masks, which open the
    template's file."""
    image = bpy.data.images[image_name(name)]
    images = {}
    for shown in (image, bpy.data.images.get(dye_image_name(name))):
        if shown is None:
            continue
        data = bytes(shown.packed_file.data) if shown.packed_file is not None else b""
        if not data.startswith(PNG_SIGNATURE):
            raise ArtPipelineError(f'"{shown.name}" must be packed as a PNG.')
        images[shown.name] = data
    record = {"name": material_name(name), "baseColor": "#FFFFFF", "baseMap": image.name,
              "metallic": round(float(image.get("metallic", 0.0)), 4),
              "smoothness": round(1.0 - float(image.get("roughness", 0.6)), 4)}
    if len(images) > 1:
        record["dyeMap"] = dye_image_name(name)
        record["dyeColors"] = parse_dye_colors(bpy.data.images[dye_image_name(name)].get(DYE_COLORS))
    return images, record


def export(name: str, image: tuple[dict[str, bytes], dict], folder: str) -> tuple[list[tuple[str, str]], dict]:
    """Writes the images that exported_image read as PNG files and returns their names and paths and
    the material's record."""
    images, record = image
    os.makedirs(folder, exist_ok=True)
    written = []
    for shown, data in images.items():
        path = os.path.join(folder, f"{shown}.png")
        with open(path, "wb") as handle:
            handle.write(data)
        written.append((shown, path))
    return written, record


def dress(template: str, texture_file: str, name: str, labels: list[str]) -> bool:
    """Puts the texture on every version of the template in the open template file, dyed in its
    natural colors, for its review sheet and its masks. Returns whether the texture has transparent
    parts."""
    with bpy.data.libraries.load(texture_file, link=False) as (source, target):
        if image_name(name) not in source.images:
            raise ArtPipelineError(f'{texture_file} has no image "{image_name(name)}".')
        target.images = [image_name(name)] + ([dye_image_name(name)] if dye_image_name(name) in source.images else [])
    material = _material(name, target.images[0], target.images[1] if len(target.images) > 1 else None)
    for label in labels:
        version = bpy.data.objects[garment.version_name(template, label)]
        for slot in range(len(version.data.materials)):
            version.data.materials[slot] = material
    return has_holes(target.images[0])


def masks(texture_file: str, name: str, template_file: str, template: str, labels: list[str], band: str,
          covers: bool = True, inners: list[tuple[str, str, object, str]] = (), recipe=None, compressed: bool = False, *,
          units) -> tuple[dict[str, list], dict]:
    """The texture's own masks on every body, with the faces it hides of every template of a lower
    layer worn under it, given as its file, its name, its recipe, and its band, one list for every
    body, the skin that comes through the garment in the test poses, and the pair check of every such
    template, computed in the template's file with the texture on every version, so its holes hide
    nothing and the pair check follows what it hides. units, called once the template's file is open
    and before the texture goes on, gives the cached units of the checks, as coverage.checks takes
    them. Returns the hide margins of its open edges that reach each template on every body, and per
    body the masks, the motion check, and the pair checks by template. The template's file is opened
    in place of the texture's and not saved."""
    bpy.ops.wm.open_mainfile(filepath=template_file)
    cached = units()
    dress(template, texture_file, name, labels)
    worn: dict[str, list] = {}

    def versions(inner: str):
        # The templates of lower layers go on the bodies, all in their order, when a check first needs one.
        def get() -> list:
            if not worn:
                for path, other, _recipe, _band in inners:
                    worn[other] = garment.append_inner(path, other, labels, compressed)
            return worn[inner]
        return get

    # A template that covers nothing hides nothing, and its own pair check, with all of its cloth
    # opaque, holds for its textures too.
    own, under = coverage.checks(template, labels, band, covers, recipe,
                                 [(inner, inner_band, inner_recipe, versions(inner)) for _path, inner, inner_recipe, inner_band in inners],
                                 True, cached, pair_checks=covers, motion_first=False)
    margins = {inner: list(zip(labels, check.over)) for inner, check in under.items() if check.over is not None}
    results = {label: (own[label].coverage, own[label].motion,
                       {inner: under[inner].pairs[index] for _path, inner, _recipe, _band in inners} if covers else {})
               for index, label in enumerate(labels)}
    return margins, results


def mask_record(name: str, template: str, result: coverage.Coverage) -> dict:
    """The texture's masks for one body, in the data format of its template's."""
    data = coverage.record(template, result)
    data["garment"] = name
    return data
