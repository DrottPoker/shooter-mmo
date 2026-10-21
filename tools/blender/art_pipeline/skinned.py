"""Rules of the rig and animation stages of skinned assets: one armature with exactly the bones of
the asset's skeleton definition, every mesh bound to it with clean weights that agree across the
cuts between body parts, and animation clips that Unity can import as they were approved. An
animation library has the same armature and clip rules without meshes, and its armature must be
the rig of the character it was authored on."""

import hashlib
import math
import re

import bpy
from bpy_extras import anim_utils

from .scene import ArtPipelineError
from .skeleton import Skeleton

MAX_INFLUENCES = 4
WEIGHT_TOLERANCE = 1e-3
CUT_TOLERANCE = 1e-4
LOOP_TOLERANCE = 1e-4
FRAME_RATE = 30
CHANNEL_PATTERN = re.compile(r'^pose\.bones\["([^"]+)"\]\.(rotation_quaternion|location)$')


def _key(co) -> tuple[int, int, int]:
    return tuple(int(round(c * 1e5)) for c in co)


def _is_rest(pb: bpy.types.PoseBone) -> bool:
    q = pb.rotation_quaternion
    return (all(abs(v) <= 1e-6 for v in pb.location) and all(abs(v - 1.0) <= 1e-6 for v in pb.scale)
            and abs(abs(q.w) - 1.0) <= 1e-6 and all(abs(v) <= 1e-6 for v in pb.rotation_euler)
            and abs(pb.rotation_axis_angle[0]) <= 1e-6)


def armature_of(objects: list[bpy.types.Object]) -> bpy.types.Object | None:
    armatures = [o for o in objects if o.type == "ARMATURE"]
    return armatures[0] if len(armatures) == 1 else None


def _rounded(value: float) -> str:
    # Rounded to a hundredth of a millimetre, and never "-0.00000".
    return f"{round(value, 5) or 0.0:.5f}"


def rig_fingerprint(arm: bpy.types.Object) -> str:
    """SHA-256 of the armature's rest pose: every bone's name, parent, deform flag, length, and
    rest matrix in armature space. Clips authored on one rig fit another rig with the same
    fingerprint exactly."""
    lines = []
    for bone in sorted(arm.data.bones, key=lambda b: b.name):
        values = [v for row in bone.matrix_local for v in row] + [bone.length]
        parent = bone.parent.name if bone.parent else ""
        lines.append(f"{bone.name}|{parent}|{int(bone.use_deform)}|" + ",".join(_rounded(v) for v in values))
    return hashlib.sha256("\n".join(lines).encode("utf-8")).hexdigest()


def rig_errors(objects: list[bpy.types.Object], roots: list[bpy.types.Object], skeleton: Skeleton,
               meshes_allowed: bool = True) -> list[str]:
    errors: list[str] = []
    armatures = [o for o in objects if o.type == "ARMATURE"]
    if len(armatures) != 1:
        return [f"A rigged asset exports exactly one armature; the Export collection has {len(armatures)}."]
    arm = armatures[0]
    if [r.name for r in roots] != [arm.name]:
        errors.append(f'The armature "{arm.name}" must be the only root object; the meshes are its children.')
    errors += _bone_errors(arm, skeleton)
    errors += _rest_errors(arm)
    if not meshes_allowed:
        others = sorted(o.name for o in objects if o != arm)
        if others:
            errors.append(f"An animation library exports only its armature; the Export collection also holds {others}. "
                          "Keep the bodies that show the clips in the Reference collection.")
        return errors
    deform = set(skeleton.deform_bones)
    meshes = [o for o in objects if o.type == "MESH"]
    weights = {}
    for obj in meshes:
        problems = _binding_errors(obj, arm)
        if not problems:
            problems, weights[obj.name] = _weight_errors(obj, deform)
        errors += problems
    errors += _cut_errors(meshes, weights)
    return errors


def linked_armature(path: str, name: str) -> bpy.types.Object:
    """The armature object of another .blend file, linked read-only into this session."""
    with bpy.data.libraries.load(path, link=True) as (source, target):
        if name not in source.objects:
            raise ArtPipelineError(f'{path} has no object "{name}".')
        target.objects = [name]
    obj = target.objects[0]
    if obj is None or obj.type != "ARMATURE":
        raise ArtPipelineError(f'"{name}" in {path} is not an armature.')
    return obj


def rig_source_errors(arm: bpy.types.Object, rig_path: str, rig_root: str) -> list[str]:
    """An animation library's armature must be the rig of the character its clips are authored on,
    so the clips look in the game as they did in review."""
    if rig_fingerprint(linked_armature(rig_path, rig_root)) != rig_fingerprint(arm):
        return [f'The armature "{arm.name}" is not the current rig "{rig_root}" of {rig_path}; update it with '
                "tools/Sync-AnimationLibrary.ps1 and review the clips again."]
    return []


def _bone_errors(arm: bpy.types.Object, skeleton: Skeleton) -> list[str]:
    errors = []
    bones = arm.data.bones
    expected = {b.name for b in skeleton.bones}
    actual = {b.name for b in bones}
    if expected - actual:
        errors.append(f'The armature "{arm.name}" lacks the {skeleton.name} bones {sorted(expected - actual)}.')
    if actual - expected:
        errors.append(f'The armature "{arm.name}" has bones outside the {skeleton.name} skeleton: '
                      f"{sorted(actual - expected)}.")
    for bone in bones:
        if bone.name not in expected:
            continue
        spec = skeleton.bone(bone.name)
        parent = bone.parent.name if bone.parent else None
        if parent != spec.parent:
            errors.append(f'Bone "{bone.name}" must be a child of "{spec.parent}", not "{parent}".')
        if bone.use_deform != spec.deform:
            errors.append(f'Bone "{bone.name}" must {"" if spec.deform else "not "}deform the skin.')
    return errors


def _rest_errors(arm: bpy.types.Object) -> list[str]:
    errors = []
    if arm.data.pose_position != "POSE":
        errors.append(f'The armature "{arm.name}" must show its pose (Pose Position), not the rest position.')
    if arm.constraints or any(pb.constraints for pb in arm.pose.bones):
        errors.append(f'The armature "{arm.name}" and its bones must have no constraints.')
    posed = [pb.name for pb in arm.pose.bones if not _is_rest(pb)]
    if posed:
        errors.append(f'The armature "{arm.name}" must be in its rest pose; these bones are posed: {posed[:8]}.')
    data = arm.animation_data
    if data is not None and (data.action is not None or len(data.nla_tracks)):
        errors.append(f'The armature "{arm.name}" must have no active action or NLA tracks; every clip is an '
                      "action of its own.")
    return errors


def _binding_errors(obj: bpy.types.Object, arm: bpy.types.Object) -> list[str]:
    errors = []
    if obj.parent != arm or obj.parent_type != "OBJECT":
        errors.append(f'Mesh "{obj.name}" must be a child of the armature "{arm.name}".')
    modifiers = list(obj.modifiers)
    md = modifiers[0] if len(modifiers) == 1 else None
    if md is None or md.type != "ARMATURE" or md.object != arm:
        errors.append(f'Mesh "{obj.name}" must have one modifier, an Armature modifier with the object "{arm.name}".')
    elif not md.use_vertex_groups or md.use_bone_envelopes or md.use_deform_preserve_volume or md.use_multi_modifier:
        errors.append(f'The Armature modifier of "{obj.name}" must deform with vertex groups only, without envelopes, '
                      "Preserve Volume, or Multi Modifier, as Unity skins.")
    if obj.data.shape_keys is not None:
        errors.append(f'Mesh "{obj.name}" must have no shape keys.')
    return errors


def _weight_errors(obj: bpy.types.Object, deform: set[str]) -> tuple[list[str], list[dict[str, float]]]:
    errors = []
    names = {g.index: g.name for g in obj.vertex_groups}
    strangers = sorted(n for n in names.values() if n not in deform)
    if strangers:
        errors.append(f'Mesh "{obj.name}" has vertex groups that are no deforming bone: {strangers[:8]}.')
    unweighted = crowded = unnormalized = 0
    weights = []
    for v in obj.data.vertices:
        w = {names[g.group]: g.weight for g in v.groups if g.weight > 0.0}
        weights.append(w)
        if not w:
            unweighted += 1
        elif len(w) > MAX_INFLUENCES:
            crowded += 1
        elif abs(sum(w.values()) - 1.0) > WEIGHT_TOLERANCE:
            unnormalized += 1
    if unweighted:
        errors.append(f'Mesh "{obj.name}" has {unweighted} vertices without weights.')
    if crowded:
        errors.append(f'Mesh "{obj.name}" has {crowded} vertices with more than {MAX_INFLUENCES} bones.')
    if unnormalized:
        errors.append(f'Mesh "{obj.name}" has {unnormalized} vertices whose weights do not add up to 1.')
    return errors, weights


def _cut_errors(meshes: list[bpy.types.Object], weights: dict[str, list[dict[str, float]]]) -> list[str]:
    """Vertices of different meshes at the same place must move together, or the cut opens."""
    seen: dict[tuple[int, int, int], tuple[str, dict[str, float]]] = {}
    opened: dict[tuple[str, str], int] = {}
    for obj in meshes:
        if obj.name not in weights:
            continue
        m = obj.matrix_world
        for v, w in zip(obj.data.vertices, weights[obj.name]):
            key = _key(m @ v.co)
            other = seen.setdefault(key, (obj.name, w))
            if other[0] == obj.name:
                continue
            bones = set(w) | set(other[1])
            if any(abs(w.get(b, 0.0) - other[1].get(b, 0.0)) > CUT_TOLERANCE for b in bones):
                pair = tuple(sorted((obj.name, other[0])))
                opened[pair] = opened.get(pair, 0) + 1
    return [f'The cut between "{a}" and "{b}" opens: {n} vertices at the same place have different weights.'
            for (a, b), n in sorted(opened.items())]


def animation_errors(arm: bpy.types.Object, clips: list[str], skeleton: Skeleton) -> list[str]:
    errors = []
    scene = bpy.context.scene
    if scene.render.fps != FRAME_RATE or abs(scene.render.fps_base - 1.0) > 1e-6:
        errors.append(f"The scene must run at {FRAME_RATE} frames per second.")
    actions = sorted(bpy.data.actions, key=lambda a: a.name)
    names = [a.name for a in actions]
    if sorted(names) != sorted(clips):
        errors.append(f"The actions are {names}; the manifest's clips are {sorted(clips)}.")
    bones = {b.name for b in skeleton.bones}
    for action in actions:
        errors += _action_errors(action, bones, skeleton.root.name)
    return errors


def _action_errors(action: bpy.types.Action, bones: set[str], root_bone: str) -> list[str]:
    errors = []
    where = f'Action "{action.name}"'
    if len(action.slots) != 1 or action.slots[0].target_id_type != "OBJECT":
        return [f"{where} must have one slot, for the armature object."]
    start, end = action.frame_range
    if (not action.use_frame_range or start != 0.0 or end <= 0.0 or end != math.floor(end)):
        errors.append(f"{where} must have a manual frame range from frame 0 to a whole end frame.")
    loop = action.get("loop")
    if not isinstance(loop, bool):
        errors.append(f'{where} needs the custom property "loop", true or false.')
    channelbag = anim_utils.action_get_channelbag_for_slot(action, action.slots[0])
    curves = list(channelbag.fcurves) if channelbag is not None else []
    if not curves:
        return errors + [f"{where} animates nothing."]
    rotations: dict[str, set[int]] = {}
    unlooped = []
    for fc in curves:
        match = CHANNEL_PATTERN.match(fc.data_path)
        if match is None or match.group(1) not in bones:
            errors.append(f'{where} animates "{fc.data_path}"; clips only turn the skeleton\'s bones '
                          "(rotation_quaternion) and move its root bone (location).")
            continue
        bone, channel = match.groups()
        if channel == "location" and bone != root_bone:
            errors.append(f'{where} moves the bone "{bone}"; only "{root_bone}" may move.')
        if channel == "rotation_quaternion":
            rotations.setdefault(bone, set()).add(fc.array_index)
        frames = [k.co.x for k in fc.keyframe_points]
        if frames and (min(frames) < start - 1e-6 or max(frames) > end + 1e-6):
            errors.append(f'{where} has keys of "{fc.data_path}" outside its frame range.')
        if loop is True and abs(fc.evaluate(start) - fc.evaluate(end)) > LOOP_TOLERANCE:
            unlooped.append(f"{fc.data_path}[{fc.array_index}]")
    if unlooped:
        errors.append(f"{where} loops, but its first and last frames differ in {len(unlooped)} channels, such as "
                      f"{unlooped[:4]}.")
    incomplete = sorted(b for b, channels in rotations.items() if channels != {0, 1, 2, 3})
    if incomplete:
        errors.append(f"{where} must key all four quaternion channels of {incomplete[:8]}.")
    return errors
