using UnityEngine;

namespace Arnafjordur.Core
{
    /// <summary>
    /// Single source of truth for the coastal Árnafjørður visual palette across scene materials, lighting, and UI.
    /// </summary>
    public static class Palette
    {
        // Coastal stone, turf, and architecture
        public static readonly Color DeepFjordSlate   = new Color32(0x1D, 0x29, 0x35, 0xFF); // #1D2935
        public static readonly Color WetBasalt        = new Color32(0x4A, 0x58, 0x66, 0xFF); // #4A5866
        public static readonly Color HarbourConcrete  = new Color32(0xC9, 0xC2, 0xB6, 0xFF); // #C9C2B6
        public static readonly Color DriftwoodTimber  = new Color32(0x8A, 0x6B, 0x4F, 0xFF); // #8A6B4F
        public static readonly Color TurfMoss         = new Color32(0x6D, 0x80, 0x67, 0xFF); // #6D8067
        public static readonly Color BoathouseRust    = new Color32(0xA6, 0x44, 0x38, 0xFF); // #A64438
        public static readonly Color SeaFoam          = new Color32(0xD8, 0xE2, 0xE4, 0xFF); // #D8E2E4

        // Lighting and North Atlantic ambient gradient
        public static readonly Color SkyZenithMist    = new Color32(0x6E, 0x84, 0x96, 0xFF); // #6E8496
        public static readonly Color SkyHorizonMist   = new Color32(0xB8, 0xC5, 0xCC, 0xFF); // #B8C5CC
        public static readonly Color GroundShadowTone = new Color32(0x2F, 0x3B, 0x46, 0xFF); // #2F3B46
        public static readonly Color SunbreakKey      = new Color32(0xFF, 0xE6, 0xC4, 0xFF); // #FFE6C4
        public static readonly Color LanternAmber     = new Color32(0xE3, 0x9D, 0x54, 0xFF); // #E39D54

        // House interiors (the teal house's rooms): painted panel walls, pine floors, simple Nordic furniture
        public static readonly Color InteriorPlaster      = new Color32(0xE9, 0xE4, 0xD8, 0xFF); // #E9E4D8
        public static readonly Color InteriorTrim         = new Color32(0xF5, 0xF3, 0xEE, 0xFF); // #F5F3EE
        public static readonly Color InteriorKitchenWall  = new Color32(0xE6, 0xD9, 0xAE, 0xFF); // #E6D9AE
        public static readonly Color InteriorLivingWall   = new Color32(0xC4, 0xCC, 0xBA, 0xFF); // #C4CCBA
        public static readonly Color InteriorBedroomWall  = new Color32(0xA7, 0xB8, 0xC6, 0xFF); // #A7B8C6
        public static readonly Color InteriorPineFloor    = new Color32(0xB0, 0x86, 0x5A, 0xFF); // #B0865A
        public static readonly Color InteriorOak          = new Color32(0x6E, 0x4E, 0x34, 0xFF); // #6E4E34
        public static readonly Color InteriorBirch        = new Color32(0xD6, 0xBF, 0x96, 0xFF); // #D6BF96
        public static readonly Color InteriorWool         = new Color32(0x56, 0x6A, 0x79, 0xFF); // #566A79
        public static readonly Color InteriorLinen        = new Color32(0xEE, 0xE9, 0xE0, 0xFF); // #EEE9E0
        public static readonly Color InteriorSteel        = new Color32(0xB4, 0xB9, 0xBD, 0xFF); // #B4B9BD
        public static readonly Color InteriorCeramic      = new Color32(0xF6, 0xF6, 0xF3, 0xFF); // #F6F6F3
        public static readonly Color InteriorLampGlow     = new Color32(0xFF, 0xD7, 0xA0, 0xFF); // #FFD7A0

        // UI surfaces and readable contrast accents
        public static readonly Color UiPanelSlate     = new Color32(0x16, 0x20, 0x29, 0xEE); // #162029
        public static readonly Color UiTextParchment  = new Color32(0xF1, 0xEC, 0xE1, 0xFF); // #F1ECE1
        public static readonly Color UiAccentAmber    = LanternAmber;
        public static readonly Color UiConfirmGreen   = new Color32(0x4E, 0x9A, 0x7A, 0xFF); // #4E9A7A

        // Sea mine Module 3: the 8 wire insulation colours, in panel order (left to right)
        public static readonly Color[] BombWires =
        {
            new Color32(0xC8, 0x2A, 0x24, 0xFF), // red
            new Color32(0x2E, 0x5E, 0xC8, 0xFF), // blue
            new Color32(0xE8, 0xC2, 0x2C, 0xFF), // yellow
            new Color32(0x3C, 0x9A, 0x48, 0xFF), // green
            new Color32(0xEE, 0xEC, 0xE6, 0xFF), // white
            new Color32(0x22, 0x22, 0x24, 0xFF), // black
            new Color32(0xE8, 0x7A, 0x22, 0xFF), // orange
            new Color32(0x86, 0x44, 0xB0, 0xFF), // purple
        };

        public static readonly string[] BombWireNames = { "RED", "BLUE", "YELLOW", "GREEN", "WHITE", "BLACK", "ORANGE", "PURPLE" };
    }
}
