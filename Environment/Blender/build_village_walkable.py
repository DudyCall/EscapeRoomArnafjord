"""
Where the players may walk in the playable Árnafjørður village scene (Arnafjordur_Village.unity):
the church driveway and parking bay, the road down over the Stórá bridge and through the village,
three footpaths, the grass between the houses below the street, and the beach at the head of the fjord. Everything else (the mountain road, the
slopes, the sea) is left out, so the players cannot get there.

Reads the terrain and road straight from TunnelExit.blend so the result always matches the
world the scenes use, and writes VillageWalkable.json for Tools/Village/Rebuild Village Scene.

Run: blender --background TunnelExit.blend --python build_village_walkable.py -- <Assets/Environment/Village dir>
"""
import bpy
import json
import math
import os
import sys
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
ARGV = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
OUT = os.path.abspath(ARGV[0] if ARGV else os.path.join(HERE, "out_village"))
os.makedirs(OUT, exist_ok=True)

SEA = -89.2

# ---- tunables (metres)
ROAD_HALF_WIDTH = 1.45        # keeps the players' feet on the 1.7 m half-width road strip (it is a deck over the Stórá)
DRIVEWAY_HALF_WIDTH = 1.5
BAY_HALF_WIDTH = 2.75
PATH_HALF_WIDTH = 0.95        # walkable part of a footpath
PATH_RIBBON_HALF_WIDTH = 1.35  # the gravel that is drawn
UPHILL_STUB = 9.0             # how far up the mountain road from the church turn-off the players may go
BEACH_MIN_H, BEACH_MAX_H = 0.22, 4.3   # metres above sea level that count as beach
BEACH_X_MIN, BEACH_Z_MIN, BEACH_Z_MAX = 416.0, 309.0, 403.0
BEACH_HOUSE_CLEARANCE = 1.6
BEACH_RIVER_CLEARANCE = 6.5
CELL = 0.5
GRASS_X_MIN = 375.0            # the grass slope between the village street and the beach
GRASS_HOUSE_CLEARANCE = 0.5    # houses have colliders; this only keeps the outline off their walls
GRASS_ABOVE_ROAD = 0.4         # seaward ground up to this much above the street (the Unity builder banks it up to the road edge)


# ------------------------------------------------------------------ meshes from the open .blend, in Unity coordinates
def mesh_tris(name, box):
    ob = bpy.data.objects[name]
    me = ob.data
    co = np.zeros(len(me.vertices) * 3)
    me.vertices.foreach_get("co", co)
    co = co.reshape(-1, 3)
    M = np.array(ob.matrix_world)
    co = co @ M[:3, :3].T + M[:3, 3]
    v = np.stack([-co[:, 0], co[:, 2], -co[:, 1]], 1)       # Blender (-x, -z, y) -> Unity (x, y, z)
    me.calc_loop_triangles()
    t = np.zeros(len(me.loop_triangles) * 3, dtype=np.int32)
    me.loop_triangles.foreach_get("vertices", t)
    t = t.reshape(-1, 3)
    c = v[t].mean(1)
    keep = (c[:, 0] > box[0]) & (c[:, 0] < box[1]) & (c[:, 2] > box[2]) & (c[:, 2] < box[3])
    return v, t[keep]


def height_sampler(v, t):
    A, B, C = v[t[:, 0]], v[t[:, 1]], v[t[:, 2]]
    den = (B[:, 2] - C[:, 2]) * (A[:, 0] - C[:, 0]) + (C[:, 0] - B[:, 0]) * (A[:, 2] - C[:, 2])
    ok = np.abs(den) > 1e-9
    den = np.where(ok, den, 1.0)

    def sample(x, z):
        l1 = ((B[:, 2] - C[:, 2]) * (x - C[:, 0]) + (C[:, 0] - B[:, 0]) * (z - C[:, 2])) / den
        l2 = ((C[:, 2] - A[:, 2]) * (x - C[:, 0]) + (A[:, 0] - C[:, 0]) * (z - C[:, 2])) / den
        l3 = 1.0 - l1 - l2
        inside = ok & (l1 >= -1e-6) & (l2 >= -1e-6) & (l3 >= -1e-6)
        if not inside.any():
            return float("nan")
        return float((l1 * A[:, 1] + l2 * B[:, 1] + l3 * C[:, 1])[inside].max())

    return sample


BOX = (330.0, 560.0, 220.0, 520.0)
terrain_y = height_sampler(*mesh_tris("Exit_Terrain", BOX))
road_y = height_sampler(*mesh_tris("Exit_Road", BOX))

with open(os.path.join(HERE, "arnafjordur_osm.json"), "r", encoding="utf-8") as fh:
    osm = json.load(fh)


def house_footprints():
    """Footprints of the village houses, sized as build_tunnel_exit.py builds them: (x, z, yaw, half width, half depth)."""
    out = []
    for b in osm["buildings"]:
        if b["kind"] == "construction" or not (BOX[0] < b["x"] < BOX[1] and BOX[2] < b["z"] < BOX[3]):
            continue
        if b["kind"] == "church":
            w, d = max(b["w"], 12.6), max(b["d"], 8.4)
        else:
            w, d = max(b["w"], 3.5), max(b["d"], 3.5)
        out.append((b["x"], b["z"], b["yaw"], w * 0.5, d * 0.5))
    return out


HOUSES = house_footprints()


def house_distance(x, z):
    best = 1e9
    for cx, cz, yaw, hw, hd in HOUSES:
        c, s = math.cos(yaw), math.sin(yaw)
        dx, dz = x - cx, z - cz
        lx, lz = dx * c - dz * s, dx * s + dz * c
        best = min(best, math.hypot(max(abs(lx) - hw, 0.0), max(abs(lz) - hd, 0.0)))
    return best


def seg_distance(px, pz, ax, az, bx, bz):
    dx, dz = bx - ax, bz - az
    ln2 = dx * dx + dz * dz
    t = 0.0 if ln2 < 1e-12 else min(1.0, max(0.0, ((px - ax) * dx + (pz - az) * dz) / ln2))
    return math.hypot(px - (ax + dx * t), pz - (az + dz * t))


def line_distance(px, pz, line):
    return min(seg_distance(px, pz, a[0], a[1], b[0], b[1]) for a, b in zip(line[:-1], line[1:]))


RIVER = [p for p in osm["stora_river"] if 360.0 < p[0] < 490.0]


def densify(pts, step):
    out = []
    for a, b in zip(pts[:-1], pts[1:]):
        n = max(1, int(math.ceil(math.hypot(b[0] - a[0], b[1] - a[1]) / step)))
        for k in range(n):
            out.append((a[0] + (b[0] - a[0]) * k / n, a[1] + (b[1] - a[1]) * k / n))
    out.append(tuple(pts[-1]))
    return out


def with_height(pts, sampler):
    out = []
    for x, z in pts:
        y = sampler(x, z)
        if math.isnan(y):
            y = terrain_y(x, z)
        out.append({"x": round(x, 3), "y": round(y, 3), "z": round(z, 3)})
    return out


# ------------------------------------------------------------------ 1. church driveway + parking bay (the forecourt the car drove in on)
with open(os.path.join(OUT, "..", "ChurchArrival", "ChurchArrivalPath.json"), "r", encoding="utf-8") as fh:
    arrival = json.load(fh)
apts = [(p["x"], p["z"], p["y"]) for p in arrival["points"]]
acc = [0.0]
for a, b in zip(apts[:-1], apts[1:]):
    acc.append(acc[-1] + math.hypot(b[0] - a[0], b[1] - a[1]))
STOP = arrival["stopDistance"]
PARK = arrival["parkPosition"]


def arrival_span(d0, d1):
    return [{"x": round(x, 3), "y": round(y, 3), "z": round(z, 3)} for (x, z, y), d in zip(apts, acc) if d0 <= d <= d1]


# The forecourt ribbon runs from STOP-28 to STOP+2.5 and flares into the bay over its second half.
driveway = arrival_span(STOP - 27.0, STOP - 6.0)
bay = arrival_span(STOP - 9.5, STOP - 0.4)

# ------------------------------------------------------------------ 2. the road: church turn-off -> Stórá bridge -> village street
vr = osm["village_roads"]
vr0 = list(reversed(vr[0]))                 # mountain -> bridge
turnoff = next(i for i, p in enumerate(vr0) if abs(p[0] - 417.24) < 0.01)
up = vr0[turnoff - 1]
ln = math.hypot(up[0] - vr0[turnoff][0], up[1] - vr0[turnoff][1])
stub = (vr0[turnoff][0] + (up[0] - vr0[turnoff][0]) * UPHILL_STUB / ln,
        vr0[turnoff][1] + (up[1] - vr0[turnoff][1]) * UPHILL_STUB / ln)
road_line = [stub] + [tuple(p) for p in vr0[turnoff:]] + [tuple(vr[1][0])] + [tuple(p) for p in vr[2][1:]] \
    + [tuple(p) for p in vr[3][1:-1]]
road = densify(road_line, 2.5)

# ------------------------------------------------------------------ 3. footpaths (drawn as gravel ribbons by the Unity scene builder)
footpaths = {
    # from the parking bay straight down the bank to the road, so nobody has to walk round by the turn-off
    "ChurchPath": [(PARK["x"] + 0.9, PARK["z"] + 2.3), (400.6, 286.6), (402.0, 290.4), (402.75, 293.55)],
    # north bank of the Stórá, between the river and the first houses, from the bridge down to the beach
    "BeachPathSouth": [(395.45, 315.2), (400.5, 315.6), (408.0, 315.3), (416.5, 314.6), (423.5, 315.4), (430.0, 317.5)],
    # further along the street, a second way down between the houses
    "BeachPathNorth": [(400.75, 399.2), (406.5, 399.4), (413.0, 398.6), (419.0, 396.5), (424.0, 393.5)],
}
for name, pts in footpaths.items():
    for x, z in densify(pts, 1.0):
        d = house_distance(x, z)
        if d < PATH_RIBBON_HALF_WIDTH + 0.4:
            print(f"WARNING {name}: only {d:.2f} m from a house at ({x:.1f}, {z:.1f})")

# ------------------------------------------------------------------ 4. the beach: low ground between the houses and the water
xs = np.arange(BEACH_X_MIN, 470.0 + CELL, CELL)
zs = np.arange(BEACH_Z_MIN, BEACH_Z_MAX + CELL, CELL)
Hg = np.array([[terrain_y(x, z) - SEA for x in xs] for z in zs])
mask = (Hg >= BEACH_MIN_H) & (Hg <= BEACH_MAX_H)
gz, gx = np.gradient(Hg, CELL)
mask &= np.hypot(gx, gz) < math.tan(math.radians(32.0))
for iz, z in enumerate(zs):
    for ix, x in enumerate(xs):
        if mask[iz, ix] and (house_distance(x, z) < BEACH_HOUSE_CLEARANCE or line_distance(x, z, RIVER) < BEACH_RIVER_CLEARANCE):
            mask[iz, ix] = False

def simplify(pts, tol):
    if len(pts) < 3:
        return pts
    a, b = pts[0], pts[-1]
    worst, wi = 0.0, 0
    for i in range(1, len(pts) - 1):
        d = seg_distance(pts[i][0], pts[i][1], a[0], a[1], b[0], b[1])
        if d > worst:
            worst, wi = d, i
    if worst <= tol:
        return [a, b]
    return simplify(pts[:wi + 1], tol)[:-1] + simplify(pts[wi:], tol)


def zone_outline(mask, xs, zs, seed_xz):
    """Connected piece of mask around seed_xz, holes filled, as a simplified outline loop."""
    seed = (int(round((seed_xz[1] - zs[0]) / CELL)), int(round((seed_xz[0] - xs[0]) / CELL)))
    if not mask[seed]:
        raise SystemExit(f"zone seed {seed_xz} is not inside its mask")
    comp = np.zeros_like(mask)
    stack = [seed]
    comp[seed] = True
    while stack:
        iz, ix = stack.pop()
        for dz, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            jz, jx = iz + dz, ix + dx
            if 0 <= jz < mask.shape[0] and 0 <= jx < mask.shape[1] and mask[jz, jx] and not comp[jz, jx]:
                comp[jz, jx] = True
                stack.append((jz, jx))
    # fill holes so the outline is one loop
    pad = np.pad(comp, 1)
    seen = np.zeros_like(pad)
    stack = [(0, 0)]
    seen[0, 0] = True
    while stack:
        iz, ix = stack.pop()
        for dz, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            jz, jx = iz + dz, ix + dx
            if 0 <= jz < pad.shape[0] and 0 <= jx < pad.shape[1] and not pad[jz, jx] and not seen[jz, jx]:
                seen[jz, jx] = True
                stack.append((jz, jx))
    comp = ~seen[1:-1, 1:-1]
    # trace the outline: directed cell edges with the zone on the left, chained into one loop
    edges = {}
    for iz in range(comp.shape[0]):
        for ix in range(comp.shape[1]):
            if not comp[iz, ix]:
                continue
            if iz == 0 or not comp[iz - 1, ix]:
                edges[(ix, iz)] = (ix + 1, iz)
            if ix == comp.shape[1] - 1 or not comp[iz, ix + 1]:
                edges[(ix + 1, iz)] = (ix + 1, iz + 1)
            if iz == comp.shape[0] - 1 or not comp[iz + 1, ix]:
                edges[(ix + 1, iz + 1)] = (ix, iz + 1)
            if ix == 0 or not comp[iz, ix - 1]:
                edges[(ix, iz + 1)] = (ix, iz)
    start = next(iter(edges))
    loop = [start]
    cur = edges[start]
    while cur != start:
        loop.append(cur)
        cur = edges[cur]
    outline = [(xs[0] - CELL * 0.5 + ix * CELL, zs[0] - CELL * 0.5 + iz * CELL) for ix, iz in loop]
    far = max(range(len(outline)), key=lambda i: math.hypot(outline[i][0] - outline[0][0], outline[i][1] - outline[0][1]))
    pts = simplify(outline[:far + 1], 0.45)[:-1] + simplify(outline[far:] + [outline[0]], 0.45)[:-1]
    return pts, 0.5 * abs(sum(a[0] * b[1] - b[0] * a[1] for a, b in zip(pts, pts[1:] + pts[:1])))


# keep the piece the south footpath arrives on
beach, area = zone_outline(mask, xs, zs, (430.0, 317.5))

# ------------------------------------------------------------------ 5. the grass between the houses below the street, down to the beach
road_pts = np.array([(x, z, road_y(x, z)) for x, z in densify(road_line, 1.0)])
gxs = np.arange(GRASS_X_MIN, 470.0 + CELL, CELL)
Gg = np.array([[terrain_y(x, z) - SEA for x in gxs] for z in zs])
gmask = Gg >= BEACH_MIN_H
ggz, ggx = np.gradient(Gg, CELL)
gmask &= np.hypot(ggx, ggz) < math.tan(math.radians(32.0))
for iz, z in enumerate(zs):
    for ix, x in enumerate(gxs):
        if not gmask[iz, ix]:
            continue
        k = int(np.argmin((road_pts[:, 0] - x) ** 2 + (road_pts[:, 1] - z) ** 2))
        a, b = road_pts[max(k - 1, 0)], road_pts[min(k + 1, len(road_pts) - 1)]
        seaward = (b[1] - a[1]) * (x - a[0]) - (b[0] - a[0]) * (z - a[1]) > 0.0   # right of the street, heading north
        if (not seaward or Gg[iz, ix] + SEA > road_pts[k, 2] + GRASS_ABOVE_ROAD or house_distance(x, z) < GRASS_HOUSE_CLEARANCE
                or line_distance(x, z, RIVER) < BEACH_RIVER_CLEARANCE):
            gmask[iz, ix] = False
grass, grass_area = zone_outline(gmask, gxs, zs, (430.0, 317.5))

data = {
    "seaLevel": SEA,
    "corridors": [
        {"name": "ChurchDriveway", "halfWidth": DRIVEWAY_HALF_WIDTH, "points": driveway},
        {"name": "ParkingBay", "halfWidth": BAY_HALF_WIDTH, "points": bay},
        {"name": "VillageRoad", "halfWidth": ROAD_HALF_WIDTH, "points": with_height(road, road_y)},
    ],
    "footpaths": [
        {"name": name, "halfWidth": PATH_HALF_WIDTH, "ribbonHalfWidth": PATH_RIBBON_HALF_WIDTH,
         "points": with_height(densify(pts, 1.0), terrain_y)}
        for name, pts in footpaths.items()
    ],
    "zones": [
        {"name": "Beach", "outline": with_height(beach, terrain_y)},
        {"name": "VillageGrass", "outline": with_height(grass, terrain_y)},
    ],
    "houses": [{"x": round(a, 2), "z": round(b, 2), "yaw": round(c, 4), "halfWidth": round(d, 2), "halfDepth": round(e, 2)}
               for a, b, c, d, e in HOUSES],
}
out_path = os.path.join(OUT, "VillageWalkable.json")
with open(out_path, "w", encoding="utf-8") as fh:
    json.dump(data, fh, indent=1)
print(f"Village walkable: road {len(road)} pts, driveway {len(driveway)}, bay {len(bay)}, "
      f"beach {len(beach)} corners / {area:.0f} m2, grass {len(grass)} corners / {grass_area:.0f} m2, {len(HOUSES)} houses -> {out_path}")
