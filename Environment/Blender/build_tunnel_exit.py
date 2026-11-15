"""
Outside the Árnafjørður end of the old tunnel (Árnafjarðartunnilin): rock-cut concrete portal,
T-junction with the left road crossing Stórá into Hvannasundstunnilin (toward Depil), and
Árnfjarðarvegur turning right down the mountainside towards Árnafjørður village, church, and fjord.

Built from real OpenStreetMap + Mapzen Terrarium DEM data (arnafjordur_geo.npz, arnafjordur_osm.json).

Run:  blender --background --python build_tunnel_exit.py -- <unity Assets/Environment/TunnelExit dir> [--no-render] [--houses-only]
      (--houses-only rewrites just VillageHouses.json: door + window spots of the detailed village houses)

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
# --houses-only: rebuild the scene in memory but only write VillageHouses.json (no .blend, no FBX, no renders)
HOUSES_ONLY = "--houses-only" in ARGV
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

# 512x512 Tileable house cladding / corrugated roof detail (neutral 0.5 for URP _DETAIL_MULX2)
h_rng = np.random.default_rng(83)
px_u = np.arange(512)
board_w = 32  # 16 boards per UV tile (12.5 cm per board at 0.5 UV cycles/m)
phase_u = (px_u % board_w) / float(board_w)
board_id = px_u // board_w
board_tone = h_rng.uniform(-0.035, 0.035, 512 // board_w)[board_id]
groove = -0.14 * np.exp(-((phase_u - 0.0) / 0.065) ** 2) - 0.14 * np.exp(-((phase_u - 1.0) / 0.065) ** 2)
bevel = 0.035 * np.exp(-((phase_u - 0.12) / 0.07) ** 2)
u_strip = (board_tone + groove + bevel)[None, :]
grain_raw = fractal(512, 1.3, 81)
grain_vert = np.mean([np.roll(grain_raw, shift, axis=0) for shift in range(-8, 9, 2)], axis=0) - 0.5
weather = fractal(512, 0.9, 82) - 0.5
h_det = np.clip(0.5 + u_strip + grain_vert * 0.18 + weather * 0.10, 0.22, 0.78)
save_png("Exit_House_Detail", np.stack([h_det, h_det, h_det], -1), non_color=True)

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
    "HouseBlack": make_mat("HouseBlack", (0.032, 0.034, 0.038), None, 0.74),
    "HouseWhite": make_mat("HouseWhite", (0.88, 0.89, 0.88), None, 0.70),
    "HouseRed": make_mat("HouseRed", (0.36, 0.055, 0.048), None, 0.70),
    "HouseGrey": make_mat("HouseGrey", (0.74, 0.76, 0.78), None, 0.82),
    "HouseTeal": make_mat("HouseTeal", (0.24, 0.44, 0.40), None, 0.70),
    "HouseYellow": make_mat("HouseYellow", (0.72, 0.50, 0.14), None, 0.70),
    "HouseDoor": make_mat("HouseDoor", (0.14, 0.26, 0.18), None, 0.55),
    "HouseTimber": make_mat("HouseTimber", (0.26, 0.20, 0.15), None, 0.78),
    "HouseWindow": make_mat("HouseWindow", (0.06, 0.09, 0.12), None, 0.12),
    "HouseWindowLit": make_mat("HouseWindowLit", (0.89, 0.62, 0.33), None, 0.15),
    "RoofTurf": make_mat("RoofTurf", (0.11, 0.23, 0.16), None, 0.72),
    "RoofRed": make_mat("RoofRed", (0.42, 0.09, 0.07), None, 0.58),
    "RoofGrey": make_mat("RoofGrey", (0.20, 0.22, 0.24), None, 0.52),
    "RoofBlack": make_mat("RoofBlack", (0.05, 0.055, 0.062), None, 0.62),
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
WALKABLE_BOX = (300.0, 500.0, 240.0, 480.0)


def _load_walkable_bounds():
    candidates = [
        os.path.join(OUT, "..", "Village", "VillageWalkable.json"),
        os.path.join(HERE, "..", "..", "ArnafjordurEscapeRoom", "Assets", "Environment", "Village", "VillageWalkable.json"),
    ]
    for path in candidates:
        if os.path.isfile(path):
            with open(path, "r", encoding="utf-8") as fh:
                wdata = json.load(fh)
            corridors = []
            for c in wdata.get("corridors", []):
                pts = [(p["x"], p["z"]) for p in c.get("points", [])]
                if len(pts) >= 2:
                    corridors.append((c["name"], float(c["halfWidth"]), pts))
            for fp in wdata.get("footpaths", []):
                pts = [(p["x"], p["z"]) for p in fp.get("points", [])]
                if len(pts) >= 2:
                    corridors.append((fp["name"], float(fp["halfWidth"]), pts))
            beach_poly = []
            for z in wdata.get("zones", []):
                if z.get("name") == "Beach":
                    beach_poly = [(p["x"], p["z"]) for p in z.get("outline", [])]
                    break
            return corridors, beach_poly

    fallback = [(f"vroad_{i}", 1.45, [tuple(p) for p in vr]) for i, vr in enumerate(osm["village_roads"][:4])]
    fallback += [
        ("ChurchPath", 0.95, [(400.6, 286.6), (402.0, 290.4), (402.75, 293.55)]),
        ("BeachPathSouth", 0.95, [(395.45, 315.2), (408.0, 315.3), (423.5, 315.4), (430.0, 317.5)]),
        ("BeachPathNorth", 0.95, [(400.75, 399.2), (413.0, 398.6), (424.0, 393.5)]),
    ]
    return fallback, []


WALK_CORRIDORS, WALK_BEACH_POLY = _load_walkable_bounds()


def _seg_dist_2d(px, pz, ax, az, bx, bz):
    dx, dz = bx - ax, bz - az
    l2 = dx * dx + dz * dz
    t = 0.0 if l2 < 1e-12 else min(1.0, max(0.0, ((px - ax) * dx + (pz - az) * dz) / l2))
    return math.hypot(px - (ax + dx * t), pz - (az + dz * t))


def _inside_poly_2d(px, pz, poly):
    inside = False
    n = len(poly)
    if n < 3:
        return False
    j = n - 1
    for i in range(n):
        ax, az = poly[i]
        bx, bz = poly[j]
        if (az > pz) != (bz > pz) and px < (bx - ax) * (pz - az) / (bz - az) + ax:
            inside = not inside
        j = i
    return inside


def walkable_edge_clearance(px, pz):
    best = 1e9
    for _, hw, pts in WALK_CORRIDORS:
        d = min(_seg_dist_2d(px, pz, a[0], a[1], b[0], b[1]) for a, b in zip(pts[:-1], pts[1:]))
        best = min(best, d - hw)
    if len(WALK_BEACH_POLY) >= 3:
        d_edge = min(_seg_dist_2d(px, pz, WALK_BEACH_POLY[i][0], WALK_BEACH_POLY[i][1],
                                  WALK_BEACH_POLY[(i + 1) % len(WALK_BEACH_POLY)][0],
                                  WALK_BEACH_POLY[(i + 1) % len(WALK_BEACH_POLY)][1])
                     for i in range(len(WALK_BEACH_POLY)))
        if _inside_poly_2d(px, pz, WALK_BEACH_POLY):
            d_edge = -d_edge
        best = min(best, d_edge)
    return best


def road_or_path_distance(px, pz):
    best = 1e9
    for _, _, pts in WALK_CORRIDORS:
        d = min(_seg_dist_2d(px, pz, a[0], a[1], b[0], b[1]) for a, b in zip(pts[:-1], pts[1:]))
        best = min(best, d)
    return best


def _point_obb_dist(px, pz, cx, cz, yaw, w, d):
    c, s = math.cos(yaw), math.sin(yaw)
    dx, dz = px - cx, pz - cz
    lx = dx * c - dz * s
    lz = dx * s + dz * c
    ox = abs(lx) - w * 0.5
    oz = abs(lz) - d * 0.5
    return math.hypot(max(ox, 0.0), max(oz, 0.0)) + min(max(ox, oz), 0.0)


def _compute_building_obbs():
    obbs = []
    for idx, b in enumerate(osm["buildings"]):
        if b["kind"] == "construction":
            continue
        if b["kind"] == "church":
            w, d, yaw = max(b["w"], 12.6), max(b["d"], 8.4), b["yaw"]
        else:
            w, d, yaw = max(b["w"], 3.5), max(b["d"], 3.5), b["yaw"]
            if d > w:
                w, d = d, w
                yaw += math.pi * 0.5
        c, s = math.cos(yaw), math.sin(yaw)
        cx, cz = b["x"], b["z"]
        c_ys = [float(bil(H, cx + sx * w * 0.5 * c + sz * d * 0.5 * s,
                           cz - sx * w * 0.5 * s + sz * d * 0.5 * c))
                for sx in (-1, 1) for sz in (-1, 1)]
        if max(c_ys) < SEA + 0.6:
            continue
        obbs.append((idx, cx, cz, yaw, w, d))
    return obbs


BUILDING_OBBS = _compute_building_obbs()


def neighbor_dist(b_idx, px, pz):
    best = 1e9
    for o_idx, ocx, ocz, oyaw, ow, od in BUILDING_OBBS:
        if o_idx == b_idx:
            continue
        d = _point_obb_dist(px, pz, ocx, ocz, oyaw, ow, od)
        if d < best:
            best = d
    return best


def wall_clearance(b_idx, cx, cz, yaw, w, d, nx_loc, nz_loc):
    c, s = math.cos(yaw), math.sin(yaw)
    best = 1e9
    for u in (-0.28, 0.0, 0.28):
        if abs(nz_loc) > 0.5:
            lx = u * w
            lz = nz_loc * (d * 0.5 + 0.05)
        else:
            lx = nx_loc * (w * 0.5 + 0.05)
            lz = u * d
        px = cx + lx * c + lz * s
        pz = cz - lx * s + lz * c
        best = min(best, neighbor_dist(b_idx, px, pz))
    return best


def _quad_uvs(pts, mat, uv_scale=0.5, u_axis=None, v_axis=None):
    p0 = Vector(pts[0])
    if u_axis is None:
        du = Vector(pts[1]) - p0
        u_axis = du.normalized() if du.length > 1e-6 else Vector((1.0, 0.0, 0.0))
    if v_axis is None:
        dv = Vector(pts[3]) - p0
        v_axis = dv.normalized() if dv.length > 1e-6 else Vector((0.0, 1.0, 0.0))
    uvs = []
    for p in pts:
        dvec = Vector(p) - p0
        u = dvec.dot(u_axis) * uv_scale
        v = dvec.dot(v_axis) * uv_scale
        if mat == "PortalConcrete":
            # Keep concrete UVs inside the clean grey band of Exit_Portal_Albedo.png (below moss v > 0.62)
            u = 0.10 + min(0.40, abs(u) * 0.08)
            v = 0.10 + min(0.40, abs(v) * 0.08)
        uvs.append((u, v))
    return uvs


def add_quad_uv(mb, p0, p1, p2, p3, mat, want_dir, uv_scale=0.5, u_axis=None, v_axis=None):
    pts = [p0, p1, p2, p3]
    uvs = _quad_uvs(pts, mat, uv_scale, u_axis, v_axis)
    mb.add(pts, [[0, 1, 2, 3]], mat, want_dir, uvs)


def add_box_uv(mb, bot4, top4, mat, uv_scale=0.5, include_bottom=False, include_top=True, skip_sides=()):
    cen = sum((Vector(p) for p in bot4 + top4), Vector((0.0, 0.0, 0.0))) / 8.0
    out_w = lambda q, c=cen: tuple(q - c)
    for i in range(4):
        if i in skip_sides:
            continue
        j = (i + 1) % 4
        add_quad_uv(mb, bot4[i], bot4[j], top4[j], top4[i], mat, out_w, uv_scale)
    if include_top:
        add_quad_uv(mb, top4[0], top4[1], top4[2], top4[3], mat, out_w, uv_scale)
    if include_bottom:
        add_quad_uv(mb, bot4[3], bot4[2], bot4[1], bot4[0], mat, out_w, uv_scale)


def add_box(mb, pts_8, mat, cen):
    out_w = lambda q, c=cen: tuple(q - c)
    mb.add(pts_8, [[0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7], [4, 5, 6, 7]], mat, out_w)


# Detailed houses with their entrance doors and wall windows, written to VillageHouses.json so Unity can make
# doors knockable and build interiors behind the right walls (world = portal-local Unity coordinates).
HOUSE_RECORDS = []


def house(cx, cz, yaw, w, d, h, roof_h, wall, roof, windows=True, is_church=False, b_idx=0, kind="house", trim_override=None, plinth_override=None):
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

    # Keep Árnafjarðar kirkja backdrop shell intact for ChurchDetail in ChurchArea.fbx
    if is_church:
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
            n_win = 4
            wh, ww, wy0 = 1.65, 0.75, 1.05
            for side in (-1, 1):
                lz = side * (d * 0.5 + 0.04)
                for wi in range(n_win):
                    lx = (w * 0.68) * ((wi + 0.5) / n_win - 0.5)
                    wv = [PL(lx - ww * 0.5, wy0, lz), PL(lx + ww * 0.5, wy0, lz),
                          PL(lx + ww * 0.5, wy0 + wh, lz), PL(lx - ww * 0.5, wy0 + wh, lz)]
                    village.add(wv, [[0, 1, 2, 3]], "HouseWindow", lambda q, cen=cen: tuple(q - cen))

        # Traditional Faroese bell-cote (fleche) at the west gable end of Árnafjarðar kirkja
        tx0 = -w * 0.33
        tw = 1.9
        th0 = h + roof_h * 0.45
        th1 = h + roof_h + 3.6
        t_c = [(-tw / 2, -tw / 2), (tw / 2, -tw / 2), (tw / 2, tw / 2), (-tw / 2, tw / 2)]
        tvs = [PL(tx0 + x, th0, z) for x, z in t_c] + [PL(tx0 + x, th1, z) for x, z in t_c]
        tcen = Vector(PL(tx0, 0.5 * (th0 + th1), 0))
        add_box(village, tvs, "HouseWhite", tcen)
        sh = th1 + 2.8
        svs = [PL(tx0 + x, th1, z) for x, z in t_c] + [PL(tx0, sh, 0)]
        scen = Vector(PL(tx0, th1 + 1.0, 0))
        village.add(svs, [[0, 1, 4], [1, 2, 4], [2, 3, 4], [3, 0, 4]], "RoofRed", lambda q, sc=scen: tuple(q - sc))
        return True

    in_walkable = (WALKABLE_BOX[0] <= cx <= WALKABLE_BOX[1] and WALKABLE_BOX[2] <= cz <= WALKABLE_BOX[3])
    is_detailed = in_walkable

    # Contrasting Faroese trim and door materials
    if trim_override is not None:
        trim_mat = trim_override
    elif wall in ("HouseBlack", "HouseTimber"):
        trim_mat = wall
    else:
        trim_mat = "HouseWhite"

    if wall == "HouseRed":
        door_mat = "HouseWhite" if b_idx % 2 == 0 else "HouseDoor"
    elif wall in ("HouseWhite", "HouseTeal", "HouseYellow"):
        door_mat = ("HouseWhite", "HouseDoor", "HouseBlack")[b_idx % 3]
    else:
        door_mat = ("HouseTimber", "HouseBlack", "HouseDoor")[b_idx % 3]

    if plinth_override is not None:
        plinth_mat = plinth_override
    elif wall in ("HouseWhite", "HouseRed", "HouseTeal", "HouseYellow") and w >= 5.8:
        plinth_mat = "HouseGrey"
    else:
        plinth_mat = "PortalConcrete" if b_idx % 2 == 0 else "BasaltRock"
    plinth_h = 0.34 if is_detailed else 0.28
    plinth_pad = 0.04 if is_detailed else 0.03

    # Clearances per wall (for overhang trimming and exposed facade placement)
    clear_zp = wall_clearance(b_idx, cx, cz, yaw, w, d, 0.0, 1.0)
    clear_zn = wall_clearance(b_idx, cx, cz, yaw, w, d, 0.0, -1.0)
    clear_xp = wall_clearance(b_idx, cx, cz, yaw, w, d, 1.0, 0.0)
    clear_xn = wall_clearance(b_idx, cx, cz, yaw, w, d, -1.0, 0.0)

    # 1. Foundation Plinth (kjallari) + water-table ledge
    p_corners = [(-w / 2 - plinth_pad, -d / 2 - plinth_pad), (w / 2 + plinth_pad, -d / 2 - plinth_pad),
                 (w / 2 + plinth_pad, d / 2 + plinth_pad), (-w / 2 - plinth_pad, d / 2 + plinth_pad)]
    add_box_uv(village, [PL(x, b, z) for x, z in p_corners], [PL(x, plinth_h, z) for x, z in p_corners],
               plinth_mat, uv_scale=0.5, include_top=True)
    if is_detailed:
        wt_pad = plinth_pad + 0.02
        wt_corners = [(-w / 2 - wt_pad, -d / 2 - wt_pad), (w / 2 + wt_pad, -d / 2 - wt_pad),
                      (w / 2 + wt_pad, d / 2 + wt_pad), (-w / 2 - wt_pad, d / 2 + wt_pad)]
        add_box_uv(village, [PL(x, plinth_h - 0.06, z) for x, z in wt_corners],
                   [PL(x, plinth_h + 0.015, z) for x, z in wt_corners],
                   plinth_mat, uv_scale=0.5, include_top=True)

    # 2. Main Timber Walls (overlapping plinth top slightly so there is no crack)
    wall_y0 = plinth_h - 0.02
    add_box_uv(village, [PL(x, wall_y0, z) for x, z in corners], [PL(x, h, z) for x, z in corners],
               wall, uv_scale=0.5, include_top=False)

    # Gable triangles with vertically aligned cladding UVs
    cen = Vector(PL(0, h * 0.5, 0))
    out = lambda q, cen=cen: tuple(q - cen)
    for gx, g_norm_sign in ((-w * 0.5, -1.0), (w * 0.5, 1.0)):
        gt0 = PL(gx, h - 0.02, -d * 0.5)
        gt1 = PL(gx, h - 0.02, d * 0.5)
        gt2 = PL(gx, h + roof_h, 0.0)
        u_ax = (Vector(gt1) - Vector(gt0)).normalized()
        v_ax = Vector((0.0, 1.0, 0.0))
        uvs_g = [
            (0.0, (h - 0.02 - wall_y0) * 0.5),
            (d * 0.5, (h - 0.02 - wall_y0) * 0.5),
            (d * 0.25, (h + roof_h - wall_y0) * 0.5),
        ]
        village.add([gt0, gt1, gt2], [[0, 1, 2]], wall,
                    lambda q, gns=g_norm_sign: (gns * c, 0.0, -gns * s), uvs_g)

    # 3. Roof Slab (real thickness + soffit + eaves fascia + gable rake bargeboards)
    slope = roof_h / max(d * 0.5, 0.5)
    max_oz = min(0.34, 0.24 / max(slope, 0.2))
    oz_pos = max_oz if clear_zp >= 0.45 else 0.05
    oz_neg = max_oz if clear_zn >= 0.45 else 0.05
    ox_pos = (0.28 if is_detailed else 0.24) if clear_xp >= 0.45 else 0.04
    ox_neg = (0.28 if is_detailed else 0.24) if clear_xn >= 0.45 else 0.04
    t_roof = 0.16 if is_detailed else 0.12

    x0_r = -w * 0.5 - ox_neg
    x1_r = w * 0.5 + ox_pos
    y_rb = h + roof_h
    y_rt = y_rb + t_roof

    for side, oz_s in ((-1.0, oz_neg), (1.0, oz_pos)):
        z_e = side * (d * 0.5 + oz_s)
        y_eb = h - slope * oz_s
        y_et = y_eb + t_roof

        bot4 = [PL(x0_r, y_rb, 0.0), PL(x1_r, y_rb, 0.0), PL(x1_r, y_eb, z_e), PL(x0_r, y_eb, z_e)]
        top4 = [PL(x0_r, y_rt, 0.0), PL(x1_r, y_rt, 0.0), PL(x1_r, y_et, z_e), PL(x0_r, y_et, z_e)]
        rcen = sum((Vector(p) for p in bot4 + top4), Vector((0.0, 0.0, 0.0))) / 8.0
        rout = lambda q, rc=rcen: tuple(q - rc)

        # Top roof slope (u along ridge X, v down slope -> corrugated seams run down-slope)
        add_quad_uv(village, top4[0], top4[1], top4[2], top4[3], roof, rout, uv_scale=0.5)
        # Soffit underside
        add_quad_uv(village, bot4[3], bot4[2], bot4[1], bot4[0], trim_mat if is_detailed else roof, rout, uv_scale=0.5)
        # Eave fascia board (side 2)
        add_quad_uv(village, bot4[2], bot4[3], top4[3], top4[2], trim_mat if is_detailed else roof, rout, uv_scale=0.5)
        # Gable rake edges (sides 1 and 3)
        add_quad_uv(village, bot4[1], bot4[2], top4[2], top4[1], trim_mat if is_detailed else roof, rout, uv_scale=0.5)
        add_quad_uv(village, bot4[3], bot4[0], top4[0], top4[3], trim_mat if is_detailed else roof, rout, uv_scale=0.5)

        if is_detailed:
            # Proud gable bargeboard strip (vindskide) along both gable rakes
            for rx_in, rx_out in ((x0_r - 0.035, x0_r + 0.015), (x1_r - 0.015, x1_r + 0.035)):
                b_bot4 = [PL(rx_in, y_rb - 0.03, 0.0), PL(rx_out, y_rb - 0.03, 0.0),
                          PL(rx_out, y_eb - 0.03, z_e), PL(rx_in, y_eb - 0.03, z_e)]
                b_top4 = [PL(rx_in, y_rt + 0.03, 0.0), PL(rx_out, y_rt + 0.03, 0.0),
                          PL(rx_out, y_et + 0.03, z_e), PL(rx_in, y_et + 0.03, z_e)]
                add_box_uv(village, b_bot4, b_top4, trim_mat, uv_scale=0.5, include_bottom=True, skip_sides=(0,))

            # Traditional timber turf retainer edge board (torvhald) along lower eaves of turf roofs
            if roof == "RoofTurf" and kind == "boathouse":
                z_ret_in = side * (d * 0.5 + oz_s - 0.06)
                z_ret_out = side * (d * 0.5 + oz_s + 0.01)
                z_r0, z_r1 = min(z_ret_in, z_ret_out), max(z_ret_in, z_ret_out)
                add_box_uv(village,
                           [PL(x0_r, y_et - 0.02, z_r0), PL(x1_r, y_et - 0.02, z_r0),
                            PL(x1_r, y_et - 0.02, z_r1), PL(x0_r, y_et - 0.02, z_r1)],
                           [PL(x0_r, y_et + 0.09, z_r0), PL(x1_r, y_et + 0.09, z_r0),
                            PL(x1_r, y_et + 0.09, z_r1), PL(x0_r, y_et + 0.09, z_r1)],
                           "HouseTimber", uv_scale=0.5, include_bottom=False)

            # Rainwater gutter along the eaves
            gutter_mat = "HouseBlack" if wall != "HouseBlack" else "HouseGrey"
            zg_in = side * (d * 0.5 + oz_s + 0.005)
            zg_out = side * (d * 0.5 + oz_s + 0.075)
            zg0, zg1 = min(zg_in, zg_out), max(zg_in, zg_out)
            add_box_uv(village,
                       [PL(x0_r, y_eb - 0.02, zg0), PL(x1_r, y_eb - 0.02, zg0),
                        PL(x1_r, y_eb - 0.02, zg1), PL(x0_r, y_eb - 0.02, zg1)],
                       [PL(x0_r, y_eb + 0.055, zg0), PL(x1_r, y_eb + 0.055, zg0),
                        PL(x1_r, y_eb + 0.055, zg1), PL(x0_r, y_eb + 0.055, zg1)],
                       gutter_mat, uv_scale=0.5, include_bottom=True)

    if is_detailed:
        # Ridge cap beam
        ridge_mat = trim_mat if roof == "RoofBlack" else "RoofBlack"
        add_box_uv(village,
                   [PL(x0_r - 0.01, y_rt - 0.03, -0.09), PL(x1_r + 0.01, y_rt - 0.03, -0.09),
                    PL(x1_r + 0.01, y_rt - 0.03, 0.09), PL(x0_r - 0.01, y_rt - 0.03, 0.09)],
                   [PL(x0_r - 0.01, y_rt + 0.065, -0.09), PL(x1_r + 0.01, y_rt + 0.065, -0.09),
                    PL(x1_r + 0.01, y_rt + 0.065, 0.09), PL(x0_r - 0.01, y_rt + 0.065, 0.09)],
                   ridge_mat, uv_scale=0.5, include_bottom=False)

        # Vertical rainwater downspout on one clear eave corner
        ds_side = 1.0 if clear_zp >= clear_zn else -1.0
        ds_clear = clear_zp if ds_side > 0 else clear_zn
        ds_oz = oz_pos if ds_side > 0 else oz_neg
        if ds_clear >= 0.9:
            gutter_mat = "HouseBlack" if wall != "HouseBlack" else "HouseGrey"
            ds_x = w * 0.5 - 0.14
            ds_z = ds_side * (d * 0.5 + 0.04)
            ds_y_top = h - slope * ds_oz + 0.02
            add_box_uv(village,
                       [PL(ds_x - 0.03, plinth_h + 0.04, ds_z - 0.03), PL(ds_x + 0.03, plinth_h + 0.04, ds_z - 0.03),
                        PL(ds_x + 0.03, plinth_h + 0.04, ds_z + 0.03), PL(ds_x - 0.03, plinth_h + 0.04, ds_z + 0.03)],
                       [PL(ds_x - 0.03, ds_y_top, ds_z - 0.03), PL(ds_x + 0.03, ds_y_top, ds_z - 0.03),
                        PL(ds_x + 0.03, ds_y_top, ds_z + 0.03), PL(ds_x - 0.03, ds_y_top, ds_z + 0.03)],
                       gutter_mat, uv_scale=0.5, include_bottom=True)
            # Top elbow connecting gutter to wall pipe
            ez0 = min(ds_z, ds_side * (d * 0.5 + ds_oz + 0.04))
            ez1 = max(ds_z, ds_side * (d * 0.5 + ds_oz + 0.04))
            add_box_uv(village,
                       [PL(ds_x - 0.03, ds_y_top - 0.05, ez0), PL(ds_x + 0.03, ds_y_top - 0.05, ez0),
                        PL(ds_x + 0.03, ds_y_top - 0.05, ez1), PL(ds_x - 0.03, ds_y_top - 0.05, ez1)],
                       [PL(ds_x - 0.03, ds_y_top + 0.02, ez0), PL(ds_x + 0.03, ds_y_top + 0.02, ez0),
                        PL(ds_x + 0.03, ds_y_top + 0.02, ez1), PL(ds_x - 0.03, ds_y_top + 0.02, ez1)],
                       gutter_mat, uv_scale=0.5, include_bottom=True)

        # 4. Corner Boards (quoins) and Gable-Base Trim Band
        cb_w = 0.11
        cb_p = 0.022
        for sx in (-1.0, 1.0):
            for sz in (-1.0, 1.0):
                cx0 = sx * (w * 0.5 - cb_w * 0.5) - cb_w * 0.5
                cx1 = cx0 + cb_w
                cz0 = sz * (d * 0.5 - cb_w * 0.5) - cb_w * 0.5
                cz1 = cz0 + cb_w
                # Expand slightly outward beyond wall faces
                if sx < 0:
                    cx0 = -w * 0.5 - cb_p
                else:
                    cx1 = w * 0.5 + cb_p
                if sz < 0:
                    cz0 = -d * 0.5 - cb_p
                else:
                    cz1 = d * 0.5 + cb_p
                add_box_uv(village,
                           [PL(cx0, plinth_h, cz0), PL(cx1, plinth_h, cz0),
                            PL(cx1, plinth_h, cz1), PL(cx0, plinth_h, cz1)],
                           [PL(cx0, h + 0.02, cz0), PL(cx1, h + 0.02, cz0),
                            PL(cx1, h + 0.02, cz1), PL(cx0, h + 0.02, cz1)],
                           trim_mat, uv_scale=0.5, include_bottom=False)

        for gx_s in (-1.0, 1.0):
            bx0 = gx_s * (w * 0.5) - (0.025 if gx_s < 0 else 0.0)
            bx1 = gx_s * (w * 0.5) + (0.025 if gx_s > 0 else 0.0)
            add_box_uv(village,
                       [PL(bx0, h - 0.02, -d * 0.5 - 0.02), PL(bx1, h - 0.02, -d * 0.5 - 0.02),
                        PL(bx1, h - 0.02, d * 0.5 + 0.02), PL(bx0, h - 0.02, d * 0.5 + 0.02)],
                       [PL(bx0, h + 0.06, -d * 0.5 - 0.02), PL(bx1, h + 0.06, -d * 0.5 - 0.02),
                        PL(bx1, h + 0.06, d * 0.5 + 0.02), PL(bx0, h + 0.06, d * 0.5 + 0.02)],
                       trim_mat, uv_scale=0.5, include_bottom=True)

        # 5. Chimney Stack on residential houses
        if kind not in ("shed", "boathouse", "industrial", "commercial", "garage", "service") and w >= 5.2 and d >= 4.2:
            ch_x = w * 0.20 * (1.0 if b_idx % 2 == 0 else -1.0)
            ch_z = -d * 0.14
            ch_r = 0.25
            ch_top = h + roof_h + 0.65
            add_box_uv(village,
                       [PL(ch_x - ch_r, h, ch_z - ch_r), PL(ch_x + ch_r, h, ch_z - ch_r),
                        PL(ch_x + ch_r, h, ch_z + ch_r), PL(ch_x - ch_r, h, ch_z + ch_r)],
                       [PL(ch_x - ch_r, ch_top, ch_z - ch_r), PL(ch_x + ch_r, ch_top, ch_z - ch_r),
                        PL(ch_x + ch_r, ch_top, ch_z + ch_r), PL(ch_x - ch_r, ch_top, ch_z + ch_r)],
                       "PortalConcrete", uv_scale=0.5, include_bottom=False)
            # Chimney cap & flue pot
            cap_r = ch_r + 0.05
            add_box_uv(village,
                       [PL(ch_x - cap_r, ch_top, ch_z - cap_r), PL(ch_x + cap_r, ch_top, ch_z - cap_r),
                        PL(ch_x + cap_r, ch_top, ch_z + cap_r), PL(ch_x - cap_r, ch_top, ch_z + cap_r)],
                       [PL(ch_x - cap_r, ch_top + 0.08, ch_z - cap_r), PL(ch_x + cap_r, ch_top + 0.08, ch_z - cap_r),
                        PL(ch_x + cap_r, ch_top + 0.08, ch_z + cap_r), PL(ch_x - cap_r, ch_top + 0.08, ch_z + cap_r)],
                       "HouseBlack", uv_scale=0.5, include_bottom=True)
            add_box_uv(village,
                       [PL(ch_x - 0.12, ch_top + 0.08, ch_z - 0.12), PL(ch_x + 0.12, ch_top + 0.08, ch_z - 0.12),
                        PL(ch_x + 0.12, ch_top + 0.08, ch_z + 0.12), PL(ch_x - 0.12, ch_top + 0.08, ch_z + 0.12)],
                       [PL(ch_x - 0.12, ch_top + 0.24, ch_z - 0.12), PL(ch_x + 0.12, ch_top + 0.24, ch_z - 0.12),
                        PL(ch_x + 0.12, ch_top + 0.24, ch_z + 0.12), PL(ch_x - 0.12, ch_top + 0.24, ch_z + 0.12)],
                       "HouseBlack", uv_scale=0.5, include_bottom=False)

    # 6. Windows & Entrance Doors (unified across all 4 walls)
    if not is_detailed:
        if windows and w >= 5.5:
            n_win = max(2, min(4, int(w / 3.2)))
            wh = 1.05
            ww = 0.85
            wy0 = max(0.95, h * 0.38)
            for side in (-1, 1):
                lz = side * (d * 0.5 + 0.03)
                wn = (side * s, 0.0, side * c)
                for wi in range(n_win):
                    lx = (w * 0.68) * ((wi + 0.5) / n_win - 0.5)
                    lit = (kind not in ("shed", "boathouse", "garage", "service")) and ((b_idx * 37 + wi * 7) % 10 < 2)
                    gmat = "HouseWindowLit" if lit else "HouseWindow"
                    add_quad_uv(village, PL(lx - ww * 0.5, wy0, lz), PL(lx + ww * 0.5, wy0, lz),
                                PL(lx + ww * 0.5, wy0 + wh, lz), PL(lx - ww * 0.5, wy0 + wh, lz),
                                gmat, lambda q, wn=wn: wn, uv_scale=0.5)
        return True

    record = {
        "index": b_idx, "kind": kind, "wall": wall, "door": door_mat,
        "x": round(cx, 3), "y": round(y0, 3), "z": round(cz, 3), "yaw": round(yaw, 5),
        "w": round(w, 3), "d": round(d, 3), "h": round(h, 3), "roofH": round(roof_h, 3),
        "plinthH": round(plinth_h, 3), "groundMin": round(y_min, 3), "groundMax": round(y_max, 3),
        "doors": [], "windows": [],
    }
    HOUSE_RECORDS.append(record)

    def record_opening(lst, wdef, u_c, y_bottom, width, height):
        _, ox, oz, tx, tz, nx, nz, _, _ = wdef
        px, py, pz = PL(ox + u_c * tx, y_bottom, oz + u_c * tz)
        lst.append({
            "wall": wdef[0], "u": round(u_c, 3),
            "x": round(px, 3), "y": round(py, 3), "z": round(pz, 3),
            "nx": round(nx * c + nz * s, 5), "nz": round(-nx * s + nz * c, 5),
            "width": round(width, 3), "height": round(height, 3),
        })

    # Wall definitions: (key, ox, oz, tx, tz, nx, nz, span, clear)
    walls = [
        ("zp", 0.0, d * 0.5, 1.0, 0.0, 0.0, 1.0, w, clear_zp),
        ("zn", 0.0, -d * 0.5, -1.0, 0.0, 0.0, -1.0, w, clear_zn),
        ("xp", w * 0.5, 0.0, 0.0, -1.0, 1.0, 0.0, d, clear_xp),
        ("xn", -w * 0.5, 0.0, 0.0, 1.0, -1.0, 0.0, d, clear_xn),
    ]

    def wall_world_pt(wdef, u_along, n_out=0.0):
        _, ox, oz, tx, tz, nx, nz, _, _ = wdef
        lx = ox + u_along * tx + n_out * nx
        lz = oz + u_along * tz + n_out * nz
        return P(lx, 0.0, lz)[0], P(lx, 0.0, lz)[2]

    def add_wall_box(wdef, u0, u1, y0_w, y1_w, n0, n1, mat):
        _, ox, oz, tx, tz, nx, nz, _, _ = wdef
        WL = lambda u, y, n: PL(ox + u * tx + n * nx, y, oz + u * tz + n * nz)
        add_box_uv(village,
                   [WL(u0, y0_w, n0), WL(u1, y0_w, n0), WL(u1, y0_w, n1), WL(u0, y0_w, n1)],
                   [WL(u0, y1_w, n0), WL(u1, y1_w, n0), WL(u1, y1_w, n1), WL(u0, y1_w, n1)],
                   mat, uv_scale=0.5, include_bottom=True, include_top=True)

    def add_detailed_window(wdef, u_c, wy0, ww, wh, gmat, shallow=False, mullions=True):
        _, ox, oz, tx, tz, nx, nz, _, _ = wdef
        WL = lambda u, y, n: PL(ox + u * tx + n * nx, y, oz + u * tz + n * nz)
        wn = (nx * c + nz * s, 0.0, -nx * s + nz * c)
        sc = 0.55 if shallow else 1.0
        record_opening(record["windows"], wdef, u_c, wy0, ww, wh)
        # Glass pane at +0.022m (no z-fighting with wall at 0)
        n_glass = 0.020 * sc + 0.008
        add_quad_uv(village, WL(u_c - ww * 0.5, wy0, n_glass), WL(u_c + ww * 0.5, wy0, n_glass),
                    WL(u_c + ww * 0.5, wy0 + wh, n_glass), WL(u_c - ww * 0.5, wy0 + wh, n_glass),
                    gmat, lambda q, wn=wn: wn, uv_scale=0.5)
        # Jambs, bottom sill, and top lintel
        jb = 0.052
        add_wall_box(wdef, u_c - ww * 0.5 - jb, u_c - ww * 0.5 + 0.008, wy0, wy0 + wh, -0.01, 0.042 * sc + 0.008, trim_mat)
        add_wall_box(wdef, u_c + ww * 0.5 - 0.008, u_c + ww * 0.5 + jb, wy0, wy0 + wh, -0.01, 0.042 * sc + 0.008, trim_mat)
        add_wall_box(wdef, u_c - ww * 0.5 - 0.075, u_c + ww * 0.5 + 0.075, wy0 - 0.058, wy0 + 0.008, -0.01, 0.070 * sc + 0.008, trim_mat)
        add_wall_box(wdef, u_c - ww * 0.5 - 0.065, u_c + ww * 0.5 + 0.065, wy0 + wh - 0.008, wy0 + wh + 0.058, -0.01, 0.056 * sc + 0.008, trim_mat)
        if mullions:
            add_wall_box(wdef, u_c - 0.020, u_c + 0.020, wy0, wy0 + wh, 0.012, 0.038 * sc + 0.008, trim_mat)
            add_wall_box(wdef, u_c - ww * 0.5, u_c + ww * 0.5, wy0 + wh * 0.52, wy0 + wh * 0.52 + 0.036, 0.012, 0.038 * sc + 0.008, trim_mat)

    def add_entrance_door(wdef, u_c, span, shallow=False):
        _, ox, oz, tx, tz, nx, nz, _, _ = wdef
        dw = min(0.92, span * 0.30)
        dh = min(1.94, max(1.62, h - plinth_h - 0.16))
        sc = 0.65 if shallow else 1.0
        record_opening(record["doors"], wdef, u_c, plinth_h, dw, dh)
        # Door panel + outer casing + lever handle
        add_wall_box(wdef, u_c - dw * 0.5, u_c + dw * 0.5, plinth_h, plinth_h + dh, -0.005, 0.030, door_mat)
        add_wall_box(wdef, u_c - dw * 0.5 - 0.07, u_c - dw * 0.5 + 0.01, plinth_h, plinth_h + dh, -0.01, 0.052 * sc, trim_mat)
        add_wall_box(wdef, u_c + dw * 0.5 - 0.01, u_c + dw * 0.5 + 0.07, plinth_h, plinth_h + dh, -0.01, 0.052 * sc, trim_mat)
        add_wall_box(wdef, u_c - dw * 0.5 - 0.075, u_c + dw * 0.5 + 0.075, plinth_h + dh - 0.01, plinth_h + dh + 0.07, -0.01, 0.058 * sc, trim_mat)
        add_wall_box(wdef, u_c + dw * 0.25, u_c + dw * 0.40, plinth_h + dh * 0.47, plinth_h + dh * 0.505, 0.025, 0.058, "GuardrailMetal")

        # Over-door protective canopy/hood (when eave clearance and road clearance allow)
        wpx, wpz = wall_world_pt(wdef, u_c, 0.30)
        road_clear = walkable_edge_clearance(wpx, wpz)
        if not shallow and road_clear >= 0.45 and plinth_h + dh + 0.22 <= h:
            cw = dw * 0.5 + 0.18
            add_wall_box(wdef, u_c - cw, u_c + cw, plinth_h + dh + 0.05, plinth_h + dh + 0.11, -0.01, 0.26, trim_mat)
            # Sloped roof cap on door hood
            WL = lambda u, y, n: PL(ox + u * tx + n * nx, y, oz + u * tz + n * nz)
            add_box_uv(village,
                       [WL(u_c - cw - 0.02, plinth_h + dh + 0.11, -0.01),
                        WL(u_c + cw + 0.02, plinth_h + dh + 0.11, -0.01),
                        WL(u_c + cw + 0.02, plinth_h + dh + 0.09, 0.30),
                        WL(u_c - cw - 0.02, plinth_h + dh + 0.09, 0.30)],
                       [WL(u_c - cw - 0.02, plinth_h + dh + 0.22, -0.01),
                        WL(u_c + cw + 0.02, plinth_h + dh + 0.22, -0.01),
                        WL(u_c + cw + 0.02, plinth_h + dh + 0.13, 0.30),
                        WL(u_c - cw - 0.02, plinth_h + dh + 0.13, 0.30)],
                       roof, uv_scale=0.5, include_bottom=False)

        # Warm porch lantern beside the door on residential houses
        if kind not in ("shed", "boathouse", "garage", "service"):
            u_lan = u_c - dw * 0.5 - 0.17
            if abs(u_lan) <= span * 0.5 - 0.24:
                ly_lan = min(h - 0.32, plinth_h + dh * 0.78)
                add_wall_box(wdef, u_lan - 0.05, u_lan + 0.05, ly_lan, ly_lan + 0.14, 0.0, 0.095 * sc, "HouseWindowLit")
                add_wall_box(wdef, u_lan - 0.065, u_lan + 0.065, ly_lan + 0.14, ly_lan + 0.175, -0.005, 0.11 * sc, "HouseBlack")

        # Grounded stone steps where terrain drops below threshold and corridor/neighbor clearance allows
        fx, fz = wall_world_pt(wdef, u_c, 0.55)
        y_front = float(bil(H, fx, fz))
        drop = (y0 + plinth_h) - y_front
        sw = dw * 0.5 + 0.18
        step_clear = all(
            walkable_edge_clearance(*wall_world_pt(wdef, u_c + su * sw, 0.80)) >= 0.35
            and neighbor_dist(b_idx, *wall_world_pt(wdef, u_c + su * sw, 0.80)) >= 0.35
            for su in (-1.0, 0.0, 1.0)
        )
        y_bot = min(plinth_h - 0.55, y_front - y0 - 0.45)
        if drop > 0.10 and step_clear and not shallow:
            n_steps = min(3, max(1, int(math.ceil(drop / 0.24))))
            step_drop = min(0.22, drop / n_steps)
            for si in range(n_steps):
                n0 = 0.0 if si == 0 else 0.36 + (si - 1) * 0.18
                n1 = 0.36 + si * 0.18
                y_top_s = plinth_h - 0.02 - si * step_drop
                add_wall_box(wdef, u_c - sw, u_c + sw, y_bot, y_top_s, n0, n1, "PortalConcrete")
        elif drop > 0.04 and walkable_edge_clearance(fx, fz) >= 0.22:
            add_wall_box(wdef, u_c - sw * 0.85, u_c + sw * 0.85, y_bot, plinth_h - 0.02, 0.0, 0.14, "PortalConcrete")

    # Select best exposed wall for entrance door (prefer closest to village street / footpaths)
    door_wall_key = None
    if h - plinth_h >= 1.80:
        best_score = 1e9
        for wdef in walls:
            wkey, _, _, _, _, _, _, wspan, wclear = wdef
            if wclear < 1.1 or wspan < 2.8:
                continue
            mx, mz = wall_world_pt(wdef, 0.0, 0.4)
            rdist = road_or_path_distance(mx, mz)
            eclear = walkable_edge_clearance(mx, mz)
            # Slight preference for long walls and walls with safe step room
            score = rdist + (0.9 if wspan == d else 0.0) + (2.5 if eclear < 0.6 else 0.0)
            if score < best_score:
                best_score = score
                door_wall_key = wkey

    # Populate each exposed wall with windows (and door on the chosen wall)
    for w_idx, wdef in enumerate(walls):
        wkey, _, _, _, _, _, _, wspan, wclear = wdef
        if wclear < 1.1:
            continue
        is_gable = wkey in ("xp", "xn")
        if not is_gable:
            n_slots = 1 if wspan < 3.9 else (2 if wspan < 6.5 else (3 if wspan < 9.2 else 4))
        else:
            n_slots = 2 if wspan >= 4.8 else 1

        mx, mz = wall_world_pt(wdef, 0.0, 0.1)
        shallow_wall = walkable_edge_clearance(mx, mz) < 0.75

        ww = min(0.82, (wspan * 0.68) / n_slots * 0.62)
        wh = min(1.04, max(0.62, (h - plinth_h) * 0.43))
        wy0 = plinth_h + max(0.50, (h - plinth_h - wh) * 0.46)
        if wy0 + wh > h - 0.26:
            wy0 = max(plinth_h + 0.35, h - 0.26 - wh)
            if wy0 + wh > h - 0.22:
                wh = max(0.45, h - 0.22 - wy0)

        # Pick which slot becomes the entrance door on door_wall_key
        door_slot = -1
        if wkey == door_wall_key:
            best_sd = 1e9
            for si in range(n_slots):
                su = (wspan * 0.68) * ((si + 0.5) / n_slots - 0.5)
                sx_w, sz_w = wall_world_pt(wdef, su, 0.4)
                sd = road_or_path_distance(sx_w, sz_w)
                if sd < best_sd:
                    best_sd = sd
                    door_slot = si

        for si in range(n_slots):
            su = (wspan * 0.68) * ((si + 0.5) / n_slots - 0.5)
            wx_w, wz_w = wall_world_pt(wdef, su, 0.05)
            wx_out, wz_out = wall_world_pt(wdef, su, 0.60)
            if neighbor_dist(b_idx, wx_w, wz_w) < 0.85 or neighbor_dist(b_idx, wx_out, wz_out) < 0.40:
                continue
            if si == door_slot:
                add_entrance_door(wdef, su, wspan, shallow=shallow_wall)
            else:
                lit = (kind not in ("shed", "boathouse", "garage", "service")) and ((b_idx * 37 + w_idx * 13 + si * 7) % 10 < 3)
                gmat = "HouseWindowLit" if lit else "HouseWindow"
                add_detailed_window(wdef, su, wy0, ww, wh, gmat, shallow=shallow_wall, mullions=(ww >= 0.45))

        # Gable attic window centered in the triangle above the tie-beam
        if is_gable and roof_h >= 1.35 and wspan >= 3.4:
            aww = min(0.68, wspan * 0.20)
            max_tri_h = roof_h * (1.0 - aww / max(wspan, 0.5))
            awh = min(0.78, max(0.45, max_tri_h * 0.46))
            awy0 = h + max(0.18, max_tri_h * 0.14)
            if awy0 + awh <= h + max_tri_h - 0.14:
                lit_attic = (kind not in ("shed", "boathouse", "garage", "service")) and ((b_idx * 41 + w_idx * 19) % 10 < 2)
                gmat_a = "HouseWindowLit" if lit_attic else "HouseWindow"
                add_detailed_window(wdef, 0.0, awy0, aww, awh, gmat_a, shallow=shallow_wall, mullions=True)

        # Downhill basement (kjallari) windows when slope exposes the foundation wall
        if y0 - y_min > 0.85 and not is_gable and not shallow_wall:
            nb_base = min(2, max(1, int(wspan / 4.2)))
            for bi in range(nb_base):
                bu = (wspan * 0.56) * ((bi + 0.5) / nb_base - 0.5)
                fx_b, fz_b = wall_world_pt(wdef, bu, 0.15)
                gy_b = float(bil(H, fx_b, fz_b))
                if y0 - 0.45 - 0.22 > gy_b + 0.16 and neighbor_dist(b_idx, fx_b, fz_b) >= 0.90:
                    add_detailed_window(wdef, bu, -0.52, 0.54, 0.38, "HouseWindow", shallow=False, mullions=True)

    return True


# Exact real-world Árnafjørður building colors keyed by OSM building index:
# (wall_mat, roof_mat, trim_mat, plinth_mat, optional (h, roof_h) override)
REAL_VILLAGE_HOUSES = {
    # Near church
    63: ("HouseBlack", "RoofBlack", "HouseBlack", "BasaltRock", None),
    33: ("HouseWhite", "RoofBlack", "HouseWhite", "HouseGrey", None),
    # West of road — bridge & river foreground
    28: ("HouseWhite", "RoofBlack", "HouseWhite", "HouseGrey", (4.5, 2.05)),
    37: ("HouseTimber", "RoofTurf", "HouseWhite", "PortalConcrete", (2.3, 1.45)),
    22: ("HouseRed", "RoofBlack", "HouseWhite", "HouseGrey", (3.9, 2.20)),
    42: ("HouseRed", "RoofBlack", "HouseWhite", "PortalConcrete", (2.8, 1.85)),
    41: ("HouseBlack", "RoofBlack", "HouseBlack", "BasaltRock", (2.5, 1.55)),
    23: ("HouseRed", "RoofBlack", "HouseWhite", "HouseGrey", (3.6, 2.10)),
    64: ("HouseRed", "RoofTurf", "HouseWhite", "PortalConcrete", None),
    65: ("HouseBlack", "RoofBlack", "HouseBlack", "BasaltRock", None),
    # West of road — uphill & midground
    67: ("HouseTimber", "RoofTurf", "HouseTimber", "BasaltRock", None),
    66: ("HouseBlack", "RoofTurf", "HouseBlack", "BasaltRock", None),
    25: ("HouseWhite", "RoofBlack", "HouseWhite", "HouseGrey", None),
    24: ("HouseWhite", "RoofBlack", "HouseWhite", "HouseGrey", None),
    26: ("HouseBlack", "RoofBlack", "HouseBlack", "BasaltRock", None),
    43: ("HouseBlack", "RoofTurf", "HouseBlack", "BasaltRock", None),
    20: ("HouseWhite", "RoofBlack", "HouseWhite", "HouseGrey", None),
    19: ("HouseRed", "RoofBlack", "HouseWhite", "HouseGrey", None),
    60: ("HouseWhite", "RoofBlack", "HouseWhite", "PortalConcrete", None),
    # East of road — seafoam-teal house complex & beachside sheds
    21: ("HouseTeal", "RoofBlack", "HouseWhite", "HouseGrey", (4.6, 2.20)),
    38: ("HouseTeal", "RoofBlack", "HouseWhite", "HouseGrey", (2.6, 1.55)),
    39: ("HouseTeal", "RoofBlack", "HouseWhite", "HouseGrey", (3.0, 1.70)),
    17: ("HouseTeal", "RoofBlack", "HouseWhite", "HouseGrey", None),
    36: ("HouseTimber", "RoofRed", "HouseTimber", "PortalConcrete", (2.4, 1.50)),
    27: ("HouseTimber", "RoofTurf", "HouseTimber", "PortalConcrete", (2.8, 1.75)),
    35: ("HouseBlack", "RoofTurf", "HouseBlack", "BasaltRock", (2.5, 1.55)),
    34: ("HouseBlack", "RoofTurf", "HouseBlack", "BasaltRock", (2.6, 1.60)),
    40: ("HouseBlack", "RoofBlack", "HouseBlack", "BasaltRock", None),
    # East of road — white house with green roof, yellow cottage & black boathouses
    18: ("HouseWhite", "RoofTurf", "HouseWhite", "HouseGrey", (3.7, 2.15)),
    44: ("HouseYellow", "RoofBlack", "HouseWhite", "PortalConcrete", None),
    69: ("HouseYellow", "RoofBlack", "HouseWhite", "PortalConcrete", None),
    16: ("HouseBlack", "RoofBlack", "HouseBlack", "BasaltRock", None),
    45: ("HouseBlack", "RoofBlack", "HouseBlack", "BasaltRock", None),
    46: ("HouseBlack", "RoofBlack", "HouseBlack", "BasaltRock", None),
    103: ("HouseBlack", "RoofBlack", "HouseBlack", "BasaltRock", None),
    47: ("HouseBlack", "RoofBlack", "HouseBlack", "BasaltRock", None),
    48: ("HouseBlack", "RoofBlack", "HouseBlack", "BasaltRock", None),
    # Upper village road & shoreline
    15: ("HouseRed", "RoofBlack", "HouseWhite", "HouseGrey", None),
    14: ("HouseWhite", "RoofBlack", "HouseWhite", "HouseGrey", None),
    13: ("HouseRed", "RoofBlack", "HouseWhite", "HouseGrey", None),
    49: ("HouseRed", "RoofBlack", "HouseWhite", "HouseGrey", None),
    70: ("HouseWhite", "RoofBlack", "HouseWhite", "HouseGrey", None),
    12: ("HouseWhite", "RoofBlack", "HouseWhite", "HouseGrey", None),
    11: ("HouseWhite", "RoofBlack", "HouseWhite", "HouseGrey", None),
    71: ("HouseBlack", "RoofBlack", "HouseBlack", "BasaltRock", None),
    50: ("HouseBlack", "RoofBlack", "HouseBlack", "BasaltRock", None),
    # Northern village & harbour
    51: ("HouseBlack", "RoofBlack", "HouseBlack", "BasaltRock", None),
    10: ("HouseYellow", "RoofBlack", "HouseWhite", "HouseGrey", None),
    74: ("HouseTeal", "RoofBlack", "HouseWhite", "PortalConcrete", None),
    107: ("HouseWhite", "RoofGrey", "HouseWhite", "PortalConcrete", None),
}
# Buildings where the real-world roof ridge runs along the shorter side of the nearly-square footprint
RIDGE_SWAP_INDICES = {22, 28}

walls_res = ["HouseWhite", "HouseRed", "HouseBlack", "HouseWhite", "HouseBlack", "HouseTimber"]
roofs_res = ["RoofBlack", "RoofBlack", "RoofTurf", "RoofBlack", "RoofGrey"]
b_count = 0
for idx, b in enumerate(osm["buildings"]):
    if b["kind"] == "construction":
        continue
    if b["kind"] == "church":
        house(b["x"], b["z"], b["yaw"], max(b["w"], 12.6), max(b["d"], 8.4), 4.6, 3.1,
              "HouseWhite", "RoofRed", windows=True, is_church=True, b_idx=idx, kind="church")
        b_count += 1
        continue
    w = max(b["w"], 3.5)
    d = max(b["d"], 3.5)
    yaw = b["yaw"]
    if d > w:
        w, d = d, w
        yaw += math.pi * 0.5
    if idx in RIDGE_SWAP_INDICES:
        w, d = d, w
        yaw += math.pi * 0.5
    if b["z"] < 120.0:
        wall = "HouseBlack"
        roof = "RoofGrey" if idx % 2 == 0 else "RoofBlack"
        h = 2.1
        rh = 1.1
    elif b["kind"] in ("industrial", "commercial"):
        wall = "HouseWhite" if idx % 3 == 0 else "HouseBlack"
        roof = "RoofGrey" if idx % 2 == 0 else "RoofBlack"
        h = rng.uniform(4.2, 5.8)
        rh = rng.uniform(1.6, 2.3)
    elif b["kind"] == "boathouse":
        wall = "HouseBlack" if idx % 3 != 0 else "HouseTimber"
        roof = "RoofTurf" if idx % 2 == 0 else "RoofBlack"
        h = rng.uniform(2.5, 3.2)
        rh = rng.uniform(1.6, 2.2)
    else:
        wall = walls_res[idx % len(walls_res)]
        roof = "RoofRed" if b.get("roof_color") == "red" else roofs_res[idx % len(roofs_res)]
        h = rng.uniform(3.2, 4.8) if max(w, d) > 8.5 else rng.uniform(2.4, 3.3)
        rh = rng.uniform(1.7, 2.5)

    trim_ov = None
    plinth_ov = None
    if idx in REAL_VILLAGE_HOUSES:
        wall, roof, trim_ov, plinth_ov, h_ov = REAL_VILLAGE_HOUSES[idx]
        if h_ov is not None:
            h, rh = h_ov

    if house(b["x"], b["z"], yaw, w, d, h, rh, wall, roof, windows=(max(w, d) >= 7.0),
             b_idx=idx, kind=b["kind"], trim_override=trim_ov, plinth_override=plinth_ov):
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

with open(os.path.join(OUT, "VillageHouses.json"), "w", encoding="utf-8") as fh:
    json.dump({"houses": HOUSE_RECORDS}, fh, indent=1)
print("VillageHouses.json:", len(HOUSE_RECORDS), "houses,", sum(len(r["doors"]) for r in HOUSE_RECORDS), "doors,",
      "village faces:", len(village.f))
if HOUSES_ONLY:
    sys.exit(0)

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
