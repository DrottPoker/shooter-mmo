"""The game views: what a body draws of the garments it wears and the paint layers it shows, by the
client's PlayerBodyMeshPlan and BodyAppearanceCatalog, the rule that marks skin, garments, and contact
under which cloth lies, the camera that frames a figure, and the numbers of a class picture. Runs
without Blender: see blender_source.py. It needs numpy."""

import ast
import json
import math
import pathlib
import re
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from blender_source import BLENDER_ROOT, REPOSITORY_ROOT, load  # noqa: E402

view = load("art_pipeline/game_view.py", "Worn", "worn_from", "hidden_faces", "drawn", "shape_of", "cover_layers",
            "pull_in", "kept_body_faces", "kept_garment_faces", "shown_paints", "paints_over_limit", "shape_problem",
            "garment_triangles", "fan", "triangle_samples", "triangle_normals", "dense_samples", "lies_under",
            "covered_at_rest", "cloth_met", "aim", "toward_camera", "class_counts", "VIEW_VERSION", "THROUGH_REACH", "NONE", "THROUGH",
            "CONTACT", "MAX_PAINT_LAYERS", "UNDER_HEADWEAR", "FIELD_OF_VIEW", "FRAME_MARGIN", "WIDTH", "HEIGHT", "NEAREST",
            numpy=True)
# The pair check's own test of a place at rest, which the views ask whether a garment covered a triangle.
coverage = load("art_pipeline/coverage.py", "_rest_cover", "REACH", "PAIR_REST_DEPTH", numpy=True)

import numpy as np  # noqa: E402  (load() skips the file without numpy)

GARMENT_DATA = REPOSITORY_ROOT / "src" / "Client" / "Assets" / "Art" / "Models" / "Garments"


def worn(name, slot, layer, template=None, **fields):
    return view.Worn(name, template or name, slot, layer, **fields)


class DrawnTests(unittest.TestCase):
    """PlayerBodyMeshes.Drawn and ShapeOf."""

    def test_a_higher_layer_hides_a_slot_and_a_lower_or_equal_one_does_not(self):
        trousers = worn("WoolTrousers", "legs", 100)
        robe = worn("WoolRobe", "body", 300, hides=("legs",))
        greaves = worn("IronGreaves", "legs", 400)
        self.assertEqual([g.name for g in view.drawn([trousers, robe, greaves])], ["WoolRobe", "IronGreaves"])
        low = worn("Kilt", "body", 50, hides=("legs",))
        self.assertEqual([g.name for g in view.drawn([trousers, low])], ["Kilt", "WoolTrousers"])
        same = worn("Sash", "body", 100, hides=("legs",))
        self.assertEqual([g.name for g in view.drawn([trousers, same])], ["Sash", "WoolTrousers"])

    def test_a_garment_that_is_not_drawn_still_hides(self):
        trousers = worn("WoolTrousers", "legs", 100)
        robe = worn("WoolRobe", "body", 300, hides=("legs",))
        mantle = worn("Mantle", "back", 500, hides=("body",))
        self.assertEqual([g.name for g in view.drawn([trousers, robe, mantle])], ["Mantle"])

    def test_headwear_that_hides_the_hair_leaves_it_out_whatever_their_layers(self):
        hair = worn("DarkBrownHair", "hair", 550, template="ShoulderHair")
        for form, shown in (("hide", []), ("compress", ["DarkBrownHair"]), ("show", ["DarkBrownHair"])):
            helm = worn("Helm", "head", 10, hair=form)
            with self.subTest(form=form):
                self.assertEqual([g.name for g in view.drawn([hair, helm]) if g.slot == "hair"], shown)

    def test_the_order_is_the_layer_then_the_name_by_code_point(self):
        found = view.drawn([worn("alpha", "hands", 100), worn("Zeta", "feet", 100), worn("Boots", "feet", 50)])
        self.assertEqual([g.name for g in found], ["Boots", "Zeta", "alpha"])

    def test_hair_takes_its_form_under_headwear_only_under_drawn_headwear_that_presses_it(self):
        hair = worn("DarkBrownHair", "hair", 550, template="ShoulderHair")
        hood = worn("WoolHoodUp", "head", 600, hair="compress")
        down = worn("WoolHoodDown", "head", 600, hair="show")
        self.assertEqual(view.shape_of(hair, [hair, hood]), view.UNDER_HEADWEAR)
        self.assertIsNone(view.shape_of(hair, [hair, down]))
        self.assertIsNone(view.shape_of(hood, [hair, hood]))
        self.assertIsNone(view.shape_of(hair, [hair]))


class KeptTests(unittest.TestCase):
    """Plan.Compute, AddTriangles, and FindHiding."""

    def test_a_body_face_is_covered_at_the_highest_layer_and_a_vertex_pulled_in_by_the_most(self):
        shirt = worn("LinenShirt", "body", 150, template="Shirt", covered=(1, 2), pull_vertices=(5, 6),
                     pull_distances=(0.001, 0.003))
        trousers = worn("WoolTrousers", "legs", 100, template="Trousers", covered=(2, 3), pull_vertices=(6, 7),
                        pull_distances=(0.002, 0.0005))
        self.assertEqual(view.cover_layers([trousers, shirt]), {1: 150, 2: 150, 3: 100})
        self.assertEqual(view.pull_in([trousers, shirt]), {5: 0.001, 6: 0.003, 7: 0.0005})
        self.assertEqual(view.kept_body_faces(5, view.cover_layers([trousers, shirt])).tolist(),
                         [True, False, False, False, True])

    def test_a_garment_face_goes_where_a_higher_layer_covers_the_body_face_under_it(self):
        trousers = worn("WoolTrousers", "legs", 100, template="Trousers", covered=(7,), underlay=(7, 8, 9, 10))
        shirt = worn("LinenShirt", "body", 150, template="Shirt", covered=(8,))
        sash = worn("Sash", "hands", 100, template="Sash", covered=(9,))
        covers = view.cover_layers([trousers, shirt, sash])
        self.assertEqual(view.kept_garment_faces(trousers, [trousers, shirt, sash], covers, 4).tolist(),
                         [True, False, True, True])

    def test_a_garment_face_goes_where_a_drawn_garment_of_a_higher_layer_hides_it_by_its_template(self):
        trousers = worn("WoolTrousers", "legs", 100, template="Trousers", underlay=(0, 0, 0, 0))
        shirt = worn("LinenShirt", "body", 150, template="Shirt", hidden={"Trousers": (1,), "Boots": (2,)})
        lower = worn("Wrap", "hands", 50, template="Wrap", hidden={"Trousers": (3,)})
        covers = view.cover_layers([trousers, shirt, lower])
        self.assertEqual(view.kept_garment_faces(trousers, [lower, trousers, shirt], covers, 4).tolist(),
                         [True, False, True, True])

    def test_only_the_underwear_of_a_slot_whose_drawn_garment_hides_it_goes(self):
        paints = [{"name": "Briefs", "category": "underwear", "slot": "legs", "layer": 900},
                  {"name": "Bra", "category": "underwear", "slot": "body", "layer": 910},
                  {"name": "Tattoo", "category": "tattoo", "slot": None, "layer": 200},
                  {"name": "Runes", "category": "tattoo", "slot": None, "layer": 210}]
        named = ["Briefs", "Bra", "Tattoo", "Runes"]
        top = worn("Halter", "body", 150, hides_underwear=True)
        self.assertEqual([p["name"] for p in view.shown_paints(paints, named, [top])], ["Tattoo", "Briefs"])
        self.assertEqual([p["name"] for p in view.shown_paints(paints, named, [])], ["Tattoo", "Briefs", "Bra"])

    def test_the_body_shows_its_first_underwear_of_a_slot_the_look_names_none_of(self):
        catalog = [{"name": "Boxers", "category": "underwear", "slot": "legs", "layer": 905},
                   {"name": "Briefs", "category": "underwear", "slot": "legs", "layer": 900},
                   {"name": "Bra", "category": "underwear", "slot": "body", "layer": 910},
                   {"name": "Tattoo", "category": "tattoo", "slot": None, "layer": 200}]
        # Nothing named: the first underwear of each slot by layer, and no tattoo, which is not required.
        self.assertEqual([p["name"] for p in view.shown_paints(catalog, [], [])], ["Briefs", "Bra"])
        # A named underwear of a slot takes the place of the body's first.
        self.assertEqual([p["name"] for p in view.shown_paints(catalog, ["Boxers", "Tattoo"], [])], ["Tattoo", "Boxers", "Bra"])
        # A required underwear that a drawn garment hides still goes.
        top = worn("Halter", "body", 150, hides_underwear=True)
        self.assertEqual([p["name"] for p in view.shown_paints(catalog, [], [top])], ["Briefs"])

    def test_the_category_is_compared_without_case(self):
        catalog = [{"name": "Briefs", "category": "Underwear", "slot": "legs", "layer": 900},
                   {"name": "Boxers", "category": "UNDERWEAR", "slot": "legs", "layer": 905}]
        self.assertEqual([p["name"] for p in view.shown_paints(catalog, [], [])], ["Briefs"])
        self.assertEqual([p["name"] for p in view.shown_paints(catalog, ["Boxers"], [])], ["Boxers"])
        top = worn("Shorts", "legs", 100, hides_underwear=True)
        self.assertEqual(view.shown_paints(catalog, [], [top]), [])

    def test_more_paint_layers_on_a_material_than_the_skin_shader_draws_are_found(self):
        paints = [{"name": f"P{i}", "part": "Body"} for i in range(view.MAX_PAINT_LAYERS)] + [{"name": "F", "part": "Face"}]
        self.assertEqual(view.paints_over_limit(paints), {})
        self.assertEqual(view.paints_over_limit(paints + [{"name": "X", "part": "Body"}]), {"Body": view.MAX_PAINT_LAYERS + 1})

    def test_a_mesh_without_the_shape_key_it_takes_cannot_be_drawn(self):
        self.assertIsNone(view.shape_problem("Hair", None, []))
        self.assertIsNone(view.shape_problem("Hair", view.UNDER_HEADWEAR, ["Basis", view.UNDER_HEADWEAR]))
        self.assertIn(view.UNDER_HEADWEAR, view.shape_problem("Hair", view.UNDER_HEADWEAR, ["Basis"]))
        self.assertIsNotNone(view.shape_problem("Hair", view.UNDER_HEADWEAR, []))

    def test_every_drawn_triangle_of_a_garment_can_be_marked_and_its_turned_edges_are_no_cloth(self):
        triangles = [(0, 1, 2), (2, 3, 4), (4, 5, 6)]
        turned = [False, False, False, True, False, False, False]
        marked, cloth = view.garment_triangles([True, True, False], turned, triangles)
        self.assertEqual(marked.tolist(), [True, True, False])
        self.assertEqual(cloth.tolist(), [True, False, False])

    def test_every_face_splits_from_its_first_corner(self):
        self.assertEqual(view.fan([(0, 1, 2, 3), (4, 5, 6), (7, 8, 9, 10, 11)]),
                         [(0, 0, 1, 2), (0, 0, 2, 3), (1, 4, 5, 6), (2, 7, 8, 9), (2, 7, 9, 10), (2, 7, 10, 11)])

    def test_the_hidden_faces_are_read_per_template_and_must_add_up(self):
        data = {"innerTemplates": ["Boots", "Trousers"], "innerHiddenCounts": [1, 2], "innerHiddenFaces": [4, 7, 9]}
        self.assertEqual(view.hidden_faces(data), {"Boots": (4,), "Trousers": (7, 9)})
        with self.assertRaises(ValueError):
            view.hidden_faces(dict(data, innerHiddenFaces=[4, 7, 9, 11]))

    def test_every_exported_garment_data_reads_as_the_catalog_reads_it(self):
        files = sorted(GARMENT_DATA.glob("*.json"))
        self.assertTrue(files)
        fields = {"slot": "body", "layer": 150}
        for path in files:
            data = json.loads(path.read_text(encoding="utf-8"))
            with self.subTest(data=path.name):
                garment = view.worn_from(data["garment"], data["garment"], fields, data)
                self.assertEqual(len(garment.underlay), data["garmentFaces"])
                self.assertTrue(all(0 <= f < data["bodyFaces"] for f in garment.covered))
                for template, count in zip(data["innerTemplates"], data["innerFaceCounts"]):
                    self.assertTrue(all(0 <= f < count for f in garment.hidden[template]))


class Tree:
    """A stand-in for mathutils.bvhtree.BVHTree.ray_cast over a few triangles: the nearest hit from either
    side within the distance."""

    def __init__(self, vertices, triangles):
        self.vertices = np.asarray(vertices, float)
        self.triangles = [tuple(t) for t in triangles]

    def ray_cast(self, origin, direction, distance=math.inf):
        origin, direction = np.asarray(origin, float), np.asarray(direction, float)
        best = (None, None, None, None)
        for index, (a, b, c) in enumerate(self.triangles):
            p0, p1, p2 = self.vertices[a], self.vertices[b], self.vertices[c]
            e1, e2 = p1 - p0, p2 - p0
            h = np.cross(direction, e2)
            det = e1 @ h
            if abs(det) < 1e-15:
                continue
            s = origin - p0
            u = (s @ h) / det
            q = np.cross(s, e1)
            v = (direction @ q) / det
            t = (e2 @ q) / det
            if u < 0.0 or v < 0.0 or u + v > 1.0 or t <= 0.0 or t > distance:
                continue
            if best[3] is None or t < best[3]:
                best = (origin + direction * t, np.cross(e1, e2), index, t)
        return best


class Cloth(Tree):
    """Cloth whose texture is transparent where opaque says no; tree is itself, as the pair check casts
    rays through a garment's tree."""

    def __init__(self, vertices, triangles, opaque=lambda location: True, holes=False):
        super().__init__(vertices, triangles)
        self.test = opaque
        self.tree = self
        self.holes = holes

    def opaque(self, triangle, location):
        return self.test(np.asarray(location))


def square(z, low=-1.0, high=1.0, up=True):
    """Two triangles of a square at height z, facing up or down by their winding."""
    vertices = [(low, low, z), (high, low, z), (high, high, z), (low, high, z)]
    return vertices, ([(0, 1, 2), (0, 2, 3)] if up else [(0, 2, 1), (0, 3, 2)])


def skin(z, low=-0.01, high=0.01):
    """A small skin triangle at height z facing up, out of the body below it."""
    return np.array([[(low, low, z), (high, low, z), (low, high, z)]])


class LiesUnderTests(unittest.TestCase):
    """The rule of both classes: a ray into the body or under the garment, from the centre or a corner
    pulled toward it, meets opaque cloth within the reach before the body's surface."""

    def setUp(self):
        self.cloth = Cloth(*square(0.0))
        # The body's surface deep below, beyond the reach.
        self.body = Tree(*square(-0.2, up=False))

    def marked(self, corners, cloth=None, body="default"):
        return bool(view.lies_under(np.asarray(corners, float), cloth or self.cloth, self.body if body == "default" else body)[0])

    def test_skin_that_pokes_through_the_cloth_is_marked(self):
        self.assertTrue(self.marked(skin(0.002)))

    def test_skin_that_lies_a_millimeter_under_the_cloth_is_not(self):
        self.assertFalse(self.marked(skin(-0.001)))

    def test_a_finger_near_the_cloth_but_beside_its_edge_is_not(self):
        cloth = Cloth(*square(0.0, high=0.0))
        self.assertFalse(self.marked(skin(0.002, low=0.005, high=0.02), cloth))
        self.assertTrue(self.marked(skin(0.002, low=-0.02, high=-0.005), cloth))

    def test_cloth_beyond_the_reach_or_behind_the_body_s_own_surface_is_not(self):
        self.assertFalse(self.marked(skin(view.THROUGH_REACH + 0.001)))
        self.assertTrue(self.marked(skin(view.THROUGH_REACH - 0.001)))
        self.assertFalse(self.marked(skin(0.01), body=Tree(*square(0.005, up=False))))

    def test_a_transparent_place_lets_the_ray_on_to_cloth_behind_it(self):
        holes = Cloth(*square(0.0), opaque=lambda location: False)
        self.assertFalse(self.marked(skin(0.002), holes))
        vertices, triangles = square(0.0)
        below, more = square(-0.005)
        layered = Cloth(vertices + below, triangles + [tuple(v + 4 for v in t) for t in more],
                        opaque=lambda location: location[2] < -0.001)
        self.assertTrue(self.marked(skin(0.002), layered))

    def test_a_strand_of_a_net_between_the_samples_is_found_by_following_the_triangle_closer(self):
        corners = skin(0.002)
        strand = Cloth(*square(0.0), opaque=lambda location: 0.0025 <= location[0] <= 0.0045)
        samples = view.triangle_samples(corners)[0]
        self.assertFalse(any(0.0025 <= x <= 0.0045 for x in samples[:, 0]), "no first sample lies over the strand")
        self.assertTrue(self.marked(corners, strand))
        dense = view.dense_samples(corners[0])
        self.assertLessEqual(float(np.linalg.norm(dense[1] - dense[0])), 0.003 + 1e-12)

    def test_cloth_met_from_its_inner_side_is_passed_as_a_lining_behind_hair_in_an_opening(self):
        lining = Cloth(*square(0.0, up=False))
        self.assertFalse(self.marked(skin(0.002), lining))
        vertices, triangles = square(0.0, up=False)
        below, more = square(-0.005)
        layered = Cloth(vertices + below, triangles + [tuple(v + 4 for v in t) for t in more])
        self.assertTrue(self.marked(skin(0.002), layered))

    def test_a_corner_alone_can_mark_a_triangle(self):
        # Only the sample of the corner at (0.03, -0.03), pulled to x = 0.022, lies over the cloth, whose
        # edge runs at x = 0.015.
        cloth = Cloth([(0.015, -1.0, 0.0), (1.0, -1.0, 0.0), (1.0, 1.0, 0.0), (0.015, 1.0, 0.0)], [(0, 1, 2), (0, 2, 3)])
        corners = [[(-0.03, -0.03, 0.002), (0.03, -0.03, 0.002), (-0.03, 0.03, 0.002)]]
        self.assertTrue(self.marked(corners, cloth))

    def test_a_triangle_without_area_is_never_marked(self):
        self.assertFalse(self.marked([[(0.0, 0.0, 0.002)] * 3]))

    def classes(self, corners, cloth, owned):
        return view.lies_under(np.asarray(corners, float), cloth, self.body, owned=owned).tolist()

    def at_rest(self, corners, cloth, band, body=None):
        return view.covered_at_rest(np.asarray(corners, float), cloth, body, coverage.REACH[band], coverage._rest_cover,
                                    coverage.PAIR_REST_DEPTH).tolist()

    def test_skin_under_the_garment_at_rest_and_through_it_in_the_pose_is_through(self):
        # At rest the skin lies 5 mm under the shirt; in the pose it comes 2 mm out through it.
        rest = self.at_rest(skin(0.0), Cloth(*square(0.005)), "tight")
        self.assertEqual(rest, [True])
        self.assertEqual(self.classes(skin(0.002), Cloth(*square(0.0)), lambda i, t: rest[i]), [view.THROUGH])
        self.assertEqual(self.classes(skin(-0.001), Cloth(*square(0.0)), lambda i, t: rest[i]), [view.NONE])

    def test_skin_apart_from_the_garment_at_rest_and_through_it_in_the_pose_is_contact(self):
        # A forearm at rest has no shirt within the tight band over it; in the pose it presses into the
        # shirt over the chest.
        rest = self.at_rest(skin(0.0), Cloth(*square(0.05)), "tight")
        self.assertEqual(rest, [False])
        self.assertEqual(self.classes(skin(0.002), Cloth(*square(0.0)), lambda i, t: rest[i]), [view.CONTACT])
        # Cloth met on its outer side at rest, such as a skirt beside a hand, covers nothing.
        self.assertEqual(self.at_rest(skin(0.0), Cloth(*square(0.02, up=False)), "tight"), [False])
        # Without what each garment covered at rest nothing is contact.
        self.assertEqual(view.lies_under(skin(0.002), Cloth(*square(0.0)), self.body).tolist(), [view.THROUGH])

    def test_a_loose_band_reaches_a_triangle_200_mm_away_at_rest(self):
        skirt = Cloth(*square(0.2))
        self.assertEqual(self.at_rest(skin(0.0), skirt, "loose"), [True])
        self.assertEqual(self.at_rest(skin(0.0), skirt, "tight"), [False])
        self.assertEqual(self.classes(skin(0.002), Cloth(*square(0.0)), lambda i, t: self.at_rest(skin(0.0), skirt, "loose")[i]),
                         [view.THROUGH])

    def owned_at_rest(self, corners, cloth, band, garment_of=lambda t: 0):
        """What the views take as covered at rest: the class rule finds the garment's cloth under the
        triangle in the rest pose, or the pair check's rest test finds it over the triangle."""
        met = view.cloth_met(np.asarray(corners, float), cloth, self.body)
        covered = self.at_rest(corners, cloth, band, body=self.body)
        return lambda i, t: garment_of(t) in {garment_of(m) for m in met[i]} or covered[i]

    def test_through_at_rest_by_20_mm_beyond_the_rest_depth_is_inner_not_contact(self):
        # Hair 20 mm outside a hood's brim in its opening at rest: the pair check's rest test does not
        # take it, the class rule does, so it is through in every pose.
        brim = Cloth(*square(0.0))
        self.assertEqual(self.at_rest(skin(0.02), brim, "loose"), [False])
        owned = self.owned_at_rest(skin(0.02), brim, "loose")
        self.assertEqual(self.classes(skin(0.02), brim, owned), [view.THROUGH])
        self.assertEqual(self.classes(skin(0.01), brim, owned), [view.THROUGH])
        # Apart at rest, it is contact in a pose.
        apart = self.owned_at_rest(skin(0.05), brim, "loose")
        self.assertEqual(self.classes(skin(0.01), brim, apart), [view.CONTACT])

    def test_the_rule_gives_no_contact_in_the_rest_pose(self):
        # Random triangles over two garments' cloth, one with holes, classified in the rest pose with what
        # each garment covers at rest: nothing is contact, whatever the rays meet.
        rng = np.random.default_rng(7)
        lower, upper = square(0.0), square(0.012, low=-0.5, high=0.5, up=False)
        vertices = lower[0] + upper[0]
        triangles = lower[1] + [tuple(v + 4 for v in t) for t in upper[1]]
        cloth = Cloth(vertices, triangles, opaque=lambda location: location[2] > 0.006 or not 0.0 < location[0] % 0.01 < 0.006,
                      holes=True)
        garment_of = lambda t: 0 if t < 2 else 1  # noqa: E731
        found = []
        for _ in range(60):
            centre = rng.uniform((-0.3, -0.3, -0.02), (0.3, 0.3, 0.03))
            corners = (centre + rng.normal(0.0, 0.01, (3, 3)))[None]
            owned = self.owned_at_rest(corners, cloth, "tight", garment_of)
            found.append(self.classes(corners, cloth, owned)[0])
        self.assertNotIn(view.CONTACT, found)
        self.assertIn(view.THROUGH, found)

    def test_a_triangle_through_the_garment_already_at_rest_is_its_own(self):
        # Hair 2 mm outside a hood's brim at rest, within the pair check's depth at rest; deeper out it is
        # not.
        self.assertEqual(self.at_rest(skin(0.002), Cloth(*square(0.0)), "loose"), [True])
        self.assertEqual(self.at_rest(skin(coverage.PAIR_REST_DEPTH + 0.005), Cloth(*square(0.0, up=False)), "tight"), [False])

    def test_the_body_between_the_skin_and_the_cloth_ends_the_cover_at_rest(self):
        self.assertEqual(self.at_rest(skin(0.0), Cloth(*square(0.02)), "tight", body=Tree(*square(0.01))), [False])
        self.assertEqual(self.at_rest(skin(0.0), Cloth(*square(0.02)), "tight", body=Tree(*square(0.03))), [True])

    def test_a_net_covers_at_rest_by_its_strands_found_closer(self):
        strands = lambda location: 0.0025 <= location[0] <= 0.0045  # noqa: E731
        self.assertEqual(self.at_rest(skin(0.0), Cloth(*square(0.005), opaque=strands, holes=True), "tight"), [True])
        self.assertEqual(self.at_rest(skin(0.0), Cloth(*square(0.005), opaque=strands), "tight"), [False])

    def test_cloth_that_covered_it_at_rest_under_one_sample_makes_the_triangle_through(self):
        # Two garments' cloth: the half x < 0.015 did not cover the triangle at rest, the other half did.
        vertices = [(-1.0, -1.0, 0.0), (0.015, -1.0, 0.0), (0.015, 1.0, 0.0), (-1.0, 1.0, 0.0),
                    (0.015, -1.0, 0.0), (1.0, -1.0, 0.0), (1.0, 1.0, 0.0), (0.015, 1.0, 0.0)]
        split = Cloth(vertices, [(0, 1, 2), (0, 2, 3), (4, 5, 6), (4, 6, 7)])
        corners = [[(-0.03, -0.03, 0.002), (0.03, -0.03, 0.002), (-0.03, 0.03, 0.002)]]
        self.assertEqual(self.classes(corners, split, lambda i, t: t >= 2), [view.THROUGH])
        self.assertEqual(self.classes(corners, split, lambda i, t: False), [view.CONTACT])

    def test_a_net_strand_that_did_not_cover_it_at_rest_is_contact(self):
        strand = Cloth(*square(0.0), opaque=lambda location: 0.0025 <= location[0] <= 0.0045)
        self.assertEqual(self.classes(skin(0.002), strand, lambda i, t: False), [view.CONTACT])
        self.assertEqual(self.classes(skin(0.002), strand, lambda i, t: True), [view.THROUGH])


class CameraTests(unittest.TestCase):
    def test_a_yaw_of_0_looks_at_the_back_and_180_at_the_front(self):
        np.testing.assert_allclose(view.toward_camera(0.0, 0.0), (0.0, -1.0, 0.0), atol=1e-12)
        np.testing.assert_allclose(view.toward_camera(180.0, 0.0), (0.0, 1.0, 0.0), atol=1e-12)
        np.testing.assert_allclose(view.toward_camera(90.0, 90.0), (0.0, 0.0, 1.0), atol=1e-12)

    def test_the_figure_fills_the_frame_but_its_margin_in_the_game_s_field_of_view(self):
        rng = np.random.default_rng(3)
        points = rng.uniform((-0.3, -0.2, 0.0), (0.3, 0.2, 1.8), (500, 3))
        for yaw, pitch in ((215.0, 20.0), (35.0, 20.0), (125.0, 0.0)):
            toward, centre, distance = view.aim(points, yaw, pitch)
            right = np.cross(-toward, (0.0, 0.0, 1.0))
            right /= np.linalg.norm(right)
            up = np.cross(right, -toward)
            relative = points - centre
            span = distance - relative @ toward
            x, y = np.abs(relative @ right / span), np.abs(relative @ up / span)
            tan_y = math.tan(math.radians(view.FIELD_OF_VIEW) / 2.0)
            tan_x = tan_y * view.WIDTH / view.HEIGHT
            with self.subTest(yaw=yaw, pitch=pitch):
                self.assertLessEqual(max(x.max() / tan_x, y.max() / tan_y), 1.0 - view.FRAME_MARGIN + 1e-6)
                self.assertGreater(max(x.max() / tan_x, y.max() / tan_y), 1.0 - view.FRAME_MARGIN - 0.01)
                self.assertGreaterEqual(distance, view.NEAREST)

    def test_a_tiny_figure_is_seen_from_the_game_s_least_distance(self):
        self.assertEqual(view.aim(np.zeros((3, 3)), 215.0, 20.0)[2], view.NEAREST)


class ClassCountTests(unittest.TestCase):
    def test_the_counts_take_exact_colours_and_the_figure_is_everything_but_the_background(self):
        pixels = np.zeros((2, 4, 4))
        pixels[0, 0] = (1.0, 0.0, 0.0, 1.0)
        pixels[0, 1] = (0.0, 1.0, 0.0, 1.0)
        pixels[0, 2] = (0.0, 1.0, 0.0, 1.0)
        pixels[1, 0] = (0.0, 0.0, 0.0, 1.0)
        pixels[1, 1] = (0.0, 0.0, 1.0, 1.0)
        self.assertEqual(view.class_counts(pixels), {"skin": 1, "inner": 2, "contact": 1, "figure": 5})

    def test_a_blended_edge_or_any_other_colour_stops_the_count(self):
        pixels = np.zeros((1, 2, 4))
        pixels[0, 0] = (0.5, 0.0, 0.0, 1.0)
        with self.assertRaises(ValueError):
            view.class_counts(pixels)
        pixels[0, 0] = (0.0, 0.0, 0.0, 0.5)
        with self.assertRaises(ValueError):
            view.class_counts(pixels)


SET = json.loads((REPOSITORY_ROOT / "tools" / "art" / "views" / "standard-set.json").read_text(encoding="utf-8"))
BASELINE = REPOSITORY_ROOT / "art" / "Views" / "baseline.json"
# The pairs of templates, outer over inner, that cannot meet: the pair check found no triangle of the inner
# one within reach of the outer one's cloth on any body at rest, so it follows nothing (2026-11-22). Every
# other pair that a body can wear and draw together is worn together in an outfit of the set.
APART = {("Cloak", "Gloves"), ("Gloves", "Boots"), ("Gloves", "Shirt"), ("Gloves", "Trousers"), ("HoodDown", "Boots"),
         ("HoodDown", "Gloves"), ("HoodDown", "Trousers"), ("HoodUp", "Boots"), ("HoodUp", "Gloves"), ("HoodUp", "Trousers"),
         ("LongShirt", "Boots"), ("NasalHelm", "Boots"), ("NasalHelm", "Cloak"), ("NasalHelm", "Gloves"),
         ("NasalHelm", "LongShirt"), ("NasalHelm", "Robe"), ("NasalHelm", "Shirt"), ("NasalHelm", "Trousers"),
         ("Shirt", "Boots"), ("ShoulderHair", "Boots"), ("ShoulderHair", "Gloves"), ("ShoulderHair", "Trousers")}


def manifest(folder: str) -> dict:
    return json.loads((REPOSITORY_ROOT / folder / "asset.json").read_text(encoding="utf-8"))


def templates() -> dict[str, dict]:
    return {path.parent.name: manifest(f"art/Garments/{path.parent.name}") for path in (REPOSITORY_ROOT / "art" / "Garments").glob("*/asset.json")}


def textures() -> dict[str, str]:
    """Every garment texture by name, with the name of its template."""
    return {path.parent.name: manifest(f"art/GarmentTextures/{path.parent.name}")["garment"].rsplit("/", 1)[1]
            for path in (REPOSITORY_ROOT / "art" / "GarmentTextures").glob("*/asset.json")}


class StandardSetTests(unittest.TestCase):
    """tools/art/views/standard-set.json: what the views show."""

    def test_every_template_and_texture_is_in_an_outfit_and_every_outfit_is_one_a_player_wears(self):
        made, worn = textures(), [texture for outfit in SET["outfits"] for texture in outfit["garments"]]
        self.assertEqual(sorted(set(worn)), sorted(made))
        self.assertEqual(sorted({made[t] for t in worn}), sorted(templates()))
        for outfit in SET["outfits"]:
            slots = [templates()[made[t]]["slot"] for t in outfit["garments"]]
            with self.subTest(outfit=outfit["name"]):
                self.assertEqual(len(slots), len(set(slots)), "one garment per visual slot")
                self.assertTrue(set(outfit["paints"]) <= {p.parent.name for p in (REPOSITORY_ROOT / "art" / "Paint").glob("*/asset.json")})

    def test_every_pair_of_templates_that_can_meet_is_worn_together(self):
        found, made = templates(), textures()
        pairs = set()
        for outer, a in found.items():
            for inner, b in found.items():
                drawn_together = a["slot"] != b["slot"] and b["slot"] not in a.get("hides", []) and \
                    not (b["slot"] == "hair" and a.get("hair") == "hide")
                if a["layer"] > b["layer"] and drawn_together and (outer, inner) not in APART:
                    pairs.add((outer, inner))
        self.assertEqual(len(pairs), 26)
        worn = [{made[t] for t in outfit["garments"]} for outfit in SET["outfits"]]
        missing = sorted(pair for pair in pairs if not any(set(pair) <= outfit for outfit in worn))
        self.assertEqual(missing, [])
        self.assertTrue(APART <= {(o, i) for o in found for i in found}, "every pair apart names two templates")

    def test_the_poses_are_frames_of_the_clips_with_a_phase_and_the_set_stays_below_600_views(self):
        library = manifest(SET["library"])
        # The export's frames of a clip is its last frame; a clip starts at frame 0.
        frames = {clip["name"]: clip["frames"] for clip in library["export"]["clips"]}
        cameras = {camera["name"]: camera for camera in SET["cameras"]}
        self.assertTrue(all(camera["frame"] in ("figure", "head", "waist", "feet") for camera in cameras.values()))
        for pose in SET["poses"]:
            with self.subTest(pose=pose["name"]):
                self.assertEqual(pose["phase"], "rare" if pose.get("clip") == "Death" else "clean")
                self.assertTrue(set(pose["cameras"]) <= set(cameras))
                if "clip" in pose:
                    self.assertLessEqual(pose["frame"], frames[pose["clip"]])
        script = (REPOSITORY_ROOT / "tools" / "art" / "ArtViews.ps1").read_text(encoding="utf-8")
        self.assertIn("[int]$pose.frame -gt $frames[$clip]", script)
        clips = {pose.get("clip") for pose in SET["poses"]}
        self.assertTrue({None, "Idle", "Walk", "Jog", "Death", "SwordSlash", "OneHandStrike", "GreataxeSwing", "Cast", "BowShot"} <= clips)
        views = len(SET["outfits"]) * len(SET["bodies"]) * sum(len(pose["cameras"]) for pose in SET["poses"])
        self.assertEqual(views, 560)
        self.assertLess(views, 600)


class BaselineTests(unittest.TestCase):
    """art/Views/baseline.json: one line per view of the set, sorted, with its committed key and numbers."""

    def test_the_baseline_names_every_view_of_the_set_once_in_order_one_line_each(self):
        if not BASELINE.is_file():
            self.skipTest("art/Views/baseline.json is not written yet")
        text = BASELINE.read_text(encoding="utf-8")
        data = json.loads(text)
        self.assertEqual(data["viewVersion"], view.VIEW_VERSION)
        names = [entry["name"] for entry in data["views"]]
        expected = sorted(f"{outfit['name']}_{body.rsplit('_', 1)[1]}_{pose['name']}_{camera}" for outfit in SET["outfits"]
                          for body in SET["bodies"] for pose in SET["poses"] for camera in pose["cameras"])
        self.assertEqual(names, expected)
        lines = [line for line in text.splitlines() if '"name"' in line]
        self.assertEqual(len(lines), len(names))
        for entry in data["views"]:
            self.assertRegex(entry["key"], "^[0-9a-f]{64}$")
            self.assertTrue(all(isinstance(entry[n], int) and entry[n] >= 0 for n in ("skin", "inner", "contact", "figure")))
            self.assertLessEqual(entry["skin"] + entry["inner"] + entry["contact"], entry["figure"])

    def test_no_view_of_the_rest_pose_has_contact(self):
        # What shows through at rest is the garment's own; contact needs motion.
        if not BASELINE.is_file():
            self.skipTest("art/Views/baseline.json is not written yet")
        rest = [pose["name"] for pose in SET["poses"] if "clip" not in pose]
        views = json.loads(BASELINE.read_text(encoding="utf-8"))["views"]
        at_rest = [entry for entry in views if entry["name"].split("_")[2] in rest]
        self.assertTrue(at_rest)
        self.assertEqual([entry["name"] for entry in at_rest if entry["contact"]], [])


class ScriptTests(unittest.TestCase):
    """How tools/art/Render-ArtViews.ps1 and tools/art/ArtViews.ps1 run the renderer and read its lines."""

    @staticmethod
    def text(name: str) -> str:
        return (REPOSITORY_ROOT / "tools" / "art" / name).read_text(encoding="utf-8")

    def test_the_version_of_the_views_and_the_paint_limit_are_the_same_on_both_sides(self):
        from blender_source import powershell_value
        self.assertEqual(int(powershell_value("ArtViews.ps1", "ArtViewVersion")), view.VIEW_VERSION)
        self.assertEqual(int(powershell_value("ArtViews.ps1", "ArtViewMaxPaintLayers")), view.MAX_PAINT_LAYERS)
        self.assertEqual(powershell_value("ArtViews.ps1", "ArtViewRequiredCategories"), '@("underwear")')

    def test_the_fingerprint_of_the_code_in_the_key_covers_every_module_the_renderer_runs(self):
        from blender_source import powershell_value
        paths = re.findall(r'"([^"]+)"', powershell_value("ArtViews.ps1", "ArtViewCodePaths"))
        self.assertIn("art/Skeletons", paths)
        # The scripts that build the renderer's job and the views' keys.
        self.assertIn("tools/art/Render-ArtViews.ps1", paths)
        self.assertIn("tools/art/ArtViews.ps1", paths)
        self.assertTrue(all((REPOSITORY_ROOT / path).exists() for path in paths))
        covered = [(REPOSITORY_ROOT / path).resolve() for path in paths]

        def inside(path: pathlib.Path) -> bool:
            return any(path == folder or folder in path.parents for folder in covered)

        def local(dotted: str, base: pathlib.Path = BLENDER_ROOT) -> list[pathlib.Path]:
            target = base.joinpath(*dotted.split(".")) if dotted else base
            return [p for p in (target.with_suffix(".py"), target / "__init__.py") if p.is_file()]

        seen, pending = set(), [(BLENDER_ROOT / "art_views_cli.py").resolve()]
        while pending:
            path = pending.pop()
            if path in seen:
                continue
            seen.add(path)
            with self.subTest(module=path.relative_to(REPOSITORY_ROOT).as_posix()):
                self.assertTrue(inside(path))
            for node in ast.walk(ast.parse(path.read_text(encoding="utf-8"))):
                found = []
                if isinstance(node, ast.ImportFrom) and node.level:
                    base = path.parent
                    for _ in range(node.level - 1):
                        base = base.parent
                    found += local(node.module or "", base)
                    found += [p for alias in node.names for p in local(alias.name, base.joinpath(*(node.module or "").split(".")) if node.module else base)]
                elif isinstance(node, ast.ImportFrom) and node.module and local(node.module.split(".")[0]):
                    found += local(node.module)
                    found += [p for alias in node.names for p in local(f"{node.module}.{alias.name}")]
                elif isinstance(node, ast.Import):
                    found += [p for alias in node.names for p in local(alias.name)]
                pending += [p.resolve() for p in found]
        self.assertIn((BLENDER_ROOT / "art_pipeline" / "game_view.py").resolve(), seen)
        self.assertIn((BLENDER_ROOT / "human_base" / "rig.py").resolve(), seen)

    def test_the_views_run_their_own_script_with_one_job_file_in_the_pipeline_s_environment(self):
        script = self.text("Render-ArtViews.ps1")
        self.assertIn('Join-Path $PSScriptRoot "blender/art_views_cli.py"', script)
        self.assertIn("New-ArtBlenderJob $manifest @($jobPath)", script)
        self.assertIn("Script = $script:ArtViewsCli", script)
        self.assertIn("Invoke-ArtBlenderJobs $blender $jobs.ToArray() $count $onDone", script)
        self.assertIn('SourcePath = ""', script)
        common = self.text("ArtPipelineCommon.ps1")
        self.assertIn("Script = $script:ArtBlenderCli", common)
        self.assertIn("Get-ArtBlenderArguments $Job.Manifest $Job.Arguments $Job.Script", common)
        cli = (REPOSITORY_ROOT / "tools" / "art" / "blender" / "art_views_cli.py").read_text(encoding="utf-8")
        self.assertIn('print(f"ART_VIEW ', cli)
        self.assertIn('"ART_VIEW *"', script)

    def test_the_job_file_holds_what_the_renderer_reads(self):
        script = self.text("Render-ArtViews.ps1")
        renderer = (REPOSITORY_ROOT / "tools" / "art" / "blender" / "art_pipeline" / "game_view.py").read_text(encoding="utf-8")
        for key in ("body", "file", "root", "garments", "texture", "textureFile", "template", "templateFile", "data", "paints",
                    "name", "category", "slot", "layer", "part", "named", "library", "views", "key", "committed", "pose", "clip",
                    "frame", "camera", "yaw", "pitch", "look", "classes", "numbers"):
            with self.subTest(key=key):
                self.assertRegex(script, rf"\b{key} = ")
                self.assertRegex(renderer, rf"""["']{key}["']""")

    def test_both_caches_lie_below_the_shared_root_of_the_main_checkout(self):
        common = self.text("ArtPipelineCommon.ps1")
        root = common[common.index("function Resolve-ArtCacheRoot"):common.index("function Get-ArtBuildFolder")]
        self.assertIn("rev-parse --path-format=absolute --git-common-dir", root)
        self.assertIn("rev-parse --show-toplevel", root)
        self.assertIn('Join-Path $main "tools/art/ArtPipelineCommon.ps1"', root)
        self.assertIn('Join-Path $main "artifacts/art-cache"', root)
        self.assertIn('$arguments = @("--cache-root", (Get-ArtCacheRoot))', common)
        self.assertIn("$cacheRoot = Get-ArtCacheRoot", self.text("Render-ArtViews.ps1"))
        self.assertIn('Join-Path $CacheRoot "views"', self.text("ArtViews.ps1"))
        cli = (REPOSITORY_ROOT / "tools" / "art" / "blender" / "art_pipeline_cli.py").read_text(encoding="utf-8")
        self.assertIn('check_cache.Store(os.path.join(args.cache_root, "checks") if args.cache_root else None', cli)

    def test_the_verification_holds_the_baseline_and_gives_a_waiting_asset_its_views(self):
        verify = self.text("Verify-ArtAssets.ps1")
        self.assertIn('. (Join-Path $PSScriptRoot "ArtViews.ps1")', verify)
        self.assertIn("Get-ArtViewBaselineProblems", verify)
        self.assertIn("$owner = Get-ArtViewProblemOwner $viewProblem $waitingAssets", verify)
        self.assertIn("Add-ArtAssetProblem $owner $viewProblem.Problem", verify)
        views = self.text("ArtViews.ps1")
        self.assertIn("run tools/art/Render-ArtViews.ps1 -WriteBaseline", views)
        # The committed key reads content records and the code only: what CI has without Blender and
        # without the files.
        key = views[views.index("function Get-ArtViewKeyText"):views.index("function Get-ArtViewKey(")]
        self.assertNotIn("Get-ArtFileSha256", key)
        self.assertNotIn("SourcePath", key)
        for line in ("code $Code", "blender $script:ArtBlenderVersion", "clip:$($View.Pose.Clip)", "skeleton=", "rig=", "texture=",
                     "paint=", "data $($garment.Data)=", "band=$($template.Band)"):
            self.assertIn(line, key)
        self.assertIn("Get-ArtViewKeyText $View (Get-ArtViewCode)", views)

    def test_the_baseline_takes_approved_exported_content_only_and_the_check_has_three_exit_codes(self):
        script, views = self.text("Render-ArtViews.ps1"), self.text("ArtViews.ps1")
        gate = views[views.index("function Get-ArtViewGate("):views.index("function Read-ArtViewNumbers")]
        for call in ("Get-ArtExportGateProblem $manifest", "Get-ArtExportChangeProblem $manifest", "Get-ArtExportFileProblems $manifest"):
            self.assertIn(call, gate)
        self.assertIn("-WriteBaseline measures the exported data only; leave out -DataFolder.", script)
        self.assertLess(script.index("$gate = Get-ArtViewGate $everyView"), script.index("Invoke-ArtViewRender $views $everyView $committed"))
        compare = views[views.index("function Compare-ArtViews"):views.index("function Read-ArtViewKeys")]
        self.assertLess(compare.index("return 2"), compare.index("return 1"))
        self.assertIn("exit 1", script)
        self.assertIn("$script:ArtViewTolerance", compare)

    def test_the_build_measures_the_baseline_after_its_exports_and_before_it_verifies(self):
        build = self.text("Build-Art.ps1")
        views = build.index('"Render-ArtViews.ps1") -WriteBaseline -Jobs $Jobs -BlenderPath $blender')
        self.assertLess(build.index('"Update-UnityArt.ps1")'), views)
        self.assertLess(views, build.index('"Verify-ArtAssets.ps1") -Waiting'))
        self.assertLess(build.index("if (@(Get-ArtViewBaselineProblems).Count -gt 0)"), views)
        self.assertIn('Add-ArtPhase "game views" $watch', build)


if __name__ == "__main__":
    unittest.main()
