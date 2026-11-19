"""A reference surface that nets are laid onto: its sections by planes, the nearest point of it,
and rays to it, of the whole surface or of the part of it that a rule selects, so a net of one
finger never lands on the next finger and an arm never on the chest beside it."""

import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree


def _unit(v):
    v = np.asarray(v, float)
    return v / max(np.linalg.norm(v), 1e-12)


class Loop:
    """A closed section curve: its points in order, without the first repeated."""

    def __init__(self, points):
        self.points = np.asarray(points, float)
        nxt = np.roll(self.points, -1, axis=0)
        self.lengths = np.linalg.norm(nxt - self.points, axis=1)
        self.length = float(self.lengths.sum())
        weights = self.lengths[:, None]
        self.centre = ((self.points + nxt) / 2 * weights).sum(0) / max(self.length, 1e-12)

    def area(self, normal):
        """The signed area of the loop seen against the normal, positive when counter-clockwise."""
        p = self.points - self.centre
        return float(np.cross(p, np.roll(p, -1, axis=0)).sum(0) @ _unit(normal)) / 2

    def oriented(self, normal):
        """The loop running counter-clockwise seen against the normal."""
        return self if self.area(normal) >= 0 else Loop(self.points[::-1])

    def start_at(self, direction, normal, centre=None):
        """The loop starting where it crosses the half-plane from the centre along the direction."""
        centre = self.centre if centre is None else np.asarray(centre, float)
        normal = _unit(normal)
        d = _unit(np.asarray(direction, float) - (np.asarray(direction, float) @ normal) * normal)
        side = np.cross(normal, d)
        rel = self.points - centre
        s = rel @ side
        forward = rel @ d
        best, best_point = None, None
        count = len(self.points)
        for i in range(count):
            j = (i + 1) % count
            if (s[i] <= 0 < s[j]) or (s[i] >= 0 > s[j]) or (s[i] == 0 and s[j] == 0):
                t = 0.0 if s[i] == s[j] else s[i] / (s[i] - s[j])
                point = self.points[i] + t * (self.points[j] - self.points[i])
                reach = (point - centre) @ d
                if reach > 0 and (best is None or reach > best[0]):
                    best, best_point = (reach, i), point
        if best is None:
            i = int(np.argmax(forward))
            return Loop(np.roll(self.points, -i, axis=0))
        i = best[1]
        points = np.concatenate([[best_point], self.points[i + 1:], self.points[:i + 1]])
        return Loop(points)

    def resample(self, count, shares=None):
        """Points at even shares of the loop's length from its first point, or at the given shares."""
        shares = np.arange(count) / count if shares is None else np.asarray(shares, float)
        cumulative = np.concatenate([[0.0], np.cumsum(self.lengths)])
        out = []
        n = len(self.points)
        for share in shares:
            target = (share % 1.0) * self.length
            i = int(np.searchsorted(cumulative, target, side="right") - 1)
            i = min(max(i, 0), n - 1)
            t = (target - cumulative[i]) / max(self.lengths[i], 1e-12)
            out.append(self.points[i] + t * (self.points[(i + 1) % n] - self.points[i]))
        return np.array(out)

    def nearest_point(self, point):
        """The point of the loop nearest to a point."""
        nxt = np.roll(self.points, -1, axis=0)
        ab = nxt - self.points
        t = np.clip(((point - self.points) * ab).sum(1) / np.maximum((ab * ab).sum(1), 1e-18), 0.0, 1.0)
        nearest = self.points + ab * t[:, None]
        return nearest[int(np.argmin(np.linalg.norm(nearest - point, axis=1)))]

    def share_of(self, point):
        """The share of the loop's length at which the loop comes nearest to the point."""
        cumulative = np.concatenate([[0.0], np.cumsum(self.lengths)])
        nxt = np.roll(self.points, -1, axis=0)
        ab = nxt - self.points
        t = np.clip(((point - self.points) * ab).sum(1) / np.maximum((ab * ab).sum(1), 1e-18), 0.0, 1.0)
        nearest = self.points + ab * t[:, None]
        i = int(np.argmin(np.linalg.norm(nearest - point, axis=1)))
        return float((cumulative[i] + t[i] * self.lengths[i]) / self.length)


class Surface:
    def __init__(self, points, triangles):
        self.points = np.asarray(points, float)
        self.triangles = np.asarray(triangles, np.int64)
        self._tree = None
        self._normals = None

    @classmethod
    def of_object(cls, obj):
        mesh = obj.data
        mesh.calc_loop_triangles()
        points = np.empty(len(mesh.vertices) * 3)
        mesh.vertices.foreach_get("co", points)
        m = np.array(obj.matrix_world)
        points = points.reshape(-1, 3) @ m[:3, :3].T + m[:3, 3]
        triangles = np.empty(len(mesh.loop_triangles) * 3, np.int32)
        mesh.loop_triangles.foreach_get("vertices", triangles)
        return cls(points, triangles.reshape(-1, 3))

    def part(self, keep):
        """The surface of the triangles whose centres the rule keeps: keep(centres) -> bool array."""
        centres = self.points[self.triangles].mean(1)
        return Surface(self.points, self.triangles[np.asarray(keep(centres), bool)])

    @property
    def tree(self):
        if self._tree is None:
            self._tree = BVHTree.FromPolygons([tuple(p) for p in self.points], [tuple(int(v) for v in t) for t in self.triangles])
        return self._tree

    @property
    def normals(self):
        if self._normals is None:
            p = self.points[self.triangles]
            n = np.cross(p[:, 1] - p[:, 0], p[:, 2] - p[:, 0])
            self._normals = n / np.maximum(np.linalg.norm(n, axis=1), 1e-18)[:, None]
        return self._normals

    def nearest(self, point, reach=1.0):
        """The nearest point of the surface, its normal, and its distance, or None."""
        found = self.tree.find_nearest(Vector(point), reach)
        if found[0] is None:
            return None
        return np.array(found[0]), self.normals[found[2]], float(found[3])

    def ray(self, origin, direction, reach=1.0):
        """Where a ray first meets the surface: the point, the normal there, and the distance."""
        found = self.tree.ray_cast(Vector(origin), Vector(_unit(direction)), reach)
        if found[0] is None:
            return None
        return np.array(found[0]), self.normals[found[2]], float(found[3])

    def project(self, point, normal=None, reach=0.08, agree=0.2):
        """The point of the surface that a net vertex belongs to: the nearest one whose normal agrees
        with the vertex's, tried first along the vertex's normal both ways, then anywhere near."""
        point = np.asarray(point, float)
        best = None
        if normal is not None:
            normal = _unit(normal)
            for sign in (1.0, -1.0):
                hit = self.ray(point - sign * normal * 1e-5, sign * normal, reach)
                if hit is not None and hit[1] @ normal > agree and (best is None or hit[2] < best[2]):
                    best = hit
        near = self.nearest(point, reach)
        if near is not None and (normal is None or near[1] @ normal > agree):
            if best is None or near[2] < best[2] * 0.7:
                best = near
        if best is None and near is not None:
            best = near
        return None if best is None else best[0]

    def section(self, origin, normal):
        """The closed curves in which the plane through the origin cuts the surface."""
        origin, normal = np.asarray(origin, float), _unit(normal)
        distance = (self.points - origin) @ normal
        distance = np.where(distance == 0.0, 1e-12, distance)
        tri = self.triangles
        d = distance[tri]
        crossing = (d.min(1) < 0) & (d.max(1) > 0)
        tri, d = tri[crossing], d[crossing]
        if not len(tri):
            return []
        points = {}
        links = {}

        def cut(a, b):
            key = (a, b) if a < b else (b, a)
            if key not in points:
                da, db = distance[a], distance[b]
                points[key] = self.points[a] + (self.points[b] - self.points[a]) * (da / (da - db))
            return key

        for (a, b, c), (da, db, dc) in zip(tri.tolist(), d.tolist()):
            keys = []
            if (da < 0) != (db < 0):
                keys.append(cut(a, b))
            if (db < 0) != (dc < 0):
                keys.append(cut(b, c))
            if (dc < 0) != (da < 0):
                keys.append(cut(c, a))
            if len(keys) == 2:
                links.setdefault(keys[0], []).append(keys[1])
                links.setdefault(keys[1], []).append(keys[0])
        loops = []
        seen = set()
        for start in links:
            if start in seen or len(links[start]) != 2:
                continue
            chain = [start]
            seen.add(start)
            previous, current = start, links[start][0]
            closed = False
            while True:
                if current == start:
                    closed = True
                    break
                if current in seen or len(links.get(current, ())) != 2:
                    break
                seen.add(current)
                chain.append(current)
                a, b = links[current]
                previous, current = current, (b if a == previous else a)
            if closed and len(chain) >= 3:
                loops.append(Loop([points[k] for k in chain]))
        return loops

    def loop(self, origin, normal, near=None, largest=False):
        """One section curve of the plane: the one whose centre lies nearest to a point (the origin
        by default), or the longest one."""
        loops = self.section(origin, normal)
        if not loops:
            return None
        if largest:
            return max(loops, key=lambda l: l.length).oriented(normal)
        near = np.asarray(origin if near is None else near, float)
        return min(loops, key=lambda l: np.linalg.norm(l.centre - near)).oriented(normal)
