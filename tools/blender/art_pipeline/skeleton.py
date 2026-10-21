"""Shared skeleton definitions, art/Skeletons/<Name>.json: the bones of an armature with their
parents, which bones deform the skin, the Unity Humanoid bone each maps to, and the attachment
sockets. Every playable body of a skeleton has exactly these bones, so animations and equipment
fit all of them."""

import json
from dataclasses import dataclass

from .scene import ArtPipelineError


@dataclass(frozen=True)
class SkeletonBone:
    name: str
    parent: str | None
    deform: bool
    human: str | None
    socket: str | None


@dataclass(frozen=True)
class Skeleton:
    name: str
    bones: tuple[SkeletonBone, ...]

    def bone(self, name: str) -> SkeletonBone:
        return next(b for b in self.bones if b.name == name)

    @property
    def deform_bones(self) -> list[str]:
        return [b.name for b in self.bones if b.deform]

    @property
    def root(self) -> SkeletonBone:
        return next(b for b in self.bones if b.parent is None)


def load(path: str) -> Skeleton:
    try:
        with open(path, encoding="utf-8") as f:
            data = json.load(f)
    except (OSError, ValueError) as error:
        raise ArtPipelineError(f"Cannot read the skeleton definition {path}: {error}") from error
    bones = tuple(SkeletonBone(name=b["name"], parent=b.get("parent"), deform=bool(b["deform"]),
                               human=b.get("human"), socket=b.get("socket")) for b in data["bones"])
    names = [b.name for b in bones]
    if len(set(names)) != len(names) or sum(b.parent is None for b in bones) != 1:
        raise ArtPipelineError(f"The skeleton definition {path} needs unique bone names and one root bone.")
    for index, b in enumerate(bones):
        if b.parent is not None and b.parent not in names[:index]:
            raise ArtPipelineError(f'Bone "{b.name}" in {path} must follow its parent "{b.parent}".')
    return Skeleton(name=data["name"], bones=bones)
