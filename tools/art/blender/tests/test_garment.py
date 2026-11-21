"""The garment template rules of garment.py that need no Blender: its tables, the names it gives, the
recipe text a file keeps, the reading of an asset.json, checked against every garment of art/, the
turned edges read from a mesh's faces and their rule, and the stable order of a build. See
blender_source.py for how a Blender module is read without Blender."""

import ast
import json
import pathlib
import random
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from blender_source import REPOSITORY_ROOT, load, source_text  # noqa: E402

scene = load("art_pipeline/scene.py", "ArtPipelineError")
cage = load("art_pipeline/cage.py", "REGIONS", "ATTACHED", "SIDED", "read_manifest", ArtPipelineError=scene.ArtPipelineError)
hang = load("art_pipeline/hang.py", "PREFIXES", "SIDES", "hem_height", "_gap", "LEAVE", "EDGE_GAP", "MARGINS", "ROOT_BLEND",
            ArtPipelineError=scene.ArtPipelineError)
garment = load("art_pipeline/garment.py", "BANDS", "OFFSETS", "SLOTS", "SHAPE_KEYS", "UNDER_HEADWEAR", "MAX_INFLUENCES",
               "collection_name", "version_name", "material_name", "image_name", "file_sha256", "recipe_text",
               "recipe_regions", "export_paths", "read_wearing", "read_manifests", "turned_edges", "fold_error",
               "MIN_FOLD", "SETTLE_MARGIN", "_by_index", cage=cage, hang=hang, ArtPipelineError=scene.ArtPipelineError)
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
        self.assertNotIn('"to"', text)

    def test_the_hem_of_a_hang_part_is_kept_and_changes_the_recipe(self):
        part = {"line": "SkirtWaist", "side": "all", "chains": "skirt", "flare": 0.08}
        to_the_tips = garment.recipe_text({**self.BASE, "hang": [part]})
        to_a_line = garment.recipe_text({**self.BASE, "hang": [{**part, "to": "SkirtHem"}]})
        self.assertIn('"to":"SkirtHem"', to_a_line)
        self.assertNotEqual(to_the_tips, to_a_line)

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
                    # A part's hem is a line of the legs, whether or not the template covers them.
                    if "to" in part:
                        self.assertEqual(self.lines[part["to"]]["region"], "legs")

    def test_the_robe_s_skirt_ends_above_the_ankle_so_the_shoes_show(self):
        robe = json.loads((ART / "Garments" / "Robe" / "asset.json").read_text(encoding="utf-8"))
        skirt = next(part for part in robe["recipe"]["hang"] if part["chains"] == "skirt")
        hem, ankle = self.lines[skirt["to"]], self.lines["LegAnkle"]
        self.assertEqual((hem["bone"], ankle["bone"]), ("LowerLeg", "LowerLeg"))
        # A smaller share of the lower leg lies higher on it.
        self.assertLess(hem["share"], ankle["share"])

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


class LeaveTests(unittest.TestCase):
    MARGIN = 0.03

    def test_cloth_leaves_the_body_from_its_edge_s_gap_up_to_the_margin(self):
        self.assertEqual(hang._gap(0.0, self.MARGIN), hang.EDGE_GAP)
        self.assertAlmostEqual(hang._gap(0.02, self.MARGIN), hang.EDGE_GAP + 0.02 * hang.LEAVE)
        self.assertEqual(hang._gap(1.0, self.MARGIN), self.MARGIN)

    def test_a_skirt_from_the_waist_leaves_the_body_evenly_down_to_its_chains(self):
        over = 0.13
        self.assertEqual(hang._gap(0.0, self.MARGIN, over), hang.EDGE_GAP)
        self.assertAlmostEqual(hang._gap(over / 2.0, self.MARGIN, over), (hang.EDGE_GAP + self.MARGIN) / 2.0)
        self.assertAlmostEqual(hang._gap(over, self.MARGIN, over), self.MARGIN)
        self.assertEqual(hang._gap(over * 2.0, self.MARGIN, over), self.MARGIN)
        # It stands off the body less than cloth that leaves it at once, all the way down to there.
        for depth in (0.02, 0.05, 0.1):
            self.assertLess(hang._gap(depth, self.MARGIN, over), hang._gap(depth, self.MARGIN))

    def test_a_skirt_from_just_above_its_chains_leaves_the_body_no_faster_than_other_cloth(self):
        for depth in (0.0, 0.01, 0.03):
            self.assertEqual(hang._gap(depth, self.MARGIN, 0.01), hang._gap(depth, self.MARGIN))

    def test_the_skirt_keeps_room_for_the_thighs_and_blends_into_its_chains_close_to_their_roots(self):
        self.assertGreaterEqual(hang.MARGINS["skirt"], 0.03)
        self.assertTrue(0.0 < hang.ROOT_BLEND < 0.1)


class HemTests(unittest.TestCase):
    PART = {"line": "SkirtWaist", "chains": "skirt", "to": "SkirtHem"}

    def test_a_part_without_a_hem_hangs_to_the_tips_of_its_chains(self):
        self.assertEqual(hang.hem_height(self.PART, 0.085, None), 0.085)

    def test_a_part_ends_at_its_hem_above_the_tips(self):
        self.assertEqual(hang.hem_height(self.PART, 0.085, 0.139), 0.139)
        self.assertEqual(hang.hem_height(self.PART, 0.085, 0.085), 0.085)

    def test_a_hem_below_the_tips_has_no_chain_to_carry_it(self):
        with self.assertRaises(scene.ArtPipelineError) as raised:
            hang.hem_height(self.PART, 0.085, 0.06)
        self.assertIn("SkirtHem", str(raised.exception))
        self.assertIn("2.5 cm", str(raised.exception))


def turn(polygons: list[tuple[int, ...]], count: int) -> tuple[list[tuple[int, ...]], list[tuple[int, int, int]]]:
    """A cloth's faces with its open edges turned as _turn_edges turns them: per vertex of its border a
    rim and a flap, numbered after the cloth's vertices in the order of the border's, and per border
    edge a rim face and a flap face running the edge against the cloth's face. Returns the faces and
    the turned edges as (vertex, rim, flap)."""
    uses: dict[tuple[int, int], int] = {}
    for polygon in polygons:
        for i, a in enumerate(polygon):
            b = polygon[(i + 1) % len(polygon)]
            uses[(min(a, b), max(a, b))] = uses.get((min(a, b), max(a, b)), 0) + 1
    border = sorted({v for key, n in uses.items() if n == 1 for v in key})
    rim = {v: count + 2 * k for k, v in enumerate(border)}
    flap = {v: count + 2 * k + 1 for k, v in enumerate(border)}
    faces = list(polygons)
    for polygon in polygons:
        for i, a in enumerate(polygon):
            b = polygon[(i + 1) % len(polygon)]
            if uses[(min(a, b), max(a, b))] == 1:
                faces += [(b, a, rim[a], rim[b]), (rim[b], rim[a], flap[a], flap[b])]
    return faces, [(v, rim[v], flap[v]) for v in border]


# Three rows of three vertices in four quads, and a ring of four quads, open at both ends.
GRID = [(0, 1, 4, 3), (1, 2, 5, 4), (3, 4, 7, 6), (4, 5, 8, 7)]
RING = [(i, (i + 1) % 4, 4 + (i + 1) % 4, 4 + i) for i in range(4)]


class TurnedEdgeTests(unittest.TestCase):
    def test_every_vertex_of_the_border_is_found_with_its_rim_and_its_flap(self):
        faces, expected = turn(GRID, 9)
        self.assertEqual(garment.turned_edges(faces), expected)
        self.assertEqual([hem for hem, _rim, _flap in expected], [0, 1, 2, 3, 5, 6, 7, 8])

    def test_both_open_ends_of_a_ring_are_found(self):
        faces, expected = turn(RING, 8)
        self.assertEqual(garment.turned_edges(faces), expected)
        self.assertEqual(len(expected), 8)

    def test_the_order_of_the_faces_does_not_matter(self):
        faces, expected = turn(GRID, 9)
        shuffled = list(faces)
        random.Random(7).shuffle(shuffled)
        self.assertEqual(garment.turned_edges(shuffled), expected)

    def test_a_border_that_is_not_turned_is_refused(self):
        with self.assertRaises(scene.ArtPipelineError) as raised:
            garment.turned_edges(GRID)
        self.assertIn("-Rebuild", str(raised.exception))

    def test_a_mesh_without_a_border_has_no_turned_edges(self):
        # A closed box.
        box = [(0, 1, 2, 3), (4, 7, 6, 5), (0, 4, 5, 1), (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)]
        self.assertEqual(garment.turned_edges(box), [])


class FoldRuleTests(unittest.TestCase):
    def test_turned_vertices_inside_their_cloth_keep_the_rule(self):
        self.assertIsNone(garment.fold_error("Shirt_Male", [0.002, garment.MIN_FOLD], [0.002, 0.0]))

    def test_a_thin_fold_names_the_version_the_count_and_the_thinnest_fold(self):
        error = garment.fold_error("Shirt_Male", [0.002, 0.0001, -0.00011], [0.002, 0.002, 0.002])
        self.assertIn('"Shirt_Male"', error)
        self.assertTrue(error.startswith("2 turned vertices lie"))
        self.assertIn(f"{garment.MIN_FOLD * 1000:.1f} mm", error)
        self.assertIn("the thinnest fold is -0.11 mm", error)

    def test_a_flap_outside_its_cloth_breaks_the_rule(self):
        error = garment.fold_error("Cloak_Female", [0.004], [0.004, -0.0004])
        self.assertTrue(error.startswith("1 turned vertex lies outside the cloth of \"Cloak_Female\""))
        self.assertIn("-0.40 mm", error)

    def test_the_least_fold_leaves_room_under_cloth_a_settle_pushed_out_and_under_every_hem(self):
        self.assertGreater(garment.MIN_FOLD, 0.0)
        self.assertLess(garment.MIN_FOLD, garment.SETTLE_MARGIN)
        for name, manifest in manifests().items():
            if isinstance(manifest["recipe"], dict) and manifest["recipe"]["hem"] > 0.0:
                with self.subTest(garment=name):
                    self.assertLess(garment.MIN_FOLD, manifest["recipe"]["hem"])


class _Element:
    def __init__(self, index: int):
        self.index = index


class BuildOrderTests(unittest.TestCase):
    """A rebuild gives its new vertices and faces the same order every time, so it goes over the
    elements of a BMesh by their indices, never over a set of them, whose order follows memory."""

    BUILD = {"art_pipeline/garment.py": ("_cut", "_cut_curved", "_turn_edges"), "art_pipeline/hang.py": ("_bisect",)}

    def test_elements_are_taken_once_each_by_index_whatever_order_they_come_in(self):
        elements = [_Element(i) for i in (5, 1, 4, 0, 3, 2)]
        for seed in range(5):
            given = elements + elements[:3]
            random.Random(seed).shuffle(given)
            self.assertEqual([e.index for e in garment._by_index(given)], [0, 1, 2, 3, 4, 5])

    def test_the_build_goes_over_no_set(self):
        for path, names in self.BUILD.items():
            tree = ast.parse(source_text(path))
            functions = {node.name: node for node in tree.body if isinstance(node, ast.FunctionDef)}
            for name in names:
                for node in ast.walk(functions[name]):
                    if isinstance(node, (ast.For, ast.comprehension)):
                        iterated = [node.iter]
                    elif isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and node.func.id in ("list", "tuple"):
                        iterated = node.args
                    else:
                        continue
                    for value in iterated:
                        with self.subTest(function=name, code=ast.unparse(node)):
                            self.assertNotIsInstance(value, (ast.Set, ast.SetComp))


if __name__ == "__main__":
    unittest.main()
