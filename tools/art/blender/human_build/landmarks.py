"""The rig's landmarks of a built body: named points on its skin, in Blender's world space, that the
rig places joints by, the reviews aim their cameras at, and paint layers pair to move between
bodies. assemble.py writes them onto the root of each body file as the custom properties
lmw_<name>, each a flat list of x, y, z. A landmark with a point on each side lists the right
side's points (+X) first and then their mirror images.

The earlier bodies, art/Characters/Human_Male and Human_Female, have the same keys with as many
points in the same order and with the same meaning, so a paint layer made on one of them pairs
its landmarks with these:

- finger_tips, finger_back, finger_front: per digit, from the little finger to the thumb, its tip
  and the middles of its last ring's two corners on the nail's side and on the pad's side;
- toe_tips, toe_back, toe_front: the same per toe, from the big toe to the little one;
- wrist: the right wrist's ring, from the palm's side a little toward the thumb on toward the
  little finger's side;
- eye_rim: the right lids' inner edge in twenty points, as the rim of the earlier bodies' eyes;
- upper_lip_line, lower_lip_line: each lip's edge at the line between the lips, from right to
  left over the middle of the mouth;
- mouth_corner, mouth_centre: the right corner of the mouth and the middle of the line between
  the lips;
- upper_lip, lower_lip, chin_front, chin_bottom, nose_tip, subnasale, sellion, forehead: points
  of the face's middle line.

The rig also reads landmarks that only these bodies have:

- shoulder_joint: where the reference's arm was turned to its pose, which the rig's search for the
  shoulder joint starts from;
- finger_bases: per digit the ring it grows from out of the palm, eight points each, which the
  rig parts the digit's skin from the palm's by;
- finger_joints: per digit the middles of the rings around its two joints;
- toe_ball: the middle of the ring at the ball of the foot, over the joints of the toes."""

import math

import numpy as np

DIGITS = ("Little", "Ring", "Middle", "Index", "Thumb")
TOES = ("Big", "Second", "Third", "Fourth", "Little")
# The rings and lines of the cage that the landmarks read through the subdivided net.
RINGS = ("arm_end",) + tuple(f"hand_{digit}_base" for digit in DIGITS)
LINES = ("lip_upper", "lip_lower")
# The earlier bodies' lines between the lips reach this share of the way to the mouth's corner,
# with points at these shares of it, and these lines are laid at the same shares.
LIP_SHARES = (0.55, 0.48, 0.34, 0.17)
# The rim of the eye: each lid in RIM_STEPS steps from the inner corner to the outer one, at the
# shares (1 - cos(180 k / RIM_STEPS degrees)) / 2 of the way between them.
RIM_STEPS = 10
# The wrist's ring starts this many degrees from the palm's side toward the thumb's.
WRIST_START = 30.0
# Lengths at a body height of REFERENCE_HEIGHT, in metres: the face lies in front of a plane this far
# behind the eyeball's centre, the forehead's point lies this far above the sellion, and the parts
# of the face's middle line are looked for within these heights.
REFERENCE_HEIGHT = 1.785
FACE_BEHIND_EYE = 0.04
FOREHEAD_ABOVE_SELLION = 0.0458
BROW_ABOVE_EYE = 0.035
LOWER_LIP_REACH = 0.02
# The line down the chin from the lower lip ends where it has turned back under the chin this far
# behind the lower lip. Its lowest point is the one furthest down and forward at this angle below the
# forward direction, where the front of the chin turns under it.
CHIN_DEPTH = 0.025
CHIN_BOTTOM_ANGLE = 60.0


def trace(fine):
    """Names, in a subdivided net, the rings and lines of the cage that the landmarks read, as
    <name>_path through the vertices that subdividing put between theirs."""
    for name in RINGS + LINES:
        if name in fine.marks:
            fine.marks[f"{name}_path"] = fine.path(fine.marks[name], closed=name in RINGS)


def _unit(v):
    v = np.asarray(v, float)
    return v / max(float(np.linalg.norm(v)), 1e-12)


def _mirrored(points):
    return [np.asarray(p, float) * (-1.0, 1.0, 1.0) for p in points]


def both(right):
    """The points of the right side followed by their mirror images."""
    right = [np.asarray(p, float) for p in right]
    return right + _mirrored(right)


def ordered_ring(points, ring, start, toward):
    """A closed ring of vertices from the one that lies furthest in the direction start from the
    ring's middle, on around the way whose next vertex lies further in the direction toward."""
    points = np.asarray(points, float)
    middle = points[list(ring)].mean(0)
    first = int(np.argmax((points[list(ring)] - middle) @ np.asarray(start, float)))
    count = len(ring)
    ahead = [ring[(first + k) % count] for k in range(count)]
    back = [ring[(first - k) % count] for k in range(count)]
    toward = np.asarray(toward, float)
    return ahead if (points[ahead[1]] - middle) @ toward >= (points[back[1]] - middle) @ toward else back


def _along(chain, a, b, share):
    """The point of a line of points at a share of the way from a to b, measured along a to b."""
    axis = b - a
    length = float(np.linalg.norm(axis))
    along = (chain - a) @ axis / (length * length)
    if np.any(np.diff(along) <= 0.0):
        raise ValueError("a lid's edge turns back on its way from one corner of the eye to the other")
    return np.array([np.interp(share, along, chain[:, k]) for k in range(3)])


def eye_rim(edge):
    """The rim of the eye from its lids' inner edge, which runs from the inner corner over the upper
    lid to the outer corner halfway and back under the lower lid: each lid at RIM_STEPS - 1 shares
    of the way between the corners, in the earlier bodies' order: the upper lid's second to last
    point, the outer corner, the lower lid's points back to its first, the inner corner, and the
    upper lid's first point."""
    edge = np.asarray(edge, float)
    half = len(edge) // 2
    upper = edge[:half + 1]
    lower = np.concatenate([edge[:1], edge[half:][::-1]])
    inner, outer = upper[0], upper[-1]
    shares = [(1.0 - math.cos(math.pi * k / RIM_STEPS)) / 2.0 for k in range(1, RIM_STEPS)]
    up = [_along(upper, inner, outer, share) for share in shares]
    down = [_along(lower, inner, outer, share) for share in shares]
    return up[1:] + [outer] + down[:0:-1] + [down[0], inner, up[0]]


def lip_line(line, shares=LIP_SHARES):
    """A lip's edge at the line between the lips, given from the middle of the mouth (x = 0) to its
    right corner: the points at the shares of the way to the corner on the right, the middle, and
    their mirror images, from right to left."""
    line = np.asarray(line, float)
    x = line[:, 0]
    if np.any(np.diff(x) <= 0.0):
        raise ValueError("the line between the lips turns back on its way to the mouth's corner")
    right = [np.array([np.interp(share * x[-1], x, line[:, k]) for k in range(3)]) for share in shares]
    return right + [line[0]] + _mirrored(right[::-1])


def middle_line(points, faces, zones, parts, zone):
    """The points of the mirror plane on the skin faces of one zone, as an (n, 3) array."""
    points = np.asarray(points, float)
    on = {v for face, z, part in zip(faces, zones, parts) if z == zone and part == 0
          for v in face if abs(points[v][0]) < 1e-6}
    return points[sorted(on)]


def face_points(line, mouth, eye, scale):
    """The points of the face's middle line: line holds the points of the middle line over the head
    in any order, mouth is the middle of the line between the lips and eye the right eyeball's
    centre. Each is the point furthest forward or back within heights set by the ones before it; the
    chin's front is the point of the chin that stands out furthest in front of the line from the
    lower lip to the chin's lowest point."""
    line = np.asarray(line, float)
    front = line[line[:, 1] > eye[1] - FACE_BEHIND_EYE * scale]

    def pick(low, high, value, largest=True):
        inside = front[(front[:, 2] >= low) & (front[:, 2] <= high)]
        if not len(inside):
            raise ValueError(f"the face's middle line has no point between {low:.4f} and {high:.4f} m high")
        values = value(inside)
        return inside[int(np.argmax(values) if largest else np.argmin(values))]

    def forward(p):
        return p[:, 1]

    angle = math.radians(CHIN_BOTTOM_ANGLE)
    found = {}
    found["nose_tip"] = pick(mouth[2], eye[2], forward)
    brow = pick(eye[2], eye[2] + BROW_ABOVE_EYE * scale, forward)
    found["sellion"] = pick(found["nose_tip"][2], brow[2], forward, largest=False)
    found["upper_lip"] = pick(mouth[2], (mouth[2] + found["nose_tip"][2]) / 2, forward)
    found["subnasale"] = pick(found["upper_lip"][2], found["nose_tip"][2], forward, largest=False)
    found["lower_lip"] = lip = pick(mouth[2] - LOWER_LIP_REACH * scale, mouth[2], forward)
    # Down the chin from the lower lip until the line has turned back under it.
    below = front[front[:, 2] < lip[2]]
    below = below[np.argsort(-below[:, 2])]
    turned = np.nonzero(below[:, 1] < lip[1] - CHIN_DEPTH * scale)[0]
    chin = below[:turned[0] if len(turned) else len(below)]
    if not len(chin):
        raise ValueError("the face's middle line has no chin below the lower lip")
    bottom = found["chin_bottom"] = chin[int(np.argmax(chin[:, 1] * math.cos(angle) - chin[:, 2] * math.sin(angle)))]
    between = chin[chin[:, 2] > bottom[2]]
    way = _unit(bottom - lip)
    ahead = np.array([0.0, -way[2], way[1]]) * (1.0 if way[2] < 0.0 else -1.0)
    found["chin_front"] = between[int(np.argmax((between - lip) @ ahead))] if len(between) else bottom
    # The forehead's point on the line between the points just below and above its height.
    height = found["sellion"][2] + FOREHEAD_ABOVE_SELLION * scale
    brow_line = front[front[:, 1] > found["sellion"][1] - FACE_BEHIND_EYE * scale]
    brow_line = brow_line[np.argsort(brow_line[:, 2])]
    if not brow_line[0, 2] <= height <= brow_line[-1, 2]:
        raise ValueError(f"the face's middle line does not reach the forehead {height:.4f} m high")
    found["forehead"] = np.array([0.0, np.interp(height, brow_line[:, 2], brow_line[:, 1]), height])
    return found


def of_net(points, marks, faces, zones, parts, head_zone, eye, shoulder):
    """Every landmark of a built body from its net: the positions of all its vertices, the named
    vertices of its right half, its faces with their zones and hidden parts, the zone of the head,
    the right eyeball's centre, and the reference's right shoulder joint. Returns lists of points
    by landmark."""
    points = np.asarray(points, float)
    scale = float(points[:, 2].max()) / REFERENCE_HEIGHT

    def middle(name):
        return points[list(marks[name])].mean(0)

    found = {"finger_tips": both(points[marks[f"{digit}_tip"][0]] for digit in DIGITS),
             "finger_back": both(middle(f"hand_{digit}_nail") for digit in DIGITS),
             "finger_front": both(middle(f"hand_{digit}_pad") for digit in DIGITS),
             "finger_bases": both(points[v] for digit in DIGITS for v in marks[f"hand_{digit}_base_path"]),
             "finger_joints": both(middle(f"hand_{digit}_{joint}") for digit in DIGITS for joint in ("first", "second")),
             "toe_tips": both(points[marks[f"{toe}_toe_tip"][0]] for toe in TOES),
             "toe_back": both(middle(f"foot_{toe}_nail") for toe in TOES),
             "toe_front": both(middle(f"foot_{toe}_pad") for toe in TOES),
             "toe_ball": both([middle("foot_ball")]),
             "shoulder_joint": both([shoulder])}

    # The wrist from the palm's side a little toward the thumb: the palm faces from the nails to the
    # pads of the fingers, and the thumb's side lies from the little finger toward the index.
    palmar = _unit(sum(middle(f"hand_{digit}_pad") - middle(f"hand_{digit}_nail") for digit in DIGITS[:4]))
    radial = middle("hand_Index_base") - middle("hand_Little_base")
    radial = _unit(radial - (radial @ palmar) * palmar)
    turn = math.radians(WRIST_START)
    ring = ordered_ring(points, marks["arm_end_path"], math.cos(turn) * palmar + math.sin(turn) * radial, -radial)
    found["wrist"] = [points[v] for v in ring]
    found["eye_rim"] = eye_rim(points[marks["lid_inner"]])

    upper, lower = points[marks["lip_upper_path"]], points[marks["lip_lower_path"]]
    found["upper_lip_line"] = lip_line(upper)
    found["lower_lip_line"] = lip_line(lower)
    found["mouth_corner"] = [upper[-1]]
    mouth = (upper[0] + lower[0]) / 2
    found["mouth_centre"] = [mouth]
    line = middle_line(points, faces, zones, parts, head_zone)
    found.update({name: [point] for name, point in face_points(line, mouth, eye, scale).items()})
    return found
