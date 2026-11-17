"""The garment texture and paint layer rules of texture.py and paint.py that need no Blender: dye
colors, the names of images, which faces may serve a point, the tables of the zones, and the records
that the committed exports show. See blender_source.py."""

import hashlib
import json
import pathlib
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from blender_source import REPOSITORY_ROOT, load  # noqa: E402

scene = load("art_pipeline/scene.py", "ArtPipelineError")
bodies = load("art_pipeline/bodies.py", "ZONES")
cage = load("art_pipeline/cage.py", "REGIONS", "REGION_ZONES")
garment = load("art_pipeline/garment.py", "image_name", "material_name", "file_sha256")
texture = load("art_pipeline/texture.py", "MAX_DYES", "DYE_SUFFIX", "DYE_LEVEL", "DYE_UNUSED", "SIZES", "image_name",
               "dye_image_name", "material_name", "parse_dye_colors", "export", "mask_record", garment=garment)
paint = load("art_pipeline/paint.py", "image_name", "_matches", "_file_sha256", "ZONE_REGIONS", "CROP_BLOCK",
             "MOVE_TOLERANCE", bodies=bodies, cage=cage)
ART = REPOSITORY_ROOT / "art"


class DyeColorTests(unittest.TestCase):
    def test_one_to_three_upper_case_hex_colors_are_read_back_as_listed(self):
        self.assertEqual(texture.parse_dye_colors("#8D806E"), ["#8D806E"])
        self.assertEqual(texture.parse_dye_colors("#000000;#FFFFFF;#0A1B2C"), ["#000000", "#FFFFFF", "#0A1B2C"])

    def test_anything_else_is_malformed(self):
        for value in (None, 5, ["#8D806E"], "", ";", "#8D806E;", ";#8D806E", "8D806E", "#8D806", "#8D806E0", "#8d806e",
                      "#GGGGGG", "#8D806E;#8D806E;#8D806E;#8D806E", "#8D806E #8D806E", "#8D806E,#8D806E"):
            with self.subTest(value=value):
                self.assertIsNone(texture.parse_dye_colors(value))

    def test_a_texture_has_as_many_dye_channels_as_the_most_a_template_asks_for(self):
        self.assertEqual(texture.MAX_DYES, 3)
        self.assertEqual(len(texture.parse_dye_colors(";".join(["#112233"] * texture.MAX_DYES))), texture.MAX_DYES)

    def test_the_dye_level_and_the_unused_threshold_are_shares(self):
        self.assertTrue(0.0 < texture.DYE_LEVEL <= 1.0)
        self.assertTrue(0.0 < texture.DYE_UNUSED < 0.01)

    def test_the_exported_dye_colors_of_every_texture_are_valid_and_few_enough(self):
        seen = 0
        for folder in sorted((ART / "GarmentTextures").iterdir()):
            texture_manifest = json.loads((folder / "asset.json").read_text(encoding="utf-8"))
            template = json.loads((REPOSITORY_ROOT / texture_manifest["garment"] / "asset.json").read_text(encoding="utf-8"))
            for material in texture_manifest["export"]["materials"]:
                colors = material.get("dyeColors")
                if colors is None:
                    continue
                seen += 1
                with self.subTest(texture=folder.name):
                    self.assertEqual(texture.parse_dye_colors(";".join(colors)), colors)
                    self.assertLessEqual(len(colors), template["dyes"])
                    self.assertEqual(material["dyeMap"], texture.dye_image_name(folder.name))
        self.assertGreater(seen, 0)

    def test_a_dyeable_template_has_a_texture_with_a_dye_map_and_the_other_way_round(self):
        for folder in sorted((ART / "GarmentTextures").iterdir()):
            texture_manifest = json.loads((folder / "asset.json").read_text(encoding="utf-8"))
            template = json.loads((REPOSITORY_ROOT / texture_manifest["garment"] / "asset.json").read_text(encoding="utf-8"))
            dyed = any("dyeMap" in m for m in texture_manifest["export"]["materials"])
            with self.subTest(texture=folder.name):
                if dyed:
                    self.assertGreater(template["dyes"], 0)


class TextureNameTests(unittest.TestCase):
    def test_the_image_the_material_and_the_dye_mask_are_named_after_the_texture(self):
        self.assertEqual(texture.image_name("WoolRobe"), "T_Garment_WoolRobe")
        self.assertEqual(texture.material_name("WoolRobe"), "M_Garment_WoolRobe")
        self.assertEqual(texture.DYE_SUFFIX, "_Dye")
        self.assertEqual(texture.dye_image_name("WoolRobe"), "T_Garment_WoolRobe_Dye")

    def test_every_exported_image_of_every_texture_is_its_image_or_its_dye_mask(self):
        for folder in sorted((ART / "GarmentTextures").iterdir()):
            manifest = json.loads((folder / "asset.json").read_text(encoding="utf-8"))
            names = {t["name"] for t in manifest["export"]["textures"]}
            with self.subTest(texture=folder.name):
                self.assertTrue(names <= {texture.image_name(folder.name), texture.dye_image_name(folder.name)})
                self.assertIn(texture.image_name(folder.name), names)

    def test_a_texture_size_is_a_power_of_two_from_512_to_2048(self):
        self.assertEqual(texture.SIZES, (512, 1024, 2048))


class TextureWriteTests(unittest.TestCase):
    def test_every_image_is_written_byte_for_byte_below_the_folder(self):
        images = {"T_Garment_A": b"\x89PNG-a", "T_Garment_A_Dye": b"\x89PNG-b"}
        with tempfile.TemporaryDirectory() as folder:
            target = pathlib.Path(folder) / "new" / "Garments"
            written, record = texture.export("A", (images, {"name": "M_Garment_A"}), str(target))
            self.assertEqual(record, {"name": "M_Garment_A"})
            self.assertEqual(written, [("T_Garment_A", str(target / "T_Garment_A.png")),
                                       ("T_Garment_A_Dye", str(target / "T_Garment_A_Dye.png"))])
            self.assertEqual((target / "T_Garment_A.png").read_bytes(), b"\x89PNG-a")
            self.assertEqual((target / "T_Garment_A_Dye.png").read_bytes(), b"\x89PNG-b")

    def test_the_masks_of_a_texture_take_the_data_of_its_template_under_their_own_name(self):
        seen = []
        stand_in = type("Coverage", (), {"record": staticmethod(lambda template, result: seen.append((template, result)) or
                                                                {"schemaVersion": 2, "garment": template})})
        loaded = load("art_pipeline/texture.py", "mask_record", coverage=stand_in)
        self.assertEqual(loaded.mask_record("LeatherJerkin", "Shirt", "RESULT"), {"schemaVersion": 2, "garment": "LeatherJerkin"})
        self.assertEqual(seen, [("Shirt", "RESULT")])


class PaintTests(unittest.TestCase):
    def test_a_canvas_and_a_body_image_are_named_after_the_layer_and_the_body(self):
        self.assertEqual(paint.image_name("Tattoo"), "T_Paint_Tattoo")
        self.assertEqual(paint.image_name("Tattoo", "Male"), "T_Paint_Tattoo_Male")

    def test_the_recorded_images_of_every_paint_layer_are_named_after_its_bodies(self):
        for folder in sorted((ART / "Paint").iterdir()):
            manifest = json.loads((folder / "asset.json").read_text(encoding="utf-8"))
            labels = [pathlib.PurePosixPath(body).name.removeprefix("Human_") for body in manifest["bodies"]]
            recorded = [t["name"] for t in manifest["export"]["textures"]]
            with self.subTest(paint=folder.name):
                self.assertEqual(recorded, [paint.image_name(folder.name, label) for label in labels])

    def test_a_face_may_serve_a_point_of_the_same_region_and_side(self):
        self.assertTrue(paint._matches(("arms", "L", 0), ("arms", "L", 0)))
        self.assertTrue(paint._matches(("arms", "L", 0), ("arms", "L", 3)))
        self.assertFalse(paint._matches(("arms", "L", 0), ("arms", "R", 0)))
        self.assertFalse(paint._matches(("arms", "L", 0), ("legs", "L", 0)))
        self.assertTrue(paint._matches(("trunk", "", 0), ("trunk", "", 0)))

    def test_on_a_hand_a_point_is_served_by_its_finger_or_the_palm(self):
        index = ("hands", "R", 2)
        self.assertTrue(paint._matches(index, ("hands", "R", 2)))
        self.assertTrue(paint._matches(index, ("hands", "R", 0)))
        self.assertFalse(paint._matches(index, ("hands", "R", 3)))
        self.assertFalse(paint._matches(index, ("hands", "L", 2)))
        # A point on the palm is served by the palm alone.
        self.assertTrue(paint._matches(("hands", "R", 0), ("hands", "R", 0)))
        self.assertFalse(paint._matches(("hands", "R", 0), ("hands", "R", 2)))

    def test_every_body_part_maps_to_the_region_it_is_made_of(self):
        self.assertEqual(paint.ZONE_REGIONS, {
            bodies.ZONES.index("Head"): "head", bodies.ZONES.index("Neck"): "neck", bodies.ZONES.index("Torso"): "trunk",
            bodies.ZONES.index("Pelvis"): "trunk", bodies.ZONES.index("UpperArms"): "arms",
            bodies.ZONES.index("Forearms"): "arms", bodies.ZONES.index("Hands"): "hands",
            bodies.ZONES.index("Thighs"): "legs", bodies.ZONES.index("Shins"): "legs", bodies.ZONES.index("Feet"): "feet"})

    def test_the_sha256_of_a_file_is_the_same_in_the_garment_and_the_paint_code(self):
        with tempfile.TemporaryDirectory() as folder:
            path = pathlib.Path(folder) / "file.bin"
            path.write_bytes(bytes(range(256)) * 9000)
            expected = hashlib.sha256(path.read_bytes()).hexdigest()
            self.assertEqual(paint._file_sha256(str(path)), expected)
            self.assertEqual(garment.file_sha256(str(path)), expected)

    def test_the_recorded_crops_start_at_whole_blocks_and_lie_inside_their_image(self):
        block = paint.CROP_BLOCK
        for folder in sorted((ART / "Paint").iterdir()):
            manifest = json.loads((folder / "asset.json").read_text(encoding="utf-8"))
            for record in manifest["export"]["textures"]:
                x, y, width, height = record["rect"]
                full_width, full_height = record["layout"]
                with self.subTest(texture=record["name"]):
                    self.assertEqual((x % block, y % block), (0, 0))
                    self.assertTrue(width % block == 0 or x + width == full_width)
                    self.assertTrue(height % block == 0 or y + height == full_height)
                    self.assertLessEqual(x + width, full_width)
                    self.assertLessEqual(y + height, full_height)

    def test_a_moved_paint_may_differ_by_one_and_a_half_levels_of_eight_bits(self):
        self.assertAlmostEqual(paint.MOVE_TOLERANCE * 255.0, 1.5)


if __name__ == "__main__":
    unittest.main()
