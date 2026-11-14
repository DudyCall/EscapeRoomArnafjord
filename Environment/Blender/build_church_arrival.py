"""
Close-up churchyard arrival assets & driving path for Intro_ChurchArrival.unity:
Builds on top of TunnelExit.fbx coordinates so TunnelExit.fbx can be reused as the full valley
backdrop while adding close-range detail around Árnafjarðar kirkja, its gently contoured churchyard
parking forecourt, dry-stone wall & gate, church porch/doors/mullioned windows/spire cross, and
Stórá village bridge parapets.

Run: blender --background --python build_church_arrival.py -- <Assets/Environment/ChurchArrival dir> [--no-render]
"""
import bpy
import json
import math
import os
import sys
import numpy as np
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
ARGV = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
OUT = os.path.abspath(next((a for a in ARGV if not a.startswith("--")), os.path.join(HERE, "out_church")))
os.makedirs(OUT, exist_ok=True)

PORTAL_ASL = 89.2
SEA = -PORTAL_ASL
X_MIN, X_MAX = -650.0, 2050.0
Z_MIN, Z_MAX = -550.0, 2050.0

bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene


def U(x, y, z):
    """Unity-style (x right, y up, z forward) -> Blender."""
    return Vector((-x, -z, y))


def sstep_scalar(a, b, x):
    t = min(max((x - a) / max(b - a, 1e-8), 0.0), 1.0)
    return t * t * (3.0 - 2.0 * t)


def sstep(a, b, x):
    t = np.clip((x - a) / max(b - a, 1e-8), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def fractal(n, beta, seed):
    r = np.random.default_rng(seed)
    F = np.fft.fft2(r.standard_normal((n, n)))
    fx = np.fft.fftfreq(n)[:, None]
    fy = np.fft.fftfreq(n)[None, :]
    f = np.sqrt(fx * fx + fy * fy)
    f[0, 0] = 1.0
    F /= f ** beta
    F[0, 0] = 0.0
    out = np.real(np.fft.ifft2(F))
    return (out - out.min()) / max(out.max() - out.min(), 1e-8)


# ------------------------------------------------------------------ real geo & OSM (identical to build_tunnel_exit.py)
geo = np.load(os.path.join(HERE, "arnafjordur_geo.npz"))
gx0, gx1 = float(geo["gx0"]), float(geo["gx1"])
gz0, gz1 = float(geo["gz0"]), float(geo["gz1"])
raw_elev = geo["elev"].astype(np.float64) - PORTAL_ASL
raw_sea = geo["is_sea"].astype(np.float64)
nb = 0.25 * (
    np.roll(raw_elev, 1, axis=0) + np.roll(raw_elev, -1, axis=0)
    + np.roll(raw_elev, 1, axis=1) + np.roll(raw_elev, -1, axis=1)
)
raw_elev = np.where(np.abs(raw_elev - nb) > 28.0, nb, raw_elev)

with open(os.path.join(HERE, "arnafjordur_osm.json"), "r", encoding="utf-8") as fh:
    osm = json.load(fh)


def sample_dem(grid, x, z):
    fx = np.clip((x - gx0) / (gx1 - gx0) * (grid.shape[1] - 1), 0.0, grid.shape[1] - 1.0001)
    fz = np.clip((z - gz0) / (gz1 - gz0) * (grid.shape[0] - 1), 0.0, grid.shape[0] - 1.0001)
    x0 = np.floor(fx).astype(np.int32)
    z0 = np.floor(fz).astype(np.int32)
    tx = fx - x0
    tz = fz - z0
    return (
        grid[z0, x0] * (1 - tx) * (1 - tz)
        + grid[z0, x0 + 1] * tx * (1 - tz)
        + grid[z0 + 1, x0] * (1 - tx) * tz
        + grid[z0 + 1, x0 + 1] * tx * tz
    )


def dem_y(x, z):
    return float(sample_dem(raw_elev, float(x), float(z)))


def resample_polyline(pts, step=2.0):
    out = []
    if len(pts) < 2:
        return out
    acc = 0.0
    for i in range(len(pts) - 1):
        p0, p1 = pts[i], pts[i + 1]
        dx = p1[0] - p0[0]
        dz = p1[1] - p0[1]
        seg_len = math.hypot(dx, dz)
        if seg_len < 1e-4:
            continue
        n = max(1, int(math.ceil(seg_len / step)))
        for k in range(n):
            t = k / n
            x = p0[0] + dx * t
            z = p0[1] + dz * t
            y = p0[2] + (p1[2] - p0[2]) * t if len(p0) > 2 else dem_y(x, z)
            if out:
                acc += math.hypot(x - out[-1][0], z - out[-1][2])
            out.append((x, y, z, acc))
    lp = pts[-1]
    ly = lp[2] if len(lp) > 2 else dem_y(lp[0], lp[1])
    if out:
        acc += math.hypot(lp[0] - out[-1][0], lp[1] - out[-1][2])
    out.append((lp[0], ly, lp[1], acc))
    return out


# Reconstruct exact village height surface H around the lower switchback & church
N = 1024
ax_x = np.linspace(X_MIN, X_MAX, N)
ax_z = np.linspace(Z_MIN, Z_MAX, N)
X, Z = np.meshgrid(ax_x, ax_z)
nat = sample_dem(raw_elev, X, Z)
is_sea_grid = sample_dem(raw_sea, X, Z) > 0.5
nat = np.where(is_sea_grid, np.minimum(nat, SEA - 8.0), np.maximum(nat, SEA + 0.25))
n_fine = (fractal(N, 1.6, 51) - 0.5) * 3.2 + (fractal(N, 0.9, 52) - 0.5) * 1.1
road_focus_mask = sstep(35.0, 200.0, np.hypot(X, Z))
nat = np.where(is_sea_grid, nat, nat + n_fine * (0.25 + 0.75 * road_focus_mask))

vr0_rev = list(reversed(osm["village_roads"][0]))
vr0_pts = []
y_end_top = dem_y(221.25, 73.61) + 0.18
for idx, (vx, vz) in enumerate(vr0_rev):
    vy = max(SEA + 0.8, dem_y(vx, vz) + 0.22)
    if 0 < idx < 6:
        t = idx / 5.0
        vy = y_end_top * (1.0 - t) + vy * t
    vr0_pts.append((float(vx), float(vz), float(vy)))
VR0_RESAMPLED = resample_polyline(vr0_pts, step=3.0)

D_village_road = np.full_like(nat, 1e9)
RY_village_road = np.zeros_like(nat)
for x, y, z, _ in VR0_RESAMPLED[::2]:
    dd = np.hypot(X - x, Z - z)
    m = dd < D_village_road
    D_village_road = np.where(m, dd, D_village_road)
    RY_village_road = np.where(m, y, RY_village_road)
for vr in osm["village_roads"][1:]:
    for x, y, z, _ in resample_polyline([(float(px), float(pz), max(SEA + 0.75, dem_y(px, pz) + 0.20)) for px, pz in vr], step=3.0)[::2]:
        dd = np.hypot(X - x, Z - z)
        m = dd < D_village_road
        D_village_road = np.where(m, dd, D_village_road)
        RY_village_road = np.where(m, y, RY_village_road)

blend_vr = sstep(2.4, 10.0, D_village_road)
H = (RY_village_road - 0.12) * (1.0 - blend_vr) + nat * blend_vr


def terrain_y(x, z):
    fx = (x - X_MIN) / (X_MAX - X_MIN) * (N - 1)
    fz = (z - Z_MIN) / (Z_MAX - Z_MIN) * (N - 1)
    x0 = int(min(max(math.floor(fx), 0), N - 2))
    z0 = int(min(max(math.floor(fz), 0), N - 2))
    tx, tz = fx - x0, fz - z0
    return float(
        H[z0, x0] * (1.0 - tx) * (1.0 - tz)
        + H[z0, x0 + 1] * tx * (1.0 - tz)
        + H[z0 + 1, x0] * (1.0 - tx) * tz
        + H[z0 + 1, x0 + 1] * tx * tz
    )


# ------------------------------------------------------------------ materials
MATS = {}


def mk_mat(name, col, rough=0.8):
    m = bpy.data.materials.new(name)
    bsdf = m.node_tree.nodes.get("Principled BSDF") if m.node_tree else None
    if bsdf is None:
        m.use_nodes = True
        bsdf = m.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = (*col, 1.0)
    bsdf.inputs["Roughness"].default_value = rough
    MATS[name] = m


mk_mat("TunnelRoad", (0.16, 0.17, 0.19), 0.72)
mk_mat("ChurchGravel", (0.28, 0.27, 0.25), 0.90)
mk_mat("DryStone", (0.24, 0.24, 0.23), 0.92)
mk_mat("PortalConcrete", (0.48, 0.48, 0.47), 0.85)
mk_mat("GuardrailMetal", (0.65, 0.68, 0.70), 0.38)
mk_mat("HouseWhite", (0.86, 0.86, 0.84), 0.72)
mk_mat("HouseBlack", (0.11, 0.12, 0.13), 0.78)
mk_mat("RoofRed", (0.58, 0.14, 0.12), 0.65)
mk_mat("ChurchDoor", (0.14, 0.26, 0.18), 0.55)
mk_mat("HouseWindow", (0.10, 0.15, 0.21), 0.16)
mk_mat("ChurchLawn", (0.21, 0.29, 0.12), 0.90)


class MB:
    def __init__(self):
        self.v, self.uv, self.f, self.m = [], [], [], []

    def add(self, verts, faces, mat, want=None, uvs=None):
        base = len(self.v)
        for f in faces:
            f = list(f)
            if want is not None:
                c = sum((Vector(verts[i]) for i in f), Vector((0, 0, 0))) / len(f)
                a, b, cc = (U(*verts[f[0]]), U(*verts[f[1]]), U(*verts[f[2]]))
                n = (b - a).cross(cc - a)
                w = want(c)
                if n.dot(U(*w)) < 0:
                    f.reverse()
            self.f.append([base + i for i in f])
            self.m.append(mat)
        self.v += [U(*p) for p in verts]
        self.uv += list(uvs) if uvs is not None else [(0.0, 0.0)] * len(verts)

    def to_object(self, name, angle=40.0):
        me = bpy.data.meshes.new(name)
        me.from_pydata([tuple(v) for v in self.v], [], self.f)
        names = []
        for n in self.m:
            if n not in names:
                names.append(n)
        for n in names:
            me.materials.append(MATS[n])
        me.polygons.foreach_set("material_index", [names.index(n) for n in self.m])
        me.polygons.foreach_set("use_smooth", [True] * len(self.f))
        uvl = me.uv_layers.new(name="UVMap")
        for lp in me.loops:
            uvl.data[lp.index].uv = self.uv[lp.vertex_index]
        me.update()
        try:
            me.set_sharp_from_angle(angle=math.radians(angle))
        except Exception:
            pass
        ob = bpy.data.objects.new(name, me)
        scene.collection.objects.link(ob)
        print(name, "tris:", sum(len(f) - 2 for f in self.f))
        return ob


UP = lambda c: (0, 1, 0)


def add_box(mb, corners, mat, center):
    out = lambda q, c=center: tuple(q - c)
    mb.add(corners, [
        [0, 1, 2, 3], [4, 5, 6, 7],
        [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7],
    ], mat, out)


# ------------------------------------------------------------------ Church & Parking Bay Coordinates
ch = [b for b in osm["buildings"] if b["kind"] == "church"][0]
CH_X, CH_Z, CH_YAW = ch["x"], ch["z"], ch["yaw"]  # 387.03, 268.97, 0.7916
CH_W, CH_D = max(ch["w"], 12.6), max(ch["d"], 8.4)
ch_c, ch_s = math.cos(CH_YAW), math.sin(CH_YAW)


def CHL(lx, ly, lz):
    return (CH_X + lx * ch_c + lz * ch_s, CH_Y0 + ly, CH_Z - lx * ch_s + lz * ch_c)


# Match TunnelExit.fbx house() y0 using H (via terrain_y) so close-up church trim & plinth align tightly!
c_ys = [terrain_y(CH_X + sx * CH_W * 0.5 * ch_c + sz * CH_D * 0.5 * ch_s,
                  CH_Z - sx * CH_W * 0.5 * ch_s + sz * CH_D * 0.5 * ch_c)
        for sx in (-1, 1) for sz in (-1, 1)]
CH_Y0 = 0.65 * sum(c_ys) * 0.25 + 0.35 * max(c_ys)
CH_H = 4.6
CH_ROOF_H = 3.1


# ------------------------------------------------------------------ 1. Driving Path (vr0 switchback descent -> Church Parking Bay)
drive_raw = []
for idx in range(38, 57):  # down to [437.79, 271.25]
    vx, vz = vr0_rev[idx]
    vy = max(SEA + 0.8, dem_y(vx, vz) + 0.22)
    drive_raw.append((float(vx), float(vz), float(vy)))

# Smooth gentle turnoff starting from [417.24, 280.26] -> parking bay (398.4, 280.2) beside the churchyard gate.
# Keeping the bay closer to the road contour (y_park ~ -80.45m) avoids a steep wedge cliff on the downhill edge!
p0 = (417.24, 280.26)
y_turn = dem_y(*p0) + 0.22  # ~ -81.99
park_xy = (398.4, 280.2)
y_park = max(terrain_y(*park_xy) + 0.18, -80.55)

p1 = (411.5, 282.5)
p2 = (404.8, 282.2)
p3 = park_xy
# Include [417.24, 280.26] seamlessly first
drive_raw.append((p0[0], p0[1], y_turn))
for i in range(1, 22):
    t = i / 21.0
    u = 1.0 - t
    bx = u**3 * p0[0] + 3 * u**2 * t * p1[0] + 3 * u * t**2 * p2[0] + t**3 * p3[0]
    bz = u**3 * p0[1] + 3 * u**2 * t * p1[1] + 3 * u * t**2 * p2[1] + t**3 * p3[1]
    ht = sstep_scalar(0.0, 1.0, t)
    by = y_turn * (1.0 - ht) + y_park * ht
    if t > 0.22:
        by = max(by, terrain_y(bx, bz) + 0.12)
    drive_raw.append((bx, bz, by))

# Update y_park to match final endpoint height accurately
y_park = drive_raw[-1][2]

# Add 4.2m straight tangent tail beyond park stop point so Sample(stop + 2.6) keeps exact forward heading
dx_tail = p3[0] - p2[0]
dz_tail = p3[1] - p2[1]
ln_tail = math.hypot(dx_tail, dz_tail)
tx_tail, tz_tail = dx_tail / ln_tail, dz_tail / ln_tail
drive_raw.append((p3[0] + tx_tail * 4.2, p3[1] + tz_tail * 4.2, y_park))

PATH = resample_polyline(drive_raw, step=1.2)
STOP_DISTANCE = PATH[-1][3] - 4.2
print(f"Church arrival path: len={PATH[-1][3]:.1f}m, stopDistance={STOP_DISTANCE:.1f}m, park=({p3[0]:.2f}, {y_park:.2f}, {p3[1]:.2f}), CH_Y0={CH_Y0:.2f}")

with open(os.path.join(OUT, "ChurchArrivalPath.json"), "w", encoding="utf-8") as fh:
    json.dump({
        "points": [{"x": round(x, 3), "y": round(y, 3), "z": round(z, 3)} for x, y, z, _ in PATH],
        "stopDistance": round(STOP_DISTANCE, 3),
        "parkPosition": {"x": round(p3[0], 3), "y": round(y_park, 3), "z": round(p3[1], 3)},
        "churchPosition": {"x": round(CH_X, 2), "y": round(CH_Y0, 2), "z": round(CH_Z, 2)},
    }, fh, indent=2)


# ------------------------------------------------------------------ 2. Smooth Contoured Forecourt & Sloped Turf Skirt
forecourt = MB()

# Build a smooth continuous driveway + widened parking bay ribbon with sloped gravel/turf batters down into H!
turn_pts = [p for p in PATH if STOP_DISTANCE - 28.0 <= p[3] <= STOP_DISTANCE + 2.5]
if len(turn_pts) >= 2:
    n_t = len(turn_pts)
    grid_rows = []  # 4 vertices across per station: outer_L_toe, edge_L, edge_R, outer_R_toe
    for k, (x, y, z, d) in enumerate(turn_pts):
        a = turn_pts[max(k - 1, 0)]
        b = turn_pts[min(k + 1, n_t - 1)]
        tx, tz = b[0] - a[0], b[2] - a[2]
        ln = max(1e-5, math.hypot(tx, tz))
        nx, nz = tz / ln, -tx / ln
        frac = k / max(1, n_t - 1)
        # Flare cleanly from road half-width 1.95m to cozy ~3.4m church parking bay, then taper gently around nose
        hw = 1.95 + 1.45 * sstep_scalar(0.20, 0.78, frac)
        if frac > 0.90:
            hw *= (1.0 - 0.15 * sstep_scalar(0.90, 1.0, frac))
        shoulder = hw + 0.9
        lx_toe, lz_toe = x - nx * shoulder, z - nz * shoulder
        rx_toe, rz_toe = x + nx * shoulder, z + nz * shoulder
        y_surf = y + 0.008 * sstep_scalar(0.08, 0.30, frac)
        y_l_toe = min(y_surf - 0.12, terrain_y(lx_toe, lz_toe) - 0.30)
        y_r_toe = min(y_surf - 0.12, terrain_y(rx_toe, rz_toe) - 0.30)
        grid_rows.append([
            (lx_toe, y_l_toe, lz_toe),
            (x - nx * hw, y_surf, z - nz * hw),
            (x + nx * hw, y_surf, z + nz * hw),
            (rx_toe, y_r_toe, rz_toe),
        ])
    fv, ff, fm, fuv = [], [], [], []
    # Flatten rows into MB carefully per material
    # Inner asphalt/gravel driving & parking surface (cols 1..2) + narrow beveled gravel shoulder onto Exit_Terrain
    rv, rf, ruv = [], [], []
    sv_l, sf_l, sv_r, sf_r = [], [], [], []
    for k, row in enumerate(grid_rows):
        d = turn_pts[k][3]
        rv += [row[1], row[2]]
        ruv += [(0.0, d / 4.0), (1.0, d / 4.0)]
        sv_l += [row[0], row[1]]
        sv_r += [row[2], row[3]]
    for k in range(n_t - 1):
        rf.append([2 * k, 2 * k + 1, 2 * k + 3, 2 * k + 2])
        sf_l.append([2 * k, 2 * k + 1, 2 * k + 3, 2 * k + 2])
        sf_r.append([2 * k, 2 * k + 1, 2 * k + 3, 2 * k + 2])
    forecourt.add(rv, rf, "ChurchGravel", UP, ruv)
    forecourt.add(sv_l, sf_l, "DryStone", UP)
    forecourt.add(sv_r, sf_r, "DryStone", UP)

forecourt.to_object("ChurchForecourt")


# ------------------------------------------------------------------ 3. Close-up Details on Árnafjarðar kirkja
church = MB()

# Subtle basalt stone foundation plinth hugging CH_Y0 - 2.2 to CH_Y0 + 0.32 so it seats cleanly into Exit_Terrain
p_pad = 0.06
pcorners = [
    CHL(-CH_W * 0.5 - p_pad, -2.2, -CH_D * 0.5 - p_pad),
    CHL(CH_W * 0.5 + p_pad, -2.2, -CH_D * 0.5 - p_pad),
    CHL(CH_W * 0.5 + p_pad, -2.2, CH_D * 0.5 + p_pad),
    CHL(-CH_W * 0.5 - p_pad, -2.2, CH_D * 0.5 + p_pad),
    CHL(-CH_W * 0.5 - p_pad, 0.32, -CH_D * 0.5 - p_pad),
    CHL(CH_W * 0.5 + p_pad, 0.32, -CH_D * 0.5 - p_pad),
    CHL(CH_W * 0.5 + p_pad, 0.32, CH_D * 0.5 + p_pad),
    CHL(-CH_W * 0.5 - p_pad, 0.32, CH_D * 0.5 + p_pad),
]
add_box(church, pcorners, "DryStone", Vector(CHL(0, -0.3, 0)))

# Traditional painted entrance vestibule / porch on West-Southwest gable (lx = -CH_W*0.5) & painted green double door
pw, pd, ph = 2.1, 2.8, 3.0
porch_c = Vector(CHL(-CH_W * 0.5 - pw * 0.45, ph * 0.5, 0.0))
porch_v = [
    CHL(-CH_W * 0.5 - pw, -1.1, -pd * 0.5), CHL(-CH_W * 0.5 + 0.1, -1.1, -pd * 0.5),
    CHL(-CH_W * 0.5 + 0.1, -1.1, pd * 0.5), CHL(-CH_W * 0.5 - pw, -1.1, pd * 0.5),
    CHL(-CH_W * 0.5 - pw, ph, -pd * 0.5), CHL(-CH_W * 0.5 + 0.1, ph, -pd * 0.5),
    CHL(-CH_W * 0.5 + 0.1, ph, pd * 0.5), CHL(-CH_W * 0.5 - pw, ph, pd * 0.5),
]
add_box(church, porch_v, "HouseWhite", porch_c)
# Matching stone base on porch
add_box(church, [
    CHL(-CH_W * 0.5 - pw - 0.04, -1.1, -pd * 0.5 - 0.04), CHL(-CH_W * 0.5 + 0.05, -1.1, -pd * 0.5 - 0.04),
    CHL(-CH_W * 0.5 + 0.05, -1.1, pd * 0.5 + 0.04), CHL(-CH_W * 0.5 - pw - 0.04, -1.1, pd * 0.5 + 0.04),
    CHL(-CH_W * 0.5 - pw - 0.04, 0.25, -pd * 0.5 - 0.04), CHL(-CH_W * 0.5 + 0.05, 0.25, -pd * 0.5 - 0.04),
    CHL(-CH_W * 0.5 + 0.05, 0.25, pd * 0.5 + 0.04), CHL(-CH_W * 0.5 - pw - 0.04, 0.25, pd * 0.5 + 0.04),
], "DryStone", porch_c)
# Pitched red roof over porch
pr_o = 0.22
pr_v = [
    CHL(-CH_W * 0.5 - pw - pr_o, ph - 0.06, -pd * 0.5 - pr_o),
    CHL(-CH_W * 0.5 + 0.1, ph - 0.06, -pd * 0.5 - pr_o),
    CHL(-CH_W * 0.5 + 0.1, ph - 0.06, pd * 0.5 + pr_o),
    CHL(-CH_W * 0.5 - pw - pr_o, ph - 0.06, pd * 0.5 + pr_o),
    CHL(-CH_W * 0.5 - pw - pr_o, ph + 1.05, 0.0),
    CHL(-CH_W * 0.5 + 0.1, ph + 1.05, 0.0),
]
church.add(pr_v, [[0, 1, 5, 4], [3, 2, 5, 4]], "RoofRed", lambda q, c=porch_c: (q[0] - c[0], 3.0, q[2] - c[2]))
church.add([
    CHL(-CH_W * 0.5 - pw, ph, -pd * 0.5), CHL(-CH_W * 0.5 - pw, ph, pd * 0.5),
    CHL(-CH_W * 0.5 - pw, ph + 1.02, 0.0),
], [[0, 1, 2]], "HouseWhite", lambda q, c=porch_c: tuple(q - c))

# Heritage dark-green panelled wooden church door on West porch gable
dx_door = -CH_W * 0.5 - pw - 0.04
dw, dh = 1.35, 2.25
door_v = [
    CHL(dx_door, 0.22, -dw * 0.5), CHL(dx_door, 0.22, dw * 0.5),
    CHL(dx_door, 0.22 + dh, dw * 0.5), CHL(dx_door, 0.22 + dh, -dw * 0.5),
]
church.add(door_v, [[0, 1, 2, 3]], "ChurchDoor", lambda q, c=porch_c: (-ch_c, 0, ch_s))

# Stone entrance steps descending cleanly from porch door
for si in range(4):
    sx0 = -CH_W * 0.5 - pw - 0.55 - si * 0.42
    sx1 = sx0 + 0.48
    sy_top = 0.22 - si * 0.34
    sv = [
        CHL(sx0, sy_top - 0.45, -1.25), CHL(sx1, sy_top - 0.45, -1.25),
        CHL(sx1, sy_top - 0.45, 1.25), CHL(sx0, sy_top - 0.45, 1.25),
        CHL(sx0, sy_top, -1.25), CHL(sx1, sy_top, -1.25),
        CHL(sx1, sy_top, 1.25), CHL(sx0, sy_top, 1.25),
    ]
    add_box(church, sv, "PortalConcrete", Vector(CHL(0.5 * (sx0 + sx1), sy_top - 0.2, 0)))

# Symmetric white timber window sills & cross-mullions aligned cleanly with TunnelExit's fixed church windows
for side in (-1, 1):
    lz_f = side * (CH_D * 0.5 + 0.07)
    for wi in range(4):
        lx = (CH_W * 0.68) * ((wi + 0.5) / 4 - 0.5)
        ww, wh, wy0 = 0.86, 1.72, 1.02
        wc = Vector(CHL(lx, wy0 + wh * 0.5, side * CH_D * 0.4))
        bars = [
            (lx - ww * 0.52, lx + ww * 0.52, wy0 - 0.08, wy0 + 0.04),
            (lx - ww * 0.52, lx + ww * 0.52, wy0 + wh - 0.04, wy0 + wh + 0.06),
            (lx - ww * 0.04, lx + ww * 0.04, wy0, wy0 + wh),
            (lx - ww * 0.46, lx + ww * 0.46, wy0 + wh * 0.52, wy0 + wh * 0.57),
        ]
        for bx0, bx1, by0, by1 in bars:
            bv = [
                CHL(bx0, by0, lz_f - 0.03), CHL(bx1, by0, lz_f - 0.03),
                CHL(bx1, by0, lz_f + 0.04), CHL(bx0, by0, lz_f + 0.04),
                CHL(bx0, by1, lz_f - 0.03), CHL(bx1, by1, lz_f - 0.03),
                CHL(bx1, by1, lz_f + 0.04), CHL(bx0, by1, lz_f + 0.04),
            ]
            add_box(church, bv, "HouseWhite", wc)

# White timber cross crowning Árnafjarðar kirkja spire
tx0 = -CH_W * 0.33
sh_spire = CH_H + CH_ROOF_H + 3.6 + 2.8
cross_c = Vector(CHL(tx0, sh_spire + 0.5, 0))
add_box(church, [
    CHL(tx0 - 0.05, sh_spire - 0.15, -0.05), CHL(tx0 + 0.05, sh_spire - 0.15, -0.05),
    CHL(tx0 + 0.05, sh_spire - 0.15, 0.05), CHL(tx0 - 0.05, sh_spire - 0.15, 0.05),
    CHL(tx0 - 0.04, sh_spire + 1.15, -0.04), CHL(tx0 + 0.04, sh_spire + 1.15, -0.04),
    CHL(tx0 + 0.04, sh_spire + 1.15, 0.04), CHL(tx0 - 0.04, sh_spire + 1.15, 0.04),
], "HouseWhite", cross_c)
add_box(church, [
    CHL(tx0 - 0.04, sh_spire + 0.72, -0.36), CHL(tx0 + 0.04, sh_spire + 0.72, -0.36),
    CHL(tx0 + 0.04, sh_spire + 0.72, 0.36), CHL(tx0 - 0.04, sh_spire + 0.72, 0.36),
    CHL(tx0 - 0.04, sh_spire + 0.80, -0.36), CHL(tx0 + 0.04, sh_spire + 0.80, -0.36),
    CHL(tx0 + 0.04, sh_spire + 0.80, 0.36), CHL(tx0 - 0.04, sh_spire + 0.80, 0.36),
], "HouseWhite", cross_c)

# Dark eaves / gutter trim along nave roof edges
for side in (-1, 1):
    z_e = side * (CH_D * 0.5 + 0.30)
    gc = Vector(CHL(0, CH_H - 0.1, z_e))
    add_box(church, [
        CHL(-CH_W * 0.5 - 0.32, CH_H - 0.22, z_e - 0.06), CHL(CH_W * 0.5 + 0.32, CH_H - 0.22, z_e - 0.06),
        CHL(CH_W * 0.5 + 0.32, CH_H - 0.22, z_e + 0.06), CHL(-CH_W * 0.5 - 0.32, CH_H - 0.22, z_e + 0.06),
        CHL(-CH_W * 0.5 - 0.32, CH_H - 0.04, z_e - 0.06), CHL(CH_W * 0.5 + 0.32, CH_H - 0.04, z_e - 0.06),
        CHL(CH_W * 0.5 + 0.32, CH_H - 0.04, z_e + 0.06), CHL(-CH_W * 0.5 - 0.32, CH_H - 0.04, z_e + 0.06),
    ], "HouseBlack", gc)

church.to_object("ChurchDetail")


# ------------------------------------------------------------------ 4. Dry-stone Churchyard Wall, Gate, Stórá Bridge Parapets & Marker Posts
props = MB()


def add_wall_segment(p0, p1, half_t=0.25, top_h=0.82, bot_d=0.9):
    dx, dz = p1[0] - p0[0], p1[1] - p0[1]
    ln = max(1e-4, math.hypot(dx, dz))
    nx, nz = -dz / ln, dx / ln
    y0_p = terrain_y(p0[0], p0[1])
    y1_p = terrain_y(p1[0], p1[1])
    y0_top = y0_p + top_h
    y1_top = y1_p + top_h
    cen = Vector((0.5 * (p0[0] + p1[0]), 0.5 * (y0_p + y1_p), 0.5 * (p0[1] + p1[1])))
    wv = [
        (p0[0] - nx * half_t, y0_p - bot_d, p0[1] - nz * half_t),
        (p1[0] - nx * half_t, y1_p - bot_d, p1[1] - nz * half_t),
        (p1[0] + nx * half_t, y1_p - bot_d, p1[1] + nz * half_t),
        (p0[0] + nx * half_t, y0_p - bot_d, p0[1] + nz * half_t),
        (p0[0] - nx * half_t * 0.88, y0_top, p0[1] - nz * half_t * 0.88),
        (p1[0] - nx * half_t * 0.88, y1_top, p1[1] - nz * half_t * 0.88),
        (p1[0] + nx * half_t * 0.88, y1_top, p1[1] + nz * half_t * 0.88),
        (p0[0] + nx * half_t * 0.88, y0_top, p0[1] + nz * half_t * 0.88),
    ]
    add_box(props, wv, "DryStone", cen)


def add_pillar(px, pz, py_base, w=0.56, h=1.18):
    cen = Vector((px, py_base + h * 0.5, pz))
    hw = w * 0.5
    pv = [
        (px - hw, py_base - 0.8, pz - hw), (px + hw, py_base - 0.8, pz - hw),
        (px + hw, py_base - 0.8, pz + hw), (px - hw, py_base - 0.8, pz + hw),
        (px - hw, py_base + h, pz - hw), (px + hw, py_base + h, pz - hw),
        (px + hw, py_base + h, pz + hw), (px - hw, py_base + h, pz + hw),
    ]
    add_box(props, pv, "DryStone", cen)


w_sw_c = (CHL(-CH_W * 0.68, 0, CH_D * 0.72)[0], CHL(-CH_W * 0.68, 0, CH_D * 0.72)[2])
w_se_c = (CHL(CH_W * 0.64, 0, CH_D * 0.72)[0], CHL(CH_W * 0.64, 0, CH_D * 0.72)[2])
w_ne_c = (CHL(CH_W * 0.64, 0, -CH_D * 0.68)[0], CHL(CH_W * 0.64, 0, -CH_D * 0.68)[2])
g_t1, g_t2 = 0.24, 0.42
g_p1 = (w_sw_c[0] * (1 - g_t1) + w_se_c[0] * g_t1, w_sw_c[1] * (1 - g_t1) + w_se_c[1] * g_t1)
g_p2 = (w_sw_c[0] * (1 - g_t2) + w_se_c[0] * g_t2, w_sw_c[1] * (1 - g_t2) + w_se_c[1] * g_t2)

add_wall_segment(w_sw_c, g_p1)
add_wall_segment(g_p2, w_se_c)
add_wall_segment(w_se_c, w_ne_c)
for gx, gz in (g_p1, g_p2):
    add_pillar(gx, gz, terrain_y(gx, gz))

# Stórá concrete village bridge parapets at vr[1]
br0, br1 = (398.40, 303.40), (396.60, 310.20)
bdx, bdz = br1[0] - br0[0], br1[1] - br0[1]
bln = math.hypot(bdx, bdz)
bnx, bnz = -bdz / bln, bdx / bln
y_br0 = max(SEA + 0.75, dem_y(*br0) + 0.20)
y_br1 = max(SEA + 0.75, dem_y(*br1) + 0.20)
for side in (-1.0, 1.0):
    off = side * 2.15
    p0x, p0z = br0[0] + bnx * off, br0[1] + bnz * off
    p1x, p1z = br1[0] + bnx * off, br1[1] + bnz * off
    bcen = Vector((0.5 * (p0x + p1x), 0.5 * (y_br0 + y_br1) + 0.4, 0.5 * (p0z + p1z)))
    ht = 0.20
    bv = [
        (p0x - bnx * ht, y_br0 - 1.5, p0z - bnz * ht), (p1x - bnx * ht, y_br1 - 1.5, p1z - bnz * ht),
        (p1x + bnx * ht, y_br1 - 1.5, p1z + bnz * ht), (p0x + bnx * ht, y_br0 - 1.5, p0z + bnz * ht),
        (p0x - bnx * ht, y_br0 + 0.92, p0z - bnz * ht), (p1x - bnx * ht, y_br1 + 0.92, p1z - bnz * ht),
        (p1x + bnx * ht, y_br1 + 0.92, p1z + bnz * ht), (p0x + bnx * ht, y_br0 + 0.92, p0z + bnz * ht),
    ]
    add_box(props, bv, "PortalConcrete", bcen)

# Faroese roadside guide bollards every ~16m along the switchback descent
last_post_d = -100.0
for x, y, z, d in PATH:
    if d > STOP_DISTANCE - 35.0:
        break
    if d - last_post_d >= 16.0:
        last_post_d = d
        idx_c = min(len(PATH) - 2, max(0, int(d / 1.2)))
        tx, tz = PATH[idx_c + 1][0] - PATH[idx_c][0], PATH[idx_c + 1][2] - PATH[idx_c][2]
        ln = max(1e-4, math.hypot(tx, tz))
        nx, nz = -tz / ln, tx / ln
        px, pz = x + nx * 2.15, z + nz * 2.15
        py = y
        pcen = Vector((px, py + 0.4, pz))
        pw = 0.055
        add_box(props, [
            (px - pw, py - 0.5, pz - pw), (px + pw, py - 0.5, pz - pw),
            (px + pw, py - 0.5, pz + pw), (px - pw, py - 0.5, pz + pw),
            (px - pw, py + 0.82, pz - pw), (px + pw, py + 0.82, pz - pw),
            (px + pw, py + 0.82, pz + pw), (px - pw, py + 0.82, pz + pw),
        ], "HouseWhite", pcen)

props.to_object("ChurchyardWallAndProps")


# ------------------------------------------------------------------ export FBX
fbx_path = os.path.join(OUT, "ChurchArea.fbx")
bpy.ops.export_scene.fbx(
    filepath=fbx_path,
    use_selection=False,
    object_types={'MESH'},
    mesh_smooth_type='FACE',
    use_mesh_modifiers=True,
    apply_scale_options='FBX_SCALE_ALL',
)
print("Exported:", fbx_path)
