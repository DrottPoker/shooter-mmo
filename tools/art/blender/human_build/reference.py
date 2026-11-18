"""The reference meshes of the human base bodies, taken from the Human Base Meshes bundle v1.4.1 of
Blender Studio (CC0): the realistic male and female bodies with their eyes, turned to face +Y,
centred on x = 0, standing on z = 0, and scaled to the heights of the game's bodies.

    blender --background --factory-startup --disable-autoexec <bundle.blend> \
        --python tools/art/blender/human_build/reference.py -- <out.blend>

The bundle is read as data only. The output holds, per body, Reference_<Label> (the sculpt at
SCULPT_LEVEL), ReferenceBase_<Label> (its control cage, for its edge flow), and the eyeballs."""

import math
import sys

import bpy
import numpy as np
from mathutils import Matrix

BUNDLE_SHA256 = "3c121505651140ceb4d69fd1d8923f7788ffadd81672f5be14845a5f2c75c137"
BODIES = (("Male", "GEO-body_male_realistic", 1.79), ("Female", "GEO-body_female_realistic", 1.70))
# The multires level of the reference surface: level 2 has about 170,000 vertices, enough for a net
# of 7,000 faces, and keeps the files small.
SCULPT_LEVEL = 2
HEAD = "Head - Generic Topology"
HEAD_SHAPES = (("Male", "Real_MALE"), ("Female", "Real_FEMALE"))
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


def build(out_path):
    made = []
    for label, name, height in BODIES:
        source = bpy.data.objects[name]
        sculpt = _evaluated(source, SCULPT_LEVEL)
        placement = _placement(_points(sculpt), height)
        sculpt.transform(placement)
        base = _evaluated(source, 0)
        base.transform(placement)
        eyes = []
        for child in source.children:
            if child.type == "MESH" and ".eye." in child.name:
                eye = _evaluated(child, 0)
                eye.transform(placement)
                eyes.append(("L" if child.name.endswith(".L") else "R", eye))
        made.append((label, sculpt, base, eyes))

    # The head cage of the bundle, a coarse cage of quads with loops around the eyes and the mouth,
    # in its male and its female shape, turned to face +Y where it lies.
    heads = []
    head = bpy.data.objects[HEAD]
    turn = Matrix.Rotation(math.pi, 4, "Z")
    for label, key in HEAD_SHAPES:
        mesh = bpy.data.meshes.new(f"HeadCage_{label}")
        blocks = head.data.shape_keys.key_blocks
        points = [turn @ (head.matrix_world @ blocks[key].data[i].co) for i in range(len(head.data.vertices))]
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
