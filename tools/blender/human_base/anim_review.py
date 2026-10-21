"""Review sheets of the animated base characters, lit like the model review: each clip as a strip
of frames from the side and from the front three-quarter, on a ground grid, so the reviewer sees the
poses, the foot contacts and the arm swing. A review file shows both characters walking across the
grid at each clip's speed, so a foot that slides on the ground shows at once."""

import os

import bpy
from bpy_extras import anim_utils
from mathutils import Vector

from . import animate, review, rig_review

STRIP = 8  # frames per strip


def _ground(width=6.0, length=6.0):
    """A floor of 0.25 m checkers."""
    bpy.ops.mesh.primitive_plane_add(size=1.0, location=(0.0, 0.0, 0.0))
    plane = bpy.context.active_object
    plane.name = "ReviewGround"
    for v in plane.data.vertices:
        v.co.x *= width
        v.co.y *= length
    mat = bpy.data.materials.new("ReviewGround")
    mat.use_nodes = True
    nt = mat.node_tree
    bsdf = next(n for n in nt.nodes if n.type == "BSDF_PRINCIPLED")
    coords = nt.nodes.new("ShaderNodeTexCoord")
    checker = nt.nodes.new("ShaderNodeTexChecker")
    nt.links.new(coords.outputs["Object"], checker.inputs["Vector"])
    checker.inputs["Scale"].default_value = 4.0
    checker.inputs["Color1"].default_value = (0.32, 0.33, 0.35, 1.0)
    checker.inputs["Color2"].default_value = (0.26, 0.27, 0.29, 1.0)
    nt.links.new(checker.outputs["Color"], bsdf.inputs["Base Color"])
    bsdf.inputs["Roughness"].default_value = 0.9
    plane.data.materials.append(mat)
    return plane


def _strip(arm, cam, action, yaw, frames, dist):
    """Cells of one clip at evenly spaced frames from one side."""
    cells = []
    for k in range(frames):
        def draw(path, k=k):
            arm.animation_data.action = action
            end = int(action.frame_range[1])
            bpy.context.scene.frame_set(round(k * end / frames))
            review.aim(cam, Vector((0.0, 0.0, 0.9)), yaw, 4, dist)
            return rig_review._render(path + ".png")
        cells.append(draw)
    return cells


# The review file plays each clip this many seconds, the jog and the walk on a moving floor.
PLAY_SECONDS = {"Idle": 4.0, "Walk": 4.0, "Jog": 3.0}
REVIEW_OFFSET = 0.5


def _sample(arm, action):
    """The pose of every bone at every frame of a clip."""
    arm.animation_data.action = action
    frames = []
    for f in range(int(action.frame_range[1])):
        bpy.context.scene.frame_set(f)
        frames.append({pb.name: (pb.rotation_quaternion.copy(), pb.location.copy()) for pb in arm.pose.bones})
    return frames


def _samples(path, label):
    """The poses of every clip of the character in a file, frame by frame."""
    bpy.ops.wm.open_mainfile(filepath=path)
    arm = bpy.data.objects[label]
    arm.animation_data_create()
    return {name: _sample(arm, bpy.data.actions[name]) for name in PLAY_SECONDS}


def review_file(male_path, female_path, out):
    """Both animated characters side by side, playing Idle, Walk and Jog one after the other on a
    floor that moves under them at each clip's speed, like a treadmill: a foot on the ground keeps
    its place on the checkers unless it slides."""
    clips = {"Female": _samples(female_path, "Female"), "Male": _samples(male_path, "Male")}
    bpy.ops.wm.open_mainfile(filepath=male_path)
    with bpy.data.libraries.load(female_path, link=False) as (_source, target):
        target.collections = ["Female"]
    bpy.data.collections["Export"].children.link(bpy.data.collections["Female"])
    scene = bpy.context.scene
    floor = _ground(2.5, 60.0)
    floor.animation_data_create()
    floor.animation_data.action = bpy.data.actions.new("ReviewFloor")
    frame, travelled = 0, 0.0
    for name, seconds in PLAY_SECONDS.items():
        floor.location.y = -travelled
        floor.keyframe_insert("location", index=1, frame=frame)
        frame += round(seconds * animate.FPS)
        travelled += animate.CLIPS[name] * seconds
    floor.location.y = -travelled
    floor.keyframe_insert("location", index=1, frame=frame)
    channelbag = anim_utils.action_get_channelbag_for_slot(floor.animation_data.action, floor.animation_data.action_slot)
    for fc in channelbag.fcurves:
        for key in fc.keyframe_points:
            key.interpolation = "LINEAR"
    for label, x in (("Male", -REVIEW_OFFSET), ("Female", REVIEW_OFFSET)):
        arm = bpy.data.objects[label]
        arm.location.x = x
        arm.animation_data_create()
        arm.animation_data.action = bpy.data.actions.new(f"Review_{label}")
        start = 0
        for name, seconds in PLAY_SECONDS.items():
            frames = clips[label][name]
            for k in range(round(seconds * animate.FPS)):
                for bone, (rotation, location) in frames[k % len(frames)].items():
                    pb = arm.pose.bones[bone]
                    pb.rotation_quaternion = rotation
                    pb.keyframe_insert("rotation_quaternion", frame=start + k)
                    if bone == "Hips":
                        pb.location = location
                        pb.keyframe_insert("location", frame=start + k)
            start += round(seconds * animate.FPS)
    scene.frame_start, scene.frame_end = 0, frame - 1
    scene.frame_set(0)
    bpy.ops.wm.save_as_mainfile(filepath=out, compress=True, relative_remap=False)
    return out


def render(path, label, out_dir, prefix):
    """Renders a sheet per clip to <out_dir>/<prefix>_anim_<clip>.png."""
    bpy.ops.wm.open_mainfile(filepath=path)
    out_dir = os.path.abspath(out_dir)
    os.makedirs(out_dir, exist_ok=True)
    arm = bpy.data.objects[label]
    arm.animation_data_create()
    cam = review.add_camera(lens=50)
    written = []
    for name in ("Idle", "Walk", "Jog"):
        review.setup((300, 420))
        _ground()
        action = bpy.data.actions[name]
        cells = _strip(arm, cam, action, 90, STRIP, 3.4) + _strip(arm, cam, action, 30, STRIP, 3.4)
        written.append(rig_review._grid(cells, STRIP, os.path.join(out_dir, f"{prefix}_anim_{name.lower()}.png")))
    arm.animation_data.action = None
    return written
