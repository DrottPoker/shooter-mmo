"""Renders clips as videos and measures their motion, so every clip is reviewed in motion with numbers
that show what a still frame hides: a body that sinks, a bone that whips, a clip in which only a part
of the body moves, a foot under the ground, or a loop that jumps. Each video shows every body of the
file from the front right on a checkered ground, the clip played three times; a ground that moves
back at the speed the game moves the body shows a foot that slides. The file is never saved."""

import math
import os
import tempfile

import bpy
import numpy as np
from mathutils import Quaternion, Vector

from . import library, preview
from .scene import ArtPipelineError, export_objects

WIDTH = 960
HEIGHT = 540
FPS = 30
PLAYS = 3
# The camera looks at the bodies from the front right, from this far around and this far above.
YAW = 28.0
PITCH = 10.0
# Room around the bodies' bounds, as a share of the camera's distance.
MARGIN = 1.08
# A bone moves when it turns in its joint, against its parent, by more than this many degrees from
# the clip's first frame.
MOVING_TURN = 5.0
# Bones shorter than this, in meters, such as fingers and toes, are left out of the fastest bone:
# they ride on a hand or a foot and would always outrun it.
SHORT_BONE = 0.05
GROUND = "Reference_Ground"


def motion_numbers(heads: np.ndarray, tails: np.ndarray, root: np.ndarray, rest_root_z: float, fps: float,
                   loop: bool, names: list[str], lengths: np.ndarray, turns: np.ndarray) -> dict:
    """The motion of a clip from the world positions of its bones' heads and tails, (frames, bones, 3)
    with names naming the bones, of the root bone's head, (frames, 3), the bones' rest lengths, and
    turns, (frames, bones), the degrees each bone has turned against its parent since the first
    frame: its length, how far the root travels horizontally and how low it sinks as a share of its
    rest height, the bone of at least SHORT_BONE whose head moves fastest between two frames and its
    speed, the share of the bones that turn more than MOVING_TURN in their joints, the lowest head
    or tail, and for a loop the largest distance of a head between the first and the last frame."""
    heads, tails, root = (np.asarray(a, dtype=np.float64) for a in (heads, tails, root))
    lengths, turns = np.asarray(lengths, dtype=np.float64), np.asarray(turns, dtype=np.float64)
    count = heads.shape[0]
    track = root[:, :2]
    fastest, speed = None, 0.0
    if count > 1 and heads.shape[1]:
        steps = np.linalg.norm(np.diff(heads, axis=0), axis=2) * fps
        steps[:, lengths < SHORT_BONE] = 0.0
        frame, bone = np.unravel_index(int(np.argmax(steps)), steps.shape)
        if steps[frame, bone] > 0.0:
            fastest, speed = names[bone], float(steps[frame, bone])
    moving = float((turns.max(axis=0) > MOVING_TURN).mean()) if heads.shape[1] else 0.0
    lowest = min(float(heads[..., 2].min()), float(tails[..., 2].min())) if heads.shape[1] else float(root[:, 2].min())
    gap = float(np.linalg.norm(heads[-1] - heads[0], axis=1).max()) if loop and heads.shape[1] else 0.0
    return {
        "frames": count - 1,
        "seconds": round((count - 1) / fps, 2),
        "rootTravel": round(float(np.linalg.norm(track.max(axis=0) - track.min(axis=0))), 2) + 0.0,
        "rootLowest": round(float(root[:, 2].min()) / rest_root_z, 2) + 0.0 if rest_root_z > 0.0 else 0.0,
        "fastestBone": fastest,
        "fastestSpeed": round(speed, 1) + 0.0,
        "movingShare": round(moving, 2) + 0.0,
        "lowestPoint": round(lowest, 3) + 0.0,
        "loopGap": round(gap, 3) + 0.0,
    }


def _play(arm: bpy.types.Object, action: bpy.types.Action | None) -> None:
    """Plays the action on the armature from its rest pose, so a bone the clip does not key, such
    as every bone but the fingers in a hand pose, is at rest and not where the last clip left it."""
    for bone in arm.pose.bones:
        bone.location = (0.0, 0.0, 0.0)
        bone.rotation_quaternion = Quaternion()
        bone.rotation_euler = (0.0, 0.0, 0.0)
        bone.scale = (1.0, 1.0, 1.0)
    arm.animation_data_create()
    arm.animation_data.action = action
    if action is not None and action.slots:
        arm.animation_data.action_slot = action.slots[0]


def _frames(action: bpy.types.Action) -> range:
    start, end = (int(round(v)) for v in action.frame_range)
    return range(start, end + 1)


def measure(arm: bpy.types.Object, action: bpy.types.Action, skip: set[str] | frozenset[str] = frozenset()) -> dict:
    """The motion numbers of a clip played on the armature, over its deforming bones but those in
    skip, such as the cloth chains that the runtime solver moves, with the clip's name first."""
    scene = bpy.context.scene
    names = [b.name for b in arm.data.bones if b.use_deform and b.name not in skip]
    root = next(b for b in arm.data.bones if b.parent is None)
    matrix = arm.matrix_world
    active = arm.animation_data.action if arm.animation_data is not None else None
    _play(arm, action)
    heads, tails, roots, turns, first = [], [], [], [], {}
    for frame in _frames(action):
        scene.frame_set(frame)
        bones = arm.pose.bones
        heads.append([tuple(matrix @ bones[n].head) for n in names])
        tails.append([tuple(matrix @ bones[n].tail) for n in names])
        roots.append(tuple(matrix @ bones[root.name].head))
        row = []
        for n in names:
            bone = bones[n]
            joint = (bone.parent.matrix.inverted() @ bone.matrix if bone.parent else bone.matrix).to_quaternion()
            row.append(math.degrees(2.0 * math.acos(min(1.0, abs(first.setdefault(n, joint).dot(joint))))))
        turns.append(row)
    _play(arm, active)
    shape = (len(roots), len(names), 3)
    numbers = motion_numbers(np.array(heads, dtype=np.float64).reshape(shape), np.array(tails, dtype=np.float64).reshape(shape),
                             np.array(roots), (matrix @ root.head_local).z, scene.render.fps / scene.render.fps_base,
                             bool(action.get("loop", False)), names, np.array([arm.data.bones[n].length for n in names]),
                             np.array(turns, dtype=np.float64).reshape(shape[:2]))
    return {"clip": action.name, **numbers}


def _bodies() -> list[bpy.types.Object]:
    """Every mesh the file shows: of an animation library its rig's body and its preview bodies in
    the Reference collection, of a skinned asset its meshes in the Export collection."""
    found = set(export_objects())
    reference = bpy.data.collections.get(library.REFERENCE_COLLECTION)
    if reference is not None:
        found |= set(reference.all_objects)
    return sorted((o for o in found if o.type == "MESH" and o.name != GROUND and not o.hide_render), key=lambda o: o.name)


def _ground(bodies: list[bpy.types.Object]) -> bpy.types.Object:
    """The file's checkered ground, or one made like an animation library's under the bodies."""
    ground = bpy.data.objects.get(GROUND)
    if ground is None:
        xs = [(o.matrix_world @ Vector(c)).x for o in bodies for c in o.bound_box]
        library._ground(bpy.context.scene.collection, (min(xs) + max(xs)) / 2.0)
        ground = bpy.data.objects[GROUND]
    ground.hide_render = False
    return ground


def _stretch(ground: bpy.types.Object, flat: bpy.types.Mesh, travel: float) -> None:
    """Gives the ground its flat mesh lengthened toward +Y by the distance it moves back, so it never
    runs out under the bodies. Its checkers keep their places, since they follow the object's own
    coordinates."""
    mesh = flat.copy()
    for vertex in mesh.vertices:
        if vertex.co.y > 0.0:
            vertex.co.y += travel
    ground.data = mesh


def _bounds(scene: bpy.types.Scene, frames: range, bodies: list[bpy.types.Object], used: list[tuple]) -> tuple[Vector, Vector]:
    """The bounds of the bodies and the tools they hold over every frame of the clip."""
    low = Vector((math.inf,) * 3)
    high = Vector((-math.inf,) * 3)
    depsgraph = bpy.context.evaluated_depsgraph_get()
    tools = [o for _arm, _socket, _root, objects in used for o in objects if o.type == "MESH"]
    for frame in frames:
        scene.frame_set(frame)
        preview.place_held(used)
        for obj in bodies + tools:
            evaluated = obj.evaluated_get(depsgraph)
            for corner in evaluated.bound_box:
                point = evaluated.matrix_world @ Vector(corner)
                low = Vector(map(min, low, point))
                high = Vector(map(max, high, point))
    return low, high


def _aim(camera: bpy.types.Object, sun: bpy.types.Object, low: Vector, high: Vector) -> None:
    """Puts the camera at the front right, just far enough that every corner of the bounds is in view."""
    yaw, pitch = math.radians(YAW), math.radians(PITCH)
    direction = Vector((math.sin(yaw) * math.cos(pitch), math.cos(yaw) * math.cos(pitch), math.sin(pitch)))
    rotation = (-direction).to_track_quat("-Z", "Y").to_matrix()
    right, up = rotation.col[0], rotation.col[1]
    half_x = math.tan(camera.data.angle / 2.0)
    half_y = half_x * HEIGHT / WIDTH
    center = (low + high) / 2.0
    distance = 0.0
    for corner in ((x, y, z) for x in (low.x, high.x) for y in (low.y, high.y) for z in (low.z, high.z)):
        offset = Vector(corner) - center
        toward = offset.dot(direction)
        distance = max(distance, abs(offset.dot(right)) / half_x + toward, abs(offset.dot(up)) / half_y + toward)
    camera.location = center + direction * distance * MARGIN
    camera.rotation_euler = rotation.to_euler()
    sun.rotation_euler = (math.radians(50.0), 0.0, math.radians(200.0))


def _encode(plays: list[list[str]], output: str) -> None:
    """Joins the frames of each play into one H.264 video in MP4 at FPS, with Blender's sequencer."""
    scene = bpy.data.scenes.new("ClipVideo")
    try:
        scene.render.fps, scene.render.fps_base = FPS, 1.0
        scene.render.resolution_x, scene.render.resolution_y = WIDTH, HEIGHT
        scene.render.resolution_percentage = 100
        editor = scene.sequence_editor_create()
        strips = editor.strips if hasattr(editor, "strips") else editor.sequences
        start = 1
        for index, paths in enumerate(plays):
            strip = strips.new_image(name=f"Play{index}", filepath=paths[0], channel=1, frame_start=start)
            for path in paths[1:]:
                strip.elements.append(os.path.basename(path))
            start += len(paths)
        scene.frame_start, scene.frame_end = 1, start - 1
        settings = scene.render.image_settings
        if hasattr(settings, "media_type"):
            settings.media_type = "VIDEO"
        settings.file_format = "FFMPEG"
        scene.render.ffmpeg.format = "MPEG4"
        scene.render.ffmpeg.codec = "H264"
        scene.render.ffmpeg.constant_rate_factor = "HIGH"
        # The frames are already in display colors.
        scene.view_settings.view_transform = "Standard"
        scene.render.filepath = output
        bpy.ops.render.render(animation=True, scene=scene.name)
    finally:
        bpy.data.scenes.remove(scene)
    if not os.path.isfile(output) or os.path.getsize(output) == 0:
        raise ArtPipelineError(f"Blender wrote no video {output}.")


def render_clips(output: str, armature: str, clips: list[str], speed: float, skip: set[str],
                 held: list[tuple[str, str, str]]) -> list[dict]:
    """Writes output/<file>_<clip>.mp4 for every named clip played on the armature, with the tools
    used in a clip held as the clip sheet holds them; returns per clip its motion numbers and video."""
    scene = bpy.context.scene
    name = os.path.splitext(os.path.basename(bpy.data.filepath))[0]
    arm = bpy.data.objects.get(armature)
    if arm is None or arm.type != "ARMATURE":
        raise ArtPipelineError(f'The file has no armature "{armature}".')
    missing = [clip for clip in clips if clip not in bpy.data.actions]
    if missing:
        raise ArtPipelineError(f"The file has no clip {', '.join(missing)}.")
    os.makedirs(output, exist_ok=True)
    bodies = _bodies()
    if not bodies:
        raise ArtPipelineError("The file shows no body to play its clips on.")
    reference = bpy.data.collections.get(library.REFERENCE_COLLECTION)
    others = [o for o in (reference.all_objects if reference is not None else []) if o.type == "ARMATURE" and o is not arm]
    tools = preview.held_tools([arm] + sorted(others, key=lambda o: o.name), held)
    for obj in scene.objects:
        obj.hide_render = obj not in bodies
    ground = _ground(bodies)
    base = ground.location.copy()
    flat = ground.data
    camera, sun = preview._studio(scene, WIDTH)
    scene.render.resolution_x, scene.render.resolution_y = WIDTH, HEIGHT
    scene.render.image_settings.color_mode = "RGB"

    results = []
    for clip in clips:
        action = bpy.data.actions[clip]
        used = preview.show_held(tools, clip)
        frames = _frames(action)
        numbers = measure(arm, action, skip)
        _play(arm, action)
        _aim(camera, sun, *_bounds(scene, frames, bodies, used))
        # A looping clip ends on its first pose again, which the next play shows, so a play leaves
        # its last frame out and the loop runs on without a held frame.
        if action.get("loop", False) and len(frames) > 1:
            frames = frames[:-1]
        # A moving ground moves on through every play, so each play is rendered; a still one once.
        plays = PLAYS if speed > 0.0 else 1
        ground.location = base
        _stretch(ground, flat, speed * plays * len(frames) / FPS)
        video = os.path.join(output, f"{name}_{clip}.mp4")
        with tempfile.TemporaryDirectory(dir=output, prefix=f"{name}_{clip}_frames_") as scratch:
            rendered = []
            for play in range(plays):
                paths = []
                for index, frame in enumerate(frames):
                    scene.frame_set(frame)
                    preview.place_held(used)
                    ground.location.y = base.y - speed * (play * len(frames) + index) / FPS
                    paths.append(os.path.join(scratch, f"{play}_{index:04d}.png"))
                    scene.render.filepath = paths[-1]
                    bpy.ops.render.render(write_still=True)
                rendered.append(paths)
            _encode(rendered * PLAYS if plays == 1 else rendered, video)
        results.append(dict(numbers, video=video))
    _play(arm, None)
    return results
