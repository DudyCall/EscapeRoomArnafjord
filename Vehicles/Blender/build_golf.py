"""
Procedural Volkswagen Golf Mk7 (2015), 5-door, blue metallic.

Run:  blender --background --python build_golf.py -- [out.fbx] [--no-render]

Car coordinates used below: (x, s, z)
  x = across (car's LEFT is +x), s = metres behind the front bumper, z = up from ground.
Blender: front faces -Y, origin on the ground at the middle of the wheelbase.
"""
import bpy, bmesh, math, os, sys, time
from mathutils import Vector, Matrix
from mathutils.bvhtree import BVHTree

T0 = time.time()
HERE = os.path.dirname(os.path.abspath(__file__))
ARGV = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
FBX_PATH = next((a for a in ARGV if a.lower().endswith(".fbx")), os.path.join(HERE, "VWGolf.fbx"))
DO_RENDER = "--no-render" not in ARGV

# ---- real-world Golf Mk7 dimensions (m) ------------------------------------
LENGTH, WIDTH, HEIGHT = 4.255, 1.799, 1.452
S_FRONT_AXLE, WHEELBASE = 0.880, 2.637
S_REAR_AXLE = S_FRONT_AXLE + WHEELBASE
S_MID = S_FRONT_AXLE + WHEELBASE / 2
TRACK_F, TRACK_R = 1.549, 1.520
TYRE_R, TYRE_W, RIM_R = 0.316, 0.205, 0.203      # 205/55 R16
ARCH_R = 0.355


def V(x, s, z):
    return Vector((x, s - S_MID, z))


bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene

# ---------------------------------------------------------------- materials
def make_mat(name, color, metallic=0.0, rough=0.5, alpha=1.0, emission=None, estr=0.0, coat=0.0):
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

    def setin(n, v):
        if n in b.inputs:
            b.inputs[n].default_value = v
    setin("Base Color", (*color, 1.0))
    setin("Metallic", metallic)
    setin("Roughness", rough)
    setin("Alpha", alpha)
    setin("Coat Weight", coat)
    setin("Coat Roughness", 0.03)
    if emission:
        setin("Emission Color", (*emission, 1.0))
        setin("Emission Strength", estr)
    m.diffuse_color = (*color, alpha)
    return m


MATS = {
    "CarPaint":      make_mat("CarPaint", (0.012, 0.075, 0.30), metallic=0.75, rough=0.28, coat=1.0),
    "Glass":         make_mat("Glass", (0.02, 0.03, 0.035), rough=0.03, alpha=0.38),
    "TrimBlack":     make_mat("TrimBlack", (0.012, 0.012, 0.014), rough=0.25),
    "Underbody":     make_mat("Underbody", (0.02, 0.02, 0.022), rough=0.85),
    "Interior":      make_mat("Interior", (0.035, 0.036, 0.04), rough=0.8),
    "Seat":          make_mat("Seat", (0.06, 0.062, 0.07), rough=0.9),
    "HeadlightLens": make_mat("HeadlightLens", (0.20, 0.22, 0.25), metallic=1.0, rough=0.16),
    "TaillightLens": make_mat("TaillightLens", (0.45, 0.01, 0.01), rough=0.12,
                              emission=(1.0, 0.03, 0.02), estr=0.3),
    "Chrome":        make_mat("Chrome", (0.82, 0.83, 0.85), metallic=1.0, rough=0.08),
    "Tyre":          make_mat("Tyre", (0.015, 0.015, 0.016), rough=0.9),
    "Alloy":         make_mat("Alloy", (0.62, 0.63, 0.65), metallic=1.0, rough=0.28),
    "BrakeDisc":     make_mat("BrakeDisc", (0.30, 0.30, 0.31), metallic=1.0, rough=0.45),
    "PlateWhite":    make_mat("PlateWhite", (0.88, 0.88, 0.86), rough=0.45),
    "PlateBlue":     make_mat("PlateBlue", (0.01, 0.08, 0.45), rough=0.45),
}


# ------------------------------------------------------------- mesh builder
def newell(vs, f):
    n = Vector((0, 0, 0))
    for a in range(len(f)):
        p, q = vs[f[a]], vs[f[(a + 1) % len(f)]]
        n.x += (p.y - q.y) * (p.z + q.z)
        n.y += (p.z - q.z) * (p.x + q.x)
        n.z += (p.x - q.x) * (p.y + q.y)
    return n


class MB:
    def __init__(self):
        self.v, self.f, self.m = [], [], []

    def add(self, verts, faces, mat, orient=None, xf=None):
        base = len(self.v)
        vs = [(xf @ Vector(v)) if xf is not None else Vector(v) for v in verts]
        for f in faces:
            f = list(f)
            if orient is not None:
                c = sum((vs[i] for i in f), Vector((0, 0, 0))) / len(f)
                if newell(vs, f).dot(orient(c)) < 0:
                    f.reverse()
            self.f.append([base + i for i in f])
            self.m.append(mat)
        self.v += vs

    def tris(self):
        return sum(len(f) - 2 for f in self.f)

    def to_mesh(self, name, angle=40.0):
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
        me.update()
        tb = bmesh.new()                       # n-gons (boolean cuts, caps) -> triangles for a clean engine import
        tb.from_mesh(me)
        ngons = [f for f in tb.faces if len(f.verts) > 4]
        if ngons:
            bmesh.ops.triangulate(tb, faces=ngons)
        tb.to_mesh(me)
        tb.free()
        try:
            me.set_sharp_from_angle(angle=math.radians(angle))
        except Exception as e:
            print("set_sharp_from_angle failed:", e)
        return me


def new_obj(name, mesh, loc=(0, 0, 0), rot=None):
    o = bpy.data.objects.new(name, mesh)
    scene.collection.objects.link(o)
    o.location = loc
    if rot is not None:
        o.rotation_euler = rot
    return o


def rbox(dims, bevel=0.03, seg=3):
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:
        v.co = Vector((v.co.x * dims[0], v.co.y * dims[1], v.co.z * dims[2]))
    if bevel > 0:
        bmesh.ops.bevel(bm, geom=list(bm.edges), offset=bevel, segments=seg, profile=0.5, affect='EDGES')
    bm.verts.index_update()
    vs = [v.co.copy() for v in bm.verts]
    fs = [[v.index for v in f.verts] for f in bm.faces]
    bm.free()
    return vs, fs


def lathe(profile, segs, closed):
    """profile: [(radius, x)] revolved about the X axis."""
    vs, fs = [], []
    n = len(profile)
    for a in range(segs):
        ph = 2 * math.pi * a / segs
        for r, x in profile:
            vs.append(Vector((x, r * math.cos(ph), r * math.sin(ph))))
    for a in range(segs):
        b = (a + 1) % segs
        for p in range(n if closed else n - 1):
            q = (p + 1) % n
            fs.append([a * n + p, a * n + q, b * n + q, b * n + p])
    return vs, fs


def disc(radius, x, segs):
    vs = [Vector((x, radius * math.cos(2 * math.pi * a / segs), radius * math.sin(2 * math.pi * a / segs)))
          for a in range(segs)]
    return vs, [list(range(segs))]


def T(x, s, z):
    return Matrix.Translation(V(x, s, z))


def RX(deg):
    return Matrix.Rotation(math.radians(deg), 4, 'X')


# =============================================================== BODY SHELL
def lerp_table(tab, x):
    if x <= tab[0][0]:
        return tab[0][1]
    for (x0, y0), (x1, y1) in zip(tab, tab[1:]):
        if x <= x1:
            return y0 + (y1 - y0) * (x - x0) / (x1 - x0)
    return tab[-1][1]


# centre-line silhouette of hood / windscreen / roof / rear window
ZCL = [(0.04, 0.745), (0.20, 0.800), (0.40, 0.850), (0.70, 0.905), (1.00, 0.950), (1.18, 0.985),
       (2.00, 1.400), (2.12, 1.428), (2.35, 1.447), (2.60, 1.452), (3.00, 1.443), (3.35, 1.418),
       (3.66, 1.390), (3.72, 1.345), (4.06, 1.040), (4.15, 0.985)]

def zcl(s):
    return lerp_table(ZCL, s)


# stations along the side:
# (s, s_top, bow, Wmax, w_top, z0, z2, z3, z4, z_belt, crown, bulge)
ST = [
    (0.20, 0.20, -0.16, 0.8200, 0.690, 0.200, 0.36, 0.50, 0.61, 0.670, 0.040, 0.25),
    (0.34, 0.34, -0.15, 0.8650, 0.745, 0.200, 0.37, 0.51, 0.65, 0.715, 0.040, 0.25),
    (0.54, 0.54, -0.14, 0.8850, 0.770, 0.200, 0.37, 0.52, 0.70, 0.770, 0.040, 0.25),
    (0.88, 0.88, -0.13, 0.8995, 0.785, 0.195, 0.37, 0.52, 0.75, 0.830, 0.040, 0.25),
    (1.20, 1.20, -0.13, 0.8930, 0.785, 0.190, 0.37, 0.52, 0.79, 0.885, 0.035, 0.22),
    (1.33, 1.33, -0.13, 0.8880, 0.780, 0.190, 0.37, 0.52, 0.80, 0.915, 0.030, 0.15),
    (1.45, 1.45, -0.13, 0.8860, 0.745, 0.190, 0.37, 0.52, 0.80, 0.930, 0.030, 0.05),
    (1.75, 1.75, -0.11, 0.8850, 0.675, 0.190, 0.37, 0.52, 0.80, 0.938, 0.040, 0.03),
    (2.08, 2.08, -0.08, 0.8850, 0.615, 0.190, 0.37, 0.52, 0.80, 0.945, 0.055, 0.03),
    (2.22, 2.22, -0.07, 0.8850, 0.605, 0.190, 0.37, 0.52, 0.80, 0.948, 0.070, 0.03),
    (2.58, 2.58, -0.02, 0.8850, 0.600, 0.190, 0.37, 0.52, 0.805, 0.955, 0.075, 0.03),
    (2.66, 2.66, -0.01, 0.8850, 0.600, 0.190, 0.37, 0.52, 0.805, 0.957, 0.075, 0.03),
    (3.00, 3.00, 0.01, 0.8880, 0.600, 0.190, 0.37, 0.52, 0.81, 0.967, 0.075, 0.03),
    (3.14, 3.12, 0.02, 0.8930, 0.600, 0.190, 0.37, 0.52, 0.815, 0.975, 0.075, 0.03),
    (3.40, 3.30, 0.05, 0.8995, 0.600, 0.190, 0.37, 0.52, 0.82, 0.985, 0.070, 0.03),
    (3.62, 3.60, 0.06, 0.8980, 0.605, 0.190, 0.37, 0.52, 0.82, 0.985, 0.065, 0.03),
    (3.70, 3.66, 0.06, 0.8950, 0.615, 0.200, 0.37, 0.52, 0.82, 0.985, 0.050, 0.03),
    (3.92, 3.96, 0.10, 0.8780, 0.700, 0.230, 0.39, 0.53, 0.79, 0.920, 0.030, 0.10),
    (4.06, 4.06, 0.09, 0.8500, 0.735, 0.270, 0.41, 0.55, 0.74, 0.840, 0.020, 0.15),
]
BBOT = [-0.14, -0.10, -0.05, -0.02] + [0.0] * 12 + [0.02, 0.06, 0.11]
# centre-line profile of the nose and tail, per row: (z, s)
FRONT = [(0.19, 0.06), (0.25, 0.025), (0.36, 0.005), (0.50, 0.0), (0.61, 0.005),
         (0.675, 0.012), (0.70, 0.018), (0.72, 0.025), (0.735, 0.032), (0.745, 0.04)]
REAR = [(0.28, 4.17), (0.33, 4.22), (0.42, 4.248), (0.55, 4.255), (0.70, 4.245),
        (0.84, 4.225), (0.885, 4.21), (0.925, 4.195), (0.96, 4.175), (0.985, 4.15)]

A = [-1.0, -0.90, -0.62, -0.32, 0.0, 0.32, 0.62, 0.90, 1.0]
NX, NY, NZ = len(A) - 1, len(ST) - 1, 9
QE, PE = 3.0, 2.0
TWIN = [0.30, 0.60, 0.88, 1.0]


def side_row(j, k):
    s, st, B, W, wt, z0, z2, z3, z4, zb, cr, bu = ST[j]
    if k == 0: return 0.90 * W, s, z0
    if k == 1: return 0.955 * W, s, z0 + 0.06
    if k == 2: return 0.99 * W, s, z2
    if k == 3: return W, s, z3
    if k == 4: return 0.99 * W, s, z4
    if k == 5: return 0.955 * W, s, zb
    t = TWIN[k - 6]
    bx, bz = 0.955 * W, zb
    tx, tz = wt, zcl(st + B) - cr
    dx, dz = tx - bx, tz - bz
    L = math.hypot(dx, dz)
    off = bu * L * math.sin(math.pi * t)
    return bx + dx * t + dz / L * off, s + (st - s) * t, bz + dz * t - dx / L * off


def lattice_pos(i, j, k):
    a = A[i]
    aa = abs(a)
    if i in (0, NX):
        w, s, z = side_row(j, k)
        return V(a * w, s, z)
    st = ST[j]
    if k == NZ:
        return V(a * st[4], st[1] + st[2] * (1 - aa ** QE), zcl(st[1] + st[2]) - st[10] * aa ** PE)
    if k == 0:
        return V(a * 0.90 * st[3], st[0] + BBOT[j] * (1 - aa ** QE), st[5])
    w, s, z = side_row(j, k)
    zc, sc = (FRONT if j == 0 else REAR)[k]
    return V(a * w, s + (sc - s) * (1 - aa ** QE), zc + (z - zc) * aa ** PE)


def build_body_cage():
    bm = bmesh.new()
    cache = {}

    def vert(key):
        if key not in cache:
            cache[key] = bm.verts.new(lattice_pos(*key))
        return cache[key]

    regions = {}
    PAINT, GLASS, TRIM, UNDER = 0, 1, 2, 3

    def quad(keys, mat, region=None):
        f = bm.faces.new([vert(k) for k in keys])
        f.material_index = mat
        if region:
            regions.setdefault(region, []).append(f)

    for i in range(NX):
        for j in range(NY):
            quad([(i, j, 0), (i + 1, j, 0), (i + 1, j + 1, 0), (i, j + 1, 0)], UNDER)
            inner = 1 <= i <= NX - 2
            if inner and 5 <= j <= 7:
                quad([(i, j, NZ), (i + 1, j, NZ), (i + 1, j + 1, NZ), (i, j + 1, NZ)], GLASS, "windscreen")
            elif inner and j == 16:
                quad([(i, j, NZ), (i + 1, j, NZ), (i + 1, j + 1, NZ), (i, j + 1, NZ)], GLASS, "rearwindow")
            else:
                quad([(i, j, NZ), (i + 1, j, NZ), (i + 1, j + 1, NZ), (i, j + 1, NZ)], PAINT)
    for i in (0, NX):
        for j in range(NY):
            for k in range(NZ):
                keys = [(i, j, k), (i, j + 1, k), (i, j + 1, k + 1), (i, j, k + 1)]
                if 5 <= k <= 7 and 6 <= j <= 9:
                    quad(keys, GLASS, ("front", i))
                elif 5 <= k <= 7 and 11 <= j <= 13:
                    quad(keys, GLASS, ("rear", i))
                elif 5 <= k <= 7 and j in (5, 10):
                    quad(keys, TRIM)
                else:
                    quad(keys, PAINT)
    for j in (0, NY):
        for i in range(NX):
            for k in range(NZ):
                quad([(i, j, k), (i + 1, j, k), (i + 1, j, k + 1), (i, j, k + 1)], PAINT)

    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    cl = bm.edges.layers.float.new('crease_edge')

    def crease(k1, k2, val):
        e = bm.edges.get((cache[k1], cache[k2]))
        if e is not None:
            e[cl] = max(e[cl], val)

    for i in (0, NX):
        for j in range(NY):
            crease((i, j, 4), (i, j + 1, 4), 0.75)                 # shoulder / character line
            crease((i, j, 1), (i, j + 1, 1), 0.4)                  # sill
            crease((i, j, NZ), (i, j + 1, NZ), 0.3 if j < 5 else 0.5)   # fender crest / roof rail
            if 5 <= j <= 15:
                crease((i, j, 5), (i, j + 1, 5), 0.3)
        for k in range(NZ):
            crease((i, NY, k), (i, NY, k + 1), 0.3)                # tail corners
    for i in range(NX):
        for j, val in ((0, 0.4), (5, 0.5), (15, 0.9), (16, 0.7), (17, 0.4), (NY, 0.5)):
            crease((i, j, NZ), (i + 1, j, NZ), val)
        crease((i, 0, 4), (i + 1, 0, 4), 0.3)
    for key, faces in regions.items():
        th = 0.030 if key in ("windscreen", "rearwindow") else 0.022
        res = bmesh.ops.inset_region(bm, faces=faces, thickness=th, depth=0.0,
                                     use_even_offset=True, use_boundary=True)
        for f in res["faces"]:
            f.material_index = TRIM
    me = bpy.data.meshes.new("BodyCage")
    bm.to_mesh(me)
    bm.free()
    return me


def build_cutter():
    bm = bmesh.new()
    segs = 48
    for s_ax in (S_FRONT_AXLE, S_REAR_AXLE):
        for sg in (1, -1):
            rings = []
            for x in (0.50 * sg, 1.10 * sg):
                rings.append([bm.verts.new(V(x, s_ax + ARCH_R * math.cos(2 * math.pi * a / segs),
                                             TYRE_R + ARCH_R * math.sin(2 * math.pi * a / segs)))
                              for a in range(segs)])
            for a in range(segs):
                b = (a + 1) % segs
                bm.faces.new([rings[0][a], rings[0][b], rings[1][b], rings[1][a]])
            bm.faces.new(rings[0])
            bm.faces.new(rings[1])
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    for f in bm.faces:
        f.material_index = 3
    me = bpy.data.meshes.new("Cutter")
    bm.to_mesh(me)
    bm.free()
    return me


SLOT_NAMES = ["CarPaint", "Glass", "TrimBlack", "Underbody"]
cage = new_obj("BodyCage", build_body_cage())
cutter = new_obj("Cutter", build_cutter())
for o in (cage, cutter):
    for n in SLOT_NAMES:
        o.data.materials.append(MATS[n])
sub = cage.modifiers.new("sub", 'SUBSURF')
sub.levels = 2
boo = cage.modifiers.new("wells", 'BOOLEAN')
boo.operation = 'DIFFERENCE'
boo.object = cutter
try:
    boo.solver = 'EXACT'
except Exception as e:
    print("solver:", e)
try:
    boo.material_mode = 'INDEX'
except Exception as e:
    print("material_mode:", e)
dg = bpy.context.evaluated_depsgraph_get()
dg.update()
shell_me = bpy.data.meshes.new_from_object(cage.evaluated_get(dg), depsgraph=dg)
shell_slots = [m.name if m else "CarPaint" for m in shell_me.materials]
for o in (cage, cutter):
    bpy.data.objects.remove(o)

shell = bmesh.new()
shell.from_mesh(shell_me)
shell.normal_update()
shell.verts.ensure_lookup_table()
shell.faces.ensure_lookup_table()
bvh = BVHTree.FromBMesh(shell)
print("shell: %d verts, %d faces" % (len(shell.verts), len(shell.faces)))
xs = [v.co.x for v in shell.verts]; ys = [v.co.y for v in shell.verts]; zs = [v.co.z for v in shell.verts]
print("shell size  L=%.3f W=%.3f H=%.3f" % (max(ys) - min(ys), max(xs) - min(xs), max(zs)))


# ------------------------------------------------------------------- doors
# Each door is its own object, with its origin on the hinge line, so it can swing open about its vertical axis.
# Outlines follow the shut lines drawn further down: (z, s) of the leading edge of the front door / trailing
# edge of the rear door, the B-pillar split between them, and the sill.
DOOR_Z0 = 0.25
DOOR_F_EDGE = [(0.25, 1.40), (0.50, 1.385), (0.80, 1.385), (0.895, 1.40)]
DOOR_R_EDGE = [(0.25, 3.14), (0.42, 3.13), (0.56, 3.19), (0.66, 3.28), (0.73, 3.36), (0.82, 3.40), (0.965, 3.41)]
S_BPILLAR = 2.62
DOORS, HINGES = {}, {}
for _nm, _sg, _sh in (("Door_FL", 1, 1.40), ("Door_FR", -1, 1.40), ("Door_RL", 1, S_BPILLAR), ("Door_RR", -1, S_BPILLAR)):
    HINGES[_nm] = V(_sg * 0.88, _sh, 0.60)


def door_of(f):
    c, n = f.calc_center_median(), f.normal
    if abs(n.x) < 0.5 or abs(c.x) < 0.5 or c.z < DOOR_Z0:
        return None
    s = c.y + S_MID
    side = "L" if c.x > 0 else "R"
    if lerp_table(DOOR_F_EDGE, c.z) <= s < S_BPILLAR:
        return "Door_F" + side
    if S_BPILLAR <= s < lerp_table(DOOR_R_EDGE, c.z):
        return "Door_R" + side
    return None


FACE_DOOR = [door_of(f) for f in shell.faces]


def door_at(s, sg):
    return DOORS["Door_%s%s" % ("F" if s < S_BPILLAR else "R", "L" if sg > 0 else "R")]


def add_shell(mb, keep, mat_fn, flip=False, offset=0.0, part=None):
    remap, vs, fs, ms = {}, [], [], []
    for f in shell.faces:
        name = shell_slots[f.material_index]
        if not keep(name) or FACE_DOOR[f.index] != part:
            continue
        idx = []
        for v in f.verts:
            if v.index not in remap:
                remap[v.index] = len(vs)
                vs.append(v.co - v.normal * offset)
            idx.append(remap[v.index])
        if flip:
            idx.reverse()
        fs.append(idx)
        ms.append(mat_fn(name))
    base = len(mb.v)
    mb.v += vs
    mb.f += [[base + i for i in f] for f in fs]
    mb.m += ms


body = MB()
glass = MB()
interior = MB()
add_shell(body, lambda n: n != "Glass", lambda n: n)
add_shell(glass, lambda n: n == "Glass", lambda n: "Glass")
add_shell(interior, lambda n: n != "Glass", lambda n: "Interior", flip=True, offset=0.006)
for _nm in HINGES:
    DOORS[_nm] = MB()
    add_shell(DOORS[_nm], lambda n: n != "Glass", lambda n: n, part=_nm)
    add_shell(DOORS[_nm], lambda n: n == "Glass", lambda n: "Glass", part=_nm)
    add_shell(DOORS[_nm], lambda n: n != "Glass", lambda n: "Interior", flip=True, offset=0.006, part=_nm)


# ------------------------------------------------ projection onto the shell
RAYS = {
    "front": (lambda u, v: V(u, -3.0, v), Vector((0, 1, 0))),
    "rear":  (lambda u, v: V(u, 8.0, v), Vector((0, -1, 0))),
    "left":  (lambda u, v: V(3.0, u, v), Vector((-1, 0, 0))),
    "right": (lambda u, v: V(-3.0, u, v), Vector((1, 0, 0))),
    "top":   (lambda u, v: V(u, v, 3.0), Vector((0, 0, -1))),
}
_D = 0.70711
for _k, _sx, _sy, _c in (("fl", 1, 1, (0.78, 0.12)), ("fr", -1, 1, (-0.78, 0.12)),
                         ("rl", 1, -1, (0.80, 4.10)), ("rr", -1, -1, (-0.80, 4.10))):
    _d = Vector((-_sx * _D, _sy * _D, 0))
    _t = Vector((_sx * _D, _sy * _D, 0))
    RAYS[_k] = ((lambda u, v, c=_c, t=_t, d=_d: V(c[0], c[1], v) + t * u - d * 3.0), _d)


def cast(kind, u, v):
    org, d = RAYS[kind]
    loc, nrm, idx, dist = bvh.ray_cast(org(u, v), d)
    if loc is None and kind in ("front", "rear", "top"):
        for step in range(1, 40):                     # slide toward the centre line until we hit
            uu = u - math.copysign(0.01 * step, u)
            loc, nrm, idx, dist = bvh.ray_cast(org(uu, v), d)
            if loc is not None:
                break
    if loc is None:
        raise RuntimeError("ray miss %s %.3f %.3f" % (kind, u, v))
    if nrm.dot(d) > 0:
        nrm = -nrm
    return loc, nrm


def resample(pts, n):
    pts = [Vector(p) for p in pts]
    seg = [(pts[i + 1] - pts[i]).length for i in range(len(pts) - 1)]
    total = sum(seg)
    out = []
    for a in range(n + 1):
        d = total * a / n
        i = 0
        while i < len(seg) - 1 and d > seg[i]:
            d -= seg[i]
            i += 1
        t = d / seg[i] if seg[i] > 0 else 0
        out.append(pts[i].lerp(pts[i + 1], min(t, 1.0)))
    return out


def patch(mb, kind, top, bottom, nu, nv, mat, offset=0.004, rim=0.012, mirror=False):
    sg = -1 if mirror else 1
    tp, bt = resample(top, nu), resample(bottom, nu)
    P, N = [], []
    for iv in range(nv + 1):
        for iu in range(nu + 1):
            p = tp[iu].lerp(bt[iu], iv / nv)
            loc, nrm = cast(kind, p.x * sg if kind in ("front", "rear", "top") else p.x, p.y)
            P.append(loc + nrm * offset)
            N.append(nrm)
    idx = lambda iu, iv: iv * (nu + 1) + iu
    faces = [[idx(u, v), idx(u + 1, v), idx(u + 1, v + 1), idx(u, v + 1)] for v in range(nv) for u in range(nu)]
    d = RAYS[kind][1]
    mb.add(P, faces, mat, orient=lambda c: -d)
    loop = [idx(u, 0) for u in range(nu + 1)] + [idx(nu, v) for v in range(1, nv + 1)] + \
           [idx(u, nv) for u in range(nu - 1, -1, -1)] + [idx(0, v) for v in range(nv - 1, 0, -1)]
    cen = sum(P, Vector((0, 0, 0))) / len(P)
    rv = [P[i] for i in loop] + [P[i] - N[i] * (offset + rim) for i in loop]
    n = len(loop)
    rf = [[a, (a + 1) % n, n + (a + 1) % n, n + a] for a in range(n)]
    mb.add(rv, rf, mat, orient=lambda c: c - cen)


def ribbon(mb, kind, pts, mat="Underbody", width=0.006, offset=0.0025, mirror=False, closed=False):
    sg = -1 if mirror else 1
    pts = [Vector(p) for p in pts]
    if closed:
        pts = pts + [pts[0]]
    total = sum((pts[i + 1] - pts[i]).length for i in range(len(pts) - 1))
    rs = resample(pts, max(2, int(total / 0.02)))
    hits = [cast(kind, p.x * sg if kind in ("front", "rear", "top") else p.x, p.y) for p in rs]
    vs = []
    n = len(hits)
    for a in range(n):
        p0 = hits[max(a - 1, 0)][0] if not closed else hits[(a - 1) % (n - 1)][0]
        p1 = hits[min(a + 1, n - 1)][0] if not closed else hits[(a + 1) % (n - 1)][0]
        loc, nrm = hits[a]
        t = (p1 - p0).normalized()
        b = nrm.cross(t).normalized()
        vs += [loc + nrm * offset + b * width / 2, loc + nrm * offset - b * width / 2]
    fs = [[2 * a, 2 * a + 1, 2 * a + 3, 2 * a + 2] for a in range(n - 1)]
    d = RAYS[kind][1]
    mb.add(vs, fs, mat, orient=lambda c: -d)


def surface_frame(kind, u, v, lift=0.0):
    """Matrix whose local +X is the viewer's right, +Y up, +Z out of the surface."""
    loc, n = cast(kind, u, v)
    up = Vector((0, 0, 1))
    right = up.cross(n).normalized()
    up2 = n.cross(right).normalized()
    m = Matrix.Identity(4)
    for r in range(3):
        m[r][0], m[r][1], m[r][2], m[r][3] = right[r], up2[r], n[r], loc[r] + n[r] * lift
    return m


# ------------------------------------------------------------ front details
for mir in (False, True):
    patch(body, "front", [(0.36, 0.708), (0.55, 0.708), (0.70, 0.698), (0.765, 0.684)],
          [(0.405, 0.632), (0.55, 0.610), (0.68, 0.612), (0.765, 0.640)], 16, 5, "HeadlightLens", mirror=mir)
    # inner projector rings
    for cx in (0.52, 0.65):
        m = surface_frame("front", -cx if mir else cx, 0.660, lift=0.006)
        vs, fs = lathe([(0.036, 0.0), (0.029, 0.004), (0.0, 0.004)], 16, False)
        rot = Matrix(((0, 0, 1, 0), (0, 1, 0, 0), (1, 0, 0, 0), (0, 0, 0, 1)))  # lathe X axis -> local Z
        nz = m.to_3x3() @ Vector((0, 0, 1))
        body.add(vs, fs, "Chrome", orient=lambda c, nz=nz: nz, xf=m @ rot)
for kind in ("fl", "fr"):
    try:
        patch(body, kind, [(-0.10, 0.694), (0.12, 0.682)], [(-0.10, 0.628), (0.12, 0.646)], 6, 4, "HeadlightLens")
    except RuntimeError as e:
        print("corner lamp skipped:", e)
for kind in ("rl", "rr"):
    try:
        patch(body, kind, [(-0.11, 0.946), (0.11, 0.947)], [(-0.11, 0.846), (0.11, 0.857)], 6, 4, "TaillightLens")
    except RuntimeError as e:
        print("corner lamp skipped:", e)
# upper grille strip + chrome line
patch(body, "front", [(-0.36, 0.708), (0.36, 0.708)], [(-0.405, 0.634), (0.405, 0.634)], 20, 3, "TrimBlack")
patch(body, "front", [(-0.40, 0.646), (0.40, 0.646)], [(-0.40, 0.638), (0.40, 0.638)], 20, 1, "Chrome",
      offset=0.007, rim=0.003)
# lower intake
patch(body, "front", [(-0.62, 0.365), (-0.50, 0.385), (0.50, 0.385), (0.62, 0.365)],
      [(-0.57, 0.275), (-0.45, 0.262), (0.45, 0.262), (0.57, 0.275)], 24, 4, "TrimBlack")
for y in (0.300, 0.335):
    patch(body, "front", [(-0.50, y + 0.004), (0.50, y + 0.004)], [(-0.50, y - 0.004), (0.50, y - 0.004)],
          16, 1, "Underbody", offset=0.007, rim=0.002)

# ------------------------------------------------------------- rear details
for mir in (False, True):
    patch(body, "rear", [(0.34, 0.968), (0.55, 0.965), (0.68, 0.953), (0.745, 0.938)],
          [(0.37, 0.858), (0.55, 0.842), (0.67, 0.832), (0.745, 0.846)], 14, 5, "TaillightLens", mirror=mir)
for side in ("left", "right"):
    patch(body, side, [(3.90, 0.948), (4.05, 0.944)], [(3.945, 0.862), (4.05, 0.850)], 5, 4, "TaillightLens")
patch(body, "rear", [(-0.62, 0.395), (0.62, 0.395)], [(-0.58, 0.305), (0.58, 0.305)], 20, 3, "TrimBlack")


# ------------------------------------------------------------------- badges
def badge(kind, z, R):
    m = surface_frame(kind, 0.0, z, lift=0.007)
    seg = 28
    ring = [Vector((R * math.cos(2 * math.pi * a / seg), R * math.sin(2 * math.pi * a / seg), 0.0)) for a in range(seg)]
    inner = [p * 0.84 for p in ring]
    nz = m.to_3x3() @ Vector((0, 0, 1))
    o = lambda c: nz
    body.add(ring + inner, [[a, (a + 1) % seg, seg + (a + 1) % seg, seg + a] for a in range(seg)], "Chrome", o, m)
    body.add(inner, [list(range(seg))], "PlateBlue", o, m)
    back = [p + Vector((0, 0, -0.012)) for p in ring]
    body.add(ring + back, [[a, (a + 1) % seg, seg + (a + 1) % seg, seg + a] for a in range(seg)], "Chrome",
             lambda c: c - (m @ Vector((0, 0, 0))), m)
    strokes = [[(-0.34, 0.66), (0.0, 0.12), (0.34, 0.66)],
               [(-0.60, 0.22), (-0.27, -0.62), (0.0, 0.0), (0.27, -0.62), (0.60, 0.22)]]
    w = 0.055 * R
    for line in strokes:
        for (x0, y0), (x1, y1) in zip(line, line[1:]):
            p0, p1 = Vector((x0 * R, y0 * R, 0.0015)), Vector((x1 * R, y1 * R, 0.0015))
            d = (p1 - p0).normalized()
            n = Vector((-d.y, d.x, 0)) * w
            body.add([p0 - d * w * 0.5 - n, p1 + d * w * 0.5 - n, p1 + d * w * 0.5 + n, p0 - d * w * 0.5 + n],
                     [[0, 1, 2, 3]], "Chrome", o, m)


badge("front", 0.676, 0.060)
badge("rear", 0.905, 0.058)


# ------------------------------------------------------------ number plates
def plate(kind, z, text="AF 215"):
    m = surface_frame(kind, 0.0, z, lift=0.010)
    nz = m.to_3x3() @ Vector((0, 0, 1))
    vs, fs = rbox((0.52, 0.11, 0.012), bevel=0.003, seg=1)
    body.add(vs, fs, "PlateWhite", xf=m)
    vs, fs = rbox((0.045, 0.104, 0.0125), bevel=0.0, seg=1)
    body.add(vs, fs, "PlateBlue", xf=m @ Matrix.Translation((-0.235, 0, 0.0005)))
    cu = bpy.data.curves.new("platetext", 'FONT')
    cu.body = text
    cu.size = 0.092
    cu.align_x = 'CENTER'
    cu.align_y = 'CENTER'
    ob = bpy.data.objects.new("platetext", cu)
    scene.collection.objects.link(ob)
    d2 = bpy.context.evaluated_depsgraph_get()
    d2.update()
    tm = bpy.data.meshes.new_from_object(ob.evaluated_get(d2), depsgraph=d2)
    tv = [Vector((v.co.x + 0.02, v.co.y, 0.0075)) for v in tm.vertices]
    tf = [list(p.vertices) for p in tm.polygons]
    body.add(tv, tf, "PlateBlue", orient=lambda c: nz, xf=m)
    bpy.data.objects.remove(ob)
    bpy.data.meshes.remove(tm)


plate("front", 0.505)
plate("rear", 0.655)

# --------------------------------------------------------------- shut lines
ARC_R = [(3.14, 0.25), (3.13, 0.42), (3.19, 0.56), (3.28, 0.66), (3.36, 0.73), (3.40, 0.82), (3.41, 0.965)]
for side in ("left", "right"):
    ribbon(body, side, [(1.40, 0.25), (1.385, 0.50), (1.385, 0.80), (1.40, 0.895)])
    ribbon(body, side, [(2.62, 0.25), (2.62, 0.935)])
    ribbon(body, side, [(1.40, 0.25), (3.14, 0.25)])
    ribbon(body, side, ARC_R)
flap = [(3.80 + 0.075 * math.copysign(abs(math.cos(a)) ** 0.5, math.cos(a)),
         0.80 + 0.075 * math.copysign(abs(math.sin(a)) ** 0.5, math.sin(a)))
        for a in [2 * math.pi * i / 24 for i in range(24)]]
ribbon(body, "right", flap, closed=True)
for mir in (False, True):
    ribbon(body, "top", [(0.655, 0.13), (0.70, 0.34), (0.725, 0.70), (0.732, 1.00), (0.728, 1.25)], mirror=mir)
    ribbon(body, "rear", [(0.67, 0.975), (0.665, 0.80), (0.645, 0.62), (0.61, 0.50), (0.0, 0.50)], mirror=mir)

# ------------------------------------------------------------- door handles
for side, sg in (("left", 1), ("right", -1)):
    for s, z in ((2.43, 0.835), (3.21, 0.846)):
        loc, n = cast(side, s, z)
        vs, fs = rbox((0.036, 0.19, 0.034), bevel=0.012, seg=3)
        door_at(s, sg).add(vs, fs, "CarPaint", xf=Matrix.Translation(loc + n * 0.006))

# ------------------------------------------------------------------ mirrors
for side, sg in (("left", 1), ("right", -1)):
    loc, n = cast(side, 1.50, 0.975)
    c = loc + Vector((0.125 * sg, 0.0, 0.02))
    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=20, v_segments=12, radius=1.0)
    for v in bm.verts:
        y = min(v.co.y, 0.55)
        v.co = Vector((v.co.x * 0.105, y * 0.062, v.co.z * 0.068))
    bm.verts.index_update()
    vs = [v.co.copy() for v in bm.verts]
    fs = [[v.index for v in f.verts] for f in bm.faces]
    bm.free()
    door = door_at(1.50, sg)                 # the mirror rides on the front door
    door.add(vs, fs, "CarPaint", xf=Matrix.Translation(c))
    seg = 20
    dv = [Vector((0.082 * math.cos(2 * math.pi * a / seg), 0.062 * 0.55 + 0.001, 0.052 * math.sin(2 * math.pi * a / seg)))
          for a in range(seg)]
    door.add(dv, [list(range(seg))], "Chrome", orient=lambda c2: Vector((0, 1, 0)), xf=Matrix.Translation(c))
    vs, fs = rbox((0.10, 0.06, 0.030), bevel=0.008, seg=2)
    door.add(vs, fs, "TrimBlack", xf=Matrix.Translation(loc + Vector((0.035 * sg, 0.0, -0.01))))

# shark-fin aerial
loc, n = cast("top", 0.0, 3.47)
bm = bmesh.new()
bmesh.ops.create_cone(bm, cap_ends=True, segments=14, radius1=1.0, radius2=0.12, depth=1.0)
for v in bm.verts:
    h = v.co.z + 0.5
    v.co = Vector((v.co.x * 0.028, v.co.y * 0.075 + h * 0.055, h * 0.055))
bm.verts.index_update()
body.add([v.co.copy() for v in bm.verts], [[v.index for v in f.verts] for f in bm.faces], "TrimBlack",
         xf=Matrix.Translation(loc - Vector((0, 0, 0.003))))
bm.free()

# wipers
for x0, x1 in ((0.52, -0.02), (-0.08, -0.60)):
    p0, _ = cast("top", x0, 1.36 if abs(x0) > 0.3 else 1.25)
    p1, _ = cast("top", x1, 1.36 if abs(x1) > 0.3 else 1.25)
    mid = (p0 + p1) / 2 + Vector((0, 0, 0.012))
    d = (p1 - p0)
    xa = d.normalized()
    za = Vector((0, 0, 1))
    ya = za.cross(xa).normalized()
    za = xa.cross(ya)
    m = Matrix.Identity(4)
    for r in range(3):
        m[r][0], m[r][1], m[r][2], m[r][3] = xa[r], ya[r], za[r], mid[r]
    vs, fs = rbox((d.length, 0.022, 0.014), bevel=0.004, seg=1)
    body.add(vs, fs, "Underbody", xf=m)

# ================================================================= INTERIOR
SEAT_ANCHORS = {}


def seat(x, s, width, name=None):
    vs, fs = rbox((width, 0.50, 0.14), bevel=0.04)
    interior.add(vs, fs, "Seat", xf=T(x, s, 0.345) @ RX(6))
    vs, fs = rbox((width - 0.02, 0.12, 0.62), bevel=0.04)
    interior.add(vs, fs, "Seat", xf=T(x, s + 0.31, 0.70) @ RX(-18))
    if name:
        SEAT_ANCHORS[name] = V(x, s + 0.05, 0.43)


def headrest(x, s, z):
    vs, fs = rbox((0.25, 0.10, 0.18), bevel=0.035)
    interior.add(vs, fs, "Seat", xf=T(x, s, z) @ RX(-12))
    vs, fs = rbox((0.10, 0.02, 0.12), bevel=0.0, seg=1)
    interior.add(vs, fs, "TrimBlack", xf=T(x, s - 0.01, z - 0.12) @ RX(-12))


seat(0.37, 2.38, 0.50, "Seat_Driver")
seat(-0.37, 2.38, 0.50, "Seat_FrontPassenger")
headrest(0.37, 2.81, 1.09)
headrest(-0.37, 2.81, 1.09)
# rear bench
vs, fs = rbox((1.22, 0.50, 0.14), bevel=0.04)
interior.add(vs, fs, "Seat", xf=T(0, 3.25, 0.355) @ RX(5))
vs, fs = rbox((1.22, 0.12, 0.60), bevel=0.04)
interior.add(vs, fs, "Seat", xf=T(0, 3.58, 0.70) @ RX(-20))
for x, nm in ((0.40, "Seat_RearLeft"), (0.0, "Seat_RearMiddle"), (-0.40, "Seat_RearRight")):
    headrest(x, 3.71, 1.06)
    SEAT_ANCHORS[nm] = V(x, 3.30, 0.43)
# parcel shelf
vs, fs = rbox((1.20, 0.36, 0.02), bevel=0.0, seg=1)
interior.add(vs, fs, "Interior", xf=T(0, 3.86, 0.93))
# floor
vs, fs = rbox((1.46, 2.55, 0.02), bevel=0.0, seg=1)
interior.add(vs, fs, "Interior", xf=T(0, 2.62, 0.265))

# dashboard (extruded profile)
prof = [(1.30, 0.50), (1.30, 0.925), (1.55, 0.972), (1.73, 0.975), (1.80, 0.925), (1.81, 0.70), (1.72, 0.50)]
hw = 0.70
dv = [V(-hw, s, z) for s, z in prof] + [V(hw, s, z) for s, z in prof]
n = len(prof)
df = [[a, (a + 1) % n, n + (a + 1) % n, n + a] for a in range(n)] + [list(range(n)), list(range(n, 2 * n))]
dc = sum(dv, Vector((0, 0, 0))) / len(dv)
interior.add(dv, df, "Interior", orient=lambda c: c - dc)
# instrument binnacle, centre screen, console, gear knob
vs, fs = rbox((0.36, 0.20, 0.07), bevel=0.025)
interior.add(vs, fs, "Interior", xf=T(0.37, 1.70, 0.985))
vs, fs = rbox((0.24, 0.012, 0.13), bevel=0.004, seg=1)
interior.add(vs, fs, "TrimBlack", xf=T(0.0, 1.812, 0.845) @ RX(-4))
vs, fs = rbox((0.22, 1.05, 0.26), bevel=0.03)
interior.add(vs, fs, "Interior", xf=T(0.0, 2.30, 0.40))
vs, fs = rbox((0.05, 0.05, 0.10), bevel=0.02)
interior.add(vs, fs, "TrimBlack", xf=T(0.0, 2.08, 0.57))

# steering wheel (own object so it can turn about its local X axis)
sw = MB()
SW_R = 0.185
tube = [(SW_R + 0.016 * math.cos(2 * math.pi * a / 8), 0.016 * math.sin(2 * math.pi * a / 8)) for a in range(8)]
vs, fs = lathe(tube, 28, True)
sw.add(vs, fs, "TrimBlack", orient=lambda c: c - Vector((0, c.y, c.z)).normalized() * SW_R)
vs, fs = rbox((0.05, 0.10, 0.08), bevel=0.02)
sw.add(vs, fs, "TrimBlack", xf=Matrix.Translation((-0.015, 0, 0)))
for dims, cen in (((0.014, 0.034, 0.17), (-0.008, 0, -0.095)), ((0.014, 0.17, 0.034), (-0.008, 0.095, 0.0)),
                  ((0.014, 0.17, 0.034), (-0.008, -0.095, 0.0))):
    vs, fs = rbox(dims, bevel=0.005, seg=1)
    sw.add(vs, fs, "TrimBlack", xf=Matrix.Translation(cen))
vs, fs = rbox((0.20, 0.05, 0.05), bevel=0.01, seg=1)           # column
sw.add(vs, fs, "Interior", xf=Matrix.Translation((-0.11, 0, 0)))

# =================================================================== WHEELS
wh = MB()
hw_t = TYRE_W / 2
tyre = [(0.204, 0.086), (0.235, 0.100), (0.272, hw_t), (0.298, 0.098), (0.311, 0.084), (TYRE_R, 0.055),
        (TYRE_R, -0.055), (0.311, -0.084), (0.298, -0.098), (0.272, -hw_t), (0.235, -0.100), (0.204, -0.086),
        (0.198, -0.08), (0.198, 0.08)]
SEG = 36
vs, fs = lathe(tyre, SEG, True)
wh.add(vs, fs, "Tyre", orient=lambda c: c - Vector((0, c.y, c.z)).normalized() * 0.26)
for gx in (-0.03, 0.03):                                         # tread grooves
    vs, fs = lathe([(TYRE_R + 0.0008, gx - 0.006), (TYRE_R + 0.0008, gx + 0.006)], SEG, False)
    wh.add(vs, fs, "Underbody", orient=lambda c: Vector((0, c.y, c.z)))
rim = [(0.209, 0.090), (0.209, 0.096), (0.199, 0.096), (0.192, 0.080), (0.186, 0.040), (0.186, -0.085), (0.206, -0.092)]
vs, fs = lathe(rim, SEG, False)
wh.add(vs, fs, "Alloy", orient=lambda c: Vector((1.2, -c.y, -c.z)) if c.x > 0.085 else Vector((0.0, -c.y, -c.z)))
vs, fs = disc(0.188, -0.03, SEG)
wh.add(vs, fs, "Underbody", orient=lambda c: Vector((1, 0, 0)))
vs, fs = disc(0.188, -0.088, SEG)
wh.add(vs, fs, "Underbody", orient=lambda c: Vector((-1, 0, 0)))
vs, fs = lathe([(0.150, 0.0), (0.150, 0.022), (0.0, 0.022)], SEG, False)   # brake disc
wh.add(vs, fs, "BrakeDisc", orient=lambda c: Vector((1, c.y, c.z)))
vs, fs = lathe([(0.062, 0.02), (0.062, 0.068), (0.050, 0.078), (0.030, 0.080), (0.0, 0.080)], 20, False)
wh.add(vs, fs, "Alloy", orient=lambda c: Vector((1, c.y * 4, c.z * 4)))
vs, fs = disc(0.027, 0.0815, 16)
wh.add(vs, fs, "TrimBlack", orient=lambda c: Vector((1, 0, 0)))
for sp in range(5):
    th = 2 * math.pi * sp / 5 + math.pi / 2
    for dsg in (-1, 1):
        a1, a2 = th + dsg * 0.16, th + dsg * 0.24
        p1 = Vector((0, 0.050 * math.cos(a1), 0.050 * math.sin(a1)))
        p2 = Vector((0, 0.196 * math.cos(a2), 0.196 * math.sin(a2)))
        d = (p2 - p1).normalized()
        pr = Vector((0, -d.z, d.y))
        pts = []
        for p, hwid, xf_, in ((p1, 0.013, 0.072), (p2, 0.010, 0.088)):
            for xx in (xf_, xf_ - 0.024):
                pts += [p + pr * hwid + Vector((xx, 0, 0)), p - pr * hwid + Vector((xx, 0, 0))]
        # pts: 0,1 front@p1  2,3 back@p1  4,5 front@p2  6,7 back@p2
        faces = [[0, 1, 5, 4], [2, 3, 7, 6], [0, 2, 6, 4], [1, 3, 7, 5], [0, 1, 3, 2], [4, 5, 7, 6]]
        cen = sum(pts, Vector((0, 0, 0))) / 8
        wh.add(pts, faces, "Alloy", orient=lambda c, cen=cen: c - cen)

# ================================================================= ASSEMBLE
objs = []
objs.append(new_obj("Body", body.to_mesh("Body", 42)))
objs.append(new_obj("Glass", glass.to_mesh("Glass", 60)))
objs.append(new_obj("Interior", interior.to_mesh("Interior", 42)))
for _nm, _mb in DOORS.items():
    _mb.v = [v - HINGES[_nm] for v in _mb.v]
    objs.append(new_obj(_nm, _mb.to_mesh(_nm, 42), loc=HINGES[_nm]))
    print("door %s: %d tris" % (_nm, _mb.tris()))
sw_obj = new_obj("SteeringWheel", sw.to_mesh("SteeringWheel", 42), loc=V(0.37, 1.94, 0.90))
sw_obj.rotation_euler = (RX(25) @ Matrix.Rotation(math.radians(90), 4, 'Z')).to_euler()
objs.append(sw_obj)
wheel_mesh = wh.to_mesh("Wheel", 35)
for nm, sg, s_ax, tr in (("Wheel_FL", 1, S_FRONT_AXLE, TRACK_F), ("Wheel_FR", -1, S_FRONT_AXLE, TRACK_F),
                         ("Wheel_RL", 1, S_REAR_AXLE, TRACK_R), ("Wheel_RR", -1, S_REAR_AXLE, TRACK_R)):
    objs.append(new_obj(nm, wheel_mesh, loc=V(sg * tr / 2, s_ax, TYRE_R),
                        rot=(0, 0, 0) if sg > 0 else (0, 0, math.pi)))
for nm, p in SEAT_ANCHORS.items():
    e = bpy.data.objects.new(nm, None)
    e.empty_display_size = 0.15
    e.location = p
    scene.collection.objects.link(e)
    objs.append(e)

print("tris: body %d  glass %d  interior %d  steering %d  wheel %d (x4)  TOTAL %d" % (
    body.tris(), glass.tris(), interior.tris(), sw.tris(), wh.tris(),
    body.tris() + glass.tris() + interior.tris() + sw.tris() + 4 * wh.tris()))

bpy.ops.wm.save_as_mainfile(filepath=os.path.join(HERE, "VWGolf.blend"))

for o in scene.objects:
    o.select_set(o in objs)
bpy.context.view_layer.objects.active = objs[0]
os.makedirs(os.path.dirname(FBX_PATH), exist_ok=True)
bpy.ops.export_scene.fbx(filepath=FBX_PATH, use_selection=True, object_types={'MESH', 'EMPTY'},
                         apply_scale_options='FBX_SCALE_UNITS', axis_forward='-Z', axis_up='Y',
                         bake_space_transform=True, mesh_smooth_type='OFF', add_leaf_bones=False,
                         bake_anim=False)
print("exported", FBX_PATH, "(%.1fs)" % (time.time() - T0))

# ================================================================== PREVIEW
if DO_RENDER:
    prev = os.path.join(HERE, "preview")
    os.makedirs(prev, exist_ok=True)
    gm = bpy.data.meshes.new("Ground")
    gm.from_pydata([(-40, -40, 0), (40, -40, 0), (40, 40, 0), (-40, 40, 0)], [], [[0, 1, 2, 3]])
    gm.materials.append(make_mat("Ground", (0.16, 0.16, 0.17), rough=0.7))
    new_obj("Ground", gm)
    sun = bpy.data.lights.new("Sun", 'SUN')
    sun.energy = 3.5
    sun.angle = math.radians(8)
    so = bpy.data.objects.new("Sun", sun)
    so.rotation_euler = (math.radians(52), 0, math.radians(-35))
    scene.collection.objects.link(so)
    w = bpy.data.worlds.new("World")
    try:
        w.use_nodes = True
    except Exception:
        pass
    bg = next((nd for nd in w.node_tree.nodes if nd.type == 'BACKGROUND'), None)
    if bg:
        bg.inputs[0].default_value = (0.55, 0.66, 0.78, 1)
        bg.inputs[1].default_value = 1.1
    scene.world = w
    scene.render.engine = 'CYCLES'
    scene.cycles.samples = 40
    try:
        scene.cycles.use_denoising = True
    except Exception:
        pass
    scene.render.resolution_x, scene.render.resolution_y = 1100, 620
    scene.render.image_settings.file_format = 'JPEG'
    cam = bpy.data.cameras.new("Cam")
    co = bpy.data.objects.new("Cam", cam)
    scene.collection.objects.link(co)
    scene.camera = co
    shots = {
        "front34": ((3.3, -4.6, 1.45), (0, -0.2, 0.62), 40),
        "rear34": ((-3.4, 4.8, 1.55), (0, 0.2, 0.65), 40),
        "side": ((8.5, 0.0, 0.95), (0, 0.0, 0.70), 70),
        "front": ((0, -8.0, 0.85), (0, 0, 0.70), 80),
        "rear": ((0, 8.0, 0.95), (0, 0, 0.72), 80),
        "top": ((0.01, 0.0, 9.0), (0, 0, 0.5), 60),
        "interior": ((1.9, 0.9, 1.75), (0.1, 0.2, 0.75), 35),
    }
    only = [a[7:] for a in ARGV if a.startswith("--shot=")]
    for name, (loc, tgt, lens) in shots.items():
        if only and name not in only:
            continue
        co.location = loc
        cam.lens = lens
        co.rotation_euler = (Vector(tgt) - Vector(loc)).to_track_quat('-Z', 'Y').to_euler()
        scene.render.filepath = os.path.join(prev, "golf_%s.jpg" % name)
        bpy.ops.render.render(write_still=True)
    print("renders done (%.1fs)" % (time.time() - T0))
