"""The cache of the garment checks below artifacts/art-cache/: the results a sync, an approval, the review
sheet, and the export would otherwise compute again. Two kinds of unit are cached: a garment's own check
on one body, what it covers and, at the rig stage, what motion does to the skin under it; and a pair, a
template of a lower layer worn under it, on every body: the hide margins, the hidden faces, and the pair
check. A masked texture's units are those of its template with the texture on it.

The key of a unit is the SHA-256 of everything it reads, which its entry names for debugging: the
content fingerprints of the garments involved and of a texture's image, the fit of each body, the cage's
content, the content of each clip library with its clips, the fields of the templates' asset.json that
the checks read, the review stage and the kind of unit, and one fingerprint of the pipeline's code and
Blender's version, so any change of the code misses every entry. A unit with a fingerprint missing is
computed and not cached, and a run writes no entry once a file it reads from another asset, a clip
library, a template of a lower layer, or a texture, is no longer what the fingerprint given for it
says. An entry is a JSON file and an .npz file of arrays, which is loaded without pickle; a missing,
broken, or older entry reads as none, so the folder may be deleted at any time.

Standard library, numpy, and manifest_io only, so the tests run it without Blender. See docs/ART_PIPELINE.md, Cache Of
The Garment Checks."""

import hashlib
import io
import json
import os
import pathlib
import re
import tempfile
import zipfile

import numpy as np

from . import manifest_io

# The version of the keys and entries; a change misses every older entry.
SCHEMA = 1
ROOT = pathlib.Path(__file__).resolve().parents[4] / "artifacts" / "art-cache" / "checks"
# The fields of a template's asset.json that the checks read, or that choose what they read.
FIELDS = ("band", "covers", "hair", "hides", "layer", "recipe", "slot")
SHA_PATTERN = re.compile(r"^[0-9a-f]{64}$")
# The kinds of array an entry may hold: booleans, whole numbers, and floats, never objects.
ARRAY_KINDS = "biuf"


def is_sha(value) -> bool:
    return isinstance(value, str) and SHA_PATTERN.match(value) is not None


def code_files(repository: pathlib.Path, folders: list[pathlib.Path], files: list[pathlib.Path]) -> list[tuple[str, bytes]]:
    """The files the code fingerprint reads: every file below the folders, but Python's caches, and the
    files, each by its path relative to the repository, so the fingerprint is the same in every checkout."""
    found = {}
    for folder in folders:
        for path in folder.rglob("*"):
            if path.is_file() and "__pycache__" not in path.parts:
                found[path.resolve()] = None
    for path in files:
        found[pathlib.Path(path).resolve()] = None
    root = repository.resolve()
    named = []
    for path in found:
        try:
            name = path.relative_to(root).as_posix()
        except ValueError:
            name = path.as_posix()
        named.append((name, path.read_bytes()))
    return named


def code_fingerprint(files: list[tuple[str, bytes]], blender: str) -> str:
    """SHA-256 of the pipeline's code: every file by its name and its bytes, in the order of the names,
    and Blender's version."""
    digest = hashlib.sha256(f"art-check-code/{SCHEMA}\n{blender}\n".encode("utf-8"))
    for name, data in sorted(files):
        encoded = name.encode("utf-8")
        digest.update(len(encoded).to_bytes(8, "little") + encoded + len(data).to_bytes(8, "little") + data)
    return digest.hexdigest()


def manifest_fields(path: str) -> dict:
    """The fields of a template's asset.json that the checks read, a missing one as None."""
    data = manifest_io.read_json(path)
    return {name: data.get(name) for name in FIELDS}


def key(inputs: dict) -> str:
    """The key of a unit: the SHA-256 of its inputs as JSON with sorted keys."""
    text = json.dumps(inputs, sort_keys=True, separators=(",", ":"), ensure_ascii=True, allow_nan=False)
    return hashlib.sha256(f"art-check/{SCHEMA}\n{text}".encode("utf-8")).hexdigest()


def own_inputs(base: dict | None, label: str, fit, stage: str) -> dict | None:
    """What a garment's own check on one body reads, or None when a fingerprint is missing: at the rig
    stage also the clips."""
    if base is None or not is_sha(fit) or (stage == "rig" and base["clips"] is None):
        return None
    inputs = {"unit": "own", "stage": stage, "code": base["code"], "garment": base["garment"],
              "texture": base["texture"], "cage": base["cage"], "body": {"label": label, "fit": fit}}
    if stage == "rig":
        inputs["clips"] = base["clips"]
    return inputs


def pair_inputs(base: dict | None, fits: list[tuple[str, object]], inner: dict | None, stage: str) -> dict | None:
    """What a pair reads, the garment over a template of a lower layer on every body, or None when a
    fingerprint is missing: at the rig stage also the clips."""
    if base is None or inner is None or not is_sha(inner.get("content")) or not all(is_sha(fit) for _label, fit in fits) or \
            (stage == "rig" and base["clips"] is None):
        return None
    inputs = {"unit": "pair", "stage": stage, "code": base["code"], "garment": base["garment"],
              "texture": base["texture"], "cage": base["cage"],
              "bodies": [{"label": label, "fit": fit} for label, fit in fits], "inner": inner}
    if stage == "rig":
        inputs["clips"] = base["clips"]
    return inputs


def base_inputs(code, garment: dict, cage, clips: list[dict] | None, texture: dict | None = None) -> dict | None:
    """What every unit of a run reads: the code, the garment with its content and fields, the cage, the
    clip libraries, each its content and clips, or None when one lacks its content, and a texture's image.
    None when a fingerprint the units all need is missing."""
    if not is_sha(code) or not is_sha(garment.get("content")) or not is_sha(cage) or \
            (texture is not None and not is_sha(texture.get("image"))):
        return None
    if clips is not None and not all(is_sha(c.get("content")) for c in clips):
        clips = None
    return {"code": code, "garment": garment, "cage": cage, "clips": clips, "texture": texture}


class Store:
    """The entries below a folder, one per key: <key[:2]>/<key>.json names the unit's inputs and holds its
    plain data and the SHA-256 of <key[:2]>/<key>.npz, which holds its arrays. Each file is written to a
    temporary file first and moved into place, the arrays before the JSON, so a reader finds a whole
    entry or none."""

    def __init__(self, root=None, enabled: bool = True):
        self.root = pathlib.Path(root) if root is not None else ROOT
        self.enabled = enabled

    def paths(self, key_: str) -> tuple[pathlib.Path, pathlib.Path]:
        folder = self.root / key_[:2]
        return folder / f"{key_}.json", folder / f"{key_}.npz"

    def load(self, key_: str) -> tuple[dict, dict[str, np.ndarray]] | None:
        if not self.enabled:
            return None
        entry_path, arrays_path = self.paths(key_)
        try:
            entry = json.loads(entry_path.read_text(encoding="utf-8"))
            blob = arrays_path.read_bytes()
            if entry.get("schema") != SCHEMA or entry.get("key") != key_ or \
                    entry.get("arraysSha256") != hashlib.sha256(blob).hexdigest():
                return None
            with np.load(io.BytesIO(blob), allow_pickle=False) as found:
                arrays = {name: found[name] for name in found.files}
            return entry["data"], arrays
        except (OSError, ValueError, KeyError, TypeError, zipfile.BadZipFile):
            return None

    def save(self, key_: str, inputs: dict, data: dict, arrays: dict[str, np.ndarray]) -> bool:
        """Writes the entry and returns whether it did; a folder that cannot be written leaves it out."""
        if not self.enabled:
            return False
        for name, array in arrays.items():
            if np.asarray(array).dtype.kind not in ARRAY_KINDS:
                raise TypeError(f'The array "{name}" of a cache entry holds {np.asarray(array).dtype}.')
        buffer = io.BytesIO()
        np.savez(buffer, **{name: np.asarray(array) for name, array in arrays.items()})
        blob = buffer.getvalue()
        entry = {"schema": SCHEMA, "key": key_, "inputs": inputs, "arraysSha256": hashlib.sha256(blob).hexdigest(),
                 "data": data}
        entry_path, arrays_path = self.paths(key_)
        try:
            entry_path.parent.mkdir(parents=True, exist_ok=True)
            _replace(arrays_path, blob)
            _replace(entry_path, (json.dumps(entry, indent=1) + "\n").encode("utf-8"))
        except OSError:
            return False
        return True


def _replace(path: pathlib.Path, data: bytes) -> None:
    handle, temporary = tempfile.mkstemp(dir=path.parent, prefix=path.name + ".", suffix=".tmp")
    try:
        with os.fdopen(handle, "wb") as out:
            out.write(data)
        os.replace(temporary, path)
    except BaseException:
        if os.path.exists(temporary):
            os.remove(temporary)
        raise


def source_problem(source: dict) -> str | None:
    """Why a file a run reads from another asset no longer is what its units' keys say, or None. A
    source names the asset, its file, the fingerprint the run was given for it, and where that
    fingerprint lies in the content record of the asset.json next to the file: the file's SHA-256 must
    be the record's sourceSha256, and the record's fingerprint the one given. A file of the run's own
    asset that the run reads again from disk is held instead to its SHA-256 when the run began."""
    name = source["name"]
    path = pathlib.Path(source["path"])
    try:
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
    except OSError:
        return f"{name} could not be read"
    if "sha256" in source:
        return None if digest == source["sha256"] else f"{name} changed during the run"
    try:
        record = manifest_io.read_json(path.parent / "asset.json")
    except (OSError, ValueError):
        return f"{name} has no asset.json to compare"
    content = record.get("content") if isinstance(record, dict) else None
    if not isinstance(content, dict):
        return f"{name} has no content record to compare"
    value = content
    for part in source["record"]:
        value = value.get(part) if isinstance(value, dict) else None
    if content.get("sourceSha256") != digest or value != source["content"]:
        return f"{name} changed during the run"
    return None


class Units:
    """The cached units of one run: their inputs, the store they are read from and written to, the files
    the run reads from other assets, and how many units the run used, how many came from the store,
    and how many it wrote. Before every entry is written, each of those files must still be what the
    keys say; once one is not, the run writes nothing more and says why."""

    def __init__(self, store: Store, base: dict | None = None, fits: dict | None = None, inners: dict | None = None,
                 notes: list[str] | None = None, sources: list[dict] | None = None):
        self.store = store
        self.base = base
        self.fits = fits or {}
        self.inners = inners or {}
        self.notes = list(notes or [])
        self.sources = list(sources or [])
        self.halted = False
        self.used = self.cached = self.stored = 0

    def own(self, label: str, stage: str) -> dict | None:
        return own_inputs(self.base, label, self.fits.get(label), stage)

    def pair(self, inner: str, labels: list[str], stage: str) -> dict | None:
        return pair_inputs(self.base, [(label, self.fits.get(label)) for label in labels], self.inners.get(inner), stage)

    def load(self, inputs: dict | None) -> tuple[dict, dict] | None:
        self.used += 1
        found = self.store.load(key(inputs)) if inputs is not None else None
        if found is not None:
            self.cached += 1
        return found

    def save(self, inputs: dict | None, data: dict, arrays: dict) -> None:
        """Writes the entry unless the unit has no key, the store is off, or a source no longer holds,
        every source hashed again for every entry."""
        if inputs is None or not self.store.enabled or self.halted:
            return
        for source in self.sources:
            problem = source_problem(source)
            if problem is not None:
                self.halted = True
                self.notes.append(problem)
                return
        if self.store.save(key(inputs), inputs, data, arrays):
            self.stored += 1

    def report(self) -> dict:
        """The line a run prints for the PowerShell scripts, which count the units from the cache."""
        return {"units": self.used, "cached": self.cached, "stored": self.stored, "notes": self.notes}
