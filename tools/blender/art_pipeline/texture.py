"""Garment textures: the look of a garment template, its painted image in the template's UVs, in an
art asset of its own, so a new texture never changes the template's file. See
docs/EQUIPMENT_VISUALS_DESIGN.md.

The file holds the image T_Garment_<Name>, packed, with the custom properties roughness and
metallic, and the mesh Preview, a copy of the template's first version with the material
M_Garment_<Name> that shows the image, for painting in Texture Paint mode. Preview keeps the UVs the
image is painted in, and the property template_uvs the fingerprint of the template's UVs then, so a
template whose UVs change is noticed and its texture moved to the new UVs by projection from
Preview. A texture may also start from the look of another mesh by projection, such as a garment
made before templates.

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

from . import coverage, garment
from .export import PNG_SIGNATURE
from .scene import ArtPipelineError, relative_image_paths

PREVIEW = "Preview"
TEMPLATE_UVS = "template_uvs"
SIZES = (512, 1024, 2048)
# A projection looks this far off the target's surface, both ways, for the source's surface.
PROJECTION_REACH = 0.06
BAKE_MARGIN = 8
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


def _bake_dye(source: bpy.types.Object, target: bpy.types.Object, source_dye: bpy.types.Image,
              dye: bpy.types.Image) -> None:
    """The source's dye mask baked into the dye mask in the target's UVs, as its emission, the same
    way as its alpha; a texel no ray reached is dyed by no channel."""
    shown = bpy.data.materials.new("DyeShown")
    shown.use_nodes = True
    nodes = shown.node_tree.nodes
    for node in list(nodes):
        nodes.remove(node)
    texture = nodes.new("ShaderNodeTexImage")
    texture.image = source_dye
    emission = nodes.new("ShaderNodeEmission")
    output = nodes.new("ShaderNodeOutputMaterial")
    shown.node_tree.links.new(texture.outputs["Color"], emission.inputs["Color"])
    shown.node_tree.links.new(emission.outputs["Emission"], output.inputs["Surface"])
    width, height = dye.size
    baked = bpy.data.images.new("DyeBaked", width, height, alpha=True, float_buffer=True)
    baked.colorspace_settings.name = "Non-Color"
    kept = list(source.data.materials)
    try:
        for slot in range(len(source.data.materials)):
            source.data.materials[slot] = shown
        _bake_pass(source, target, baked, "DYE")
    finally:
        for slot, original in enumerate(kept):
            source.data.materials[slot] = original
        bpy.data.materials.remove(shown)
    values = np.empty(width * height * 4, np.float32)
    baked.pixels.foreach_get(values)
    bpy.data.images.remove(baked)
    values = values.reshape(-1, 4)
    reached = values[:, 3:4] > 0.5
    values[:, :3] = np.where(reached, np.clip(values[:, :3], 0.0, 1.0), 0.0)
    values[:, 3] = 1.0
    dye.pixels.foreach_set(values.ravel())
    dye.update()


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
    mesh moved over by projection, and the preview on the template's first version."""
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
    UVs changed, or when the preview's own faces or UVs were edited, so the image is always in the
    UVs of the model Unity draws. Returns whether the file changed; an unchanged file is not saved,
    so its approval stays valid."""
    bpy.ops.wm.open_mainfile(filepath=file)
    preview = bpy.data.objects.get(PREVIEW)
    image = bpy.data.images.get(image_name(name))
    if preview is None or image is None:
        raise ArtPipelineError(f'The texture needs its image "{image_name(name)}" and its "{PREVIEW}".')
    current = current_template_uvs(template_file, template, label)
    if preview.get(TEMPLATE_UVS) == current and uv_fingerprint(preview.data) == current:
        return False
    dye = bpy.data.images.get(dye_image_name(name))
    colors = parse_dye_colors(dye.get(DYE_COLORS)) if dye is not None else None
    old = preview
    old.name = f"{PREVIEW}_old"
    old_image = image.copy()
    old_image.pack()
    old_dye = None
    if colors:
        old_dye = dye.copy()
        old_dye.pack()
        old_dye[DYE_COLORS] = ";".join(colors)
    # The old preview shows the texture dyed in its natural colors, so the bake moves its painted
    # look, and the neutral image is divided again by the moved mask.
    old.data.materials[0] = _material(f"{name}_old", old_image, old_dye)
    mesh = _template_first(template_file, template, label)
    new = _preview(name, mesh, image)
    _bake(old, new, image)
    if colors:
        _bake_dye(old, new, old_dye, dye)
        pixels = _pixels(image)
        neutral = srgb_to_linear(pixels[..., :3]) / dye_factor(_pixels(dye)[..., :3], colors)
        _set_linear(image, np.clip(neutral, 0.0, 1.0), pixels[..., 3])
        dye.pack()
        new.data.materials[0] = _material(name, image, dye)
    new[TEMPLATE_UVS] = current
    image.pack()
    old_mesh = old.data
    bpy.data.objects.remove(old, do_unlink=True)
    bpy.data.meshes.remove(old_mesh)
    bpy.data.images.remove(old_image)
    if old_dye is not None:
        bpy.data.images.remove(old_dye)
    bpy.data.orphans_purge(do_recursive=True)
    relative_image_paths()
    bpy.ops.wm.save_as_mainfile(filepath=file, compress=True, relative_remap=False)
    return True


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
        errors.append(f'"{PREVIEW}" must show "{dye.name}"; run tools/Set-TextureDyes.ps1 again.')
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
            errors.append(f"The template {template} has other UVs than the texture was painted in; run tools/Sync-TextureAsset.ps1.")
        elif uv_fingerprint(preview.data) != current:
            errors.append(f'"{PREVIEW}" has other faces or UVs than the template {template}; run tools/Sync-TextureAsset.ps1, '
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
          libraries: list[tuple[str, list[str]]], covers: bool = True) -> dict[str, tuple[coverage.Coverage, coverage.Motion]]:
    """The texture's own masks on every body, and the skin that comes through the garment in the test
    poses and the clips of the libraries, computed in the template's file with the texture on every
    version. The template's file is opened in place of the texture's and not saved."""
    bpy.ops.wm.open_mainfile(filepath=template_file)
    dress(template, texture_file, name, labels)
    actions = coverage.load_actions(libraries)
    results = {}
    for label in labels:
        result = coverage.compute(template, label, band, covers)
        results[label] = (result, coverage.check_motion(template, result, actions))
    return results


def mask_record(name: str, template: str, result: coverage.Coverage) -> dict:
    """The texture's masks for one body, in the data format of its template's."""
    data = coverage.record(template, result)
    data["garment"] = name
    return data
