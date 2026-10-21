"""Writes the Export collection as FBX with the fixed settings that the Unity import rules expect.
A skinned asset also exports its armature with every animation clip as its own take, and writes
the images of its materials as PNG files for Unity. An animation library exports only its armature
and its clips."""

import contextlib
import datetime
import os
import sys

import bpy
from io_scene_fbx import export_fbx_bin

from .scene import EXPORT_COLLECTION, ArtPipelineError, export_objects
from .validate import principled_inputs

OBJECT_TYPES = {"static": {"EMPTY", "MESH"}, "skinned": {"ARMATURE", "MESH"}, "animations": {"ARMATURE"}}
# Kinds whose FBX carries an armature and its clips.
ANIMATED_KINDS = {"skinned", "animations"}
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
# The creation time every FBX header records.
FBX_TIME = datetime.datetime(2000, 1, 1)


def _linear_to_srgb(value: float) -> float:
    value = min(max(value, 0.0), 1.0)
    return value * 12.92 if value <= 0.0031308 else 1.055 * value ** (1.0 / 2.4) - 0.055


def _exported_materials() -> list[bpy.types.Material]:
    materials = {slot.material for obj in export_objects() if obj.type == "MESH"
                 for slot in obj.material_slots if slot.material is not None}
    return sorted(materials, key=lambda m: m.name)


def _base_image(material: bpy.types.Material) -> bpy.types.Image | None:
    images = [n.image for n in material.node_tree.nodes if n.type == "TEX_IMAGE"]
    return images[0] if images else None


def material_records() -> list[dict[str, object]]:
    """The approved look of every exported material, as Unity URP Lit values: the sRGB
    Base Color, Metallic, and Smoothness (1 - Roughness), and for a painted material the name of
    the image that gives its Base Color, in which case the Base Color is white. Unity tests compare
    the project materials with this record."""
    records = []
    for material in _exported_materials():
        bsdf = next((n for n in material.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
        if bsdf is None:
            raise ArtPipelineError(f'Material "{material.name}" has no Principled BSDF.')
        image = _base_image(material)
        if image is None and principled_inputs(material) is None:
            raise ArtPipelineError(f'Material "{material.name}" is not a flat Principled BSDF.')
        record = {"name": material.name}
        if image is None:
            red, green, blue = (round(_linear_to_srgb(c) * 255) for c in bsdf.inputs["Base Color"].default_value[:3])
            record["baseColor"] = f"#{red:02X}{green:02X}{blue:02X}"
        else:
            record["baseColor"] = "#FFFFFF"
            record["baseMap"] = image.name
        record["metallic"] = round(float(bsdf.inputs["Metallic"].default_value), 2)
        record["smoothness"] = round(1.0 - float(bsdf.inputs["Roughness"].default_value), 2)
        records.append(record)
    return records


def parse_variants(text: str | None) -> dict[str, list[str]]:
    """The variants argument, "M_Face=T_A+T_B;M_Eye=T_C": the other images each material may take
    in the game instead of its own."""
    variants: dict[str, list[str]] = {}
    for entry in (text or "").split(";"):
        if entry:
            material, images = entry.split("=", 1)
            variants[material] = [i for i in images.split("+") if i]
    return variants


def variant_errors(variants: dict[str, list[str]]) -> list[str]:
    """Every variant is a packed PNG of the same size as the image of its painted material, so it
    fits the material's UV layout and its texture import."""
    errors = []
    materials = {m.name: m for m in _exported_materials()}
    for name, images in sorted(variants.items()):
        material = materials.get(name)
        base = _base_image(material) if material is not None else None
        if base is None:
            errors.append(f'The variants of "{name}" need an exported material with an Image Texture.')
            continue
        for image_name in images:
            image = bpy.data.images.get(image_name)
            if image is None or image.packed_file is None or not bytes(image.packed_file.data).startswith(PNG_SIGNATURE):
                errors.append(f'Variant "{image_name}" of "{name}" must be an image packed as a PNG in the file.')
            elif tuple(image.size) != tuple(base.size):
                errors.append(f'Variant "{image_name}" of "{name}" is {tuple(image.size)} pixels; '
                              f'"{base.name}" is {tuple(base.size)}.')
    return errors


def _write_image(image: bpy.types.Image, folder: str, owner: str) -> tuple[str, str]:
    data = bytes(image.packed_file.data) if image.packed_file is not None else b""
    if not data.startswith(PNG_SIGNATURE):
        raise ArtPipelineError(f'Image "{image.name}" of "{owner}" must be packed as a PNG.')
    os.makedirs(folder, exist_ok=True)
    path = os.path.join(folder, f"{image.name}.png")
    with open(path, "wb") as f:
        f.write(data)
    return image.name, path


def export_textures(folder: str, variants: dict[str, list[str]] | None = None) -> list[tuple[str, str]]:
    """Writes the packed image of every painted material and every variant image as
    <folder>/<image>.png, byte for byte as it is packed, and returns the names and paths, once per
    image also when materials share it."""
    written = []
    images = set()
    for material in _exported_materials():
        image = _base_image(material)
        if image is None or image.name in images:
            continue
        images.add(image.name)
        written.append(_write_image(image, folder, material.name))
    for material, names in sorted((variants or {}).items()):
        for name in names:
            if name not in images:
                images.add(name)
                written.append(_write_image(bpy.data.images[name], folder, material))
    return written


def clip_records() -> list[dict[str, object]]:
    """Every animation clip: its name, its length in frames, and whether it loops."""
    return [{"name": a.name, "frames": int(a.frame_range[1]), "loop": bool(a.get("loop"))}
            for a in sorted(bpy.data.actions, key=lambda a: a.name)]


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


class _DataWithoutPath:
    """bpy.data, but the file path reads as empty."""

    def __getattr__(self, name):
        return "" if name == "filepath" else getattr(bpy.data, name)


class _BpyWithoutPath:
    data = _DataWithoutPath()

    def __getattr__(self, name):
        return getattr(bpy, name)


@contextlib.contextmanager
def _reproducible_fbx():
    """The same file gives the same FBX bytes: Blender's exporter writes the time of the export and
    the path of the .blend file into the header, and derives object ids from Python's string
    hashes, which tools/ArtPipelineCommon.ps1 fixes with PYTHONHASHSEED."""
    if sys.flags.hash_randomization:
        raise ArtPipelineError("Export through tools/Export-ArtAsset.ps1, which runs Blender with fixed string hashes.")
    header, module_bpy = export_fbx_bin.fbx_header_elements, export_fbx_bin.bpy
    export_fbx_bin.fbx_header_elements = lambda root, scene_data, time=None: header(root, scene_data, FBX_TIME)
    export_fbx_bin.bpy = _BpyWithoutPath()
    try:
        yield
    finally:
        export_fbx_bin.fbx_header_elements, export_fbx_bin.bpy = header, module_bpy


def export_fbx(kind: str, target: str) -> None:
    if kind not in OBJECT_TYPES:
        raise ArtPipelineError(f'Assets of kind "{kind}" cannot be exported.')
    with _reproducible_fbx():
        _export_fbx(kind, target)


def _export_fbx(kind: str, target: str) -> None:
    _activate_export_collection()
    os.makedirs(os.path.dirname(target), exist_ok=True)
    animated = kind in ANIMATED_KINDS
    # Blender's own axes go into the file unchanged and the Unity importer bakes the axis
    # conversion, so no object keeps a correction rotation. Blender +Y becomes Unity +Z, so an
    # asset faces +Y in Blender. Blender's Apply Transform is not used: it breaks armatures.
    # Every bone goes out, also the sockets that deform nothing, without leaf bones, and every
    # action becomes a take sampled at each frame, without simplification.
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
        primary_bone_axis="Y",
        secondary_bone_axis="X",
        use_armature_deform_only=False,
        armature_nodetype="NULL",
        bake_anim=animated,
        bake_anim_use_all_bones=True,
        bake_anim_use_nla_strips=False,
        bake_anim_use_all_actions=True,
        bake_anim_force_startend_keying=True,
        bake_anim_step=1.0,
        bake_anim_simplify_factor=0.0,
        path_mode="AUTO",
        embed_textures=False,
    )
    if result != {"FINISHED"} or not os.path.isfile(target):
        raise ArtPipelineError(f"The FBX export did not finish: {result}.")
