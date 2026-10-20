"""Procedural painting of the base character textures from baked per-texel data: rest position
(unwarped, metres), low-poly normal, detail normal from the realistic sculpt, ambient
occlusion and body part. Everything is a function of the 3D surface, so colours match across
UV seams. Colours are sRGB in 0..1."""

import math

import numpy as np
from mathutils import Vector
from mathutils.kdtree import KDTree


PARTS = ("torso", "arm", "leg", "neck", "head", "hand", "foot", "socket", "mouth")
SKIN = {"male": np.array((0.78, 0.60, 0.50)), "female": np.array((0.87, 0.68, 0.58))}
UNDERWEAR = np.array((0.60, 0.66, 0.74))
LIGHT = np.array((0.0, 0.34, 0.94)) / np.linalg.norm((0.0, 0.34, 0.94))


def smoothstep(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def _hash(ix, iy, iz, seed):
    h = (ix * 73856093) ^ (iy * 19349663) ^ (iz * 83492791) ^ (seed * 2654435761)
    h = h & 0xFFFFFFFF
    h = ((h ^ (h >> 13)) * 1274126177) & 0xFFFFFFFF
    return ((h ^ (h >> 16)) & 0xFFFF) / 65535.0


def value_noise(p, freq, seed=0):
    """Smooth 3D value noise in 0..1."""
    q = p * freq
    i = np.floor(q).astype(np.int64)
    f = q - i
    f = f * f * (3.0 - 2.0 * f)
    out = 0.0
    for dx in (0, 1):
        for dy in (0, 1):
            for dz in (0, 1):
                w = (f[..., 0] if dx else 1 - f[..., 0]) * (f[..., 1] if dy else 1 - f[..., 1]) * (f[..., 2] if dz else 1 - f[..., 2])
                out = out + w * _hash(i[..., 0] + dx, i[..., 1] + dy, i[..., 2] + dz, seed)
    return out


def fbm(p, freq, octaves=3, seed=0):
    total, amp, norm = 0.0, 1.0, 0.0
    for o in range(octaves):
        total = total + amp * value_noise(p, freq * (2 ** o), seed + o)
        norm += amp
        amp *= 0.5
    return total / norm


def speckles(p, freq, density, seed=0):
    """Sparse round dots: 1 inside a dot, soft edge."""
    q = p * freq
    i = np.floor(q).astype(np.int64)
    f = q - i
    keep = _hash(i[..., 0], i[..., 1], i[..., 2], seed) < density
    cx = _hash(i[..., 0], i[..., 1], i[..., 2], seed + 1) * 0.6 + 0.2
    cy = _hash(i[..., 0], i[..., 1], i[..., 2], seed + 2) * 0.6 + 0.2
    cz = _hash(i[..., 0], i[..., 1], i[..., 2], seed + 3) * 0.6 + 0.2
    r = _hash(i[..., 0], i[..., 1], i[..., 2], seed + 4) * 0.15 + 0.12
    d = np.sqrt((f[..., 0] - cx) ** 2 + (f[..., 1] - cy) ** 2 + (f[..., 2] - cz) ** 2)
    return keep * smoothstep(r, r * 0.55, d)


def mix(a, b, t):
    t = np.asarray(t)[..., None]
    return a * (1.0 - t) + b * t


def dot(a, b):
    return (a * b).sum(-1)


def mirrored(P):
    """Mirror-symmetric coordinates: the body texture is shared by both halves."""
    Q = P.copy()
    Q[..., 0] = np.abs(Q[..., 0])
    return Q


def skin_base(sex, P):
    P = mirrored(P)
    base = np.broadcast_to(SKIN[sex], P.shape).copy()
    n = fbm(P, 9.0, 3, seed=11) - 0.5
    base *= (1.0 + 0.06 * n)[..., None]
    warm = fbm(P, 3.0, 2, seed=5)
    base = mix(base, base * np.array((1.04, 0.95, 0.93)), 0.5 * warm)
    return base


def shading(Nlo, Nhi, AO, w_detail, detail_gain=1.15):
    dlo = np.clip(dot(Nlo, LIGHT), -1.0, 1.0)
    dhi = np.clip(dot(Nhi, LIGHT), -1.0, 1.0)
    form = 1.0 + 0.20 * (dlo - 0.55)
    detail = 1.0 + detail_gain * w_detail * (dhi - dlo)
    ao = 1.0 - 0.55 * (1.0 - np.clip(AO, 0.35, 1.0))
    return form * detail * ao


def warm_shadows(color, shade):
    """Shadowed skin turns warmer and redder, as in painted skin."""
    dark = np.clip(1.0 - shade, 0.0, 1.0)[..., None]
    return color * (1.0 + dark * np.array((0.10, -0.06, -0.10)))


def briefs_mask(sex, P, part):
    x, y, z = P[..., 0], P[..., 1], P[..., 2]
    ax = np.abs(x)
    torso_phi = np.arctan2(ax, y - 0.02)  # 0 front, pi back
    if sex == "male":
        top = 0.925 + 0.022 * (1 - np.cos(torso_phi)) / 2
        leg_base, leg_side, leg_back = 0.80, 0.055, 0.014
    else:
        top = 0.905 + 0.020 * (1 - np.cos(torso_phi)) / 2
        leg_base, leg_side, leg_back = 0.815, 0.065, 0.016
    psi = np.arctan2(ax - 0.10, y - 0.02)  # around the thigh: 0 front, +pi/2 outside
    leg = leg_base + leg_side * np.sin(psi) - leg_back * (1 - np.cos(psi)) / 2
    inside = smoothstep(top + 0.0015, top - 0.0015, z) * smoothstep(leg - 0.0015, leg + 0.0015, z)
    region = np.isin(part, (PARTS.index("torso"), PARTS.index("leg")))
    m = inside * region
    band = m * smoothstep(top - 0.024, top - 0.021, z)
    hem = m * smoothstep(leg + 0.008, leg + 0.005, z)
    return m, band, hem


def bra_mask(P, part, apex):
    """A balconette bra from rest positions, relative to the right breast apex (bx, by, bz):
    an underbust band all around, cups whose upper edge rises from a low centre gore over the
    breast to the strap anchor and falls along a side wing to the band, and straps that run
    from the anchor over the shoulder to the back band. Returns the cloth mask and the trim."""
    x, y, z = P[..., 0], P[..., 1], P[..., 2]
    u = np.abs(x)
    torso = (part == PARTS.index("torso")).astype(float)
    bx, by, bz = apex
    aa = 0.0012
    band_lo, band_hi = bz - 0.080, bz - 0.050
    band = smoothstep(band_lo - aa, band_lo + aa, z) * smoothstep(band_hi + aa, band_hi - aa, z)
    # Upper edge of the cups: over the front it rises with u = |x| from a low centre gore to the
    # strap anchor; from there the side wing falls with the angle around the torso and meets
    # the band just behind the side, so the cloth ends in a clean edge all the way round.
    gore_u, anchor_u = 0.010, bx + 0.030
    gore_z, anchor_z = band_hi + 0.010, bz + 0.050
    yc = by - 0.13  # torso axis behind the bust
    theta = np.arctan2(u, y - yc)
    theta_anchor = math.atan2(anchor_u, by - 0.03 - yc)
    theta_side = math.radians(100.0)
    s1 = np.clip((u - gore_u) / (anchor_u - gore_u), 0.0, 1.0)
    rise = gore_z + (anchor_z - gore_z) * np.sin(s1 * math.pi * 0.5) ** 1.3
    fall = anchor_z + (band_hi - anchor_z) * np.clip((theta - theta_anchor) / (theta_side - theta_anchor), 0.0, 1.0) ** 0.8
    top = np.where(theta <= theta_anchor, rise, fall)
    cups = smoothstep(band_hi - 0.003, band_hi, z) * smoothstep(top + aa, top - aa, z)
    cups *= smoothstep(theta_side + 0.02, theta_side - 0.02, theta)
    # Straps: the centre line moves smoothly from the front anchor to the back over the shoulder.
    over = smoothstep(-0.05, 0.05, y)  # 0 behind the shoulder, 1 in front of it
    strap_u = (bx - 0.010) + (anchor_u - 0.004 - (bx - 0.010)) * over
    strap_u = strap_u - (z - anchor_z).clip(0.0, None) * 0.10 * over
    z_start = band_hi + (anchor_z - 0.004 - band_hi) * over
    strap = smoothstep(0.0068, 0.0054, np.abs(u - strap_u)) * smoothstep(z_start - aa, z_start + aa, z)
    m = np.clip(band + cups + strap, 0.0, 1.0) * torso
    # Trim: a narrow darker edge along every border of the cloth.
    edge_top = smoothstep(0.0032, 0.0016, top - z) * cups
    edge_band = (smoothstep(band_lo + 0.0032, band_lo + 0.0016, z) + smoothstep(band_hi - 0.0032, band_hi - 0.0016, z) * (1 - cups)) * band
    edge_strap = smoothstep(0.0040, 0.0054, np.abs(u - strap_u)) * strap
    hem = np.clip(edge_top + edge_band + edge_strap, 0, 1) * torso
    return m, hem


def nipple_mask(P, apex, radius, inner=0.7):
    """1 within inner * radius of the apex (front view), fading to 0 at radius."""
    x, y, z = P[..., 0], P[..., 1], P[..., 2]
    d = np.sqrt((np.abs(x) - apex[0]) ** 2 + (z - apex[2]) ** 2)
    front = smoothstep(apex[1] - 0.03, apex[1] - 0.01, y)
    return smoothstep(radius, radius * inner, d) * front


def paint_body(sex, P, Nlo, Nhi, AO, part, lm):
    y, z = P[..., 1], P[..., 2]
    chin = lm["chin_bottom"][0][2]
    w_detail = np.isin(part, [PARTS.index(p) for p in ("torso", "arm", "leg", "neck")]).astype(float)
    w_detail *= smoothstep(chin - 0.005, chin - 0.05, z)
    wrists = lm["wrist_centres"]
    for c in wrists:
        w_detail *= smoothstep(0.015, 0.06, np.linalg.norm(P - c, axis=-1))
    w_detail *= smoothstep(0.14, 0.20, z)  # ankles and feet
    neck_base = lm["neck_base_z"]
    w_detail *= 1.0 - 0.6 * smoothstep(neck_base - 0.06, neck_base + 0.02, z)
    # The head is shaded more softly (its AO under the jaw and behind the ears reads as dirt),
    # fading in from the neck so the face and body textures agree where they meet.
    soft = smoothstep(chin - 0.05, chin - 0.01, z)
    AO = AO + (1.0 - 0.6 * (1.0 - AO) - AO) * soft
    shade = shading(Nlo, Nhi, AO, w_detail, detail_gain=0.95)
    skin = skin_base(sex, P)
    # Redder knees, elbows, knuckles and ears.
    knees = smoothstep(0.06, 0.02, np.abs(z - lm["knee_z"])) * (y > lm["knee_front_y"]) * (part == PARTS.index("leg"))
    skin = mix(skin, skin * np.array((1.05, 0.90, 0.88)), 0.5 * knees)
    color = warm_shadows(skin * shade[..., None], shade)
    # Nipples (male) or the bra (female), and briefs for both.
    if sex == "male":
        # Areola: browner pink than the skin, deepest at the centre and fading to the edge.
        torso = part == PARTS.index("torso")
        areola = nipple_mask(P, lm["chest_apex"], 0.0115, inner=0.25) * torso
        color = mix(color, color * np.array((0.72, 0.58, 0.58)), 0.8 * areola)
    cloth_shade = shading(Nlo, Nhi, AO, w_detail, detail_gain=0.18)
    cloth = UNDERWEAR * cloth_shade[..., None]
    weave = fbm(mirrored(P), 220.0, 1, seed=3) - 0.5
    cloth *= (1.0 + 0.05 * weave)[..., None]
    m, band, hem = briefs_mask(sex, P, part)
    color = mix(color, cloth, m)
    color = mix(color, color * 0.80, band * 0.9)
    color = mix(color, color * 0.84, hem * 0.9)
    if sex == "female":
        bm, bhem = bra_mask(P, part, lm["chest_apex"])
        color = mix(color, cloth, bm)
        color = mix(color, color * 0.82, bhem * 0.9)
    # Finger and toe nails: on the back of each digit's end, from the tip back over the end.
    for key, part_name, sizes in (("finger", "hand", (0.0068, 0.0068, 0.0068, 0.0068, 0.0078)),
                                  ("toe", "foot", (0.0080, 0.0050, 0.0046, 0.0042, 0.0040))):
        tips, backs, fronts = lm[key + "_tips"], lm[key + "_back"], lm[key + "_front"]
        for k, (tip, back, front) in enumerate(zip(tips, backs, fronts)):
            n = back - front
            n = n / np.linalg.norm(n)
            r = sizes[k % 5]
            centre = tip + (back - tip) * 0.55 - n * 0.001
            d = np.linalg.norm(P - centre, axis=-1)
            nail = smoothstep(r, r * 0.8, d) * smoothstep(0.30, 0.50, dot(Nlo, n)) * (part == PARTS.index(part_name))
            color = mix(color, color * np.array((1.12, 1.02, 1.0)) + 0.04, 0.7 * nail)
            edge = nail * smoothstep(r * 0.75, r * 0.45, np.linalg.norm(P - tip, axis=-1))
            color = mix(color, np.array((0.93, 0.88, 0.84)), 0.45 * edge)
    return np.clip(color, 0.0, 1.0)


def _almond(u, W, up, low, tilt, upper):
    t = np.clip(1.0 - (u / W) ** 2, 0.0, 1.0)
    return tilt * u + (up * t ** 0.7 if upper else -low * t ** 0.85)


def eye_area_layer(color, P, part, eyeball, opening, sex):
    """The skin around the modelled eyes: wet inner lid rims on the eyeball, lash lines along
    the lid margins, the caruncle, a lid crease and the dark socket behind the eyeball.
    eyeball: (centre of the eye at x > 0, radius); opening: the lid margin almond (half width,
    upper and lower height, tilt) in front of the eyeball centre."""
    c, r = eyeball
    W, up, low, tilt = opening
    x, y, z = P[..., 0], P[..., 1], P[..., 2]
    u = np.abs(x) - c[0]  # positive toward the outer corner
    v = z - c[2]
    rr = np.sqrt(u ** 2 + (y - c[1]) ** 2 + v ** 2)
    front = smoothstep(c[1] - 0.004, c[1] + 0.002, y)
    near = smoothstep(r + 0.0075, r + 0.004, rr) * front
    v_up = _almond(u, W, up, low, tilt, True)
    v_lo = _almond(u, W, up, low, tilt, False)
    inside_u = smoothstep(W + 0.0015, W - 0.0005, np.abs(u))
    # Socket (behind the eyeball, hardly ever seen) and the wet rims rolling onto the eyeball.
    socket = (part == PARTS.index("socket")).astype(float)
    color = mix(color, np.array((0.32, 0.17, 0.16)), socket)
    rim = smoothstep(r + 0.0018, r + 0.0009, rr) * front * (1 - socket)
    color = mix(color, np.array((0.78, 0.50, 0.48)), 0.85 * rim)
    # Upper lash line, thicker toward the outer corner; lighter lower lash line.
    lash_w = 0.0009 + 0.0008 * smoothstep(-W, W, u)
    upper = smoothstep(v_up - 0.0003, v_up + 0.0001, v) * smoothstep(v_up + lash_w + 0.0004, v_up + lash_w, v)
    lash = np.array((0.12, 0.08, 0.07))
    color = mix(color, lash, 0.95 * upper * inside_u * near * (1 - rim))
    if sex == "female":
        flick = smoothstep(0.0009, 0.0003, np.abs(v - (tilt * W + 0.0008 + (u - W) * 0.35)))
        flick *= smoothstep(W - 0.001, W, u) * smoothstep(W + 0.0038, W + 0.0022, u) * front
        color = mix(color, lash, 0.9 * flick)
    lower = smoothstep(v_lo + 0.0002, v_lo - 0.0002, v) * smoothstep(v_lo - 0.0010, v_lo - 0.0005, v)
    lower *= smoothstep(-W + 0.004, W * 0.2, u) * inside_u * near * (1 - rim)
    color = mix(color, np.array((0.42, 0.30, 0.28)), 0.55 * lower)
    # Caruncle at the inner corner.
    car = smoothstep(0.0026, 0.0012, np.sqrt((u + W - 0.0008) ** 2 + (v + tilt * W) ** 2)) * front
    color = mix(color, np.array((0.80, 0.52, 0.50)), 0.7 * car)
    # Lid crease above the upper lid and a slightly tinted lid.
    t_up = np.clip(1 - (u / W) ** 2, 0, 1)
    crease_v = tilt * u + up * t_up ** 0.7 * 0.4 + 0.0062
    crease = smoothstep(0.0012, 0.0, np.abs(v - crease_v)) * smoothstep(W, W * 0.3, np.abs(u)) * front
    color = mix(color, color * 0.80, 0.55 * crease)
    lid = smoothstep(v_up + 0.0006, v_up + 0.0016, v) * smoothstep(crease_v + 0.001, crease_v - 0.0005, v)
    lid *= smoothstep(W + 0.001, W * 0.6, np.abs(u)) * front * (1 - rim)
    color = mix(color, color * np.array((0.93, 0.88, 0.88)), 0.6 * lid)
    return color, lid


def mouth_layer(color, P, part, mouth_front_y):
    """Inside of the mouth: lip-coloured just behind the lips, darker and redder further in."""
    inside = (part == PARTS.index("mouth")).astype(float)
    depth = np.clip((mouth_front_y - P[..., 1]) / 0.035, 0.0, 1.0)
    wet = mix(np.broadcast_to(np.array((0.62, 0.30, 0.30)), P.shape), np.array((0.16, 0.05, 0.06)), smoothstep(0.08, 0.6, depth))
    return mix(color, wet, inside)


def lip_layer(color, P, lm, sex, lip_rgb):
    x, y, z = P[..., 0], P[..., 1], P[..., 2]
    zm = lm["mouth_centre"][0][2]
    xc, zc = abs(lm["mouth_corner"][0][0]), lm["mouth_corner"][0][2]
    yc = lm["mouth_corner"][0][1]
    u = np.abs(x)
    W = xc * 1.10
    line = zm + (zc - zm) * np.clip(u / xc, 0, 1.3) ** 2
    v = z - line
    front = smoothstep(yc - 0.02, yc - 0.008, y)
    t = np.clip(1 - (u / W) ** 2, 0, 1)
    cupid = 1.0 - 0.20 * np.exp(-(u / 0.0032) ** 2) + 0.10 * np.exp(-((u - 0.0058) / 0.0035) ** 2)
    h_up = (0.0080 if sex == "male" else 0.0085) * t ** 0.55 * cupid
    h_lo = (0.0102 if sex == "male" else 0.0108) * t ** 0.75
    aa = 0.0005
    upper = smoothstep(-aa, aa, v) * smoothstep(h_up + aa, h_up - aa, v)
    lower = smoothstep(-h_lo - aa, -h_lo + aa, v) * smoothstep(aa, -aa, v)
    lips = np.clip(upper + lower, 0, 1) * front
    lip = np.broadcast_to(np.asarray(lip_rgb), P.shape).copy()
    lip = mix(lip, lip * 0.86, upper)  # upper lip a little darker, it faces down
    shine = smoothstep(0.004, 0.0, np.sqrt((u * 0.55) ** 2 + (v + h_lo * 0.45) ** 2)) * lower
    lip = mix(lip, np.minimum(lip * 1.25 + 0.05, 1.0), 0.6 * shine)
    color = mix(color, lip, 0.92 * lips)
    mouth = smoothstep(0.0009, 0.0002, np.abs(v)) * smoothstep(W, W * 0.5, u) * front
    color = mix(color, np.array((0.30, 0.17, 0.16)), 0.8 * mouth)
    corners = smoothstep(0.0035, 0.0012, np.sqrt((u - W * 0.98) ** 2 + v ** 2)) * front
    color = mix(color, color * 0.75, 0.6 * corners)
    under = smoothstep(0.004, 0.0, np.abs(v + h_lo + 0.003)) * smoothstep(W, W * 0.3, u) * front
    color = mix(color, color * 0.86, 0.6 * under)
    return color, lips


def face_shading(color, P, lm, eye, sex, Nlo):
    """Painted light and shadow of the face, in the spirit of hand-painted game faces."""
    x, y, z = P[..., 0], P[..., 1], P[..., 2]
    u = np.abs(x)
    ex, ey, ez = eye

    def blob(cu, cz, ru, rz, front_y):
        d = np.sqrt(((u - cu) / ru) ** 2 + ((z - cz) / rz) ** 2)
        return smoothstep(1.0, 0.0, d) * smoothstep(front_y - 0.03, front_y, y)

    socket = blob(ex - 0.002, ez + 0.002, 0.024, 0.017, ey - 0.01) * (1 - blob(ex, ez, 0.013, 0.006, ey - 0.01))
    color = mix(color, color * np.array((0.84, 0.80, 0.82)), 0.65 * socket)
    brow = blob(ex - 0.002, ez + 0.019, 0.026, 0.007, ey - 0.005)
    color = mix(color, np.minimum(color * 1.08, 1), 0.7 * brow)
    cheek = blob(ex + 0.010, ez - 0.026, 0.020, 0.014, ey - 0.03)
    color = mix(color, np.minimum(color * 1.07, 1), 0.7 * cheek)
    if sex == "male":
        hollow = blob(ex + 0.022, ez - 0.048, 0.012, 0.022, ey - 0.06)
        color = mix(color, color * 0.93, 0.6 * hollow)
    nose_tip = lm["nose_tip"][0]
    sell = lm["sellion"][0]
    bridge = smoothstep(0.006, 0.0, u) * smoothstep(sell[2] + 0.004, sell[2] - 0.002, z) * smoothstep(nose_tip[2] - 0.004, nose_tip[2] + 0.004, z)
    color = mix(color, np.minimum(color * 1.10, 1), 0.6 * bridge * (y > sell[1] - 0.01))
    sub = lm["subnasale"][0]
    # Nostrils only where the nose faces down (its underside), never on the wings.
    nostril = smoothstep(0.0045, 0.0015, np.sqrt((u - 0.0085) ** 2 + (z - sub[2] - 0.0035) ** 2)) * (y > sub[1] - 0.025)
    nostril *= smoothstep(-0.35, -0.75, Nlo[..., 2])
    color = mix(color, color * np.array((0.55, 0.42, 0.40)), 0.75 * nostril)
    # Nasolabial fold from the nose wing to beside the mouth corner.
    corner = lm["mouth_corner"][0]
    a = np.array((0.016, sub[2] + 0.006))
    b = np.array((abs(corner[0]) + 0.008, corner[2] - 0.006))
    ab = b - a
    t = np.clip(((u - a[0]) * ab[0] + (z - a[1]) * ab[1]) / (ab @ ab), 0, 1)
    d = np.sqrt((u - a[0] - t * ab[0]) ** 2 + (z - a[1] - t * ab[1]) ** 2)
    fold = smoothstep(0.003, 0.0, d) * (y > corner[1] - 0.03)
    color = mix(color, color * 0.88, (0.55 if sex == "male" else 0.3) * fold)
    chin = lm["chin_front"][0]
    chin_hl = blob(0.0, chin[2] + 0.004, 0.013, 0.008, chin[1] - 0.01)
    color = mix(color, np.minimum(color * 1.07, 1), 0.6 * chin_hl)
    return color


def stubble_layer(color, P, lm, eye, strength, lips):
    x, z = P[..., 0], P[..., 2]
    u = np.abs(x)
    ez = eye[2]
    chin = lm["chin_bottom"][0][2]
    # Clean cheek line from the sideburn down to beside the mouth corner.
    cheek_line = ez - 0.040 - (0.066 - u) * 0.45
    region = smoothstep(cheek_line + 0.002, cheek_line - 0.004, z)
    region *= smoothstep(chin - 0.045, chin - 0.025, z)  # fades out on the throat
    nose = lm["subnasale"][0][2]
    region *= 1 - smoothstep(nose - 0.004, nose + 0.002, z) * smoothstep(0.03, 0.02, u)
    region *= 1 - lips
    dots = speckles(P, 2200.0, 0.75, seed=23) + 0.5 * speckles(P, 3100.0, 0.6, seed=29)
    shade = region * (0.15 + 0.85 * np.clip(dots, 0, 1))
    return mix(color, color * np.array((0.58, 0.58, 0.63)), strength * shade)


def scar_and_lines(color, P, lm, eye):
    x, y, z = P[..., 0], P[..., 1], P[..., 2]
    u = np.abs(x)
    ex, ey, ez = eye
    front = smoothstep(ey - 0.04, ey - 0.02, y)
    # Forehead lines.
    for k, h in enumerate((0.032, 0.040)):
        wav = 0.0010 * np.sin(u * 120 + k)
        line = smoothstep(0.0009, 0.0, np.abs(z - (ez + h + wav))) * smoothstep(0.036, 0.016, u)
        color = mix(color, color * 0.86, 0.45 * line * front)
    # Crow's feet at the outer eye corners.
    for k, ang in enumerate((-0.35, 0.0, 0.35)):
        du, dv = u - (ex + 0.019), z - ez
        along = du * math.cos(ang) + dv * math.sin(ang)
        across = -du * math.sin(ang) + dv * math.cos(ang)
        line = smoothstep(0.0009, 0.0, np.abs(across)) * smoothstep(0.0, 0.002, along) * smoothstep(0.012, 0.006, along)
        color = mix(color, color * 0.84, 0.5 * line)
    # A sword scar over the left brow down onto the cheek (character's left, +x).
    a = np.array((ex + 0.006, ez + 0.035))
    b = np.array((ex + 0.020, ez - 0.035))
    ab = b - a
    xx = x
    t = np.clip(((xx - a[0]) * ab[0] + (z - a[1]) * ab[1]) / (ab @ ab), 0, 1)
    d = np.sqrt((xx - a[0] - t * ab[0]) ** 2 + (z - a[1] - t * ab[1]) ** 2)
    width = 0.0016 * np.sin(t * math.pi) ** 0.5 + 0.0003
    eye_gap = smoothstep(0.005, 0.009, np.sqrt((xx - ex) ** 2 + (z - ez) ** 2 * 2.5))
    scar = smoothstep(width + 0.0004, width - 0.0002, d) * front * eye_gap * (x > 0)
    edge = smoothstep(width + 0.0018, width + 0.0004, d) * (1 - scar) * front * eye_gap * (x > 0)
    color = mix(color, color * np.array((0.82, 0.70, 0.70)), 0.6 * edge)
    color = mix(color, np.array((0.90, 0.72, 0.68)), 0.8 * scar)
    return color


def blush_layer(color, P, eye, lid):
    x, y, z = P[..., 0], P[..., 1], P[..., 2]
    u = np.abs(x)
    ex, ey, ez = eye
    d = np.sqrt(((u - ex - 0.006) / 0.026) ** 2 + ((z - ez + 0.028) / 0.019) ** 2)
    blush = smoothstep(1.0, 0.0, d) ** 1.6 * smoothstep(ey - 0.05, ey - 0.03, y)
    color = mix(color, color * np.array((1.03, 0.84, 0.84)), 0.6 * blush)
    color = mix(color, color * np.array((0.86, 0.74, 0.78)), 0.55 * lid)
    return color


def freckle_layer(color, P, eye):
    x, y, z = P[..., 0], P[..., 1], P[..., 2]
    u = np.abs(x)
    _ex, ey, ez = eye
    d = np.sqrt(((u - 0.012) / 0.040) ** 2 + ((z - ez + 0.025) / 0.022) ** 2)
    region = smoothstep(1.0, 0.3, d) * smoothstep(ey - 0.04, ey - 0.02, y)
    dots = speckles(P, 520.0, 0.65, seed=31) + 0.7 * speckles(P, 760.0, 0.5, seed=37)
    color = mix(color, color * np.array((0.74, 0.58, 0.47)), 0.85 * np.clip(dots, 0, 1) * region)
    return color


FACE_STYLES = {
    ("male", "Stubble"): {"lip": (0.70, 0.47, 0.44)},
    ("male", "Scarred"): {"lip": (0.68, 0.46, 0.43)},
    ("female", "Blush"): {"lip": (0.80, 0.40, 0.44)},
    ("female", "Freckles"): {"lip": (0.76, 0.46, 0.45)},
}


def border_distance(P, border):
    """Distance of every texel position to the nearest sampled border point."""
    kd = KDTree(len(border))
    for i, b in enumerate(border):
        kd.insert(Vector(b), i)
    kd.balance()
    flat = P.reshape(-1, 3)
    out = np.empty(len(flat))
    for i, p in enumerate(flat):
        out[i] = kd.find(Vector(p))[2]
    return out.reshape(P.shape[:-1])


def paint_face(sex, variant, P, Nlo, AO, lm, part):
    style = FACE_STYLES[(sex, variant)]
    eye = np.asarray(lm["eye"]).reshape(-1, 3).mean(0)
    chin_z = lm["chin_bottom"][0][2]
    soft = smoothstep(chin_z - 0.05, chin_z - 0.01, P[..., 2])
    AO = AO + (1.0 - 0.6 * (1.0 - AO) - AO) * soft
    shade = shading(Nlo, Nlo, AO, 0.0)
    skin = skin_base(sex, P)
    plain = warm_shadows(skin * shade[..., None], shade)
    color = plain.copy()
    color = face_shading(color, P, lm, eye, sex, Nlo)
    color, lips = lip_layer(color, P, lm, sex, style["lip"])
    color, lid = eye_area_layer(color, P, part, lm["eyeball"], lm["eye_opening"], sex)
    color = mouth_layer(color, P, part, lm["mouth_front_y"])
    if variant == "Stubble":
        color = stubble_layer(color, P, lm, eye, 0.42, lips)
    elif variant == "Scarred":
        color = stubble_layer(color, P, lm, eye, 0.18, lips)
        color = scar_and_lines(color, P, lm, eye)
    elif variant == "Blush":
        color = blush_layer(color, P, eye, lid)
    elif variant == "Freckles":
        color = freckle_layer(color, P, eye)
    keep = smoothstep(0.004, 0.016, border_distance(P, lm["face_border"]))
    keep = np.maximum(keep, np.isin(part, (PARTS.index("socket"), PARTS.index("mouth"))).astype(float))
    color = mix(plain, color, keep)
    return np.clip(color, 0.0, 1.0)


EYE_IRIS = {
    ("male", "Teal"): (0.20, 0.40, 0.40), ("male", "Brown"): (0.33, 0.21, 0.12),
    ("female", "Teal"): (0.18, 0.43, 0.43), ("female", "Hazel"): (0.40, 0.32, 0.15),
}
EYE_VARIANTS = {"male": ("Teal", "Brown"), "female": ("Teal", "Hazel")}


def paint_eye(sex, variant, eye_radius, iris_radius=0.0056, pupil_radius=0.0019, size=512):
    """Eyeball texture in the polar layout of the eyeball UVs: the front pole in the middle,
    the angle from the pole growing outward (0.49 of the width at the back pole)."""
    v, u = (np.mgrid[0:size, 0:size] + 0.5) / size
    du, dv = u - 0.5, v - 0.5
    rho = np.sqrt(du ** 2 + dv ** 2)
    theta = np.clip(rho / 0.49, 0.0, 1.0) * math.pi
    ang = np.arctan2(dv, du)
    # Distance on the eyeball from the front pole (metres) and a point on it for noise.
    s = theta * eye_radius
    pt = np.stack([np.sin(theta) * np.cos(ang), np.cos(theta), np.sin(theta) * np.sin(ang)], -1) * eye_radius
    th_iris = math.asin(min(iris_radius / eye_radius, 1.0)) * eye_radius
    th_pupil = math.asin(pupil_radius / eye_radius) * eye_radius
    sclera = np.broadcast_to(np.array((0.84, 0.81, 0.77)), pt.shape).copy()
    sclera = mix(sclera, np.array((0.74, 0.62, 0.58)), 0.75 * smoothstep(th_iris * 1.3, th_iris * 2.6, s))
    veins = smoothstep(0.62, 0.70, fbm(pt * np.array((1.0, 0.3, 1.0)), 900.0, 2, seed=51)) * smoothstep(th_iris * 1.4, th_iris * 2.6, s)
    sclera = mix(sclera, np.array((0.78, 0.42, 0.40)), 0.35 * veins)
    iris_rgb = np.asarray(EYE_IRIS[(sex, variant)])
    fibres = 0.82 + 0.36 * value_noise(np.stack([np.cos(ang) * 45, np.sin(ang) * 45, s * 2500], -1), 1.0, seed=17)
    iris = iris_rgb * fibres[..., None]
    iris = mix(iris, np.minimum(iris * 1.35 + 0.03, 1.0), smoothstep(th_iris * 0.75, th_iris * 0.35, s) * 0.4)
    iris = mix(iris, iris * 0.40, smoothstep(th_iris * 0.80, th_iris, s))
    iris = mix(iris, np.array((0.02, 0.02, 0.03)), smoothstep(th_pupil * 1.08, th_pupil * 0.92, s))
    in_iris = smoothstep(th_iris * 1.03, th_iris * 0.97, s)
    return np.clip(mix(sclera, iris, in_iris), 0.0, 1.0)
