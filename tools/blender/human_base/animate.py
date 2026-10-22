"""The gait model of the base characters: Idle, Walk and Jog made by script on the rig of an
animation library, as starting points that are then edited by hand. Each clip is one action of the
armature, loops seamlessly, and stays in place: the game moves the
character, so during a step the foot on the ground slides back at exactly the clip's speed and stays
put when the game moves the character at that speed. The legs are solved from the feet: every frame
places both ankles and turns the thigh and the shin to reach them, with the knee over the toes.

Bone turns follow the axis convention of poses.py: limbs bend forward or toward the palm about X,
the arms raise sideways about Z, and the spine and the head twist about Y. Turns given for the right
side are mirrored onto the left."""

import math
from dataclasses import dataclass

import bpy
from bpy_extras import anim_utils
from mathutils import Matrix, Quaternion, Vector

from . import poses

FPS = 30
# The game's speeds: the jog is the full movement speed, the walk a heavy load.
WALK_SPEED = 1.4
JOG_SPEED = 5.0
IDLE_FRAMES = 120
# The clips and the speed the game moves the character at while each plays.
CLIPS = {"Idle": 0.0, "Walk": WALK_SPEED, "Jog": JOG_SPEED}
SIDES = (("R", 1.0), ("L", -1.0))
SEGMENTS = ("Proximal", "Intermediate", "Distal")


@dataclass(frozen=True)
class Gait:
    speed: float          # m/s: the foot on the ground moves back at this speed
    step: float           # step length over leg length (hip joint to ankle)
    stance: float         # share of a cycle each foot is on the ground
    heel_off: float       # share of the stance when the heel lifts
    land: float           # where the ankle lands in front of the hip joint, over leg length
    hip_height: float     # highest height of the hip joints above the ankles, over leg length
    bob: float            # m; up at mid stance when positive (walk), down when negative (jog)
    lift: float           # m, swing height of the ankle
    lift_peak: float      # share of the swing at the highest point
    strike_pitch: float   # degrees the foot turns up at the heel strike
    push_pitch: float     # degrees the foot turns down at the toe off
    pelvis_yaw: float     # degrees: the hip of the leg in front turns forward
    pelvis_drop: float    # degrees: the hip of the swinging leg drops
    pelvis_tilt: float    # degrees the pelvis tips forward and back twice a cycle
    sway: float           # m: the hips shift over the foot on the ground
    lean: float           # degrees of forward lean of the spine
    arm_swing: float      # degrees forward and back
    arm_angle: float      # degrees between the upper arm and the vertical, seen from the front
    elbow: float          # degrees of elbow bend
    elbow_swing: float    # degrees more bend when the arm is in front
    arm_cross: float      # degrees the arm swings in toward the body when it is in front
    wrist_swing: float    # degrees the hand trails the swing of the forearm
    finger_swing: float   # how much more the fingers curl when the arm swings back
    fist: float           # how far the relaxed fingers close toward a fist: 0 relaxed, 1 fist
    wrist: float          # degrees the hands turn back from the bind pose, where they point forward
    track: float          # how far each ankle is from the centre line, over leg length
    counter_twist: float  # degrees the chest turns against the pelvis
    lift_off: float       # share of the body's speed the foot keeps moving back with as it lifts
    landing: float        # share of the body's speed the foot moves back with as it lands; at 1 it
                          # touches down without moving over the ground


WALK = Gait(speed=WALK_SPEED, step=0.85, stance=0.60, heel_off=0.40, land=0.40, hip_height=0.95, bob=0.015,
            lift=0.07, lift_peak=0.4, strike_pitch=18.0, push_pitch=-55.0, pelvis_yaw=8.0, pelvis_drop=4.5,
            pelvis_tilt=2.0, sway=0.02, lean=2.0, arm_swing=20.0, arm_angle=7.0, elbow=14.0, elbow_swing=16.0,
            arm_cross=6.0, wrist_swing=10.0, finger_swing=0.15, fist=0.0, wrist=22.0, track=0.10,
            counter_twist=12.0, lift_off=0.2, landing=1.0)
JOG = Gait(speed=JOG_SPEED, step=2.0, stance=0.26, heel_off=0.40, land=0.30, hip_height=0.92, bob=-0.03,
           lift=0.20, lift_peak=0.45, strike_pitch=6.0, push_pitch=-60.0, pelvis_yaw=14.0, pelvis_drop=4.0,
           pelvis_tilt=3.0, sway=0.012, lean=9.0, arm_swing=40.0, arm_angle=10.0, elbow=80.0, elbow_swing=20.0,
           arm_cross=12.0, wrist_swing=6.0, finger_swing=0.1, fist=0.5, wrist=12.0, track=0.065,
           counter_twist=24.0, lift_off=0.25, landing=0.45)
# Overlapping action: parts further from the pelvis follow its motion later, by these shares of a
# cycle: the chest, each arm, its forearm and its hand.
CHEST_LAG = 0.03
ARM_LAG = 0.02
FOREARM_LAG = 0.06
HAND_LAG = 0.11
# Standing, each ankle is this far from the centre line, over leg length: about the width of the hips.
IDLE_TRACK = 0.145
IDLE_ARM_ANGLE = 5.0
IDLE_WRIST = 26.0
# A relaxed hand: each finger curls a little more than the one before it, toward the little finger,
# and the thumb rests along the index finger. Degrees per segment.
RELAXED_FINGERS = {"Index": (10, 16, 8), "Middle": (14, 20, 10), "Ring": (18, 24, 12), "Little": (22, 28, 14)}
RELAXED_THUMB = (8, 12, 10)
# The hips sink below the reach of the legs by a soft minimum of this width, so the knees never lock.
REACH = 0.985
SOFT_MIN = 0.005
# The swinging leg starts to hold the hips down from this share of its swing.
APPROACH = 0.5


def _ease(x):
    x = min(max(x, 0.0), 1.0)
    return x * x * x * (x * (6 * x - 15) + 10)


def _lift(u, peak):
    """0 at both ends and 1 at the peak: a smooth lift off and a straighter way down to the strike."""
    if u <= peak:
        return math.sin(0.5 * math.pi * u / peak) ** 2
    return math.sin(0.5 * math.pi * (1 - u) / (1 - peak))


def _hermite(y0, y1, m0, m1, u):
    """Cubic from y0 to y1 with the slopes m0 and m1 at the ends."""
    u2, u3 = u * u, u * u * u
    return (2 * u3 - 3 * u2 + 1) * y0 + (u3 - 2 * u2 + u) * m0 + (3 * u2 - 2 * u3) * y1 + (u3 - u2) * m1


def _soft_min(values, width):
    low = min(values)
    return low - width * math.log(sum(math.exp(-(v - low) / width) for v in values))


def _turn(bone, turns):
    """Local rotation of a bone from turns given for the right side or the centre; a left bone
    takes the mirror turns. The eyes share one set of axes, so they turn alike."""
    side = "L" if bone.endswith("_L") and not bone.startswith("Eye_") else None
    return poses._turns(bone, turns, side)


class Rig:
    """The armature's rest frames, with forward kinematics in armature space (the armature sits at
    the origin without rotation)."""

    def __init__(self, arm):
        self.arm = arm
        self.bones = arm.data.bones
        self.rest = {b.name: b.matrix_local.copy() for b in self.bones}
        self.hip_joint = {s: self.rest[f"UpperLeg_{s}"].translation.copy() for s, _x in SIDES}
        self.ankle = {s: self.rest[f"Foot_{s}"].translation.copy() for s, _x in SIDES}
        self.ball = {s: self.rest[f"Toes_{s}"].translation - self.ankle[s] for s, _x in SIDES}
        self.thigh = self.bones["UpperLeg_R"].length
        self.shin = self.bones["LowerLeg_R"].length
        self.leg = (self.hip_joint["R"] - self.ankle["R"]).length
        # The upper arm's angle from the vertical in the bind pose, seen from the front.
        arm = self.rest["LowerArm_R"].translation - self.rest["UpperArm_R"].translation
        self.arm_angle = math.degrees(math.atan2(abs(arm.x), -arm.z))

    def child_base(self, name, parent_pose):
        bone = self.bones[name]
        return parent_pose @ self.rest[bone.parent.name].inverted() @ self.rest[name]

    def local(self, name, parent_pose, world_rotation):
        """The local rotation that gives the bone the world (armature space) rotation."""
        return (self.child_base(name, parent_pose).to_3x3().inverted() @ world_rotation).to_quaternion()

    def pose(self, name, parent_pose, rotation):
        return self.child_base(name, parent_pose) @ rotation.to_matrix().to_4x4()

    def hips(self, rotation, offset):
        """Local rotation and location of the hips bone, and its pose matrix, from a world rotation
        about the hips and a world offset."""
        rest = self.rest["Hips"]
        r = rest.to_3x3()
        pose = Matrix.Translation(rest.translation + offset) @ (rotation @ r).to_4x4()
        return (r.inverted() @ rotation @ r).to_quaternion(), r.inverted() @ offset, pose


def _leg_ik(rig, hip, ankle, knee_dir):
    """World rotations of the thigh and the shin that bring the ankle to the target with the knee
    toward knee_dir."""
    a, b = rig.thigh, rig.shin
    to = ankle - hip
    d = min(to.length, (a + b) * 0.9995)
    direction = to.normalized()
    hip_angle = math.acos(max(-1.0, min(1.0, (a * a + d * d - b * b) / (2 * a * d))))
    hinge = direction.cross(knee_dir).normalized()
    toward_knee = hinge.cross(direction).normalized()
    knee = hip + (direction * math.cos(hip_angle) + toward_knee * math.sin(hip_angle)) * a
    reached = hip + direction * d
    frames = []
    for y in ((knee - hip).normalized(), (reached - knee).normalized()):
        z = hinge.cross(y).normalized()  # the front of the leg, as the bone's rest Z
        frames.append(Matrix((y.cross(z), y, z)).transposed())
    return frames


def _foot_rotation(rig, side, pitch):
    """World rotation of the foot: its rest rotation turned toes up (positive pitch)."""
    rest = rig.rest[f"Foot_{side}"].to_3x3()
    return Quaternion(Vector((1.0, 0.0, 0.0)), math.radians(pitch)).to_matrix() @ rest


def _foot(rig, side, gait, cycle, phase):
    """Ankle target, foot pitch, toe bend, how firmly the leg must reach its footprint, and the
    footprint (where the ankle is on the ground now, or will be at the next strike), for one foot
    at a phase of its own cycle, 0 at its heel strike."""
    stance_dist = gait.speed * gait.stance * cycle
    front = gait.land * rig.leg
    p = phase % 1.0
    rest_ankle = rig.ankle[side]
    if p < gait.stance:
        s = p / gait.stance
        y = front - s * stance_dist
        lift = 0.0
        if s < 0.12:
            pitch = gait.strike_pitch * (1 - _ease(s / 0.12))
        elif s < gait.heel_off:
            pitch = 0.0
        else:
            pitch = gait.push_pitch * _ease((s - gait.heel_off) / (1 - gait.heel_off))
        toes = -min(pitch, 0.0)  # the toes stay flat on the ground while the heel lifts
    else:
        u = (p - gait.stance) / (1 - gait.stance)
        # The foot leaves the ground swinging forward at once and slows down for the landing, where
        # it moves back with the body's speed (or a share of it), so it touches down without
        # skidding.
        swing_time = (1 - gait.stance) * cycle
        y = _hermite(front - stance_dist, front, -gait.lift_off * gait.speed * swing_time,
                     -gait.landing * gait.speed * swing_time, u)
        lift = gait.lift * _lift(u, gait.lift_peak)
        pitch = gait.push_pitch + (gait.strike_pitch - gait.push_pitch) * _ease(min(1.0, u * 1.3))
        toes = -gait.push_pitch * (1 - _ease(min(1.0, u * 2.0)))
    # The foot turns about its heel when turned up and about its ball when turned down.
    pivot = Vector((0.0, -0.06 * rig.leg, -rest_ankle.z)) if pitch > 0 else rig.ball[side]
    turn = Matrix.Rotation(math.radians(pitch), 3, "X")
    track = math.copysign(gait.track * rig.leg, rest_ankle.x)
    ankle = Vector((track, rest_ankle.y + y, rest_ankle.z + lift)) + (pivot - turn @ pivot)
    # How firmly the leg must reach its foot: on the ground fully, and while it swings toward the
    # next landing more and more, so the hips are low enough when the heel strikes.
    contact = 1.0 if p < gait.stance else _ease((u - APPROACH) / (1 - APPROACH))
    landing = ankle if p < gait.stance else _foot(rig, side, gait, cycle, 0.0)[0]
    return ankle, pitch, toes, contact, landing


def _legs(rig, local, hips_pose, feet):
    for side, _x in SIDES:
        ankle, pitch, toes, _contact, _landing = feet[side]
        joint = (hips_pose @ rig.rest["Hips"].inverted() @ rig.rest[f"UpperLeg_{side}"]).translation
        foot = _foot_rotation(rig, side, pitch)
        thigh, shin = _leg_ik(rig, joint, ankle, foot.col[1].normalized())
        local[f"UpperLeg_{side}"] = rig.local(f"UpperLeg_{side}", hips_pose, thigh)
        thigh_pose = rig.pose(f"UpperLeg_{side}", hips_pose, local[f"UpperLeg_{side}"])
        local[f"LowerLeg_{side}"] = rig.local(f"LowerLeg_{side}", thigh_pose, shin)
        shin_pose = rig.pose(f"LowerLeg_{side}", thigh_pose, local[f"LowerLeg_{side}"])
        local[f"Foot_{side}"] = rig.local(f"Foot_{side}", shin_pose, foot)
        local[f"Toes_{side}"] = _turn(f"Toes_{side}", (("X", toes),))


def _upper(local, turns):
    for bone, t in turns.items():
        local[bone] = _turn(bone, t)


def _hands(local, fist, curl=None):
    """Both hands relaxed, or closed that far toward a fist, each closing a share more by curl."""
    closed = poses._fist(1.0)
    relaxed = {f"{finger}{segment}_s": (("X", -angle),) for finger, angles in RELAXED_FINGERS.items()
               for segment, angle in zip(SEGMENTS, angles)}
    relaxed.update({f"Thumb{segment}_s": (("X", -angle),) for segment, angle in zip(SEGMENTS, RELAXED_THUMB)})
    for bone in relaxed:
        turns = {}
        for axis, angle in relaxed[bone]:
            turns[axis] = turns.get(axis, 0.0) + angle * (1 - fist)
        for axis, angle in closed[bone]:
            turns[axis] = turns.get(axis, 0.0) + angle * fist
        for side, _x in SIDES:
            more = 1.0 + (curl or {}).get(side, 0.0)
            local[f"{bone[:-2]}_{side}"] = _turn(f"{bone[:-2]}_{side}", tuple((a, v * more) for a, v in turns.items()))


def gait_frames(rig, gait):
    """Frames of one cycle: two steps at the gait's speed."""
    return max(8, round(2.0 * gait.step * rig.leg / gait.speed * FPS))


def gait_frame(rig, gait, frames, frame):
    """Local rotations and the hips' location at one frame of a gait cycle: the right heel strikes
    at frame 0, the left halfway."""
    phase = frame / frames
    cycle = frames / FPS
    feet = {"R": _foot(rig, "R", gait, cycle, phase), "L": _foot(rig, "L", gait, cycle, phase + 0.5)}
    right_front = math.cos(2 * math.pi * phase)  # 1 when the right leg is in front
    right_stance = math.sin(2 * math.pi * phase)  # 1 at the right foot's mid stance
    yaw = gait.pelvis_yaw * right_front  # the hip of the leg in front turns forward with it
    drop = -gait.pelvis_drop * right_stance  # the hip over the swinging leg drops
    tilt = gait.pelvis_tilt * math.cos(4 * math.pi * (phase - 0.75 * gait.stance))  # forward at the push off
    rotation = (Matrix.Rotation(math.radians(yaw), 3, "Z") @ Matrix.Rotation(math.radians(drop), 3, "Y")
                @ Matrix.Rotation(math.radians(-tilt), 3, "X"))
    sway = gait.sway * right_stance

    # The hips rise and fall with the steps, and sink further where a leg could not reach its foot.
    hips_rest = rig.rest["Hips"].translation
    mid_stance = math.cos(4 * math.pi * (phase - gait.stance / 2))  # 1 at the mid stance of either foot
    bob = abs(gait.bob) * ((mid_stance if gait.bob > 0 else -mid_stance) - 1.0) / 2.0
    wanted = rig.ankle["R"].z + gait.hip_height * rig.leg - rig.hip_joint["R"].z + bob
    limits = [wanted]
    reach = (rig.thigh + rig.shin) * REACH
    for side, _x in SIDES:
        joint = hips_rest + rotation @ (rig.hip_joint[side] - hips_rest) + Vector((sway, 0.0, 0.0))
        _ankle, _pitch, _toes, contact, ankle = feet[side]
        flat = (joint.x - ankle.x) ** 2 + (joint.y - ankle.y) ** 2
        # A leg early in its swing bends its knee and does not hold the hips down; late in the
        # swing it holds them low enough to reach its next footprint.
        limits.append(ankle.z + math.sqrt(max(reach * reach - flat, 0.0)) - joint.z + (1.0 - contact) * 0.3)
    offset = Vector((sway, 0.0, _soft_min(limits, SOFT_MIN)))
    hips_q, hips_loc, hips_pose = rig.hips(rotation, offset)
    local = {"Hips": hips_q}

    # The shoulders turn against the pelvis a little later, and the head keeps looking ahead. The
    # lower back takes up the pelvis' tilt, so the chest and the head do not rock back and forth.
    twist = gait.counter_twist * math.cos(2 * math.pi * (phase - CHEST_LAG))
    head_twist = yaw - twist
    turns = {
        "Spine": (("X", gait.lean * 0.4 - tilt * 0.8), ("Y", -twist * 0.3), ("Z", -drop * 0.6)),
        "Chest": (("X", gait.lean * 0.4 - tilt * 0.2), ("Y", -twist * 0.4), ("Z", -drop * 0.3)),
        "UpperChest": (("X", gait.lean * 0.2), ("Y", -twist * 0.3)),
        "Neck": (("X", -gait.lean * 0.5), ("Y", -head_twist * 0.5)),
        "Head": (("X", -gait.lean * 0.4), ("Y", -head_twist * 0.5)),
    }
    curl = {}
    for side, sign in SIDES:
        # Each arm swings with the opposite leg; the forearm and the hand follow later.
        arm = -sign * math.cos(2 * math.pi * (phase - ARM_LAG))
        forearm = -sign * math.cos(2 * math.pi * (phase - ARM_LAG - FOREARM_LAG))
        hand = -sign * math.cos(2 * math.pi * (phase - ARM_LAG - HAND_LAG))
        turns[f"Shoulder_{side}"] = (("X", 2.0), ("Z", 3.0 * arm))
        turns[f"UpperArm_{side}"] = (("Z", rig.arm_angle - gait.arm_angle + gait.arm_cross * max(arm, 0.0)),
                                     ("X", gait.arm_swing * arm))
        turns[f"LowerArm_{side}"] = (("X", gait.elbow + gait.elbow_swing * (forearm + 1.0) / 2.0),)
        turns[f"Hand_{side}"] = (("Z", -gait.wrist - gait.wrist_swing * hand), ("X", -6.0))
        curl[side] = gait.finger_swing * (1.0 - hand) / 2.0
    _upper(local, turns)
    _hands(local, gait.fist, curl)
    _legs(rig, local, hips_pose, feet)
    return local, hips_loc


def idle_frame(rig, frames, frame):
    """A relaxed stand with the feet planted: breathing, a slow weight shift and a look around."""
    t = frame / frames
    breath = math.sin(2 * math.pi * t * 3)
    shift = math.sin(2 * math.pi * t)
    look = math.sin(2 * math.pi * t + 1.0)
    rotation = Matrix.Rotation(math.radians(-1.5 * shift), 3, "Y")
    offset = Vector((0.012 * shift, 0.0, -0.015 + 0.002 * breath))
    hips_q, hips_loc, hips_pose = rig.hips(rotation, offset)
    local = {"Hips": hips_q}
    gaze = math.sin(2 * math.pi * t * 2)
    turns = {
        "Spine": (("X", 1.0), ("Z", 1.0 * shift)),
        "Chest": (("X", 0.8 * breath),),
        "UpperChest": (("X", 0.6 * breath),),
        "Neck": (("Y", 3.0 * look), ("Z", -0.8 * shift)),
        "Head": (("X", 1.5 * math.sin(4 * math.pi * t + 0.5)), ("Y", 4.0 * look)),
        "Eye_R": (("Z", -8.0 * gaze),),
        "Eye_L": (("Z", -8.0 * gaze),),
    }
    for side, _x in SIDES:
        turns[f"Shoulder_{side}"] = (("X", 1.5 * breath),)
        turns[f"UpperArm_{side}"] = (("Z", rig.arm_angle - IDLE_ARM_ANGLE), ("X", -2.0 + 1.0 * breath))
        turns[f"LowerArm_{side}"] = (("X", 10.0 + 1.5 * breath),)
        turns[f"Hand_{side}"] = (("Z", -IDLE_WRIST), ("X", -4.0))
    _upper(local, turns)
    _hands(local, 0.0)
    feet = {}
    for side, _x in SIDES:
        ankle = rig.ankle[side].copy()
        ankle.x = math.copysign(IDLE_TRACK * rig.leg, ankle.x)
        feet[side] = (ankle, 0.0, 0.0, 1.0, ankle)
    _legs(rig, local, hips_pose, feet)
    return local, hips_loc


def bake(arm, name, frames, frame_fn):
    """Writes one looping clip as an action: every frame keyed, and the last frame the first."""
    keys = [frame_fn(f) for f in range(frames)]
    action = bpy.data.actions.new(name)
    action.use_fake_user = True
    arm.animation_data_create()
    arm.animation_data.action = action
    previous = {}
    for f in range(frames + 1):
        local, hips_loc = keys[f % frames]
        for pb in arm.pose.bones:
            q = local.get(pb.name, Quaternion())
            if pb.name in previous and previous[pb.name].dot(q) < 0:
                q = -q
            if f == frames:
                if previous[pb.name].dot(first[pb.name]) < 0:
                    raise RuntimeError(f"{name}: {pb.name} does not come back to its first rotation")
                q = first[pb.name]
            pb.rotation_quaternion = q
            pb.keyframe_insert("rotation_quaternion", frame=f)
            previous[pb.name] = q
        if f == 0:
            first = dict(previous)
        hips = arm.pose.bones["Hips"]
        hips.location = hips_loc
        hips.keyframe_insert("location", frame=f)
    channelbag = anim_utils.action_get_channelbag_for_slot(action, action.slots[0])
    for fc in channelbag.fcurves:
        for key in fc.keyframe_points:
            key.interpolation = "LINEAR"
    action.frame_range = (0, frames)
    action.use_frame_range = True
    action["loop"] = True
    arm.animation_data.action = None
    for pb in arm.pose.bones:
        pb.rotation_quaternion = Quaternion()
        pb.location = (0.0, 0.0, 0.0)
    return action


def generate(path, label, names):
    """Adds the named clips of the gait model (Idle, Walk, Jog) to an animation library on its
    armature label, as starting points to edit by hand, and saves it. A clip the library already
    has is kept as it is, so handmade keys are never replaced."""
    unknown = [name for name in names if name not in CLIPS]
    if unknown:
        raise RuntimeError(f"The gait model makes the clips {list(CLIPS)}, not {unknown}.")
    bpy.ops.wm.open_mainfile(filepath=path)
    arm = bpy.data.objects[label]
    arm.animation_data_create()
    active = arm.animation_data.action
    rig = Rig(arm)
    scene = bpy.context.scene
    scene.render.fps, scene.render.fps_base = FPS, 1.0
    made = []
    for name in names:
        if name in bpy.data.actions:
            print(f"KEPT {name}: the library has the clip already")
            continue
        if name == "Idle":
            bake(arm, name, IDLE_FRAMES, lambda f: idle_frame(rig, IDLE_FRAMES, f))
        else:
            gait = WALK if name == "Walk" else JOG
            frames = gait_frames(rig, gait)
            bake(arm, name, frames, lambda f, gait=gait, frames=frames: gait_frame(rig, gait, frames, f))
        made.append(name)
    if not made:
        return
    # The FBX export bakes every action as a take only for an object that has animation data.
    arm.animation_data.action = active
    scene.frame_start, scene.frame_end = 0, max(int(a.frame_range[1]) for a in bpy.data.actions)
    bpy.ops.wm.save_as_mainfile(filepath=path, compress=True, relative_remap=False)
    print(f"GENERATED {label}: {made} at {FPS} fps -> {path}")
