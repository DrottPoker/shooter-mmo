"""Measures how closely a model follows its reference pictures, the image empties of the file's
Reference collection. Each picture is seen by an orthographic camera exactly as the picture faces,
the model's silhouette is rendered from there, and the two figures are compared: their overlap, the
share of the picture's figure that the model leaves out, the share the model adds, and an overlay
image of both. It is a measure, not a rule, and the file is never saved."""

import os
import tempfile

import bpy
import numpy as np
from mathutils import Vector

from . import preview
from .scene import ArtPipelineError, descendants, export_objects

REFERENCE_COLLECTION = "Reference"
# A picture is compared at its own size, or with its larger side at this many pixels when larger.
MAX_SIZE = 2048
# A pixel with alpha above this belongs to the figure: of a picture with transparency, and of the
# rendered model.
ALPHA = 0.5
# A pixel of an opaque picture belongs to the figure when its color is this far from the background.
BACKGROUND_DISTANCE = 0.045
# The background of an opaque picture: overall from squares this many pixels wide in its top
# corners, and per row from strips this wide at its left and right edges.
CORNER = 12
EDGE = 6
# The bottom rows of an opaque picture, often a ground line, are left out of its figure.
GROUND_SHARE = 0.02
# The overlay: where both have the figure, only the picture, only the model, and neither.
BOTH_COLOR = (0.25, 0.75, 0.3)
PICTURE_COLOR = (0.9, 0.2, 0.2)
MODEL_COLOR = (0.2, 0.4, 0.95)
NEITHER_GREY = 0.12


def figure_mask(pixels: np.ndarray) -> np.ndarray:
    """The figure of a picture given as RGBA rows from the bottom up. A picture with transparency
    (any alpha below ALPHA) has its figure where the alpha is above it. In an opaque picture it is
    every pixel whose color differs by more than BACKGROUND_DISTANCE from the background, both from
    the background of its top corners and from that of its row's left and right edges, since a
    painted background is often a soft gradient; the bottom GROUND_SHARE of the rows is left out."""
    alpha = pixels[..., 3]
    if (alpha < ALPHA).any():
        return alpha > ALPHA
    rgb = pixels[..., :3]
    height = rgb.shape[0]
    # Rows run bottom up, so the top corners are the last rows.
    corners = np.concatenate([rgb[-CORNER:, :CORNER].reshape(-1, 3), rgb[-CORNER:, -CORNER:].reshape(-1, 3)])
    background = np.median(corners, axis=0)
    distance = np.linalg.norm(rgb - background, axis=2)
    edge = (rgb[:, :EDGE].mean(axis=1) + rgb[:, -EDGE:].mean(axis=1)) / 2.0
    row_distance = np.linalg.norm(rgb - edge[:, None, :], axis=2)
    mask = np.minimum(distance, row_distance) > BACKGROUND_DISTANCE
    mask[:int(height * GROUND_SHARE)] = False
    return mask


def resample(mask: np.ndarray, width: int, height: int) -> np.ndarray:
    """The mask at another size, each new pixel taking the old pixel under its middle."""
    rows = ((np.arange(height) + 0.5) * mask.shape[0] / height).astype(np.int64).clip(0, mask.shape[0] - 1)
    columns = ((np.arange(width) + 0.5) * mask.shape[1] / width).astype(np.int64).clip(0, mask.shape[1] - 1)
    return mask[rows[:, None], columns[None, :]]


def overlap(picture: np.ndarray, model: np.ndarray) -> tuple[float, float, float]:
    """The overlap of two figures, intersection over union, then the shares of the picture's figure
    that only the picture has and that only the model has."""
    union = int(np.logical_or(picture, model).sum())
    figure = int(picture.sum())
    both = float(np.logical_and(picture, model).sum() / union) if union else 0.0
    if not figure:
        return both, 0.0, 0.0
    return (both, float((picture & ~model).sum() / figure), float((~picture & model).sum() / figure))


def overlay(picture: np.ndarray, model: np.ndarray) -> np.ndarray:
    """RGBA rows of both figures: green where both have it, red where only the picture has it, and
    blue where only the model has it, over dark grey."""
    out = np.full(picture.shape + (4,), NEITHER_GREY, dtype=np.float32)
    out[..., 3] = 1.0
    out[picture & model, :3] = BOTH_COLOR
    out[picture & ~model, :3] = PICTURE_COLOR
    out[~picture & model, :3] = MODEL_COLOR
    return out


def picture_middle(width: int, height: int, size: float, offset: tuple[float, float]) -> tuple[float, float]:
    """The middle of an image empty's picture in the empty's own axes: the picture's larger side is
    the display size, and it spans from its offset to the offset plus one of its width and height."""
    longest = max(width, height)
    return ((offset[0] + 0.5) * size * width / longest, (offset[1] + 0.5) * size * height / longest)


def render_size(width: int, height: int) -> tuple[int, int]:
    """The size a picture is compared at: its own, or with its larger side MAX_SIZE when larger."""
    scale = min(1.0, MAX_SIZE / max(width, height))
    return max(1, round(width * scale)), max(1, round(height * scale))


def pictures() -> list[bpy.types.Object]:
    """The image empties of the Reference collection that show an image."""
    collection = bpy.data.collections.get(REFERENCE_COLLECTION)
    if collection is None:
        return []
    return [o for o in collection.all_objects
            if o.type == "EMPTY" and o.empty_display_type == "IMAGE" and o.data is not None]


def _pixels(image: bpy.types.Image) -> np.ndarray:
    width, height = image.size
    data = np.empty(width * height * 4, dtype=np.float32)
    image.pixels.foreach_get(data)
    return data.reshape(height, width, 4)


def _model(root_name: str) -> list[bpy.types.Object]:
    """Every mesh of the Export collection below the root object, the root among them."""
    exported = set(export_objects())
    root = bpy.data.objects.get(root_name)
    if root is None or root not in exported:
        raise ArtPipelineError(f'The Export collection has no object "{root_name}".')
    meshes = [o for o in descendants(root) if o in exported and o.type == "MESH"]
    if not meshes:
        raise ArtPipelineError(f'"{root_name}" has no mesh in the Export collection to compare.')
    return meshes


def compare(name: str, root_name: str, output: str) -> list[dict]:
    """Compares the model below the root object with every reference picture and writes an overlay
    per picture, output/<name>_reference_<picture>.png; returns per picture its numbers."""
    views = pictures()
    if not views:
        raise ArtPipelineError(f"{name} has no reference picture in its {REFERENCE_COLLECTION} collection.")
    model = _model(root_name)
    os.makedirs(output, exist_ok=True)
    scene = bpy.context.scene
    for obj in scene.objects:
        obj.hide_render = obj not in model
    preview._set_engine(scene)
    scene.render.film_transparent = True
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.view_settings.view_transform = "Standard"
    camera = preview._add_object(scene, "ReferenceCamera", bpy.data.cameras.new("ReferenceCamera"))
    camera.data.type = "ORTHO"
    scene.camera = camera
    depsgraph = bpy.context.evaluated_depsgraph_get()
    corners = [o.evaluated_get(depsgraph).matrix_world @ Vector(c) for o in model for c in o.evaluated_get(depsgraph).bound_box]

    results = []
    with tempfile.TemporaryDirectory(dir=output, prefix=f"{name}_reference_") as scratch:
        for empty in views:
            image = empty.data
            width, height = image.size
            if not width or not height:
                raise ArtPipelineError(f"The picture of {empty.name} has no pixels; pack its image into the file.")
            middle = empty.matrix_world @ Vector(picture_middle(width, height, empty.empty_display_size,
                                                               tuple(empty.empty_image_offset)) + (0.0,))
            # A camera looks along its local -Z with its local Y up, which is how a picture's front is seen.
            rotation = empty.matrix_world.to_3x3().normalized()
            scale = empty.matrix_world.to_scale()
            back = rotation @ Vector((0.0, 0.0, 1.0))
            distance = max((c - middle).length for c in corners) + 1.0
            camera.matrix_world = rotation.to_4x4()
            camera.location = middle + back * distance
            camera.data.ortho_scale = empty.empty_display_size * abs(scale.x if width >= height else scale.y)
            camera.data.clip_start = 0.01
            camera.data.clip_end = distance * 2.0
            size = render_size(width, height)
            scene.render.resolution_x, scene.render.resolution_y = size
            path = os.path.join(scratch, f"{empty.name}.png")
            scene.render.filepath = path
            bpy.ops.render.render(write_still=True)
            rendered = bpy.data.images.load(path)
            try:
                silhouette = _pixels(rendered)[..., 3] > ALPHA
            finally:
                bpy.data.images.remove(rendered)
            figure = resample(figure_mask(_pixels(image)), size[0], size[1])
            both, picture_only, model_only = overlap(figure, silhouette)
            written = preview._save(overlay(figure, silhouette), os.path.join(output, f"{name}_reference_{empty.name}.png"))
            results.append({"view": empty.name, "overlap": round(both, 4), "pictureOnly": round(picture_only, 4),
                            "modelOnly": round(model_only, 4), "overlay": written})
    return results
