"""Blender entry point of the game views. tools/art/Render-ArtViews.ps1 runs one Blender per outfit and body,
without an open file, with one job file that names the body, the garments, the paint layers, the clip
library, and every view to render with the files it writes:

    blender --background --factory-startup --python-use-system-env --python-exit-code 1 \
        --python tools/art/blender/art_views_cli.py -- <job.json>

It prints ART_VIEW and the numbers of each view, as JSON, once its pictures and numbers are written,
without the bone heads that its numbers file holds, and ART_PIPELINE_ERROR with the reason when the views
cannot be drawn. See docs/ART_PIPELINE.md, Game Views.
"""

import json
import os
import sys
import time

# Keep tools/ free of __pycache__ folders.
sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from art_pipeline import game_view, manifest_io  # noqa: E402
from art_pipeline.scene import ArtPipelineError  # noqa: E402


def main(argv: list[str]) -> int:
    if len(argv) != 1:
        print("ART_PIPELINE_ERROR art_views_cli.py takes one job file.")
        return 2
    job = manifest_io.read_json(argv[0])
    began = time.perf_counter()
    try:
        for numbers in game_view.render_job(job):
            seconds = round(time.perf_counter() - began, 2)
            shown = {key: value for key, value in numbers.items() if key != "bones"}
            print(f"ART_VIEW {json.dumps(dict(shown, seconds=seconds), sort_keys=True, separators=(',', ':'))}")
            sys.stdout.flush()
            began = time.perf_counter()
    except ArtPipelineError as error:
        print(f"ART_PIPELINE_ERROR {error}")
        return 1
    return 0


exit_code = main(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
sys.stdout.flush()
sys.exit(exit_code)
