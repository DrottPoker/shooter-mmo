"""Reading another asset's asset.json while the PowerShell scripts replace it: manifest_io.read_json
tries again while Windows refuses the file and gives up after its time. Runs without Blender: see
blender_source.py."""

import io
import json
import pathlib
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from blender_source import BLENDER_ROOT, load, source_text  # noqa: E402

manifest_io = load("art_pipeline/manifest_io.py", "read_json", "WAIT_SECONDS", "PAUSE_SECONDS")


class Clock:
    """A clock that a sleep moves on, so the tests wait for nothing."""

    def __init__(self):
        self.now = 0.0
        self.sleeps = []

    def time(self) -> float:
        return self.now

    def sleep(self, seconds: float) -> None:
        self.sleeps.append(seconds)
        self.now += seconds


def refusing(times: int, text: str = '{"slot": "head"}'):
    """An opener that refuses the file the first times, as Windows does while it is replaced."""
    calls = []

    def opener(path, encoding):
        calls.append((path, encoding))
        if len(calls) <= times:
            raise PermissionError(13, "The process cannot access the file", path)
        return io.StringIO(text)

    return opener, calls


class ReadJsonTests(unittest.TestCase):
    def test_a_file_reads_as_its_json(self):
        with tempfile.TemporaryDirectory() as folder:
            path = pathlib.Path(folder) / "asset.json"
            path.write_text(json.dumps({"band": "tight", "layer": 100}), encoding="utf-8")
            self.assertEqual(manifest_io.read_json(str(path)), {"band": "tight", "layer": 100})

    def test_a_refused_file_is_read_again_until_it_opens(self):
        clock = Clock()
        opener, calls = refusing(3)
        self.assertEqual(manifest_io.read_json("asset.json", wait=1.0, pause=0.05, opener=opener, clock=clock.time,
                                               sleep=clock.sleep), {"slot": "head"})
        self.assertEqual(len(calls), 4)
        self.assertEqual(clock.sleeps, [0.05, 0.05, 0.05])
        self.assertEqual(calls[0], ("asset.json", "utf-8"))

    def test_a_file_refused_longer_than_the_wait_raises_its_permission_error(self):
        clock = Clock()
        opener, calls = refusing(1000)
        with self.assertRaises(PermissionError):
            manifest_io.read_json("asset.json", wait=0.2, pause=0.05, opener=opener, clock=clock.time, sleep=clock.sleep)
        self.assertGreaterEqual(clock.now, 0.2)
        self.assertLess(len(calls), 10)

    def test_any_other_error_is_raised_at_once(self):
        clock = Clock()
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaises(FileNotFoundError):
                manifest_io.read_json(str(pathlib.Path(folder) / "missing.json"), clock=clock.time, sleep=clock.sleep)
            broken = pathlib.Path(folder) / "asset.json"
            broken.write_text("{", encoding="utf-8")
            with self.assertRaises(ValueError):
                manifest_io.read_json(str(broken), clock=clock.time, sleep=clock.sleep)
        self.assertEqual(clock.sleeps, [])

    def test_the_wait_is_a_few_seconds(self):
        self.assertGreaterEqual(manifest_io.WAIT_SECONDS, 1.0)
        self.assertLessEqual(manifest_io.WAIT_SECONDS, 10.0)
        self.assertLess(manifest_io.PAUSE_SECONDS, manifest_io.WAIT_SECONDS)

    def test_every_read_of_an_asset_json_in_the_pipeline_goes_through_it(self):
        """The readers of asset.json files take read_json; only the skeleton definitions, which no script
        writes during a run, are read with json.load."""
        for path in sorted((BLENDER_ROOT / "art_pipeline").glob("*.py")) + [BLENDER_ROOT / "art_pipeline_cli.py"]:
            relative = path.relative_to(BLENDER_ROOT).as_posix()
            if relative in ("art_pipeline/manifest_io.py", "art_pipeline/skeleton.py", "art_pipeline/cloth_core.py"):
                continue
            text = source_text(relative)
            with self.subTest(module=relative):
                self.assertNotIn("json.load(", text)
                self.assertNotIn('/ "asset.json").read_text', text)
        self.assertIn('recipe = manifest_io.read_json(args.manifest)["recipe"]', source_text("art_pipeline_cli.py"))
        for relative, reader in (("art_pipeline/cage.py", "def read_manifest"), ("art_pipeline/garment.py", "def read_wearing"),
                                 ("art_pipeline/garment.py", "def read_manifests"), ("art_pipeline/garment.py", "def read_stiff"),
                                 ("art_pipeline/check_cache.py", "def manifest_fields"),
                                 ("art_pipeline/check_cache.py", "def source_problem"), ("art_pipeline_cli.py", "def _recipe_next_to")):
            text = source_text(relative)
            body = text[text.index(reader):]
            body = body[:body.find("\ndef ", 1) if "\ndef " in body[1:] else len(body)]
            with self.subTest(reader=reader):
                self.assertIn("manifest_io.read_json(", body)


if __name__ == "__main__":
    unittest.main()
