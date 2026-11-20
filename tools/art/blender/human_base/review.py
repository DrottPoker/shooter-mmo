"""Review sheets of the base characters, lit by an even sky and a key light that follows the camera:
overview, parts, faces, eyes, mouth, hands, feet, seams and wireframe. Each character is an
armature (Male, Female) whose children are its source body, the body parts written from it,
eyeballs, teeth and tongue; landmarks are custom properties of the armature. Renders show the body
parts, which are what the game gets."""

import math
import os
import tempfile

import bpy
import numpy as np
from mathutils import Vector

from art_pipeline import bodies, poses

from . import files


def srgb_to_linear(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def flat_material(name, srgb, roughness=0.8):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    if mat.node_tree is None:
        mat.use_nodes = True
    color = tuple(srgb_to_linear(c) for c in srgb) + (1.0,)
    bsdf = next(n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = color
    bsdf.inputs["Roughness"].default_value = roughness
    mat.diffuse_color = color
    return mat


def setup_scene(size=(800, 800), engine="EEVEE", samples=32, background=(0.42, 0.47, 0.55)):
    scene = bpy.context.scene
    if engine == "EEVEE":
        for name in ("BLENDER_EEVEE", "BLENDER_EEVEE_NEXT"):
            try:
                scene.render.engine = name
                break
            except TypeError:
                continue
        scene.eevee.taa_render_samples = samples
    else:
        scene.render.engine = "BLENDER_WORKBENCH"
        shading = scene.display.shading
        shading.light = "STUDIO"
        shading.color_type = "MATERIAL"
    scene.render.resolution_x, scene.render.resolution_y = size
    scene.render.resolution_percentage = 100
    scene.render.film_transparent = False
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "None"
    world = bpy.data.worlds.get("ReviewWorld") or bpy.data.worlds.new("ReviewWorld")
    if world.node_tree is None:
        world.use_nodes = True
    bg = next(n for n in world.node_tree.nodes if n.type == "BACKGROUND")
    bg.inputs["Color"].default_value = tuple(srgb_to_linear(c) for c in background) + (1.0,)
    bg.inputs["Strength"].default_value = 1.0
    scene.world = world
    return scene


def add_camera(name="ReviewCamera", lens=50.0, ortho=None):
    cam_data = bpy.data.cameras.new(name)
    cam_data.lens = lens
    cam_data.clip_start = 0.01
    if ortho:
        cam_data.type = "ORTHO"
        cam_data.ortho_scale = ortho
    cam = bpy.data.objects.new(name, cam_data)
    bpy.context.scene.collection.objects.link(cam)
    bpy.context.scene.camera = cam
    return cam


def aim(cam, target, yaw_deg, pitch_deg, distance):
    """Yaw 0 looks at the front of an asset that faces +Y (camera on +Y); yaw 90 from +X. The key
    light of setup turns with the camera."""
    yaw, pitch = math.radians(yaw_deg), math.radians(pitch_deg)
    direction = Vector((math.sin(yaw) * math.cos(pitch), math.cos(yaw) * math.cos(pitch), math.sin(pitch)))
    cam.location = Vector(target) + direction * distance
    cam.rotation_euler = (Vector(target) - cam.location).to_track_quat("-Z", "Y").to_euler()
    key = bpy.data.objects.get(KEY_LIGHT_NAME)
    if key is not None:
        (toward, aside, above), _energy = KEY_LIGHT
        way = (Vector((math.sin(yaw), math.cos(yaw), 0.0)) * toward + Vector((math.cos(yaw), -math.sin(yaw), 0.0)) * aside
               + Vector((0.0, 0.0, above)))
        key.rotation_euler = way.normalized().to_track_quat("Z", "Y").to_euler()


def wire_overlay(obj, thickness=0.0012, srgb=(0.05, 0.05, 0.06)):
    """A copy with a wireframe modifier to show the topology on top of the shaded mesh."""
    wire = obj.copy()
    wire.data = obj.data.copy()
    wire.name = obj.name + "_wire"
    for md in list(wire.modifiers):
        wire.modifiers.remove(md)
    md = wire.modifiers.new("wire", "WIREFRAME")
    md.thickness = thickness
    md.use_replace = True
    md.use_even_offset = False
    mat = flat_material("M_Wire", srgb, 1.0)
    wire.data.materials.clear()
    wire.data.materials.append(mat)
    for p in wire.data.polygons:
        p.material_index = 0
    bpy.context.scene.collection.objects.link(wire)
    if obj.parent:
        wire.parent = obj.parent
        wire.matrix_parent_inverse = obj.matrix_parent_inverse
    return wire


def render_to_array(path):
    scene = bpy.context.scene
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    image = bpy.data.images.load(path)
    try:
        w, h = image.size
        px = np.empty(w * h * 4, dtype=np.float32)
        image.pixels.foreach_get(px)
        return px.reshape(h, w, 4)
    finally:
        bpy.data.images.remove(image)


def render_grid(shots, columns, output):
    """shots: callables that set up the scene for one cell (camera, visibility). Renders every
    cell and writes one sheet, row by row from the top."""
    scene = bpy.context.scene
    w, h = scene.render.resolution_x, scene.render.resolution_y
    rows = math.ceil(len(shots) / columns)
    sheet = np.zeros((h * rows, w * columns, 4), dtype=np.float32)
    sheet[..., 3] = 1.0
    with tempfile.TemporaryDirectory() as tmp:
        for i, shot in enumerate(shots):
            shot()
            px = render_to_array(os.path.join(tmp, f"{i}.png"))
            r, c = divmod(i, columns)
            top = (rows - 1 - r) * h
            sheet[top:top + h, c * w:(c + 1) * w] = px
    os.makedirs(os.path.dirname(output), exist_ok=True)
    image = bpy.data.images.new("Sheet", sheet.shape[1], sheet.shape[0], alpha=True)
    image.pixels.foreach_set(sheet.ravel())
    image.filepath_raw = output
    image.file_format = "PNG"
    image.save()
    bpy.data.images.remove(image)
    return output


# A key light over the even light of the sky, without shadows: the sky alone lights a body of one
# colour so evenly that its forms do not show. The light follows the camera, so a body is lit alike
# from every side: it comes from where the camera stands, from the camera's left, and from above,
# by these shares, with this strength.
KEY_LIGHT = ((1.0, 0.45, 0.9), 3.5)
KEY_LIGHT_NAME = "ReviewKeyLight"
# The characters stand this far left and right of the centre, clear of each other's hands, and the
# sheets of the whole figures show both in cells of this size from this far.
REVIEW_OFFSET = 0.8
FIGURE_SIZE, FIGURE_DISTANCE = (1050, 700), 4.6
BODIES = ("Male", "Female")
PART_COLOURS = {"Head": (0.85, 0.72, 0.62), "Neck": (0.95, 0.85, 0.35), "Torso": (0.85, 0.45, 0.35),
                "Pelvis": (0.45, 0.55, 0.85), "UpperArms": (0.45, 0.80, 0.45), "Forearms": (0.25, 0.60, 0.40),
                "Hands": (0.35, 0.85, 0.85), "Thighs": (0.70, 0.45, 0.85), "Shins": (0.55, 0.30, 0.70),
                "Feet": (0.95, 0.65, 0.20)}


def setup(size):
    scene = setup_scene(size, samples=48)
    scene.view_settings.view_transform = "Standard"
    scene.render.film_transparent = False
    key = bpy.data.objects.get(KEY_LIGHT_NAME)
    if key is None:
        key = bpy.data.objects.new(KEY_LIGHT_NAME, bpy.data.lights.new(KEY_LIGHT_NAME, "SUN"))
        scene.collection.objects.link(key)
    key.data.energy = KEY_LIGHT[1]
    key.data.use_shadow = False
    # Plain backdrop behind the characters (the sky still lights them).
    world = scene.world
    nt = world.node_tree
    bg = next(n for n in nt.nodes if n.type == "BACKGROUND")
    light_path = nt.nodes.new("ShaderNodeLightPath")
    mix = nt.nodes.new("ShaderNodeMixShader")
    plain = nt.nodes.new("ShaderNodeBackground")
    plain.inputs["Color"].default_value = (0.20, 0.23, 0.28, 1.0)
    out = next(n for n in nt.nodes if n.type == "OUTPUT_WORLD")
    nt.links.new(light_path.outputs["Is Camera Ray"], mix.inputs["Fac"])
    nt.links.new(bg.outputs["Background"], mix.inputs[1])
    nt.links.new(plain.outputs["Background"], mix.inputs[2])
    nt.links.new(mix.outputs["Shader"], out.inputs["Surface"])
    return scene


def landmark(obj, key, index=None):
    """Mean of a landmark's vertices (or one of them) in the posed mesh, in world space."""
    vals = obj[f"lmw_{key}"]
    pts = [Vector(vals[i:i + 3]) for i in range(0, len(vals), 3)]
    p = pts[index] if index is not None else sum(pts, Vector()) / len(pts)
    return obj.matrix_world @ p


def extras(body):
    """Eyeballs, teeth and tongue of a character."""
    return [o for o in bpy.data.objects[body].children
            if o.type == "MESH" and "zone" not in o and not bodies.is_source_body(o)]


def parts(body):
    """The body parts of a character."""
    return [o for o in bpy.data.objects[body].children if o.type == "MESH" and "zone" in o]


def joint(obj, bone):
    """Where a bone of the character begins, in world space."""
    return obj.matrix_world @ obj.data.bones[bone].head_local


# Whether a render hid each object before show_only hid any.
_HIDDEN = {}


def show_only(body=None):
    """Has renders show one character alone, so the other does not stand behind it in a close-up,
    or both again without a name."""
    for label in BODIES:
        for o in bpy.data.objects[label].children:
            hidden = _HIDDEN.setdefault(o.name, o.hide_render)
            o.hide_render = hidden or (body is not None and label != body)


def eye_objects(body):
    return [o for o in extras(body) if "_Eye_" in o.name]


def eye_centre(body):
    eyes = eye_objects(body)
    return sum((o.matrix_world.translation for o in eyes), Vector()) / len(eyes)


def head_target(obj):
    eye = eye_centre(obj.name)
    return Vector((obj.matrix_world.translation.x, eye.y - 0.05, eye.z - 0.03))


def _images(material, prefix):
    """The material's own image first, then every other image of the file named with the prefix."""
    own = next(n.image.name for n in bpy.data.materials[material].node_tree.nodes
               if n.type == "TEX_IMAGE" and n.image is not None)
    return [own] + sorted(i.name for i in bpy.data.images if i.name.startswith(prefix) and i.name != own)


def face_sets():
    """The faces and eyes the faces sheet shows: per body, the materials' own images, then every
    variant T_Face_<Label>_* and T_Eye_<Label>_* the file holds, paired in order, so a new variant
    shows without a change here."""
    sets = []
    for label in BODIES:
        faces = _images(f"M_Face_{label}", f"T_Face_{label}_")
        eyes = _images(f"M_Eye_{label}", f"T_Eye_{label}_")
        for i in range(max(len(faces), len(eyes))):
            sets.append((label, faces[min(i, len(faces) - 1)], eyes[min(i, len(eyes) - 1)]))
    return sets


def set_texture(mat_name, image_name):
    mat = bpy.data.materials[mat_name]
    tex = next(n for n in mat.node_tree.nodes if n.type == "TEX_IMAGE")
    tex.image = bpy.data.images[image_name]


def parts_sheet(out, cam):
    """The body parts in colours (front, three-quarter, side, back)."""
    setup(FIGURE_SIZE)
    saved = {}
    for body in BODIES:
        for o in parts(body):
            saved[o.name] = (list(o.data.materials), [p.material_index for p in o.data.polygons])
            mat = flat_material(f"Part_{o['zone']}", PART_COLOURS[o["zone"]], 0.7)
            o.data.materials.clear()
            o.data.materials.append(mat)
            for p in o.data.polygons:
                p.material_index = 0
    shots = []
    for yaw in (0, 35, 90, 180):
        shots.append(lambda yaw=yaw: aim(cam, Vector((0, 0, 0.92)), yaw, 3, FIGURE_DISTANCE))
    path = render_grid(shots, 4, out)
    return path, saved


def overview(out, cam):
    setup(FIGURE_SIZE)
    shots = []
    for yaw in (0, 35, 90, 180):
        shots.append(lambda yaw=yaw: aim(cam, Vector((0, 0, 0.92)), yaw, 3, FIGURE_DISTANCE))
    return render_grid(shots, 4, out)


def faces(out, cam):
    setup((640, 640))
    shots = []
    sets = face_sets()
    for body, face_img, eye_img in sets:
        obj = bpy.data.objects[body]
        label = body
        for yaw, pitch in ((0, 2), (30, 4), (75, 2)):
            def shot(obj=obj, label=label, face_img=face_img, eye_img=eye_img, yaw=yaw, pitch=pitch):
                show_only(label)
                set_texture(f"M_Face_{label}", face_img)
                set_texture(f"M_Eye_{label}", eye_img)
                aim(cam, head_target(obj), yaw, pitch, 0.62)
            shots.append(shot)
    path = render_grid(shots, 3, out)
    show_only()
    for label in BODIES:
        set_texture(f"M_Face_{label}", next(f for b, f, _e in sets if b == label))
        set_texture(f"M_Eye_{label}", next(e for b, _f, e in sets if b == label))
    return path


def look(body, yaw_deg, pitch_deg):
    """Turns both eyeballs: yaw to the character's right (+x) and pitch up, in degrees."""
    for o in eye_objects(body):
        o.rotation_euler = (math.radians(pitch_deg), 0.0, math.radians(-yaw_deg))


def eyes(out, cam):
    """Eye close-ups straight ahead, then the gaze test: the eyeballs turn inside the lids."""
    setup((640, 320))
    shots = []
    for body in BODIES:
        c = eye_centre(body) + Vector((0.0, 0.012, 0.0))
        for yaw, pitch, dist, gaze in ((0, 0, 0.30, (0, 0)), (35, 5, 0.30, (0, 0)), (0, 0, 0.30, (22, 0)),
                                       (0, 0, 0.30, (-22, 0)), (0, 0, 0.30, (0, 16)), (0, 0, 0.30, (0, -12))):
            def shot(body=body, c=c, yaw=yaw, pitch=pitch, dist=dist, gaze=gaze):
                show_only(body)
                look(body, *gaze)
                aim(cam, c, yaw, pitch, dist)
            shots.append(shot)
    path = render_grid(shots, 6, out)
    show_only()
    for body in BODIES:
        look(body, 0, 0)
    return path


def mouth(out, cam):
    """The mouth closed, then opened by the jaw bone: a little, well open from the front, from
    three quarters and from below, and wide open."""
    setup((560, 480))
    shots = []
    views = ((0, 0, 2), (0, 40, 0), (8, 0, 2), (16, 0, 2), (16, 40, 0), (16, 0, -25), (26, 0, 2))
    for body in BODIES:
        obj = bpy.data.objects[body]
        c = landmark(obj, "mouth_centre") + Vector((0.0, -0.01, -0.012))
        for angle, yaw, pitch in views:
            def shot(obj=obj, c=c, angle=angle, yaw=yaw, pitch=pitch):
                show_only(obj.name)
                poses.apply(obj, {"Jaw": (("X", -angle),)} if angle else {})
                aim(cam, c, yaw, pitch, 0.26)
            shots.append(shot)
    path = render_grid(shots, len(views), out)
    show_only()
    for body in BODIES:
        poses.rest(bpy.data.objects[body])
    return path


def hands(out, cam):
    setup((640, 640))
    shots = []
    for name in BODIES:
        obj = bpy.data.objects[name]
        tip = landmark(obj, "finger_tips", 2)
        c = (tip + landmark(obj, "wrist")) / 2
        for yaw, pitch in ((80, 10), (10, 15), (150, 10)):
            def shot(name=name, c=c, yaw=yaw, pitch=pitch):
                show_only(name)
                aim(cam, c, yaw, pitch, 0.42)
            shots.append(shot)
    path = render_grid(shots, 3, out)
    show_only()
    return path


def feet(out, cam):
    setup((640, 520))
    shots = []
    for name in BODIES:
        obj = bpy.data.objects[name]
        toe = landmark(obj, "toe_tips", 1)
        c = Vector((toe.x - 0.02 * (1 if toe.x > obj.matrix_world.translation.x else -1), toe.y - 0.09, 0.05))
        for yaw, pitch, dist in ((20, 35, 0.5), (90, 5, 0.45), (-40, 15, 0.45)):
            def shot(name=name, c=c, yaw=yaw, pitch=pitch, dist=dist):
                show_only(name)
                aim(cam, c, yaw, pitch, dist)
            shots.append(shot)
    path = render_grid(shots, 3, out)
    show_only()
    return path


def wire(out, cam):
    setup(FIGURE_SIZE)
    for name in BODIES:
        for o in parts(name):
            wire_overlay(o, 0.0007)
        for o in extras(name):
            wire_overlay(o, 0.0003)
    shots = [lambda yaw=yaw: aim(cam, Vector((0, 0, 0.92)), yaw, 3, FIGURE_DISTANCE) for yaw in (0, 35, 180)]
    obj = bpy.data.objects["Male"]
    shots.append(lambda: aim(cam, head_target(obj) + Vector((0, 0, -0.05)), 30, 5, 1.0))
    return render_grid(shots, 4, out)


def seams(out, cam):
    """Close-ups where texture seams or creases could show."""
    setup((560, 560))
    shots = []
    for name in BODIES:
        obj = bpy.data.objects[name]
        head = head_target(obj)
        views = [
            (head + Vector((0, -0.02, -0.08)), 100, 0, 0.55),        # neck from the side
            (head + Vector((0.07, -0.03, 0.0)), 70, 10, 0.45),        # temple: face and head textures meet
            (joint(obj, "UpperArm_R"), 0, 35, 0.55),                  # shoulder top and armpit
            (joint(obj, "LowerArm_R"), 20, 5, 0.6),                   # elbow, inner arm
            (landmark(obj, "wrist"), -60, 5, 0.45),                   # wrist seam
            (joint(obj, "LowerLeg_R"), -25, 5, 0.75),                 # knee, inner leg seam
        ]
        for c, yaw, pitch, dist in views:
            def shot(name=name, c=c, yaw=yaw, pitch=pitch, dist=dist):
                show_only(name)
                aim(cam, c, yaw, pitch, dist)
            shots.append(shot)
    path = render_grid(shots, 6, out)
    show_only()
    return path


SHEETS = ("overview", "parts", "faces", "eyes", "mouth", "hands", "feet", "seams", "wire")


def review_file(male_path, female_path, out):
    """Both characters side by side, as the review sheets show them, for the reviewer to open."""
    files.open_both(male_path, female_path, REVIEW_OFFSET)
    bpy.ops.wm.save_as_mainfile(filepath=out, compress=True, relative_remap=False)
    return out


def render(path, out_dir, prefix, sheets=SHEETS):
    """Renders the review sheets of a character file to <out_dir>/<prefix>_<sheet>.png."""
    bpy.ops.wm.open_mainfile(filepath=path)
    os.makedirs(out_dir, exist_ok=True)
    cam = add_camera(lens=50)
    plain = {"overview": overview, "faces": faces, "eyes": eyes, "mouth": mouth, "hands": hands, "feet": feet,
             "seams": seams}
    written = []
    # Sheets without wireframes first; the wireframe sheets add overlays that stay in the scene.
    for sheet, draw in plain.items():
        if sheet in sheets:
            cam.data.lens = 50 if sheet == "overview" else 85
            written.append(draw(os.path.join(out_dir, f"{prefix}_{sheet}.png"), cam))
    if "parts" in sheets:
        cam.data.lens = 50
        out, saved = parts_sheet(os.path.join(out_dir, f"{prefix}_parts.png"), cam)
        written.append(out)
        for name, (mats, indices) in saved.items():
            me = bpy.data.objects[name].data
            me.materials.clear()
            for m in mats:
                me.materials.append(m)
            for poly, i in zip(me.polygons, indices):
                poly.material_index = i
    if "wire" in sheets:
        cam.data.lens = 50
        written.append(wire(os.path.join(out_dir, f"{prefix}_wire.png"), cam))
        cam.data.lens = 85
        written.append(hands(os.path.join(out_dir, f"{prefix}_hands_wire.png"), cam))
        written.append(feet(os.path.join(out_dir, f"{prefix}_feet_wire.png"), cam))
    return written
