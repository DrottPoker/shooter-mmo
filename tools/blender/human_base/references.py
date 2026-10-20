"""Reference data of the build: the realistic male and female bodies of the Human Base Meshes
bundle (CC0, Blender Studio) as surfaces with identical triangulation, and the Generic Topology
head in its realistic male and female shapes."""

import bpy
import numpy as np

from . import geo


def quad_split_tris(mesh):
    """Triangles from polygons with a fixed split, so meshes with identical topology get
    identical triangles whatever their shape."""
    loop_start = np.empty(len(mesh.polygons), np.int32)
    loop_total = np.empty(len(mesh.polygons), np.int32)
    mesh.polygons.foreach_get("loop_start", loop_start)
    mesh.polygons.foreach_get("loop_total", loop_total)
    loop_vert = np.empty(len(mesh.loops), np.int32)
    mesh.loops.foreach_get("vertex_index", loop_vert)
    tris = []
    for start, total in zip(loop_start, loop_total):
        v = loop_vert[start:start + total]
        for k in range(1, total - 1):
            tris.append((v[0], v[k], v[k + 1]))
    return np.array(tris, np.int32)


def surface_of(obj):
    mesh = obj.data
    co = np.empty(len(mesh.vertices) * 3, np.float64)
    mesh.vertices.foreach_get("co", co)
    co = co.reshape(-1, 3)
    mw = np.array(obj.matrix_world)
    co = co @ mw[:3, :3].T + mw[:3, 3]
    return geo.Surface(co, quad_split_tris(mesh))


class References:
    def __init__(self, path):
        with bpy.data.libraries.load(path, link=False) as (src, dst):
            dst.objects = [n for n in src.objects if n.startswith("Ref")]
        self.objects = {o.name: o for o in dst.objects}
        self.male = surface_of(self.objects["RefMale"])
        self.female = surface_of(self.objects["RefFemale"])
        assert len(self.male.tris) == len(self.female.tris)
        self.head = {
            "male": np.array([v.co[:] for v in self.objects["RefHeadMale"].data.vertices]),
            "female": np.array([v.co[:] for v in self.objects["RefHeadFemale"].data.vertices]),
        }
        hm = self.objects["RefHeadMale"].data
        self.head_faces = [tuple(p.vertices) for p in hm.polygons]

    def body(self, sex):
        return self.male if sex == "male" else self.female


# The objects of the Human Base Meshes bundle the build reads, as data only.
BUNDLE_BODIES = {"RefMale": "GEO-body_male_realistic", "RefFemale": "GEO-body_female_realistic"}
BUNDLE_HEAD = "Head - Generic Topology"
HEAD_SHAPES = (("Real_MALE", "RefHeadMale"), ("Real_FEMALE", "RefHeadFemale"), ("Basis", "RefHeadBasis"))


def _evaluated_copy(obj, name, collection):
    """A plain mesh copy of a bundle body at its full multires level."""
    for md in obj.modifiers:
        if md.type == "MULTIRES":
            md.levels = md.total_levels
            md.render_levels = md.total_levels
    bpy.context.view_layer.update()
    ev = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
    mesh = bpy.data.meshes.new_from_object(ev)
    mesh.name = name
    new = bpy.data.objects.new(name, mesh)
    new.matrix_world = obj.matrix_world.copy()
    collection.objects.link(new)
    return new


def prepare(bundle, out):
    """Writes the reference file: the realistic bodies at full multires level, centred and
    facing +Y, and the Generic Topology head in its realistic male and female shapes."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    names = list(BUNDLE_BODIES.values()) + [BUNDLE_HEAD]
    with bpy.data.libraries.load(bundle, link=False) as (src, dst):
        dst.objects = [n for n in src.objects if n in names]
    objs = {o.name: o for o in dst.objects}
    for o in objs.values():
        bpy.context.scene.collection.objects.link(o)
    refs = bpy.data.collections.new("Refs")
    bpy.context.scene.collection.children.link(refs)
    bodies = [_evaluated_copy(objs[source], name, refs) for name, source in BUNDLE_BODIES.items()]
    for o in bodies:
        # Bodies stand at their own x offsets in the bundle; centre them and keep their facing (-Y).
        matrix = o.matrix_world.copy()
        matrix.translation.x = 0.0
        o.data.transform(matrix)
        o.matrix_world.identity()
        # Turn to face +Y, the asset convention.
        for v in o.data.vertices:
            v.co.x, v.co.y = -v.co.x, -v.co.y
        o.data.update()
    assert len(bodies[0].data.vertices) == len(bodies[1].data.vertices), "the bundle bodies differ in topology"
    head = objs[BUNDLE_HEAD]
    kb = head.data.shape_keys.key_blocks
    for key, name in HEAD_SHAPES:
        mesh = head.data.copy()
        mesh.name = name
        new = bpy.data.objects.new(name, mesh)
        refs.objects.link(new)
        co = [d.co.copy() for d in kb[key].data]
        new.shape_key_clear()
        for v, c in zip(mesh.vertices, co):
            v.co = (-c.x, -c.y, c.z)
        mesh.update()
    for o in list(objs.values()):
        bpy.data.objects.remove(o)
    for m in list(bpy.data.meshes):
        if m.users == 0:
            bpy.data.meshes.remove(m)
    bpy.ops.wm.save_as_mainfile(filepath=out, compress=True)
