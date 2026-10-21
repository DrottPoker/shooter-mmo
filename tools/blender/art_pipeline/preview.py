"""Renders a review sheet for an asset: one row per exported root object, seen from four
sides at the game camera pitch, plus a fifth view with a 1.8 m mannequin for scale. Only
the Export collection is rendered, on a dark green ground."""

import math
import os
import tempfile

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector

from .scene import descendants, export_objects, root_objects

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


def render_sheet(output: str, cell: int) -> str:
    os.makedirs(os.path.dirname(output), exist_ok=True)
    scene = bpy.context.scene
    exported = export_objects()
    roots = root_objects()
    for obj in scene.objects:
        obj.hide_render = obj not in exported

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

    image = bpy.data.images.new("PreviewSheet", sheet.shape[1], sheet.shape[0], alpha=True)
    image.pixels.foreach_set(sheet.ravel())
    image.filepath_raw = output
    image.file_format = "PNG"
    image.save()
    return output

