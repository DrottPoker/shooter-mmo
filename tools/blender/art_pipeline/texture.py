"""Garment textures: the look of a garment template, its painted image in the template's UVs, in an
art asset of its own, so a new texture never changes the template's file. See
docs/EQUIPMENT_VISUALS_DESIGN.md.

The file holds the image T_Garment_<Name>, packed, with the custom properties roughness and
metallic, and the mesh Preview, a copy of the template's first version with the material
M_Garment_<Name> that shows the image, for painting in Texture Paint mode. Preview keeps the UVs the
image is painted in, and the property template_uvs the fingerprint of the template's UVs then, so a
template whose UVs change is noticed and its texture moved to the new UVs by projection from
Preview. A texture may also start from the look of another mesh by projection, such as a garment
made before templates."""

import hashlib
import os

import bpy
import numpy as np

from . import garment
from .scene import ArtPipelineError, relative_image_paths

PREVIEW = "Preview"
TEMPLATE_UVS = "template_uvs"
SIZES = (512, 1024, 2048)
# A projection looks this far off the target's surface, both ways, for the source's surface.
PROJECTION_REACH = 0.06
BAKE_MARGIN = 8
ALPHA_CUT = 0.5


def image_name(name: str) -> str:
    return garment.image_name(name)


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


def _material(name: str, image: bpy.types.Image) -> bpy.types.Material:
    """The texture's material: its image as the color, with its roughness and metallic, drawn from
    both sides like the game draws garments."""
    material = bpy.data.materials.get(material_name(name)) or bpy.data.materials.new(material_name(name))
    material.use_nodes = True
    nodes, links = material.node_tree.nodes, material.node_tree.links
    bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
    texture = next((n for n in nodes if n.type == "TEX_IMAGE"), None) or nodes.new("ShaderNodeTexImage")
    texture.image = image
    links.new(texture.outputs["Color"], bsdf.inputs["Base Color"])
    bsdf.inputs["Roughness"].default_value = float(image.get("roughness", 0.6))
    bsdf.inputs["Metallic"].default_value = float(image.get("metallic", 0.0))
    material.use_backface_culling = False
    return material


def _preview(name: str, mesh: bpy.types.Mesh, image: bpy.types.Image) -> bpy.types.Object:
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
    mesh.materials.append(_material(name, image))
    for polygon in mesh.polygons:
        polygon.material_index = 0
    return obj


def _bake(source: bpy.types.Object, target: bpy.types.Object, image: bpy.types.Image) -> None:
    """The look of the source, its material's base color where it shows on its surface, baked into
    the image in the target's UVs by rays from the target's surface."""
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
        bpy.ops.object.bake(type="DIFFUSE", pass_filter={"COLOR"}, use_selected_to_active=True,
                            cage_extrusion=PROJECTION_REACH, max_ray_distance=2.0 * PROJECTION_REACH,
                            margin=BAKE_MARGIN, use_clear=True)
    finally:
        bake_target.nodes.remove(node)


def create(file: str, name: str, template_file: str, template: str, label: str, size: int,
           source_file: str | None, source_object: str | None) -> None:
    """Writes the first file of a texture: its image, a neutral light grey or the look of another
    mesh moved over by projection, and the preview on the template's first version."""
    if size not in SIZES:
        raise ArtPipelineError(f"A garment texture is {', '.join(str(s) for s in SIZES)} pixels square.")
    bpy.ops.wm.read_factory_settings(use_empty=True)
    image = bpy.data.images.new(image_name(name), size, size, alpha=False)
    image.generated_color = (0.8, 0.8, 0.8, 1.0)
    image["roughness"] = 0.6
    image["metallic"] = 0.0
    mesh = _template_first(template_file, template, label)
    preview = _preview(name, mesh, image)
    preview[TEMPLATE_UVS] = uv_fingerprint(mesh)
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


def sync(file: str, name: str, template_file: str, template: str, label: str) -> bool:
    """Moves the texture to the template's UVs by projection from its preview when the template's
    UVs changed. Returns whether the file changed; an unchanged file is not saved, so its approval
    stays valid."""
    bpy.ops.wm.open_mainfile(filepath=file)
    preview = bpy.data.objects.get(PREVIEW)
    image = bpy.data.images.get(image_name(name))
    if preview is None or image is None:
        raise ArtPipelineError(f'The texture needs its image "{image_name(name)}" and its "{PREVIEW}".')
    current = current_template_uvs(template_file, template, label)
    if preview.get(TEMPLATE_UVS) == current:
        return False
    old = preview
    old.name = f"{PREVIEW}_old"
    old_image = image.copy()
    old_image.pack()
    old.data.materials[0] = _material(f"{name}_old", old_image)
    mesh = _template_first(template_file, template, label)
    new = _preview(name, mesh, image)
    _bake(old, new, image)
    new[TEMPLATE_UVS] = current
    image.pack()
    old_mesh = old.data
    bpy.data.objects.remove(old, do_unlink=True)
    bpy.data.meshes.remove(old_mesh)
    bpy.data.images.remove(old_image)
    bpy.data.orphans_purge(do_recursive=True)
    relative_image_paths()
    bpy.ops.wm.save_as_mainfile(filepath=file, compress=True, relative_remap=False)
    return True


def texture_errors(name: str, template_file: str, template: str, label: str) -> list[str]:
    """The rules of a texture: its packed square image of a known size with its roughness and
    metallic, opaque, since transparent parts and their masks come with delivery step 5c, its
    preview showing it, and the template's current UVs."""
    errors = []
    image = bpy.data.images.get(image_name(name))
    preview = bpy.data.objects.get(PREVIEW)
    if image is None or image.packed_file is None:
        return [f'The texture needs its image "{image_name(name)}" packed into the file.']
    width, height = image.size
    if width != height or width not in SIZES:
        errors.append(f'"{image.name}" is {width} by {height} pixels; a garment texture is '
                      f"{', '.join(str(s) for s in SIZES)} pixels square.")
    for key in ("roughness", "metallic"):
        value = image.get(key)
        if not isinstance(value, (int, float)) or not 0.0 <= value <= 1.0:
            errors.append(f'"{image.name}" needs the custom property {key} from 0 to 1.')
    pixels = np.empty(width * height * 4, np.float32)
    image.pixels.foreach_get(pixels)
    if (pixels[3::4] < ALPHA_CUT).any():
        errors.append(f'"{image.name}" has transparent parts; textures with holes and their own masks come with '
                      "delivery step 5c.")
    if preview is None or preview.type != "MESH" or not preview.data.materials or \
            preview.data.materials[0] is None or preview.data.materials[0].name != material_name(name):
        errors.append(f'The texture needs its "{PREVIEW}" with the material "{material_name(name)}".')
    elif preview.get(TEMPLATE_UVS) != current_template_uvs(template_file, template, label):
        errors.append(f"The template {template} has other UVs than the texture was painted in; run tools/Sync-TextureAsset.ps1.")
    return errors


def export(name: str, folder: str) -> tuple[str, dict]:
    """Writes the image as a PNG and returns its path and the material's record."""
    image = bpy.data.images[image_name(name)]
    os.makedirs(folder, exist_ok=True)
    path = os.path.join(folder, f"{image.name}.png")
    with open(path, "wb") as handle:
        handle.write(image.packed_file.data)
    record = {"name": material_name(name), "baseColor": "#FFFFFF", "baseMap": image.name,
              "metallic": round(float(image.get("metallic", 0.0)), 4),
              "smoothness": round(1.0 - float(image.get("roughness", 0.6)), 4)}
    return path, record


def dress(template: str, texture_file: str, name: str, labels: list[str]) -> None:
    """Puts the texture on every version of the template in the open template file, for its review
    sheet."""
    with bpy.data.libraries.load(texture_file, link=False) as (source, target):
        if image_name(name) not in source.images:
            raise ArtPipelineError(f'{texture_file} has no image "{image_name(name)}".')
        target.images = [image_name(name)]
    material = _material(name, target.images[0])
    for label in labels:
        version = bpy.data.objects[garment.version_name(template, label)]
        for slot in range(len(version.data.materials)):
            version.data.materials[slot] = material
