using System.Collections.Generic;
using System.IO;
using DW1Remake.Formats;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DW1Remake.Game
{
    /// <summary>Save / load, the quick start (skip the opening) and the development debug menu (F10) of the room.</summary>
    public sealed partial class PlayableRoom
    {
        [Tooltip("Skip the opening: start in File City as the boy with Agumon, the opening's starting numbers and 10 bag slots. For testing later content.")]
        public bool QuickStart = false;
        [Tooltip("If a save file exists (F11 saves in the editor), continue from it instead of starting fresh.")]
        public bool LoadSaveOnStart = false;

        public static string SavePath { get { return Path.Combine(Application.persistentDataPath, "save1.json"); } }

        // ------------------------------------------------------------------ save / load (F11 / F12 in the editor and development builds)

        public void SaveToFile()
        {
            if (_player == null || _mapId < 0) return;
            var d = SaveGameData.From(_state);
            d.Map = _mapNames[_mapId]; d.X = _player.position.x; d.Y = _player.position.y; d.Z = _player.position.z;
            d.Clock = _clock; d.Day = _dayCount; d.PartnerType = PartnerType;
            File.WriteAllText(SavePath, JsonUtility.ToJson(d));
            Say("Saved to " + SavePath);
        }

        SaveGameData ReadSave()
        {
            try { return File.Exists(SavePath) ? JsonUtility.FromJson<SaveGameData>(File.ReadAllText(SavePath)) : null; }
            catch (System.Exception e) { Debug.LogWarning("Save file unreadable: " + e.Message); return null; }
        }

        // applies a save to the state; the caller loads the map and puts the player down (see PlaceFromSave)
        void ApplySave(SaveGameData d)
        {
            d.ApplyTo(_state);
            _clock = d.Clock; _dayCount = d.Day;
            _intro = false;
        }

        void PlaceFromSave(SaveGameData d)
        {
            _player.position = new Vector3(d.X, d.Y, d.Z);
            if (_partner != null) { _partner.gameObject.SetActive(true); PlacePartner(_player.position, Vector3.back); }
        }

        public void LoadFromFile()
        {
            var d = ReadSave();
            if (d == null) { Say("No save file yet (F11 saves)."); return; }
            if (_changing || _battle != null && _battle.Active) return;
            ApplySave(d);
            int mi = _mapNames.IndexOf(d.Map);
            if (mi < 0) { Say("Save points to an unknown map " + d.Map); return; }
            try { LoadMap(MapCatalog.PathFor(_mapNames, mi), -1); PlaceFromSave(d); }
            catch (System.Exception e) { _error = e.Message; Debug.LogException(e); return; }
            Say("Loaded.");
        }

        // the starting state of a game that skipped the opening
        void QuickStartState()
        {
            _state.PlayerName = PlayerNameText; _state.PartnerName = PartnerNameText;
            ushort[] v = { 80, 60, 70, 70, 800, 600, 800, 600 };
            for (int i = 0; i < v.Length; i++) _state.Stats[i] = v[i];
            _state.ItemSlots = 10;
            _state.Vars[103] = (byte)PartnerType;
            _state.SetFlag(54, true); _state.SetFlag(55, false);
            _state.Raised[PartnerType] = true;
        }

        // leaves the setup screens or the opening script wherever they are and starts in File City, as Quick Start does
        void SkipOpening()
        {
            if (_changing) return;
            _setup = null; DestroySetupStage(); _intro = false; _fade = 0f;
            QuickStartState();
            if (_partner == null) { HasPartner = true; BuildPartner(); }
            int ti = _mapNames.IndexOf("TWNA01");
            if (ti < 0) { Say("TWNA01 not found."); return; }
            try { LoadMap(MapCatalog.PathFor(_mapNames, ti), 2); }
            catch (System.Exception e) { _error = e.Message; Debug.LogException(e); return; }
            if (_partner != null) { _partner.gameObject.SetActive(true); PlacePartner(_player.position, Vector3.back); }
            Say("Opening skipped.");
        }

        // ------------------------------------------------------------------ keys and debug menu

        bool _debugOpen; Vector2 _debugScroll; string _mapFilter = "", _flagText = "249";

        void HandleDebugAndSaveKeys()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current; if (k == null) return;
            if (k.f10Key.wasPressedThisFrame) _debugOpen = !_debugOpen;
            if (k.f11Key.wasPressedThisFrame) SaveToFile();
            if (k.f12Key.wasPressedThisFrame) LoadFromFile();
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.F10)) _debugOpen = !_debugOpen;
            if (Input.GetKeyDown(KeyCode.F11)) SaveToFile();
            if (Input.GetKeyDown(KeyCode.F12)) LoadFromFile();
#endif
#endif
        }

        static readonly int[] RecruitFlags = { 0xF9, 0xCC, 0xCB, 0xCD, 0xD3, 0xD5, 0xD9, 0xDA, 0xDB, 0xDC, 0xDD, 0xDE, 0xDF, 0xE0, 0xE1, 0xE3, 0xE4, 0xE7, 0xE8, 0xE9, 0xEA, 0xEB,
                                               0xED, 0xEE, 0xF0, 0xF1, 0xF2, 0xF5, 0xF6, 0xF7, 0xF8, 0xFA, 0xFC, 0xFF, 0x100, 0x101, 0x102, 0xD0, 0xD1 };

        void DrawDebugMenu()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!_debugOpen || _state == null) return;
            float top = _hintBottom + 30f;
            GUILayout.BeginArea(new Rect(20, top, Mathf.Min(600f, Screen.width - 40f), Screen.height - top - 20f), GUI.skin.box);
            GUILayout.Label("DEBUG MENU (F10 closes)   F11 = save, F12 = load");
            _debugScroll = GUILayout.BeginScrollView(_debugScroll);
            GUILayout.Label("Progress");
            if (GUILayout.Button("Skip the opening (go to File City with Agumon)")) { SkipOpening(); _debugOpen = false; }
            if (GUILayout.Button("Recruit Coelamon (flag 0xF9)")) { _state.SetFlag(0xF9, true); BuildNpcs(); }
            if (GUILayout.Button("Recruit EVERY Digimon (all recruit flags)")) { foreach (var f in RecruitFlags) _state.SetFlag(f, true); BuildNpcs(); Say("All recruit flags on."); }
            GUILayout.BeginHorizontal(); GUILayout.Label("Flag", GUILayout.Width(40)); _flagText = GUILayout.TextField(_flagText, GUILayout.Width(80));
            int fl; int.TryParse(_flagText, out fl);
            if (GUILayout.Button("On")) { _state.SetFlag(fl, true); BuildNpcs(); }
            if (GUILayout.Button("Off")) { _state.SetFlag(fl, false); BuildNpcs(); }
            GUILayout.Label(_state.GetFlag(fl) ? "is ON" : "is off"); GUILayout.EndHorizontal();

            GUILayout.Label("Partner and bag");
            if (GUILayout.Button("Full HP and MP")) { _state.Stats[6] = _state.Stats[4]; _state.Stats[7] = _state.Stats[5]; }
            if (GUILayout.Button("Brains 500 (all battle orders)")) _state.Stats[3] = 500;
            if (GUILayout.Button("Offense/Defense/Speed 300")) { _state.Stats[0] = 300; _state.Stats[1] = 300; _state.Stats[2] = 300; }
            if (GUILayout.Button("+10000 Bits")) _state.Money += 10000;
            if (GUILayout.Button("5 of every item (bag slots 128)")) { for (int i = 0; i < _state.Bag.Length; i++) _state.Bag[i] = 5; _state.ItemSlots = 128; }
            if (GUILayout.Button("Learn every technique")) { for (int i = 0; i < 58; i++) _state.Techs[i] = true; }
            if (GUILayout.Button("Raise every Digimon (Chart page)")) { for (int i = 0; i < _state.Raised.Length; i++) _state.Raised[i] = true; }
            if (GUILayout.Button("Collect every card")) { for (int i = 0; i < Portraits.Count; i++) _state.Cards[i] = true; }

            GUILayout.Label("Time");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("+1 h")) _clock = (_clock + 1f) % 24f;
            if (GUILayout.Button("+6 h")) _clock = (_clock + 6f) % 24f;
            if (GUILayout.Button("Morning (8:00)")) _clock = 8f;
            if (GUILayout.Button("Night (22:00)")) _clock = 22f;
            GUILayout.EndHorizontal();

            GUILayout.Label("Go to map (type part of the name)");
            _mapFilter = GUILayout.TextField(_mapFilter);
            int shown = 0;
            for (int i = 0; i < _mapNames.Count && shown < 40; i++)
            {
                if (_mapFilter.Length > 0 && _mapNames[i].IndexOf(_mapFilter, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                shown++;
                if (GUILayout.Button(i + "  " + _mapNames[i])) { GotoMap(MapCatalog.PathFor(_mapNames, i)); _debugOpen = false; }
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
#endif
        }
    }
}
