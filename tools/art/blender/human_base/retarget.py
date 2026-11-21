"""Clips of another skeleton moved onto the Humanoid rig of an animation library, as starting points
that a person then edits by hand, like the gait model's clips. The source, such as a clip library in a
.glb file, is imported into the library file, and every frame of its clips turns each mapped Humanoid
bone from its rest as the source bone turns from its rest, in world space. A map file names the source
bone of each Humanoid bone and how far the source is turned about Z to face +Y as the Humanoid does.

Before that, the limbs, feet, and fingers are aligned along their length to the source's rest pose,
and each hand takes the source hand's whole frame, so a rest pose that differs, such as an A pose
against a T pose, is no part of the motion. The hips move as the source's hips move, scaled by the
ratio of the leg lengths, and the legs are solved again with the gait model's two-bone IK so each
ankle is where the source's is from the place its hips rest at, scaled, wherever the source stands in
its file, the knee toward the retargeted leg's front. A hand that holds
something may take the library's Grip clip on its fingers instead. The result is one clip that does
not loop, keyed on every frame as the gait model keys, and nothing replaces a clip the library has.

A refit moves the clips a library has the same way, from the rig they were keyed on to the library's
new rig of the same skeleton, when the rig's joints moved: a clip looks as it did, although the rig
rests in another pose. It replaces every clip with the same clip on the new rig."""

import json
import math
import os
import re

import bpy
from bpy_extras import anim_utils
from mathutils import Matrix, Quaternion, Vector

from . import animate

# The Humanoid bones every map names: the hips and both legs, which the hips' movement and the legs'
# IK need.
REQUIRED = ("Hips", "UpperLeg_L", "UpperLeg_R", "LowerLeg_L", "LowerLeg_R", "Foot_L", "Foot_R")
SIDES = ("L", "R")
# Bones aligned along their length to the source's rest pose: each points from its head to the head
# of this bone, on the same side, when the map names both.
LIMBS = {"UpperArm": "LowerArm", "LowerArm": "Hand", "UpperLeg": "LowerLeg", "LowerLeg": "Foot", "Foot": "Toes"}
# Each finger segment is aligned toward the next segment, and the last one toward its own tail.
FINGERS = ("Thumb", "Index", "Middle", "Ring", "Little")
SEGMENTS = ("Proximal", "Intermediate", "Distal")
# A hand takes the source hand's whole frame, from the wrist and the bases of these fingers, in this
# order: the middle finger gives the hand's length, the index and the little finger its breadth.
HAND_FRAME = ("MiddleProximal", "IndexProximal", "LittleProximal")
# The library clip whose finger pose a hand that holds something takes.
GRIP_CLIP = "Grip"
GRIPS = {"-": "", "L": "L", "R": "R", "LR": "LR"}
SOURCE_TYPES = (".glb", ".gltf", ".fbx", ".bvh")
# A refit aligns the limbs of LIMBS but the feet: both rigs rest with their soles on the ground, so
# a foot keeps its turn from the ground and does not take the direction of the old rig's bone.
REFIT_LIMBS = {bone: toward for bone, toward in LIMBS.items() if bone != "Foot"}
# What a clip keys of a bone: its turn, and of the hips their place.
ROTATION, LOCATION = "rotation_quaternion", "location"
# Two rigs rest alike when no bone's rest matrix differs by more than this.
SAME_REST = 1e-6


def map_errors(data, target_bones, source_bones, skeleton="Humanoid") -> list[str]:
    """What is wrong with a map, read from its JSON: it must turn the source by a number of degrees
    in "facing" and name in "bones" the source bone of each Humanoid bone, among them every bone of
    REQUIRED, and every bone it names must be a bone of its skeleton."""
    if not isinstance(data, dict) or not isinstance(data.get("bones"), dict):
        return ['The map needs "bones", from each Humanoid bone to the source bone it follows.']
    errors = []
    facing = data.get("facing")
    if isinstance(facing, bool) or not isinstance(facing, (int, float)) or not math.isfinite(facing):
        errors.append('The map needs "facing", the degrees the source turns about Z to face +Y.')
    bones = data["bones"]
    for target, source in bones.items():
        if target not in target_bones:
            errors.append(f'The map names "{target}", which is no bone of {skeleton}.')
        if not isinstance(source, str) or source not in source_bones:
            errors.append(f'The map names "{source}" for {target}, which is no bone of the source skeleton.')
    missing = [bone for bone in REQUIRED if bone not in bones]
    if missing:
        errors.append(f"The map must name the source bone of {', '.join(missing)}.")
    return errors


def aligned_pairs(bones, limbs=LIMBS) -> list[tuple[str, str | None]]:
    """The Humanoid bones aligned along their length, each with the bone whose head it points to, or
    None for its own tail, for the mapped bones of a map: the limbs of limbs, and the fingers."""
    pairs = []
    for side in SIDES:
        for bone, toward in limbs.items():
            if f"{bone}_{side}" in bones and f"{toward}_{side}" in bones:
                pairs.append((f"{bone}_{side}", f"{toward}_{side}"))
        for finger in FINGERS:
            names = [f"{finger}{segment}_{side}" for segment in SEGMENTS]
            for index, name in enumerate(names):
                if name not in bones:
                    continue
                if index == len(names) - 1:
                    pairs.append((name, None))
                elif names[index + 1] in bones:
                    pairs.append((name, names[index + 1]))
    return pairs


def hand_sides(bones) -> list[str]:
    """The sides whose hand takes the source hand's frame: the map names the hand and the finger
    bases of HAND_FRAME."""
    return [side for side in SIDES if all(f"{bone}_{side}" in bones for bone in ("Hand",) + HAND_FRAME)]


def keyed(paths) -> tuple[set[str], bool, list[str]]:
    """What a clip keys, from the data paths of its curves: the bones it turns, whether it moves
    the hips, and the curves a refit cannot move, which are all but a bone's quaternion and the
    hips' place."""
    bones, hips, other = set(), False, set()
    for path in paths:
        match = re.fullmatch(r'pose\.bones\["([^"]+)"\]\.(\w+)', path)
        if match and match.group(2) == ROTATION:
            bones.add(match.group(1))
        elif match and match.group(2) == LOCATION and match.group(1) == "Hips":
            hips = True
        else:
            other.add(path)
    return bones, hips, sorted(other)


def distinct_frames(start: int, end: int, loop: bool) -> int:
    """The frames a clip plays before it repeats or ends: a looping clip's last frame is its first
    again."""
    return end - start if loop else end - start + 1


def _hand_frame(wrist, middle, index, little):
    x = (middle - wrist).normalized()
    n = (index - wrist).cross(little - wrist).normalized()
    y = n.cross(x).normalized()
    return Matrix((x, y, x.cross(y))).transposed()


def _play(obj, action):
    """Plays the action on the armature from its rest pose, so a bone the clip does not key is at
    rest and not where the clip sampled before it left it."""
    for bone in obj.pose.bones:
        bone.location = (0.0, 0.0, 0.0)
        bone.rotation_quaternion = Quaternion()
        bone.rotation_euler = (0.0, 0.0, 0.0)
        bone.scale = (1.0, 1.0, 1.0)
    obj.animation_data_create()
    obj.animation_data.action = action
    if action is not None and action.slots:
        obj.animation_data.action_slot = action.slots[0]


def _import(path):
    """Imports the source file into the open file; returns the new objects and actions."""
    objects, actions = set(bpy.data.objects), set(bpy.data.actions)
    extension = os.path.splitext(path)[1].lower()
    if extension in (".glb", ".gltf"):
        bpy.ops.import_scene.gltf(filepath=path)
    elif extension == ".fbx":
        bpy.ops.import_scene.fbx(filepath=path)
    else:
        # The scene runs at the clip's rate, so the BVH frames are scaled to it.
        bpy.ops.import_anim.bvh(filepath=path, use_fps_scale=True)
    return [o for o in bpy.data.objects if o not in objects], [a for a in bpy.data.actions if a not in actions]


def _source_action(actions, clip):
    """The imported action of a clip, also when a clip of the library had its name first and Blender
    added a number to it."""
    exact = [a for a in actions if a.name == clip]
    numbered = [a for a in actions if re.fullmatch(re.escape(clip) + r"\.\d{3}", a.name)]
    return (exact + numbered + [None])[0]


def _sample(scene, source, actions, mapped):
    """The world matrix of every mapped source bone on every frame of the clips played one after
    another, the first frame of a later clip left out."""
    frames = []
    for index, action in enumerate(actions):
        _play(source, action)
        start, end = (int(round(v)) for v in action.frame_range)
        for frame in range(start if index == 0 else start + 1, end + 1):
            scene.frame_set(frame)
            bpy.context.view_layer.update()
            frames.append({name: source.matrix_world @ source.pose.bones[name].matrix for name in mapped})
    _play(source, None)
    return frames


def _grip(hands):
    """The finger rotations of the library's Grip clip on the given hands, at its first frame."""
    values = {}
    action = bpy.data.actions[GRIP_CLIP]
    channelbag = anim_utils.action_get_channelbag_for_slot(action, action.slots[0])
    for fc in channelbag.fcurves:
        bone = fc.data_path.split('"')[1]
        if fc.data_path.endswith("rotation_quaternion") and bone.startswith(FINGERS) and bone[-1] in hands:
            values.setdefault(bone, [1.0, 0.0, 0.0, 0.0])[fc.array_index] = fc.evaluate(0.0)
    return {bone: Quaternion(q).normalized() for bone, q in values.items()}


def _ordered(bones):
    """The bones with every parent before its children."""
    ordered = []

    def walk(bone):
        ordered.append(bone)
        for child in bone.children:
            walk(child)

    for bone in bones:
        if bone.parent is None:
            walk(bone)
    return ordered


class Retarget:
    """The fixed parts of a retarget from a source armature to the Humanoid armature, from both
    rest poses and the map, and the pose of one frame."""

    def __init__(self, target, source, bones, facing, skip, limbs=LIMBS):
        self.target, self.bones, self.skip = target, bones, skip
        self.rig = animate.Rig(target)
        self.turn = Matrix.Rotation(math.radians(facing), 3, "Z")
        self.turn_back = self.turn.inverted()
        self.rest_t = {b.name: b.matrix_local.to_3x3() for b in target.data.bones}
        head_t = {b.name: b.head_local.copy() for b in target.data.bones}
        tail_t = {b.name: b.tail_local.copy() for b in target.data.bones}
        world = source.matrix_world
        self.rest_s = {b.name: (world @ b.matrix_local).to_3x3().normalized() for b in source.data.bones}
        self.head_s = {b.name: world @ b.head_local for b in source.data.bones}
        tail_s = {b.name: world @ b.tail_local for b in source.data.bones}
        self.align = {}
        for bone, toward in aligned_pairs(bones, limbs):
            d_t = (head_t[toward] if toward else tail_t[bone]) - head_t[bone]
            end_s = self.head_s[bones[toward]] if toward else tail_s[bones[bone]]
            d_s = self.turn @ (end_s - self.head_s[bones[bone]])
            self.align[bone] = d_t.rotation_difference(d_s).to_matrix()
        for side in hand_sides(bones):
            names = [f"{bone}_{side}" for bone in ("Hand",) + HAND_FRAME]
            f_t = _hand_frame(*(head_t[n] for n in names))
            f_s = _hand_frame(*(self.turn @ self.head_s[bones[n]] for n in names))
            self.align[f"Hand_{side}"] = f_s @ f_t.inverted()
        # The source's leg length against the Humanoid's, from the hip joint to the ankle.
        hip, ankle = bones["UpperLeg_R"], bones["Foot_R"]
        self.scale = (head_t["UpperLeg_R"].z - head_t["Foot_R"].z) / (self.head_s[hip].z - self.head_s[ankle].z)
        self.ankle_lift = head_t["Foot_R"].z - self.scale * self.head_s[ankle].z
        self.hips_t = head_t["Hips"].copy()
        self.order = [b for b in _ordered(target.data.bones) if b.name != "Hips" and b.name not in skip]

    def frame(self, world, grip):
        """Local rotations of the Humanoid bones and the hips' location on one frame of the source,
        the world matrices of its mapped bones."""
        want = {}
        for name_t, name_s in self.bones.items():
            if name_t in self.skip:
                continue
            delta = self.turn @ world[name_s].to_3x3().normalized() @ self.rest_s[name_s].inverted() @ self.turn_back
            want[name_t] = delta @ self.align.get(name_t, Matrix.Identity(3)) @ self.rest_t[name_t]
        hips_s = self.bones["Hips"]
        offset = self.scale * (self.turn @ (world[hips_s].translation - self.head_s[hips_s]))
        local, pose = {}, {}
        local["Hips"], hips_loc, pose["Hips"] = self.rig.hips(want["Hips"] @ self.rest_t["Hips"].inverted(), offset)
        for bone in self.order:
            name = bone.name
            parent = pose[bone.parent.name]
            side = name[-1]
            if name.startswith("UpperLeg_"):
                hip = self.rig.child_base(name, parent).translation
                # From the place the hips rest at, as the hips' own movement is, so a source that
                # stands away from its file's origin gives the same legs.
                foot = world[self.bones[f"Foot_{side}"]].translation
                reach = self.scale * (self.turn @ (foot - self.head_s[hips_s]))
                ankle = Vector((self.hips_t.x + reach.x, self.hips_t.y + reach.y, self.scale * foot.z + self.ankle_lift))
                knee = want[name].col[2] + want[f"LowerLeg_{side}"].col[2]
                want[name], want[f"LowerLeg_{side}"] = animate._leg_ik(self.rig, hip, ankle, knee)
            if name in grip:
                local[name] = grip[name]
            elif name in want:
                local[name] = self.rig.local(name, parent, want[name])
            else:
                local[name] = Quaternion()
            pose[name] = self.rig.pose(name, parent, local[name])
        return local, hips_loc


def _remove(objects, actions, collections):
    """Removes what the import added, and what only that used."""
    for obj in objects:
        bpy.data.objects.remove(obj, do_unlink=True)
    for action in actions:
        bpy.data.actions.remove(action)
    for collection in collections:
        bpy.data.collections.remove(collection)
    bpy.data.orphans_purge(do_local_ids=True, do_linked_ids=True, do_recursive=True)


def import_clip(path, label, source_path, map_path, clips, name, grip, skeleton_path):
    """Adds the clip name to the animation library at path, on its armature label, from the clips of
    the source file joined by "+", played one after another, with the map file's bones, and saves
    the library. grip is "-", or the hands, L, R, or LR, whose fingers take the Grip clip. Returns
    the report lines, the broken rules, and the new clip's motion numbers."""
    from art_pipeline import clip_video
    from art_pipeline import skeleton as skeleton_definition

    clips = [clip for clip in clips.split("+") if clip]
    if grip not in GRIPS:
        return [], [f"The grip is one of {', '.join(GRIPS)}, not {grip}."], None
    if os.path.splitext(source_path)[1].lower() not in SOURCE_TYPES:
        return [], [f"The source is a {', '.join(SOURCE_TYPES)} file, not {os.path.basename(source_path)}."], None
    try:
        with open(map_path, encoding="utf-8") as handle:
            data = json.load(handle)
    except (OSError, ValueError) as error:
        return [], [f"Cannot read the map {map_path}: {error}"], None
    skeleton = skeleton_definition.load(skeleton_path)
    skip = frozenset(skeleton.unkeyed_bones)
    bpy.ops.wm.open_mainfile(filepath=path)
    if name in bpy.data.actions:
        return [], [f"{name}: the library has the clip already, and a clip is never replaced."], None
    target = bpy.data.objects[label]
    errors = [] if GRIPS[grip] == "" or GRIP_CLIP in bpy.data.actions else [f"The library has no {GRIP_CLIP} clip for the grip."]
    scene = bpy.context.scene
    scene.render.fps, scene.render.fps_base = animate.FPS, 1.0
    collections = set(bpy.data.collections)
    objects, actions = _import(os.path.abspath(source_path))
    added = [c for c in bpy.data.collections if c not in collections]
    source = next((o for o in objects if o.type == "ARMATURE"), None)
    if source is None:
        return [], errors + [f"{os.path.basename(source_path)} has no armature."], None
    errors += map_errors(data, {b.name for b in target.data.bones}, {b.name for b in source.data.bones}, skeleton.name)
    chosen = [_source_action(actions, clip) for clip in clips]
    missing = [clip for clip, action in zip(clips, chosen) if action is None]
    if missing:
        errors.append(f"{os.path.basename(source_path)} has no clip {', '.join(missing)}; it has "
                      f"{', '.join(sorted(a.name for a in actions))}.")
    if not clips:
        errors.append("Name at least one clip of the source.")
    if errors:
        return [], errors, None
    if source.animation_data is not None:
        for track in list(source.animation_data.nla_tracks):
            source.animation_data.nla_tracks.remove(track)
        source.animation_data.action = None

    bones = dict(data["bones"])
    retarget = Retarget(target, source, bones, float(data["facing"]), skip)
    frames = _sample(scene, source, chosen, set(bones.values()))
    held = _grip(GRIPS[grip]) if GRIPS[grip] else {}
    target.animation_data_create()
    active = target.animation_data.action
    action = animate.bake(target, name, len(frames), lambda f: retarget.frame(frames[f], held), skip, loop=False)
    motion = clip_video.measure(target, action, skip)
    _remove(objects, actions, added)
    # A source clip of the same name had the name first, so Blender numbered the new action.
    action.name = name
    if action.name != name:
        return [], [f"{name}: Blender named the new clip {action.name}; choose another name."], None
    motion["clip"] = name
    # The FBX export bakes every action as a take only for an object that has animation data.
    target.animation_data.action = active
    scene.frame_start, scene.frame_end = 0, max(int(a.frame_range[1]) for a in bpy.data.actions)
    bpy.ops.wm.save_as_mainfile(filepath=path, compress=True, relative_remap=False)
    # Frames counted as the export records them: the last frame's number.
    lines = [f"{name}: {len(frames) - 1} frames at {animate.FPS} fps from {' + '.join(clips)} of {os.path.basename(source_path)}, "
             f"{len(bones)} bones mapped, {len(retarget.align)} aligned to the source's rest pose"
             + (f", the fingers of {' and '.join(GRIPS[grip])} in {GRIP_CLIP}" if GRIPS[grip] else "") + f" -> {path}"]
    return lines, [], motion


def _sample_clip(scene, source, action, start, count, mapped, carried):
    """Per frame of a clip played on the source: the world matrix of every mapped bone, and the
    own turn of every carried bone."""
    _play(source, action)
    frames = []
    for frame in range(start, start + count):
        scene.frame_set(frame)
        bpy.context.view_layer.update()
        bones = source.pose.bones
        frames.append(({name: source.matrix_world @ bones[name].matrix for name in mapped},
                       {name: bones[name].rotation_quaternion.copy() for name in carried}))
    _play(source, None)
    return frames


def _rest_difference(target, source, names):
    """How far the rests of two armatures differ at most over the named bones: the largest
    difference between two entries of their rest matrices."""
    worst = 0.0
    for name in names:
        a, b = target.data.bones[name].matrix_local, source.data.bones[name].matrix_local
        worst = max(worst, max(abs(a[row][column] - b[row][column]) for row in range(4) for column in range(4)))
    return worst


def refit(path, label, old_path, skeleton_path, leave):
    """Moves the clips of the animation library at path from the rig they were keyed on to its
    armature label, a new rig of the same skeleton, and saves the library. old_path is the library
    as it was before its armature was replaced, which still has the old rig under the same name.

    Every frame of a clip turns each deforming bone of the new rig from its rest as the old rig's
    bone turns from its rest, in world space, as an imported clip is moved: the arms and the fingers
    aligned to the old rest pose, each hand in the old hand's frame, the hips' movement scaled by
    the legs' lengths, and the legs solved again so each ankle is where it was. A foot keeps its
    turn from the ground, and a bone that deforms nothing, such as a socket, its own turn. A clip
    keys the bones it keyed, on every frame, loops if it looped, and keeps its name.

    The clips named in leave, joined by ",", or "-" for none, are removed instead, for the gait
    model to make again on the new rig. Two rigs that rest alike leave the library as it is.
    Returns the report lines, the broken rules, and no motion numbers."""
    from art_pipeline import clip_video
    from art_pipeline import skeleton as skeleton_definition

    skeleton = skeleton_definition.load(skeleton_path)
    skip = frozenset(skeleton.unkeyed_bones)
    leave = [] if leave == "-" else [name for name in leave.split(",") if name]
    bpy.ops.wm.open_mainfile(filepath=path)
    target = bpy.data.objects.get(label)
    if target is None or target.type != "ARMATURE":
        return [], [f"{path} has no armature {label}."], None
    clips = sorted(action.name for action in bpy.data.actions)
    scene = bpy.context.scene
    scene.render.fps, scene.render.fps_base = animate.FPS, 1.0

    # The old rig, appended beside the new one; a clip it was showing comes along and is removed.
    objects, actions = set(bpy.data.objects), set(bpy.data.actions)
    with bpy.data.libraries.load(os.path.abspath(old_path), link=False) as (found, loaded):
        if label not in found.objects:
            return [], [f"{old_path} has no armature {label}."], None
        loaded.objects = [label]
    source = loaded.objects[0]
    if source.type != "ARMATURE":
        return [], [f"{label} in {old_path} is not an armature."], None
    added = [o for o in bpy.data.objects if o not in objects]
    scene.collection.objects.link(source)
    if source.animation_data is not None:
        for track in list(source.animation_data.nla_tracks):
            source.animation_data.nla_tracks.remove(track)
        source.animation_data.action = None
    for action in [a for a in bpy.data.actions if a not in actions]:
        bpy.data.actions.remove(action)

    common = [b.name for b in target.data.bones if b.name in source.data.bones]
    apart = _rest_difference(target, source, common)
    if apart <= SAME_REST and not leave:
        return [f"{label} rests as the rig its clips were keyed on: the clips stay as they are"], [], None
    deform = set(skeleton.deform_bones)
    bones = {name: name for name in common if name in deform and name not in skip}
    carried = [name for name in common if name not in deform and name not in skip]
    missing = [bone for bone in REQUIRED if bone not in bones]
    if missing:
        return [], [f"Both rigs need the bones {', '.join(missing)} for a refit."], None

    plans, errors = [], []
    for name in clips:
        if name in leave:
            continue
        action = bpy.data.actions[name]
        if not action.slots:
            errors.append(f"{name}: the clip has no slot to play on the armature.")
            continue
        channelbag = anim_utils.action_get_channelbag_for_slot(action, action.slots[0])
        turned, hips_move, other = keyed(fc.data_path for fc in channelbag.fcurves)
        if other:
            errors.append(f"{name}: a refit moves the turns of bones and the hips' place only, not {', '.join(other)}.")
        if hips_move:
            turned.add("Hips")
        start, end = (int(round(v)) for v in action.frame_range)
        loop = bool(action.get("loop", False))
        if distinct_frames(start, end, loop) < 1:
            errors.append(f"{name}: the clip has no frame.")
        plans.append((name, action, turned, start, distinct_frames(start, end, loop), loop))
    if errors:
        return [], errors, None

    moved = Retarget(target, source, bones, 0.0, skip, REFIT_LIMBS)
    target.animation_data_create()
    active = target.animation_data.action.name if target.animation_data.action is not None else None
    present = {b.name for b in target.data.bones}
    lines = [f"{label}: {len(plans)} clips moved to the new rig, {len(bones)} bones, {len(moved.align)} of them aligned to the "
             f"old rest pose, the legs {moved.scale:.3f} times as long"]
    for name, action, turned, start, count, loop in plans:
        frames = _sample_clip(scene, source, action, start, count, set(bones.values()), carried)
        before = clip_video.measure(source, action, skip)
        # The old clip gives its name to the new one.
        action.name = f"{name}.before"
        clip_skip = frozenset(present - turned) | skip

        def frame(index, frames=frames):
            world, turns = frames[index]
            local, hips_location = moved.frame(world, {})
            local.update(turns)
            return local, hips_location

        made = animate.bake(target, name, count, frame, clip_skip, loop)
        if made.name != name:
            return [], [f"{name}: Blender named the new clip {made.name}."], None
        after = clip_video.measure(target, made, skip)
        bpy.data.actions.remove(action)
        gone = sorted((turned - present) | (turned & skip))
        lines.append(f"{name}: {after['frames']} frames, {'loops' if loop else 'ends'}, "
                     f"{len(turned & present - skip)} bones keyed; lowest point {before['lowestPoint']:.3f} -> {after['lowestPoint']:.3f} m, hips at their lowest "
                     f"{before['rootLowest']:.2f} -> {after['rootLowest']:.2f} of their rest height"
                     + (f"; the keys of {', '.join(gone)} are dropped" if gone else ""))
    for name in leave:
        if name in bpy.data.actions:
            bpy.data.actions.remove(bpy.data.actions[name])
            lines.append(f"{name}: removed, for the gait model to make again")
    _remove(added, [], [])
    # The FBX export bakes every action as a take only for an object that has animation data.
    target.animation_data.action = bpy.data.actions.get(active) if active else None
    scene.frame_start, scene.frame_end = 0, max((int(a.frame_range[1]) for a in bpy.data.actions), default=60)
    bpy.ops.wm.save_as_mainfile(filepath=path, compress=True, relative_remap=False)
    return lines, [], None
