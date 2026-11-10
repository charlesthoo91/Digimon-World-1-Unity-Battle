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
