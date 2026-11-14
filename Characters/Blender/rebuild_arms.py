"""Rebuild the arms of an Arnafjordur character.

The source scans (Characters/Blender/<Name>.blend) have the arms fused to the
torso and the hands fused to the hips. This script:
  1. finds the groove between arm and body and cuts the mesh cleanly along it,
  2. closes the inner arm/hand and the body/hip side with new, textured surfaces,
  3. swings the arms out to the rest pose of the existing rig,
  4. skins the result to the existing armature (animations are kept as they are),
  5. saves <Name>_Rigged.blend, the albedo texture and the Unity FBX.

Run:  blender -b -P rebuild_arms.py -- <Name> [--no-export] [--debug <dir>]
Inputs are read from Characters/Blender/backup_v1 (old rig + old albedo) so the
script can be re-run safely.
"""
import bpy, bmesh, sys, os, math
import numpy as np
from mathutils import Vector, Matrix
from mathutils.bvhtree import BVHTree
from mathutils.interpolate import poly_3d_calc

argv = sys.argv[sys.argv.index("--") + 1:]
NAME = argv[0]
EXPORT = "--no-export" not in argv
DEBUG = argv[argv.index("--debug") + 1] if "--debug" in argv else None

HERE = os.path.dirname(os.path.abspath(__file__))
BACKUP = os.path.join(HERE, "backup_v1")
UNITY_CHAR = os.path.normpath(os.path.join(HERE, "..", "..", "ArnafjordurEscapeRoom", "Assets", "Characters", NAME))
SRC_BLEND = os.path.join(HERE, NAME + ".blend")
OLD_RIG_BLEND = os.path.join(BACKUP, NAME + "_Rigged.blend")
OLD_ALBEDO = os.path.join(BACKUP, NAME + "_Albedo.png")
NEW_ALBEDO = os.path.join(UNITY_CHAR, "Textures", NAME + "_Albedo.png")
OUT_BLEND = os.path.join(HERE, NAME + "_Rigged.blend")
OUT_FBX = os.path.join(UNITY_CHAR, NAME + ".fbx")

BULGE_ARM = 0.42      # depth of the new inner-arm surface, as a fraction of the cut width
BULGE_BODY = 0.10     # same for the body side
PPM = 520.0           # texels per metre for the new surfaces
PAD = 6


def smoothstep(x, a, b):
    if b <= a:
        return 1.0 if x >= a else 0.0
    t = min(1.0, max(0.0, (x - a) / (b - a)))
    return t * t * (3 - 2 * t)


# ----------------------------------------------------------------------------
# scene: old rig file as base, plus the fused source mesh
# ----------------------------------------------------------------------------
bpy.ops.wm.open_mainfile(filepath=OLD_RIG_BLEND)
rig = bpy.data.objects[NAME + "_Rig"]
old_obj = bpy.data.objects[NAME]
old_obj.name = NAME + "_old"
mat = old_obj.material_slots[0].material
with bpy.data.libraries.load(SRC_BLEND, link=False) as (src, dst):
    dst.meshes = list(src.meshes)
src_mesh = dst.meshes[0]

bones = {b.name: (rig.matrix_world @ b.head_local, rig.matrix_world @ b.tail_local) for b in rig.data.bones}

bm = bmesh.new()
bm.from_mesh(src_mesh)
bm.verts.ensure_lookup_table()
bm.faces.ensure_lookup_table()
uv_layer = bm.loops.layers.uv.verify()
cap_layer = bm.faces.layers.int.new("cap")
fside_layer = bm.faces.layers.int.new("fside")   # +1 arm side / -1 body side of the cut
bvh = BVHTree.FromBMesh(bm)

# ----------------------------------------------------------------------------
# 1. groove detection (per side, in mirrored coordinates X = side * x)
# ----------------------------------------------------------------------------
DZ, DX = 0.005, 0.002
ZS = np.arange(0.55, 1.50, DZ)
XS = np.arange(0.06, 0.50, DX)


def thickness_row(side, z):
    T = np.zeros(len(XS))
    for i, X in enumerate(XS):
        f = bvh.ray_cast(Vector((side * X, -3, z)), Vector((0, 1, 0)))
        if f[0] is None:
            continue
        b = bvh.ray_cast(Vector((side * X, 3, z)), Vector((0, -1, 0)))
        T[i] = b[0].y - f[0].y
    return T


def find_groove(side):
    J = bones[("Left" if side > 0 else "Right") + "UpperArm"][0]
    xg = np.full(len(ZS), np.nan)
    prom = np.zeros(len(ZS))
    xmax = np.zeros(len(ZS))
    for k, z in enumerate(ZS):
        T = thickness_row(side, z)
        hit = np.nonzero(T > 0.004)[0]
        if len(hit) == 0:
            continue
        imax = hit[-1]
        xmax[k] = XS[imax]
        lo = max(0, imax - int(0.07 / DX))
        ip = lo + int(np.argmax(T[lo:imax + 1]))
        run, irun, stop = T[ip], ip, None
        i = ip
        limit = max(0, imax - int(0.17 / DX))
        while i > limit:
            i -= 1
            if T[i] < run:
                run, irun = T[i], i
            elif T[i] > run + 0.012:
                stop = i
                break
        if stop is not None and irun != ip:
            xg[k] = XS[irun]
            prom[k] = min(T[ip], T[stop]) - run
    # hand bottom: biggest inward jump of the silhouette going down
    zr = [k for k, z in enumerate(ZS) if 0.62 <= z <= 1.0]
    jumps = [(xmax[k + 3] - xmax[k], k) for k in zr]
    k_hb = max(jumps)[1] + 2
    z_hb = ZS[k_hb]
    # armpit: highest z with a clear groove
    z_ap = None
    for k in range(len(ZS) - 1, 3, -1):
        z = ZS[k]
        if z > J.z - 0.04 or z < J.z - 0.22:
            continue
        if all(prom[k - j] > 0.008 for j in range(4)):
            z_ap = z
            break
    if z_ap is None:
        z_ap = J.z - 0.10
    z_ap = float(min(z_ap, J.z - 0.055))
    ok = ~np.isnan(xg) & (prom > 0.004) & (ZS >= z_hb + 0.005) & (ZS <= z_ap + 0.02)
    zz, xx = ZS[ok], xg[ok].copy()
    # median + gaussian smoothing
    med = np.array([np.median(xx[max(0, i - 4):i + 5]) for i in range(len(xx))])
    ker = np.exp(-0.5 * (np.arange(-6, 7) / 3.0) ** 2)
    ker /= ker.sum()
    padded = np.concatenate([np.full(6, med[0]), med, np.full(6, med[-1])])
    sm = np.convolve(padded, ker, mode="valid")
    print(f"[{NAME}] side {side:+d}: hand bottom z={z_hb:.3f} armpit z={z_ap:.3f} groove X {sm.min():.3f}..{sm.max():.3f} ({len(zz)} rows)")
    return dict(side=side, J=J, zz=zz, xx=sm, z_hb=float(z_hb), z_ap=z_ap)


def groove_x(g, z):
    """Cut surface X(z); above the armpit it leans towards the shoulder joint."""
    if z < g["zz"][0]:      # just below the hand the cut swings outwards so it leaves the leg alone
        return float(g["xx"][0] + max(0.0, g["z_hb"] - 0.01 - z) * 2.0)
    if z <= g["z_ap"]:
        return float(np.interp(z, g["zz"], g["xx"]))
    x_ap = float(np.interp(g["z_ap"], g["zz"], g["xx"]))
    x_top = abs(g["J"].x) - 0.02
    t = min(1.0, (z - g["z_ap"]) / max(1e-4, g["J"].z - g["z_ap"]))
    return x_ap + (x_top - x_ap) * t


grooves = {s: find_groove(s) for s in (1, -1)}

# ----------------------------------------------------------------------------
# 2. cut along the groove
# ----------------------------------------------------------------------------
preexisting_boundary = {e for e in bm.edges if e.is_boundary}
print(f"[{NAME}] source mesh: {len(bm.verts)} verts, {len(preexisting_boundary)} open edges")
side_of = {}      # vert -> +1 / -1 / 0  (which arm region it may belong to)
arm_w = {}        # vert -> rotation weight 0..1


def sdist(g, co):
    return g["side"] * co.x - groove_x(g, co.z)


def cut_side(g):
    side = g["side"]
    z_lo, z_hi = g["z_hb"] - 0.10, g["z_ap"]
    sval = {}
    for v in bm.verts:
        if side * v.co.x > 0.05 and z_lo - 0.06 < v.co.z < z_hi + 0.05:
            s = sdist(g, v.co)
            sval[v] = s if abs(s) > 1e-6 else 1e-6
    to_split = []
    for e in bm.edges:
        a, b = e.verts
        if a in sval and b in sval and sval[a] * sval[b] < 0:
            f = sval[a] / (sval[a] - sval[b])
            zc = a.co.z + (b.co.z - a.co.z) * f
            if z_lo <= zc <= z_hi:
                to_split.append((e, a, f))
    new_verts = set()
    for e, a, f in to_split:
        _, nv = bmesh.utils.edge_split(e, a, f)
        new_verts.add(nv)
    cut_edges = []
    for f in list(bm.faces):
        nvs = [v for v in f.verts if v in new_verts]
        if len(nvs) == 2:
            res = bmesh.utils.face_split(f, nvs[0], nvs[1])
            cut_edges.append(res[1].edge)
            for part in (f, res[0]):
                orig = [v for v in part.verts if v not in new_verts]
                part[fside_layer] = 1 if sval[orig[0]] > 0 else -1
    bmesh.ops.triangulate(bm, faces=[f for f in bm.faces if len(f.verts) > 3])
    cut_edges = [e for e in cut_edges if e.is_valid]
    # keep only the largest connected chain of cut edges
    comps, seen = [], set()
    for e in cut_edges:
        if e in seen:
            continue
        comp, stack = [], [e]
        seen.add(e)
        cs = set(cut_edges)
        while stack:
            c = stack.pop()
            comp.append(c)
            for v in c.verts:
                for e2 in v.link_edges:
                    if e2 in cs and e2 not in seen:
                        seen.add(e2)
                        stack.append(e2)
        comps.append(comp)
    print(f"[{NAME}] side {side:+d}: {len(to_split)} edges split, cut chains {[len(c) for c in comps]}")
    bmesh.ops.split_edges(bm, edges=[e for c in comps for e in c])


def boundary_components(g, want_arm):
    """Boundary loops/chains on one side of the cut: list of (ordered verts, edges, closed)."""
    side = g["side"]
    z_lo, z_hi = g["z_hb"] - 0.14, g["z_ap"] + 0.01
    edges = []
    for e in bm.edges:
        if not e.is_boundary or e in preexisting_boundary:
            continue
        a, b = e.verts
        if side * a.co.x < 0.05 or not (z_lo < a.co.z < z_hi and z_lo < b.co.z < z_hi):
            continue
        fs = e.link_faces[0][fside_layer]
        if fs != 0 and (fs > 0) == want_arm:
            edges.append(e)
    adj = {}
    for e in edges:
        a, b = e.verts
        adj.setdefault(a, []).append((b, e))
        adj.setdefault(b, []).append((a, e))
    out, seen = [], set()
    for start in list(adj):
        if start in seen:
            continue
        comp, stack = {start}, [start]
        while stack:
            v = stack.pop()
            for o, _ in adj[v]:
                if o not in comp:
                    comp.add(o)
                    stack.append(o)
        seen |= comp
        ends = [v for v in comp if len(adj[v]) == 1]
        assert len(ends) in (0, 2) and all(len(adj[v]) <= 2 for v in comp), "unexpected cut topology"
        closed = not ends
        first = min(ends, key=lambda v: v.co.y) if ends else max(comp, key=lambda v: v.co.z)
        order, oedges, used, cur = [first], [], set(), first
        while True:
            nxt = [(v, e) for v, e in adj[cur] if e not in used]
            if not nxt:
                break
            v, e = nxt[0]
            used.add(e)
            oedges.append(e)
            if v == first:
                break
            order.append(v)
            cur = v
        out.append((order, oedges, closed))
    return out


def refine_cap(tag, chk=()):
    """Longest-edge bisection of the fill triangles until they are about the size of the scan's."""
    for _ in range(5):
        faces = [f for f in bm.faces if f[cap_layer] == tag]
        fs = set(faces)
        long_edges = list({e for f in faces for e in f.edges
                           if all(lf in fs for lf in e.link_faces) and e.calc_length() > 0.02})
        if not long_edges:
            break
        long_edges.sort(key=lambda e: -e.calc_length())
        for e in long_edges:
            if not e.is_valid or e.calc_length() <= 0.02:
                continue
            _, nv = bmesh.utils.edge_split(e, e.verts[0], 0.5)
            for f in list(nv.link_faces):
                if len(f.verts) == 4:
                    vs = list(f.verts)
                    opp = vs[(vs.index(nv) + 2) % 4]
                    nf, _ = bmesh.utils.face_split(f, nv, opp)
                    nf[cap_layer] = tag
                    f[cap_layer] = tag
        faces = [f for f in bm.faces if f[cap_layer] == tag]
        fs = set(faces)
        inner = list({e for f in faces for e in f.edges if all(lf in fs for lf in e.link_faces)})
        bmesh.ops.beautify_fill(bm, faces=faces, edges=inner)
    assert all(v.is_valid for v in chk), "refine removed boundary verts"


caps = []   # dicts describing each new surface


def build_caps(g, tag_base):
    out = []
    bm.verts.index_update()
    lines = {}
    comps = {w: boundary_components(g, w) for w in (False, True)}
    for want_arm, tag in ((False, tag_base), (True, tag_base + 1)):
        for order, oedges, closed in comps[want_arm]:
            fill_edges = list(oedges)
            line_set = set()
            if not closed:
                key = frozenset((order[0].index, order[-1].index))
                if key not in lines:    # line across the open end, shared by the body and the arm surface
                    front, back = order[0], order[-1]
                    n = max(2, int(round((back.co - front.co).length / 0.012)))
                    line = [front] + [bm.verts.new(front.co.lerp(back.co, i / n)) for i in range(1, n)] + [back]
                    lines[key] = (line, [bm.edges.new((line[i], line[i + 1])) for i in range(n)])
                line, line_edges = lines[key]
                fill_edges += line_edges
                line_set = set(line[1:-1])
            before = set(bm.faces)
            bmesh.ops.triangle_fill(bm, use_beauty=True, use_dissolve=False, edges=fill_edges)
            newf = [f for f in bm.faces if f not in before]
            assert newf, "cap fill failed"
            sub = tag * 100 + len(out)
            for f in newf:
                f[cap_layer] = sub
                f.smooth = True
            refine_cap(sub, order)
            faces = [f for f in bm.faces if f[cap_layer] == sub]
            cverts = {v for f in faces for v in f.verts}
            chain_set = set(order)
            interior = [v for v in cverts if v not in chain_set and v not in line_set]
            zs = np.array([v.co.z for v in order])
            ys = np.array([v.co.y for v in order])
            ibot = int(np.argmin(zs))
            a1 = list(range(0, ibot + 1))
            a2 = list(range(ibot, len(order))) + ([0] if closed else [])
            if ys[a1].mean() > ys[a2].mean():
                a1, a2 = a2, a1
            fo, bo = np.argsort(zs[a1]), np.argsort(zs[a2])
            prof = dict(fz=zs[a1][fo], fy=ys[a1][fo], bz=zs[a2][bo], by=ys[a2][bo],
                        zmin=float(zs.min()), zmax=float(zs.max()))
            out.append(dict(g=g, tag=sub, arm=want_arm, chain=order, interior=interior,
                            line=line_set, prof=prof, closed=closed))
    return out


def relax(vs, iters, fac=0.5, axes=(0, 1, 2)):
    for _ in range(iters):
        new = {}
        for v in vs:
            nb = [e.other_vert(v).co for e in v.link_edges]
            c = sum(nb, Vector()) / len(nb)
            t = v.co.copy()
            for a in axes:
                t[a] += (c[a] - t[a]) * fac
            new[v] = t
        for v, co in new.items():
            v.co = co


for side in (1, -1):
    g = grooves[side]
    cut_side(g)
    caps += build_caps(g, 1 if side > 0 else 3)

bm.verts.ensure_lookup_table()
bm.faces.ensure_lookup_table()

# ----------------------------------------------------------------------------
# 3. arm rotation (arms-down scan pose -> rest pose of the rig)
# ----------------------------------------------------------------------------
is_arm = set()
for side in (1, -1):
    g = grooves[side]
    z_lo, z_hi = g["z_hb"] - 0.14, g["z_ap"]
    body_new, arm_new, line_new = set(), set(), set()
    for c in caps:
        if c["g"] is g:
            (arm_new if c["arm"] else body_new).update(c["chain"], c["interior"])
            line_new |= c["line"]
    shared = (body_new & arm_new) | line_new          # the two ends of the cut + armpit line
    cand = set()
    for v in bm.verts:
        if side * v.co.x > 0.05 and z_lo < v.co.z <= z_hi + 0.03:
            # below the armpit the cut itself separates arm from body, so verts lying on it count as arm
            if sdist(g, v.co) > (-1e-5 if v.co.z <= z_hi else 1e-4):
                cand.add(v)
    cand = (cand | arm_new) - body_new - shared
    g["shared"] = shared
    # arm = candidate verts connected to the outermost candidate
    seed = max(cand, key=lambda v: side * v.co.x - abs(v.co.z - (g["z_hb"] + 0.1)))
    comp, stack = {seed}, [seed]
    while stack:
        v = stack.pop()
        for e in v.link_edges:
            o = e.other_vert(v)
            if o in cand and o not in comp:
                comp.add(o)
                stack.append(o)
    g["arm_verts"] = comp
    is_arm |= comp

    P = "Left" if side > 0 else "Right"
    J = bones[P + "UpperArm"][0]
    H = bones[P + "Hand"][0]
    r = (H - J).length
    ring = [v.co for v in comp if abs((v.co - J).length - r) < 0.03]
    C0 = sum(ring, Vector()) / len(ring)
    d0, d1 = (C0 - J).normalized(), (H - J).normalized()
    g["R"] = d0.rotation_difference(d1).to_matrix()
    g["theta"] = d0.angle(d1)
    print(f"[{NAME}] side {side:+d}: arm verts {len(comp)}, swing {math.degrees(g['theta']):.1f} deg")

# cap shapes (in the scan pose) + UV parametrisation
for c in caps:
    g, prof, side = c["g"], c["prof"], c["g"]["side"]
    relax(c["interior"], 6, 0.5, axes=(1, 2))
    tan_t = math.tan(g["theta"])
    b = BULGE_ARM if c["arm"] else BULGE_BODY
    direction = -1.0 if c["arm"] else 1.0
    c["uvpos"] = {}
    for v in list(c["interior"]) + list(c["chain"]) + list(c["line"]):
        c["uvpos"][v] = (v.co.y, v.co.z)
    for v in c["interior"]:
        z = min(max(v.co.z, prof["zmin"]), prof["zmax"])
        yf = float(np.interp(z, prof["fz"], prof["fy"]))
        yb = float(np.interp(z, prof["bz"], prof["by"]))
        chord = max(1e-4, yb - yf)
        t = min(1.0, max(0.0, (v.co.y - yf) / chord))
        sep = max(0.0, (g["z_ap"] - v.co.z)) * tan_t + 0.002
        depth = min(b * chord, 0.46 * sep)
        # fade the bulge out near the bottom of the cut so it closes smoothly
        depth *= smoothstep(v.co.z, prof["zmin"], prof["zmin"] + 0.02) * 0.6 + 0.4
        if c["closed"]:
            depth *= smoothstep(prof["zmax"] - v.co.z, 0.0, 0.02) * 0.6 + 0.4
        X = groove_x(g, v.co.z) + direction * depth * math.sqrt(max(0.0, 4 * t * (1 - t)))
        v.co.x = side * X

# rotation weights
bm.normal_update()
scan_co = {v: v.co.copy() for v in bm.verts}
scan_vn = {v: v.normal.copy() for v in bm.verts}
bm.verts.index_update()


def fkey(f):
    return tuple(sorted(v.index for v in f.verts))


scan_fn = {fkey(f): f.normal.copy() for f in bm.faces}
for v in bm.verts:
    arm_w[v] = 0.0
for side in (1, -1):
    g = grooves[side]
    J = g["J"]
    for v in bm.verts:
        if side * v.co.x < 0.03:
            continue
        z = v.co.z
        if v in g["arm_verts"]:
            base = 1.0
        elif z <= g["z_ap"]:
            base = 0.0
        else:
            base = None
        soft = smoothstep(z, g["z_ap"] - 0.05, g["z_ap"] + 0.03)   # 0 below armpit, 1 above
        a = 0.055
        blend = smoothstep(sdist(g, v.co), -a, a) if z > g["z_ap"] - 0.06 and z < J.z + 0.12 else 0.0
        if base is None:
            w = blend
        else:
            w = base * (1 - soft) + blend * soft
        arm_w[v] = max(arm_w[v], w)
    for v in g["shared"]:
        arm_w[v] = 0.5
# harmonic interpolation of the weight over the new surfaces near the armpit
for c in caps:
    for _ in range(40):
        for v in c["interior"]:
            if v.co.z > c["g"]["z_ap"] - 0.07:
                nb = [arm_w[e.other_vert(v)] for e in v.link_edges]
                arm_w[v] = sum(nb) / len(nb)

for side in (1, -1):
    g = grooves[side]
    J, R = g["J"], g["R"]
    for v in bm.verts:
        w = arm_w[v]
        if w > 0 and side * v.co.x > 0:
            target = J + R @ (v.co - J)
            v.co = v.co.lerp(target, w)

# smooth the seams: on the body the leftover rim of the arm/hand is flattened away,
# on the arm the edge of the old groove is softened
def grow(start, n, allowed):
    region, frontier = set(start), set(start)
    for _ in range(n):
        nxt = set()
        for v in frontier:
            for e in v.link_edges:
                o = e.other_vert(v)
                if o not in region and allowed(o):
                    nxt.add(o)
        region |= nxt
        frontier = nxt
    return region


cap_new = set()
for c in caps:
    cap_new |= set(c["interior"]) | c["line"]
for c in caps:
    chain = set(c["chain"])
    if c["arm"]:
        region = grow(chain, 3, lambda v: v in is_arm and v not in cap_new) | set(c["interior"])
        relax(list(region), 20, 0.5)
    else:
        region = grow(chain, 5, lambda v: v not in is_arm and v not in cap_new) | set(c["interior"])
        relax(list(region), 90, 0.5)
        # the rim keeps the old hand/shadow colours: give it the new surface's texture too
        for f in bm.faces:
            if not f[cap_layer] and all(v in region for v in f.verts):
                f[cap_layer] = c["tag"]
    c["region"] = region

bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
for f in bm.faces:
    f.smooth = True

# ----------------------------------------------------------------------------
# 4. texture: the new surfaces, and the old surface that faced the gap between arm and
#    body (the scan has no usable colour there), get a colour that follows the clean
#    surface next to them at the same height
# ----------------------------------------------------------------------------
img = bpy.data.images.load(OLD_ALBEDO, check_existing=False)
W, Hh = img.size
px = np.empty(W * Hh * 4, dtype=np.float32)
img.pixels.foreach_get(px)
px = px.reshape(Hh, W, 4)
paint_layer = bm.faces.layers.int.new("paint")


def sample(uv):
    x = min(W - 1, max(0, int(uv.x * W)))
    y = min(Hh - 1, max(0, int(uv.y * Hh)))
    return px[max(0, y - 1):y + 2, max(0, x - 1):x + 2, :3].reshape(-1, 3).mean(axis=0)


def is_skin(c):
    return c[0] > 0.25 and c[0] > c[1] * 1.08 and c[1] > c[2] * 1.02 and c[0] - c[2] > 0.07


groups = []
for gi_, side in enumerate((1, -1)):
    g = grooves[side]
    tags = {False: set(), True: set()}
    zc = []
    for c in caps:
        if c["g"] is g:
            tags[c["arm"]].add(c["tag"])
            zc += [scan_co[v].z for v in c["chain"]]
    z0, z1 = min(zc) - 0.03, g["z_ap"] + 0.03
    sv = {}
    for v in bm.verts:
        if v in scan_co and side * scan_co[v].x > 0.03 and z0 - 0.2 < scan_co[v].z < z1 + 0.2:
            sv[v] = sdist(g, scan_co[v])
    for arm in (False, True):
        pid = gi_ * 2 + (2 if arm else 1)
        faces = []
        for f in bm.faces:
            if f[cap_layer] in tags[arm]:
                faces.append(f)
                continue
            if f[cap_layer] or fkey(f) not in scan_fn or not all(v in sv for v in f.verts):
                continue
            zf = sum(scan_co[v].z for v in f.verts) / 3
            if not (z0 < zf < z1):
                continue
            nx = scan_fn[fkey(f)].x * side
            if arm:
                if all(v in g["arm_verts"] for v in f.verts) and nx < -0.3 and min(sv[v] for v in f.verts) < 0.035:
                    faces.append(f)
            else:
                if not any(v in is_arm for v in f.verts) and nx > 0.45 and max(sv[v] for v in f.verts) > -0.04:
                    faces.append(f)
        if arm:
            # hands: parts the scan never saw got clothing colours; repaint whatever is not skin
            front = []
            for v in g["arm_verts"]:
                if v in cap_new or v not in sv or sv[v] < 0.012:
                    continue
                loop = next((l for l in v.link_loops if scan_fn.get(fkey(l.face), Vector((0, 0, 0))).y < -0.6), None)
                if loop is not None:
                    front.append((scan_co[v].z, is_skin(sample(loop[uv_layer].uv))))
            if sum(1 for _, sk in front if sk) >= 8:
                zs_ = sorted({round(z, 2) for z, _ in front})
                z_cuff = max(zs_, key=lambda t: sum(1 for z, sk in front if (z < t) == sk))
                chosen = set(faces)
                extra = 0
                for f in bm.faces:
                    if f in chosen or f[cap_layer] or not all(v in g["arm_verts"] for v in f.verts):
                        continue
                    if max(scan_co[v].z for v in f.verts) < z_cuff - 0.005:
                        col = sum(sample(l[uv_layer].uv) for l in f.loops) / 3
                        if not is_skin(col):
                            faces.append(f)
                            extra += 1
                print(f"[{NAME}] side {side:+d}: cuff at z={z_cuff:.2f}, {extra} off-colour hand faces repainted")
        for f in faces:
            f[paint_layer] = pid
        pverts = {v for f in faces for v in f.verts}
        # clean reference surface
        ref = []
        for v, sd in sv.items():
            if v in pverts or v in cap_new or v not in scan_vn:
                continue
            n = scan_vn[v]
            if arm:
                ok = v in g["arm_verts"] and (n.x * side > 0.2 or abs(n.y) > 0.75) and sd > 0.015
            else:
                ok = v not in is_arm and abs(n.y) > 0.5 and -0.10 < sd < -0.015
            if ok:
                ref.append(v)
        groups.append(dict(g=g, arm=arm, pid=pid, faces=faces, verts=pverts, ref=ref, z0=z0, z1=z1))

occ = np.zeros((Hh, W), dtype=bool)
for f in bm.faces:
    if f[paint_layer]:
        continue
    uv = [l[uv_layer].uv for l in f.loops]
    xs = [u.x * W for u in uv]
    ys = [u.y * Hh for u in uv]
    x0, x1 = max(0, int(min(xs)) - 4), min(W, int(max(xs)) + 5)
    y0, y1 = max(0, int(min(ys)) - 4), min(Hh, int(max(ys)) + 5)
    occ[y0:y1, x0:x1] = True     # conservative: bounding box of every UV triangle


def find_free(w, h):
    integ = np.zeros((Hh + 1, W + 1), dtype=np.int64)
    integ[1:, 1:] = occ.cumsum(0).cumsum(1)
    for y in range(2, Hh - h - 2, 4):
        s_ = integ[y + h, w:] - integ[y, w:] - integ[y + h, :-w] + integ[y, :-w]
        free = np.nonzero(s_[2:W - w - 2] == 0)[0]
        if len(free):
            return int(free[0]) + 2, y
    return None


# sample the reference colours before anything is painted
for gr in groups:
    cols = {}
    for v in gr["ref"]:
        loop = next((l for l in v.link_loops if not l.face[paint_layer]), None)
        if loop is not None:
            cols[v] = sample(loop[uv_layer].uv)
    gr["cols"] = cols

def fit_front_projection(gr):
    """Find where the scan's front-view texture next to the repainted area lives in the atlas.

    Returns a list of texture islands, each an affine map (x, z) -> uv plus, per height,
    the x-range of clean surface that can be cloned from.
    """
    g, side = gr["g"], gr["g"]["side"]
    P, U = [], []
    for v in bm.verts:
        if v not in scan_co or v in cap_new or side * scan_co[v].x < 0.03:
            continue
        co = scan_co[v]
        if not (gr["z0"] < co.z < gr["z1"]):
            continue
        sd = sdist(g, co)
        if gr["arm"]:
            if v not in g["arm_verts"] or not (0.012 < sd < 0.065):
                continue
        elif v in is_arm or not (-0.10 < sd < -0.02):
            continue
        loop = next((l for l in v.link_loops
                     if not l.face[paint_layer] and scan_fn.get(fkey(l.face), Vector((0, 0, 0))).y < -0.6), None)
        if loop is not None:
            P.append((co.x, co.z, 1.0))
            U.append(loop[uv_layer].uv[:])
    islands = []
    if len(P) < 30:
        return islands
    P, U = np.array(P), np.array(U)
    left = np.ones(len(P), dtype=bool)
    rng = np.random.default_rng(11)
    while left.sum() >= 15 and len(islands) < 4:
        idx = np.nonzero(left)[0]
        best = None
        for _ in range(400):
            i0 = rng.choice(idx)
            near = idx[np.argsort(np.linalg.norm(P[idx, :2] - P[i0, :2], axis=1))[:8]]
            tri_ = rng.choice(near, 3, replace=False)
            try:
                A = np.linalg.solve(P[tri_], U[tri_])
            except np.linalg.LinAlgError:
                continue
            inl = left & (np.linalg.norm(P @ A - U, axis=1) * W < 4.0)
            if best is None or inl.sum() > best.sum():
                best = inl
        if best is None or best.sum() < 12:
            break
        for _ in range(3):
            A, *_ = np.linalg.lstsq(P[best], U[best], rcond=None)
            best = left & (np.linalg.norm(P @ A - U, axis=1) * W < 4.0)
        if best.sum() < 12:
            break
        zc, lo, hi = [], [], []
        X, Z = side * P[best, 0], P[best, 1]
        for zb in np.arange(gr["z0"], gr["z1"], 0.02):
            m = (Z >= zb) & (Z < zb + 0.02)
            if m.sum() >= 3:
                a_, b_ = np.percentile(X[m], [15, 85])
                if b_ - a_ > 0.012:
                    zc.append(zb + 0.01)
                    lo.append(a_)
                    hi.append(b_)
        if len(zc) >= 2:
            sm = lambda arr: np.convolve(np.pad(arr, 1, mode="edge"), [0.25, 0.5, 0.25], mode="valid")
            islands.append(dict(A=A, zc=np.array(zc), lo=sm(np.array(lo)), hi=sm(np.array(hi)), n=int(best.sum()), Z=Z.copy()))
        left &= ~best
    print(f"[{NAME}] front-view texture {'arm ' if gr['arm'] else 'body'} side {side:+d}: {len(P)} reference points, "
          f"islands {[(i['n'], round(float(i['zc'][0]), 2), round(float(i['zc'][-1]), 2)) for i in islands]}")
    return islands


def tri(t, w):
    t = t % (2 * w)
    return (t if t <= w else 2 * w - t) / w


for gr in groups:
    gr["islands"] = fit_front_projection(gr) if gr["faces"] else []

for gr in groups:
    if not gr["faces"]:
        continue
    if gr["islands"]:
        # clone the real texture of the neighbouring front surface at the same height
        side, isl = gr["g"]["side"], gr["islands"]
        y_min = min(scan_co[v].y for v in gr["verts"])
        # per 2 cm of height: the island that has the most clean surface there
        zb = np.arange(gr["z0"] - 0.1, gr["z1"] + 0.1, 0.02)
        owner = np.full(len(zb), -1)
        for k, z_ in enumerate(zb):
            cnt = [int(((i["Z"] >= z_) & (i["Z"] < z_ + 0.02)).sum()) for i in isl]
            if max(cnt) >= 3:
                owner[k] = int(np.argmax(cnt))
        have = np.nonzero(owner >= 0)[0]
        for f in gr["faces"]:
            zf = sum(scan_co[v].z for v in f.verts) / 3
            k = int(np.clip((zf - zb[0]) / 0.02, 0, len(zb) - 1))
            it = isl[owner[have[np.argmin(np.abs(have - k))]]] if len(have) else isl[0]
            for l in f.loops:
                co = scan_co[l.vert]
                z = min(max(co.z, it["zc"][0] - 0.01), it["zc"][-1] + 0.01)
                lo = float(np.interp(z, it["zc"], it["lo"]))
                hi = float(np.interp(z, it["zc"], it["hi"]))
                X = lo + (hi - lo) * tri(co.y - y_min, 0.05)
                uv = np.array((side * X, z, 1.0)) @ it["A"]
                l[uv_layer].uv = (float(uv[0]), float(uv[1]))
        print(f"[{NAME}] repaint {'arm ' if gr['arm'] else 'body'} side {side:+d}: {len(gr['faces'])} faces cloned from the front view")
        continue
    ys = [scan_co[v].y for v in gr["verts"]]
    zs = [scan_co[v].z for v in gr["verts"]]
    ymin, ymax, zmin, zmax = min(ys), max(ys), min(zs), max(zs)
    ppm = PPM
    while True:
        w = int((ymax - ymin) * ppm) + 2 * PAD + 2
        h = int((zmax - zmin) * ppm) + 2 * PAD + 2
        pos = find_free(w, h)
        if pos or ppm < 120:
            break
        ppm *= 0.8
    assert pos, "no free texture space"
    x0, y0 = pos
    occ[y0:y0 + h, x0:x0 + w] = True
    zbins = np.arange(zmin, zmax + 0.01, 0.01)
    acc = [[] for _ in zbins]
    for v, col in gr["cols"].items():
        k = int(round((scan_co[v].z - zmin) / 0.01))
        if 0 <= k < len(acc):
            acc[k].append(col)
    ramp = np.full((len(zbins), 3), np.nan)
    for k, a in enumerate(acc):
        if a:
            a = np.array(a)
            lum = a.sum(axis=1)
            ramp[k] = a[lum >= np.median(lum)].mean(axis=0)     # brighter half: skips baked shadow
    good = ~np.isnan(ramp[:, 0])
    assert good.any(), "no colour samples"
    for ch in range(3):
        ramp[:, ch] = np.interp(np.arange(len(zbins)), np.nonzero(good)[0], ramp[good, ch])
    for _ in range(2):
        sm = ramp.copy()
        sm[1:-1] = 0.25 * ramp[:-2] + 0.5 * ramp[1:-1] + 0.25 * ramp[2:]
        ramp = sm
    rng = np.random.default_rng(7 + gr["pid"])
    for r in range(h):
        k = (r - PAD - 1) / ppm / 0.01
        col = np.array([np.interp(k, np.arange(len(zbins)), ramp[:, ch]) for ch in range(3)])
        noise = 1.0 + (rng.random((w, 1)) - 0.5) * 0.05
        px[y0 + r, x0:x0 + w, :3] = np.clip(col[None, :] * noise, 0, 1)
        px[y0 + r, x0:x0 + w, 3] = 1.0
    for f in gr["faces"]:
        for l in f.loops:
            co = scan_co[l.vert]
            l[uv_layer].uv = ((x0 + PAD + 1 + (co.y - ymin) * ppm) / W, (y0 + PAD + 1 + (co.z - zmin) * ppm) / Hh)
    print(f"[{NAME}] repaint {'arm ' if gr['arm'] else 'body'} side {gr['g']['side']:+d}: {len(gr['faces'])} faces, "
          f"{len(gr['cols'])} colour samples, texture {w}x{h}px at ({x0},{y0}), {ppm:.0f} px/m")

# ----------------------------------------------------------------------------
# 4b. glasses: the scan has them melted into the face. Flatten that, and build real frames.
# ----------------------------------------------------------------------------
GLASSES = {
    # z: eye height, cx: lens centre, a/b: lens half width/height (rim centre line),
    # rim: rim thickness, exp: corner squareness, temple: (height, thickness)
    "Olavur": dict(z=1.623, cx=0.0405, a=0.0310, b=0.0175, rim=0.0030, depth=0.0026, exp=3.5, hinge=0.60,
                   color=(0.17, 0.155, 0.15), temple=(0.0030, 0.0016), bridge=0.0030, inpaint="portrait",
                   portrait=os.path.normpath(os.path.join(HERE, "..", "Olavur_Portrait.jpg"))),
    "Leivur": dict(z=1.628, cx=0.0420, a=0.0340, b=0.0165, rim=0.0014, depth=0.0014, exp=3.0, hinge=0.35,
                   color=(0.50, 0.50, 0.52), temple=(0.0018, 0.0014), bridge=0.0016, inpaint=False),
}


def _dilate_mask(mask, iters):
    out = mask.copy()
    for _ in range(iters):
        p = np.pad(out, 1, mode='constant', constant_values=False)
        out = (p[:-2, 1:-1] | p[2:, 1:-1] | p[1:-1, :-2] | p[1:-1, 2:] |
               p[:-2, :-2] | p[:-2, 2:] | p[2:, :-2] | p[2:, 2:])
    return out


def _erode_mask(mask, iters):
    return ~_dilate_mask(~mask, iters)


def _laplace_inpaint(rgb, mask, known_mask, bfs_iters=60, relax_iters=180, noise_std=0.010, rng=None):
    """Fill masked RGB texels via BFS nearest-neighbor seeding followed by harmonic relaxation."""
    out = rgb.copy()
    cur_k = known_mask.copy()
    todo = mask.copy()
    cur_c = rgb.copy()
    h, w = mask.shape
    for _ in range(bfs_iters):
        if not todo.any():
            break
        acc = np.zeros_like(cur_c)
        cnt = np.zeros((h, w), dtype=np.float32)
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)):
            k = np.roll(np.roll(cur_k, dy, 0), dx, 1)
            acc += np.roll(np.roll(cur_c, dy, 0), dx, 1) * k[..., None]
            cnt += k
        upd = todo & (cnt > 0)
        cur_c[upd] = acc[upd] / cnt[upd][..., None]
        cur_k |= upd
        todo &= ~upd
    out[mask] = cur_c[mask]
    for _ in range(relax_iters):
        sm = (np.roll(out, 1, 0) + np.roll(out, -1, 0) + np.roll(out, 1, 1) + np.roll(out, -1, 1)) * 0.25
        out[mask] = sm[mask]
    if noise_std > 0 and rng is not None:
        gn = rng.normal(0.0, noise_std, out.shape).astype(np.float32)
        gn = (gn + np.roll(gn, 1, 0) + np.roll(gn, -1, 0) + np.roll(gn, 1, 1) + np.roll(gn, -1, 1)) / 5.0
        out[mask] = np.clip(out[mask] + gn[mask], 0.0, 1.0)
    return out


def _rasterize_uv_xyz(faces_data, width, height):
    """Rasterize 3D face coordinates into (height, width) UV texel buffers."""
    valid = np.zeros((height, width), dtype=bool)
    xyz = np.zeros((height, width, 3), dtype=np.float32)
    for fcos, uvs in faces_data:
        xs = np.array([u.x * width for u in uvs])
        ys = np.array([u.y * height for u in uvs])
        cos = np.array([[c.x, c.y, c.z] for c in fcos], dtype=np.float32)
        x0_, x1_ = int(max(0, xs.min() - 1)), int(min(width - 1, xs.max() + 1))
        y0_, y1_ = int(max(0, ys.min() - 1)), int(min(height - 1, ys.max() + 1))
        gx, gy = np.meshgrid(np.arange(x0_, x1_ + 1) + 0.5, np.arange(y0_, y1_ + 1) + 0.5)
        d = (ys[1] - ys[2]) * (xs[0] - xs[2]) + (xs[2] - xs[1]) * (ys[0] - ys[2])
        if abs(d) < 1e-9:
            continue
        l0 = ((ys[1] - ys[2]) * (gx - xs[2]) + (xs[2] - xs[1]) * (gy - ys[2])) / d
        l1 = ((ys[2] - ys[0]) * (gx - xs[2]) + (xs[0] - xs[2]) * (gy - ys[2])) / d
        l2 = 1.0 - l0 - l1
        inside = (l0 >= -0.05) & (l1 >= -0.05) & (l2 >= -0.05)
        if not inside.any():
            continue
        l0c, l1c, l2c = np.clip(l0, 0, 1), np.clip(l1, 0, 1), np.clip(l2, 0, 1)
        ls = l0c + l1c + l2c + 1e-9
        tri_xyz = (l0c / ls)[..., None] * cos[0] + (l1c / ls)[..., None] * cos[1] + (l2c / ls)[..., None] * cos[2]
        valid[y0_:y1_ + 1, x0_:x1_ + 1] |= inside
        sl = xyz[y0_:y1_ + 1, x0_:x1_ + 1]
        sl[inside] = tri_xyz[inside]
    return valid, xyz


def _rebuild_portrait_face(bm, uv_layer, paint_layer, px, W, Hh, occ, find_free_fn, portrait_path, arm_w):
    """Reconstruct Olavur's melted eye/goggle craters cleanly and project sharp portrait eyes onto a new UV island."""
    assert os.path.exists(portrait_path), f"Missing portrait image: {portrait_path}"
    yc = -0.030

    def to_cyl(co):
        dy = -(co.y - yc)
        return math.atan2(co.x, dy), co.z, math.hypot(co.x, dy)

    orig_head_faces = [
        ([v.co.copy() for v in f.verts], [l[uv_layer].uv.copy() for l in f.loops])
        for f in bm.faces
        if not f[paint_layer] and max(v.co.z for v in f.verts) >= 1.56
        and min(v.co.z for v in f.verts) <= 1.69 and min(v.co.y for v in f.verts) <= 0.02
    ]

    def in_eye_patch(co):
        if co.y >= -0.072 or co.z >= 1.651:
            return False
        ax = abs(co.x)
        if ax < 0.017:
            return False
        z_lo = 1.594 - 0.012 * smoothstep(ax, 0.020, 0.030) * (1.0 - smoothstep(ax, 0.065, 0.078))
        return co.z > z_lo

    del_faces = [f for f in bm.faces if in_eye_patch(sum((v.co for v in f.verts), Vector()) / 3)]
    bmesh.ops.delete(bm, geom=del_faces, context='FACES')
    loose_v = [v for v in bm.verts if not v.link_faces]
    if loose_v:
        bmesh.ops.delete(bm, geom=loose_v, context='VERTS')

    # Drop any disconnected goggle-rim island left behind after cutting the craters
    seen = set()
    comps = []
    for v in bm.verts:
        if v in seen:
            continue
        c, st = {v}, [v]
        seen.add(v)
        while st:
            cur = st.pop()
            for e in cur.link_edges:
                o = e.other_vert(cur)
                if o not in seen:
                    seen.add(o)
                    c.add(o)
                    st.append(o)
        comps.append(c)
    if len(comps) > 1:
        main_c = max(comps, key=len)
        scrap = [v for c in comps if c is not main_c for v in c]
        bmesh.ops.delete(bm, geom=scrap, context='VERTS')

    eye_layer = bm.faces.layers.int.new("eye_cap")
    bedges = [e for e in bm.edges if e.is_boundary]
    before_verts = set(bm.verts)
    orig_co = {v: v.co.copy() for v in bm.verts}
    for e in bedges:
        for v in e.verts:
            th, z, _ = to_cyl(v.co)
            v.co = Vector((th * 0.12, 0.0, z))

    before_faces = set(bm.faces)
    bmesh.ops.triangle_fill(bm, use_beauty=True, use_dissolve=False, edges=bedges)
    for f in bm.faces:
        if f not in before_faces:
            f[eye_layer] = 1

    for _ in range(6):
        faces = [f for f in bm.faces if f[eye_layer] == 1]
        fs = set(faces)
        long_edges = list({e for f in faces for e in f.edges
                           if all(lf in fs for lf in e.link_faces) and e.calc_length() > 0.008})
        if not long_edges:
            break
        long_edges.sort(key=lambda e: -e.calc_length())
        for e in long_edges:
            if not e.is_valid or e.calc_length() <= 0.008:
                continue
            _, nv = bmesh.utils.edge_split(e, e.verts[0], 0.5)
            for f in list(nv.link_faces):
                if len(f.verts) == 4:
                    vs = list(f.verts)
                    opp = vs[(vs.index(nv) + 2) % 4]
                    nf, _ = bmesh.utils.face_split(f, nv, opp)
                    nf[eye_layer] = 1
                    f[eye_layer] = 1
        faces = [f for f in bm.faces if f[eye_layer] == 1]
        fs = set(faces)
        inner = list({e for f in faces for e in f.edges if all(lf in fs for lf in e.link_faces)})
        bmesh.ops.beautify_fill(bm, faces=faces, edges=inner)

    for v, co in orig_co.items():
        v.co = co

    interior = [v for v in bm.verts if v not in before_verts]
    cap_faces = [f for f in bm.faces if f[eye_layer] == 1]
    boundary_verts = {v for f in cap_faces for v in f.verts if v in before_verts}

    th_cur, z_cur, r_cur = {}, {}, {}
    for v in bm.verts:
        if v in before_verts:
            th, z, r = to_cyl(v.co)
        else:
            th, z, r = v.co.x / 0.12, v.co.z, 0.12
        th_cur[v], z_cur[v], r_cur[v] = th, z, r

    for _ in range(600):
        nt, nz, nr = {}, {}, {}
        for v in interior:
            nbs = [e.other_vert(v) for e in v.link_edges]
            nt[v] = sum(th_cur[o] for o in nbs) / len(nbs)
            nz[v] = sum(z_cur[o] for o in nbs) / len(nbs)
            nr[v] = sum(r_cur[o] for o in nbs) / len(nbs)
        th_cur.update(nt); z_cur.update(nz); r_cur.update(nr)

    collar1 = {v for v in boundary_verts if not (abs(v.co.x) < 0.023 and v.co.z < 1.622)}
    collar2 = {e.other_vert(v) for v in collar1 for e in v.link_edges if e.other_vert(v) in before_verts} - collar1
    collar2 = {v for v in collar2 if not (abs(v.co.x) < 0.023 and v.co.z < 1.622)}
    temple_geom = {v for v in before_verts if abs(v.co.x) > 0.060 and -0.078 < v.co.y < -0.028 and 1.610 < v.co.z < 1.648}
    for _ in range(15):
        upd_r = {}
        for v in collar1 | collar2 | temple_geom:
            nbs = [e.other_vert(v) for e in v.link_edges]
            avg_r = sum(r_cur[o] for o in nbs) / len(nbs)
            w = 0.55 if (v in collar1 or v in temple_geom) else 0.25
            upd_r[v] = r_cur[v] + w * (avg_r - r_cur[v])
        r_cur.update(upd_r)
        for _ in range(20):
            nr = {v: sum(r_cur[e.other_vert(v)] for e in v.link_edges) / len(v.link_edges) for v in interior}
            r_cur.update(nr)

    for v in interior:
        x = r_cur[v] * math.sin(th_cur[v])
        y = yc - r_cur[v] * math.cos(th_cur[v])
        z = z_cur[v]
        if y < -0.095:
            ax = abs(x)
            z_lo = 1.594 - 0.012 * smoothstep(ax, 0.020, 0.030) * (1.0 - smoothstep(ax, 0.065, 0.078))
            w_edge = smoothstep(z, z_lo, z_lo + 0.008) * (1.0 - smoothstep(z, 1.643, 1.651)) * smoothstep(ax, 0.018, 0.026)
            dx = (ax - 0.0375) / 0.020
            dz = (z - 1.6235) / 0.014
            d2 = dx * dx + dz * dz
            if d2 < 2.5:
                socket = -0.0038 * math.exp(-d2 * 1.1)
                eyeball = 0.0016 * math.exp(-(dx * dx * 2.2 + dz * dz * 3.2))
                r_cur[v] += (socket + eyeball) * w_edge

    for v in set(interior) | collar1 | collar2 | temple_geom:
        r, th, z = r_cur[v], th_cur[v], z_cur[v]
        v.co.x = r * math.sin(th)
        v.co.y = yc - r * math.cos(th)
        v.co.z = z

    bm.normal_update()
    for f in cap_faces:
        c = sum((v.co for v in f.verts), Vector()) / 3
        rad = Vector((c.x, c.y - yc, 0)).normalized()
        if f.normal.dot(rad) < 0:
            f.normal_flip()
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    for f in cap_faces:
        f.smooth = True

    rng = np.random.default_rng(42)
    patch_faces = []
    for f in bm.faces:
        if f[eye_layer] == 1:
            patch_faces.append(f)
        else:
            c = sum((v.co for v in f.verts), Vector()) / 3
            if c.y < -0.10 and 1.618 < c.z < 1.651 and abs(c.x) <= 0.022:
                patch_faces.append(f)
    patch_set = set(patch_faces)

    # Clean baked temple stripes, outer-rim remnants and nose-pad shadows directly on pre-cut UVs
    head_valid, head_xyz = _rasterize_uv_xyz(orig_head_faces, W, Hh)
    hx, hy, hz = head_xyz[..., 0], head_xyz[..., 1], head_xyz[..., 2]
    lum_px = px[..., :3].mean(axis=-1)
    rb_px = px[..., 0] - px[..., 2]

    temple_dirty = _dilate_mask(
        head_valid & (np.abs(hx) > 0.050) & (hy > -0.105) & (hy < -0.019) & (hz > 1.624) & (hz < 1.651)
        & ((lum_px < 0.46) | ((lum_px < 0.58) & (rb_px < 0.13))),
        2,
    )
    outer_dirty = _dilate_mask(
        head_valid & (hy <= -0.070) & (hz > 1.608) & (hz < 1.651) & (np.abs(hx) > 0.022) & (np.abs(hx) < 0.088)
        & ((lum_px < 0.48) | ((lum_px < 0.60) & (rb_px < 0.13))),
        2,
    )
    nose_dirty = _dilate_mask(
        head_valid & (hy < -0.098) & (np.abs(hx) < 0.025) & (hz > 1.598) & (hz < 1.625)
        & ((lum_px < 0.52) | (rb_px < 0.14)),
        1,
    )
    side_clean = (temple_dirty | outer_dirty | nose_dirty) & head_valid & (hy < -0.016) & (hz > 1.586) & (hz < 1.652)
    known_side = head_valid & (~side_clean) & (lum_px >= 0.48) & (rb_px >= 0.135)
    px[..., :3] = _laplace_inpaint(px[..., :3], side_clean, known_side, bfs_iters=140, relax_iters=200, noise_std=0.010, rng=rng)

    # Remove glasses frames from portrait crop before projecting clean eyes & eyebrows
    pimg = bpy.data.images.load(portrait_path, check_existing=False)
    pw, ph = pimg.size
    ppx = np.empty(pw * ph * 4, dtype=np.float32)
    pimg.pixels.foreach_get(ppx)
    bpy.data.images.remove(pimg)
    ppx = ppx.reshape(ph, pw, 4)[..., :3]

    r0, r1 = ph - 630, ph - 390
    c0, c1 = 180, 740
    eb = ppx[r0:r1, c0:c1].copy()
    rr, cc = np.mgrid[r0:r1, c0:c1]
    pL_b = (ph - 1 - 505.2, 345.5)
    pR_b = (ph - 1 - 498.6, 575.6)
    in_eye_L = ((cc - pL_b[1]) / 50.0) ** 2 + ((rr - pL_b[0]) / 24.0) ** 2 < 1.0
    in_eye_R = ((cc - pR_b[1]) / 50.0) ** 2 + ((rr - pR_b[0]) / 24.0) ** 2 < 1.0
    in_frame_zone = (cc > 212) & (cc < 706) & (rr > ph - 615) & (rr < ph - 438)
    lum = eb.mean(axis=2)
    fmask = (lum < 0.31) & ((eb[:, :, 0] - eb[:, :, 2]) < 0.08) & in_frame_zone & (~in_eye_L) & (~in_eye_R)
    seed = _dilate_mask(_erode_mask(fmask, 1), 2) & fmask
    for _ in range(15):
        seed = _dilate_mask(seed, 1) & fmask
    fmask = _dilate_mask(seed, 8) & (~in_eye_L) & (~in_eye_R)
    ppx[r0:r1, c0:c1] = _laplace_inpaint(eb, fmask, ~fmask, bfs_iters=25, relax_iters=250, noise_std=0.008, rng=rng)

    # Sample strictly non-patch faces so new cap faces' placeholder (0,0) UVs are never touched
    nonpatch_faces = [f for f in bm.faces if f not in patch_set]
    bm_idx = {v: i for i, v in enumerate(bm.verts)}
    np_cos = [v.co.copy() for v in bm.verts]
    np_polys = [[bm_idx[v] for v in f.verts] for f in nonpatch_faces]
    np_uvs = [[l[uv_layer].uv.copy() for l in f.loops] for f in nonpatch_faces]
    nonpatch_bvh = BVHTree.FromPolygons([c[:] for c in np_cos], np_polys)

    def sample_nonpatch_cyl(th, z):
        dx, dy = math.sin(th), -math.cos(th)
        hit = nonpatch_bvh.ray_cast(Vector((dx * 0.5, yc + dy * 0.5, z)), Vector((-dx, -dy, 0.0)))
        if hit[0] is None:
            return None
        fi = hit[2]
        bw = poly_3d_calc([np_cos[i] for i in np_polys[fi]], hit[0])
        uv = Vector((0.0, 0.0))
        for u, b in zip(np_uvs[fi], bw):
            uv += u * b
        fx = np.clip(uv.x * W - 0.5, 0, W - 2)
        fy = np.clip(uv.y * Hh - 0.5, 0, Hh - 2)
        ix, iy = int(fx), int(fy)
        tx, ty = fx - ix, fy - iy
        return ((1 - ty) * (1 - tx) * px[iy, ix, :3] + (1 - ty) * tx * px[iy, ix + 1, :3] +
                ty * (1 - tx) * px[iy + 1, ix, :3] + ty * tx * px[iy + 1, ix + 1, :3])

    port_samples, alb_samples = [], []
    for sx in np.linspace(-0.045, 0.045, 10):
        for sz in (1.656, 1.660, 1.582, 1.586):
            ac = sample_nonpatch_cyl(math.atan2(sx, 0.10), sz)
            if ac is None or ac.mean() < 0.2:
                continue
            pc = int(round(460.5 + sx * 3200.0))
            pr = ph - 1 - int(round(501.9 - (sz - 1.624) * 3200.0))
            if 0 <= pr < ph and 0 <= pc < pw:
                port_samples.append(ppx[pr, pc])
                alb_samples.append(ac)
    scale = np.array(alb_samples).mean(axis=0) / np.array(port_samples).mean(axis=0)

    uw, vh = 280, 140
    pos = find_free_fn(uw, vh)
    assert pos, "no free texture space for Olavur face patch"
    ux0, uy0 = pos
    occ[uy0:uy0 + vh, ux0:ux0 + uw] = True

    ths = [to_cyl(v.co)[0] for f in patch_faces for v in f.verts]
    zs = [v.co.z for f in patch_faces for v in f.verts]
    th_min, th_max = min(ths) - 0.02, max(ths) + 0.02
    z_min, z_max = min(zs) - 0.002, max(zs) + 0.002
    pad = 6
    iw, ih = uw - 2 * pad, vh - 2 * pad
    full_bvh = BVHTree.FromBMesh(bm)

    orig_grid = np.zeros((vh, uw, 3), dtype=np.float32)
    x_grid = np.zeros((vh, uw), dtype=np.float32)
    z_grid = np.zeros((vh, uw), dtype=np.float32)
    valid_g = np.zeros((vh, uw), dtype=bool)
    for iy in range(vh):
        z = z_min + float(np.clip((iy - pad + 0.5) / ih, 0.0, 1.0)) * (z_max - z_min)
        for ix in range(uw):
            th = th_min + float(np.clip((ix - pad + 0.5) / iw, 0.0, 1.0)) * (th_max - th_min)
            z_grid[iy, ix] = z
            dx, dy = math.sin(th), -math.cos(th)
            hit = full_bvh.ray_cast(Vector((dx * 0.5, yc + dy * 0.5, z)), Vector((-dx, -dy, 0.0)))
            x_grid[iy, ix] = hit[0].x if hit[0] is not None else 0.11 * dx
            oc = sample_nonpatch_cyl(th, z)
            if oc is not None and oc.mean() > 0.02:
                orig_grid[iy, ix] = oc
                valid_g[iy, ix] = True

    in_patch_px = np.zeros((vh, uw), dtype=bool)
    for f in patch_faces:
        uvs = [((to_cyl(v.co)[0] - th_min) / (th_max - th_min) * iw + pad,
                (v.co.z - z_min) / (z_max - z_min) * ih + pad) for v in f.verts]
        xs_ = np.array([p[0] for p in uvs])
        ys_ = np.array([p[1] for p in uvs])
        x0_, x1_ = int(max(0, xs_.min() - 1)), int(min(uw - 1, xs_.max() + 1))
        y0_, y1_ = int(max(0, ys_.min() - 1)), int(min(vh - 1, ys_.max() + 1))
        gx, gy = np.meshgrid(np.arange(x0_, x1_ + 1) + 0.5, np.arange(y0_, y1_ + 1) + 0.5)
        d = (ys_[1] - ys_[2]) * (xs_[0] - xs_[2]) + (xs_[2] - xs_[1]) * (ys_[0] - ys_[2])
        if abs(d) < 1e-9:
            continue
        l0 = ((ys_[1] - ys_[2]) * (gx - xs_[2]) + (xs_[2] - xs_[1]) * (gy - ys_[2])) / d
        l1 = ((ys_[2] - ys_[0]) * (gx - xs_[2]) + (xs_[0] - xs_[2]) * (gy - ys_[2])) / d
        in_patch_px[y0_:y1_ + 1, x0_:x1_ + 1] |= (l0 >= -0.05) & (l1 >= -0.05) & (l0 + l1 <= 1.05)

    safe_inner = _erode_mask(in_patch_px, 1)
    edge_w = np.zeros((vh, uw), dtype=np.float32)
    cur_m = safe_inner.copy()
    for step in range(1, 10):
        edge_w[cur_m] = step / 9.0
        cur_m = _erode_mask(cur_m, 1)

    lum_o = orig_grid.mean(axis=2)
    rb_o = orig_grid[..., 0] - orig_grid[..., 2]
    clean_mask = safe_inner | (~valid_g) | (in_patch_px & ((lum_o < 0.52) | (rb_o < 0.14)))
    base_grid = _laplace_inpaint(orig_grid, clean_mask, valid_g & (~clean_mask), bfs_iters=180, relax_iters=220, noise_std=0.012, rng=rng)

    patch_tex = base_grid.copy()
    ang = -0.0287
    for iy in range(vh):
        for ix in range(uw):
            ew_ = float(edge_w[iy, ix])
            if ew_ <= 0.0:
                continue
            x, z = float(x_grid[iy, ix]), float(z_grid[iy, ix])
            xr = x * math.cos(ang) - (z - 1.624) * math.sin(ang)
            zr = x * math.sin(ang) + (z - 1.624) * math.cos(ang)
            pc_f = 460.5 + xr * 3200.0
            pr_f = ph - 1.0 - (501.9 - zr * 3200.0)
            pc0 = int(np.clip(math.floor(pc_f), 0, pw - 2))
            pr0 = int(np.clip(math.floor(pr_f), 0, ph - 2))
            fx, fy = np.clip(pc_f - pc0, 0, 1), np.clip(pr_f - pr0, 0, 1)
            pcol = ((1 - fy) * (1 - fx) * ppx[pr0, pc0] + (1 - fy) * fx * ppx[pr0, pc0 + 1] +
                    fy * (1 - fx) * ppx[pr0 + 1, pc0] + fy * fx * ppx[pr0 + 1, pc0 + 1]) * scale
            w_x = 1.0 - smoothstep(abs(x), 0.052, 0.065)
            w_z = smoothstep(z, 1.605, 1.615) * (1.0 - smoothstep(z, 1.643, 1.649))
            w_nose = smoothstep(z, 1.619, 1.630) if abs(x) < 0.018 else 1.0
            wp = ew_ * w_x * w_z * w_nose
            patch_tex[iy, ix] = np.clip(base_grid[iy, ix] * (1.0 - wp) + pcol * wp, 0, 1)

    px[uy0:uy0 + vh, ux0:ux0 + uw, :3] = patch_tex
    px[uy0:uy0 + vh, ux0:ux0 + uw, 3] = 1.0
    for f in patch_faces:
        f[paint_layer] = 8
        for l in f.loops:
            th, z, _ = to_cyl(l.vert.co)
            u = (ux0 + pad + (th - th_min) / (th_max - th_min) * iw) / W
            v = (uy0 + pad + (z - z_min) / (z_max - z_min) * ih) / Hh
            l[uv_layer].uv = (u, v)

    bm.faces.layers.int.remove(eye_layer)
    for v in bm.verts:
        arm_w.setdefault(v, 0.0)
    print(f"[{NAME}] glasses: rebuilt face cap ({len(cap_faces)} faces) & cleaned {int(side_clean.sum())} side texels")


glasses_verts = set()
if NAME in GLASSES:
    G = GLASSES[NAME]
    zc, cx, ga, gb = G["z"], G["cx"], G["a"], G["b"]
    head_y = bones["Head"][0].y
    bm.normal_update()

    if G["inpaint"] == "portrait":
        _rebuild_portrait_face(bm, uv_layer, paint_layer, px, W, Hh, occ, find_free, G["portrait"], arm_w)
    else:
        band = [v for v in bm.verts if zc - 0.04 < v.co.z < zc + 0.04 and v.co.y < head_y - 0.005 and abs(v.co.x) < 0.12]
        orig = {v: v.co.copy() for v in band}
        relax(band, 30, 0.5)
        disp = {v: (orig[v] - v.co).dot(v.normal) for v in band}
        for v in band:
            v.co = orig[v]
        bandset = set(band)
        lumps = {v for v in band if abs(disp[v]) > 0.0015 and not (abs(v.co.x) < 0.011 and v.co.z < zc + 0.004)}
        # press the lumps (and the pits behind the lenses) onto the smooth face, moving verts only
        # along their normal so the face texture stays where it is
        region = grow(lumps, 1, lambda v: v in bandset)
        for v in region:
            v.co = orig[v] - v.normal * (disp[v] * smoothstep(abs(disp[v]), 0.0006, 0.0020))
        print(f"[{NAME}] glasses: flattened {len(region)} face verts")

        def tri_mask(faces, mask):
            for f in faces:
                uv = [l[uv_layer].uv for l in f.loops]
                xs = np.array([u.x * W for u in uv])
                ys = np.array([u.y * Hh for u in uv])
                x0, x1 = int(max(0, xs.min() - 1)), int(min(W - 1, xs.max() + 1))
                y0, y1 = int(max(0, ys.min() - 1)), int(min(Hh - 1, ys.max() + 1))
                gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
                d = (ys[1] - ys[2]) * (xs[0] - xs[2]) + (xs[2] - xs[1]) * (ys[0] - ys[2])
                if abs(d) < 1e-9:
                    continue
                l0 = ((ys[1] - ys[2]) * (gx - xs[2]) + (xs[2] - xs[1]) * (gy - ys[2])) / d
                l1 = ((ys[2] - ys[0]) * (gx - xs[2]) + (xs[0] - xs[2]) * (gy - ys[2])) / d
                inside = (l0 >= -0.05) & (l1 >= -0.05) & (l0 + l1 <= 1.05)
                mask[y0:y1 + 1, x0:x1 + 1] |= inside

        if G["inpaint"]:
            # remove the frames painted onto the skin: fill them in from the skin around them
            prot = {v for v in lumps if disp[v] > 0.0015}
            prot = grow(prot, 1, lambda v: v in bandset)
            frame_faces = [f for f in bm.faces if not f[paint_layer] and any(v in prot for v in f.verts)]
            head_faces = [f for f in bm.faces if not f[paint_layer] and all(v.co.z > zc - 0.12 for v in f.verts)]
            mask = np.zeros((Hh, W), dtype=bool)
            valid = np.zeros((Hh, W), dtype=bool)
            tri_mask(frame_faces, mask)
            tri_mask(head_faces, valid)
            for _ in range(2):      # dilate
                m = mask.copy()
                m[1:] |= mask[:-1]
                m[:-1] |= mask[1:]
                m[:, 1:] |= mask[:, :-1]
                m[:, :-1] |= mask[:, 1:]
                mask = m
            mask &= valid
            if G["inpaint"] == "dark":
                # only the dark frame pixels, and never what shows through the new lenses (the eyes)
                mask &= px[..., :3].mean(axis=-1) < G.get("dark", 0.28)
                e_ = G["exp"]
                lens_faces = [f for f in bm.faces if not f[paint_layer] and all(
                    (abs((abs(v.co.x) - cx) / ga) ** e_ + abs((v.co.z - zc) / gb) ** e_) < 0.8 for v in f.verts)]
                lens = np.zeros((Hh, W), dtype=bool)
                tri_mask(lens_faces, lens)
                mask &= ~lens
                for _ in range(2):
                    m = mask.copy()
                    m[1:] |= mask[:-1]
                    m[:-1] |= mask[1:]
                    m[:, 1:] |= mask[:, :-1]
                    m[:, :-1] |= mask[:, 1:]
                    mask = m
                mask &= valid & ~lens
            known = valid & ~mask
            col = px[..., :3].copy()
            todo = mask.copy()
            for _ in range(200):
                if not todo.any():
                    break
                acc = np.zeros_like(col)
                cnt = np.zeros((Hh, W))
                for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)):
                    k = np.roll(np.roll(known, dy, 0), dx, 1)
                    acc += np.roll(np.roll(col, dy, 0), dx, 1) * k[..., None]
                    cnt += k
                new = todo & (cnt > 0)
                col[new] = acc[new] / cnt[new][:, None]
                known |= new
                todo &= ~new
            for _ in range(3):      # soften the fill
                blur = (col + np.roll(col, 1, 0) + np.roll(col, -1, 0) + np.roll(col, 1, 1) + np.roll(col, -1, 1)) / 5
                col[mask] = blur[mask]
            px[..., :3][mask] = col[mask]
            print(f"[{NAME}] glasses: {int(mask.sum())} texels of painted frame filled with skin")

    fbvh = BVHTree.FromBMesh(bm)

    def face_y(x, z):
        hit = fbvh.ray_cast(Vector((x, -3, z)), Vector((0, 1, 0)))
        return hit[0].y if hit[0] is not None else 0.0

    def side_x(sgn, y, z):
        hit = fbvh.ray_cast(Vector((sgn * 3, y, z)), Vector((-sgn, 0, 0)))
        return abs(hit[0].x) if hit[0] is not None else 0.0

    wrap = 0.22
    gfaces = []

    def tube(points, hw, hh, up_hint, closed):
        n = len(points)
        rings = []
        for i, p in enumerate(points):
            t = (points[(i + 1) % n] - points[i - 1]) if closed else (points[min(i + 1, n - 1)] - points[max(i - 1, 0)])
            t.normalize()
            side = t.cross(up_hint).normalized()
            up = side.cross(t).normalized()
            rings.append([bm.verts.new(p + side * (sx * hw) + up * (sy * hh)) for sx, sy in ((1, 1), (1, -1), (-1, -1), (-1, 1))])
        for i in range(n if closed else n - 1):
            a, b = rings[i], rings[(i + 1) % n]
            for k in range(4):
                gfaces.append(bm.faces.new((a[k], a[(k + 1) % 4], b[(k + 1) % 4], b[k])))
        if not closed:
            gfaces.append(bm.faces.new(rings[0][::-1]))
            gfaces.append(bm.faces.new(rings[-1]))
        for r in rings:
            glasses_verts.update(r)

    def sp(c, e):
        return math.copysign(abs(c) ** (2.0 / e), c)

    Y = Vector((0, 1, 0))

    def rim_points(sgn, y_l):
        pts = []
        for i in range(32):
            th = 2 * math.pi * i / 32
            X = sgn * cx + ga * sp(math.cos(th), G["exp"])
            Z = zc + gb * sp(math.sin(th), G["exp"])
            pts.append(Vector((X, y_l + wrap * (abs(X) - cx), Z)))
        return pts

    # sit the frames as close to the face as they can go without touching it
    y_l = min(face_y(q.x, q.z) - q.y for sgn in (1, -1) for q in rim_points(sgn, 0.0)) - G["depth"] / 2 - 0.0012
    for sgn in (1, -1):
        tube(rim_points(sgn, y_l), G["rim"] / 2, G["depth"] / 2, Y, True)
        # temple arm: from the hinge back along the head to the ear
        z_t = zc + gb * G["hinge"]
        hx = cx + ga + G["rim"] * 0.3
        y0 = y_l + wrap * ga
        path = [Vector((sgn * hx, y0, z_t))]
        y_stop = head_y + 0.012
        base = None
        y = y0
        while y < y_stop:
            y += 0.007
            sx_ = side_x(sgn, y, z_t)
            if y > head_y - 0.04:
                if base is None:
                    base = sx_
                elif sx_ > base + 0.007:        # reached the ear: tuck the tip in behind it
                    path.append(Vector((sgn * (base + 0.002), y + 0.004, z_t - 0.004)))
                    break
            path.append(Vector((sgn * max(sx_ + 0.0018, hx), y, z_t)))
        tube(path, G["temple"][1] / 2, G["temple"][0] / 2, Vector((0, 0, 1)), False)
    xb = cx - ga + G["rim"] * 0.3
    zb_ = zc + gb * 0.45
    bpts = [Vector((float(x), y_l - wrap * ga, zb_ + 0.0025 * (1 - (x / xb) ** 2))) for x in np.linspace(-xb, xb, 7)]
    tube(bpts, G["bridge"] / 2, G["depth"] / 2, Y, False)

    bmesh.ops.recalc_face_normals(bm, faces=gfaces)
    pos = find_free(14, 14)
    assert pos, "no free texture space for the glasses colour"
    x0, y0 = pos
    occ[y0:y0 + 14, x0:x0 + 14] = True
    px[y0:y0 + 14, x0:x0 + 14, :3] = G["color"]
    px[y0:y0 + 14, x0:x0 + 14, 3] = 1.0
    for f in gfaces:
        f.smooth = False
        f[paint_layer] = 9
        for l in f.loops:
            l[uv_layer].uv = ((x0 + 7) / W, (y0 + 7) / Hh)
    for v in glasses_verts:
        arm_w[v] = 0.0
    print(f"[{NAME}] glasses: built frames with {len(glasses_verts)} verts, lens plane y={y_l:.3f}")

out_img = bpy.data.images.new(NAME + "_Albedo_new", W, Hh, alpha=True)
out_img.pixels.foreach_set(px.reshape(-1))
out_img.filepath_raw = NEW_ALBEDO
out_img.file_format = 'PNG'
out_img.save()
bpy.data.images.remove(out_img)
bpy.data.images.remove(img)

# ----------------------------------------------------------------------------
# 5. skin weights
# ----------------------------------------------------------------------------
bm.verts.ensure_lookup_table()
bm.verts.index_update()
nv = len(bm.verts)
gnames = [vg.name for vg in old_obj.vertex_groups]
gi = {n: i for i, n in enumerate(gnames)}
om = old_obj.data
old_w = np.zeros((len(om.vertices), len(gnames)), dtype=np.float64)
for v in om.vertices:
    for ge in v.groups:
        old_w[v.index, ge.group] = ge.weight
obvh = BVHTree.FromPolygons([v.co[:] for v in om.vertices], [p.vertices[:] for p in om.polygons])
Wt = np.zeros((nv, len(gnames)))
for v in bm.verts:
    loc, _, idx, _ = obvh.find_nearest(v.co)
    pv = om.polygons[idx].vertices
    bw = poly_3d_calc([om.vertices[i].co for i in pv], loc)
    for i, b in zip(pv, bw):
        Wt[v.index] += b * old_w[i]

arm_groups = {s: [gi[p + n] for n in ("UpperArm", "LowerArm", "Hand")] for s, p in ((1, "Left"), (-1, "Right"))}
all_arm = arm_groups[1] + arm_groups[-1]
Wbody = Wt.copy()
Wbody[:, all_arm] = 0.0
# verts whose old weights were (mostly) arm weights get body weights from their neighbours
valid = Wbody.sum(axis=1) > 0.35
cut_near = set()
for c in caps:
    if not c["arm"]:
        cut_near |= set(v.index for v in c["interior"]) | set(v.index for v in c["chain"])
for i in cut_near:
    valid[i] = False
rows = Wbody.sum(axis=1, keepdims=True)
Wbody = np.where(rows > 1e-6, Wbody / np.maximum(rows, 1e-6), 0.0)
nbrs = [[e.other_vert(v).index for e in v.link_edges] for v in bm.verts]
invalid = [i for i in range(nv) if not valid[i]]
for _ in range(200):
    todo = []
    for i in invalid:
        if valid[i]:
            continue
        nb = [j for j in nbrs[i] if valid[j]]
        if nb:
            todo.append((i, Wbody[nb].mean(axis=0)))
    if not todo:
        break
    for i, w in todo:
        Wbody[i] = w / max(1e-9, w.sum())
        valid[i] = True
for _ in range(3):   # smooth the filled-in region
    upd = {i: 0.5 * Wbody[i] + 0.5 * Wbody[nbrs[i]].mean(axis=0) for i in invalid if nbrs[i]}
    for i, w in upd.items():
        Wbody[i] = w / max(1e-9, w.sum())

Warm = np.zeros_like(Wt)
aw = np.zeros(nv)
for side, P in ((1, "Left"), (-1, "Right")):
    J, E = bones[P + "UpperArm"]
    _, Wr = bones[P + "LowerArm"]
    _, Tip = bones[P + "Hand"]
    dU, dL, dH = (E - J).normalized(), (Wr - E).normalized(), (Tip - Wr).normalized()
    de, dw = (dU + dL).normalized(), (dL + dH).normalized()
    iu, il, ih = arm_groups[side]
    for v in bm.verts:
        a = arm_w[v]
        if a <= 0 or side * v.co.x <= 0:
            continue
        wl = smoothstep((v.co - E).dot(de), -0.05, 0.05)
        wh = smoothstep((v.co - Wr).dot(dw), -0.025, 0.025)
        Warm[v.index, iu] = 1 - wl
        Warm[v.index, il] = wl * (1 - wh)
        Warm[v.index, ih] = wl * wh
        aw[v.index] = a
Wfin = (1 - aw)[:, None] * Wbody + aw[:, None] * Warm
for v in glasses_verts:         # the frames ride rigidly on the head
    Wfin[v.index] = 0.0
    Wfin[v.index, gi["Head"]] = 1.0
# keep the 4 strongest influences
for i in range(nv):
    row = Wfin[i]
    if np.count_nonzero(row > 1e-4) > 4:
        keep = np.argsort(row)[-4:]
        m = np.zeros_like(row)
        m[keep] = row[keep]
        row = m
    row[row < 1e-4] = 0
    s = row.sum()
    Wfin[i] = row / s if s > 0 else row
assert (Wfin.sum(axis=1) > 0.99).all(), "unweighted vertices"

# ----------------------------------------------------------------------------
# 6. object, save, export
# ----------------------------------------------------------------------------
new_mesh = bpy.data.meshes.new(NAME + "_Mesh")
bm.to_mesh(new_mesh)
bm.free()
new_mesh.materials.append(mat)
obj = bpy.data.objects.new(NAME, new_mesh)
for col in old_obj.users_collection:
    col.objects.link(obj)
obj.parent = rig
obj.matrix_parent_inverse = old_obj.matrix_parent_inverse.copy()
for n in gnames:
    obj.vertex_groups.new(name=n)
for i in range(nv):
    for gidx in np.nonzero(Wfin[i])[0]:
        obj.vertex_groups[int(gidx)].add([i], float(Wfin[i, gidx]), 'REPLACE')
mod = obj.modifiers.new("Armature", 'ARMATURE')
mod.object = rig
old_mesh_name = old_obj.data.name
bpy.data.objects.remove(old_obj)
for m in list(bpy.data.meshes):
    if m.users == 0:
        bpy.data.meshes.remove(m)
new_mesh.name = NAME + "_Mesh"      # Unity refers to the mesh by this name
for im in bpy.data.images:
    if im.source == 'FILE' and im.name.startswith(NAME + "_Albedo"):
        im.filepath = NEW_ALBEDO
        im.reload()
print(f"[{NAME}] new mesh: {len(new_mesh.vertices)} verts, {len(new_mesh.polygons)} tris")

if DEBUG:
    os.makedirs(DEBUG, exist_ok=True)
    scene = bpy.context.scene
    scene.render.engine = 'BLENDER_WORKBENCH'
    scene.render.resolution_x, scene.render.resolution_y = 700, 1000
    cam_d = bpy.data.cameras.new("DbgCam")
    cam_d.type = 'ORTHO'
    cam = bpy.data.objects.new("DbgCam", cam_d)
    scene.collection.objects.link(cam)
    scene.camera = cam
    views = {"front": ((0, -5, 0.9), 1.95), "back": ((0, 5, 0.9), 1.95), "q": ((3.5, -3.5, 0.9), 1.95),
             "qb": ((-3.5, 3.5, 0.9), 1.95), "hipq": ((3.5, -3.5, 0.95), 0.75), "hipfront": ((0, -5, 0.95), 0.75),
             "armin": ((-3.0, -4.0, 1.1), 0.9), "hipback": ((2.5, 4.0, 0.95), 0.75), "side": ((5, 0, 0.9), 1.95)}

    def shots(prefix, names, mode):
        sh = scene.display.shading
        sh.light = 'STUDIO' if mode == 'SINGLE' else 'FLAT'
        sh.color_type = mode
        sh.show_cavity = mode == 'SINGLE'
        sh.single_color = (0.7, 0.7, 0.72)
        for vn in names:
            pos, sc = views[vn]
            pos = Vector(pos)
            cam.location = pos
            cam.rotation_euler = (Vector((0, 0, pos.z)) - pos).to_track_quat('-Z', 'Y').to_euler()
            cam_d.ortho_scale = sc
            scene.render.filepath = os.path.join(DEBUG, f"{NAME}_{prefix}_{vn}.png")
            bpy.ops.render.render(write_still=True)

    rig.data.pose_position = 'REST'
    shots("rest_solid", ["front", "back", "q", "hipq", "armin", "hipback", "side"], 'SINGLE')
    shots("rest_tex", ["front", "back", "q", "qb", "hipq", "hipfront", "armin", "hipback", "side"], 'TEXTURE')
    rig.data.pose_position = 'POSE'
    ad = rig.animation_data or rig.animation_data_create()
    for an, frames in (("Walk", (0, 8, 16, 24)), ("Run", (0, 5, 10, 15)), ("Idle", (0,))):
        act = bpy.data.actions.get(an)
        if not act:
            continue
        ad.action = act
        if hasattr(ad, "action_slot") and act.slots:
            ad.action_slot = act.slots[0]
        for fr in frames:
            scene.frame_set(fr)
            shots(f"{an}{fr:02d}_tex", ["q", "front"], 'TEXTURE')
            if an == "Run":
                shots(f"{an}{fr:02d}_solid", ["q", "qb"], 'SINGLE')
    bpy.data.objects.remove(cam)
    scene.frame_set(0)

for im in bpy.data.images:
    if im.source == 'FILE' and im.name.startswith(NAME + "_Albedo"):
        im.filepath = "//" + os.path.relpath(NEW_ALBEDO, HERE)
bpy.ops.wm.save_as_mainfile(filepath=OUT_BLEND)
for im in bpy.data.images:      # save_as remaps relative paths, so set the final one afterwards
    if im.source == 'FILE' and im.name.startswith(NAME + "_Albedo"):
        im.filepath = "//" + os.path.relpath(NEW_ALBEDO, HERE)
bpy.ops.wm.save_mainfile()

if EXPORT:
    for o in bpy.data.objects:
        o.select_set(o in (obj, rig))
    bpy.context.view_layer.objects.active = rig
    bpy.ops.export_scene.fbx(
        filepath=OUT_FBX, use_selection=True, object_types={'ARMATURE', 'MESH'},
        apply_scale_options='FBX_SCALE_UNITS', add_leaf_bones=False,
        mesh_smooth_type='FACE', use_tspace=True,
        bake_anim=True, bake_anim_use_all_actions=True, bake_anim_use_nla_strips=False,
        bake_anim_use_all_bones=True, bake_anim_force_startend_keying=True, bake_anim_simplify_factor=0.0,
        path_mode='AUTO', embed_textures=False)
    print(f"[{NAME}] exported {OUT_FBX}")
