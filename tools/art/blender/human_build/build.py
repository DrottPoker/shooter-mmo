"""Builds the net of a human body from its reference and writes it into a work file for review.

    blender --background --factory-startup --disable-autoexec <reference.blend> \
        --python tools/art/blender/human_build/build.py -- <Male|Female> <out.blend> [parts]

The work file holds the reference, the coarse cage of the right half (Cage_<Label>), and the fitted
net of the whole body (Net_<Label>) with the face attribute "zone" and, per vertex, its distance
from the reference in the attribute "distance"."""

import os
import sys
import time

import bpy
import numpy as np

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from human_build import body, foot as feet, hand as hands, head as heads  # noqa: E402
from human_build.net import Net  # noqa: E402
from human_build.surface import Surface  # noqa: E402


def _object(name, net, collection, distances=None):
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata([tuple(p) for p in net.points], [], [tuple(f) for f in net.faces])
    mesh.update()
    zone = mesh.attributes.new("zone", "INT", "FACE")
    zone.data.foreach_set("value", np.array(net.zones, np.int32))
    if distances is not None:
        layer = mesh.attributes.new("distance", "FLOAT", "POINT")
        layer.data.foreach_set("value", np.asarray(distances, np.float32))
    for polygon in mesh.polygons:
        polygon.use_smooth = True
    old = bpy.data.objects.get(name)
    if old is not None:
        bpy.data.objects.remove(old, do_unlink=True)
    obj = bpy.data.objects.new(name, mesh)
    collection.objects.link(obj)
    return obj


def build(label, out_path, parts):
    started = time.time()
    surface = Surface.of_object(bpy.data.objects[f"Reference_{label}"])
    measures = body.Measures(surface)
    print(f"HUMAN_BUILD {label}: height {measures.height:.3f}, legs part at {measures.leg_split:.3f}, armpit "
          f"{measures.armpit_z:.3f}, gap x {measures.gap_x:.3f}, shoulder top {measures.shoulder_top:.3f}")
    net = Net()
    body.build_trunk(net, measures)
    hand = hands.Hand(measures) if "hand" in parts else None
    if hand is not None:
        for line in hand.report():
            print("HUMAN_BUILD", line)
    if "arm" in parts:
        _rings, joints = body.build_arm(net, measures, wrist_start=None if hand is None else hand.palmar)
        print("HUMAN_BUILD arm joints", [np.round(j, 3).tolist() for j in joints])
    if hand is not None:
        hands.build_hand(net, hand)
    if "neck" in parts:
        body.build_neck(net, measures)
    head = None
    if "head" in parts and "neck" in parts:
        cage = bpy.data.objects[f"HeadCage_{label}"]
        cage_points = [tuple(cage.matrix_world @ v.co) for v in cage.data.vertices]
        cage_faces = [tuple(p.vertices) for p in cage.data.polygons]
        eyes, radius = {}, 0.0
        for side in ("R", "L"):
            eye = bpy.data.objects[f"Reference_{label}_Eye_{side}"]
            co = np.array([eye.matrix_world @ v.co for v in eye.data.vertices])
            eyes[side] = co.mean(0)
            radius = float((co.max(0) - co.min(0)).max() / 2)
        head = heads.Head(measures, cage_points, cage_faces, eyes, radius)
        print(f"HUMAN_BUILD head cage scaled {head.size:.3f}, mouth half width {head.half_mouth * 1000:.0f} mm")
        for line in head.face.report():
            print("HUMAN_BUILD", line)
        heads.build_head(net, head)
    foot = None
    if "leg" in parts:
        _rings, joints = body.build_legs(net, measures)
        print("HUMAN_BUILD leg joints", [np.round(j, 3).tolist() for j in joints])
        if "foot" in parts:
            foot = feet.Foot(measures, joints[2], joints[2] - joints[1])
            for line in foot.report():
                print("HUMAN_BUILD", line)
            feet.build_foot(net, foot)
    problems = net.check()
    for problem in problems:
        print("HUMAN_BUILD_FAILED", problem)
    fine = net.subdivided()
    if head is not None:
        heads.refine_head(fine, net, head)
    skins = dict(measures.skins())
    if hand is not None:
        skins.update(hand.skins())
    if foot is not None:
        skins.update(foot.skins())
    if head is not None:
        skins.update(head.skins())
    fine.fit(skins, rounds=10)
    whole = fine.mirrored()
    points = whole.array()
    distances = np.array([(surface.nearest(p) or (None, None, 9.0))[2] for p in points])
    collection = bpy.data.collections.get("Build") or bpy.data.collections.new("Build")
    if collection.name not in bpy.context.scene.collection.children:
        bpy.context.scene.collection.children.link(collection)
    _object(f"Cage_{label}", net, collection)
    _object(f"Net_{label}", whole, collection, distances)
    triangles = 2 * len(whole.faces)
    print(f"HUMAN_BUILD {label}: cage {len(net.faces)} faces, net {len(whole.points)} vertices, {len(whole.faces)} faces, "
          f"{triangles} triangles, distance median {np.median(distances) * 1000:.2f} mm, 95 % {np.percentile(distances, 95) * 1000:.2f} mm, "
          f"max {distances.max() * 1000:.2f} mm, {time.time() - started:.0f} s")
    area, smallest, largest, fold, centres = whole.quality()
    edges = whole.edges()
    lengths = np.array([np.linalg.norm(points[a] - points[b]) for a, b in edges])
    print(f"HUMAN_BUILD {label}: edges median {np.median(lengths) * 1000:.1f} mm, 5 % {np.percentile(lengths, 5) * 1000:.1f} mm, "
          f"95 % {np.percentile(lengths, 95) * 1000:.1f} mm; faces with a corner under 35 degrees: {(smallest < 35).sum()}, "
          f"over 150: {(largest > 150).sum()}, folded: {(fold < 0.5).sum()}, under 20 mm2: {(area < 20e-6).sum()}")
    worst = np.argsort(fold)[:6]
    for index in worst:
        if fold[index] < 0.7 or smallest[index] < 35:
            print(f"HUMAN_BUILD   face at {np.round(centres[index], 3).tolist()}: fold {fold[index]:.2f}, corners "
                  f"{smallest[index]:.0f}..{largest[index]:.0f} degrees, area {area[index] * 1e6:.0f} mm2, zone {whole.zones[index]}")
    for index in np.argsort(smallest)[:6]:
        if smallest[index] < 35:
            print(f"HUMAN_BUILD   sharp face at {np.round(centres[index], 3).tolist()}: corners {smallest[index]:.0f}..{largest[index]:.0f}, "
                  f"area {area[index] * 1e6:.0f} mm2, zone {whole.zones[index]}")
    bpy.ops.wm.save_as_mainfile(filepath=out_path, compress=True, relative_remap=False)
    print(f"HUMAN_BUILD_SAVED {out_path}")


if __name__ == "__main__":
    arguments = sys.argv[sys.argv.index("--") + 1:]
    build(arguments[0], arguments[1], set(arguments[2].split(",")) if len(arguments) > 2 else {"arm", "neck", "leg", "hand", "foot", "head"})
