"""Heroic proportions: smooth spatial warps applied to a body and to its reference after the fit.
Coordinates are those of the unwarped reference (feet at z = 0, facing +y).

Male: taller with a realistic head size, broader shoulders, a slightly narrower waist and a
stronger neck. Female: taller with a realistic head size and a fuller bust set a little closer
together. Both: the feet turned
to the same small toe-out angle (the reference poses differ)."""

import math

import numpy as np

from . import feet


PARAMS = {
    "male": {
        "scale": 1.07, "neck_base": 1.40, "chin": 1.455, "shoulder": 0.022, "waist": 0.045,
        "neck": 0.12, "bust": 0.0, "bust_inward": 0.0, "toe_out": 8.0,
    },
    "female": {
        "scale": 1.04, "neck_base": 1.365, "chin": 1.42, "shoulder": 0.006, "waist": 0.02,
        "neck": 0.0, "bust": 0.22, "bust_inward": 0.009, "toe_out": 8.0,
    },
}


def smoothstep(x, a, b):
    t = np.clip((x - a) / (b - a), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def bump(x, a, m, b):
    return smoothstep(x, a, m) * (1.0 - smoothstep(x, m, b))


def bust_centres(surface):
    """Apex of each breast and a centre behind it from which the bust is scaled."""
    co = surface.co
    region = co[(co[:, 0] > 0.04) & (co[:, 0] < 0.15) & (co[:, 2] > 0.98) & (co[:, 2] < 1.25)]
    apex = region[np.argmax(region[:, 1])]
    centre = apex - np.array([0.012, 0.075, 0.01])
    return apex, centre


def warp(points, sex, surface=None):
    """Returns the warped copy of an (N, 3) array."""
    p = np.array(points, np.float64)
    q = p.copy()
    k = PARAMS[sex]
    ax = np.abs(p[:, 0])
    sx = np.sign(p[:, 0])
    x, y, z = p[:, 0], p[:, 1], p[:, 2]
    # Fuller bust: scale each breast about a centre behind its apex, with smooth weights that
    # fade toward the sternum, the armpit and the back.
    if k["bust"] > 0 and surface is not None:
        _apex, c = bust_centres(surface)
        for side in (1.0, -1.0):
            cc = c * np.array([side, 1.0, 1.0])
            d = p - cc
            dist = np.linalg.norm(d * np.array([1.0, 0.8, 1.0]), axis=1)
            w = np.exp(-(dist / 0.085) ** 2) * smoothstep(d[:, 1], -0.01, 0.05)
            push = d * (k["bust"] * w)[:, None]
            push[:, 0] *= smoothstep(side * p[:, 0], 0.0, 0.06)
            q += push
            # Breasts a little closer together; nothing moves at the centre line.
            w_in = np.exp(-(dist / 0.09) ** 2) * smoothstep(d[:, 1], -0.02, 0.04) * smoothstep(side * p[:, 0], 0.0, 0.045)
            q[:, 0] -= side * k["bust_inward"] * w_in
    # Narrower waist on the torso only (the forearms hang beside it further out).
    w_waist = bump(z, 0.90, 1.02, 1.14) * (1.0 - smoothstep(ax, 0.17, 0.21))
    q[:, 0] -= x * k["waist"] * w_waist
    # Stronger neck: radial scale around the neck axis.
    if k["neck"] > 0:
        w_neck = bump(z, k["neck_base"] - 0.05, k["neck_base"] + 0.02, k["chin"] + 0.01) * (1.0 - smoothstep(ax, 0.07, 0.10))
        neck_y = 0.01
        q[:, 0] += x * k["neck"] * w_neck
        q[:, 1] += (y - neck_y) * k["neck"] * w_neck
    # Broader shoulders: move the shoulder girdle and the whole arm outward.
    shoulder_w = smoothstep(ax, 0.06, 0.17) * smoothstep(z, 1.10, 1.27) * (1.0 - smoothstep(z, k["neck_base"], k["chin"]))
    arm_w = smoothstep(ax, 0.17, 0.215) * smoothstep(z, 0.55, 0.65)
    q[:, 0] += sx * k["shoulder"] * np.maximum(shoulder_w, arm_w)
    # Feet: turn each foot about its ankle so the toe-out angle is the same for both bodies.
    if surface is not None:
        ankle, _r, fwd, _lat, _up = feet.frame(surface)
        angle = math.degrees(math.atan2(fwd.x, fwd.y))
        turn = math.radians(angle - k["toe_out"])
        w_foot = 1.0 - smoothstep(z, 0.07, 0.16)
        for side in (1.0, -1.0):
            on = (x * side > 0) * w_foot
            a = turn * side * on
            cx, cy = ankle.x * side, ankle.y
            dx, dy = q[:, 0] - cx, q[:, 1] - cy
            ca, sa = np.cos(a), np.sin(a)
            nx = cx + dx * ca - dy * sa
            ny = cy + dx * sa + dy * ca
            q[:, 0] = np.where(on > 0, nx, q[:, 0])
            q[:, 1] = np.where(on > 0, ny, q[:, 1])
    # Height: scale the body about the feet and keep the head at its realistic size.
    s = k["scale"]
    w_head = smoothstep(z, k["neck_base"], k["chin"] + 0.01)
    pivot = np.array([0.0, 0.0, k["chin"]])
    body = q * s
    head = pivot * s + (q - pivot)
    q = body * (1.0 - w_head)[:, None] + head * w_head[:, None]
    return q
