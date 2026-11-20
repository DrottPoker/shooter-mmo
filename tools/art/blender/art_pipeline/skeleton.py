"""Shared skeleton definitions, art/Skeletons/<Name>.json: the bones of an armature with their
parents, which bones deform the skin, the Unity Humanoid bone each maps to, the attachment
sockets, the cloth chains that loose garments hang on, and the eyelids that close over the eyes.
Every playable body of a skeleton has exactly these bones, so animations and equipment fit all of
them."""

import json
from dataclasses import dataclass

from .scene import ArtPipelineError

# The cloth chains a skeleton may have.
CHAINS = ("skirt", "cloak", "hood")
LIDS = ("upper", "lower")


@dataclass(frozen=True)
class SkeletonBone:
    name: str
    parent: str | None
    deform: bool
    human: str | None
    socket: str | None
    # The cloth chain the bone belongs to, such as skirt, cloak, or hood. The body is never
    # weighted to it, and clips leave it to the runtime cloth solver.
    chain: str | None = None


@dataclass(frozen=True)
class Eyelids:
    """The lids of the eyes: per eye its eye bone and the bones of its upper and its lower lid, which
    sit at the eye's centre and close the eye by turning about their X axis, the upper lid's bone by
    upper_closed degrees and the lower lid's by lower_closed. The rig weights the skin so the lids
    meet at these turns."""
    eyes: tuple[tuple[str, str, str], ...]
    upper_closed: float
    lower_closed: float

    @property
    def bones(self) -> list[str]:
        return [bone for _eye, upper, lower in self.eyes for bone in (upper, lower)]

    def closed(self, lid: str) -> float:
        return {"upper": self.upper_closed, "lower": self.lower_closed}[lid]


@dataclass(frozen=True)
class Skeleton:
    name: str
    bones: tuple[SkeletonBone, ...]
    eyelids: Eyelids | None = None

    def bone(self, name: str) -> SkeletonBone:
        return next(b for b in self.bones if b.name == name)

    @property
    def deform_bones(self) -> list[str]:
        return [b.name for b in self.bones if b.deform]

    @property
    def chain_bones(self) -> set[str]:
        return {b.name for b in self.bones if b.chain}

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
                               human=b.get("human"), socket=b.get("socket"), chain=b.get("chain"))
                  for b in data["bones"])
    names = [b.name for b in bones]
    if len(set(names)) != len(names) or sum(b.parent is None for b in bones) != 1:
        raise ArtPipelineError(f"The skeleton definition {path} needs unique bone names and one root bone.")
    for index, b in enumerate(bones):
        if b.chain is not None and (b.chain not in CHAINS or not b.deform or b.human or b.socket):
            raise ArtPipelineError(f'Bone "{b.name}" in {path} must be a deforming bone of one of the chains {CHAINS}, '
                                   "outside the Humanoid avatar and the sockets.")
        if b.parent is not None and b.parent not in names[:index]:
            raise ArtPipelineError(f'Bone "{b.name}" in {path} must follow its parent "{b.parent}".')
    return Skeleton(name=data["name"], bones=bones, eyelids=_eyelids(data.get("eyelids"), bones, path))


def _eyelids(data: dict | None, bones: tuple[SkeletonBone, ...], path: str) -> Eyelids | None:
    """The eyelids section: each lid bone a deforming child of its eye's parent outside the Humanoid
    avatar, the sockets, and the chains; the upper lid closing downward (a negative turn) and the
    lower lid upward."""
    if data is None:
        return None
    by_name = {b.name: b for b in bones}
    try:
        eyes = tuple((e["eye"], e["upper"], e["lower"]) for e in data["eyes"])
        lids = Eyelids(eyes=eyes, upper_closed=float(data["upperClosedDegrees"]), lower_closed=float(data["lowerClosedDegrees"]))
    except (KeyError, TypeError, ValueError) as error:
        raise ArtPipelineError(f"The eyelids of {path} need eyes with an eye, an upper, and a lower bone, "
                               "upperClosedDegrees, and lowerClosedDegrees.") from error
    if not eyes or not lids.upper_closed < 0.0 < lids.lower_closed:
        raise ArtPipelineError(f"The eyelids of {path} need at least one eye, the upper lid closing by a negative "
                               "turn and the lower lid by a positive one.")
    for eye, upper, lower in eyes:
        if eye not in by_name:
            raise ArtPipelineError(f'The eyelids of {path} name the eye "{eye}", which is not a bone.')
        for lid in (upper, lower):
            b = by_name.get(lid)
            if b is None or b.parent != by_name[eye].parent or not b.deform or b.human or b.socket or b.chain:
                raise ArtPipelineError(f'The eyelid "{lid}" of {path} must be a deforming bone beside "{eye}", outside '
                                       "the Humanoid avatar, the sockets, and the chains.")
    if len(set(lids.bones)) != len(lids.bones):
        raise ArtPipelineError(f"The eyelids of {path} name a bone twice.")
    return lids
