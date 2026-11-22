"""Reads the content parts of the open asset file by kind, so an approval binds to what a review stage
shows and the export writes instead of to the bytes of the file. content_core.py hashes the parts and
chains the stages; docs/ART_PIPELINE.md lists what each part holds under Review Stages.

Content is the asset's own data: what the export writes, what the review shows, and what another asset
reads from the file. Copies of other assets, such as the bodies of a garment, bookkeeping of another
asset's state, view and editor state, unused datablocks, and the order of named things are not."""

import os

import bpy
import numpy as np
from bpy_extras import anim_utils

from . import content_core as core
from . import texture
from .scene import EXPORT_COLLECTION, ArtPipelineError

SOURCE_COLLECTION = "Source"
# The edge attribute that marks the seams a person unwraps UVs along.
UV_SEAM = "uv_seam"
# A part whose object or image the file lacks, so its fingerprint differs from any real one.
MISSING = "missing"
# Settings of modifiers and constraints that only the interface uses.
_UI_SETTINGS = {"rna_type", "show_expanded", "show_in_editmode", "show_on_cage", "is_active", "active",
                "is_override_data_editable", "persistent_uid", "execution_time", "use_apply_on_spline"}
# The field and width of every attribute data type.
_ATTRIBUTE_FIELDS = {
    "FLOAT": ("value", 1, np.float64), "INT": ("value", 1, np.int32), "BOOLEAN": ("value", 1, np.bool_),
    "INT8": ("value", 1, np.int32), "FLOAT_VECTOR": ("vector", 3, np.float64), "FLOAT2": ("vector", 2, np.float64),
    "INT16_2D": ("value", 2, np.int32), "INT32_2D": ("value", 2, np.int32), "FLOAT_COLOR": ("color", 4, np.float64),
    "BYTE_COLOR": ("color", 4, np.float64), "QUATERNION": ("value", 4, np.float64), "FLOAT4X4": ("value", 16, np.float64),
}


def _plain(value):
    """A custom property's value as plain data."""
    if hasattr(value, "to_dict"):
        return {str(k): _plain(v) for k, v in value.to_dict().items()}
    if hasattr(value, "to_list"):
        return [_plain(v) for v in value.to_list()]
    if isinstance(value, dict):
        return {str(k): _plain(v) for k, v in value.items()}
    if isinstance(value, (list, tuple)):
        return [_plain(v) for v in value]
    if value is None or isinstance(value, (str, bool, int, float)):
        return value
    if isinstance(value, bpy.types.ID):
        return value.name
    return repr(value)


def _properties(owner) -> dict:
    """The custom properties of a datablock, bone, or pose bone, by name."""
    return {str(key): _plain(owner[key]) for key in owner.keys()}


def _settings(struct) -> dict:
    """What a modifier, constraint, or F-curve modifier does: its type and every setting that is not
    interface state, with other datablocks by name."""
    found = {"type": getattr(struct, "type", type(struct).__name__)}
    for prop in struct.bl_rna.properties:
        key = prop.identifier
        if key in _UI_SETTINGS or prop.is_readonly or prop.type == "COLLECTION":
            continue
        value = getattr(struct, key, None)
        if prop.type == "POINTER":
            if isinstance(value, bpy.types.ID):
                found[key] = value.name
            elif value is None:
                found[key] = None
            continue
        if prop.type == "ENUM" and prop.is_enum_flag:
            found[key] = sorted(value)
        elif prop.type == "FLOAT" and prop.array_length > 0:
            found[key] = np.array(value, np.float64).reshape(-1)
        elif prop.type in ("INT", "BOOLEAN") and prop.array_length > 0:
            found[key] = [int(v) for v in value]
        else:
            found[key] = value
    return found


def _drivers(owner) -> list:
    data = getattr(owner, "animation_data", None)
    if data is None:
        return []
    return sorted([d.data_path, d.array_index, d.driver.type, d.driver.expression] for d in data.drivers)


def _floats(collection, field: str, width: int) -> np.ndarray:
    values = np.empty(len(collection) * width, np.float64)
    if len(collection):
        collection.foreach_get(field, values)
    return values.reshape(-1, width) if width > 1 else values


def _ints(collection, field: str, width: int) -> np.ndarray:
    values = np.empty(len(collection) * width, np.int32)
    if len(collection):
        collection.foreach_get(field, values)
    return values.astype(np.int64)


def _attribute(attribute) -> dict:
    field, width, dtype = _ATTRIBUTE_FIELDS.get(attribute.data_type, (None, 0, None))
    record = {"domain": attribute.domain, "type": attribute.data_type}
    if field is None:
        record["values"] = [repr(getattr(d, "value", None)) for d in attribute.data]
        return record
    values = np.empty(len(attribute.data) * width, dtype)
    if len(attribute.data):
        attribute.data.foreach_get(field, values)
    record["values"] = values.astype(np.uint8) if dtype is np.bool_ else values
    return record


def _object(obj: bpy.types.Object, properties: bool = True) -> dict:
    """An object as the export places it: its type, data, parent, world matrix, rotation mode, its
    viewport switch, which the FBX writes as its visibility, its custom properties, its modifiers
    but the armature's, its constraints, drivers, and material slots."""
    parent = obj.parent
    return {
        "type": obj.type,
        "data": obj.data.name if obj.data is not None else None,
        "parent": parent.name if parent is not None else None,
        "parentType": obj.parent_type if parent is not None else None,
        "parentBone": obj.parent_bone if parent is not None and obj.parent_type == "BONE" else None,
        "matrix": np.array(obj.matrix_world, np.float64).reshape(-1),
        "rotationMode": obj.rotation_mode,
        "hideViewport": obj.hide_viewport,
        "properties": _properties(obj) if properties else {},
        "modifiers": [_settings(m) for m in obj.modifiers if m.type != "ARMATURE"],
        "constraints": [_settings(c) for c in obj.constraints],
        "drivers": _drivers(obj),
        "materials": [[slot.link, slot.material.name if slot.material is not None else None] for slot in obj.material_slots],
    }


def _shape_keys(mesh: bpy.types.Mesh):
    keys = mesh.shape_keys
    if keys is None:
        return None
    return {
        "relative": keys.use_relative,
        "reference": keys.reference_key.name if keys.reference_key is not None else None,
        "blocks": [{"name": block.name, "relativeKey": block.relative_key.name if block.relative_key else None,
                    "value": block.value, "mute": block.mute, "vertexGroup": block.vertex_group,
                    "sliderMin": block.slider_min, "sliderMax": block.slider_max, "interpolation": block.interpolation,
                    "co": _floats(block.data, "co", 3)} for block in keys.key_blocks],
    }


def _mesh(obj: bpy.types.Object, uv_maps: bool = False) -> dict:
    """A mesh as the export sees it: its vertices, edges, faces and corners in their order, its corner
    normals, every attribute but the UV maps and the seams, which only unwrapping reads, its shape
    keys, and its custom properties. With uv_maps also its UV maps; without them they are a part of
    their own."""
    mesh = obj.data
    uv_names = {layer.name for layer in mesh.uv_layers} | {UV_SEAM}
    record = {
        "name": mesh.name,
        "vertices": _floats(mesh.vertices, "co", 3),
        "edges": _ints(mesh.edges, "vertices", 2),
        "faceSizes": _ints(mesh.polygons, "loop_total", 1),
        "corners": _ints(mesh.loops, "vertex_index", 1),
        "normals": _floats(mesh.corner_normals, "vector", 3),
        "attributes": {a.name: _attribute(a) for a in mesh.attributes
                       if not a.name.startswith(".") and a.name != "position" and a.name not in uv_names},
        "shapeKeys": _shape_keys(mesh),
        "properties": _properties(mesh),
    }
    if uv_maps:
        record["uvMaps"] = _uvs(obj)
    return record


def _uvs(obj: bpy.types.Object, skip: str | None = None) -> list:
    """The UV maps of a mesh in their order, which is the order of the FBX's UV channels, with which
    is active and which renders."""
    return [[layer.name, layer.active, layer.active_render, _floats(layer.data, "uv", 2)]
            for layer in obj.data.uv_layers if layer.name != skip]


def _weights(obj: bpy.types.Object) -> dict:
    """A mesh's skin: its vertex groups by name with every vertex's weight in them, and its armature
    modifiers."""
    names = sorted(g.name for g in obj.vertex_groups)
    column = {g.index: names.index(g.name) for g in obj.vertex_groups}
    weights = np.zeros((len(obj.data.vertices), len(names)))
    for vertex in obj.data.vertices:
        for group in vertex.groups:
            if group.group in column:
                weights[vertex.index, column[group.group]] = group.weight
    armatures = [{"name": m.name, "object": m.object.name if m.object is not None else None,
                  "vertexGroups": m.use_vertex_groups, "envelopes": m.use_bone_envelopes,
                  "preserveVolume": m.use_deform_preserve_volume, "multiModifier": m.use_multi_modifier,
                  "viewport": m.show_viewport, "render": m.show_render}
                 for m in obj.modifiers if m.type == "ARMATURE"]
    return {"groups": names, "weights": core.Rounded(weights, core.WEIGHT_DECIMALS), "armatures": armatures}


def _skeleton(arm: bpy.types.Object, properties: bool) -> dict:
    """An armature's rest pose: every bone by name with its parent, deform and connect flags, rest
    matrix, length, how it inherits its parent's motion, and its custom properties; every pose bone's
    rotation mode and constraints, which the clips are read through; and, with properties, the
    armature's custom properties, such as the landmarks of a body's rig."""
    bones = {bone.name: {
        "parent": bone.parent.name if bone.parent is not None else None,
        "deform": bone.use_deform,
        "connect": bone.use_connect,
        "matrix": np.array(bone.matrix_local, np.float64).reshape(-1),
        "length": bone.length,
        "inheritRotation": bone.use_inherit_rotation,
        "inheritScale": bone.inherit_scale,
        "localLocation": bone.use_local_location,
        "relativeParent": bone.use_relative_parent,
        "properties": _properties(bone),
    } for bone in arm.data.bones}
    pose = {pb.name: {"rotationMode": pb.rotation_mode, "constraints": [_settings(c) for c in pb.constraints],
                      "properties": _properties(pb)} for pb in arm.pose.bones} if arm.pose is not None else {}
    return {"data": arm.data.name, "bones": bones, "pose": pose, "posePosition": arm.data.pose_position,
            "dataProperties": _properties(arm.data), "properties": _properties(arm) if properties else {}}


def _keyframe_enums(points, field: str) -> list:
    return [getattr(point, field) for point in points]


def _clip(action: bpy.types.Action) -> dict:
    """An animation clip as the export bakes it: its curves by slot, path, and channel, with their
    keys, handles, interpolation, extrapolation, and modifiers, its frame range, its custom
    properties, such as loop, and the scene's frame rate it plays at."""
    curves = []
    for slot in action.slots:
        channelbag = anim_utils.action_get_channelbag_for_slot(action, slot)
        for fc in (channelbag.fcurves if channelbag is not None else []):
            points = fc.keyframe_points
            curves.append({
                "slot": slot.identifier, "path": fc.data_path, "index": fc.array_index,
                "keys": _floats(points, "co", 2), "left": _floats(points, "handle_left", 2),
                "right": _floats(points, "handle_right", 2), "interpolation": _keyframe_enums(points, "interpolation"),
                "easing": _keyframe_enums(points, "easing"), "leftType": _keyframe_enums(points, "handle_left_type"),
                "rightType": _keyframe_enums(points, "handle_right_type"), "extrapolation": fc.extrapolation,
                "mute": fc.mute, "modifiers": [_settings(m) for m in fc.modifiers],
            })
    curves.sort(key=lambda c: (c["slot"], c["path"], c["index"]))
    scene = bpy.context.scene
    return {"frameRange": list(action.frame_range), "manualRange": action.use_frame_range, "cyclic": action.use_cyclic,
            "properties": _properties(action), "slots": sorted([s.identifier, s.target_id_type] for s in action.slots),
            "curves": curves, "fps": scene.render.fps, "fpsBase": scene.render.fps_base}


def _image(image: bpy.types.Image, properties: bool = True) -> dict:
    """An image's pixels, 8-bit values of an 8-bit image and float32 values of a float image, with its
    size, channels, color space, alpha mode, whether it is packed, and its custom properties."""
    width, height = image.size
    record = {"size": [width, height], "channels": image.channels, "colorSpace": image.colorspace_settings.name,
              "alphaMode": image.alpha_mode, "float": image.is_float, "packed": image.packed_file is not None,
              "source": image.source, "properties": _properties(image) if properties else {}}
    count = width * height * image.channels
    if count:
        pixels = np.empty(count, np.float32)
        image.pixels.foreach_get(pixels)
        record["pixels"] = pixels if image.is_float else np.round(pixels * 255.0).astype(np.uint8)
    return record


def _node_value(socket):
    value = socket.default_value
    return list(value) if hasattr(value, "__len__") and not isinstance(value, str) else value


def _material(material: bpy.types.Material) -> dict:
    """A material's node tree as human_base/checks.py compares it: every node with its image, the
    values of its unlinked inputs, and the settings that say what it does, and every link."""
    nodes, links = [], []
    if material.node_tree is not None:
        for node in material.node_tree.nodes:
            values = {s.identifier: _node_value(s) for s in node.inputs if hasattr(s, "default_value") and not s.is_linked}
            settings = {p: getattr(node, p) for p in ("interpolation", "extension", "projection", "distribution",
                                                       "subsurface_method", "target", "data_type", "blend_type",
                                                       "operation", "clamp_factor", "clamp_result", "uv_map")
                        if hasattr(node, p)}
            image = getattr(node, "image", None)
            nodes.append({"name": node.name, "type": node.bl_idname, "image": image.name if image is not None else None,
                          "values": values, "settings": settings})
        links = sorted([link.from_node.name, link.from_socket.identifier, link.to_node.name, link.to_socket.identifier]
                       for link in material.node_tree.links)
    nodes.sort(key=lambda n: n["name"])
    return {"nodes": nodes, "links": links, "properties": _properties(material)}


def _materials(objects: list[bpy.types.Object], variant_prefixes: bool = False) -> dict:
    """The materials of the meshes by name with the pixels of the images they use, and with
    variant_prefixes every other image of a painted material's variants, T_<Part>_<Label>_<Option>."""
    materials = {slot.material.name: slot.material for obj in objects if obj.type == "MESH"
                 for slot in obj.material_slots if slot.material is not None}
    images = {}
    for material in materials.values():
        for node in (material.node_tree.nodes if material.node_tree is not None else []):
            image = getattr(node, "image", None)
            if image is not None:
                images[image.name] = image
    if variant_prefixes:
        prefixes = tuple("T_" + name[2:] + "_" for name in materials if name.startswith("M_"))
        images.update({i.name: i for i in bpy.data.images if i.name.startswith(prefixes)})
    return {"materials": {name: _material(m) for name, m in materials.items()},
            "images": {name: _image(image) for name, image in images.items()}}


def _collection_objects(name: str) -> list[bpy.types.Object]:
    collection = bpy.data.collections.get(name)
    return sorted(collection.all_objects, key=lambda o: o.name) if collection is not None else []


def _model_parts(objects: list[bpy.types.Object], variant_prefixes: bool = False) -> dict:
    """The parts of a model stage: shape, uv, and materials. An armature's custom properties belong to
    its skeleton."""
    shape = {}
    for obj in objects:
        entry = {"object": _object(obj, properties=obj.type != "ARMATURE")}
        if obj.type == "MESH":
            entry["mesh"] = _mesh(obj)
        shape[obj.name] = entry
    uv = {obj.name: _uvs(obj) for obj in objects if obj.type == "MESH"}
    return {"shape": shape, "uv": uv, "materials": _materials(objects, variant_prefixes)}


def _clip_parts(args) -> dict:
    """A part for every clip of asset.json; the validation finds an action that is no clip."""
    names = [c for c in (getattr(args, "clips", "") or "").split(",") if c]
    return {f"clip:{name}": _clip(bpy.data.actions[name]) if name in bpy.data.actions else MISSING for name in names}


def _static(args) -> dict:
    return _model_parts(_collection_objects(EXPORT_COLLECTION))


def _skinned(args, has_clips: bool) -> dict:
    """The exported objects and the editable source body, whose zones, paint UVs, and weights the
    export reads."""
    exported = _collection_objects(EXPORT_COLLECTION)
    objects = exported + [o for o in _collection_objects(SOURCE_COLLECTION) if o not in exported]
    parts = _model_parts(objects, variant_prefixes=True)
    parts["skeleton"] = {o.name: _skeleton(o, properties=True) for o in objects if o.type == "ARMATURE"}
    parts["weights"] = {o.name: _weights(o) for o in objects if o.type == "MESH"}
    if has_clips:
        parts.update(_clip_parts(args))
    return parts


def _animations(args) -> dict:
    """The armature the clips are keyed on, placed as the export places it, and the clips. The bodies
    that show the clips are copies of other assets."""
    armatures = [o for o in _collection_objects(EXPORT_COLLECTION) if o.type == "ARMATURE"]
    parts = {"skeleton": {o.name: {"object": _object(o, properties=False), "rig": _skeleton(o, properties=False)}
                          for o in armatures}}
    parts.update(_clip_parts(args))
    return parts


def _garment(args) -> dict:
    """Every body's version of the template. Its uv part is the fingerprint of the first version's UVs
    that a texture records in template_uvs; the other UV maps belong to its shape. The bodies, their
    armatures, and the cage are copies."""
    labels = args.roots.split(",")
    name = args.garment
    versions = [bpy.data.objects.get(f"{name}_{label}") for label in labels]
    first = versions[0]
    active = first.data.uv_layers.active.name if first is not None and first.data.uv_layers.active is not None else None
    shape, weights = {}, {}
    for label, version in zip(labels, versions):
        key = f"{name}_{label}"
        if version is None or version.type != "MESH":
            shape[key] = weights[key] = MISSING
            continue
        entry = {"object": _object(version), "mesh": _mesh(version)}
        entry["mesh"]["uvMaps"] = _uvs(version, skip=active if version == first else None)
        shape[key] = entry
        weights[key] = _weights(version)
    present = [v for v in versions if v is not None and v.type == "MESH"]
    uv = texture.uv_fingerprint(first.data) if first is not None and first.type == "MESH" else core.fingerprint(MISSING)
    return {"shape": shape, "uv": uv, "materials": _materials(present), "weights": weights}


def _texture(args) -> dict:
    """The texture's image and dye mask, the values and dye colors the export reads with the
    material that shows them, and the fingerprint of the template's UVs it is painted in. Preview
    and its cage are copies of the template."""
    name = args.texture
    images = [bpy.data.images.get(texture.image_name(name)), bpy.data.images.get(texture.dye_image_name(name))]
    material = bpy.data.materials.get(texture.material_name(name))
    preview = bpy.data.objects.get(texture.PREVIEW)
    stored = preview.get(texture.TEMPLATE_UVS) if preview is not None else None
    return {
        "image": {i.name: _image(i, properties=False) for i in images if i is not None} or MISSING,
        "material": {"images": {i.name: _properties(i) for i in images if i is not None},
                     "material": _material(material) if material is not None else MISSING},
        "templateUv": stored if isinstance(stored, str) and core.SHA_PATTERN.match(stored) else core.fingerprint(MISSING),
    }


def _paint(args) -> dict:
    """The canvas and its cage version, the canvas's paint with its roughness and metallic, and the
    paint moved to every body, as the export writes it. The bodies are copies."""
    name = args.paint
    canvas = {}
    for obj_name in ("Canvas", "Canvas_Cage"):
        obj = bpy.data.objects.get(obj_name)
        canvas[obj_name] = MISSING if obj is None or obj.type != "MESH" else {"object": _object(obj),
                                                                               "mesh": _mesh(obj, uv_maps=True)}
    paint_image = bpy.data.images.get(f"T_Paint_{name}")
    parts = {"canvas": canvas, "paint": _image(paint_image) if paint_image is not None else MISSING}
    for label in args.roots.split(","):
        image = bpy.data.images.get(f"T_Paint_{name}_{label}")
        parts[f"body:{label}"] = _image(image) if image is not None else MISSING
    return parts


def _cage(args) -> dict:
    """The cage's reference, its rig and its net with the net's weights, and its version on every
    body. The bodies are copies."""
    net = {}
    reference = bpy.data.objects.get("Cage")
    net["Cage"] = MISSING if reference is None or reference.type != "MESH" else {
        "object": _object(reference), "mesh": _mesh(reference), "weights": _weights(reference)}
    rig = bpy.data.objects.get("CageRig")
    net["CageRig"] = MISSING if rig is None or rig.type != "ARMATURE" else {
        "object": _object(rig, properties=False), "rig": _skeleton(rig, properties=True)}
    parts = {"net": net}
    for label in args.roots.split(","):
        version = bpy.data.objects.get(f"Cage_{label}")
        parts[f"fit:{label}"] = MISSING if version is None or version.type != "MESH" else {
            "object": _object(version), "mesh": _mesh(version)}
    return parts


def read(args) -> dict:
    """The content record of the open file: its version, its stage fingerprints, and its parts."""
    # The world matrices of a file just opened are evaluated with its view layer.
    bpy.context.view_layer.update()
    has_clips = bool([c for c in (getattr(args, "clips", "") or "").split(",") if c])
    stages = core.stages_of(args.kind, has_clips)
    if args.kind == "static":
        data = _static(args)
    elif args.kind == "skinned":
        data = _skinned(args, "animation" in stages)
    elif args.kind == "animations":
        data = _animations(args)
    elif args.kind == "garment":
        data = _garment(args)
    elif args.kind == "texture":
        data = _texture(args)
    elif args.kind == "paint":
        data = _paint(args)
    elif args.kind == "cage":
        data = _cage(args)
    else:
        raise ArtPipelineError(f'An asset of kind "{args.kind}" has no content.')
    parts = {name: value if isinstance(value, str) and core.SHA_PATTERN.match(value) else core.fingerprint(value)
             for name, value in data.items()}
    return core.record(args.kind, parts, stages)


# The scripted edits of the proof in tools/art/Test-ArtContent.ps1, which makes them on copies: edits
# of content that change exactly the parts they touch, and edits of what is not content.
PROOF_EDITS = ("vertex", "net-vertex", "uv", "material", "pixel", "body-pixel", "weight", "bone", "key", "copy",
               "select", "active", "hide", "frame", "unused")


def _own_meshes(args) -> list[bpy.types.Object]:
    """The asset's own meshes that an edit of its content changes, first the one it edits."""
    if args.kind in ("static", "skinned"):
        return [o for o in _collection_objects(EXPORT_COLLECTION) if o.type == "MESH"]
    labels = args.roots.split(",")
    names = {"garment": [f"{args.garment}_{label}" for label in labels], "paint": ["Canvas"],
             "cage": [f"Cage_{label}" for label in labels]}.get(args.kind, [])
    return [bpy.data.objects[n] for n in names if n in bpy.data.objects]


def _first(found: list, what: str):
    if not found:
        raise ArtPipelineError(f"The proof edit needs {what}, which the file lacks.")
    return found[0]


def _set_pixel(image: bpy.types.Image) -> None:
    """Changes the first pixel's red by one step and packs the image again, so the save keeps it."""
    pixels = np.empty(len(image.pixels), np.float32)
    image.pixels.foreach_get(pixels)
    pixels[0] = pixels[0] - 1.0 / 255.0 if pixels[0] >= 0.5 else pixels[0] + 1.0 / 255.0
    image.pixels.foreach_set(pixels)
    image.update()
    image.pack()


def proof_edit(case: str, args) -> None:
    """Makes one scripted edit in the open file, which must be a copy: an asset's own file has its
    asset.json next to it."""
    if os.path.exists(os.path.join(os.path.dirname(bpy.data.filepath), "asset.json")):
        raise ArtPipelineError(f"{bpy.data.filepath} is an asset's own file; the proof edits copies only.")
    if case in ("vertex", "net-vertex", "copy"):
        if case == "net-vertex":
            obj = _first([bpy.data.objects[n] for n in ("Cage",) if n in bpy.data.objects], "the cage's reference")
        elif case == "copy":
            reference = [o for o in _collection_objects("Reference") if o.type == "MESH"]
            if args.kind == "texture":
                reference = [bpy.data.objects[texture.PREVIEW]]
            obj = _first(reference, "a copy of another asset")
        else:
            obj = _first(_own_meshes(args), "a mesh of its own")
        # A hundredth of a millimeter.
        obj.data.vertices[0].co.x += 0.00001
        obj.data.update()
    elif case == "uv":
        obj = _first([o for o in _own_meshes(args) if o.data.uv_layers.active is not None], "a UV map")
        obj.data.uv_layers.active.data[0].uv.x += 0.001
    elif case == "material":
        if args.kind == "texture":
            image = bpy.data.images[texture.image_name(args.texture)]
            image["roughness"] = float(image["roughness"]) + (0.1 if float(image["roughness"]) < 0.9 else -0.1)
        else:
            materials = sorted({s.material for o in _own_meshes(args) for s in o.material_slots if s.material is not None},
                               key=lambda m: m.name)
            bsdf = next(n for n in _first(materials, "a material").node_tree.nodes if n.type == "BSDF_PRINCIPLED")
            roughness = bsdf.inputs["Roughness"]
            roughness.default_value += 0.1 if roughness.default_value < 0.9 else -0.1
    elif case in ("pixel", "body-pixel"):
        if args.kind == "texture":
            image = bpy.data.images[texture.image_name(args.texture)]
        elif args.kind == "paint":
            label = args.roots.split(",")[0]
            image = bpy.data.images[f"T_Paint_{args.paint}" + (f"_{label}" if case == "body-pixel" else "")]
        else:
            images = sorted({n.image for o in _own_meshes(args) for s in o.material_slots if s.material is not None
                             for n in s.material.node_tree.nodes if getattr(n, "image", None) is not None},
                            key=lambda i: i.name)
            image = _first(images, "an image of its materials")
        _set_pixel(image)
    elif case == "weight":
        obj = _first([o for o in _own_meshes(args) if o.vertex_groups], "a mesh with weights")
        vertex = next(v for v in obj.data.vertices if v.groups)
        group = vertex.groups[0]
        group.weight = group.weight - 0.01 if group.weight >= 0.5 else group.weight + 0.01
    elif case == "bone":
        arm = _first([o for o in _collection_objects(EXPORT_COLLECTION) if o.type == "ARMATURE"], "an armature")
        bpy.context.view_layer.objects.active = arm
        with bpy.context.temp_override(active_object=arm, object=arm):
            bpy.ops.object.mode_set(mode="EDIT")
            bone = sorted(arm.data.edit_bones, key=lambda b: b.name)[-1]
            bone.head.z += 0.001
            bone.tail.z += 0.001
            bpy.ops.object.mode_set(mode="OBJECT")
    elif case == "key":
        action = _first(sorted(bpy.data.actions, key=lambda a: a.name), "a clip")
        channelbag = anim_utils.action_get_channelbag_for_slot(action, action.slots[0])
        curve = sorted(channelbag.fcurves, key=lambda c: (c.data_path, c.array_index))[0]
        curve.keyframe_points[0].co.y += 0.01
    elif case == "select":
        for obj in bpy.context.view_layer.objects:
            obj.select_set(True)
            if obj.type == "MESH":
                obj.data.vertices.foreach_set("select", [True] * len(obj.data.vertices))
    elif case == "active":
        bpy.context.view_layer.objects.active = sorted(bpy.context.view_layer.objects, key=lambda o: o.name)[-1]
    elif case == "hide":
        for obj in bpy.context.view_layer.objects:
            obj.hide_set(True)
    elif case == "frame":
        bpy.context.scene.frame_set(bpy.context.scene.frame_current + 7)
    elif case == "unused":
        bpy.data.meshes.new("ProofUnused").use_fake_user = True
        bpy.data.materials.new("ProofUnused").use_fake_user = True
