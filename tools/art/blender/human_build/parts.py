"""The parts of a human head that are meshes of their own: the eyeballs, which turn on the eye
bones, the upper teeth, which stay with the head, and the lower teeth and the tongue, which follow
the jaw. Each is returned as points and faces, its faces looking outward."""

import math

import numpy as np

X, Y, Z = np.eye(3)


def eyeball(centre, radius, segments=16, rings=9):
    """A ball whose poles lie on the line of sight, so its rings are circles around the pupil: the
    iris is a disc on its front pole. Returns points, faces, and a UV per point seen from in front."""
    centre = np.asarray(centre, float)
    points, uvs = [centre + Y * radius], [(0.5, 0.5)]
    for ring in range(1, rings):
        polar = math.pi * ring / rings
        for segment in range(segments):
            turn = 2 * math.pi * segment / segments
            offset = (X * math.sin(polar) * math.cos(turn) + Z * math.sin(polar) * math.sin(turn) + Y * math.cos(polar))
            points.append(centre + offset * radius)
            # Seen from in front: the whole front half fills the image, the back half folds behind it.
            reach = min(polar, math.pi - polar) / (math.pi / 2) * 0.5
            uvs.append((0.5 + reach * math.cos(turn), 0.5 + reach * math.sin(turn)))
    points.append(centre - Y * radius)
    uvs.append((0.5, 0.5))
    faces = []
    last = len(points) - 1

    def at(ring, segment):
        return 1 + (ring - 1) * segments + segment % segments

    for segment in range(segments):
        faces.append((0, at(1, segment + 1), at(1, segment)))
        for ring in range(1, rings - 1):
            faces.append((at(ring, segment), at(ring, segment + 1), at(ring + 1, segment + 1), at(ring + 1, segment)))
        faces.append((last, at(rings - 1, segment), at(rings - 1, segment + 1)))
    return np.array(points), faces, uvs


def _arch(front, half_width, depth, count):
    """Points of a row of teeth seen from above: from the last tooth at the right over the front
    teeth to the last tooth at the left, on half an oval."""
    out = []
    for k in range(count + 1):
        turn = math.pi * k / count
        out.append(np.array([half_width * math.cos(turn), front[1] - depth * (1 - math.sin(turn)), front[2]]))
    return out


def teeth(front, half_width, depth, height, thickness, edge, upper, segments=10):
    """A row of teeth as one band: its outer face, its biting edge, and its inner face, open
    toward the gums. The front is the middle of the front teeth's biting edge. The band is as
    thick as thickness at the gums and as edge at its biting edge, as a tooth narrows to its
    edge."""
    front = np.asarray(front, float)
    outer = _arch(front, half_width, depth, segments)
    biting = _arch(front - Y * edge, half_width - edge, depth - edge * 0.4, segments)
    inner = _arch(front - Y * thickness, half_width - thickness, depth - thickness * 0.4, segments)
    rise = Z * (height if upper else -height)
    points, faces = [], []
    for o, b, i in zip(outer, biting, inner):
        points += [o + rise, o, b, i + rise]
    for k in range(segments):
        a, b = 4 * k, 4 * (k + 1)
        for step in range(3):
            quad = (a + step, b + step, b + step + 1, a + step + 1)
            faces.append(quad[::-1] if upper else quad)
    # The two ends behind the last teeth.
    first, last = (0, 1, 2, 3), (4 * segments + 3, 4 * segments + 2, 4 * segments + 1, 4 * segments)
    faces.append(first[::-1] if upper else first)
    faces.append(last[::-1] if upper else last)
    return np.array(points), faces


# The tongue's tip is this share of its width wide.
TONGUE_TIP = 0.12


def tongue(tip, length, half_width, thickness, columns=4, rows=5):
    """A tongue at rest in the lower row of teeth: a flat body with a rounded top and a rounded
    tip behind the lower front teeth, open underneath."""
    tip = np.asarray(tip, float)
    points, faces = [], []
    for row in range(rows + 1):
        along = row / rows
        # Rounded from the tip to its whole width two thirds back.
        width = half_width * math.sin(math.pi / 2 * min(TONGUE_TIP + (1.0 - TONGUE_TIP) * along / 0.7, 1.0))
        for column in range(columns + 1):
            across = -1.0 + 2.0 * column / columns
            crown = thickness * (1.0 - across * across) * (0.55 + 0.45 * math.sin(math.pi * min(along * 1.4, 1.0)))
            points.append(tip - Y * (length * along) + X * (width * across) + Z * crown)
    for row in range(rows):
        for column in range(columns):
            a = row * (columns + 1) + column
            b = a + columns + 1
            faces.append((a, b, b + 1, a + 1))
    return np.array(points), faces
