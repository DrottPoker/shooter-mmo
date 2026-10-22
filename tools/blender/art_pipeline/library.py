"""Animation libraries: handmade clips of one skeleton in a file of their own, so a rebuild of the
scripted characters never overwrites them. The Export collection holds the armature the clips are
keyed on, the rig of one character renamed after its skeleton. The Reference collection holds the
bodies that show the clips: that character's meshes bound to the armature, every preview character
on its own armature that copies the pose, side by side, and a ground grid. Sync builds the file, or
replaces its armature and bodies when the rig changed, and keeps every action."""

import os

import bpy

from . import bodies, skinned
from .scene import EXPORT_COLLECTION, ArtPipelineError, relative_image_paths

REFERENCE_COLLECTION = "Reference"
PREVIEW_PREFIX = "Preview_"
# The preview bodies stand this far apart along +X, the characters' right, so a body lying on the
# ground does not overlap its neighbour.
PREVIEW_SPACING = 2.0
FRAME_RATE = 30
GROUND_SIZE = 8.0


def _collection(name: str) -> bpy.types.Collection:
    collection = bpy.data.collections.get(name)
    if collection is None:
        collection = bpy.data.collections.new(name)
    if collection.name not in bpy.context.scene.collection.children:
        bpy.context.scene.collection.children.link(collection)
    return collection


def _descendants(obj: bpy.types.Object) -> list[bpy.types.Object]:
    result = [obj]
    for child in obj.children:
        result.extend(_descendants(child))
    return result


def _append(path: str, root: str) -> list[bpy.types.Object]:
    """The root object of another file and everything below it but its source body, appended without
    a collection."""
    with bpy.data.libraries.load(path, link=False) as (source, target):
        if root not in source.objects:
            raise ArtPipelineError(f'{path} has no object "{root}".')
        target.objects = list(source.objects)
    loaded = [o for o in target.objects if o is not None]
    top = next(o for o in loaded if o.name == root)
    if top.type != "ARMATURE":
        raise ArtPipelineError(f'"{root}" in {path} is not an armature.')
    # The parts show the body as the game does; the editable source body would cover them.
    keep = [o for o in _descendants(top) if not bodies.is_source_body(o)]
    for obj in loaded:
        if obj not in keep:
            bpy.data.objects.remove(obj, do_unlink=True)
    return keep


def _clear() -> None:
    """Removes every object and what only they used, and keeps the actions."""
    for action in bpy.data.actions:
        action.use_fake_user = True
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)
    bpy.data.orphans_purge(do_local_ids=True, do_linked_ids=True, do_recursive=True)


def _follow(preview: bpy.types.Object, arm: bpy.types.Object) -> None:
    """The preview armature copies the pose of the authoring armature bone by bone, in local space,
    and the hips' movement scaled to its own hip height, as Unity retargets a Humanoid clip."""
    scale = preview.data.bones["Hips"].head_local.z / arm.data.bones["Hips"].head_local.z
    for pb in preview.pose.bones:
        rotation = pb.constraints.new("COPY_ROTATION")
        rotation.target, rotation.subtarget = arm, pb.name
        rotation.owner_space = rotation.target_space = "LOCAL"
        rotation.mix_mode = "REPLACE"
        if pb.parent is not None:
            continue
        location = pb.constraints.new("TRANSFORM")
        location.target, location.subtarget = arm, pb.name
        location.owner_space = location.target_space = "LOCAL"
        location.map_from = location.map_to = "LOCATION"
        location.use_motion_extrapolate = True
        for axis in "xyz":
            setattr(location, f"from_min_{axis}", -1.0)
            setattr(location, f"from_max_{axis}", 1.0)
            setattr(location, f"to_min_{axis}", -scale)
            setattr(location, f"to_max_{axis}", scale)


def _ground(collection: bpy.types.Collection, center_x: float) -> None:
    mesh = bpy.data.meshes.new("Reference_Ground")
    half = GROUND_SIZE / 2.0
    mesh.from_pydata([(-half, -half, 0.0), (half, -half, 0.0), (half, half, 0.0), (-half, half, 0.0)], [],
                     [(0, 1, 2, 3)])
    material = bpy.data.materials.new("Reference_Ground")
    material.use_nodes = True
    nodes = material.node_tree.nodes
    bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
    coords = nodes.new("ShaderNodeTexCoord")
    checker = nodes.new("ShaderNodeTexChecker")
    material.node_tree.links.new(coords.outputs["Object"], checker.inputs["Vector"])
    # Quarter-metre checkers, so a sliding or sinking body shows at once.
    checker.inputs["Scale"].default_value = GROUND_SIZE * 2.0
    checker.inputs["Color1"].default_value = (0.32, 0.33, 0.35, 1.0)
    checker.inputs["Color2"].default_value = (0.26, 0.27, 0.29, 1.0)
    material.node_tree.links.new(checker.outputs["Color"], bsdf.inputs["Base Color"])
    bsdf.inputs["Roughness"].default_value = 0.9
    mesh.materials.append(material)
    ground = bpy.data.objects.new("Reference_Ground", mesh)
    ground.location.x = center_x
    ground.hide_select = True
    collection.objects.link(ground)


def _material_preview() -> None:
    for screen in bpy.data.screens:
        for area in screen.areas:
            for space in area.spaces:
                if space.type == "VIEW_3D":
                    space.shading.type = "MATERIAL"


def is_up_to_date(armature_name: str, rig_path: str, rig_root: str, previews: list[tuple[str, str]]) -> bool:
    """Whether the open library's armature is the current rig and every preview body is there."""
    arm = bpy.data.objects.get(armature_name)
    if arm is None or arm.type != "ARMATURE":
        return False
    if any(bpy.data.objects.get(PREVIEW_PREFIX + root) is None for _path, root in previews):
        return False
    return not skinned.rig_source_errors(arm, rig_path, rig_root)


def sync(library: str, armature_name: str, rig_path: str, rig_root: str, previews: list[tuple[str, str]],
         force: bool = False) -> bool:
    """Builds the library file, or rebuilds its armature and bodies from the current characters, and
    saves it. Returns False and leaves the file as it is when it is up to date."""
    if os.path.isfile(library):
        bpy.ops.wm.open_mainfile(filepath=library)
        if not force and is_up_to_date(armature_name, rig_path, rig_root, previews):
            return False
        # Opened again, so the rig that the check linked leaves no trace in the file.
        bpy.ops.wm.open_mainfile(filepath=library)
    else:
        bpy.ops.wm.read_factory_settings(use_empty=True)
    _clear()
    export = _collection(EXPORT_COLLECTION)
    reference = _collection(REFERENCE_COLLECTION)

    body = _append(rig_path, rig_root)
    arm = body[0]
    arm.name = armature_name
    arm.data.name = armature_name
    export.objects.link(arm)
    for obj in body[1:]:
        reference.objects.link(obj)
    arm.animation_data_create()
    arm.animation_data.action = None

    for index, (path, root) in enumerate(previews):
        preview_body = _append(path, root)
        preview = preview_body[0]
        preview.name = PREVIEW_PREFIX + root
        preview.location.x = PREVIEW_SPACING * (index + 1)
        for obj in preview_body:
            reference.objects.link(obj)
        _follow(preview, arm)

    _ground(reference, PREVIEW_SPACING * len(previews) / 2.0)
    relative_image_paths()
    scene = bpy.context.scene
    scene.render.fps, scene.render.fps_base = FRAME_RATE, 1.0
    ends = [int(a.frame_range[1]) for a in bpy.data.actions]
    scene.frame_start, scene.frame_end = 0, max(ends, default=60)
    _material_preview()
    bpy.ops.wm.save_as_mainfile(filepath=library, compress=True, relative_remap=False)
    return True
