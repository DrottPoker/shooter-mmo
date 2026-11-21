"""The hand pose Grip (human_base/grip.py): its geometry, and its fit on a small hand whose answers
follow from how it is built. It needs numpy; without numpy this file is skipped."""

import math
import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from blender_source import BLENDER_ROOT  # noqa: E402

try:
    import numpy as np
except ImportError:
    raise unittest.SkipTest("numpy is not installed; see requirements.txt") from None

sys.path.insert(0, str(BLENDER_ROOT))

from human_base import grip  # noqa: E402

AXES = {"X": (1.0, 0.0, 0.0), "Y": (0.0, 1.0, 0.0), "Z": (0.0, 0.0, 1.0)}


def turn(turns):
    """A bone's turns as its rotation, each about its own axis after the one before."""
    rotation = np.eye(3)
    for axis, degrees in turns:
        x, y, z = AXES[axis]
        c, s = math.cos(math.radians(degrees)), math.sin(math.radians(degrees))
        k = np.array([[0.0, -z, y], [z, 0.0, -x], [-y, x, 0.0]])
        rotation = rotation @ (np.eye(3) + s * k + (1.0 - c) * (k @ k))
    return rotation


def small_hand(spread=0.0, thickness=0.008, handle_below=0.028):
    """A right hand of four fingers and a thumb. Every finger runs along +Y from its knuckle with
    its pad toward -Z and closes about its X axis; the knuckles lie 20 mm apart along X, and the
    handle runs along X handle_below under them. spread turns the outer fingers that many degrees
    apart at rest. Each segment's skin is rings of points around it."""
    names, rests, parents, points, weights = [], {}, {}, [], []
    lengths = (0.040, 0.025, 0.020)

    def bone(name, head, direction, parent):
        y = np.asarray(direction, float) / np.linalg.norm(direction)
        z = np.array([0.0, 0.0, 1.0])
        x = np.cross(y, z)
        rest = np.eye(4)
        rest[:3, 0], rest[:3, 1], rest[:3, 2], rest[:3, 3] = x, y, z, head
        names.append(name)
        rests[name], parents[name] = rest, parent
        return rest

    def skin(name, rest, length):
        for along in np.linspace(0.1, 0.9, 9) * length:
            for angle in np.linspace(0.0, 2.0 * math.pi, 12, endpoint=False):
                offset = rest[:3, 0] * math.cos(angle) * thickness + rest[:3, 2] * math.sin(angle) * thickness
                points.append(rest[:3, 3] + rest[:3, 1] * along + offset)
                weights.append(name)

    rests["Hand_R"] = np.eye(4)
    for index, finger in enumerate(grip.FINGERS):
        knuckle = np.array([0.030 - 0.020 * index, 0.0, 0.0])
        angle = math.radians(spread * (1.5 - index) / 1.5)
        direction = np.array([math.sin(angle), math.cos(angle), 0.0])
        head, parent = knuckle, "Hand_R"
        for segment, length in zip(grip.SEGMENTS, lengths):
            rest = bone(f"{finger}{segment}_R", head, direction, parent)
            skin(f"{finger}{segment}_R", rest, length)
            head, parent = head + direction * length, f"{finger}{segment}_R"
    # The thumb starts beside the index finger's knuckle and points along the fingers.
    head, parent = np.array([0.055, -0.030, -0.010]), "Hand_R"
    for segment, length in zip(grip.SEGMENTS, (0.035, 0.028, 0.022)):
        rest = bone(f"Thumb{segment}_R", head, (0.0, 1.0, 0.0), parent)
        skin(f"Thumb{segment}_R", rest, length)
        head, parent = head + np.array([0.0, length, 0.0]), f"Thumb{segment}_R"
    table = np.zeros((len(points), len(names)))
    for row, name in enumerate(weights):
        table[row, names.index(name)] = 1.0
    return grip.Hand(points, table, names, rests, parents, (0.0, 0.0, -handle_below), (1.0, 0.0, 0.0), turn)


class GapTests(unittest.TestCase):
    def test_a_point_is_as_far_outside_a_cylinder_as_it_lies_from_its_surface(self):
        points = [(0.0, 0.03, 0.0), (5.0, 0.0, 0.01), (-2.0, 0.0, 0.0)]
        np.testing.assert_allclose(grip.cylinder_gap(points, (0, 0, 0), (2, 0, 0), 0.02), [0.01, -0.01, -0.02])

    def test_a_capsule_ends_in_half_balls(self):
        points = [(0.0, 0.03, 0.0), (0.05, 0.0, 0.0), (0.045, 0.0, 0.0), (-0.03, 0.04, 0.0)]
        np.testing.assert_allclose(grip.capsule_gap(points, (0, 0, 0), (0.04, 0, 0), 0.01), [0.02, 0.0, -0.005, 0.04], atol=1e-12)


class SearchTests(unittest.TestCase):
    def test_a_joint_closes_to_where_its_pad_first_touches(self):
        self.assertAlmostEqual(grip.first_contact(lambda v: 30.0 - v, 0.0, 90.0), 30.0, places=3)
        # It stops at the first touch, also when the pad would come off again further on.
        self.assertAlmostEqual(grip.first_contact(lambda v: abs(v - 50.0) - 10.0, 0.0, 90.0), 40.0, places=3)

    def test_a_pad_that_touches_at_rest_keeps_its_joint_open(self):
        self.assertEqual(grip.first_contact(lambda v: -1.0, 0.0, 90.0), 0.0)

    def test_a_pad_that_never_touches_comes_as_near_as_it_can(self):
        self.assertAlmostEqual(grip.first_contact(lambda v: 2.0 + (v - 60.0) ** 2, 0.0, 90.0), 60.0, delta=0.5)

    def test_the_lowest_cost_is_found_on_a_narrowing_grid(self):
        found = grip.lowest(lambda a, b: (a - 12.3) ** 2 + (b + 4.5) ** 2, ((0.0, 90.0), (-20.0, 20.0)))
        self.assertAlmostEqual(found[0], 12.3, delta=0.05)
        self.assertAlmostEqual(found[1], -4.5, delta=0.05)

    def test_the_thumbs_cost_grows_far_faster_inside_than_off(self):
        self.assertAlmostEqual(grip.thumb_cost([(0.0, 0.002), (0.001, 0.001)]), 0.002 ** 2 + 0.001 ** 2)
        self.assertGreater(grip.thumb_cost([(-0.001, 0.0)]), 100.0 * grip.thumb_cost([(0.0, 0.001)]))


class HandTests(unittest.TestCase):
    def test_a_finger_that_crosses_the_handle_at_right_angles_needs_no_turn(self):
        across = small_hand().across()
        for finger in grip.FINGERS:
            np.testing.assert_allclose(across[finger], (0.0, 0.0), atol=1e-9)

    def test_a_spread_finger_turns_back_to_close_across_the_handle(self):
        across = small_hand(spread=15.0).across()
        # The index finger is spread 15 degrees toward +X at rest: it turns as far back about Z.
        self.assertAlmostEqual(across["Index"][0], 15.0, places=6)
        self.assertAlmostEqual(across["Little"][0], -15.0, places=6)
        self.assertAlmostEqual(across["Index"][1], 0.0, places=6)

    def test_a_posed_finger_carries_its_skin_and_the_segments_beyond_it(self):
        hand = small_hand()
        rest = hand.skin(hand.poses({}))
        np.testing.assert_allclose(rest, hand.points, atol=1e-12)
        closed = hand.skin(hand.poses({"IndexProximal": (("X", -90.0),)}))
        moved = np.linalg.norm(closed - hand.points, axis=1) > 1e-9
        # Closing the knuckle moves the whole index finger and nothing else.
        index = np.array([name.startswith("Index") for name in np.array(hand.names)[np.argmax(hand.weights, axis=1)]])
        self.assertTrue((moved == index).all())
        # A finger along +Y closed 90 degrees toward its pad points along -Z.
        self.assertLess(closed[index][:, 2].min(), -0.07)

    def test_a_pose_may_give_a_bones_rotation_itself(self):
        hand = small_hand()
        by_turns = hand.pads({"MiddleProximal": (("X", -40.0),)})
        by_matrix = hand.pads({"MiddleProximal": turn((("X", -40.0),))})
        self.assertEqual(by_turns, by_matrix)


class FitTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.hand = small_hand(spread=12.0)
        cls.angles, cls.across = grip.fit(cls.hand)
        cls.measured = grip.measure(cls.hand, grip.finger_turns(cls.angles, cls.across))

    def test_every_finger_closes_until_its_pads_touch_the_handle(self):
        for finger in grip.FINGERS:
            for nearest, _mean in self.measured[finger]:
                self.assertAlmostEqual(nearest, grip.TOUCH, delta=0.0003)
            knuckle, middle, last = self.angles[finger]
            self.assertTrue(20.0 < knuckle < grip.CLOSED[0], knuckle)
            self.assertTrue(20.0 < middle < grip.CLOSED[1], middle)
            self.assertTrue(0.0 <= last < grip.CLOSED[2], last)

    def test_the_fingers_lie_against_each_other(self):
        for finger, _neighbour in grip.NEIGHBOURS:
            self.assertAlmostEqual(self.measured["between"][finger], 0.0, delta=0.0005)

    def test_the_thumb_lies_on_the_handle_or_the_fingers_and_not_in_them(self):
        for nearest, _mean in self.measured["Thumb"]:
            self.assertGreater(nearest, -grip.THUMB_INSIDE)
            self.assertLess(nearest, 0.003)

    def test_a_fitted_grip_has_no_problems_and_a_line_per_digit(self):
        self.assertEqual(grip.problems(self.measured), [])
        lines = grip.report(self.measured, self.angles, self.across)
        self.assertEqual(len(lines), 5)
        self.assertTrue(lines[0].startswith("Grip Index: to the side "))
        self.assertIn("mm from its neighbour", lines[0])
        self.assertTrue(lines[4].startswith("Grip Thumb: base "))
        self.assertEqual(len(grip.report(self.measured)), 5)

    def test_an_open_hand_is_no_grip(self):
        opened = {finger: (0.0, 0.0, 0.0) for finger in grip.FINGERS}
        opened["Thumb"] = (0.0, 0.0, 0.0)
        measured = grip.measure(self.hand, grip.finger_turns(opened, self.hand.across()))
        faults = grip.problems(measured)
        self.assertTrue(any("off the handle" in fault for fault in faults), faults)

    def test_a_finger_through_the_handle_is_named(self):
        measured = {finger: [(grip.TOUCH, 0.002)] * 3 for finger in grip.FINGERS}
        measured["Ring"] = [(-0.004, 0.0)] * 3
        measured["Thumb"] = [(0.0, 0.001)] * 2
        measured["between"] = {"Index": 0.0, "Ring": 0.006, "Little": -0.004}
        self.assertEqual(grip.problems(measured), ["Grip: the ring finger lies 4.0 mm inside the handle",
                                                   "Grip: the ring finger lies 6.0 mm from its neighbour",
                                                   "Grip: the little finger lies 4.0 mm inside its neighbour"])


class ClipTests(unittest.TestCase):
    def test_the_pose_keys_the_three_bones_of_five_digits_of_both_hands(self):
        keyed = grip.keyed_bones()
        self.assertEqual(len(keyed), 30)
        self.assertIn("ThumbDistal_L", keyed)
        self.assertIn("LittleProximal_R", keyed)

    def test_a_fingers_turns_close_it_after_it_turned_to_the_side(self):
        angles = {finger: (40.0, 80.0, 20.0) for finger in grip.FINGERS}
        angles["Thumb"] = (30.0, 10.0, 40.0)
        across = {finger: (5.0, -3.0) for finger in grip.FINGERS}
        turns = grip.finger_turns(angles, across)
        self.assertEqual(turns["RingProximal"], (("Z", 5.0), ("Y", -3.0), ("X", -40.0)))
        self.assertEqual(turns["RingIntermediate"], (("X", -80.0),))
        self.assertEqual(turns["RingDistal"], (("X", -20.0),))
        self.assertEqual(turns["ThumbProximal"], (("X", -30.0), ("Z", 10.0)))
        self.assertEqual(turns["ThumbDistal"], (("X", -40.0 * grip.THUMB_LAST),))


if __name__ == "__main__":
    unittest.main()
