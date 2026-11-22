"""The content fingerprints of an art asset, without Blender: the review stages and parts of every
kind, the rounding and hashing of plain data, and the chain of the stage fingerprints. An approval
binds to the fingerprint of its stage, so a file saved again with the same content keeps its
approvals. content.py reads the parts of an open .blend file; docs/ART_PIPELINE.md describes what each
part holds under Review Stages."""

import hashlib
import re
import struct

import numpy as np

# The version of the fingerprints. tools/art/ArtPipelineCommon.ps1 keeps the same number. A change of
# how any part is read ships with a conversion that first proves each record under the old version.
VERSION = 1
# The decimals numbers keep: positions, matrices, normals, UVs, key values, and handles 6, weights 5.
DECIMALS = 6
WEIGHT_DECIMALS = 5

# The review stages of every kind in order, as tools/art/ArtPipelineCommon.ps1 lists them. A skinned
# asset without clips of its own ends with its rig.
STAGES = {
    "static": ("model",),
    "skinned": ("model", "rig", "animation"),
    "animations": ("animation",),
    "paint": ("paint",),
    "garment": ("model", "rig"),
    "cage": ("model",),
    "texture": ("texture",),
}

# The parts of every stage by kind. A name that ends in ":" stands for one part per item, such as
# clip:<Name> for every clip.
PARTS = {
    "static": {"model": ("materials", "shape", "uv")},
    "skinned": {"model": ("materials", "shape", "uv"), "rig": ("skeleton", "weights"), "animation": ("clip:",)},
    "animations": {"animation": ("clip:", "skeleton")},
    "paint": {"paint": ("body:", "canvas", "paint")},
    "garment": {"model": ("materials", "shape", "uv"), "rig": ("weights",)},
    "cage": {"model": ("fit:", "net")},
    "texture": {"texture": ("image", "material", "templateUv")},
}

# A part name, such as shape or clip:Idle.
PART_PATTERN = re.compile(r"^[A-Za-z][A-Za-z0-9]*(:[A-Za-z][A-Za-z0-9_]*)?$")
SHA_PATTERN = re.compile(r"^[0-9a-f]{64}$")


class Rounded:
    """Numbers fed with other decimals than DECIMALS, such as weights."""

    def __init__(self, values, decimals: int):
        self.values = values
        self.decimals = decimals


def rounded(values, decimals: int = DECIMALS) -> np.ndarray:
    """Values as float64 rounded to the decimals, with negative zero made positive."""
    return np.round(np.asarray(values, np.float64), decimals) + 0.0


def stages_of(kind: str, has_clips: bool = True) -> tuple[str, ...]:
    """The review stages of an asset of the kind; a skinned asset without clips ends with its rig."""
    stages = STAGES[kind]
    if kind == "skinned" and not has_clips:
        return tuple(s for s in stages if s != "animation")
    return stages


def stage_of(kind: str, part: str) -> str:
    """The stage a part of an asset of the kind belongs to."""
    for stage, names in PARTS[kind].items():
        for name in names:
            if part == name or (name.endswith(":") and part.startswith(name) and len(part) > len(name)):
                return stage
    raise ValueError(f'A {kind} asset has no part "{part}".')


def _array(digest, values: np.ndarray, decimals: int) -> None:
    digest.update(b"a" + struct.pack("<q", values.ndim) + b"".join(struct.pack("<q", d) for d in values.shape))
    kind = values.dtype.kind
    if kind == "f" and values.dtype == np.float32:
        digest.update(b"g" + rounded(values, decimals).astype("<f4").tobytes())
    elif kind == "f":
        digest.update(b"f" + rounded(values, decimals).astype("<f8").tobytes())
    elif values.dtype == np.uint8 or kind == "b":
        digest.update(b"u" + values.astype(np.uint8).tobytes())
    elif kind in "iu":
        digest.update(b"i" + values.astype("<i8").tobytes())
    else:
        raise TypeError(f"An array of {values.dtype} has no fingerprint.")


def _feed(digest, value, decimals: int = DECIMALS) -> None:
    """Feeds one value in a form that tells every type and length apart: None, booleans, whole and
    real numbers, text, bytes, arrays, lists, and mappings by sorted key."""
    if value is None:
        digest.update(b"n")
    elif isinstance(value, Rounded):
        _feed(digest, np.asarray(value.values, np.float64), value.decimals)
    elif isinstance(value, (bool, np.bool_)):
        digest.update(b"b" + (b"\x01" if value else b"\x00"))
    elif isinstance(value, (int, np.integer)):
        digest.update(b"i" + struct.pack("<q", int(value)))
    elif isinstance(value, (float, np.floating)):
        digest.update(b"f" + struct.pack("<d", float(rounded(value, decimals))))
    elif isinstance(value, str):
        data = value.encode("utf-8")
        digest.update(b"s" + struct.pack("<q", len(data)) + data)
    elif isinstance(value, (bytes, bytearray, memoryview)):
        data = bytes(value)
        digest.update(b"y" + struct.pack("<q", len(data)) + data)
    elif isinstance(value, np.ndarray):
        _array(digest, value, decimals)
    elif isinstance(value, dict):
        keys = sorted(value)
        if any(not isinstance(k, str) for k in keys):
            raise TypeError("A mapping is fed by text keys.")
        digest.update(b"d" + struct.pack("<q", len(keys)))
        for key in keys:
            _feed(digest, key)
            _feed(digest, value[key], decimals)
    elif isinstance(value, (list, tuple)):
        digest.update(b"l" + struct.pack("<q", len(value)))
        for item in value:
            _feed(digest, item, decimals)
    else:
        raise TypeError(f"A {type(value).__name__} has no fingerprint.")


def fingerprint(value) -> str:
    """SHA-256 of plain data: numbers rounded to DECIMALS unless Rounded says otherwise, mappings by
    their sorted keys, so the order of named things does not matter, and lists in their order."""
    digest = hashlib.sha256()
    _feed(digest, value)
    return digest.hexdigest()


def chain(kind: str, parts: dict[str, str], stages: tuple[str, ...]) -> dict[str, str]:
    """The fingerprint of every stage: its kind, its name, the previous stage's fingerprint, and its
    parts by name, so a change of a part changes its stage and every later one."""
    found = {}
    previous = ""
    for stage in stages:
        names = sorted(name for name in parts if stage_of(kind, name) == stage)
        text = f"art-content/{VERSION}/{kind}/{stage}\n{previous}\n" + "".join(f"{name}={parts[name]}\n" for name in names)
        previous = hashlib.sha256(text.encode("utf-8")).hexdigest()
        found[stage] = previous
    return found


def record(kind: str, parts: dict[str, str], stages: tuple[str, ...]) -> dict:
    """The content record of a file as tools/art/ArtPipelineCommon.ps1 reads it: the version, the
    stages in their order, and the parts by name."""
    for name, value in parts.items():
        if not PART_PATTERN.match(name) or not SHA_PATTERN.match(value):
            raise ValueError(f'The part "{name}" needs a name of letters and digits and a SHA-256.')
        if stage_of(kind, name) not in stages:
            raise ValueError(f'The part "{name}" belongs to no review stage of this {kind} asset.')
    return {"version": VERSION, "stages": chain(kind, parts, stages), "parts": {name: parts[name] for name in sorted(parts)}}
