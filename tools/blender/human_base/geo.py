"""Geometry helpers: reference surfaces with BVH trees, ray and nearest-point projection,
cross-sections, and similarity alignment (ICP)."""

import math

import bpy
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree


def mesh_arrays(mesh):
    mesh.calc_loop_triangles()
    co = np.empty(len(mesh.vertices) * 3, np.float64)
    mesh.vertices.foreach_get("co", co)
    tris = np.empty(len(mesh.loop_triangles) * 3, np.int32)
    mesh.loop_triangles.foreach_get("vertices", tris)
    return co.reshape(-1, 3), tris.reshape(-1, 3)


class Surface:
    """A reference surface: vertex array, triangles and a BVH tree."""

    def __init__(self, co, tris):
        self.co = np.asarray(co, np.float64)
        self.tris = np.asarray(tris, np.int32)
        self.tree = BVHTree.FromPolygons(self.co.tolist(), self.tris.tolist())

    @classmethod
    def from_object(cls, obj):
        co, tris = mesh_arrays(obj.data)
        mw = np.array(obj.matrix_world)
        co = co @ mw[:3, :3].T + mw[:3, 3]
        return cls(co, tris)

    def nearest(self, p):
        loc, n, i, d = self.tree.find_nearest(Vector(p))
        return loc, n, i, d

    def first_hit(self, origin, direction, max_dist=1.0):
        d = Vector(direction)
        if d.length < 1e-12:
            return None
        d.normalize()
        loc, n, i, dist = self.tree.ray_cast(Vector(origin), d, max_dist)
        return (loc, n, i, dist) if loc is not None else None

    def barycentric(self, p, tri_index):
        a, b, c = (Vector(self.co[k]) for k in self.tris[tri_index])
        v0, v1, v2 = b - a, c - a, Vector(p) - a
        d00, d01, d11 = v0.dot(v0), v0.dot(v1), v1.dot(v1)
        d20, d21 = v2.dot(v0), v2.dot(v1)
        den = d00 * d11 - d01 * d01
        if abs(den) < 1e-20:
            return (1.0, 0.0, 0.0)
        v = (d11 * d20 - d01 * d21) / den
        w = (d00 * d21 - d01 * d20) / den
        return (1.0 - v - w, v, w)

    def point_at(self, tri_index, bary):
        a, b, c = (self.co[k] for k in self.tris[tri_index])
        return Vector(a * bary[0] + b * bary[1] + c * bary[2])


def plane_basis(n):
    n = Vector(n).normalized()
    a = Vector((0, 0, 1)) if abs(n.z) < 0.9 else Vector((1, 0, 0))
    u = n.cross(a).normalized()
    return u, n.cross(u).normalized()


def section(surface, p, n, rays=32, iterations=4, max_dist=0.4):
    """Centre and mean radius of the cross-section around p in the plane with normal n,
    found by casting a fan of rays from the current centre estimate."""
    p = Vector(p)
    u, w = plane_basis(n)
    hits = []
    for _ in range(iterations):
        hits = []
        for k in range(rays):
            a = 2 * math.pi * k / rays
            hit = surface.first_hit(p, u * math.cos(a) + w * math.sin(a), max_dist)
            if hit:
                hits.append(hit[0])
        if len(hits) < rays * 0.8:
            return None
        p = sum(hits, Vector()) / len(hits)
    return p, sum((h - p).length for h in hits) / len(hits)


def vertex_normals(co, faces):
    mesh = bpy.data.meshes.new("_normals")
    mesh.from_pydata([tuple(c) for c in co], [], [tuple(f) for f in faces])
    mesh.validate()
    normals = np.array([v.normal[:] for v in mesh.vertices])
    bpy.data.meshes.remove(mesh)
    return normals


def project_along(surface, p, n, reach=0.02):
    """Closest surface hit along +n or -n from p, else the nearest surface point."""
    p, n = Vector(p), Vector(n)
    best = None
    for d in (n, -n):
        hit = surface.first_hit(p, d, reach)
        if hit and (best is None or hit[3] < best[3]):
            best = hit
    if best is None:
        return Vector(surface.nearest(p)[0])
    return Vector(best[0])


def umeyama(src, dst, with_scale=True, allow_rotation=True):
    """Similarity transform (s, R, t) minimising |s R src + t - dst|."""
    src, dst = np.asarray(src), np.asarray(dst)
    mu_s, mu_d = src.mean(0), dst.mean(0)
    xs, xd = src - mu_s, dst - mu_d
    if allow_rotation:
        cov = xd.T @ xs / len(src)
        u, sig, vt = np.linalg.svd(cov)
        d = np.ones(3)
        if np.linalg.det(u) * np.linalg.det(vt) < 0:
            d[2] = -1
        r = u @ np.diag(d) @ vt
        var_s = (xs ** 2).sum() / len(src)
        s = (sig * d).sum() / var_s if with_scale else 1.0
    else:
        r = np.eye(3)
        s = math.sqrt((xd ** 2).sum() / max((xs ** 2).sum(), 1e-12)) if with_scale else 1.0
    t = mu_d - s * r @ mu_s
    return s, r, t


def icp(points, surface, iterations=30, with_scale=True, allow_rotation=True, weights=None):
    """Aligns points (N x 3) to a surface; returns the transformed points and the 4x4 matrix."""
    pts = np.asarray(points, np.float64).copy()
    total = np.eye(4)
    for _ in range(iterations):
        nearest = np.array([surface.nearest(p)[0] for p in pts])
        s, r, t = umeyama(pts, nearest, with_scale, allow_rotation)
        pts = (s * (r @ pts.T)).T + t
        step = np.eye(4)
        step[:3, :3] = s * r
        step[:3, 3] = t
        total = step @ total
    return pts, total
