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

        /// <summary>Map pictures and map pieces are upscaled on this PC when a map loads (MapRemaster); nothing is stored or shipped.</summary>
        public static bool RemasteredMaps { get { return Get("RemasteredMaps"); } set { Set("RemasteredMaps", value); } }

        /// <summary>Greymon's arena (ROOM13 / ROOM19) offers a battle against a friend's Digimon on another PC (PlayableRoomOnline).</summary>
        public static bool OnlineBattle { get { return Get("OnlineBattle"); } set { Set("OnlineBattle", value); } }

        /// <summary>The picture is drawn up to 60 times a second instead of the original 20 (the game's own clock stays at 20 either way).</summary>
        public static bool Smooth60 { get { return Get("Smooth60"); } set { Set("Smooth60", value); } }

        static bool Get(string key) { try { return PlayerPrefs.GetInt(Prefix + key, 0) != 0; } catch { return false; } }

        static void Set(string key, bool on)
        {
            try { PlayerPrefs.SetInt(Prefix + key, on ? 1 : 0); PlayerPrefs.Save(); }
            catch (System.Exception e) { Debug.LogWarning("[mods] " + e.Message); }
        }
    }
}
