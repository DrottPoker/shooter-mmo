"""The rig of a base character: an armature with the shared Humanoid skeleton
(art/Skeletons/Humanoid.json) with its joints placed from the character's own geometry, and every
body part, eyeball, tooth row and the tongue bound to it. The character's file is the source: rig
places the joints again and solves the weights, weights only solves the weights from the joints the
file has, and sync only writes the body parts from the source body (art_pipeline.bodies). Each of
them writes the weights into the source body and its parts.

The character faces +Y, so its right side is +X: the bones ending in _R sit at +X.

Joints come from the cuts between the body parts and the landmarks the armature carries: the
elbow, wrist, knee and ankle from the rings where two body parts meet, the shoulder joint at the
centre of the shoulder's round cap, the joints of the fingers, the thumb and the toes from the rings
of each digit, the jaw hinge in front of the ears where the model review's mouth preview put it, and
the spine at fixed shares of the torso between the hip joints and the base of the neck.

Skin weights are solved once on the whole body, the parts joined at their cuts, by Blender's bone
heat. They are then limited per zone and per digit and shaped where the arms meet the torso; the
hips, knees and elbows get weights by rule from their anatomy, the jaw takes the lower face, and
every vertex
keeps at most four bones with weights that add up to one. Each part takes the weights of its
vertices from the whole body, so both sides of a cut carry the same weights and the cuts never
open. Eyeballs follow their eye bone, the upper teeth the head, and the lower teeth and the tongue
the jaw."""

import math
from collections import deque

import bpy
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree

from art_pipeline import bodies
from art_pipeline import skeleton as skeleton_definition

ZONES = bodies.ZONES
SIDES = (("R", 1.0), ("L", -1.0))
# The landmark digits from the little finger to the index finger, then the thumb.
DIGITS = ("Little", "Ring", "Middle", "Index", "Thumb")
SEGMENTS = ("Proximal", "Intermediate", "Distal")
# Rings of eight vertices counted from a digit's tip: the distal and proximal finger joints and
# the base ring at the web; the thumb's end joint and its base in the palm; the toes' base ring.
FINGER_LAYERS = {"dip": 4, "pip": 6, "base": 8}
THUMB_LAYERS = {"ip": 6, "mcp": 10}
TOE_BASE_LAYER = 6
RING_SIZE = 8
# Offsets are given for the male body (1.785 m) and scale with the body's height.
REFERENCE_HEIGHT = 1.785
# The hip joint sits this far above the crotch, near the groin crease, so the thigh swings out of
# the leg opening instead of rising into the belly.
HIP_ABOVE_CROTCH = 0.045
# Spine, Chest and UpperChest at these shares between the hip joints and the base of the neck,
# this far in front of the back.
SPINE_SHARES = (0.21, 0.47, 0.72)
SPINE_INSETS = (0.07, 0.065, 0.06)
# The ball of the shoulder joint sits this far below the top of the shoulder and inside the outer
# side of the deltoid.
SHOULDER_DEPTH = 0.045
MAX_INFLUENCES = 4
MIN_WEIGHT = 0.02
# Bones the zones may use; "_s" stands for the side of the vertex.
ZONE_BONES = {
    "Head": ("Head", "Neck"),
    "Neck": ("Neck", "Head", "UpperChest", "Shoulder_s"),
    "Torso": ("Hips", "Spine", "Chest", "UpperChest", "Neck", "Shoulder_s", "UpperArm_s"),
    "Pelvis": ("Hips", "Spine", "UpperLeg_s"),
    "UpperArms": ("UpperChest", "Shoulder_s", "UpperArm_s", "LowerArm_s"),
    "Forearms": ("UpperArm_s", "LowerArm_s", "Hand_s"),
    "Hands": ("LowerArm_s", "Hand_s"),
    "Thighs": ("Hips", "UpperLeg_s", "LowerLeg_s"),
    "Shins": ("UpperLeg_s", "LowerLeg_s", "Foot_s"),
    "Feet": ("LowerLeg_s", "Foot_s", "Toes_s"),
}
# Bones that the bone heat leaves out: their weights are set by rule.
RULE_BONES = ("Jaw", "Eye_L", "Eye_R")
# Shaping of the bone heat where limbs meet the body, so raising an arm or a leg does not drag the
# torso or the buttock along. Distances are measured to the cut where the two parts meet.
# The upper arm keeps at most this share of the torso at the armpit cut, fading out over this distance.
UPPER_ARM_ON_TORSO = (0.5, 0.06)
# The clavicle moves the skin over the collarbone and, at the back, over the shoulder blade: its
# share fades out from the first to the second distance below the line of the collarbone.
CLAVICLE_REACH = {"front": (0.01, 0.06), "back": (0.04, 0.12)}
SHAPE_REACH = 0.14
# The hip: the thigh moves the skin below a boundary that runs around it, with a wide blend so the
# top of the thigh and the buttock stretch instead of folding at the leg line: just above the groin
# in front, over the greater trochanter at the outer side, halfway up the buttock at the back and
# at the crotch on the inner side, with a short blend. Per angle around the thigh (0 front, 90 outer side, 180
# back, 270 inner side): the boundary's distance below the hip joint along the thigh (negative is
# above it) and the half width of the blend across it. The hip's shaping reaches this far down the
# thigh.
HIP_BOUNDARY = ((0, -0.02, 0.07), (90, -0.03, 0.07), (180, 0.0, 0.10), (270, 0.02, 0.03))
HIP_REGION = 0.22
# Between the legs the thighs' shares fade toward the lowest point of the crotch, down to this share
# at it, within this distance of it, on the pelvis and the thighs alike, and are then smoothed
# there: the crotch keeps its place, the skin around it stretches evenly, and no two neighbouring
# vertices differ much, which would cut a notch when a thigh is raised. A long blend down the
# inner thigh, far from the hip joint, would sink in instead.
CROTCH_RADIUS = 0.07
CROTCH_FLOOR = 0.0
CROTCH_SMOOTHING = 6
# The knee and the elbow blend from the upper to the lower bone across their joint, along the lower
# bone, over a half width on the side the joint folds to (the back of the knee, the inside of the
# elbow) and another on the opposite side, within this reach of the joint.
# A short blend at the fold keeps the thigh and the calf in shape (they may touch), a wider one at
# the front rounds the kneecap.
HINGES = (("UpperLeg", "LowerLeg", ("Thighs", "Shins"), (0.0, -1.0, 0.0), 0.04, 0.08, 0.14),
          ("UpperArm", "LowerArm", ("UpperArms", "Forearms"), (0.0, 1.0, 0.0), 0.03, 0.05, 0.10))
SMOOTH_ITERATIONS = 6
# The cloth chains. Skirt chains around the hips, by their angle from the front toward the right
# side; their tops sit this far above the hip joints and this far out from the body, and they hang
# to the ankles in four bones.
SKIRT_CHAINS = (("SkirtFront", 0.0, ""), ("SkirtFrontSide", 45.0, "_R"), ("SkirtSide", 90.0, "_R"),
                ("SkirtBackSide", 135.0, "_R"), ("SkirtBack", 180.0, ""))
SKIRT_ABOVE_HIP = 0.02
SKIRT_GAP = 0.01
SKIRT_BONES = 4
# Cloak chains across the back, by their distance from the centre line; their tops sit this far
# below the base of the neck and this far behind the body, and they hang in five bones to the
# middle of the calves, this share of the way from the knee to the ankle.
CLOAK_CHAINS = (("CloakCenter", 0.0, ""), ("CloakInner", 0.09, "_R"), ("CloakOuter", 0.18, "_R"))
CLOAK_BELOW_NECK = 0.04
CLOAK_GAP = 0.015
CLOAK_BONES = 5
CLOAK_DOWN_THE_SHIN = 0.5
# The hood chain starts this far behind the back of the head at eye height and steps down and back.
HOOD_GAP = 0.02
HOOD_STEPS = ((-0.03, -0.07), (-0.02, -0.07))


# ----------------------------------------------------------------------------------- geometry

def _world(obj):
    co = np.empty(len(obj.data.vertices) * 3)
    obj.data.vertices.foreach_get("co", co)
    m = np.array(obj.matrix_world)
    return co.reshape(-1, 3) @ m[:3, :3].T + m[:3, 3]


def _key(p):
    return tuple(int(round(c * 1e5)) for c in p)


def _part(label, zone):
    return bpy.data.objects[f"{label}_{zone}"]


def _cut(label, a, b, side=None):
    """Centroid of the ring where two parts meet, on one side (+1 right, -1 left) or whole."""
    keys = {_key(p) for p in _world(_part(label, a))}
    pts = [p for p in _world(_part(label, b)) if _key(p) in keys and (side is None or p[0] * side > 1e-4)]
    return Vector(np.mean(pts, 0))


def _section(label, zones, z, half_width):
    """Back and front (y) and the largest |x| of the horizontal section at height z through the
    parts, within |x| <= half_width."""
    found = []
    for zone in zones:
        obj = _part(label, zone)
        co = _world(obj)
        e = np.array([edge.vertices[:] for edge in obj.data.edges])
        a, b = co[e[:, 0]], co[e[:, 1]]
        dz = b[:, 2] - a[:, 2]
        ok = np.abs(dz) > 1e-12
        t = np.where(ok, (z - a[:, 2]) / np.where(ok, dz, 1.0), -1.0)
        hit = (t >= 0.0) & (t <= 1.0)
        p = a[hit] + (b[hit] - a[hit]) * t[hit, None]
        found.append(p[np.abs(p[:, 0]) <= half_width])
    p = np.concatenate(found)
    return float(p[:, 1].min()), float(p[:, 1].max()), float(np.abs(p[:, 0]).max())


def _adjacency(obj):
    adj = [[] for _ in obj.data.vertices]
    for edge in obj.data.edges:
        a, b = edge.vertices
        adj[a].append(b)
        adj[b].append(a)
    return adj


def _layers(obj, start, count, adj=None):
    """Vertices at graph distance 0 .. count-1 from the vertex nearest to start."""
    co = _world(obj)
    adj = adj or _adjacency(obj)
    first = int(np.argmin(np.linalg.norm(co - np.asarray(start), axis=1)))
    dist = {first: 0}
    queue = deque([first])
    while queue:
        v = queue.popleft()
        if dist[v] + 1 >= count:
            continue
        for w in adj[v]:
            if w not in dist:
                dist[w] = dist[v] + 1
                queue.append(w)
    layers = [[] for _ in range(count)]
    for v, d in dist.items():
        layers[d].append(v)
    return layers, co


def _ring_centre(layers, co, index, what):
    ring = layers[index]
    if len(ring) != RING_SIZE:
        raise RuntimeError(f"{what}: ring {index} has {len(ring)} vertices, expected {RING_SIZE}")
    return Vector(co[ring].mean(0))


def _points(root, key):
    values = root[f"lmw_{key}"]
    return [Vector(values[i:i + 3]) for i in range(0, len(values), 3)]


def _mirror(v):
    return Vector((-v.x, v.y, v.z))


def _shoulder_joint(label, guess, s):
    """Centre of the ball of the right shoulder joint: SHOULDER_DEPTH below the top of the shoulder
    and inside the outer side of the deltoid, halfway between the front and the back, found from
    the guess by casting rays to the surface."""
    points, faces = [], []
    for zone in ("UpperArms", "Torso", "Neck"):
        obj = _part(label, zone)
        faces += [[v + len(points) for v in p.vertices] for p in obj.data.polygons]
        points += [Vector(p) for p in _world(obj)]
    tree = BVHTree.FromPolygons(points, faces)

    def surface(origin, direction):
        found = tree.ray_cast(origin, Vector(direction), 0.4)[0]
        if found is None:
            raise RuntimeError(f"{label}: no shoulder surface {direction} of {tuple(origin)}")
        return found

    depth = SHOULDER_DEPTH * s
    c = guess.copy()
    for _ in range(4):
        c.z = surface(c, (0, 0, 1)).z - depth
        c.x = surface(c - Vector((0.03, 0, 0)), (1, 0, 0)).x - depth
        c.y = (surface(c, (0, 1, 0)).y + surface(c, (0, -1, 0)).y) / 2
    return c


def joints(label, shoulder_guess):
    """Head, tail and roll direction (the bone's Z axis) of every deform bone and socket, and the
    vertices of every digit, in the character's coordinates (root at the origin). The search for
    the right shoulder joint starts at shoulder_guess, such as where the rig had it."""
    root = bpy.data.objects[label]
    head_co = _world(_part(label, "Head"))
    height = float(head_co[:, 2].max())
    s = height / REFERENCE_HEIGHT
    fwd, up = Vector((0, 1, 0)), Vector((0, 0, 1))
    bones = {}

    # Spine from the hip joints to the base of the neck.
    pelvis = _world(_part(label, "Pelvis"))
    crotch_z = float(pelvis[np.abs(pelvis[:, 0]) < 1e-5][:, 2].min())  # lowest point on the centre line
    hip_z = crotch_z + HIP_ABOVE_CROTCH * s
    back, front, lateral = _section(label, ("Pelvis", "Thighs"), hip_z, 0.28)
    hip_y = (back + front) / 2
    neck_base = _cut(label, "Neck", "Torso")
    neck_base.x = 0.0
    spine = []
    for share, inset in zip(SPINE_SHARES, SPINE_INSETS):
        z = hip_z + share * (neck_base.z - hip_z)
        b, _f, _x = _section(label, ("Torso", "Pelvis"), z, 0.10)
        spine.append(Vector((0.0, b + inset * s, z)))
    hips = Vector((0.0, hip_y, hip_z))
    mouth = Vector(root["lmw_mouth_centre"][:3])
    head_z = mouth.z - 0.005 * s
    neck_back, _f, _x = _section(label, ("Neck", "Head"), head_z, 0.05)
    head = Vector((0.0, neck_back + 0.045 * s, head_z))
    chain = [("Hips", hips), ("Spine", spine[0]), ("Chest", spine[1]), ("UpperChest", spine[2]),
             ("Neck", neck_base), ("Head", head)]
    for (name, a), (_n, b) in zip(chain, chain[1:]):
        bones[name] = (a, b, fwd)
    bones["Head"] = (head, Vector((0.0, head.y, height)), fwd)

    # Jaw hinge in front of the ears, as in the model review's mouth preview; eyes at the
    # eyeball centres, looking forward.
    eyes = {side: o for o in root.children if "_Eye_" in o.name
            for side, sx in SIDES if o.matrix_world.translation.x * sx > 0}
    eye_y = sum(o.matrix_world.translation.y for o in eyes.values()) / 2
    hinge = Vector((0.0, eye_y - 0.068, mouth.z + 0.028))
    bones["Jaw"] = (hinge, Vector(root["lmw_chin_front"][:3]), up)
    for side, eye in eyes.items():
        c = eye.matrix_world.translation.copy()
        bones[f"Eye_{side}"] = (c, c + fwd * 0.025, up)

    # Right arm and leg; the left side mirrors them.
    shoulder = _shoulder_joint(label, Vector(shoulder_guess), s)
    clavicle = Vector((0.022 * s, neck_base.y + 0.05 * s, neck_base.z - 0.035 * s))
    elbow = _cut(label, "UpperArms", "Forearms", 1.0)
    wrist = _cut(label, "Forearms", "Hands", 1.0)
    right = {"Shoulder": (clavicle, shoulder, up), "UpperArm": (shoulder, elbow, fwd),
             "LowerArm": (elbow, wrist, fwd)}

    hands = _part(label, "Hands")
    adj = _adjacency(hands)
    tips = _points(root, "finger_tips")[:5]
    digit_verts = {}
    finger_joints = {}
    for digit, tip in zip(DIGITS, tips):
        last = (THUMB_LAYERS["mcp"] if digit == "Thumb" else FINGER_LAYERS["base"]) + 1
        layers, co = _layers(hands, tip, last, adj)
        digit_verts[digit] = sorted(v for layer in layers for v in layer)
        tip_point = Vector(co[layers[0][0]])
        if digit == "Thumb":
            ip = _ring_centre(layers, co, THUMB_LAYERS["ip"], f"{label} thumb")
            mcp = _ring_centre(layers, co, THUMB_LAYERS["mcp"], f"{label} thumb")
            cmc = wrist.lerp(mcp, 0.4)
            finger_joints[digit] = (cmc, mcp, ip, tip_point)
        else:
            dip = _ring_centre(layers, co, FINGER_LAYERS["dip"], f"{label} {digit}")
            pip = _ring_centre(layers, co, FINGER_LAYERS["pip"], f"{label} {digit}")
            base = _ring_centre(layers, co, FINGER_LAYERS["base"], f"{label} {digit}")
            mcp = base + (base - pip) * 0.55
            finger_joints[digit] = (mcp, pip, dip, tip_point)
    knuckles = [finger_joints[d][0] for d in ("Little", "Ring", "Middle", "Index")]
    along = (finger_joints["Middle"][0] - wrist).normalized()
    radial = (knuckles[3] - knuckles[0]).normalized()
    dorsal = along.cross(radial).normalized()
    if dorsal.x < 0:  # the back of the right hand faces outward in the A-pose
        dorsal = -dorsal
    right["Hand"] = (wrist, finger_joints["Middle"][0], dorsal)
    # Each finger bends toward its pad: its Z axis points from the pad to the nail at its end. The
    # thumb, which points forward and down beside the index finger, bends toward the little
    # finger, so it closes over the fingers of a fist instead of into the palm.
    nails = [(b - f).normalized() for b, f in zip(_points(root, "finger_back")[:4], _points(root, "finger_front")[:4])]
    nails.append(radial)
    for (digit, points), nail in zip(finger_joints.items(), nails):
        for segment, a, b in zip(SEGMENTS, points, points[1:]):
            right[f"{digit}{segment}"] = (a, b, nail)
    grip = sum(knuckles, Vector()) / 4 - dorsal * 0.028 * s
    right["Socket_MainHand"] = (grip, grip + radial * 0.08, -dorsal)

    hip = Vector((lateral / 2, hip_y, hip_z))
    knee = _cut(label, "Thighs", "Shins", 1.0)
    ankle = _cut(label, "Shins", "Feet", 1.0) - up * (0.03 * s)
    feet = _part(label, "Feet")
    adj = _adjacency(feet)
    bases, toe_tips = [], []
    for tip in _points(root, "toe_tips")[:5]:
        layers, co = _layers(feet, tip, TOE_BASE_LAYER + 1, adj)
        bases.append(_ring_centre(layers, co, TOE_BASE_LAYER, f"{label} toe"))
        toe_tips.append(Vector(co[layers[0][0]]))
    ball = sum(bases, Vector()) / 5
    toe_dir = Vector(((ball - ankle).x, (ball - ankle).y, 0.0)).normalized()
    ball = ball - toe_dir * (0.012 * s)
    toes_end = sum(toe_tips, Vector()) / 5
    toes_end.z = ball.z
    right.update({"UpperLeg": (hip, knee, fwd), "LowerLeg": (knee, ankle, fwd), "Foot": (ankle, ball, up),
                  "Toes": (ball, toes_end, up)})

    for name, (a, b, z) in right.items():
        if name.startswith("Socket_"):
            bones[name] = (a, b, z)
            bones["Socket_OffHand"] = (_mirror(a), _mirror(b), _mirror(z))
            continue
        bones[f"{name}_R"] = (a, b, z)
        bones[f"{name}_L"] = (_mirror(a), _mirror(b), _mirror(z))
    back_y, _f, _x = _section(label, ("Torso",), spine[2].z, 0.10)
    socket = Vector((0.0, back_y - 0.045 * s, spine[2].z))
    bones["Socket_Back"] = (socket, socket + up * 0.10, -fwd)
    eye_z = bones["Eye_R"][0].z
    bones.update(_chain_bones(label, s, hip_y, hip_z, knee, ankle, neck_base, head, eye_z))
    return bones, digit_verts


def _surface_tree(label, zones):
    points, faces = [], []
    for zone in zones:
        obj = _part(label, zone)
        faces += [[v + len(points) for v in p.vertices] for p in obj.data.polygons]
        points += [Vector(p) for p in _world(obj)]
    return BVHTree.FromPolygons(points, faces)


def _hit(label, tree, origin, direction, what):
    found = tree.ray_cast(origin, direction, 1.0)[0]
    if found is None:
        raise RuntimeError(f"{label}: no body surface for the {what} chain {tuple(direction)} of {tuple(origin)}")
    return found


def _hanging(head, end_z, count, roll):
    """A chain of count bones hanging straight down from head to the height end_z."""
    step = (head.z - end_z) / count
    joints_ = [head - Vector((0.0, 0.0, step * i)) for i in range(count + 1)]
    return [(a, b, roll) for a, b in zip(joints_, joints_[1:])]


def _chain_bones(label, s, hip_y, hip_z, knee, ankle, neck_base, head, eye_z):
    """The cloth chains, placed from the body: the skirt chains on a ring around the hips that
    hangs to the ankles, the cloak chains from the shoulder blades to the middle of the calves, and
    the hood chain behind the head. Each bone's Z axis points away from the body, the way its cloth
    faces."""
    bones = {}
    fwd = Vector((0.0, 1.0, 0.0))
    hips = _surface_tree(label, ("Pelvis", "Thighs", "Torso"))
    centre = Vector((0.0, hip_y, hip_z + SKIRT_ABOVE_HIP * s))
    for name, degrees, suffix in SKIRT_CHAINS:
        angle = math.radians(degrees)
        out = Vector((math.sin(angle), math.cos(angle), 0.0))
        top = _hit(label, hips, centre, out, name) + out * (SKIRT_GAP * s)
        for i, bone in enumerate(_hanging(top, ankle.z, SKIRT_BONES, out), 1):
            bones[f"{name}{i}{suffix}"] = bone
    back = _surface_tree(label, ("Torso",))
    z = neck_base.z - CLOAK_BELOW_NECK * s
    calf = knee.z + (ankle.z - knee.z) * CLOAK_DOWN_THE_SHIN
    for name, x, suffix in CLOAK_CHAINS:
        # From behind, so a chain beside narrow shoulders finds the back and not the arm in front.
        origin = Vector((x * s, neck_base.y - 1.0, z))
        top = _hit(label, back, origin, fwd, name) - fwd * (CLOAK_GAP * s)
        for i, bone in enumerate(_hanging(top, calf, CLOAK_BONES, -fwd), 1):
            bones[f"{name}{i}{suffix}"] = bone
    skull = _surface_tree(label, ("Head",))
    top = _hit(label, skull, Vector((0.0, head.y, eye_z)), -fwd, "Hood") - fwd * (HOOD_GAP * s)
    previous = top
    for i, (dy, dz) in enumerate(HOOD_STEPS, 1):
        point = previous + Vector((0.0, dy * s, dz * s))
        bones[f"Hood{i}"] = (previous, point, -fwd)
        previous = point
    for name in [n for n in bones if n.endswith("_R")]:
        a, b, roll = bones[name]
        bones[name[:-2] + "_L"] = (_mirror(a), _mirror(b), _mirror(roll))
    return bones


# ----------------------------------------------------------------------------------- armature

def meshes(root):
    """Every mesh of a character, its source body with them."""
    return sorted((o for o in root.children if o.type == "MESH"), key=lambda o: o.name)


def characters_children(root):
    """The meshes of a character that the rig binds: its parts, eyeballs, teeth, and tongue, without
    the source body, which takes the weights of the parts afterwards."""
    return [o for o in meshes(root) if not bodies.is_source_body(o)]


def normalize(label):
    """Names the eyeballs by the character's side: it faces +Y, so its right eye is at +X. Models
    built before this rule had the names the other way round."""
    root = bpy.data.objects[label]
    eyes = [o for o in root.children if "_Eye_" in o.name]
    for o in eyes:
        o.name = o.data.name = f"{o.name}_tmp"
    for o in eyes:
        o.name = o.data.name = f"{label}_Eye_{'R' if o.matrix_world.translation.x > 0 else 'L'}"


def strip(label):
    """Takes a rig off a character, if it has one: the root is an empty again, with the
    landmarks, and the meshes lose their weights and armature modifiers. Returns the root."""
    root = bpy.data.objects[label]
    children = meshes(root)
    for o in children:
        for md in [m for m in o.modifiers if m.type == "ARMATURE"]:
            o.modifiers.remove(md)
        o.vertex_groups.clear()
    for action in list(bpy.data.actions):
        bpy.data.actions.remove(action)
    if root.type == "EMPTY":
        return root
    collection = root.users_collection[0]
    props = {k: root[k] for k in root.keys()}
    armature = root.data
    root.name = f"{label}_rig"
    empty = bpy.data.objects.new(label, None)
    empty.empty_display_type = "PLAIN_AXES"
    empty.empty_display_size = 0.25
    collection.objects.link(empty)
    for k, v in props.items():
        empty[k] = v
    for o in children:
        basis = o.matrix_basis.copy()
        o.parent = empty
        o.matrix_parent_inverse.identity()
        o.matrix_basis = basis
    bpy.data.objects.remove(root)
    bpy.data.armatures.remove(armature)
    return empty


def _select_only(*objects):
    """Selects only the given objects and makes the first one active."""
    view_layer = bpy.context.view_layer
    view_layer.update()
    for o in bpy.context.scene.objects:
        o.select_set(o in objects)
    view_layer.objects.active = objects[0] if objects else None


def make_armature(label, skeleton, bones):
    """Replaces the root empty with an armature of the skeleton, named like the root and
    carrying its landmarks."""
    root = bpy.data.objects[label]
    collection = root.users_collection[0]
    props = {k: root[k] for k in root.keys()}
    children = meshes(root)
    root.name = f"{label}_empty"
    data = bpy.data.armatures.new(label)
    data.display_type = "OCTAHEDRAL"
    arm = bpy.data.objects.new(label, data)
    arm.show_in_front = True
    collection.objects.link(arm)
    for k, v in props.items():
        arm[k] = v
    for o in children:
        basis = o.matrix_basis.copy()
        o.parent = arm
        o.matrix_parent_inverse.identity()
        o.matrix_basis = basis
    bpy.data.objects.remove(root)

    _select_only(arm)
    bpy.ops.object.mode_set(mode="EDIT")
    edit = data.edit_bones
    for spec in skeleton.bones:
        head, tail, z_axis = bones[spec.name]
        bone = edit.new(spec.name)
        bone.head, bone.tail = head, tail
        bone.align_roll(z_axis)
        bone.use_deform = spec.deform
        if spec.parent is not None:
            bone.parent = edit[spec.parent]
            bone.use_connect = False
    bpy.ops.object.mode_set(mode="OBJECT")
    _assign_collections(arm, skeleton, skeleton.bones)
    return arm


def _assign_collections(arm, skeleton, specs):
    """Puts bones into the Deform, Sockets, and Cloth bone collections, which only sort them in
    Blender, and makes them turn by quaternions."""
    data = arm.data
    collections = {name: data.collections.get(name) or data.collections.new(name)
                   for name in ("Deform", "Sockets", "Cloth")}
    for spec in specs:
        kind = "Cloth" if spec.chain else "Deform" if spec.deform else "Sockets"
        collections[kind].assign(data.bones[spec.name])
        arm.pose.bones[spec.name].rotation_mode = "QUATERNION"


# A cloth chain bone lies where the rig rules put it when its ends and its Z axis are this close.
CHAIN_TOLERANCE = 1e-5


def _placed(bone, head, tail, z_axis):
    """Whether a bone already lies as the rig rules place it: its ends, and its Z axis along the
    part of z_axis across the bone, as align_roll turns it."""
    along = (tail - head).normalized()
    across = (z_axis - along * z_axis.dot(along)).normalized()
    own = bone.matrix_local.to_3x3().col[2]
    return ((bone.head_local - head).length <= CHAIN_TOLERANCE and (bone.tail_local - tail).length <= CHAIN_TOLERANCE
            and (own - across).length <= CHAIN_TOLERANCE)


def place_rule_bones(label, skeleton):
    """Adds the bones the skeleton has and the rig lacks, such as new cloth chains, and moves the cloth
    chains that do not lie where the rig rules now put them, which no skin moves with; every other
    bone stays where it is. Returns the names of the bones it added and of those it moved."""
    arm = bpy.data.objects[label]
    missing = [spec for spec in skeleton.bones if spec.name not in arm.data.bones]
    chains = [spec for spec in skeleton.bones if spec.chain and spec.name in arm.data.bones]
    bones, _digits = joints(label, arm.data.bones["UpperArm_R"].head_local)
    moved = [spec for spec in chains if not _placed(arm.data.bones[spec.name], *bones[spec.name])]
    if not missing and not moved:
        return [], []
    _select_only(arm)
    bpy.ops.object.mode_set(mode="EDIT")
    edit = arm.data.edit_bones
    for spec in missing:
        head, tail, z_axis = bones[spec.name]
        bone = edit.new(spec.name)
        bone.head, bone.tail = head, tail
        bone.align_roll(z_axis)
        bone.use_deform = spec.deform
        if spec.parent is not None:
            bone.parent = edit[spec.parent]
            bone.use_connect = False
    for spec in moved:
        head, tail, z_axis = bones[spec.name]
        bone = edit[spec.name]
        bone.head, bone.tail = head, tail
        bone.align_roll(z_axis)
    bpy.ops.object.mode_set(mode="OBJECT")
    _assign_collections(arm, skeleton, missing)
    return [spec.name for spec in missing], [spec.name for spec in moved]


# ------------------------------------------------------------------------------------ weights

def _whole_body(label):
    """The body parts joined at their cuts: positions, faces, and per part the index of each of
    its vertices in the whole body."""
    keys, points, faces, index = {}, [], [], {}
    for zone in ZONES:
        obj = _part(label, zone)
        co = _world(obj)
        local = []
        for p in co:
            k = _key(p)
            if k not in keys:
                keys[k] = len(points)
                points.append(p)
            local.append(keys[k])
        if len(set(local)) != len(local):
            raise RuntimeError(f"{obj.name} has vertices at the same place")
        index[zone] = np.array(local)
        faces += [tuple(local[v] for v in poly.vertices) for poly in obj.data.polygons]
    return np.array(points), faces, index


def _ring(label, a, b, side):
    """Positions where two parts meet, on one side."""
    keys = {_key(p) for p in _world(_part(label, a))}
    return np.array([p for p in _world(_part(label, b)) if _key(p) in keys and p[0] * side > 1e-4])


def _nearest(points, targets):
    return np.sqrt(((points[:, None, :] - targets[None, :, :]) ** 2).sum(2)).min(1)


def _smooth(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0.0, 1.0)
    return t * t * (3 - 2 * t)


def _move_excess(weights, rows, col, cap, targets):
    """Caps one bone's weight on the given vertices and gives what is above the cap to the
    strongest of the target bones there, or to the first target where none has weight."""
    excess = np.maximum(weights[rows, col] - cap, 0.0)
    weights[rows, col] -= excess
    held = weights[np.ix_(rows, targets)]
    pick = np.where(held.max(1) > 0, np.array(targets)[np.argmax(held, 1)], targets[0])
    np.add.at(weights, (rows, pick), excess)


def _around(axis, forward, outward):
    """Unit vectors perpendicular to an axis: forward and outward as far as they are."""
    f = forward - axis * (forward @ axis)
    f /= np.linalg.norm(f)
    o = outward - axis * (outward @ axis)
    o -= f * (o @ f)
    return f, o / np.linalg.norm(o)


def hip_weights(points, faces, index, weights, column, arm, s):
    """Sets the share of each thigh around the hip from HIP_BOUNDARY: the thigh takes the skin
    below the boundary, the pelvis keeps the rest with its own bones from the bone heat."""
    bones = arm.data.bones
    rows = np.unique(np.concatenate([index["Pelvis"], index["Thighs"]]))
    pelvis = points[index["Pelvis"]]
    centre = pelvis[np.abs(pelvis[:, 0]) < 1e-5]
    crotch_point = centre[np.argmin(centre[:, 2])]
    to_crotch = np.linalg.norm(points[rows] - crotch_point, axis=1)
    crotch = CROTCH_FLOOR + (1.0 - CROTCH_FLOOR) * _smooth(0.0, CROTCH_RADIUS * s, to_crotch)
    angles = [a for a, _b, _w in HIP_BOUNDARY] + [360.0]
    below = [b for _a, b, _w in HIP_BOUNDARY] + [HIP_BOUNDARY[0][1]]
    widths = [w for _a, _b, w in HIP_BOUNDARY] + [HIP_BOUNDARY[0][2]]
    share, inside = {}, {}
    for side, sx in SIDES:
        bone = bones[f"UpperLeg_{side}"]
        hip, knee = np.array(bone.head_local), np.array(bone.tail_local)
        axis = (knee - hip) / np.linalg.norm(knee - hip)
        forward, outward = _around(axis, np.array([0.0, 1.0, 0.0]), np.array([sx, 0.0, 0.0]))
        v = points[rows] - hip
        t = v @ axis
        radial = v - t[:, None] * axis
        phi = np.degrees(np.arctan2(radial @ outward, radial @ forward)) % 360.0
        b = np.interp(phi, angles, below) * s
        w = np.interp(phi, angles, widths) * s
        thigh = _smooth(b - w, b + w, t) * crotch
        inside[side] = (points[rows, 0] * sx > -1e-6) & (t < HIP_REGION * s)
        share[side] = np.where(inside[side], thigh, 0.0)
    region = inside["R"] | inside["L"]
    selected = rows[region]
    legs = [column[f"{bone}_{side}"] for bone in ("UpperLeg", "LowerLeg") for side, _sx in SIDES]
    rest = weights[selected].copy()
    rest[:, legs] = 0.0
    total = rest.sum(1, keepdims=True)
    hips = np.zeros_like(rest)
    hips[:, column["Hips"]] = 1.0
    rest = np.where(total > 1e-9, rest / np.maximum(total, 1e-9), hips)
    thighs = share["R"][region] + share["L"][region]
    weights[selected] = rest * (1.0 - thighs)[:, None]
    for side, _sx in SIDES:
        weights[selected, column[f"UpperLeg_{side}"]] += share[side][region]

    # Smooth the weights around the crotch over the body's edges.
    near = rows[to_crotch < CROTCH_RADIUS * s * 1.5]
    neighbours = {v: set() for v in near}
    for face in faces:
        for i, v in enumerate(face):
            if v in neighbours:
                neighbours[v].update((face[i - 1], face[(i + 1) % len(face)]))
    for _ in range(CROTCH_SMOOTHING):
        mean = np.array([weights[list(neighbours[v])].mean(0) for v in near])
        weights[near] = 0.5 * weights[near] + 0.5 * mean
    return weights


def hinge_weights(points, index, weights, column, arm, s):
    """The knees and the elbows: the lower bone's share rises smoothly across the joint, along the
    lower bone, so the joint bends over a band about as wide as it is thick."""
    bones = arm.data.bones
    for upper, lower, zones, fold, fold_width, outer_width, reach in HINGES:
        rows = np.unique(np.concatenate([index[z] for z in zones]))
        for side, sx in SIDES:
            bone = bones[f"{lower}_{side}"]
            joint, end = np.array(bone.head_local), np.array(bone.tail_local)
            axis = (end - joint) / np.linalg.norm(end - joint)
            towards, _outward = _around(axis, np.array(fold), np.array([sx, 0.0, 0.0]))
            v = points[rows] - joint
            t = v @ axis
            radial = v - t[:, None] * axis
            cos = (radial @ towards) / np.maximum(np.linalg.norm(radial, axis=1), 1e-9)
            width = (outer_width + (fold_width - outer_width) * (cos + 1.0) / 2.0) * s
            near = (points[rows, 0] * sx > 1e-6) & (np.abs(t) < reach * s)
            selected = rows[near]
            share = _smooth(-width[near], width[near], t[near])
            weights[selected] = 0.0
            weights[selected, column[f"{upper}_{side}"]] = 1.0 - share
            weights[selected, column[f"{lower}_{side}"]] = share
    return weights


def shape_weights(label, arm, points, faces, index, weights, allowed, column):
    """Limits the bone heat where the arms meet the torso, then smooths the weights there, so each
    arm bends its own skin and leaves the chest and the back in place."""
    bones = arm.data.bones
    near = np.zeros(len(points), bool)
    for side, sx in SIDES:
        on = points[:, 0] * sx > -1e-6  # this side and the centre line
        torso = index["Torso"][on[index["Torso"]]]
        ring = _ring(label, "Torso", "UpperArms", sx)
        d = _nearest(points[torso], ring)
        share, reach = UPPER_ARM_ON_TORSO
        spine = [column[n] for n in ("UpperChest", "Chest", "Spine")]
        _move_excess(weights, torso, column[f"UpperArm_{side}"], share * (1 - _smooth(0.0, reach, d)), spine)
        clavicle = bones[f"Shoulder_{side}"]
        a, b = np.array(clavicle.head_local), np.array(clavicle.tail_local)
        t = np.clip((points[torso, 0] - a[0]) / (b[0] - a[0]), 0.0, 1.0)
        below = a[2] + t * (b[2] - a[2]) - points[torso, 2]
        front = points[torso, 1] > b[1]
        lo = np.where(front, CLAVICLE_REACH["front"][0], CLAVICLE_REACH["back"][0])
        hi = np.where(front, CLAVICLE_REACH["front"][1], CLAVICLE_REACH["back"][1])
        _move_excess(weights, torso, column[f"Shoulder_{side}"], 1 - _smooth(lo, hi, below),
                     [column["UpperChest"], column["Neck"]])
        near[torso[d < SHAPE_REACH]] = True
        arm_rows = index["UpperArms"][on[index["UpperArms"]]]
        near[arm_rows[_nearest(points[arm_rows], ring) < SHAPE_REACH]] = True

    # Laplacian smoothing of the reshaped weights over the body's edges.
    neighbours = [set() for _ in points]
    for face in faces:
        for i, v in enumerate(face):
            neighbours[v].update((face[i - 1], face[(i + 1) % len(face)]))
    rows = np.where(near)[0]
    for _ in range(SMOOTH_ITERATIONS):
        mean = np.array([weights[list(neighbours[v])].mean(0) for v in rows])
        weights[rows] = np.where(allowed[rows], 0.5 * weights[rows] + 0.5 * mean, 0.0)
        weights[rows] /= weights[rows].sum(1, keepdims=True)
    return weights


def _segment_distance(points, a, b):
    ab = b - a
    t = np.clip(((points - a) @ ab) / max(ab @ ab, 1e-12), 0.0, 1.0)
    return np.linalg.norm(points - (a + t[:, None] * ab), axis=1)


def _jaw_share(label, points):
    """How much of the head each point gives to the jaw: below the line between the lips (with a
    soft band beyond the mouth corners so the cheeks stretch), above the throat, and in front of
    the hinge. The same rule as the model review's mouth preview."""
    root = bpy.data.objects[label]
    up, lo = root["lmw_upper_lip_line"], root["lmw_lower_lip_line"]
    line = sorted((abs(up[i]), (up[i + 2] + lo[i + 2]) / 2) for i in range(0, len(up), 3))
    xs = np.array([x for x, _z in line])
    zs = np.array([z for _x, z in line])
    corner_x = xs[-1]
    chin_z = root["lmw_chin_bottom"][2]
    hinge_y = bpy.data.armatures[label].bones["Jaw"].head_local.y

    def smooth(e0, e1, x):
        t = np.clip((x - e0) / (e1 - e0), 0.0, 1.0)
        return t * t * (3 - 2 * t)

    ax = np.abs(points[:, 0])
    line_z = np.interp(np.minimum(ax, corner_x), xs, zs)
    band = 0.0006 + np.maximum(ax - corner_x, 0.0) * 0.6
    return (smooth(line_z + band, line_z - band, points[:, 2]) * smooth(chin_z - 0.045, chin_z - 0.012, points[:, 2])
            * smooth(hinge_y - 0.015, hinge_y + 0.02, points[:, 1]))


def solve_weights(label, arm, skeleton, digit_verts):
    """Weights of every whole-body vertex as an (N, bones) array over the skeleton's deform bones,
    and the whole body's index of every part's vertices."""
    names = skeleton.deform_bones
    column = {n: i for i, n in enumerate(names)}
    points, faces, index = _whole_body(label)
    mesh = bpy.data.meshes.new(f"{label}_weights")
    mesh.from_pydata([tuple(p) for p in points], [], faces)
    temp = bpy.data.objects.new(f"{label}_weights", mesh)
    bpy.context.scene.collection.objects.link(temp)
    # The cloth chains move garments only, never the body.
    for bone in arm.data.bones:
        bone.use_deform = (bone.name in column and bone.name not in RULE_BONES
                           and bone.name not in skeleton.chain_bones)
    _select_only(arm, temp)
    bpy.ops.object.parent_set(type="ARMATURE_AUTO")
    weights = np.zeros((len(points), len(names)))
    groups = {g.index: column[g.name] for g in temp.vertex_groups if g.name in column}
    for v in mesh.vertices:
        for g in v.groups:
            if g.group in groups:
                weights[v.index, groups[g.group]] = g.weight
    for bone in arm.data.bones:
        bone.use_deform = skeleton.bone(bone.name).deform
    bpy.data.objects.remove(temp)
    bpy.data.meshes.remove(mesh)

    # Allowed bones per vertex: its zone's bones on its own side, and per digit its own bones.
    allowed = np.zeros_like(weights, dtype=bool)
    sides = np.where(points[:, 0] > 1e-3, 1, np.where(points[:, 0] < -1e-3, -1, 0))
    for zone, bones in ZONE_BONES.items():
        idx = index[zone]
        for name in bones:
            if name.endswith("_s"):
                for side, sx in SIDES:
                    on = idx[(sides[idx] == sx) | (sides[idx] == 0)]
                    allowed[on, column[name[:-2] + "_" + side]] = True
            else:
                allowed[idx, column[name]] = True
    hand_idx = index["Hands"]
    for side, sx in SIDES:
        on_side = hand_idx[sides[hand_idx] == sx]
        in_digit = np.zeros(len(points), bool)
        for digit, verts in digit_verts.items():
            idx = hand_idx[verts] if sx > 0 else _mirrored(points, hand_idx[verts])
            in_digit[idx] = True
            for segment in SEGMENTS:
                allowed[idx, column[f"{digit}{segment}_{side}"]] = True
        palm = on_side[~in_digit[on_side]]
        for digit in DIGITS:
            allowed[palm, column[f"{digit}Proximal_{side}"]] = True
    weights = np.where(allowed, weights, 0.0)

    # Vertices whose heat went only to bones they may not use take their nearest allowed bone.
    empty = np.where(weights.sum(1) < 1e-6)[0]
    if len(empty):
        dist = np.full((len(empty), len(names)), np.inf)
        for name, i in column.items():
            bone = arm.data.bones[name]
            dist[:, i] = _segment_distance(points[empty], np.array(bone.head_local), np.array(bone.tail_local))
        dist = np.where(allowed[empty], dist, np.inf)
        weights[empty, np.argmin(dist, 1)] = 1.0

    weights /= weights.sum(1, keepdims=True)
    weights = shape_weights(label, arm, points, faces, index, weights, allowed, column)
    scale = float(points[:, 2].max()) / REFERENCE_HEIGHT
    weights = hip_weights(points, faces, index, weights, column, arm, scale)
    weights = hinge_weights(points, index, weights, column, arm, scale)
    head_idx = np.concatenate([index["Head"], index["Neck"]])
    share = _jaw_share(label, points[head_idx]) * weights[head_idx, column["Head"]]
    weights[head_idx, column["Jaw"]] += share
    weights[head_idx, column["Head"]] -= share
    return limit(symmetrize(points, weights, names), names), index


def _mirrored(points, idx):
    """The whole-body vertices that mirror the given ones across x = 0."""
    lookup = {_key(p): i for i, p in enumerate(points)}
    return np.array([lookup[_key((-points[i][0], points[i][1], points[i][2]))] for i in idx])


def _mirror_columns(names):
    """For every bone, the column of its mirror bone (itself on the centre line)."""
    flip = {"_L": "_R", "_R": "_L"}
    return np.array([names.index(n[:-2] + flip[n[-2:]]) if n[-2:] in flip else i for i, n in enumerate(names)])


def symmetrize(points, weights, names):
    """The left side takes the weights of its mirror vertices on the right, and the centre line the
    mean of both sides, so the bodies stay exactly mirror symmetric when they bend."""
    perm = _mirror_columns(names)
    mirror = _mirrored(points, np.arange(len(points)))
    w = weights.copy()
    left = points[:, 0] < -1e-6
    centre = np.abs(points[:, 0]) <= 1e-6
    w[left] = weights[mirror[left]][:, perm]
    w[centre] = (weights[centre] + weights[centre][:, perm]) / 2
    return w


def limit(weights, names):
    """At most four bones per vertex, none below MIN_WEIGHT, adding up to one. A bone and its mirror
    bone are kept or dropped together, so symmetric weights stay symmetric."""
    perm = _mirror_columns(names)
    w = weights.copy()
    w[w < MIN_WEIGHT] = 0.0
    for v in np.where((w > 0).sum(1) > MAX_INFLUENCES)[0]:
        units = sorted({tuple(sorted((i, int(perm[i])))) for i in np.nonzero(w[v])[0]},
                       key=lambda u: (-sum(w[v, i] for i in set(u)), u))
        kept, count = [], 0
        for unit in units:
            size = len(set(unit))
            if count + size <= MAX_INFLUENCES:
                kept += list(set(unit))
                count += size
        row = np.zeros_like(w[v])
        row[kept] = w[v, kept]
        w[v] = row
    w /= w.sum(1, keepdims=True)
    return w


def bind(label, arm, skeleton, digit_verts):
    """Binds every mesh of the character to the armature."""
    names = skeleton.deform_bones
    weights, index = solve_weights(label, arm, skeleton, digit_verts)
    rigid = {}
    for o in characters_children(arm):
        if o.get("zone") is None:
            if "_Eye_" in o.name:
                rigid[o.name] = f"Eye_{o.name[-1]}"
            elif "Teeth_Upper" in o.name:
                rigid[o.name] = "Head"
            else:
                rigid[o.name] = "Jaw"
    for o in characters_children(arm):
        if o.name in rigid:
            o.vertex_groups.clear()
            group = o.vertex_groups.new(name=rigid[o.name])
            group.add(list(range(len(o.data.vertices))), 1.0, "REPLACE")
        else:
            bodies.set_weights(o, weights[index[o["zone"]]], names)
        bodies.bind(o, arm)


def _save(out):
    _select_only()
    bpy.data.orphans_purge(do_local_ids=True, do_linked_ids=True, do_recursive=True)
    bpy.ops.wm.save_as_mainfile(filepath=out, compress=True, relative_remap=False)


def sync(path, label, skeleton_path, out):
    """Writes the body parts of a character from its source body, with the body's weights, adds the
    bones the skeleton has and the rig lacks, places the cloth chains by the rig rules, and saves
    the file."""
    bpy.ops.wm.open_mainfile(filepath=path)
    skeleton = skeleton_definition.load(skeleton_path)
    parts = bodies.write_parts(label, skeleton.deform_bones)
    added, moved = place_rule_bones(label, skeleton)
    _save(out)
    print(f"SYNCED {label}: {len(parts)} parts, {len(added)} new bones, {len(moved)} cloth chain bones moved -> {out}")


def weights(path, label, skeleton_path, out):
    """Solves the skin weights of a character again from the joints of its rig, writes them into
    its source body and its parts, and saves the file."""
    bpy.ops.wm.open_mainfile(filepath=path)
    skeleton = skeleton_definition.load(skeleton_path)
    names = skeleton.deform_bones
    normalize(label)
    bodies.write_parts(label, names)
    place_rule_bones(label, skeleton)
    arm = bpy.data.objects[label]
    _bones, digit_verts = joints(label, arm.data.bones["UpperArm_R"].head_local)
    bind(label, arm, skeleton, digit_verts)
    bodies.body_weights_from_parts(label, names)
    bodies.write_parts(label, names)
    _save(out)
    print(f"WEIGHTED {label}: {len(arm.data.bones)} bones -> {out}")


def rig(path, label, skeleton_path, out):
    """Places the joints of a character again from its model, starting the shoulder's search where
    the rig had it, solves the skin weights, writes them into its source body and its parts, and
    saves the file. The clips of animation libraries keyed on the old rig need a new review."""
    bpy.ops.wm.open_mainfile(filepath=path)
    skeleton = skeleton_definition.load(skeleton_path)
    names = skeleton.deform_bones
    normalize(label)
    shoulder_guess = bpy.data.objects[label].data.bones["UpperArm_R"].head_local.copy()
    bodies.write_parts(label, names)
    strip(label)
    bones, digit_verts = joints(label, shoulder_guess)
    arm = make_armature(label, skeleton, bones)
    bind(label, arm, skeleton, digit_verts)
    bodies.body_weights_from_parts(label, names)
    bodies.bind(bodies.source_body(label), arm)
    bodies.write_parts(label, names)
    _save(out)
    print(f"RIGGED {label}: {len(arm.data.bones)} bones -> {out}")
