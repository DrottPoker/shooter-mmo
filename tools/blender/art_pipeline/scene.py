"""Shared helpers for reading the exportable content of an asset .blend file."""

import argparse
import sys

import bpy

EXPORT_COLLECTION = "Export"


class ArtPipelineError(Exception):
    """A problem with the asset that the user must fix in Blender."""


def parse_args(parser: argparse.ArgumentParser) -> argparse.Namespace:
    """Parse the arguments after the "--" separator of the Blender command line."""
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    return parser.parse_args(argv)


def export_collection() -> bpy.types.Collection:
    collection = bpy.data.collections.get(EXPORT_COLLECTION)
    if collection is None:
        raise ArtPipelineError(f'The file has no "{EXPORT_COLLECTION}" collection.')
    if not collection.all_objects:
        raise ArtPipelineError(f'The "{EXPORT_COLLECTION}" collection is empty.')
    return collection


def export_objects() -> list[bpy.types.Object]:
    return sorted(export_collection().all_objects, key=lambda o: o.name)


def root_objects() -> list[bpy.types.Object]:
    """Objects in the Export collection whose parent is not exported, in name order."""
    exported = set(export_objects())
    return [o for o in sorted(exported, key=lambda o: o.name) if o.parent not in exported]


def descendants(obj: bpy.types.Object) -> list[bpy.types.Object]:
    result = [obj]
    for child in obj.children:
        result.extend(descendants(child))
    return result


def triangle_count(obj: bpy.types.Object) -> int:
    """Triangles of the evaluated mesh, with modifiers applied as the export applies them."""
    if obj.type != "MESH":
        return 0
    evaluated = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
    mesh = evaluated.to_mesh()
    try:
        mesh.calc_loop_triangles()
        return len(mesh.loop_triangles)
    finally:
        evaluated.to_mesh_clear()
