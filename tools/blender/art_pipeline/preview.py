"""Renders a review sheet for an asset: one row per exported root object, seen from four
sides at the game camera pitch, plus a fifth view with a 1.8 m mannequin for scale. Only
the Export collection is rendered, on a dark green ground. The sheet of an attachment shows it alone
and held on its socket by the bodies of an animation library. The sheet of an animation library
shows every clip on the bodies of its Reference collection instead: two rows of frames per clip,
from the front right and from the back left. The sheet of a garment shows it on every body, at rest
and in the test poses. The sheet of a paint layer shows it on every body as the game does, up close
where it paints, and its canvas's image."""

import math
import os
import tempfile

import bmesh
import bpy
import numpy as np
from bpy_extras import anim_utils
from mathutils import Matrix, Vector
from mathutils.kdtree import KDTree

from . import bodies, cage, cloth, coverage, garment, paint, poses
from .scene import ArtPipelineError, descendants, export_objects, root_objects

YAWS = (35.0, 125.0, 215.0, 305.0)
SCALE_VIEW_YAW = 20.0
PITCH = 28.0
GROUND_SRGB = (0.19, 0.28, 0.20)
MANNEQUIN_SRGB = (0.62, 0.63, 0.66)
MANNEQUIN_HEIGHT = 1.8


def _srgb_to_linear(c: float) -> float:
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def _flat_material(name: str, srgb: tuple[float, float, float]) -> bpy.types.Material:
    material = bpy.data.materials.new(name)
    if material.node_tree is None:
        material.use_nodes = True
    color = tuple(_srgb_to_linear(c) for c in srgb) + (1.0,)
    bsdf = next(n for n in material.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = color
    bsdf.inputs["Roughness"].default_value = 0.9
    return material


def _set_engine(scene: bpy.types.Scene) -> None:
    for engine in ("BLENDER_EEVEE", "BLENDER_EEVEE_NEXT"):
        try:
            scene.render.engine = engine
            scene.eevee.taa_render_samples = 32
            return
        except TypeError:
            continue
    scene.render.engine = "BLENDER_WORKBENCH"


def _set_world(scene: bpy.types.Scene) -> None:
    world = bpy.data.worlds.new("PreviewWorld")
    if world.node_tree is None:
        world.use_nodes = True
    background = next(n for n in world.node_tree.nodes if n.type == "BACKGROUND")
    background.inputs["Color"].default_value = (0.42, 0.47, 0.55, 1.0)
    background.inputs["Strength"].default_value = 0.9
    scene.world = world


def _add_object(scene: bpy.types.Scene, name: str, data: bpy.types.ID | None) -> bpy.types.Object:
    obj = bpy.data.objects.new(name, data)
    scene.collection.objects.link(obj)
    return obj


def _add_mannequin(scene: bpy.types.Scene, material: bpy.types.Material) -> bpy.types.Object:
    radius = 0.24
    mesh = bpy.data.meshes.new("PreviewMannequin")
    bm = bmesh.new()
    bmesh.ops.create_cone(
        bm, cap_ends=True, segments=16, radius1=radius, radius2=radius,
        depth=MANNEQUIN_HEIGHT - 2 * radius,
        matrix=Matrix.Translation((0.0, 0.0, MANNEQUIN_HEIGHT / 2)))
    for z in (radius, MANNEQUIN_HEIGHT - radius):
        bmesh.ops.create_uvsphere(
            bm, u_segments=16, v_segments=8, radius=radius,
            matrix=Matrix.Translation((0.0, 0.0, z)))
    bm.to_mesh(mesh)
    bm.free()
    mesh.materials.append(material)
    return _add_object(scene, "PreviewMannequin", mesh)


def _bounds(objects: list[bpy.types.Object]) -> tuple[Vector, Vector]:
    corners = [o.matrix_world @ Vector(c) for o in objects if o.type == "MESH" for c in o.bound_box]
    if not corners:
        # A root without meshes still gets a frame around its origin.
        corners = [o.matrix_world.translation for o in objects]
    low = Vector((min(c.x for c in corners), min(c.y for c in corners), min(c.z for c in corners)))
    high = Vector((max(c.x for c in corners), max(c.y for c in corners), max(c.z for c in corners)))
    return low, high


def _aim(camera: bpy.types.Object, sun: bpy.types.Object, center: Vector, radius: float, yaw: float) -> None:
    fov = camera.data.angle
    distance = radius / math.sin(fov / 2.0) * 1.08
    pitch = math.radians(PITCH)
    direction = Vector((math.sin(math.radians(yaw)) * math.cos(pitch),
                        -math.cos(math.radians(yaw)) * math.cos(pitch),
                        math.sin(pitch)))
    camera.location = center + direction * distance
    camera.rotation_euler = (center - camera.location).to_track_quat("-Z", "Y").to_euler()
    # Key light from the upper left of the camera, so every view is lit the same way.
    sun.rotation_euler = (math.radians(50.0), 0.0, math.radians(yaw - 45.0))


def _render(scene: bpy.types.Scene, path: str) -> np.ndarray:
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    image = bpy.data.images.load(path)
    try:
        width, height = image.size
        pixels = np.empty(width * height * 4, dtype=np.float32)
        image.pixels.foreach_get(pixels)
        return pixels.reshape(height, width, 4)
    finally:
        bpy.data.images.remove(image)


def _studio(scene: bpy.types.Scene, cell: int) -> tuple[bpy.types.Object, bpy.types.Object]:
    """Engine, world, output settings, a camera, and a sun; returns the camera and the sun."""
    _set_engine(scene)
    _set_world(scene)
    scene.render.resolution_x = cell
    scene.render.resolution_y = cell
    scene.render.resolution_percentage = 100
    scene.render.film_transparent = False
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "None"

    camera = _add_object(scene, "PreviewCamera", bpy.data.cameras.new("PreviewCamera"))
    camera.data.lens = 50.0
    scene.camera = camera
    sun_data = bpy.data.lights.new("PreviewSun", "SUN")
    sun_data.energy = 3.5
    sun_data.angle = math.radians(8.0)
    sun = _add_object(scene, "PreviewSun", sun_data)
    return camera, sun


def _save(sheet: np.ndarray, output: str) -> str:
    image = bpy.data.images.new("PreviewSheet", sheet.shape[1], sheet.shape[0], alpha=True)
    image.pixels.foreach_set(sheet.ravel())
    image.filepath_raw = output
    image.file_format = "PNG"
    image.save()
    return output


def render_sheet(output: str, cell: int) -> str:
    os.makedirs(os.path.dirname(output), exist_ok=True)
    scene = bpy.context.scene
    exported = export_objects()
    roots = root_objects()
    for obj in scene.objects:
        obj.hide_render = obj not in exported

    camera, sun = _studio(scene, cell)
    ground_mesh = bpy.data.meshes.new("PreviewGround")
    ground_mesh.from_pydata([(-30, -30, 0), (30, -30, 0), (30, 30, 0), (-30, 30, 0)], [], [(0, 1, 2, 3)])
    ground_mesh.materials.append(_flat_material("PreviewGround", GROUND_SRGB))
    _add_object(scene, "PreviewGround", ground_mesh)
    mannequin = _add_mannequin(scene, _flat_material("PreviewMannequin", MANNEQUIN_SRGB))

    columns = len(YAWS) + 1
    sheet = np.zeros((cell * len(roots), cell * columns, 4), dtype=np.float32)
    with tempfile.TemporaryDirectory() as scratch:
        for row, root in enumerate(roots):
            visible = set(descendants(root))
            for obj in exported:
                obj.hide_render = obj not in visible
            low, high = _bounds(list(visible))
            center = (low + high) / 2.0
            radius = max((high - low).length / 2.0, 0.1)

            mannequin.hide_render = True
            for column, yaw in enumerate(YAWS):
                _aim(camera, sun, center, radius, yaw)
                pixels = _render(scene, os.path.join(scratch, f"{row}_{column}.png"))
                top = (len(roots) - 1 - row) * cell
                sheet[top:top + cell, column * cell:(column + 1) * cell] = pixels

            mannequin.hide_render = False
            mannequin.location = (high.x + 0.45, center.y, 0.0)
            scale_low = Vector((low.x, min(low.y, center.y - 0.3), 0.0))
            scale_high = Vector((high.x + 0.75, max(high.y, center.y + 0.3), max(high.z, MANNEQUIN_HEIGHT)))
            scale_center = (scale_low + scale_high) / 2.0
            _aim(camera, sun, scale_center, (scale_high - scale_low).length / 2.0, SCALE_VIEW_YAW)
            pixels = _render(scene, os.path.join(scratch, f"{row}_scale.png"))
            top = (len(roots) - 1 - row) * cell
            sheet[top:top + cell, len(YAWS) * cell:] = pixels

    return _save(sheet, output)


# The close-ups of a paint layer: its painted texels joined into the pieces of paint they make,
# such as an armband or an emblem, by cubes of this size, a piece shown from up to this many
# directions that see at least this share of it unhidden, the directions this far apart at least,
# and a piece with fewer than this share of the painted texels left out.
PAINT_PIECE_CUBE = 0.04
PAINT_PIECE_VIEWS = 4
PAINT_PIECE_SEEN = 0.25
PAINT_PIECE_SPREAD = 60.0
PAINT_PIECE_SHARE = 0.01
PAINT_CLOSE_PITCH = 10.0
# The canvas image is shown over this grey with the outlines of its faces in the paint UV map.
CANVAS_GREY = 0.42
CANVAS_LINE = (0.85, 0.85, 0.85)


def _painted_points(body: bpy.types.Object, part: str, label: str, image: bpy.types.Image,
                    limit: int = 40000) -> tuple[np.ndarray, np.ndarray]:
    """World positions and face normals of the body's texels the image paints."""
    rect, full = paint.place_crop(image)
    texels = paint.Texels(body.data, paint._painted_index(body, part, label), tuple(full))
    alpha = np.zeros((full[1], full[0]), np.float32)
    alpha[rect[1]:rect[1] + rect[3], rect[0]:rect[0] + rect[2]] = paint._pixels(image)[..., 3]
    chosen = np.nonzero(alpha.reshape(-1)[texels.places] > 0.3)[0]
    if len(chosen) > limit:
        chosen = chosen[np.linspace(0, len(chosen) - 1, limit).astype(np.int64)]
    texels.places, texels.triangles, texels.weights = texels.places[chosen], texels.triangles[chosen], texels.weights[chosen]
    local = texels.points(body.data)
    normals = np.array([body.data.polygons[int(f)].normal[:] for f in texels.faces[texels.triangles]]).reshape(-1, 3)
    m = np.array(body.matrix_world)
    return local @ m[:3, :3].T + m[:3, 3], normals @ m[:3, :3].T


def _pieces(points: np.ndarray) -> list[np.ndarray]:
    """The indices of each piece of paint: the points in touching cubes of PAINT_PIECE_CUBE."""
    cells = np.floor(points / PAINT_PIECE_CUBE).astype(np.int64)
    keys = {tuple(c) for c in cells}
    parent = {k: k for k in keys}

    def root(k):
        while parent[k] != k:
            parent[k] = parent[parent[k]]
            k = parent[k]
        return k

    for k in keys:
        for d in ((1, 0, 0), (0, 1, 0), (0, 0, 1), (1, 1, 0), (1, -1, 0), (1, 0, 1), (1, 0, -1), (0, 1, 1), (0, 1, -1),
                  (1, 1, 1), (1, 1, -1), (1, -1, 1), (1, -1, -1)):
            n = (k[0] + d[0], k[1] + d[1], k[2] + d[2])
            if n in parent:
                a, b = root(k), root(n)
                if a != b:
                    parent[a] = b
    groups: dict = {}
    for i, c in enumerate(cells):
        groups.setdefault(root(tuple(c)), []).append(i)
    found = [np.array(g) for g in groups.values() if len(g) >= PAINT_PIECE_SHARE * len(points)]
    return sorted(found, key=lambda g: -float(points[g, 2].mean()))


def _piece_views(scene: bpy.types.Scene, camera: bpy.types.Object, points: np.ndarray,
                 normals: np.ndarray) -> list[tuple[Vector, float, Vector]]:
    """The views of a piece of paint, every 45 degrees around it, from which the camera sees at least
    PAINT_PIECE_SEEN of it facing it and not hidden by the body, at least PAINT_PIECE_SPREAD apart, the
    best first: per view the direction to the camera and the middle and size of the part it sees."""
    sample = np.linspace(0, len(points) - 1, min(len(points), 400)).astype(np.int64)
    low, high = points[sample].min(axis=0), points[sample].max(axis=0)
    middle = Vector(((low + high) / 2.0).tolist())
    size = max(float(np.linalg.norm(high - low)) / 2.0 * 1.2, 0.06)
    depsgraph = bpy.context.evaluated_depsgraph_get()
    scored = []
    for yaw in range(0, 360, 45):
        pitch = math.radians(PAINT_CLOSE_PITCH)
        direction = Vector((math.sin(math.radians(yaw)) * math.cos(pitch), -math.cos(math.radians(yaw)) * math.cos(pitch),
                            math.sin(pitch)))
        eye = middle + direction * (size / math.sin(camera.data.angle / 2.0) * 1.05)
        seen = []
        for i in sample:
            point = Vector(points[i].tolist())
            toward = eye - point
            if Vector(normals[i].tolist()).dot(toward.normalized()) <= 0.2:
                continue
            hit, location, _normal, _index, _obj, _matrix = scene.ray_cast(depsgraph, eye, -toward.normalized(), distance=toward.length + 0.01)
            if hit and (location - point).length < 0.004:
                seen.append(i)
        if len(seen) >= PAINT_PIECE_SEEN * len(sample):
            part = points[seen]
            part_low, part_high = part.min(axis=0), part.max(axis=0)
            scored.append((len(seen), float(yaw), direction, Vector(((part_low + part_high) / 2.0).tolist()),
                           max(float(np.linalg.norm(part_high - part_low)) / 2.0 * 1.25, 0.06)))
    chosen = []
    for _count, yaw, direction, centre, extent in sorted(scored, key=lambda s: -s[0]):
        if all(min(abs(yaw - other), 360.0 - abs(yaw - other)) >= PAINT_PIECE_SPREAD for other, *_rest in chosen):
            chosen.append((yaw, direction, centre, extent))
        if len(chosen) == PAINT_PIECE_VIEWS:
            break
    return [(direction, extent, centre) for _yaw, direction, centre, extent in sorted(chosen, key=lambda c: c[0])]


def _canvas_view(canvas: bpy.types.Object, part: str, image: bpy.types.Image, width: int, height: int) -> np.ndarray:
    """The canvas image over grey, scaled to the size, with the outlines of its painted material's
    faces in the paint UV map."""
    rgba = paint._pixels(image)
    source_h, source_w = rgba.shape[:2]
    gx, gy = np.meshgrid((np.arange(width) + 0.5) / width, (np.arange(height) + 0.5) / height)
    sampled = paint._sample(rgba, np.stack([gx.ravel(), gy.ravel()], axis=1)).reshape(height, width, 4)
    view = np.empty((height, width, 4), np.float32)
    view[..., :3] = CANVAS_GREY * (1.0 - sampled[..., 3:]) + sampled[..., :3] * sampled[..., 3:]
    view[..., 3] = 1.0
    index = paint._painted_index(canvas, part, canvas[paint.CANVAS_BODY])
    uv = bodies._uvs(canvas.data, bodies.PAINT_UV) * (width, height)
    line = np.array(CANVAS_LINE, np.float32)
    for polygon in canvas.data.polygons:
        if polygon.material_index != index:
            continue
        corners = uv[list(polygon.loop_indices)]
        for a, b in zip(corners, np.roll(corners, -1, axis=0)):
            steps = int(max(abs(b - a).max(), 1.0)) + 1
            points = (a + (b - a) * np.linspace(0.0, 1.0, steps)[:, None]).astype(np.int64)
            px, py = points[:, 0].clip(0, width - 1), points[:, 1].clip(0, height - 1)
            view[py, px, :3] = view[py, px, :3] * 0.6 + line * 0.4
    return view


def render_paint_sheet(output: str, cell: int, name: str, part: str, labels: list[str], layer: int,
                       others: list[tuple[str, str, int]]) -> str:
    """A paint layer on every body of its file as the game shows it, over and under the other layers
    the body always wears, such as its underwear, in the order of their layers: the bodies side by
    side from four sides, then every piece of the paint up close on each body from the sides it is
    seen from, then the canvas's image in its paint UV map."""
    os.makedirs(os.path.dirname(output), exist_ok=True)
    scene = bpy.context.scene
    canvas = bpy.data.objects[paint.CANVAS]
    canvas.hide_render = True
    targets = [bpy.data.objects[f"Paint_{label}"] for label in labels]
    for body, label in zip(targets, labels):
        stack = [(layer, bpy.data.images[paint.image_name(name, label)])]
        for path, other, other_layer in others:
            wanted = paint.image_name(other, label)
            with bpy.data.libraries.load(path, link=False) as (source, target):
                target.images = [wanted] if wanted in source.images else []
            for image in [i for i in target.images if i is not None]:
                if image.get(paint.RECT) is None:
                    raise ArtPipelineError(f"{path} keeps {wanted} without its crop; run tools/Sync-PaintAsset.ps1 for {other}.")
                stack.append((other_layer, image))
        index = paint._painted_index(body, part, label)
        paint.preview_material(body, index, f"Preview_{part}_{label}", [image for _l, image in sorted(stack, key=lambda s: s[0])])
    camera, sun = _studio(scene, cell)
    low, high = _bounds(targets)
    center = (low + high) / 2.0
    radius = max((high - low).length / 2.0, 0.1)
    columns = len(YAWS)
    rows = []
    with tempfile.TemporaryDirectory() as scratch:
        overview = []
        for column, yaw in enumerate(YAWS):
            _aim(camera, sun, center, radius, yaw)
            overview.append(_render(scene, os.path.join(scratch, f"a{column}.png")))
        rows.append(overview)
        closeups = []
        for body, label in zip(targets, labels):
            points, normals = _painted_points(body, part, label, bpy.data.images[paint.image_name(name, label)])
            if len(points) == 0:
                continue
            for piece in _pieces(points):
                for direction, size, middle in _piece_views(scene, camera, points[piece], normals[piece]):
                    _aim_from(camera, sun, middle, size, direction)
                    closeups.append(_render(scene, os.path.join(scratch, f"c{len(closeups)}.png")))
        for start in range(0, len(closeups), columns):
            rows.append(closeups[start:start + columns])
    empty = np.concatenate([np.full((cell, cell, 3), CANVAS_GREY, np.float32), np.ones((cell, cell, 1), np.float32)], axis=2)
    sheet_rows = [np.concatenate(row + [empty] * (columns - len(row)), axis=1) for row in rows]
    canvas_image = bpy.data.images[paint.image_name(name)]
    width = cell * columns
    height = int(round(width * canvas_image.size[1] / canvas_image.size[0]))
    sheet_rows.append(_canvas_view(canvas, part, canvas_image, width, height))
    # Rows run bottom up in the image, so the first row ends at the top.
    return _save(np.concatenate(list(reversed(sheet_rows)), axis=0), output)


# The test poses of a garment's rig review, seen from the front right; an asset faces +Y, so a
# yaw of 0 looks at its back.
GARMENT_POSES = ("t_pose", "arms_up", "arms_forward", "torso", "stride", "sit")
GARMENT_POSE_YAW = 215.0
# A garment shorter than this share of the body's height, such as a glove or a boot, is seen up close
# instead of the torso.
CLOSE_SHARE = 0.4
# The mask view: body faces the garment covers, the skin at its edges that stays visible, the
# vertices it pulls in, the skin that comes through it in motion even so, and the other skin.
COVERED_SRGB = (0.95, 0.45, 0.10)
EDGE_SRGB = (0.98, 0.84, 0.40)
THROUGH_SRGB = (0.85, 0.10, 0.20)
PULLED_SRGB = (0.15, 0.45, 0.95)
VISIBLE_SRGB = (0.78, 0.74, 0.70)


def _mask_material(body: bpy.types.Object, result) -> bpy.types.Material:
    """A material that shows the cover mask and the pull-in on the body, from a color attribute."""
    attribute = body.data.color_attributes.new("CoverPreview", "BYTE_COLOR", "CORNER")
    pulled = set(int(v) for v in result.pull_vertices)
    colors = {key: tuple(_srgb_to_linear(c) for c in srgb) + (1.0,)
              for key, srgb in (("covered", COVERED_SRGB), ("edge", EDGE_SRGB), ("through", THROUGH_SRGB),
                                ("pulled", PULLED_SRGB), ("visible", VISIBLE_SRGB))}
    for polygon in body.data.polygons:
        for loop in polygon.loop_indices:
            if result.through is not None and result.through[polygon.index]:
                color = colors["through"]
            elif result.covered[polygon.index]:
                color = colors["covered"]
            elif body.data.loops[loop].vertex_index in pulled:
                color = colors["pulled"]
            elif result.edge is not None and result.edge[polygon.index]:
                color = colors["edge"]
            else:
                color = colors["visible"]
            attribute.data[loop].color = color
    material = bpy.data.materials.new("PreviewCoverMask")
    if material.node_tree is None:
        material.use_nodes = True
    nodes = material.node_tree.nodes
    bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Roughness"].default_value = 0.9
    source = nodes.new("ShaderNodeVertexColor")
    source.layer_name = attribute.name
    material.node_tree.links.new(source.outputs["Color"], bsdf.inputs["Base Color"])
    return material


def _review_grid(versions: list[bpy.types.Object]) -> None:
    """A template without a texture shows a grid in its UVs, so the sheet shows how they stretch."""
    grid = bpy.data.images.new("PreviewGrid", 1024, 1024)
    grid.generated_type = "UV_GRID"
    for material in {m for v in versions for m in v.data.materials if m is not None and m.use_nodes}:
        nodes = material.node_tree.nodes
        if any(n.type == "TEX_IMAGE" for n in nodes):
            continue
        texture = nodes.new("ShaderNodeTexImage")
        texture.image = grid
        bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
        material.node_tree.links.new(texture.outputs["Color"], bsdf.inputs["Base Color"])


def render_garment_sheet(output: str, cell: int, name: str | None = None, band: str | None = None,
                         libraries: list[tuple[str, list[str]]] | None = None, template: str | None = None,
                         covers: bool = True) -> str:
    """A garment on every body of its file, rows per body: the body in the garment from four sides
    with its torso, or the garment itself when it is a glove or a boot, up close, then the test poses
    that bend it, and with the garment's name and band the body without it, colored: orange the
    covered faces, yellow the skin at the garment's edges that stays visible, blue the pulled-in
    vertices, and red the skin that comes through the garment in the test poses and the clips of the
    libraries. template names the garment's template where name is left out, for the close-ups."""
    template = template or name
    os.makedirs(os.path.dirname(output), exist_ok=True)
    scene = bpy.context.scene
    armatures = sorted((o for o in scene.objects if o.type == "ARMATURE"), key=lambda o: o.name)
    if name:
        _review_grid([bpy.data.objects[garment.version_name(name, arm.name)] for arm in armatures])
    camera, sun = _studio(scene, cell)
    columns = max(len(YAWS) + 2, len(GARMENT_POSES))
    per_body = 3 if name else 2
    rows = per_body * len(armatures)
    sheet = np.zeros((cell * rows, cell * columns, 4), dtype=np.float32)

    def place(row: int, column: int, path: str) -> None:
        top = (rows - 1 - row) * cell
        sheet[top:top + cell, column * cell:(column + 1) * cell] = _render(scene, path)

    def around(row: int, center: Vector, radius: float, low: Vector, high: Vector, tag: str,
               close: tuple[Vector, float, tuple[float, float]]) -> None:
        for column, yaw in enumerate(YAWS):
            _aim(camera, sun, center, radius, yaw)
            place(row, column, os.path.join(scratch, f"{tag}_{column}.png"))
        target, size, yaws = close
        for column, yaw in enumerate(yaws, len(YAWS)):
            _aim(camera, sun, target, size, yaw)
            place(row, column, os.path.join(scratch, f"{tag}_c{column}.png"))

    def close_up(arm: bpy.types.Object, center: Vector, low: Vector, high: Vector) -> tuple[Vector, float, tuple[float, float]]:
        """The torso from the front and the back, or for a garment much shorter than the body, such
        as a glove or a boot, the one on the body's left from outside and from the front."""
        height = high.z - low.z
        version = bpy.data.objects.get(garment.version_name(template, arm.name)) if template else None
        if version is not None:
            co = bodies.world_positions(version)
            left = co[co[:, 0] < arm.matrix_world.translation.x]
            if np.ptp(co[:, 2]) < CLOSE_SHARE * height and len(left):
                near, far = Vector(left.min(axis=0)), Vector(left.max(axis=0))
                return (near + far) / 2.0, max((far - near).length * 0.6, 0.08), (270.0, 180.0)
        return Vector((center.x, center.y, low.z + height * 0.68)), height * 0.22, (180.0, 0.0)

    actions = coverage.load_actions(libraries or []) if name else []
    with tempfile.TemporaryDirectory() as scratch:
        for index, arm in enumerate(armatures):
            shown = set(descendants(arm))
            for obj in scene.objects:
                if obj.type == "MESH":
                    obj.hide_render = obj not in shown or "zone" in obj
            poses.rest(arm)
            low, high = _bounds([o for o in shown if o.type == "MESH"])
            center = (low + high) / 2.0
            radius = max((high - low).length / 2.0, 0.1)
            row = per_body * index
            close = close_up(arm, center, low, high)
            around(row, center, radius, low, high, f"{index}", close)
            # The chains of loose parts lie in every test pose as the runtime solver lays them.
            worn = bpy.data.objects.get(garment.version_name(template, arm.name)) if template else None
            kinds, reach = cloth.worn(arm, [worn]) if worn is not None else ([], {})
            found = cloth.capsules(arm, bodies.source_body(arm.name)) if kinds else []
            for column, pose_name in enumerate(GARMENT_POSES):
                poses.apply(arm, next(p for n, _d, p in poses.POSES if n == pose_name))
                cloth.follow(arm, kinds, found, reach)
                # Raised arms reach above the rest pose's bounds.
                _aim(camera, sun, center, radius * 1.2, GARMENT_POSE_YAW)
                place(row + 1, column, os.path.join(scratch, f"{index}_p{column}.png"))
            poses.rest(arm)
            if name:
                body = bodies.source_body(arm.name)
                version = bpy.data.objects[garment.version_name(name, arm.name)]
                materials = list(body.data.materials)
                result = coverage.compute(name, arm.name, band, covers)
                coverage.check_motion(name, result, actions)
                mask = _mask_material(body, result)
                for slot in range(len(body.data.materials)):
                    body.data.materials[slot] = mask
                version.hide_render = True
                around(row + 2, center, radius, low, high, f"{index}_m", close)
                version.hide_render = False
                for slot, material in enumerate(materials):
                    body.data.materials[slot] = material
                body.data.color_attributes.remove(body.data.color_attributes["CoverPreview"])
    return _save(sheet, output)


# The cage's sheet: each body from the front, the right side, the back, and the back left, once
# colored by the cage's distance from the skin with the standard lines on it, once in one cloth
# color, and its torso up close in the cloth color. A view from low down shows the figure.
CAGE_YAWS = (180.0, 90.0, 0.0, 35.0)
# The torso up close from the front right, the back left, the back, and the back right, where the arms
# hide nothing.
CAGE_TORSO_YAWS = (145.0, 35.0, 0.0, 325.0)
# The left hand and foot up close: the hand from outside and from the front, past the thigh that hides
# the palm, the foot from outside and from the front, each as a bone it is aimed at, the direction it
# is seen from, and the view's radius.
CAGE_ENDS = (("Hand_L", (-1.0, 0.15, 0.1), 0.13), ("Hand_L", (0.45, 1.0, -0.25), 0.13),
             ("Foot_L", (-1.0, 0.1, 0.25), 0.16), ("Foot_L", (-0.4, 1.0, 0.5), 0.16))
# The head up close from the front, the right side, the back, and the front left from above.
CAGE_HEAD = ((0.0, 1.0, 0.1), (1.0, 0.0, 0.1), (0.0, -1.0, 0.15), (-0.6, 0.7, 0.6))
CAGE_HEAD_RADIUS = 0.17
CAGE_ROWS = 5
CAGE_PITCH = 8.0
# Shown this far off the cage, which lies 1 mm from the skin, so the skin does not shine through.
CAGE_DISPLAY_OFFSET = 0.004
CAGE_LINE_OFFSET = 0.007
CAGE_LINE_RADIUS = 0.003
# The distance colors: at the skin, at the middle, and at the far end of the scale and beyond.
CAGE_GAP_SCALE = 0.03
CAGE_GAP_SRGB = ((0.32, 0.46, 0.86), (0.96, 0.86, 0.32), (0.88, 0.18, 0.14))
CAGE_CLOTH_SRGB = (0.42, 0.30, 0.22)
CAGE_LINE_SRGB = ((0.98, 0.98, 0.98), (0.10, 0.10, 0.10), (0.10, 0.85, 0.35), (0.95, 0.20, 0.75),
                  (0.10, 0.80, 0.95), (1.00, 0.55, 0.00))


def _gap_color(distance: float) -> tuple[float, float, float, float]:
    t = min(max(distance / CAGE_GAP_SCALE, 0.0), 1.0) * 2.0
    low, high = (CAGE_GAP_SRGB[0], CAGE_GAP_SRGB[1]) if t <= 1.0 else (CAGE_GAP_SRGB[1], CAGE_GAP_SRGB[2])
    t = t if t <= 1.0 else t - 1.0
    return tuple(_srgb_to_linear(a + (b - a) * t) for a, b in zip(low, high)) + (1.0,)


def _aim_low(camera: bpy.types.Object, sun: bpy.types.Object, center: Vector, radius: float, yaw: float) -> None:
    distance = radius / math.sin(camera.data.angle / 2.0) * 1.05
    pitch = math.radians(CAGE_PITCH)
    direction = Vector((math.sin(math.radians(yaw)) * math.cos(pitch), -math.cos(math.radians(yaw)) * math.cos(pitch),
                        math.sin(pitch)))
    camera.location = center + direction * distance
    camera.rotation_euler = (center - camera.location).to_track_quat("-Z", "Y").to_euler()
    sun.rotation_euler = (math.radians(50.0), 0.0, math.radians(yaw - 45.0))


def _aim_from(camera: bpy.types.Object, sun: bpy.types.Object, center: Vector, radius: float, direction: Vector) -> None:
    camera.location = center + direction.normalized() * (radius / math.sin(camera.data.angle / 2.0) * 1.05)
    camera.rotation_euler = (center - camera.location).to_track_quat("-Z", "Y").to_euler()
    yaw = math.degrees(math.atan2(direction.x, -direction.y))
    sun.rotation_euler = (math.radians(50.0), 0.0, math.radians(yaw - 45.0))


def render_cage_sheet(output: str, cell: int, labels: list[str], lines: list[dict]) -> str:
    """The garment cage on every body: per body a row colored by the cage's distance from the skin,
    blue at the skin, yellow at 15 mm, and red from 30 mm, with the standard lines drawn on it, a
    row in one cloth color, each from the front, the right side, the back, and the back left, a
    row of the torso up close in the cloth color from the front right, the back left, the back, and
    the back right, a row of the left hand and foot up close in the distance colors, and a row of
    the head up close in the distance colors from the front, the right side, the back, and the front
    left from above."""
    os.makedirs(os.path.dirname(output), exist_ok=True)
    scene = bpy.context.scene
    for obj in scene.objects:
        obj.hide_render = True
    camera, sun = _studio(scene, cell)
    cloth = _flat_material("PreviewCageCloth", CAGE_CLOTH_SRGB)
    gap = bpy.data.materials.new("PreviewCageGap")
    if gap.node_tree is None:
        gap.use_nodes = True
    bsdf = next(n for n in gap.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Roughness"].default_value = 0.8
    source = gap.node_tree.nodes.new("ShaderNodeAttribute")
    source.attribute_name = "CageGap"
    gap.node_tree.links.new(source.outputs["Color"], bsdf.inputs["Base Color"])
    line_materials = [_flat_material(f"PreviewCageLine{i}", srgb) for i, srgb in enumerate(CAGE_LINE_SRGB)]

    rows = CAGE_ROWS * len(labels)
    sheet = np.zeros((cell * rows, cell * len(CAGE_YAWS), 4), dtype=np.float32)
    with tempfile.TemporaryDirectory() as scratch:
        for index, label in enumerate(labels):
            version = bpy.data.objects[cage.version_name(label)]
            body = bodies.source_body(label)
            co = bodies.world_positions(version)
            normals = cage.Net.of(version.data).normals(co)
            mesh = version.data.copy()
            mesh.name = f"PreviewCage_{label}"
            mesh.vertices.foreach_set("co", (co + normals * CAGE_DISPLAY_OFFSET).reshape(-1))
            colors = mesh.color_attributes.new("CageGap", "FLOAT_COLOR", "POINT")
            for i, distance in enumerate(cage.distances(label)):
                colors.data[i].color = _gap_color(float(distance))
            shown = _add_object(scene, mesh.name, mesh)
            shown.data.materials.clear()
            shown.data.materials.append(gap)

            drawn = []
            near = KDTree(len(co))
            for i, point in enumerate(co):
                near.insert(point, i)
            near.balance()
            for number, line in enumerate(lines):
                curve = bpy.data.curves.new(f"PreviewLine_{label}_{line['name']}", "CURVE")
                curve.dimensions = "3D"
                curve.bevel_depth = CAGE_LINE_RADIUS
                for _side, segments in cage.line_segments(label, line):
                    for a, b in segments:
                        spline = curve.splines.new("POLY")
                        spline.points.add(1)
                        for point, end in zip(spline.points, (a, b)):
                            offset = Vector(normals[near.find(end)[1]]) * CAGE_LINE_OFFSET
                            point.co = tuple(end + offset) + (1.0,)
                curve.materials.append(line_materials[number % len(line_materials)])
                drawn.append(_add_object(scene, curve.name, curve))

            body.hide_render = False
            low, high = _bounds([body])
            height = high.z - low.z
            whole = (Vector(((low.x + high.x) / 2.0, (low.y + high.y) / 2.0, low.z + height * 0.56)), height * 0.5)
            torso = (Vector(((low.x + high.x) / 2.0, (low.y + high.y) / 2.0, low.z + height * 0.6)), height * 0.23)
            views = ((CAGE_ROWS * index, gap, True, whole, CAGE_YAWS), (CAGE_ROWS * index + 1, cloth, False, whole, CAGE_YAWS),
                     (CAGE_ROWS * index + 2, cloth, False, torso, CAGE_TORSO_YAWS))
            for row, material, show_lines, (center, radius), yaws in views:
                shown.data.materials[0] = material
                for obj in drawn:
                    obj.hide_render = not show_lines
                for column, yaw in enumerate(yaws):
                    _aim_low(camera, sun, center, radius, yaw)
                    top = (rows - 1 - row) * cell
                    sheet[top:top + cell, column * cell:(column + 1) * cell] = _render(
                        scene, os.path.join(scratch, f"{label}_{row}_{column}.png"))
            arm = bpy.data.objects[label]
            shown.data.materials[0] = gap
            for obj in drawn:
                obj.hide_render = False
            row = CAGE_ROWS * index + 3
            for column, (bone, direction, radius) in enumerate(CAGE_ENDS):
                target = arm.data.bones[bone]
                center = arm.matrix_world @ (target.tail_local if bone.startswith("Hand") else
                                             target.head_local.lerp(target.tail_local, 0.6) - Vector((0.0, 0.0, 0.03)))
                _aim_from(camera, sun, center, radius, Vector(direction))
                top = (rows - 1 - row) * cell
                sheet[top:top + cell, column * cell:(column + 1) * cell] = _render(
                    scene, os.path.join(scratch, f"{label}_{row}_{column}.png"))
            row = CAGE_ROWS * index + 4
            head = arm.data.bones["Head"]
            center = arm.matrix_world @ head.head_local.lerp(head.tail_local, 0.4)
            for column, direction in enumerate(CAGE_HEAD):
                _aim_from(camera, sun, center, CAGE_HEAD_RADIUS, Vector(direction))
                top = (rows - 1 - row) * cell
                sheet[top:top + cell, column * cell:(column + 1) * cell] = _render(
                    scene, os.path.join(scratch, f"{label}_{row}_{column}.png"))
            body.hide_render = True
            shown.hide_render = True
            for obj in drawn:
                obj.hide_render = True
    return _save(sheet, output)


CLIP_FRAMES = 8
CLIP_YAWS = (35.0, 215.0)


# An attachment is shown held in the first frame of this clip, with the hand pose of the other.
HELD_CLIP = "Idle"
GRIP_CLIP = "Grip"
# The close-up of the hand that holds an attachment, and the room around a whole body.
HAND_RADIUS = 0.15
BODY_RADIUS = 1.1
# The meshes of a body that the close-up from inside the arm shows, so the thigh hides nothing.
HELD_ARM_PARTS = ("_Hands", "_Forearms", "_UpperArms")


def _append_held(path: str, root: str) -> tuple[list[bpy.types.Object], list[bpy.types.Object]]:
    """Every object of an animation library and its clips HELD_CLIP and GRIP_CLIP, appended into this
    file; returns the armatures, the library's own first, and every appended object."""
    with bpy.data.libraries.load(path, link=False) as (source, target):
        if root not in source.objects:
            raise ArtPipelineError(f'{path} has no object "{root}".')
        if HELD_CLIP not in source.actions:
            raise ArtPipelineError(f'{path} has no clip "{HELD_CLIP}" to show an attachment held in.')
        target.objects = list(source.objects)
        target.actions = [name for name in (HELD_CLIP, GRIP_CLIP) if name in source.actions]
    loaded = [o for o in target.objects if o is not None]
    for obj in loaded:
        bpy.context.scene.collection.objects.link(obj)
    arms = sorted((o for o in loaded if o.type == "ARMATURE"), key=lambda o: (o.name != root, o.name))
    return arms, loaded


def _hold_pose(arm: bpy.types.Object) -> None:
    """The armature in the first frame of HELD_CLIP with the hand pose of GRIP_CLIP over it, as the
    game layers them, without an action, so the pose stays."""
    pose = {}
    for name in (HELD_CLIP, GRIP_CLIP):
        action = bpy.data.actions.get(name)
        if action is None or not action.slots:
            continue
        channelbag = anim_utils.action_get_channelbag_for_slot(action, action.slots[0])
        for fc in channelbag.fcurves if channelbag is not None else []:
            pose[(fc.data_path, fc.array_index)] = fc.evaluate(0.0)
    poses.rest(arm)
    for (path, index), value in pose.items():
        bone, channel = path.split('"')[1], path.rsplit(".", 1)[1]
        getattr(arm.pose.bones[bone], channel)[index] = value
    bpy.context.view_layer.update()


def _held_copy(root: bpy.types.Object, arm: bpy.types.Object, socket: str) -> list[bpy.types.Object]:
    """A copy of the attachment whose root lies on the armature's socket, as the game puts it there."""
    copies = {}
    for obj in descendants(root):
        copy = obj.copy()
        bpy.context.scene.collection.objects.link(copy)
        copies[obj] = copy
    for obj, copy in copies.items():
        if obj.parent in copies:
            copy.parent = copies[obj.parent]
            copy.matrix_parent_inverse = obj.matrix_parent_inverse.copy()
    copies[root].parent = None
    copies[root].matrix_world = arm.matrix_world @ arm.pose.bones[socket].matrix
    bpy.context.view_layer.update()
    return list(copies.values())


def render_attachment_sheet(output: str, cell: int, socket: str, library_path: str, library_root: str) -> str:
    """An attachment alone from four sides and up close where it is held, and held on its socket by
    every body of an animation library, in the first frame of Idle with the hand pose Grip when the
    library has it: a row per body with the whole body, the attachment seen from outside the arm,
    and the hand up close from the front, from inside with only the arms shown, and from behind."""
    os.makedirs(os.path.dirname(output), exist_ok=True)
    scene = bpy.context.scene
    exported = export_objects()
    root = root_objects()[0]
    camera, sun = _studio(scene, cell)
    arms, loaded = _append_held(library_path, library_root)
    # The other bodies copy the pose of the library's armature.
    _hold_pose(arms[0])
    held = [_held_copy(root, arm, socket) for arm in arms]
    arm_parts = [o for o in loaded if o.type == "MESH" and o.name.endswith(HELD_ARM_PARTS)]

    def show(objects):
        visible = set(objects)
        for obj in scene.objects:
            obj.hide_render = obj not in visible

    cells = []
    low, high = _bounds(exported)
    center = (low + high) / 2.0
    radius = max((high - low).length / 2.0, 0.1)
    # Held in a hand, an attachment shows every side, so every other view looks from below.
    for index, yaw in enumerate(YAWS):
        pitch = math.radians(PITCH if index % 2 == 0 else -PITCH)
        direction = Vector((math.sin(math.radians(yaw)) * math.cos(pitch),
                            -math.cos(math.radians(yaw)) * math.cos(pitch), math.sin(pitch)))
        cells.append((exported, lambda d=direction: _aim_from(camera, sun, center, radius, d)))
    cells.append((exported, lambda: _aim(camera, sun, root.matrix_world.translation, HAND_RADIUS, YAWS[0])))
    for arm, copy in zip(arms, held):
        bone = arm.matrix_world @ arm.pose.bones[socket].head
        side = 1.0 if bone.x > arm.matrix_world.translation.x else -1.0
        body_center = Vector((arm.matrix_world.translation.x, 0.0, 0.95))
        item_low, item_high = _bounds(copy)
        item_center = (item_low + item_high) / 2.0
        item_radius = max((item_high - item_low).length / 2.0, HAND_RADIUS)
        cells += [
            (loaded + copy, lambda c=body_center, s=side: _aim_from(
                camera, sun, c, BODY_RADIUS, Vector((s * 0.55, 1.0, 0.35)))),
            (loaded + copy, lambda c=item_center, r=item_radius, s=side: _aim_from(
                camera, sun, c, r, Vector((s * 1.0, 0.45, 0.25)))),
            (loaded + copy, lambda c=bone, s=side: _aim_from(
                camera, sun, c, HAND_RADIUS, Vector((s * 0.3, 1.0, 0.15)))),
            (arm_parts + copy, lambda c=bone, s=side: _aim_from(
                camera, sun, c, HAND_RADIUS, Vector((-s * 1.0, 0.3, 0.1)))),
            (loaded + copy, lambda c=bone, s=side: _aim_from(
                camera, sun, c, HAND_RADIUS, Vector((s * 0.3, -1.0, 0.3)))),
        ]

    columns = len(YAWS) + 1
    rows = (len(cells) + columns - 1) // columns
    sheet = np.zeros((cell * rows, cell * columns, 4), dtype=np.float32)
    with tempfile.TemporaryDirectory() as scratch:
        for index, (objects, aim) in enumerate(cells):
            show(objects)
            aim()
            pixels = _render(scene, os.path.join(scratch, f"{index}.png"))
            row, column = divmod(index, columns)
            # Image rows run bottom up, so the first row is drawn at the top.
            bottom = sheet.shape[0] - (row + 1) * cell
            sheet[bottom:bottom + cell, column * cell:(column + 1) * cell] = pixels
    return _save(sheet, output)


def render_clip_sheet(output: str, cell: int) -> str:
    """Every clip of an animation library at evenly spaced frames, from its first to its last, on
    the bodies of the Reference collection and their ground grid."""
    os.makedirs(os.path.dirname(output), exist_ok=True)
    scene = bpy.context.scene
    arm = next(o for o in export_objects() if o.type == "ARMATURE")
    reference = bpy.data.collections.get("Reference")
    shown = set(reference.all_objects) if reference is not None else set()
    for obj in scene.objects:
        obj.hide_render = obj not in shown
    camera, sun = _studio(scene, cell)
    bodies = [o for o in shown if o.type == "MESH" and o.name != "Reference_Ground"]
    low, high = _bounds(bodies)
    # Room for a body lying on the ground in any direction.
    center = Vector(((low.x + high.x) / 2.0, (low.y + high.y) / 2.0, 0.7))
    radius = max((high.x - low.x) / 2.0, 0.0) + 1.6

    actions = sorted(bpy.data.actions, key=lambda a: a.name)
    arm.animation_data_create()
    sheet = np.zeros((cell * len(CLIP_YAWS) * len(actions), cell * CLIP_FRAMES, 4), dtype=np.float32)
    with tempfile.TemporaryDirectory() as scratch:
        for index, action in enumerate(actions):
            arm.animation_data.action = action
            if action.slots:
                arm.animation_data.action_slot = action.slots[0]
            end = int(action.frame_range[1])
            for view, yaw in enumerate(CLIP_YAWS):
                row = index * len(CLIP_YAWS) + view
                top = row * cell
                _aim(camera, sun, center, radius, yaw)
                for column in range(CLIP_FRAMES):
                    scene.frame_set(round(column * end / (CLIP_FRAMES - 1)))
                    pixels = _render(scene, os.path.join(scratch, f"{row}_{column}.png"))
                    # Image rows run bottom up, so the first clip is drawn at the top.
                    bottom = sheet.shape[0] - top - cell
                    sheet[bottom:bottom + cell, column * cell:(column + 1) * cell] = pixels
    arm.animation_data.action = None
    return _save(sheet, output)

