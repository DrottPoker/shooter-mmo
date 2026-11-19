"""A net of quads built vertex by vertex and face by face: the coarse cage of a body's right half
with the centre line, its subdivision, its fitting onto a reference surface, and its mirror image.

Every face carries its body zone and the name of the skin it is laid onto, a part of the reference
surface, so a finger's faces land on that finger only. A vertex may be held: on the mirror plane
x = 0, on a plane that a border between two body parts lies in, or where it is.

Every face also carries the name of its island of the UV map, the one that is current when the face
is made, and the net keeps the edges that are seams inside an island, such as the line down the
inside of an arm. An island whose name ends in _R lies on the right half; its mirror image's name
ends in _L. The other islands lie across the middle and stay whole."""

import math

import numpy as np

MIRROR = "mirror"
FIXED = "fixed"
# A vertex of a part that has no skin to lie on, such as the inside of the mouth: it stays where it
# was built, and so do the vertices that subdividing puts between such vertices.
RIGID = "rigid"


def _unit(v):
    v = np.asarray(v, float)
    return v / max(np.linalg.norm(v), 1e-12)


class Net:
    def __init__(self):
        self.points = []
        self.faces = []
        self.zones = []
        self.skins = []
        # The UV island of every face, the island that new faces get, and the seams inside islands.
        self.islands = []
        self.island = None
        self.seams = set()
        # Per vertex: a set of holds, each MIRROR, FIXED, or ("plane", name); and the planes by name.
        self.holds = []
        self.planes = {}
        self.marks = {}

    # ------------------------------------------------------------------ building

    def add(self, point, *holds):
        self.points.append(np.asarray(point, float))
        self.holds.append(set(holds))
        return len(self.points) - 1

    def add_ring(self, points, *holds):
        return [self.add(p, *holds) for p in points]

    def plane(self, name, origin, normal):
        """Names a plane that vertices may be held in, such as the cut between two body parts."""
        self.planes[name] = (np.asarray(origin, float), _unit(normal))
        return ("plane", name)

    def hold(self, vertices, *holds):
        for v in vertices:
            self.holds[v].update(holds)

    def quad(self, a, b, c, d, zone, skin):
        """A face with its corners counter-clockwise seen from outside."""
        self.face((a, b, c, d), zone, skin)

    def face(self, corners, zone, skin):
        """A face of three or more corners, counter-clockwise seen from outside. The cage may have
        a triangle or a five-sided face where a ring gains or loses vertices; subdividing turns
        every face into quads."""
        corners = tuple(corners)
        if len(set(corners)) != len(corners) or len(corners) < 3:
            raise ValueError(f"degenerate face {corners}")
        self.faces.append(corners)
        self.zones.append(zone)
        self.skins.append(skin)
        self.islands.append(self.island)

    def seam(self, vertices):
        """Makes the edges along a line of vertices seams of the UV map."""
        for a, b in zip(vertices[:-1], vertices[1:]):
            self.seams.add((a, b) if a < b else (b, a))

    def strip(self, lower, upper, zone, skin, closed=True, flip=False, islands=None):
        """Faces between two rows of as many vertices; lower runs counter-clockwise seen from the
        side the upper row is on, so the faces look outward. islands names the UV island of each
        face in turn, where they differ along the strip."""
        if len(lower) != len(upper):
            raise ValueError(f"rows of {len(lower)} and {len(upper)} vertices")
        count = len(lower)
        for i in range(count if closed else count - 1):
            j = (i + 1) % count
            if islands is not None:
                self.island = islands[i]
            if flip:
                self.quad(lower[j], lower[i], upper[i], upper[j], zone, skin)
            else:
                self.quad(lower[i], lower[j], upper[j], upper[i], zone, skin)

    def mark(self, name, vertices):
        """Remembers vertices by name, such as a ring a later part joins or a landmark."""
        self.marks[name] = list(vertices)

    # ------------------------------------------------------------------ topology

    def edges(self):
        """Every edge as a sorted vertex pair with the faces at it."""
        found = {}
        for index, face in enumerate(self.faces):
            for k in range(len(face)):
                a, b = face[k], face[(k + 1) % len(face)]
                found.setdefault((a, b) if a < b else (b, a), []).append(index)
        return found

    def check(self, allow_open=True):
        """The problems of the net: edges with more than two faces, faces that disagree about their
        outside, and, unless allowed, open edges."""
        problems = []
        directed = {}
        for index, face in enumerate(self.faces):
            for k in range(len(face)):
                key = (face[k], face[(k + 1) % len(face)])
                if key in directed:
                    problems.append(f"faces {directed[key]} and {index} run the same way along edge {key}")
                directed[key] = index
        for key, faces in self.edges().items():
            if len(faces) > 2:
                problems.append(f"edge {key} has {len(faces)} faces")
            if len(faces) == 1 and not allow_open:
                problems.append(f"edge {key} is open")
        return problems

    def open_vertices(self):
        return {v for key, faces in self.edges().items() if len(faces) == 1 for v in key}

    def subdivided(self):
        """The net with every face split in four: a vertex at every face's centre and every edge's
        middle. A new vertex keeps the holds its makers share. The new net names the vertex at each
        edge's middle in edge_point and the one at each face's centre in face_point."""
        fine = Net()
        fine.planes = dict(self.planes)
        for point, holds in zip(self.points, self.holds):
            fine.add(point, *holds)
        edge_point, face_point = {}, []

        def middle(a, b):
            key = (a, b) if a < b else (b, a)
            if key not in edge_point:
                shared = (self.holds[a] & self.holds[b]) - {FIXED}
                edge_point[key] = fine.add((self.points[a] + self.points[b]) / 2, *shared)
            return edge_point[key]

        for face, zone, skin, island in zip(self.faces, self.zones, self.skins, self.islands):
            count = len(face)
            shared = set.intersection(*[self.holds[v] for v in face]) - {FIXED}
            centre = fine.add(np.mean([self.points[v] for v in face], axis=0), *shared)
            face_point.append(centre)
            mids = [middle(face[k], face[(k + 1) % count]) for k in range(count)]
            fine.island = island
            for k in range(count):
                fine.quad(face[k], mids[k], centre, mids[k - 1], zone, skin)
        for a, b in self.seams:
            fine.seam([a, edge_point[(a, b)], b])
        fine.marks = {name: list(vertices) for name, vertices in self.marks.items()}
        fine.edge_point = edge_point
        fine.face_point = face_point
        return fine

    # ------------------------------------------------------------------ geometry

    def array(self):
        return np.array(self.points, float)

    def vertex_faces(self):
        out = [[] for _ in self.points]
        for index, face in enumerate(self.faces):
            for v in face:
                out[v].append(index)
        return out

    def neighbours(self):
        out = [set() for _ in self.points]
        for face in self.faces:
            for k in range(len(face)):
                a, b = face[k], face[(k + 1) % len(face)]
                out[a].add(b)
                out[b].add(a)
        return out

    def normals(self, points=None):
        points = self.array() if points is None else points
        faces = np.array(self.faces)
        p = points[faces]
        face_normals = np.cross(p[:, 2] - p[:, 0], p[:, 3] - p[:, 1])
        out = np.zeros_like(points)
        for k in range(4):
            np.add.at(out, faces[:, k], face_normals)
        return out / np.maximum(np.linalg.norm(out, axis=1), 1e-18)[:, None]

    def _held(self, v, point):
        holds = self.holds[v]
        for hold in holds:
            if isinstance(hold, tuple) and hold[0] == "plane":
                origin, normal = self.planes[hold[1]]
                point = point - ((point - origin) @ normal) * normal
        if MIRROR in holds:
            point = np.array([0.0, point[1], point[2]])
        return point

    def fit(self, skins, rounds=12, rate=0.5, reach=0.08, reaches=None, nearest=(), report=None):
        """Lays every vertex onto the skin of its faces and relaxes the net along the surface: each
        round a vertex moves part of the way toward the middle of its neighbours, within the
        surface, and is laid onto the skin again. Held vertices stay in their planes. A skin is
        looked for within the reach, or within its own reach in reaches: a short one keeps the
        vertices beside a hollow, a nostril or the slit between the lips, out of it. The skins
        named in nearest take a vertex at their nearest point instead of along the vertex's normal:
        in a narrow gap, such as under the arm, the normal points along the gap to its end."""
        reaches = reaches or {}
        points = self.array()
        vertex_faces = self.vertex_faces()
        neighbours = [np.array(sorted(n)) for n in self.neighbours()]
        own_skins = [sorted({self.skins[f] for f in faces if self.skins[f] in skins}) for faces in vertex_faces]
        fixed = np.array([FIXED in h or RIGID in h or not own for h, own in zip(self.holds, own_skins)])
        is_open = np.zeros(len(points), bool)
        is_open[list(self.open_vertices())] = True

        def lay(v, point, normal):
            best = None
            for name in own_skins[v]:
                surface = skins[name]
                if name in nearest:
                    near = surface.nearest(point, reaches.get(name, reach))
                    found = None if near is None else near[0]
                else:
                    found = surface.project(point, normal, reaches.get(name, reach))
                if found is not None:
                    distance = np.linalg.norm(found - point)
                    if best is None or distance < best[0]:
                        best = (distance, found)
            return point if best is None else best[1]

        def lay_all(points):
            normals = self.normals(points)
            out = points.copy()
            for v in range(len(points)):
                if fixed[v]:
                    continue
                target = lay(v, points[v], normals[v])
                # A held vertex is laid within its planes: alternate between surface and planes.
                for _ in range(3):
                    target = self._held(v, target)
                    if not any(isinstance(h, tuple) for h in self.holds[v]) and MIRROR not in self.holds[v]:
                        break
                    target = self._held(v, lay(v, target, normals[v]))
                out[v] = target
            return out

        points = lay_all(points)
        for round_index in range(rounds):
            normals = self.normals(points)
            moved = points.copy()
            for v in range(len(points)):
                if fixed[v] or is_open[v]:
                    continue
                step = points[neighbours[v]].mean(0) - points[v]
                step -= (step @ normals[v]) * normals[v]
                moved[v] = self._held(v, points[v] + rate * step)
            points = lay_all(moved)
            if report is not None:
                report(round_index, points)
        self.points = [p for p in points]
        return points

    def slivers(self, limit=5.0):
        """The faces that stay slivers whichever way they are cut into triangles: with a corner
        under the limit, in degrees, in a triangle of the better of the cuts from two neighbouring
        corners."""
        points = self.array()

        def smallest(a, b, c):
            out = 180.0
            for p, q, r in ((a, b, c), (b, c, a), (c, a, b)):
                u, w = points[q] - points[p], points[r] - points[p]
                lengths = np.linalg.norm(u) * np.linalg.norm(w)
                out = min(out, 0.0 if lengths < 1e-18 else math.degrees(math.acos(float(np.clip(u @ w / lengths, -1.0, 1.0)))))
            return out

        found = []
        for index, face in enumerate(self.faces):
            best = 0.0
            for shift in (0, 1):
                turned = tuple(face[shift:]) + tuple(face[:shift])
                best = max(best, min(smallest(turned[0], turned[k], turned[k + 1]) for k in range(1, len(turned) - 1)))
            if best < limit:
                found.append(index)
        return found

    def folded(self, limit=0.3, hidden=()):
        """The four-sided faces that fold whichever way they are cut: the two triangles of the
        better cut look ways that agree less than the limit, as the cosine of the angle between
        them. Faces laid onto the hidden skins are left out."""
        points = self.array()

        def agreement(a, b, c, d):
            first = np.cross(points[b] - points[a], points[c] - points[a])
            second = np.cross(points[c] - points[a], points[d] - points[a])
            lengths = np.linalg.norm(first) * np.linalg.norm(second)
            return -1.0 if lengths < 1e-24 else float(first @ second / lengths)

        found = []
        for index, face in enumerate(self.faces):
            if len(face) == 4 and self.skins[index] not in hidden:
                if max(agreement(*face), agreement(*(face[1:] + face[:1]))) < limit:
                    found.append(index)
        return found

    def mend(self, hidden=(), sliver=8.0, fold=0.3, rounds=8, rate=0.5):
        """Evens out the vertices of the faces that are slivers or folded, a few rounds, until none
        is left. Vertices on the mirror plane and of the hidden parts stay."""
        for _ in range(rounds):
            bad = set(self.folded(fold, hidden)) | {f for f in self.slivers(sliver) if self.skins[f] not in hidden}
            if not bad:
                break
            loose = sorted({v for f in bad for v in self.faces[f] if not ({RIGID, MIRROR} & self.holds[v])})
            self.smooth(loose, 1, rate)

    def touching(self, skin):
        """The vertices with a face that is laid onto the skin of this name."""
        return [v for v, faces in enumerate(self.vertex_faces()) if any(self.skins[f] == skin for f in faces)]

    def smooth(self, vertices, rounds, rate=0.5):
        """Evens out the given vertices: each round every one moves part of the way toward the
        middle of its neighbours, whichever way that is."""
        points = self.array()
        neighbours = [np.array(sorted(n)) for n in self.neighbours()]
        for _ in range(rounds):
            moved = points.copy()
            for v in vertices:
                moved[v] = points[v] + rate * (points[neighbours[v]].mean(0) - points[v])
            points = moved
        self.points = [p for p in points]

    def quality(self):
        """Per face: its area, its smallest and largest corner angle in degrees, and how far its
        two halves are folded against each other (the cosine of the angle between the normals of
        its two triangles, 1 for a flat face)."""
        points = self.array()
        faces = np.array(self.faces)
        p = points[faces]
        angles = []
        for k in range(4):
            a, b = p[:, (k + 1) % 4] - p[:, k], p[:, k - 1] - p[:, k]
            cosine = (a * b).sum(1) / np.maximum(np.linalg.norm(a, axis=1) * np.linalg.norm(b, axis=1), 1e-18)
            angles.append(np.degrees(np.arccos(np.clip(cosine, -1.0, 1.0))))
        angles = np.array(angles)
        n1 = np.cross(p[:, 1] - p[:, 0], p[:, 2] - p[:, 0])
        n2 = np.cross(p[:, 2] - p[:, 0], p[:, 3] - p[:, 0])
        area = (np.linalg.norm(n1, axis=1) + np.linalg.norm(n2, axis=1)) / 2
        fold = (n1 * n2).sum(1) / np.maximum(np.linalg.norm(n1, axis=1) * np.linalg.norm(n2, axis=1), 1e-18)
        return area, angles.min(0), angles.max(0), fold, p.mean(1)

    def mirrored(self, tolerance=1e-7):
        """The whole net: this half and its mirror image across x = 0, sharing the vertices held on
        the mirror plane."""
        whole = Net()
        whole.planes = dict(self.planes)
        twin = {}
        for v, (point, holds) in enumerate(zip(self.points, self.holds)):
            whole.add(point, *holds)
        for v, (point, holds) in enumerate(zip(self.points, self.holds)):
            if MIRROR in holds:
                if abs(point[0]) > tolerance:
                    raise ValueError(f"vertex {v} is held on the mirror plane but lies at x = {point[0]}")
                twin[v] = v
            else:
                if abs(point[0]) <= tolerance:
                    raise ValueError(f"vertex {v} lies on the mirror plane but is not held on it")
                twin[v] = whole.add((-point[0], point[1], point[2]), *holds)
        for face, zone, skin, island in zip(self.faces, self.zones, self.skins, self.islands):
            whole.island = island
            whole.face(face, zone, skin)
        for face, zone, skin, island in zip(self.faces, self.zones, self.skins, self.islands):
            whole.island = island[:-2] + "_L" if island is not None and island.endswith("_R") else island
            whole.face([twin[v] for v in reversed(face)], zone, skin)
        for a, b in self.seams:
            whole.seam([a, b])
            whole.seam([twin[a], twin[b]])
        whole.marks = {name: list(vertices) for name, vertices in self.marks.items()}
        whole.twin = twin
        return whole
