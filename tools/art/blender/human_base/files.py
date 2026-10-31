"""Both base characters in one Blender session, for the checks and the review files."""

import bpy

from art_pipeline import bodies

LABELS = ("Male", "Female")


def open_both(male_path, female_path, offset=0.0):
    """Opens the male character's file and appends every object and image of the female's. With an
    offset, the male stands that far to the left and the female to the right. The female's parts are
    hidden in the viewport like the male's, under her source body."""
    bpy.ops.wm.open_mainfile(filepath=male_path)
    # The images too, for the face and eye variants that no material uses.
    with bpy.data.libraries.load(female_path, link=False) as (source, target):
        target.objects = list(source.objects)
        target.images = list(source.images)
    for obj in target.objects:
        if obj is not None:
            bpy.context.scene.collection.objects.link(obj)
    for image in target.images:
        if image is not None:
            image.use_fake_user = True
    bpy.context.view_layer.update()
    for obj in bodies.parts_of("Female"):
        obj.hide_set(True)
    if offset:
        bpy.data.objects["Male"].location.x = -offset
        bpy.data.objects["Female"].location.x = offset
        bpy.context.view_layer.update()
