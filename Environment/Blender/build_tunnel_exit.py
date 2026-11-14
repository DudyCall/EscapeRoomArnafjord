"""
Outside the Árnafjørður end of the old tunnel (Árnafjarðartunnilin): rock-cut concrete portal,
T-junction with the left road crossing Stórá into Hvannasundstunnilin (toward Depil), and
Árnfjarðarvegur turning right down the mountainside towards Árnafjørður village, church, and fjord.

Built from real OpenStreetMap + Mapzen Terrarium DEM data (arnafjordur_geo.npz, arnafjordur_osm.json).

Run:  blender --background --python build_tunnel_exit.py -- <unity Assets/Environment/TunnelExit dir> [--no-render]

All geometry is authored in Unity-style coordinates, portal-local:
  x = right (SSE), y = up, z = out of the tunnel (ENE). The portal mouth is the origin (road centre, z = 0).
"""
import bpy
import json
import math
import os
import shutil
import sys
import numpy as np
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
ARGV = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
OUT = os.path.abspath(next((a for a in ARGV if not a.startswith("--")), os.path.join(HERE, "out_exit")))
DO_RENDER = "--no-render" not in ARGV
TEX = os.path.join(OUT, "Textures")
os.makedirs(TEX, exist_ok=True)

PORTAL_ASL = 89.2
SEA = -PORTAL_ASL
ROAD_HALF = 1.75
X_MIN, X_MAX = -650.0, 2050.0
Z_MIN, Z_MAX = -550.0, 2050.0

bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
rng = np.random.default_rng(17)


def U(x, y, z):
    """Unity-style (x right, y up, z forward) -> Blender."""
    return Vector((-x, -z, y))


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


def sstep(a, b, x):
    t = np.clip((x - a) / max(b - a, 1e-8), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def sstep_scalar(a, b, x):
    t = min(max((x - a) / max(b - a, 1e-8), 0.0), 1.0)
    return t * t * (3.0 - 2.0 * t)


# ------------------------------------------------------------------ real geo & OSM
geo = np.load(os.path.join(HERE, "arnafjordur_geo.npz"))
gx0, gx1 = float(geo["gx0"]), float(geo["gx1"])
gz0, gz1 = float(geo["gz0"]), float(geo["gz1"])
raw_elev = geo["elev"].astype(np.float64) - PORTAL_ASL
raw_sea = geo["is_sea"].astype(np.float64)

# Despike any isolated SRTM glitch on distant mountain walls without softening real terrain
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


# ------------------------------------------------------------------ road geometry & driving path
def resample_polyline(pts, step=2.5, y_fn=None):
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
            y = p0[2] + (p1[2] - p0[2]) * t if len(p0) > 2 else (y_fn(x, z) if y_fn else 0.0)
            if out:
                acc += math.hypot(x - out[-1][0], z - out[-1][2])
            out.append((x, y, z, acc))
    lp = pts[-1]
    ly = lp[2] if len(lp) > 2 else (y_fn(lp[0], lp[1]) if y_fn else 0.0)
    if out:
        acc += math.hypot(lp[0] - out[-1][0], lp[1] - out[-1][2])
    out.append((lp[0], ly, lp[1], acc))
    return out


def build_main_drive_path():
    """Single-lane drive: straight out of Árnafjarðartunnilin -> right turn at T-junction onto Árnfjarðarvegur."""
    pts = []
    # Inside tunnel (-24..0) and straight exit to start of right-turn fillet (0..18)
    for z in range(-24, 19, 2):
        y = 0.0 if z <= 2 else -0.32 * sstep_scalar(2.0, 20.0, z)
        pts.append((0.0, float(z), y))
    # Smooth 90-deg right turn from (0, 18) -> apex (0.2, 28.65) -> (15.4, 28.8)
    R = 10.6
    cx, cz = R, 18.0
    for i in range(1, 14):
        a = (math.pi * 0.5) * i / 14.0
        px = cx - R * math.cos(a)
        pz = cz + R * math.sin(a)
        ang = i / 14.0
        y = -0.32 - 0.85 * ang
        pts.append((px, pz, y))
    # Continue along Árnfjarðarvegur mountainside ledge (+X) descending cleanly toward switchback
    # Real elevation drops from ~88.7m ASL (y=-0.5) at junction to ~53m ASL (y=-36) at x=308.6
    upper = osm["upper_arnfjardarvegur"][1:]  # starts at [15.39, 28.8]
    for idx, (ux, uz) in enumerate(upper):
        # Smooth engineering grade ~11.2% along the mountainside cut
        t_dist = max(0.0, ux - 10.6)
        uy = -1.17 - 0.112 * t_dist
        pts.append((float(ux), float(uz), float(uy)))

    # Interpolate finely (~1.2m steps) so IntroPathDrive car & wheel rotation are silky smooth
    out = []
    d = -24.0
    prev = None
    dense = resample_polyline(pts, step=1.2)
    for x, y, z, _ in dense:
        if prev is not None:
            d += math.hypot(x - prev[0], z - prev[1])
        prev = (x, z)
        out.append((x, y, z, d))
    return out


PATH = build_main_drive_path()
print("drive path len %.1f m, end (%.1f, %.1f, %.1f)" % (PATH[-1][3], PATH[-1][0], PATH[-1][1], PATH[-1][2]))


def build_depil_road():
    """Left fork from T-junction (0.2, 28.65) across Stórá concrete bridge into Hvannasundstunnilin (-87.6, 58.7)."""
    raw = osm["depil_road"]  # [(0.19, 28.65), ..., (-72.27, 44.17), (-87.58, 58.72)]
    # Start gently from (0.2, 28.65) curving left; rise smoothly from -0.55m at junction to +3.2m at Hvannasundstunnilin
    pts = []
    # Smooth approach arc tangent from straight road (0, 21) -> (-12, 29.2) then follow depil_road
    pts.append((0.0, 20.0, -0.36))
    pts.append((-0.5, 25.0, -0.46))
    pts.append((-3.2, 28.0, -0.54))
    pts.append((-12.0, 29.3, -0.25))
    total_pts = raw[1:]  # starting from [-33.16, 30.38]
    for ux, uz in total_pts:
        # Linear progression toward (-87.58, 58.72) where Hvannasundstunnilin sits at ~+3.2m above our portal
        frac = min(1.0, max(0.0, (-ux - 12.0) / (87.58 - 12.0)))
        uy = -0.25 + 3.45 * frac
        pts.append((float(ux), float(uz), float(uy)))
    # Extend 16m straight into Hvannasundstunnilin dark bore
    p_prev = pts[-2]
    p_end = pts[-1]
    dx = p_end[0] - p_prev[0]
    dz = p_end[1] - p_prev[1]
    ln = math.hypot(dx, dz)
    dx, dz = dx / ln, dz / ln
    pts.append((p_end[0] + dx * 16.0, p_end[1] + dz * 16.0, p_end[2]))
    return resample_polyline(pts, step=2.0), (p_end[0], p_end[2], p_end[1]), (-dx, -dz)


DEPIL_ROAD, HV_PORTAL_POS, HV_PORTAL_FWD = build_depil_road()


def build_switchback_and_village_roads():
    """Smooth hairpin from upper Árnfjarðarvegur down vroad 0..10 into Árnafjørður village & harbour."""
    roads = []
    # Smooth C1 Bezier hairpin from (295, 32.1) -> (308.6, 32.41) arcing out to (334, 48) -> (265, 65) -> (221.25, 73.61)
    y_start = -1.17 - 0.112 * (308.6 - 10.6)  # ~ -34.55
    y_end = dem_y(221.25, 73.61) + 0.18
    p0 = (308.6, 32.41)
    p1 = (352.0, 33.5)
    p2 = (348.0, 62.0)
    p3 = (221.25, 73.61)
    hairpin_pts = [(302.0, 32.3, y_start + 0.74)]
    n_arc = 18
    for i in range(n_arc + 1):
        t = i / n_arc
        u = 1.0 - t
        bx = u**3 * p0[0] + 3 * u**2 * t * p1[0] + 3 * u * t**2 * p2[0] + t**3 * p3[0]
        bz = u**3 * p0[1] + 3 * u**2 * t * p1[1] + 3 * u * t**2 * p2[1] + t**3 * p3[1]
        by = y_start * (1.0 - t) + y_end * t
        hairpin_pts.append((float(bx), float(bz), float(by)))

    # Note: vroad 0 in OSM runs from [398.68, 302.44] (bottom bridge) up to [221.25, 73.61]
    vr0 = list(reversed(osm["village_roads"][0]))
    for idx, (vx, vz) in enumerate(vr0[1:]):
        vy = max(SEA + 0.8, dem_y(vx, vz) + 0.22)
        if idx < 5:
            t = (idx + 1) / 5.0
            vy = y_end * (1.0 - t) + vy * t
        hairpin_pts.append((float(vx), float(vz), float(vy)))
    roads.append(("switchback", resample_polyline(hairpin_pts, step=3.0), 1.85))

    # Village streets (vroad 1..10)
    for ridx, vr in enumerate(osm["village_roads"][1:], start=1):
        vpts = []
        for vx, vz in vr:
            vy = max(SEA + 0.75, dem_y(vx, vz) + 0.20)
            vpts.append((float(vx), float(vz), float(vy)))
        roads.append((f"vroad_{ridx}", resample_polyline(vpts, step=3.0), 1.70))
    return roads


VILLAGE_ROADS = build_switchback_and_village_roads()


# ------------------------------------------------------------------ height grid (DEM + road carving + river gorge)
N = 1024
ax_x = np.linspace(X_MIN, X_MAX, N)
ax_z = np.linspace(Z_MIN, Z_MAX, N)
X, Z = np.meshgrid(ax_x, ax_z)  # axis 0 = z, axis 1 = x

nat = sample_dem(raw_elev, X, Z)
# Keep sea bed depressed cleanly where OSM says sea
is_sea_grid = sample_dem(raw_sea, X, Z) > 0.5
nat = np.where(is_sea_grid, np.minimum(nat, SEA - 8.0), np.maximum(nat, SEA + 0.25))

# Fine detail fractal noise so DEM looks crisp without artificial step terraces
n_fine = (fractal(N, 1.6, 51) - 0.5) * 3.2 + (fractal(N, 0.9, 52) - 0.5) * 1.1
# Damp fine displacement near roads and sea level
road_focus_mask = sstep(35.0, 200.0, np.hypot(X, Z))
nat = np.where(is_sea_grid, nat, nat + n_fine * (0.25 + 0.75 * road_focus_mask))

# Carve clean cut/fill around upper foreground roads & portals
# Accumulate nearest road point for blending terrain cleanly under roadbeds
D_road = np.full_like(nat, 1e9)
RY_road = np.zeros_like(nat)

# Bridge span midpoint (-68.1, 40.8) radius ~5.5m should NOT pull terrain up (Stórá flows underneath!)
bridge_mid_x, bridge_mid_z = -68.14, 40.85

road_samples = []
for x, y, z, _ in PATH:
    if z >= -0.5:
        road_samples.append((x, y, z, 24.0))
for x, y, z, _ in DEPIL_ROAD:
    # Skip bridge center so stream gorge drops cleanly under deck
    if math.hypot(x - bridge_mid_x, z - bridge_mid_z) < 5.0:
        continue
    # Only carve out to Hvannasundstunnilin face
    if math.hypot(x - HV_PORTAL_POS[0], z - HV_PORTAL_POS[2]) < 15.0 and (
        (x - HV_PORTAL_POS[0]) * HV_PORTAL_FWD[0] + (z - HV_PORTAL_POS[2]) * HV_PORTAL_FWD[1]
    ) < -0.5:
        continue
    road_samples.append((x, y, z, 18.0))

# Downsample slightly for fast numpy carving on 1024x1024
for x, y, z, rad in road_samples[::2]:
    dd = np.hypot(X - x, Z - z)
    m = dd < D_road
    D_road = np.where(m, dd, D_road)
    RY_road = np.where(m, y, RY_road)

# Carve switchback top nicely as well
D_village_road = np.full_like(nat, 1e9)
RY_village_road = np.zeros_like(nat)
for rname, rpts, _ in VILLAGE_ROADS:
    for x, y, z, _ in rpts[::2]:
        dd = np.hypot(X - x, Z - z)
        m = dd < D_village_road
        D_village_road = np.where(m, dd, D_village_road)
        RY_village_road = np.where(m, y, RY_village_road)

# Upper portal/junction cut: flat 2.6m shoulder around roadbed, smooth slope transition out to 24m
# Uphill side (-Z of upper road) stays steeper cut; valley side (+Z) drops naturally
blend_upper = sstep(2.6, 26.0, D_road)
# Preserve mountain massif above & behind Árnafjarðartunnilin (z < -0.8) and Hvannasundstunnilin
in_t1_roof = (Z < -0.5) & (np.abs(X) < 16.0)
hv_along = (X - HV_PORTAL_POS[0]) * HV_PORTAL_FWD[0] + (Z - HV_PORTAL_POS[2]) * HV_PORTAL_FWD[1]
hv_perp = np.abs(-(X - HV_PORTAL_POS[0]) * HV_PORTAL_FWD[1] + (Z - HV_PORTAL_POS[2]) * HV_PORTAL_FWD[0])
in_hv_roof = (hv_along < -0.4) & (hv_perp < 16.0)

H = (RY_road - 0.08) * (1.0 - blend_upper) + nat * blend_upper
# Gentle flatten under switchback / village roads
blend_vr = sstep(2.4, 10.0, D_village_road)
H = (RY_village_road - 0.12) * (1.0 - blend_vr) + H * blend_vr

# Enforce smooth dome cover BEHIND both concrete portals (along < -0.5) so bores sit deep inside turf without blocking mouths
t1_w = (1.0 - sstep(2.8, 16.0, np.abs(X))) * (1.0 - sstep(-4.2, -0.6, Z)) * sstep(-42.0, -14.0, Z)
t1_cover = 4.95 + np.maximum(-Z - 0.6, 0.0) * 0.72 + (fractal(N, 1.8, 61) - 0.5) * 1.1
H = np.where(t1_w > 0.01, np.maximum(H, H * (1.0 - t1_w) + t1_cover * t1_w), H)

hv_w = (1.0 - sstep(2.8, 16.0, hv_perp)) * (1.0 - sstep(-4.2, -0.6, hv_along))
hv_cover = HV_PORTAL_POS[1] + 4.95 + np.maximum(-hv_along - 0.6, 0.0) * 0.68
H = np.where(hv_w > 0.01, np.maximum(H, H * (1.0 - hv_w) + hv_cover * hv_w), H)

# Carve continuous Stórá stream gorge using densely resampled OSM points (especially under Depil bridge!)
riv_dense = resample_polyline(osm["stora_river"], step=2.0)
D_river = np.full_like(nat, 1e9)
for rx, _, rz, _ in riv_dense[::2]:
    dd = np.hypot(X - rx, Z - rz)
    D_river = np.minimum(D_river, dd)
gorge_depth = 3.8 * (1.0 - sstep(1.8, 8.0, D_river)) * (~is_sea_grid)
bridge_gorge = 2.4 * (1.0 - sstep(2.2, 11.5, np.hypot(X - bridge_mid_x, Z - bridge_mid_z)))
H = np.where(is_sea_grid, H, H - np.maximum(gorge_depth, bridge_gorge))

D_all_roads = np.minimum(D_road, D_village_road)


def bil(grid, x, z):
    fx = (x - X_MIN) / (X_MAX - X_MIN) * (N - 1)
    fz = (z - Z_MIN) / (Z_MAX - Z_MIN) * (N - 1)
    x0 = int(min(max(math.floor(fx), 0), N - 2))
    z0 = int(min(max(math.floor(fz), 0), N - 2))
    tx, tz = fx - x0, fz - z0
    return (
        grid[z0, x0] * (1.0 - tx) * (1.0 - tz)
        + grid[z0, x0 + 1] * tx * (1.0 - tz)
        + grid[z0 + 1, x0] * (1.0 - tx) * tz
        + grid[z0 + 1, x0 + 1] * tx * tz
    )


# ------------------------------------------------------------------ textures
def save_png(name, rgb, non_color=False):
    h_px, w_px = rgb.shape[0], rgb.shape[1]
    img = bpy.data.images.new(name, w_px, h_px, alpha=False)
    if non_color:
        img.colorspace_settings.name = 'Non-Color'
    rgba = np.concatenate([np.clip(rgb, 0.0, 1.0), np.ones((h_px, w_px, 1))], -1).astype(np.float32)
    img.pixels.foreach_set(rgba.ravel())
    img.filepath_raw = os.path.join(TEX, name + ".png")
    img.file_format = 'PNG'
    img.save()
    return img


# 2048x2048 Terrain Albedo (upsampled slope/height masks + high-freq noise, zero zebra bands!)
T_N = 2048
tx_ax = np.linspace(X_MIN, X_MAX, T_N)
tz_ax = np.linspace(Z_MIN, Z_MAX, T_N)
TX, TZ = np.meshgrid(tx_ax, tz_ax)

# Interpolate H, D_all_roads, D_river to T_N cleanly using bilinear index sampling
def sample_grid_2d(grid, xg, zg):
    fx = np.clip((xg - X_MIN) / (X_MAX - X_MIN) * (grid.shape[1] - 1), 0.0, grid.shape[1] - 1.0001)
    fz = np.clip((zg - Z_MIN) / (Z_MAX - Z_MIN) * (grid.shape[0] - 1), 0.0, grid.shape[0] - 1.0001)
    x0 = np.floor(fx).astype(np.int32)
    z0 = np.floor(fz).astype(np.int32)
    tx, tz = fx - x0, fz - z0
    return (
        grid[z0, x0] * (1 - tx) * (1 - tz)
        + grid[z0, x0 + 1] * tx * (1 - tz)
        + grid[z0 + 1, x0] * (1 - tx) * tz
        + grid[z0 + 1, x0 + 1] * tx * tz
    )

HT = sample_grid_2d(H, TX, TZ)
DRT = sample_grid_2d(D_all_roads, TX, TZ)
DRV = sample_grid_2d(D_river, TX, TZ)
cell_t = (X_MAX - X_MIN) / (T_N - 1)
gz_t, gx_t = np.gradient(HT, cell_t)
slope_t = np.hypot(gx_t, gz_t)

v1 = fractal(T_N, 1.7, 31)[..., None]
v2 = fractal(T_N, 0.9, 32)[..., None]
v_strata = fractal(T_N, 1.4, 33)

# Rich Faroese turf/moss hillside palette (olive greens + peat warmth near lower slopes)
grass_low = np.array([0.19, 0.25, 0.11]) * (1.0 - v1) + np.array([0.30, 0.30, 0.14]) * v1
grass_high = np.array([0.23, 0.26, 0.14]) * (1.0 - v1) + np.array([0.31, 0.29, 0.19]) * v1
alt_t = sstep(60.0, 260.0, HT)[..., None]
grass = (grass_low * (1.0 - alt_t) + grass_high * alt_t) * (0.82 + 0.36 * v2)

# Subtle geological horizontal basalt strata ONLY on steep exposed cliff slopes (slope > 0.65)
strata_band = 0.85 + 0.15 * np.sin((HT + (v_strata - 0.5) * 14.0) * (2.0 * math.pi / 22.0))
rock_col = np.array([0.155, 0.165, 0.175]) * strata_band[..., None] * (0.78 + 0.44 * v2)
rk_mask = sstep(0.68, 1.18, slope_t + 0.25 * (v1[..., 0] - 0.5))[..., None]
col = grass * (1.0 - rk_mask) + rock_col * rk_mask

# Exposed rocky streambed along Stórá
riv_mask = (1.0 - sstep(2.2, 6.2, DRV))[..., None]
col = col * (1.0 - 0.78 * riv_mask) + np.array([0.13, 0.135, 0.13]) * (0.75 + 0.5 * v2) * (0.78 * riv_mask)

# Dark basalt cobble/shingle shore at the head of Árnafjørður
shore_mask = (1.0 - sstep(SEA + 0.2, SEA + 3.2, HT))[..., None]
col = col * (1.0 - shore_mask) + np.array([0.085, 0.088, 0.095]) * (0.8 + 0.4 * v2) * shore_mask

# Dark grey crushed-basalt verge right along road edges
verge_mask = (1.0 - sstep(1.8, 4.0, DRT))[..., None]
col = col * (1.0 - verge_mask) + np.array([0.19, 0.19, 0.18]) * (0.85 + 0.3 * v2) * verge_mask

# Desaturate/cool high ridges slightly (> 220m above portal = >310m ASL)
high_mask = sstep(210.0, 460.0, HT)[..., None]
col = col * (1.0 - 0.38 * high_mask) + np.array([0.20, 0.205, 0.20]) * (0.38 * high_mask)

terrain_img = save_png("Exit_Terrain_Albedo", col)

# 512x512 Detail noise
det = 0.5 + (fractal(512, 0.9, 41) - 0.5) * 0.55 + (fractal(512, 1.8, 42) - 0.5) * 0.35
save_png("Exit_Terrain_Detail", np.stack([det, det, det], -1), non_color=True)

# 512x512 Weathered Faroese concrete portal albedo (damp streaks + slight mossy weathering)
pn1 = fractal(512, 1.5, 71)
pn2 = fractal(512, 0.8, 72)
streak = np.repeat(fractal(512, 2.2, 73)[0:1, :], 512, axis=0)
p_base = np.full((512, 512, 3), [0.46, 0.46, 0.44])
p_base *= (0.80 + 0.24 * pn1[..., None] + 0.12 * pn2[..., None])
p_base *= (0.82 + 0.18 * streak[..., None])
# Subtle moss tint toward top/damp crevices
v_grid = np.linspace(0.0, 1.0, 512)[:, None, None]
moss_mix = sstep(0.62, 0.96, v_grid[..., 0] + 0.22 * (pn1 - 0.5))[..., None] * 0.35
p_base = p_base * (1.0 - moss_mix) + np.array([0.22, 0.28, 0.14]) * moss_mix
portal_img = save_png("Exit_Portal_Albedo", p_base)

# Copy Exit_Signs_Albedo.png into Textures/ and load in Blender for previews
signs_src = os.path.join(HERE, "Exit_Signs_Albedo.png")
signs_dst = os.path.join(TEX, "Exit_Signs_Albedo.png")
shutil.copy2(signs_src, signs_dst)
signs_img = bpy.data.images.load(signs_dst)


def make_mat(name, color, img=None, rough=0.8, metallic=0.0):
    m = bpy.data.materials.new(name)
    try:
        m.use_nodes = True
    except Exception:
        pass
    nt = m.node_tree
    b = next((n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED'), None)
    if b is None:
        nt.nodes.clear()
        b = nt.nodes.new("ShaderNodeBsdfPrincipled")
        o = nt.nodes.new("ShaderNodeOutputMaterial")
        nt.links.new(b.outputs[0], o.inputs[0])
    b.inputs["Base Color"].default_value = (*color, 1.0)
    b.inputs["Roughness"].default_value = rough
    b.inputs["Metallic"].default_value = metallic
    if img is not None:
        tn = nt.nodes.new("ShaderNodeTexImage")
        tn.image = img
        nt.links.new(tn.outputs["Color"], b.inputs["Base Color"])
    return m


MATS = {
    "ExitTerrain": make_mat("ExitTerrain", (0.20, 0.25, 0.12), terrain_img, 0.95),
    "TunnelRoad": make_mat("TunnelRoad", (0.085, 0.085, 0.09), None, 0.78),
    "PortalConcrete": make_mat("PortalConcrete", (0.28, 0.28, 0.26), portal_img, 0.90),
    "BasaltRock": make_mat("BasaltRock", (0.055, 0.058, 0.064), None, 0.94),
    "TunnelDarkness": make_mat("TunnelDarkness", (0.008, 0.008, 0.01), None, 1.0),
    "FjordWater": make_mat("FjordWater", (0.032, 0.068, 0.098), None, 0.08),
    "RiverWater": make_mat("RiverWater", (0.09, 0.15, 0.18), None, 0.12),
    "RoadSign": make_mat("RoadSign", (0.85, 0.85, 0.85), signs_img, 0.42),
    "GuardrailMetal": make_mat("GuardrailMetal", (0.46, 0.48, 0.50), None, 0.45, 0.72),
    "HouseBlack": make_mat("HouseBlack", (0.035, 0.036, 0.04), None, 0.72),
    "HouseWhite": make_mat("HouseWhite", (0.84, 0.835, 0.79), None, 0.70),
    "HouseRed": make_mat("HouseRed", (0.44, 0.07, 0.05), None, 0.68),
    "HouseGrey": make_mat("HouseGrey", (0.28, 0.30, 0.31), None, 0.72),
    "HouseWindow": make_mat("HouseWindow", (0.06, 0.09, 0.12), None, 0.12),
    "RoofTurf": make_mat("RoofTurf", (0.16, 0.24, 0.08), None, 0.95),
    "RoofRed": make_mat("RoofRed", (0.46, 0.09, 0.06), None, 0.58),
    "RoofGrey": make_mat("RoofGrey", (0.21, 0.22, 0.24), None, 0.50),
    "RoofBlack": make_mat("RoofBlack", (0.06, 0.065, 0.07), None, 0.62),
}


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
        except Exception as e:
            print("sharp:", e)
        ob = bpy.data.objects.new(name, me)
        scene.collection.objects.link(ob)
        print(name, "tris:", sum(len(f) - 2 for f in self.f))
        return ob


UP = lambda c: (0, 1, 0)


def make_nonuniform_axis(lo, hi, focus_lo, focus_hi, n_focus, n_lo, n_hi, p_lo=1.7, p_hi=1.8):
    a_lo = lo + (focus_lo - lo) * (1.0 - np.linspace(1.0, 0.0, n_lo, endpoint=False) ** p_lo)
    a_mid = np.linspace(focus_lo, focus_hi, n_focus, endpoint=False)
    a_hi = focus_hi + (hi - focus_hi) * (np.linspace(0.0, 1.0, n_hi) ** p_hi)
    return np.concatenate([a_lo, a_mid, a_hi])


# High-resolution non-uniform terrain grid focused tightly on portals/junction/village
coords_x = make_nonuniform_axis(X_MIN, X_MAX, -110.0, 260.0, 110, 42, 88)
coords_z = make_nonuniform_axis(Z_MIN, Z_MAX, -20.0, 110.0, 68, 42, 110)
GX, GZ = len(coords_x), len(coords_z)

tv, tuv, tf = [], [], []
for j in range(GZ):
    z = float(coords_z[j])
    for i in range(GX):
        x = float(coords_x[i])
        y = float(bil(H, x, z))
        tv.append((x, y, z))
        tuv.append(((x - X_MIN) / (X_MAX - X_MIN), (z - Z_MIN) / (Z_MAX - Z_MIN)))

for j in range(GZ - 1):
    for i in range(GX - 1):
        xs = 0.5 * (coords_x[i] + coords_x[i + 1])
        zs = 0.5 * (coords_z[j] + coords_z[j + 1])
        # Punch clean mouth holes right behind both concrete portals (tucked well inside -3.2m brow rocks)
        if abs(xs) < 2.20 and -2.3 < zs < -0.1:
            continue
        ha = (xs - HV_PORTAL_POS[0]) * HV_PORTAL_FWD[0] + (zs - HV_PORTAL_POS[2]) * HV_PORTAL_FWD[1]
        hp = abs(-(xs - HV_PORTAL_POS[0]) * HV_PORTAL_FWD[1] + (zs - HV_PORTAL_POS[2]) * HV_PORTAL_FWD[0])
        if -2.3 < ha < -0.1 and hp < 2.20:
            continue
        tf.append([j * GX + i, j * GX + i + 1, (j + 1) * GX + i + 1, (j + 1) * GX + i])

terrain = MB()
terrain.add(tv, tf, "ExitTerrain", UP, tuv)


# ------------------------------------------------------------------ roads + junction apron
road = MB()


def add_road_strip(mb, pts, half_w, mat="TunnelRoad"):
    if len(pts) < 2:
        return
    rv, ruv, rf = [], [], []
    for k, (x, y, z, d) in enumerate(pts):
        a = pts[max(k - 1, 0)]
        b = pts[min(k + 1, len(pts) - 1)]
        tx, tz = b[0] - a[0], b[2] - a[2]
        ln = max(1e-5, math.hypot(tx, tz))
        nx, nz = tz / ln, -tx / ln
        rv += [(x - nx * half_w, y, z - nz * half_w), (x + nx * half_w, y, z + nz * half_w)]
        ruv += [(0.0, d / 4.0), (1.0, d / 4.0)]
    for k in range(len(pts) - 1):
        rf.append([2 * k, 2 * k + 1, 2 * k + 3, 2 * k + 2])
    mb.add(rv, rf, mat, UP, ruv)


# Main drive road starts cleanly at z = -0.4m (flush with Bore_1 floor at y=0)
add_road_strip(road, [p for p in PATH if p[2] >= -0.4], ROAD_HALF)
# Depil left branch + continuation into Hvannasundstunnilin (skip center overlap with main right turn)
add_road_strip(road, [p for p in DEPIL_ROAD if p[0] <= -4.0], ROAD_HALF)
# Flush paved T-junction fillet apron tucked seamlessly at road grade with perimeter skirt (zero shadow gaps)
apron_pts = [
    (-5.0, -0.49, 30.2), (0.0, -0.50, 30.5), (5.0, -0.62, 30.2),
    (9.5, -1.06, 29.6), (9.5, -1.06, 26.9), (5.2, -0.72, 26.2), (2.8, -0.54, 24.0),
    (1.75, -0.37, 19.0), (-1.75, -0.37, 19.0), (-3.8, -0.49, 25.0),
    (-9.5, -0.32, 27.5), (-9.5, -0.32, 30.2),
]
apron_c = (-0.2, -0.52, 28.4)
av = [apron_c] + apron_pts
auv = [(0.5, 0.5)] + [(0.5 + (p[0] - apron_c[0]) * 0.285, 0.5 + (p[2] - apron_c[2]) * 0.285) for p in apron_pts]
af = [[0, 1 + i, 1 + ((i + 1) % len(apron_pts))] for i in range(len(apron_pts))]
road.add(av, af, "TunnelRoad", UP, auv)
# Downward asphalt curb skirt around apron rim so no terrain gap ever peeks underneath
sk_v = apron_pts + [(p[0], p[1] - 0.55, p[2]) for p in apron_pts]
na = len(apron_pts)
sk_f = [[i, (i + 1) % na, na + (i + 1) % na, na + i] for i in range(na)]
road.add(sk_v, sk_f, "TunnelRoad", lambda c: (c[0] - apron_c[0], 0.0, c[2] - apron_c[2]))

# Switchback & village roads
for rname, rpts, hw in VILLAGE_ROADS:
    add_road_strip(road, rpts, hw)


# ------------------------------------------------------------------ portals & blasted basalt rock + concrete bridge
portal = MB()
SEG = 28


def prof(a, h, i):
    ph = math.pi * i / SEG
    c, s = math.cos(ph), math.sin(ph)
    return (a * math.copysign(abs(c) ** 0.6, c), h * abs(s) ** 0.75)


def build_rock_portal(origin_x, origin_y, origin_z, fwd_x, fwd_z, add_dark_bore=False, bore_len=18.0, seed=101):
    """Build a compact weathered concrete Faroese tunnel portal + blasted basalt shoulders + hazard jambs."""
    r_loc = np.random.default_rng(seed)
    right_x, right_z = -fwd_z, fwd_x

    def P(lx, ly, lz):
        return (
            origin_x + lx * right_x + lz * fwd_x,
            origin_y + ly,
            origin_z + lx * right_z + lz * fwd_z,
        )

    IN = [prof(2.05, 4.15, i) for i in range(SEG + 1)]
    OUTP = [prof(2.44, 4.52, i) for i in range(SEG + 1)]
    # Compact collar headwall (~2.75m half-width, ~4.72m high) tucked cleanly inside rock/turf cut
    HW, HH, HT = 2.75, 4.72, 1.35

    def rect_pt(i):
        ph = math.pi * i / SEG
        c, s = math.cos(ph), math.sin(ph)
        tt = min(HW / max(abs(c), 1e-6), HH / max(s, 1e-6))
        return (c * tt, s * tt)

    RECT = [rect_pt(i) for i in range(SEG + 1)]

    def strip(pa, za, pb, zb, mat, want_local):
        vs = [P(p[0], p[1], za) for p in pa] + [P(p[0], p[1], zb) for p in pb]
        uvs = [(p[0] * 0.14 + 0.5, p[1] * 0.14) for p in pa] + [(p[0] * 0.14 + 0.5, p[1] * 0.14) for p in pb]
        n = len(pa)
        fs = [[i, i + 1, n + i + 1, n + i] for i in range(n - 1)]

        def want_world(wc):
            wx, wy, wz = want_local(wc)
            return (wx * right_x + wz * fwd_x, wy, wx * right_z + wz * fwd_z)

        portal.add(vs, fs, mat, want_world, uvs)

    Z0, Z1 = -3.0, 0.35
    strip(IN, Z0, IN, Z1, "PortalConcrete", lambda c: (0, -1, 0))
    strip(OUTP, -1.2, OUTP, Z1, "PortalConcrete", lambda c: (0, 1, 0))
    strip(IN, Z1, OUTP, Z1, "PortalConcrete", lambda c: (0, 0, 1))
    strip(OUTP, 0.0, RECT, 0.0, "PortalConcrete", lambda c: (0, 0, 1))
    # Top & sides of concrete collar
    portal.add([P(-HW, HH, 0), P(HW, HH, 0), P(HW, HH, -HT), P(-HW, HH, -HT)], [[0, 1, 2, 3]], "PortalConcrete", UP,
               [(0, 0), (1, 0), (1, 0.3), (0, 0.3)])
    for sx in (-1, 1):
        portal.add([P(sx * HW, -0.8, 0), P(sx * HW, HH, 0), P(sx * HW, HH, -HT), P(sx * HW, -0.8, -HT)],
                   [[0, 1, 2, 3]], "PortalConcrete",
                   lambda c, sx=sx: (sx * right_x, 0, sx * right_z),
                   [(0, 0), (0, 1), (0.3, 1), (0.3, 0)])
        portal.add([P(sx * 2.05, -0.8, 0), P(sx * HW, -0.8, 0), P(sx * HW, 0.0, 0), P(sx * 2.05, 0.0, 0)],
                   [[0, 1, 2, 3]], "PortalConcrete", lambda c: (fwd_x, 0, fwd_z),
                   [(0, 0), (0.2, 0), (0.2, 0.2), (0, 0.2)])

    # Yellow/black hazard jamb plates on portal cheeks (UV bottom-right-left [0.505..0.745, 0.005..0.495])
    for sx in (-1, 1):
        x_in = sx * 2.10
        x_out = sx * 2.48
        x0, x1 = (x_in, x_out) if sx > 0 else (x_out, x_in)
        portal.add(
            [P(x0, 0.15, 0.38), P(x1, 0.15, 0.38), P(x1, 2.35, 0.38), P(x0, 2.35, 0.38)],
            [[0, 1, 2, 3]], "RoadSign", lambda c: (fwd_x, 0, fwd_z),
            [(0.505, 0.005), (0.745, 0.005), (0.745, 0.495), (0.505, 0.495)]
        )

    # Organic blasted basalt rock outcrops flanking left/right and browing over the portal collar
    rock_specs = (
        (-3.6, 0.4, 5.1, 1.45, 1.7, 0.12), (-5.0, 2.2, 4.2, 1.7, 2.0, -0.18), (-6.8, 4.6, 2.9, 1.8, 2.1, 0.10),
        (3.6, 0.5, 5.0, 1.45, 1.75, -0.14), (5.1, 2.4, 4.1, 1.75, 2.0, 0.16), (6.9, 4.8, 2.7, 1.9, 2.1, -0.10),
    )
    for lx, lz, h_rock, w_rock, d_rock, tilt in rock_specs:
        # Irregular tapered 6-vertex prism tilted back into the mountain slope
        h_top = h_rock + r_loc.uniform(-0.3, 0.35)
        w1, d1 = w_rock * 1.15, d_rock * 1.15
        w2, d2 = w_rock * 0.45, d_rock * 0.45
        pts_r = [
            P(lx - w1, -1.2, lz - d1 - 0.6), P(lx + w1, -1.2, lz - d1 - 0.6),
            P(lx + w1 * 0.85, -1.2, lz + d1), P(lx - w1 * 0.85, -1.2, lz + d1),
            P(lx - w2 + tilt, h_top, lz - d2 - 0.9),
            P(lx + w2 + tilt, h_top * 0.92, lz - d2 - 0.7),
            P(lx + w2 * 0.7, h_top * 0.78, lz + d2 * 0.2),
            P(lx - w2 * 0.7, h_top * 0.84, lz + d2 * 0.25),
        ]
        cen = Vector(P(lx, h_top * 0.3, lz))
        portal.add(pts_r, [[0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7], [4, 5, 6, 7]],
                   "BasaltRock", lambda q, cen=cen: tuple(q - cen))

    # Overhanging blasted basalt brow stones extending deep back into the mountain turf (-3.2m)
    for lx in (-2.0, -0.65, 0.7, 2.05):
        h0 = HH - 0.18
        h1 = HH + r_loc.uniform(0.55, 1.05)
        pts_b = [
            P(lx - 1.05, h0, -3.2), P(lx + 1.05, h0, -3.2),
            P(lx + 0.90, h0, 0.42), P(lx - 0.90, h0, 0.42),
            P(lx - 0.85, h1 + 0.35, -3.2), P(lx + 0.85, h1 + 0.35, -3.2),
            P(lx + 0.55, h1 * 0.96, 0.05), P(lx - 0.55, h1 * 0.96, 0.05),
        ]
        cen = Vector(P(lx, 0.5 * (h0 + h1), -1.2))
        portal.add(pts_b, [[0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7], [4, 5, 6, 7]],
                   "BasaltRock", lambda q, cen=cen: tuple(q - cen))

    if add_dark_bore:
        BORE = [prof(2.10, 4.20, i) for i in range(SEG + 1)]
        strip(BORE, -bore_len, BORE, -2.9, "TunnelDarkness", lambda c: (0, -1, 0))
        # Black end plug inside bore
        portal.add(
            [P(-2.2, -0.5, -bore_len + 0.3), P(2.2, -0.5, -bore_len + 0.3),
             P(2.2, 4.4, -bore_len + 0.3), P(-2.2, 4.4, -bore_len + 0.3)],
            [[0, 1, 2, 3]], "TunnelDarkness", lambda c: (fwd_x, 0, fwd_z)
        )


# 1. Main Árnafjarðartunnilin portal at origin (0, 0, 0) facing +Z
build_rock_portal(0.0, 0.0, 0.0, 0.0, 1.0, add_dark_bore=False, seed=101)
# 2. Old Hvannasundstunnilin portal at (-87.58, +3.2, 58.72) facing toward Depil bridge
build_rock_portal(HV_PORTAL_POS[0], HV_PORTAL_POS[1], HV_PORTAL_POS[2], HV_PORTAL_FWD[0], HV_PORTAL_FWD[1],
                  add_dark_bore=True, bore_len=18.0, seed=202)


# ------------------------------------------------------------------ props: Stórá bridge, T-junction signs, guardrails
props = MB()

# Concrete bridge over Stórá gorge along Depil branch (-63.0, 36.8) to (-73.2, 44.9)
bx0, bz0 = -63.0, 36.8
bx1, bz1 = -73.2, 44.9
by0, by1 = 1.85, 2.50
b_dx, b_dz = bx1 - bx0, bz1 - bz0
b_len = math.hypot(b_dx, b_dz)
b_tx, b_tz = b_dx / b_len, b_dz / b_len
b_nx, b_nz = b_tz, -b_tx
bw_half = ROAD_HALF + 0.45

# Deck slab under roadway
deck_v = [
    (bx0 - b_nx * bw_half, by0 - 0.55, bz0 - b_nz * bw_half),
    (bx0 + b_nx * bw_half, by0 - 0.55, bz0 + b_nz * bw_half),
    (bx1 + b_nx * bw_half, by1 - 0.55, bz1 + b_nz * bw_half),
    (bx1 - b_nx * bw_half, by1 - 0.55, bz1 - b_nz * bw_half),
    (bx0 - b_nx * bw_half, by0 - 0.03, bz0 - b_nz * bw_half),
    (bx0 + b_nx * bw_half, by0 - 0.03, bz0 + b_nz * bw_half),
    (bx1 + b_nx * bw_half, by1 - 0.03, bz1 + b_nz * bw_half),
    (bx1 - b_nx * bw_half, by1 - 0.03, bz1 - b_nz * bw_half),
]
b_cen = Vector((0.5 * (bx0 + bx1), 0.5 * (by0 + by1) - 0.3, 0.5 * (bz0 + bz1)))
props.add(deck_v, [[0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7], [4, 5, 6, 7], [0, 3, 2, 1]],
          "PortalConcrete", lambda q: tuple(q - b_cen))

# Concrete bridge parapets + end abutments
for s_side in (-1.0, 1.0):
    px_in = s_side * (ROAD_HALF + 0.08)
    px_out = s_side * bw_half
    p_x0, p_x1 = min(px_in, px_out), max(px_in, px_out)
    pv = [
        (bx0 + b_nx * p_x0, by0 - 0.05, bz0 + b_nz * p_x0),
        (bx0 + b_nx * p_x1, by0 - 0.05, bz0 + b_nz * p_x1),
        (bx1 + b_nx * p_x1, by1 - 0.05, bz1 + b_nz * p_x1),
        (bx1 + b_nx * p_x0, by1 - 0.05, bz1 + b_nz * p_x0),
        (bx0 + b_nx * p_x0, by0 + 0.90, bz0 + b_nz * p_x0),
        (bx0 + b_nx * p_x1, by0 + 0.90, bz0 + b_nz * p_x1),
        (bx1 + b_nx * p_x1, by1 + 0.90, bz1 + b_nz * p_x1),
        (bx1 + b_nx * p_x0, by1 + 0.90, bz1 + b_nz * p_x0),
    ]
    pc = Vector((0.5 * (bx0 + bx1) + b_nx * 0.5 * (p_x0 + p_x1), 0.5 * (by0 + by1) + 0.4, 0.5 * (bz0 + bz1) + b_nz * 0.5 * (p_x0 + p_x1)))
    props.add(pv, [[0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7], [4, 5, 6, 7]],
              "PortalConcrete", lambda q, pc=pc: tuple(q - pc))

for ep_x, ep_y, ep_z in ((bx0, by0, bz0), (bx1, by1, bz1)):
    ab_v = [
        (ep_x - b_nx * (bw_half + 0.2) - b_tx * 0.7, ep_y - 5.5, ep_z - b_nz * (bw_half + 0.2) - b_tz * 0.7),
        (ep_x + b_nx * (bw_half + 0.2) - b_tx * 0.7, ep_y - 5.5, ep_z + b_nz * (bw_half + 0.2) - b_tz * 0.7),
        (ep_x + b_nx * (bw_half + 0.2) + b_tx * 0.7, ep_y - 5.5, ep_z + b_nz * (bw_half + 0.2) + b_tz * 0.7),
        (ep_x - b_nx * (bw_half + 0.2) + b_tx * 0.7, ep_y - 5.5, ep_z - b_nz * (bw_half + 0.2) + b_tz * 0.7),
        (ep_x - b_nx * (bw_half + 0.2) - b_tx * 0.7, ep_y - 0.1, ep_z - b_nz * (bw_half + 0.2) - b_tz * 0.7),
        (ep_x + b_nx * (bw_half + 0.2) - b_tx * 0.7, ep_y - 0.1, ep_z + b_nz * (bw_half + 0.2) - b_tz * 0.7),
        (ep_x + b_nx * (bw_half + 0.2) + b_tx * 0.7, ep_y - 0.1, ep_z + b_nz * (bw_half + 0.2) + b_tz * 0.7),
        (ep_x - b_nx * (bw_half + 0.2) + b_tx * 0.7, ep_y - 0.1, ep_z - b_nz * (bw_half + 0.2) + b_tz * 0.7),
    ]
    ac = Vector((ep_x, ep_y - 2.5, ep_z))
    props.add(ab_v, [[0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]],
              "PortalConcrete", lambda q, ac=ac: tuple(q - ac))


def add_post_and_sign(cx, base_y, cz, front_dx, front_dz, w, h, center_y, uv_rect, two_posts=False):
    """Add metal post(s) + double-sided sign board; front faces (front_dx, 0, front_dz)."""
    ln = math.hypot(front_dx, front_dz)
    fx, fz = front_dx / ln, front_dz / ln
    # Viewer looks along (-fx, 0, -fz); in left-handed Unity, screen right = (-fz, 0, fx)
    rx, rz = -fz, fx
    lx, lz = -rx, -rz

    post_offsets = [-w * 0.30, w * 0.30] if two_posts else [0.0]
    pw = 0.045
    top_y = center_y + h * 0.42
    for po in post_offsets:
        px, pz = cx + rx * po - fx * 0.05, cz + rz * po - fz * 0.05
        pv = [
            (px - pw, base_y - 0.6, pz - pw), (px + pw, base_y - 0.6, pz - pw),
            (px + pw, base_y - 0.6, pz + pw), (px - pw, base_y - 0.6, pz + pw),
            (px - pw, top_y, pz - pw), (px + pw, top_y, pz - pw),
            (px + pw, top_y, pz + pw), (px - pw, top_y, pz + pw),
        ]
        pc = Vector((px, 0.5 * (base_y + top_y), pz))
        props.add(pv, [[0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]],
                  "GuardrailMetal", lambda q, pc=pc: tuple(q - pc))

    u0, v0, u1, v1 = uv_rect
    y0, y1 = center_y - h * 0.5, center_y + h * 0.5
    off_f = 0.025
    # Front textured face
    fv = [
        (cx + lx * (w * 0.5) + fx * off_f, y0, cz + lz * (w * 0.5) + fz * off_f),
        (cx + rx * (w * 0.5) + fx * off_f, y0, cz + rz * (w * 0.5) + fz * off_f),
        (cx + rx * (w * 0.5) + fx * off_f, y1, cz + rz * (w * 0.5) + fz * off_f),
        (cx + lx * (w * 0.5) + fx * off_f, y1, cz + lz * (w * 0.5) + fz * off_f),
    ]
    props.add(fv, [[0, 1, 2, 3]], "RoadSign", lambda c: (fx, 0, fz), [(u0, v0), (u1, v0), (u1, v1), (u0, v1)])
    # Grey metal backing plate
    off_b = -0.025
    bv = [
        (cx + lx * (w * 0.5) + fx * off_b, y0, cz + lz * (w * 0.5) + fz * off_b),
        (cx + rx * (w * 0.5) + fx * off_b, y0, cz + rz * (w * 0.5) + fz * off_b),
        (cx + rx * (w * 0.5) + fx * off_b, y1, cz + rz * (w * 0.5) + fz * off_b),
        (cx + lx * (w * 0.5) + fx * off_b, y1, cz + lz * (w * 0.5) + fz * off_b),
    ]
    props.add(bv, [[0, 1, 2, 3]], "GuardrailMetal", lambda c: (-fx, 0, -fz))


# Junction directional signpost (stacked <- Depil / Árnafjørður -> facing emerging car & upper cameras)
j_y = -0.52
# Top blade: <- Depil (UV [0.005..0.495, 0.505..0.995])
add_post_and_sign(1.6, j_y, 32.6, -0.18, -0.98, w=1.75, h=0.62, center_y=j_y + 2.35,
                  uv_rect=(0.005, 0.505, 0.495, 0.995), two_posts=False)
# Lower blade: Árnafjørður -> (UV [0.505..0.995, 0.505..0.995])
add_post_and_sign(1.6, j_y, 32.6, -0.18, -0.98, w=1.75, h=0.62, center_y=j_y + 1.68,
                  uv_rect=(0.505, 0.505, 0.995, 0.995), two_posts=False)

# Faroese T-junction double chevron board (<< >>) dead-ahead of the tunnel mouth
add_post_and_sign(-1.4, j_y, 32.9, 0.0, -1.0, w=2.55, h=1.05, center_y=j_y + 1.45,
                  uv_rect=(0.005, 0.005, 0.495, 0.495), two_posts=True)

# Red-ringed tunnel oncoming-priority sign near Hvannasundstunnilin portal right edge
hx, hy, hz = HV_PORTAL_POS
h_rx, h_rz = -HV_PORTAL_FWD[1], HV_PORTAL_FWD[0]
add_post_and_sign(hx + h_rx * 3.1 + HV_PORTAL_FWD[0] * 2.2, hy,
                  hz + h_rz * 3.1 + HV_PORTAL_FWD[1] * 2.2,
                  HV_PORTAL_FWD[0], HV_PORTAL_FWD[1], w=0.82, h=0.82, center_y=hy + 2.0,
                  uv_rect=(0.755, 0.005, 0.995, 0.495), two_posts=False)


# Galvanized steel W-beam guardrails along valley (+Z) edge of Árnfjarðarvegur & Depil approach
def add_guardrail(pts, side_sign=1.0, offset=2.15):
    if len(pts) < 2:
        return
    gv, gf = [], []
    for k, (x, y, z, d) in enumerate(pts):
        a = pts[max(k - 1, 0)]
        b = pts[min(k + 1, len(pts) - 1)]
        tx, tz = b[0] - a[0], b[2] - a[2]
        ln = max(1e-5, math.hypot(tx, tz))
        nx, nz = (tz / ln) * side_sign, (-tx / ln) * side_sign
        rx, rz = x + nx * offset, z + nz * offset
        rx_out, rz_out = rx + nx * 0.14, rz + nz * 0.14
        gv += [(rx, y + 0.46, rz), (rx, y + 0.78, rz), (rx_out, y + 0.78, rz_out), (rx_out, y + 0.46, rz_out)]
        # Steel I-posts every ~4m
        if k % 3 == 0:
            pw = 0.05
            pv = [
                (rx - pw, y - 0.5, rz - pw), (rx + pw, y - 0.5, rz - pw),
                (rx + pw, y - 0.5, rz + pw), (rx - pw, y - 0.5, rz + pw),
                (rx - pw, y + 0.80, rz - pw), (rx + pw, y + 0.80, rz - pw),
                (rx + pw, y + 0.80, rz + pw), (rx - pw, y + 0.80, rz + pw),
            ]
            pc = Vector((rx, y + 0.2, rz))
            props.add(pv, [[0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7], [4, 5, 6, 7]],
                      "GuardrailMetal", lambda q, pc=pc: tuple(q - pc))
    for k in range(len(pts) - 1):
        i0 = 4 * k
        i1 = 4 * (k + 1)
        gf += [
            [i0 + 0, i1 + 0, i1 + 1, i0 + 1],
            [i0 + 1, i1 + 1, i1 + 2, i0 + 2],
            [i0 + 2, i1 + 2, i1 + 3, i0 + 3],
        ]
    props.add(gv, gf, "GuardrailMetal")


# Right-turn / valley drop-off guardrail along Árnfjarðarvegur (car travel +X -> right-hand shoulder is +Z)
add_guardrail([p for p in PATH if p[0] >= 10.0], side_sign=1.0, offset=2.15)
# Valley (+Z) guardrail approaching Depil bridge (travel -X -> left-hand side of travel = +Z)
add_guardrail([p for p in DEPIL_ROAD if -62.5 <= p[0] <= -6.0], side_sign=-1.0, offset=2.15)


# ------------------------------------------------------------------ water: fjord sea + Stórá mountain stream
water = MB()
water.add(
    [(X_MIN - 200, SEA, Z_MIN - 200), (X_MAX + 200, SEA, Z_MIN - 200),
     (X_MAX + 200, SEA, Z_MAX + 200), (X_MIN - 200, SEA, Z_MAX + 200)],
    [[0, 1, 2, 3]], "FjordWater", UP
)

# Continuous Stórá stream ribbon from densely resampled points so it never clips under terrain bumps
riv_pts = []
r_acc = 0.0
for rx, _, rz, _ in riv_dense:
    ry = max(SEA + 0.14, float(bil(H, rx, rz)) + 0.45)
    if riv_pts:
        r_acc += math.hypot(rx - riv_pts[-1][0], rz - riv_pts[-1][2])
    riv_pts.append((float(rx), float(ry), float(rz), r_acc))
add_road_strip(water, riv_pts, half_w=1.85, mat="RiverWater")


# ------------------------------------------------------------------ village, Árnafjarðar kirkja, harbour & salmon pens
village = MB()


def add_box(mb, pts_8, mat, cen):
    out_w = lambda q, c=cen: tuple(q - c)
    mb.add(pts_8, [[0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7], [4, 5, 6, 7]], mat, out_w)


def house(cx, cz, yaw, w, d, h, roof_h, wall, roof, windows=True, is_church=False):
    # Ensure corners are grounded cleanly on slopes
    c, s = math.cos(yaw), math.sin(yaw)

    def P(lx, ly, lz):
        return (cx + lx * c + lz * s, ly, cz - lx * s + lz * c)

    corner_ys = [float(bil(H, *P(sx * w * 0.5, 0.0, sz * d * 0.5)[0::2])) for sx in (-1, 1) for sz in (-1, 1)]
    y_max = max(corner_ys)
    y_min = min(corner_ys)
    if y_max < SEA + 0.6:
        return False
    y0 = 0.65 * sum(corner_ys) * 0.25 + 0.35 * y_max

    def PL(lx, ly, lz):
        return (cx + lx * c + lz * s, y0 + ly, cz - lx * s + lz * c)

    b = y_min - y0 - 1.4
    corners = [(-w / 2, -d / 2), (w / 2, -d / 2), (w / 2, d / 2), (-w / 2, d / 2)]
    vs = [PL(x, b, z) for x, z in corners] + [PL(x, h, z) for x, z in corners]
    cen = Vector(PL(0, h / 2, 0))
    out = lambda q, cen=cen: tuple(q - cen)
    village.add(vs, [[0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]], wall, out)

    o = 0.32
    r = [
        PL(-w / 2 - o, h - 0.1, -d / 2 - o), PL(w / 2 + o, h - 0.1, -d / 2 - o),
        PL(w / 2 + o, h - 0.1, d / 2 + o), PL(-w / 2 - o, h - 0.1, d / 2 + o),
        PL(-w / 2 - o, h + roof_h, 0), PL(w / 2 + o, h + roof_h, 0),
    ]
    village.add(r, [[0, 1, 5, 4], [3, 2, 5, 4]], roof, lambda q, cen=cen: (q[0] - cen[0], 3.0, q[2] - cen[2]))
    g = [
        PL(-w / 2, h, -d / 2), PL(-w / 2, h, d / 2), PL(-w / 2, h + roof_h - 0.08, 0),
        PL(w / 2, h, -d / 2), PL(w / 2, h, d / 2), PL(w / 2, h + roof_h - 0.08, 0),
    ]
    village.add(g, [[0, 1, 2], [3, 4, 5]], wall, out)

    if windows and w >= 5.5 and d >= 4.0:
        n_win = 4 if is_church else max(2, min(4, int(w / 3.2)))
        wh = 1.65 if is_church else 1.05
        ww = 0.75 if is_church else 0.85
        wy0 = 1.05 if is_church else max(0.95, h * 0.38)
        for side in (-1, 1):
            lz = side * (d * 0.5 + 0.04)
            for wi in range(n_win):
                lx = (w * 0.68) * ((wi + 0.5) / n_win - 0.5)
                wv = [PL(lx - ww * 0.5, wy0, lz), PL(lx + ww * 0.5, wy0, lz),
                      PL(lx + ww * 0.5, wy0 + wh, lz), PL(lx - ww * 0.5, wy0 + wh, lz)]
                village.add(wv, [[0, 1, 2, 3]], "HouseWindow", lambda q, cen=cen: tuple(q - cen))

    if is_church:
        # Traditional Faroese bell-cote (fleche) at the west gable end of Árnafjarðar kirkja
        tx0 = -w * 0.33
        tw = 1.9
        th0 = h + roof_h * 0.45
        th1 = h + roof_h + 3.6
        t_c = [(-tw / 2, -tw / 2), (tw / 2, -tw / 2), (tw / 2, tw / 2), (-tw / 2, tw / 2)]
        tvs = [PL(tx0 + x, th0, z) for x, z in t_c] + [PL(tx0 + x, th1, z) for x, z in t_c]
        tcen = Vector(PL(tx0, 0.5 * (th0 + th1), 0))
        add_box(village, tvs, "HouseWhite", tcen)
        # Red pyramid spire cap
        sh = th1 + 2.8
        svs = [PL(tx0 + x, th1, z) for x, z in t_c] + [PL(tx0, sh, 0)]
        scen = Vector(PL(tx0, th1 + 1.0, 0))
        village.add(svs, [[0, 1, 4], [1, 2, 4], [2, 3, 4], [3, 0, 4]], "RoofRed", lambda q, sc=scen: tuple(q - sc))

    return True


walls_res = ["HouseBlack", "HouseWhite", "HouseBlack", "HouseGrey", "HouseRed", "HouseWhite"]
roofs_res = ["RoofGrey", "RoofTurf", "RoofRed", "RoofBlack", "RoofTurf"]
b_count = 0
for idx, b in enumerate(osm["buildings"]):
    if b["kind"] == "construction":
        continue
    if b["kind"] == "church":
        house(b["x"], b["z"], b["yaw"], max(b["w"], 12.6), max(b["d"], 8.4), 4.6, 3.1,
              "HouseWhite", "RoofRed", windows=True, is_church=True)
        b_count += 1
        continue
    w = max(b["w"], 3.5)
    d = max(b["d"], 3.5)
    # Orient ridge along longer footprint axis cleanly
    yaw = b["yaw"]
    if d > w:
        w, d = d, w
        yaw += math.pi * 0.5
    if b["z"] < 120.0:
        # Upper hillside service/agricultural outbuildings: muted dark Faroese barns/sheds
        wall = "HouseBlack"
        roof = "RoofGrey" if idx % 2 == 0 else "RoofBlack"
        h = 2.1
        rh = 1.1
    elif b["kind"] in ("industrial", "commercial"):
        wall = "HouseGrey" if idx % 2 == 0 else "HouseBlack"
        roof = "RoofGrey"
        h = rng.uniform(4.2, 5.8)
        rh = rng.uniform(1.6, 2.3)
    elif b["kind"] == "boathouse":
        wall = "HouseBlack" if idx % 3 != 0 else "HouseRed"
        roof = "RoofTurf" if idx % 2 == 0 else "RoofGrey"
        h = rng.uniform(2.5, 3.2)
        rh = rng.uniform(1.6, 2.2)
    else:
        wall = walls_res[idx % len(walls_res)]
        roof = "RoofRed" if b.get("roof_color") == "red" else roofs_res[idx % len(roofs_res)]
        h = rng.uniform(3.2, 4.8) if max(w, d) > 8.5 else rng.uniform(2.4, 3.3)
        rh = rng.uniform(1.7, 2.5)
    if house(b["x"], b["z"], yaw, w, d, h, rh, wall, roof, windows=(max(w, d) >= 7.0)):
        b_count += 1

print("OSM buildings placed:", b_count)

# Concrete harbour quay projecting at (669, 711) into the fjord edge
qx0, qz0 = 656.0, 692.0
qx1, qz1 = 716.0, 742.0
qy_top = SEA + 2.0
qy_bot = SEA - 6.0
q_cen = Vector((0.5 * (qx0 + qx1), 0.5 * (qy_top + qy_bot), 0.5 * (qz0 + qz1)))
add_box(village, [
    (qx0, qy_bot, qz0), (qx1, qy_bot, qz0), (qx1, qy_bot, qz1), (qx0, qy_bot, qz1),
    (qx0, qy_top, qz0), (qx1, qy_top, qz0), (qx1, qy_top, qz1), (qx0, qy_top, qz1),
], "PortalConcrete", q_cen)

# Iconic Árnafjørður circular salmon aquaculture cages out in the fjord
for cx_p, cz_p, rad in ((815.0, 605.0, 14.0), (885.0, 650.0, 14.0)):
    n_seg = 20
    r_in, r_out = rad - 0.55, rad + 0.55
    py0, py1 = SEA + 0.05, SEA + 0.85
    pvs, pfs = [], []
    for s in range(n_seg):
        a = 2.0 * math.pi * s / n_seg
        ca, sa = math.cos(a), math.sin(a)
        pvs += [
            (cx_p + ca * r_in, py0, cz_p + sa * r_in),
            (cx_p + ca * r_out, py0, cz_p + sa * r_out),
            (cx_p + ca * r_out, py1, cz_p + sa * r_out),
            (cx_p + ca * r_in, py1, cz_p + sa * r_in),
        ]
    for s in range(n_seg):
        s2 = (s + 1) % n_seg
        a0, b0 = 4 * s, 4 * s2
        pfs += [
            [a0 + 1, b0 + 1, b0 + 2, a0 + 2],
            [a0 + 2, b0 + 2, b0 + 3, a0 + 3],
            [a0 + 3, b0 + 3, b0 + 0, a0 + 0],
        ]
    village.add(pvs, pfs, "HouseBlack")


# ------------------------------------------------------------------ export FBX + path JSON
objs = [
    terrain.to_object("Exit_Terrain", 60),
    road.to_object("Exit_Road", 60),
    portal.to_object("Exit_Portal", 35),
    props.to_object("Exit_Props", 35),
    water.to_object("Exit_Water", 60),
    village.to_object("Exit_Village", 30),
]

with open(os.path.join(OUT, "TunnelExitPath.json"), "w", encoding="utf-8") as fh:
    json.dump({"points": [{"x": round(p[0], 3), "y": round(p[1], 3), "z": round(p[2], 3)} for p in PATH]}, fh)

bpy.ops.wm.save_as_mainfile(filepath=os.path.join(HERE, "TunnelExit.blend"))
for o in scene.objects:
    o.select_set(o in objs)
bpy.context.view_layer.objects.active = objs[0]
bpy.ops.export_scene.fbx(
    filepath=os.path.join(OUT, "TunnelExit.fbx"),
    use_selection=True,
    object_types={'MESH'},
    apply_scale_options='FBX_SCALE_UNITS',
    axis_forward='-Z',
    axis_up='Y',
    bake_space_transform=True,
    mesh_smooth_type='OFF',
    bake_anim=False,
)
print("exported to", OUT)


# ------------------------------------------------------------------ Blender Cycles test renders
if DO_RENDER:
    prev = os.path.join(HERE, "preview")
    os.makedirs(prev, exist_ok=True)
    sun = bpy.data.lights.new("Sun", 'SUN')
    sun.energy = 2.8
    so = bpy.data.objects.new("Sun", sun)
    so.rotation_euler = (math.radians(52), 0, math.radians(215))
    scene.collection.objects.link(so)
    w = bpy.data.worlds.new("World")
    try:
        w.use_nodes = True
    except Exception:
        pass
    bg = next((nd for nd in w.node_tree.nodes if nd.type == 'BACKGROUND'), None)
    if bg:
        bg.inputs[0].default_value = (0.58, 0.67, 0.74, 1)
        bg.inputs[1].default_value = 1.0
    scene.world = w
    scene.render.engine = 'CYCLES'
    scene.cycles.samples = 24
    try:
        scene.cycles.use_denoising = True
    except Exception:
        pass
    scene.render.resolution_x, scene.render.resolution_y = 1100, 620
    scene.render.image_settings.file_format = 'JPEG'
    cam = bpy.data.cameras.new("Cam")
    cam.clip_end = 4500
    co = bpy.data.objects.new("Cam", cam)
    scene.collection.objects.link(co)
    scene.camera = co

    shots = (
        ("portal_front", (8.0, 1.6, 38.0), (0.0, 1.5, 0.0), 26),
        ("shot1_portal", (6.2, 1.9, 17.5), (-5.0, 0.5, 34.0), 22),
        ("shot2_fork", (-6.0, 17.0, -5.0), (-26.0, 1.0, 38.0), 20),
        ("shot3_fjord", (-12.0, 14.5, -5.0), (140.0, -28.0, 95.0), 24),
        ("village", (260.0, -5.0, 120.0), (540.0, -78.0, 560.0), 30),
        ("aerial", (-120.0, 260.0, -140.0), (350.0, -55.0, 420.0), 26),
    )
    for nm, loc, tgt, lens in shots:
        co.location = U(*loc)
        cam.lens = lens
        co.rotation_euler = (U(*tgt) - U(*loc)).to_track_quat('-Z', 'Y').to_euler()
        scene.render.filepath = os.path.join(prev, f"exit_{nm}.jpg")
        bpy.ops.render.render(write_still=True)
    print("renders done")
