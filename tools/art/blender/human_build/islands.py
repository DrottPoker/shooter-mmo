"""The islands of the UV map that follow from the built net's shape instead of its cage: the hand's
back and palm, the toes' top and sole, and the back of each ear.

A hand opens like a glove. Its faces are parted by which way they look, toward the palm's side or
away from it, so the seam runs along the sides of the hand and of every finger and around the tips.
The toes are parted the same way into the foot's top and its sole, whose seam the cage names as far
as the toes' webs.
An ear stands out from the head, so its front and its back would lie on top of each other in the
map: the faces of the ear that look back at the head are an island of their own, and its front
stays part of the head's island. The back is one piece: the few faces inside the ear's folds that
look back as well stay with the front."""

import numpy as np

from .net import Net

HAND = "hand"
TOES = "toes"
FOOT_TOP, FOOT_SOLE = "foot_top", "foot_sole"
# The ear's front looks this way on the right side, out from the head and a little forward.
EAR_OUTWARD = (1.0, 0.35, 0.0)


def _unit(v):
    v = np.asarray(v, float)
    return v / max(np.linalg.norm(v), 1e-12)


def _normals(net: Net):
    points = net.array()
    out = []
    for face in net.faces:
        p = points[list(face)]
        out.append(_unit(np.cross(p[1] - p[0], p[2] - p[0]) + np.cross(p[2] - p[0], p[3 % len(face)] - p[0])))
    return np.array(out)


def _edge_faces(net: Net, faces):
    """The pairs of the given faces that share an edge, as (face, other face, edge)."""
    by_edge = {}
    for f in faces:
        face = net.faces[f]
        for a, b in zip(face, face[1:] + face[:1]):
            by_edge.setdefault((a, b) if a < b else (b, a), []).append(f)
    return [(pair[0], pair[1], edge) for edge, pair in by_edge.items() if len(pair) == 2]


def _looking(net: Net, normals, faces, toward):
    """Which of the faces look a way, after a face that its neighbours outvote has taken their
    side, so each half is one piece."""
    looks = {f: bool(normals[f] @ toward > 0.0) for f in faces}
    pairs = _edge_faces(net, faces)
    for _ in range(4):
        votes = {f: 0 for f in faces}
        for a, b, _edge in pairs:
            votes[a] += 1 if looks[b] else -1
            votes[b] += 1 if looks[a] else -1
        changed = False
        for f in faces:
            # Outvoted by three of four neighbours or more.
            if looks[f] and votes[f] <= -2 or not looks[f] and votes[f] >= 2:
                looks[f] = not looks[f]
                changed = True
        if not changed:
            break
    return looks


def part_hands(net: Net, palmar):
    """Names the faces of each hand's island hand_back or hand_palm. palmar is the way the right
    palm looks; the left hand's is its mirror image."""
    normals = _normals(net)
    for side, sign in (("R", 1.0), ("L", -1.0)):
        faces = [f for f, island in enumerate(net.islands) if island == f"{HAND}_{side}"]
        palm = _looking(net, normals, faces, _unit(np.asarray(palmar, float) * (sign, 1.0, 1.0)))
        for f in faces:
            net.islands[f] = f"{HAND}_{'palm' if palm[f] else 'back'}_{side}"


def part_toes(net: Net):
    """Names the faces of each foot's toes foot_top or foot_sole: the faces that look down belong
    to the sole."""
    normals = _normals(net)
    for side in ("R", "L"):
        faces = [f for f, island in enumerate(net.islands) if island == f"{TOES}_{side}"]
        sole = _looking(net, normals, faces, np.array([0.0, 0.0, -1.0]))
        for f in faces:
            net.islands[f] = f"{FOOT_SOLE if sole[f] else FOOT_TOP}_{side}"


def part_ears(net: Net, ear_skin="ear"):
    """Gives the back of each ear an island of its own: the faces of the ear that look back at the
    head. A face that its neighbours outvote takes their side, and of the pieces that then look
    back the largest is the ear's back."""
    normals = _normals(net)
    points = net.array()
    ears = [f for f, skin in enumerate(net.skins) if skin == ear_skin]
    for side, sign in (("R", 1.0), ("L", -1.0)):
        faces = [f for f in ears if sign * points[list(net.faces[f])][:, 0].mean() > 0]
        outward = _unit(np.asarray(EAR_OUTWARD) * (sign, 1.0, 1.0))
        back = {f: bool(normals[f] @ outward < 0.0) for f in faces}
        pairs = _edge_faces(net, faces)
        for _ in range(4):
            votes = {f: 0 for f in faces}
            for a, b, _edge in pairs:
                votes[a] += 1 if back[b] else -1
                votes[b] += 1 if back[a] else -1
            changed = False
            for f in faces:
                if back[f] and votes[f] <= -2 or not back[f] and votes[f] >= 2:
                    back[f] = not back[f]
                    changed = True
            if not changed:
                break
        beside = {f: [] for f in faces if back[f]}
        for a, b, _edge in pairs:
            if back[a] and back[b]:
                beside[a].append(b)
                beside[b].append(a)
        pieces, left = [], set(beside)
        while left:
            piece, todo = set(), [min(left)]
            while todo:
                f = todo.pop()
                if f in left:
                    left.discard(f)
                    piece.add(f)
                    todo.extend(beside[f])
            pieces.append(piece)
        for f in max(pieces, key=lambda piece: (len(piece), -min(piece)), default=()):
            net.islands[f] = f"ear_back_{side}"
