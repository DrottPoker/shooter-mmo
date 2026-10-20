"""Turns the textured base bodies into two separate characters: a collection per body type with
a root object (later the rig's root) and the body split into its zones (head, neck, torso,
pelvis, upper arms, forearms, hands, thighs, shins, feet) plus eyeballs, teeth and tongue as
children. Equipment can then hide whole zones, and hair, beards and clothes are modelled on
top. Each character is also written as its own art asset file.

The split keeps the full body's UVs, textures and shading: every part gets the full body's
corner normals as custom normals, so the parts meet without visible seams."""

import bmesh
import bpy
import numpy as np

from . import geometry, textures


ZONE_NAMES = {"head": "Head", "neck": "Neck", "torso": "Torso", "pelvis": "Pelvis", "upper_arms": "UpperArms",
              "forearms": "Forearms", "hands": "Hands", "thighs": "Thighs", "shins": "Shins", "feet": "Feet"}


def corner_normals(mesh):
    out = np.empty(len(mesh.loops) * 3, np.float32)
    mesh.corner_normals.foreach_get("vector", out)
    return out.reshape(-1, 3)


def drop_unused_materials(mesh):
    used = sorted({p.material_index for p in mesh.polygons})
    mats = [mesh.materials[i] for i in used]
    remap = {old: new for new, old in enumerate(used)}
    indices = [remap[p.material_index] for p in mesh.polygons]
    # Clearing the slots resets every face to slot 0, so the indices are set afterwards.
    mesh.materials.clear()
    for m in mats:
        mesh.materials.append(m)
    for p, i in zip(mesh.polygons, indices):
        p.material_index = i


def split_part(body, zone_index, name, normals, coll, root):
    """A copy of the body holding only the faces of one zone, with the body's normals."""
    mesh = body.data.copy()
    mesh.name = name
    zone = np.empty(len(mesh.polygons), np.int32)
    mesh.attributes["zone"].data.foreach_get("value", zone)
    first_loop = np.array([p.loop_start for p in mesh.polygons])
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bm.faces.ensure_lookup_table()
    origin = bm.faces.layers.int.new("orig_face")
    for f in bm.faces:
        f[origin] = f.index
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if zone[f.index] != zone_index], context="FACES")
    # Corner normals of the full body, face by face in the original corner order.
    custom = []
    for f in bm.faces:
        start = first_loop[f[origin]]
        custom += [normals[start + k] for k in range(len(f.loops))]
    bm.faces.layers.int.remove(origin)
    bm.to_mesh(mesh)
    bm.free()
    mesh.normals_split_custom_set([tuple(n) for n in custom])
    drop_unused_materials(mesh)
    obj = bpy.data.objects.new(name, mesh)
    coll.objects.link(obj)
    obj.parent = root
    obj["zone"] = ZONE_NAMES[geometry.ZONES[zone_index]]
    return obj


def organize(path, out):
    """Splits both textured bodies into their characters and saves them side by side."""
    bpy.ops.wm.open_mainfile(filepath=path)
    old = bpy.data.collections.get("Characters")
    for label in ("Male", "Female"):
        body = bpy.data.objects[f"{label}_Body"]
        coll = bpy.data.collections.new(label)
        bpy.context.scene.collection.children.link(coll)
        root = bpy.data.objects.new(label, None)
        root.empty_display_type = "PLAIN_AXES"
        root.empty_display_size = 0.25
        root.location = body.location
        coll.objects.link(root)
        for key in body.keys():
            root[key] = body[key]
        normals = corner_normals(body.data)
        zone = np.empty(len(body.data.polygons), np.int32)
        body.data.attributes["zone"].data.foreach_get("value", zone)
        for zi, zone_key in enumerate(geometry.ZONES):
            if (zone == zi).any():
                split_part(body, zi, f"{label}_{ZONE_NAMES[zone_key]}", normals, coll, root)
        bpy.context.view_layer.update()
        for o in [o for o in bpy.data.objects if o.get("body") == body.name]:
            world = o.matrix_world.copy()
            for c in list(o.users_collection):
                c.objects.unlink(o)
            coll.objects.link(o)
            o.parent = root
            o.matrix_world = world
            del o["body"]
        mesh = body.data
        bpy.data.objects.remove(body)
        bpy.data.meshes.remove(mesh)
    if old is not None and not old.objects:
        bpy.data.collections.remove(old)
    textures.material_preview_viewport()
    bpy.ops.wm.save_as_mainfile(filepath=out, compress=True)
    for label in ("Male", "Female"):
        root = bpy.data.objects[label]
        parts = sorted(c.name for c in root.children)
        tris = sum(sum(len(p.vertices) - 2 for p in c.data.polygons) for c in root.children)
        print(f"CHARACTER {label}: {len(parts)} parts, {tris} triangles: {', '.join(parts)}")
    print("SAVED", out)


LABELS = ("Male", "Female")


def write_asset(path, label, out):
    """Writes one character of the organized file as an art pipeline asset: its collection inside
    the Export collection, the root at the origin, and nothing of the other character."""
    bpy.ops.wm.open_mainfile(filepath=path)
    for other in (lbl for lbl in LABELS if lbl != label):
        coll = bpy.data.collections[other]
        for o in list(coll.objects):
            bpy.data.objects.remove(o)
        bpy.data.collections.remove(coll)
        # Images keep a fake user, so the other character's textures go by name.
        for image in [i for i in bpy.data.images if other in i.name.split("_")]:
            bpy.data.images.remove(image)
    coll = bpy.data.collections[label]
    export = bpy.data.collections.new("Export")
    scene = bpy.context.scene.collection
    scene.children.link(export)
    scene.children.unlink(coll)
    export.children.link(coll)
    bpy.data.objects[label].location = (0.0, 0.0, 0.0)
    bpy.data.orphans_purge(do_local_ids=True, do_linked_ids=True, do_recursive=True)
    textures.material_preview_viewport()
    # The textures are packed; their relative paths stay as they are, so the asset does not
    # record where the build ran.
    bpy.ops.wm.save_as_mainfile(filepath=out, compress=True, relative_remap=False)
    triangles = sum(len(p.vertices) - 2 for o in bpy.data.objects if o.type == "MESH" for p in o.data.polygons)
    print(f"ASSET {label}: {len(bpy.data.objects)} objects, {triangles} triangles, "
          f"{len(bpy.data.images)} images -> {out}")
