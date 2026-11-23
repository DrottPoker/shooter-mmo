"""The stiff parts of a modeled garment template: an island of its mesh that is stiff in the world, such
as a helmet's nasal bar, which its asset.json names in the field "stiff" by one of its vertices. The
smooth field a modeled template moves with bends a thin part that stands off the cage, so where a
version follows a move of its cage or reaches a new body, such a part follows as one piece instead:
by the similarity, a rotation, one scale, and a translation, that carries its vertices most closely
from where the field had them to where the field puts them now.

Standard library and numpy only, so the tests run it without Blender. See docs/ART_PIPELINE.md,
Building A Template."""

import math

import numpy as np

from .scene import ArtPipelineError

FIELD = "stiff"


def entries(data: dict) -> list[tuple[str, int]]:
    """The stiff parts an asset.json names, as (name, vertex) in its order, or none.
    tools/art/ArtPipelineCommon.ps1 checks their form when it reads the asset.json."""
    found = data.get(FIELD) or []
    if not isinstance(found, list) or not all(isinstance(e, dict) and isinstance(e.get("name"), str) and
                                              isinstance(e.get("vertex"), int) and not isinstance(e.get("vertex"), bool)
                                              for e in found):
        raise ArtPipelineError(f'The field "{FIELD}" of asset.json must list stiff parts, each with its "name" and '
                               'the index of one of its vertices, "vertex".')
    return [(e["name"], e["vertex"]) for e in found]


def islands(count: int, polygons: list[tuple[int, ...]]) -> np.ndarray:
    """The island of every vertex, the parts of the mesh that its edges join, numbered in the order of
    their lowest vertex; a vertex of no face is an island of its own."""
    parent = list(range(count))

    def root(vertex: int) -> int:
        while parent[vertex] != vertex:
            parent[vertex] = parent[parent[vertex]]
            vertex = parent[vertex]
        return vertex

    for polygon in polygons:
        for i, a in enumerate(polygon):
            ra, rb = root(a), root(polygon[(i + 1) % len(polygon)])
            if ra != rb:
                parent[max(ra, rb)] = min(ra, rb)
    roots = np.array([root(v) for v in range(count)], np.int64)
    # Every root is the lowest vertex of its island, so the order of the roots is the islands' order.
    _unique, numbers = np.unique(roots, return_inverse=True)
    return numbers.reshape(-1)


def parts(named: list[tuple[str, int]], count: int, polygons: list[tuple[int, ...]],
          version: str) -> list[tuple[str, np.ndarray]]:
    """The vertices of every named stiff part on a version: the island of its mesh that holds the
    part's vertex. Every version of a template has the same topology, so the same vertices on each.
    A vertex out of range, an island that is the whole mesh, or two parts on one island stop the
    sync."""
    if not named:
        return []
    numbers = islands(count, polygons)
    found = []
    owner = {}
    for name, vertex in named:
        if not 0 <= vertex < count:
            raise ArtPipelineError(f'The field "{FIELD}" of asset.json names the vertex {vertex} for the part "{name}", '
                                   f'but "{version}" has {count} vertices.')
        island = int(numbers[vertex])
        members = np.nonzero(numbers == island)[0]
        if len(members) == count:
            raise ArtPipelineError(f'The field "{FIELD}" of asset.json names the part "{name}" by the vertex {vertex}, '
                                   f'whose island is the whole mesh of "{version}"; a stiff part is an island of its '
                                   "own, joined to the rest of the mesh by no edge.")
        if island in owner:
            raise ArtPipelineError(f'The field "{FIELD}" of asset.json names the parts "{owner[island]}" and "{name}" on '
                                   f'one island of "{version}"; each island is at most one stiff part.')
        owner[island] = name
        found.append((name, members))
    return found


def similarity(source, target) -> tuple[np.ndarray, float, np.ndarray]:
    """The rotation, the one scale, and the translation that carry the points source most closely to
    target in the least squares, without a mirror, by Umeyama's method: target is about
    scale * source @ rotation.T + translation."""
    source, target = np.asarray(source, float), np.asarray(target, float)
    middle_source, middle_target = source.mean(axis=0), target.mean(axis=0)
    a, b = source - middle_source, target - middle_target
    spread = (a * a).sum() / len(a)
    if spread <= 0.0:
        return np.eye(3), 1.0, middle_target - middle_source
    u, sigma, vt = np.linalg.svd(b.T @ a / len(a))
    sign = np.ones(3)
    if np.linalg.det(u) * np.linalg.det(vt) < 0.0:
        sign[2] = -1.0
    rotation = u @ np.diag(sign) @ vt
    scale = float((sigma * sign).sum() / spread)
    return rotation, scale, middle_target - scale * rotation @ middle_source


def carry(transform: tuple[np.ndarray, float, np.ndarray], points) -> np.ndarray:
    """The points carried by a similarity."""
    rotation, scale, translation = transform
    return scale * np.asarray(points, float) @ rotation.T + translation


def measures(transform: tuple[np.ndarray, float, np.ndarray], points) -> tuple[float, float, float, float]:
    """What a similarity does to a part at points: how far it moves the part's centre and its farthest
    vertex, in meters, how far it turns it, in degrees, and its scale."""
    points = np.asarray(points, float)
    moved = carry(transform, points)
    rotation, scale, _translation = transform
    turn = math.degrees(math.acos(max(-1.0, min(1.0, (float(np.trace(rotation)) - 1.0) / 2.0))))
    return (float(np.linalg.norm(moved.mean(axis=0) - points.mean(axis=0))),
            float(np.linalg.norm(moved - points, axis=1).max(initial=0.0)), turn, scale)
