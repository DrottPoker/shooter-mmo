"""The garment template rules of garment.py that need no Blender: its tables, the names it gives, the
recipe text a file keeps, and the reading of an asset.json, checked against every garment of art/.
See blender_source.py for how a Blender module is read without Blender."""

import json
import pathlib
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from blender_source import REPOSITORY_ROOT, load  # noqa: E402

scene = load("art_pipeline/scene.py", "ArtPipelineError")
cage = load("art_pipeline/cage.py", "REGIONS", "ATTACHED", "SIDED", "read_manifest", ArtPipelineError=scene.ArtPipelineError)
hang = load("art_pipeline/hang.py", "PREFIXES", "SIDES")
garment = load("art_pipeline/garment.py", "BANDS", "OFFSETS", "SLOTS", "SHAPE_KEYS", "UNDER_HEADWEAR", "MAX_INFLUENCES",
               "collection_name", "version_name", "material_name", "image_name", "file_sha256", "recipe_text",
               "recipe_regions", "export_paths", "read_wearing", "read_manifests", cage=cage, hang=hang,
               ArtPipelineError=scene.ArtPipelineError)
coverage = load("art_pipeline/coverage.py", "REACH", "EDGE_REACH")
texture = load("art_pipeline/texture.py", "MAX_DYES")

ART = REPOSITORY_ROOT / "art"
CAGE_MANIFEST = ART / "Cages" / "Humanoid" / "asset.json"


def manifests() -> dict[str, dict]:
    return {d.name: json.loads((d / "asset.json").read_text(encoding="utf-8")) for d in sorted((ART / "Garments").iterdir())}


class GarmentTableTests(unittest.TestCase):
    def test_every_band_has_a_least_distance_below_its_largest(self):
        for band, (low, high) in garment.BANDS.items():
            with self.subTest(band=band):
                self.assertGreaterEqual(low, 0.0)
                self.assertTrue(high is None or high > low)

    def test_a_recipe_builds_each_band_inside_that_band(self):
        self.assertEqual(set(garment.OFFSETS), set(garment.BANDS))
        for band, offset in garment.OFFSETS.items():
            low, high = garment.BANDS[band]
            with self.subTest(band=band):
                self.assertGreaterEqual(offset, low)
                self.assertTrue(high is None or offset <= high)

    def test_the_bands_are_ordered_by_distance_from_the_body(self):
        order = ["tight", "mid", "outer"]
        for near, far in zip(order, order[1:]):
            self.assertLess(garment.BANDS[near][1], garment.BANDS[far][1])
            self.assertLess(garment.OFFSETS[near], garment.OFFSETS[far])

    def test_coverage_measures_every_band(self):
        self.assertEqual(set(coverage.REACH), set(garment.BANDS))
        self.assertEqual(set(coverage.EDGE_REACH), set(garment.BANDS))

    def test_the_visual_slots_are_the_equipment_slots_other_players_see_and_the_hair(self):
        content = json.loads((REPOSITORY_ROOT / "content" / "characters" / "appearance.json").read_text(encoding="utf-8"))
        visible = set(content["visibleEquipmentSlots"])
        self.assertEqual(set(garment.SLOTS) - {"hair"}, visible - {"main_hand", "off_hand"})

    def test_only_hair_has_a_shape_key_and_it_is_the_one_headwear_presses(self):
        self.assertEqual(garment.SHAPE_KEYS, {"hair": (garment.UNDER_HEADWEAR,)})
        self.assertIn("hair", garment.SLOTS)

    def test_a_vertex_has_at_most_four_influences_as_the_rig_rules_allow(self):
        rig = load("art_pipeline/skinned.py", "MAX_INFLUENCES")
        self.assertEqual(garment.MAX_INFLUENCES, rig.MAX_INFLUENCES)


class GarmentNameTests(unittest.TestCase):
    def test_a_garment_is_named_after_its_template_and_its_body(self):
        self.assertEqual(garment.collection_name("Male"), "Export_Male")
        self.assertEqual(garment.version_name("Robe", "Female"), "Robe_Female")
        self.assertEqual(garment.material_name("WoolRobe"), "M_Garment_WoolRobe")
        self.assertEqual(garment.image_name("WoolRobe"), "T_Garment_WoolRobe")

    def test_the_exported_files_are_named_like_the_versions(self):
        self.assertEqual(garment.export_paths("Robe", ["Male", "Female"], "out"),
                         [("Male", str(pathlib.Path("out") / "Robe_Male.fbx")),
                          ("Female", str(pathlib.Path("out") / "Robe_Female.fbx"))])

    def test_the_recorded_exports_of_every_garment_are_named_as_the_code_names_them(self):
        for name, manifest in manifests().items():
            exported = manifest["export"]["models"]
            expected = garment.export_paths(name, [m["body"] for m in exported], "src/Client/Assets/Art/Models/Garments")
            for (label, path), model in zip(expected, exported):
                with self.subTest(garment=name, body=label):
                    self.assertEqual(pathlib.PurePath(path).name, pathlib.PurePosixPath(model["target"]).name)
                    self.assertEqual(pathlib.PurePath(path).with_suffix(".json").name,
                                     pathlib.PurePosixPath(model["data"]).name)

    def test_the_recorded_materials_and_images_of_every_texture_are_named_as_the_code_names_them(self):
        for folder in sorted((ART / "GarmentTextures").iterdir()):
            manifest = json.loads((folder / "asset.json").read_text(encoding="utf-8"))
            materials = [m["name"] for m in manifest["export"].get("materials", [])]
            with self.subTest(texture=folder.name):
                self.assertEqual(materials, [garment.material_name(folder.name)])


class RecipeTextTests(unittest.TestCase):
    BASE = {"regions": ["trunk"], "cuts": [{"line": "Waist", "keep": "head"}], "ease": 0, "hem": 0.002}

    def test_numbers_are_kept_as_floats_and_keys_in_order_without_spaces(self):
        self.assertEqual(garment.recipe_text(self.BASE),
                         '{"cuts":[{"keep":"head","line":"Waist"}],"ease":0.0,"hem":0.002,"regions":["trunk"]}')

    def test_zero_and_zero_point_zero_read_alike(self):
        self.assertEqual(garment.recipe_text({**self.BASE, "ease": 0}), garment.recipe_text({**self.BASE, "ease": 0.0}))
        self.assertEqual(garment.recipe_text({**self.BASE, "hem": 1}), garment.recipe_text({**self.BASE, "hem": 1.0}))

    def test_an_empty_over_or_hang_reads_like_none(self):
        plain = garment.recipe_text(self.BASE)
        self.assertEqual(garment.recipe_text({**self.BASE, "over": []}), plain)
        self.assertEqual(garment.recipe_text({**self.BASE, "hang": []}), plain)
        self.assertNotIn("over", plain)
        self.assertNotIn("hang", plain)

    def test_a_filled_over_stays(self):
        over = [{"line": "Waist", "keep": "head", "band": "tight"}]
        self.assertIn('"over":[{"band":"tight","keep":"head","line":"Waist"}]', garment.recipe_text({**self.BASE, "over": over}))

    def test_the_numbers_of_a_hang_part_are_floats(self):
        part = {"line": "SkirtWaist", "side": "all", "chains": "skirt", "flare": 1, "top": 1, "width": 1, "extra": 2}
        text = garment.recipe_text({**self.BASE, "hang": [part]})
        self.assertIn('"flare":1.0', text)
        self.assertIn('"top":1.0', text)
        self.assertIn('"width":1.0', text)
        # What is not a measure of the part stays as it is.
        self.assertIn('"extra":2', text)

    def test_a_hang_part_without_a_measure_gains_none(self):
        part = {"line": "SkirtWaist", "side": "all", "chains": "skirt", "flare": 0.08}
        text = garment.recipe_text({**self.BASE, "hang": [part]})
        self.assertNotIn('"top"', text)
        self.assertNotIn('"width"', text)

    def test_a_modeled_template_keeps_its_word(self):
        self.assertEqual(garment.recipe_text("modeled"), '"modeled"')

    def test_the_text_of_every_recipe_is_what_it_reads_back_as(self):
        for name, manifest in manifests().items():
            text = garment.recipe_text(manifest["recipe"])
            with self.subTest(garment=name):
                self.assertEqual(garment.recipe_text(json.loads(text)), text)

    def test_the_recipe_is_not_changed_by_reading_its_text(self):
        recipe = {**self.BASE, "over": []}
        garment.recipe_text(recipe)
        self.assertEqual(recipe["ease"], 0)
        self.assertIn("over", recipe)


class RecipeRegionsTests(unittest.TestCase):
    def test_a_template_is_bound_to_every_region_but_the_extremities_it_does_not_cover(self):
        self.assertEqual(garment.recipe_regions({"regions": ["neck", "trunk", "arms"]}), ("neck", "trunk", "arms", "legs"))

    def test_a_covered_extremity_is_bound_too(self):
        self.assertEqual(garment.recipe_regions({"regions": ["legs", "feet"]}), ("neck", "trunk", "arms", "legs", "feet"))
        self.assertEqual(garment.recipe_regions({"regions": ["arms", "hands"]}), ("neck", "trunk", "arms", "legs", "hands"))
        self.assertEqual(garment.recipe_regions({"regions": ["neck", "head"]}), ("neck", "trunk", "arms", "legs", "head"))

    def test_a_modeled_template_may_lie_on_any_region(self):
        self.assertIsNone(garment.recipe_regions("modeled"))

    def test_the_regions_keep_the_order_of_the_cage(self):
        for name, manifest in manifests().items():
            regions = garment.recipe_regions(manifest["recipe"])
            if regions is not None:
                with self.subTest(garment=name):
                    self.assertEqual(list(regions), [r for r in cage.REGIONS if r in regions])


class ManifestReadingTests(unittest.TestCase):
    def test_the_band_recipe_lines_and_cage_file_of_a_real_template_are_read(self):
        band, recipe, lines, cage_file = garment.read_manifests(str(ART / "Garments" / "Robe" / "asset.json"), str(CAGE_MANIFEST))
        self.assertEqual(band, "tight")
        self.assertEqual(recipe["regions"], ["neck", "trunk", "arms"])
        self.assertIn("SkirtWaist", [line["name"] for line in lines])
        self.assertEqual(pathlib.Path(cage_file), ART / "Cages" / "Humanoid" / "Humanoid.blend")

    def test_a_modeled_template_reads_as_the_word(self):
        _band, recipe, _lines, _file = garment.read_manifests(str(ART / "Garments" / "NasalHelm" / "asset.json"), str(CAGE_MANIFEST))
        self.assertEqual(recipe, "modeled")

    def test_the_cage_file_is_the_blend_named_after_the_cage_folder(self):
        with tempfile.TemporaryDirectory() as folder:
            root = pathlib.Path(folder)
            (root / "Cage").mkdir()
            (root / "Cage" / "asset.json").write_text(json.dumps({"figure": 0.06, "lines": []}), encoding="utf-8")
            (root / "asset.json").write_text(json.dumps({"band": "mid", "recipe": "modeled"}), encoding="utf-8")
            _band, _recipe, lines, cage_file = garment.read_manifests(str(root / "asset.json"), str(root / "Cage" / "asset.json"))
        self.assertEqual(lines, [])
        self.assertEqual(pathlib.Path(cage_file).name, "Cage.blend")
        self.assertEqual(pathlib.Path(cage_file).parent.name, "Cage")

    def test_the_slot_and_the_hair_rule_of_a_real_template_are_read(self):
        self.assertEqual(garment.read_wearing(str(ART / "Garments" / "HoodUp" / "asset.json")), ("head", "compress"))
        self.assertEqual(garment.read_wearing(str(ART / "Garments" / "HoodDown" / "asset.json")), ("head", "show"))
        self.assertEqual(garment.read_wearing(str(ART / "Garments" / "Robe" / "asset.json")), ("body", None))

    def test_the_sha256_of_a_file_is_its_digest_whatever_its_size(self):
        with tempfile.TemporaryDirectory() as folder:
            small, large = pathlib.Path(folder) / "small", pathlib.Path(folder) / "large"
            small.write_bytes(b"abc")
            large.write_bytes(b"x" * ((1 << 20) * 2 + 5))
            self.assertEqual(garment.file_sha256(str(small)),
                             "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")
            import hashlib
            self.assertEqual(garment.file_sha256(str(large)), hashlib.sha256(large.read_bytes()).hexdigest())


class GarmentManifestAgreeWithTheCodeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.manifests = manifests()
        cage_manifest = json.loads(CAGE_MANIFEST.read_text(encoding="utf-8"))
        cls.lines = {line["name"]: line for line in cage_manifest["lines"]}

    def test_every_garment_has_a_band_slot_and_hair_rule_the_code_knows(self):
        for name, manifest in self.manifests.items():
            with self.subTest(garment=name):
                self.assertIn(manifest["band"], garment.BANDS)
                self.assertIn(manifest["slot"], garment.SLOTS)
                self.assertIn(manifest["hair"], (None, "hide", "compress", "show"))

    def test_a_template_that_presses_hair_is_headwear(self):
        for name, manifest in self.manifests.items():
            if manifest["hair"] is not None:
                with self.subTest(garment=name):
                    self.assertEqual(manifest["slot"], "head")

    def test_every_recipe_names_regions_and_lines_of_the_cage(self):
        for name, manifest in self.manifests.items():
            recipe = manifest["recipe"]
            if recipe == "modeled":
                continue
            with self.subTest(garment=name):
                self.assertTrue(set(recipe["regions"]) <= set(cage.REGIONS))
                for use in recipe["cuts"] + recipe.get("over", []) + recipe.get("hang", []):
                    self.assertIn(use["line"], self.lines)
                for cut in recipe["cuts"]:
                    self.assertIn(cut["keep"], ("head", "tail"))
                for over in recipe.get("over", []):
                    self.assertIn(over["keep"], ("head", "tail"))
                    self.assertIn(over["band"], garment.OFFSETS)
                for part in recipe.get("hang", []):
                    self.assertIn(part["chains"], hang.PREFIXES)
                    self.assertIn(part["side"], hang.SIDES)

    def test_every_cut_line_lies_in_a_region_the_recipe_builds(self):
        for name, manifest in self.manifests.items():
            recipe = manifest["recipe"]
            if recipe == "modeled":
                continue
            bound = garment.recipe_regions(recipe)
            for cut in recipe["cuts"]:
                with self.subTest(garment=name, line=cut["line"]):
                    self.assertIn(self.lines[cut["line"]]["region"], bound)

    def test_a_template_with_dyes_asks_for_at_most_the_dye_channels_of_a_texture(self):
        for name, manifest in self.manifests.items():
            with self.subTest(garment=name):
                self.assertLessEqual(manifest["dyes"], texture.MAX_DYES)


if __name__ == "__main__":
    unittest.main()
