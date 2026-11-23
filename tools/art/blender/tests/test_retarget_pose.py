"""The pose of a clip on a body that is not its library's rig, as the game retargets it: a body equal
to the rig gets the rig's pose exactly, a scaled one the scaled pose with the centre of mass where the
scaled rig's lies, a body whose reference pose differs keeps the turn from its own reference, across
the swing between the reference directions but on the arms, the avatar skeleton of a .fbx.meta read in
Blender's axes and held to the armature, and on recorded data the model held to the game's poses within
the budget. Runs without Blender: python -m unittest discover -s tools/art/blender/tests. It needs
numpy. See docs/ART_PIPELINE.md, Game Views."""

import json
import math
import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))

try:
    import numpy as np
except ImportError:
    raise unittest.SkipTest("numpy is not installed; see requirements.txt") from None

from art_pipeline import retarget_pose  # noqa: E402

FIXTURE = pathlib.Path(__file__).resolve().parent / "fixtures" / "retarget_game_poses.json"
# The budget of a pose: the mean distance of the bone heads from the game's, in millimeters, which
# tools/art/Capture-GameViews.ps1 reports against too.
BUDGET_MM = 10.0

# A plain standing figure of the main human bones, in Blender's armature space (x right, y front, z up):
# name, parent, Unity human name, head.
FIGURE = (
    ("Hips", None, "Hips", (0.0, 0.0, 1.0)),
    ("Spine", "Hips", "Spine", (0.0, 0.0, 1.1)),
    ("Chest", "Spine", "Chest", (0.0, 0.01, 1.25)),
    ("UpperChest", "Chest", "UpperChest", (0.0, 0.0, 1.4)),
    ("Neck", "UpperChest", "Neck", (0.0, -0.01, 1.55)),
    ("Head", "Neck", "Head", (0.0, 0.0, 1.65)),
    ("Shoulder_L", "UpperChest", "LeftShoulder", (0.03, 0.0, 1.5)),
    ("UpperArm_L", "Shoulder_L", "LeftUpperArm", (0.18, 0.0, 1.5)),
    ("LowerArm_L", "UpperArm_L", "LeftLowerArm", (0.38, 0.0, 1.3)),
    ("Hand_L", "LowerArm_L", "LeftHand", (0.55, 0.02, 1.13)),
    ("Shoulder_R", "UpperChest", "RightShoulder", (-0.03, 0.0, 1.5)),
    ("UpperArm_R", "Shoulder_R", "RightUpperArm", (-0.18, 0.0, 1.5)),
    ("LowerArm_R", "UpperArm_R", "RightLowerArm", (-0.38, 0.0, 1.3)),
    ("Hand_R", "LowerArm_R", "RightHand", (-0.55, 0.02, 1.13)),
    ("UpperLeg_L", "Hips", "LeftUpperLeg", (0.1, 0.0, 0.95)),
    ("LowerLeg_L", "UpperLeg_L", "LeftLowerLeg", (0.1, 0.01, 0.5)),
    ("Foot_L", "LowerLeg_L", "LeftFoot", (0.1, 0.0, 0.08)),
    ("Toes_L", "Foot_L", "LeftToes", (0.1, 0.12, 0.0)),
    ("UpperLeg_R", "Hips", "RightUpperLeg", (-0.1, 0.0, 0.95)),
    ("LowerLeg_R", "UpperLeg_R", "RightLowerLeg", (-0.1, 0.01, 0.5)),
    ("Foot_R", "LowerLeg_R", "RightFoot", (-0.1, 0.0, 0.08)),
    ("Toes_R", "Foot_R", "RightToes", (-0.1, 0.12, 0.0)),
)
PARENTS = {name: parent for name, parent, _human, _head in FIGURE}
HUMAN = {name: human for name, _parent, human, _head in FIGURE}
ORDER = retarget_pose.parents_first(PARENTS)


def turn(axis, degrees: float) -> np.ndarray:
    """A right-handed turn about an axis."""
    axis = np.asarray(axis, float) / np.linalg.norm(axis)
    cross = np.array([[0.0, -axis[2], axis[1]], [axis[2], 0.0, -axis[0]], [-axis[1], axis[0], 0.0]])
    angle = math.radians(degrees)
    return np.eye(3) + math.sin(angle) * cross + (1.0 - math.cos(angle)) * cross @ cross


def toward(direction) -> np.ndarray:
    """A bone's rest turn whose Y axis points along direction."""
    y = np.asarray(direction, float) / np.linalg.norm(direction)
    other = np.array([1.0, 0.0, 0.0]) if abs(y[0]) < 0.9 else np.array([0.0, 0.0, 1.0])
    z = np.cross(other, y)
    z /= np.linalg.norm(z)
    return np.stack([np.cross(y, z), y, z], axis=1)


def figure_rest(scale: float = 1.0) -> dict:
    """The figure at rest, every bone pointing at its first child, an end bone on along its parent."""
    heads = {name: np.array(head) * scale for name, _parent, _human, head in FIGURE}
    children = {}
    for name, parent, _human, _head in FIGURE:
        children.setdefault(parent, []).append(name)
    rest = {}
    for name in ORDER:
        if name in children:
            direction = heads[children[name][0]] - heads[name]
        else:
            direction = heads[name] - heads[PARENTS[name]]
        matrix = np.eye(4)
        matrix[:3, :3] = toward(direction)
        matrix[:3, 3] = heads[name]
        rest[name] = matrix
    return rest


def rotations_of(rest: dict) -> dict:
    return {name: matrix[:3, :3].copy() for name, matrix in rest.items()}


def pose_of(rest: dict, local: dict, root_shift=(0.0, 0.0, 0.0)) -> dict:
    """The armature-space pose of a figure whose bones turn by the given world turns relative to their
    parents', each carried by its parent, the hips moved by root_shift."""
    world = {}
    for name in ORDER:
        parent = PARENTS[name]
        own = local.get(name, np.eye(3))
        world[name] = own if parent is None else world[parent] @ own
    rotations = {name: world[name] @ rest[name][:3, :3] for name in ORDER}
    return retarget_pose.forward(ORDER, PARENTS, rest, rotations, rest["Hips"][:3, 3] + np.asarray(root_shift))


CLIP = {"Hips": turn((0, 0, 1), 20.0), "Spine": turn((1, 0, 0), 15.0), "UpperArm_L": turn((0, 1, 0), -50.0),
        "LowerArm_L": turn((1, 0, 0), 40.0), "UpperLeg_R": turn((1, 0, 0), 35.0), "LowerLeg_R": turn((1, 0, 0), -60.0),
        "Head": turn((0, 0, 1), 30.0), "Hand_R": turn((0, 1, 0), 25.0)}


class FigureTests(unittest.TestCase):
    def test_a_body_equal_to_the_rig_gets_the_rigs_pose_exactly(self):
        rest = figure_rest()
        model = retarget_pose.Retarget(PARENTS, HUMAN, rest, rotations_of(rest), rest, rotations_of(rest))
        rig = pose_of(rest, CLIP, (0.02, 0.05, -0.1))
        body = model.pose(rig)
        self.assertAlmostEqual(model.scale, 1.0, places=12)
        for name in ORDER:
            with self.subTest(bone=name):
                np.testing.assert_allclose(body[name], rig[name], atol=1e-12)

    def test_a_scaled_body_gets_the_scaled_pose_with_the_centre_of_mass_where_the_scaled_rigs_lies(self):
        rig_rest, body_rest = figure_rest(), figure_rest(0.9)
        model = retarget_pose.Retarget(PARENTS, HUMAN, body_rest, rotations_of(body_rest), rig_rest, rotations_of(rig_rest))
        rig = pose_of(rig_rest, CLIP, (0.02, 0.05, -0.1))
        body = model.pose(rig)
        self.assertAlmostEqual(model.scale, 0.9, places=12)
        heads = {name: matrix[:3, 3] for name, matrix in body.items()}
        rig_heads = {name: matrix[:3, 3] for name, matrix in rig.items()}
        np.testing.assert_allclose(model.centre_of_mass(heads), 0.9 * model.centre_of_mass(rig_heads), atol=1e-12)
        for name in ORDER:
            with self.subTest(bone=name):
                np.testing.assert_allclose(heads[name], 0.9 * rig_heads[name], atol=1e-12)
                np.testing.assert_allclose(body[name][:3, :3], rig[name][:3, :3], atol=1e-12)

    def test_a_body_keeps_the_turn_from_its_own_reference_pose(self):
        # The body's reference holds its left forearm and hand turned by a known turn; the rig turns
        # only that forearm, which moves no bone of the body frame.
        rest = figure_rest()
        known = turn((0, 1, 0), 25.0)
        body_reference = rotations_of(rest)
        for name in ("LowerArm_L", "Hand_L"):
            body_reference[name] = known @ body_reference[name]
        model = retarget_pose.Retarget(PARENTS, HUMAN, rest, body_reference, rest, rotations_of(rest))
        clip = turn((1, 0, 0), 40.0)
        body = model.pose(pose_of(rest, {"LowerArm_L": clip}))
        for name in ("LowerArm_L", "Hand_L"):
            with self.subTest(bone=name):
                np.testing.assert_allclose(body[name][:3, :3], clip @ known @ rest[name][:3, :3], atol=1e-12)

    def test_a_leg_turns_across_the_swing_between_the_reference_directions_and_an_arm_does_not(self):
        rest = figure_rest()
        lean = turn((1, 0, 0), 12.0)
        body_reference = rotations_of(rest)
        for name in ("LowerLeg_L", "Foot_L", "Toes_L", "UpperArm_L", "LowerArm_L", "Hand_L"):
            body_reference[name] = lean @ body_reference[name]
        model = retarget_pose.Retarget(PARENTS, HUMAN, rest, body_reference, rest, rotations_of(rest))
        knee, raise_arm = turn((1, 0, 0), -50.0), turn((0, 1, 0), -40.0)
        body = model.pose(pose_of(rest, {"LowerLeg_L": knee, "UpperArm_L": raise_arm}))
        # The shin's own direction leans, so its bend turns across the lean; the arm takes the turn as is.
        np.testing.assert_allclose(body["LowerLeg_L"][:3, :3], lean @ knee @ lean.T @ body_reference["LowerLeg_L"], atol=1e-12)
        np.testing.assert_allclose(body["UpperArm_L"][:3, :3], raise_arm @ body_reference["UpperArm_L"], atol=1e-12)

    def test_the_local_poses_give_the_armature_space_poses_back(self):
        rest = figure_rest()
        posed = pose_of(rest, CLIP, (0.0, 0.1, 0.0))
        basis = retarget_pose.bases(PARENTS, rest, posed)
        again = {}
        for name in ORDER:
            parent = PARENTS[name]
            again[name] = rest[name] @ basis[name] if parent is None else \
                again[parent] @ np.linalg.inv(rest[parent]) @ rest[name] @ basis[name]
            np.testing.assert_allclose(again[name], posed[name], atol=1e-12)

    def test_a_skeleton_without_the_bones_of_the_body_frame_is_refused(self):
        rest = figure_rest()
        human = {name: role for name, role in HUMAN.items() if name != "UpperArm_R"}
        with self.assertRaisesRegex(ValueError, "RightUpperArm"):
            retarget_pose.Retarget(PARENTS, human, rest, rotations_of(rest), rest, rotations_of(rest))


META = """  humanDescription:
    serializedVersion: 3
    human: []
    skeleton:
    - name: Model(Clone)
      parentName:
      position: {x: 0, y: 0, z: 0}
      rotation: {x: 0, y: 0, z: 0, w: 1}
      scale: {x: 1, y: 1, z: 1}
    - name: Root
      parentName: Model(Clone)
      position: {x: 0, y: 1, z: 0.5}
      rotation: {x: 0, y: 0, z: 0, w: 1}
      scale: {x: 1, y: 1, z: 1}
    - name: Child
      parentName: Root
      position: {x: 0, y: 0, z: 0.25}
      rotation: {x: 0, y: 0.70710678, z: 0, w: 0.70710678}
      scale: {x: 1, y: 1, z: 1}
    armTwist: 0.5
  lastHumanDescriptionAvatarSource: {instanceID: 0}
"""


def matrix(rotation, head) -> np.ndarray:
    result = np.eye(4)
    result[:3, :3] = rotation
    result[:3, 3] = head
    return result


class AvatarTests(unittest.TestCase):
    PARENTS = {"Root": None, "Child": "Root"}

    def rest(self, child_head=(0.0, 0.75, 1.0)):
        return {"Root": matrix(np.eye(3), (0.0, 0.5, 1.0)), "Child": matrix(np.eye(3), child_head)}

    def test_the_skeleton_lines_are_the_entries_of_the_human_description(self):
        lines = retarget_pose.avatar_skeleton_lines(META.replace("\n", "\r\n"))
        self.assertEqual(lines[0], "    - name: Model(Clone)")
        self.assertEqual(lines[-1], "      scale: {x: 1, y: 1, z: 1}")
        self.assertEqual(len(lines), 15)
        self.assertEqual(retarget_pose.avatar_skeleton_lines("  other:\n    skeleton:\n    - name: A\n"), [])
        self.assertEqual(retarget_pose.avatar_skeleton_lines("  humanDescription:\n    skeleton: []\n"), [])

    def test_the_reference_pose_is_read_in_blenders_axes(self):
        pose = retarget_pose.avatar_pose(retarget_pose.avatar_skeleton_lines(META))
        np.testing.assert_allclose(pose["Root"][1], (0.0, 0.5, 1.0), atol=1e-12)
        np.testing.assert_allclose(pose["Child"][1], (0.0, 0.75, 1.0), atol=1e-12)
        # A quarter turn about Unity's up is a quarter turn the other way about Blender's up: +X goes to -Y.
        np.testing.assert_allclose(pose["Child"][0] @ (1.0, 0.0, 0.0), (0.0, -1.0, 0.0), atol=1e-7)
        reference = retarget_pose.reference_of(retarget_pose.avatar_skeleton_lines(META), self.rest(), self.PARENTS)
        np.testing.assert_allclose(reference["Child"], pose["Child"][0], atol=1e-12)

    def test_an_avatar_that_does_not_fit_the_armature_is_refused(self):
        lines = retarget_pose.avatar_skeleton_lines(META)
        with self.assertRaisesRegex(ValueError, "no avatar skeleton"):
            retarget_pose.reference_of([], self.rest(), self.PARENTS)
        with self.assertRaisesRegex(ValueError, "Other"):
            retarget_pose.reference_of(lines, {**self.rest(), "Other": matrix(np.eye(3), (0, 0, 0))}, {**self.PARENTS, "Other": "Root"})
        with self.assertRaisesRegex(ValueError, "Child lies 20.0 mm otherwise from Root"):
            retarget_pose.reference_of(lines, self.rest((0.0, 0.77, 1.0)), self.PARENTS)
        moved = self.rest()
        moved["Root"] = matrix(np.eye(3), (0.0, 0.5, 1.01))
        moved["Child"] = matrix(np.eye(3), (0.0, 0.75, 1.01))
        with self.assertRaisesRegex(ValueError, "root Root"):
            retarget_pose.reference_of(lines, moved, self.PARENTS)


class GamePoseTests(unittest.TestCase):
    """The model against the game: the rig's pose in recorded clip frames, retargeted onto the female,
    lies within the budget of the bone heads the game showed in the same frames."""

    @classmethod
    def setUpClass(cls):
        with open(FIXTURE, encoding="utf-8") as file:
            cls.fixture = json.load(file)

    @staticmethod
    def place(values) -> np.ndarray:
        return matrix(retarget_pose._quaternion(*values[3:]), values[:3])

    def test_the_female_lies_within_the_budget_of_the_games_poses(self):
        names = [bone[0] for bone in self.fixture["bones"]]
        parents = {bone[0]: bone[1] for bone in self.fixture["bones"]}
        human = {bone[0]: bone[2] for bone in self.fixture["bones"]}
        body, rig = self.fixture["body"], self.fixture["rig"]
        model = retarget_pose.Retarget(
            parents, human,
            {name: self.place(values) for name, values in zip(names, body["rest"])},
            {name: retarget_pose._quaternion(*values) for name, values in zip(names, body["reference"])},
            {name: self.place(values) for name, values in zip(names, rig["rest"])},
            {name: retarget_pose._quaternion(*values) for name, values in zip(names, rig["reference"])})
        self.assertGreater(len(self.fixture["poses"]), 2)
        for pose in self.fixture["poses"]:
            posed = model.pose({name: self.place(values) for name, values in zip(names, pose["rig"])})
            distances = [np.linalg.norm(np.array(game) - posed[name][:3, 3][[0, 2, 1]]) * 1000.0
                         for name, game in zip(names, pose["game"])]
            with self.subTest(pose=pose["pose"]):
                self.assertLessEqual(float(np.mean(distances)), BUDGET_MM)


if __name__ == "__main__":
    unittest.main()
