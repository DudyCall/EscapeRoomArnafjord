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

        // The black boathouse: board-walled hay loft with sheep upstairs, concrete boat room with a Faroese boat below
        public static readonly Color BarnPlank            = new Color32(0x8C, 0x6A, 0x4A, 0xFF); // #8C6A4A
        public static readonly Color BarnPlankDark        = new Color32(0x4E, 0x3A, 0x2A, 0xFF); // #4E3A2A
        public static readonly Color HayStraw             = new Color32(0xD2, 0xB2, 0x62, 0xFF); // #D2B262
        public static readonly Color HayDry               = new Color32(0x9E, 0x84, 0x4A, 0xFF); // #9E844A
        public static readonly Color SheepWoolCream       = new Color32(0xDD, 0xD3, 0xBE, 0xFF); // #DDD3BE
        public static readonly Color SheepWoolBrown       = new Color32(0x6E, 0x50, 0x3B, 0xFF); // #6E503B
        public static readonly Color SheepWoolDark        = new Color32(0x3B, 0x33, 0x2E, 0xFF); // #3B332E
        public static readonly Color SheepWoolGrey        = new Color32(0x9C, 0x96, 0x8C, 0xFF); // #9C968C
        public static readonly Color SheepFace            = new Color32(0x2C, 0x25, 0x21, 0xFF); // #2C2521
        public static readonly Color BoathouseConcrete    = new Color32(0x9A, 0x98, 0x92, 0xFF); // #9A9892
        public static readonly Color BoatHullWhite        = new Color32(0xE6, 0xE3, 0xDA, 0xFF); // #E6E3DA
        public static readonly Color BoatStrakeBlue       = new Color32(0x2F, 0x5C, 0x8A, 0xFF); // #2F5C8A
        public static readonly Color BoatTar              = new Color32(0x2A, 0x24, 0x20, 0xFF); // #2A2420
        public static readonly Color BoatInteriorWood     = new Color32(0xB0, 0x8A, 0x5E, 0xFF); // #B08A5E
        public static readonly Color FishCrateBlue        = new Color32(0x3C, 0x6E, 0x9C, 0xFF); // #3C6E9C
        public static readonly Color NetGreen             = new Color32(0x3E, 0x5A, 0x48, 0xFF); // #3E5A48
        public static readonly Color GunWalnut            = new Color32(0x5E, 0x3A, 0x22, 0xFF); // #5E3A22
        public static readonly Color GunBluedSteel        = new Color32(0x2A, 0x2E, 0x33, 0xFF); // #2A2E33
        public static readonly Color GunBrass             = new Color32(0xB8, 0x96, 0x3E, 0xFF); // #B8963E

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
