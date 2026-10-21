"""Blender entry point for the art pipeline scripts in tools/. Run it inside Blender:

    blender --background --factory-startup <asset.blend> --python-exit-code 1 \
        --python tools/blender/art_pipeline_cli.py -- <validate|export|preview> [options]

Every result line starts with ART_ so the PowerShell scripts can read it from Blender's log.
"""

import argparse
import json
import os
import sys

# Keep tools/ free of __pycache__ folders.
sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from art_pipeline import library, preview  # noqa: E402
from art_pipeline.export import clip_records, export_fbx, export_textures, material_records  # noqa: E402
from art_pipeline.scene import ArtPipelineError, parse_args  # noqa: E402
from art_pipeline.validate import validate  # noqa: E402


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="art_pipeline_cli")
    commands = parser.add_subparsers(dest="command", required=True)
    for name in ("validate", "export"):
        command = commands.add_parser(name)
        command.add_argument("--kind", required=True)
        command.add_argument("--roots", required=True, help="Comma-separated root object names.")
        command.add_argument("--triangle-budget", type=int, help="Not for an animation library.")
        command.add_argument("--stage", default="model", help="The review stage whose rules apply, with every earlier one.")
        command.add_argument("--skeleton", help="The skeleton definition of a skinned asset.")
        command.add_argument("--clips", default="", help="Comma-separated animation clip names of a skinned asset.")
        command.add_argument("--rig-source", help="The .blend file of the rig an animation library is authored on.")
        command.add_argument("--rig-root", help="The armature in the rig source.")
        if name == "export":
            command.add_argument("--target", required=True, help="Absolute FBX path.")
            command.add_argument("--textures", help="Folder for the images of painted materials.")
    command = commands.add_parser("preview")
    command.add_argument("--output", required=True, help="PNG file to write.")
    command.add_argument("--cell-size", type=int, default=420)
    command.add_argument("--kind", default="static")
    command = commands.add_parser("library-sync")
    command.add_argument("--library", required=True, help="The animation library .blend to build or update.")
    command.add_argument("--armature", required=True, help="The name of the armature the clips are keyed on.")
    command.add_argument("--rig-source", required=True)
    command.add_argument("--rig-root", required=True)
    command.add_argument("--preview", action="append", default=[], help="<file.blend>=<armature> of a body that shows the clips.")
    command.add_argument("--force", action="store_true")
    return parser


def main() -> int:
    args = parse_args(_parser())
    if args.command == "preview":
        render = preview.render_clip_sheet if args.kind == "animations" else preview.render_sheet
        path = render(os.path.abspath(args.output), args.cell_size)
        print(f"ART_PREVIEW {path}")
        return 0
    if args.command == "library-sync":
        previews = [tuple(p.rsplit("=", 1)) for p in args.preview]
        changed = library.sync(os.path.abspath(args.library), args.armature, os.path.abspath(args.rig_source),
                               args.rig_root, [(os.path.abspath(path), root) for path, root in previews], args.force)
        print(f"ART_LIBRARY {'synced' if changed else 'up-to-date'} {args.library}")
        return 0

    errors, triangles = validate(args.kind, args.roots.split(","), args.triangle_budget, args.stage, args.skeleton,
                                 [c for c in args.clips.split(",") if c],
                                 os.path.abspath(args.rig_source) if args.rig_source else None, args.rig_root)
    for error in errors:
        print(f"ART_PIPELINE_ERROR {error}")
    if errors:
        return 1
    print(f"ART_VALIDATION triangles={triangles}")
    if args.command == "export":
        export_fbx(args.kind, args.target)
        for record in material_records():
            print(f"ART_MATERIAL {json.dumps(record, separators=(',', ':'))}")
        if args.textures:
            for name, path in export_textures(args.textures):
                print(f"ART_TEXTURE {json.dumps({'name': name, 'path': path}, separators=(',', ':'))}")
        if args.kind in ("skinned", "animations"):
            for record in clip_records():
                print(f"ART_CLIP {json.dumps(record, separators=(',', ':'))}")
        print(f"ART_EXPORT {args.target}")
    return 0


try:
    exit_code = main()
except ArtPipelineError as error:
    print(f"ART_PIPELINE_ERROR {error}")
    exit_code = 1
sys.stdout.flush()
sys.exit(exit_code)
