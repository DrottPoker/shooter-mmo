"""Checks the Export collection against the art pipeline rules before an approval or export."""

import re

import bpy

from .scene import EXPORT_COLLECTION, export_collection, export_objects, root_objects, triangle_count

NAME_PATTERN = re.compile(r"^[A-Za-z][A-Za-z0-9_]*$")
MATERIAL_PATTERN = re.compile(r"^M_[A-Za-z0-9_]+$")
# What the model stage of each kind exports. A skinned asset gains its armature with the rig stage.
OBJECT_TYPES = {"static": {"MESH", "EMPTY"}, "skinned": {"MESH", "EMPTY"}}
# Kinds whose materials may take the Base Color from one packed image, such as a painted body.
TEXTURED_KINDS = {"skinned"}
TOLERANCE = 1e-4


def principled_inputs(material: bpy.types.Material) -> dict[str, bpy.types.NodeSocket] | None:
    """The flat Base Color, Metallic, and Roughness of the material's only Principled BSDF."""
    if material.node_tree is None:
        return None
    nodes = [n for n in material.node_tree.nodes if n.type == "BSDF_PRINCIPLED"]
    if len(nodes) != 1:
        return None
    inputs = {name: nodes[0].inputs[name] for name in ("Base Color", "Metallic", "Roughness")}
    return None if any(socket.is_linked for socket in inputs.values()) else inputs


def surface_problem(material: bpy.types.Material) -> str | None:
    """None when the material's only Principled BSDF feeds the Surface of its only Material
    Output, so the approved look is what renders."""
    if material.node_tree is None:
        return "has no nodes"
    nodes = material.node_tree.nodes
    bsdfs = [n for n in nodes if n.type == "BSDF_PRINCIPLED"]
    outputs = [n for n in nodes if n.type == "OUTPUT_MATERIAL"]
    if len(bsdfs) != 1 or len(outputs) != 1:
        return "must hold one Principled BSDF and one Material Output"
    links = outputs[0].inputs["Surface"].links
    if len(links) != 1 or links[0].from_node != bsdfs[0] or links[0].from_socket.name != "BSDF":
        return "must connect its Principled BSDF straight to the Surface of the Material Output"
    return None


def textured_principled_problem(material: bpy.types.Material) -> str | None:
    """None when the material is one Principled BSDF with flat Metallic and Roughness and a
    Base Color that is flat or comes straight from one Image Texture packed into the file."""
    problem = surface_problem(material)
    if problem is not None:
        return problem
    nodes = material.node_tree.nodes
    bsdfs = [n for n in nodes if n.type == "BSDF_PRINCIPLED"]
    images = [n for n in nodes if n.type == "TEX_IMAGE"]
    others = [n for n in nodes if n.type not in ("BSDF_PRINCIPLED", "TEX_IMAGE", "OUTPUT_MATERIAL")]
    if len(images) > 1 or others:
        return "must hold one Principled BSDF, at most one Image Texture, and the output, nothing else"
    bsdf = bsdfs[0]
    if bsdf.inputs["Metallic"].is_linked or bsdf.inputs["Roughness"].is_linked:
        return "must have a flat Metallic and Roughness"
    base = bsdf.inputs["Base Color"]
    if images:
        image_node = images[0]
        links = base.links
        if len(links) != 1 or links[0].from_node != image_node or links[0].from_socket.name != "Color":
            return "must feed its Image Texture's Color straight into the Base Color"
        if any(socket.is_linked for socket in image_node.inputs):
            return "must read its Image Texture with the default UV map"
        image = image_node.image
        if image is None or image.packed_file is None:
            return "must use an image that is packed into the .blend file (File > External Data > Pack Resources)"
    elif base.is_linked:
        return "must have a flat Base Color or one Image Texture feeding it"
    return None


def _is_identity_scale(obj: bpy.types.Object) -> bool:
    return all(abs(s - 1.0) <= TOLERANCE for s in obj.scale)


def _is_at_origin(obj: bpy.types.Object) -> bool:
    return (all(abs(v) <= TOLERANCE for v in obj.location)
            and all(abs(v) <= TOLERANCE for v in obj.rotation_euler)
            and obj.rotation_mode == "XYZ")


def _layer_collection(layer: bpy.types.LayerCollection, name: str) -> bpy.types.LayerCollection | None:
    if layer.collection.name == name:
        return layer
    for child in layer.children:
        found = _layer_collection(child, name)
        if found is not None:
            return found
    return None


def validate(kind: str, roots: list[str], triangle_budget: int) -> tuple[list[str], int]:
    """Return the problems found and the total exported triangle count."""
    errors: list[str] = []
    export_collection()
    layer = _layer_collection(bpy.context.view_layer.layer_collection, EXPORT_COLLECTION)
    if layer is None or layer.exclude:
        errors.append(f'The "{EXPORT_COLLECTION}" collection must be included in the view layer.')

    allowed_types = OBJECT_TYPES.get(kind)
    if allowed_types is None:
        return [f'Asset kind "{kind}" is not supported yet.'], 0

    objects = export_objects()
    for obj in objects:
        if not NAME_PATTERN.match(obj.name):
            errors.append(f'Object "{obj.name}" must use only letters, digits, and underscores.')
        if obj.type not in allowed_types:
            errors.append(f'Object "{obj.name}" is a {obj.type}; a {kind} asset exports only '
                          f'{", ".join(sorted(allowed_types))}.')
        if not _is_identity_scale(obj):
            errors.append(f'Object "{obj.name}" has scale {tuple(round(s, 4) for s in obj.scale)}; '
                          "apply the scale (Ctrl+A > Scale).")
        if obj.type == "MESH":
            errors.extend(_mesh_errors(obj, kind))

    actual_roots = [o.name for o in root_objects()]
    if sorted(actual_roots) != sorted(roots):
        errors.append(f"The root objects are {sorted(actual_roots)}; the manifest expects {sorted(roots)}.")
    for root in root_objects():
        if not _is_at_origin(root):
            errors.append(f'Root object "{root.name}" must sit at the origin without rotation, '
                          "because its origin is the actor position in the game.")

    triangles = sum(triangle_count(o) for o in objects)
    if triangles > triangle_budget:
        errors.append(f"The asset has {triangles} triangles; the budget is {triangle_budget}.")
    return errors, triangles


def _mesh_errors(obj: bpy.types.Object, kind: str) -> list[str]:
    errors: list[str] = []
    mesh = obj.data
    if not mesh.polygons:
        return [f'Mesh "{obj.name}" has no faces.']
    if not obj.material_slots:
        errors.append(f'Mesh "{obj.name}" has no material.')
    used = {p.material_index for p in mesh.polygons}
    for index, slot in enumerate(obj.material_slots):
        if slot.material is None:
            errors.append(f'Mesh "{obj.name}" has an empty material slot {index}.')
        elif not MATERIAL_PATTERN.match(slot.material.name):
            errors.append(f'Material "{slot.material.name}" on "{obj.name}" must be named like the Unity '
                          "material it maps to, for example M_Resource_IronVeinRock.")
        elif kind in TEXTURED_KINDS:
            problem = textured_principled_problem(slot.material)
            if problem is not None:
                errors.append(f'Material "{slot.material.name}" {problem}.')
        elif principled_inputs(slot.material) is None:
            errors.append(f'Material "{slot.material.name}" must be one Principled BSDF with a flat Base Color, '
                          "Metallic, and Roughness, without textures or other nodes feeding them.")
        elif (problem := surface_problem(slot.material)) is not None:
            errors.append(f'Material "{slot.material.name}" {problem}.')
        if index not in used:
            errors.append(f'Mesh "{obj.name}" has an unused material slot {index}; remove it.')
    return errors
