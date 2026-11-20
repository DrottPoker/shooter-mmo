"""The reference meshes of the human base bodies, taken from the Human Base Meshes bundle v1.4.1 of
Blender Studio (CC0): the realistic male and female bodies with their eyes, turned to face +Y,
centred on x = 0, standing on z = 0, scaled to the heights of the game's bodies, and with their
arms turned out to an A-pose.

    blender --background --factory-startup --disable-autoexec <bundle.blend> \
        --python tools/art/blender/human_build/reference.py -- <out.blend>

The bundle is read as data only. The output holds, per body, Reference_<Label> (the sculpt at
SCULPT_LEVEL), ReferenceBase_<Label> (its control cage, for its edge flow), the eyeballs,
HeadCage_<Label>: the bundle's head cage laid onto that body's head, and HeadEar_<Label>: the right
ear of that body's control cage with the skin around it, and how it is set into the head cage.

The head cage is the bundle's coarse cage of the female body's head, so a move, a turn, and one
scale lay it onto her reference. The two bodies of the bundle are sculpted on one net, so a point
of her surface, as a place in a face of that net, is the same feature on his: each vertex of the
cage takes on him the place it has on her, and the loops around the eyes, the mouth, the nose, and
the ear lie on the same features of both."""

import math
import os
import sys

import bpy
import numpy as np
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from human_build import body  # noqa: E402
from human_build.head import EAR_FACES, outline  # noqa: E402
from human_build.net import evened  # noqa: E402
from human_build.surface import Surface  # noqa: E402

BUNDLE_SHA256 = "3c121505651140ceb4d69fd1d8923f7788ffadd81672f5be14845a5f2c75c137"
BODIES = (("Male", "GEO-body_male_realistic", 1.79), ("Female", "GEO-body_female_realistic", 1.70))
# The multires level of the reference surface: level 2 has about 170,000 vertices, enough for a net
# of 7,000 faces, and keeps the files small.
SCULPT_LEVEL = 2
# The bundle's bodies stand with their arms hanging, the line from the shoulder joint to the wrist
# 19 degrees from straight down. A body is bound to its skeleton best in the middle of what its arms
# do, with the armpit open: the arms are turned out about the shoulder joints until that line is
# ARM_ANGLE degrees from straight down. The bundle's own coarse net under the sculpt is turned, so
# the sculpt's detail follows it. Each vertex takes a share of the turn, and where the shares lie
# decides whether the body looks like one that holds its arms out or like one whose skin was
# pulled:
# - The round of the shoulder turns whole. It is round about the joint, so turning it about the
#   joint keeps its shape, where a part of the turn would shear it flat: every vertex further out
#   than ARM_CAP outside the joint, above the armpit, takes the whole turn, and so does the arm
#   from ARM_BELOW below the armpit.
# - The trunk stays. The side of the chest under the armpit, from ARM_FLANK below it, and the
#   chest and the back inside the joint take none of the turn: ARM_ROOT inside it at the height
#   of the armpit, where the folds of the chest's and the back's muscles run out to the arm, and
#   ARM_TOP inside it at the joint's own height and above, over the top of the shoulder. With
#   more of the trunk taking part, its side is drawn out to the arm like a web and the armpit
#   lies far out on the arm.
# - Between those the share evens out along the net: a band over the top of the shoulder, the
#   two folds, and the hollow of the armpit between them.
# The skin of that passage below the joint is stretched to several times its width, and the
# sculpt's own folds of a hanging arm stand in it as creases and spikes: it is evened out,
# ARM_EVEN_ROUNDS rounds, most in the middle of the passage. Lengths at 1.79 m.
ARM_ANGLE = 45.0
ARM_CAP, ARM_BELOW = 0.01, 0.03
ARM_FLANK, ARM_ROOT, ARM_TOP = 0.025, 0.065, 0.02
ARM_EVEN_ROUNDS = 60
# The armpit. The bundle's arms hang against the chest, so its armpit is closed: the arm and the
# trunk meet where their skins touch, low and out from the chest, and turning the arm out leaves
# that seam as the top of the gap between them, with the arm grown into the trunk above it. On a
# body that holds its arms out, the underside of the arm runs in to the side of the chest, and a
# hollow lies there between the fold of the chest's muscle in front and the fold of the back's
# muscle behind. So the reference's armpit is opened by body measures. The top of the gap is
# moved up and in, to ARMPIT_BELOW_SHOULDER below the top of the shoulder, as far as an armpit
# lies below the point of the shoulder on a standing body, and ARMPIT_BESIDE out from the foot of
# the chest's side, which is its outline ARMPIT_WALL below the armpit. Below that foot the side
# of the chest keeps its place. Above it the side comes to run straight from the foot to the new
# top of the gap: each piece of the body takes a share of the move, and across the line of the
# arm that share is what the outline of the chest's side needs to land on the straight line, so
# only what stood out beyond that line is drawn in, and nothing under the armpit is pinched.
# Along the line of the arm the share falls evenly over ARMPIT_REACH, so the underside of the arm
# is drawn in along itself. The move is whole through the whole depth of the body there, from
# the skin behind the shoulder to the skin in front of it, so the folds move as they are with
# what lies between them, and it ends ARMPIT_BEYOND behind and in front of that depth. Between
# the folds the skin is lifted ARMPIT_HOLLOW more, to a hollow that runs out along the underside
# of the arm; the folds are where the ring around the shoulder lies less than ARMPIT_ARC above
# its lowest point. No piece of the body may be left less than ARMPIT_ROOM of its room, or skin
# would be laid over skin, and the side of the chest may nowhere narrow by more than
# ARMPIT_NARROWS on its way up to the armpit, or it would bulge below it. Lengths at 1.79 m.
ARMPIT_BELOW_SHOULDER = 0.135
ARMPIT_BESIDE = 0.01
ARMPIT_WALL = 0.06
ARMPIT_REACH, ARMPIT_BEYOND = 0.12, 0.05
ARMPIT_HOLLOW = 0.025
ARMPIT_ARC = 0.015
ARMPIT_ROOM = 0.1
ARMPIT_NARROWS = 0.002
HEAD = "Head - Generic Topology"
# The shape of the head cage that is the female body's head, and the body it is laid onto first.
HEAD_SHAPE, HEAD_BODY = "Real_FEMALE", "Female"
# The cage is fitted by its vertices above this share of its height, which leaves out the faces
# that close its underside, in this many rounds of nearest points within this reach.
HEAD_FIT_ABOVE, HEAD_FIT_ROUNDS, HEAD_FIT_REACH = 0.2, 40, 0.02
# The reference has a mouth cavity, eye sockets, and nostrils inside the head. A vertex of the cage
# is laid onto skin that is seen from outside: skin from which a ray leaves the head, straight out
# or tilted this many degrees in one of eight directions, without meeting skin again within the
# reach. Such skin is looked for within HEAD_SKIN_NEAR of the vertex.
HEAD_SEEN_TILT, HEAD_SEEN_REACH, HEAD_SKIN_NEAR = 50.0, 0.08, 0.02
# The cage is a head alone: behind the chin its centre line rises into its own short neck, where
# the body's throat begins further forward and lower. The last vertices of that line lie inside
# the body there, nearest to the floor of its mouth, with no skin seen from outside within reach.
# They are laid along the skin of the mirror plane instead, at the cage's own spacing: the first
# two vertices of the line lie under the chin and give the way, and the others follow them around
# the corner onto the throat, the last of them on the ring under the head. The way is walked in
# steps of HEAD_CHIN_STEP, which the reference's skin is smooth enough for.
HEAD_CHIN_LINE = (298, 314, 316, 300)
HEAD_CHIN_STEP = 0.0005
# The head cage's ear is a box of sixteen faces, far too coarse for an ear's folds, and the faces
# around the ring it stands on are narrow and twisted where the ear meets the head. The body's own
# control cage has an ear that is built for the folds, with even rings of faces on the skin around
# it. So the box and the faces that touch its ring leave the head cage. Under the ear's lobe the
# cage's next vertices lie nearer to the ear than the skin around it needs: where a vertex of that
# hole's outline lies within EAR_ROOM of the ring, the faces that touch it leave the cage too. The
# control cage's faces inside the outline take their place: every face that keeps EAR_MARGIN clear
# of the outline as the subdivided cage has it, with a vertex in the middle of each of its edges.
# The band between that outline and the edge of the control cage's patch lies on plain skin, and
# triangles close it: of all ways to close it, the one whose triangles are least thin, where a
# triangle that would look into the head counts as BAND_TURNED thin ones. The two bodies' control
# cages are one net and the head cage lies on the same places of both, so one choice of faces and
# triangles is made for both bodies and serves both. A face of the control cage that is smaller
# than EAR_LEAST on a body, in square metres, is smaller than a body's faces may be: its shortest
# edge closes, so its two vertices become one.
EAR_ROOM, EAR_MARGIN = 0.012, 0.006
EAR_LEAST = 1.2e-6
BAND_TURNED = 1000.0
# The female figure, as the reviewer asked for it: clearly female, with wide hips, a narrow waist,
# and a larger bust that sits close together, without exaggeration, and later a bust a little
# larger and rounder, attractive. The bundle's female is slim and athletic, so her reference is
# shaped by smooth, mirrored fields before the net is laid onto it: the hips widen and the waist
# narrows by these shares, in bands at these shares of her height, and each breast swells forward
# by BUST_FORWARD and moves BUST_INWARD toward the middle, in a field that reaches BUST_REACH
# across the front of the chest about a point BUST_LOWER below and BUST_INSIDE inside the
# breast's tip. The swell is flat over the field's middle and falls over its outer share
# BUST_EDGE, so the breast moves forward whole and its sides and its lower half fill out, where a
# swell that falls from the tip makes the breast pointed. The breast also swells by BUST_ROUND
# about a point BUST_DEPTH behind its tip, out in every direction, which rounds its lower half and
# its sides; toward the middle it moves no closer than it lies, so the two never meet. A swell
# keeps the fold under the breast as soft as the bundle's, where growing the breast about its root
# would deepen it. The field fades in between the two depths of BUST_FRONT behind the tip, so the
# back keeps its shape. Lengths are in metres on a body of 1.70 m. The head, the arms, and the legs
# below the knee keep the bundle's shape.
HIPS_WIDER, HIPS_AT, HIPS_BAND = 0.075, 0.515, (0.36, 0.61)
WAIST_NARROWER, WAIST_AT, WAIST_BAND = 0.065, 0.628, (0.565, 0.70)
BUST_FORWARD, BUST_INWARD, BUST_REACH = 0.022, 0.014, 0.105
BUST_LOWER, BUST_INSIDE, BUST_EDGE = 0.002, 0.008, 0.7
BUST_ROUND, BUST_DEPTH = 0.005, 0.045
BUST_FRONT = (0.11, 0.05)
BUST_BETWEEN = (0.70, 0.775)
TRUNK_HALF_WIDTH = (0.235, 0.285)
# The body's hips lie this far in front of the origin, as on the bodies the game has, so the
# origin stays under the body's weight.
HIPS_FORWARD = 0.015
HIP_HEIGHT_SHARE = 0.52
# The male's groin, as the reviewer asked for it: smooth, without a bulge that stands out, as a
# body for the game's underwear and clothes. The two bodies of the bundle are sculpted on one net,
# so each vertex of his groin has its place on hers, which is smooth: within GROIN_REACH of the
# crotch, as half its width, its height above the crotch, and its depth below it, his groin takes
# her form, laid onto his body by the move, turn, and scale that best lay the edge of that patch
# onto his. It takes all of it within GROIN_CORE, as half its width and its height, and fades to
# his own form at the patch's edge, evened out along the net. GROIN_KEEP of his own form stays, a
# hint of a male groin. Lengths at 1.79 m.
GROIN_REACH = (0.075, 0.13, 0.03)
GROIN_CORE = (0.035, 0.085)
GROIN_KEEP = 0.2
# A little more fullness on both bodies, as the reviewer asked for it: more filled out and not as
# thin, only a little, and the male most. The female's upper arm, the thinnest part of her, grows
# by about as much as his in width. The limbs, the round of the shoulder, the chest, and the neck
# grow about their own middles by these shares of their width, where each is widest, fading out
# toward the joints, so the elbows, the wrists, the knees, and the ankles keep their width. FULLER_ALONG gives where along each limb, from its upper joint at 0 to its lower one
# at 1, the growth starts, is greatest, and ends; FULLER_REACH how far from the limb's line its skin
# lies, at 1.79 m. The round of the shoulder grows about the shoulder joint within SHOULDER_REACH of
# it, the chest about the trunk's middle between the waist and the armpit, and the neck about its
# middle from the notch above the breastbone up to NECK_TOP above it.
FULLER = {
    "Male": {"upper_arm": 0.07, "forearm": 0.06, "shoulder": 0.05, "thigh": 0.06, "calf": 0.07, "chest": 0.04, "neck": 0.05},
    "Female": {"upper_arm": 0.08, "forearm": 0.05, "shoulder": 0.03, "thigh": 0.04, "calf": 0.04, "chest": 0.0, "neck": 0.02},
}
FULLER_ALONG = {"upper_arm": (0.15, 0.55, 0.97), "forearm": (0.0, 0.3, 0.85), "thigh": (0.1, 0.4, 0.92), "calf": (0.05, 0.3, 0.8)}
FULLER_REACH = {"upper_arm": 0.075, "forearm": 0.065, "thigh": 0.12, "calf": 0.085}
SHOULDER_REACH, NECK_TOP = 0.09, 0.09


def _evaluated(obj, level):
    for modifier in obj.modifiers:
        if modifier.type == "MULTIRES":
            modifier.levels = min(level, modifier.total_levels)
    bpy.context.view_layer.update()
    depsgraph = bpy.context.evaluated_depsgraph_get()
    mesh = bpy.data.meshes.new_from_object(obj.evaluated_get(depsgraph), depsgraph=depsgraph)
    mesh.transform(obj.matrix_world)
    return mesh


def arm_shares(points, edges, in_arm, joint, armpit_z, scale):
    """The share of the arm's turn that each vertex of a body's net takes. in_arm says which
    vertices lie beyond the gap between the arm and the trunk, on either side of the body, and
    joint is the right arm's shoulder joint."""
    points, in_arm = np.asarray(points, float), np.asarray(in_arm, bool)
    side, z = np.abs(points[:, 0]), points[:, 2]
    turns = (in_arm & (z < armpit_z - ARM_BELOW * scale)) | ((side > joint[0] + ARM_CAP * scale) & (z > armpit_z))
    rise = np.clip((z - armpit_z) / (joint[2] - armpit_z), 0.0, 1.0)
    inside = (ARM_ROOT + (ARM_TOP - ARM_ROOT) * rise) * scale
    stays = (side < joint[0] - inside) | (~in_arm & (z < armpit_z - ARM_FLANK * scale))
    stays &= ~turns
    edges = np.asarray(edges, np.int64)
    lengths = np.linalg.norm(points[edges[:, 0]] - points[edges[:, 1]], axis=1)
    return evened(len(points), edges, 1.0 / np.maximum(lengths, 1e-9), turns | stays, turns.astype(float))


def passage_evened(before, after, edges, joint, angle, rounds=ARM_EVEN_ROUNDS):
    """A body's points after its arms were turned out, with the stretched skin below the joint
    evened out. before and after hold the same vertices; how far each turned about its shoulder
    joint says where it lies in the passage from the trunk to the arm, and a vertex in the
    middle of the passage moves furthest toward the middle of its neighbours."""
    before, after = np.asarray(before, float), np.array(after, float)
    edges = np.asarray(edges, np.int64)
    a, b = edges[:, 0], edges[:, 1]
    sign = np.where(before[:, 0] >= 0.0, 1.0, -1.0)

    def bearing(points):
        return np.arctan2(points[:, 2] - joint[2], sign * points[:, 0] - joint[0])

    turn = np.angle(np.exp(1j * (bearing(after) - bearing(before))))
    share = np.clip(turn / math.radians(angle), 0.0, 1.0)
    weight = np.where(before[:, 2] < joint[2], 4.0 * share * (1.0 - share), 0.0)
    count = np.maximum(np.bincount(a, minlength=len(after)) + np.bincount(b, minlength=len(after)), 1)
    for _ in range(rounds):
        total = np.zeros_like(after)
        np.add.at(total, a, after[b])
        np.add.at(total, b, after[a])
        after += (0.5 * weight)[:, None] * (total / count[:, None] - after)
    return after


def turned_out(points, shares, joint, angle):
    """The points with the arms turned out by an angle in degrees about the line from back to
    front through each shoulder joint, each point by its share of the angle. joint is the right
    arm's, at +x; the left arm turns as its mirror image."""
    points = np.asarray(points, float)
    sign = np.where(points[:, 0] >= 0.0, 1.0, -1.0)
    turn = -sign * np.radians(angle) * np.asarray(shares, float)
    x, z = points[:, 0] - sign * joint[0], points[:, 2] - joint[2]
    out = points.copy()
    out[:, 0] = sign * joint[0] + x * np.cos(turn) + z * np.sin(turn)
    out[:, 2] = joint[2] - x * np.sin(turn) + z * np.cos(turn)
    return out


def pose_arms(source, placement, sculpt, label):
    """Turns the arms of a body of the bundle out, in the bundle's own net: the object's vertices
    move, and its sculpt follows when it is evaluated again. sculpt is the body's sculpt as the
    placement stands it. Gives the right arm's shoulder joint, which the turn leaves in place,
    and the angle of the turn in degrees."""
    if source.data.shape_keys is not None:
        raise RuntimeError(f"{source.name} has shape keys; its vertices cannot be turned as they are")
    m = body.Measures(Surface(_points(sculpt), _triangles(sculpt)))
    joint, _elbow, wrist, _surface = body._arm_axis(m)
    hangs = math.degrees(math.atan2(wrist[0] - joint[0], joint[2] - wrist[2]))
    to_placed = placement @ source.matrix_world
    points = np.array([tuple(to_placed @ v.co) for v in source.data.vertices])
    edges = np.array([tuple(e.vertices) for e in source.data.edges], np.int64)
    side, z = np.abs(points[:, 0]), points[:, 2]
    in_arm = (side > m.gap(z)) & (z > 0.35 * m.height)
    shares = arm_shares(points, edges, in_arm, joint, m.armpit_z, m.scale)
    posed = turned_out(points, shares, joint, ARM_ANGLE - hangs)
    back = to_placed.inverted()
    for v, point in zip(source.data.vertices, posed):
        v.co = back @ Vector(point)
    source.data.update()
    part = int(((shares > 0.001) & (shares < 0.999)).sum())
    print(f"HUMAN_BUILD_REFERENCE {label}: the arm hangs {hangs:.1f} degrees from straight down and is turned out to "
          f"{ARM_ANGLE:.0f} about the shoulder joint at {np.round(joint, 3).tolist()}; {part} of the bundle's "
          f"{len(points)} vertices take a part of the turn")
    return joint, ARM_ANGLE - hangs


def _bell(t):
    """1 at 0, falling smoothly to 0 at 1 and beyond, level at both ends."""
    t = np.minimum(np.asarray(t, float) ** 2, 1.0)
    return (1.0 - t) ** 1.5


def _eased(u):
    """A distance in reaches as a loss of share: the distance itself up to 0.7, then levelling
    out to 1 at 1.3, so a share that falls evenly with the distance ends without a crease."""
    u = np.abs(np.asarray(u, float))
    return np.where(u < 0.7, u, 1.0 - np.maximum(1.3 - u, 0.0) ** 2 / 1.2)


def wall_shares(wall, top, to, arm, scale):
    """How much of the armpit's move the side of the chest takes at distances across the line of
    the arm, so that it comes to run straight from its foot to the new top of the gap. wall is
    the outline of the chest's side as (x, z) from its foot, which stays, up to the top of the
    gap; top and to are the top of the gap before and after the move, and arm is the way the
    upper arm runs, out and down. Gives the distances across the arm's line, from 0 at the top of
    the gap outward, and the share at each, which only falls with the distance and ends at
    nothing a centimetre beyond the foot."""
    wall, top, to = np.asarray(wall, float), np.asarray(top, float), np.asarray(to, float)
    foot = wall[0]
    slope = (to[0] - foot[0]) / (to[2] - foot[1])
    # A point of the outline moved by its share lies on the line from the foot to the new top.
    share = (wall[:, 0] - foot[0] - slope * (wall[:, 1] - foot[1])) / (slope * (to[2] - top[2]) - (to[0] - top[0]))
    out, rise = wall[:, 0] - top[0], wall[:, 1] - top[2]
    across = out * arm[2] - rise * arm[0]
    if across.min() <= 0.0:
        raise ValueError("the outline of the chest's side crosses the line of the arm below the top of the gap")
    # What the outline loses of the move by lying a little along the arm's line is not lost again.
    along = _eased((out * arm[0] + rise * arm[2]) / (ARMPIT_REACH * scale))
    lost = np.sqrt(np.maximum((1.0 - np.clip(share, 0.0, 1.0)) ** 2 - along * along, 0.0))
    order = np.argsort(across)
    share = np.minimum.accumulate(np.concatenate([[1.0], 1.0 - lost[order]]))
    return np.concatenate([[0.0], across[order], [across.max() + 0.01 * scale]]), np.concatenate([share, [0.0]])


def armpit_opened(points, top, move, arm, across, depth, hollow, half_width, scale):
    """The points with both armpits opened. top is the top of the gap between the right arm and
    the trunk, in the middle between its folds, which lie half_width behind and in front of it;
    arm is the way the upper arm runs, out and down, across is the share of the move at
    distances across the arm's line (wall_shares), and depth is the y of the body's skin behind
    and in front of the shoulder. Each point is moved by its share of move, up and in to the side
    of the chest, and between the folds lifted by its share of hollow more. The share is whole
    at the top of the gap through that depth; it falls evenly over ARMPIT_REACH along the arm,
    as across says across it, and evenly over ARMPIT_BEYOND behind and in front of the depth.
    The left side is the mirror image."""
    points = np.asarray(points, float)
    top, move = np.asarray(top, float), np.asarray(move, float)
    sign = np.where(points[:, 0] >= 0.0, 1.0, -1.0)
    out = np.abs(points[:, 0]) - top[0]
    rise = points[:, 2] - top[2]
    along = _eased((out * arm[0] + rise * arm[2]) / (ARMPIT_REACH * scale))
    lost = 1.0 - np.interp(np.abs(out * arm[2] - rise * arm[0]), across[0], across[1], right=0.0)
    beyond = _eased(np.maximum(np.maximum(depth[0] - points[:, 1], points[:, 1] - depth[1]), 0.0) / (ARMPIT_BEYOND * scale))
    share = np.clip(1.0 - np.sqrt(along * along + lost * lost + beyond * beyond), 0.0, 1.0)
    lift = hollow * share * _bell((points[:, 1] - top[1]) / half_width)
    opened = points.copy()
    opened[:, 0] = sign * (np.abs(points[:, 0]) + share * move[0])
    opened[:, 1] += share * move[1]
    opened[:, 2] += share * move[2] + lift
    return opened


def armpit_room(top, move, arm, across, depth, hollow, half_width, scale, step=0.002):
    """The least share of its room that the opening of the armpit leaves a piece of the body:
    1 where nothing moves, less where the body is pressed together, and nothing or less where
    skin would be laid over skin. Measured in the middle of the hollow and at a fold, which the
    opening moves most, on a grid of step across the reach of the move."""
    reach = 1.3 * ARMPIT_REACH * scale
    span = np.arange(-reach, reach + step / 2, step)
    out, rise = np.meshgrid(span, span, indexing="ij")
    least = 1.0
    for y in (top[1], top[1] + half_width):
        grid = np.stack([top[0] + out, np.full_like(out, y), top[2] + rise], axis=-1)
        opened = armpit_opened(grid.reshape(-1, 3), top, move, arm, across, depth, hollow, half_width, scale)
        opened = opened.reshape(grid.shape)
        x_out, x_rise = np.gradient(opened[..., 0], step)
        z_out, z_rise = np.gradient(opened[..., 2], step)
        least = min(least, float((x_out * z_rise - x_rise * z_out).min()))
    return least


def narrowing(outline):
    """How far an outline of the chest's side, as its x from the foot upward, narrows on its way
    up: the most that it lies inside a point further down."""
    outline = np.asarray(outline, float)
    return float((np.maximum.accumulate(outline) - outline).max())


def _side(m, z, middle, half):
    """How far out the side of the chest lies at a height, between the armpit's folds."""
    points = m._central(z).points
    return float(points[np.abs(points[:, 1] - middle) <= half][:, 0].max())


def _open_armpit(sculpt, base, label):
    """Opens the armpits of a body's reference and its control cage, by the measures of its
    reference, and names the armpit for the build."""
    joint = sculpt.get("shoulder_joint")
    m = body.Measures(Surface(_points(sculpt), _triangles(sculpt)), joint)
    s = m.scale
    ring = m.shoulder_ring.points
    low = ring[ring[:, 2] < ring[:, 2].min() + ARMPIT_ARC * s]
    middle, half = (low[:, 1].min() + low[:, 1].max()) / 2, (low[:, 1].max() - low[:, 1].min()) / 2
    depth = (float(ring[:, 1].min()), float(ring[:, 1].max()))
    shoulder, elbow, _wrist, _surface = body._arm_axis(m)
    arm = np.array([elbow[0] - shoulder[0], 0.0, elbow[2] - shoulder[2]])
    arm /= np.linalg.norm(arm)
    top = np.array([m.gap_x, middle, m.armpit_z])
    # The outline of the chest's side from its foot up to the top of the gap.
    heights = np.linspace(m.armpit_z - ARMPIT_WALL * s, m.armpit_z - 0.004, 25)
    wall = np.array([(_side(m, z, middle, half), z) for z in heights])
    to = np.array([wall[0, 0] + ARMPIT_BESIDE * s, middle, m.shoulder_top - ARMPIT_BELOW_SHOULDER * s])
    move = to - top
    if move[2] <= 0.0 or move[0] >= 0.0:
        raise RuntimeError(f"{label}: the armpit already lies where it is opened to, {np.round(move * 100, 1).tolist()} cm away")
    across = wall_shares(wall, top, to, arm, s)
    hollow = ARMPIT_HOLLOW * s
    room = armpit_room(top, move, arm, across, depth, hollow, half, s)
    if room < ARMPIT_ROOM:
        raise RuntimeError(f"{label}: opening the armpit leaves a piece of the body {room:.2f} of its room, so it lays "
                           f"skin over skin; the move is {np.round(move * 100, 1).tolist()} cm and the hollow "
                           f"{hollow * 100:.1f} cm")
    for mesh in (sculpt, base):
        opened = armpit_opened(_points(mesh), top, move, arm, across, depth, hollow, half, s)
        mesh.vertices.foreach_set("co", opened.ravel())
        mesh.update()
    # The build takes the armpit from here: the top of the gap between the arm and the trunk, in
    # the middle of the hollow from front to back. Measured again, the hollow above the folds
    # would count as a part of the gap.
    named = [float(to[0]), float(middle), float(to[2])]
    measured = body.Measures(Surface(_points(sculpt), _triangles(sculpt)), joint, named)
    # Just below the named height the arm must be apart from the trunk on its whole width.
    if measured._central(to[2] - 0.004).points[:, 0].max() > to[0] + 0.05 * s:
        raise RuntimeError(f"{label}: a fold of the opened armpit hangs below the height it was opened to")
    # The side of the chest as it now runs, from below its foot up to the armpit.
    heights = np.arange(wall[0, 1] - ARMPIT_WALL * s, to[2] - 0.004, 0.005 * s)
    outline = [_side(measured, z, middle, half) for z in heights]
    narrows = narrowing(outline)
    if narrows > ARMPIT_NARROWS * s:
        raise RuntimeError(f"{label}: the side of the chest narrows by {narrows * 1000:.1f} mm on its way up to the armpit, "
                           f"so it bulges below it")
    sculpt["armpit"] = named
    print(f"HUMAN_BUILD_REFERENCE {label}: the armpit, {(m.shoulder_top - m.armpit_z) * 100:.1f} cm below the top of the "
          f"shoulder and {(m.gap_x - wall[0, 0]) * 100:.1f} cm out from the foot of the chest's side, is moved "
          f"{move[2] * 100:.1f} cm up and {-move[0] * 100:.1f} cm in, through a depth of {(depth[1] - depth[0]) * 100:.1f} cm, "
          f"with a hollow {hollow * 100:.1f} cm deep and {2 * half * 100:.1f} cm from front to back, "
          f"{(depth[1] - middle) * 100:.1f} cm behind the front; the side of the chest takes a part of the move up to "
          f"{across[0][-1] * 100:.1f} cm across the arm's line and narrows by {narrows * 1000:.1f} mm at most, "
          f"{outline[0] * 100:.1f} cm out at {heights[0] * 100:.1f} and {outline[-1] * 100:.1f} cm under the armpit; the body "
          f"keeps {room:.2f} of its room or more")


def _points(mesh):
    co = np.empty(len(mesh.vertices) * 3)
    mesh.vertices.foreach_get("co", co)
    return co.reshape(-1, 3)


def _placement(points, height):
    """The matrix that turns the bundle's body, which faces -Y, to face +Y, centres it, stands it
    on the ground, and scales it to the height."""
    low, high = points.min(0), points.max(0)
    scale = height / (high[2] - low[2])
    centre_x = (low[0] + high[0]) / 2
    band = points[np.abs(points[:, 2] - (low[2] + HIP_HEIGHT_SHARE * (high[2] - low[2]))) < 0.01]
    trunk = band[np.abs(band[:, 0] - centre_x) < 0.2]
    hips_y = (trunk[:, 1].min() + trunk[:, 1].max()) / 2
    move = Matrix.Translation((-centre_x, -hips_y, -low[2]))
    turn = Matrix.Rotation(math.pi, 4, "Z")
    return Matrix.Translation((0.0, HIPS_FORWARD, 0.0)) @ Matrix.Scale(scale, 4) @ turn @ move


def _bump(value, low, peak, high):
    """A smooth rise from low to peak and fall to high: 1 at the peak, 0 outside."""
    value = np.asarray(value, float)
    rise = np.clip((value - low) / (peak - low), 0.0, 1.0)
    fall = np.clip((high - value) / (high - peak), 0.0, 1.0)
    t = np.where(value <= peak, rise, fall)
    return t * t * (3.0 - 2.0 * t)


def _rise(value, low, high):
    """A smooth rise from 0 at low to 1 at high, and 1 beyond."""
    t = np.clip((np.asarray(value, float) - low) / (high - low), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def _nipple(points, height):
    """The right breast's frontmost point."""
    low, high = BUST_BETWEEN
    chest = np.where((points[:, 2] > low * height) & (points[:, 2] < high * height)
                     & (points[:, 0] > 0.03 * height / 1.7) & (points[:, 0] < 0.15 * height / 1.7))[0]
    return points[chest[int(np.argmax(points[chest, 1]))]].copy()


def shape_female(points, height, nipple=None):
    """The female reference's points with the hips widened, the waist narrowed, and the bust
    enlarged and moved together. The fields are mirror symmetric and fade out smoothly."""
    s = height / 1.70
    out = points.copy()
    z = points[:, 2] / height
    # Only the trunk and the thighs: the arms and the hands hang beside them.
    inner, outer = TRUNK_HALF_WIDTH
    trunk = 1.0 - np.clip((np.abs(points[:, 0]) - inner * s) / ((outer - inner) * s), 0.0, 1.0)
    hips = _bump(z, HIPS_BAND[0], HIPS_AT, HIPS_BAND[1]) * trunk
    waist = _bump(z, WAIST_BAND[0], WAIST_AT, WAIST_BAND[1]) * trunk
    out[:, 0] *= 1.0 + HIPS_WIDER * hips - WAIST_NARROWER * waist
    out[:, 1] *= 1.0 - 0.5 * WAIST_NARROWER * waist
    nipple = _nipple(points, height) if nipple is None else nipple
    far, near = BUST_FRONT
    facing = _rise(points[:, 1], nipple[1] - far * s, nipple[1] - near * s)
    for side in (1.0, -1.0):
        middle_x = side * (nipple[0] - BUST_INSIDE * s)
        middle_z = nipple[2] - BUST_LOWER * s
        across = np.hypot(points[:, 0] - middle_x, points[:, 2] - middle_z) / (BUST_REACH * s)
        # Flat over the middle of the field and falling over its outer share.
        weight = _rise(1.0 - across, 0.0, BUST_EDGE) * facing
        out[:, 1] += BUST_FORWARD * s * weight
        out[:, 0] -= side * BUST_INWARD * s * weight
        # Out in every direction from a point behind the tip, but no closer to the middle.
        centre = np.array([middle_x, nipple[1] - BUST_DEPTH * s, middle_z])
        away = points - centre
        away /= np.maximum(np.linalg.norm(away, axis=1), 1e-12)[:, None]
        inward = side * away[:, 0] < 0.0
        away[inward, 0] *= np.clip(np.abs(points[inward, 0]) / (0.05 * s), 0.0, 1.0)
        out += BUST_ROUND * s * weight[:, None] * away
    return out, nipple


def _shape(mesh, height, nipple=None):
    points = _points(mesh)
    shaped, nipple = shape_female(points, height, nipple)
    mesh.vertices.foreach_set("co", shaped.ravel())
    mesh.update()
    return nipple


def _similarity(source, target):
    """The turn, the scale, and the move that best lay the source points onto the target points,
    as a function of points."""
    source, target = np.asarray(source, float), np.asarray(target, float)
    a, b = source.mean(0), target.mean(0)
    u, stretch, vt = np.linalg.svd((source - a).T @ (target - b))
    flip = np.sign(np.linalg.det(vt.T @ u.T))
    turn = vt.T @ np.diag([1.0, 1.0, flip]) @ u.T
    size = (stretch * [1.0, 1.0, flip]).sum() / ((source - a) ** 2).sum()
    return lambda points: (np.asarray(points, float) - a) @ turn.T * size + b


def groin_smoothed(male, female, edges, crotch, scale, keep=GROIN_KEEP):
    """The male body's points with his groin in the female's smooth form. male and female are
    the points of one net, edges its edges, and crotch the lowest point of his centre line between
    the legs."""
    male, female = np.asarray(male, float), np.asarray(female, float)
    x, y, z = np.abs(male[:, 0]), male[:, 1], male[:, 2] - crotch[2]
    width, height, depth = GROIN_REACH
    patch = (x < width * scale) & (z < height * scale) & (z > -depth * scale) & (y > crotch[1])
    core = patch & (x < GROIN_CORE[0] * scale) & (z < GROIN_CORE[1] * scale) & (z > 0.0) & (y > crotch[1] + 0.01 * scale)
    edges = np.asarray(edges, np.int64)
    lengths = np.linalg.norm(male[edges[:, 0]] - male[edges[:, 1]], axis=1)
    share = evened(len(male), edges, 1.0 / np.maximum(lengths, 1e-9), core | ~patch, core.astype(float))
    # Her patch laid onto his by its edge: the patch's vertices that take little of her form.
    edge = patch & (share < 0.25)
    laid = _similarity(female[edge], male[edge])(female[patch])
    out = male.copy()
    out[patch] += ((1.0 - keep) * share[patch])[:, None] * (laid - male[patch])
    # The centre line stays on the mirror plane.
    out[np.abs(male[:, 0]) < 1e-6, 0] = 0.0
    return out


def _grown_about_line(points, start, end, amount, along, reach, mask):
    """How far each point moves when the limb from start to end grows about its line: by amount
    of its distance from the line where it is greatest along the limb, fading out at the ends of
    along and beyond reach from the line. The points are on the right side; mask weighs each."""
    axis = end - start
    length = float(np.linalg.norm(axis))
    axis = axis / length
    t = (points - start) @ axis / length
    radial = points - start - np.outer(t * length, axis)
    distance = np.linalg.norm(radial, axis=1)
    near = np.clip((1.3 * reach - distance) / (0.3 * reach), 0.0, 1.0)
    weight = amount * _bump(t, *along) * near * near * (3.0 - 2.0 * near) * mask
    return radial * weight[:, None]


def fuller(points, amounts, joint, elbow, wrist, hip, knee, ankle, gap, centre_y, waist_z, armpit_z, notch_z, scale):
    """The points of a body grown a little fuller: each limb about its line, the round of the
    shoulder about the joint, and the chest and the neck about the trunk's middle. The arms' and
    the legs' joints are the right side's; the left side grows as its mirror image. gap(z) is
    where the arm parts from the trunk at a height and centre_y(z) the trunk's middle front to back."""
    points = np.asarray(points, float)
    side = np.where(points[:, 0] >= 0.0, 1.0, -1.0)
    right = points * np.column_stack([side, np.ones_like(side), np.ones_like(side)])
    x, z = right[:, 0], right[:, 2]
    # The arms: what lies beyond the gap between the arm and the trunk.
    arm = np.clip((x - gap(z) + 0.01 * scale) / (0.02 * scale), 0.0, 1.0)
    # The legs: what lies off the centre line; each limb's own reach keeps the growth on it.
    leg = np.clip(x / (0.03 * scale), 0.0, 1.0)
    move = np.zeros_like(right)
    for name, start, end, mask in (("upper_arm", joint, elbow, arm), ("forearm", elbow, wrist, arm),
                                   ("thigh", hip, knee, leg), ("calf", knee, ankle, leg)):
        if amounts[name]:
            move += _grown_about_line(right, start, end, amounts[name], FULLER_ALONG[name], FULLER_REACH[name] * scale, mask)
    if amounts["shoulder"]:
        offset = right - joint
        distance = np.linalg.norm(offset, axis=1)
        round_ = np.clip(1.0 - (distance / (SHOULDER_REACH * scale)) ** 2, 0.0, 1.0) ** 2
        outside = np.clip((x - gap(z) + 0.04 * scale) / (0.04 * scale), 0.0, 1.0)
        move += offset * (amounts["shoulder"] * round_ * outside)[:, None]
    middle = np.column_stack([np.zeros_like(x), centre_y(z), z])
    across = right - middle
    if amounts["chest"]:
        trunk = 1.0 - arm
        height = _bump(z, waist_z, waist_z + 0.75 * (armpit_z - waist_z), armpit_z)
        move += across * (amounts["chest"] * height * trunk)[:, None] * [1.0, 1.0, 0.0]
    if amounts["neck"]:
        near = np.clip((0.09 * scale - np.linalg.norm(across[:, :2], axis=1)) / (0.03 * scale), 0.0, 1.0)
        height = _bump(z, notch_z, notch_z + 0.4 * NECK_TOP * scale, notch_z + NECK_TOP * scale)
        move += across * (amounts["neck"] * height * near)[:, None] * [1.0, 1.0, 0.0]
    out = right + move
    out[:, 0] *= side
    # The centre line stays on the mirror plane.
    out[np.abs(points[:, 0]) < 1e-6, 0] = 0.0
    return out


def _crotch(points, height):
    """The lowest point of the centre line between the legs."""
    middle = points[(np.abs(points[:, 0]) < 0.004) & (points[:, 2] > 0.4 * height) & (points[:, 2] < 0.6 * height)]
    return middle[int(np.argmin(middle[:, 2]))]


def _edges(mesh):
    edges = np.empty(len(mesh.edges) * 2, np.int64)
    mesh.edges.foreach_get("vertices", edges)
    return edges.reshape(-1, 2)


def _fill_out(sculpt, base, label):
    """Grows a body's reference and its control cage a little fuller, by the measures of its
    reference."""
    points = _points(sculpt)
    m = body.Measures(Surface(points, _triangles(sculpt)), sculpt.get("shoulder_joint"))
    s = m.scale
    joint, elbow, wrist, _surface = body._arm_axis(m)
    hip, knee, ankle = body.leg_axis(m)
    heights = np.arange(m.leg_split + 0.03 * s, m.notch_z + NECK_TOP * s + 0.02 * s, 0.005)
    middles = [(z, loop.centre[1]) for z, loop in ((z, m._central(z)) for z in heights) if loop is not None]
    zs, ys = np.array(middles).T

    def centre_y(z):
        return np.interp(z, zs, ys)

    waist_z = min((m._central(z).length, z) for z in np.arange(0.58, 0.66, 0.005) * m.height if m._central(z) is not None)[1]
    for mesh in (sculpt, base):
        grown = fuller(_points(mesh), FULLER[label], joint, elbow, wrist, hip, knee, ankle, m.gap, centre_y, waist_z,
                       m.armpit_z, m.notch_z, s)
        mesh.vertices.foreach_set("co", grown.ravel())
        mesh.update()
    print(f"HUMAN_BUILD_REFERENCE {label}: grown fuller by "
          + ", ".join(f"{name.replace('_', ' ')} {share * 100:.0f} %" for name, share in FULLER[label].items() if share))


def _triangles(mesh):
    """Every face as triangles of its own vertices, cut the same way on every mesh of one net."""
    out = []
    for polygon in mesh.polygons:
        v = list(polygon.vertices)
        out.extend((v[0], v[k], v[k + 1]) for k in range(1, len(v) - 1))
    return np.array(out, np.int64)


def _shares(point, corners):
    """The shares of a triangle's three corners that make up a point in its plane."""
    a, b, c = corners
    solved = np.linalg.lstsq(np.stack([b - a, c - a], axis=1), point - a, rcond=None)[0]
    return np.array([1.0 - solved[0] - solved[1], solved[0], solved[1]])


def _along_skin(nearest, start, heading, length, step=HEAD_CHIN_STEP):
    """The point of the skin a length onward from a point of it, along the skin in the mirror plane
    x = 0, and the way the skin runs there. nearest(point) is the nearest point of the skin."""
    point, heading = np.array(start, float), np.array(heading, float)
    point[0] = heading[0] = 0.0
    heading /= np.linalg.norm(heading)
    left = float(length)
    for _ in range(int(length / step) * 8 + 16):
        # The skin's points are single precision: a shorter rest than this is no step.
        if left <= 1e-6:
            break
        onward = np.array(nearest(point + heading * min(step, left)), float)
        onward[0] = 0.0
        gone = float(np.linalg.norm(onward - point))
        if gone < 1e-7:
            raise RuntimeError(f"the way along the skin of the mirror plane ends at {np.round(point, 4).tolist()}")
        # A full step says which way the skin runs; the short last one says it too coarsely.
        if gone > step / 4:
            heading = (onward - point) / gone
        point, left = onward, left - gone
    return point, heading


def lay_head_cage(cage, bodies, first):
    """The head cage's vertices on every body's head: {label: points}. bodies maps a label to the
    points of that body's reference and shares the triangles of the one net; first is the label
    of the body whose head the cage is."""
    points, triangles = bodies[first]["points"], bodies[first]["triangles"]
    height = points[:, 2].max()
    head = triangles[points[triangles][:, :, 2].min(1) > 0.84 * height]
    tree = BVHTree.FromPolygons([tuple(p) for p in points], [tuple(int(v) for v in t) for t in head])
    top = points[points[:, 2] > 0.875 * height]
    placed = cage - [(cage[:, 0].min() + cage[:, 0].max()) / 2, 0.0, 0.0]
    placed *= (top[:, 0].max() - top[:, 0].min()) / (placed[:, 0].max() - placed[:, 0].min())
    placed += [0.0, top[:, 1].max() - placed[:, 1].max(), top[:, 2].max() - placed[:, 2].max()]
    low, high = placed[:, 2].min(), placed[:, 2].max()
    fitted = placed[:, 2] > low + HEAD_FIT_ABOVE * (high - low)
    for _ in range(HEAD_FIT_ROUNDS):
        source, goal = [], []
        for p in placed[fitted]:
            found = tree.find_nearest(Vector(p), HEAD_FIT_REACH)
            if found[0] is not None:
                source.append(p)
                goal.append(found[0])
        # Both sides of the head carry each pair, so the cage stays upright on the mirror plane.
        source, goal = np.array(source), np.array(goal)
        source = np.concatenate([source, source * (-1, 1, 1)])
        goal = np.concatenate([goal, goal * (-1, 1, 1)])
        centre_s, centre_g = source.mean(0), goal.mean(0)
        a, b = source - centre_s, goal - centre_g
        u, stretch, vt = np.linalg.svd(a.T @ b)
        flip = np.sign(np.linalg.det(vt.T @ u.T))
        turn = vt.T @ np.diag([1.0, 1.0, flip]) @ u.T
        size = (stretch * [1.0, 1.0, flip]).sum() / (a ** 2).sum()
        placed = (placed - centre_s) @ turn.T * size + centre_g
    def seen(location, normal):
        """Whether the skin at a point of the reference is seen from outside the head."""
        normal = np.array(normal)
        across = np.cross(normal, (0.0, 0.0, 1.0) if abs(normal[2]) < 0.9 else (1.0, 0.0, 0.0))
        across /= np.linalg.norm(across)
        other = np.cross(normal, across)
        tilt = math.radians(HEAD_SEEN_TILT)
        ways = [normal] + [normal * math.cos(tilt) + (across * math.cos(a) + other * math.sin(a)) * math.sin(tilt)
                           for a in np.arange(8) * math.pi / 4]
        start = np.array(location) + normal * 2e-4
        return any(tree.ray_cast(Vector(start), Vector(way), HEAD_SEEN_REACH)[0] is None for way in ways)

    landed, away, inside = [], [], 0
    for p in placed:
        location, normal, index, distance = tree.find_nearest(Vector(p))
        if not seen(location, normal):
            near = sorted(tree.find_nearest_range(Vector(p), HEAD_SKIN_NEAR), key=lambda found: found[3])
            for found in near:
                if seen(found[0], found[1]):
                    location, normal, index, distance = found
                    inside += 1
                    break
        landed.append((np.array(location), index))
        away.append(distance)
    # The centre line under the chin, onward along the skin from its last vertex under the chin.
    under, last = (landed[v][0] * (0.0, 1.0, 1.0) for v in HEAD_CHIN_LINE[:2])
    at, heading, walked = last, last - under, []
    for before, vertex in zip(HEAD_CHIN_LINE[1:], HEAD_CHIN_LINE[2:]):
        at, heading = _along_skin(lambda point: tree.find_nearest(Vector(point))[0], at, heading,
                                  np.linalg.norm(placed[vertex] - placed[before]))
        location, _normal, index, _distance = tree.find_nearest(Vector(at))
        walked.append(f"{vertex} {np.linalg.norm(np.array(location) - landed[vertex][0]) * 1000:.0f} mm")
        landed[vertex] = (np.array(location), index)
    def on_bodies(location, index):
        corners = head[index]
        shares = _shares(np.asarray(location, float), points[corners])
        return {label: shares @ body["points"][corners] for label, body in bodies.items()}

    out = {label: np.zeros_like(placed) for label in bodies}
    for v, (location, index) in enumerate(landed):
        for label, point in on_bodies(location, index).items():
            out[label][v] = point
    for laid in out.values():
        laid[np.abs(cage[:, 0] - (cage[:, 0].min() + cage[:, 0].max()) / 2) < 1e-4, 0] = 0.0
    away = np.array(away)[fitted]
    print(f"HUMAN_BUILD_REFERENCE head cage on {first}: median {np.median(away) * 1000:.2f} mm from the reference, "
          f"90 % {np.percentile(away, 90) * 1000:.2f} mm, max {away.max() * 1000:.2f} mm; {inside} vertices lay nearest to "
          f"skin inside the head and took the nearest skin seen from outside; the centre line under the chin was laid "
          f"along the skin, which moved vertex {' and vertex '.join(walked)}")
    return out


def _to_line(points, line):
    """How far each point lies from a closed line of points."""
    points, line = np.asarray(points, float), np.asarray(line, float)
    best = np.full(len(points), np.inf)
    for a, b in zip(line, np.roll(line, -1, axis=0)):
        along = b - a
        share = np.clip((points - a) @ along / max(float(along @ along), 1e-12), 0.0, 1.0)
        best = np.minimum(best, np.linalg.norm(points - (a + share[:, None] * along), axis=1))
    return best


def _rim(polys, faces):
    """The edge of a patch of faces as one line of vertices, running the way the faces do."""
    directed = {(a, b) for f in faces for a, b in zip(polys[f], polys[f][1:] + polys[f][:1])}
    onward = {}
    for a, b in directed:
        if (b, a) not in directed:
            if a in onward:
                raise RuntimeError("the ear's patch of the control cage has an edge that meets itself")
            onward[a] = b
    first = min(onward)
    line = [first]
    while onward[line[-1]] != first and len(line) <= len(onward):
        line.append(onward[line[-1]])
    if len(line) != len(onward):
        raise RuntimeError("the ear's patch of the control cage has more than one edge around it")
    return line


def ear_patch(outlines, bases, polys, margin=EAR_MARGIN):
    """The control cage's faces that take the place of the head cage's ear and the faces around
    it. outlines holds, for each body, the points of the outline of the hole they leave, and
    bases that body's control cage. Gives the faces and the vertices of their edge in order."""
    outlines = [np.asarray(outline, float) for outline in outlines]
    points = bases[0]
    # The faces are looked for beside the outline, a little further out than it reaches.
    low, high = outlines[0].min(0) - 0.02, outlines[0].max(0) + 0.02
    beside = (points[:, 0] > 0) & (points[:, 1] > low[1]) & (points[:, 1] < high[1]) & (points[:, 2] > low[2]) & (points[:, 2] < high[2])
    edge = set()
    by_edge = {}
    for f, p in enumerate(polys):
        if not all(beside[v] for v in p):
            edge.update(v for v in p if beside[v])
            continue
        for a, b in zip(p, p[1:] + p[:1]):
            by_edge.setdefault((a, b) if a < b else (b, a), []).append(f)

    def clear(f):
        """Whether a face keeps the margin to the outline on every body: its corners, the middles
        of its edges, and its own middle."""
        for outline, base in zip(outlines, bases):
            corners = base[list(polys[f])]
            samples = np.concatenate([corners, (corners + np.roll(corners, -1, axis=0)) / 2, corners.mean(0)[None]])
            if _to_line(samples, outline).min() <= margin:
                return False
        return True

    # From the face at the ear's outermost point, as far as faces keep clear of the outline.
    out_most = int(np.where(beside)[0][np.argmax(points[beside, 0])])
    seed = next(f for faces in by_edge.values() for f in faces if out_most in polys[f])
    ear, todo, known = {seed}, [seed], {seed: True}
    while todo:
        p = polys[todo.pop()]
        if edge & set(p):
            raise RuntimeError(f"the outline around the ear does not close the ear off from the head: the control cage's faces "
                               f"inside it reach {np.round(points[list(p)].mean(0), 4).tolist()}")
        for a, b in zip(p, p[1:] + p[:1]):
            for g in by_edge[(a, b) if a < b else (b, a)]:
                if g not in known:
                    known[g] = clear(g)
                    if known[g]:
                        ear.add(g)
                        todo.append(g)
    # A face that hangs on the others by one edge alone would stick out of the patch's edge.
    while True:
        directed = {(a, b) for f in ear for a, b in zip(polys[f], polys[f][1:] + polys[f][:1])}
        loose = {f for f in ear if sum((b, a) in directed for a, b in zip(polys[f], polys[f][1:] + polys[f][:1])) < 2}
        if not loose:
            break
        ear -= loose
    ear = sorted(ear)
    return ear, _rim(polys, ear)


def without_tiny(faces, bodies, least=EAR_LEAST):
    """The faces of a patch, by their vertices, without a face smaller than the least area on any
    body: the smallest such face loses its shortest edge, whose second vertex becomes its first
    in every face, until none is left. bodies holds each body's points."""
    faces = [tuple(face) for face in faces]

    def area(face):
        return min(float(np.linalg.norm(np.cross(points[list(face)], np.roll(points[list(face)], -1, axis=0)).sum(0))) / 2
                   for points in bodies)

    while True:
        smallest = min(faces, key=area)
        if area(smallest) >= least:
            return faces
        keep, drop = min(zip(smallest, smallest[1:] + smallest[:1]),
                         key=lambda edge: max(float(np.linalg.norm(points[edge[0]] - points[edge[1]])) for points in bodies))
        merged = []
        for face in faces:
            corners = [keep if v == drop else v for v in face]
            corners = [v for k, v in enumerate(corners) if v != corners[k - 1]]
            if len(corners) >= 3:
                merged.append(tuple(corners))
        faces = merged


def _thin(a, b, c):
    """How thin a triangle is: 1 for one of three equal sides, more the thinner it is."""
    sides = float(((a - b) ** 2).sum() + ((b - c) ** 2).sum() + ((c - a) ** 2).sum())
    return sides / max(2.0 * math.sqrt(3.0) * float(np.linalg.norm(np.cross(b - a, c - a))), 1e-12)


def band(outers, inners, outward):
    """The triangles that close the band between two lines of points that run the same way round,
    the outer and the inner one, as triples of ("outer" or "inner", place in that line), each
    counter-clockwise seen from outside. outers and inners hold each body's points of the two
    lines, and outward the way the skin looks at each of the first body's inner points. Gives the
    triangles and how thin the thinnest of them is on any body."""
    n, m = len(outers[0]), len(inners[0])
    # What it costs to step on along the outer line, with the triangle of outer i, outer i + 1 and
    # inner j, and along the inner line, with the triangle of inner j + 1, inner j and outer i.
    step_outer, step_inner = np.zeros((n, m)), np.zeros((n, m))
    worst_outer, worst_inner = np.zeros((n, m)), np.zeros((n, m))
    for i in range(n):
        for j in range(m):
            for k, (outer, inner) in enumerate(zip(outers, inners)):
                a, b, c = outer[i], outer[(i + 1) % n], inner[j]
                d, e, f = inner[(j + 1) % m], inner[j], outer[i]
                thin = (_thin(a, b, c), _thin(d, e, f))
                if k == 0:
                    if np.cross(b - a, c - a) @ outward[j] <= 0:
                        step_outer[i, j] += BAND_TURNED
                    if np.cross(e - d, f - d) @ (outward[j] + outward[(j + 1) % m]) <= 0:
                        step_inner[i, j] += BAND_TURNED
                step_outer[i, j] += thin[0]
                step_inner[i, j] += thin[1]
                worst_outer[i, j] = max(worst_outer[i, j], thin[0])
                worst_inner[i, j] = max(worst_inner[i, j], thin[1])
    best = None
    for start in range(m):
        cost = np.full((n + 1, m + 1), np.inf)
        along_outer = np.zeros((n + 1, m + 1), bool)
        cost[0, 0] = 0.0
        for i in range(n + 1):
            for j in range(m + 1):
                at = (start + j) % m
                if i < n and cost[i, j] + step_outer[i, at] < cost[i + 1, j]:
                    cost[i + 1, j] = cost[i, j] + step_outer[i, at]
                    along_outer[i + 1, j] = True
                if j < m and cost[i, j] + step_inner[i % n, at] < cost[i, j + 1]:
                    cost[i, j + 1] = cost[i, j] + step_inner[i % n, at]
                    along_outer[i, j + 1] = False
        if best is None or cost[n, m] < best[0]:
            best = (float(cost[n, m]), start, along_outer)
    total, start, along_outer = best
    if total >= BAND_TURNED:
        raise RuntimeError("the band around the ear cannot be closed without a triangle that looks into the head")
    i, j, out, thinnest = n, m, [], 0.0
    while i or j:
        if along_outer[i, j]:
            i -= 1
            at = (start + j) % m
            out.append((("outer", i), ("outer", (i + 1) % n), ("inner", at)))
            thinnest = max(thinnest, worst_outer[i, at])
        else:
            j -= 1
            at = (start + j) % m
            out.append((("inner", (at + 1) % m), ("inner", at), ("outer", i % n)))
            thinnest = max(thinnest, worst_inner[i % n, at])
    return out[::-1], float(thinnest)


def build(out_path):
    made = []
    for label, name, height in BODIES:
        source = bpy.data.objects[name]
        sculpt = _evaluated(source, SCULPT_LEVEL)
        placement = _placement(_points(sculpt), height)
        sculpt.transform(placement)
        # The arms are turned out in the bundle's own net, and the body is taken again.
        joint, angle = pose_arms(source, placement, sculpt, label)
        hanging = _points(sculpt)
        bpy.data.meshes.remove(sculpt)
        sculpt = _evaluated(source, SCULPT_LEVEL)
        sculpt.transform(placement)
        # The stretched skin of the passage from the trunk to the arm is evened out.
        edges = np.empty(len(sculpt.edges) * 2, np.int64)
        sculpt.edges.foreach_get("vertices", edges)
        sculpt.vertices.foreach_set("co", passage_evened(hanging, _points(sculpt), edges.reshape(-1, 2), joint, angle).ravel())
        sculpt.update()
        # The build measures the arm from the joint it was turned about.
        sculpt["shoulder_joint"] = [float(c) for c in joint]
        base = _evaluated(source, 0)
        base.transform(placement)
        eyes = []
        for child in source.children:
            if child.type == "MESH" and ".eye." in child.name:
                eye = _evaluated(child, 0)
                eye.transform(placement)
                eyes.append(("L" if child.name.endswith(".L") else "R", eye))
        made.append((label, sculpt, base, eyes))

    # The male's groin takes the female's smooth form, from her body as the bundle has it.
    heights = {label: height for label, _name, height in BODIES}
    meshes = {label: (sculpt, base) for label, sculpt, base, _eyes in made}
    crotch = _crotch(_points(meshes["Male"][0]), heights["Male"])
    for his, hers in zip(meshes["Male"], meshes["Female"]):
        smoothed = groin_smoothed(_points(his), _points(hers), _edges(his), crotch, heights["Male"] / 1.79)
        his.vertices.foreach_set("co", smoothed.ravel())
        his.update()
    for label, sculpt, base, _eyes in made:
        height = heights[label]
        if label == "Female":
            nipple = _shape(sculpt, height)
            _shape(base, height, nipple)
        _fill_out(sculpt, base, label)
        _open_armpit(sculpt, base, label)

    # The head cage of the bundle, a coarse cage of quads with loops around the eyes and the mouth,
    # turned to face +Y where it lies, and laid onto every body's head.
    heads = []
    head = bpy.data.objects[HEAD]
    turn = Matrix.Rotation(math.pi, 4, "Z")
    blocks = head.data.shape_keys.key_blocks
    cage = np.array([tuple(turn @ (head.matrix_world @ blocks[HEAD_SHAPE].data[i].co))
                     for i in range(len(head.data.vertices))])
    bodies = {label: {"points": _points(sculpt), "triangles": _triangles(sculpt)} for label, sculpt, _b, _e in made}
    cage_faces = [tuple(p.vertices) for p in head.data.polygons]
    laid = lay_head_cage(cage, bodies, HEAD_BODY)
    # The head cage's ear and the faces that touch the ring it stands on leave the cage, and with
    # them the faces at the vertices of that hole's outline that lie too near the ring.
    ring = outline([cage_faces[index] for index in EAR_FACES])
    cut_faces = set(EAR_FACES) | {index for index, face in enumerate(cage_faces) if set(ring) & set(face)}
    hole = outline([cage_faces[index] for index in sorted(cut_faces)])
    near = _to_line(laid[HEAD_BODY][hole], laid[HEAD_BODY][ring])
    tight = {v for v, away in zip(hole, near) if away < EAR_ROOM}
    cut_faces = sorted(cut_faces | {index for index, face in enumerate(cage_faces) if tight & set(face)})
    hole = outline([cage_faces[index] for index in cut_faces])
    # The outline of the hole as the subdivided cage has it: the cage's vertices around the hole
    # and, between each two of them, the middle of their edge on the skin.
    labels = [HEAD_BODY] + [label for label in bodies if label != HEAD_BODY]
    outlines = []
    for label in labels:
        points, triangles = bodies[label]["points"], bodies[label]["triangles"]
        skin = triangles[points[triangles][:, :, 2].min(1) > 0.84 * points[:, 2].max()]
        tree = BVHTree.FromPolygons([tuple(p) for p in points], [tuple(int(v) for v in t) for t in skin])
        corners = laid[label][hole]
        middles = [np.array(tree.find_nearest(Vector((a + b) / 2))[0]) for a, b in zip(corners, np.roll(corners, -1, axis=0))]
        outlines.append(np.array([p for pair in zip(corners, middles) for p in pair]))
    # The control cage's faces inside it, and the triangles of the band between the two. The two
    # bodies' control cages are one net, so the same faces and triangles serve both.
    bases = {label: base for label, _sculpt, base, _eyes in made}
    polys = [tuple(p.vertices) for p in bases[HEAD_BODY].polygons]
    control = [_points(bases[label]) for label in labels]
    patch, _edge = ear_patch(outlines, control, polys)
    # From here on the faces by their vertices: closing a too small face's edge changes them.
    faces = without_tiny([polys[f] for f in patch], control)
    rim = _rim(faces, range(len(faces)))
    first = control[0]
    outward = np.zeros((len(first), 3))
    for face in faces:
        corners = first[list(face)]
        outward[list(face)] += np.cross(corners - np.roll(corners, 1, axis=0), np.roll(corners, -1, axis=0) - corners).sum(0)
    triangles, thinnest = band(outlines, [points[rim] for points in control], outward[rim])
    number = {"outer": lambda k: int(k), "inner": lambda k: -1 - int(k)}
    used = sorted({v for face in faces for v in face})
    place = {v: k for k, v in enumerate(used)}
    for label, base in bases.items():
        mesh = bpy.data.meshes.new(f"HeadEar_{label}")
        mesh.from_pydata([tuple(p) for p in _points(base)[used]], [], [tuple(place[v] for v in face) for face in faces])
        mesh.update()
        mesh["cut"] = [int(index) for index in cut_faces]
        mesh["hole"] = [int(v) for v in hole]
        mesh["rim"] = [place[v] for v in rim]
        # Each triangle of the band as three numbers: a vertex of the hole's outline by its place
        # there, the cage's own at the even places and the middles of its edges at the odd ones,
        # or, below zero, a vertex of the patch's edge as -1 minus its place in that edge.
        mesh["band"] = [number[name](k) for triple in triangles for name, k in triple]
        heads.append(mesh)
    for label, points in laid.items():
        mesh = bpy.data.meshes.new(f"HeadCage_{label}")
        mesh.from_pydata([tuple(p) for p in points], [], cage_faces)
        mesh.update()
        heads.append(mesh)
    print(f"HUMAN_BUILD_REFERENCE ear: {len(faces)} faces of the control cage, {sum(len(face) - 2 for face in faces)} triangles, in place of "
          f"{len(cut_faces)} faces of the head cage; the band between the hole's outline of {len(outlines[0])} vertices and the patch's edge of "
          f"{len(rim)} is closed by {len(triangles)} triangles, the thinnest {thinnest:.1f} times as thin as one of equal sides")

    # A fresh file with only the reference meshes.
    keep = {id(mesh) for _label, sculpt, base, eyes in made for mesh in (sculpt, base, *[e for _s, e in eyes])}
    keep |= {id(mesh) for mesh in heads}
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)
    for collection in list(bpy.data.collections):
        bpy.data.collections.remove(collection)
    for mesh in list(bpy.data.meshes):
        if id(mesh) not in keep:
            bpy.data.meshes.remove(mesh)
    scene = bpy.context.scene
    reference = bpy.data.collections.new("Reference")
    scene.collection.children.link(reference)
    for label, sculpt, base, eyes in made:
        for mesh, prefix in ((sculpt, "Reference"), (base, "ReferenceBase")):
            mesh.name = f"{prefix}_{label}"
            for polygon in mesh.polygons:
                polygon.use_smooth = True
            reference.objects.link(bpy.data.objects.new(mesh.name, mesh))
        for side, eye in eyes:
            # The bundle's left is at its +X; turned to face +Y, sides are named by where they end up.
            side = "R" if _points(eye)[:, 0].mean() > 0 else "L"
            eye.name = f"Reference_{label}_Eye_{side}"
            reference.objects.link(bpy.data.objects.new(eye.name, eye))
        points = _points(sculpt)
        print(f"HUMAN_BUILD_REFERENCE {label}: {len(points)} vertices, height {points[:, 2].max():.4f} m, "
              f"width {points[:, 0].max() - points[:, 0].min():.4f} m, mirror offset {abs(points[:, 0].min() + points[:, 0].max()):.5f} m")
    for mesh in heads:
        reference.objects.link(bpy.data.objects.new(mesh.name, mesh))
        print(f"HUMAN_BUILD_REFERENCE {mesh.name}: {len(mesh.vertices)} vertices, {len(mesh.polygons)} faces")
    bpy.data.orphans_purge(do_local_ids=True, do_linked_ids=True, do_recursive=True)
    os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=out_path, compress=True, relative_remap=False)
    print(f"HUMAN_BUILD_SAVED {out_path}")


if __name__ == "__main__":
    build(sys.argv[sys.argv.index("--") + 1])
