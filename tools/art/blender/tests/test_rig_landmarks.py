"""The logic of the rebuilt bodies' rig that needs numpy but not Blender: the landmarks the assembly
writes from the build's named vertices (human_build/landmarks.py), the eyelids' weights
(human_base/lids.py), the lips that meet (human_base/rig.py), and the turn that brings an arm of
another bind pose to the one the test poses were made for, on small nets and lines whose answers
follow from the input. Without numpy this file is skipped."""

import math
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

from human_base import lids  # noqa: E402
from human_build import landmarks  # noqa: E402
from human_build.net import Net  # noqa: E402

poses = load("art_pipeline/poses.py", "arm_turn", "eyelids", "REST_ARM_DROP", "ARM_TOLERANCE")
rig = load("human_base/rig.py", "lip_sides", "mouth_share", "skull_shares", "JAW_CORNER", "JAW_OVAL", "JAW_LIP_BAND",
           "JAW_CORNER_BAND", "JAW_CHEEK_BAND", "JAW_LIP_REACH", "SKULL_BAND", numpy=True)


class LipTests(unittest.TestCase):
    # Two lips that meet: the upper lip's edge 0, 1 and the lower lip's 2, 3 at the same places,
    # the upper lip's next row 4, 5 above them and the lower lip's 6, 7 below.
    POINTS = np.array([[0.0, 0, 0], [0.01, 0, 0], [0.0, 0, 0], [0.01, 0, 0],
                       [0.0, 0, 0.01], [0.01, 0, 0.01], [0.0, 0, -0.01], [0.01, 0, -0.01]])
    FACES = [(0, 1, 5, 4), (2, 6, 7, 3)]

    def test_each_lip_at_the_line_where_they_meet_is_named_by_the_side_its_neighbours_lie_on(self):
        self.assertEqual(rig.lip_sides(self.POINTS, self.FACES, np.arange(8)).tolist(), [1, 1, -1, -1, 0, 0, 0, 0])

    def test_a_body_without_vertices_at_one_place_names_no_lip(self):
        points = self.POINTS[[0, 1, 4, 5, 6, 7]]
        self.assertEqual(rig.lip_sides(points, [(0, 1, 3, 2), (0, 4, 5, 1)], np.arange(6)).tolist(), [0] * 6)

    def test_a_row_given_twice_is_named_each_time(self):
        # The rows of the head and the neck name the vertices of their cut twice.
        rows = np.array([0, 1, 2, 3, 4, 5, 6, 7, 2])
        self.assertEqual(rig.lip_sides(self.POINTS, self.FACES, rows).tolist(), [1, 1, -1, -1, 0, 0, 0, 0, -1])


class MouthShareTests(unittest.TestCase):
    """How much of the jaw's turn the skin at the mouth takes, for a mouth whose corner lies 25 mm
    from its middle."""
    CORNER = 0.025

    def share(self, height, across, lips=None):
        return float(rig.mouth_share(np.array([height]), np.array([across]), self.CORNER,
                                     None if lips is None else np.array([lips]))[0])

    def test_in_the_middle_the_lower_lip_follows_the_jaw_and_the_upper_lip_the_head(self):
        self.assertAlmostEqual(self.share(0.0, 0.0, lips=-1), 1.0)
        self.assertAlmostEqual(self.share(0.0, 0.0, lips=1), 0.0)
        self.assertAlmostEqual(self.share(-0.001, 0.0), 1.0)
        self.assertAlmostEqual(self.share(0.001, 0.0), 0.0)

    def test_the_corner_follows_the_jaw_by_its_share_whichever_lip_names_it(self):
        for lips in (None, 1, -1):
            self.assertAlmostEqual(self.share(0.0, self.CORNER, lips), rig.JAW_CORNER)
        # Less than half, so the upper lip stays nearly level.
        self.assertTrue(0.0 < rig.JAW_CORNER < 0.5)

    def test_the_lips_part_by_the_height_of_the_oval(self):
        for across in (0.0, 0.2, 0.5, 0.8, 0.95, 1.0):
            x = across * self.CORNER
            oval = (1.0 - across * across) ** rig.JAW_OVAL
            with self.subTest(across=across):
                self.assertAlmostEqual(self.share(0.0, x, lips=-1) - self.share(0.0, x, lips=1), oval)
                # Each lip covers its share of the way to the corner's share.
                self.assertAlmostEqual(self.share(0.0, x, lips=1), rig.JAW_CORNER * (1.0 - oval))
                self.assertAlmostEqual(self.share(0.0, x, lips=-1), 1.0 - (1.0 - rig.JAW_CORNER) * (1.0 - oval))

    def test_the_oval_lies_between_an_ellipse_and_two_arcs(self):
        self.assertTrue(0.5 <= rig.JAW_OVAL <= 1.0)

    def test_a_lip_follows_the_jaw_by_more_toward_the_lower_lips_middle(self):
        lower = [self.share(0.0, x * self.CORNER, lips=-1) for x in (0.0, 0.3, 0.6, 0.9, 1.0)]
        upper = [self.share(0.0, x * self.CORNER, lips=1) for x in (0.0, 0.3, 0.6, 0.9, 1.0)]
        self.assertEqual(lower, sorted(lower, reverse=True))
        self.assertEqual(upper, sorted(upper))

    def test_a_point_on_the_line_that_names_no_lip_takes_the_mean_of_the_two(self):
        x = 0.6 * self.CORNER
        mean = (self.share(0.0, x, lips=1) + self.share(0.0, x, lips=-1)) / 2.0
        self.assertAlmostEqual(self.share(0.0, x), mean)
        self.assertAlmostEqual(self.share(0.0, x, lips=0), mean)

    def test_a_named_lip_counts_only_near_the_line(self):
        # A vertex that shares its place far from the line is skin above or below it like any other.
        above = rig.JAW_LIP_REACH * 2
        self.assertAlmostEqual(self.share(above, 0.0, lips=-1), self.share(above, 0.0))
        self.assertAlmostEqual(self.share(-above, 0.0, lips=1), self.share(-above, 0.0))

    def test_the_skin_passes_from_the_head_to_the_jaw_over_the_band_at_the_corner_and_beyond_it(self):
        band = rig.JAW_LIP_BAND + rig.JAW_CORNER_BAND
        self.assertAlmostEqual(self.share(band, self.CORNER), 0.0)
        self.assertAlmostEqual(self.share(-band, self.CORNER), 1.0)
        self.assertGreater(self.share(band / 2, self.CORNER), 0.0)
        self.assertLess(self.share(band / 2, self.CORNER), rig.JAW_CORNER)
        # Beyond the corner the band is wider, so the cheek stretches over more skin.
        wide = band + 0.01 * rig.JAW_CHEEK_BAND
        self.assertAlmostEqual(self.share(wide, self.CORNER + 0.01), 0.0)
        self.assertGreater(self.share(band, self.CORNER + 0.01), 0.0)

    def test_the_share_falls_steadily_from_below_the_line_to_above_it(self):
        for across in (0.3, 0.7, 1.0, 1.4):
            heights = np.linspace(-0.02, 0.02, 81)
            shares = rig.mouth_share(heights, np.full(81, across * self.CORNER), self.CORNER)
            with self.subTest(across=across):
                self.assertTrue((np.diff(shares) <= 1e-12).all())
                self.assertTrue(((shares >= 0.0) & (shares <= 1.0)).all())


class SkullTests(unittest.TestCase):
    """How much of its weight on the neck the head's skin keeps, by its distance from the cut."""
    CUT = np.array([[0.0, 0.0, 0.0], [0.02, 0.0, 0.0]])
    BAND = (0.005, 0.035)

    def keeps(self, *point):
        return float(rig.skull_shares(np.array([point]), self.CUT, self.BAND)[0])

    def test_the_skin_at_the_cut_keeps_its_weight_and_the_face_keeps_none(self):
        self.assertAlmostEqual(self.keeps(0.0, 0.0, 0.0), 1.0)
        self.assertAlmostEqual(self.keeps(0.02, 0.003, 0.004), 1.0)
        self.assertAlmostEqual(self.keeps(0.0, 0.0, 0.035), 0.0)
        self.assertAlmostEqual(self.keeps(0.0, 0.08, 0.06), 0.0)

    def test_the_distance_is_to_the_nearest_point_of_the_cut(self):
        self.assertAlmostEqual(self.keeps(0.02, 0.0, 0.02), self.keeps(0.0, 0.0, 0.02))
        self.assertAlmostEqual(self.keeps(0.0, 0.0, 0.02), 0.5)

    def test_the_weight_falls_steadily_with_the_distance(self):
        kept = [self.keeps(0.0, 0.0, z) for z in np.linspace(0.0, 0.05, 26)]
        self.assertEqual(kept, sorted(kept, reverse=True))

    def test_the_band_ends_well_before_the_chin(self):
        # The chin lies some 7 cm from the cut under the jaw, the mouth further.
        self.assertTrue(0.0 < rig.SKULL_BAND[0] < rig.SKULL_BAND[1] <= 0.04)


class TraceTests(unittest.TestCase):
    def test_a_ring_and_a_line_of_the_cage_run_through_the_middles_that_subdividing_adds(self):
        net = Net()
        for x, y in ((0, 0), (1, 0), (1, 1), (0, 1)):
            net.add((x, y, 0.0))
        net.quad(0, 1, 2, 3, 0, "skin")
        net.mark("arm_end", [0, 1, 2, 3])
        net.mark("lip_upper", [0, 1, 2])
        fine = net.subdivided()
        landmarks.trace(fine)
        middle = fine.edge_point
        self.assertEqual(fine.marks["arm_end_path"], [0, middle[(0, 1)], 1, middle[(1, 2)], 2, middle[(2, 3)], 3, middle[(0, 3)]])
        self.assertEqual(fine.marks["lip_upper_path"], [0, middle[(0, 1)], 1, middle[(1, 2)], 2])
        # The cage's own marks stay as they were.
        self.assertEqual(fine.marks["arm_end"], [0, 1, 2, 3])


class LandmarkTests(unittest.TestCase):
    def test_both_sides_list_the_right_side_first_then_its_mirror_image(self):
        self.assertEqual([p.tolist() for p in landmarks.both([(1.0, 2.0, 3.0), (4.0, 5.0, 6.0)])],
                         [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0], [-1.0, 2.0, 3.0], [-4.0, 5.0, 6.0]])

    def test_a_ring_starts_furthest_toward_a_direction_and_runs_toward_another(self):
        angles = np.radians(np.arange(8) * 45.0)
        points = np.column_stack([np.cos(angles), np.sin(angles), np.zeros(8)])
        ring = list(range(8))
        self.assertEqual(landmarks.ordered_ring(points, ring, (0.0, 1.0, 0.0), (1.0, 0.0, 0.0)), [2, 1, 0, 7, 6, 5, 4, 3])
        self.assertEqual(landmarks.ordered_ring(points, ring, (0.0, 1.0, 0.0), (-1.0, 0.0, 0.0)), [2, 3, 4, 5, 6, 7, 0, 1])

    def test_the_lip_line_takes_the_earlier_bodies_shares_of_the_way_to_the_corner_and_mirrors_them(self):
        line = np.array([[0.0, 0.10, 1.50], [0.01, 0.099, 1.50], [0.02, 0.096, 1.501], [0.025, 0.094, 1.502]])
        points = landmarks.lip_line(line)
        self.assertEqual(len(points), 9)
        xs = [round(float(p[0]), 6) for p in points]
        self.assertEqual(xs, [round(share * 0.025, 6) for share in landmarks.LIP_SHARES] + [0.0]
                         + [round(-share * 0.025, 6) for share in reversed(landmarks.LIP_SHARES)])
        self.assertTrue(np.allclose(points[0] * (-1.0, 1.0, 1.0), points[-1]))
        with self.assertRaises(ValueError):
            landmarks.lip_line(line[::-1])

    def test_the_eye_rim_has_the_earlier_bodies_order_and_shares(self):
        # A lid edge of 24 spokes on a circle in front of the eye, from the inner corner (-x) over
        # the upper lid (+z) to the outer corner (+x) and back under the lower lid.
        angles = np.radians(180.0 - 15.0 * np.arange(24))
        edge = np.column_stack([np.cos(angles), np.full(24, 0.5), 0.5 * np.sin(angles)])
        rim = landmarks.eye_rim(edge)
        self.assertEqual(len(rim), 20)
        self.assertTrue(np.allclose(rim[8], edge[12]))   # the outer corner
        self.assertTrue(np.allclose(rim[18], edge[0]))   # the inner corner
        shares = [(1 - math.cos(math.pi * k / landmarks.RIM_STEPS)) / 2 for k in range(1, landmarks.RIM_STEPS)]
        upper = [rim[19]] + rim[0:8]
        lower = [rim[17]] + rim[16:8:-1]
        for share, top, bottom in zip(shares, upper, lower):
            self.assertAlmostEqual(float(top[0]), -1.0 + 2.0 * share, places=9)
            self.assertAlmostEqual(float(bottom[0]), -1.0 + 2.0 * share, places=9)
            self.assertGreater(top[2], 0.0)
            self.assertLess(bottom[2], 0.0)

    def test_a_lid_edge_that_turns_back_is_refused(self):
        angles = np.radians(180.0 - 15.0 * np.arange(24))
        edge = np.column_stack([np.cos(angles), np.full(24, 0.5), 0.5 * np.sin(angles)])
        edge[3, 0] = edge[1, 0]
        with self.assertRaises(ValueError):
            landmarks.eye_rim(edge)

    def test_the_middle_line_holds_the_mirror_planes_vertices_of_one_zones_skin(self):
        points = np.array([[0.0, 0, 0], [0.1, 0, 0], [0.0, 1, 0], [0.0, 2, 0], [0.1, 2, 0]])
        faces = [(0, 1, 2), (2, 3, 4), (0, 2, 3)]
        line = landmarks.middle_line(points, faces, zones=[0, 0, 1], parts=[0, 8, 0], zone=0)
        self.assertEqual(line.tolist(), [[0.0, 0.0, 0.0], [0.0, 1.0, 0.0]])

    def test_the_faces_points_are_the_extremes_of_the_middle_line_in_their_order(self):
        # A face's middle line in y (forward) by z, with its features where the rules look for them.
        profile = {1.700: 0.130, 1.685: 0.134, 1.675: 0.135, 1.668: 0.132, 1.660: 0.138, 1.640: 0.160, 1.628: 0.150,
                   1.618: 0.144, 1.612: 0.153, 1.606: 0.146, 1.600: 0.151, 1.590: 0.140, 1.575: 0.146, 1.565: 0.140,
                   1.558: 0.128, 1.552: 0.110, 1.715: 0.128}
        line = np.array([[0.0, y, z] for z, y in profile.items()] + [[0.0, 0.0, 1.62]])  # the back of the head
        found = landmarks.face_points(line, mouth=np.array([0.0, 0.146, 1.606]), eye=np.array([0.03, 0.12, 1.667]), scale=1.0)
        expected = {"nose_tip": 1.640, "sellion": 1.668, "upper_lip": 1.612, "subnasale": 1.618, "lower_lip": 1.600,
                    "chin_front": 1.575}
        for name, z in expected.items():
            self.assertAlmostEqual(float(found[name][2]), z, places=6, msg=name)
        self.assertAlmostEqual(float(found["chin_bottom"][2]), 1.558, places=6)
        self.assertAlmostEqual(float(found["forehead"][2]), 1.668 + landmarks.FOREHEAD_ABOVE_SELLION, places=9)
        self.assertEqual(float(found["forehead"][0]), 0.0)


def eye_net(centre=(0.03, 0.0, 0.0), radius=0.012, opening=30.0, skin_rings=3):
    """An eye's lids around the line of sight +Y: 24 spokes from the inner corner (toward -x) over the
    upper lid; the inner edge at opening degrees from the line of sight on a sphere a little larger
    than the eyeball, skin rings outward from it, and a pocket ring behind the edge, outside the
    eyeball. Returns the points, the faces, and their parts."""
    centre = np.asarray(centre, float)
    spokes = np.radians(180.0 - 15.0 * np.arange(24))
    rows = []
    for degrees, size in [(opening - 8.0, 1.06)] + [(opening + 6.0 * j, 1.03) for j in range(skin_rings + 1)]:
        theta = math.radians(degrees)
        rows.append([centre + radius * size * np.array([math.sin(theta) * math.cos(a), math.cos(theta), math.sin(theta) * math.sin(a)])
                     for a in spokes])
    points = np.array([p for row in rows for p in row])
    faces, parts = [], []
    for r in range(len(rows) - 1):
        for k in range(24):
            j = (k + 1) % 24
            faces.append((r * 24 + k, r * 24 + j, (r + 1) * 24 + j, (r + 1) * 24 + k))
            parts.append(lids.SOCKET_PART if r == 0 else 0)
    return points, faces, parts


class LidTests(unittest.TestCase):
    CLOSED = {"upper": -50.0, "lower": 12.5}
    FACTORS = {"upper": (1.0, 0.5, 0.25), "lower": (1.0, 0.4, 0.2)}

    def test_the_inner_edge_is_the_closed_line_where_the_lids_skin_meets_the_pocket(self):
        points, faces, parts = eye_net()
        cycle = lids.edge_cycle(points, faces, parts, np.array([0.03, 0.0, 0.0]))
        self.assertEqual(sorted(cycle), list(range(24, 48)))
        upper, lower = lids.lids(points, cycle)
        self.assertEqual((upper[0], upper[-1]), (24, 36))   # from the inner corner to the outer one
        self.assertEqual(upper, list(range(24, 37)))
        self.assertEqual(lower, [24] + list(range(47, 35, -1)))

    def test_each_edge_vertex_turns_to_where_the_lids_meet_and_the_corners_stay(self):
        points, faces, parts = eye_net()
        centre = np.array([0.03, 0.0, 0.0])
        found, largest = lids.weights(points, faces, parts, centre, 0.012, self.CLOSED, 0.2, self.FACTORS)
        # The lids are mirror images across the line of sight: the meeting line lies at 0.6 of the
        # upper lid's angle below it, so the upper lid needs 1.6 times its angle and the lower 0.4.
        self.assertAlmostEqual(largest["upper"], 1.6 * 30.0, places=6)
        self.assertAlmostEqual(largest["lower"], 0.4 * 30.0, places=6)
        self.assertAlmostEqual(found["upper"][24 + 6], lids.blended(math.radians(48.0), math.radians(50.0)), places=9)
        self.assertAlmostEqual(found["lower"][24 + 18], lids.blended(math.radians(12.0), math.radians(12.5)), places=9)
        for corner in (24, 36):
            self.assertEqual(found["upper"].get(corner, 0.0), 0.0)
            self.assertNotIn(corner, found["lower"])

        # Blended as the skin is between rest and turned by its closed angle, each edge vertex of a
        # lid reaches the line where the lids meet, at its own place across the eye.
        def closed(v, degrees, weight):
            d = points[v] - centre
            a = math.radians(degrees)
            turned = np.array([d[0], d[1] * math.cos(a) - d[2] * math.sin(a), d[1] * math.sin(a) + d[2] * math.cos(a)])
            p = (1.0 - weight) * d + weight * turned
            return p[0], math.atan2(p[2], p[1])

        for k in range(1, 12):
            a, b = 24 + k, 24 + (24 - k) % 24
            up_across, up = closed(a, -50.0, found["upper"][a])
            down_across, down = closed(b, 12.5, found["lower"][b])
            self.assertAlmostEqual(up_across, down_across, places=12)
            self.assertAlmostEqual(up, down, places=9)

    def test_the_lids_meet_at_each_place_across_the_eye_also_where_their_vertices_lie_apart(self):
        # The lower lid's vertices lie further out than the upper's: each upper vertex still meets
        # the lower lid's line between two of its vertices.
        points, faces, parts = eye_net()
        centre = np.array([0.03, 0.0, 0.0])
        lower = list(range(37, 48))
        points[lower, 0] = centre[0] + (points[lower, 0] - centre[0]) * 0.95
        upper_chain, lower_chain = lids.lids(points, lids.edge_cycle(points, faces, parts, centre))
        up, down = lids.needs(points, upper_chain, lower_chain, centre, 0.2)
        across, angles = lids.profile(points, lower_chain, centre)
        for v, turn in up.items():
            bottom = np.interp(points[v][0], across, angles)
            self.assertAlmostEqual(lids.angle(points, v, centre) - turn, bottom + 0.2 * (lids.angle(points, v, centre) - bottom), places=12)

    def test_the_blended_weight_is_none_for_no_turn_and_all_for_the_closed_angle(self):
        closed = math.radians(44.0)
        self.assertEqual(lids.blended(0.0, closed), 0.0)
        self.assertAlmostEqual(lids.blended(closed, closed), 1.0, places=12)
        # Halfway along the straight line lies the middle of the turn.
        self.assertAlmostEqual(lids.blended(closed / 2, closed), 0.5, places=12)

    def test_the_skin_rings_take_their_nearest_edge_vertexs_weight_times_their_share(self):
        points, faces, parts = eye_net()
        found, _largest = lids.weights(points, faces, parts, np.array([0.03, 0.0, 0.0]), 0.012, self.CLOSED, 0.2, self.FACTORS)
        edge = found["upper"][24 + 6]
        for ring, share in enumerate(self.FACTORS["upper"], start=1):
            self.assertAlmostEqual(found["upper"][24 * (ring + 1) + 6], edge * share, places=9)
        # The pocket behind the edge follows the edge.
        self.assertAlmostEqual(found["upper"][6], edge, places=9)

    def test_lids_that_need_more_than_their_closed_angle_are_refused(self):
        points, faces, parts = eye_net()
        with self.assertRaisesRegex(ValueError, "upper lid needs 48.0 degrees"):
            lids.weights(points, faces, parts, np.array([0.03, 0.0, 0.0]), 0.012, {"upper": -40.0, "lower": 12.5}, 0.2,
                         self.FACTORS)

    def test_an_edge_that_is_not_one_closed_line_is_refused(self):
        points, faces, parts = eye_net()
        # Half of the pocket is skin: the edge ends where the pocket does.
        parts = [0 if 12 <= index < 24 else part for index, part in enumerate(parts)]
        with self.assertRaises(ValueError):
            lids.edge_cycle(points, faces, parts, np.array([0.03, 0.0, 0.0]))


class ArmTurnTests(unittest.TestCase):
    def test_an_arm_of_another_bind_pose_turns_to_the_one_the_poses_were_made_for(self):
        drop = math.radians(45.0)
        tail = (math.cos(drop), 0.0, -math.sin(drop))
        self.assertAlmostEqual(poses.arm_turn((0.0, 0.0, 0.0), tail), poses.REST_ARM_DROP - 45.0, places=9)

    def test_an_arm_near_that_angle_keeps_its_own(self):
        for degrees in (poses.REST_ARM_DROP, poses.REST_ARM_DROP + poses.ARM_TOLERANCE * 0.9):
            drop = math.radians(degrees)
            self.assertEqual(poses.arm_turn((0.0, 0.0, 1.0), (math.cos(drop), 0.0, 1.0 - math.sin(drop))), 0.0)

    def test_closed_eyelids_turn_each_lid_bone_by_its_share_of_the_closed_angle(self):
        class Lids:
            eyes = (("Eye_L", "UpL", "DownL"), ("Eye_R", "UpR", "DownR"))
            upper_closed, lower_closed = -44.0, 11.0

        self.assertEqual(poses.eyelids(Lids, 0.5), {"UpL": (("X", -22.0),), "DownL": (("X", 5.5),), "UpR": (("X", -22.0),),
                                                    "DownR": (("X", 5.5),)})


if __name__ == "__main__":
    unittest.main()
