"""Finds the digits of a realistic reference hand or foot: tips by geodesic distance from the
wrist (or ankle) band, then each digit's centre line and thickness by cross-sections walked
back from the tip until the section widens into the palm (or sole)."""

import heapq
import math

import numpy as np
from mathutils import Vector

from . import geo


def region_graph(surface, keep):
    """Adjacency (with edge lengths) of the reference vertices for which keep is true."""
    idx = np.nonzero(keep)[0]
    local = -np.ones(len(surface.co), np.int64)
    local[idx] = np.arange(len(idx))
    adj = [[] for _ in range(len(idx))]
    co = surface.co
    for a, b, c in surface.tris:
        for u, v in ((a, b), (b, c), (c, a)):
            lu, lv = local[u], local[v]
            if lu >= 0 and lv >= 0:
                d = float(np.linalg.norm(co[u] - co[v]))
                adj[lu].append((lv, d))
                adj[lv].append((lu, d))
    return idx, adj


def geodesic(adj, seeds):
    dist = [math.inf] * len(adj)
    prev = [-1] * len(adj)
    heap = []
    for s in seeds:
        dist[s] = 0.0
        heap.append((0.0, s))
    heapq.heapify(heap)
    while heap:
        d, u = heapq.heappop(heap)
        if d > dist[u]:
            continue
        for v, w in adj[u]:
            nd = d + w
            if nd < dist[v]:
                dist[v] = nd
                prev[v] = u
                heapq.heappush(heap, (nd, v))
    return np.array(dist), prev


def find_tips(adj, dist, count):
    """Digit tips as the most persistent maxima of the geodesic distance: vertices are added
    from far to near and merged by union-find; when two regions meet, the one with the lower
    peak dies, and its persistence (peak minus meeting distance) is about the digit length.
    Bumps on the skin die young, so the count longest-living peaks are the digits."""
    order = [i for i in np.argsort(-dist) if math.isfinite(dist[i])]
    parent = {}

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    peak = {}
    persistence = {}
    for v in order:
        roots = {find(u) for u, _w in adj[v] if u in parent}
        parent[v] = v
        if not roots:
            peak[v] = v
            continue
        best = max(roots, key=lambda r: dist[peak[r]])
        parent[v] = best
        for r in roots:
            if r != best:
                persistence[peak[r]] = dist[peak[r]] - dist[v]
                parent[r] = best
    for r in {find(v) for v in parent}:
        persistence[peak[r]] = math.inf
    ranked = sorted(persistence, key=lambda p: -persistence[p])
    return ranked[:count], [persistence[p] for p in ranked[:count + 2]]


def centre_line(surface, path_points, step=0.004, reach=0.024, widen=1.5):
    """Centre line of a digit from its geodesic path (tip to base): every sample is pushed
    inside along the surface normal and re-centred with a short ray fan, staying near the path
    so neighbouring digits are never picked up. Stops where the section widens."""
    pts = [Vector(p) for p in path_points]
    samples = [pts[0]]
    acc = 0.0
    for a, b in zip(pts, pts[1:]):
        seg = (b - a).length
        while acc + seg >= step and seg > 1e-9:
            t = (step - acc) / seg
            a = a.lerp(b, t)
            samples.append(a)
            seg = (b - a).length
            acc = 0.0
        acc += seg
    out, radii = [], []
    for k in range(1, len(samples) - 1):
        d = (samples[min(k + 2, len(samples) - 1)] - samples[max(k - 2, 0)]).normalized()
        _loc, normal, _i, _dist = surface.nearest(samples[k])
        r_guess = radii[-1] if radii else 0.008
        guess = samples[k] - Vector(normal).normalized() * r_guess
        sec = geo.section(surface, guess, d, rays=24, iterations=2, max_dist=reach)
        if sec is None:
            if len(radii) >= 4:
                break
            continue
        c, r = sec
        if (c - guess).length > 0.006:
            c = guess
        if len(radii) >= 4 and r > widen * float(np.median(radii[:6])):
            break
        out.append((c, r))
        radii.append(r)
    return out


def trace(surface, origin, direction, side_axis, count, reach=0.24, separation=0.014, side_sign=1.0):
    """Digits beyond the plane through origin with normal direction, on one side of x = 0.
    Returns a list of dicts (tip, path, line) sorted along side_axis (most negative first)."""
    co = surface.co
    rel = co - np.array(origin)
    along = rel @ np.array(direction)
    keep = (along > -0.01) & (np.linalg.norm(rel, axis=1) < reach) & (co[:, 0] * side_sign > 0)
    idx, adj = region_graph(surface, keep)
    pts = co[idx]
    # Only the part connected to the wrist (or ankle): a thigh near the hand is its own piece.
    start = int(np.argmin(np.linalg.norm(pts - np.array(origin), axis=1)))
    reached = {start}
    stack = [start]
    while stack:
        u = stack.pop()
        for v, _w in adj[u]:
            if v not in reached:
                reached.add(v)
                stack.append(v)
    seeds = [i for i in reached if along[idx[i]] < 0.004]
    dist, prev = geodesic(adj, seeds)
    tips, lives = find_tips(adj, dist, count)
    print("tip persistence", [round(x, 4) for x in lives])
    digits = []
    for t in tips:
        path = []
        v = t
        while v != -1:
            path.append(pts[v])
            v = prev[v]
        line = centre_line(surface, path)
        digits.append({"tip": Vector(pts[t]), "path": path, "line": line})
    digits.sort(key=lambda dg: Vector(dg["tip"]).dot(Vector(side_axis)))
    return digits


class Digit:
    """A traced digit as a polyline from the tip to its base with radii."""

    def __init__(self, traced):
        self.tip = Vector(traced["tip"])
        self.points = [self.tip] + [Vector(c) for c, _r in traced["line"]]
        self.radii = [traced["line"][0][1]] + [r for _c, r in traced["line"]]
        self.cum = [0.0]
        for a, b in zip(self.points, self.points[1:]):
            self.cum.append(self.cum[-1] + (b - a).length)
        self.length = self.cum[-1]
        self.base = self.points[-1]

    def at(self, s):
        """Centre, radius and tangent (toward the tip) at arc length s from the tip; beyond
        the base the line is extended straight."""
        pts, cum = self.points, self.cum
        if s >= cum[-1]:
            t = (pts[-4] - pts[-1]).normalized()
            return pts[-1] - t * (s - cum[-1]), self.radii[-1], t
        k = max(i for i in range(len(cum)) if cum[i] <= s)
        k = min(k, len(cum) - 2)
        f = (s - cum[k]) / max(cum[k + 1] - cum[k], 1e-9)
        c = pts[k].lerp(pts[k + 1], f)
        r = self.radii[k] + (self.radii[k + 1] - self.radii[k]) * f
        a, b = max(k - 2, 0), min(k + 3, len(pts) - 1)
        return c, r, (pts[a] - pts[b]).normalized()
