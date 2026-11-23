"""Blender entry point of the human base character tools. tools/art/Sync-HumanBase.ps1,
tools/art/Sync-AnimationLibrary.ps1, and tools/art/Import-ArtClip.ps1 run each step in its own Blender:

    blender --background --factory-startup --disable-autoexec --python-exit-code 1 \
        --python tools/art/blender/human_base_cli.py -- <step> <arguments>

Steps:
    sync <asset.blend> <Male|Female> <skeleton.json> <out.blend>
    weights <asset.blend> <Male|Female> <skeleton.json> <out.blend>
    rig <asset.blend> <Male|Female> <skeleton.json> <out.blend>
    check-mesh <male.blend> <female.blend>
    check-seams <male.blend> <female.blend>
    check-parts <male.blend> <female.blend>
    check-rig <before.blend> <after.blend> <Male|Female> <skeleton.json>
    compare <a.blend> <b.blend>
    review-file <male.blend> <female.blend> <review.blend>
    review <review.blend> <output dir> <prefix>
    review-rig <asset.blend> <Male|Female> <skeleton.json> <output dir> <prefix>
    rig-review-file <male.blend> <female.blend> <skeleton.json> <review.blend>
    generate-clips <library.blend> <armature> <Idle,Walk,Jog,Grip> <skeleton.json>
    remake-clips <library.blend> <armature> <Idle,Walk,Jog,Grip> <skeleton.json>
    check-clips <library.blend> <armature>
    import-clip <library.blend> <armature> <source file> <map.json> <Clip+Clip> <Name> <-|L|R|LR> <skeleton.json>
    refit-clips <library.blend> <armature> <library before.blend> <skeleton.json> <Clip,Clip|->

Report lines start with HUMAN_BASE_REPORT, broken rules with HUMAN_BASE_FAILED, the result
of compare is HUMAN_BASE_SAME yes or no, and the motion numbers of an imported clip are
HUMAN_BASE_MOTION and a JSON object, so the PowerShell scripts can read them from Blender's
log.
"""

import json
import os
import sys

# Keep tools/ free of __pycache__ folders.
sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from human_base import animate, checks, retarget, review, rig, rig_review  # noqa: E402

STEPS = {
    "sync": (4, rig.sync),
    "weights": (4, rig.weights),
    "rig": (4, rig.rig),
    "review-file": (3, review.review_file),
    "generate-clips": (4, lambda path, armature, names, skeleton: animate.generate(path, armature, names.split(","), skeleton)),
    "remake-clips": (4, lambda path, armature, names, skeleton: animate.generate(path, armature, names.split(","), skeleton,
                                                                               replace=True)),
}
CHECKS = {
    "check-mesh": (2, checks.mesh),
    "check-seams": (2, checks.seams),
    "check-parts": (2, checks.parts),
    "check-rig": (4, checks.rig),
    "check-clips": (2, checks.clips),
    "compare": (2, checks.compare),
}
RENDERS = {
    "review": (3, review.render),
    "review-rig": (5, rig_review.render),
}
# Steps that add to a file or replace what it has, and report on it.
IMPORTS = {
    "import-clip": (8, retarget.import_clip),
    "refit-clips": (5, retarget.refit),
}


def _count(step: str, args: list[str], count: int) -> bool:
    if len(args) != count:
        print(f"HUMAN_BASE_FAILED {step} takes {count} arguments")
        return False
    return True


def main(argv: list[str]) -> int:
    if not argv:
        print(__doc__)
        return 2
    step, args = argv[0], argv[1:]
    if step in STEPS:
        count, run = STEPS[step]
        if not _count(step, args, count):
            return 2
        run(*args)
        return 0
    if step in CHECKS:
        count, run = CHECKS[step]
        if not _count(step, args, count):
            return 2
        lines, failures = run(*args)
        for line in lines:
            print(f"HUMAN_BASE_REPORT {line}")
        if step == "compare":
            print(f"HUMAN_BASE_SAME {'no' if failures else 'yes'}")
            return 0
        for failure in failures:
            print(f"HUMAN_BASE_FAILED {failure}")
        return 1 if failures else 0
    if step in RENDERS:
        count, run = RENDERS[step]
        if not _count(step, args, count):
            return 2
        for path in run(*args):
            print(f"HUMAN_BASE_REPORT review sheet {path}")
        return 0
    if step in IMPORTS:
        count, run = IMPORTS[step]
        if not _count(step, args, count):
            return 2
        lines, failures, motion = run(*args)
        for line in lines:
            print(f"HUMAN_BASE_REPORT {line}")
        if motion is not None:
            print(f"HUMAN_BASE_MOTION {json.dumps(motion, separators=(',', ':'))}")
        for failure in failures:
            print(f"HUMAN_BASE_FAILED {failure}")
        return 1 if failures else 0
    if step == "rig-review-file":
        if not _count(step, args, 4):
            return 2
        print(f"HUMAN_BASE_REPORT review file {rig_review.review_file(*args)}")
        return 0
    print(f"HUMAN_BASE_FAILED unknown step {step}")
    return 2


exit_code = main(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
sys.stdout.flush()
sys.exit(exit_code)
