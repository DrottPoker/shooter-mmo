"""The logic of the human build that needs numpy but not Blender: the net's subdivision, mirror image,
islands, seams, slivers, and folds, the islands that follow from the net's shape, and the cutting of
faces and the layout of the UV map, on small nets whose answers follow from the input. Without
numpy this file is skipped; see test_numeric_logic.py and blender_source.py."""

import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from blender_source import BLENDER_ROOT, load  # noqa: E402

try:
    import numpy as np
except ImportError:
    raise unittest.SkipTest("numpy is not installed; see requirements.txt") from None

sys.path.insert(0, str(BLENDER_ROOT))

from human_build import islands  # noqa: E402
from human_build.net import FIXED, MIRROR, PINNED, RIGID, Net  # noqa: E402

assemble = load("human_build/assemble.py", "lay_out", "cut_faces", "mend", "_cut_score", "_smallest_corner", "LABELS",
                "SLIVER_ANGLE", numpy=True)


def strip_net():
    """Two quads beside the mirror plane, counter-clockwise seen from +Z:
        3 4 5
        0 1 2      vertices 0 and 3 lie on the plane x = 0."""
    net = Net()
    for index, (x, y) in enumerate([(0, 0), (1, 0), (2, 0), (0, 1), (1, 1), (2, 1)]):
        net.add((x, y, 0.0), *([MIRROR] if x == 0 else []))
    net.island = "front"
    net.quad(0, 1, 4, 3, 2, "trunk")
    net.island = "arm_R"
    net.quad(1, 2, 5, 4, 4, "arm")
    net.seam([1, 4])
    return net


class NetTests(unittest.TestCase):
    def test_a_face_takes_the_island_that_is_current(self):
        net = strip_net()
        self.assertEqual(net.islands, ["front", "arm_R"])
        self.assertEqual(net.seams, {(1, 4)})

    def test_a_strip_names_the_island_of_each_face(self):
        net = Net()
        lower = net.add_ring([(0, 0, 0), (1, 0, 0), (2, 0, 0)])
        upper = net.add_ring([(0, 1, 0), (1, 1, 0), (2, 1, 0)])
        net.strip(lower, upper, 0, "skin", closed=False, islands=["back", "front"])
        self.assertEqual(net.islands, ["back", "front"])

    def test_subdividing_keeps_islands_and_seams(self):
        fine = strip_net().subdivided()
        self.assertEqual(len(fine.faces), 8)
        self.assertEqual(fine.islands, ["front"] * 4 + ["arm_R"] * 4)
        middle = fine.edge_point[(1, 4)]
        self.assertEqual(fine.seams, {(1, middle), (4, middle)})
        self.assertEqual(len(fine.face_point), 2)
        np.testing.assert_allclose(fine.points[fine.face_point[0]], (0.5, 0.5, 0.0))

    def test_a_new_vertex_keeps_the_holds_its_makers_share_but_is_not_fixed(self):
        net = strip_net()
        net.hold([0, 3], FIXED, PINNED)
        fine = net.subdivided()
        self.assertEqual(fine.holds[fine.edge_point[(0, 3)]], {MIRROR})
        self.assertEqual(fine.holds[fine.edge_point[(0, 1)]], set())

    def test_the_mirror_image_names_the_left_islands_and_keeps_the_middle_ones(self):
        whole = strip_net().mirrored()
        self.assertEqual(len(whole.points), 10)
        self.assertEqual(whole.islands, ["front", "arm_R", "front", "arm_L"])
        self.assertEqual(len(whole.seams), 2)
        # The mirrored faces look the same way as their originals.
        points = whole.array()
        for face in whole.faces:
            a, b, c = (points[v] for v in face[:3])
            self.assertGreater(np.cross(b - a, c - a)[2], 0.0)

    def test_a_vertex_on_the_mirror_plane_must_be_held_on_it(self):
        net = Net()
        for x, y in [(0, 0), (1, 0), (1, 1), (0, 1)]:
            net.add((x, y, 0.0))
        net.quad(0, 1, 2, 3, 0, "skin")
        with self.assertRaises(ValueError):
            net.mirrored()

    def test_a_square_is_no_sliver_and_a_needle_is(self):
        net = Net()
        for point in [(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0), (2, 0, 0), (2, 0.01, 0), (1, 0.01, 0)]:
            net.add(point)
        net.quad(0, 1, 2, 3, 0, "skin")
        net.quad(1, 4, 5, 6, 0, "skin")
        self.assertEqual(net.slivers(5.0), [1])

    def test_a_face_with_a_straight_corner_is_no_sliver_cut_through_that_corner(self):
        net = Net()
        # Vertex 1 lies almost on the line from 0 to 2.
        for point in [(0, 0, 0), (1, 0.01, 0), (2, 0, 0), (1, 1, 0)]:
            net.add(point)
        net.quad(0, 1, 2, 3, 0, "skin")
        self.assertEqual(net.slivers(5.0), [])

    def test_a_folded_face_is_found_and_a_hidden_one_is_left_out(self):
        net = Net()
        # A bow tie: its two triangles look opposite ways whichever way it is cut.
        for point in [(0, 0, 0), (1, 0, 0), (0, 1, 0), (1, 1, 0)]:
            net.add(point)
        net.quad(0, 1, 2, 3, 0, "skin")
        net.quad(0, 1, 2, 3, 0, "mouth")
        self.assertEqual(net.folded(0.3, hidden=("mouth",)), [0])

    def test_mending_moves_neither_mirror_nor_rigid_vertices(self):
        net = Net()
        for point, holds in [((0, 0, 0), [MIRROR]), ((1, 0, 0), [RIGID]), ((0, 1, 0), []), ((1, 1, 0), [])]:
            net.add(point, *holds)
        net.quad(0, 1, 2, 3, 0, "skin")
        net.mend()
        np.testing.assert_allclose(net.points[0], (0, 0, 0))
        np.testing.assert_allclose(net.points[1], (1, 0, 0))

    def test_mending_keeps_a_pinned_vertex_and_a_plane_hold_and_moves_a_fixed_vertex(self):
        net = Net()
        shelf = net.plane("shelf", (0, 0, 0.3), (0, 0, 1))
        # A needle 1 long and 0.01 wide, a sliver whichever way it is cut.
        net.add((0, 0, 0), FIXED, PINNED)
        net.add((1, 0, 0.3), shelf)
        net.add((1, 0.01, 0.3), FIXED)
        net.add((0, 0.01, 0))
        net.quad(0, 1, 2, 3, 0, "skin")
        net.mend(rounds=1)
        np.testing.assert_allclose(net.points[0], (0, 0, 0))
        self.assertLess(net.points[1][0], 1.0)
        self.assertAlmostEqual(net.points[1][2], 0.3)
        self.assertLess(net.points[2][0], 1.0)

    def test_smoothing_moves_a_vertex_toward_the_middle_of_its_neighbours(self):
        net = Net()
        for point in [(0, 0, 0), (2, 0, 0), (2, 2, 0), (0, 2, 0), (4, 0, 0), (4, 2, 0)]:
            net.add(point)
        net.quad(0, 1, 2, 3, 0, "skin")
        net.quad(1, 4, 5, 2, 0, "skin")
        net.smooth([1], 1, rate=0.5)
        # The neighbours of vertex 1 are 0, 2, and 4; their middle is (2, 2/3, 0).
        np.testing.assert_allclose(net.points[1], (2.0, 1.0 / 3.0, 0.0))

    def test_touching_names_the_vertices_of_a_skin(self):
        self.assertEqual(strip_net().touching("arm"), [1, 2, 4, 5])


class IslandTests(unittest.TestCase):
    def hand(self):
        """A box of six faces as a hand on each side: its top looks along +Z."""
        net = Net()
        corners = [(1, 0, 0), (2, 0, 0), (2, 1, 0), (1, 1, 0), (1, 0, 1), (2, 0, 1), (2, 1, 1), (1, 1, 1)]
        for point in corners:
            net.add(point)
        for point in corners:
            net.add((-point[0], point[1], point[2]))
        box = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
        net.island = "hand_R"
        for face in box:
            net.quad(*face, 6, "palm")
        net.island = "hand_L"
        for face in box:
            net.quad(*[v + 8 for v in reversed(face)], 6, "palm")
        return net

    def test_a_hand_parts_into_a_back_and_a_palm(self):
        net = self.hand()
        # Two of the box's sides look toward the palm's side and two away from it.
        islands.part_hands(net, (0.2, 0.2, 1.0))
        self.assertEqual(net.islands[0], "hand_back_R")
        self.assertEqual(net.islands[1], "hand_palm_R")
        self.assertEqual(net.islands[6], "hand_back_L")
        self.assertEqual(net.islands[7], "hand_palm_L")
        self.assertEqual({name.rsplit("_", 1)[0] for name in net.islands}, {"hand_back", "hand_palm"})

    def test_the_back_of_an_ear_is_an_island_of_its_own(self):
        net = Net()
        # Two faces of a right ear: one looks out from the head, along +X, one back at it.
        for point in [(1, 0, 0), (1, 1, 0), (1, 1, 1), (1, 0, 1)]:
            net.add(point)
        net.island = "head"
        net.quad(0, 1, 2, 3, 0, "ear")
        net.quad(3, 2, 1, 0, 0, "ear")
        net.quad(0, 1, 2, 3, 0, "head")
        islands.part_ears(net)
        self.assertEqual(net.islands, ["head", "ear_back_R", "head"])


class AssembleTests(unittest.TestCase):
    def test_boxes_are_laid_out_without_overlap_inside_the_image(self):
        sizes = [(0.45, 0.8), (0.45, 0.8), (0.5, 0.85), (0.5, 0.85), (0.3, 0.6), (0.3, 0.6), (0.1, 0.2), (0.4, 0.1)]
        for aspect in (1.0, 2.0):
            scale, spots = assemble.lay_out(sizes, aspect, 0.006)
            boxes = [(x, y, x + w * scale, y + h * scale) for (x, y), (w, h) in zip(spots, sizes)]
            for box in boxes:
                self.assertGreaterEqual(box[0], 0.0)
                self.assertGreaterEqual(box[1], 0.0)
                self.assertLessEqual(box[2], aspect + 1e-9)
                self.assertLessEqual(box[3], 1.0 + 1e-9)
            for i, a in enumerate(boxes):
                for b in boxes[i + 1:]:
                    apart = a[2] <= b[0] + 1e-9 or b[2] <= a[0] + 1e-9 or a[3] <= b[1] + 1e-9 or b[3] <= a[1] + 1e-9
                    self.assertTrue(apart, f"{a} and {b} overlap")

    def test_one_box_fills_the_image_up_to_the_gap(self):
        scale, spots = assemble.lay_out([(1.0, 1.0)], 1.0, 0.01)
        self.assertAlmostEqual(scale, 0.99, places=6)
        self.assertAlmostEqual(spots[0][0], 0.005, places=6)

    def test_a_wider_image_takes_larger_boxes(self):
        sizes = [(1.0, 1.0), (1.0, 1.0)]
        self.assertGreater(assemble.lay_out(sizes, 2.0, 0.0)[0], assemble.lay_out(sizes, 1.0, 0.0)[0] * 1.9)

    def test_a_face_is_cut_through_its_straight_corner_on_both_bodies(self):
        # On the first body vertex 1 lies almost on the line from 0 to 2; on the second it is a square.
        bent = np.array([(0, 0, 0), (1, 0.01, 0), (2, 0, 0), (1, 1, 0)], float)
        square = np.array([(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)], float)
        nets = {"Male": {"points": bent, "faces": [(0, 1, 2, 3)]}, "Female": {"points": square, "faces": [(0, 1, 2, 3)]}}
        self.assertEqual(assemble.cut_faces(nets), [(1, 2, 3, 0)])

    def test_bodies_of_different_nets_are_refused(self):
        points = np.zeros((4, 3))
        nets = {"Male": {"points": points, "faces": [(0, 1, 2, 3)]}, "Female": {"points": points, "faces": [(1, 2, 3, 0)]}}
        with self.assertRaises(RuntimeError):
            assemble.cut_faces(nets)

    def needles(self, pinned):
        """A needle on each side of the mirror plane, the same on both bodies: vertices 0 to 3 at
        the right, 4 to 7 their mirror images."""
        right = np.array([(1, 0, 0), (2, 0, 0), (2, 0.01, 0.3), (1, 0.01, 0)], float)
        points = np.concatenate([right, right * (-1, 1, 1)])
        faces = [(0, 1, 2, 3), (7, 6, 5, 4)]
        twin = np.array([4, 5, 6, 7, 0, 1, 2, 3])
        return {label: {"points": points.copy(), "faces": faces, "pinned": np.array(pinned, bool), "twin": twin}
                for label in assemble.LABELS}

    def test_mending_leaves_good_faces_alone(self):
        square = np.array([(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)], float)
        nets = {label: {"points": square.copy(), "faces": [(0, 1, 2, 3)], "pinned": np.zeros(4, bool), "twin": np.arange(4)}
                for label in assemble.LABELS}
        assemble.mend(nets)
        for net in nets.values():
            np.testing.assert_allclose(net["points"], square)

    def test_mending_never_moves_a_pinned_vertex_and_says_what_it_could_not_mend(self):
        nets = self.needles([True] * 8)
        before = {label: net["points"].copy() for label, net in nets.items()}
        with self.assertRaises(RuntimeError):
            assemble.mend(nets)
        for label, net in nets.items():
            np.testing.assert_allclose(net["points"], before[label])

    def test_mending_moves_a_vertex_with_its_mirror_image(self):
        # Only vertex 2 is loose; its mirror image 6 is pinned by mistake and must still follow.
        nets = self.needles([True, True, False, True, True, True, True, True])
        try:
            assemble.mend(nets, rounds=1)
        except RuntimeError:
            pass
        for net in nets.values():
            np.testing.assert_allclose(net["points"][6], net["points"][2] * (-1, 1, 1))
            self.assertFalse(np.allclose(net["points"][2], (2, 0.01, 0.3)))

    def test_a_triangle_is_left_as_it_is(self):
        points = np.array([(0, 0, 0), (1, 0, 0), (0, 1, 0)], float)
        nets = {label: {"points": points, "faces": [(0, 1, 2)]} for label in assemble.LABELS}
        self.assertEqual(assemble.cut_faces(nets), [(0, 1, 2)])


if __name__ == "__main__":
    unittest.main()
