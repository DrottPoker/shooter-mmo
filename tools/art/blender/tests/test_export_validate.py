"""The export and validation rules of export.py and validate.py that need no Blender: the variants
argument, the sRGB conversion of the recorded colors, the name and transform rules, and the kinds and
review stages, checked against every asset.json of art/. See blender_source.py."""

import json
import math
import pathlib
import sys
import types
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from blender_source import REPOSITORY_ROOT, load  # noqa: E402

scene = load("art_pipeline/scene.py", "ArtPipelineError")
validate = load("art_pipeline/validate.py", "STAGES", "OBJECT_TYPES", "RIGGED_OBJECT_TYPES", "ARMATURE_KINDS",
                "TEXTURED_KINDS", "NAME_PATTERN", "MATERIAL_PATTERN", "GRIP_ROOT", "TOLERANCE", "EMISSION_INPUTS",
                "_is_identity_scale", "_is_at_origin")
export = load("art_pipeline/export.py", "OBJECT_TYPES", "ANIMATED_KINDS", "PNG_SIGNATURE", "FBX_TIME", "parse_variants",
              "_linear_to_srgb", "_emission", "_image_file_name")
poses = load("art_pipeline/poses.py", "exact_rest")
ART = REPOSITORY_ROOT / "art"


def manifests() -> dict[str, dict]:
    return {path.parent.name: json.loads(path.read_text(encoding="utf-8")) for path in sorted(ART.glob("*/*/asset.json"))}


class VariantArgumentTests(unittest.TestCase):
    def test_materials_and_their_images_are_split_at_the_signs(self):
        self.assertEqual(export.parse_variants("M_Face=T_A+T_B;M_Eye=T_C"), {"M_Face": ["T_A", "T_B"], "M_Eye": ["T_C"]})

    def test_nothing_or_nothing_but_separators_is_no_variants(self):
        for text in (None, "", ";", ";;"):
            with self.subTest(text=text):
                self.assertEqual(export.parse_variants(text), {})

    def test_empty_images_are_dropped_and_a_material_without_images_keeps_its_name(self):
        self.assertEqual(export.parse_variants("M_A=T_1+;M_B=;"), {"M_A": ["T_1"], "M_B": []})

    def test_an_image_name_may_hold_an_equals_sign_after_the_first(self):
        self.assertEqual(export.parse_variants("M_A=T_1=x"), {"M_A": ["T_1=x"]})

    def test_a_material_named_twice_keeps_the_last(self):
        self.assertEqual(export.parse_variants("M_A=T_1;M_A=T_2"), {"M_A": ["T_2"]})

    def test_an_entry_without_a_material_is_an_error_not_a_guess(self):
        with self.assertRaises(ValueError):
            export.parse_variants("T_1+T_2")

    def test_the_variants_of_every_character_read_as_the_script_writes_them(self):
        # ArtPipelineCommon.ps1 joins each material's images with "+" and the materials with ";".
        for name, manifest in manifests().items():
            variants = manifest.get("variants")
            if not variants:
                continue
            text = ";".join(f"{v['material']}={'+'.join(v['images'])}" for v in variants)
            with self.subTest(asset=name):
                self.assertEqual(export.parse_variants(text), {v["material"]: v["images"] for v in variants})


class ColorTests(unittest.TestCase):
    def test_black_and_white_stay_and_the_range_is_clamped(self):
        self.assertEqual(export._linear_to_srgb(0.0), 0.0)
        self.assertAlmostEqual(export._linear_to_srgb(1.0), 1.0, places=12)
        self.assertEqual(export._linear_to_srgb(-3.0), 0.0)
        self.assertAlmostEqual(export._linear_to_srgb(7.0), 1.0, places=12)

    def test_the_dark_end_is_a_straight_line_and_it_joins_the_curve(self):
        self.assertAlmostEqual(export._linear_to_srgb(0.002), 0.002 * 12.92, places=12)
        below = export._linear_to_srgb(0.0031308 - 1e-9)
        above = export._linear_to_srgb(0.0031308 + 1e-9)
        self.assertAlmostEqual(below, above, places=6)

    def test_middle_grey_is_where_the_srgb_standard_puts_it(self):
        # sRGB 0.5 is linear 0.214041, and sRGB 0.735357 is linear 0.5.
        self.assertAlmostEqual(export._linear_to_srgb(0.2140411), 0.5, places=5)
        self.assertAlmostEqual(export._linear_to_srgb(0.5), 0.7353569, places=6)

    def test_the_curve_only_rises(self):
        values = [export._linear_to_srgb(i / 200) for i in range(201)]
        self.assertEqual(values, sorted(values))

    def test_the_inverse_in_the_preview_returns_the_value(self):
        preview = load("art_pipeline/preview.py", "_srgb_to_linear")
        for value in (0.0, 0.001, 0.05, 0.2140411, 0.5, 0.9, 1.0):
            with self.subTest(value=value):
                self.assertAlmostEqual(preview._srgb_to_linear(export._linear_to_srgb(value)), value, places=9)

    @staticmethod
    def _bsdf(color, strength):
        # The two sockets _emission reads, as plain values.
        socket = types.SimpleNamespace
        return types.SimpleNamespace(inputs={"Emission Color": socket(default_value=color),
                                             "Emission Strength": socket(default_value=strength)})

    def test_a_material_that_does_not_glow_has_no_emission(self):
        self.assertIsNone(export._emission(self._bsdf((1.0, 0.5, 0.2, 1.0), 0.0)))
        self.assertIsNone(export._emission(self._bsdf((0.0, 0.0, 0.0, 1.0), 5.0)))

    def test_a_glow_is_the_color_times_the_strength_at_most_one_as_srgb_hex(self):
        self.assertEqual(export._emission(self._bsdf((1.0, 1.0, 1.0, 1.0), 1.0)), "#FFFFFF")
        # A strength of 4 lifts the dark channels, and a channel never exceeds 1.
        self.assertEqual(export._emission(self._bsdf((0.5, 0.0, 0.0, 1.0), 4.0)), "#FF0000")
        self.assertEqual(export._emission(self._bsdf((0.0, 0.5, 0.0, 1.0), 1.0)), "#00BC00")

    def test_the_recorded_glows_and_colors_of_every_asset_are_hex_srgb(self):
        for name, manifest in manifests().items():
            for material in (manifest.get("export") or {}).get("materials", []):
                for key in ("baseColor", "emission"):
                    if key in material:
                        with self.subTest(asset=name, material=material["name"], key=key):
                            self.assertRegex(material[key], r"^#[0-9A-F]{6}$")


class KindAndStageTests(unittest.TestCase):
    def test_every_kind_blender_checks_has_the_objects_it_may_export(self):
        self.assertEqual(set(validate.STAGES), set(validate.OBJECT_TYPES))
        self.assertTrue(set(validate.STAGES) <= set(export.OBJECT_TYPES))

    def test_a_rigged_asset_exports_no_empties_and_a_library_nothing_but_its_armature(self):
        self.assertEqual(validate.RIGGED_OBJECT_TYPES, {"MESH", "ARMATURE"})
        self.assertEqual(validate.OBJECT_TYPES["animations"], {"ARMATURE"})
        self.assertEqual(export.OBJECT_TYPES["animations"], {"ARMATURE"})
        self.assertNotIn("EMPTY", export.OBJECT_TYPES["skinned"])

    def test_the_animation_stage_comes_last_and_only_a_library_starts_there(self):
        for kind, stages in validate.STAGES.items():
            with self.subTest(kind=kind):
                self.assertEqual(len(stages), len(set(stages)))
                if "animation" in stages:
                    self.assertEqual(stages[-1], "animation")
        self.assertEqual(validate.STAGES["animations"], ("animation",))
        self.assertEqual(validate.ARMATURE_KINDS, {"animations"})

    def test_animated_kinds_have_an_armature_to_export(self):
        for kind in export.ANIMATED_KINDS:
            self.assertIn("ARMATURE", export.OBJECT_TYPES[kind])

    def test_the_kinds_with_textured_materials_are_kinds_with_stages(self):
        self.assertTrue(validate.TEXTURED_KINDS <= set(validate.STAGES))

    def test_the_png_signature_and_the_fixed_export_time(self):
        self.assertEqual(export.PNG_SIGNATURE, bytes([0x89]) + b"PNG\r\n" + bytes([0x1A]) + b"\n")
        self.assertEqual(export.PNG_SIGNATURE[1:4], b"PNG")
        # Every FBX header records this time, so the same file gives the same bytes.
        self.assertEqual((export.FBX_TIME.year, export.FBX_TIME.month, export.FBX_TIME.day), (2000, 1, 1))


class CheckoutFreeExportTests(unittest.TestCase):
    """What makes the FBX of the same content the same bytes in every checkout and after every check."""

    def test_an_image_is_named_by_its_file_name_alone_in_both_of_its_fields(self):
        cases = {("D:\\any\\checkout\\Models\\T_Axe.png", "T_Axe.png"): "T_Axe.png",
                 ("/home/a/Models/textures/packed/T_Wolf", "textures\\packed\\T_Wolf"): "T_Wolf",
                 ("C:\\x\\T_Body.png", "../Textures/T_Body.png"): "T_Body.png"}
        for (absolute, relative), name in cases.items():
            with self.subTest(relative=relative):
                named = export._image_file_name(lambda image, scene_data, found=(absolute, relative): found)
                self.assertEqual(named(None, None), (name, name))

    def test_every_bone_goes_exactly_to_rest_also_its_scale(self):
        def bone():
            return types.SimpleNamespace(rotation_mode="XYZ", rotation_quaternion=(0.7, 0.7, 0.0, 0.0),
                                         rotation_euler=(0.1, 0.0, 0.0), rotation_axis_angle=(0.2, 1.0, 0.0, 0.0),
                                         location=(0.0, 0.01, 0.0), scale=(1.0, 0.9999998807907104, 1.0000001192092896))
        arm = types.SimpleNamespace(pose=types.SimpleNamespace(bones=[bone(), bone()]))
        poses.exact_rest(arm)
        for pose_bone in arm.pose.bones:
            self.assertEqual((pose_bone.rotation_mode, pose_bone.rotation_quaternion, pose_bone.rotation_euler,
                              pose_bone.rotation_axis_angle, pose_bone.location, pose_bone.scale),
                             ("QUATERNION", (1.0, 0.0, 0.0, 0.0), (0.0, 0.0, 0.0), (0.0, 0.0, 1.0, 0.0), (0.0, 0.0, 0.0),
                              (1.0, 1.0, 1.0)))


class NameRuleTests(unittest.TestCase):
    def test_an_object_name_starts_with_a_letter_and_holds_letters_digits_and_underscores(self):
        pattern = validate.NAME_PATTERN
        for good in ("Male", "Iron_Vein", "Rock2", "a"):
            self.assertIsNotNone(pattern.match(good), good)
        for bad in ("", "2Rock", "_Rock", "Rock.001", "Rock Vein", "Rock-1", "Brännvin"):
            self.assertIsNone(pattern.match(bad), bad)

    def test_a_material_is_named_like_the_unity_material_it_maps_to(self):
        pattern = validate.MATERIAL_PATTERN
        for good in ("M_Body_Male", "M_Resource_IronVeinRock", "M_1"):
            self.assertIsNotNone(pattern.match(good), good)
        for bad in ("Body", "m_Body", "M_", "M_Body.001", "M-Body", "T_Body_Male"):
            self.assertIsNone(pattern.match(bad), bad)

    def test_every_root_object_of_every_asset_is_a_valid_name(self):
        for name, manifest in manifests().items():
            for root in manifest.get("rootObjects", []):
                with self.subTest(asset=name, root=root):
                    self.assertIsNotNone(validate.NAME_PATTERN.match(root))

    def test_every_recorded_material_of_every_asset_is_a_valid_material_name(self):
        for name, manifest in manifests().items():
            for material in (manifest.get("export") or {}).get("materials", []):
                with self.subTest(asset=name, material=material["name"]):
                    self.assertIsNotNone(validate.MATERIAL_PATTERN.match(material["name"]))

    def test_an_attachment_s_only_root_is_grip(self):
        self.assertEqual(validate.GRIP_ROOT, "Grip")
        attachments = [m for m in manifests().values() if m.get("rootObjects") == ["Grip"]]
        self.assertTrue(attachments)

    def test_the_stages_every_asset_was_approved_in_are_stages_of_its_kind(self):
        for name, manifest in manifests().items():
            stages = validate.STAGES.get(manifest["kind"])
            if stages is not None:
                with self.subTest(asset=name):
                    self.assertTrue(set(manifest["approvals"]) <= set(stages))


class TransformRuleTests(unittest.TestCase):
    @staticmethod
    def _object(scale=(1.0, 1.0, 1.0), location=(0.0, 0.0, 0.0), rotation=(0.0, 0.0, 0.0), mode="XYZ"):
        return types.SimpleNamespace(scale=scale, location=location, rotation_euler=rotation, rotation_mode=mode)

    def test_a_scale_of_one_is_applied_and_a_hair_off_is_too(self):
        self.assertTrue(validate._is_identity_scale(self._object()))
        self.assertTrue(validate._is_identity_scale(self._object(scale=(1.00005, 0.99995, 1.0))))

    def test_a_scale_that_is_not_applied_is_found_on_any_axis(self):
        for scale in ((2.0, 1.0, 1.0), (1.0, 0.5, 1.0), (1.0, 1.0, 1.001), (-1.0, 1.0, 1.0)):
            self.assertFalse(validate._is_identity_scale(self._object(scale=scale)), scale)

    def test_a_root_at_the_origin_without_rotation_is_where_the_game_wants_its_actor(self):
        self.assertTrue(validate._is_at_origin(self._object()))
        self.assertTrue(validate._is_at_origin(self._object(location=(0.00005, 0.0, -0.00005))))

    def test_a_root_that_sits_off_the_origin_or_turned_is_found(self):
        for kwargs in ({"location": (0.001, 0.0, 0.0)}, {"location": (0.0, 0.0, 1.0)}, {"rotation": (0.0, 0.0, 0.01)},
                       {"mode": "QUATERNION"}):
            self.assertFalse(validate._is_at_origin(self._object(**kwargs)), kwargs)

    def test_the_tolerance_is_a_tenth_of_a_millimeter(self):
        self.assertTrue(math.isclose(validate.TOLERANCE, 1e-4))


if __name__ == "__main__":
    unittest.main()
