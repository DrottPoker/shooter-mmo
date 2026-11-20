"""The plain skin of the rebuilt bodies (human_build/skin.py): its images, and that they are the
faces and the eyes the game offers a character, by the names a stored character holds. It needs
numpy; without numpy this file is skipped."""

import json
import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from blender_source import BLENDER_ROOT, REPOSITORY_ROOT  # noqa: E402

try:
    import numpy as np
except ImportError:
    raise unittest.SkipTest("numpy is not installed; see requirements.txt") from None

sys.path.insert(0, str(BLENDER_ROOT))

from human_build import skin  # noqa: E402


def degrees_at(image, degrees, turn=0.0):
    """The pixel this many degrees from the line of sight, in the direction turn, in radians."""
    height, width = image.shape[:2]
    reach = degrees / 90.0 * 0.5
    return image[int((0.5 + reach * np.sin(turn)) * height), int((0.5 + reach * np.cos(turn)) * width), :3]


class FlatTests(unittest.TestCase):
    def test_a_flat_image_is_one_colour_all_over_and_opaque(self):
        image = skin.flat((8, 4), "#C9A084")
        self.assertEqual(image.shape, (4, 8, 4))
        np.testing.assert_allclose(image[..., :3], np.broadcast_to([201 / 255, 160 / 255, 132 / 255], (4, 8, 3)), atol=1e-6)
        self.assertTrue((image[..., 3] == 1.0).all())

    def test_the_body_and_every_face_of_a_body_have_the_skins_colour_at_their_sizes(self):
        made = skin.images("Female")
        self.assertEqual(made["T_Body_Female"].shape, (2048, 4096, 4))
        for face in ("Blush", "Freckles"):
            self.assertEqual(made[f"T_Face_Female_{face}"].shape, (2048, 2048, 4))
            np.testing.assert_allclose(made[f"T_Face_Female_{face}"][7, 11, :3], skin.rgb(skin.SKIN), atol=1e-6)


class PaintedTests(unittest.TestCase):
    RED, WHITE = [1.0, 0.0, 0.0], [1.0, 1.0, 1.0]

    def test_a_polygon_takes_the_colour_with_its_bleed_and_nothing_beyond(self):
        image = skin.flat((64, 32), "#FFFFFF")
        # Texels 16..32 across and 8..24 up.
        square = [(0.25, 0.25), (0.5, 0.25), (0.5, 0.75), (0.25, 0.75)]
        out = skin.painted(image, [square], "#FF0000", bleed=2.0)
        np.testing.assert_allclose(out[16, 24, :3], self.RED)
        np.testing.assert_allclose(out[16, 33, :3], self.RED)
        np.testing.assert_allclose(out[6, 20, :3], self.RED)
        np.testing.assert_allclose(out[16, 35, :3], self.WHITE)
        np.testing.assert_allclose(out[4, 20, :3], self.WHITE)
        self.assertTrue((out[..., 3] == 1.0).all())
        # The image it was given stays as it was.
        self.assertTrue((image[..., :3] == 1.0).all())

    def test_a_polygon_with_a_corner_cut_out_is_filled_inside_its_outline_only(self):
        image = skin.flat((32, 32), "#FFFFFF")
        corner = [(0.0, 0.0), (1.0, 0.0), (1.0, 0.5), (0.5, 0.5), (0.5, 1.0), (0.0, 1.0)]
        out = skin.painted(image, [corner], "#FF0000", bleed=0.0)
        np.testing.assert_allclose(out[24, 8, :3], self.RED)
        np.testing.assert_allclose(out[8, 24, :3], self.RED)
        np.testing.assert_allclose(out[24, 24, :3], self.WHITE)

    def test_a_polygon_at_the_images_edge_stays_on_the_image(self):
        image = skin.flat((16, 16), "#FFFFFF")
        out = skin.painted(image, [[(0.9, 0.9), (1.0, 0.9), (1.0, 1.0), (0.9, 1.0)]], "#FF0000")
        self.assertEqual(out.shape, image.shape)
        np.testing.assert_allclose(out[15, 15, :3], self.RED)

    def test_every_face_shows_the_inside_of_the_mouth_and_the_body_does_not(self):
        mouth = [[(0.5, 0.5), (0.52, 0.5), (0.52, 0.52), (0.5, 0.52)]]
        made = skin.images("Male", mouth)
        for face in ("Stubble", "Clean"):
            np.testing.assert_allclose(made[f"T_Face_Male_{face}"][1045, 1045, :3], skin.rgb(skin.MOUTH), atol=1e-6)
            np.testing.assert_allclose(made[f"T_Face_Male_{face}"][900, 900, :3], skin.rgb(skin.SKIN), atol=1e-6)
        np.testing.assert_allclose(made["T_Body_Male"][1045, 1045, :3], skin.rgb(skin.SKIN), atol=1e-6)

    def test_the_mouths_colour_stays_between_two_islands(self):
        # Islands lie 0.006 of the image's height apart: the colour reaches less than half of that.
        self.assertLess(skin.BLEED, 0.006 * skin.FACE_SIZE[1] / 2)


class EyeTests(unittest.TestCase):
    def setUp(self):
        self.eye = skin.eye((512, 512), "#3F8F8E")

    def test_the_pupil_is_dark_the_iris_has_its_colour_and_the_white_lies_around_it(self):
        self.assertLess(degrees_at(self.eye, 0.0).max(), 0.05)
        self.assertLess(degrees_at(self.eye, skin.PUPIL * 0.6).max(), 0.05)
        # Halfway out the iris, its colour's order of red, green, and blue, and nowhere near white.
        iris = degrees_at(self.eye, skin.IRIS * 0.6)
        self.assertLess(iris[0], iris[2])
        self.assertLess(iris[2], iris[1] + 0.05)
        self.assertLess(iris.max(), 0.8)
        white = degrees_at(self.eye, skin.IRIS + 6.0)
        np.testing.assert_allclose(white, skin.rgb(skin.EYE_WHITE), atol=0.03)

    def test_the_iris_ends_in_a_ring_darker_than_its_middle(self):
        for turn in (0.3, 1.9, 4.0):
            ring = degrees_at(self.eye, skin.IRIS * 0.97, turn).sum()
            middle = degrees_at(self.eye, skin.IRIS * 0.6, turn).sum()
            self.assertLess(ring, middle * 0.85)

    def test_the_white_turns_redder_toward_the_equator(self):
        front, edge = degrees_at(self.eye, skin.IRIS + 6.0), degrees_at(self.eye, 86.0)
        self.assertLess(edge[1], front[1])
        self.assertGreater(edge[0] - edge[1], front[0] - front[1])

    def test_the_iris_is_as_wide_whatever_the_image_s_size(self):
        small = skin.eye((128, 128), "#6B4428")
        self.assertLess(degrees_at(small, skin.IRIS - 4.0).max(), 0.6)
        np.testing.assert_allclose(degrees_at(small, skin.IRIS + 8.0), skin.rgb(skin.EYE_WHITE), atol=0.04)


class OfferTests(unittest.TestCase):
    """The images are what the game offers: the content names the options, the manifests the images."""

    def offers(self, label):
        with open(REPOSITORY_ROOT / "content" / "characters" / "appearance.json", encoding="utf-8") as f:
            content = json.load(f)
        body = next(b for b in content["bodyModels"] if b["id"] == f"Human_{label}")
        return {offer["kind"]: offer["options"] for offer in body["offers"] if offer["kind"] in ("face", "eyes")}

    def test_every_face_and_eye_colour_the_content_offers_has_an_image_and_no_other_has(self):
        for label in ("Male", "Female"):
            with self.subTest(label=label):
                offers = self.offers(label)
                self.assertEqual(list(skin.FACES[label]), offers["face"])
                self.assertEqual(list(skin.EYES[label]), offers["eyes"])
                expected = {f"T_Body_{label}"} | {f"T_Face_{label}_{o}" for o in offers["face"]}
                expected |= {f"T_Eye_{label}_{o}" for o in offers["eyes"]}
                self.assertEqual(set(skin.images(label)), expected)

    def test_a_materials_own_image_is_its_first_option_and_the_variants_are_the_others(self):
        self.assertEqual(skin.own_image("Body", "Male"), "T_Body_Male")
        self.assertEqual(skin.own_image("Face", "Male"), "T_Face_Male_Stubble")
        self.assertEqual(skin.own_image("Eye", "Female"), "T_Eye_Female_Teal")
        self.assertIsNone(skin.own_image("Teeth", "Male"))
        self.assertEqual(skin.variants("Female"), [{"material": "M_Eye_Female", "images": ["T_Eye_Female_Hazel"]},
                                                   {"material": "M_Face_Female", "images": ["T_Face_Female_Freckles"]}])

    def test_every_eye_colour_has_an_iris_colour(self):
        for colours in skin.EYES.values():
            for colour in colours:
                self.assertIn(colour, skin.IRIS_COLOURS)


if __name__ == "__main__":
    unittest.main()
