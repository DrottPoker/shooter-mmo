"""The logic of the human build that needs numpy but not Blender: the net's subdivision, mirror image,
islands, seams, slivers, folds, and tucks, the islands that follow from the net's shape, the way
along a skin that lays the head cage's line under the chin, the outline of the cage's faces over a
nostril, the ear's patch of the control cage and the band of triangles that sets it into the head
cage, and the cutting of faces and the layout of the UV map, on small nets and skins whose answers
follow from the input. Without numpy this file is skipped; see test_numeric_logic.py and
blender_source.py."""

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
reference = load("human_build/reference.py", "_along_skin", "HEAD_CHIN_STEP", "_to_line", "_rim", "ear_patch", "without_tiny",
                 "_thin", "band", "EAR_MARGIN", "evened", "arm_shares", "turned_out", "passage_evened", numpy=True)
head = load("human_build/head.py", "outline", "with_room", numpy=True)
body = load("human_build/body.py", "refine_trunk", "NAVEL_ROW", "CHEST_LINE", numpy=True, Net=Net, FIXED=FIXED, MIRROR=MIRROR,
            PINNED=PINNED)


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

    def test_a_net_tucked_into_itself_is_found_and_an_open_fold_or_a_ridge_is_not(self):
        def fold(across, height, skin="skin"):
            """Two faces that look up and share the edge from vertex 1 to vertex 4, which lies on
            the ground; their outer edges lie this far across and this high."""
            net = Net()
            for y in (0, 1):
                for x, z in ((-across, height), (0, 0), (across, height)):
                    net.add((x, y, z))
            net.quad(0, 1, 4, 3, 0, "skin")
            net.quad(1, 2, 5, 4, 0, skin)
            return net

        # Walls that rise steeply from the shared edge look toward each other.
        self.assertEqual(fold(0.2, 1.0).tucked(), [(1, 4)])
        # A fold as open as a right angle is a crease of the body.
        self.assertEqual(fold(1.0, 1.0).tucked(), [])
        # The same walls falling away from the edge are a ridge.
        self.assertEqual(fold(0.2, -1.0).tucked(), [])
        self.assertEqual(fold(0.2, 1.0, skin="mouth").tucked(hidden=("mouth",)), [])
        # Two toes lie against each other: the edge through their web is no tuck, and a fold
        # within one toe still is.
        self.assertEqual(fold(0.2, 1.0, skin="toe").tucked(apart=("skin", "toe")), [])
        self.assertEqual(fold(0.2, 1.0).tucked(apart=("skin", "toe")), [(1, 4)])

    def test_a_vertex_looks_the_way_of_its_faces_of_any_number_of_corners(self):
        net = Net()
        for point in [(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0), (2, 0, 0), (2, 1, 0), (1.5, 2, 0)]:
            net.add(point)
        net.quad(0, 1, 2, 3, 0, "skin")
        net.face((1, 4, 2), 0, "skin")
        net.face((4, 5, 6, 2), 0, "skin")
        np.testing.assert_allclose(net.normals(), np.tile([0.0, 0.0, 1.0], (7, 1)), atol=1e-12)
        # The measures of a face's corners are those of the faces of four corners, by number.
        self.assertEqual(net.quality()[-1].tolist(), [0, 2])


class ReferenceTests(unittest.TestCase):
    RADIUS = 0.05

    def round_skin(self, point):
        """The nearest point of a tube around the x axis, a little off the mirror plane."""
        across = np.array([0.0, point[1], point[2]])
        return np.array([0.001, 0.0, 0.0]) + across / np.linalg.norm(across) * self.RADIUS

    def test_the_way_along_a_round_skin_is_as_long_as_asked_and_stays_on_the_mirror_plane(self):
        # A quarter of the way around: from the front of the tube to straight under it.
        point, heading = reference._along_skin(self.round_skin, (0.0, self.RADIUS, 0.0), (0.0, 0.0, -1.0),
                                               self.RADIUS * np.pi / 2)
        np.testing.assert_allclose(point, (0.0, 0.0, -self.RADIUS), atol=2e-5)
        np.testing.assert_allclose(heading, (0.0, -1.0, 0.0), atol=0.02)

    def test_the_way_goes_on_from_where_the_last_one_ended(self):
        eighth = self.RADIUS * np.pi / 4
        point, heading = reference._along_skin(self.round_skin, (0.0, self.RADIUS, 0.0), (0.0, 0.0, -1.0), eighth)
        point, heading = reference._along_skin(self.round_skin, point, heading, eighth)
        np.testing.assert_allclose(point, (0.0, 0.0, -self.RADIUS), atol=2e-5)

    def test_a_way_that_cannot_go_on_says_so(self):
        with self.assertRaises(RuntimeError):
            reference._along_skin(lambda point: (0.0, self.RADIUS, 0.0), (0.0, self.RADIUS, 0.0), (0.0, 0.0, -1.0), 0.01)


class PoseTests(unittest.TestCase):
    """The turn of the arms: the shares of it along a net, and the turn itself."""

    def test_values_even_out_between_the_fixed_ends_of_a_line(self):
        edges = [(0, 1), (1, 2), (2, 3), (3, 4)]
        fixed = [True, False, False, False, True]
        out = reference.evened(5, edges, [1.0] * 4, fixed, [0.0, 9.0, 9.0, 9.0, 1.0])
        np.testing.assert_allclose(out, [0.0, 0.25, 0.5, 0.75, 1.0], atol=1e-9)

    def test_a_vertex_lies_nearer_to_the_value_of_the_neighbour_its_edge_weighs_more_for(self):
        out = reference.evened(3, [(0, 1), (1, 2)], [1.0, 3.0], [True, False, True], [0.0, 0.0, 1.0])
        np.testing.assert_allclose(out, [0.0, 0.75, 1.0], atol=1e-9)

    def test_a_net_that_is_fixed_everywhere_keeps_its_values(self):
        np.testing.assert_allclose(reference.evened(2, [(0, 1)], [1.0], [True, True], [0.25, 0.5]), [0.25, 0.5])

    JOINT = np.array([0.20, 0.0, 1.43])
    ARMPIT = 1.30

    def shoulder(self):
        """A line of four vertices over the top of each shoulder of a body of 1.79 m, whose right
        shoulder joint lies at JOINT: one on the trunk well inside the joint, two over the joint,
        and one on the round of the shoulder outside it. The left side is the mirror image."""
        right = np.array([(0.05, 0.0, 1.40), (0.185, 0.0, 1.47), (0.205, 0.0, 1.47), (0.25, 0.0, 1.40)])
        points = np.concatenate([right, right * (-1.0, 1.0, 1.0)])
        edges = [(0, 1), (1, 2), (2, 3), (4, 5), (5, 6), (6, 7)]
        return points, edges

    def shares(self, points, edges, in_arm):
        return reference.arm_shares(points, edges, in_arm, self.JOINT, self.ARMPIT, 1.0)

    def test_the_round_of_the_shoulder_turns_whole_and_the_trunk_not_and_the_top_evens_out_between(self):
        points, edges = self.shoulder()
        shares = self.shares(points, edges, np.zeros(8, bool))
        self.assertEqual((shares[0], shares[3]), (0.0, 1.0))
        # Along one line the share grows with the way gone along it.
        steps = np.linalg.norm(np.diff(points[:4], axis=0), axis=1)
        np.testing.assert_allclose(shares[1:3], np.cumsum(steps)[:2] / steps.sum(), atol=1e-9)
        np.testing.assert_allclose(shares[4:], shares[:4], atol=1e-12)

    def test_the_arm_turns_whole_from_a_little_below_the_armpit_and_the_side_of_the_chest_stays(self):
        # From the side of the chest under the armpit, over the hollow, to the arm: the chest's
        # vertex stays, the arm's first vertex below the armpit is not yet turned whole, and the
        # next one is.
        points = np.array([(0.15, 0.0, 1.26), (0.17, 0.0, 1.30), (0.19, 0.0, 1.285), (0.21, 0.0, 1.25)])
        shares = self.shares(points, [(0, 1), (1, 2), (2, 3)], np.array([False, False, True, True]))
        self.assertEqual((shares[0], shares[3]), (0.0, 1.0))
        self.assertTrue(0.0 < shares[1] < shares[2] < 1.0)

    def test_a_hip_as_wide_as_the_shoulder_joint_stays(self):
        # Only above the armpit does what lies outside the joint belong to the shoulder.
        points = np.array([(0.25, 0.0, 0.95), (0.25, 0.0, 1.40)])
        shares = self.shares(points, [(0, 1)], np.zeros(2, bool))
        np.testing.assert_allclose(shares, [0.0, 1.0])

    def test_the_stretched_skin_of_the_passage_is_evened_out_and_the_trunk_and_the_arm_are_not(self):
        # A line from the trunk over the passage to the arm, below the joint, with a spike on
        # the vertex in the middle of the passage, and one vertex of the passage above the joint.
        before = np.array([(0.15, 0.0, 1.20), (0.20, 0.0, 1.15), (0.25, 0.0, 1.10), (0.20, 0.0, 1.50)])
        after = reference.turned_out(before, [0.0, 0.5, 1.0, 0.5], self.JOINT, 40.0)
        after[1, 1] += 0.01
        after[3, 1] += 0.01
        edges = [(0, 1), (1, 2), (0, 3), (3, 2)]
        out = reference.passage_evened(before, after, edges, self.JOINT, 40.0, rounds=1)
        np.testing.assert_allclose(out[[0, 2, 3]], after[[0, 2, 3]], atol=1e-12)
        np.testing.assert_allclose(out[1], (after[1] + (after[0] + after[2]) / 2) / 2, atol=1e-9)

    def test_an_arm_turns_out_about_its_shoulder_joint_and_the_left_as_its_mirror_image(self):
        joint = np.array([0.2, 0.0, 1.4])
        # A point of each arm 30 cm straight below its joint, and a point of the trunk.
        points = np.array([(0.2, 0.01, 1.1), (-0.2, 0.01, 1.1), (0.0, 0.1, 1.0)])
        out = reference.turned_out(points, [1.0, 1.0, 0.0], joint, 90.0)
        np.testing.assert_allclose(out, [(0.5, 0.01, 1.4), (-0.5, 0.01, 1.4), (0.0, 0.1, 1.0)], atol=1e-12)
        half = reference.turned_out(points, [0.5, 0.5, 0.0], joint, 90.0)
        np.testing.assert_allclose(half[0], (0.2 + 0.3 * np.sqrt(0.5), 0.01, 1.4 - 0.3 * np.sqrt(0.5)), atol=1e-12)
        np.testing.assert_allclose(half[1], half[0] * (-1.0, 1.0, 1.0), atol=1e-12)


class EarTests(unittest.TestCase):
    """The ear's patch of a control cage and the band around it, on a flat cage of square faces that
    looks along +X: eleven by eleven vertices a centimetre apart, with face 10 * row + column."""

    def cage(self, outermost=(5, 5)):
        points = np.array([(1.0, 0.01 * column, 0.01 * row) for row in range(11) for column in range(11)])
        # The ear stands out from the head: the flood starts at the cage's outermost vertex.
        points[11 * outermost[0] + outermost[1], 0] += 0.01
        polys = [(11 * row + column, 11 * row + column + 1, 11 * row + column + 12, 11 * row + column + 11)
                 for row in range(10) for column in range(10)]
        return points, polys

    @staticmethod
    def square(low, high, top=None):
        """The corners of a square in the plane of the cage and the middles of its sides, counter-
        clockwise seen from +X."""
        top = high if top is None else top
        corners = np.array([(1.0, low, low), (1.0, high, low), (1.0, high, top), (1.0, low, top)])
        return np.array([p for a, b in zip(corners, np.roll(corners, -1, axis=0)) for p in (a, (a + b) / 2)])

    def test_a_point_is_as_far_from_a_closed_line_as_from_its_nearest_side(self):
        line = [(0, 0, 0), (2, 0, 0), (2, 2, 0), (0, 2, 0)]
        far = reference._to_line([(1, 1, 0), (1, 0.5, 0), (3, 3, 0), (0, 1, 1)], line)
        np.testing.assert_allclose(far, [1.0, 0.5, np.sqrt(2.0), 1.0])

    def test_the_edge_of_a_patch_runs_around_it_the_way_its_faces_run(self):
        polys = [(0, 1, 4, 3), (1, 2, 5, 4), (9, 9, 9)]
        rim = reference._rim(polys, [0, 1])
        self.assertEqual(rim, [0, 1, 2, 5, 4, 3])

    def test_a_patch_that_only_hangs_together_in_a_vertex_has_no_edge(self):
        with self.assertRaises(RuntimeError):
            reference._rim([(0, 1, 4, 3), (4, 5, 8, 7)], [0, 1])

    def test_the_patch_is_the_faces_that_keep_clear_of_the_outline(self):
        points, polys = self.cage()
        # A face whose corner lies a centimetre inside the outline keeps the margin of 6 mm; the
        # faces beside it reach the outline itself.
        faces, rim = reference.ear_patch([self.square(0.02, 0.08)], [points], polys)
        self.assertEqual(faces, [10 * row + column for row in range(3, 7) for column in range(3, 7)])
        self.assertEqual(len(rim), 16)
        self.assertEqual(set(rim), {v for f in faces for v in polys[f]} - {11 * row + column for row in (4, 5, 6) for column in (4, 5, 6)})

    def test_a_face_must_keep_clear_on_every_body(self):
        points, polys = self.cage()
        # On the second body the outline's upper side lies half a centimetre lower: the upper row
        # of the first body's faces comes within the margin of it.
        faces, _rim = reference.ear_patch([self.square(0.02, 0.08), self.square(0.02, 0.08, top=0.075)], [points, points], polys)
        self.assertEqual(faces, [10 * row + column for row in range(3, 6) for column in range(3, 7)])

    def test_a_face_that_hangs_on_the_patch_by_one_edge_is_left_out(self):
        points, polys = self.cage()
        # The outline reaches two rows higher, but of the row above the square only one face is
        # there: it would stick out of the patch's edge.
        kept = [poly for f, poly in enumerate(polys) if f // 10 != 7 or f == 75]
        faces, _rim = reference.ear_patch([self.square(0.02, 0.08, top=0.10)], [points], kept)
        self.assertEqual([kept[f] for f in faces], [polys[10 * row + column] for row in range(3, 7) for column in range(3, 7)])

    def test_an_outline_that_does_not_close_the_ear_off_is_refused(self):
        points, polys = self.cage()
        with self.assertRaises(RuntimeError):
            reference.ear_patch([self.square(0.02, 0.08) + (0.5, 0.0, 0.0)], [points], polys)

    def test_a_face_that_is_too_small_on_a_body_loses_its_shortest_edge(self):
        # A needle of a triangle between two faces of a square millimetre and more:
        #     3 . 4 5        vertices 4 and 5 lie a tenth of a millimetre apart
        #     0 . 1 2
        points = np.array([(0, 0, 0), (2, 0, 0), (4, 0, 0), (0, 2, 0), (2, 2, 0), (2.1, 2, 0)], float) / 1000
        faces = [(0, 1, 4, 3), (1, 5, 4), (1, 2, 5)]
        # Every face but the needle is large enough: the needle goes, and the faces beside it
        # take the vertex that is left of its shortest edge.
        self.assertEqual(reference.without_tiny(faces, [points], least=1.2e-6), [(0, 1, 5, 3), (1, 2, 5)])
        self.assertEqual(reference.without_tiny(faces, [points], least=0.05e-6), faces)
        # On a body of half the size the last face, of two square millimetres on the first body,
        # is too small as well.
        self.assertEqual(reference.without_tiny(faces, [points, points / 2], least=0.7e-6), [(0, 1, 5, 3)])

    def test_a_triangle_of_equal_sides_is_not_thin_and_a_needle_is(self):
        a, b = np.array([0.0, 0.0, 0.0]), np.array([1.0, 0.0, 0.0])
        self.assertAlmostEqual(reference._thin(a, b, np.array([0.5, np.sqrt(0.75), 0.0])), 1.0)
        self.assertGreater(reference._thin(a, b, np.array([0.5, 0.01, 0.0])), 40.0)

    def test_the_band_between_two_lines_is_closed_by_triangles_that_look_out(self):
        outer, inner = self.square(0.0, 0.04), self.square(0.01, 0.03)[::2]
        outward = np.tile([1.0, 0.0, 0.0], (len(inner), 1))
        triangles, thinnest = reference.band([outer], [inner], outward)
        # One triangle for each side of each line, and together they cover the band exactly.
        self.assertEqual(len(triangles), len(outer) + len(inner))
        lines = {"outer": outer, "inner": inner}
        area = 0.0
        for triangle in triangles:
            a, b, c = (lines[name][place] for name, place in triangle)
            normal = np.cross(b - a, c - a)
            self.assertGreater(normal[0], 0.0)
            area += normal[0] / 2
        self.assertAlmostEqual(area, 0.04 ** 2 - 0.02 ** 2)
        sides = {(triangle[0], triangle[1]) for triangle in triangles}
        self.assertEqual(len({side for side in sides if side[0][0] == side[1][0] == "outer"}), len(outer))
        self.assertEqual(len({side for side in sides if side[0][0] == side[1][0] == "inner"}), len(inner))
        self.assertLess(thinnest, 3.0)

    def test_a_band_whose_lines_run_against_each_other_is_refused(self):
        outer, inner = self.square(0.0, 0.04), self.square(0.01, 0.03)[::2]
        outward = np.tile([1.0, 0.0, 0.0], (len(inner), 1))
        with self.assertRaises(RuntimeError):
            reference.band([outer[::-1]], [inner], outward)


class TrunkTests(unittest.TestCase):
    """The navel and the line over the chest, on the front of a trunk as a flat cage that looks
    along +Y: rows 6 cm apart of two faces 4 cm wide beside the mirror plane."""
    ROWS = 13

    class Flat:
        """A body whose front is the plane y = 0, with its navel on the cage's row for it."""
        scale = 1.0
        navel_z = 0.06 * 4

        @staticmethod
        def front(x, z):
            return np.array([x, 0.0, z])

    def refined(self):
        net = Net()
        rows = [[net.add((0.08 - 0.04 * column, 0.0, 0.06 * row), *([MIRROR] if column == 2 else [])) for column in range(3)]
                for row in range(self.ROWS)]
        net.island = "front"
        for lower, upper in zip(rows, rows[1:]):
            for k in range(2):
                net.quad(lower[k], lower[k + 1], upper[k + 1], upper[k], 2, "trunk")
        net.mark("front_centre", [row[-1] for row in rows])
        net.mark("front_beside", [row[-2] for row in rows])
        fine = net.subdivided()
        before = (len(fine.faces), fine.open_vertices())
        body.refine_trunk(fine, net, self.Flat)
        return fine, before

    def test_the_navel_and_the_chest_line_part_faces_without_a_gap(self):
        self.assertEqual((body.NAVEL_ROW, body.CHEST_LINE), (4, (8, 11)))
        fine, (faces_before, open_before) = self.refined()
        # The line parts eight faces at the centre line, from half a row below its first row to
        # half a row above its last, and the navel's two faces become twelve.
        self.assertEqual(len(fine.faces), faces_before + 8 + 10)
        self.assertEqual(sorted(len(face) for face in fine.faces).count(3), 2 + 4)
        self.assertEqual(fine.check(), [])
        # The edge around the front is the one it had: no face has come loose from its neighbour.
        self.assertEqual(fine.open_vertices() - open_before, {v for v in fine.open_vertices() if fine.points[v][0] == 0.0} - open_before)
        points, area = fine.array(), 0.0
        for face in fine.faces:
            p = points[list(face)]
            normal = np.cross(p, np.roll(p, -1, axis=0)).sum(0)
            self.assertGreater(normal[1], 0.0)
            area += normal[1] / 2
        self.assertAlmostEqual(area, 0.08 * 0.06 * (self.ROWS - 1))
        self.assertEqual(set(fine.islands), {"front"})
        self.assertEqual(set(fine.skins), {"trunk"})

    def test_the_vertices_of_the_navel_stay_where_they_are_put_and_on_the_mirror_plane(self):
        fine, _before = self.refined()
        pinned = [v for v, holds in enumerate(fine.holds) if PINNED in holds]
        # The vertex in the navel's depth, and five on its edge and five halfway to it.
        self.assertEqual(len(pinned), 11)
        for v in pinned:
            self.assertIn(FIXED, fine.holds[v])
            self.assertEqual(MIRROR in fine.holds[v], fine.points[v][0] == 0.0)
            self.assertLess(np.linalg.norm(fine.points[v] - (0.0, 0.0, self.Flat.navel_z)), 0.0131)


class HeadTests(unittest.TestCase):
    def test_the_outline_of_two_faces_runs_around_both_the_way_they_run(self):
        # Two faces that share the edge between vertices 1 and 4:
        #     3 4 5
        #     0 1 2
        loop = head.outline([(0, 1, 4, 3), (1, 2, 5, 4)])
        start = loop.index(0)
        self.assertEqual(loop[start:] + loop[:start], [0, 1, 2, 5, 4, 3])

    def test_faces_that_only_touch_in_a_vertex_have_no_outline(self):
        with self.assertRaises(RuntimeError):
            head.outline([(0, 1, 4, 3), (4, 5, 8, 7)])

    def test_an_outline_makes_room_for_a_point_that_lies_too_near_to_one_of_its_edges(self):
        square = [(0.0, 0.0), (4.0, 0.0), (4.0, 4.0), (0.0, 4.0)]
        # The point lies half a unit inside the right edge, which owes it a whole unit.
        moved = head.with_room(square, [(3.5, 2.0), (2.0, 2.0)], 1.0)
        np.testing.assert_allclose(moved, [(0.0, 0.0), (4.5, 0.0), (4.5, 4.0), (0.0, 4.0)])

    def test_an_outline_with_room_enough_stays_as_it_is(self):
        square = [(0.0, 0.0), (4.0, 0.0), (4.0, 4.0), (0.0, 4.0)]
        np.testing.assert_allclose(head.with_room(square, [(2.0, 2.0), (1.0, 3.0)], 1.0), square)

    def test_a_corner_moves_with_both_of_its_edges(self):
        square = [(0.0, 0.0), (4.0, 0.0), (4.0, 4.0), (0.0, 4.0)]
        # The point lies half a unit inside the right edge and the upper edge.
        moved = head.with_room(square, [(3.5, 3.5)], 1.0)
        np.testing.assert_allclose(moved[2], (4.5, 4.5))
        for corner in moved:
            self.assertGreaterEqual(4.5 + 1e-9, max(corner))


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

    def test_the_toes_part_into_the_top_and_the_sole_of_the_foot(self):
        # A toe on each side as a tube of four faces along +Y that stands on one edge: two of
        # its faces look up and two down.
        net = Net()
        ring = [(1.5, 0.0), (1.0, 0.5), (0.5, 0.0), (1.0, -0.5)]
        for sign in (1.0, -1.0):
            for y in (0.0, 1.0):
                for x, z in ring:
                    net.add((sign * x, y, z))
        net.island = "toes_R"
        for k in range(4):
            net.quad(k, k + 4, (k + 1) % 4 + 4, (k + 1) % 4, 6, "toe")
        net.island = "toes_L"
        for k in range(4):
            net.quad((k + 1) % 4 + 8, (k + 1) % 4 + 12, k + 12, k + 8, 6, "toe")
        islands.part_toes(net)
        self.assertEqual(net.islands[:4], ["foot_top_R", "foot_top_R", "foot_sole_R", "foot_sole_R"])
        self.assertEqual(net.islands[4:], ["foot_top_L", "foot_top_L", "foot_sole_L", "foot_sole_L"])

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

    def test_the_back_of_an_ear_is_one_piece(self):
        net = Net()
        # A right ear with a back of two faces side by side, and, apart from them, one face
        # inside a fold that looks back as well.
        for point in [(1, 0, 0), (1, 1, 0), (1, 2, 0), (1, 0, 1), (1, 1, 1), (1, 2, 1), (1, 5, 0), (1, 6, 0), (1, 6, 1), (1, 5, 1)]:
            net.add(point)
        net.island = "head"
        net.quad(3, 4, 1, 0, 0, "ear")
        net.quad(4, 5, 2, 1, 0, "ear")
        net.quad(9, 8, 7, 6, 0, "ear")
        islands.part_ears(net)
        self.assertEqual(net.islands, ["ear_back_R", "ear_back_R", "head"])


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

    def test_a_bent_face_is_cut_along_its_side_of_the_skin(self):
        # A face whose corners 1 and 3 stand 3 mm above the plane of corners 0 and 2: cut from 0
        # to 2 it has a valley along the cut, cut from 1 to 3 a ridge.
        points = np.array([(0, 0, 0), (0.02, 0, 0.003), (0.02, 0.02, 0), (0, 0.02, 0.003)], float)

        def nets(skin):
            return {label: {"points": points, "faces": [(0, 1, 2, 3)], "skin": skin} for label in assemble.LABELS}

        # The skin lies in the valley, or over the ridge.
        self.assertEqual(assemble.cut_faces(nets(lambda point: abs(point[2]))), [(0, 1, 2, 3)])
        self.assertEqual(assemble.cut_faces(nets(lambda point: abs(point[2] - 0.003))), [(1, 2, 3, 0)])
        # A face of a hidden part lies on no skin, and a net that knows no skin is cut as before.
        hidden = nets(lambda point: abs(point[2] - 0.003))
        for net in hidden.values():
            net["part"] = [7]
        self.assertEqual(assemble.cut_faces(hidden), [(0, 1, 2, 3)])
        plain = {label: {"points": points, "faces": [(0, 1, 2, 3)]} for label in assemble.LABELS}
        self.assertEqual(assemble.cut_faces(plain), [(0, 1, 2, 3)])

    def test_a_triangle_is_left_as_it_is(self):
        points = np.array([(0, 0, 0), (1, 0, 0), (0, 1, 0)], float)
        nets = {label: {"points": points, "faces": [(0, 1, 2)]} for label in assemble.LABELS}
        self.assertEqual(assemble.cut_faces(nets), [(0, 1, 2)])


if __name__ == "__main__":
    unittest.main()
