"""Blender entry point for the art pipeline scripts in tools/. Run it inside Blender:

    blender --background --factory-startup <asset.blend> --python-exit-code 1 \
        --python tools/blender/art_pipeline_cli.py -- <validate|export|preview> [options]

new-static writes the first file of a static asset, library-sync builds or updates an animation
library, paint-sync a paint layer, garment-create and garment-sync a garment template,
garment-review-file writes a template's review file, cage-create and cage-sync the garment cage,
and texture-create and texture-sync a garment texture; they run without an open file. Every result
line starts with ART_ so the PowerShell scripts can read it from Blender's log.
"""

import argparse
import json
import os
import sys

import bpy

# Keep tools/ free of __pycache__ folders.
sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from art_pipeline import bodies, cage, coverage, garment, library, paint, preview, skinned, texture  # noqa: E402
from art_pipeline import skeleton as skeleton_definition  # noqa: E402
from art_pipeline.export import (clip_records, export_fbx, export_textures, material_records, parse_variants,  # noqa: E402
                                  variant_errors)
from art_pipeline.scene import ArtPipelineError, new_static_file, parse_args  # noqa: E402
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
        command.add_argument("--variants", help='Other images of painted materials, "M_A=T_1+T_2;M_B=T_3".')
        command.add_argument("--paint", help="The name of a paint layer.")
        command.add_argument("--part", help="The painted material part of a paint layer, such as Body.")
        command.add_argument("--garment", help="The name of a garment.")
        command.add_argument("--band", help="The band of a garment: tight, mid, outer, or loose.")
        command.add_argument("--body", action="append", default=[],
                             help="<file.blend>=<armature> of a body a garment fits, the first body first.")
        command.add_argument("--clip-library", action="append", default=[],
                             help="<file.blend>=<Clip>+<Clip> of an animation library whose clips a garment is checked in.")
        command.add_argument("--manifest", help="The asset.json of a cage or a garment template.")
        command.add_argument("--cage-manifest", help="The asset.json of a garment template's cage.")
        command.add_argument("--texture", help="The name of a garment texture.")
        command.add_argument("--garment-file", help="The .blend file of a texture's template.")
        if name == "export":
            command.add_argument("--target", help="Absolute FBX path; a paint layer has none.")
            command.add_argument("--textures", help="Folder for the images of painted materials.")
    command = commands.add_parser("preview")
    command.add_argument("--output", required=True, help="PNG file to write.")
    command.add_argument("--cell-size", type=int, default=420)
    command.add_argument("--kind", default="static")
    command.add_argument("--garment", help="The name of a garment, whose sheet also shows what it covers.")
    command.add_argument("--band", help="The band of a garment.")
    command.add_argument("--clip-library", action="append", default=[],
                         help="<file.blend>=<Clip>+<Clip> of an animation library whose clips complete a garment's mask.")
    command.add_argument("--roots", help="Comma-separated armatures of the bodies of a cage or a texture, in order.")
    command.add_argument("--manifest", help="The asset.json of a cage, with its standard lines.")
    command.add_argument("--texture", help="The name of a garment texture, shown on its template.")
    command.add_argument("--texture-file", help="The .blend file of the texture.")
    command = commands.add_parser("library-sync")
    command.add_argument("--library", required=True, help="The animation library .blend to build or update.")
    command.add_argument("--armature", required=True, help="The name of the armature the clips are keyed on.")
    command.add_argument("--rig-source", required=True)
    command.add_argument("--rig-root", required=True)
    command.add_argument("--preview", action="append", default=[], help="<file.blend>=<armature> of a body that shows the clips.")
    command.add_argument("--force", action="store_true")
    command = commands.add_parser("new-static")
    command.add_argument("--file", required=True, help="The .blend file of the new static asset.")
    command.add_argument("--roots", required=True, help="Comma-separated root object names.")
    for name in ("garment-create", "garment-sync", "garment-review-file"):
        command = commands.add_parser(name)
        command.add_argument("--file", required=True, help="The template .blend to create, update, or review.")
        command.add_argument("--garment", required=True)
        command.add_argument("--body", action="append", default=[], help="<file.blend>=<armature>, the first body first.")
        if name in ("garment-create", "garment-sync"):
            command.add_argument("--manifest", required=True, help="The template's asset.json, with its band and recipe.")
            command.add_argument("--cage-manifest", required=True, help="The asset.json of the template's cage.")
        if name == "garment-sync":
            command.add_argument("--weights", action="store_true",
                                 help="Transfer every weight from the bodies again, also those painted by hand.")
            command.add_argument("--rebuild", action="store_true",
                                 help="Build the first version again from its recipe, keeping its handmade shape.")
        elif name == "garment-review-file":
            command.add_argument("--output", required=True, help="The review .blend to write.")
    for name in ("texture-create", "texture-sync"):
        command = commands.add_parser(name)
        command.add_argument("--file", required=True, help="The texture .blend to create or update.")
        command.add_argument("--texture", required=True)
        command.add_argument("--garment-file", required=True, help="The .blend file of the texture's template.")
        command.add_argument("--garment", required=True, help="The name of the texture's template.")
        command.add_argument("--label", required=True, help="The armature of the template's first body.")
        if name == "texture-create":
            command.add_argument("--size", type=int, default=1024)
            command.add_argument("--source-file", help="A .blend file whose mesh the texture's look is projected from.")
            command.add_argument("--source-object")
    for name in ("cage-create", "cage-sync"):
        command = commands.add_parser(name)
        command.add_argument("--file", required=True, help="The cage .blend to create or update.")
        command.add_argument("--manifest", required=True, help="The cage's asset.json, with its figure scale and lines.")
        command.add_argument("--body", action="append", default=[], help="<file.blend>=<armature>, the first body first.")
    command = commands.add_parser("paint-sync")
    command.add_argument("--file", required=True, help="The paint layer .blend to build or update.")
    command.add_argument("--paint", required=True)
    command.add_argument("--part", required=True)
    command.add_argument("--body", action="append", default=[], help="<file.blend>=<armature>, the painted body first.")
    command.add_argument("--force", action="store_true")
    return parser


def _paint(args) -> int:
    """Validation and export of a paint layer, which holds images and no exported objects."""
    labels = args.roots.split(",")
    errors = paint.paint_errors(args.paint, args.part, labels)
    for error in errors:
        print(f"ART_PIPELINE_ERROR {error}")
    if errors:
        return 1
    print("ART_VALIDATION paint")
    if args.command == "export":
        for name, path in paint.export_images(args.paint, labels, args.textures):
            print(f"ART_TEXTURE {json.dumps({'name': name, 'path': path}, separators=(',', ':'))}")
        print(f"ART_PAINT {json.dumps(paint.paint_record(args.paint, labels), separators=(',', ':'))}")
    return 0


def _bodies(args) -> list[tuple[str, str]]:
    return [(os.path.abspath(path), root) for path, root in (b.rsplit("=", 1) for b in args.body)]


def _clip_libraries(args) -> list[tuple[str, list[str]]]:
    return [(os.path.abspath(path), clips.split("+")) for path, clips in (e.rsplit("=", 1) for e in args.clip_library)]


def _texture(args) -> int:
    """Validation and export of a garment texture: its image in its template's current UVs, and its
    material."""
    errors = texture.texture_errors(args.texture, os.path.abspath(args.garment_file), args.garment, args.roots.split(",")[0])
    for error in errors:
        print(f"ART_PIPELINE_ERROR {error}")
    if errors:
        return 1
    print(f"ART_VALIDATION texture={texture.image_name(args.texture)}")
    if args.command == "export":
        path, record = texture.export(args.texture, args.textures)
        print(f"ART_TEXTURE {json.dumps({'name': texture.image_name(args.texture), 'path': path}, separators=(',', ':'))}")
        print(f"ART_MATERIAL {json.dumps(record, separators=(',', ':'))}")
        print(f"ART_EXPORT {path}")
    return 0


def _garment(args) -> int:
    """Validation and export of a garment: one version per body, each exported with its body's
    armature from its own collection and with its data: the cover mask, underlay, and pull-in. The
    model stage checks the shape, the fitting, and the band, and reports the mask; the rig stage
    checks the armature and the weights, and reports the skin that comes through the garment in the
    test poses and the clips. The export needs the rig stage."""
    body_list = _bodies(args)
    labels = args.roots.split(",")
    if [root for _path, root in body_list] != labels:
        raise ArtPipelineError("A garment is checked against its bodies; pass one --body per root, in order.")
    band, recipe, _lines, cage_file = garment.read_manifests(args.manifest, args.cage_manifest)
    if band != args.band:
        raise ArtPipelineError(f'The band "{args.band}" is not the band of {args.manifest}.')
    errors = garment.stale_errors(args.garment, args.band, recipe, body_list, cage_file)
    errors += garment.garment_errors(args.garment, labels, args.band, args.triangle_budget)
    versions = [v for v in (bpy.data.objects.get(garment.version_name(args.garment, label)) for label in labels)
                if v is not None]
    if args.stage == "rig":
        skeleton = skeleton_definition.load(args.skeleton)
        for label in labels:
            arm = bpy.data.objects.get(label)
            version = bpy.data.objects.get(garment.version_name(args.garment, label))
            if arm is not None and version is not None:
                errors += skinned.rig_errors([arm, version], [arm], skeleton)
    elif args.stage != "model":
        raise ArtPipelineError(f'A garment has the review stages model and rig, not "{args.stage}".')
    coverages = {}
    if not errors:
        for label in labels:
            coverages[label] = coverage.compute(args.garment, label, args.band)
            if args.stage == "model":
                print(f"ART_REPORT {coverage.summary(args.garment, coverages[label])}")
    if not errors and args.stage == "rig":
        actions = coverage.load_actions(_clip_libraries(args))
        for label in labels:
            motion = coverage.check_motion(args.garment, coverages[label], actions)
            print(f"ART_REPORT {coverage.motion_summary(args.garment, motion)}")
            print(f"ART_REPORT {coverage.summary(args.garment, coverages[label])}")
        for library_data in {a.library for a in actions if a.library is not None}:
            bpy.data.libraries.remove(library_data)
    for error in errors:
        print(f"ART_PIPELINE_ERROR {error}")
    if errors:
        return 1
    triangles = max(sum(len(p.vertices) - 2 for p in v.data.polygons) for v in versions)
    print(f"ART_VALIDATION triangles={triangles}")
    if args.command == "export":
        if args.stage != "rig":
            raise ArtPipelineError("A garment is exported with its rig stage, which checks it in motion.")
        for label, target in garment.export_paths(args.garment, labels, args.target):
            export_fbx("garment", target, garment.collection_name(label))
            data = os.path.splitext(target)[0] + ".json"
            coverage.write_record(data, coverage.record(args.garment, coverages[label]))
            print(f"ART_GARMENT_FBX {json.dumps({'label': label, 'path': target, 'data': data}, separators=(',', ':'))}")
        for record in material_records(versions):
            print(f"ART_MATERIAL {json.dumps(record, separators=(',', ':'))}")
        if args.textures:
            for name, path in export_textures(args.textures, {}, versions):
                print(f"ART_TEXTURE {json.dumps({'name': name, 'path': path}, separators=(',', ':'))}")
        print(f"ART_EXPORT {args.target}")
    return 0


def _cage(args) -> int:
    """Validation of the garment cage, which has one review stage and no export: its bodies and
    fitting are as a sync leaves them, and its versions keep the cage's rules on every body."""
    body_list = _bodies(args)
    labels = args.roots.split(",")
    if [root for _path, root in body_list] != labels:
        raise ArtPipelineError("A cage is checked against its bodies; pass one --body per root, in order.")
    if args.stage != "model":
        raise ArtPipelineError(f'A cage has the review stage model, not "{args.stage}".')
    if args.command == "export":
        raise ArtPipelineError("A cage has no export; garments record its approved file.")
    figure, lines = cage.read_manifest(args.manifest)
    errors = cage.stale_errors(body_list, figure, lines)
    if not errors:
        errors = cage.cage_errors(labels, lines)
    for line in cage.report(labels):
        print(f"ART_REPORT {line}")
    for error in errors:
        print(f"ART_PIPELINE_ERROR {error}")
    if errors:
        return 1
    print(f"ART_VALIDATION faces={len(bpy.data.objects[cage.REFERENCE].data.polygons)}")
    return 0


def main() -> int:
    args = parse_args(_parser())
    if args.command == "preview":
        if args.kind == "texture":
            labels = args.roots.split(",")
            texture.dress(args.garment, os.path.abspath(args.texture_file), args.texture, labels)
            path = preview.render_garment_sheet(os.path.abspath(args.output), args.cell_size)
            print(f"ART_PREVIEW {path}")
            return 0
        if args.kind == "cage":
            _figure, lines = cage.read_manifest(args.manifest)
            path = preview.render_cage_sheet(os.path.abspath(args.output), args.cell_size, args.roots.split(","), lines)
            print(f"ART_PREVIEW {path}")
            return 0
        if args.kind == "garment":
            path = preview.render_garment_sheet(os.path.abspath(args.output), args.cell_size, args.garment, args.band,
                                                _clip_libraries(args))
            print(f"ART_PREVIEW {path}")
            return 0
        render = {"animations": preview.render_clip_sheet, "paint": preview.render_paint_sheet}.get(
            args.kind, preview.render_sheet)
        path = render(os.path.abspath(args.output), args.cell_size)
        print(f"ART_PREVIEW {path}")
        return 0
    if args.command == "paint-sync":
        painted = [tuple(b.rsplit("=", 1)) for b in args.body]
        changed = paint.sync(os.path.abspath(args.file), args.paint, args.part,
                             [(os.path.abspath(path), root) for path, root in painted], args.force)
        print(f"ART_PAINT_SYNC {'synced' if changed else 'up-to-date'} {args.file}")
        return 0
    if args.command == "new-static":
        new_static_file(os.path.abspath(args.file), args.roots.split(","))
        print(f"ART_NEW {args.file}")
        return 0
    if args.command == "garment-review-file":
        path = garment.review_file(os.path.abspath(args.file), [r for _p, r in _bodies(args)], os.path.abspath(args.output))
        print(f"ART_GARMENT_REVIEW {path}")
        return 0
    if args.command in ("garment-create", "garment-sync"):
        body_list = _bodies(args)
        band, recipe, lines, cage_file = garment.read_manifests(args.manifest, args.cage_manifest)
        state = "created"
        if args.command == "garment-create":
            garment.create(os.path.abspath(args.file), args.garment, band, recipe, lines, body_list, cage_file)
        else:
            changed, hand, notes = garment.sync(os.path.abspath(args.file), args.garment, band, recipe, lines, body_list,
                                                cage_file, args.weights, args.rebuild)
            state = "synced" if changed else "up-to-date"
            for note in notes:
                print(f"ART_REPORT {note}")
            if hand:
                print(f"ART_REPORT {hand} vertices keep the weights painted on them by hand.")
        for line in garment.report(args.garment, [r for _p, r in body_list]):
            print(f"ART_REPORT {line}")
        print(f"ART_GARMENT {state} {args.file}")
        return 0
    if args.command in ("cage-create", "cage-sync"):
        body_list = _bodies(args)
        figure, lines = cage.read_manifest(args.manifest)
        state = "created"
        if args.command == "cage-create":
            cage.create(os.path.abspath(args.file), body_list, figure, lines)
        else:
            state = "synced" if cage.sync(os.path.abspath(args.file), body_list, figure, lines) else "up-to-date"
        for line in cage.report([r for _p, r in body_list]):
            print(f"ART_REPORT {line}")
        print(f"ART_CAGE {state} {args.file}")
        return 0
    if args.command in ("texture-create", "texture-sync"):
        state = "created"
        if args.command == "texture-create":
            texture.create(os.path.abspath(args.file), args.texture, os.path.abspath(args.garment_file), args.garment,
                           args.label, args.size, os.path.abspath(args.source_file) if args.source_file else None,
                           args.source_object)
        else:
            moved = texture.sync(os.path.abspath(args.file), args.texture, os.path.abspath(args.garment_file), args.garment,
                                 args.label)
            state = "synced" if moved else "up-to-date"
        print(f"ART_TEXTURE_FILE {state} {args.file}")
        return 0
    if args.command == "library-sync":
        previews = [tuple(p.rsplit("=", 1)) for p in args.preview]
        changed = library.sync(os.path.abspath(args.library), args.armature, os.path.abspath(args.rig_source),
                               args.rig_root, [(os.path.abspath(path), root) for path, root in previews], args.force)
        print(f"ART_LIBRARY {'synced' if changed else 'up-to-date'} {args.library}")
        return 0
    if args.kind == "paint":
        return _paint(args)
    if args.kind == "garment":
        return _garment(args)
    if args.kind == "cage":
        return _cage(args)
    if args.kind == "texture":
        return _texture(args)

    errors, triangles = validate(args.kind, args.roots.split(","), args.triangle_budget, args.stage, args.skeleton,
                                 [c for c in args.clips.split(",") if c],
                                 os.path.abspath(args.rig_source) if args.rig_source else None, args.rig_root)
    variants = parse_variants(args.variants)
    errors += variant_errors(variants)
    for error in errors:
        print(f"ART_PIPELINE_ERROR {error}")
    if errors:
        return 1
    print(f"ART_VALIDATION triangles={triangles}")
    if args.command == "export":
        export_fbx(args.kind, args.target)
        # A body with an editable mesh exports its topology, which garments refer to.
        roots = args.roots.split(",")
        if args.kind == "skinned" and len(roots) == 1 and bodies.source_body(roots[0]) is not None:
            data = os.path.splitext(args.target)[0] + ".json"
            coverage.write_record(data, bodies.topology(roots[0]))
            print(f"ART_DATA {json.dumps({'path': data}, separators=(',', ':'))}")
        for record in material_records():
            print(f"ART_MATERIAL {json.dumps(record, separators=(',', ':'))}")
        if args.textures:
            for name, path in export_textures(args.textures, variants):
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
