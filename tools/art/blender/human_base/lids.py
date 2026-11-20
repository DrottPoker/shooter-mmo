"""The skin weights of the eyelids. Each eye has an upper and a lower lid bone that turn about the
eyeball's centre around the bone's X axis, the body's X axis: closed, the upper lid's bone turns by
the skeleton's closed angle down, the lower lid's up. A lid takes the skin between its inner edge
and the brow or the cheek, so the lids close as a whole and the skin above and below follows them.

The inner edge is the line where the lids' skin meets the pocket behind them, which holds the
eyeball. Its two corners lie nearest to and furthest from the nose. The lids meet on a line that
lies, at each place across the eye, at the share meet of the way from the lower lid's edge to the
upper lid's, around the eyeball's centre. A turn about the X axis keeps a vertex's place across the
eye, so each vertex of the edge needs a turn of its own to reach that line at its place, and its
weight is the one at which the skin, which blends the vertex's place at rest and turned by the
closed angle in a straight line, reaches the line's angle. The corners stay. The rings of skin
outward from the edge take the weight of their nearest vertex of the edge times their ring's share,
and the pocket behind the edge follows the edge.

Only numpy: the rig reads the meshes in Blender and gives this module their arrays."""

import math
from collections import deque

import numpy as np

# The hidden part of the faces of the pocket behind the lids, as the body files' face attribute
# "part" names it (human_build.head.HIDDEN_PARTS); skin faces have part 0.
SOCKET_PART = 7
# The lids' skin and pocket lie within this distance of the eyeball's centre, at 1.785 m.
REACH = 0.03


def edge_cycle(points, faces, parts, centre, reach=REACH):
    """The inner edge of one eye's lids as a closed line of vertices: the vertices of both skin and
    pocket faces within reach of the eyeball's centre, in order along the pocket's faces."""
    points = np.asarray(points, float)
    skin = {v for face, part in zip(faces, parts) if part == 0 for v in face}
    pocket = {v for face, part in zip(faces, parts) if part == SOCKET_PART for v in face}
    edge = {v for v in skin & pocket if np.linalg.norm(points[v] - centre) < reach}
    if len(edge) < 6:
        raise ValueError(f"the eye at {np.round(centre, 4).tolist()} has {len(edge)} vertices on its lids' inner edge")
    beside = {v: set() for v in edge}
    for face, part in zip(faces, parts):
        if part != SOCKET_PART:
            continue
        for a, b in zip(face, face[1:] + face[:1]):
            if a in edge and b in edge:
                beside[a].add(b)
                beside[b].add(a)
    if any(len(next_to) != 2 for next_to in beside.values()):
        raise ValueError("the lids' inner edge is not one closed line")
    start = min(edge)
    cycle, previous = [start], None
    while True:
        step = next(v for v in sorted(beside[cycle[-1]]) if v != previous)
        previous = cycle[-1]
        if step == start:
            break
        cycle.append(step)
    if len(cycle) != len(edge):
        raise ValueError("the lids' inner edge is more than one line")
    return cycle


def lids(points, cycle):
    """The upper and the lower lid's edge, each from the inner corner (nearest the middle of the
    face) to the outer corner, by a cycle of the inner edge."""
    points = np.asarray(points, float)
    across = np.abs(points[cycle][:, 0])
    inner, outer = cycle[int(np.argmin(across))], cycle[int(np.argmax(across))]
    k = cycle.index(inner)
    turned = cycle[k:] + cycle[:k]
    o = turned.index(outer)
    one, other = turned[:o + 1], [turned[0]] + turned[o:][::-1]
    if len(one) != len(other):
        raise ValueError(f"the lids' edges have {len(one)} and {len(other)} vertices; the lids need as many")
    return (one, other) if points[one][:, 2].mean() > points[other][:, 2].mean() else (other, one)


def angle(points, vertex, centre):
    """A point's angle about the eyeball's centre around the X axis: up from the forward direction."""
    d = np.asarray(points[vertex], float) - centre
    return math.atan2(d[2], d[1])


def profile(points, chain, centre):
    """A lid edge's places across the eye, growing from the inner corner to the outer one, and its
    angles about the eyeball's centre."""
    points = np.asarray(points, float)
    sign = 1.0 if points[chain[-1]][0] > points[chain[0]][0] else -1.0
    across = np.array([points[v][0] for v in chain]) * sign
    if np.any(np.diff(across) <= 0.0):
        raise ValueError("a lid's edge turns back on its way across the eye")
    return across, np.array([angle(points, v, centre) for v in chain])


def needs(points, upper, lower, centre, meet):
    """The turn each vertex of the lids' edges needs to reach the line where the lids meet at its
    place across the eye, in radians, for the vertices between the corners."""
    up_across, up_angles = profile(points, upper, centre)
    down_across, down_angles = profile(points, lower, centre)
    up, down = {}, {}
    for v, across, top in zip(upper[1:-1], up_across[1:-1], up_angles[1:-1]):
        bottom = np.interp(across, down_across, down_angles)
        up[v] = (1.0 - meet) * (top - bottom)
    for v, across, bottom in zip(lower[1:-1], down_across[1:-1], down_angles[1:-1]):
        top = np.interp(across, up_across, up_angles)
        down[v] = meet * (top - bottom)
    return up, down


def blended(turn, closed):
    """The weight at which a point blended in a straight line between its place at rest and its place
    turned by the closed angle about an axis lies turned by turn about it, in radians."""
    if turn <= 0.0:
        return 0.0
    rise = math.tan(turn)
    return rise / (math.sin(closed) + rise * (1.0 - math.cos(closed)))


def weights(points, faces, parts, centre, radius, closed, meet, factors, reach=REACH):
    """The weights of one eye's lids: per lid ("upper", "lower") a dict of vertex to weight, and the
    largest turn each lid's edge needs, in degrees. closed holds each lid's closed angle in degrees,
    factors each lid's share per ring of skin outward from the edge. Raises when the lids need more
    than their closed angles to meet."""
    points = np.asarray(points, float)
    centre = np.asarray(centre, float)
    cycle = edge_cycle(points, faces, parts, centre, reach)
    upper, lower = lids(points, cycle)
    up, down = needs(points, upper, lower, centre, meet)
    largest = {"upper": math.degrees(max(up.values())), "lower": math.degrees(max(down.values()))}
    for lid, value in largest.items():
        if value > abs(closed[lid]) + 1e-6:
            raise ValueError(f"the {lid} lid needs {value:.1f} degrees to close, more than its closed angle "
                             f"{abs(closed[lid]):.1f}")
    edge = {upper[0]: ("upper", 0.0), upper[-1]: ("upper", 0.0)}
    edge.update({v: ("upper", blended(turn, math.radians(abs(closed["upper"])))) for v, turn in up.items()})
    edge.update({v: ("lower", blended(turn, math.radians(abs(closed["lower"])))) for v, turn in down.items()})
    order = list(edge)
    places = points[order]

    def nearest(v):
        return edge[order[int(np.argmin(np.linalg.norm(places - points[v], axis=1)))]]

    out = {"upper": {}, "lower": {}}
    for v, (lid, weight) in edge.items():
        out[lid][v] = weight
    # The rings of skin outward from the edge, over the skin's edges.
    beside = {}
    for face, part in zip(faces, parts):
        if part == 0:
            for a, b in zip(face, face[1:] + face[:1]):
                beside.setdefault(a, set()).add(b)
                beside.setdefault(b, set()).add(a)
    rings = max(len(f) for f in factors.values())
    ring = {v: 0 for v in edge}
    queue = deque(sorted(edge))
    while queue:
        v = queue.popleft()
        if ring[v] >= rings:
            continue
        for w in sorted(beside.get(v, ())):
            if w not in ring:
                ring[w] = ring[v] + 1
                queue.append(w)
    for v, step in ring.items():
        if step == 0:
            continue
        lid, weight = nearest(v)
        share = factors[lid][step - 1] if step - 1 < len(factors[lid]) else 0.0
        if weight * share > 0.0:
            out[lid][v] = weight * share
    # The pocket behind the edge, outside the eyeball, follows the edge.
    skin = {v for face, part in zip(faces, parts) if part == 0 for v in face}
    pocket = {v for face, part in zip(faces, parts) if part == SOCKET_PART for v in face} - skin
    for v in sorted(pocket):
        distance = np.linalg.norm(points[v] - centre)
        if radius <= distance < reach:
            lid, weight = nearest(v)
            if weight > 0.0:
                out[lid][v] = weight
    return out, largest
