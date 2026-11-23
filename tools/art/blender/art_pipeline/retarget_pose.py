"""The pose of a clip on a body that is not the rig its animation library is keyed on, as the game gives
it to that body through Unity's Humanoid retargeting, on plain 4x4 matrices in armature space with
numpy and the standard library only, so the tests run it without Blender. game_view.py plays the clip
on the rig, appended apart and never drawn, and poses the body with Retarget.pose. The library's own
rig plays its clips as they are, and the rest pose is never retargeted. See docs/ART_PIPELINE.md, Game
Views, and tools/art/blender/tests/test_retarget_pose.py, which holds the model to the game's poses.

Unity's data:

- The reference poses. A Humanoid avatar measures every turn from its reference pose, the rest pose
  with the arms raised to a T-pose, which Unity writes into humanDescription.skeleton of the model's
  .fbx.meta when it imports the body. Evidence: turns from the rest poses leave the female's clip
  poses 33.2 mm from the game's on average; turns from the reference poses, with the rest of this
  model, 5.8 mm.

Reconstruction, each fitted on the capture of 13 clip poses of one outfit on the female:

- The turns. Every bone takes the turn its rig bone made from its reference pose, relative to its
  parent's, seen across the shortest swing from the rig's reference bone direction to the body's, as
  Unity's muscle frames follow a bone's own direction; the upper arms, forearms, and hands take it
  without the swing. Evidence: with the swing on these too, 6.7 mm; without any swing, 7.9 mm.
- The body frame. The body turns about its hips so the frame of its upper legs and upper arms, up from
  the middle of the upper legs to the middle of the upper arms and right along the sum of the two
  left-to-right lines, turns from its reference pose as the rig's does. Evidence: without it 14.5 mm,
  with the upper arms' line alone 8.3 mm, with the sum 7.9 mm.
- The centre of mass. The hips lie where they put the body's centre of mass, of the heads of its main
  human bones with Unity-like masses, at the rig's times the human scale, the ratio of the two
  reference poses' centre of mass heights. Evidence: the hips' 22 mm backward offset goes; the ratio
  of the hip heights instead leaves them 8 mm too high in every pose, this one 0.1 mm; the mass tables
  tried differ by at most 0.5 mm."""

from __future__ import annotations

import numpy as np

# The bones of the body frame, by their Unity human names.
FRAME_BONES = ("LeftUpperLeg", "RightUpperLeg", "LeftUpperArm", "RightUpperArm")
# The bones that take the rig's turn without the swing between the two reference directions.
UNSWUNG = frozenset({"LeftUpperArm", "RightUpperArm", "LeftLowerArm", "RightLowerArm", "LeftHand", "RightHand"})
# The masses of the main human bones, at their heads, for the centre of mass.
MASSES = {
    "Hips": 12.0, "LeftUpperLeg": 10.0, "RightUpperLeg": 10.0, "LeftLowerLeg": 4.0, "RightLowerLeg": 4.0,
    "LeftFoot": 0.8, "RightFoot": 0.8, "LeftToes": 0.2, "RightToes": 0.2, "Spine": 2.5, "Chest": 12.0,
    "UpperChest": 12.0, "Neck": 1.0, "Head": 4.0, "LeftShoulder": 0.5, "RightShoulder": 0.5,
    "LeftUpperArm": 2.0, "RightUpperArm": 2.0, "LeftLowerArm": 1.5, "RightLowerArm": 1.5, "LeftHand": 0.5,
    "RightHand": 0.5,
}
# Unity's axes in an imported model, x right, y up, z front, against Blender's armature space, x right,
# y front, z up: the model bakes the axis conversion, so they differ by swapping y and z.
SWAP = np.array([[1.0, 0.0, 0.0], [0.0, 0.0, 1.0], [0.0, 1.0, 0.0]])
# How far an avatar's reference pose may lie from the armature's rest where a turn to a T-pose cannot
# move it: a bone's distance from its parent, the root's place in meters, and the root's turn in degrees.
FIT_DISTANCE = 0.0005
FIT_DEGREES = 0.5


def rotation_of(matrix) -> np.ndarray:
    """The rotation of a matrix, orthonormal: a pose matrix may hold the float rounding of a scale."""
    u, _s, vt = np.linalg.svd(np.asarray(matrix, float)[:3, :3])
    rotation = u @ vt
    if np.linalg.det(rotation) < 0.0:
        u[:, -1] = -u[:, -1]
        rotation = u @ vt
    return rotation


def parents_first(parents: dict[str, str | None]) -> list[str]:
    """The bones in an order in which every parent comes before its children, each level by name."""
    order, placed = [], set()
    pending = sorted(parents)
    while pending:
        later = [name for name in pending if parents[name] is not None and parents[name] not in placed]
        if len(later) == len(pending):
            raise ValueError(f"The bones {later[:3]} have no root among their parents.")
        for name in pending:
            if name not in later:
                order.append(name)
                placed.add(name)
        pending = later
    return order


def swing(source, target) -> np.ndarray:
    """The shortest turn that takes the direction source to the direction target."""
    a = np.asarray(source, float) / np.linalg.norm(source)
    b = np.asarray(target, float) / np.linalg.norm(target)
    axis = np.cross(a, b)
    cosine = float(np.dot(a, b))
    if np.linalg.norm(axis) < 1e-12:
        if cosine > 0.0:
            return np.eye(3)
        raise ValueError("Two reference directions point against each other.")
    cross = np.array([[0.0, -axis[2], axis[1]], [axis[2], 0.0, -axis[0]], [-axis[1], axis[0], 0.0]])
    return np.eye(3) + cross + cross @ cross / (1.0 + cosine)


def forward(order: list[str], parents: dict[str, str | None], rest: dict[str, np.ndarray],
            rotations: dict[str, np.ndarray], root_head) -> dict[str, np.ndarray]:
    """The armature-space matrices of the bones with the given rotations, each bone's head carried by its
    posed parent as it lies from the parent at rest, the root's head at root_head."""
    posed = {}
    for name in order:
        parent = parents[name]
        matrix = np.eye(4)
        matrix[:3, :3] = rotations[name]
        if parent is None:
            matrix[:3, 3] = root_head
        else:
            offset = rotation_of(rest[parent]).T @ (rest[name][:3, 3] - rest[parent][:3, 3])
            matrix[:3, 3] = posed[parent][:3, 3] + posed[parent][:3, :3] @ offset
        posed[name] = matrix
    return posed


def bases(parents: dict[str, str | None], rest: dict[str, np.ndarray], posed: dict[str, np.ndarray]) -> dict[str, np.ndarray]:
    """The local pose of every bone, Blender's matrix_basis, that gives the armature-space matrices:
    rest times the basis for a root, and the parent's pose times the bone's rest relative to the
    parent's rest times the basis for a child."""
    found = {}
    for name, parent in parents.items():
        bone_rest = np.asarray(rest[name], float)
        if parent is None:
            found[name] = np.linalg.inv(bone_rest) @ posed[name]
        else:
            relative = np.linalg.inv(np.asarray(rest[parent], float)) @ bone_rest
            found[name] = np.linalg.inv(relative) @ np.linalg.inv(posed[parent]) @ posed[name]
    return found


def avatar_skeleton_lines(text: str) -> list[str]:
    """The lines of humanDescription.skeleton of a model's .fbx.meta, the entries under `    skeleton:`
    with their line ends removed, or none when the meta has no avatar. tools/art/ArtViews.ps1 takes the
    same lines for the fingerprint in the views' keys."""
    lines = text.replace("\r\n", "\n").split("\n")
    try:
        start = lines.index("  humanDescription:")
    except ValueError:
        return []
    for index in range(start + 1, len(lines)):
        line = lines[index]
        if line.rstrip() == "    skeleton:":
            found = []
            for entry in lines[index + 1:]:
                if not (entry.startswith("    - ") or entry.startswith("      ")):
                    break
                found.append(entry.rstrip())
            return found
        if line.startswith("  ") and not line.startswith("   "):
            break
    return []


def _numbers(text: str, names: tuple[str, ...]) -> np.ndarray:
    fields = {key.strip(): value.strip() for key, value in (part.split(":", 1) for part in text.strip().strip("{}").split(","))}
    return np.array([float(fields[name]) for name in names])


def _quaternion(x: float, y: float, z: float, w: float) -> np.ndarray:
    norm = np.sqrt(x * x + y * y + z * z + w * w)
    x, y, z, w = x / norm, y / norm, z / norm, w / norm
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def avatar_pose(lines: list[str]) -> dict[str, tuple[np.ndarray, np.ndarray]]:
    """Every transform of an avatar's skeleton in its reference pose, the model root first: its rotation
    and its position in Blender's armature space, each entry under its parent by name."""
    entries, current = [], None
    for line in lines:
        if line.startswith("    - name:"):
            current = {"name": line.split(":", 1)[1].strip()}
            entries.append(current)
        elif current is not None:
            key, _colon, value = line.strip().partition(":")
            current[key] = value.strip()
    pose: dict[str, tuple[np.ndarray, np.ndarray]] = {}
    for entry in entries:
        try:
            position = _numbers(entry["position"], ("x", "y", "z"))
            rotation = _quaternion(*_numbers(entry["rotation"], ("x", "y", "z", "w")))
        except (KeyError, ValueError) as error:
            raise ValueError(f"has an avatar skeleton entry {entry['name']} without a position and a rotation") from error
        parent = entry.get("parentName", "")
        if parent:
            if parent not in pose:
                raise ValueError(f"names {parent}, the parent of {entry['name']}, after it in its avatar skeleton")
            parent_rotation, parent_position = pose[parent]
            pose[entry["name"]] = (parent_rotation @ rotation, parent_position + parent_rotation @ position)
        else:
            pose[entry["name"]] = (rotation, position)
    return {name: (SWAP @ rotation @ SWAP, SWAP @ position) for name, (rotation, position) in pose.items()}


def reference_of(lines: list[str], rest: dict[str, np.ndarray], parents: dict[str, str | None]) -> dict[str, np.ndarray]:
    """The reference pose of an armature from its avatar's skeleton, the turn of every bone in armature
    space. Raises ValueError when the avatar has no skeleton, lacks a bone of the armature, or does not
    fit it: a bone lies at another distance from its parent, or the root at another place or turn, than
    the armature's rest has, which no turn to a T-pose changes."""
    if not lines:
        raise ValueError("has no avatar skeleton (humanDescription.skeleton)")
    pose = avatar_pose(lines)
    missing = [name for name in parents if name not in pose]
    if missing:
        raise ValueError(f"has an avatar skeleton without the bones {', '.join(missing[:3])} of the armature")
    for name, parent in parents.items():
        bone_rest = np.asarray(rest[name], float)
        if parent is None:
            turn = rotation_of(bone_rest).T @ pose[name][0]
            degrees = np.degrees(np.arccos(np.clip((np.trace(turn) - 1.0) / 2.0, -1.0, 1.0)))
            if np.linalg.norm(pose[name][1] - bone_rest[:3, 3]) > FIT_DISTANCE or degrees > FIT_DEGREES:
                raise ValueError(f"has an avatar skeleton whose root {name} does not lie as the armature's rest does")
            continue
        here = np.linalg.norm(pose[name][1] - pose[parent][1])
        there = np.linalg.norm(bone_rest[:3, 3] - np.asarray(rest[parent], float)[:3, 3])
        if abs(here - there) > FIT_DISTANCE:
            raise ValueError(f"has an avatar skeleton whose {name} lies {abs(here - there) * 1000.0:.1f} mm otherwise from "
                             f"{parent} than the armature's rest")
    return {name: pose[name][0] for name in parents}


class Retarget:
    """Poses a body after its library's rig: the parents of the body's bones, the Unity human name of
    each human bone, and the rest matrices and reference turns of the body and of the rig in armature
    space, the rig having every bone of the body."""

    def __init__(self, parents: dict[str, str | None], human: dict[str, str], body_rest: dict[str, np.ndarray],
                 body_reference: dict[str, np.ndarray], rig_rest: dict[str, np.ndarray], rig_reference: dict[str, np.ndarray]):
        self.parents = dict(parents)
        self.order = parents_first(self.parents)
        roots = [name for name in self.order if self.parents[name] is None]
        if len(roots) != 1:
            raise ValueError(f"The armature has {len(roots)} roots, not one.")
        self.root = roots[0]
        missing = [name for name in self.parents if name not in rig_rest or name not in rig_reference or name not in body_reference]
        if missing:
            raise ValueError(f"The rig or a reference pose lacks the bones {', '.join(missing[:3])}.")
        self.human = {name: role for name, role in human.items() if name in self.parents}
        bone_of = {role: name for name, role in self.human.items()}
        absent = [role for role in FRAME_BONES + tuple(MASSES) if role not in bone_of]
        if absent:
            raise ValueError(f"The armature maps no bone to the human bones {', '.join(absent[:3])}.")
        self.frame_bones = [bone_of[role] for role in FRAME_BONES]
        self.masses = [(bone_of[role], mass) for role, mass in MASSES.items()]
        self.body_rest = {name: np.asarray(body_rest[name], float) for name in self.parents}
        self.rig_rest = {name: np.asarray(rig_rest[name], float) for name in self.parents}
        self.body_reference = {name: rotation_of(body_reference[name]) for name in self.parents}
        self.rig_reference = {name: rotation_of(rig_reference[name]) for name in self.parents}
        # A bone's direction is its Y axis, from its head toward its tail.
        self.swings = {name: np.eye(3) if self.human.get(name) in UNSWUNG else
                       swing(self.rig_reference[name][:, 1], self.body_reference[name][:, 1]) for name in self.parents}
        body_heads = self._heads(forward(self.order, self.parents, self.body_rest, self.body_reference, self.body_rest[self.root][:3, 3]))
        rig_heads = self._heads(forward(self.order, self.parents, self.rig_rest, self.rig_reference, self.rig_rest[self.root][:3, 3]))
        self.body_frame_reference = self.frame(body_heads)
        self.rig_frame_reference = self.frame(rig_heads)
        rig_height = self.centre_of_mass(rig_heads)[2]
        if rig_height <= 0.0:
            raise ValueError("The rig's centre of mass lies at or below the ground in its reference pose.")
        self.scale = self.centre_of_mass(body_heads)[2] / rig_height

    @staticmethod
    def _heads(posed: dict[str, np.ndarray]) -> dict[str, np.ndarray]:
        return {name: matrix[:3, 3] for name, matrix in posed.items()}

    def frame(self, heads: dict[str, np.ndarray]) -> np.ndarray:
        """The body frame of bone heads: up from the middle of the upper legs to the middle of the upper
        arms, right along the sum of the upper legs' and the upper arms' left-to-right lines, as columns
        right, front, up."""
        left_leg, right_leg, left_arm, right_arm = (np.asarray(heads[name], float) for name in self.frame_bones)
        up = (left_arm + right_arm) / 2.0 - (left_leg + right_leg) / 2.0
        up /= np.linalg.norm(up)
        right = (right_leg - left_leg) + (right_arm - left_arm)
        right -= np.dot(right, up) * up
        right /= np.linalg.norm(right)
        return np.stack([right, np.cross(up, right), up], axis=1)

    def centre_of_mass(self, heads: dict[str, np.ndarray]) -> np.ndarray:
        """The centre of mass of the main human bones' heads."""
        total = sum(mass for _name, mass in self.masses)
        return sum(np.asarray(heads[name], float) * mass for name, mass in self.masses) / total

    def pose(self, rig_posed: dict[str, np.ndarray]) -> dict[str, np.ndarray]:
        """The armature-space matrices of the body's bones for the rig's armature-space pose matrices."""
        rig_turns = {name: rotation_of(rig_posed[name]) @ self.rig_reference[name].T for name in self.order}
        turns = {}
        for name in self.order:
            parent = self.parents[name]
            if parent is None:
                turns[name] = rig_turns[name]
            else:
                relative = rig_turns[parent].T @ rig_turns[name]
                across = self.swings[name]
                turns[name] = turns[parent] @ across @ relative @ across.T
        rotations = {name: turns[name] @ self.body_reference[name] for name in self.order}
        rig_heads = {name: np.asarray(rig_posed[name], float)[:3, 3] for name in self.order}
        # The body frame turns from its reference as the rig's does.
        trial = self._heads(forward(self.order, self.parents, self.body_rest, rotations, np.zeros(3)))
        rig_turn = self.frame(rig_heads) @ self.rig_frame_reference.T
        body_turn = self.frame(trial) @ self.body_frame_reference.T
        about_hips = rig_turn @ body_turn.T
        rotations = {name: about_hips @ rotation for name, rotation in rotations.items()}
        # The hips put the centre of mass at the rig's times the human scale.
        trial = self._heads(forward(self.order, self.parents, self.body_rest, rotations, np.zeros(3)))
        root_head = self.scale * self.centre_of_mass(rig_heads) - self.centre_of_mass(trial)
        return forward(self.order, self.parents, self.body_rest, rotations, root_head)
