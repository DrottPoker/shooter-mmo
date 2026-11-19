"""The reference meshes of the human base bodies, taken from the Human Base Meshes bundle v1.4.1 of
Blender Studio (CC0): the realistic male and female bodies with their eyes, turned to face +Y,
centred on x = 0, standing on z = 0, and scaled to the heights of the game's bodies.

    blender --background --factory-startup --disable-autoexec <bundle.blend> \
        --python tools/art/blender/human_build/reference.py -- <out.blend>

The bundle is read as data only. The output holds, per body, Reference_<Label> (the sculpt at
SCULPT_LEVEL), ReferenceBase_<Label> (its control cage, for its edge flow), the eyeballs, and
HeadCage_<Label>: the bundle's head cage laid onto that body's head.

The head cage is the bundle's coarse cage of the female body's head, so a move, a turn, and one
scale lay it onto her reference. The two bodies of the bundle are sculpted on one net, so a point
of her surface, as a place in a face of that net, is the same feature on his: each vertex of the
cage takes on him the place it has on her, and the loops around the eyes, the mouth, the nose, and
the ear lie on the same features of both."""

import math
import sys

import bpy
import numpy as np
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

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

    out = {label: np.zeros_like(placed) for label in bodies}
    away, inside = [], 0
    for v, p in enumerate(placed):
        location, normal, index, distance = tree.find_nearest(Vector(p))
        if not seen(location, normal):
            near = sorted(tree.find_nearest_range(Vector(p), HEAD_SKIN_NEAR), key=lambda found: found[3])
            for found in near:
                if seen(found[0], found[1]):
                    location, normal, index, distance = found
                    inside += 1
                    break
        corners = head[index]
        shares = _shares(np.array(location), points[corners])
        away.append(distance)
        for label, body in bodies.items():
            out[label][v] = shares @ body["points"][corners]
    for laid in out.values():
        laid[np.abs(cage[:, 0] - (cage[:, 0].min() + cage[:, 0].max()) / 2) < 1e-4, 0] = 0.0
    away = np.array(away)[fitted]
    print(f"HUMAN_BUILD_REFERENCE head cage on {first}: median {np.median(away) * 1000:.2f} mm from the reference, "
          f"90 % {np.percentile(away, 90) * 1000:.2f} mm, max {away.max() * 1000:.2f} mm; {inside} vertices lay nearest to "
          f"skin inside the head and took the nearest skin seen from outside")
    return out


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
    for label, points in lay_head_cage(cage, bodies, HEAD_BODY).items():
        mesh = bpy.data.meshes.new(f"HeadCage_{label}")
        mesh.from_pydata([tuple(p) for p in points], [], [tuple(p.vertices) for p in head.data.polygons])
        mesh.update()
        heads.append(mesh)

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
    bpy.ops.wm.save_as_mainfile(filepath=out_path, compress=True, relative_remap=False)
    print(f"HUMAN_BUILD_SAVED {out_path}")


if __name__ == "__main__":
    build(sys.argv[sys.argv.index("--") + 1])
