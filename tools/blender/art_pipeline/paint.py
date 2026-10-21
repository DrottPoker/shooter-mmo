"""Paint layers: transparent paint over the skin of a body, such as the paint of a leather jerkin,
in an art asset of its own. It is painted once, on the first body of the asset, in the image
T_Paint_<Name>_<Label> laid out like that body's texture T_<Part>_<Label>. Every other body has the
same topology, part by part, so the build moves the paint to its own UV layout: each texel of the
other body takes the paint at the same place of the same face on the first body.

The file holds, per body, one static mesh Paint_<Label>: the body's parts joined in a fixed order,
in its rest pose, drawn with a preview material that lays the paint over the skin. A body other
than the first also has the UV map of the first one, UVMap_Paint, which the move reads."""

import os
import tempfile

import bpy
import numpy as np

from .scene import ArtPipelineError, relative_image_paths

REFERENCE_COLLECTION = "Reference"
PAINT_UV = "UVMap_Paint"
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
BODY_SPACING = 1.2
# The roughness and metallic of the paint, kept as custom properties of the painted image.
DEFAULT_ROUGHNESS = 0.6
# A moved texel may differ this much from a fresh move before the file counts as stale.
MOVE_TOLERANCE = 2.5 / 255.0


def image_name(name: str, label: str) -> str:
    return f"T_Paint_{name}_{label}"


def _collection(name: str) -> bpy.types.Collection:
    collection = bpy.data.collections.get(name)
    if collection is None:
        collection = bpy.data.collections.new(name)
    if collection.name not in bpy.context.scene.collection.children:
        bpy.context.scene.collection.children.link(collection)
    return collection


def _suffix(obj: bpy.types.Object, label: str) -> str:
    return obj.name[len(label) + 1:] if obj.name.startswith(label + "_") else obj.name


def _joined_body(path: str, root: str, label: str, collection: bpy.types.Collection) -> bpy.types.Object:
    """The body's mesh parts of another file, joined in the order of their names without the label,
    so bodies of the same topology join into meshes of the same topology."""
    with bpy.data.libraries.load(path, link=False) as (source, target):
        if root not in source.objects:
            raise ArtPipelineError(f'{path} has no object "{root}".')
        target.objects = list(source.objects)
    loaded = [o for o in target.objects if o is not None]
    parts = sorted((o for o in loaded if o.type == "MESH"), key=lambda o: _suffix(o, label))
    for obj in loaded:
        collection.objects.link(obj)
    for obj in parts:
        obj.modifiers.clear()
        world = obj.matrix_world.copy()
        obj.parent = None
        obj.matrix_world = world
    for obj in loaded:
        if obj not in parts:
            bpy.data.objects.remove(obj, do_unlink=True)
    bpy.ops.object.select_all(action="DESELECT")
    for obj in parts:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    bpy.ops.object.join()
    body = bpy.context.view_layer.objects.active
    body.name = f"Paint_{label}"
    body.data.name = f"Paint_{label}"
    for group in list(body.vertex_groups):
        body.vertex_groups.remove(group)
    return body


def _body_material(body: bpy.types.Object, part: str, label: str) -> int:
    for index, slot in enumerate(body.material_slots):
        if slot.material is not None and slot.material.name in (f"M_{part}_{label}", f"Preview_{part}_{label}"):
            return index
    raise ArtPipelineError(f'"{body.name}" has no material M_{part}_{label}.')


def _skin_image(material: bpy.types.Material) -> bpy.types.Image:
    images = [n.image for n in material.node_tree.nodes if n.type == "TEX_IMAGE" and n.image is not None
              and not n.image.name.startswith("T_Paint_")]
    if len(images) != 1:
        raise ArtPipelineError(f'Material "{material.name}" must have one skin image.')
    return images[0]


def _paint_image(name: str, like: bpy.types.Image) -> bpy.types.Image:
    image = bpy.data.images.get(name)
    if image is None:
        image = bpy.data.images.new(name, like.size[0], like.size[1], alpha=True)
        image.generated_color = (0.0, 0.0, 0.0, 0.0)
        image.alpha_mode = "STRAIGHT"
        image["roughness"] = DEFAULT_ROUGHNESS
        image["metallic"] = 0.0
        store(image, np.zeros((like.size[1], like.size[0], 4), dtype=np.float32))
    return image


def store(image: bpy.types.Image, rgba: np.ndarray) -> None:
    """Writes straight RGBA pixels (rows bottom up, 0..1) into the image and packs it as a PNG.
    The image is packed from memory with a path relative to the file, so no file records the
    folder it was made in."""
    image.pixels.foreach_set(np.ascontiguousarray(rgba, dtype=np.float32).ravel())
    image.update()
    with tempfile.TemporaryDirectory() as scratch:
        path = os.path.join(scratch, f"{image.name}.png")
        image.filepath_raw = path
        image.file_format = "PNG"
        image.save()
        with open(path, "rb") as f:
            png = f.read()
    image.filepath_raw = f"//{image.name}.png"
    image.pack(data=png, data_len=len(png))
    image.source = "FILE"


def _preview_material(body: bpy.types.Object, index: int, part: str, label: str,
                      paint: bpy.types.Image) -> None:
    """The body material, with the paint laid over the skin by its alpha, also its roughness."""
    source = body.material_slots[index].material
    if source.name.startswith("Preview_"):
        return
    material = source.copy()
    material.name = f"Preview_{part}_{label}"
    nodes, links = material.node_tree.nodes, material.node_tree.links
    bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
    skin = next(n for n in nodes if n.type == "TEX_IMAGE")
    layer = nodes.new("ShaderNodeTexImage")
    layer.image = paint
    layer.location = (skin.location.x, skin.location.y - 300)
    color = nodes.new("ShaderNodeMix")
    color.data_type = "RGBA"
    links.new(layer.outputs["Alpha"], color.inputs["Factor"])
    links.new(skin.outputs["Color"], color.inputs["A"])
    links.new(layer.outputs["Color"], color.inputs["B"])
    links.new(color.outputs["Result"], bsdf.inputs["Base Color"])
    roughness = nodes.new("ShaderNodeMix")
    roughness.data_type = "FLOAT"
    links.new(layer.outputs["Alpha"], roughness.inputs["Factor"])
    roughness.inputs["A"].default_value = bsdf.inputs["Roughness"].default_value
    driver = roughness.inputs["B"].driver_add("default_value").driver
    variable = driver.variables.new()
    variable.targets[0].id_type = "IMAGE"
    variable.targets[0].id = paint
    variable.targets[0].data_path = '["roughness"]'
    driver.expression = variable.name
    links.new(roughness.outputs["Result"], bsdf.inputs["Roughness"])
    body.material_slots[index].material = material


def _copy_paint_uvs(source: bpy.types.Object, body: bpy.types.Object) -> None:
    """The first body's UV map on another body of the same topology."""
    a, b = source.data, body.data
    if len(a.loops) != len(b.loops) or len(a.polygons) != len(b.polygons) or any(
            pa.vertices[:] != pb.vertices[:] for pa, pb in zip(a.polygons, b.polygons)):
        raise ArtPipelineError(f'"{body.name}" and "{source.name}" must have the same topology for the paint to move.')
    if PAINT_UV not in b.uv_layers:
        b.uv_layers.new(name=PAINT_UV)
    uvs = np.empty(len(a.loops) * 2, dtype=np.float32)
    a.uv_layers["UVMap"].data.foreach_get("uv", uvs)
    b.uv_layers[PAINT_UV].data.foreach_set("uv", uvs)
    b.uv_layers.active = b.uv_layers["UVMap"]
    b.uv_layers["UVMap"].active_render = True


def bodies_in_file(name: str, part: str, labels: list[str]) -> list[tuple[bpy.types.Object, int, bpy.types.Image]]:
    """Each body's mesh, the slot of its painted material, and its paint image, first body first."""
    result = []
    for label in labels:
        body = bpy.data.objects.get(f"Paint_{label}")
        paint = bpy.data.images.get(image_name(name, label))
        if body is None or paint is None:
            raise ArtPipelineError(f"The file lacks Paint_{label} or {image_name(name, label)}; run "
                                   "tools/Sync-PaintAsset.ps1.")
        result.append((body, _body_material(body, part, label), paint))
    return result


def _bake_material(paint: bpy.types.Image, target: bpy.types.Image, channel: str) -> bpy.types.Material:
    material = bpy.data.materials.new(f"_move_{channel}")
    material.use_nodes = True
    nodes, links = material.node_tree.nodes, material.node_tree.links
    nodes.clear()
    out = nodes.new("ShaderNodeOutputMaterial")
    emit = nodes.new("ShaderNodeEmission")
    uv = nodes.new("ShaderNodeUVMap")
    uv.uv_map = PAINT_UV
    source = nodes.new("ShaderNodeTexImage")
    source.image = paint
    source.interpolation = "Linear"
    links.new(uv.outputs["UV"], source.inputs["Vector"])
    links.new(source.outputs["Color" if channel == "color" else "Alpha"], emit.inputs["Color"])
    links.new(emit.outputs["Emission"], out.inputs["Surface"])
    node = nodes.new("ShaderNodeTexImage")
    node.image = target
    nodes.active = node
    return material


def _linear_to_srgb(x: np.ndarray) -> np.ndarray:
    x = np.clip(x, 0.0, 1.0)
    return np.where(x <= 0.0031308, x * 12.92, 1.055 * np.power(x, 1.0 / 2.4) - 0.055)


def moved(first: tuple, other: tuple) -> np.ndarray:
    """The paint of the first body in the UV layout of the other one, as straight RGBA rows."""
    _source_body, _source_index, paint = first
    body, index, target = other
    width, height = target.size
    scene = bpy.context.scene
    engine = scene.render.engine
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 1
    bake = scene.render.bake
    bake.margin, bake.margin_type = 16, "EXTEND"
    own = [slot.material for slot in body.material_slots]
    scratch = bpy.data.images.new("_move_scratch", 8, 8, float_buffer=True)
    channels = {}
    try:
        for channel in ("color", "alpha"):
            result = bpy.data.images.new(f"_move_{channel}", width, height, float_buffer=True)
            result.colorspace_settings.name = "Non-Color"
            materials = []
            for slot_index, slot in enumerate(body.material_slots):
                # Only the painted material's faces write into the paint; the others, such as the
                # face in its own UV space, bake into a scratch image.
                material = _bake_material(paint, result if slot_index == index else scratch, channel)
                materials.append(material)
                slot.material = material
            bpy.ops.object.select_all(action="DESELECT")
            body.select_set(True)
            bpy.context.view_layer.objects.active = body
            bpy.ops.object.bake(type="EMIT", use_clear=True, use_selected_to_active=False)
            pixels = np.empty(width * height * 4, dtype=np.float32)
            result.pixels.foreach_get(pixels)
            channels[channel] = pixels.reshape(height, width, 4)
            bpy.data.images.remove(result)
            for slot, material in zip(body.material_slots, own):
                slot.material = material
            for material in materials:
                bpy.data.materials.remove(material)
    finally:
        for slot, material in zip(body.material_slots, own):
            slot.material = material
        bpy.data.images.remove(scratch)
        scene.render.engine = engine
    rgba = np.empty((height, width, 4), dtype=np.float32)
    # The color bakes as linear light; the paint image keeps sRGB values.
    rgba[..., :3] = _linear_to_srgb(channels["color"][..., :3])
    rgba[..., 3] = np.clip(channels["alpha"][..., 0], 0.0, 1.0)
    return np.round(rgba * 255.0) / 255.0


def refresh(name: str, part: str, labels: list[str]) -> list[str]:
    """Moves the paint of the first body to every other body and packs the results."""
    bodies = bodies_in_file(name, part, labels)
    for other in bodies[1:]:
        store(other[2], moved(bodies[0], other))
        other[2]["roughness"] = bodies[0][2].get("roughness", DEFAULT_ROUGHNESS)
        other[2]["metallic"] = bodies[0][2].get("metallic", 0.0)
    return [image_name(name, label) for label in labels]


def paint_errors(name: str, part: str, labels: list[str]) -> list[str]:
    """The rules of a paint asset: every paint image is a packed RGBA PNG of its body texture's size,
    with a roughness and metallic from 0 to 1, and the paint of every other body is the move of the
    first body's paint, so the file shows what the game will."""
    try:
        bodies = bodies_in_file(name, part, labels)
    except ArtPipelineError as error:
        return [str(error)]
    errors = []
    for body, index, image in bodies:
        skin = _skin_image(body.material_slots[index].material)
        data = bytes(image.packed_file.data) if image.packed_file is not None else b""
        if not data.startswith(PNG_SIGNATURE):
            errors.append(f'Paint "{image.name}" must be packed into the file as a PNG.')
        if tuple(image.size) != tuple(skin.size):
            errors.append(f'Paint "{image.name}" is {tuple(image.size)} pixels; the skin "{skin.name}" is '
                          f"{tuple(skin.size)}.")
        for prop in ("roughness", "metallic"):
            value = image.get(prop)
            if not isinstance(value, (int, float)) or not 0.0 <= value <= 1.0:
                errors.append(f'Paint "{image.name}" needs the custom property "{prop}" from 0 to 1.')
    if errors:
        return errors
    first = bodies[0][2]
    for other in bodies[1:]:
        width, height = other[2].size
        packed = np.empty(width * height * 4, dtype=np.float32)
        other[2].pixels.foreach_get(packed)
        difference = np.abs(packed.reshape(height, width, 4) - moved(bodies[0], other))
        if float(difference.max()) > MOVE_TOLERANCE:
            errors.append(f'Paint "{other[2].name}" is not the move of "{first.name}"; run tools/Sync-PaintAsset.ps1.')
        for prop in ("roughness", "metallic"):
            if abs(float(other[2].get(prop, -1.0)) - float(first.get(prop, -2.0))) > 1e-6:
                errors.append(f'Paint "{other[2].name}" must have the {prop} of "{first.name}".')
    return errors


def paint_record(name: str, labels: list[str]) -> dict[str, float]:
    first = bpy.data.images[image_name(name, labels[0])]
    return {"smoothness": round(1.0 - float(first["roughness"]), 2), "metallic": round(float(first["metallic"]), 2)}


def export_images(name: str, labels: list[str], folder: str) -> list[tuple[str, str]]:
    written = []
    os.makedirs(folder, exist_ok=True)
    for label in labels:
        image = bpy.data.images[image_name(name, label)]
        path = os.path.join(folder, f"{image.name}.png")
        with open(path, "wb") as f:
            f.write(bytes(image.packed_file.data))
        written.append((image.name, path))
    return written


def _ground(collection: bpy.types.Collection, width: float) -> None:
    mesh = bpy.data.meshes.new("Reference_Ground")
    half = max(width, 3.0) / 2.0 + 1.0
    mesh.from_pydata([(-half, -2.0, 0.0), (half, -2.0, 0.0), (half, 2.0, 0.0), (-half, 2.0, 0.0)], [], [(0, 1, 2, 3)])
    material = bpy.data.materials.new("Reference_Ground")
    material.use_nodes = True
    bsdf = next(n for n in material.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = (0.08, 0.11, 0.08, 1.0)
    bsdf.inputs["Roughness"].default_value = 0.9
    mesh.materials.append(material)
    ground = bpy.data.objects.new("Reference_Ground", mesh)
    ground.location.x = width / 2.0
    ground.hide_select = True
    collection.objects.link(ground)


def sync(path: str, name: str, part: str, bodies: list[tuple[str, str]], force: bool = False) -> bool:
    """Builds the paint file from the bodies (file, armature) or rebuilds its body meshes, keeps the
    painted image of the first body, moves its paint to the others, and saves. Returns False and
    leaves an existing file as it is when its bodies are there and its moved paint is current."""
    labels = [root for _path, root in bodies]
    if os.path.isfile(path):
        bpy.ops.wm.open_mainfile(filepath=path)
        if not force and not paint_errors(name, part, labels):
            return False
    else:
        bpy.ops.wm.read_factory_settings(use_empty=True)
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)
    for material in [m for m in bpy.data.materials if m.name.startswith("Preview_")]:
        bpy.data.materials.remove(material)
    bpy.data.orphans_purge(do_local_ids=True, do_linked_ids=True, do_recursive=True)
    reference = _collection(REFERENCE_COLLECTION)
    joined = []
    for index, (body_path, root) in enumerate(bodies):
        body = _joined_body(body_path, root, root, reference)
        body.location.x = BODY_SPACING * index
        slot = _body_material(body, part, root)
        skin = _skin_image(body.material_slots[slot].material)
        paint = _paint_image(image_name(name, root), skin)
        paint.use_fake_user = True
        _preview_material(body, slot, part, root, paint)
        if joined:
            _copy_paint_uvs(joined[0], body)
        joined.append(body)
    _ground(reference, BODY_SPACING * (len(bodies) - 1))
    relative_image_paths()
    for screen in bpy.data.screens:
        for area in screen.areas:
            for space in area.spaces:
                if space.type == "VIEW_3D":
                    space.shading.type = "MATERIAL"
    refresh(name, part, labels)
    bpy.ops.wm.save_as_mainfile(filepath=path, compress=True, relative_remap=False)
    return True
