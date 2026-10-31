"""Test poses for the rig review: each pose turns bones about their own axes. The bone axes of the
base characters follow one convention (see human_base.rig.joints): every limb, digit and spine bone bends
forward, or toward the palm, about its X axis; the arms raise sideways about Z, and the head and
the spine twist about Y. A bone name ending in _s means both sides: the left side takes the same
X turn and the opposite Y and Z turns, so the pose stays mirror symmetric. Both eyes share one set
of axes (looking forward, Z up), so a gaze to one side names each eye with the same turns."""

import math

import bpy
from mathutils import Quaternion, Vector

FINGERS = ("Index", "Middle", "Ring", "Little")
# A pose with this key lowers the hips until the ankles are back at their height in the rest pose,
# so bent legs keep the feet on the ground.
PLANT_FEET = "plant_feet"


def _fist(curl=1.0):
    pose = {}
    for finger in FINGERS:
        pose[f"{finger}Proximal_s"] = (("X", -75 * curl),)
        pose[f"{finger}Intermediate_s"] = (("X", -95 * curl),)
        pose[f"{finger}Distal_s"] = (("X", -60 * curl),)
    # The thumb lies over the middle segments of the index and middle fingers.
    pose["ThumbProximal_s"] = (("X", -18 * curl), ("Z", 10 * curl), ("Y", -5 * curl))
    pose["ThumbIntermediate_s"] = (("X", -20 * curl),)
    pose["ThumbDistal_s"] = (("X", -35 * curl),)
    return pose


# Name, description and the turns of each bone in degrees, applied in order.
POSES = (
    ("rest", "Bind pose (A-pose)", {}),
    ("t_pose", "Arms raised to the side", {"UpperArm_s": (("Z", -62),), "Shoulder_s": (("X", 6),)}),
    ("arms_up", "Arms overhead", {"UpperArm_s": (("Z", -125),), "Shoulder_s": (("X", 22),),
                                  "LowerArm_s": (("X", 15),)}),
    ("arms_forward", "Arms forward, elbows bent", {"UpperArm_s": (("X", 80), ("Z", -10)),
                                                   "LowerArm_s": (("X", 70),)}),
    ("elbows", "Elbows fully bent", {"UpperArm_s": (("X", 25),), "LowerArm_s": (("X", 125),),
                                     "Hand_s": (("X", -20),)}),
    ("stride", "Long stride", {"UpperLeg_R": (("X", 55),), "LowerLeg_R": (("X", -60),),
                               "UpperLeg_L": (("X", -30),), "LowerLeg_L": (("X", -35),),
                               "Foot_L": (("X", 25),), "Toes_L": (("X", 30),)}),
    # Sitting tilts the pelvis back and rounds the lower back, so the hips bend less than the
    # thighs turn.
    ("sit", "Sitting on a chair, hands on the thighs",
     {PLANT_FEET: True, "Hips": (("X", -18),), "Spine": (("X", 12),), "Chest": (("X", 8),), "UpperChest": (("X", 4),),
      "UpperLeg_s": (("X", 70), ("Z", 4)), "LowerLeg_s": (("X", -92),), "Foot_s": (("X", 4),),
      "UpperArm_s": (("X", 18), ("Z", 4)), "LowerArm_s": (("X", 32),), "Hand_s": (("X", -20),)}),
    ("fist", "Hands closed to fists", _fist()),
    ("face", "Jaw open, eyes to the right", {"Jaw": (("X", -14),), "Eye_R": (("Z", -25),), "Eye_L": (("Z", -25),)}),
    ("torso", "Bend and twist, head turned", {"Spine": (("X", 15),), "Chest": (("X", 10), ("Y", 12)),
                                              "UpperChest": (("Y", 12),), "Neck": (("Y", -15),),
                                              "Head": (("Y", -20), ("X", -10))}),
)


def _turns(name, turns, side):
    q = Quaternion()
    for axis, degrees in turns:
        sign = -1.0 if side == "L" and axis in ("Y", "Z") else 1.0
        q = q @ Quaternion(Vector({"X": (1, 0, 0), "Y": (0, 1, 0), "Z": (0, 0, 1)}[axis]),
                           math.radians(degrees * sign))
    return q


def apply(arm, pose):
    """Sets the armature to one pose; bones the pose does not name stay at rest."""
    for pb in arm.pose.bones:
        pb.rotation_mode = "QUATERNION"
        pb.rotation_quaternion = Quaternion()
        pb.location = (0.0, 0.0, 0.0)
    for name, turns in pose.items():
        if name == PLANT_FEET:
            continue
        if name.endswith("_s"):
            for side in ("R", "L"):
                arm.pose.bones[f"{name[:-2]}_{side}"].rotation_quaternion = _turns(name, turns, side)
        else:
            arm.pose.bones[name].rotation_quaternion = _turns(name, turns, None)
    bpy.context.view_layer.update()
    if pose.get(PLANT_FEET):
        feet = [f"Foot_{side}" for side in ("R", "L")]
        rest_z = sum(arm.data.bones[f].head_local.z for f in feet) / 2
        posed_z = sum((arm.matrix_world @ arm.pose.bones[f].head).z for f in feet) / 2
        hips = arm.pose.bones["Hips"]
        hips.location = hips.bone.matrix_local.to_3x3().inverted() @ Vector((0.0, 0.0, rest_z - posed_z))
        bpy.context.view_layer.update()


def rest(arm):
    apply(arm, {})
