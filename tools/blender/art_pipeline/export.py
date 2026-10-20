"""Writes the Export collection as FBX with the fixed settings that the Unity import rules expect."""

import os

import bpy

from .scene import EXPORT_COLLECTION, ArtPipelineError, export_objects
from .validate import principled_inputs

OBJECT_TYPES = {"static": {"EMPTY", "MESH"}}


def _linear_to_srgb(value: float) -> float:
    value = min(max(value, 0.0), 1.0)
    return value * 12.92 if value <= 0.0031308 else 1.055 * value ** (1.0 / 2.4) - 0.055


def material_records() -> list[dict[str, object]]:
    """The approved look of every exported material, as Unity URP Lit values: the sRGB
    Base Color, Metallic, and Smoothness (1 - Roughness). Unity tests compare the project
    materials with this record."""
    materials = {slot.material for obj in export_objects() if obj.type == "MESH"
                 for slot in obj.material_slots if slot.material is not None}
    records = []
    for material in sorted(materials, key=lambda m: m.name):
        inputs = principled_inputs(material)
        if inputs is None:
            raise ArtPipelineError(f'Material "{material.name}" is not a flat Principled BSDF.')
        red, green, blue = (round(_linear_to_srgb(c) * 255) for c in inputs["Base Color"].default_value[:3])
        records.append({
            "name": material.name,
            "baseColor": f"#{red:02X}{green:02X}{blue:02X}",
            "metallic": round(float(inputs["Metallic"].default_value), 2),
            "smoothness": round(1.0 - float(inputs["Roughness"].default_value), 2),
        })
    return records


def _activate_export_collection() -> None:
    def find(layer: bpy.types.LayerCollection) -> bpy.types.LayerCollection | None:
        if layer.collection.name == EXPORT_COLLECTION:
            return layer
        for child in layer.children:
            found = find(child)
            if found is not None:
                return found
        return None

    layer = find(bpy.context.view_layer.layer_collection)
    if layer is None:
        raise ArtPipelineError(f'The file has no "{EXPORT_COLLECTION}" collection in the view layer.')
    bpy.context.view_layer.active_layer_collection = layer


def export_fbx(kind: str, target: str) -> None:
    _activate_export_collection()
    os.makedirs(os.path.dirname(target), exist_ok=True)
    # Blender's own axes go into the file unchanged and the Unity importer bakes the axis
    # conversion, so no object keeps a correction rotation. Blender +Y becomes Unity +Z, so an
    # asset faces +Y in Blender. Blender's Apply Transform is not used: it breaks armatures.
    result = bpy.ops.export_scene.fbx(
        filepath=target,
        check_existing=False,
        use_selection=False,
        use_visible=False,
        use_active_collection=True,
        global_scale=1.0,
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_UNITS",
        use_space_transform=True,
        bake_space_transform=False,
        axis_forward="Y",
        axis_up="Z",
        object_types=OBJECT_TYPES[kind],
        use_mesh_modifiers=True,
        mesh_smooth_type="FACE",
        use_triangles=False,
        use_tspace=False,
        use_custom_props=False,
        add_leaf_bones=False,
        bake_anim=False,
        path_mode="AUTO",
        embed_textures=False,
    )
    if result != {"FINISHED"} or not os.path.isfile(target):
        raise ArtPipelineError(f"The FBX export did not finish: {result}.")
