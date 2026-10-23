"""The bodies that a garment or the garment cage is fitted to, appended from their own files: each
body's armature in a collection of the fitted asset, and its source body, eyes, teeth, and tongue in
the Reference collection, unselectable, so they show the body and the fitting reads them. A body
whose rig, surface, or weights changed in its own file is appended again, and what the fitted asset
keeps on its armature stays on it."""

import hashlib

import bpy
import numpy as np

from . import bodies, skinned
from .scene import ArtPipelineError

REFERENCE_COLLECTION = "Reference"


def collection(name: str) -> bpy.types.Collection:
    found = bpy.data.collections.get(name)
    if found is None:
        found = bpy.data.collections.new(name)
    if found.name not in bpy.context.scene.collection.children:
        bpy.context.scene.collection.children.link(found)
    return found


def references(arm: bpy.types.Object) -> list[bpy.types.Object]:
    """The body's meshes in the Reference collection: its source body, eyes, teeth, and tongue."""
    reference = bpy.data.collections.get(REFERENCE_COLLECTION)
    return [o for o in arm.children_recursive if reference is not None and o.name in reference.objects]


def append_body(path: str, root: str, collection_name: str) -> bpy.types.Object:
    """The body's armature and everything below it but its parts, appended: the source body shows
    the body in the viewport and in renders, and the fitting reads it."""
    with bpy.data.libraries.load(path, link=False) as (source, target):
        if root not in source.objects:
            raise ArtPipelineError(f'{path} has no object "{root}".')
        target.objects = list(source.objects)
        target.images = list(source.images)
    loaded = [o for o in target.objects if o is not None]
    arm = next(o for o in loaded if o.name == root)
    keep = {arm}
    stack = list(arm.children)
    while stack:
        child = stack.pop()
        if "zone" not in child:
            keep.add(child)
            stack.extend(child.children)
    for obj in loaded:
        if obj not in keep:
            bpy.data.objects.remove(obj, do_unlink=True)
    for image in target.images:
        if image is not None and image.users == 0:
            bpy.data.images.remove(image)
    if bodies.source_body(root) is None:
        raise ArtPipelineError(f'{path} has no source body "{bodies.body_name(root)}".')
    own = collection(collection_name)
    reference = collection(REFERENCE_COLLECTION)
    own.objects.link(arm)
    for obj in keep - {arm}:
        reference.objects.link(obj)
        obj.hide_render = False
        obj.hide_select = True
    return arm


def fingerprint(arm: bpy.types.Object, body: bpy.types.Object) -> str:
    """SHA-256 of what the fitting reads from a body: its rig, its surface, and its weights."""
    names = sorted(g.name for g in body.vertex_groups)
    digest = hashlib.sha256(skinned.rig_fingerprint(arm).encode("utf-8"))
    digest.update(";".join(names).encode("utf-8"))
    digest.update((np.round(bodies.world_positions(body), 6) + 0.0).tobytes())
    digest.update((np.round(bodies.weights_of(body, names), 5) + 0.0).tobytes())
    return digest.hexdigest()


def current_fingerprint(path: str, root: str) -> str:
    """The fingerprint of the body as its own file holds it now, read through a link that is
    removed again, so nothing of it stays in this file."""
    names = [root, bodies.body_name(root)]
    with bpy.data.libraries.load(path, link=True) as (source, target):
        missing = [n for n in names if n not in source.objects]
        if missing:
            raise ArtPipelineError(f"{path} has no objects {missing}.")
        target.objects = names
    library = target.objects[0].library
    try:
        return fingerprint(target.objects[0], target.objects[1])
    finally:
        bpy.data.libraries.remove(library)


def stale_bodies(body_list: list[tuple[str, str]]) -> list[str]:
    stale = []
    for path, root in body_list:
        arm = bpy.data.objects.get(root)
        body = bodies.source_body(root)
        here = fingerprint(arm, body) if arm is not None and arm.type == "ARMATURE" and body is not None else None
        if here != current_fingerprint(path, root):
            stale.append(root)
    return stale


def replace_body(path: str, root: str, collection_name: str) -> None:
    """Appends the body again from its file, keeping the meshes the fitted asset has on it."""
    old = bpy.data.objects.get(root)
    kept = []
    if old is not None:
        found = references(old)
        kept = [o for o in old.children if o not in found]
        for obj in found:
            bpy.data.objects.remove(obj, do_unlink=True)
        old.name = f"{root}_replaced"
        if old.data is not None:
            old.data.name = f"{root}_replaced"
        bpy.data.orphans_purge(do_recursive=True)
    arm = append_body(path, root, collection_name)
    for obj in kept:
        obj.parent = arm
    if old is not None:
        bpy.data.objects.remove(old, do_unlink=True)
        bpy.data.orphans_purge(do_recursive=True)
