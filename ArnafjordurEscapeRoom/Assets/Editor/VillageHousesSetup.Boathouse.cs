using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arnafjordur.Characters;
using Arnafjordur.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Arnafjordur.Editor
{
    /// <summary>
    /// The black boathouse (naust) on the bank at the north end of the village road, OSM building 50. Its road-side half
    /// is dug into the hillside and its sea side stands on a tall concrete foundation, so it gets two rooms: a
    /// board-walled hay loft with sheep at door level, and below it, inside the foundation, a concrete boat room with a
    /// clinker-built Faroese boat on rollers and boat doors out to a slipway. Outside, a timber landing at the door and
    /// stairs up the bank meet the gravel BoathousePath from build_village_walkable.py.
    /// </summary>
    public static partial class VillageHousesSetup
    {
        private const int Boathouse = 50;
        private const string BoathouseRootName = "Boathouse_Interior";
        private const string BoathouseOutsideName = "Boathouse_Outside";
        private const string BoathouseReply = "Only sheep bleating inside.";

        /// <summary>Lock id of the boathouse door; Leivur's <see cref="HouseKey"/> carries the same id.</summary>
        public const string BoathouseLockId = "boathouse";

        // House frame: x along the ridge (+x is the door gable), +z towards the sea, y = 0 on the loft floor.
        private const float LoftBoard = 0.08f;            // loft floor boards; their underside is the boat room's ceiling
        private const float BoatFloorAboveSea = 0.75f;
        private const float StairX0 = -3.1f, StairX1 = 1.0f, StairZ1 = -1.95f;   // stairwell along the land wall, top at StairX1
        private const float PenX1 = 1.2f, PenZ0 = 0.45f;  // sheep pen: the sea side, from the far gable to PenX1
        private const float BoatHalfLength = 1.8f, BoatCentreZ = -0.05f;   // leaves ~1 m between the stern and the boat doors
        private const float LandingX1 = 5.75f, LandingZ0 = -1.85f, LandingZ1 = -0.2f;   // timber landing at the door

        /// <summary>Where the walk test after the build goes (set by <see cref="BuildBoathouse"/>).</summary>
        private sealed class BoathouseProbe
        {
            public HouseInterior Interior;
            public HouseDoor FrontDoor;
            public Vector3[] OutsideRoute, InsideRoute, AroundBoatRoute;
        }

        private static BoathouseProbe _boathouseProbe;

        private static InteriorResult BuildBoathouse(HouseData h, Collider shell, WalkableArea area, float sea)
        {
            _boathouseProbe = null;
            Opening door = h.doors?.FirstOrDefault();
            if (door == null)
            {
                Debug.LogError("[VillageHousesSetup] The boathouse has no door in VillageHouses.json.");
                return null;
            }

            float floorWorld = h.y + h.plinthH + FloorLift;
            var pose = new Vector3(h.x, floorWorld, h.z);
            Quaternion rot = Quaternion.Euler(0f, h.yaw * Mathf.Rad2Deg, 0f);
            var root = new GameObject(BoathouseRootName);
            root.transform.SetPositionAndRotation(pose, rot);
            Transform rt = root.transform;
            var roomsGo = new GameObject("Rooms");
            roomsGo.transform.SetParent(rt, false);
            var kit = new Kit(roomsGo.transform);

            float X0 = -h.w * 0.5f + Inset, X1 = h.w * 0.5f - Inset, Z0 = -h.d * 0.5f + Inset, Z1 = h.d * 0.5f - Inset;
            float down = sea + BoatFloorAboveSea - floorWorld;
            float eave = Mathf.Min(2.4f, h.h - h.plinthH - FloorLift - 0.15f);
            float ridge = Mathf.Min(eave + Z1 * 0.79f, h.h + h.roofH - h.plinthH - FloorLift - 0.35f);
            Vector3 doorLocal = rt.InverseTransformPoint(new Vector3(door.x, door.y, door.z));
            float sill = doorLocal.y;
            if (doorLocal.x < X1 || doorLocal.z < StairZ1 + 0.4f || doorLocal.z > PenZ0 - 0.4f || down > -2.7f || X0 > StairX0 - 0.5f)
            {
                Debug.LogError($"[VillageHousesSetup] The boathouse isn't shaped the way its rooms expect (door at {doorLocal}, boat floor {down:F2}).");
                UnityEngine.Object.DestroyImmediate(root);
                return null;
            }

            var mats = new BoathouseMats(kit);
            var result = new InteriorResult { Root = rt, Reply = BoathouseReply, LockId = BoathouseLockId };

            BuildLoft(kit, mats, X0, X1, Z0, Z1, eave, ridge, down);
            BuildInsideStairs(kit, mats, Z0, down);
            BuildBoatRoom(kit, mats, X0, X1, Z0, Z1, down);
            BuildSheepPen(kit, mats, X0, Z1);
            BuildHayStore(kit, mats, X0, X1, Z1);
            BuildLoftTools(kit, mats, X1, Z0);
            BuildBoat(kit, mats, new Vector3(0f, down + 0.19f, BoatCentreZ));
            BuildBoatRoomProps(kit, mats, X0, X1, Z0, Z1, down);

            BuildWindows(kit, rt, h,
                p => p.y > -0.2f ? 0f : down,
                p => p.y > -0.2f ? ridge - Mathf.Abs(p.z) * (ridge - eave) / Z1 : -LoftBoard);
            HouseDoor inside = BuildInsideDoor(kit, rt, door, p => 0f, out Vector3 insideStand);
            result.InsideDoors[h.index] = inside;
            result.InsideStand[h.index] = insideStand;

            kit.CeilingLamp("Loft_West", new Vector3(-1.7f, ridge - 0.05f, 0f), ridge, 7f);
            kit.CeilingLamp("Loft_East", new Vector3(2.0f, ridge - 0.05f, 0f), ridge, 7f);
            kit.CeilingLamp("BoatRoom_Land", new Vector3(-2.0f, -LoftBoard - 0.2f, -0.6f), -LoftBoard - 0.2f - down, 6.5f);
            kit.CeilingLamp("BoatRoom_Sea", new Vector3(1.9f, -LoftBoard - 0.2f, 1.2f), -LoftBoard - 0.2f - down, 6.5f);

            Vector3[] Rect(float x0, float x1, float z0, float z1, float y) => new[]
            {
                rt.TransformPoint(new Vector3(x0, y, z0)), rt.TransformPoint(new Vector3(x1, y, z0)),
                rt.TransformPoint(new Vector3(x1, y, z1)), rt.TransformPoint(new Vector3(x0, y, z1)),
            };
            result.Zones.Add(new WalkableArea.Zone { name = "Boathouse_Inside", outline = Rect(X0, X1, Z0, Z1, 0f) });

            var interior = root.AddComponent<HouseInterior>();
            interior.Configure("boathouse", roomsGo, shell != null ? new[] { shell } : new Collider[0]);
            EditorUtility.SetDirty(interior);
            result.Interior = interior;

            // Outside: landing, stairs and the sea front, always visible.
            var outside = new GameObject(BoathouseOutsideName) { isStatic = true };
            outside.transform.SetPositionAndRotation(pose, rot);
            if (!BuildWalkway(outside.transform, mats, h, sill, area, out Vector3 stairTop, out Vector3 stairFootWorld))
            {
                UnityEngine.Object.DestroyImmediate(outside);
            }
            else
            {
                BuildSeaFront(outside.transform, mats, h, down);
                Vector3 outsideStand = rt.TransformPoint(new Vector3(h.w * 0.5f + 0.75f, sill, doorLocal.z));
                _boathouseProbe = new BoathouseProbe
                {
                    Interior = interior,
                    OutsideRoute = new[] { stairTop, stairFootWorld, outsideStand },
                    InsideRoute = new[]
                    {
                        insideStand,
                        rt.TransformPoint(new Vector3(StairX1 + 0.7f, 0f, (Z0 + StairZ1) * 0.5f)),
                        rt.TransformPoint(new Vector3(StairX0 - 0.35f, down, (Z0 + StairZ1) * 0.5f)),
                        rt.TransformPoint(new Vector3(-2.3f, down, 0.6f)),
                    },
                    // From the foot of the stairs, behind the stern along the boat doors, to the workbench.
                    AroundBoatRoute = new[]
                    {
                        rt.TransformPoint(new Vector3(StairX0 - 0.35f, down, (Z0 + StairZ1) * 0.5f)),
                        rt.TransformPoint(new Vector3(-1.6f, down, 2.25f)),
                        rt.TransformPoint(new Vector3(1.6f, down, 2.25f)),
                        rt.TransformPoint(new Vector3(2.75f, down, -0.4f)),
                    },
                };

                Vector3 W(Vector3 world) => rt.InverseTransformPoint(world);
                result.Shots.Add(("village_boathouse.png", W(new Vector3(424.6f, -78.4f, 443.2f)), W(new Vector3(439.5f, -85.3f, 452.2f))));
                result.Shots.Add(("village_boathouse_stairs.png", W(new Vector3(441.6f, -82.2f, 446.0f)), W(new Vector3(437.7f, -84.4f, 451.3f))));
                result.Shots.Add(("village_boathouse_sea.png", W(new Vector3(452.5f, -87.4f, 447.6f)), W(new Vector3(444.6f, -87.6f, 455.2f))));
            }

            CutTerrainHole(rt, h.w * 0.5f - 0.03f, h.d * 0.5f - 0.03f, "the boathouse");

            const float Eye = 1.6f;
            result.Shots.Add(("interior_boathouse_loft.png", new Vector3(3.2f, Eye, -1.5f), new Vector3(-1.6f, 0.5f, 1.5f)));
            result.Shots.Add(("interior_boathouse_wallkey.png", new Vector3(2.25f, 1.42f, -0.65f), new Vector3(X1 - Skin, 1.15f, -0.15f)));
            result.Shots.Add(("interior_boathouse_stairs.png", new Vector3(3.0f, Eye + 0.1f, 0.1f), new Vector3(-1.6f, -1.2f, -2.4f)));
            result.Shots.Add(("interior_boathouse_boatroom.png", new Vector3(-3.3f, down + Eye, -1.3f), new Vector3(0.6f, down + 0.5f, 1.4f)));
            result.Shots.Add(("interior_boathouse_gun.png", new Vector3(2.35f, down + 1.45f, -0.25f), new Vector3(3.5f, down + 0.95f, -0.5f)));
            result.Shots.Add(("interior_boathouse_doors.png", new Vector3(3.1f, down + Eye + 0.1f, -2.2f), new Vector3(-0.6f, down + 0.8f, 2.6f)));

            Physics.SyncTransforms();
            return result;
        }

        // ------------------------------------------------------------------ materials

        /// <summary>The boathouse's materials: procedural board, concrete, hay and wool textures plus flat colours.</summary>
        private sealed class BoathouseMats
        {
            public readonly Material Planks, Timber, Weathered, Tarred, Concrete, ConcreteFloor, Hay, HayHeap, Twine;
            public readonly Material[] Wool;
            public readonly Material Face, Black, Steel, Linen, Rope, HullWhite, HullBlue, HullTar, BoatWood, Crate, Net, Buoy, BuoyRed, Water;

            public BoathouseMats(Kit k)
            {
                Texture2D planks = SaveTexture("Tex_Boathouse_Planks.png", 512, PaintPlanks(512));
                Texture2D concrete = SaveTexture("Tex_Boathouse_Concrete.png", 512, PaintConcrete(512));
                Texture2D hay = SaveTexture("Tex_Boathouse_Hay.png", 256, PaintHay(256));
                Texture2D wool = SaveTexture("Tex_Boathouse_Wool.png", 256, PaintWool(256));

                Planks = Tex(k.Mat("BarnPlanks", Color.white, 0.12f), planks, Vector2.one);
                Timber = Tex(k.Mat("BarnTimber", new Color(0.66f, 0.62f, 0.58f), 0.1f), planks, new Vector2(0.5f, 0.5f));
                Weathered = Tex(k.Mat("WeatheredPlanks", new Color(0.72f, 0.72f, 0.74f), 0.1f), planks, Vector2.one);
                Tarred = Tex(k.Mat("TarredPlanks", new Color(0.3f, 0.29f, 0.29f), 0.25f), planks, Vector2.one);
                Concrete = Tex(k.Mat("BoathouseConcrete", Color.white, 0.08f), concrete, Vector2.one);
                ConcreteFloor = Tex(k.Mat("BoathouseConcreteFloor", new Color(0.72f, 0.72f, 0.72f), 0.32f), concrete, Vector2.one);
                Hay = Tex(k.Mat("Hay", Color.white, 0.05f), hay, Vector2.one);
                HayHeap = Tex(k.Mat("HayHeap", Color.white, 0.05f), hay, new Vector2(5f, 3f));
                Twine = k.Mat("Twine", Palette.BoathouseRust * 0.8f, 0.1f);
                Wool = new[]
                {
                    Tex(k.Mat("WoolCream", Palette.SheepWoolCream, 0.04f), wool, new Vector2(5f, 3f)),
                    Tex(k.Mat("WoolBrown", Palette.SheepWoolBrown, 0.04f), wool, new Vector2(5f, 3f)),
                    Tex(k.Mat("WoolDark", Palette.SheepWoolDark, 0.04f), wool, new Vector2(5f, 3f)),
                    Tex(k.Mat("WoolGrey", Palette.SheepWoolGrey, 0.04f), wool, new Vector2(5f, 3f)),
                };
                Face = k.Mat("SheepFace", Palette.SheepFace, 0.2f);
                Black = k.Mat("Black", Palette.DeepFjordSlate * 0.6f, 0.5f);
                Steel = k.Mat("Steel", Palette.InteriorSteel, 0.7f, null, 0.8f);
                Linen = k.Mat("Linen", Palette.InteriorLinen, 0.1f);
                Rope = k.Mat("Rope", Palette.DriftwoodTimber, 0.1f);
                HullWhite = k.Mat("BoatHullWhite", Palette.BoatHullWhite, 0.45f);
                HullBlue = k.Mat("BoatStrakeBlue", Palette.BoatStrakeBlue, 0.45f);
                HullTar = k.Mat("BoatTar", Palette.BoatTar, 0.35f);
                BoatWood = k.Mat("BoatWood", Palette.BoatInteriorWood, 0.2f);
                Crate = k.Mat("FishCrate", Palette.FishCrateBlue, 0.35f);
                Net = k.Mat("FishingNet", Palette.NetGreen, 0.1f);
                Buoy = k.Mat("BuoyOrange", Palette.LanternAmber, 0.5f);
                BuoyRed = k.Mat("BuoyRed", Palette.BoathouseRust, 0.5f);
                Water = k.Mat("Water", Palette.SkyZenithMist, 0.95f);
                LongGrainMats.Clear();
                LongGrainMats.Add(Timber);
                LongGrainMats.Add(Tarred);
            }

            private static Material Tex(Material m, Texture2D tex, Vector2 scale)
            {
                m.SetTexture("_BaseMap", tex);
                m.SetTextureScale("_BaseMap", scale);
                EditorUtility.SetDirty(m);
                return m;
            }
        }

        // ------------------------------------------------------------------ the loft: board walls, open roof, sheep and hay

        private static void BuildLoft(Kit kit, BoathouseMats m, float X0, float X1, float Z0, float Z1, float eave, float ridge, float down)
        {
            var shell = new Batch();
            var timber = new Batch();
            const float Tile = 0.6f;

            // Floor boards with the stairwell left open.
            void Slab(float x0, float x1, float z0, float z1) => shell.Box(m.Planks, new Vector3(x0, -LoftBoard, z0), new Vector3(x1, 0f, z1), Tile);
            Slab(X0, X1, StairZ1, Z1);
            Slab(X0, StairX0, Z0, StairZ1);
            Slab(StairX1, X1, Z0, StairZ1);

            // Board walls up to the eaves, gables up to the ridge, and the underside of the roof.
            shell.Box(m.Planks, new Vector3(X0, -0.02f, Z0), new Vector3(X1, eave, Z0 + Skin), Tile);
            shell.Box(m.Planks, new Vector3(X0, -0.02f, Z1 - Skin), new Vector3(X1, eave, Z1), Tile);
            foreach ((float x, float nx) in new[] { (X0 + Skin, 1f), (X1 - Skin, -1f) })
            {
                shell.Poly(m.Planks, new Vector3(nx, 0f, 0f), Vector3.forward, Vector3.up, Tile,
                    new Vector3(x, -0.02f, Z0), new Vector3(x, -0.02f, Z1), new Vector3(x, eave, Z1), new Vector3(x, ridge, 0f), new Vector3(x, eave, Z0));
            }

            foreach (float side in new[] { -1f, 1f })
            {
                float zEave = side * Z1;
                Vector3 slope = new Vector3(0f, ridge - eave, -zEave).normalized;     // eave -> ridge
                Vector3 inward = Vector3.Cross(Vector3.right, slope);
                if (inward.y > 0f) inward = -inward;
                shell.Poly(m.Planks, inward, Vector3.right, slope, Tile,
                    new Vector3(X0, eave, zEave), new Vector3(X1, eave, zEave), new Vector3(X1, ridge, 0f), new Vector3(X0, ridge, 0f));

                // Rafters under the boards, the wall plate along the eave, and studs in the long wall.
                float len = new Vector2(ridge - eave, Z1).magnitude;
                Vector3 mid = new Vector3(0f, (eave + ridge) * 0.5f, zEave * 0.5f) + inward * 0.075f;
                for (float x = X0 + 0.25f; x < X1 - 0.1f; x += 0.95f)
                {
                    Matrix4x4 xf = Matrix4x4.TRS(new Vector3(x, mid.y, mid.z), Quaternion.LookRotation(slope, -inward), Vector3.one);
                    timber.Box(m.Timber, new Vector3(-0.045f, -0.075f, -len * 0.5f), new Vector3(0.045f, 0.075f, len * 0.5f), Tile, xf);
                }

                float zw = side < 0f ? Z0 + Skin : Z1 - Skin - 0.14f;
                timber.Box(m.Timber, new Vector3(X0, eave - 0.16f, zw), new Vector3(X1, eave, zw + 0.14f), Tile);
                float zs = side < 0f ? Z0 + Skin : Z1 - Skin - 0.1f;
                for (float x = X0 + 0.9f; x < X1 - 0.5f; x += 1.9f)
                {
                    timber.Box(m.Timber, new Vector3(x - 0.06f, 0f, zs), new Vector3(x + 0.06f, eave - 0.16f, zs + 0.1f), Tile);
                }
            }

            timber.Box(m.Timber, new Vector3(X0, ridge - 0.22f, -0.07f), new Vector3(X1, ridge - 0.02f, 0.07f), Tile);
            foreach (float x in new[] { -2.35f, 0.15f, 2.6f })
            {
                timber.Box(m.Timber, new Vector3(x - 0.07f, eave - 0.2f, Z0 + Skin), new Vector3(x + 0.07f, eave - 0.02f, Z1 - Skin), Tile);
            }

            // Joists under the loft floor, seen from the boat room, with a trimmer along the stairwell.
            for (float x = X0 + 0.35f; x < X1; x += 0.8f)
            {
                bool overWell = x > StairX0 - 0.06f && x < StairX1 + 0.06f;
                timber.Box(m.Timber, new Vector3(x - 0.06f, -LoftBoard - 0.2f, overWell ? StairZ1 : Z0 + Skin),
                    new Vector3(x + 0.06f, -LoftBoard, Z1 - Skin), Tile, null, true);
            }

            timber.Box(m.Timber, new Vector3(StairX0, -LoftBoard - 0.2f, StairZ1), new Vector3(StairX1, -LoftBoard, StairZ1 + 0.12f), Tile);

            // Railing round the stairwell (solid to walk into, so nobody steps off the edge).
            Transform rails = kit.Group("StairwellRailing");
            foreach (float x in new[] { StairX0, (StairX0 + StairX1) * 0.5f, StairX1 })
            {
                timber.Box(m.Timber, new Vector3(x - 0.05f, 0f, StairZ1 - 0.05f), new Vector3(x + 0.05f, 1.02f, StairZ1 + 0.05f), Tile);
            }

            timber.Box(m.Timber, new Vector3(StairX0 - 0.05f, 0f, Z0 + Skin), new Vector3(StairX0 + 0.05f, 1.02f, Z0 + Skin + 0.1f), Tile);
            foreach (float y in new[] { 0.5f, 0.95f })
            {
                timber.Box(m.Timber, new Vector3(StairX0, y, StairZ1 - 0.03f), new Vector3(StairX1, y + 0.07f, StairZ1 + 0.03f), Tile);
                timber.Box(m.Timber, new Vector3(StairX0 - 0.03f, y, Z0 + Skin), new Vector3(StairX0 + 0.03f, y + 0.07f, StairZ1), Tile, null, true);
            }

            kit.Blocker(new Vector3(StairX0, 0f, StairZ1 - 0.04f), new Vector3(StairX1, 1.05f, StairZ1 + 0.04f), rails);
            kit.Blocker(new Vector3(StairX0 - 0.04f, 0f, Z0), new Vector3(StairX0 + 0.04f, 1.05f, StairZ1), rails);

            shell.Build("Boathouse_LoftShell", kit.Root, true, true);
            timber.Build("Boathouse_LoftTimber", kit.Root, false, true);
        }

        /// <summary>A steep barn stair down the stairwell: open treads on two stringers, a ramp collider to walk on.</summary>
        private static void BuildInsideStairs(Kit kit, BoathouseMats m, float Z0, float down)
        {
            var b = new Batch();
            float rise = -down, run = StairX1 - StairX0;
            int n = Mathf.Max(2, Mathf.RoundToInt(rise / 0.19f));
            float rStep = rise / n, aStep = run / n;
            float zIn = Z0 + Skin, zOut = StairZ1;
            for (int k = 1; k < n; k++)
            {
                float cx = StairX0 + k * aStep, top = down + k * rStep;
                b.Box(m.Timber, new Vector3(cx - aStep * 0.5f - 0.03f, top - 0.045f, zIn + 0.05f), new Vector3(cx + aStep * 0.5f, top, zOut - 0.05f), 0.6f, null, true);
            }

            float angle = Mathf.Atan2(rise, run) * Mathf.Rad2Deg, length = new Vector2(run, rise).magnitude;
            var mid = new Vector3((StairX0 + StairX1) * 0.5f, down * 0.5f, 0f);
            Quaternion tilt = Quaternion.Euler(0f, 0f, angle);
            foreach (float z in new[] { zIn + 0.03f, zOut - 0.03f })
            {
                Matrix4x4 xf = Matrix4x4.TRS(new Vector3(mid.x, mid.y, z), tilt, Vector3.one);
                b.Box(m.Timber, new Vector3(-length * 0.5f, -0.22f, -0.025f), new Vector3(length * 0.5f, 0.0f, 0.025f), 0.6f, xf);
            }

            // Handrail on the open side.
            Matrix4x4 rail = Matrix4x4.TRS(new Vector3(mid.x, mid.y + 0.92f, zOut - 0.02f), tilt, Vector3.one);
            b.Box(m.Timber, new Vector3(-length * 0.5f + 0.3f, -0.035f, -0.03f), new Vector3(length * 0.5f, 0.035f, 0.03f), 0.6f, rail);
            b.Build("Boathouse_InsideStairs", kit.Root, false, true);

            var ramp = new GameObject("StairRamp") { isStatic = true };
            ramp.transform.SetParent(kit.Root, false);
            ramp.transform.localPosition = new Vector3(mid.x, mid.y, (zIn + zOut) * 0.5f);
            ramp.transform.localRotation = tilt;
            var box = ramp.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, -0.05f, 0f);
            box.size = new Vector3(length, 0.1f, zOut - zIn);

            // Under the low end of the stairs: solid, so nobody walks in under the treads.
            Transform under = kit.Group("UnderStairs");
            float tan = rise / run;
            for (float xa = StairX0 + 0.4f; xa < StairX0 + 2.0f / tan; xa += 0.4f)
            {
                float top = down + (xa - StairX0) * tan - 0.16f;
                if (top > down + 0.05f) kit.Blocker(new Vector3(xa, down, Z0), new Vector3(xa + 0.4f, top, StairZ1), under);
            }
        }

        private static void BuildBoatRoom(Kit kit, BoathouseMats m, float X0, float X1, float Z0, float Z1, float down)
        {
            var shell = new Batch();
            const float Tile = 1.6f;
            float top = -LoftBoard;
            shell.Box(m.ConcreteFloor, new Vector3(X0, down - 0.3f, Z0), new Vector3(X1, down, Z1), Tile);
            shell.Box(m.Concrete, new Vector3(X0, down - 0.02f, Z0), new Vector3(X1, top, Z0 + Skin), Tile);
            shell.Box(m.Concrete, new Vector3(X0, down - 0.02f, Z1 - Skin), new Vector3(X1, top, Z1), Tile);
            shell.Box(m.Concrete, new Vector3(X0, down - 0.02f, Z0 + Skin), new Vector3(X0 + Skin, top, Z1 - Skin), Tile);
            shell.Box(m.Concrete, new Vector3(X1 - Skin, down - 0.02f, Z0 + Skin), new Vector3(X1, top, Z1 - Skin), Tile);
            shell.Build("Boathouse_BoatRoomShell", kit.Root, true, true);

            // The boat doors in the sea wall, seen from inside: board leaves with ledges and braces, barred shut.
            var doors = new Batch();
            float zw = Z1 - Skin, h = 2.15f;
            foreach (float s in new[] { -1f, 1f })
            {
                float a = s * 0.015f, b = s * 1.5f;
                float x0 = Mathf.Min(a, b), x1 = Mathf.Max(a, b);
                doors.Box(m.Planks, new Vector3(x0, down, zw - 0.05f), new Vector3(x1, down + h, zw), 0.6f);
                foreach (float y in new[] { 0.3f, 1.75f })
                {
                    doors.Box(m.Timber, new Vector3(x0 + 0.06f, down + y, zw - 0.09f), new Vector3(x1 - 0.06f, down + y + 0.14f, zw - 0.05f), 0.6f);
                }

                Vector3 p0 = new Vector3(x0 + 0.12f, down + 0.44f, zw - 0.07f), p1 = new Vector3(x1 - 0.12f, down + 1.75f, zw - 0.07f);
                if (s < 0f) (p0.y, p1.y) = (p1.y, p0.y);
                Vector3 d = p1 - p0;
                Matrix4x4 brace = Matrix4x4.TRS((p0 + p1) * 0.5f, Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg), Vector3.one);
                doors.Box(m.Timber, new Vector3(-d.magnitude * 0.5f, -0.06f, -0.02f), new Vector3(d.magnitude * 0.5f, 0.06f, 0.02f), 0.6f, brace);
                doors.Box(m.Timber, new Vector3(s * 1.5f - (s > 0 ? 0f : 0.1f), down, zw - 0.1f), new Vector3(s * 1.5f + (s > 0 ? 0.1f : 0f), down + h + 0.1f, zw), 0.6f);
                doors.Box(m.Black, new Vector3(s * 1.62f - 0.06f, down + 1.0f, zw - 0.2f), new Vector3(s * 1.62f + 0.06f, down + 1.22f, zw - 0.09f), 0.6f);
            }

            doors.Box(m.Timber, new Vector3(-1.6f, down + h, zw - 0.1f), new Vector3(1.6f, down + h + 0.12f, zw), 0.6f);
            doors.Box(m.Timber, new Vector3(-1.78f, down + 1.04f, zw - 0.2f), new Vector3(1.78f, down + 1.18f, zw - 0.1f), 0.6f);
            doors.Build("Boathouse_BoatDoors", kit.Root, false, true);
        }

        // ------------------------------------------------------------------ sheep and hay

        private static void BuildSheepPen(Kit kit, BoathouseMats m, float X0, float Z1)
        {
            Transform pen = kit.Group("SheepPen");
            var b = new Batch();
            float xw = X0 + Skin, zw = Z1 - Skin;

            // Hurdle fence along the aisle and across to the sea wall.
            void Fence(Vector3 from, Vector3 to)
            {
                Vector3 d = to - from;
                int posts = Mathf.Max(2, Mathf.CeilToInt(d.magnitude / 1.25f) + 1);
                for (int i = 0; i < posts; i++)
                {
                    Vector3 p = Vector3.Lerp(from, to, i / (posts - 1f));
                    b.Box(m.Timber, new Vector3(p.x - 0.045f, 0f, p.z - 0.045f), new Vector3(p.x + 0.045f, 1.08f, p.z + 0.045f), 0.6f);
                }

                bool alongX = Mathf.Abs(d.x) > Mathf.Abs(d.z);
                foreach (float y in new[] { 0.32f, 0.66f, 0.98f })
                {
                    Vector3 lo = Vector3.Min(from, to), hi = Vector3.Max(from, to);
                    b.Box(m.Timber, new Vector3(lo.x - (alongX ? 0f : 0.02f), y, lo.z - (alongX ? 0.02f : 0f)),
                        new Vector3(hi.x + (alongX ? 0f : 0.02f), y + 0.08f, hi.z + (alongX ? 0.02f : 0f)), 0.6f, null, !alongX);
                }

                kit.Blocker(Vector3.Min(from, to) - new Vector3(0.05f, 0f, 0.05f), Vector3.Max(from, to) + new Vector3(0.05f, 1.1f, 0.05f), pen);
            }

            Fence(new Vector3(xw, 0f, PenZ0), new Vector3(PenX1, 0f, PenZ0));
            Fence(new Vector3(PenX1, 0f, PenZ0), new Vector3(PenX1, 0f, zw));

            // Straw bedding, and a slatted hay rack over a trough along the sea wall.
            b.Box(m.Hay, new Vector3(xw, 0f, PenZ0 + 0.05f), new Vector3(PenX1 - 0.05f, 0.025f, zw), 0.5f);
            const float RackX0 = -3.3f, RackX1 = 0.55f;
            float tz = zw - 0.42f;
            b.Box(m.Timber, new Vector3(RackX0, 0.28f, tz), new Vector3(RackX1, 0.32f, zw), 0.6f);
            b.Box(m.Timber, new Vector3(RackX0, 0.28f, tz - 0.03f), new Vector3(RackX1, 0.6f, tz), 0.6f);
            foreach (float x in new[] { RackX0, RackX1 - 0.03f })
            {
                b.Box(m.Timber, new Vector3(x, 0f, tz - 0.03f), new Vector3(x + 0.03f, 1.3f, zw), 0.6f);
            }

            b.Box(m.Timber, new Vector3(RackX0, 1.24f, tz + 0.05f), new Vector3(RackX1, 1.3f, tz + 0.11f), 0.6f);
            for (float x = RackX0 + 0.12f; x < RackX1 - 0.05f; x += 0.13f)
            {
                b.Box(m.Timber, new Vector3(x - 0.015f, 0.6f, tz + 0.065f), new Vector3(x + 0.015f, 1.24f, tz + 0.095f), 0.6f);
            }

            kit.Blocker(new Vector3(RackX0, 0f, tz - 0.05f), new Vector3(RackX1, 1.3f, zw), pen);
            for (float x = RackX0 + 0.35f; x < RackX1; x += 0.7f)
            {
                Ball("TroughHay", new Vector3(x, 0.42f, tz + 0.2f), new Vector3(0.75f, 0.22f, 0.36f), m.HayHeap, pen);
                Ball("RackHay", new Vector3(x + 0.1f, 0.95f, zw - 0.18f), new Vector3(0.7f, 0.55f, 0.3f), m.HayHeap, pen);
            }

            // Water tub.
            kit.Cyl("WaterTub", new Vector3(0.75f, 0.2f, 1.05f), 0.26f, 0.4f, m.Timber, Col.Box, pen);
            kit.Cyl("TubWater", new Vector3(0.75f, 0.36f, 1.05f), 0.23f, 0.02f, m.Water, Col.None, pen);
            b.Build("Boathouse_SheepPen", kit.Root, false, true);

            // Five Faroese sheep in their mixed colours: two at the rack, one lying with her lamb, one at the fence.
            Sheep(kit, m, pen, "Sheep_Cream", new Vector3(-2.6f, 0f, 1.75f), 0f, m.Wool[0], 1f, false, 40f, 11);
            Sheep(kit, m, pen, "Sheep_Brown", new Vector3(-1.2f, 0f, 1.8f), 12f, m.Wool[1], 1f, false, 42f, 23);
            Sheep(kit, m, pen, "Sheep_Black", new Vector3(-0.15f, 0f, 1.05f), 115f, m.Wool[2], 1f, true, 8f, 37);
            Sheep(kit, m, pen, "Lamb", new Vector3(-0.95f, 0f, 0.95f), 70f, m.Wool[0], 0.62f, false, 30f, 41);
            Sheep(kit, m, pen, "Sheep_Grey", new Vector3(0.35f, 0f, 1.25f), 196f, m.Wool[3], 1f, false, -4f, 53);
        }

        /// <summary>A sheep from wool lumps and a dark face; the head turns on its own (<see cref="SheepIdle"/>).</summary>
        private static void Sheep(Kit kit, BoathouseMats m, Transform parent, string name, Vector3 at, float yaw, Material wool,
            float scale, bool lying, float eatPitch, int seed)
        {
            var s = new GameObject(name).transform;
            s.SetParent(parent, false);
            s.localPosition = at;
            s.localRotation = Quaternion.Euler(0f, yaw, 0f);
            s.localScale = Vector3.one * scale;

            if (!lying)
            {
                foreach (float lx in new[] { -0.13f, 0.13f })
                foreach (float lz in new[] { -0.27f, 0.27f })
                {
                    kit.Cyl("Leg", new Vector3(lx, 0.21f, lz), 0.035f, 0.42f, m.Face, Col.None, s);
                }
            }

            float by = lying ? 0.3f : 0.62f;
            Ball("Body", new Vector3(0f, by, 0f), new Vector3(0.6f, lying ? 0.42f : 0.5f, 0.92f), wool, s, true);
            foreach (float lx in new[] { -0.14f, 0.14f })
            {
                Ball("Wool", new Vector3(lx, by + 0.11f, 0.18f), new Vector3(0.36f, 0.34f, 0.42f), wool, s);
                Ball("Wool", new Vector3(lx, by + 0.09f, -0.2f), new Vector3(0.38f, 0.36f, 0.44f), wool, s);
            }

            Ball("Wool", new Vector3(0f, by + 0.17f, 0f), new Vector3(0.42f, 0.3f, 0.52f), wool, s);
            Ball("Rump", new Vector3(0f, by + 0.02f, -0.4f), new Vector3(0.44f, 0.42f, 0.3f), wool, s);
            Ball("Tail", new Vector3(0f, by - 0.04f, -0.52f), new Vector3(0.1f, 0.17f, 0.08f), wool, s);

            var head = new GameObject("Head").transform;
            head.SetParent(s, false);
            head.localPosition = new Vector3(0f, by + 0.14f, 0.42f);
            Ball("Neck", new Vector3(0f, 0f, 0.02f), new Vector3(0.3f, 0.32f, 0.3f), wool, head);
            GameObject face = Ball("Face", new Vector3(0f, -0.05f, 0.2f), new Vector3(0.17f, 0.19f, 0.33f), m.Face, head);
            face.transform.localRotation = Quaternion.Euler(18f, 0f, 0f);
            Ball("TopKnot", new Vector3(0f, 0.07f, 0.11f), new Vector3(0.19f, 0.12f, 0.19f), wool, head);
            foreach (float sx in new[] { -1f, 1f })
            {
                GameObject ear = Ball("Ear", new Vector3(sx * 0.12f, 0.03f, 0.14f), new Vector3(0.15f, 0.035f, 0.07f), m.Face, head);
                ear.transform.localRotation = Quaternion.Euler(0f, sx * 15f, sx * -22f);
                Ball("Eye", new Vector3(sx * 0.07f, -0.01f, 0.26f), Vector3.one * 0.032f, m.Black, head);
            }

            Ball("Nose", new Vector3(0f, -0.12f, 0.34f), new Vector3(0.07f, 0.05f, 0.04f), m.Black, head);

            foreach (Transform t in s.GetComponentsInChildren<Transform>(true)) t.gameObject.isStatic = false;   // the head moves
            var idle = s.gameObject.AddComponent<SheepIdle>();
            idle.Configure(head, eatPitch, seed);
            EditorUtility.SetDirty(idle);
        }

        private static void BuildHayStore(Kit kit, BoathouseMats m, float X0, float X1, float Z1)
        {
            Transform store = kit.Group("HayStore");
            var b = new Batch();
            float xw = X1 - Skin, zw = Z1 - Skin;
            const float BL = 0.9f, BW = 0.45f, BH = 0.36f;

            void Bale(float x0, float y0, float z0, bool alongX)
            {
                Vector3 size = alongX ? new Vector3(BL, BH, BW) : new Vector3(BW, BH, BL);
                Vector3 lo = new Vector3(x0, y0, z0), hi = lo + size;
                b.Box(m.Hay, lo, hi, 0.5f, null, !alongX);
                foreach (float f in new[] { 0.3f, 0.7f })
                {
                    if (alongX) b.Box(m.Twine, new Vector3(x0 + BL * f - 0.008f, y0 - 0.004f, z0 - 0.006f), new Vector3(x0 + BL * f + 0.008f, y0 + BH + 0.006f, z0 + BW + 0.006f), 1f);
                    else b.Box(m.Twine, new Vector3(x0 - 0.006f, y0 - 0.004f, z0 + BL * f - 0.008f), new Vector3(x0 + BW + 0.006f, y0 + BH + 0.006f, z0 + BL * f + 0.008f), 1f);
                }
            }

            // Bales stacked against the sea wall by the door gable: three high at the back, stepping down to the aisle.
            float sx0 = PenX1 + 0.18f;
            for (int row = 0; row < 4; row++)
            {
                float z0 = zw - BW * (row + 1);
                int layers = 3 - Mathf.Min(row, 2);
                for (int layer = 0; layer < layers; layer++)
                for (int col = 0; col < 2; col++)
                {
                    float x0 = sx0 + col * (BL + 0.02f) + ((layer + row) % 2) * 0.12f;
                    if (x0 + BL > xw) x0 = xw - BL;
                    Bale(x0, layer * BH, z0, true);
                }
            }

            kit.Blocker(new Vector3(sx0, 0f, zw - BW * 4f), new Vector3(xw, BH * 3f, zw), store);
            Ball("LooseHay", new Vector3(sx0 + 1.1f, BH * 3f, zw - 0.35f), new Vector3(1.4f, 0.45f, 0.7f), m.HayHeap, store);
            Ball("LooseHay", new Vector3(sx0 + 0.9f, 0.05f, zw - BW * 4f - 0.2f), new Vector3(1.5f, 0.35f, 0.5f), m.HayHeap, store);

            // Two bales along the far gable, and a heap with the pitchfork in it.
            float gx = X0 + Skin;
            Bale(gx, 0f, StairZ1 + 0.05f, false);
            Bale(gx, 0f, StairZ1 + 0.05f + BL + 0.02f, false);
            Bale(gx, BH, StairZ1 + 0.3f, false);
            kit.Blocker(new Vector3(gx, 0f, StairZ1 + 0.05f), new Vector3(gx + BW, BH * 2f, StairZ1 + 0.07f + BL * 2f), store);
            Ball("LooseHay", new Vector3(gx + 0.55f, 0.05f, PenZ0 - 0.45f), new Vector3(0.9f, 0.5f, 0.75f), m.HayHeap, store);
            Ball("LooseHay", new Vector3(gx + 0.3f, BH * 2f, StairZ1 + 1.1f), new Vector3(0.6f, 0.25f, 1.2f), m.HayHeap, store);

            b.Build("Boathouse_Hay", kit.Root, false, true);
        }

        private static void BuildLoftTools(Kit kit, BoathouseMats m, float X1, float Z0)
        {
            Transform tools = kit.Group("LoftTools");
            float zw = Z0 + Skin;

            // A hay fork stuck in the heap by the far gable, another leaning in the corner by the door
            // (the floor between the door and the top of the stairs stays clear).
            Fork(kit, m, tools, new Vector3(-3.15f, 0.25f, PenZ0 - 0.45f), Quaternion.Euler(-14f, 0f, 10f));
            Fork(kit, m, tools, new Vector3(X1 - 0.25f, 0f, zw + 0.12f), Quaternion.Euler(-12f, 0f, 0f));

            kit.Cyl("Bucket", new Vector3(X1 - 0.7f, 0.15f, zw + 0.3f), 0.15f, 0.3f, m.Steel, Col.Box, tools);
            kit.Cyl("BucketFeed", new Vector3(X1 - 0.7f, 0.27f, zw + 0.3f), 0.13f, 0.02f, m.HayHeap, Col.None, tools);
            for (int i = 0; i < 2; i++)
            {
                GameObject sack = Ball("FeedSack", new Vector3(X1 - 0.3f, 0.27f, -0.05f + i * 0.47f), new Vector3(0.36f, 0.58f, 0.45f), m.Linen, tools, true);
                sack.transform.localRotation = Quaternion.Euler(0f, i * 25f - 10f, i * 6f);
            }

            kit.Blocker(new Vector3(X1 - 0.55f, 0f, -0.3f), new Vector3(X1, 0.6f, 0.68f), tools);

            // A brass sea-mine key hanging on an iron hook between the gable window and the loft door.
            float xWall = X1 - Skin;
            const float hookY = 1.38f, hookZ = -0.22f;
            kit.Box("KeyHookPlate", new Vector3(xWall - 0.006f, hookY - 0.025f, hookZ - 0.020f),
                new Vector3(xWall, hookY + 0.025f, hookZ + 0.020f), m.Black, Col.None, tools);
            GameObject peg = kit.Cyl("KeyHookPeg", new Vector3(xWall - 0.020f, hookY, hookZ), 0.0042f, 0.036f, m.Black, Col.None, tools);
            peg.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            kit.Box("KeyHookTip", new Vector3(xWall - 0.038f, hookY, hookZ - 0.004f),
                new Vector3(xWall - 0.032f, hookY + 0.014f, hookZ + 0.004f), m.Black, Col.None, tools);

            PickupItem wallKey = VillageSceneSetup.BuildBombKeyObject("BombKey_Boathouse", "Brass Key");
            wallKey.transform.SetParent(kit.Root, false);
            wallKey.transform.localPosition = new Vector3(xWall - 0.020f, hookY, hookZ);
            wallKey.transform.localRotation = Quaternion.LookRotation(Vector3.down, Vector3.left);
            if (wallKey.TryGetComponent(out BoxCollider keyBox))
            {
                keyBox.isTrigger = true;
                keyBox.size = new Vector3(0.08f, 0.06f, 0.14f);
            }
            foreach (Transform kt in wallKey.GetComponentsInChildren<Transform>(true)) kt.gameObject.isStatic = false;
        }

        private static void Fork(Kit kit, BoathouseMats m, Transform parent, Vector3 foot, Quaternion lean)
        {
            var f = new GameObject("HayFork").transform;
            f.SetParent(parent, false);
            f.localPosition = foot;
            f.localRotation = lean;
            kit.Cyl("Handle", new Vector3(0f, 0.95f, 0f), 0.017f, 1.3f, m.Timber, Col.None, f);
            kit.Box("Head", new Vector3(-0.12f, 0.24f, -0.015f), new Vector3(0.12f, 0.3f, 0.015f), m.Steel, Col.None, f);
            foreach (float x in new[] { -0.11f, 0f, 0.11f })
            {
                kit.Cyl("Tine", new Vector3(x, 0.12f, 0f), 0.007f, 0.26f, m.Steel, Col.None, f);
            }
        }

        // ------------------------------------------------------------------ the boat room

        /// <summary>
        /// A clinker-built, double-ended Faroese boat (bátur): six lapped strakes (tarred below, white, a blue sheer
        /// strake), high stems at both ends, three thwarts and a pair of oars, resting on log rollers.
        /// </summary>
        private static void BuildBoat(Kit kit, BoathouseMats m, Vector3 at)
        {
            const float A = BoatHalfLength, HB = 0.72f, D = 0.58f, S = 0.32f, Lap = 0.012f, Thick = 0.025f;
            const int Stations = 44, Strakes = 6, PerStrake = 3, InnerRows = 8;
            float Keel(float t) { float e = Mathf.Max(0f, Mathf.Abs(t) - 0.68f); return 0.06f * t * t + 2.6f * e * e; }
            float Sheer(float t) => D + S * Mathf.Pow(Mathf.Abs(t), 2.2f);
            float Half(float t) => HB * Mathf.Pow(Mathf.Max(0f, 1f - Mathf.Pow(Mathf.Abs(t), 2.3f)), 0.72f);
            float Shape(float v) => Mathf.Pow(Mathf.Sin(Mathf.Clamp01(v) * Mathf.PI * 0.5f), 0.55f);
            float T(int i) => -1f + 2f * i / Stations;

            var boat = new GameObject("Boat").transform;
            boat.SetParent(kit.Root, false);
            boat.localPosition = at;

            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new[] { new List<int>(), new List<int>(), new List<int>(), new List<int>() };   // tar, white, blue, wood

            // A grid of vertices (stations x rows) turned into triangles that face the way `hint` says.
            void Grid(int sub, int rows, Func<int, int, Vector3> at2, Vector3 hint)
            {
                int b0 = verts.Count;
                for (int i = 0; i <= Stations; i++)
                for (int j = 0; j < rows; j++)
                {
                    Vector3 p = at2(i, j);
                    verts.Add(p);
                    uvs.Add(new Vector2(p.y * 2.5f, p.z * 0.6f));
                }

                int G(int i, int j) => b0 + i * rows + j;
                Vector3 pa = verts[G(Stations / 2, 0)], pb = verts[G(Stations / 2 + 1, 0)], pc = verts[G(Stations / 2 + 1, 1)];
                bool flip = Vector3.Dot(Vector3.Cross(pb - pa, pc - pa), hint) < 0f;
                for (int i = 0; i < Stations; i++)
                for (int j = 0; j < rows - 1; j++)
                {
                    int a = G(i, j), b = G(i + 1, j), c = G(i + 1, j + 1), d = G(i, j + 1);
                    if (flip) tris[sub].AddRange(new[] { a, c, b, a, d, c });
                    else tris[sub].AddRange(new[] { a, b, c, a, c, d });
                }
            }

            foreach (float side in new[] { -1f, 1f })
            {
                for (int k = 0; k < Strakes; k++)
                {
                    int sub = k <= 1 ? 0 : k == Strakes - 1 ? 2 : 1;
                    int strake = k;
                    Grid(sub, PerStrake, (i, j) =>
                    {
                        float t = T(i), f = j / (PerStrake - 1f), v = (strake + f) / Strakes, hb = Half(t);
                        float x = hb * Shape(v) + Lap * f * Mathf.Min(1f, hb * 8f);
                        return new Vector3(side * x, Keel(t) + (Sheer(t) - Keel(t)) * v, t * A);
                    }, new Vector3(side, -0.3f, 0f));
                }

                Grid(3, InnerRows, (i, j) =>
                {
                    float t = T(i), v = Mathf.Lerp(0.04f, 1f, j / (InnerRows - 1f));
                    float x = Mathf.Max(0f, Half(t) * Shape(v) - Thick);
                    return new Vector3(side * x, Keel(t) + 0.02f + (Sheer(t) - Keel(t) - 0.02f) * v, t * A * 0.995f);
                }, new Vector3(-side, 0.3f, 0f));

                // Gunwale capping, painted with the sheer strake.
                Grid(2, 2, (i, j) =>
                {
                    float t = T(i), hb = Half(t);
                    float x = j == 0 ? hb + Lap * Mathf.Min(1f, hb * 8f) : Mathf.Max(0f, hb - Thick);
                    return new Vector3(side * x, Sheer(t) + 0.004f, t * A);
                }, Vector3.up);
            }

            var mesh = new Mesh { name = "Boathouse_BoatHull" };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = tris.Length;
            for (int i = 0; i < tris.Length; i++) mesh.SetTriangles(tris[i], i);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            string path = $"{InteriorDir}/Boathouse_BoatHull.asset";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mesh, path);
            var hull = new GameObject("Hull") { isStatic = true };
            hull.transform.SetParent(boat, false);
            hull.AddComponent<MeshFilter>().sharedMesh = mesh;
            hull.AddComponent<MeshRenderer>().sharedMaterials = new[] { m.HullTar, m.HullWhite, m.HullBlue, m.BoatWood };

            // Keel, raked stems standing proud of the gunwale, thwarts, bottom boards, tholes and the oars.
            kit.Box("Keel", new Vector3(-0.035f, -0.05f, -A * 0.74f), new Vector3(0.035f, 0.02f, A * 0.74f), m.HullTar, Col.None, boat);
            foreach (float e in new[] { -1f, 1f })
            {
                float y0 = Keel(1f) - 0.05f, y1 = Sheer(1f) + 0.17f;
                GameObject stem = kit.Box("Stem", new Vector3(-0.03f, y0, e * A - 0.045f), new Vector3(0.03f, y1, e * A + 0.045f), m.HullBlue, Col.None, boat);
                stem.transform.localRotation = Quaternion.Euler(e * 13f, 0f, 0f);
                GameObject foot = kit.Box("Forefoot", new Vector3(-0.03f, -0.02f, e * A * 0.74f - 0.05f), new Vector3(0.03f, y0 + 0.1f, e * A * 0.74f + 0.05f), m.HullTar, Col.None, boat);
                foot.transform.localRotation = Quaternion.Euler(e * 35f, 0f, 0f);
                foot.transform.localPosition += new Vector3(0f, 0f, e * 0.12f);
            }

            float thwartTop = 0f;
            foreach (float t in new[] { -0.5f, 0.02f, 0.5f })
            {
                float y = Sheer(t) - 0.2f, v = (y - Keel(t)) / (Sheer(t) - Keel(t));
                float x = Half(t) * Shape(v) - Thick - 0.01f;
                kit.Box("Thwart", new Vector3(-x, y - 0.035f, t * A - 0.11f), new Vector3(x, y, t * A + 0.11f), m.BoatWood, Col.None, boat);
                if (Mathf.Abs(t) < 0.1f) thwartTop = y;
            }

            kit.Box("BottomBoards", new Vector3(-0.26f, 0.08f, -A * 0.55f), new Vector3(0.26f, 0.1f, A * 0.55f), m.BoatWood, Col.None, boat);
            foreach (float t in new[] { -0.12f, 0.36f })
            foreach (float sx in new[] { -1f, 1f })
            {
                kit.Box("Thole", new Vector3(sx * Half(t) - 0.02f, Sheer(t), t * A - 0.02f), new Vector3(sx * Half(t) + 0.02f, Sheer(t) + 0.13f, t * A + 0.02f), m.BoatWood, Col.None, boat);
            }

            foreach (float sx in new[] { -0.2f, 0.2f })
            {
                GameObject shaft = kit.Cyl("Oar", new Vector3(sx, thwartTop + 0.024f, 0.25f), 0.022f, 2.7f, m.BoatWood, Col.None, boat);
                shaft.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                kit.Box("OarBlade", new Vector3(sx - 0.065f, thwartTop + 0.012f, 1.05f), new Vector3(sx + 0.065f, thwartTop + 0.03f, 1.6f), m.BoatWood, Col.None, boat);
            }

            var col = boat.gameObject.AddComponent<BoxCollider>();
            col.center = new Vector3(0f, 0.45f, 0f);
            col.size = new Vector3(HB * 2f + 0.05f, 0.95f, A * 2f);

            // Log rollers (hlunnar) under the keel and on the way to the doors.
            foreach (float z in new[] { at.z - 1.1f, at.z, at.z + 1.1f })
            {
                GameObject log = kit.Cyl("Roller", new Vector3(0f, at.y - 0.12f, z), 0.07f, 1.5f, m.Timber, Col.None);
                log.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            }
        }

        private static void BuildBoatRoomProps(Kit kit, BoathouseMats m, float X0, float X1, float Z0, float Z1, float down)
        {
            Transform props = kit.Group("BoatRoomProps");
            var b = new Batch();
            float xw = X1 - Skin, xg = X0 + Skin, zl = Z0 + Skin, zs = Z1 - Skin;

            // Workbench on the door-end gable, with a vice and a few tools.
            float bx0 = xw - 0.62f;
            b.Box(m.Timber, new Vector3(bx0, down + 0.86f, -1.4f), new Vector3(xw, down + 0.92f, 0.6f), 0.6f, null, true);
            b.Box(m.Timber, new Vector3(bx0 + 0.05f, down + 0.2f, -1.35f), new Vector3(xw, down + 0.24f, 0.55f), 0.6f, null, true);
            foreach (float z in new[] { -1.35f, 0.5f })
            foreach (float x in new[] { bx0 + 0.04f, xw - 0.1f })
            {
                b.Box(m.Timber, new Vector3(x, down, z), new Vector3(x + 0.07f, down + 0.86f, z + 0.07f), 0.6f);
            }

            b.Box(m.Timber, new Vector3(xw - 0.03f, down + 1.2f, -1.4f), new Vector3(xw, down + 1.9f, 0.6f), 0.6f, null, true);
            kit.Box("Vice", new Vector3(bx0 - 0.02f, down + 0.82f, 0.25f), new Vector3(bx0 + 0.16f, down + 1.02f, 0.45f), m.Steel, Col.None, props);
            kit.Box("Saw", new Vector3(xw - 0.02f, down + 1.3f, -1.2f), new Vector3(xw - 0.005f, down + 1.45f, -0.6f), m.Steel, Col.None, props);
            kit.Box("Hammer", new Vector3(bx0 + 0.2f, down + 0.92f, 0.12f), new Vector3(bx0 + 0.24f, down + 0.95f, 0.42f), m.Timber, Col.None, props);
            kit.Box("HammerHead", new Vector3(bx0 + 0.16f, down + 0.92f, 0.42f), new Vector3(bx0 + 0.28f, down + 0.97f, 0.46f), m.Black, Col.None, props);
            kit.Cyl("PaintTin", new Vector3(bx0 + 0.47f, down + 1.0f, 0.42f), 0.09f, 0.16f, m.HullBlue, Col.None, props);
            // The old double-barrel, lying on its side along the bench, loaded with two shells.
            BuildShotgun(kit, new Vector3(bx0 + 0.29f, down + 0.92f + 0.0225f, -0.55f), 0f);
            kit.Blocker(new Vector3(bx0, down, -1.4f), new Vector3(xw, down + 0.95f, 0.6f), props);

            // Fish crates stacked in the corner by the boat doors.
            void Crate(float x0, float y0, float z0)
            {
                const float W = 0.42f, L = 0.62f, H = 0.28f, T = 0.02f;
                b.Box(m.Crate, new Vector3(x0, y0, z0), new Vector3(x0 + W, y0 + T, z0 + L), 1f);
                b.Box(m.Crate, new Vector3(x0, y0, z0), new Vector3(x0 + T, y0 + H, z0 + L), 1f);
                b.Box(m.Crate, new Vector3(x0 + W - T, y0, z0), new Vector3(x0 + W, y0 + H, z0 + L), 1f);
                b.Box(m.Crate, new Vector3(x0, y0, z0), new Vector3(x0 + W, y0 + H, z0 + T), 1f);
                b.Box(m.Crate, new Vector3(x0, y0, z0 + L - T), new Vector3(x0 + W, y0 + H, z0 + L), 1f);
            }

            for (int i = 0; i < 3; i++) Crate(xw - 0.92f, down + i * 0.285f, zs - 0.66f);
            for (int i = 0; i < 2; i++) Crate(xw - 0.46f, down + i * 0.285f, zs - 0.66f);
            kit.Blocker(new Vector3(xw - 0.94f, down, zs - 0.68f), new Vector3(xw, down + 0.86f, zs), props);

            // Crates and a coil of rope tucked under the stairs.
            for (int i = 0; i < 2; i++) Crate(-2.05f, down + i * 0.285f, zl + 0.08f);
            for (int i = 0; i < 3; i++) Crate(-1.5f, down + i * 0.285f, zl + 0.08f);

            // A net hung on the far gable with its cork floats, buoys below, oars leaning beside it.
            float nz0 = 0.15f, nz1 = 2.35f;
            b.Poly(m.Net, Vector3.right, Vector3.forward, Vector3.up, 0.5f,
                new Vector3(xg + 0.03f, down + 0.85f, nz0 + 0.2f), new Vector3(xg + 0.03f, down + 0.95f, nz1 - 0.15f),
                new Vector3(xg + 0.03f, down + 2.25f, nz1), new Vector3(xg + 0.03f, down + 2.25f, nz0));
            b.Box(m.Timber, new Vector3(xg, down + 2.25f, nz0 - 0.1f), new Vector3(xg + 0.06f, down + 2.31f, nz1 + 0.1f), 0.6f, null, true);
            for (float z = nz0 + 0.12f; z < nz1; z += 0.3f)
            {
                GameObject cork = kit.Cyl("Float", new Vector3(xg + 0.06f, down + 2.2f, z), 0.04f, 0.08f, m.Buoy, Col.None, props);
                cork.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            }

            Ball("Buoy", new Vector3(xg + 0.3f, down + 0.2f, 1.55f), Vector3.one * 0.4f, m.Buoy, props, true);
            Ball("Buoy", new Vector3(xg + 0.25f, down + 0.18f, 2.05f), Vector3.one * 0.36f, m.BuoyRed, props, true);
            Ball("Buoy", new Vector3(xg + 0.62f, down + 0.17f, 2.4f), Vector3.one * 0.34f, m.Buoy, props, true);
            kit.Blocker(new Vector3(xg, down, 1.3f), new Vector3(xg + 0.85f, down + 0.4f, 2.6f), props);
            foreach (float z in new[] { -1.15f, -0.95f })
            {
                GameObject oar = kit.Cyl("Oar", new Vector3(xg + 0.25f, down + 1.3f, z), 0.022f, 2.6f, m.BoatWood, Col.None, props);
                oar.transform.localRotation = Quaternion.Euler(0f, 0f, 10f);   // top against the wall
            }

            for (int i = 0; i < 3; i++)
            {
                kit.Cyl("Rope", new Vector3(-2.4f, down + 0.025f + i * 0.045f, 2.1f), 0.28f - i * 0.02f, 0.045f, m.Rope, Col.None, props);
            }

            kit.Box("FuelCan", new Vector3(xw - 0.5f, down, zl + 0.1f), new Vector3(xw - 0.15f, down + 0.42f, zl + 0.3f), m.BuoyRed, Col.Box, props);
            b.Build("Boathouse_BoatRoomProps", kit.Root, false, true);
        }

        /// <summary>
        /// An old side-by-side double-barrelled shotgun with outside hammers and a walnut stock and fore-end, loaded with
        /// two shells (<see cref="DoubleBarrelGun"/>). Built in the gun's own frame (origin at the trigger, +Z out of the
        /// barrels, +Y up) and laid on its side at <paramref name="trigger"/>.
        /// </summary>
        private static DoubleBarrelGun BuildShotgun(Kit kit, Vector3 trigger, float yaw)
        {
            Material walnut = kit.Mat("GunWalnut", Palette.GunWalnut, 0.55f);
            Material steel = kit.Mat("GunSteel", Palette.GunBluedSteel, 0.62f, null, 0.85f);
            Material brass = kit.Mat("GunBrass", Palette.GunBrass, 0.7f, null, 1f);
            Material black = kit.Mat("Black", Palette.DeepFjordSlate * 0.6f, 0.5f);
            Material flashMat = kit.Mat("MuzzleFlash", Palette.LanternAmber, 0f, Palette.SunbreakKey * 9f);

            var root = new GameObject("Shotgun");
            root.transform.SetParent(kit.Root, false);
            root.transform.localPosition = trigger;
            root.transform.localRotation = Quaternion.Euler(0f, yaw, 90f);   // on its side: the stock points across the bench
            Transform g = root.transform;
            const float Breech = 0.07f, BarrelLen = 0.66f, R = 0.0105f;
            float muzzleZ = Breech + BarrelLen;

            foreach (float sx in new[] { -1f, 1f })
            {
                GameObject barrel = kit.Cyl("Barrel", new Vector3(sx * R, 0f, Breech + BarrelLen * 0.5f), R, BarrelLen, steel, Col.None, g);
                barrel.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                GameObject bore = kit.Cyl("Bore", new Vector3(sx * R, 0f, muzzleZ + 0.0005f), R * 0.72f, 0.002f, black, Col.None, g);
                bore.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                GameObject hammer = kit.Box("Hammer", new Vector3(sx * 0.017f - 0.004f, 0.004f, -0.062f), new Vector3(sx * 0.017f + 0.004f, 0.034f, -0.048f), steel, Col.None, g);
                hammer.transform.localRotation = Quaternion.Euler(-22f, 0f, 0f);
            }

            kit.Box("Rib", new Vector3(-0.006f, 0.006f, Breech), new Vector3(0.006f, 0.0135f, muzzleZ), steel, Col.None, g);
            kit.Box("UnderRib", new Vector3(-0.005f, -0.0125f, Breech), new Vector3(0.005f, -0.004f, muzzleZ), steel, Col.None, g);
            Ball("Bead", new Vector3(0f, 0.016f, muzzleZ - 0.01f), Vector3.one * 0.007f, brass, g);
            kit.Box("ForeEnd", new Vector3(-0.021f, -0.03f, Breech + 0.03f), new Vector3(0.021f, -0.006f, Breech + 0.27f), walnut, Col.None, g);
            kit.Box("Action", new Vector3(-0.022f, -0.032f, -0.05f), new Vector3(0.022f, 0.012f, Breech), steel, Col.None, g);
            kit.Box("TopLever", new Vector3(-0.004f, 0.012f, -0.052f), new Vector3(0.004f, 0.018f, -0.012f), steel, Col.None, g);
            kit.Box("GuardBottom", new Vector3(-0.004f, -0.064f, -0.052f), new Vector3(0.004f, -0.058f, 0.03f), steel, Col.None, g);
            kit.Box("GuardFront", new Vector3(-0.004f, -0.064f, 0.024f), new Vector3(0.004f, -0.03f, 0.03f), steel, Col.None, g);
            kit.Box("GuardBack", new Vector3(-0.004f, -0.064f, -0.052f), new Vector3(0.004f, -0.035f, -0.046f), steel, Col.None, g);
            foreach (float tz in new[] { -0.014f, 0.008f })
            {
                kit.Box("Trigger", new Vector3(-0.002f, -0.054f, tz - 0.003f), new Vector3(0.002f, -0.03f, tz + 0.003f), brass, Col.None, g);
            }

            // Walnut stock: a slim wrist dropping into the butt, with a black butt plate.
            var stock = new Batch();
            stock.Prism(walnut, 0.019f, 0.3f, new Vector2(-0.05f, 0.012f), new Vector2(-0.05f, -0.032f), new Vector2(-0.19f, -0.07f), new Vector2(-0.19f, -0.012f));
            stock.Prism(walnut, 0.021f, 0.3f, new Vector2(-0.19f, -0.012f), new Vector2(-0.19f, -0.07f), new Vector2(-0.43f, -0.15f), new Vector2(-0.43f, -0.035f));
            stock.Prism(black, 0.0215f, 0.3f, new Vector2(-0.43f, -0.035f), new Vector2(-0.43f, -0.15f), new Vector2(-0.442f, -0.152f), new Vector2(-0.442f, -0.033f));
            stock.Build("Boathouse_ShotgunStock", g, false, true);

            Transform Point(string name, Vector3 at, Transform parent)
            {
                var t = new GameObject(name).transform;
                t.SetParent(parent, false);
                t.localPosition = at;
                return t;
            }

            Transform muzzle = Point("Muzzle", new Vector3(0f, 0f, muzzleZ + 0.01f), g);
            Transform rightGrip = Point("RightGrip", new Vector3(0f, -0.045f, -0.1f), g);
            Transform leftGrip = Point("LeftGrip", new Vector3(0f, -0.036f, Breech + 0.15f), g);

            // Muzzle flash: a six-pointed star and a short cone of fire, with a light; shown for a moment per shot.
            var flash = new GameObject("MuzzleFlash");
            flash.transform.SetParent(muzzle, false);
            for (int k = 0; k < 3; k++)
            {
                GameObject ray = kit.Box("FlashRay", new Vector3(-0.08f, -0.011f, 0f), new Vector3(0.08f, 0.011f, 0.004f), flashMat, Col.None, flash.transform);
                ray.transform.localRotation = Quaternion.Euler(0f, 0f, k * 60f);
            }

            GameObject cone = kit.Cyl("FlashCone", new Vector3(0f, 0f, 0.07f), 0.028f, 0.14f, flashMat, Col.None, flash.transform);
            cone.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            foreach (Renderer r in flash.GetComponentsInChildren<Renderer>(true)) r.shadowCastingMode = ShadowCastingMode.Off;
            var lightGo = new GameObject("FlashLight");
            lightGo.transform.SetParent(flash.transform, false);
            lightGo.transform.localPosition = new Vector3(0f, 0f, 0.1f);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = Palette.LanternAmber;
            light.range = 7f;
            light.intensity = 4f;
            light.shadows = LightShadows.None;

            var box = root.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, -0.065f, 0.15f);
            box.size = new Vector3(0.05f, 0.17f, 1.2f);
            var item = root.AddComponent<PickupItem>();
            item.Configure("Double-barrel (2 shells)", 0f, -1f, false, "Fire", false);
            var gun = root.AddComponent<DoubleBarrelGun>();
            gun.Configure(muzzle, rightGrip, leftGrip, flash, light, 2);
            flash.SetActive(false);
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.isStatic = false;   // it gets picked up
            EditorUtility.SetDirty(gun);
            return gun;
        }

        // ------------------------------------------------------------------ outside: landing, stairs, sea front

        /// <summary>
        /// Timber landing at the door and concrete steps cut into the bank up to the end of the BoathousePath, plus their
        /// walkable zone and corridor.
        /// </summary>
        private static bool BuildWalkway(Transform frame, BoathouseMats m, HouseData h, float sill, WalkableArea area, out Vector3 topWorld, out Vector3 footWorld)
        {
            topWorld = footWorld = Vector3.zero;
            Collider terrain = GameObject.Find("Exit_Terrain")?.GetComponent<Collider>();
            if (terrain == null)
            {
                Debug.LogError("[VillageHousesSetup] No Exit_Terrain collider: the boathouse stairs can't be fitted.");
                return false;
            }

            float? Ground(Vector3 local)
            {
                Vector3 w = frame.TransformPoint(local);
                return terrain.Raycast(new Ray(new Vector3(w.x, 60f, w.z), Vector3.down), out RaycastHit hit, 300f) ? hit.point.y - frame.position.y : (float?)null;
            }

            // The flight runs straight from the landing's landward edge up to the end of the gravel path. The bank it
            // climbs is convex and its triangles are ~14 m across, so the steps can't simply rest on it: they are cut
            // into it instead. The terrain is clipped out under the flight, and solid concrete steps between stepped
            // side walls fill the cut (down below the ground wherever the bank falls away).
            WalkableArea.Corridor path = area.Corridors.FirstOrDefault(c => c.name == "BoathousePath");
            if (path.points == null || path.points.Length == 0)
            {
                Debug.LogError("[VillageHousesSetup] No BoathousePath footpath in the walkable area: rebuild VillageWalkable.json.");
                return false;
            }

            const float Half = 0.55f, Wall = 0.12f;
            Vector3 top = frame.InverseTransformPoint(path.points[path.points.Length - 1]);
            float hw = h.w * 0.5f + 0.45f, hd = h.d * 0.5f + 0.45f, best = float.PositiveInfinity;
            Vector3 foot = Vector3.zero;
            for (float fx = 4.5f; fx <= 5.61f; fx += 0.1f)
            {
                var f = new Vector3(fx, sill, LandingZ0 + 0.05f);
                Vector3 d = top - f;
                d.y = 0f;
                float deg = Mathf.Atan2(top.y - sill, d.magnitude) * Mathf.Rad2Deg;
                if (d.magnitude < 1.5f || deg > 37f) continue;
                Vector3 across = Vector3.Cross(d.normalized, Vector3.up);
                bool clear = true;
                for (float t = 0f; t <= 1f && clear; t += 0.05f)
                {
                    foreach (float side in new[] { -Half - Wall, Half + Wall })
                    {
                        Vector3 p = f + d * t + across * side;
                        if (Mathf.Abs(p.x) < hw && Mathf.Abs(p.z) < hd) clear = false;
                    }
                }

                if (clear && Mathf.Abs(deg - 32f) < best)
                {
                    best = Mathf.Abs(deg - 32f);
                    foot = f;
                }
            }

            if (float.IsPositiveInfinity(best))
            {
                Debug.LogError($"[VillageHousesSetup] No straight flight fits between the boathouse landing and the path end {path.points[path.points.Length - 1]}.");
                return false;
            }

            Vector3 flat = top - foot;
            flat.y = 0f;
            float run = flat.magnitude, rise = top.y - sill;
            float slopeDeg = Mathf.Atan2(rise, run) * Mathf.Rad2Deg;
            Quaternion turn = Quaternion.Euler(0f, Mathf.Atan2(-flat.z, flat.x) * Mathf.Rad2Deg, 0f);   // +x up the flight, +z its sea side
            var b = new Batch();
            float xHouse = h.w * 0.5f + 0.05f;

            // Landing: boards on joists, posts down to the rock, rails on the open sides.
            b.Box(m.Weathered, new Vector3(xHouse, sill - 0.05f, LandingZ0), new Vector3(LandingX1, sill, LandingZ1), 0.6f, null, true);
            foreach (float z in new[] { LandingZ0 + 0.08f, (LandingZ0 + LandingZ1) * 0.5f, LandingZ1 - 0.08f })
            {
                b.Box(m.Tarred, new Vector3(xHouse, sill - 0.22f, z - 0.05f), new Vector3(LandingX1, sill - 0.05f, z + 0.05f), 0.6f);
            }

            void Post(Vector3 top, float size)
            {
                float bottom = (Ground(top) ?? top.y - 1f) - 0.25f;
                if (bottom < top.y - 0.05f) b.Box(m.Tarred, new Vector3(top.x - size, bottom, top.z - size), new Vector3(top.x + size, top.y, top.z + size), 0.6f);
            }

            foreach (float x in new[] { xHouse + 0.1f, LandingX1 - 0.08f })
            foreach (float z in new[] { LandingZ0 + 0.08f, LandingZ1 - 0.08f })
            {
                Post(new Vector3(x, sill - 0.2f, z), 0.06f);
            }

            float railZ = LandingZ1 - 0.05f, railX = LandingX1 - 0.05f;
            foreach (float x in new[] { xHouse + 0.05f, (xHouse + LandingX1) * 0.5f, railX })
            {
                b.Box(m.Weathered, new Vector3(x - 0.04f, sill, railZ - 0.04f), new Vector3(x + 0.04f, sill + 0.98f, railZ + 0.04f), 0.6f);
            }

            b.Box(m.Weathered, new Vector3(railX - 0.04f, sill, -1.24f), new Vector3(railX + 0.04f, sill + 0.98f, -1.16f), 0.6f);
            foreach (float y in new[] { 0.48f, 0.92f })
            {
                b.Box(m.Weathered, new Vector3(xHouse, sill + y, railZ - 0.03f), new Vector3(LandingX1, sill + y + 0.07f, railZ + 0.03f), 0.6f);
                b.Box(m.Weathered, new Vector3(railX - 0.03f, sill + y, -1.24f), new Vector3(railX + 0.03f, sill + y + 0.07f, LandingZ1), 0.6f, null, true);
            }

            var deck = new GameObject("Landing") { isStatic = true };
            deck.transform.SetParent(frame, false);
            var deckBox = deck.AddComponent<BoxCollider>();
            deckBox.center = new Vector3((xHouse + LandingX1) * 0.5f, sill - 0.15f, (LandingZ0 + LandingZ1) * 0.5f);
            deckBox.size = new Vector3(LandingX1 - xHouse, 0.3f, LandingZ1 - LandingZ0);

            // Stairs, in their own frame: x up the flight, z across it (+z the downhill, sea side), y = 0 at the foot.
            Matrix4x4 S = Matrix4x4.TRS(foot, turn, Vector3.one);
            float? G(float a, float z) => Ground(foot + turn * new Vector3(a, 0f, z)) - sill;
            int n = Mathf.Max(2, Mathf.RoundToInt(rise / 0.18f));
            float rStep = rise / n, aStep = run / n, cut = 0f;
            for (int k = 0; k <= n; k++)
            {
                float a0 = k == 0 ? 0f : (k - 0.5f) * aStep, a1 = k == n ? run + 0.12f : (k + 0.5f) * aStep;
                float y = k * rStep - (k == 0 ? 0.01f : 0f);
                float low = y, left = float.NegativeInfinity, right = float.NegativeInfinity;
                foreach (float a in new[] { a0, (a0 + a1) * 0.5f, a1 })
                {
                    foreach (float z in new[] { -Half - Wall, 0f, Half + Wall })
                    {
                        float g = G(a, z) ?? y;
                        low = Mathf.Min(low, g);
                        if (z < 0f) left = Mathf.Max(left, g);
                        if (z > 0f) right = Mathf.Max(right, g);
                    }
                }

                b.Box(m.ConcreteFloor, new Vector3(a0, low - 0.4f, -Half), new Vector3(a1, y, Half), 1.6f, S, true);
                foreach ((float z0, float z1, float g) in new[] { (-Half - Wall, -Half, left), (Half, Half + Wall, right) })
                {
                    b.Box(m.Concrete, new Vector3(a0, Mathf.Min(g, y) - 0.4f, z0), new Vector3(a1, Mathf.Max(g + 0.06f, y + 0.12f), z1), 1.6f, S);
                }

                cut = Mathf.Max(cut, Mathf.Max(left, right) - y);
            }

            // Handrail on the sea side, standing on the side wall.
            float length = new Vector2(run, rise).magnitude;
            Quaternion slope = Quaternion.Euler(0f, 0f, slopeDeg);
            foreach (float f in new[] { 0.06f, 0.5f, 0.94f })
            {
                b.Box(m.Weathered, new Vector3(run * f - 0.04f, rise * f + 0.1f, Half + 0.02f), new Vector3(run * f + 0.04f, rise * f + 0.98f, Half + 0.1f), 0.6f, S);
            }

            Matrix4x4 rail = S * Matrix4x4.TRS(new Vector3(run * 0.5f, rise * 0.5f + 0.94f, Half + 0.06f), slope, Vector3.one);
            b.Box(m.Weathered, new Vector3(-length * 0.47f, -0.035f, -0.04f), new Vector3(length * 0.47f, 0.035f, 0.04f), 0.6f, rail);
            b.Build("Boathouse_Walkway", frame, false, true);

            var ramp = new GameObject("StairRamp") { isStatic = true };
            ramp.transform.SetParent(frame, false);
            ramp.transform.localPosition = foot + turn * new Vector3(run * 0.5f, rise * 0.5f, 0f);
            ramp.transform.localRotation = turn * slope;
            var rampBox = ramp.AddComponent<BoxCollider>();
            rampBox.center = new Vector3(0f, -0.05f, 0f);
            rampBox.size = new Vector3(length, 0.1f, Half * 2f);

            var cutFrame = new GameObject("StairCut").transform;
            cutFrame.SetPositionAndRotation(frame.TransformPoint(foot + turn * new Vector3((run + 0.12f) * 0.5f, 0f, 0f)), frame.rotation * turn);
            CutTerrainHole(cutFrame, (run + 0.12f) * 0.5f, Half + Wall, "the boathouse stairs");
            UnityEngine.Object.DestroyImmediate(cutFrame.gameObject);

            // Walkable: the landing, and a corridor up the flight to the gravel path.
            footWorld = frame.TransformPoint(foot);
            topWorld = path.points[path.points.Length - 1];
            var landing = new WalkableArea.Zone
            {
                name = "Boathouse_Landing",
                outline = new[]
                {
                    frame.TransformPoint(new Vector3(xHouse, sill, LandingZ0)), frame.TransformPoint(new Vector3(LandingX1 - 0.12f, sill, LandingZ0)),
                    frame.TransformPoint(new Vector3(LandingX1 - 0.12f, sill, LandingZ1 - 0.12f)), frame.TransformPoint(new Vector3(xHouse, sill, LandingZ1 - 0.12f)),
                },
            };
            var stairs = new WalkableArea.Corridor { name = "Boathouse_Stairs", halfWidth = 0.45f, points = new[] { footWorld, topWorld } };
            area.Configure(area.Corridors.Append(stairs).ToArray(), area.Zones.Append(landing).ToArray());
            EditorUtility.SetDirty(area);
            Debug.Log($"[VillageHousesSetup] Boathouse stairs: {n} steps, foot x {foot.x:F1}, slope {slopeDeg:F0} deg, run {run:F2} m, rise {rise:F2} m, cut up to {cut:F2} m into the bank, top {topWorld}");
            return true;
        }

        /// <summary>Tarred boat doors on the foundation's sea face and a concrete slipway down into the fjord.</summary>
        private static void BuildSeaFront(Transform frame, BoathouseMats m, HouseData h, float down)
        {
            var b = new Batch();
            float zf = h.d * 0.5f + 0.045f, ht = 2.15f;
            foreach (float s in new[] { -1f, 1f })
            {
                float x0 = Mathf.Min(s * 0.015f, s * 1.5f), x1 = Mathf.Max(s * 0.015f, s * 1.5f);
                b.Box(m.Tarred, new Vector3(x0, down, zf), new Vector3(x1, down + ht, zf + 0.06f), 0.6f);
                foreach (float y in new[] { 0.3f, 1.05f, 1.8f })
                {
                    b.Box(m.Black, new Vector3(s > 0 ? x1 - 0.7f : x0, down + y, zf + 0.06f), new Vector3(s > 0 ? x1 : x0 + 0.7f, down + y + 0.06f, zf + 0.075f), 1f);
                }
            }

            b.Box(m.Tarred, new Vector3(-1.64f, down, zf), new Vector3(-1.5f, down + ht + 0.14f, zf + 0.1f), 0.6f);
            b.Box(m.Tarred, new Vector3(1.5f, down, zf), new Vector3(1.64f, down + ht + 0.14f, zf + 0.1f), 0.6f);
            b.Box(m.Tarred, new Vector3(-1.64f, down + ht, zf), new Vector3(1.64f, down + ht + 0.14f, zf + 0.1f), 0.6f);

            Matrix4x4 slip = Matrix4x4.TRS(new Vector3(0f, down - 0.03f, zf), Quaternion.Euler(9f, 0f, 0f), Vector3.one);
            b.Box(m.ConcreteFloor, new Vector3(-1.45f, -2.6f, 0f), new Vector3(1.45f, 0f, 5.2f), 1.6f, slip, true);
            foreach (float x in new[] { -0.5f, 0.5f })
            {
                b.Box(m.Tarred, new Vector3(x - 0.07f, 0f, 0.1f), new Vector3(x + 0.07f, 0.08f, 5.0f), 0.6f, slip, true);
            }

            b.Build("Boathouse_SeaFront", frame, false, true);
        }

        // ------------------------------------------------------------------ terrain

        /// <summary>
        /// Cuts the terrain out of a rectangle in <paramref name="frame"/> (a house footprint, or under steps cut into a
        /// bank), so the hillside can't fill rooms that go below ground. The terrain triangles are ~14 m across, far too coarse to press down locally, so
        /// they are clipped against the rectangle instead; the house's foundation hides the cut from outside.
        /// </summary>
        private static void CutTerrainHole(Transform frame, float halfW, float halfD, string what)
        {
            GameObject terrainGo = GameObject.Find("Exit_Terrain");
            if (terrainGo == null || !terrainGo.TryGetComponent(out MeshFilter mf) || mf.sharedMesh == null) return;
            Mesh mesh = mf.sharedMesh;
            if (!AssetDatabase.GetAssetPath(mesh).EndsWith(".asset"))
            {
                Debug.LogWarning($"[VillageHousesSetup] Terrain mesh is the imported FBX; not cutting it under {what}.");
                return;
            }

            Transform tf = terrainGo.transform;
            var pos = new List<Vector3>(); mesh.GetVertices(pos);
            var nrm = new List<Vector3>(); mesh.GetNormals(nrm);
            var tan = new List<Vector4>(); mesh.GetTangents(tan);
            var col = new List<Color>(); mesh.GetColors(col);
            var uv = new List<Vector2>[4];
            for (int c = 0; c < 4; c++) { uv[c] = new List<Vector2>(); mesh.GetUVs(c, uv[c]); }
            int count = pos.Count;
            var flat = new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                Vector3 l = frame.InverseTransformPoint(tf.TransformPoint(pos[i]));
                flat[i] = new Vector2(l.x, l.z);
            }

            var planes = new[] { (new Vector2(1f, 0f), halfW), (new Vector2(-1f, 0f), halfW), (new Vector2(0f, 1f), halfD), (new Vector2(0f, -1f), halfD) };
            var subs = new List<int>[mesh.subMeshCount];
            int clipped = 0;
            for (int sm = 0; sm < mesh.subMeshCount; sm++)
            {
                int[] tris = mesh.GetTriangles(sm);
                var keep = new List<int>(tris.Length);
                for (int t = 0; t < tris.Length; t += 3)
                {
                    int ia = tris[t], ib = tris[t + 1], ic = tris[t + 2];
                    Vector2 a = flat[ia], b = flat[ib], c = flat[ic];
                    if (Mathf.Max(a.x, b.x, c.x) <= -halfW || Mathf.Min(a.x, b.x, c.x) >= halfW
                        || Mathf.Max(a.y, b.y, c.y) <= -halfD || Mathf.Min(a.y, b.y, c.y) >= halfD)
                    {
                        keep.Add(ia); keep.Add(ib); keep.Add(ic);
                        continue;
                    }

                    clipped++;
                    var poly = new List<(Vector2 p, Vector3 w)> { (a, new Vector3(1f, 0f, 0f)), (b, new Vector3(0f, 1f, 0f)), (c, new Vector3(0f, 0f, 1f)) };
                    foreach ((Vector2 n, float d) in planes)
                    {
                        // Whatever lies beyond this edge of the rectangle is outside the hole: keep it; go on with the rest.
                        Emit(ClipHalf(poly, n, d, true), ia, ib, ic, keep);
                        poly = ClipHalf(poly, n, d, false);
                        if (poly.Count < 3) break;
                    }
                }

                subs[sm] = keep;
            }

            void Emit(List<(Vector2 p, Vector3 w)> piece, int ia, int ib, int ic, List<int> keep)
            {
                if (piece.Count < 3) return;
                float area2 = 0f;
                for (int i = 0; i < piece.Count; i++) { Vector2 p = piece[i].p, q = piece[(i + 1) % piece.Count].p; area2 += p.x * q.y - q.x * p.y; }
                if (Mathf.Abs(area2) < 1e-5f) return;
                var idx = new int[piece.Count];
                for (int i = 0; i < piece.Count; i++)
                {
                    Vector3 w = piece[i].w;
                    if (w.x > 0.9999f) { idx[i] = ia; continue; }
                    if (w.y > 0.9999f) { idx[i] = ib; continue; }
                    if (w.z > 0.9999f) { idx[i] = ic; continue; }
                    idx[i] = pos.Count;
                    pos.Add(pos[ia] * w.x + pos[ib] * w.y + pos[ic] * w.z);
                    if (nrm.Count > 0) nrm.Add((nrm[ia] * w.x + nrm[ib] * w.y + nrm[ic] * w.z).normalized);
                    if (tan.Count > 0) tan.Add(tan[ia] * w.x + tan[ib] * w.y + tan[ic] * w.z);
                    if (col.Count > 0) col.Add(col[ia] * w.x + col[ib] * w.y + col[ic] * w.z);
                    for (int c = 0; c < 4; c++)
                    {
                        if (uv[c].Count > 0) uv[c].Add(uv[c][ia] * w.x + uv[c][ib] * w.y + uv[c][ic] * w.z);
                    }
                }

                for (int i = 1; i < idx.Length - 1; i++) { keep.Add(idx[0]); keep.Add(idx[i]); keep.Add(idx[i + 1]); }
            }

            if (clipped == 0) return;
            if (pos.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(pos);
            if (nrm.Count > 0) mesh.SetNormals(nrm);
            if (tan.Count > 0) mesh.SetTangents(tan);
            if (col.Count > 0) mesh.SetColors(col);
            for (int c = 0; c < 4; c++)
            {
                if (uv[c].Count > 0) mesh.SetUVs(c, uv[c]);
            }

            for (int sm = 0; sm < subs.Length; sm++) mesh.SetTriangles(subs[sm], sm);
            mesh.RecalculateBounds();
            EditorUtility.SetDirty(mesh);
            if (terrainGo.TryGetComponent(out MeshCollider mc))
            {
                mc.sharedMesh = null;
                mc.sharedMesh = mesh;
            }

            Debug.Log($"[VillageHousesSetup] cut the terrain out from under {what} ({clipped} triangles clipped, {pos.Count - count} vertices added)");
        }

        /// <summary>Sutherland-Hodgman: the part of a convex polygon inside (dot(n, p) &lt;= d) or outside that edge.</summary>
        private static List<(Vector2 p, Vector3 w)> ClipHalf(List<(Vector2 p, Vector3 w)> poly, Vector2 n, float d, bool outside)
        {
            var res = new List<(Vector2, Vector3)>(poly.Count + 2);
            float F((Vector2 p, Vector3 w) v) => outside ? d - Vector2.Dot(n, v.p) : Vector2.Dot(n, v.p) - d;
            for (int i = 0; i < poly.Count; i++)
            {
                var cur = poly[i];
                var nxt = poly[(i + 1) % poly.Count];
                float fc = F(cur), fn = F(nxt);
                if (fc <= 0f) res.Add(cur);
                if ((fc < 0f && fn > 0f) || (fc > 0f && fn < 0f))
                {
                    float t = fc / (fc - fn);
                    res.Add((Vector2.Lerp(cur.p, nxt.p, t), Vector3.Lerp(cur.w, nxt.w, t)));
                }
            }

            return res;
        }

        // ------------------------------------------------------------------ walk test

        /// <summary>
        /// Walks a player down the boathouse stairs to the door and back up, then (indoors) from the loft door down to
        /// the boat room and back, with the CharacterController, and logs whether they got there at the right height.
        /// </summary>
        public static void VerifyBoathouse(CharacterMotor player)
        {
            BoathouseProbe probe = _boathouseProbe;
            if (probe == null || player == null || !player.TryGetComponent(out CharacterController cc)) return;
            Transform t = player.transform;
            Vector3 keepPos = t.position;
            Quaternion keepRot = t.rotation;
            string log = "[VillageHousesSetup] Boathouse walk test:";
            bool ok = true;

            void Put(Vector3 p)
            {
                cc.enabled = false;
                t.position = p + Vector3.up * 0.05f;
                cc.enabled = true;
                Physics.SyncTransforms();
            }

            void Walk(string label, Vector3[] route)
            {
                Put(route[0]);
                for (int w = 1; w < route.Length; w++)
                {
                    for (int step = 0; step < 400; step++)
                    {
                        Vector3 d = route[w] - t.position;
                        d.y = 0f;
                        if (d.magnitude < 0.08f) break;
                        cc.Move(d.normalized * Mathf.Min(0.06f, d.magnitude) + Vector3.down * 0.1f);
                        Physics.SyncTransforms();
                    }
                }

                Vector3 end = route[route.Length - 1], at = t.position;
                float flat = new Vector2(at.x - end.x, at.z - end.z).magnitude, dy = at.y - end.y;
                bool good = flat < 0.3f && Mathf.Abs(dy) < 0.2f;
                ok &= good;
                log += $"\n  {label}: ended {flat:F2} m from the goal, {dy:+0.00;-0.00} m off its height -> {(good ? "OK" : "FAILED")}";
            }

            Walk("down the stairs to the door", probe.OutsideRoute);
            Walk("back up to the path", Enumerable.Reverse(probe.OutsideRoute).ToArray());
            probe.Interior.SetInside(player, true);
            Walk("loft door down to the boat room", probe.InsideRoute);
            Walk("boat room back up to the loft door", Enumerable.Reverse(probe.InsideRoute).ToArray());
            Walk("round the boat to the workbench", probe.AroundBoatRoute);
            Walk("workbench back round the boat to the stairs", Enumerable.Reverse(probe.AroundBoatRoute).ToArray());
            probe.Interior.SetInside(player, false);

            // The front door starts locked; only Leivur's key turns it, and that key doesn't fit the sea mine.
            HouseDoor door = probe.FrontDoor;
            HouseKey key = UnityEngine.Object.FindObjectsByType<HouseKey>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();
            CharacterMotor owner = key != null ? key.GetComponentInParent<CharacterMotor>(true) : null;
            bool lockOk = door != null && door.IsLocked && key != null && key.LockId == BoathouseLockId
                          && owner != null && owner.name == "Leivur" && key.GetComponentInParent<CharacterPockets>(true) != null
                          && key.TryGetComponent(out BombKey bombKey) && !bombKey.FitsBomb;
            if (lockOk)
            {
                Put(probe.OutsideRoute[probe.OutsideRoute.Length - 1]);
                lockOk = door.InReach(player, out _) && !door.TryUnlock("wrong") && door.IsLocked && door.TryUnlock(key.LockId) && !door.IsLocked
                         && door.OtherSide != null && !door.OtherSide.IsLocked;
                door.SetLock(BoathouseLockId, true);   // back to locked for the game
            }

            ok &= lockOk;

            // A brass sea-mine key hangs on the boathouse loft wall inside the interior.
            BombKey wallBombKey = UnityEngine.Object.FindObjectsByType<BombKey>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(k => k.name == "BombKey_Boathouse");
            bool wallKeyOk = wallBombKey != null && wallBombKey.FitsBomb
                             && wallBombKey.GetComponent<HouseKey>() == null
                             && wallBombKey.GetComponentInParent<HouseInterior>(true) == probe.Interior
                             && !wallBombKey.gameObject.isStatic;
            ok &= wallKeyOk;
            log += $"\n  brass bomb key hanging on the boathouse wall -> {(wallKeyOk ? "OK" : "FAILED")}";

            // The shotgun: Leivur picks it up off the bench, fires both barrels, and the third pull only clicks.
            DoubleBarrelGun gun = UnityEngine.Object.FindObjectsByType<DoubleBarrelGun>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();
            bool gunOk = false;
            if (gun != null && player.TryGetComponent(out CharacterItemHolder holder))
            {
                Transform keepParent = gun.transform.parent;
                Vector3 gunPos = gun.transform.localPosition;
                Quaternion gunRot = gun.transform.localRotation;
                PickupItem keepHeld = holder.HeldItem;
                var gunItem = gun.GetComponent<PickupItem>();
                probe.Interior.SetInside(player, true);
                Put(probe.InsideRoute[probe.InsideRoute.Length - 1]);
                int loaded = gun.Shells;
                bool picked = holder.TryPickup(gunItem);
                holder.TryUseHeldItem();
                int afterOne = gun.Shells;
                holder.TryUseHeldItem();
                holder.TryUseHeldItem();
                gunOk = picked && loaded == 2 && afterOne == 1 && gun.Shells == 0 && gunItem.DisplayName.Contains("empty")
                        && gunItem.GetComponent<BombKey>() == null;

                holder.ReleaseHeldItem();
                gun.ClearEffects();
                gun.transform.SetParent(keepParent, false);
                gun.transform.SetLocalPositionAndRotation(gunPos, gunRot);
                gun.Reload(2);
                if (keepHeld != null) holder.TryPickup(keepHeld);
                probe.Interior.SetInside(player, false);
            }

            ok &= gunOk;
            log += $"\n  shotgun on the workbench: picked up, 2 shots then empty -> {(gunOk ? "OK" : "FAILED")}";
            log += $"\n  front door locked, Leivur's key opens it at the door, a wrong key and the sea mine don't -> {(lockOk ? "OK" : "FAILED")}";

            cc.enabled = false;
            t.SetPositionAndRotation(keepPos, keepRot);
            cc.enabled = true;
            Physics.SyncTransforms();
            if (ok) Debug.Log(log);
            else Debug.LogError(log);
        }

        // ------------------------------------------------------------------ geometry helpers

        private static GameObject Ball(string name, Vector3 centre, Vector3 size, Material mat, Transform parent, bool shadows = false)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = centre;
            go.transform.localScale = size;
            Kit.Finish(go, mat, shadows);
            return go;
        }

        /// <summary>
        /// Collects boxes and flat polygons into one mesh (a submesh per material) with UVs in metres / tile, so board
        /// and concrete textures keep their real size on every face. Saved as an asset next to the interior materials.
        /// </summary>
        /// <summary>Materials whose wood grain should run along each piece (beams, posts, rails) rather than as wall boards.</summary>
        private static readonly HashSet<Material> LongGrainMats = new HashSet<Material>();

        private sealed class Batch
        {
            private readonly List<Vector3> _v = new List<Vector3>();
            private readonly List<Vector3> _n = new List<Vector3>();
            private readonly List<Vector2> _uv = new List<Vector2>();
            private readonly List<Material> _mats = new List<Material>();
            private readonly List<List<int>> _tris = new List<List<int>>();

            private static readonly int[][] Faces =
            {
                new[] { 1, 3, 7, 5 }, new[] { 0, 4, 6, 2 },   // +x, -x
                new[] { 2, 6, 7, 3 }, new[] { 0, 1, 5, 4 },   // +y, -y
                new[] { 4, 5, 7, 6 }, new[] { 0, 2, 3, 1 },   // +z, -z
            };

            private static readonly Vector3[] FaceNormals = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };

            /// <summary>An axis-aligned box in its own space, placed by <paramref name="xf"/>. Boards run up the sides, and along x on top (along z if asked).</summary>
            public void Box(Material m, Vector3 min, Vector3 max, float tile, Matrix4x4? xf = null, bool boardsAlongZ = false)
            {
                Matrix4x4 x = xf ?? Matrix4x4.identity;
                var c = new Vector3[8];
                for (int i = 0; i < 8; i++) c[i] = new Vector3((i & 1) != 0 ? max.x : min.x, (i & 2) != 0 ? max.y : min.y, (i & 4) != 0 ? max.z : min.z);
                bool longGrain = LongGrainMats.Contains(m);
                Vector3 size = max - min;
                for (int f = 0; f < 6; f++)
                {
                    Vector3 u = f < 2 ? Vector3.forward : f < 4 ? (boardsAlongZ ? Vector3.right : Vector3.forward) : Vector3.right;
                    Vector3 v = f < 2 || f >= 4 ? Vector3.up : (boardsAlongZ ? Vector3.forward : Vector3.right);
                    if (longGrain && Mathf.Abs(Vector3.Dot(size, u)) > Mathf.Abs(Vector3.Dot(size, v))) (u, v) = (v, u);
                    var pts = new Vector3[4];
                    var uvs = new Vector2[4];
                    for (int k = 0; k < 4; k++)
                    {
                        Vector3 p = c[Faces[f][k]];
                        uvs[k] = new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, v)) / tile;
                        pts[k] = x.MultiplyPoint3x4(p);
                    }

                    Add(m, pts, x.MultiplyVector(FaceNormals[f]).normalized, uvs);
                }
            }

            /// <summary>A convex (z, y) profile extruded across x by +-<paramref name="halfWidth"/> (the shotgun stock).</summary>
            public void Prism(Material m, float halfWidth, float tile, params Vector2[] zy)
            {
                Vector2 centre = Vector2.zero;
                foreach (Vector2 p in zy) centre += p / zy.Length;
                Vector3 P(Vector2 p, float x) => new Vector3(x, p.y, p.x);
                Poly(m, Vector3.right, Vector3.forward, Vector3.up, tile, zy.Select(p => P(p, halfWidth)).ToArray());
                Poly(m, Vector3.left, Vector3.forward, Vector3.up, tile, zy.Select(p => P(p, -halfWidth)).ToArray());
                for (int i = 0; i < zy.Length; i++)
                {
                    Vector2 a = zy[i], b = zy[(i + 1) % zy.Length];
                    Vector3 along = P(b, 0f) - P(a, 0f);
                    Vector3 n = Vector3.Cross(Vector3.right, along).normalized;
                    if (Vector3.Dot(n, P((a + b) * 0.5f - centre, 0f)) < 0f) n = -n;
                    Poly(m, n, Vector3.right, along.normalized, tile, P(a, -halfWidth), P(b, -halfWidth), P(b, halfWidth), P(a, halfWidth));
                }
            }

            /// <summary>A flat convex polygon facing <paramref name="normal"/>, its texture projected on the u and v axes.</summary>
            public void Poly(Material m, Vector3 normal, Vector3 u, Vector3 v, float tile, params Vector3[] pts)
            {
                Add(m, pts, normal.normalized, pts.Select(p => new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, v)) / tile).ToArray());
            }

            private void Add(Material m, Vector3[] pts, Vector3 normal, Vector2[] uvs)
            {
                int s = _mats.IndexOf(m);
                if (s < 0)
                {
                    _mats.Add(m);
                    _tris.Add(new List<int>());
                    s = _mats.Count - 1;
                }

                int b = _v.Count;
                for (int i = 0; i < pts.Length; i++)
                {
                    _v.Add(pts[i]);
                    _n.Add(normal);
                    _uv.Add(uvs[i]);
                }

                bool flip = Vector3.Dot(Vector3.Cross(pts[1] - pts[0], pts[2] - pts[0]), normal) < 0f;
                for (int i = 1; i < pts.Length - 1; i++)
                {
                    _tris[s].Add(b);
                    _tris[s].Add(flip ? b + i + 1 : b + i);
                    _tris[s].Add(flip ? b + i : b + i + 1);
                }
            }

            public GameObject Build(string name, Transform parent, bool collider, bool shadows)
            {
                if (_v.Count == 0) return null;
                var mesh = new Mesh { name = name, indexFormat = _v.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
                mesh.SetVertices(_v);
                mesh.SetNormals(_n);
                mesh.SetUVs(0, _uv);
                mesh.subMeshCount = _tris.Count;
                for (int i = 0; i < _tris.Count; i++) mesh.SetTriangles(_tris[i], i);
                mesh.RecalculateBounds();
                string path = $"{InteriorDir}/{name}.asset";
                AssetDatabase.DeleteAsset(path);
                AssetDatabase.CreateAsset(mesh, path);

                var go = new GameObject(name) { isStatic = true };
                go.transform.SetParent(parent, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterials = _mats.ToArray();
                r.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
                if (collider) go.AddComponent<MeshCollider>().sharedMesh = mesh;
                return go;
            }
        }

        // ------------------------------------------------------------------ procedural textures (all tile seamlessly)

        private static Texture2D SaveTexture(string file, int size, Color32[] px)
        {
            string path = $"{InteriorDir}/{file}";
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.SetPixels32(px);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.sRGBTexture = true;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.mipmapEnabled = true;
            importer.anisoLevel = 4;
            importer.maxTextureSize = 512;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint n = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
                n = (n ^ (n >> 13)) * 1274126177u;
                return ((n ^ (n >> 16)) & 0xFFFF) / 65535f;
            }
        }

        /// <summary>Perlin noise that wraps every <paramref name="size"/> pixels (four shifted copies blended).</summary>
        private static float TileNoise(float x, float y, float size, float freq, float seed)
        {
            float N(float a, float b) => Mathf.PerlinNoise(a * freq + seed, b * freq + seed * 0.37f);
            float u = x / size, v = y / size;
            return N(x, y) * (1f - u) * (1f - v) + N(x - size, y) * u * (1f - v) + N(x, y - size) * (1f - u) * v + N(x - size, y - size) * u * v;
        }

        /// <summary>Four vertical pine boards (a 0.6 m tile): grain, knots, dark gaps and the odd butt joint.</summary>
        private static Color32[] PaintPlanks(int size)
        {
            var px = new Color32[size * size];
            var rng = new System.Random(5150);
            const int Boards = 4;
            int bw = size / Boards;
            var tone = new float[Boards];
            var seed = new float[Boards];
            var joint = new int[Boards];
            var knots = new List<(float x, float y, float r)>();
            for (int b = 0; b < Boards; b++)
            {
                tone[b] = (float)rng.NextDouble();
                seed[b] = rng.Next(1, 900);
                joint[b] = rng.NextDouble() < 0.5 ? rng.Next(size / 8, size * 7 / 8) : -1;
                int k = rng.Next(0, 3);
                for (int i = 0; i < k; i++)
                {
                    knots.Add((b * bw + bw * (0.25f + 0.5f * (float)rng.NextDouble()), size * (0.1f + 0.8f * (float)rng.NextDouble()), 4f + 5f * (float)rng.NextDouble()));
                }
            }

            Color light = Palette.BarnPlank, dark = Palette.BarnPlankDark;
            for (int y = 0; y < size; y++)
            {
                float th = 2f * Mathf.PI * y / size;
                for (int x = 0; x < size; x++)
                {
                    int b = x / bw;
                    float bu = (x % bw) / (float)bw;
                    float wob = Mathf.PerlinNoise(Mathf.Cos(th) * 1.4f + seed[b], Mathf.Sin(th) * 1.4f + x * 0.012f);
                    float grain = 0.5f + 0.5f * Mathf.Sin(x * 0.55f + wob * 11f);
                    float t = 0.15f + tone[b] * 0.32f + grain * 0.2f + Hash(x, y, 7) * 0.07f;
                    foreach ((float kx, float ky, float kr) in knots)
                    {
                        float dx = (x - kx) / kr, dy = (y - ky) / (kr * 1.7f), d = dx * dx + dy * dy;
                        if (d < 1f) t = Mathf.Max(t, 0.55f + 0.35f * (1f - d));
                        else if (d < 2.4f) t += 0.08f * Mathf.Sin(d * 6f);
                    }

                    Color c = Color.Lerp(light, dark, Mathf.Clamp01(t));
                    if (bu < 0.022f || bu > 0.978f) c = Color.Lerp(c, dark * 0.4f, 0.85f);
                    else if (bu < 0.06f || bu > 0.95f) c *= 0.9f;
                    if (joint[b] >= 0 && Mathf.Abs(y - joint[b]) < 1.5f) c = Color.Lerp(c, dark * 0.45f, 0.8f);
                    c.a = 1f;
                    px[y * size + x] = c;
                }
            }

            return px;
        }

        /// <summary>Board-formed concrete (a 1.6 m tile): blotchy, with the formwork's horizontal lines and air holes.</summary>
        private static Color32[] PaintConcrete(int size)
        {
            var px = new Color32[size * size];
            Color baseC = Palette.BoathouseConcrete;
            int band = size / 4;
            for (int y = 0; y < size; y++)
            {
                float bandTone = (Hash(y / band, 3, 11) - 0.5f) * 0.08f;
                for (int x = 0; x < size; x++)
                {
                    float n1 = TileNoise(x, y, size, 0.011f, 3.1f), n2 = TileNoise(x, y, size, 0.05f, 7.7f);
                    float streak = TileNoise(x, y * 0.08f, size, 0.09f, 1.3f);
                    float k = 0.78f + n1 * 0.22f + n2 * 0.1f + streak * 0.05f + Hash(x, y, 5) * 0.06f + bandTone;
                    if (y % band < 2) k *= 0.82f;
                    Color c = baseC * k;
                    c.a = 1f;
                    px[y * size + x] = c;
                }
            }

            var rng = new System.Random(77);
            for (int i = 0; i < 420; i++)
            {
                int cx = rng.Next(size), cy = rng.Next(size), r = rng.Next(1, 3);
                for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dy * dy > r * r) continue;
                    int idx = ((cy + dy + size) % size) * size + (cx + dx + size) % size;
                    Color c = px[idx];
                    px[idx] = c * 0.62f + new Color(0f, 0f, 0f, 0.38f);
                }
            }

            return px;
        }

        /// <summary>Loose straw (a 0.5 m tile): thousands of short strokes in mixed directions.</summary>
        private static Color32[] PaintHay(int size)
        {
            var px = new Color32[size * size];
            Color bg = Color.Lerp(Palette.HayDry, Palette.HayStraw, 0.3f) * 0.8f;
            bg.a = 1f;
            for (int i = 0; i < px.Length; i++) px[i] = bg;
            var rng = new System.Random(1234);
            for (int s = 0; s < 3200; s++)
            {
                float x = rng.Next(size), y = rng.Next(size), ang = (float)(rng.NextDouble() * Math.PI * 2.0), len = 6f + (float)rng.NextDouble() * 22f;
                Color c = Color.Lerp(Palette.HayDry, Palette.HayStraw * 1.15f, (float)rng.NextDouble());
                c.a = 1f;
                Color shade = c * 0.6f;
                shade.a = 1f;
                float dx = Mathf.Cos(ang), dy = Mathf.Sin(ang);
                for (float d = 0f; d < len; d += 0.7f)
                {
                    int ix = Mathf.FloorToInt(x + dx * d), iy = Mathf.FloorToInt(y + dy * d);
                    px[(((iy + 1) % size + size) % size) * size + ((ix + 1) % size + size) % size] = shade;
                    px[((iy % size + size) % size) * size + (ix % size + size) % size] = c;
                }
            }

            return px;
        }

        /// <summary>Grey wool curls (tinted per sheep by the material colour).</summary>
        private static Color32[] PaintWool(int size)
        {
            var px = new Color32[size * size];
            for (int i = 0; i < px.Length; i++) px[i] = new Color(0.7f, 0.7f, 0.7f, 1f);
            var rng = new System.Random(808);
            for (int s = 0; s < 1500; s++)
            {
                int cx = rng.Next(size), cy = rng.Next(size);
                float r = 2.5f + (float)rng.NextDouble() * 4.5f;
                int ri = Mathf.CeilToInt(r);
                for (int dy = -ri; dy <= ri; dy++)
                for (int dx = -ri; dx <= ri; dx++)
                {
                    float d = Mathf.Sqrt(dx * dx + dy * dy) / r;
                    if (d > 1f) continue;
                    float v = 0.68f + 0.32f * (1f - d * d) - (d > 0.8f ? 0.1f : 0f);
                    px[((cy + dy + size) % size) * size + (cx + dx + size) % size] = new Color(v, v, v, 1f);
                }
            }

            return px;
        }
    }
}
