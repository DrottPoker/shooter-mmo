"""Fingers, thumbs, and toes of a reference surface, traced from their tips to their webs: sections
square to a digit follow it inward until it joins its neighbour or the palm, which gives its line of
centres, its free length, and a ring of it at any share of that length."""

import numpy as np

from .surface import Surface


def _unit(v):
    v = np.asarray(v, float)
    return v / max(np.linalg.norm(v), 1e-12)


class Digit:
    def __init__(self, surface: Surface, tip, centres, lengths):
        self.surface = surface
        self.tip = np.asarray(tip, float)
        self.path = np.array([self.tip] + [np.asarray(c, float) for c in centres])
        self.girths = np.array(lengths, float)
        steps = np.linalg.norm(np.diff(self.path, axis=0), axis=1)
        self.steps = np.concatenate([[0.0], np.cumsum(steps)])
        self.length = float(self.steps[-1])

    def point(self, share):
        """The point of the digit's line at a share of its free length from the tip (0) to the web (1)."""
        target = float(np.clip(share, 0.0, 1.0)) * self.length
        i = int(np.clip(np.searchsorted(self.steps, target, side="right") - 1, 0, len(self.path) - 2))
        t = (target - self.steps[i]) / max(self.steps[i + 1] - self.steps[i], 1e-12)
        return self.path[i] + t * (self.path[i + 1] - self.path[i])

    def direction(self, share, window=0.12):
        """The direction toward the tip at a share of the free length."""
        return _unit(self.point(max(share - window, 0.0)) - self.point(min(share + window, 1.0)))

    def loop(self, share):
        """The digit's own section square to it at a share of its free length."""
        origin, normal = self.point(share), self.direction(share)
        return self.surface.loop(origin, normal, near=origin).oriented(normal), origin, normal

    def girth_peak(self, low, high):
        """The share between two shares at which the digit is thickest, such as at a knuckle."""
        shares = np.linspace(low, high, 15)
        girths = [self.loop(share)[0].length for share in shares]
        return float(shares[int(np.argmax(girths))])


def farthest(surface: Surface, near, direction, radius):
    """The point of the surface farthest along a direction within a radius of the line through a point."""
    direction = _unit(direction)
    points = surface.points[np.unique(surface.triangles)]
    rel = points - np.asarray(near, float)
    along = rel @ direction
    across = np.linalg.norm(rel - along[:, None] * direction, axis=1)
    pool = np.where(across < radius)[0]
    return points[pool[int(np.argmax(along[pool]))]]


def march(surface: Surface, tip, direction, step=0.003, reach=0.14, dome=0.012, jump=1.4):
    """Follows a digit from its tip inward: sections square to it, each a step further in, until
    the section takes in a neighbour or the palm, so its girth jumps."""
    direction = _unit(direction)
    tip = np.asarray(tip, float)
    centres, girths = [], []
    origin = tip - direction * step
    travelled = step
    widest = 0.0
    while travelled < reach:
        loop = surface.loop(origin, direction, near=origin)
        if loop is None or np.linalg.norm(loop.centre - origin) > 0.012:
            break
        if travelled > dome and loop.length > jump * widest:
            break
        centres.append(loop.centre)
        girths.append(loop.length)
        widest = max(widest, loop.length)
        if len(centres) >= 5:
            direction = _unit(0.5 * direction + 0.5 * _unit(centres[-5] - centres[-1]))
        origin = centres[-1] - direction * step
        travelled += step
    return Digit(surface, tip, centres, girths)


def tips(surface: Surface, axis, origin, step=0.004, widest=0.09, fewest=4):
    """The digits that hang along an axis: sections square to the axis from the farthest point
    inward, each loop followed from where it first shows until it joins another. Returns for each
    digit the centre of its first section and the direction toward its tip."""
    axis = _unit(axis)
    points = surface.points[np.unique(surface.triangles)]
    heights = (points - origin) @ axis
    tracks = []
    for height in np.arange(heights.max() - step / 2, heights.max() - 0.16, -step):
        loops = [l for l in surface.section(np.asarray(origin, float) + axis * height, axis) if l.length > 0.012]
        used = set()
        for track in tracks:
            if not track["alive"]:
                continue
            last = track["loops"][-1]
            candidates = [(np.linalg.norm(l.centre - last.centre), i) for i, l in enumerate(loops) if i not in used]
            if not candidates:
                track["alive"] = False
                continue
            distance, i = min(candidates)
            if distance > 0.012 or loops[i].length > 1.45 * max(last.length, 0.04):
                track["alive"] = False
            else:
                track["loops"].append(loops[i])
                used.add(i)
        for i, loop in enumerate(loops):
            if i not in used and loop.length < widest:
                near_old = any(np.linalg.norm(loop.centre - t["loops"][-1].centre) < 0.012 for t in tracks)
                if not near_old:
                    tracks.append({"loops": [loop], "alive": True, "height": height})
    found = []
    for track in tracks:
        loops = track["loops"]
        if len(loops) >= fewest:
            found.append({"first": loops[0].centre, "last": loops[-1].centre, "height": track["height"],
                          "direction": _unit(loops[0].centre - loops[-1].centre), "count": len(loops)})
    return found
