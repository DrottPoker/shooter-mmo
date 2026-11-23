"""Reads the asset.json of an asset while the PowerShell scripts may replace it. Export-ArtAsset.ps1
writes each asset.json whole, to a file beside it that then takes its place, while the Blender runs of
other assets read it; for that moment Windows refuses to open the file, with PermissionError, so a
read tries again for a few seconds before it gives up.

Standard library only, so the tests run it without Blender. See docs/ART_PIPELINE.md, Review, Approve,
And Export An Asset."""

import json
import time

# How long a read tries again while the file is being replaced, and how long it waits between tries.
WAIT_SECONDS = 5.0
PAUSE_SECONDS = 0.05


def read_json(path, wait: float = WAIT_SECONDS, pause: float = PAUSE_SECONDS, opener=open, clock=time.monotonic,
              sleep=time.sleep):
    """The JSON of the file, read again while opening it raises PermissionError, for at most wait
    seconds, after which the PermissionError is raised; any other error is raised at once."""
    deadline = clock() + wait
    while True:
        try:
            with opener(path, encoding="utf-8") as handle:
                return json.load(handle)
        except PermissionError:
            if clock() >= deadline:
                raise
            sleep(pause)
