"""The plain skin of a rebuilt body until it is painted, as images, so everything that needs an
image has one: the paint layers a canvas of the skin's size, and the game the face and the eyes it
offers a character. The body and every face are one flat colour, and on a face the inside of the
mouth is red. An eye is an eyeball seen from in front, as its UV map lays it out: white, with an
iris and a pupil on its front pole.

Every function returns straight sRGB pixels as (height, width, 4) in 0..1, rows from the bottom,
the way an image holds them. This file needs numpy and not Blender."""

import numpy as np

# The skin's flat colour and the size of the body's and the head's image; the body's image is
# twice as wide as high, as its islands are laid out.
SKIN = "#C9A084"
BODY_SIZE, FACE_SIZE, EYE_SIZE = (4096, 2048), (2048, 2048), (512, 512)
# The inside of the mouth, the inner lips and the cavity behind them: a red a little darker than
# the tongue's, which lies in it. Its colour reaches this many texels past its island's edge, less
# than half the room between two islands, so no skin shows inside the mouth from any distance.
MOUTH = "#8C4347"
BLEED = 4.0
# What the game offers of each body, by the names a stored character may hold. The first of each
# is the image of the material itself, the others are its variants.
FACES = {"Male": ("Stubble", "Clean"), "Female": ("Blush", "Freckles")}
EYES = {"Male": ("Teal", "Brown"), "Female": ("Teal", "Hazel")}
IRIS_COLOURS = {"Teal": "#3F8F8E", "Brown": "#6B4428", "Hazel": "#8A6A34"}
# The eyeball: the white at its front and toward its equator, where the lids' shade and the blood
# vessels make it darker and redder.
EYE_WHITE, EYE_EDGE = "#E9E6E0", "#D9B8B0"
# An eyeball's UV map runs from its front pole at the image's middle to its equator at the image's
# edge, even in the angle from the line of sight. The iris reaches IRIS degrees from the line of
# sight, as an iris of 11.7 mm does on an eyeball of 24 mm, and the pupil PUPIL degrees. The iris
# ends in a dark ring this wide, as a share of its radius, and has this many fibres around, which
# are this much lighter and darker than its colour.
IRIS, PUPIL = 29.0, 9.5
IRIS_RING, IRIS_FIBRES, IRIS_CONTRAST = 0.16, 37, 0.10
# Edges are this many texels soft, so no step shows.
SOFT = 1.5


def rgb(hex_colour):
    """An sRGB colour "#RRGGBB" as three numbers in 0..1."""
    return np.array([int(hex_colour[k:k + 2], 16) / 255.0 for k in (1, 3, 5)])


def flat(size, hex_colour):
    """An image of one colour."""
    width, height = size
    image = np.ones((height, width, 4), np.float32)
    image[..., :3] = rgb(hex_colour)
    return image


def painted(image, polygons, hex_colour, bleed=BLEED):
    """The image with polygons in one colour, and bleed texels around each: every polygon is its
    corners on the image, (corners, 2), from 0 at the left and the bottom to 1 at the right and
    the top, as a UV map gives a face."""
    out = image.copy()
    height, width = out.shape[:2]
    colour = rgb(hex_colour)
    for polygon in polygons:
        corners = np.asarray(polygon, float) * (width, height)
        low = np.maximum(np.floor(corners.min(0) - bleed).astype(int), 0)
        high = np.minimum(np.ceil(corners.max(0) + bleed).astype(int), (width, height))
        if (high <= low).any():
            continue
        x, y = np.meshgrid(np.arange(low[0], high[0]) + 0.5, np.arange(low[1], high[1]) + 0.5)
        inside = np.zeros(x.shape, bool)
        nearest = np.full(x.shape, np.inf)
        for a, b in zip(corners, np.roll(corners, -1, axis=0)):
            # A texel is inside when a line from it to the right crosses the outline an odd number
            # of times.
            if a[1] != b[1]:
                inside ^= ((a[1] > y) != (b[1] > y)) & (x < a[0] + (b[0] - a[0]) * (y - a[1]) / (b[1] - a[1]))
            along = b - a
            share = np.clip(((x - a[0]) * along[0] + (y - a[1]) * along[1]) / max(float(along @ along), 1e-18), 0.0, 1.0)
            nearest = np.minimum(nearest, np.hypot(x - (a[0] + share * along[0]), y - (a[1] + share * along[1])))
        out[low[1]:high[1], low[0]:high[0], :3][inside | (nearest <= bleed)] = colour
    return out


def _step(edge, distance, soft):
    """1 inside an edge and 0 outside it, over soft texels' worth of distance."""
    return np.clip((edge - distance) / soft + 0.5, 0.0, 1.0)


def eye(size, iris_colour):
    """An eyeball seen from in front: the white, darker and redder toward the equator, the iris in
    its colour with fibres and a dark ring around it, and the pupil."""
    width, height = size
    u = (np.arange(width) + 0.5) / width - 0.5
    v = (np.arange(height) + 0.5) / height - 0.5
    x, y = np.meshgrid(u, v)
    # Degrees from the line of sight: the image's edge lies at the equator.
    degrees = np.hypot(x, y) / 0.5 * 90.0
    turn = np.arctan2(y, x)
    soft = SOFT / width / 0.5 * 90.0

    toward_equator = np.clip((degrees - IRIS) / (90.0 - IRIS), 0.0, 1.0)[..., None] ** 1.5
    colour = rgb(EYE_WHITE) * (1.0 - toward_equator) + rgb(EYE_EDGE) * toward_equator

    # The iris: lighter toward the pupil, fibres around, and a dark ring at its edge.
    within = degrees / IRIS
    fibres = np.sin(turn * IRIS_FIBRES) * 0.6 + np.sin(turn * (IRIS_FIBRES * 2 + 5) + 1.3) * 0.4
    shade = 1.0 + IRIS_CONTRAST * fibres * np.clip(within * 1.5, 0.0, 1.0) + 0.18 * (1.0 - within)
    ring = np.clip((within - (1.0 - IRIS_RING)) / IRIS_RING, 0.0, 1.0)
    shade = shade * (1.0 - 0.55 * ring * ring)
    iris = np.clip(rgb(iris_colour) * shade[..., None], 0.0, 1.0)
    in_iris = _step(IRIS, degrees, soft)[..., None]
    colour = colour * (1.0 - in_iris) + iris * in_iris

    in_pupil = _step(PUPIL, degrees, soft)[..., None]
    colour = colour * (1.0 - in_pupil) + np.array([0.02, 0.02, 0.02]) * in_pupil

    image = np.ones((height, width, 4), np.float32)
    image[..., :3] = colour
    return image


def images(label, mouth=()):
    """Every image of a body's skin by its name: the body's, a face per face it offers, and an eye
    per eye colour it offers. mouth is the faces of the inside of the mouth as polygons on the
    face's image, which every face shows in red."""
    out = {f"T_Body_{label}": flat(BODY_SIZE, SKIN)}
    face_image = painted(flat(FACE_SIZE, SKIN), mouth, MOUTH)
    for face in FACES[label]:
        out[f"T_Face_{label}_{face}"] = face_image.copy()
    for colour in EYES[label]:
        out[f"T_Eye_{label}_{colour}"] = eye(EYE_SIZE, IRIS_COLOURS[colour])
    return out


def own_image(part, label):
    """The name of the image a skin material shows itself, or None for a material of one colour."""
    if part == "Body":
        return f"T_Body_{label}"
    if part == "Face":
        return f"T_Face_{label}_{FACES[label][0]}"
    if part == "Eye":
        return f"T_Eye_{label}_{EYES[label][0]}"
    return None


def variants(label):
    """The body's variants as its manifest lists them: per material, the images it may show in
    place of its own."""
    return [{"material": f"M_Eye_{label}", "images": [f"T_Eye_{label}_{c}" for c in EYES[label][1:]]},
            {"material": f"M_Face_{label}", "images": [f"T_Face_{label}_{f}" for f in FACES[label][1:]]}]
