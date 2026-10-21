"""Review sheets of the base characters, lit like Blender's Material Preview (studio HDRI):
overview, parts, faces, eyes, mouth, hands, feet, seams and wireframe. Each character is a root
object (Male, Female) whose children are the body parts, eyeballs, teeth and tongue; landmarks
are custom properties of the root."""

import math
import os
import tempfile

import bpy
import numpy as np
from mathutils import Matrix, Vector


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


def setup_scene(size=(800, 800), engine="EEVEE", samples=32, background=(0.42, 0.47, 0.55), hdri=None,
                hdri_strength=1.0):
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
    nodes, links = world.node_tree.nodes, world.node_tree.links
    bg = next(n for n in nodes if n.type == "BACKGROUND")
    if hdri:
        env = nodes.new("ShaderNodeTexEnvironment")
        env.image = bpy.data.images.load(hdri, check_existing=True)
        links.new(env.outputs["Color"], bg.inputs["Color"])
        bg.inputs["Strength"].default_value = hdri_strength
    else:
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
    """Yaw 0 looks at the front of an asset that faces +Y (camera on +Y); yaw 90 from +X."""
    yaw, pitch = math.radians(yaw_deg), math.radians(pitch_deg)
    direction = Vector((math.sin(yaw) * math.cos(pitch), math.cos(yaw) * math.cos(pitch), math.sin(pitch)))
    cam.location = Vector(target) + direction * distance
    cam.rotation_euler = (Vector(target) - cam.location).to_track_quat("-Z", "Y").to_euler()


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


# The studio light Material Preview uses.
HDRI = bpy.utils.system_resource("DATAFILES", path="studiolights/world/forest.exr")
BODIES = ("Male", "Female")
PART_COLOURS = {"Head": (0.85, 0.72, 0.62), "Neck": (0.95, 0.85, 0.35), "Torso": (0.85, 0.45, 0.35),
                "Pelvis": (0.45, 0.55, 0.85), "UpperArms": (0.45, 0.80, 0.45), "Forearms": (0.25, 0.60, 0.40),
                "Hands": (0.35, 0.85, 0.85), "Thighs": (0.70, 0.45, 0.85), "Shins": (0.55, 0.30, 0.70),
                "Feet": (0.95, 0.65, 0.20)}


def setup(size):
    scene = setup_scene(size, samples=48, hdri=HDRI if os.path.exists(HDRI) else None, hdri_strength=1.0)
    scene.view_settings.view_transform = "Standard"
    scene.render.film_transparent = False
    # Like Material Preview: no shadow from the sun EEVEE extracts from the HDRI (its shadow map
    # draws jagged dark patches, for example below the ears).
    world = scene.world
    if hasattr(world, "use_sun_shadow"):
        world.use_sun_shadow = False
    # Plain backdrop behind the characters (the HDRI still lights them).
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
    return [o for o in bpy.data.objects[body].children if o.type == "MESH" and "zone" not in o]


def parts(body):
    """The body parts of a character."""
    return [o for o in bpy.data.objects[body].children if o.type == "MESH" and "zone" in o]


def eye_objects(body):
    return [o for o in extras(body) if "_Eye_" in o.name]


def eye_centre(body):
    eyes = eye_objects(body)
    return sum((o.matrix_world.translation for o in eyes), Vector()) / len(eyes)


def head_target(obj):
    eye = eye_centre(obj.name)
    return Vector((obj.matrix_world.translation.x, eye.y - 0.05, eye.z - 0.03))


# Face and eye texture pairs shown on the faces sheet, per body type.
FACE_SETS = (
    ("Male", "T_Face_Male_Stubble", "T_Eye_Male_Teal"),
    ("Male", "T_Face_Male_Scarred", "T_Eye_Male_Brown"),
    ("Female", "T_Face_Female_Blush", "T_Eye_Female_Teal"),
    ("Female", "T_Face_Female_Freckles", "T_Eye_Female_Hazel"),
)


def set_texture(mat_name, image_name):
    mat = bpy.data.materials[mat_name]
    tex = next(n for n in mat.node_tree.nodes if n.type == "TEX_IMAGE")
    tex.image = bpy.data.images[image_name]


def parts_sheet(out, cam):
    """The body parts in colours (front, three-quarter, side, back)."""
    setup((700, 1050))
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
        shots.append(lambda yaw=yaw: aim(cam, Vector((0, 0, 0.92)), yaw, 3, 3.7))
    path = render_grid(shots, 4, out)
    return path, saved


def overview(out, cam):
    setup((700, 1050))
    shots = []
    for yaw in (0, 35, 90, 180):
        shots.append(lambda yaw=yaw: aim(cam, Vector((0, 0, 0.92)), yaw, 3, 3.7))
    return render_grid(shots, 4, out)


def faces(out, cam):
    setup((640, 640))
    shots = []
    for body, face_img, eye_img in FACE_SETS:
        obj = bpy.data.objects[body]
        label = body
        for yaw, pitch in ((0, 2), (30, 4), (75, 2)):
            def shot(obj=obj, label=label, face_img=face_img, eye_img=eye_img, yaw=yaw, pitch=pitch):
                set_texture(f"M_Face_{label}", face_img)
                set_texture(f"M_Eye_{label}", eye_img)
                aim(cam, head_target(obj), yaw, pitch, 0.62)
            shots.append(shot)
    path = render_grid(shots, 3, out)
    for label in ("Male", "Female"):
        set_texture(f"M_Face_{label}", next(f for b, f, _e in FACE_SETS if b.startswith(label)))
        set_texture(f"M_Eye_{label}", next(e for b, _f, e in FACE_SETS if b.startswith(label)))
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
                look(body, *gaze)
                aim(cam, c, yaw, pitch, dist)
            shots.append(shot)
    path = render_grid(shots, 6, out)
    for body in BODIES:
        look(body, 0, 0)
    return path


def open_jaw(body, angle_deg):
    """A preview of the jaw opening (no rig yet): the lower lip, chin and jaw turn about a hinge
    in front of the ears with smooth weights, the lower teeth and tongue with them. Returns a
    function that restores the meshes."""
    obj = bpy.data.objects[body]
    mw, inv = obj.matrix_world, obj.matrix_world.inverted()
    meshes = [o.data for o in parts(body)]
    saved = [[v.co.copy() for v in me.vertices] for me in meshes]
    mouth = Vector(obj["lmw_mouth_centre"][:3])
    chin = Vector(obj["lmw_chin_bottom"][:3])
    eye = inv @ eye_centre(body)
    hinge = Vector((0.0, eye.y - 0.068, mouth.z + 0.028))
    rot = Matrix.Rotation(-math.radians(angle_deg), 3, "X")  # chin down and back

    def smooth(e0, e1, x):
        t = min(max((x - e0) / (e1 - e0), 0.0), 1.0)
        return t * t * (3 - 2 * t)

    # The split between upper and lower face follows the line between the lips, and widens
    # into a soft band beyond the mouth corners so the cheeks stretch instead of tearing.
    up = obj["lmw_upper_lip_line"]
    lo = obj["lmw_lower_lip_line"]
    line = sorted((abs(up[i]), (up[i + 2] + lo[i + 2]) / 2) for i in range(0, len(up), 3))
    corner_x = line[-1][0]

    def line_z(ax):
        if ax >= corner_x:
            return line[-1][1]
        for (x0, z0), (x1, z1) in zip(line, line[1:]):
            if x0 <= ax <= x1:
                return z0 + (z1 - z0) * (ax - x0) / max(x1 - x0, 1e-9)
        return line[0][1]

    for v in (v for me in meshes for v in me.vertices):
        p = v.co
        zl = line_z(abs(p.x))
        band = 0.0006 + max(abs(p.x) - corner_x, 0.0) * 0.6
        w = smooth(zl + band, zl - band, p.z) * smooth(chin.z - 0.045, chin.z - 0.012, p.z)
        w *= smooth(hinge.y - 0.015, hinge.y + 0.02, p.y)
        if w > 0:
            q = hinge + rot @ (p - hinge)
            v.co = p.lerp(q, w)
    moved = []
    for o in extras(body):
        if "Lower" in o.name or "Tongue" in o.name:
            moved.append((o, [v.co.copy() for v in o.data.vertices]))
            for v in o.data.vertices:
                p = inv @ (o.matrix_world @ v.co)
                v.co = o.matrix_world.inverted() @ (mw @ (hinge + rot @ (p - hinge)))
            o.data.update()
    for me in meshes:
        me.update()

    def restore():
        for me, cos in zip(meshes, saved):
            for v, p in zip(me.vertices, cos):
                v.co = p
            me.update()
        for o, cos in moved:
            for v, p in zip(o.data.vertices, cos):
                v.co = p
            o.data.update()
    return restore


def mouth(out, cam):
    """The mouth closed, then opened as a jaw would open it (front, three-quarter, low)."""
    setup((560, 480))
    shots = []
    state = {"restore": None}
    for body in BODIES:
        obj = bpy.data.objects[body]
        c = landmark(obj, "mouth_centre") + Vector((0.0, -0.01, -0.012))
        for angle, yaw, pitch in ((0, 0, 2), (0, 40, 0), (16, 0, 2), (16, 40, 0), (16, 0, -25)):
            def shot(body=body, c=c, angle=angle, yaw=yaw, pitch=pitch):
                if state["restore"]:
                    state["restore"]()
                    state["restore"] = None
                if angle:
                    state["restore"] = open_jaw(body, angle)
                aim(cam, c, yaw, pitch, 0.26)
            shots.append(shot)
    path = render_grid(shots, 5, out)
    if state["restore"]:
        state["restore"]()
    return path


def hands(out, cam):
    setup((640, 640))
    shots = []
    for name in BODIES:
        obj = bpy.data.objects[name]
        tip = landmark(obj, "finger_tips", 2)
        c = (tip + landmark(obj, "wrist")) / 2
        for yaw, pitch in ((80, 10), (10, 15), (150, 10)):
            shots.append(lambda c=c, yaw=yaw, pitch=pitch: aim(cam, c, yaw, pitch, 0.42))
    return render_grid(shots, 3, out)


def feet(out, cam):
    setup((640, 520))
    shots = []
    for name in BODIES:
        obj = bpy.data.objects[name]
        toe = landmark(obj, "toe_tips", 1)
        c = Vector((toe.x - 0.02 * (1 if toe.x > obj.matrix_world.translation.x else -1), toe.y - 0.09, 0.05))
        for yaw, pitch, dist in ((20, 35, 0.5), (90, 5, 0.45), (-40, 15, 0.45)):
            shots.append(lambda c=c, yaw=yaw, pitch=pitch, dist=dist: aim(cam, c, yaw, pitch, dist))
    return render_grid(shots, 3, out)


def wire(out, cam):
    setup((700, 1050))
    for name in BODIES:
        for o in parts(name):
            wire_overlay(o, 0.0007)
        for o in extras(name):
            wire_overlay(o, 0.0003)
    shots = [lambda yaw=yaw: aim(cam, Vector((0, 0, 0.92)), yaw, 3, 3.7) for yaw in (0, 35, 180)]
    obj = bpy.data.objects["Male"]
    shots.append(lambda: aim(cam, head_target(obj) + Vector((0, 0, -0.05)), 30, 5, 1.0))
    return render_grid(shots, 4, out)


def seams(out, cam):
    """Close-ups where texture seams or creases could show."""
    setup((560, 560))
    shots = []
    for name in BODIES:
        obj = bpy.data.objects[name]
        x0 = obj.matrix_world.translation.x
        head = head_target(obj)
        wrist = landmark(obj, "wrist")
        z_knee = 0.50 if name == "Male" else 0.48
        views = [
            (head + Vector((0, -0.02, -0.08)), 100, 0, 0.55),        # neck from the side
            (head + Vector((0.07, -0.03, 0.0)), 70, 10, 0.45),        # temple: face and head textures meet
            (Vector((x0 + 0.20, 0.0, head.z - 0.30)), 0, 35, 0.55),   # shoulder top and armpit
            (wrist + (Vector((x0, 0, 1.06)) - wrist) * 0.55 + Vector((0, 0.02, 0)), 20, 5, 0.6),  # elbow, inner arm
            (wrist, -60, 5, 0.45),                                     # wrist seam
            (Vector((x0 + 0.12, 0.02, z_knee)), -25, 5, 0.75),        # knee, inner leg seam
        ]
        for c, yaw, pitch, dist in views:
            shots.append(lambda c=c, yaw=yaw, pitch=pitch, dist=dist: aim(cam, c, yaw, pitch, dist))
    return render_grid(shots, 6, out)


SHEETS = ("overview", "parts", "faces", "eyes", "mouth", "hands", "feet", "seams", "wire")


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
