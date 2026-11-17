"""The loader that reads the pure logic of Blender modules without Blender runs what the source says,
leaves out what needs Blender, and says so where a test reaches it. See blender_source.py."""

import dataclasses
import pathlib
import sys
import tempfile
import textwrap
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

import blender_source  # noqa: E402


class LoaderTests(unittest.TestCase):
    def _load(self, source: str, *names: str, **provided):
        with tempfile.TemporaryDirectory() as folder:
            path = pathlib.Path(folder) / "module.py"
            path.write_text(textwrap.dedent(source), encoding="utf-8", newline="\n")
            # load() reads below the Blender root; point it at the temporary file.
            original = blender_source.BLENDER_ROOT
            blender_source.BLENDER_ROOT = path.parent
            try:
                return blender_source.load("module.py", *names, **provided)
            finally:
                blender_source.BLENDER_ROOT = original
                blender_source._parsed.pop(path, None)

    def test_a_function_runs_with_the_constants_and_functions_it_uses(self):
        loaded = self._load("""
            import math
            import bpy

            SCALE = 3.0

            def half(x: bpy.types.Object) -> float:
                return x / 2

            def scaled(x):
                return half(x) * SCALE + math.floor(0.5)
            """, "scaled")
        self.assertEqual(loaded.scaled(4), 6.0)

    def test_a_definition_that_needs_blender_fails_only_where_it_is_called(self):
        loaded = self._load("""
            import bpy

            HDRI = bpy.utils.system_resource("DATAFILES")

            def pure():
                return 1

            def needs_blender():
                return bpy.data.objects
            """, "pure", "needs_blender")
        self.assertEqual(loaded.pure(), 1)
        with self.assertRaisesRegex(NameError, "bpy"):
            loaded.needs_blender()

    def test_a_requested_constant_that_needs_blender_is_an_error(self):
        with self.assertRaises(NameError):
            self._load("""
                import bpy

                HDRI = bpy.utils.system_resource("DATAFILES")
                """, "HDRI")

    def test_a_name_the_file_does_not_define_is_an_error(self):
        with self.assertRaises(LookupError):
            self._load("X = 1\n", "Y")

    def test_provided_names_stand_for_other_modules(self):
        loaded = self._load("""
            from . import cage

            def regions():
                return cage.REGIONS
            """, "regions", cage=type("Cage", (), {"REGIONS": ("neck",)}))
        self.assertEqual(loaded.regions(), ("neck",))

    def test_a_dataclass_and_its_defaults_load(self):
        loaded = self._load("""
            from dataclasses import dataclass

            @dataclass(frozen=True)
            class Bone:
                name: str
                parent: "Bone | None" = None
            """, "Bone")
        self.assertTrue(dataclasses.is_dataclass(loaded.Bone))
        self.assertIsNone(loaded.Bone("Hips").parent)

    def test_the_real_garment_module_loads_its_naming(self):
        garment = blender_source.load("art_pipeline/garment.py", "version_name", "collection_name")
        self.assertEqual(garment.version_name("Robe", "Male"), "Robe_Male")
        self.assertEqual(garment.collection_name("Male"), "Export_Male")


if __name__ == "__main__":
    unittest.main()
