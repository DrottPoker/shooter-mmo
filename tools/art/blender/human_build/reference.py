"""The reference meshes of the human base bodies, taken from the Human Base Meshes bundle v1.4.1 of
Blender Studio (CC0): the realistic male and female bodies with their eyes, turned to face +Y,
centred on x = 0, standing on z = 0, and scaled to the heights of the game's bodies.

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

from human_build.head import EAR_FACES, outline  # noqa: E402

BUNDLE_SHA256 = "3c121505651140ceb4d69fd1d8923f7788ffadd81672f5be14845a5f2c75c137"
BODIES = (("Male", "GEO-body_male_realistic", 1.79), ("Female", "GEO-body_female_realistic", 1.70))
# The multires level of the reference surface: level 2 has about 170,000 vertices, enough for a net
# of 7,000 faces, and keeps the files small.
SCULPT_LEVEL = 2
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
# triangles is made for both bodies and serves both.
EAR_ROOM, EAR_MARGIN = 0.012, 0.006
BAND_TURNED = 1000.0
# The female figure, as the reviewer asked for it: clearly female, with wide hips, a narrow waist,
# and a larger bust that sits close together, without exaggeration. The bundle's female is slim and
# athletic, so her reference is shaped by smooth, mirrored fields before the net is laid onto it:
# the hips widen and the waist narrows by these shares, in bands at these shares of her height,
# and each breast swells forward by BUST_FORWARD and moves BUST_INWARD toward the middle, in a
# field that reaches BUST_REACH across the front of the chest about a point BUST_LOWER below and
# BUST_INSIDE inside the breast's tip. A swell keeps the fold under the breast as soft as the
# bundle's, where growing the breast about its root would deepen it. The field fades in between
# the two depths of BUST_FRONT behind the tip, so the back keeps its shape. Lengths are in metres
# on a body of 1.70 m. The head, the arms, and the legs below the knee keep the bundle's shape.
HIPS_WIDER, HIPS_AT, HIPS_BAND = 0.075, 0.515, (0.36, 0.61)
WAIST_NARROWER, WAIST_AT, WAIST_BAND = 0.065, 0.628, (0.565, 0.70)
BUST_FORWARD, BUST_INWARD, BUST_REACH = 0.020, 0.014, 0.105
BUST_LOWER, BUST_INSIDE = 0.010, 0.008
BUST_FRONT = (0.11, 0.05)
BUST_BETWEEN = (0.70, 0.775)
TRUNK_HALF_WIDTH = (0.235, 0.285)
# The body's hips lie this far in front of the origin, as on the bodies the game has, so the
# origin stays under the body's weight.
HIPS_FORWARD = 0.015
HIP_HEIGHT_SHARE = 0.52


def _evaluated(obj, level):
    for modifier in obj.modifiers:
        if modifier.type == "MULTIRES":
            modifier.levels = min(level, modifier.total_levels)
    bpy.context.view_layer.update()
    depsgraph = bpy.context.evaluated_depsgraph_get()
    mesh = bpy.data.meshes.new_from_object(obj.evaluated_get(depsgraph), depsgraph=depsgraph)
    mesh.transform(obj.matrix_world)
    return mesh


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
    facing = np.clip((far * s - (nipple[1] - points[:, 1])) / ((far - near) * s), 0.0, 1.0)
    facing = facing * facing * (3.0 - 2.0 * facing)
    for side in (1.0, -1.0):
        middle_x = side * (nipple[0] - BUST_INSIDE * s)
        middle_z = nipple[2] - BUST_LOWER * s
        across = np.hypot(points[:, 0] - middle_x, points[:, 2] - middle_z)
        weight = np.clip(1.0 - (across / (BUST_REACH * s)) ** 2, 0.0, 1.0) ** 2 * facing
        out[:, 1] += BUST_FORWARD * s * weight
        out[:, 0] -= side * BUST_INWARD * s * weight
    return out, nipple


def _shape(mesh, height, nipple=None):
    points = _points(mesh)
    shaped, nipple = shape_female(points, height, nipple)
    mesh.vertices.foreach_set("co", shaped.ravel())
    mesh.update()
    return nipple


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
        base = _evaluated(source, 0)
        base.transform(placement)
        if label == "Female":
            nipple = _shape(sculpt, height)
            _shape(base, height, nipple)
        eyes = []
        for child in source.children:
            if child.type == "MESH" and ".eye." in child.name:
                eye = _evaluated(child, 0)
                eye.transform(placement)
                eyes.append(("L" if child.name.endswith(".L") else "R", eye))
        made.append((label, sculpt, base, eyes))

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
    faces, rim = ear_patch(outlines, [_points(bases[label]) for label in labels], polys)
    first = _points(bases[HEAD_BODY])
    outward = np.zeros((len(first), 3))
    for f in faces:
        corners = first[list(polys[f])]
        outward[list(polys[f])] += np.cross(corners - np.roll(corners, 1, axis=0), np.roll(corners, -1, axis=0) - corners).sum(0)
    triangles, thinnest = band(outlines, [_points(bases[label])[rim] for label in labels], outward[rim])
    number = {"outer": lambda k: int(k), "inner": lambda k: -1 - int(k)}
    used = sorted({v for f in faces for v in polys[f]})
    place = {v: k for k, v in enumerate(used)}
    for label, base in bases.items():
        mesh = bpy.data.meshes.new(f"HeadEar_{label}")
        mesh.from_pydata([tuple(p) for p in _points(base)[used]], [], [tuple(place[v] for v in polys[f]) for f in faces])
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
    print(f"HUMAN_BUILD_REFERENCE ear: {len(faces)} faces of the control cage, {sum(len(polys[f]) - 2 for f in faces)} triangles, in place of "
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
