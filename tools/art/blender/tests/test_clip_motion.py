"""The motion numbers of a clip, as far as they need numpy but not Blender: from the world positions of
its bones on every frame, how long it is, how far the root travels and sinks, the fastest bone, the
share of the bones that move, the lowest point, and how far a loop jumps. Skipped without numpy, like
test_numeric_logic.py. See blender_source.py."""

import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from blender_source import load  # noqa: E402

try:
    import numpy as np
except ImportError:
    raise unittest.SkipTest("numpy is not installed; see requirements.txt") from None

clip_video = load("art_pipeline/clip_video.py", "motion_numbers", "MOVING_TURN", "SHORT_BONE", "FPS", "WIDTH", "HEIGHT", "PLAYS",
                  numpy=True)
NAMES = ["Hips", "Hand_R", "Foot_R"]


def still(frames: int) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """Three bones standing still over the frames: heads, tails one decimeter above, and the root."""
    heads = np.tile(np.array([[0.0, 0.0, 1.0], [0.3, 0.0, 1.0], [0.1, 0.0, 0.1]]), (frames, 1, 1))
    tails = heads + (0.0, 0.0, 0.1)
    return heads, tails, heads[:, 0].copy()


LENGTHS = np.array([0.15, 0.1, 0.16])


def numbers(heads, tails, root, loop=False, fps=30, lengths=LENGTHS, turns=None):
    turns = np.zeros(heads.shape[:2]) if turns is None else turns
    return clip_video.motion_numbers(heads, tails, root, 1.0, fps, loop, NAMES, lengths, turns)


class MotionNumberTests(unittest.TestCase):
    def test_a_still_clip_moves_nothing(self):
        result = numbers(*still(31))
        self.assertEqual(result, {"frames": 30, "seconds": 1.0, "rootTravel": 0.0, "rootLowest": 1.0, "fastestBone": None,
                                  "fastestSpeed": 0.0, "movingShare": 0.0, "lowestPoint": 0.1, "loopGap": 0.0})

    def test_the_length_counts_the_frames_between_the_first_and_the_last(self):
        self.assertEqual((numbers(*still(46))["frames"], numbers(*still(46))["seconds"]), (45, 1.5))
        self.assertEqual(numbers(*still(46), fps=15)["seconds"], 3.0)

    def test_the_fastest_bone_is_the_one_whose_head_moves_furthest_between_two_frames(self):
        heads, tails, root = still(4)
        heads[2, 1] += (0.1, 0.0, 0.0)   # the hand jumps 10 cm for a frame
        heads[1:, 2] += (0.0, 0.05, 0.0)  # the foot moves 5 cm once
        result = numbers(heads, tails, root)
        self.assertEqual(result["fastestBone"], "Hand_R")
        self.assertAlmostEqual(result["fastestSpeed"], 3.0)

    def test_the_root_travel_is_its_horizontal_extent_and_its_lowest_point_a_share_of_its_rest_height(self):
        heads, tails, root = still(3)
        root[:] = [(0.0, 0.0, 1.0), (0.3, 0.4, 0.8), (0.0, 0.4, 2.0)]
        result = clip_video.motion_numbers(heads, tails, root, 0.9, 30, False, NAMES, LENGTHS, np.zeros(heads.shape[:2]))
        self.assertEqual((result["rootTravel"], result["rootLowest"]), (0.5, 0.89))

    def test_a_short_bone_such_as_a_finger_is_never_the_fastest(self):
        heads, tails, root = still(4)
        heads[2, 1] += (0.1, 0.0, 0.0)    # the hand jumps 10 cm for a frame
        heads[1:, 2] += (0.0, 0.05, 0.0)  # the foot moves 5 cm once
        lengths = np.array([0.15, clip_video.SHORT_BONE * 0.9, 0.16])
        result = numbers(heads, tails, root, lengths=lengths)
        self.assertEqual(result["fastestBone"], "Foot_R")
        self.assertAlmostEqual(result["fastestSpeed"], 1.5)
        only_short = numbers(heads, tails, root, lengths=np.full(3, clip_video.SHORT_BONE * 0.9))
        self.assertEqual((only_short["fastestBone"], only_short["fastestSpeed"]), (None, 0.0))

    def test_a_bone_moves_when_it_turns_in_its_joint_by_more_than_the_limit(self):
        heads, tails, root = still(3)
        turns = np.zeros((3, 3))
        turns[1, 1] = clip_video.MOVING_TURN * 1.5
        turns[2, 2] = clip_video.MOVING_TURN * 0.5
        self.assertEqual(numbers(heads, tails, root, turns=turns)["movingShare"], 0.33)

    def test_a_body_carried_along_by_its_root_does_not_move_in_its_joints(self):
        heads, tails, root = still(3)
        heads[1:] += (0.0, 0.5, 0.0)
        tails[1:] += (0.0, 0.5, 0.0)
        self.assertEqual(numbers(heads, tails, root)["movingShare"], 0.0)

    def test_the_lowest_point_is_the_lowest_head_or_tail_and_may_be_under_the_ground(self):
        heads, tails, root = still(2)
        tails[1, 2] = (0.1, 0.0, -0.012)
        self.assertEqual(numbers(heads, tails, root)["lowestPoint"], -0.012)

    def test_only_a_loop_has_a_gap_between_its_last_and_its_first_frame(self):
        heads, tails, root = still(5)
        heads[-1, 1] += (0.0, 0.03, 0.04)
        self.assertEqual(numbers(heads, tails, root, loop=True)["loopGap"], 0.05)
        self.assertEqual(numbers(heads, tails, root, loop=False)["loopGap"], 0.0)

    def test_a_clip_of_one_frame_has_no_speed(self):
        result = numbers(*still(1))
        self.assertEqual((result["frames"], result["fastestBone"], result["fastestSpeed"]), (0, None, 0.0))

    def test_the_video_is_960_by_540_at_30_frames_per_second_played_three_times(self):
        self.assertEqual((clip_video.WIDTH, clip_video.HEIGHT, clip_video.FPS, clip_video.PLAYS), (960, 540, 30, 3))


if __name__ == "__main__":
    unittest.main()
