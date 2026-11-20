"""Review sheets of a rigged base character, lit like the model review: the skeleton seen through
the body, the shoulders with their bones, the shoulders and the hips through their reach, the hips,
knees and elbows bending with the wireframe, the test poses of poses.POSES in full figure, close-ups
of the other joints in those poses, the hands opened and closed, the jaw, the eyes, and the eyelids
closing when the skeleton has them, and the weights of the bones where the limbs meet the body and
of the eyelids.

The sheets show the whole body, the mesh a person edits, with the eyes and the teeth. Blender
shades the parts of a posed body with a seam at every cut between two parts, because it keeps their
custom normals relative to each part's own surface, and the game, which turns the normals with the
bones, shows none."""

import math
import os
import tempfile

import bmesh
import bpy
import numpy as np
from mathutils import Vector

from art_pipeline import bodies, poses
from art_pipeline import skeleton as skeleton_definition

from . import files, review

# Bone colours of the skeleton sheet: the character's right, left, centre, and the sockets.
BONE_COLOURS = {"R": (0.95, 0.30, 0.25), "L": (0.25, 0.55, 0.95), "C": (0.95, 0.80, 0.25), "S": (0.25, 0.90, 0.40)}
XRAY = 0.85
# The hips sheet turns the thigh this far out to the side, in degrees about the thigh bone's Z axis.
HIP_OUT = 35


def _pose(name):
    return next(p for n, _d, p in poses.POSES if n == name)


def _show_whole_body(arm):
    """Has renders show the armature's whole body in place of its parts."""
    bodies.source_body(arm.name).hide_render = False
    for part in bodies.parts_of(arm.name):
        part.hide_render = True


def _shown(arm):
    """The armature's meshes that a render shows."""
    return [o for o in arm.children if o.type == "MESH" and not o.hide_render]


def _joint(arm, bone, t=0.0):
    if bone == "Eyes":  # between the eyes
        return (_joint(arm, "Eye_L") + _joint(arm, "Eye_R")) / 2
    pb = arm.pose.bones[bone]
    return arm.matrix_world @ pb.head.lerp(pb.tail, t)


def chain_bones(arm):
    """The names of the armature's cloth chain bones, which the rig keeps in its Cloth collection."""
    collection = arm.data.collections.get("Cloth")
    return {b.name for b in collection.bones} if collection is not None else set()


def bone_overlay(arm, chains=False):
    """A mesh of one octahedron per bone, like Blender's bone display, posed with the armature: the
    skeleton without its cloth chains, or the cloth chains alone."""
    bm = bmesh.new()
    colours = []
    cloth = chain_bones(arm)
    for pb in arm.pose.bones:
        if (pb.name in cloth) != chains:
            continue
        side = "S" if pb.name.startswith("Socket_") else pb.name[-1] if pb.name[-2:] in ("_L", "_R") else "C"
        m = arm.matrix_world @ pb.matrix
        length = pb.length
        w = max(0.08 * length, 0.002)
        local = [Vector((0, 0, 0)), Vector((0, length, 0)), Vector((w, 0.2 * length, w)), Vector((-w, 0.2 * length, w)),
                 Vector((-w, 0.2 * length, -w)), Vector((w, 0.2 * length, -w))]
        v = [bm.verts.new(m @ p) for p in local]
        for a, b in ((2, 3), (3, 4), (4, 5), (5, 2)):
            bm.faces.new((v[0], v[b], v[a]))
            bm.faces.new((v[1], v[a], v[b]))
            colours += [side, side]
    mesh = bpy.data.meshes.new("RigBones")
    bm.to_mesh(mesh)
    bm.free()
    for key, rgb in BONE_COLOURS.items():
        mat = bpy.data.materials.new(f"RigBone_{key}")
        mat.use_nodes = True
        nt = mat.node_tree
        nt.nodes.clear()
        emit = nt.nodes.new("ShaderNodeEmission")
        emit.inputs["Color"].default_value = (*rgb, 1.0)
        out = nt.nodes.new("ShaderNodeOutputMaterial")
        nt.links.new(emit.outputs["Emission"], out.inputs["Surface"])
        mesh.materials.append(mat)
    order = list(BONE_COLOURS)
    for poly, side in zip(mesh.polygons, colours):
        poly.material_index = order.index(side)
    obj = bpy.data.objects.new("RigBones", mesh)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def _render(path):
    return review.render_to_array(path)


def _grid(cells, columns, output):
    """cells: callables that render one cell into the given path and return its pixels."""
    scene = bpy.context.scene
    w, h = scene.render.resolution_x, scene.render.resolution_y
    rows = math.ceil(len(cells) / columns)
    sheet = np.zeros((h * rows, w * columns, 4), dtype=np.float32)
    sheet[..., 3] = 1.0
    with tempfile.TemporaryDirectory() as tmp:
        for i, cell in enumerate(cells):
            px = cell(os.path.join(tmp, f"{i}"))
            r, c = divmod(i, columns)
            top = (rows - 1 - r) * h
            sheet[top:top + h, c * w:(c + 1) * w] = px
    os.makedirs(os.path.dirname(output), exist_ok=True)
    image = bpy.data.images.new("Sheet", sheet.shape[1], sheet.shape[0], alpha=True)
    image.pixels.foreach_set(sheet.ravel())
    image.filepath_raw = output
    image.file_format = "PNG"
    image.save()
    bpy.data.images.remove(image)
    return output


def _xray(arm, path, overlay, strength=XRAY):
    """Renders the body, then the bones alone, and lays the bones over the body."""
    scene = bpy.context.scene
    meshes = _shown(arm)
    overlay.hide_render = True
    body = _render(path + "_body.png")
    overlay.hide_render = False
    for o in meshes:
        o.hide_render = True
    scene.render.film_transparent = True
    bones = _render(path + "_bones.png")
    scene.render.film_transparent = False
    for o in meshes:
        o.hide_render = False
    a = bones[..., 3:4] * strength
    return np.concatenate([body[..., :3] * (1 - a) + bones[..., :3] * a, body[..., 3:]], axis=2)


def shoulders_sheet(arm, out, cam):
    """The shoulders with their bones in the bind pose and with the arms raised, from the front
    and the back."""
    review.setup((440, 440))
    cam.data.lens = 85
    views = (("rest", 0, 5), ("t_pose", 0, 5), ("arms_up", 0, 5), ("t_pose", 150, 10), ("arms_up", 160, 5),
             ("arms_forward", 60, 5))

    def cell(view):
        def draw(path):
            pose, yaw, pitch = view
            poses.apply(arm, _pose(pose))
            overlay = bone_overlay(arm)
            c = _joint(arm, "UpperChest")
            review.aim(cam, Vector((0, c.y, c.z + 0.08)), yaw, pitch, 1.6)
            try:
                return _xray(arm, path, overlay, 0.6)
            finally:
                bpy.data.objects.remove(overlay)
        return draw

    return _grid([cell(v) for v in views], 3, out)


def skeleton(arm, out, cam):
    """The skeleton in the bind pose, seen through the body, and close-ups of the hand, the head,
    the foot and the back socket."""
    review.setup((520, 700))
    poses.rest(arm)
    overlay = bone_overlay(arm)
    mid = Vector((0, 0, _joint(arm, "Hips").z + 0.05))
    views = [(mid, yaw, 3, 3.1, 50) for yaw in (0, 90, 180)]
    views += [(_joint(arm, "Hand_R", 0.6), 75, 5, 0.55, 85), (_joint(arm, "Head", 0.25), 35, 0, 0.75, 85),
              (_joint(arm, "Foot_R", 0.5), 70, 15, 0.7, 85), (_joint(arm, "UpperChest"), 150, 15, 1.1, 85),
              (_joint(arm, "Hand_L", 0.6), -105, 5, 0.55, 85)]

    def cell(view):
        def draw(path):
            target, yaw, pitch, dist, lens = view
            cam.data.lens = lens
            review.aim(cam, target, yaw, pitch, dist)
            return _xray(arm, path, overlay)
        return draw

    path = _grid([cell(v) for v in views], 4, out)
    bpy.data.objects.remove(overlay)
    return path


def chains_sheet(arm, out, cam):
    """The cloth chains in the bind pose, seen through the body from four sides and from above the
    hips: the skirt ring hanging to the ankles, the cloak columns down the back, and the hood."""
    review.setup((520, 700))
    poses.rest(arm)
    overlay = bone_overlay(arm, chains=True)
    mid = Vector((0, 0, _joint(arm, "Hips").z + 0.05))
    views = [(mid, yaw, 3, 3.1, 50) for yaw in (0, 45, 90, 180)]
    views += [(_joint(arm, "Hips"), 0, 70, 1.6, 50), (_joint(arm, "Head", 0.25), 120, 5, 0.8, 85)]

    def cell(view):
        def draw(path):
            target, yaw, pitch, dist, lens = view
            cam.data.lens = lens
            review.aim(cam, target, yaw, pitch, dist)
            return _xray(arm, path, overlay)
        return draw

    path = _grid([cell(v) for v in views], 3, out)
    bpy.data.objects.remove(overlay)
    return path


def _shots(arm, cam, views):
    """Cells of views: the pose, the bone to look at and how far along it, the yaw, the pitch, the
    distance and, when a view names one, how far from the bone to look, in metres."""
    def cell(view):
        def draw(path):
            pose, bone, t, yaw, pitch, dist, *offset = view
            poses.apply(arm, _pose(pose) if isinstance(pose, str) else pose)
            review.aim(cam, _joint(arm, bone, t) + Vector(offset[0] if offset else (0.0, 0.0, 0.0)), yaw, pitch, dist)
            return _render(path + ".png")
        return draw
    return [cell(v) for v in views]


def arms_sheet(arm, out, cam):
    """The right shoulder through its reach, without the bones: the bind pose, the arms raised to
    the side and overhead, an arm hanging at the side, drawn back, and reaching forward."""
    review.setup((560, 560))
    cam.data.lens = 85
    near, wide = (-0.02, 0.0, 0.02), (-0.07, 0.0, 0.10)

    def arms(*turns):
        return {"UpperArm_s": turns}

    views = [("rest", 0, 5, 1.15, near), ("t_pose", 0, 5, 1.15, near), ("t_pose", 180, 10, 1.15, near),
             ("t_pose", 35, 10, 1.15, near), ("arms_up", 0, 5, 1.5, wide), ("arms_up", 180, 10, 1.5, wide),
             (arms(("Z", 14)), 0, 5, 1.15, near), (arms(("X", -40)), 180, 10, 1.15, near),
             (arms(("X", 45)), 35, 25, 1.15, near), (arms(("X", 90)), 35, 25, 1.15, near),
             (arms(("X", 90)), 90, 5, 1.15, near), ("arms_forward", 35, 10, 1.15, near)]
    return _grid(_shots(arm, cam, [(pose, "UpperArm_R", 0.0, yaw, pitch, dist, offset)
                                   for pose, yaw, pitch, dist, offset in views]), 4, out)


def hips_sheet(arm, out, cam):
    """The right hip through its reach, without the wireframe and with the arms raised out of the
    way: the thigh raised halfway, raised and bent, and out to the side, sitting, and the long
    stride."""
    review.setup((560, 560))
    cam.data.lens = 85
    groin = (-0.04, 0.0, 0.02)

    def leg(hip, knee=0, out=0):
        return {"UpperArm_s": (("Z", -70),), "UpperLeg_R": (("X", hip), ("Z", -out)), "LowerLeg_R": (("X", -knee),)}

    views = [(leg(45), 0, 0), (leg(45), 40, 10), (leg(45), 90, 0), (leg(45), 180, 0),
             (leg(90, 90), 0, 0), (leg(90, 90), 40, 10), (leg(90, 90), 90, 0), (leg(90, 90), 160, 5),
             (leg(0, 0, HIP_OUT), 0, 0), (leg(0, 0, HIP_OUT), 180, 0)]
    shots = [(pose, "UpperLeg_R", 0.0, yaw, pitch, 1.0, groin) for pose, yaw, pitch in views]
    shots += [("sit", "Hips", 0.0, 30, 10, 1.4), ("stride", "Hips", 0.0, 90, 0, 1.6)]
    return _grid(_shots(arm, cam, shots), 4, out)


def pose_sheet(arm, out, cam):
    """Every test pose in full figure, from the front and the back three-quarter."""
    review.setup((360, 500))
    cam.data.lens = 50
    views = []
    for name, _description, _pose_turns in poses.POSES:
        views += [(name, "Hips", 0.0, 30, 5, 3.6), (name, "Hips", 0.0, 150, 5, 3.6)]
    return _grid(_shots(arm, cam, views), 4, out)


def joints_sheet(arm, out, cam):
    """Close-ups of the armpit, the elbows, the knees, the feet, the trunk and the neck in the test
    poses; the shoulders and the hips have their own sheets."""
    review.setup((520, 520))
    cam.data.lens = 85
    views = [
        ("rest", "UpperArm_R", 0.0, 60, -5, 0.8), ("elbows", "LowerArm_R", 0.0, 90, 0, 0.75),
        ("elbows", "LowerArm_R", 0.0, 160, 5, 0.75), ("stride", "Hips", 0.0, 180, 5, 1.2),
        ("sit", "LowerLeg_R", 0.0, 90, 5, 0.9), ("stride", "LowerLeg_L", 0.0, -90, 0, 0.9),
        ("stride", "Toes_L", 0.0, -70, 10, 0.6), ("rest", "Foot_R", 0.5, 60, 15, 0.6),
        ("torso", "Chest", 0.0, 15, 5, 1.3), ("torso", "Neck", 0.0, 70, 5, 0.8),
        ("sit", "Hips", 0.0, 200, 10, 1.2), ("torso", "Chest", 0.0, 165, 5, 1.3),
    ]
    return _grid(_shots(arm, cam, views), 4, out)


def legs_sheet(arm, out, cam):
    """The hips, knees and elbows bending, with the wireframe and the arms raised out of the way:
    the thigh raised and bent, the knee bent, the leg stretched back, sitting, the long stride, and
    a bent elbow."""
    review.setup((420, 420))
    cam.data.lens = 85
    wires = []
    for o in [o for o in _shown(arm) if bodies.is_source_body(o) or "zone" in o]:
        wire = review.wire_overlay(o, 0.0006)
        md = wire.modifiers.new("Armature", "ARMATURE")
        md.object = arm
        wire.modifiers.move(1, 0)
        wires.append(wire)

    def leg(hip, knee=0):
        return {"UpperArm_s": (("Z", -70),), "UpperLeg_R": (("X", hip),), "LowerLeg_R": (("X", -knee),)}

    views = [(leg(45), "UpperLeg_R", 0.0, 90, 0, 0.9), (leg(90, 90), "UpperLeg_R", 0.0, 90, 0, 0.9),
             (leg(45), "UpperLeg_R", 0.0, 20, 0, 0.9), (leg(90, 90), "UpperLeg_R", 0.0, 20, 5, 0.9),
             (leg(90, 90), "UpperLeg_R", 0.0, 160, 5, 0.9), (leg(0, 60), "LowerLeg_R", 0.0, 90, 0, 0.75),
             (leg(0, 100), "LowerLeg_R", 0.0, 90, 0, 0.75), (leg(0, 100), "LowerLeg_R", 0.0, 30, 0, 0.75),
             (leg(-30, 40), "UpperLeg_R", 0.0, 160, 0, 0.9), (leg(-30, 40), "UpperLeg_R", 0.0, 90, 0, 0.9),
             ("sit", "Hips", 0.0, 90, 0, 1.4), ("sit", "Hips", 0.0, 20, 5, 1.4), ("sit", "Hips", 0.0, 200, -10, 1.4),
             ("stride", "Hips", 0.0, 90, 0, 1.6),
             ({"UpperArm_s": (("X", 20),), "LowerArm_s": (("X", 110),)}, "LowerArm_R", 0.0, 90, 0, 0.6)]
    try:
        return _grid(_shots(arm, cam, views), 5, out)
    finally:
        for wire in wires:
            mesh = wire.data
            bpy.data.objects.remove(wire)
            bpy.data.meshes.remove(mesh)


def hands_sheet(arm, out, cam):
    """The right hand open and closed to a fist, from the back, the palm and the front."""
    review.setup((420, 420))
    cam.data.lens = 85
    views = []
    for pose in ("rest", "fist"):
        views += [(pose, "Hand_R", 0.9, 90, 5, 0.55), (pose, "Hand_R", 0.9, 0, 0, 0.55),
                  (pose, "Hand_R", 0.9, 150, -5, 0.55), (pose, "Hand_R", 0.9, 30, -45, 0.55)]
    return _grid(_shots(arm, cam, views), 4, out)


def face_sheet(arm, out, cam, lids=None):
    """The jaw opening, the gaze, with the eyeballs turning inside the lids, and the head turned,
    nodded, and leaned back on the neck, where the face must stay on the skull. With eyelids, the
    lids half and fully closed from in front, the closed lids up close from the side, and closed
    with the jaw open."""
    review.setup((420, 420))
    cam.data.lens = 85
    views = []
    for degrees in (0, 6, 12, 18):
        views.append(({"Jaw": (("X", -degrees),)}, "Head", 0.2, 0, 0, 0.6))
    views += [({"Jaw": (("X", -12),)}, "Head", 0.2, 45, 0, 0.6), ({"Jaw": (("X", -12),)}, "Head", 0.2, 90, -5, 0.6)]
    for gaze in ((("Z", -22),), (("Z", 22),), (("X", 16),), (("X", -12),)):
        turns = tuple((axis, degrees) for axis, degrees in gaze)
        views.append(({"Eye_R": turns, "Eye_L": turns}, "Eyes", 0.0, 0, 0, 0.36))
    views += [({"Head": (("Y", 40),)}, "Head", 0.2, 0, 0, 0.6), ({"Head": (("Y", 40),)}, "Head", 0.2, 60, 0, 0.6),
              ({"Head": (("X", 25),)}, "Head", 0.2, 0, 0, 0.6), ({"Head": (("X", -25),)}, "Head", 0.2, 0, 0, 0.6),
              ({"Head": (("X", -25),)}, "Head", 0.2, 80, 0, 0.6)]
    if lids is not None:
        closed = poses.eyelids(lids)
        views += [(poses.eyelids(lids, 0.5), "Eyes", 0.0, 0, 0, 0.36), (closed, "Eyes", 0.0, 0, 0, 0.36),
                  (closed, "Eye_R", 0.0, 40, 5, 0.16), (closed, "Eye_R", 0.0, 85, 0, 0.16),
                  (dict(closed, Jaw=(("X", -12),)), "Head", 0.2, 25, 0, 0.6)]
    return _grid(_shots(arm, cam, views), 5, out)


# The review file's test animation: every test pose for POSE_FRAMES, with the rest pose between.
# The characters stand this far left and right of the centre, clear of each other's hands also with
# the arms straight out.
POSE_FRAMES = 24
REVIEW_OFFSET = 1.0


def review_file(male_path, female_path, skeleton_path, out):
    """Both rigged characters side by side, with an action "RigTest" on each armature that steps
    through the test poses, and closes the eyes when the skeleton has eyelids, for the reviewer to
    play in Blender."""
    lids = skeleton_definition.load(skeleton_path).eyelids
    steps = [pose for _name, _description, pose in poses.POSES[1:]] + ([poses.eyelids(lids)] if lids else [])
    files.open_both(male_path, female_path, REVIEW_OFFSET)
    scene = bpy.context.scene
    for label in files.LABELS:
        arm = bpy.data.objects[label]
        action = bpy.data.actions.new(f"RigTest_{label}")
        arm.animation_data_create()
        arm.animation_data.action = action
        frame = 0
        for pose in steps:
            for f, p in ((frame, {}), (frame + POSE_FRAMES // 2, pose), (frame + POSE_FRAMES, pose)):
                poses.apply(arm, p)
                for pb in arm.pose.bones:
                    pb.keyframe_insert("rotation_quaternion", frame=f)
                arm.pose.bones["Hips"].keyframe_insert("location", frame=f)
            frame += POSE_FRAMES + POSE_FRAMES // 2
        poses.apply(arm, {})
        for pb in arm.pose.bones:
            pb.keyframe_insert("rotation_quaternion", frame=frame)
        arm.pose.bones["Hips"].keyframe_insert("location", frame=frame)
        scene.frame_start, scene.frame_end = 0, frame
    scene.render.fps = 24
    scene.frame_set(0)
    bpy.ops.wm.save_as_mainfile(filepath=out, compress=True, relative_remap=False)
    return out


# Bones of the weights sheet, with where to look: the target joint, yaw, pitch, and distance; the
# eyelids' bones when the skeleton has them.
WEIGHT_VIEWS = (("UpperArm_R", "UpperArm_R", 10, 5, 1.0), ("UpperArm_R", "UpperArm_R", 150, 5, 1.0),
                ("Shoulder_R", "UpperArm_R", 10, 15, 1.0), ("Shoulder_R", "UpperArm_R", 150, 15, 1.0),
                ("UpperLeg_R", "UpperLeg_R", 160, 0, 1.0), ("UpperLeg_R", "UpperLeg_R", 30, 0, 1.0),
                ("Hips", "UpperLeg_R", 160, 0, 1.0), ("Hips", "UpperLeg_R", 30, 0, 1.0))
LID_WEIGHT_VIEWS = ((0, 0), (45, 5))


def _weight_colours(w):
    """Blue for no weight through yellow to red for full weight."""
    w = np.clip(w, 0.0, 1.0)
    return np.stack([np.clip(1.5 * w, 0, 1) ** 1.2, np.clip(1 - np.abs(w - 0.5) * 2, 0, 1) * 0.9,
                     np.clip(1 - 1.5 * w, 0, 1) * 0.8], 1)


def weights_sheet(arm, out, cam, lids=None):
    """How much a bone moves the skin, in the bind pose: blue none, yellow half, red all."""
    review.setup((420, 420))
    views = list(WEIGHT_VIEWS)
    if lids is not None:
        right = next((upper, lower) for eye, upper, lower in lids.eyes if eye.endswith("_R"))
        views += [(bone, "Eye_R", yaw, pitch, 0.16) for bone in right for yaw, pitch in LID_WEIGHT_VIEWS]
    cam.data.lens = 85
    poses.rest(arm)
    meshes = _shown(arm)
    mat = bpy.data.materials.new("RigWeights")
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    attr = nt.nodes.new("ShaderNodeAttribute")
    attr.attribute_name = "RigWeight"
    emit = nt.nodes.new("ShaderNodeEmission")
    output = nt.nodes.new("ShaderNodeOutputMaterial")
    nt.links.new(attr.outputs["Color"], emit.inputs["Color"])
    nt.links.new(emit.outputs["Emission"], output.inputs["Surface"])
    saved = {o.name: (list(o.data.materials), [p.material_index for p in o.data.polygons]) for o in meshes}
    for o in meshes:
        o.data.materials.clear()
        o.data.materials.append(mat)

    def show(bone):
        for o in meshes:
            group = o.vertex_groups.get(bone)
            w = np.zeros(len(o.data.vertices))
            if group is not None:
                for v in o.data.vertices:
                    w[v.index] = next((g.weight for g in v.groups if g.group == group.index), 0.0)
            colours = o.data.color_attributes.get("RigWeight") or o.data.color_attributes.new(
                "RigWeight", "FLOAT_COLOR", "POINT")
            colours.data.foreach_set("color", np.c_[_weight_colours(w), np.ones(len(w))].ravel().astype(np.float32))

    def cell(view):
        def draw(path):
            bone, target, yaw, pitch, distance = view
            show(bone)
            review.aim(cam, _joint(arm, target), yaw, pitch, distance)
            return _render(path + ".png")
        return draw

    try:
        return _grid([cell(v) for v in views], 4, out)
    finally:
        for o in meshes:
            mats, indices = saved[o.name]
            o.data.materials.clear()
            for m in mats:
                o.data.materials.append(m)
            for poly, i in zip(o.data.polygons, indices):
                poly.material_index = i
            o.data.color_attributes.remove(o.data.color_attributes["RigWeight"])
        bpy.data.materials.remove(mat)


SHEETS = {"skeleton": skeleton, "chains": chains_sheet, "shoulders": shoulders_sheet, "arms": arms_sheet,
          "hips": hips_sheet, "legs": legs_sheet, "poses": pose_sheet, "joints": joints_sheet, "hands": hands_sheet,
          "face": face_sheet, "weights": weights_sheet}


def render(path, label, skeleton_path, out_dir, prefix):
    """Renders the rig review sheets of one character to <out_dir>/<prefix>_rig_<sheet>.png."""
    lids = skeleton_definition.load(skeleton_path).eyelids
    bpy.ops.wm.open_mainfile(filepath=path)
    out_dir = os.path.abspath(out_dir)
    os.makedirs(out_dir, exist_ok=True)
    arm = bpy.data.objects[label]
    _show_whole_body(arm)
    cam = review.add_camera(lens=50)
    written = []
    for sheet, draw in SHEETS.items():
        extra = {"lids": lids} if sheet in ("face", "weights") else {}
        written.append(draw(arm, os.path.join(out_dir, f"{prefix}_rig_{sheet}.png"), cam, **extra))
    poses.rest(arm)
    return written
