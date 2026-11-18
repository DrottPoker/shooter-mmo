"""The features of a reference head that the head cage's loops are laid onto: the rim of each eye's
opening, the line where the lips meet, the nose, the chin, and the ears.

The eye's rim is found from the eyeball's own centre: a ray that leaves the head through the opening
meets no skin, one a little farther out meets the lid, and the rim lies between them. The other
features are read from the profile of the head's centre line and from sections beside it."""

import math

import numpy as np

from .surface import Surface

X, Y, Z = np.eye(3)
# A lid lies within this many radii of the eyeball's centre.
RIM_REACH = 1.5


def _unit(v):
    v = np.asarray(v, float)
    return v / max(np.linalg.norm(v), 1e-12)


def _profile(surface: Surface, x):
    """The front profile of the head in the plane at x: points ordered from the top down, as
    (forward, height) pairs of the frontmost point per height."""
    loops = surface.section((x, 0, 0), X)
    points = np.concatenate([loop.points for loop in loops]) if loops else np.zeros((0, 3))
    return points


class Face:
    def __init__(self, surface: Surface, eye_centres, eye_radius, scale):
        self.surface = surface
        self.scale = scale
        s = scale
        self.eye_centre = np.asarray(eye_centres["R"], float)
        self.eye_radius = float(eye_radius)
        top = float(surface.points[:, 2].max())
        self.top = top
        # The centre line's front, from the forehead down to under the chin.
        mid = max(surface.section((0, 0, 0), X), key=lambda l: l.length).points
        mid = mid[mid[:, 2] > top - 0.30 * s]
        self.mid = mid
        eye_z = self.eye_centre[2]

        def front_at(z):
            near = mid[np.abs(mid[:, 2] - z) < 0.0015]
            near = near[near[:, 1] > self.eye_centre[1] - 0.06 * s]
            return None if not len(near) else near[int(np.argmax(near[:, 1]))]

        heights = np.arange(eye_z + 0.03 * s, eye_z - 0.16 * s, -0.001)
        line = [front_at(z) for z in heights]
        line = np.array([p for p in line if p is not None])
        self.line = line
        z, y = line[:, 2], line[:, 1]

        def extreme(low, high, forward=True):
            pool = np.where((z >= low) & (z <= high))[0]
            return line[pool[int(np.argmax(y[pool]) if forward else np.argmin(y[pool]))]]

        # The nose: its tip, the dip above it between the eyes, and the corner under it.
        self.nose_tip = extreme(eye_z - 0.07 * s, eye_z - 0.02 * s)
        self.nasion = extreme(eye_z - 0.012 * s, eye_z + 0.02 * s, forward=False)
        self.subnasale = extreme(self.nose_tip[2] - 0.03 * s, self.nose_tip[2] - 0.004 * s, forward=False)
        # The lips: the upper lip's front, the line where the lips meet, and the lower lip's front.
        below = self.subnasale[2]
        self.upper_lip = extreme(below - 0.028 * s, below - 0.008 * s)
        self.lower_lip = extreme(self.upper_lip[2] - 0.026 * s, self.upper_lip[2] - 0.010 * s)
        self.lip_line = extreme(self.lower_lip[2], self.upper_lip[2], forward=False)
        # The chin: the dip under the lower lip, the chin's front, and its lowest point.
        self.chin_dip = extreme(self.lower_lip[2] - 0.022 * s, self.lower_lip[2] - 0.004 * s, forward=False)
        self.chin = extreme(self.chin_dip[2] - 0.035 * s, self.chin_dip[2] - 0.004 * s)
        # The chin's underside: the lowest point just behind the chin's front, before the jaw's
        # underside runs back to the neck.
        under = mid[(mid[:, 1] > self.chin[1] - 0.022 * s) & (mid[:, 2] < self.chin[2])]
        self.chin_bottom = under[int(np.argmin(under[:, 2]))]
        self.brow = extreme(eye_z + 0.012 * s, eye_z + 0.03 * s)

    # ------------------------------------------------------------------ the eye

    def _escapes(self, direction):
        """Whether a ray from the eyeball's centre leaves through the opening: it meets no lid
        within one and a half radii of the ball."""
        return self.surface.ray(self.eye_centre, direction, RIM_REACH * self.eye_radius) is None

    def eye_rim(self, count=48):
        """The rim of the right eye's opening as points on the lids, in order around the eye's
        forward axis, starting at the corner beside the nose and running over the upper lid."""
        axis = Y
        side = _unit(np.cross(axis, Z))        # toward the character's left: the nose is at -x for the right eye
        up = np.cross(side, axis)
        rim = []
        for k in range(count):
            angle = 2 * math.pi * k / count
            # Start toward the nose (-x for the right eye) and turn over the top.
            across = -X * math.cos(angle) + Z * math.sin(angle)
            across = _unit(across - (across @ axis) * axis)
            # Seen from the eyeball's centre the opening is wide, since the lids lie on the ball, and
            # at the temple's side it may reach past a right angle from the eye's axis.
            def toward(turn):
                return axis * math.cos(turn) + across * math.sin(turn)

            low, high = 0.0, 2.4
            for _ in range(24):
                middle = (low + high) / 2
                if self._escapes(toward(middle)):
                    low = middle
                else:
                    high = middle
            hit = None
            while hit is None and high < 2.6:
                hit = self.surface.ray(self.eye_centre, toward(high), RIM_REACH * self.eye_radius)
                high += 0.01
            if hit is None:
                raise RuntimeError("the eye's opening has no rim at one side")
            rim.append(hit[0])
        return np.array(rim), axis

    # ------------------------------------------------------------------ the mouth

    def lip_line_at(self, x):
        """The point of the line where the lips meet at a distance from the centre, or None beyond
        the mouth's corner: the deepest point of the profile between the two lips."""
        points = _profile(self.surface, x)
        low, high = self.lower_lip[2] - 0.004 * self.scale, self.upper_lip[2] + 0.004 * self.scale
        band = points[(points[:, 2] > low) & (points[:, 2] < high) & (points[:, 1] > self.lip_line[1] - 0.04 * self.scale)]
        if len(band) < 8:
            return None
        order = np.argsort(-band[:, 2])
        band = band[order]
        # The frontmost point per height, then the dip between the two fronts.
        heights = np.arange(high, low, -0.0005)
        front = []
        for z in heights:
            near = band[np.abs(band[:, 2] - z) < 0.0006]
            front.append(near[int(np.argmax(near[:, 1]))] if len(near) else None)
        front = [p for p in front if p is not None]
        if len(front) < 8:
            return None
        front = np.array(front)
        y = front[:, 1]
        i_top = int(np.argmax(y[:len(y) // 2]))
        i_bottom = len(y) // 2 + int(np.argmax(y[len(y) // 2:]))
        if i_bottom - i_top < 3:
            return None
        between = front[i_top:i_bottom + 1]
        deepest = float(between[:, 1].min())
        depth = min(y[i_top], y[i_bottom]) - deepest
        # The fold between pressed lips has a flat bottom: the line lies at its middle height.
        bottom = between[between[:, 1] < deepest + 0.0005]
        point = np.array([x, deepest, (bottom[:, 2].max() + bottom[:, 2].min()) / 2])
        return point, float(depth)

    def mouth_corner(self):
        """Half the mouth's width: how far from the centre the line between the lips can be followed."""
        s = self.scale
        last = 0.0
        for x in np.arange(0.004 * s, 0.045 * s, 0.002 * s):
            found = self.lip_line_at(x)
            if found is None or found[1] < 0.0004 * s:
                break
            last = x
        return last

    # ------------------------------------------------------------------ the ear

    def ear_box(self):
        """The box around the part of the right ear that stands out from the head: its lowest and
        highest corner."""
        s = self.scale
        points = self.surface.points[np.unique(self.surface.triangles)]
        eye_z = self.eye_centre[2]
        head = points[(points[:, 2] > eye_z - 0.07 * s) & (points[:, 2] < eye_z + 0.06 * s) & (points[:, 0] > 0)]
        outer = head[int(np.argmax(head[:, 0]))]
        ear = head[(head[:, 0] > outer[0] - 0.016 * s) & (np.abs(head[:, 1] - outer[1]) < 0.04 * s)
                   & (np.abs(head[:, 2] - outer[2]) < 0.045 * s)]
        return ear.min(0), ear.max(0)

    def ear(self):
        """The right ear's outermost, top, bottom, and rearmost points."""
        s = self.scale
        points = self.surface.points[np.unique(self.surface.triangles)]
        eye_z = self.eye_centre[2]
        head = points[(points[:, 2] > eye_z - 0.07 * s) & (points[:, 2] < eye_z + 0.06 * s) & (points[:, 0] > 0)]
        # The head's side without the ear: the widest point in front of the ear's place.
        outer = head[int(np.argmax(head[:, 0]))]
        ear = head[(head[:, 0] > outer[0] - 0.014 * s) & (np.abs(head[:, 1] - outer[1]) < 0.05 * s)]
        ear = ear[np.abs(ear[:, 2] - outer[2]) < 0.045 * s]
        return {"outer": outer, "top": ear[int(np.argmax(ear[:, 2]))], "bottom": ear[int(np.argmin(ear[:, 2]))],
                "rear": ear[int(np.argmin(ear[:, 1]))]}

    def report(self):
        names = ("brow", "nasion", "nose_tip", "subnasale", "upper_lip", "lip_line", "lower_lip", "chin_dip", "chin", "chin_bottom")
        return [f"{name}: forward {getattr(self, name)[1]:.4f}, height {getattr(self, name)[2]:.4f}" for name in names]
