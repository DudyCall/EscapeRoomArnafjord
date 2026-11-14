"""
Old single-lane Árnafjørður tunnel: raw blasted rock, no lining, no lights.

Run:  blender --background --python build_tunnel.py -- <unity Assets/Environment/Tunnel dir> [--no-render]

Produces two tileable 24 m pieces (same orientation as the car: travel direction is Blender -Y / Unity +Z,
origin on the road centre line at the start of the piece):
  TunnelStraight.fbx    - plain bore
  TunnelPassingBay.fbx  - bore with a passing place blasted out on the right
plus tileable rock / asphalt textures.
"""
import bpy, bmesh, math, os, sys
import numpy as np
from mathutils import Vector, noise

HERE = os.path.dirname(os.path.abspath(__file__))
ARGV = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
OUT = next((a for a in ARGV if not a.startswith("--")), os.path.join(HERE, "out"))
DO_RENDER = "--no-render" not in ARGV
TEX = os.path.join(OUT, "Textures")
os.makedirs(TEX, exist_ok=True)

L = 24.0            # piece length
DS = 0.30           # ring spacing
NR = int(round(L / DS))
M = 44              # wall quads around the bore
HALF_W, HEIGHT = 2.25, 4.4
ROAD_HALF = 1.55
BAY_EXTRA = 2.6
UV_M = 4.0          # metres per texture tile

bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene


# ------------------------------------------------------------------ textures
def fractal(n, beta, seed):
    rng = np.random.default_rng(seed)
    F = np.fft.fft2(rng.standard_normal((n, n)))
    fx = np.fft.fftfreq(n)[:, None]
    fy = np.fft.fftfreq(n)[None, :]
    f = np.sqrt(fx * fx + fy * fy)
    f[0, 0] = 1.0
    F /= f ** beta
    F[0, 0] = 0.0
    out = np.real(np.fft.ifft2(F))
    return (out - out.min()) / (out.max() - out.min())


def normal_from_height(h, strength):
    dx = (np.roll(h, -1, 1) - np.roll(h, 1, 1)) * strength
    dy = (np.roll(h, -1, 0) - np.roll(h, 1, 0)) * strength
    nz = np.ones_like(h)
    ln = np.sqrt(dx * dx + dy * dy + nz * nz)
    return np.stack([-dx / ln * 0.5 + 0.5, -dy / ln * 0.5 + 0.5, nz / ln * 0.5 + 0.5], -1)


def save_png(name, rgb, non_color=False):
    n = rgb.shape[0]
    img = bpy.data.images.new(name, n, n, alpha=False)
    if non_color:
        img.colorspace_settings.name = 'Non-Color'
    rgba = np.concatenate([np.clip(rgb, 0, 1), np.ones((n, n, 1))], -1).astype(np.float32)
    img.pixels.foreach_set(rgba.ravel())
    img.filepath_raw = os.path.join(TEX, name + ".png")
    img.file_format = 'PNG'
    img.save()
    return img


N = 1024
big = fractal(N, 1.9, 1)
mid = fractal(N, 1.3, 2)
ridge = 1.0 - np.abs(2.0 * fractal(N, 1.7, 3) - 1.0)            # cracks / fracture edges
grain = fractal(N, 0.5, 4)
height = 0.45 * big + 0.25 * mid + 0.30 * ridge ** 2
tint = fractal(N, 2.2, 5)[..., None]
basalt = np.array([0.150, 0.150, 0.158])
brown = np.array([0.255, 0.215, 0.180])
rock = (basalt * (1 - tint) + brown * tint) * (0.55 + 0.9 * height[..., None]) * (0.85 + 0.3 * grain[..., None])
damp = np.clip((fractal(N, 2.4, 6) - 0.62) * 6.0, 0, 1)[..., None]     # darker seepage patches
rock = rock * (1 - 0.45 * damp)
rock_img = save_png("Tunnel_Rock_Albedo", rock)
save_png("Tunnel_Rock_Normal", normal_from_height(height + 0.08 * grain, 22.0), non_color=True)

N2 = 512
g1, g2 = fractal(N2, 0.4, 11), fractal(N2, 1.8, 12)
u = np.linspace(0, 1, N2, endpoint=False)[None, :]
tracks = np.exp(-((u - 0.27) / 0.08) ** 2) + np.exp(-((u - 0.73) / 0.08) ** 2)
shade = (0.085 + 0.06 * g1 + 0.05 * g2) * (1.0 + 0.22 * tracks)
road = np.stack([shade, shade, shade * 1.03], -1)
road_img = save_png("Tunnel_Road_Albedo", road)
save_png("Tunnel_Road_Normal", normal_from_height(g1, 5.0), non_color=True)


def make_mat(name, img, rough):
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
    t = nt.nodes.new("ShaderNodeTexImage")
    t.image = img
    nt.links.new(t.outputs["Color"], b.inputs["Base Color"])
    b.inputs["Roughness"].default_value = rough
    return m


MAT_ROCK = make_mat("TunnelRock", rock_img, 0.7)
MAT_ROAD = make_mat("TunnelRoad", road_img, 0.8)


# ----------------------------------------------------------------- geometry
def smooth(a, b, x):
    t = min(max((x - a) / (b - a), 0.0), 1.0)
    return t * t * (3 - 2 * t)


RC = L / (2 * math.pi)       # sample noise on a circle so piece ends match exactly


def rough(s, um, seed):
    al = 2 * math.pi * s / L
    p = Vector((RC * math.cos(al), RC * math.sin(al), um + seed * 37.0))
    d = 0.30 * noise.fractal(p * 0.55, 1.0, 2.0, 4)
    d += 0.16 * noise.fractal(p * 1.7 + Vector((9, 4, 2)), 0.9, 2.1, 3)
    d += 0.13 * (noise.cell(p * 1.25) )                 # angular, blocky break-outs
    return max(-0.18, min(0.55, d + 0.10))


def build(name, bay, seed):
    verts, uvs, faces, mats = [], [], [], []
    wall = [[0] * (M + 1) for _ in range(NR + 1)]
    floor = [None] * (NR + 1)
    for r in range(NR + 1):
        s = r * DS
        ext = BAY_EXTRA * smooth(3.5, 8.0, s) * (1 - smooth(16.0, 20.5, s)) if bay else 0.0
        um = 0.0
        prev = None
        for m in range(M + 1):
            ph = math.pi * (1 - m / M)
            c, sn = math.cos(ph), math.sin(ph)
            a = HALF_W + (ext if c < 0 else 0.0)
            hh = HEIGHT + 0.25 * ext / BAY_EXTRA * max(0.0, -c) if bay else HEIGHT
            base = Vector((a * math.copysign(abs(c) ** 0.6, c), hh * abs(sn) ** 0.75))
            if prev is not None:
                um += (base - prev).length
            prev = base
            d = rough(s % L if r < NR else 0.0, um * (1.0 if not bay else HALF_W / a if c < 0 else 1.0), seed)
            out = (base - Vector((0.0, 1.5)))
            out.normalize()
            if m in (0, M):
                out = Vector((math.copysign(1.0, base.x), 0.0))
            p = base + out * d
            z = max(p.y, 0.0) + (0.05 if m in (0, M) else 0.0)
            wall[r][m] = len(verts)
            verts.append(Vector((p.x, -s, z)))
            uvs.append((m / 16.0, s / UV_M))
        xr = -(ROAD_HALF + ext)                       # car's right-hand side is -X
        xl = ROAD_HALF
        ids = {}
        for key, x, z, uv in (("rr", xr, -0.03, (0.0, s / UV_M)), ("c", 0.0, 0.0, (-xr / (xl - xr), s / UV_M)),
                              ("rl", xl, -0.03, (1.0, s / UV_M)),
                              ("sr", xr, -0.03, (-(abs(verts[wall[r][0]].x - xr)) / UV_M, s / UV_M)),
                              ("sl", xl, -0.03, (M / 16.0 + abs(verts[wall[r][M]].x - xl) / UV_M, s / UV_M))):
            ids[key] = len(verts)
            verts.append(Vector((x, -s, z)))
            uvs.append(uv)
        floor[r] = ids
    for r in range(NR):
        for m in range(M):
            faces.append([wall[r][m], wall[r][m + 1], wall[r + 1][m + 1], wall[r + 1][m]])
            mats.append(0)
        a, b = floor[r], floor[r + 1]
        faces += [[a["rr"], a["c"], b["c"], b["rr"]], [a["c"], a["rl"], b["rl"], b["c"]]]
        mats += [1, 1]
        faces += [[wall[r][0], a["sr"], b["sr"], wall[r + 1][0]], [a["sl"], wall[r][M], wall[r + 1][M], b["sl"]]]
        mats += [0, 0]
    # make every face look into the bore
    for i, f in enumerate(faces):
        c = sum((verts[k] for k in f), Vector((0, 0, 0))) / len(f)
        n = (verts[f[1]] - verts[f[0]]).cross(verts[f[2]] - verts[f[0]])
        want = Vector((-c.x * 0.3, 0.0, 1.0)) if mats[i] == 1 or c.z < 0.06 else Vector((-c.x, 0.0, 1.9 - c.z))
        if n.dot(want) < 0:
            f.reverse()
    me = bpy.data.meshes.new(name)
    me.from_pydata([tuple(v) for v in verts], [], faces)
    me.materials.append(MAT_ROCK)
    me.materials.append(MAT_ROAD)
    me.polygons.foreach_set("material_index", mats)
    me.polygons.foreach_set("use_smooth", [True] * len(faces))
    uvl = me.uv_layers.new(name="UVMap")
    for lp in me.loops:
        uvl.data[lp.index].uv = uvs[lp.vertex_index]
    me.update()
    try:
        me.set_sharp_from_angle(angle=math.radians(38))
    except Exception as e:
        print("sharp:", e)
    ob = bpy.data.objects.new(name, me)
    scene.collection.objects.link(ob)
    print(name, "tris:", len(faces) * 2)
    return ob


pieces = [build("TunnelStraight", False, 1), build("TunnelPassingBay", True, 2)]
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(HERE, "Tunnel.blend"))
for ob in pieces:
    for o in scene.objects:
        o.select_set(o is ob)
    bpy.context.view_layer.objects.active = ob
    bpy.ops.export_scene.fbx(filepath=os.path.join(OUT, ob.name + ".fbx"), use_selection=True,
                             object_types={'MESH'}, apply_scale_options='FBX_SCALE_UNITS',
                             axis_forward='-Z', axis_up='Y', bake_space_transform=True,
                             mesh_smooth_type='OFF', bake_anim=False)
print("exported to", OUT)

# ------------------------------------------------------------------ preview
if DO_RENDER:
    prev = os.path.join(HERE, "preview")
    os.makedirs(prev, exist_ok=True)
    order = [0, 1, 0, 0]
    for i, k in enumerate(order):
        if i == 0:
            continue
        d = bpy.data.objects.new("dup%d" % i, pieces[k].data)
        d.location = (0, -L * i, 0)
        scene.collection.objects.link(d)
    pieces[1].hide_render = True
    w = bpy.data.worlds.new("World")
    try:
        w.use_nodes = True
    except Exception:
        pass
    bg = next((nd for nd in w.node_tree.nodes if nd.type == 'BACKGROUND'), None)
    if bg:
        bg.inputs[0].default_value = (0, 0, 0, 1)
    scene.world = w
    for x in (-0.6, 0.6):                                   # stand-in headlights
        sp = bpy.data.lights.new("Head", 'SPOT')
        sp.energy = 9000
        sp.spot_size = math.radians(85)
        sp.spot_blend = 0.6
        sp.color = (1.0, 0.93, 0.8)
        so = bpy.data.objects.new("Head", sp)
        so.location = (x, -4.0, 0.7)
        so.rotation_euler = (math.radians(-87), 0, 0)
        scene.collection.objects.link(so)
    scene.render.engine = 'CYCLES'
    scene.cycles.samples = 96
    try:
        scene.cycles.use_denoising = True
    except Exception:
        pass
    scene.render.resolution_x, scene.render.resolution_y = 1100, 620
    scene.render.image_settings.file_format = 'JPEG'
    cam = bpy.data.cameras.new("Cam")
    cam.lens = 22
    co = bpy.data.objects.new("Cam", cam)
    scene.collection.objects.link(co)
    scene.camera = co
    for nm, loc, tgt in (("drive", (0.35, -1.5, 1.25), (0.0, -30.0, 1.3)),
                         ("bay", (0.9, -19.0, 1.6), (-1.5, -40.0, 1.4))):
        co.location = loc
        co.rotation_euler = (Vector(tgt) - Vector(loc)).to_track_quat('-Z', 'Y').to_euler()
        if nm == "bay":
            for o in scene.objects:
                if o.name.startswith("Head"):
                    o.location.y = -21.0
        scene.render.filepath = os.path.join(prev, "tunnel_%s.jpg" % nm)
        bpy.ops.render.render(write_still=True)
    print("renders done")
