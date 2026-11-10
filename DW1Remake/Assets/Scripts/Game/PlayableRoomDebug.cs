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

        // ---- the memory cards (main_menu.c): two card slots of 15 Digimon World saves each (SAVEGAME_SLOT_INFO[15]); each save is a file here.
        // MEMORY_CARD_ID / MEMORY_CARD_SLOT: the card and slot this game was started in or continued from; the save machine and the sleep
        // menu save back to it. -1 = no memory card (NEW GAME > NO MEMORY CARD): that game cannot be saved
        public const int Cards = 2, SlotsPerCard = 15;
        int _card = -1, _cardSlot = -1;
        bool _noCard;       // the player chose NO MEMORY CARD at NEW GAME (a test start in the editor without a slot uses the test save instead)

        public static string SlotPath(int card, int slot) { return Path.Combine(Application.persistentDataPath, "save_card" + (card + 1) + "_slot" + (slot + 1).ToString("00") + ".json"); }

        // the editor's test save (F11 / F12, Load Save On Start) when no slot is chosen; older builds' only save
        static string TestSavePath { get { return Path.Combine(Application.persistentDataPath, "save1.json"); } }

        string SavePath { get { return _card >= 0 && _cardSlot >= 0 ? SlotPath(_card, _cardSlot) : TestSavePath; } }

        // an older build's single save becomes card 1, slot 1 (once; the old file stays)
        static void MoveOldSave()
        {
            try { if (File.Exists(TestSavePath) && !File.Exists(SlotPath(0, 0))) File.Copy(TestSavePath, SlotPath(0, 0)); }
            catch (System.Exception e) { Debug.LogWarning("[save] " + e.Message); }
        }

        static SaveGameData ReadSlot(int card, int slot)
        {
            try { string p = SlotPath(card, slot); return File.Exists(p) ? JsonUtility.FromJson<SaveGameData>(File.ReadAllText(p)) : null; }
            catch (System.Exception e) { Debug.LogWarning("[save] slot unreadable: " + e.Message); return null; }
        }

        // ------------------------------------------------------------------ save / load (F11 / F12 in the editor and development builds)

        public void SaveToFile()
        {
            if (_player == null || _mapId < 0) return;
            var d = SaveGameData.From(_state);
            d.Map = _mapNames[_mapId]; d.X = _player.position.x; d.Y = _player.position.y; d.Z = _player.position.z;
            d.Clock = _clock; d.Day = _dayCount; d.PartnerType = PartnerType; d.MainState = _mainState;
            d.PrevScreen = _prevScreen; d.PrevExit = _prevExit; d.CurExit = _curExit;
            File.WriteAllText(SavePath, JsonUtility.ToJson(d));
            WebFiles.Sync();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Say("Saved to " + SavePath);
#endif
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
            _dayCount = d.Day;
            _mainState = d.MainState;      // loadSavegame: MAIN_STATE = savegame->mainState
            _prevScreen = d.PrevScreen; _prevExit = d.PrevExit; _curExit = d.CurExit;     // PREVIOUS_SCREEN = SAVED_STATE.previousScreen
            _care.PartnerType = d.PartnerType;
            _care.SetClock((int)d.Clock, (int)((d.Clock % 1f) * 60f));
            _clock = _care.ClockHours;
            if (d.Version < 3) _care.InitPartner(PartnerType);   // a save from before the care numbers
            _intro = false;
        }

        void PlaceFromSave(SaveGameData d)
        {
            _player.position = new Vector3(d.X, d.Y, d.Z);
            if (_partner != null && d.PartnerType != PartnerType) SwapPartner(d.PartnerType);
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
            try { LoadMap(MapCatalog.PathFor(_mapNames, mi), -1); PlaceFromSave(d); RunMapStart(255); }
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
        // dev tool: a fight at once against an Agumon with the first fight's numbers (MAYO00: 400 HP, 300 MP, 30/30/30/50, 100 Bits, Spit Fire and Sonic Jab), the partner's FINISH gauge full
        void TestFight() { TestFight(1); }

        // dev tool: 10 more of every item in the Item Keeper's bank (ScriptState.Stored: one count per item number, saved with the game).
        // The bank has a count for each of the 128 items, so everything fits; a count never goes past 99 (0x63, keeperMoveOne's limit).
        void FillKeeperBank()
        {
            if ((_battle != null && _battle.Active) || _runner != null || _changing) { Say("Not now."); return; }
            if (_itemCatalog == null) _itemCatalog = ItemCatalog.Load(_disc);
            int filled = 0, full = 0;
            foreach (var it in _itemCatalog)
            {
                if (string.IsNullOrEmpty(it.Name) || it.Id < 0 || it.Id >= _state.Stored.Length) continue;
                int c = _state.Stored[it.Id];
                if (c >= 0x63) { full++; continue; }
                _state.Stored[it.Id] = (byte)Mathf.Min(0x63, c + 10);
                filled++;
            }
            Debug.Log("[debug] Item Keeper bank: +10 of " + filled + " items" + (full > 0 ? ", " + full + " already at 99" : "") + " (of " + _itemCatalog.Count + ")");
            Say(full > 0 ? "Item Keeper: +10 of " + filled + " items (" + full + " already at 99)." : "Item Keeper: +10 of every item (" + filled + ").");
        }

        // dev tool: the same with more Agumon at once;the extra ones join as in a scripted fight with several enemies (AddEnemy)
        void TestFight(int count)
        {
            if (_battle == null || _battle.Active || _runner != null || _player == null || _partner == null || _changing) { Say("Not now."); return; }
            if (_entries == null) _entries = new List<DigimonEntry>(DigimonCatalog.Load(_disc));
            if (_allTim == null) _allTim = _disc.ReadFile("/CHDAT/ALLTIM.TIM");
            DigimonEntry entry = null;
            foreach (var e in _entries) if (e.Type == 3) { entry = e; break; }
            if (entry == null) { Say("No Agumon model."); return; }
            Vector3 right = _cam != null ? _cam.transform.right : Vector3.right; right.y = 0f; right.Normalize();
            Vector3 up = Vector3.Cross(right, Vector3.up);
            var foes = new List<Npc>();
            for (int k = 0; k < count; k++)
            {
                var data = new MapDigimon { Type = 3, FollowMode = 12, Hp = 400, Mp = 300, Off = 30, Def = 30, Spd = 30, Brain = 50, Bits = 100 };
                data.MoveSlots[0] = 46; data.MoveSlots[1] = 51; data.MoveSlots[2] = 255; data.MoveSlots[3] = 255;
                data.MoveChance[0] = 50; data.MoveChance[1] = 50;
                var go = MmdModel.Parse(_disc.ReadFile(entry.MmdPath)).Build(entry, _allTim);
                go.transform.SetParent(transform, false);
                Vector3 side = k == 0 ? Vector3.zero : up * (k == 1 ? 2.5f : -2.5f);
                Vector3 at = _player.position - right * 5f + side;
                if (!Free(at)) at = _player.position + right * 5f + side;
                go.transform.position = at;
                go.transform.rotation = FaceRot(_player.position - at);
                var n = new Npc { Data = data, T = go.transform, Anim = go.GetComponent<DigimonAnimator>(), Name = entry.Name, Home = at };
                if (n.Anim != null) { n.Anim.DigimonType = 3; n.Anim.SetSlot(IdleSlot); }
                _npcs.Add(n);
                foes.Add(n);
            }
            _debugOpen = false;
            StartBattle(foes[0]);
            for (int k = 1; k < foes.Count; k++) _battle.AddEnemy(foes[k].T, foes[k].Anim, foes[k].Name, DigimonStatsCatalog.Get(_disc, 3), foes[k].Data);
            _battle.Partner.Finish = _battle.Partner.FinishMax;
        }

        // dev tool: a fight against the Digimon of a map's record (its own numbers, techniques and chances, read from the map file), its finishing gauge FULL at the start.
        // The finisher exists only when the technique of its record's slot 3 is one of 0x3A-0x70 (Geremon of FACT09, record 3, has Party time; Psychemon of GOMI01 has none).
        // Extra records of the same map join as more enemies (count).
        bool _condTough = true;
        int _condHpBack = -1;

        void TestFightRecord(string mapName, int record, int count = 1, bool fullGauge = true)
        {
            if (_battle == null || _battle.Active || _runner != null || _player == null || _partner == null || _changing) { Say("Not now."); return; }
            int mi = _mapNames.IndexOf(mapName);
            if (mi < 0) { Say(mapName + " not found."); return; }
            string path = MapCatalog.PathFor(_mapNames, mi);
            MapBackground bg;
            try { bg = MapBackground.Parse(_disc.ReadFile(path), _disc.ReadFile(path.Substring(0, path.Length - 4) + ".TFS")); }
            catch (System.Exception e) { Say("Map read failed: " + e.Message); return; }
            if (record < 0 || record >= bg.Npcs.Count) { Say("No record " + record + "."); return; }
            if (_entries == null) _entries = new List<DigimonEntry>(DigimonCatalog.Load(_disc));
            if (_allTim == null) _allTim = _disc.ReadFile("/CHDAT/ALLTIM.TIM");
            Vector3 right = _cam != null ? _cam.transform.right : Vector3.right; right.y = 0f; right.Normalize();
            Vector3 up = Vector3.Cross(right, Vector3.up);
            var foes = new List<Npc>();
            for (int k = 0; k < count; k++)
            {
                var data = bg.Npcs[Mathf.Min(record + k, bg.Npcs.Count - 1)];
                DigimonEntry entry = null;
                foreach (var e in _entries) if (e.Type == data.Type) { entry = e; break; }
                if (entry == null) { Say("No model for type " + data.Type + "."); return; }
                var go = MmdModel.Parse(_disc.ReadFile(entry.MmdPath)).Build(entry, _allTim);
                go.transform.SetParent(transform, false);
                Vector3 side = k == 0 ? Vector3.zero : up * (k == 1 ? 2.5f : -2.5f);
                Vector3 at = _player.position - right * 5f + side;
                if (!Free(at)) at = _player.position + right * 5f + side;
                go.transform.position = at;
                go.transform.rotation = FaceRot(_player.position - at);
                var n = new Npc { Data = data, T = go.transform, Anim = go.GetComponent<DigimonAnimator>(), Name = entry.Name, Home = at };
                if (n.Anim != null) { n.Anim.DigimonType = data.Type; n.Anim.SetSlot(IdleSlot); }
                _npcs.Add(n);
                foes.Add(n);
            }
            _debugOpen = false;
            StartBattle(foes[0]);
            for (int k = 1; k < foes.Count; k++) _battle.AddEnemy(foes[k].T, foes[k].Anim, foes[k].Name, DigimonStatsCatalog.Get(_disc, foes[k].Data.Type), foes[k].Data);
            if (!fullGauge)
            {
                // the condition fights: no finishers at the start (they would cut in), and the partner can last if asked
                if (_condTough) { _condHpBack = _state.Stats[6]; _battle.Partner.MaxHp = 9999; _battle.Partner.Hp = 9999; _battle.Partner.ShownHp = 9999; }
                Debug.Log("[testfight] " + foes[0].Name + " of " + mapName + " record " + record + ", HP " + _battle.Enemy.Hp);
                return;
            }
            _battle.Partner.Finish = _battle.Partner.FinishMax;
            foreach (var f in _battle.AllFightersPublic()) if (f.Finisher != null) f.Finish = f.FinishMax;
            Debug.Log("[testfight] " + foes[0].Name + " enemy finisher: " + (_battle.Enemy.Finisher != null ? _battle.Enemy.Finisher.Name : "none (its slot 3 is not a finishing move)"));
        }

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
            // a condition fight with the 9999 HP partner is over: its real HP comes back (the fight wrote the big number into the stats)
            if (_condHpBack >= 0 && _battle != null && !_battle.Active)
            {
                _state.Stats[6] = (ushort)Mathf.Min(_condHpBack, _state.Stats[4]);
                _condHpBack = -1;
            }
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
            if (GUILayout.Button("Test fight: Agumon (the first fight's numbers), FINISH ready")) { TestFight(); }
            if (GUILayout.Button("Test fight: two Agumon at once")) { TestFight(2); }
            if (GUILayout.Button("Test fight: Psychemon (GOMI01), finishing gauge full (Tremar, no finisher in the data)")) { TestFightRecord("GOMI01", 0); }
            if (GUILayout.Button("Test fight: Psychemon + two Geremon (Tremar hits the others too)")) { TestFightRecord("GOMI01", 0, 3); }
            if (GUILayout.Button("Test fight: Geremon of FACT09 (finisher Party time), gauge full")) { TestFightRecord("FACT09", 3); }
            // condition test fights: one strong enemy per condition, picked from the map records by its technique chances x status chances and its HP
            _condTough = GUILayout.Toggle(_condTough, "Condition fights: partner gets 9999 HP for the fight (ours, debug only)");
            if (GUILayout.Button("Conditions: POISON - Kuwagamon of TRAI00 (9000 HP; Poison Claw 40%, Poison Powder 62%)")) { TestFightRecord("TRAI00", 2, 1, false); }
            if (GUILayout.Button("Conditions: CONFUSION + stun - Airdramon of TWNA02 (4400 HP; Confused Storm 60%, Megalo Spark stun 23%)")) { TestFightRecord("TWNA02", 5, 1, false); }
            if (GUILayout.Button("Conditions: STUN - IceDevimon of MGEN11 (4600 HP; Ice Statue 38%, Giga Freeze 10%)")) { TestFightRecord("MGEN11", 2, 1, false); }
            if (GUILayout.Button("Conditions: FLAT - Giromon of FACT08A (5700 HP; Delete Program 50%, Reverse Prog 12%)")) { TestFightRecord("FACT08A", 0, 1, false); }
            if (GUILayout.Button("Curling: Penguinmon (FRZL17, variable 0x7A = 0)")) { TestCurling(0); }
            if (GUILayout.Button("Curling: MetalMamemon (FRZL17, variable 0x7A = 1)")) { TestCurling(1); }
            if (GUILayout.Button("Arena: go to the File City Arena (ROOM13)")) { TestArena(); }
            if (GUILayout.Button("Arena: start the Grade D Cup now (in ROOM13 / ROOM19 / BETL02)")) { TestTournament(0); }
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
            // getCardAmount reads the count (0-9) of each of the 66 cards; the old button only set the "seen" list, so the Card page stayed empty
            if (GUILayout.Button("Items: put 10 of every item in the Item Keeper bank")) FillKeeperBank();
            if (GUILayout.Button("Recycle shop: stock every item (5 each)")) { for (int i = 0; i < _state.Recycle.Length; i++) _state.Recycle[i] = 5; Say("The recycle shop has 5 of everything."); }
            if (GUILayout.Button("Every card (1 of each of the 66)")) { for (int i = 0; i < 66; i++) { if (_state.CardCount[i] == 0) _state.CardCount[i] = 1; _state.Cards[i] = true; } Say("1 of every card."); }
            // unlockMedal: trigger 0x16C + medal (the Medals page shows 15)
            if (GUILayout.Button("Every medal (flags 0x16C-0x17A)")) { for (int i = 0; i < 15; i++) _state.SetFlag(0x16C + i, true); Say("Every medal."); }

            GUILayout.Label("Time");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("+1 h")) _care.SkipHours(1);
            if (GUILayout.Button("+6 h")) _care.SkipHours(6);
            if (GUILayout.Button("+24 h")) _care.SkipHours(24);
            if (GUILayout.Button("Morning (8:00)")) _care.AdvanceToTime(8, 0);
            if (GUILayout.Button("Night (22:00)")) _care.AdvanceToTime(22, 0);
            GUILayout.EndHorizontal();
            GUILayout.Label("Care");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Hungry")) _state.Para.FoodLevel = 1;
            if (GUILayout.Button("Needs toilet")) _state.Para.PoopLevel = 1;
            if (GUILayout.Button("Accident soon")) _state.Para.PoopingTimer = 20;
            if (GUILayout.Button("Sick")) { _state.Para.Condition |= PartnerCare.Sick; _state.Para.SicknessTimer = 1; }
            if (GUILayout.Button("Injured")) _state.Para.Condition |= PartnerCare.Injured;
            if (GUILayout.Button("Tired")) _state.Para.Tiredness = 85;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Evolve now")) _state.Para.EvoTimer = 200;
            if (GUILayout.Button("Virus full")) _state.Para.Virus = 16;
            if (GUILayout.Button("Lifespan 2 h")) _state.Para.Lifetime = 2;
            if (GUILayout.Button("Lose a life")) { _state.Life = Mathf.Max(0, _state.Life - 1); _care.WantLifeLost = true; }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Happiness -20")) _state.Happiness -= 20;
            if (GUILayout.Button("Happiness +20")) _state.Happiness += 20;
            if (GUILayout.Button("Discipline -20")) _state.Discipline -= 20;
            if (GUILayout.Button("Discipline +20")) _state.Discipline += 20;
            GUILayout.EndHorizontal();
            _care.Boundaries();

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
