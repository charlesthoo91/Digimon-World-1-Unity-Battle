using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The optional mods (title screen > MODS). Not part of the original game: every switch is off by default, and with all of them off
    /// the game is the faithful port. Kept on this PC in PlayerPrefs.
    /// </summary>
    public static class ModSettings
    {
        const string Prefix = "DW1Remake.Mod.";

        /// <summary>The digital clock (hours:minutes) under the clock dial; ours, the original shows only the dial.</summary>
        public static bool DigitalClock { get { return Get("DigitalClock"); } set { Set("DigitalClock", value); } }

        /// <summary>Greymon's arena (ROOM13 / ROOM19) offers a battle against a friend's Digimon on another PC (PlayableRoomOnline).</summary>
        public static bool OnlineBattle { get { return Get("OnlineBattle"); } set { Set("OnlineBattle", value); } }

        /// <summary>The picture is drawn up to 60 times a second instead of the original 20 (the game's own clock stays at 20 either way).</summary>
        public static bool Smooth60 { get { return Get("Smooth60"); } set { Set("Smooth60", value); } }

        /// <summary>Square on a Digimon of the Chart page shows what its evolutions ask for, the partner's numbers and the evolution the game would choose (EvolutionGuideView).</summary>
        public static bool EvolutionGuide { get { return Get("EvolutionGuide"); } set { Set("EvolutionGuide", value); } }

        /// <summary>A fifth Player menu tab ("Rec.") lists who has joined File City, the points each brings and the prosperity (RecruitView).</summary>
        public static bool RecruitTab { get { return Get("RecruitTab"); } set { Set("RecruitTab", value); } }

        /// <summary>A plate under fishing's tension gauge with its value, 0% (the hook comes out) to 100% (the line snaps); the original shows only the bars.</summary>
        public static bool TensionValue { get { return Get("TensionValue"); } set { Set("TensionValue", value); } }

        /// <summary>A "PRESS X" plate over the float while a bite can be struck (FISH_tryStrike would hook it); the original gives no sign but the float.</summary>
        public static bool FishingIndicator { get { return Get("FishingIndicator"); } set { Set("FishingIndicator", value); } }

        /// <summary>Select while the boy walks freely shows a see-through map of the screen (its walking grid, the exits with the areas they lead to, everyone as dots).</summary>
        public static bool MapView { get { return Get("MapView"); } set { Set("MapView", value); } }

        /// <summary>After a won wild fight (no flag 1, the enemies run off) the picture glides back to the boy; the original leaves it where the fight left it.</summary>
        public static bool FightRecentre { get { return Get("FightRecentre"); } set { Set("FightRecentre", value); } }

        /// <summary>The map's picture is upscaled by an AI model on this PC's graphics card and kept for next time (PlayableRoomAiUpscale); PC only.</summary>
        public static bool AiUpscale { get { return Get("AiUpscale"); } set { Set("AiUpscale", value); } }

        /// <summary>The 3D view is 16:9 instead of the original 4:3: more of the map shows at the sides; the HUD and menus stay in the 4:3 middle (UiKit.View).</summary>
        public static bool Widescreen { get { return Get("Widescreen"); } set { Set("Widescreen", value); } }

        /// <summary>The master volume, 0 to 100 (OPTIONS > VOLUME; 100 = the game's own levels).</summary>
        /// <summary>Ours (OPTIONS > SCREEN MODE, PC): 0 a window, 1 borderless full screen, 2 full screen.</summary>
        public static int ScreenMode { get { return Mathf.Clamp(GetInt("ScreenMode", 1), 0, 2); } set { SetInt("ScreenMode", value); } }
        /// <summary>Ours (OPTIONS > RESOLUTION, PC): 0 the screen's own, 1 1280x720, 2 1920x1080, 3 2560x1440, 4 3840x2160.</summary>
        public static int Resolution { get { return Mathf.Clamp(GetInt("Resolution", 0), 0, 4); } set { SetInt("Resolution", value); } }

        /// <summary>Ours (OPTIONS > STATUS NUMBERS): the Status page shows happiness, discipline and virus as numbers on their bars, and the life span left.</summary>
        public static bool StatusNumbers { get { return Get("StatusNumbers"); } set { Set("StatusNumbers", value); } }

        // ours (OPTIONS > CHEATS): put into effect when a game is loaded (PlayableRoom.ApplyCheats). Kept in memory only, never in PlayerPrefs: every cheat is off
        // each time the game starts, so a clean save is not changed by a cheat left on from an earlier session
        /// <summary>99 of every item but the story ones in the Item Keeper's (Agumon's) bank, once Agumon is recruited (flag 0xCB).</summary>
        public static bool CheatBank;
        /// <summary>Every Digimon of the Chart page counts as raised.</summary>
        public static bool CheatChart;
        /// <summary>Every medal won (flags 0x16C + n).</summary>
        public static bool CheatMedals;
        /// <summary>At least 1 of each of the 66 cards.</summary>
        public static bool CheatCards;
        /// <summary>999,999 Bits (the game's top).</summary>
        public static bool CheatBits;
        /// <summary>L1 + R1 + Circle while the clock runs moves time on one hour (from the next loaded game).</summary>
        public static bool CheatHour;
        /// <summary>Every technique learned (as the debug menu's "Learn every technique").</summary>
        public static bool CheatTechs;
        /// <summary>The six stats at the game's top (HP / MP 9999, Offense / Defense / Speed / Brains 999).</summary>
        public static bool CheatStats;

        // all cheats off at each start of the game (also in the editor when Play starts without reloading the scripts)
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void CheatsOff() { CheatBank = CheatChart = CheatMedals = CheatCards = CheatBits = CheatHour = CheatTechs = CheatStats = false; }

        public static int Volume { get { return Mathf.Clamp(GetInt("Volume", 100), 0, 100); } set { SetInt("Volume", value); } }

        /// <summary>The language of the OPTIONS page and the remake's own texts: 0 English, 1 Traditional Chinese (the disc's texts stay English).</summary>
        public static int Language { get { return GetInt("Language", 0); } set { SetInt("Language", value); } }

        // ---- controls (MODS > CONTROLS; Pad, TouchPadView). These change only how the pad is pressed, not the game, so they start usable:
        // a player with only a touch screen or only a controller must be able to reach this page.

        /// <summary>A controller (DualShock 4 or any pad Unity knows) works as the PS1 pad. On unless switched off.</summary>
        public static bool Controller { get { return GetInt("Controller", 1) != 0; } set { SetInt("Controller", value ? 1 : 0); } }

        /// <summary>The on-screen touch buttons: 0 off, 1 auto (shown once the screen is touched; the default), 2 always.</summary>
        public static int TouchButtons { get { return Mathf.Clamp(GetInt("TouchButtons", 1), 0, 2); } set { SetInt("TouchButtons", value); } }

        static int GetInt(string key, int def) { try { return PlayerPrefs.GetInt(Prefix + key, def); } catch { return def; } }

        static void SetInt(string key, int v)
        {
            try { PlayerPrefs.SetInt(Prefix + key, v); PlayerPrefs.Save(); }
            catch (System.Exception e) { Debug.LogWarning("[mods] " + e.Message); }
        }

        static bool Get(string key) { try { return PlayerPrefs.GetInt(Prefix + key, 0) != 0; } catch { return false; } }

        static void Set(string key, bool on)
        {
            try { PlayerPrefs.SetInt(Prefix + key, on ? 1 : 0); PlayerPrefs.Save(); }
            catch (System.Exception e) { Debug.LogWarning("[mods] " + e.Message); }
        }
    }
}
