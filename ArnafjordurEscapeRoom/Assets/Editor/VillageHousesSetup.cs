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
    /// Village houses from VillageHouses.json (written by build_tunnel_exit.py --houses-only): makes every front door
    /// the players can walk up to knockable (<see cref="HouseDoor"/>), and builds the rooms inside the teal house by
    /// the beach (OSM building 21 plus its entrance wing, building 38): entrance hall, hallway, kitchen, living room,
    /// bathroom and bedroom (<see cref="HouseInterior"/>). The black boathouse by the end of the road (building 50) gets
    /// its own walkway, a hay loft and a boat room: see VillageHousesSetup.Boathouse.cs. Called by
    /// <see cref="VillageSceneSetup.Build"/> once the walkable area exists.
    /// </summary>
    public static partial class VillageHousesSetup
    {
        public const string HousesJsonPath = "Assets/Environment/TunnelExit/VillageHouses.json";
        private const string InteriorDir = "Assets/Environment/Village/Interior";
        private const string DoorsRootName = "VillageHouseDoors";
        private const string InteriorRootName = "TealHouse_Interior";

        private const int TealHouse = 21, TealWing = 38;
        private const float MaxKnockDistance = 2.2f;   // a door counts when walkable ground comes this close to it
        private const float Inset = 0.18f;             // room edges sit this far inside the outer walls
        private const float Skin = 0.05f;              // thickness of each room's wall lining
        private const float HalfPartition = 0.05f;     // inner walls are two linings back to back, 0.1 m apart
        private const float FloorLift = 0.03f;         // room floors sit just above the plinth top of the shell
        private const float DoorTop = 2.05f;

        private const string TealReply = "No answer... but the door isn't locked.";
        private static readonly string[] Replies =
        {
            "Nobody answers.",
            "You hear a TV through the door, but nobody comes.",
            "A dog barks inside. Nobody opens.",
            "A curtain twitches upstairs. Nobody comes down.",
            "Silence. They must be out on the boat.",
            "Someone shouts \"Vit keypa einki!\" They're not buying anything.",
            "Footsteps... then nothing. Nobody opens.",
        };

        [Serializable] private class HouseFile { public HouseData[] houses; }

        [Serializable]
        private class HouseData
        {
            public int index;
            public string kind, wall, door;
            public float x, y, z, yaw, w, d, h, roofH, plinthH, groundMin, groundMax;
            public Opening[] doors, windows;
        }

        [Serializable]
        private class Opening
        {
            public string wall;
            public float u, x, y, z, nx, nz, width, height;
        }

        private enum Col { None, Box, Mesh }

        private sealed class Room
        {
            public string Name;
            public float X0, X1, Z0, Z1, Ceil;
            public Material Wall, Floor;
            public Room(string name, float x0, float x1, float z0, float z1, float ceil, Material wall, Material floor)
            {
                Name = name; X0 = x0; X1 = x1; Z0 = z0; Z1 = z1; Ceil = ceil; Wall = wall; Floor = floor;
            }

            public Vector3 Centre(float y) => new Vector3((X0 + X1) * 0.5f, y, (Z0 + Z1) * 0.5f);
            public bool Holds(Vector3 p) => p.x >= X0 - 0.01f && p.x <= X1 + 0.01f && p.z >= Z0 - 0.01f && p.z <= Z1 + 0.01f;
        }

        /// <summary>A gap in a wall line. AlongX: the wall runs along x at z = Line (the gap spans x); else the reverse.</summary>
        private struct Doorway
        {
            public bool AlongX;
            public float Line, A0, A1, HalfDepth;
        }

        /// <summary>One house with rooms inside: its doors seen from inside, walkable floor zones and preview shots.</summary>
        private sealed class InteriorResult
        {
            public HouseInterior Interior;
            public string Reply = TealReply;
            public string LockId;   // set for a front door that starts locked
            public readonly Dictionary<int, HouseDoor> InsideDoors = new Dictionary<int, HouseDoor>();
            public readonly Dictionary<int, Vector3> InsideStand = new Dictionary<int, Vector3>();
            public readonly List<WalkableArea.Zone> Zones = new List<WalkableArea.Zone>();
            public readonly List<(HouseData house, float floorTop)> Floors = new List<(HouseData, float)>();
            public Transform Root;
            public readonly List<(string file, Vector3 eye, Vector3 look)> Shots = new List<(string, Vector3, Vector3)>();
        }

        public static void Build(WalkableArea area, float seaLevel)
        {
            LastShots.Clear();
            foreach (string old in new[] { DoorsRootName, InteriorRootName, BoathouseRootName, BoathouseOutsideName })
            {
                GameObject existing = GameObject.Find(old);
                if (existing != null) UnityEngine.Object.DestroyImmediate(existing);
            }

            if (!File.Exists(HousesJsonPath))
            {
                Debug.LogError($"[VillageHousesSetup] Missing {HousesJsonPath}. Run build_tunnel_exit.py --houses-only.");
                return;
            }

            HouseData[] houses = JsonUtility.FromJson<HouseFile>(File.ReadAllText(HousesJsonPath)).houses ?? Array.Empty<HouseData>();
            EnsureFolder(InteriorDir);
            Collider shell = GameObject.Find("Exit_Village")?.GetComponent<Collider>();

            HouseData main = houses.FirstOrDefault(h => h.index == TealHouse);
            HouseData wing = houses.FirstOrDefault(h => h.index == TealWing);
            InteriorResult teal = main != null && wing != null ? BuildTealHouse(main, wing, shell) : null;
            HouseData boathouseData = houses.FirstOrDefault(h => h.index == Boathouse);
            // The boathouse also adds its outdoor walkway to the area here, so its door counts as reachable below.
            InteriorResult boathouse = boathouseData != null ? BuildBoathouse(boathouseData, shell, area, seaLevel) : null;
            var interiors = new[] { teal, boathouse }.Where(r => r != null).ToList();

            // Doors first, while the walkable area is still only outdoors (the rooms are added to it afterwards).
            var doorsRoot = new GameObject(DoorsRootName);
            int knockable = 0, skipped = 0;
            foreach (HouseData h in houses)
            {
                foreach (Opening o in h.doors ?? Array.Empty<Opening>())
                {
                    var pos = new Vector3(o.x, o.y, o.z);
                    Vector3 n = new Vector3(o.nx, 0f, o.nz).normalized;
                    if (!Approachable(area, pos, n, out Vector3 stand, out float distance))
                    {
                        skipped++;
                        continue;
                    }

                    var go = new GameObject($"Door_{h.index}");
                    go.transform.SetParent(doorsRoot.transform, false);
                    go.transform.SetPositionAndRotation(pos + n * 0.03f, Quaternion.LookRotation(n, Vector3.up));
                    var trigger = go.AddComponent<BoxCollider>();
                    trigger.isTrigger = true;
                    trigger.center = new Vector3(0f, o.height * 0.5f, 0.03f);
                    trigger.size = new Vector3(o.width + 0.12f, o.height, 0.12f);

                    var door = go.AddComponent<HouseDoor>();
                    InteriorResult leadsIn = interiors.FirstOrDefault(r => r.InsideDoors.ContainsKey(h.index));
                    float reach = Mathf.Max(1.9f, distance + 0.6f);
                    if (leadsIn != null)
                    {
                        HouseDoor inside = leadsIn.InsideDoors[h.index];
                        door.Configure(reach, stand, leadsIn.Reply, leadsIn.Interior, inside, false);
                        if (!string.IsNullOrEmpty(leadsIn.LockId)) door.SetLock(leadsIn.LockId, true);
                        if (leadsIn == boathouse && _boathouseProbe != null) _boathouseProbe.FrontDoor = door;
                        inside.Configure(1.7f, leadsIn.InsideStand[h.index], "", leadsIn.Interior, door, true);
                        EditorUtility.SetDirty(inside);
                    }
                    else
                    {
                        door.Configure(reach, stand, ReplyFor(h, knockable), null, null, false);
                    }

                    EditorUtility.SetDirty(door);
                    knockable++;
                }
            }

            foreach (InteriorResult r in interiors)
            {
                foreach (KeyValuePair<int, HouseDoor> pair in r.InsideDoors)
                {
                    // A door whose outside can't be reached stays shut from the inside too.
                    if (pair.Value.OtherSide == null)
                    {
                        Debug.LogWarning($"[VillageHousesSetup] The {r.Interior.HouseName} door of building {pair.Key} can't be reached from outside.");
                        UnityEngine.Object.DestroyImmediate(pair.Value);
                    }
                }

                area.Configure(area.Corridors, area.Zones.Concat(r.Zones).ToArray());
                LastShots.AddRange(r.Shots.Select(s => (r.Root, s.file, s.eye, s.look)));
            }

            EditorUtility.SetDirty(area);
            if (teal != null) PressTerrainUnder(teal.Floors);

            Physics.SyncTransforms();
            Debug.Log($"[VillageHousesSetup] {knockable} knockable doors ({skipped} out of reach), teal house interior: {(teal != null ? "built" : "missing")}, boathouse: {(boathouse != null ? "built" : "missing")}");
        }

        private static string ReplyFor(HouseData h, int i)
        {
            if (h.w * h.d < 22f) return "It sounds hollow. Just a shed, nobody's inside.";
            return Replies[(h.index * 7 + i) % Replies.Length];
        }

        /// <summary>
        /// A door counts when walkable ground reaches within <see cref="MaxKnockDistance"/> of it, on its front side,
        /// with no other building in between. Also finds where people stand when they come out of it.
        /// </summary>
        private static bool Approachable(WalkableArea area, Vector3 door, Vector3 n, out Vector3 stand, out float distance)
        {
            stand = door;
            Vector3 near = area.Clamp(door + n * 0.4f);
            Vector3 flat = near - door;
            flat.y = 0f;
            distance = flat.magnitude;
            if (distance > MaxKnockDistance || Vector3.Dot(flat, n) < 0.1f) return false;

            Vector3 from = new Vector3(near.x, door.y + 1.0f, near.z);
            Vector3 to = door + n * 0.15f + Vector3.up * 1.0f;
            if (Physics.Linecast(from, to, out RaycastHit hit, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                && hit.collider is MeshCollider)
            {
                return false;
            }

            stand = area.Clamp(door + n * 0.9f);
            stand.y = GroundY(stand, door.y + 2.5f) ?? door.y;
            return true;
        }

        private static float? GroundY(Vector3 p, float fromY)
        {
            RaycastHit[] hits = Physics.RaycastAll(new Vector3(p.x, fromY, p.z), Vector3.down, 8f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float best = float.NegativeInfinity;
            foreach (RaycastHit hit in hits)
            {
                if (hit.collider.GetComponentInParent<CharacterMotor>() != null) continue;
                best = Mathf.Max(best, hit.point.y);
            }

            return float.IsNegativeInfinity(best) ? (float?)null : best + 0.02f;
        }

        // ------------------------------------------------------------------ the teal house

        private static InteriorResult BuildTealHouse(HouseData main, HouseData wing, Collider shell)
        {
            float floorWorld = main.y + main.plinthH + FloorLift;
            var root = new GameObject(InteriorRootName);
            root.transform.SetPositionAndRotation(new Vector3(main.x, floorWorld, main.z), Quaternion.Euler(0f, main.yaw * Mathf.Rad2Deg, 0f));
            var rooms = new GameObject("Rooms");
            rooms.transform.SetParent(root.transform, false);
            Transform rt = root.transform;
            var kit = new Kit(rooms.transform);

            // Main house in its own frame (x along the ridge, z across), floor at y = 0.
            float X0 = -main.w * 0.5f + Inset, X1 = main.w * 0.5f - Inset;
            float Z0 = -main.d * 0.5f + Inset, Z1 = main.d * 0.5f - Inset;
            float ceil = Mathf.Min(2.6f, main.y + main.h - floorWorld - 0.12f);

            // The entrance wing (building 38) is a lower annex on the west gable, its floor 0.6 m higher.
            Vector3 wMin = Vector3.positiveInfinity, wMax = Vector3.negativeInfinity;
            foreach (Vector3 c in Corners(wing))
            {
                Vector3 l = rt.InverseTransformPoint(c);
                wMin = Vector3.Min(wMin, l);
                wMax = Vector3.Max(wMax, l);
            }

            float WX0 = wMin.x + Inset, WX1 = wMax.x - Inset, WZ0 = wMin.z + Inset, WZ1 = wMax.z - Inset;
            float wingFloor = wing.y + wing.plinthH + FloorLift - floorWorld;
            float wingCeil = Mathf.Min(wingFloor + 2.3f, wing.y + wing.h - floorWorld - 0.1f);
            if (WX1 > X0 - 0.1f || wingFloor < 0f)
            {
                Debug.LogError("[VillageHousesSetup] The teal house wing isn't where the interior layout expects it (west gable, higher floor).");
                UnityEngine.Object.DestroyImmediate(root);
                return null;
            }

            // Partition lines between the rooms (chosen to miss every window: see the window check below).
            const float PxA = -1.63f, PxB = 1.63f, PzBath = -1.3f, PzKitchen = -0.05f;
            const float G = HalfPartition;
            const float PassZ0 = -1.15f, PassZ1 = -0.25f;   // way from the entrance hall into the main house

            Material plaster = kit.Mat("Plaster", Palette.InteriorPlaster, 0.12f);
            Material tile = kit.Mat("Tile", Palette.SeaFoam, 0.6f);
            Material kitchenWall = kit.Mat("KitchenWall", Palette.InteriorKitchenWall, 0.15f);
            Material livingWall = kit.Mat("LivingWall", Palette.InteriorLivingWall, 0.12f);
            Material bedroomWall = kit.Mat("BedroomWall", Palette.InteriorBedroomWall, 0.12f);
            Material pine = kit.Mat("PineFloor", Palette.InteriorPineFloor, 0.35f);
            Material bathFloor = kit.Mat("BathFloor", Palette.WetBasalt, 0.45f);

            var bath = new Room("Bathroom", X0, PxA - G, Z0, PzBath - G, ceil, tile, bathFloor);
            var hall = new Room("Hallway", X0, PxA - G, PzBath + G, Z1, ceil, plaster, pine);
            var kitchen = new Room("Kitchen", PxA + G, PxB - G, Z0, PzKitchen - G, ceil, kitchenWall, pine);
            var bedroom = new Room("Bedroom", PxA + G, PxB - G, PzKitchen + G, Z1, ceil, bedroomWall, pine);
            var living = new Room("LivingRoom", PxB + G, X1, Z0, Z1, ceil, livingWall, pine);
            var porch = new Room("EntranceHall", WX0, WX1, WZ0, WZ1, wingCeil, plaster, pine);
            Room[] all = { porch, hall, kitchen, living, bath, bedroom };

            var doorways = new List<Doorway>
            {
                new Doorway { AlongX = false, Line = PxA, A0 = -1.15f, A1 = -0.25f, HalfDepth = 2f * G },   // hall - kitchen
                new Doorway { AlongX = false, Line = PxA, A0 = 0.35f, A1 = 1.25f, HalfDepth = 2f * G },     // hall - bedroom
                new Doorway { AlongX = true, Line = PzBath, A0 = -2.75f, A1 = -1.85f, HalfDepth = 2f * G }, // hall - bathroom
                new Doorway { AlongX = false, Line = PxB, A0 = -2.3f, A1 = -1.3f, HalfDepth = 2f * G },     // kitchen - living room
                new Doorway { AlongX = false, Line = (WX1 + X0) * 0.5f, A0 = PassZ0, A1 = PassZ1,           // entrance hall - hallway
                              HalfDepth = (X0 - WX1) * 0.5f + Skin },
            };

            if (PassZ0 < Mathf.Max(hall.Z0, WZ0) + 0.1f || PassZ1 > WZ1 - 0.1f)
            {
                Debug.LogWarning("[VillageHousesSetup] The passage from the entrance hall doesn't fit both rooms.");
            }

            foreach (Room room in all) BuildRoom(kit, room, doorways);
            foreach (Doorway dw in doorways) BuildDoorFrame(kit, dw, kit.Mat("Trim", Palette.InteriorTrim, 0.3f), pine);

            // Entrance hall: a raised floor over the wing's higher plinth, and steps down to the main house.
            int risers = Mathf.Max(2, Mathf.CeilToInt(wingFloor / 0.22f));
            const float Tread = 0.28f;
            float stairX = WX1 - risers * Tread;
            kit.Box("RaisedFloor", new Vector3(WX0, 0f, WZ0), new Vector3(stairX, wingFloor, WZ1), pine, Col.Box);
            for (int k = 1; k < risers; k++)
            {
                float top = wingFloor * (risers - k) / risers;
                kit.Box($"Step{k}", new Vector3(stairX + (k - 1) * Tread, 0f, WZ0), new Vector3(stairX + k * Tread, top, WZ1), pine, Col.Box);
            }

            float FloorAt(Vector3 local) => porch.Holds(local) && local.x < stairX ? wingFloor : 0f;
            float CeilAt(Vector3 local) => porch.Holds(local) ? wingCeil : ceil;

            var result = new InteriorResult { Root = rt };
            BuildWindows(kit, rt, main, FloorAt, CeilAt);
            BuildWindows(kit, rt, wing, FloorAt, CeilAt);
            CheckWindowsMissPartitions(rt, main, new[] { PxA, PxB }, new[] { PzBath, PzKitchen }, PxB, ceil);

            // The two real front doors (the wing's, and the main house's back door), seen from inside.
            foreach (HouseData h in new[] { main, wing })
            {
                foreach (Opening o in h.doors ?? Array.Empty<Opening>())
                {
                    HouseDoor inside = BuildInsideDoor(kit, rt, o, FloorAt, out Vector3 standWorld);
                    result.InsideDoors[h.index] = inside;
                    result.InsideStand[h.index] = standWorld;
                }
            }

            BuildKitchen(kit, kitchen, ceil);
            BuildLivingRoom(kit, living, ceil);
            BuildBedroom(kit, bedroom);
            BuildBathroom(kit, bath);
            BuildHallway(kit, hall);
            BuildEntranceHall(kit, porch, wingFloor, stairX, rt, wing);

            foreach (Room room in all)
            {
                float floor = room == porch ? wingFloor : 0f;
                Vector3 lampAt = room == porch ? new Vector3((WX0 + stairX) * 0.5f, 0f, (WZ0 + WZ1) * 0.5f) : room.Centre(0f);
                if (room == kitchen) lampAt = new Vector3(-0.1f, 0f, -1.55f);   // over the table
                kit.CeilingLamp(room.Name, new Vector3(lampAt.x, room.Ceil, lampAt.z), room.Ceil - floor,
                    Mathf.Max(room.X1 - room.X0, room.Z1 - room.Z0) + 1.5f);
            }

            // Walkable zones (world space): the main floor, the entrance hall and the passage between them.
            Vector3[] Rect(float x0, float x1, float z0, float z1) => new[]
            {
                rt.TransformPoint(new Vector3(x0, 0f, z0)), rt.TransformPoint(new Vector3(x1, 0f, z0)),
                rt.TransformPoint(new Vector3(x1, 0f, z1)), rt.TransformPoint(new Vector3(x0, 0f, z1)),
            };
            result.Zones.Add(new WalkableArea.Zone { name = "TealHouse_Main", outline = Rect(X0, X1, Z0, Z1) });
            result.Zones.Add(new WalkableArea.Zone { name = "TealHouse_EntranceHall", outline = Rect(WX0, WX1, WZ0, WZ1) });
            result.Zones.Add(new WalkableArea.Zone { name = "TealHouse_Passage", outline = Rect(WX1 - 0.1f, X0 + 0.1f, PassZ0, PassZ1) });
            result.Floors.Add((main, floorWorld));
            result.Floors.Add((wing, floorWorld + wingFloor));

            var interior = root.AddComponent<HouseInterior>();
            interior.Configure("teal house", rooms, shell != null ? new[] { shell } : new Collider[0]);
            EditorUtility.SetDirty(interior);
            result.Interior = interior;

            const float Eye = 1.6f;
            result.Shots.Add(("interior_hallway.png", new Vector3(-4.2f, Eye, 2.7f), new Vector3(-2.2f, 1.1f, -1.0f)));
            result.Shots.Add(("interior_kitchen.png", new Vector3(1.3f, Eye, -0.4f), new Vector3(-0.6f, 0.9f, -3.0f)));
            result.Shots.Add(("interior_livingroom.png", new Vector3(2.0f, Eye, 2.8f), new Vector3(4.3f, 0.8f, -1.2f)));
            result.Shots.Add(("interior_bedroom.png", new Vector3(-1.3f, Eye, 0.3f), new Vector3(0.6f, 0.6f, 2.6f)));
            result.Shots.Add(("interior_bathroom.png", new Vector3(-2.2f, Eye, -1.55f), new Vector3(-4.2f, 0.6f, -2.9f)));
            result.Shots.Add(("interior_entrancehall.png", new Vector3(WX1 - 0.3f, Eye + 0.2f, PassZ1), new Vector3(WX0 + 0.5f, wingFloor + 0.8f, WZ0 + 0.6f)));
            return result;
        }

        /// <summary>Preview shots from the last build, with eye and target in the frame of <c>root</c>.</summary>
        private static readonly List<(Transform root, string file, Vector3 eye, Vector3 look)> LastShots = new List<(Transform, string, Vector3, Vector3)>();

        private static IEnumerable<Vector3> Corners(HouseData h)
        {
            Quaternion r = Quaternion.Euler(0f, h.yaw * Mathf.Rad2Deg, 0f);
            var c = new Vector3(h.x, h.y, h.z);
            for (int sx = -1; sx <= 1; sx += 2)
            for (int sz = -1; sz <= 1; sz += 2)
                yield return c + r * new Vector3(sx * h.w * 0.5f, 0f, sz * h.d * 0.5f);
        }

        /// <summary>Floor, ceiling and four wall linings for one room, with the doorways cut out of the linings.</summary>
        private static void BuildRoom(Kit kit, Room r, List<Doorway> doorways)
        {
            var parent = new GameObject(r.Name).transform;
            parent.SetParent(kit.Root, false);
            Material ceilingMat = kit.Mat("Ceiling", Palette.InteriorTrim, 0.08f);

            kit.Box("Floor", new Vector3(r.X0, -0.12f, r.Z0), new Vector3(r.X1, 0f, r.Z1), r.Floor, Col.Box, parent);
            kit.Box("Ceiling", new Vector3(r.X0, r.Ceil, r.Z0), new Vector3(r.X1, r.Ceil + 0.08f, r.Z1), ceilingMat, Col.Mesh, parent);

            const float Bottom = -0.05f;
            // Linings that run along x (south and north), then along z (west and east) between them.
            foreach ((float line, float inner) in new[] { (r.Z0, r.Z0 + Skin), (r.Z1 - Skin, r.Z1) })
            {
                float edge = line == r.Z0 ? r.Z0 : r.Z1;
                var cuts = doorways.Where(d => d.AlongX && Mathf.Abs(d.Line - edge) <= d.HalfDepth + 0.01f).ToList();
                foreach ((float a0, float a1, bool lintel) in Segments(r.X0, r.X1, cuts))
                {
                    float y0 = lintel ? DoorTop : Bottom;
                    if (y0 >= r.Ceil) continue;
                    kit.Box(lintel ? "Lintel" : "Wall", new Vector3(a0, y0, line), new Vector3(a1, r.Ceil, inner), r.Wall, Col.Mesh, parent);
                }
            }

            foreach ((float line, float inner) in new[] { (r.X0, r.X0 + Skin), (r.X1 - Skin, r.X1) })
            {
                float edge = line == r.X0 ? r.X0 : r.X1;
                var cuts = doorways.Where(d => !d.AlongX && Mathf.Abs(d.Line - edge) <= d.HalfDepth + 0.01f).ToList();
                foreach ((float a0, float a1, bool lintel) in Segments(r.Z0 + Skin, r.Z1 - Skin, cuts))
                {
                    float y0 = lintel ? DoorTop : Bottom;
                    if (y0 >= r.Ceil) continue;
                    kit.Box(lintel ? "Lintel" : "Wall", new Vector3(line, y0, a0), new Vector3(inner, r.Ceil, a1), r.Wall, Col.Mesh, parent);
                }
            }
        }

        /// <summary>Splits [r0, r1] into full-height pieces between the doorway gaps, plus a lintel piece over each gap.</summary>
        private static IEnumerable<(float a0, float a1, bool lintel)> Segments(float r0, float r1, List<Doorway> cuts)
        {
            float at = r0;
            foreach (Doorway c in cuts.OrderBy(c => c.A0))
            {
                float c0 = Mathf.Max(c.A0, r0), c1 = Mathf.Min(c.A1, r1);
                if (c1 <= c0) continue;
                if (c0 > at + 0.005f) yield return (at, c0, false);
                yield return (c0, c1, true);
                at = Mathf.Max(at, c1);
            }

            if (r1 > at + 0.005f) yield return (at, r1, false);
        }

        /// <summary>Casing round a doorway, covering the hollow between the two rooms' linings, plus a threshold.</summary>
        private static void BuildDoorFrame(Kit kit, Doorway d, Material trim, Material floor)
        {
            var parent = new GameObject("Doorway").transform;
            parent.SetParent(kit.Root, false);
            float d0 = d.Line - d.HalfDepth - 0.02f, d1 = d.Line + d.HalfDepth + 0.02f;
            Vector3 P(float along, float y, float depth) => d.AlongX ? new Vector3(along, y, depth) : new Vector3(depth, y, along);
            void Piece(string name, float a0, float a1, float y0, float y1, float e0, float e1, Material m, Col col)
            {
                Vector3 p = P(a0, y0, e0), q = P(a1, y1, e1);
                kit.Box(name, Vector3.Min(p, q), Vector3.Max(p, q), m, col, parent);
            }

            Piece("JambA", d.A0 - 0.07f, d.A0, 0f, DoorTop, d0, d1, trim, Col.Mesh);
            Piece("JambB", d.A1, d.A1 + 0.07f, 0f, DoorTop, d0, d1, trim, Col.Mesh);
            Piece("Head", d.A0 - 0.07f, d.A1 + 0.07f, DoorTop, DoorTop + 0.07f, d0, d1, trim, Col.Mesh);
            Piece("Threshold", d.A0, d.A1, -0.12f, 0f, d0 + 0.02f, d1 - 0.02f, floor, Col.Box);
        }

        /// <summary>Each real window of the house, seen from inside: a dusky pane in a white frame on the room lining.</summary>
        private static void BuildWindows(Kit kit, Transform rt, HouseData h, Func<Vector3, float> floorAt, Func<Vector3, float> ceilAt)
        {
            Material trim = kit.Mat("Trim", Palette.InteriorTrim, 0.3f);
            Material glass = kit.Mat("WindowDusk", Palette.SkyZenithMist * 0.55f, 0.9f, Palette.SkyZenithMist * 0.35f);
            foreach (Opening o in h.windows ?? Array.Empty<Opening>())
            {
                Vector3 n = rt.InverseTransformDirection(new Vector3(o.nx, 0f, o.nz)).normalized;
                Vector3 p = rt.InverseTransformPoint(new Vector3(o.x, o.y, o.z)) - n * (Inset + Skin);
                if (p.y < floorAt(p) + 0.25f || p.y + o.height > ceilAt(p) - 0.04f) continue;   // attic and basement windows

                var win = new GameObject("Window").transform;
                win.SetParent(kit.Root, false);
                win.localPosition = p;
                win.localRotation = Quaternion.LookRotation(-n, Vector3.up);   // +z points into the room
                float w = o.width, ht = o.height;
                kit.Box("JambL", new Vector3(-w * 0.5f - 0.06f, 0f, 0f), new Vector3(-w * 0.5f, ht, 0.04f), trim, Col.None, win);
                kit.Box("JambR", new Vector3(w * 0.5f, 0f, 0f), new Vector3(w * 0.5f + 0.06f, ht, 0.04f), trim, Col.None, win);
                kit.Box("Head", new Vector3(-w * 0.5f - 0.06f, ht, 0f), new Vector3(w * 0.5f + 0.06f, ht + 0.07f, 0.04f), trim, Col.None, win);
                kit.Box("Sill", new Vector3(-w * 0.5f - 0.08f, -0.05f, 0f), new Vector3(w * 0.5f + 0.08f, 0f, 0.15f), trim, Col.None, win);
                kit.Box("Mullion", new Vector3(-0.02f, 0f, 0f), new Vector3(0.02f, ht, 0.025f), trim, Col.None, win);
                kit.Box("Transom", new Vector3(-w * 0.5f, ht * 0.52f, 0f), new Vector3(w * 0.5f, ht * 0.52f + 0.035f, 0.025f), trim, Col.None, win);
                var pane = GameObject.CreatePrimitive(PrimitiveType.Quad);
                UnityEngine.Object.DestroyImmediate(pane.GetComponent<Collider>());
                pane.name = "Glass";
                pane.transform.SetParent(win, false);
                pane.transform.localPosition = new Vector3(0f, ht * 0.5f, 0.004f);
                pane.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);   // a quad shows its -z side: face the room
                pane.transform.localScale = new Vector3(w, ht, 1f);
                Kit.Finish(pane, glass, false);
            }
        }

        /// <summary>Warns when a window lands on an inner wall. The across-walls (zLines) only reach as far east as zLinesMaxX.</summary>
        private static void CheckWindowsMissPartitions(Transform rt, HouseData h, float[] xLines, float[] zLines, float zLinesMaxX, float ceil)
        {
            foreach (Opening o in h.windows ?? Array.Empty<Opening>())
            {
                Vector3 p = rt.InverseTransformPoint(new Vector3(o.x, o.y, o.z));
                Vector3 n = rt.InverseTransformDirection(new Vector3(o.nx, 0f, o.nz));
                bool alongX = Mathf.Abs(n.z) > 0.7f;
                if (p.y > ceil || (!alongX && p.x > zLinesMaxX)) continue;   // attic windows, or the living room gable
                float c = alongX ? p.x : p.z;
                foreach (float line in alongX ? xLines : zLines)
                {
                    if (Mathf.Abs(c - line) < o.width * 0.5f + 0.15f)
                        Debug.LogWarning($"[VillageHousesSetup] A {h.index} window at {c:F2} runs into the inner wall at {line:F2}.");
                }
            }
        }

        private static HouseDoor BuildInsideDoor(Kit kit, Transform rt, Opening o, Func<Vector3, float> floorAt, out Vector3 standWorld)
        {
            Vector3 n = rt.InverseTransformDirection(new Vector3(o.nx, 0f, o.nz)).normalized;
            Vector3 p = rt.InverseTransformPoint(new Vector3(o.x, o.y, o.z)) - n * (Inset + Skin);
            p.y = floorAt(p);
            Material leaf = kit.Mat("DoorLeaf", Palette.InteriorBirch, 0.3f);
            Material trim = kit.Mat("Trim", Palette.InteriorTrim, 0.3f);
            Material steel = kit.Mat("Steel", Palette.InteriorSteel, 0.7f, null, 0.8f);

            var go = new GameObject("FrontDoor_Inside");
            go.transform.SetParent(kit.Root, false);
            go.transform.localPosition = p;
            go.transform.localRotation = Quaternion.LookRotation(-n, Vector3.up);   // forward: into the room
            Transform t = go.transform;
            float w = o.width, h = Mathf.Min(o.height, 1.95f);
            kit.Box("Leaf", new Vector3(-w * 0.5f, 0f, 0f), new Vector3(w * 0.5f, h, 0.045f), leaf, Col.None, t);
            kit.Box("Panel", new Vector3(-w * 0.5f + 0.12f, 0.25f, 0.045f), new Vector3(w * 0.5f - 0.12f, h - 0.3f, 0.06f), leaf, Col.None, t);
            kit.Box("JambL", new Vector3(-w * 0.5f - 0.07f, 0f, 0f), new Vector3(-w * 0.5f, h, 0.035f), trim, Col.None, t);
            kit.Box("JambR", new Vector3(w * 0.5f, 0f, 0f), new Vector3(w * 0.5f + 0.07f, h, 0.035f), trim, Col.None, t);
            kit.Box("Head", new Vector3(-w * 0.5f - 0.07f, h, 0f), new Vector3(w * 0.5f + 0.07f, h + 0.07f, 0.035f), trim, Col.None, t);
            kit.Box("Handle", new Vector3(w * 0.5f - 0.2f, 0.95f, 0.06f), new Vector3(w * 0.5f - 0.06f, 0.975f, 0.09f), steel, Col.None, t);

            var trigger = go.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.center = new Vector3(0f, h * 0.5f, 0.06f);
            trigger.size = new Vector3(w + 0.1f, h, 0.12f);
            standWorld = rt.TransformPoint(p - n * 0.8f) + Vector3.up * 0.02f;
            return go.AddComponent<HouseDoor>();
        }

        // ------------------------------------------------------------------ furniture, room by room (house frame, y = floor)

        private static void BuildKitchen(Kit k, Room r, float ceil)
        {
            Transform p = k.Group("Kitchen_Furniture");
            float x0 = r.X0 + Skin, z0 = r.Z0 + Skin;
            Material birch = k.Mat("Birch", Palette.InteriorBirch, 0.3f), oak = k.Mat("Oak", Palette.InteriorOak, 0.35f);
            Material steel = k.Mat("Steel", Palette.InteriorSteel, 0.7f, null, 0.8f), black = k.Mat("Black", Palette.DeepFjordSlate * 0.6f, 0.5f);
            Material ceramic = k.Mat("Ceramic", Palette.InteriorCeramic, 0.85f), mirror = k.Mat("Mirror", Palette.SkyHorizonMist, 0.95f, null, 0.85f);

            // Worktop run along the window wall: sink under one window, cooker between the windows.
            k.Box("Counter", new Vector3(x0, 0f, z0), new Vector3(0.95f, 0.86f, z0 + 0.6f), birch, Col.Box, p);
            k.Box("Worktop", new Vector3(x0, 0.86f, z0), new Vector3(0.97f, 0.9f, z0 + 0.63f), oak, Col.None, p);
            foreach (float hx in new[] { -1.3f, -0.7f, 0.55f })
                k.Box("Handle", new Vector3(hx - 0.07f, 0.76f, z0 + 0.6f), new Vector3(hx + 0.07f, 0.78f, z0 + 0.625f), steel, Col.None, p);
            k.Box("Sink", new Vector3(-1.15f, 0.875f, z0 + 0.12f), new Vector3(-0.5f, 0.905f, z0 + 0.5f), steel, Col.None, p);
            k.Box("SinkBowl", new Vector3(-1.1f, 0.88f, z0 + 0.16f), new Vector3(-0.55f, 0.907f, z0 + 0.46f), black, Col.None, p);
            k.Cyl("Tap", new Vector3(-0.82f, 0.99f, z0 + 0.07f), 0.015f, 0.18f, steel, Col.None, p);
            k.Box("Spout", new Vector3(-0.835f, 1.06f, z0 + 0.07f), new Vector3(-0.805f, 1.08f, z0 + 0.22f), steel, Col.None, p);
            k.Box("OvenFront", new Vector3(-0.28f, 0.1f, z0 + 0.6f), new Vector3(0.28f, 0.75f, z0 + 0.615f), black, Col.None, p);
            k.Box("OvenWindow", new Vector3(-0.2f, 0.3f, z0 + 0.615f), new Vector3(0.2f, 0.58f, z0 + 0.62f), mirror, Col.None, p);
            k.Box("OvenHandle", new Vector3(-0.22f, 0.66f, z0 + 0.62f), new Vector3(0.22f, 0.68f, z0 + 0.65f), steel, Col.None, p);
            k.Box("Hob", new Vector3(-0.3f, 0.9f, z0 + 0.05f), new Vector3(0.3f, 0.91f, z0 + 0.55f), black, Col.None, p);
            foreach (float bx in new[] { -0.13f, 0.13f })
            foreach (float bz in new[] { 0.17f, 0.42f })
                k.Cyl("Burner", new Vector3(bx, 0.912f, z0 + bz), 0.075f, 0.006f, steel, Col.None, p);
            k.Box("Hood", new Vector3(-0.32f, 1.55f, z0), new Vector3(0.32f, 1.72f, z0 + 0.48f), steel, Col.None, p);
            k.Box("HoodFlue", new Vector3(-0.12f, 1.72f, z0), new Vector3(0.12f, ceil, z0 + 0.25f), steel, Col.None, p);
            k.Cyl("Kettle", new Vector3(-1.35f, 0.99f, z0 + 0.3f), 0.08f, 0.18f, steel, Col.None, p);

            // Fridge against the hallway wall, opening into the room.
            k.Box("Fridge", new Vector3(x0, 0f, -2.45f), new Vector3(x0 + 0.65f, 1.85f, -1.8f), ceramic, Col.Box, p);
            k.Box("FridgeSplit", new Vector3(x0 + 0.65f, 1.2f, -2.45f), new Vector3(x0 + 0.655f, 1.21f, -1.8f), steel, Col.None, p);
            k.Box("FridgeHandle", new Vector3(x0 + 0.65f, 0.9f, -1.95f), new Vector3(x0 + 0.68f, 1.5f, -1.92f), steel, Col.None, p);

            // Table with three chairs.
            k.Box("TableTop", new Vector3(-0.6f, 0.72f, -1.925f), new Vector3(0.4f, 0.76f, -1.175f), oak, Col.Box, p);
            foreach (float lx in new[] { -0.56f, 0.36f })
            foreach (float lz in new[] { -1.885f, -1.215f })
                k.Box("TableLeg", new Vector3(lx - 0.025f, 0f, lz - 0.025f), new Vector3(lx + 0.025f, 0.72f, lz + 0.025f), oak, Col.None, p);
            k.Chair(new Vector3(-0.85f, 0f, -1.55f), 90f, birch, p);
            k.Chair(new Vector3(0.65f, 0f, -1.55f), -90f, birch, p);
            k.Chair(new Vector3(-0.1f, 0f, -0.95f), 180f, birch, p);
            k.Cyl("FruitBowl", new Vector3(-0.1f, 0.78f, -1.55f), 0.13f, 0.04f, ceramic, Col.None, p);
        }

        private static void BuildLivingRoom(Kit k, Room r, float ceil)
        {
            Transform p = k.Group("LivingRoom_Furniture");
            float x0 = r.X0 + Skin, x1 = r.X1 - Skin, z0 = r.Z0 + Skin, z1 = r.Z1 - Skin;
            Material wool = k.Mat("Wool", Palette.InteriorWool, 0.1f);
            Material cushion = k.Mat("WoolCushion", Color.Lerp(Palette.InteriorWool, Palette.InteriorLinen, 0.18f), 0.1f);
            Material oak = k.Mat("Oak", Palette.InteriorOak, 0.35f), rug = k.Mat("RugRed", Palette.BoathouseRust, 0.05f);
            Material moss = k.Mat("Moss", Palette.TurfMoss, 0.1f), black = k.Mat("Black", Palette.DeepFjordSlate * 0.6f, 0.5f);
            Material basalt = k.Mat("Hearth", Palette.WetBasalt, 0.4f), linen = k.Mat("Linen", Palette.InteriorLinen, 0.1f);
            Material fire = k.Mat("StoveFire", Palette.LanternAmber, 0.2f, Palette.LanternAmber * 2.2f);
            Material glow = k.Mat("LampShade", Palette.InteriorLampGlow, 0.2f, Palette.InteriorLampGlow * 1.4f);
            Material sky = k.Mat("PaintingSky", Palette.SkyHorizonMist, 0.2f), sea = k.Mat("PaintingSea", Palette.SkyZenithMist, 0.2f);

            // Sofa against the kitchen wall, facing the TV on the gable wall between its two windows.
            k.Box("SofaBase", new Vector3(x0 + 0.05f, 0f, -1.1f), new Vector3(x0 + 0.9f, 0.42f, 1.1f), wool, Col.Box, p);
            k.Box("SofaBack", new Vector3(x0 + 0.05f, 0.42f, -1.1f), new Vector3(x0 + 0.27f, 0.9f, 1.1f), wool, Col.None, p);
            k.Box("SofaArmS", new Vector3(x0 + 0.05f, 0.42f, -1.1f), new Vector3(x0 + 0.9f, 0.62f, -0.92f), wool, Col.None, p);
            k.Box("SofaArmN", new Vector3(x0 + 0.05f, 0.42f, 0.92f), new Vector3(x0 + 0.9f, 0.62f, 1.1f), wool, Col.None, p);
            k.Box("Cushion", new Vector3(x0 + 0.27f, 0.42f, -0.9f), new Vector3(x0 + 0.88f, 0.52f, -0.01f), cushion, Col.None, p);
            k.Box("Cushion", new Vector3(x0 + 0.27f, 0.42f, 0.01f), new Vector3(x0 + 0.88f, 0.52f, 0.9f), cushion, Col.None, p);
            k.Box("Pillow", new Vector3(x0 + 0.28f, 0.52f, 0.55f), new Vector3(x0 + 0.42f, 0.82f, 0.88f), rug, Col.None, p);
            k.Box("Rug", new Vector3(2.75f, 0f, -1.25f), new Vector3(4.0f, 0.012f, 1.25f), rug, Col.None, p);
            k.Box("CoffeeTable", new Vector3(3.0f, 0.38f, -0.5f), new Vector3(3.6f, 0.42f, 0.5f), oak, Col.Box, p);
            foreach (float lx in new[] { 3.04f, 3.56f })
            foreach (float lz in new[] { -0.46f, 0.46f })
                k.Box("TableLeg", new Vector3(lx - 0.025f, 0f, lz - 0.025f), new Vector3(lx + 0.025f, 0.38f, lz + 0.025f), oak, Col.None, p);
            k.Box("TvBench", new Vector3(x1 - 0.4f, 0f, -0.65f), new Vector3(x1, 0.5f, 0.65f), oak, Col.Box, p);
            k.Box("TvFoot", new Vector3(x1 - 0.25f, 0.5f, -0.1f), new Vector3(x1 - 0.1f, 0.62f, 0.1f), black, Col.None, p);
            k.Box("Tv", new Vector3(x1 - 0.17f, 0.62f, -0.58f), new Vector3(x1 - 0.12f, 1.27f, 0.58f), black, Col.None, p);

            // Bookcase on the front wall, past the window.
            const float bx0 = 3.1f, bx1 = 4.4f, depth = 0.32f, top = 1.9f;
            k.Box("ShelfSideL", new Vector3(bx0, 0f, z0), new Vector3(bx0 + 0.04f, top, z0 + depth), oak, Col.None, p);
            k.Box("ShelfSideR", new Vector3(bx1 - 0.04f, 0f, z0), new Vector3(bx1, top, z0 + depth), oak, Col.None, p);
            k.Box("ShelfBack", new Vector3(bx0, 0f, z0), new Vector3(bx1, top, z0 + 0.02f), oak, Col.None, p);
            k.Blocker(new Vector3(bx0, 0f, z0), new Vector3(bx1, top, z0 + depth), p);
            Material[] spines = { rug, wool, moss, linen, k.Mat("Birch", Palette.InteriorBirch, 0.3f), black };
            var rng = new System.Random(21);
            foreach (float sy in new[] { 0f, 0.45f, 0.9f, 1.35f, top - 0.03f })
            {
                k.Box("Shelf", new Vector3(bx0 + 0.04f, sy, z0), new Vector3(bx1 - 0.04f, sy + 0.03f, z0 + depth), oak, Col.None, p);
                if (sy > 1.8f) continue;
                for (float bx = bx0 + 0.07f; bx < bx1 - 0.12f;)
                {
                    float bw = 0.03f + (float)rng.NextDouble() * 0.04f, bh = 0.24f + (float)rng.NextDouble() * 0.12f;
                    if (rng.NextDouble() < 0.12) { bx += 0.1f; continue; }
                    k.Box("Book", new Vector3(bx, sy + 0.03f, z0 + 0.05f), new Vector3(bx + bw, sy + 0.03f + bh, z0 + depth - 0.03f), spines[rng.Next(spines.Length)], Col.None, p);
                    bx += bw + 0.004f;
                }
            }

            // Wood-burning stove in the corner, its flue up through the ceiling.
            k.Box("Hearth", new Vector3(3.9f, 0f, 2.35f), new Vector3(x1, 0.015f, z1), basalt, Col.None, p);
            k.Box("Stove", new Vector3(4.05f, 0f, 2.6f), new Vector3(4.5f, 0.75f, 3.0f), black, Col.Box, p);
            k.Box("StoveTop", new Vector3(4.03f, 0.75f, 2.58f), new Vector3(4.52f, 0.78f, 3.02f), black, Col.None, p);
            k.Box("StoveFire", new Vector3(4.12f, 0.2f, 2.595f), new Vector3(4.42f, 0.55f, 2.6f), fire, Col.None, p);
            k.Cyl("StoveFlue", new Vector3(4.27f, (0.78f + ceil) * 0.5f, 2.8f), 0.07f, ceil - 0.78f, black, Col.None, p);

            // Armchair by the stove, floor lamp by the sofa, a fjord painting over the sofa.
            k.Armchair(new Vector3(3.3f, 0f, 2.55f), 180f, moss, p);
            k.Cyl("LampBase", new Vector3(2.2f, 0.015f, 1.45f), 0.14f, 0.03f, black, Col.None, p);
            k.Cyl("LampPole", new Vector3(2.2f, 0.8f, 1.45f), 0.015f, 1.55f, black, Col.None, p);
            k.Cyl("LampShade", new Vector3(2.2f, 1.6f, 1.45f), 0.2f, 0.28f, glow, Col.None, p);
            k.Blocker(new Vector3(2.06f, 0f, 1.31f), new Vector3(2.34f, 1.7f, 1.59f), p);
            k.Box("PaintingFrame", new Vector3(x0, 1.25f, -0.65f), new Vector3(x0 + 0.03f, 1.85f, 0.65f), oak, Col.None, p);
            k.Box("PaintingSea", new Vector3(x0 + 0.03f, 1.3f, -0.6f), new Vector3(x0 + 0.035f, 1.45f, 0.6f), sea, Col.None, p);
            k.Box("PaintingHills", new Vector3(x0 + 0.03f, 1.45f, -0.6f), new Vector3(x0 + 0.035f, 1.6f, 0.6f), moss, Col.None, p);
            k.Box("PaintingSky", new Vector3(x0 + 0.03f, 1.6f, -0.6f), new Vector3(x0 + 0.035f, 1.8f, 0.6f), sky, Col.None, p);
        }

        private static void BuildBedroom(Kit k, Room r)
        {
            Transform p = k.Group("Bedroom_Furniture");
            float z0 = r.Z0 + Skin, z1 = r.Z1 - Skin;
            Material oak = k.Mat("Oak", Palette.InteriorOak, 0.35f), birch = k.Mat("Birch", Palette.InteriorBirch, 0.3f);
            Material linen = k.Mat("Linen", Palette.InteriorLinen, 0.1f), moss = k.Mat("Moss", Palette.TurfMoss, 0.1f);
            Material wool = k.Mat("Wool", Palette.InteriorWool, 0.1f), ceramic = k.Mat("Ceramic", Palette.InteriorCeramic, 0.85f);
            Material steel = k.Mat("Steel", Palette.InteriorSteel, 0.7f, null, 0.8f);
            Material glow = k.Mat("LampShade", Palette.InteriorLampGlow, 0.2f, Palette.InteriorLampGlow * 1.4f);

            // Double bed, headboard under the two back windows.
            k.Box("BedFrame", new Vector3(-0.8f, 0f, 1.0f), new Vector3(0.8f, 0.35f, z1), oak, Col.Box, p);
            k.Box("Mattress", new Vector3(-0.76f, 0.35f, 1.04f), new Vector3(0.76f, 0.55f, z1 - 0.04f), linen, Col.None, p);
            k.Box("Duvet", new Vector3(-0.8f, 0.5f, 1.0f), new Vector3(0.8f, 0.6f, 2.5f), moss, Col.None, p);
            k.Box("Pillow", new Vector3(-0.66f, 0.55f, 2.55f), new Vector3(-0.08f, 0.68f, 2.95f), linen, Col.None, p);
            k.Box("Pillow", new Vector3(0.08f, 0.55f, 2.55f), new Vector3(0.66f, 0.68f, 2.95f), linen, Col.None, p);
            k.Box("Headboard", new Vector3(-0.85f, 0f, z1 - 0.08f), new Vector3(0.85f, 1.05f, z1), oak, Col.None, p);
            foreach (float s in new[] { -1f, 1f })
            {
                k.Box("Nightstand", new Vector3(s > 0 ? 0.9f : -1.3f, 0f, 2.62f), new Vector3(s > 0 ? 1.3f : -0.9f, 0.5f, 3.02f), birch, Col.Box, p);
                k.Cyl("LampFoot", new Vector3(s * 1.1f, 0.56f, 2.82f), 0.05f, 0.12f, ceramic, Col.None, p);
                k.Cyl("LampShade", new Vector3(s * 1.1f, 0.72f, 2.82f), 0.1f, 0.16f, glow, Col.None, p);
            }

            k.Box("Wardrobe", new Vector3(0.3f, 0f, z0), new Vector3(1.5f, 2.05f, z0 + 0.6f), birch, Col.Box, p);
            k.Box("WardrobeSplit", new Vector3(0.895f, 0.05f, z0 + 0.6f), new Vector3(0.905f, 2.0f, z0 + 0.605f), oak, Col.None, p);
            k.Box("Handle", new Vector3(0.82f, 1.0f, z0 + 0.6f), new Vector3(0.84f, 1.2f, z0 + 0.63f), steel, Col.None, p);
            k.Box("Handle", new Vector3(0.96f, 1.0f, z0 + 0.6f), new Vector3(0.98f, 1.2f, z0 + 0.63f), steel, Col.None, p);
            k.Box("Rug", new Vector3(-1.45f, 0f, 1.2f), new Vector3(-0.85f, 0.012f, 2.5f), wool, Col.None, p);
        }

        private static void BuildBathroom(Kit k, Room r)
        {
            Transform p = k.Group("Bathroom_Furniture");
            float x0 = r.X0 + Skin, x1 = r.X1 - Skin, z0 = r.Z0 + Skin, z1 = r.Z1 - Skin;
            Material ceramic = k.Mat("Ceramic", Palette.InteriorCeramic, 0.85f), birch = k.Mat("Birch", Palette.InteriorBirch, 0.3f);
            Material steel = k.Mat("Steel", Palette.InteriorSteel, 0.7f, null, 0.8f), linen = k.Mat("Linen", Palette.InteriorLinen, 0.1f);
            Material water = k.Mat("Water", Palette.SkyZenithMist, 0.95f), mirror = k.Mat("Mirror", Palette.SkyHorizonMist, 0.95f, null, 0.85f);
            Material towel = k.Mat("Moss", Palette.TurfMoss, 0.1f);

            // Bathtub with a shower along the end wall, half-drawn curtain.
            k.Box("Bathtub", new Vector3(x0, 0f, z0), new Vector3(x0 + 0.75f, 0.55f, z1 - 0.05f), ceramic, Col.Box, p);
            k.Box("TubInside", new Vector3(x0 + 0.07f, 0.45f, z0 + 0.07f), new Vector3(x0 + 0.68f, 0.552f, z1 - 0.12f), water, Col.None, p);
            k.Cyl("ShowerPipe", new Vector3(x0 + 0.04f, 1.45f, z0 + 0.25f), 0.012f, 1.0f, steel, Col.None, p);
            k.Cyl("ShowerHead", new Vector3(x0 + 0.1f, 1.95f, z0 + 0.25f), 0.06f, 0.03f, steel, Col.None, p);
            k.Box("CurtainRail", new Vector3(x0 + 0.74f, 1.95f, z0), new Vector3(x0 + 0.76f, 1.97f, z1 - 0.05f), steel, Col.None, p);
            k.Box("Curtain", new Vector3(x0 + 0.74f, 0.6f, z0), new Vector3(x0 + 0.755f, 1.95f, z0 + 0.55f), linen, Col.None, p);

            // Toilet against the window wall.
            k.Box("ToiletTank", new Vector3(-3.42f, 0.4f, z0), new Vector3(-2.98f, 0.82f, z0 + 0.2f), ceramic, Col.None, p);
            k.Box("ToiletBowl", new Vector3(-3.38f, 0f, z0 + 0.2f), new Vector3(-3.02f, 0.4f, z0 + 0.62f), ceramic, Col.Box, p);
            k.Box("ToiletSeat", new Vector3(-3.4f, 0.4f, z0 + 0.18f), new Vector3(-3.0f, 0.44f, z0 + 0.64f), ceramic, Col.None, p);
            k.Box("FlushButton", new Vector3(-3.24f, 0.82f, z0 + 0.06f), new Vector3(-3.16f, 0.83f, z0 + 0.14f), steel, Col.None, p);

            // Washbasin on a cabinet, mirror above.
            k.Box("Vanity", new Vector3(x1 - 0.5f, 0f, -2.85f), new Vector3(x1, 0.8f, -2.15f), birch, Col.Box, p);
            k.Box("Basin", new Vector3(x1 - 0.47f, 0.8f, -2.8f), new Vector3(x1 - 0.03f, 0.86f, -2.2f), ceramic, Col.None, p);
            k.Box("BasinBowl", new Vector3(x1 - 0.4f, 0.855f, -2.7f), new Vector3(x1 - 0.12f, 0.862f, -2.3f), water, Col.None, p);
            k.Cyl("Tap", new Vector3(x1 - 0.06f, 0.92f, -2.5f), 0.012f, 0.12f, steel, Col.None, p);
            k.Box("Mirror", new Vector3(x1 - 0.02f, 1.15f, -2.75f), new Vector3(x1, 1.8f, -2.25f), mirror, Col.None, p);

            k.Box("TowelRail", new Vector3(-3.7f, 1.1f, z1 - 0.06f), new Vector3(-3.1f, 1.13f, z1 - 0.03f), steel, Col.None, p);
            k.Box("Towel", new Vector3(-3.65f, 0.6f, z1 - 0.07f), new Vector3(-3.15f, 1.12f, z1 - 0.04f), towel, Col.None, p);
            k.Box("BathMat", new Vector3(x0 + 0.8f, 0f, -2.4f), new Vector3(x0 + 1.28f, 0.01f, -1.8f), linen, Col.None, p);
        }

        private static void BuildHallway(Kit k, Room r)
        {
            Transform p = k.Group("Hallway_Furniture");
            float x0 = r.X0 + Skin, x1 = r.X1 - Skin;
            Material oak = k.Mat("Oak", Palette.InteriorOak, 0.35f), birch = k.Mat("Birch", Palette.InteriorBirch, 0.3f);
            Material rug = k.Mat("RugRed", Palette.BoathouseRust, 0.05f), ceramic = k.Mat("Ceramic", Palette.InteriorCeramic, 0.85f);
            Material moss = k.Mat("Moss", Palette.TurfMoss, 0.1f), sea = k.Mat("PaintingSea", Palette.SkyZenithMist, 0.2f);
            Material sky = k.Mat("PaintingSky", Palette.SkyHorizonMist, 0.2f);

            k.CoatRack(new Vector3(x0, 0f, 1.0f), Vector3.right, Vector3.forward, 1.2f, p);
            k.Box("ShoeRack", new Vector3(x0, 0f, 2.4f), new Vector3(x0 + 0.32f, 0.42f, 3.0f), oak, Col.Box, p);
            k.Boots(new Vector3(x0 + 0.16f, 0.42f, 2.62f), p);
            k.Box("Commode", new Vector3(x1 - 0.42f, 0f, 1.45f), new Vector3(x1, 0.82f, 2.25f), birch, Col.Box, p);
            k.Box("CommodeTop", new Vector3(x1 - 0.44f, 0.82f, 1.43f), new Vector3(x1, 0.85f, 2.27f), oak, Col.None, p);
            k.Cyl("KeyBowl", new Vector3(x1 - 0.2f, 0.87f, 1.7f), 0.09f, 0.03f, ceramic, Col.None, p);
            k.Box("PictureFrame", new Vector3(x1 - 0.03f, 1.3f, 1.55f), new Vector3(x1, 1.8f, 2.15f), oak, Col.None, p);
            k.Box("PictureSea", new Vector3(x1 - 0.035f, 1.35f, 1.6f), new Vector3(x1 - 0.03f, 1.5f, 2.1f), sea, Col.None, p);
            k.Box("PictureHills", new Vector3(x1 - 0.035f, 1.5f, 1.6f), new Vector3(x1 - 0.03f, 1.62f, 2.1f), moss, Col.None, p);
            k.Box("PictureSky", new Vector3(x1 - 0.035f, 1.62f, 1.6f), new Vector3(x1 - 0.03f, 1.75f, 2.1f), sky, Col.None, p);
            k.Box("Runner", new Vector3(-3.9f, 0f, -0.9f), new Vector3(-2.4f, 0.012f, 2.6f), rug, Col.None, p);
        }

        private static void BuildEntranceHall(Kit k, Room r, float floor, float stairX, Transform rt, HouseData wing)
        {
            Transform p = k.Group("EntranceHall_Furniture");
            float x0 = r.X0 + Skin, z0 = r.Z0 + Skin, z1 = r.Z1 - Skin;
            Material oak = k.Mat("Oak", Palette.InteriorOak, 0.35f), basalt = k.Mat("Hearth", Palette.WetBasalt, 0.4f);
            Material black = k.Mat("Black", Palette.DeepFjordSlate * 0.6f, 0.5f);

            k.CoatRack(new Vector3(x0 + 0.35f, floor, z1), Vector3.back, Vector3.right, 1.2f, p);
            k.Box("ShoeBench", new Vector3(x0 + 1.8f, floor, z1 - 0.36f), new Vector3(Mathf.Min(x0 + 2.7f, stairX - 0.1f), floor + 0.45f, z1), oak, Col.Box, p);
            k.Cyl("UmbrellaStand", new Vector3(x0 + 0.25f, floor + 0.25f, z0 + 0.3f), 0.12f, 0.5f, black, Col.Box, p);

            Opening front = wing.doors?.FirstOrDefault();
            if (front != null)
            {
                float doorX = rt.InverseTransformPoint(new Vector3(front.x, front.y, front.z)).x;
                k.Box("DoorMat", new Vector3(doorX - 0.4f, floor, z0), new Vector3(doorX + 0.4f, floor + 0.012f, z0 + 0.55f), basalt, Col.None, p);
                k.Boots(new Vector3(doorX + 0.6f, floor, z0 + 0.25f), p);
            }
        }

        // ------------------------------------------------------------------ terrain and previews

        /// <summary>
        /// The hillside under the teal house rises above its floor at the uphill corner. Press the terrain down
        /// inside the footprint (and blend back up within a metre outside, under the plinth) so it can't poke
        /// through the rooms.
        /// </summary>
        private static void PressTerrainUnder(List<(HouseData house, float floorTop)> floors)
        {
            GameObject terrainGo = GameObject.Find("Exit_Terrain");
            if (terrainGo == null || !terrainGo.TryGetComponent(out MeshFilter mf) || mf.sharedMesh == null) return;
            Mesh mesh = mf.sharedMesh;
            if (!AssetDatabase.GetAssetPath(mesh).EndsWith(".asset"))
            {
                Debug.LogWarning("[VillageHousesSetup] Terrain mesh is the imported FBX; not pressing it under the teal house.");
                return;
            }

            Transform tf = terrainGo.transform;
            Vector3[] v = mesh.vertices;
            int moved = 0;
            for (int i = 0; i < v.Length; i++)
            {
                Vector3 w = tf.TransformPoint(v[i]);
                foreach ((HouseData h, float top) in floors)
                {
                    Vector3 l = Quaternion.Euler(0f, -h.yaw * Mathf.Rad2Deg, 0f) * new Vector3(w.x - h.x, 0f, w.z - h.z);
                    float ox = Mathf.Max(0f, Mathf.Abs(l.x) - h.w * 0.5f), oz = Mathf.Max(0f, Mathf.Abs(l.z) - h.d * 0.5f);
                    float outside = Mathf.Sqrt(ox * ox + oz * oz);
                    if (outside > 1.2f) continue;
                    float limit = top - 0.15f + 0.5f * outside;
                    if (w.y > limit)
                    {
                        w.y = limit;
                        v[i] = tf.InverseTransformPoint(w);
                        moved++;
                    }
                }
            }

            if (moved == 0) return;
            mesh.vertices = v;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            EditorUtility.SetDirty(mesh);
            if (terrainGo.TryGetComponent(out MeshCollider mc))
            {
                mc.sharedMesh = null;
                mc.sharedMesh = mesh;
            }

            Debug.Log($"[VillageHousesSetup] pressed {moved} terrain vertices under the teal house");
        }

        /// <summary>Saves shots of the teal house and the boathouse to Docs/preview (called from the village preview pass).</summary>
        public static void RenderPreviews(Camera cam, string previewDir, Action<Camera, string> shot)
        {
            if (LastShots.Count == 0 || cam == null) return;
            Vector3 keepPos = cam.transform.position;
            Quaternion keepRot = cam.transform.rotation;
            float keepFov = cam.fieldOfView, keepNear = cam.nearClipPlane;
            cam.nearClipPlane = 0.05f;
            // Rooms are only switched on while someone is inside; outside shots shouldn't show their lamps through the walls.
            GameObject[] rooms = UnityEngine.Object.FindObjectsByType<HouseInterior>(FindObjectsInactive.Include)
                .Select(hi => hi.transform.Find("Rooms")?.gameObject).Where(g => g != null).ToArray();
            foreach ((Transform root, string file, Vector3 eye, Vector3 look) in LastShots)
            {
                if (root == null) continue;
                bool indoors = file.StartsWith("interior_");
                foreach (GameObject g in rooms) g.SetActive(indoors);
                cam.transform.position = root.TransformPoint(eye);
                cam.transform.LookAt(root.TransformPoint(look));
                cam.fieldOfView = indoors ? 68f : 50f;
                shot(cam, Path.Combine(previewDir, file));
            }

            foreach (GameObject g in rooms) g.SetActive(true);
            cam.transform.SetPositionAndRotation(keepPos, keepRot);
            cam.fieldOfView = keepFov;
            cam.nearClipPlane = keepNear;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        // ------------------------------------------------------------------ primitive kit

        /// <summary>Makes boxes, cylinders and simple furniture in the house frame, with cached interior materials.</summary>
        private sealed class Kit
        {
            public readonly Transform Root;
            private readonly Dictionary<string, Material> _mats = new Dictionary<string, Material>();

            public Kit(Transform root) => Root = root;

            public Transform Group(string name)
            {
                var t = new GameObject(name).transform;
                t.SetParent(Root, false);
                return t;
            }

            public Material Mat(string name, Color color, float smoothness, Color? emission = null, float metallic = 0f)
            {
                if (_mats.TryGetValue(name, out Material cached)) return cached;
                string path = $"{InteriorDir}/Mat_Int_{name}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null)
                {
                    mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = $"Mat_Int_{name}" };
                    AssetDatabase.CreateAsset(mat, path);
                }

                color.a = 1f;
                mat.SetColor("_BaseColor", color);
                mat.SetFloat("_Smoothness", smoothness);
                mat.SetFloat("_Metallic", metallic);
                if (emission.HasValue)
                {
                    mat.EnableKeyword("_EMISSION");
                    mat.SetColor("_EmissionColor", emission.Value);
                    mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                }
                else
                {
                    mat.DisableKeyword("_EMISSION");
                    mat.SetColor("_EmissionColor", Color.black);
                }

                EditorUtility.SetDirty(mat);
                _mats[name] = mat;
                return mat;
            }

            public GameObject Box(string name, Vector3 min, Vector3 max, Material mat, Col col, Transform parent = null)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = name;
                go.transform.SetParent(parent != null ? parent : Root, false);
                go.transform.localPosition = (min + max) * 0.5f;
                go.transform.localScale = Vector3.Max(max - min, Vector3.one * 0.001f);
                ApplyCollider(go, col);
                Finish(go, mat, (max - min).magnitude > 0.9f);
                return go;
            }

            public GameObject Cyl(string name, Vector3 centre, float radius, float height, Material mat, Col col, Transform parent = null)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                go.name = name;
                go.transform.SetParent(parent != null ? parent : Root, false);
                go.transform.localPosition = centre;
                go.transform.localScale = new Vector3(radius * 2f, height * 0.5f, radius * 2f);
                UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
                if (col != Col.None) go.AddComponent<BoxCollider>();
                Finish(go, mat, false);
                return go;
            }

            /// <summary>An invisible solid box, for furniture built from thin parts.</summary>
            public void Blocker(Vector3 min, Vector3 max, Transform parent)
            {
                var go = new GameObject("Blocker") { isStatic = true };
                go.transform.SetParent(parent, false);
                var box = go.AddComponent<BoxCollider>();
                box.center = (min + max) * 0.5f;
                box.size = max - min;
            }

            private static void ApplyCollider(GameObject go, Col col)
            {
                if (col == Col.Box) return;   // the cube primitive already has one
                UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
                if (col == Col.Mesh)
                {
                    // Mesh colliders: the camera rig only pulls in for those, so walls keep a third-person view inside.
                    go.AddComponent<MeshCollider>().sharedMesh = go.GetComponent<MeshFilter>().sharedMesh;
                }
            }

            public static void Finish(GameObject go, Material mat, bool castShadows)
            {
                var r = go.GetComponent<MeshRenderer>();
                r.sharedMaterial = mat;
                r.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
                go.isStatic = true;
            }

            /// <summary>Wooden kitchen chair; <paramref name="yaw"/> is the way a sitter faces.</summary>
            public void Chair(Vector3 at, float yaw, Material wood, Transform parent)
            {
                Transform t = Group("Chair");
                t.SetParent(parent, false);
                t.localPosition = at;
                t.localRotation = Quaternion.Euler(0f, yaw, 0f);
                Box("Seat", new Vector3(-0.2f, 0.43f, -0.2f), new Vector3(0.2f, 0.47f, 0.2f), wood, Col.Box, t);
                foreach (float lx in new[] { -0.17f, 0.17f })
                foreach (float lz in new[] { -0.17f, 0.17f })
                    Box("Leg", new Vector3(lx - 0.02f, 0f, lz - 0.02f), new Vector3(lx + 0.02f, 0.43f, lz + 0.02f), wood, Col.None, t);
                Box("Back", new Vector3(-0.2f, 0.47f, -0.22f), new Vector3(0.2f, 0.95f, -0.18f), wood, Col.None, t);
            }

            /// <summary>Upholstered armchair; <paramref name="yaw"/> is the way a sitter faces.</summary>
            public void Armchair(Vector3 at, float yaw, Material fabric, Transform parent)
            {
                Transform t = Group("Armchair");
                t.SetParent(parent, false);
                t.localPosition = at;
                t.localRotation = Quaternion.Euler(0f, yaw, 0f);
                Box("Seat", new Vector3(-0.38f, 0f, -0.36f), new Vector3(0.38f, 0.42f, 0.36f), fabric, Col.Box, t);
                Box("Back", new Vector3(-0.38f, 0.42f, -0.36f), new Vector3(0.38f, 0.95f, -0.22f), fabric, Col.None, t);
                Box("ArmL", new Vector3(-0.38f, 0.42f, -0.36f), new Vector3(-0.26f, 0.62f, 0.36f), fabric, Col.None, t);
                Box("ArmR", new Vector3(0.26f, 0.42f, -0.36f), new Vector3(0.38f, 0.62f, 0.36f), fabric, Col.None, t);
            }

            /// <summary>Hook board on a wall with three coats. <paramref name="intoRoom"/> points off the wall, <paramref name="along"/> along it.</summary>
            public void CoatRack(Vector3 start, Vector3 intoRoom, Vector3 along, float length, Transform parent)
            {
                Material oak = Mat("Oak", Palette.InteriorOak, 0.35f), steel = Mat("Steel", Palette.InteriorSteel, 0.7f, null, 0.8f);
                Material[] coats = { Mat("RugRed", Palette.BoathouseRust, 0.05f), Mat("Black", Palette.DeepFjordSlate * 0.6f, 0.5f), Mat("Moss", Palette.TurfMoss, 0.1f) };
                Vector3 P(float a, float y, float o) => start + along * a + Vector3.up * y + intoRoom * o;
                void B(string name, float a0, float a1, float y0, float y1, float o0, float o1, Material m)
                {
                    Vector3 p = P(a0, y0, o0), q = P(a1, y1, o1);
                    Box(name, Vector3.Min(p, q), Vector3.Max(p, q), m, Col.None, parent);
                }

                B("HookBoard", 0f, length, 1.55f, 1.7f, 0f, 0.03f, oak);
                for (int i = 0; i < 4; i++)
                {
                    float a = 0.12f + i * (length - 0.24f) / 3f;
                    B("Hook", a - 0.015f, a + 0.015f, 1.6f, 1.63f, 0.03f, 0.1f, steel);
                }

                float[] spans = { 0.08f, 0.42f, 0.48f, 0.8f, 0.86f, 1.12f };
                for (int i = 0; i < 3; i++)
                {
                    B("Coat", spans[i * 2] * length / 1.2f, spans[i * 2 + 1] * length / 1.2f, 0.85f - i * 0.05f, 1.6f, 0.03f, 0.22f, coats[i]);
                }
            }

            public void Boots(Vector3 at, Transform parent)
            {
                Material black = Mat("Black", Palette.DeepFjordSlate * 0.6f, 0.5f);
                Box("Boot", at + new Vector3(-0.13f, 0f, -0.06f), at + new Vector3(-0.02f, 0.3f, 0.2f), black, Col.None, parent);
                Box("Boot", at + new Vector3(0.02f, 0f, -0.06f), at + new Vector3(0.13f, 0.3f, 0.2f), black, Col.None, parent);
            }

            /// <summary>Pendant lamp with a warm point light just under its shade.</summary>
            public void CeilingLamp(string roomName, Vector3 ceilingPoint, float roomHeight, float range)
            {
                Transform t = Group($"{roomName}_Lamp");
                Material black = Mat("Black", Palette.DeepFjordSlate * 0.6f, 0.5f);
                Material glow = Mat("LampShade", Palette.InteriorLampGlow, 0.2f, Palette.InteriorLampGlow * 1.4f);
                float drop = Mathf.Clamp(roomHeight - 2.05f, 0.25f, 0.55f);
                Cyl("Cord", ceilingPoint + Vector3.down * drop * 0.5f, 0.006f, drop, black, Col.None, t);
                Cyl("Shade", ceilingPoint + Vector3.down * (drop + 0.08f), 0.17f, 0.16f, glow, Col.None, t);
                var lightGo = new GameObject("Light");
                lightGo.transform.SetParent(t, false);
                lightGo.transform.localPosition = ceilingPoint + Vector3.down * (drop + 0.3f);
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = Palette.InteriorLampGlow;
                light.intensity = 2.4f;
                light.range = range;
                light.shadows = LightShadows.None;
            }
        }
    }
}
