"""Blender entry point of the human base character build. tools/Build-HumanBase.ps1 runs each
step in its own Blender:

    blender --background --factory-startup --disable-autoexec --python-exit-code 1 \
        --python tools/blender/human_base_cli.py -- <step> <arguments>

Steps:
    references <bundle.blend> <refs.blend>
    geometry <refs.blend> <geometry.blend>
    textures <geometry.blend> <refs.blend> <textured.blend> <texture dir>
    check-mesh <textured.blend>
    check-seams <textured.blend>
    characters <textured.blend> <characters.blend>
    check-parts <characters.blend>
    asset <characters.blend> <Male|Female> <asset.blend>
    review <characters.blend> <output dir> <prefix>
    compare <a.blend> <b.blend>

Report lines start with HUMAN_BASE_REPORT, broken rules with HUMAN_BASE_FAILED, and the result
of compare is HUMAN_BASE_SAME yes or no, so the PowerShell script can read them from Blender's
log.
"""

import os
import sys

# Keep tools/ free of __pycache__ folders.
sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from human_base import characters, checks, geometry, references, review, textures  # noqa: E402

STEPS = {
    "references": (2, references.prepare),
    "geometry": (2, geometry.build),
    "textures": (4, textures.run),
    "characters": (2, characters.organize),
    "asset": (3, characters.write_asset),
}
CHECKS = {"check-mesh": checks.mesh, "check-seams": checks.seams, "check-parts": checks.parts}


def main(argv: list[str]) -> int:
    if not argv:
        print(__doc__)
        return 2
    step, args = argv[0], argv[1:]
    if step in STEPS:
        count, run = STEPS[step]
        if len(args) != count:
            print(f"HUMAN_BASE_FAILED {step} takes {count} arguments")
            return 2
        run(*args)
        return 0
    if step in CHECKS:
        lines, failures = CHECKS[step](*args)
        for line in lines:
            print(f"HUMAN_BASE_REPORT {line}")
        for failure in failures:
            print(f"HUMAN_BASE_FAILED {failure}")
        return 1 if failures else 0
    if step == "compare":
        lines, differences = checks.compare(*args)
        for line in lines:
            print(f"HUMAN_BASE_REPORT {line}")
        print(f"HUMAN_BASE_SAME {'no' if differences else 'yes'}")
        return 0
    if step == "review":
        for path in review.render(*args):
            print(f"HUMAN_BASE_REPORT review sheet {path}")
        return 0
    print(f"HUMAN_BASE_FAILED unknown step {step}")
    return 2


exit_code = main(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
sys.stdout.flush()
sys.exit(exit_code)
