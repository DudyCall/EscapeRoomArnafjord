using UnityEngine;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// A readable clue note carried in a player's pockets/hand. Pressing U / Space while holding it opens or closes
    /// the reading view via <see cref="UI.ClueNoteUi"/>.
    /// </summary>
    public sealed class ClueNote : MonoBehaviour, IUsableItem
    {
        private static Mesh s_sheetMesh;
        private static Mesh s_lineMesh;
        private static Material s_paperMat;
        private static Material s_inkMat;

        [SerializeField] private string noteTitle = "Soggy Note";
        [SerializeField, TextArea] private string noteText = FishingRod.WireClueText;

        public string NoteTitle => string.IsNullOrWhiteSpace(noteTitle) ? "Note" : noteTitle;
        public string NoteText => noteText ?? string.Empty;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticAssets()
        {
            s_sheetMesh = null;
            s_lineMesh = null;
            s_paperMat = null;
            s_inkMat = null;
        }

        /// <summary>Assigns the heading and body text shown when the player reads the note.</summary>
        public void Configure(string title, string text)
        {
            noteTitle = title;
            noteText = text;
        }

        public void OnUseItem(CharacterItemHolder user, PickupItem item)
        {
            UI.ClueNoteUi.Toggle(this, user);
        }

        /// <summary>Spawns a procedural 3D note <see cref="ClueNote"/> ready for pockets, hand, or ground.</summary>
        public static ClueNote CreateNoteObject(string goName, string displayName, string clueText, string title = "Soggy Note")
        {
            EnsureAssets();

            var root = new GameObject(goName);

            var sheet = new GameObject("Sheet");
            sheet.transform.SetParent(root.transform, false);
            sheet.transform.localScale = new Vector3(0.085f, 0.004f, 0.115f);
            sheet.AddComponent<MeshFilter>().sharedMesh = s_sheetMesh;
            sheet.AddComponent<MeshRenderer>().sharedMaterial = s_paperMat;

            // Handwritten ink lines on both sides so the note reads clearly from any camera angle.
            float[] lineZs = { 0.028f, 0.009f, -0.010f, -0.029f };
            float[] lineWidths = { 0.056f, 0.062f, 0.050f, 0.038f };
            foreach (float faceSign in new[] { 1f, -1f })
            {
                for (int i = 0; i < lineZs.Length; i++)
                {
                    var line = new GameObject($"InkLine_{(faceSign > 0 ? "Top" : "Bot")}_{i}");
                    line.transform.SetParent(root.transform, false);
                    line.transform.localPosition = new Vector3((lineWidths[i] - 0.062f) * 0.5f, faceSign * 0.0022f, lineZs[i]);
                    line.transform.localScale = new Vector3(lineWidths[i], 0.0006f, 0.0045f);
                    line.AddComponent<MeshFilter>().sharedMesh = s_lineMesh;
                    line.AddComponent<MeshRenderer>().sharedMaterial = s_inkMat;
                }
            }

            var box = root.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(0.10f, 0.02f, 0.13f);

            var item = root.AddComponent<PickupItem>();
            item.Configure(displayName, 0.025f, 0.003f, true, "Read", false);
            item.SetHeldPose(new Vector3(-0.026f, 0.008f, 0.012f), new Vector3(-68f, -72f, 0f));

            var note = root.AddComponent<ClueNote>();
            note.Configure(title, clueText);
            return note;
        }

        private static void EnsureAssets()
        {
            Mesh cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            if (s_sheetMesh == null) s_sheetMesh = cube;
            if (s_lineMesh == null) s_lineMesh = cube;

            if (s_paperMat == null)
            {
                Shader lit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                s_paperMat = new Material(lit) { name = "Mat_ClueNote_Paper" };
                s_paperMat.SetColor("_BaseColor", Core.Palette.UiTextParchment);
                s_paperMat.SetFloat("_Smoothness", 0.15f);
            }

            if (s_inkMat == null)
            {
                Shader lit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                s_inkMat = new Material(lit) { name = "Mat_ClueNote_Ink" };
                s_inkMat.SetColor("_BaseColor", Core.Palette.DeepFjordSlate);
                s_inkMat.SetFloat("_Smoothness", 0.1f);
            }
        }
    }
}
