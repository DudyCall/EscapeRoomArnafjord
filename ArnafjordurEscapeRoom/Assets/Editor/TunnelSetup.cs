using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Arnafjordur.Editor
{
    /// <summary>
    /// Builds URP materials for the old single-lane Árnafjørður tunnel pieces (exported from
    /// Environment/Blender/build_tunnel.py) and assembles them into one long unlit tunnel prefab.
    /// </summary>
    public static class TunnelSetup
    {
        private const string Dir = "Assets/Environment/Tunnel";
        private const string MatDir = Dir + "/Materials";
        private const string TexDir = Dir + "/Textures";
        private const float PieceLength = 24f;

        // 'S' = straight piece, 'B' = passing bay. Travel direction is +Z.
        private const string Layout = "SSSBSSSSBSSSSBSS";

        [MenuItem("Tools/Environment/Rebuild Arnafjordur Tunnel Prefab")]
        public static void Build()
        {
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            if (lit == null || unlit == null)
            {
                Debug.LogError("[TunnelSetup] URP shaders not found");
                return;
            }

            Directory.CreateDirectory(MatDir);
            AssetDatabase.Refresh();

            foreach (var n in new[] { "Tunnel_Rock_Normal", "Tunnel_Road_Normal" })
            {
                var ti = AssetImporter.GetAtPath($"{TexDir}/{n}.png") as TextureImporter;
                if (ti != null && ti.textureType != TextureImporterType.NormalMap)
                {
                    ti.textureType = TextureImporterType.NormalMap;
                    ti.SaveAndReimport();
                }
            }

            var rock = LitMaterial("Mat_Tunnel_Rock", lit, "Tunnel_Rock", 0.32f, 1.4f);
            var road = LitMaterial("Mat_Tunnel_Road", lit, "Tunnel_Road", 0.22f, 1.0f);
            var black = LoadOrCreate("Mat_Tunnel_Darkness", unlit);
            black.SetColor("_BaseColor", Color.black);
            EditorUtility.SetDirty(black);

            foreach (var piece in new[] { "TunnelStraight", "TunnelPassingBay" })
            {
                var importer = AssetImporter.GetAtPath($"{Dir}/{piece}.fbx") as ModelImporter;
                if (importer == null)
                {
                    Debug.LogError($"[TunnelSetup] Missing {piece}.fbx");
                    return;
                }
                importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), "TunnelRock"), rock);
                importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), "TunnelRoad"), road);
                importer.importAnimation = false;
                importer.animationType = ModelImporterAnimationType.None;
                importer.importCameras = false;
                importer.importLights = false;
                importer.SaveAndReimport();
            }
            AssetDatabase.SaveAssets();

            var straight = AssetDatabase.LoadAssetAtPath<GameObject>($"{Dir}/TunnelStraight.fbx");
            var bay = AssetDatabase.LoadAssetAtPath<GameObject>($"{Dir}/TunnelPassingBay.fbx");

            var root = new GameObject("ArnafjordurTunnel");
            int tris = 0, unmapped = 0;
            for (int i = 0; i < Layout.Length; i++)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(Layout[i] == 'B' ? bay : straight, root.transform);
                go.name = $"Piece_{i:00}_{(Layout[i] == 'B' ? "PassingBay" : "Straight")}";
                go.transform.localPosition = new Vector3(0f, 0f, i * PieceLength);
                go.isStatic = true;
                foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
                {
                    mf.gameObject.isStatic = true;
                    mf.gameObject.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
                    tris += mf.sharedMesh.triangles.Length / 3;
                    foreach (var m in mf.GetComponent<MeshRenderer>().sharedMaterials)
                        if (m != rock && m != road) unmapped++;
                }
            }

            // Black plugs so the open ends read as darkness instead of sky.
            float total = Layout.Length * PieceLength;
            Plug(root.transform, "Darkness_Start", new Vector3(0f, 3f, -0.5f), Quaternion.identity, black);
            Plug(root.transform, "Darkness_End", new Vector3(0f, 3f, total + 0.5f), Quaternion.Euler(0f, 180f, 0f), black);

            PrefabUtility.SaveAsPrefabAsset(root, $"{Dir}/ArnafjordurTunnel.prefab");

            var b = new Bounds();
            bool first = true;
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>())
            {
                if (r.name.StartsWith("Darkness")) continue;
                if (first) { b = r.bounds; first = false; } else b.Encapsulate(r.bounds);
            }
            Debug.Log($"[TunnelSetup] OK length={total}m pieces={Layout.Length} boundsMin={b.min} boundsMax={b.max} tris={tris} unmappedMaterials={unmapped}");
            Object.DestroyImmediate(root);
        }

        private static void Plug(Transform parent, string name, Vector3 pos, Quaternion rot, Material mat)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Object.DestroyImmediate(q.GetComponent<Collider>());
            q.name = name;
            q.transform.SetParent(parent, false);
            q.transform.localPosition = pos;
            q.transform.localRotation = rot * Quaternion.Euler(0f, 180f, 0f);   // quad faces -Z by default; face into the bore
            q.transform.localScale = new Vector3(16f, 10f, 1f);
            var r = q.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
        }

        private static Material LoadOrCreate(string name, Shader shader)
        {
            string path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            return mat;
        }

        private static Material LitMaterial(string name, Shader shader, string tex, float smoothness, float bump)
        {
            var mat = LoadOrCreate(name, shader);
            var albedo = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/{tex}_Albedo.png");
            var normal = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/{tex}_Normal.png");
            mat.SetTexture("_BaseMap", albedo);
            mat.SetTexture("_MainTex", albedo);
            mat.SetColor("_BaseColor", Color.white);
            mat.SetTexture("_BumpMap", normal);
            mat.SetFloat("_BumpScale", bump);
            mat.EnableKeyword("_NORMALMAP");
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Smoothness", smoothness);
            EditorUtility.SetDirty(mat);
            return mat;
        }
    }
}
