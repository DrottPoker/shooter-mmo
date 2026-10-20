"""Bakes per-texel data for both base bodies with Cycles, paints the body, face and eyeball
textures (paint), sets up the materials (body, face, eyes, teeth, tongue) with the textures
packed, and saves the file with the viewport in Material Preview."""

import os
import time

import bpy
import numpy as np
from mathutils import Euler
from mathutils.bvhtree import BVHTree

from . import head, paint, proportions, references


BODY_RES = 2048
FACE_RES = 1024
EYE_RES = 512
AO_SAMPLES = 48
FACE_VARIANTS = {"male": ("Stubble", "Scarred"), "female": ("Blush", "Freckles")}
POS_OFFSET = np.array((1.0, 1.0, 0.0))
POS_SCALE = 2.5
EMIT_KINDS = ("pos", "nrm", "part", "off", "loc")
# The detail normal bake casts from this far outside the body.
CAGE_EXTRUSION = 0.012
# Sculpt detail fades where the build moved vertices off the sculpt on purpose (the softened
# armpits), as the sculpt's folds no longer belong there ...
MOVED_FADE = (0.004, 0.010)
# ... and before the rays miss, where the sculpt lies further outside the surface than the cage.
MISS_FADE = (0.009, CAGE_EXTRUSION - 0.0005)
# Agreement (cosine) between the ray-baked sculpt normal and the sculpt's nearest face normal:
# a ray that went into a narrow fold and met its far wall paints a slit.
SAME_SURFACE = (0.55, 0.80)


def float_image(name, size):
    img = bpy.data.images.get(name)
    if img:
        bpy.data.images.remove(img)
    img = bpy.data.images.new(name, size, size, alpha=False, float_buffer=True)
    img.colorspace_settings.name = "Non-Color"
    return img


def pixels(img):
    w, h = img.size
    px = np.empty(w * h * 4, np.float32)
    img.pixels.foreach_get(px)
    return px.reshape(h, w, 4)[..., :3].astype(np.float64)


def bake_material(name, kind):
    """A temporary material whose emission is the requested data."""
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    emit = nt.nodes.new("ShaderNodeEmission")
    nt.links.new(emit.outputs["Emission"], out.inputs["Surface"])
    if kind in ("pos", "loc"):
        # pos: rest position (unwarped, for painting); loc: the warped surface in object space.
        if kind == "pos":
            src = nt.nodes.new("ShaderNodeAttribute")
            src.attribute_name = "rest_position"
            vector = src.outputs["Vector"]
        else:
            src = nt.nodes.new("ShaderNodeTexCoord")
            vector = src.outputs["Object"]
        add = nt.nodes.new("ShaderNodeVectorMath")
        add.operation = "ADD"
        add.inputs[1].default_value = tuple(POS_OFFSET)
        scale = nt.nodes.new("ShaderNodeVectorMath")
        scale.operation = "SCALE"
        scale.inputs["Scale"].default_value = 1.0 / POS_SCALE
        nt.links.new(vector, add.inputs[0])
        nt.links.new(add.outputs["Vector"], scale.inputs[0])
        nt.links.new(scale.outputs["Vector"], emit.inputs["Color"])
    elif kind == "nrm":
        geo = nt.nodes.new("ShaderNodeNewGeometry")
        mad = nt.nodes.new("ShaderNodeVectorMath")
        mad.operation = "MULTIPLY_ADD"
        mad.inputs[1].default_value = (0.5, 0.5, 0.5)
        mad.inputs[2].default_value = (0.5, 0.5, 0.5)
        nt.links.new(geo.outputs["Normal"], mad.inputs[0])
        nt.links.new(mad.outputs["Vector"], emit.inputs["Color"])
    elif kind == "part":
        attr = nt.nodes.new("ShaderNodeAttribute")
        attr.attribute_name = "part"
        div = nt.nodes.new("ShaderNodeMath")
        div.operation = "DIVIDE"
        div.inputs[1].default_value = 10.0
        nt.links.new(attr.outputs["Fac"], div.inputs[0])
        nt.links.new(div.outputs["Value"], emit.inputs["Color"])
    elif kind == "off":
        attr = nt.nodes.new("ShaderNodeAttribute")
        attr.attribute_name = "ref_offset"
        nt.links.new(attr.outputs["Fac"], emit.inputs["Color"])
    return mat


def target(mat, image):
    node = mat.node_tree.nodes.new("ShaderNodeTexImage")
    node.image = image
    for n in mat.node_tree.nodes:
        n.select = False
    node.select = True
    mat.node_tree.nodes.active = node


def bake(obj, kind, images, sources=()):
    """Bakes into images = (body image, face image) through two temporary materials."""
    scene = bpy.context.scene
    old = [s.material for s in obj.material_slots]
    mats = []
    for slot, img in zip(obj.material_slots, images):
        mat = bake_material(f"_bake_{kind}_{slot.slot_index}", kind if kind in EMIT_KINDS else "nrm")
        target(mat, img)
        slot.material = mat
        mats.append(mat)
    bpy.ops.object.select_all(action="DESELECT")
    for s in sources:
        s.select_set(True)
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bake_settings = scene.render.bake
    bake_settings.margin = 16
    # EXTEND: with mirrored islands ADJACENT_FACES pulls in data from unrelated places.
    bake_settings.margin_type = "EXTEND"
    if kind in EMIT_KINDS:
        scene.cycles.samples = 1
        bpy.ops.object.bake(type="EMIT", use_clear=True, use_selected_to_active=False)
    elif kind == "hi":
        scene.cycles.samples = 1
        bpy.ops.object.bake(type="NORMAL", normal_space="OBJECT", use_clear=True, use_selected_to_active=True,
                            cage_extrusion=CAGE_EXTRUSION, max_ray_distance=0.04)
    elif kind == "ao":
        scene.cycles.samples = AO_SAMPLES
        bpy.ops.object.bake(type="AO", use_clear=True, use_selected_to_active=False)
    for slot, mat in zip(obj.material_slots, old):
        slot.material = mat
    for m in mats:
        bpy.data.materials.remove(m)


def warped_reference(R_obj, sex, body_obj):
    mesh = R_obj.data.copy()
    co = np.empty(len(mesh.vertices) * 3, np.float64)
    mesh.vertices.foreach_get("co", co)
    co = co.reshape(-1, 3)
    surf = references.surface_of(R_obj)
    co = proportions.warp(co, sex, surf)
    mesh.vertices.foreach_set("co", co.astype(np.float32).ravel())
    mesh.update()
    obj = bpy.data.objects.new(f"_ref_{sex}", mesh)
    obj.location = body_obj.location
    bpy.context.scene.collection.objects.link(obj)
    for p in mesh.polygons:
        p.use_smooth = True
    return obj


def sculpt_tree(ref):
    bpy.context.view_layer.update()
    return BVHTree.FromObject(ref, bpy.context.evaluated_depsgraph_get())


def store_reference_offset(obj, tree):
    """Distance of every body vertex from the sculpt, as the point attribute ref_offset for
    baking. The cast vertices lie on the sculpt; the ones moved on purpose do not."""
    mesh = obj.data
    offset = [tree.find_nearest(v.co)[3] for v in mesh.vertices]
    mesh.attributes.new("ref_offset", "FLOAT", "POINT").data.foreach_set("value", offset)


def sculpt_distance(loc, tree, stride=2):
    """Signed distance from the body surface (a baked loc map) to the sculpt (negative where
    the surface lies inside it) and the sculpt's face normal at the nearest point, per texel:
    measured on every stride-th texel and repeated. Empty texels get 0 and a zero normal."""
    h, w, _ = loc.shape
    coarse = loc[::stride, ::stride]
    points = coarse * POS_SCALE - POS_OFFSET
    dist = np.zeros(coarse.shape[:2])
    normal = np.zeros(coarse.shape)
    nearest = tree.find_nearest
    for y, x in zip(*np.nonzero(coarse.sum(-1) > 0)):
        p = points[y, x]
        at, n, _i, d = nearest(p)
        dist[y, x] = d if np.dot(p - np.array(at), np.array(n)) > 0 else -d
        normal[y, x] = n

    def full(a):
        return np.repeat(np.repeat(a, stride, 0), stride, 1)[:h, :w]
    return full(dist), full(normal)


def soften_weight(w, r=2):
    """Erodes, then blurs a per-texel weight over (2r + 1)^2 texels: single texels that kept
    detail inside a faded area drop out, and fade edges turn soft instead of following texels.
    Islands lie further apart than r, so no island takes weight from another."""
    h, wd = w.shape
    k = 2 * r + 1
    p = np.pad(w, r, mode="edge")
    low = p[r:r + h, r:r + wd].copy()
    for dy in range(k):
        for dx in range(k):
            np.minimum(low, p[dy:dy + h, dx:dx + wd], out=low)
    p = np.pad(low, r, mode="edge")
    total = np.zeros_like(low)
    for dy in range(k):
        for dx in range(k):
            total += p[dy:dy + h, dx:dx + wd]
    return total / (k * k)


def landmarks(obj):
    lm = {}
    for key in obj.keys():
        if key.startswith("lm_"):
            lm[key[3:]] = np.array(obj[key], np.float64).reshape(-1, 3)
    mesh = obj.data
    rest = np.empty(len(mesh.vertices) * 3, np.float32)
    mesh.attributes["rest_position"].data.foreach_get("vector", rest)
    rest = rest.reshape(-1, 3).astype(np.float64)
    part = np.empty(len(mesh.polygons), np.int32)
    mesh.attributes["part"].data.foreach_get("value", part)
    sex = "male" if obj.name.startswith("Male") else "female"
    lm["eye"] = lm["eye_rim"]
    eyes = [np.array(obj[f"eye_{k}"], np.float64) for k in range(2)]
    right = max(eyes, key=lambda e: e[0])
    lm["eyeball"] = (right[:3], float(right[3]))
    lm["eye_opening"] = head.EYE_OPENING[sex]
    lm["mouth_front_y"] = float(obj["mouth"][4])
    w = lm["wrist"]
    lm["wrist_centres"] = [w.mean(0), w.mean(0) * np.array((-1.0, 1.0, 1.0))]
    torso_verts = {v for p in mesh.polygons if part[p.index] == paint.PARTS.index("torso") for v in p.vertices}
    tv = rest[sorted(torso_verts)]
    chest = tv[(tv[:, 0] > 0.04) & (tv[:, 0] < 0.15) & (tv[:, 2] > 0.98) & (tv[:, 2] < 1.30)]
    lm["chest_apex"] = chest[np.argmax(chest[:, 1])]
    leg_verts = {v for p in mesh.polygons if part[p.index] == paint.PARTS.index("leg") for v in p.vertices}
    lv = rest[sorted(leg_verts)]
    knee = lv[(lv[:, 2] > 0.40) & (lv[:, 2] < 0.55) & (lv[:, 0] > 0)]
    lm["knee_z"] = knee[np.argmax(knee[:, 1])][2]
    lm["knee_front_y"] = knee[:, 1].max() - 0.04
    neck = rest[sorted({v for p in mesh.polygons if part[p.index] == paint.PARTS.index("neck") for v in p.vertices})]
    lm["neck_base_z"] = neck[:, 2].min()
    # Border of the face region, sampled every 2 mm, for fading face paint before the border.
    face = {p.index for p in mesh.polygons if p.material_index == 1}
    edge_faces = {}
    for p in mesh.polygons:
        for key in p.edge_keys:
            edge_faces.setdefault(key, []).append(p.index)
    pts = []
    for (a, b), fs in edge_faces.items():
        if sum(f in face for f in fs) == 1:
            pa, pb = rest[a], rest[b]
            n = max(int(np.linalg.norm(pb - pa) / 0.002), 1)
            pts += [pa + (pb - pa) * k / n for k in range(n + 1)]
    lm["face_border"] = np.array(pts)
    return lm


def save_png(name, rgb, path):
    h, w, _ = rgb.shape
    img = bpy.data.images.get(name)
    if img:
        bpy.data.images.remove(img)
    img = bpy.data.images.new(name, w, h, alpha=False)
    rgba = np.concatenate([rgb, np.ones((h, w, 1))], -1).astype(np.float32)
    img.pixels.foreach_set(rgba.ravel())
    # A path relative to the open file, so no file of the build records the folder it ran in.
    relative = bpy.path.relpath(path)
    img.filepath_raw = relative
    img.file_format = "PNG"
    img.save()
    bpy.data.images.remove(img)
    img = bpy.data.images.load(relative)
    img.name = name
    # Packing from memory keeps the relative path; packing from the file records its full path.
    with open(path, "rb") as f:
        png = f.read()
    img.pack(data=png, data_len=len(png))
    img.use_fake_user = True
    return img


def textured_material(mat, image, roughness):
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    tex = nt.nodes.new("ShaderNodeTexImage")
    tex.image = image
    tex.location = (-400, 0)
    bsdf.location = (-100, 0)
    out.location = (250, 0)
    nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
    bsdf.inputs["Roughness"].default_value = roughness
    bsdf.inputs["Specular IOR Level"].default_value = 0.3
    return tex


def flat_material(mat, srgb, roughness):
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    out.location = (250, 0)
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
    lin = [c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4 for c in srgb]
    bsdf.inputs["Base Color"].default_value = (*lin, 1.0)
    bsdf.inputs["Roughness"].default_value = roughness
    mat.diffuse_color = (*lin, 1.0)


def material_preview_viewport():
    for screen in bpy.data.screens:
        for area in screen.areas:
            if area.type == "VIEW_3D":
                space = area.spaces[0]
                space.shading.type = "MATERIAL"
                r3d = space.region_3d
                if r3d is not None:
                    r3d.view_location = (0.0, 0.0, 0.95)
                    r3d.view_distance = 3.4
                    r3d.view_rotation = Euler((1.48, 0.0, 3.14159)).to_quaternion()


def _bilinear(img, u, v):
    h, w, _ = img.shape
    x, y = u * w - 0.5, v * h - 0.5
    x0, y0 = int(np.floor(x)), int(np.floor(y))
    fx, fy = x - x0, y - y0
    xa, xb = np.clip([x0, x0 + 1], 0, w - 1)
    ya, yb = np.clip([y0, y0 + 1], 0, h - 1)
    return (img[ya, xa] * (1 - fx) + img[ya, xb] * fx) * (1 - fy) + (img[yb, xa] * (1 - fx) + img[yb, xb] * fx) * fy


def stitch_seams(obj, body, face, radius=4.0):
    """Makes the painted colours meet exactly along every UV seam. Body to body seams move
    both sides to their average; the border between face and body only moves the face
    texture, so every face variant matches the one body texture."""
    me = obj.data
    uv = me.uv_layers["UVMap"].data
    images = {0: body, 1: face}
    corr = {k: np.zeros_like(v) for k, v in images.items()}
    weight = {k: np.zeros(v.shape[:2]) for k, v in images.items()}
    sides = {}
    for p in me.polygons:
        n = len(p.loop_indices)
        for k in range(n):
            la, lb = p.loop_indices[k], p.loop_indices[(k + 1) % n]
            a, b = me.loops[la].vertex_index, me.loops[lb].vertex_index
            sides.setdefault(tuple(sorted((a, b))), []).append((p.material_index, {a: la, b: lb}))
    r = int(np.ceil(radius))
    offsets = [(dx, dy, 1.0 - np.hypot(dx, dy) / radius) for dx in range(-r, r + 1) for dy in range(-r, r + 1) if np.hypot(dx, dy) < radius]

    def splat(mat, u, v, delta):
        img = images[mat]
        h, w, _ = img.shape
        cx, cy = int(u * w), int(v * h)
        for dx, dy, wt in offsets:
            x, y = cx + dx, cy + dy
            if 0 <= x < w and 0 <= y < h:
                corr[mat][y, x] += wt * delta
                weight[mat][y, x] += wt

    for e in me.edges:
        if not e.use_seam:
            continue
        key = tuple(sorted(e.vertices))
        s = sides.get(key, [])
        if len(s) != 2:
            continue
        a, b = key
        (m1, l1), (m2, l2) = s
        ua1, ub1 = uv[l1[a]].uv, uv[l1[b]].uv
        ua2, ub2 = uv[l2[a]].uv, uv[l2[b]].uv
        px = max((ub1 - ua1).length * images[m1].shape[1], (ub2 - ua2).length * images[m2].shape[1])
        for t in np.linspace(0.0, 1.0, max(int(px * 1.5), 2)):
            p1 = (ua1[0] + (ub1[0] - ua1[0]) * t, ua1[1] + (ub1[1] - ua1[1]) * t)
            p2 = (ua2[0] + (ub2[0] - ua2[0]) * t, ua2[1] + (ub2[1] - ua2[1]) * t)
            c1, c2 = _bilinear(images[m1], *p1), _bilinear(images[m2], *p2)
            if m1 == m2:
                target = (c1 + c2) / 2
                splat(m1, *p1, target - c1)
                splat(m2, *p2, target - c2)
            elif m1 == 1:
                splat(1, *p1, c2 - c1)
            else:
                splat(1, *p2, c1 - c2)
    out = {}
    for k, img in images.items():
        wsum = weight[k][..., None]
        out[k] = np.clip(img + corr[k] / np.maximum(wsum, 1e-6) * np.clip(wsum, 0.0, 1.0), 0.0, 1.0)
    return out[0], out[1]


def run(geometry, refs_path, out_path, tex_dir):
    os.makedirs(tex_dir, exist_ok=True)
    bpy.ops.wm.open_mainfile(filepath=geometry)
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    if scene.world is None:
        scene.world = bpy.data.worlds.new("World")
    scene.world.light_settings.distance = 0.22
    with bpy.data.libraries.load(refs_path, link=False) as (_src, dst):
        dst.objects = ["RefMale", "RefFemale"]
    ref_objs = {o.name: o for o in dst.objects}
    bodies = {"male": bpy.data.objects["Male_Body"], "female": bpy.data.objects["Female_Body"]}
    for sex, obj in bodies.items():
        t0 = time.time()
        label = sex.capitalize()
        ref = warped_reference(ref_objs["RefMale" if sex == "male" else "RefFemale"], sex, obj)
        others = [o for o in scene.objects if o not in (obj, ref)]
        hidden_before = {o.name: o.hide_render for o in others}
        for o in others:
            o.hide_render = True
        tree = sculpt_tree(ref)
        store_reference_offset(obj, tree)
        data = {}
        for kind in ("pos", "nrm", "part", "off", "loc", "hi", "ao"):
            imgs = (float_image(f"_{kind}_body", BODY_RES), float_image(f"_{kind}_face", FACE_RES))
            ref.hide_render = kind != "hi"
            bake(obj, kind, imgs, sources=(ref,) if kind == "hi" else ())
            data[kind] = [pixels(i) for i in imgs]
            for i in imgs:
                bpy.data.images.remove(i)
            print(f"  {label} baked {kind} in {time.time() - t0:.0f}s")
        # Only the body texture paints sculpt detail; the face texture uses the mesh normals.
        depth, near_normal = sculpt_distance(data["loc"][0], tree)
        print(f"  {label} measured the sculpt distance in {time.time() - t0:.0f}s")
        bpy.data.objects.remove(ref)
        obj.data.attributes.remove(obj.data.attributes["ref_offset"])
        for o in others:
            o.hide_render = hidden_before[o.name]
        lm = landmarks(obj)
        maps = []
        for k in range(2):
            P = data["pos"][k] * POS_SCALE - POS_OFFSET
            Nlo = data["nrm"][k] * 2.0 - 1.0
            Nlo /= np.maximum(np.linalg.norm(Nlo, axis=-1, keepdims=True), 1e-6)
            Nhi = data["hi"][k] * 2.0 - 1.0
            length = np.linalg.norm(Nhi, axis=-1, keepdims=True)
            Nhi /= np.maximum(length, 1e-6)
            # Where the rays missed the sculpt or hit something else, fade to the low-poly normal.
            agree = (Nhi * Nlo).sum(-1)
            w = paint.smoothstep(0.30, 0.60, agree) * paint.smoothstep(0.4, 0.6, length[..., 0])
            if k == 0:
                moved = 1.0 - paint.smoothstep(*MOVED_FADE, data["off"][0][..., 0])
                hit = 1.0 - paint.smoothstep(*MISS_FADE, -depth)
                same = paint.smoothstep(*SAME_SURFACE, (Nhi * near_normal).sum(-1))
                w = soften_weight(w * moved * hit * same)
                covered = data["loc"][0].sum(-1) > 0
                share = ", ".join(f"{name} {(f[covered] < 0.99).mean() * 100:.2f}%"
                                  for name, f in (("moved", moved), ("miss", hit), ("other surface", same)))
                print(f"  {label} sculpt detail faded on the body texture: {share}")
            Nhi = Nlo + (Nhi - Nlo) * w[..., None]
            Nhi /= np.maximum(np.linalg.norm(Nhi, axis=-1, keepdims=True), 1e-6)
            AO = data["ao"][k][..., 0]
            part = np.rint(data["part"][k][..., 0] * 10.0).astype(np.int32)
            maps.append((P, Nlo, Nhi, AO, part))
        P, Nlo, Nhi, AO, part = maps[0]
        body_rgb = paint.paint_body(sex, P, Nlo, Nhi, AO, part, lm)
        P, Nlo, Nhi, AO, part = maps[1]
        faces = [paint.paint_face(sex, variant, P, Nlo, AO, lm, part) for variant in FACE_VARIANTS[sex]]
        # Images are stored bottom row first, as UV v runs upward.
        body_rgb, faces[0] = stitch_seams(obj, body_rgb, faces[0])
        for k in range(1, len(faces)):
            _same, faces[k] = stitch_seams(obj, body_rgb, faces[k])
        body_img = save_png(f"T_Body_{label}", body_rgb, os.path.join(tex_dir, f"T_Body_{label}.png"))
        textured_material(bpy.data.materials[f"M_Body_{label}"], body_img, 0.62)
        face_imgs = []
        for variant, rgb in zip(FACE_VARIANTS[sex], faces):
            face_imgs.append(save_png(f"T_Face_{label}_{variant}", rgb, os.path.join(tex_dir, f"T_Face_{label}_{variant}.png")))
        textured_material(bpy.data.materials[f"M_Face_{label}"], face_imgs[0], 0.55)
        eye_imgs = []
        for variant in paint.EYE_VARIANTS[sex]:
            rgb = paint.paint_eye(sex, variant, lm["eyeball"][1], size=EYE_RES)
            eye_imgs.append(save_png(f"T_Eye_{label}_{variant}", rgb, os.path.join(tex_dir, f"T_Eye_{label}_{variant}.png")))
        textured_material(bpy.data.materials[f"M_Eye_{label}"], eye_imgs[0], 0.12)
        flat_material(bpy.data.materials[f"M_Teeth_{label}"], (0.86, 0.82, 0.72), 0.35)
        flat_material(bpy.data.materials[f"M_Tongue_{label}"], (0.62, 0.30, 0.31), 0.45)
        print(f"PAINTED {label} in {time.time() - t0:.0f}s")
    for o in list(ref_objs.values()):
        bpy.data.objects.remove(o)
    for m in list(bpy.data.meshes):
        if m.users == 0:
            bpy.data.meshes.remove(m)
    scene.render.engine = "BLENDER_EEVEE"
    material_preview_viewport()
    bpy.ops.wm.save_as_mainfile(filepath=out_path, compress=True)
    print("SAVED", out_path)
