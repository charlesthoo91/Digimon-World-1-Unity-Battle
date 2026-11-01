using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DW1Remake.Game
{
    /// <summary>
    /// Playable rooms: loads a map from the player's disc, reproduces the game's own camera,
    /// puts a Digimon on it and lets you walk around inside the game's walking grid.
    /// Walking onto an exit loads the connected map. Everything is built from the disc in memory.
    /// </summary>
    public sealed partial class PlayableRoom : MonoBehaviour
    {
        [Header("What to load")]
        public string DiscPath = "";
        [MapPath] public string MapPath = "/MAP/MAP1/MAYO01.MAP";   // the map you start in
        public int DigimonType = 3;            // 3 = Agumon
        public int Palette = 0;                // used only when Auto Clock is off: 0 day, 1 evening, 2 night

        [Header("Partner")]
        public bool HasPartner = true;         // a second Digimon that follows you
        [Tooltip("Scripted animations (Tokomon's jump, Poyomon's flip) also move the whole body. Untick if it looks wrong.")] public bool RootMotion = true;
        [Range(0f, 1f), Tooltip("How dark the little shadow pieces under map Digimon are (PS1: half transparent).")] public float ShadowPieceOpacity = 0.5f;
        public int PartnerType = 3;            // 3 = Agumon
        [Tooltip("Opening: moves the partner this far (meters, negative = towards the camera) from where the script's 'place' command puts him. -3.5 matches the PS1 game.")] public float PartnerPlaceZOffset = -3.5f;
        public float PartnerDistance = 3f;     // how far behind you it walks (metres)

        [Header("Foreground layers")]
        public bool ShowOverlays = true;       // tree tops, rock edges... that hide the Digimon when it walks behind them (F2 to switch)

        [Header("Time of day")]
        public bool AutoClock = true;          // the game clock runs and the map changes day / evening / night
        public float StartHour = 8f;           // the original game starts at 08:00
        public float ClockMinutesPerSecond = 1f;   // the original: 1 game minute = 20 frames = 0.67 s

        [Header("Feel")]
        public float WalkSpeed = 3f;           // metres per second (a tile is 1 metre)
        public float TurnSpeed = 720f;         // degrees per second
        public float FacingOffset = 0f;        // set to 180 if the Digimon walks backwards
        public int IdleSlot = 0;
        public int WalkSlot = 2;
        const int RunSlot = 4, PlayerRunSlot = 3;
        public float WindowHeightPx = 240f;    // how much of the painted map is visible vertically
        public float FadeSeconds = 0.25f;      // screen fade when changing map

        [Header("HD-2D look (tick off anything you don't like)")]
        public bool Hd2D = true;               // master switch for the look below
        public bool UseMapLights = true;       // light the Digimon with the map's own 3 lights
        public bool InvertLightDirection = false;
        public bool AutoOrientLights = true;   // always light the side of the Digimon that the camera sees
        public float LightIntensity = 2f;
        [Range(0f, 1f)] public float Ambient = 1f;        // brightness of the parts of the Digimon that face away from the lights
        public float CharacterBrightness = 1f;            // overall Digimon brightness (1 = the texture's own colours)
        public float TiltShiftBlur = 1f;       // blur at the top/bottom of the screen (0 = off)
        [Range(0f, 1f)] public float FocusCenter = 0.5f;
        [Range(0f, 0.5f)] public float FocusHalfHeight = 0.22f;
        public float Sharpen = 0.5f;
        public float Bloom = 0.7f;             // glow
        public float Vignette = 0.3f;          // darker corners
        public float Saturation = 10f;         // colour boost
        public float Contrast = 8f;
        [Range(0f, 1f)] public float ShadowOpacity = 0.6f;

        const float Unit = 0.01f;              // game units -> metres
        const float BackdropDistance = 300f;

        FileStream _fs;
        DiscImage _disc;
        List<string> _mapNames = new List<string>();
        List<byte> _mapFlags = new List<byte>();
        List<byte> _num8 = new List<byte>(), _num4 = new List<byte>();
        MapOverlay _overlay;
        GameObject _overlayRoot;
        Mesh _quadMesh;
        readonly Dictionary<int, Material> _ovMat = new Dictionary<int, Material>();
        readonly Dictionary<int, Texture2D> _ovTex = new Dictionary<int, Texture2D>();
        readonly List<OvAnim> _ovAnims = new List<OvAnim>();
        float _ovAcc;

        sealed class OvAnim
        {
            public Transform T; public MeshRenderer R; public MapOverlay.Inst I; public int Cur, Timer; public float Dm;
        }
        float _clock = -1f;                    // hours, 0..24; a copy of the game clock in _care
        float _clockFrames;                    // time towards the next game frame (20 a second)
        PartnerCare _care;
        int _curPalette;
        float _lightFactor = 1f;
        readonly List<Light> _lightList = new List<Light>();
        readonly List<float> _lightBase = new List<float>();
        MapScripts _scripts;
        int _mapId;
        MapBackground _map;
        Camera _cam;
        Transform _player;
        DigimonAnimator _anim;
        Transform _shadow, _shadow2;
        Transform _partner;
        DigimonAnimator _partnerAnim;
        GameObject _backdrop;
        readonly List<GameObject> _mapLights = new List<GameObject>();
        Vector3 _vpG, _fG, _rG, _dG;           // camera position and axes in game space
        float _h;                              // focal length in map pixels
        Vector2 _shiftSign = Vector2.one;
        bool _calibrated;
        bool _changing, _armed;
        GameObject _gridOverlay;
        bool _showGrid;
        float _fade;
        string _message = "";
        float _messageUntil;
        string _error = "";

        static Vector3 ToGame(Vector3 u) { return new Vector3(u.x / Unit, -u.y / Unit, u.z / Unit); }
        static Vector3 ToUnity(Vector3 g) { return new Vector3(g.x * Unit, -g.y * Unit, g.z * Unit); }

        void Start()
        {
            try { Load(); }
            catch (Exception e) { _error = e.Message; Debug.LogException(e); }
        }

        void OnDestroy()
        {
            if (_fs != null) _fs.Dispose();
        }

        void Load()
        {
            if (!File.Exists(DiscPath)) throw new FileNotFoundException("Disc not found. Use DW1 Remake > Disc Browser > Choose disc first, then recreate the room.");
            _fs = new FileStream(DiscPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            _disc = new DiscImage(_fs);
            _mapNames = MapCatalog.Load(_disc);
            _mapFlags = MapCatalog.LoadFlags(_disc);
            MapCatalog.LoadImageCounts(_disc, out _num8, out _num4);
            _clock = StartHour;
            _scripts = MapScripts.Load(_disc);
            var catalog = ItemCatalog.Load(_disc);
            _menus = new GameMenus(_state, catalog);
            _menus.Icons = ItemIcons.Load(_disc);
            _battle = new Battle(_state, FaceRot);
            _battle.Art = BattleArt.Load(_disc);
            _battle.ItemName = i => i >= 0 && i < catalog.Count ? catalog[i].Name : "";
            _battle.OnOpenBag = () => _menus.OpenBag();
            _battle.Icons = BattleIcons.Load(_disc);
            _menus.BIcons = _battle.Icons;
            _menus.Faces = Portraits.Load(_disc);
            _menus.PartnerInfo = DigimonStatsCatalog.Get(_disc, PartnerType);
            if (_menus.PartnerInfo != null && _menus.PartnerInfo.Learnable.Count > 0)
            {
                int first = _menus.PartnerInfo.Learnable[0];       // Agumon starts with Spit Fire, learned and equipped
                _state.Techs[first] = true;
                if (!_state.TechSet.Contains(first)) _state.TechSet.Add(first);
            }
            _state.Raised[PartnerType] = true;
            _care = new PartnerCare(_state, t => DigimonStatsCatalog.RaiseOf(_disc, t), c => DigimonStatsCatalog.SleepOf(_disc, c), t => DigimonStatsCatalog.Get(_disc, t).Level);
            _care.OnNewDay = () => _dayCount++;
            _care.EvoItemTarget = i => DigimonStatsCatalog.EvoItemTarget(_disc, i);
            _care.ReqOf = t => DigimonStatsCatalog.ReqOf(_disc, t);
            _care.GainOf = t => DigimonStatsCatalog.GainOf(_disc, t);
            _care.InfoOf = t => DigimonStatsCatalog.Get(_disc, t);
            _care.TechOf = DigimonStatsCatalog.TechOf;
            _menus.OnCommand = CareCommand;
            _menus.OnFeed = FeedItem;
            _menus.InBattle = () => _battle != null && _battle.Active;
            _menus.CanSleep = () => (_state.Para.Condition & PartnerCare.Sleepy) != 0;
            _care.SetClock((int)StartHour, 0);
            _clock = _care.ClockHours;
            _care.InitPartner(PartnerType);
            _menus.Art = MenuArt.Load(_disc);
            _menus.Disc = _disc;
            _menus.NeedBoyPicture = BuildBoyPicture;
            _battle.Items = _menus.Icons;
            _menus.PlayerName = PlayerNameText; _menus.PartnerName = PartnerNameText;
            _state.SpeciesNameOf = t =>
            {
                if (_entries == null) _entries = new List<DigimonEntry>(DigimonCatalog.Load(_disc));
                foreach (var e in _entries) if (e.Type == t) return e.Name;
                return "";
            };
            var itemList = ItemCatalog.Load(_disc);
            _state.ItemNameOf = id => id >= 0 && id < itemList.Count ? itemList[id].Name : "";
            _state.TimeValue = a => a == 0x69 ? 1 : a == 0x6A ? _dayCount + 1 : a == 0x6B ? (int)_clock : (int)((_clock % 1f) * 60f);

            SaveGameData sv = null; bool loadSave = false;
            if (!NewGame && LoadSaveOnStart) { sv = ReadSave(); loadSave = sv != null; }
            if (NewGame) DigimonType = 0;       // the human boy
            else if (QuickStart || loadSave) { DigimonType = 0; HasPartner = true; }
            DigimonEntry entry = null;
            foreach (var e in DigimonCatalog.Load(_disc)) if (e.Type == DigimonType) { entry = e; break; }
            if (entry == null) throw new InvalidDataException("Digimon type " + DigimonType + " not found.");
            byte[] mmd = _disc.ReadFile(entry.MmdPath);
            byte[] allTim = _disc.ReadFile("/CHDAT/ALLTIM.TIM");

            // --- the Digimon (built once, kept when the map changes)
            DigimonAnimator.UseRootMotion = RootMotion;
            MmdModel.UseLit = Hd2D && UseMapLights;
            MmdModel.Brightness = Hd2D && UseMapLights ? CharacterBrightness : 1f;
            var model = MmdModel.Parse(mmd);
            var go = model.Build(entry, allTim);
            go.transform.SetParent(transform, false);
            _player = go.transform;
            _playerName = entry.Name;
            _anim = go.GetComponent<DigimonAnimator>();
            MmdModel.UseLit = false;
            MmdModel.Brightness = 1f;

            if (HasPartner) BuildPartner();

            if (NewGame)
            {
                int ti = _mapNames.IndexOf("TOPN01");        // the opening takes place here
                if (ti >= 0) MapPath = MapCatalog.PathFor(_mapNames, ti);
                _state.SetFlag(54, true); _state.SetFlag(55, true);      // the same flags as the game's own "Opening mode"
            }
            else if (loadSave)
            {
                ApplySave(sv);
                int mi = _mapNames.IndexOf(sv.Map);
                if (mi >= 0) MapPath = MapCatalog.PathFor(_mapNames, mi);
            }
            else if (QuickStart)
            {
                QuickStartState();
                int ti = _mapNames.IndexOf("TWNA01");
                if (ti >= 0) MapPath = MapCatalog.PathFor(_mapNames, ti);
            }
            else foreach (var f in InitialFlags) _state.SetFlag(f, true);
            LoadMap(MapPath, NewGame ? 9 : (QuickStart && !loadSave) ? 2 : -1);
            if (NewGame) BeginNewGame();
            else if (loadSave) PlaceFromSave(sv);
            else if (QuickStart && _partner != null) { _partner.gameObject.SetActive(true); PlacePartner(_player.position, Vector3.back); }
            if (Hd2D) SetupHd2DOnce();
        }

        // ------------------------------------------------------------------ map Digimon (NPCs)

        sealed class Npc
        {
            public MapDigimon Data; public Transform T; public DigimonAnimator Anim; public string Name;
            public List<Vector3> Path = new List<Vector3>(); public int Next; public float Wait;
        }
        readonly List<Npc> _npcs = new List<Npc>();
        string _playerName = "", _partnerName = "";
        [Tooltip("Show each Digimon's name above it (F3 toggles).")] public bool ShowNames = true;
        List<DigimonEntry> _entries;
        byte[] _allTim;
        Npc _talkingTo;
        readonly ScriptState _state = new ScriptState();     // story flags and variables
        [Tooltip("Story flags that are on at the start of a game. Flag 54 = the early-game state (no debug Digimon in town).")]
        public int[] InitialFlags = { 54 };
        [Tooltip("Start like a real new game: you arrive in File City (TWNA01) as the boy, the Digimon gather round and the opening scene plays. Tick it before pressing Play.")]
        public bool NewGame = false;
        bool _intro, _spawnAll;
        int _arrive = 255;
        ScriptRunner _runner; string _line; List<string> _choiceOpts; int _choiceSel; bool _scriptAny;

        // ------------------------------------------------------------------ cutscene commands (walk, turn, camera, wait)

        sealed class Mover
        {
            public Transform T; public DigimonAnimator A;
            public Vector3 Goal; public bool Walking; public float Still;
            public bool HasTurn; public Quaternion TurnTo;
        }
        readonly Dictionary<Transform, Mover> _movers = new Dictionary<Transform, Mover>();
        readonly HashSet<DigimonAnimator> _animated = new HashSet<DigimonAnimator>();   // Digimon the script put into a special animation
        float _wait; bool _waitAll;
        bool _camOn; Vector3 _camPos, _camGoal; float _camSpeed;
        const float ActorSpeed = 4f;      // metres per second for scripted walks

        // actor 0xFD = the player, 0xFC = the partner, anything else = the map Digimon with that script number
        bool ActorOf(int id, out Transform t, out DigimonAnimator a)
        {
            t = null; a = null;
            if (id == 0xFD) { t = _player; a = _anim; }
            else if (id == 0xFC) { t = _partner; a = _partnerAnim; }
            else foreach (var n in _npcs) if (n.Data.Script == id) { t = n.T; a = n.Anim; break; }
            return t != null && t.gameObject.activeInHierarchy;
        }

        Mover MoverFor(Transform t, DigimonAnimator a)
        {
            Mover m;
            if (!_movers.TryGetValue(t, out m)) { m = new Mover { T = t, A = a }; _movers[t] = m; }
            return m;
        }

        // the rotation that makes a Digimon look along a ground direction (the model's front is -Z)
        Quaternion FaceRot(Vector3 dir)
        {
            dir.y = 0;
            if (dir.sqrMagnitude < 1e-4f) return Quaternion.identity;
            return Quaternion.LookRotation(-dir.normalized) * Quaternion.Euler(0, FacingOffset, 0);
        }

        bool ActorsBusy()
        {
            return !_syncBit || _movers.Count > 0 || (_camOn && !_pBusy && (_camPos - _camGoal).sqrMagnitude > 0.0004f);
        }

        // runs one cutscene command; true = the script must wait here
        bool ExecCmd(ScriptAction a)
        {
            Transform t, t2; DigimonAnimator an, an2;
            switch (a.Cmd)
            {
                case 0x49: DigimonRoutine(a.Frames); break;
                case 0x64:
                    // 0x64 n = run built-in function n (variable 254 = its parameter, usually an actor)
                    if (a.Frames == 0x37)   // ends the endless lying loop of the actor (the boy waking up)
                    {
                        if (ActorOf(a.Actor, out t, out an) && an != null) { Debug.Log("[anim] 0x64 releases the loop of actor 0x" + a.Actor.ToString("X")); an.ReleaseLoop(); }
                    }
                    else if (a.Frames == 0x1D) { _eggOpen = true; _eggSlot = 0; return true; }   // "Which egg do you want?" (script 212)
                    else if (a.Frames == 8 && _menus != null)   // the shop screen; the script switched on flag 0x180 + item for each item it sells
                    {
                        _menus.OpenShop(i => _state.GetFlag(0x180 + i));
                        _shopWait = true;
                        return true;
                    }
                    break;
                case 0x66:   // the fight: enemy = actor in script variable 251 (unset = the Digimon being talked to); Frames = variable 250 (1 = no running away)
                {
                    Npc foe = null;
                    if (a.Actor != 0 && a.Actor != 0xFF) foreach (var n in _npcs) if (n.Data.Script == a.Actor && n.T != null) { foe = n; break; }
                    if (foe == null) foe = _talkingTo;
                    if (foe != null && foe.T != null && _battle != null && !_battle.Active)
                    {
                        _line = null; _choiceOpts = null; _battleWait = true;
                        StartBattle(foe, a.Frames != 0);
                        return true;
                    }
                    break;
                }
                case 0x48:   // an actor leaves the map
                    foreach (var n in _npcs) if (n.Data.Script == a.Actor && n.T != null) { n.T.gameObject.SetActive(false); break; }
                    break;
                case 0x67: _wait = a.Frames / 20f; return _wait > 0f;
                case 0x4A: _waitAll = ActorsBusy(); return _waitAll;
                case 0x4C:
                    if (ActorOf(a.Actor, out t, out an) && ActorOf(a.Target, out t2, out an2))
                    { var m = MoverFor(t, an); m.HasTurn = true; m.TurnTo = FaceRot(t2.position - t.position); }
                    break;
                case 0x4D:
                    if (ActorOf(a.Actor, out t, out an))
                    {
                        float ang = a.Angle * 2f * Mathf.PI / 4096f;
                        var m = MoverFor(t, an); m.HasTurn = true; m.TurnTo = FaceRot(new Vector3(-Mathf.Sin(ang), 0, -Mathf.Cos(ang)));
                    }
                    break;
                case 0x4E:
                    if (ActorOf(a.Actor, out t, out an))
                    {
                        var m = MoverFor(t, an); m.Goal = new Vector3(a.X * Unit, 0, a.Z * Unit); m.Walking = true; m.Still = 0f;
                        if (an != null) an.SetSlot(WalkSlot);
                    }
                    break;
                case 0x6C:
                    if (ActorOf(a.Actor, out t, out an))
                    {
                        Debug.Log("[place] actor 0x" + a.Actor.ToString("X") + " -> (" + (a.X * Unit).ToString("F1") + ", " + (a.Z * Unit).ToString("F1") + "), player is at (" + _player.position.x.ToString("F1") + ", " + _player.position.z.ToString("F1") + ")");
                        float zFix = a.Actor == 0xFC ? PartnerPlaceZOffset : 0f;   // measured against the PS1 game: Agumon starts further from the boy than the script's number
                        t.position = new Vector3(a.X * Unit, 0, a.Z * Unit + zFix);
                        _movers.Remove(t);
                    }
                    break;
                case 0x4F:
                {
                    if (!_camOn) { _camOn = true; _camPos = _player.position; }
                    _camGoal = new Vector3(a.X * Unit, 0, a.Z * Unit);
                    float secs = Mathf.Max(0.1f, a.Frames / 20f);
                    _camSpeed = (_camGoal - _camPos).magnitude / secs;
                    break;
                }
                case 0x56:
                    // the number is the animation slot of that Digimon (0 = idle, 2 = walk, ~29 = talking...); ignored if the model has no such slot
                    Debug.Log("[anim] actor 0x" + a.Actor.ToString("X") + " slot " + a.Target + (ActorOf(a.Actor, out t, out an) && an != null ? " (model has " + an.SlotCount + " slots, has this slot: " + an.HasSlot(a.Target) + ", busy: " + an.Busy + ", walking: " + (_movers.ContainsKey(t) && _movers[t].Walking) + ")" : " - actor not found"));
                    if (ActorOf(a.Actor, out t, out an) && an != null && !(_movers.ContainsKey(t) && _movers[t].Walking)) { an.QueueSlot(a.Target, a.Target != 0 && a.Target != 2); _animated.Add(an); }
                    break;
            }
            return false;
        }

        void TickActors()
        {
            if (_camOn) _camPos = Vector3.MoveTowards(_camPos, _camGoal, _camSpeed * Time.deltaTime);
            if (_movers.Count == 0) return;
            var list = new List<Mover>(_movers.Values);
            foreach (var m in list)
            {
                if (m.T == null) { _movers.Remove(m.T); continue; }
                if (m.Walking)
                {
                    // tickEntityWalkTo: it turns straight to the goal, its walk carries it step by step, and it stops on the goal's tile
                    Vector3 to = m.Goal - m.T.position; to.y = 0;
                    float step = m.A != null ? m.A.TakeForward() : ActorSpeed * Time.deltaTime;
                    m.Still = step > 0f ? 0f : m.Still + Time.deltaTime;
                    if (m.Still > 0.5f) step = ActorSpeed * Time.deltaTime;     // an animation with no forward movement must not hang the script
                    if (TileOf(m.T.position) == TileOf(m.Goal) || to.sqrMagnitude < 0.0001f) { m.Walking = false; if (m.A != null) m.A.SetSlot(IdleSlot); }
                    else
                    {
                        m.T.rotation = FaceRot(to);
                        m.T.position += to.normalized * step;
                    }
                }
                else if (m.HasTurn)
                {
                    m.T.rotation = Quaternion.RotateTowards(m.T.rotation, m.TurnTo, 540f * Time.deltaTime);
                    if (Quaternion.Angle(m.T.rotation, m.TurnTo) < 1f) m.HasTurn = false;
                }
                if (!m.Walking && !m.HasTurn) _movers.Remove(m.T);
            }
        }

        // a script ended (or changed map): stop scripted movement and give the camera back to the player
        void EndCutscene()
        {
            foreach (var m in _movers.Values) if (m.A != null) m.A.SetSlot(IdleSlot);
            foreach (var an in _animated) if (an != null) an.SetSlot(IdleSlot);
            _animated.Clear();
            _movers.Clear(); _camOn = false; _wait = 0f; _waitAll = false;
        }

        // the opening: the real script runs in TOPN01, goes to Jijimon's house, then out to File City
        void BeginNewGame()
        {
            _state.Vars[103] = (byte)PartnerType;   // the opening picks the partner's starting stats by this (3 = Agumon)
            // first the character setup (Jijimon's questions and the name screens), then the opening script
            _setup = new CharacterSetup(_scripts != null ? _scripts.GetDgEntry(0) : null, _menus);
            _setup.DrawBackground = !BuildSetupStage();
            _setup.OnDone = () => { DestroySetupStage(); _intro = true; if (!StartScript(54, null)) FinishIntro(); };
            _setup.Start();
        }

        // ---- the black stage with Jijimon during the character setup
        GameObject _stageCam, _stageJiji;

        bool BuildSetupStage()
        {
            try
            {
                if (_entries == null) _entries = new List<DigimonEntry>(DigimonCatalog.Load(_disc));
                if (_allTim == null) _allTim = _disc.ReadFile("/CHDAT/ALLTIM.TIM");
                DigimonEntry je = null;
                foreach (var e in _entries) if (e.Name == "Jijimon") { je = e; break; }
                if (je == null) return false;
                bool lit = MmdModel.UseLit; float br = MmdModel.Brightness;
                MmdModel.UseLit = false; MmdModel.Brightness = 1f;
                var go = MmdModel.Parse(_disc.ReadFile(je.MmdPath)).Build(je, _allTim);
                MmdModel.UseLit = lit; MmdModel.Brightness = br;
                const int layer = 31;
                foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
                Vector3 origin = new Vector3(5000f, 0f, 0f);
                go.transform.position = origin + new Vector3(0.77f, -1.44f, 0f);
                go.transform.localScale = Vector3.one * 0.35f;
                var an = go.GetComponent<DigimonAnimator>();
                if (an != null) an.SetSlot(IdleSlot);
                _stageJiji = go;
                _stageCam = new GameObject("SetupCam");
                var cam = _stageCam.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
                cam.cullingMask = 1 << layer; cam.fieldOfView = 30f;
                cam.depth = (_cam != null ? _cam.depth : 0f) + 50f;
                cam.transform.position = origin + new Vector3(0f, 0f, -6f);
                cam.transform.rotation = Quaternion.identity;
                var ad = cam.GetUniversalAdditionalCameraData();
                if (ad != null) ad.renderPostProcessing = false;
                return true;
            }
            catch (System.Exception ex) { Debug.LogWarning("Setup stage failed: " + ex.Message); return false; }
        }

        void DestroySetupStage()
        {
            if (_stageCam != null) Destroy(_stageCam);
            if (_stageJiji != null) Destroy(_stageJiji);
            _stageCam = null; _stageJiji = null;
        }

        void FinishIntro()
        {
            if (!_intro) return;
            _intro = false;
            _state.SetFlag(55, false); _state.SetFlag(54, true);
            if (_partner != null) { _partner.gameObject.SetActive(true); PlacePartner(_player.position, Vector3.back); }
            Debug.Log("[new game] opening finished - you can walk around.");
        }

        // starts a map script section (NPC talk, door trigger). False if it produced nothing.
        bool StartScript(int section, Npc who)
        {
            if (_scripts == null || !_scripts.Available || _mapId < 0) return false;
            var log = new List<string>();
            _runner = new ScriptRunner(_scripts, _state, _scripts.ScriptIdForMap(_mapId), section, log);
            _talkingTo = who; _scriptAny = false; _line = null; _choiceOpts = null;
            if (_anim != null) _anim.SetSlot(IdleSlot);
            AdvanceScript();
            foreach (var l in log) Debug.Log("[script section " + section + " in " + _mapNames[_mapId] + "] " + l);
            return _scriptAny || _runner != null;
        }

        // moves the running script to its next text box, menu or map change
        void AdvanceScript()
        {
            while (_runner != null)
            {
                var a = _runner.Next();
                if (a == null) { _runner = null; _line = null; _choiceOpts = null; _talkingTo = null; EndCutscene(); if (_intro) FinishIntro(); return; }
                if (a.Cmd >= 0) { if (ExecCmd(a)) return; continue; }
                if (a.Text != null) { _line = a.Text; _speaker = a.Speaker; _choiceOpts = null; _scriptAny = true; return; }
                if (a.Options != null) { _choiceOpts = a.Options; _choiceSel = 0; _scriptAny = true; return; }
                if (a.Map >= 0 && DoChangeMap(a)) { _scriptAny = true; _runner = null; _line = null; _choiceOpts = null; _talkingTo = null; return; }
            }
        }

        bool DoChangeMap(ScriptAction a)
        {
            if (a.Map < 0 || a.Map >= _mapNames.Count || a.Exit > 9) return false;
            string next = MapCatalog.PathFor(_mapNames, a.Map);
            if (!_disc.Exists(next) || !_disc.Exists(next.Substring(0, next.Length - 4) + ".TFS")) return false;
            Debug.Log("Script: " + _mapNames[_mapId] + " -> " + _mapNames[a.Map] + " exit " + a.Exit);
            EndCutscene();
            _arrive = a.Arrive; StartCoroutine(ChangeMap(next, a.Exit));
            return true;
        }

        void ClearNpcs()
        {
            foreach (var n in _npcs) if (n.T != null) Destroy(n.T.gameObject);
            _npcs.Clear(); _runner = null; _line = null; _choiceOpts = null; _talkingTo = null;
            _movers.Clear(); _camOn = false; _wait = 0f; _waitAll = false; _animated.Clear();
        }

        void BuildNpcs()
        {
            ClearNpcs();
            if (_map == null || _map.Npcs == null) return;
            if (_entries == null) _entries = new List<DigimonEntry>(DigimonCatalog.Load(_disc));
            if (_allTim == null) _allTim = _disc.ReadFile("/CHDAT/ALLTIM.TIM");
            // the map's own script says which of its Digimon are active, depending on the story flags
            var active = new HashSet<int>();
            if (_scripts != null && _scripts.Available && _mapId >= 0)
            {
                var log = new List<string>();
                var r = new ScriptRunner(_scripts, _state, 0, _mapId, log);
                for (int guard = 0; guard < 50; guard++) { var a = r.Next(); if (a == null || a.Options != null) break; }
                foreach (var sp in r.Spawns) active.Add(sp[1]);
                foreach (var l in log) Debug.Log("[map setup " + _mapNames[_mapId] + "] " + l);
            }
            for (int ni = 0; ni < _map.Npcs.Count; ni++)
            {
                if (!active.Contains(ni) && !_spawnAll) continue;
                var d = _map.Npcs[ni];
                DigimonEntry entry = null;
                foreach (var e in _entries) if (e.Type == d.Type) { entry = e; break; }
                if (entry == null) { Debug.LogWarning("[npcs] " + _mapNames[_mapId] + " #" + ni + ": Digimon type " + d.Type + " has no model entry - not shown."); continue; }
                GameObject go;
                try
                {
                    MmdModel.UseLit = Hd2D && UseMapLights;
                    MmdModel.Brightness = Hd2D && UseMapLights ? CharacterBrightness : 1f;
                    go = MmdModel.Parse(_disc.ReadFile(entry.MmdPath)).Build(entry, _allTim);
                }
                catch (System.Exception ex) { Debug.LogWarning("NPC " + entry.Name + ": " + ex.Message); continue; }
                finally { MmdModel.UseLit = false; MmdModel.Brightness = 1f; }
                go.transform.SetParent(transform, false);
                var n = new Npc { Data = d, T = go.transform, Anim = go.GetComponent<DigimonAnimator>(), Name = entry.Name };
                Vector3 p = ToUnity(d.Pos); p.y = 0;
                go.transform.position = p;
                float a = d.RotY * 2f * Mathf.PI / 4096f;
                go.transform.rotation = Quaternion.LookRotation(new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a))) * Quaternion.Euler(0, FacingOffset, 0);
                if (d.FollowMode != 1)
                    foreach (var w in d.Waypoints) { Vector3 q = ToUnity(w); q.y = 0; n.Path.Add(q); }
                if (n.Anim != null) n.Anim.SetSlot(IdleSlot);
                _npcs.Add(n);
            }
            var sb = new System.Text.StringBuilder();
            foreach (var n in _npcs) sb.Append(n.Name).Append(" (").Append(n.T.position.x.ToString("0.0")).Append(", ").Append(n.T.position.z.ToString("0.0")).Append(")  ");
            _battleCooldown = Mathf.Max(_battleCooldown, 2f);   // no fight in the first seconds after arriving
            Debug.Log("[npcs] " + _mapNames[_mapId] + ": " + _npcs.Count + " of " + _map.Npcs.Count + " shown: " + sb);
        }

        void UpdateNpcs()
        {
            foreach (var n in _npcs)
            {
                if (n.T == null || n == _talkingTo || n == _battleNpc) continue;
                if (ChaseScripted(n)) continue;
                if (n.Data.FollowMode == 16 && Hostile(n)) { if (n.Anim != null && n.Anim.Slot != IdleSlot) n.Anim.SetSlot(IdleSlot); continue; }   // it only ever runs at you, it never strolls
                if (n.Path.Count == 0) continue;
                if (n.Wait > 0) { n.Wait -= Time.deltaTime; if (n.Anim != null && n.Anim.Slot != IdleSlot) n.Anim.SetSlot(IdleSlot); continue; }
                Vector3 pos = n.T.position, to = n.Path[n.Next] - pos; to.y = 0;
                if (to.magnitude < 0.1f) { n.Next = (n.Next + 1) % n.Path.Count; n.Wait = 1f + (n.Next % 3); continue; }
                Vector3 dir = to.normalized;
                n.T.position = pos + dir * (n.Anim != null ? n.Anim.TakeForward() : 1.5f * Time.deltaTime);
                n.T.rotation = Quaternion.RotateTowards(n.T.rotation, Quaternion.LookRotation(-dir) * Quaternion.Euler(0, FacingOffset, 0), TurnSpeed * Time.deltaTime);
                if (n.Anim != null && n.Anim.Slot != WalkSlot) n.Anim.SetSlot(WalkSlot);
            }
        }

        bool BlockedByNpc(Vector3 u)
        {
            foreach (var n in _npcs)
                if (n.T != null && n.Data.FollowMode == 1)
                {
                    float dx = u.x - n.T.position.x, dz = u.z - n.T.position.z;
                    if (dx * dx + dz * dz < 0.6f * 0.6f) return true;
                }
            return false;
        }

        // talk to the closest friendly Digimon in front of you
        void TryTalk()
        {
            Npc best = null; float bd = 2f;
            foreach (var n in _npcs)
            {
                if (n.T == null || n.Data.FollowMode != 1) continue;
                float d = Vector3.Distance(n.T.position, _player.position);
                if (d < bd) { bd = d; best = n; }
            }
            if (best == null) return;
            if (_scripts == null || !_scripts.Available) return;
            if (!StartScript(best.Data.Script, best)) { Say("(" + best.T.name + " has nothing to say - script " + best.Data.Script + " is an event that is not supported yet)"); return; }
            Vector3 look = _player.position - best.T.position; look.y = 0;
            if (look.sqrMagnitude > 0.01f)
                best.T.rotation = Quaternion.LookRotation(-look.normalized) * Quaternion.Euler(0, FacingOffset, 0);
            if (best.Anim != null) best.Anim.SetSlot(IdleSlot);
        }

        // ------------------------------------------------------------------ battles with wild Digimon

        Battle _battle; Npc _battleNpc; float _battleCooldown; Vector3 _battleFocus;

        // tickPartnerWaypoints: a partner left far off screen is put on the first off-screen tile on the line from the boy
        void BringPartnerNear()
        {
            if (_partner == null || !Outside(_partner.position, 160f, 120f)) return;
            Vector2Int from = TileOf(_player.position), to = TileOf(_partner.position);
            int dist = Mathf.Max(Mathf.Abs(to.x - from.x), Mathf.Abs(to.y - from.y));
            for (int i = 1; i <= dist; i++)
            {
                var tile = new Vector2Int(from.x + (to.x - from.x) * i / dist, from.y + (to.y - from.y) * i / dist);
                if (!Outside(TileMiddle(tile), 200f, 160f)) continue;
                _partner.position = TileMiddle(tile);
                break;
            }
            _way.Clear();
        }
        readonly List<Vector2Int> _tamerWay = new List<Vector2Int>();     // the way back to where he came in, when a wall hides it
        Vector2Int _tamerStart, _tamerPrev; int _tamerActive;

        // tickTamerWaypoints: he notes a tile only when he loses sight of the last one, and drops those a shortcut makes useless
        void TickTamerWay()
        {
            Vector2Int tile = TileOf(_player.position);
            if (tile != _tamerPrev)
            {
                if (!LineBlocked(tile.x, tile.y, _tamerStart.x, _tamerStart.y)) { _tamerWay.Clear(); _tamerActive = 0; }
                else if (_tamerWay.Count == 0 || LineBlocked(tile.x, tile.y, _tamerWay[_tamerWay.Count - 1].x, _tamerWay[_tamerWay.Count - 1].y))
                {
                    if (_tamerWay.Count == 30) _tamerWay.RemoveAt(0);
                    _tamerWay.Add(_tamerPrev);
                }
            }
            if (_tamerWay.Count > 1)
            {
                _tamerActive = Mathf.Clamp(_tamerActive, 0, _tamerWay.Count - 1);
                Vector2Int w = _tamerWay[_tamerActive];
                if (!LineBlocked(tile.x, tile.y, w.x, w.y))
                {
                    _tamerWay.RemoveRange(_tamerActive + 1, _tamerWay.Count - _tamerActive - 1);
                    _tamerActive = _tamerWay.Count > 1 ? _tamerWay.Count - 2 : _tamerWay.Count - 1;
                }
                else if (--_tamerActive < 0) _tamerActive = _tamerWay.Count - 2;
            }
            _tamerPrev = tile;
        }
        bool _shopWait, _battleWait;

        // walking into a wild Digimon (follow mode 12) starts a fight
        void TryStartBattle()
        {
            if (_battle == null || _partner == null || !_partner.gameObject.activeInHierarchy || _intro) return;
            foreach (var n in _npcs)
            {
                if (!Hostile(n)) continue;
                Vector3 d = n.T.position - _player.position; d.y = 0;
                // its own script runs at once: lines, then the fight (command 0x66)
                if (n.Data.FollowMode != 16 && _battleCooldown > 0f) continue;
                if (!Touching(n)) continue;                 // entityCheckEntityCollision: the moment the two touch
                if (_anim != null) _anim.SetSlot(IdleSlot);
                if (n.Anim != null) n.Anim.SetSlot(IdleSlot);
                if (n.Data.Script > 0 && StartScript(n.Data.Script, n)) { Vector3 look = _player.position - n.T.position; look.y = 0; if (look.sqrMagnitude > 0.01f) n.T.rotation = FaceRot(look); return; }
                StartBattle(n);
                return;
            }
        }

        [Tooltip("Metres per second of the scripted opponent (the first Agumon) when it runs after you: fast enough to meet you half way on the map, as in the original. Not measured exactly.")] public float ChaseSpeed = 7f;

        const int ChaseSlot = 4;    // the run

        // the game's collision boxes: squares of each one's radius round the two of them
        bool Touching(Npc n)
        {
            var boy = DigimonStatsCatalog.Get(_disc, 0); var it = DigimonStatsCatalog.Get(_disc, n.Data.Type);
            float reach = ((boy != null ? boy.Radius : 0) + (it != null ? it.Radius : 0)) * Unit;
            return Mathf.Abs(n.T.position.x - _player.position.x) < reach && Mathf.Abs(n.T.position.z - _player.position.z) < reach;
        }

        // the scripted opponent (follow mode 16, the first Agumon) runs after you on its map until you fight it: the story cannot go on without that fight
        bool ChaseScripted(Npc n)
        {
            if (!Hostile(n) || n.Data.FollowMode != 16 || _player == null || _runner != null || _intro || _changing || (_battle != null && _battle.Active)) return false;
            if (_partner == null || !_partner.gameObject.activeInHierarchy) return false;
            Vector3 to = _player.position - n.T.position; to.y = 0;
            float dist = to.magnitude;
            if (Touching(n)) return false;
            Vector3 dir = to / dist, pos = n.T.position;
            float step = n.Anim != null ? n.Anim.TakeForward() : ChaseSpeed * Time.deltaTime;
            if (Free(pos + new Vector3(dir.x * step, 0, 0))) pos.x += dir.x * step;
            if (Free(pos + new Vector3(0, 0, dir.z * step))) pos.z += dir.z * step;
            n.T.position = pos;
            n.T.rotation = Quaternion.RotateTowards(n.T.rotation, FaceRot(dir), TurnSpeed * Time.deltaTime);
            int run = n.Anim != null && n.Anim.HasSlot(ChaseSlot) ? ChaseSlot : WalkSlot;
            if (n.Anim != null && n.Anim.Slot != run) n.Anim.SetSlot(run);
            return true;
        }

        // wild Digimon: follow mode 12 (wanders, chases when you come close) and 16 (scripted: chases you from anywhere on the map)
        static bool Hostile(Npc n) { return n.T != null && n.T.gameObject.activeInHierarchy && (n.Data.FollowMode == 12 || n.Data.FollowMode == 16); }


        Vector3 _prePlayerPos;

        void StartBattle(Npc enemy, bool noFlee = false)
        {
            if (_state.Stats[4] == 0)   // not a New Game start: give the partner the opening's Agumon numbers so a fight can be tried
            {
                ushort[] v = { 80, 60, 70, 70, 800, 600, 800, 600 };
                for (int i = 0; i < v.Length; i++) _state.Stats[i] = v[i];
                if (_state.ItemSlots == 0) _state.ItemSlots = 10;
            }
            _battleNpc = enemy;
            if (_anim != null) _anim.SetSlot(IdleSlot);
            _prePlayerPos = _player.position;
            if (_menus != null) _menus.LastUsedItem = -1;
            _battleFocus = (_camOn ? _camPos : _player.position) + Vector3.up * 1f;
            BringPartnerNear();
            Vector3 right = _cam != null ? _cam.transform.right : Vector3.right;
            var boyInfo = DigimonStatsCatalog.Get(_disc, 0);
            _tamerActive = 0;
            _battle.Walkable = Free;
            _battle.Start(_player, _anim, _partner, _partnerAnim, _menus != null ? _menus.PartnerName : _partnerName, DigimonStatsCatalog.Get(_disc, PartnerType),
                          enemy.T, enemy.Anim, enemy.Name, DigimonStatsCatalog.Get(_disc, enemy.Data.Type), enemy.Data, right, noFlee, _tamerWay, _tamerStart, boyInfo != null ? boyInfo.Radius * Unit : 0f);
            Debug.Log("[battle] " + _battle.Partner.Name + " vs " + enemy.Name + " (" + (_battle.Enemy.Info != null ? _battle.Enemy.Info.LevelName : "?") + ", HP " + _battle.Enemy.MaxHp + ", Off " + _battle.Enemy.Off + ")");
        }

        void EndBattle()
        {
            _care.AfterBattle(_battle.Won ? PartnerCare.WonBattle : _battle.Fled ? PartnerCare.FledBattle : PartnerCare.Lost);
            if (_battle.Fled && _state.Stats[16] < 9999) _state.Stats[16]++;   // battles fled
            if (_partnerAnim != null) _partnerAnim.SetSlot(IdleSlot);
            _battleCooldown = 5f;
            _way.Clear();
            if (_anim != null) _anim.SetSlot(IdleSlot);
            if (!Free(_player.position)) _player.position = _prePlayerPos;
            if (_battle.Won && _battleNpc != null && !_battle.NoFlee)   // a beaten wild Digimon is gone; a scripted opponent (the first Agumon) stays for the script's ending
            {
                if (_battleNpc.T != null) Destroy(_battleNpc.T.gameObject);
                _npcs.Remove(_battleNpc);
            }
            else if (!_battle.Won && !_battle.Fled) LoseFight();      // section 0x4DE, as at the end of the fight command
            else if (_battleNpc != null && _battleNpc.Anim != null) _battleNpc.Anim.SetSlot(IdleSlot);
            if (_partner != null && _player != null) PlacePartner(_player.position, _player.forward);
            _battleNpc = null;
        }

        // ------------------------------------------------------------------ partner

        // the boy on the Player page: his 3D model drawn into a picture (in the PS1 page he looks like the 3D model)
        void BuildBoyPicture()
        {
            if (_menus.BoyPicture != null) return;
            try
            {
                if (_entries == null) _entries = new List<DigimonEntry>(DigimonCatalog.Load(_disc));
                if (_allTim == null) _allTim = _disc.ReadFile("/CHDAT/ALLTIM.TIM");
                DigimonEntry boy = null;
                foreach (var e in _entries) if (e.Type == 0) { boy = e; break; }
                if (boy == null) return;
                bool lit = MmdModel.UseLit; float br = MmdModel.Brightness;
                MmdModel.UseLit = false; MmdModel.Brightness = 1f;
                var go = MmdModel.Parse(_disc.ReadFile(boy.MmdPath)).Build(boy, _allTim);
                MmdModel.UseLit = lit; MmdModel.Brightness = br;
                const int layer = 30;
                Vector3 origin = new Vector3(-5000f, 0f, 0f);
                go.transform.position = origin;
                foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
                var an = go.GetComponent<DigimonAnimator>();
                if (an != null) an.SetSlot(IdleSlot);
                var b = new Bounds(origin, Vector3.zero);
                foreach (var r in go.GetComponentsInChildren<Renderer>()) b.Encapsulate(r.bounds);
                var camGo = new GameObject("BoyPictureCam");
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.cullingMask = 1 << layer; cam.fieldOfView = 30f;
                float dist = b.size.y * 0.58f / Mathf.Tan(15f * Mathf.Deg2Rad);
                cam.transform.position = b.center + new Vector3(dist * 0.35f, 0f, -dist);
                cam.transform.LookAt(b.center);
                var rt = new RenderTexture(512, 780, 16, RenderTextureFormat.ARGB32);
                cam.targetTexture = rt;
                _menus.BoyPicture = rt;
            }
            catch (System.Exception ex) { Debug.LogWarning("Boy picture failed: " + ex.Message); }
        }

        void BuildPartner()
        {
            DigimonEntry entry = null;
            foreach (var e in DigimonCatalog.Load(_disc)) if (e.Type == PartnerType) { entry = e; break; }
            if (entry == null) { Debug.LogWarning("Partner type " + PartnerType + " not found."); return; }
            MmdModel.UseLit = Hd2D && UseMapLights;
            MmdModel.Brightness = Hd2D && UseMapLights ? CharacterBrightness : 1f;
            var go = MmdModel.Parse(_disc.ReadFile(entry.MmdPath)).Build(entry, _disc.ReadFile("/CHDAT/ALLTIM.TIM"));
            MmdModel.UseLit = false;
            MmdModel.Brightness = 1f;
            go.transform.SetParent(transform, false);
            _partner = go.transform;
            _partnerName = entry.Name;
            _partnerAnim = go.GetComponent<DigimonAnimator>();
        }

        // the partner walks along the path you took, keeping some distance behind you
        float _viewUntil; int _viewSlot;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        static bool RecruitKey()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current; return k != null && k.f7Key.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.F7);
#else
            return false;
#endif
        }
#endif
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // dev tool: [ and ] play the partner's animation slots one by one (to find which slot is an attack, a hit, ...)
        void SlotViewer()
        {
            if (_partnerAnim == null || _partnerAnim.SlotCount == 0) return;
            int d = 0;
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current; if (k == null) return;
            if (k.leftBracketKey.wasPressedThisFrame) d = -1; else if (k.rightBracketKey.wasPressedThisFrame) d = 1;
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.LeftBracket)) d = -1; else if (Input.GetKeyDown(KeyCode.RightBracket)) d = 1;
#endif
            if (d == 0) return;
            int n = _partnerAnim.SlotCount;
            do { _viewSlot = (_viewSlot + d + n) % n; } while (!_partnerAnim.HasSlot(_viewSlot) && n > 1);
            _viewUntil = Time.time + 8f;
            _partnerAnim.SetSlot(_viewSlot, true);
            Say("Partner animation slot " + _viewSlot + " of " + (n - 1) + "  ( [ and ] change it )");
        }
#endif

        float _partnerStepEnd;
        readonly List<Vector2Int> _way = new List<Vector2Int>();    // tiles to go through when a wall hides you from it (at most 30, as in the game)
        Vector2Int _tamerTile;

        Vector2Int TileOf(Vector3 u) { int x, y; MapBackground.TileAt(u.x / Unit, u.z / Unit, out x, out y); return new Vector2Int(x, y); }

        Vector3 TileMiddle(Vector2Int t) { return new Vector3(((t.x - 50) * 100 + 50) * Unit, _partner.position.y, ((50 - t.y) * 100 - 50) * Unit); }

        // isLinearPathBlocked: a walk tile by tile from one to the other
        bool LineBlocked(int x1, int y1, int x2, int y2)
        {
            int dx = x2 - x1, dy = y2 - y1, x = x1, y = y1;
            bool wide = Mathf.Abs(dx) >= Mathf.Abs(dy);
            int dist = wide ? Mathf.Abs(dx) : Mathf.Abs(dy);
            if (dist == 0) return false;
            int side = wide ? dy : dx, quot = side / dist, rem = side % dist, sign = rem < 0 ? -1 : 1, acc = dist;
            rem = Mathf.Abs(rem);
            for (int i = 0; i < dist; i++)
            {
                int across = quot;
                if (rem != 0) { across = 0; acc -= rem; if (acc <= 0) { across = sign; acc += dist; } }
                if (wide) { y += across; x += dx > 0 ? 1 : -1; } else { x += across; y += dy > 0 ? 1 : -1; }
                if (_map.TileBlocked(x, y)) return true;
            }
            return false;
        }

        // isFiveTileWidePathBlocked: five lines side by side
        bool PathBlocked(Vector2Int a, Vector2Int b)
        {
            bool wide = Mathf.Abs(b.x - a.x) >= Mathf.Abs(b.y - a.y);
            for (int i = -2; i < 3; i++)
                if (wide ? LineBlocked(a.x, a.y + i, b.x, b.y + i) : LineBlocked(a.x + i, a.y, b.x + i, b.y)) return true;
            return false;
        }

        // PS1 pixels from the middle of the screen
        bool Outside(Vector3 world, float halfW, float halfH)
        {
            if (_cam == null) return false;
            Vector3 v = _cam.WorldToViewportPoint(world);
            return v.z <= 0f || Mathf.Abs((v.x - 0.5f) * 320f) > halfW || Mathf.Abs((0.5f - v.y) * 240f) > halfH;
        }

        // partnerTickCollision and partnerTickWalking: every frame it turns straight to you, or to the next waypoint when a wall is between you, and its animation carries it forward
        void UpdatePartner(Vector3 facing)
        {
            if (_partner == null || Time.time < _viewUntil) return;
            Vector3 p = _player.position;
            Vector2Int tamer = TileOf(p), me = TileOf(_partner.position);

            if (tamer != _tamerTile)
            {
                if (!PathBlocked(tamer, me)) _way.Clear();
                else if (_way.Count == 0 || PathBlocked(tamer, _way[_way.Count - 1]))
                {
                    if (_way.Count == 30) _way.RemoveAt(0);
                    _way.Add(_tamerTile);
                }
            }
            if (_way.Count >= 2)
                for (int i = 0; i < 3; i++)                 // a shortcut opened up
                {
                    int index = _way.Count - 2 - i;
                    if (index < 0) break;
                    if (!PathBlocked(tamer, _way[index])) { _way.RemoveRange(index + 1, i + 1); break; }
                }
            if (_way.Count > 0 && Outside(TileMiddle(_way[0]), 200f, 160f) && Outside(_partner.position, 160f, 120f))
            {
                _partner.position = TileMiddle(_way[0]);    // it is off screen and so is the waypoint: it just skips there
                _way.RemoveAt(0);
                me = TileOf(_partner.position);
            }

            Vector3 goal = _way.Count > 0 ? TileMiddle(_way[0]) : p, heading = goal - _partner.position;
            if (_way.Count > 0 && me == _way[0]) _way.RemoveAt(0);
            heading.y = 0f;
            if (heading.sqrMagnitude > 0.0001f)
            {
                heading.Normalize();
                _partner.rotation = Quaternion.LookRotation(-heading) * Quaternion.Euler(0, FacingOffset, 0);
            }
            _tamerTile = tamer;

            // getPartnerTamerCloseness: runs beyond 3.5 of its radius, walks beyond 2.5, else stands
            float radius = (_menus != null && _menus.PartnerInfo != null ? _menus.PartnerInfo.Radius : 120) * 0.01f;
            float away = Vector3.Distance(new Vector3(p.x, 0, p.z), new Vector3(_partner.position.x, 0, _partner.position.z));
            int cur = _partnerAnim != null ? _partnerAnim.Slot : WalkSlot, next = cur;
            int walk = PartnerSlow && _partnerAnim != null && _partnerAnim.HasSlot(3) ? 3 : WalkSlot;
            bool unhappy = (_state.Para.Condition & PartnerCare.Unhappy) != 0;
            bool stepDone = Time.time >= _partnerStepEnd;     // a walk finishes its step, and a stand its loop, before it changes
            if (stepDone && _partnerAnim != null) _partnerStepEnd = Time.time + _partnerAnim.SecondsOf(cur);
            if (away > radius * 3.5f) { if (unhappy) next = WalkSlot; else if ((cur != WalkSlot && cur != 3) || stepDone) next = RunSlot; }   // an unhappy Digimon will not run
            else if (away >= radius * 2.5f) { if (cur == RunSlot || stepDone) next = walk; }
            else if (stepDone) next = NextStand();
            if (next == RunSlot && _partnerAnim != null && !_partnerAnim.HasSlot(RunSlot)) next = walk;
            if (_partnerAnim != null && next != cur)
            {
                _partnerAnim.SetSlot(next);
                _partnerStepEnd = Time.time + _partnerAnim.SecondsOf(next);
            }

            float step = _partnerAnim != null ? _partnerAnim.TakeForward() : WalkSpeed * Time.deltaTime;
            if ((next == WalkSlot || next == 3 || next == RunSlot) && step > 0f)
            {
                Vector3 pos = _partner.position, by = heading * step;
                if (Free(pos + by)) pos += by;
                else if (Free(pos + new Vector3(by.x, 0, 0))) pos.x += by.x;    // a wall stops the blocked direction only
                else if (Free(pos + new Vector3(0, 0, by.z))) pos.z += by.z;
                pos.y = p.y;
                _partner.position = pos;
            }
        }

        void PlacePartner(Vector3 playerPos, Vector3 face)
        {
            if (_partner == null) return;
            Vector3 spot = playerPos;
            for (float d = PartnerDistance; d > 0.5f; d -= 0.5f)
                if (Free(playerPos - face * d)) { spot = playerPos - face * d; break; }
            _partner.position = spot;
            _partner.rotation = _player.rotation;
            _way.Clear();
            _tamerTile = TileOf(playerPos);
            if (_partnerAnim != null) _partnerAnim.SetSlot(IdleSlot);
        }

        // ------------------------------------------------------------------ maps

        void LoadMap(string path, int arriveAtExit)
        {
            byte[] mapBytes = _disc.ReadFile(path);
            var map = MapBackground.Parse(mapBytes, _disc.ReadFile(path.Substring(0, path.Length - 4) + ".TFS"));
            ClearOverlays();

            if (_backdrop != null)
            {
                var mr = _backdrop.GetComponent<MeshRenderer>();
                if (mr != null && mr.sharedMaterial != null)
                {
                    var t = mr.sharedMaterial.GetTexture("_BaseMap");
                    if (t != null) Destroy(t);
                    Destroy(mr.sharedMaterial);
                }
                Destroy(_backdrop);
            }
            foreach (var l in _mapLights) if (l != null) Destroy(l);
            _mapLights.Clear();
            _lightList.Clear(); _lightBase.Clear();
            ClearNpcs();

            _map = map;
            MapPath = path;
            _mapId = _mapNames.IndexOf(Path.GetFileNameWithoutExtension(path));
            _curPalette = DesiredPalette(out _lightFactor);

            // the game's camera, in game space (Y down)
            _vpG = _map.Viewpoint;
            _fG = (_map.ReferencePoint - _vpG).normalized;
            _rG = Vector3.Cross(new Vector3(0, 1, 0), _fG).normalized;
            _dG = Vector3.Cross(_fG, _rG);
            _h = _map.ViewDistance;

            SetupCamera();
            BuildBackdrop();
            BuildOverlays(mapBytes);
            if (Hd2D && UseMapLights) SetupLights();

            // where the Digimon appears
            Vector3 want, face;
            if (arriveAtExit >= 0 && arriveAtExit < 10 && (_map.Warps[arriveAtExit].SpawnX != 0 || _map.Warps[arriveAtExit].SpawnZ != 0))
            {
                var w = _map.Warps[arriveAtExit];
                want = new Vector3(w.SpawnX * Unit, 0, w.SpawnZ * Unit);
                float a = w.Rotation * 2f * Mathf.PI / 4096f;
                face = new Vector3(-Mathf.Sin(a), 0, -Mathf.Cos(a));
            }
            else
            {
                want = ToUnity(_map.ReferencePoint);
                face = Vector3.back;
            }
            _player.position = FindStart(want);
            _tamerWay.Clear(); _tamerActive = 0; _tamerStart = _tamerPrev = TileOf(_player.position);
            _player.rotation = Quaternion.LookRotation(-face) * Quaternion.Euler(0, FacingOffset, 0);
            if (_anim != null) _anim.SetSlot(IdleSlot);
            PlacePartner(_player.position, face);
            BuildNpcs();
            CareOnMap(mapBytes);
            if (_gridOverlay != null) { Destroy(_gridOverlay); _gridOverlay = null; }
            if (_showGrid) BuildGridOverlay();
            _calibrated = false;
            _armed = false;     // exits only work after you have stepped off the arrival spot
        }

        // ------------------------------------------------------------------ foreground layers

        void ClearOverlays()
        {
            if (_overlayRoot != null) Destroy(_overlayRoot);
            foreach (var t in _ovTex.Values) if (t != null) Destroy(t);
            foreach (var m in _ovMat.Values) if (m != null) Destroy(m);
            _ovTex.Clear(); _ovMat.Clear(); _ovAnims.Clear();
            _overlayRoot = null; _overlay = null;
        }

        Material GetOverlayMaterial(int obj)
        {
            Material m;
            if (_ovMat.TryGetValue(obj, out m)) return m;
            var sh = Shader.Find("DW1Remake/Overlay");
            if (sh == null) return null;
            var o = _overlay.Objects[obj];
            var tex = new Texture2D(Mathf.Max(1, o.W), Mathf.Max(1, o.H), TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.SetPixels32(_overlay.BuildObject(obj, _map.GetPalette(_curPalette)));
            tex.Apply();
            m = new Material(sh);
            m.SetTexture("_BaseMap", tex);
            m.SetVector("_Texel", new Vector4(1f / tex.width, 1f / tex.height, 0, 0));
            m.SetFloat("_BlurMax", Hd2D ? TiltShiftBlur : 0f);
            m.SetFloat("_FocusCenter", FocusCenter);
            m.SetFloat("_FocusHalf", FocusHalfHeight);
            m.SetFloat("_Sharpen", Hd2D ? Sharpen : 0f);
            if (o.Trans == 2)
            {
                // shadow pieces: the PS1 draws these semi-transparent (half shadow colour, half ground)
                m.SetFloat("_Opacity", ShadowPieceOpacity);
                m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_ZWriteOn", 0f);
                m.renderQueue = 3000;
            }
            _ovTex[obj] = tex; _ovMat[obj] = m;
            return m;
        }

        // puts a piece on the camera ray so that it covers exactly the same pixels as on the painted map
        void PlaceOverlay(Transform t, MapOverlay.Inst inst, MapOverlay.Obj ob, float dm)
        {
            float cx = inst.X + ob.W * 0.5f, cy = inst.Y + ob.H * 0.5f;
            float k = dm / _h;
            Transform c = _cam.transform;
            t.position = c.position + c.forward * dm + c.right * ((cx - _map.PixelWidth * 0.5f) * k) - c.up * ((cy - _map.PixelHeight * 0.5f) * k);
            t.rotation = c.rotation;
            t.localScale = new Vector3(ob.W * k, ob.H * k, 1f);
        }

        void BuildOverlays(byte[] mapBytes)
        {
            if (_mapId < 0 || _mapId >= _num8.Count) return;
            _overlay = MapOverlay.Parse(mapBytes, _num8[_mapId], _num4[_mapId]);
            if (_overlay.Instances.Length == 0) { _overlay = null; return; }
            if (_quadMesh == null)
            {
                var tmp = GameObject.CreatePrimitive(PrimitiveType.Quad);
                _quadMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
                Destroy(tmp);
            }
            _overlayRoot = new GameObject("MapOverlays");
            _overlayRoot.transform.SetParent(transform, false);
            _overlayRoot.SetActive(ShowOverlays);

            foreach (var inst in _overlay.Instances)
            {
                if (inst.Flag == 1 || (inst.Flag & 0x80) != 0) continue;
                int f0 = inst.Frames[0];
                if (f0 < 0 || f0 >= _overlay.Objects.Length) continue;
                var ob = _overlay.Objects[f0];
                Debug.Log("[overlay piece] obj " + f0 + " pos " + inst.X + "," + inst.Y + " size " + ob.W + "x" + ob.H + " clut " + ob.Clut + " trans " + ob.Trans + " worldY " + ob.WY + " frames " + inst.Frames[0] + "," + inst.Frames[1]);

                // how far away it is decides whether it is in front of or behind the Digimon
                Vector3 world = new Vector3(ob.WX, ob.WY, ob.WZ);
                float z = Vector3.Dot(world - _vpG, _fG);
                int order = Mathf.Clamp((int)(z / 16f), 35, 4095);
                if (ob.WY == 10000) order = 4094;
                float dm = Mathf.Min(order * 16f * Unit, 295f);

                var mat = GetOverlayMaterial(f0);
                if (mat == null) return;
                var go = new GameObject("Piece");
                go.transform.SetParent(_overlayRoot.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = _quadMesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                PlaceOverlay(go.transform, inst, ob, dm);

                int frames = 0;
                for (int k = 0; k < 8 && inst.Frames[k] != -1; k++) frames++;
                if (frames > 1) { var fs = new System.Text.StringBuilder(); for (int k = 0; k < 8; k++) fs.Append(inst.Frames[k]).Append('/').Append(inst.Times[k]).Append(' '); Debug.Log("[overlay anim] frames/times: " + fs); }
                if (frames > 1) _ovAnims.Add(new OvAnim { T = go.transform, R = mr, I = inst, Cur = 0, Timer = 0, Dm = dm });
            }
        }

        void TickOverlayAnimations()
        {
            if (_overlay == null || _ovAnims.Count == 0 || _overlayRoot == null || !_overlayRoot.activeSelf) return;
            _ovAcc += Time.deltaTime * 30f;
            while (_ovAcc >= 1f)
            {
                _ovAcc -= 1f;
                foreach (var a in _ovAnims)
                {
                    a.Timer++;
                    if (a.Timer != a.I.Times[a.Cur]) continue;
                    a.Timer = 0;
                    a.Cur++;
                    if (a.Cur >= 8 || a.I.Frames[a.Cur] == -1) a.Cur = 0;
                    int f = a.I.Frames[a.Cur];
                    if (f == -2) continue;                       // -2 = keep showing the last piece for this long (the PS1 game never blanks them)
                    if (f < 0 || f >= _overlay.Objects.Length) { a.R.enabled = false; continue; }
                    var mat = GetOverlayMaterial(f);
                    if (mat == null) continue;
                    a.R.enabled = true;
                    a.R.sharedMaterial = mat;
                    PlaceOverlay(a.T, a.I, _overlay.Objects[f], a.Dm);
                }
            }
        }

        void RefreshOverlayPalette()
        {
            if (_overlay == null) return;
            var pal = _map.GetPalette(_curPalette);
            foreach (var kv in _ovTex)
            {
                if (!_overlay.UsesMapPalette(kv.Key)) continue;
                kv.Value.SetPixels32(_overlay.BuildObject(kv.Key, pal));
                kv.Value.Apply();
            }
        }

        void SetupCamera()
        {
            _cam = Camera.main;
            if (_cam == null) _cam = new GameObject("Main Camera").AddComponent<Camera>();
            Vector3 vp = ToUnity(_vpG);
            _cam.transform.position = vp;
            _cam.transform.rotation = Quaternion.LookRotation(ToUnity(_vpG + _fG) - vp, Vector3.up);
            _cam.nearClipPlane = 1f;
            _cam.farClipPlane = BackdropDistance * 3f;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = Color.black;
            _cam.usePhysicalProperties = true;
            _cam.gateFit = Camera.GateFitMode.Vertical;
        }

        void BuildBackdrop()
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "MapBackdrop";
            Destroy(quad.GetComponent<MeshCollider>());
            quad.transform.SetParent(transform, false);
            Vector3 fU = _cam.transform.forward;
            quad.transform.position = _cam.transform.position + fU * BackdropDistance;
            quad.transform.rotation = _cam.transform.rotation;
            quad.transform.localScale = new Vector3(_map.PixelWidth * BackdropDistance / _h, _map.PixelHeight * BackdropDistance / _h, 1f);
            Texture2D tex = _map.BuildTexture(_curPalette);
            Shader bs = Hd2D ? Shader.Find("DW1Remake/Backdrop") : null;
            Material mat;
            if (bs != null)
            {
                mat = new Material(bs);
                mat.SetTexture("_BaseMap", tex);
                mat.SetVector("_Texel", new Vector4(1f / tex.width, 1f / tex.height, 0, 0));
                mat.SetFloat("_BlurMax", TiltShiftBlur);
                mat.SetFloat("_FocusCenter", FocusCenter);
                mat.SetFloat("_FocusHalf", FocusHalfHeight);
                mat.SetFloat("_Sharpen", Sharpen);
            }
            else
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                mat.SetTexture("_BaseMap", tex);
            }
            quad.GetComponent<MeshRenderer>().sharedMaterial = mat;
            _backdrop = quad;
        }

        // converts a screen direction (x right, y up) into a walking direction on the ground, using the real camera
        Vector3 ScreenToGround(Vector2 s)
        {
            if (s.sqrMagnitude < 0.0001f || _cam == null) return Vector3.zero;
            Vector3 o = _player.position;
            Vector3 p0 = _cam.WorldToScreenPoint(o);
            Vector2 a = (Vector2)(_cam.WorldToScreenPoint(o + Vector3.right) - p0);
            Vector2 b = (Vector2)(_cam.WorldToScreenPoint(o + Vector3.forward) - p0);
            float det = a.x * b.y - a.y * b.x;
            if (Mathf.Abs(det) < 1e-5f) return new Vector3(s.x, 0, s.y);
            float wx = (s.x * b.y - s.y * b.x) / det;
            float wz = (a.x * s.y - a.y * s.x) / det;
            var w = new Vector3(wx, 0, wz);
            return w.sqrMagnitude > 1e-8f ? w.normalized : new Vector3(s.x, 0, s.y);
        }

        // the nearest walkable spot to 'want', anywhere on the walking grid
        Vector3 NearestWalkable(Vector3 want)
        {
            Vector3 best = want; float bd = float.MaxValue;
            for (int tz = 0; tz < 100; tz++)
                for (int tx = 0; tx < 100; tx++)
                {
                    if (_map.Collision[tx + tz * 100] == 0x80) continue;
                    var p = new Vector3((tx - 50 + 0.5f), 0, (50 - tz - 0.5f));    // tile centre in metres
                    if (!Free(p)) continue;
                    float d = (p - want).sqrMagnitude;
                    if (d < bd) { bd = d; best = p; }
                }
            return best;
        }

        Vector3 FindStart(Vector3 want)
        {
            for (int r = 0; r < 30; r++)
                for (int dx = -r; dx <= r; dx++)
                    for (int dz = -r; dz <= r; dz++)
                    {
                        if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != r) continue;
                        var p = want + new Vector3(dx, 0, dz);
                        if (Free(p)) return p;
                    }
            return NearestWalkable(want);
        }

        bool Free(Vector3 u)
        {
            const float r = 0.3f;
            return Walkable(u.x - r, u.z - r) && Walkable(u.x + r, u.z - r) && Walkable(u.x - r, u.z + r) && Walkable(u.x + r, u.z + r) && !BlockedByNpc(u);
        }

        bool Walkable(float ux, float uz) { return _map.CollisionAt(ux / Unit, uz / Unit) != 0x80; }

        // ------------------------------------------------------------------ HD-2D look

        void SetupHd2DOnce()
        {
            // post-processing: glow, colour boost, darker corners
            var camData = _cam.GetUniversalAdditionalCameraData();
            if (camData != null) camData.renderPostProcessing = true;

            var volGo = new GameObject("HD2D Volume");
            volGo.transform.SetParent(transform, false);
            var vol = volGo.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.priority = 100;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var bloom = profile.Add<UnityEngine.Rendering.Universal.Bloom>(true);
            bloom.threshold.Override(0.85f);
            bloom.intensity.Override(Bloom);
            bloom.scatter.Override(0.65f);
            var vig = profile.Add<UnityEngine.Rendering.Universal.Vignette>(true);
            vig.intensity.Override(Vignette);
            vig.smoothness.Override(0.5f);
            var ca = profile.Add<ColorAdjustments>(true);
            ca.saturation.Override(Saturation);
            ca.contrast.Override(Contrast);
            vol.sharedProfile = profile;

            // soft shadow under the Digimon
            var shSh = Shader.Find("DW1Remake/BlobShadow");
            if (shSh != null && ShadowOpacity > 0.01f)
            {
                _shadow = MakeShadow(shSh);
                if (_partner != null) _shadow2 = MakeShadow(shSh);
            }
        }

        Transform MakeShadow(Shader shSh)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = "BlobShadow";
            Destroy(q.GetComponent<MeshCollider>());
            q.transform.SetParent(transform, false);
            q.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            q.transform.localScale = new Vector3(1.6f, 1.1f, 1f);
            var m = new Material(shSh);
            m.SetColor("_Color", new Color(0f, 0f, 0f, ShadowOpacity));
            q.GetComponent<MeshRenderer>().sharedMaterial = m;
            return q.transform;
        }

        void SetupLights()
        {
            foreach (var l in FindObjectsByType<Light>())
                if (l.type == LightType.Directional && !l.transform.IsChildOf(transform)) l.enabled = false;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(Ambient, Ambient, Ambient) * _lightFactor;
            for (int i = 0; i < 3; i++)
            {
                Vector3 g = _map.LightDir[i];
                Color c = _map.LightColor[i];
                if (g.sqrMagnitude < 1f || (c.r + c.g + c.b) < 0.01f) continue;
                var lg = new GameObject("MapLight" + i);
                lg.transform.SetParent(transform, false);
                var light = lg.AddComponent<Light>();
                light.type = LightType.Directional;
                light.color = c;
                light.intensity = LightIntensity * _lightFactor;
                _lightList.Add(light); _lightBase.Add(LightIntensity);
                light.shadows = LightShadows.None;
                Vector3 d = ToUnity(g).normalized * (InvertLightDirection ? -1f : 1f);
                if (AutoOrientLights && Vector3.Dot(d, _cam.transform.forward) < 0f) d = -d;
                lg.transform.rotation = Quaternion.LookRotation(d);
                _mapLights.Add(lg);
            }
        }

        // ------------------------------------------------------------------ input and movement

        static Vector2 ReadMove()
        {
            float x = 0, y = 0;
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (k == null) return Vector2.zero;
            if (k.rightArrowKey.isPressed || k.dKey.isPressed) x += 1;
            if (k.leftArrowKey.isPressed || k.aKey.isPressed) x -= 1;
            if (k.upArrowKey.isPressed || k.wKey.isPressed) y += 1;
            if (k.downArrowKey.isPressed || k.sKey.isPressed) y -= 1;
#elif ENABLE_LEGACY_INPUT_MANAGER
            x = Input.GetAxisRaw("Horizontal");
            y = Input.GetAxisRaw("Vertical");
#endif
            return new Vector2(x, y);
        }

        // the original game: 06:00-15:59 day, 16:00-19:59 evening (lights x0.7), 20:00-05:59 night (lights x0.5)
        int DesiredPalette(out float factor)
        {
            factor = 1f;
            if (!AutoClock) return Mathf.Clamp(Palette, 0, Mathf.Max(0, _map.PaletteCount - 1));
            if (_mapId >= 0 && _mapId < _mapFlags.Count && (_mapFlags[_mapId] & 0x40) != 0) return 0;   // map without day/night
            int hour = (int)_clock;
            int pal = 0;
            if (hour >= 16 && hour <= 19) { pal = 1; factor = 0.7f; }
            else if (hour < 6 || hour > 19) { pal = 2; factor = 0.5f; }
            return Mathf.Clamp(pal, 0, Mathf.Max(0, _map.PaletteCount - 1));
        }

        // the game stops its clock in the setup and opening, during scripts, in the Triangle menu, in fights and while the map changes
        bool GameTimeRuns
        {
            get
            {
                if (!AutoClock || _care == null || _changing || _intro || _runner != null || _partner == null || _pFrozen) return false;
                if (_setup != null && _setup.Active) return false;
                return (_menus == null || !_menus.Active) && (_battle == null || !_battle.Active);
            }
        }

        void UpdateClock()
        {
            if (_map == null) return;
            if (GameTimeRuns)
            {
                _clockFrames += ClockMinutesPerSecond * 20f * Time.deltaTime;
                while (_clockFrames >= 1f) { _clockFrames -= 1f; _care.Tick(!_pBusy); }
            }
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (k != null && k.tKey.wasPressedThisFrame) _care.SkipHours(1);   // T = jump forward one hour
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.T)) _care.SkipHours(1);
#endif
            _clock = _care.ClockHours;
            float f;
            int pal = DesiredPalette(out f);
            if (pal != _curPalette || !Mathf.Approximately(f, _lightFactor))
            {
                _lightFactor = f;
                for (int i = 0; i < _lightList.Count; i++) if (_lightList[i] != null) _lightList[i].intensity = _lightBase[i] * f;
                if (Hd2D && UseMapLights) RenderSettings.ambientLight = new Color(Ambient, Ambient, Ambient) * f;
                if (pal != _curPalette && _backdrop != null)
                {
                    _curPalette = pal;
                    var mat = _backdrop.GetComponent<MeshRenderer>().sharedMaterial;
                    var old = mat.GetTexture("_BaseMap");
                    mat.SetTexture("_BaseMap", _map.BuildTexture(pal));
                    if (old != null) Destroy(old);
                    RefreshOverlayPalette();
                }
            }
        }

        void Update()
        {
            HandleDebugAndSaveKeys();
            UpdateClock();
            if (GridKeyPressed()) ToggleGrid();
            if (NamesKeyPressed()) ShowNames = !ShowNames;
            if (StatsKeyPressed()) _showStats = !_showStats;
            if (OverlayKeyPressed()) { ShowOverlays = !ShowOverlays; if (_overlayRoot != null) _overlayRoot.SetActive(ShowOverlays); }
            TickOverlayAnimations();
            if (_player == null || _map == null || _changing) return;
            if (_setup != null && _setup.Active) { _setup.Tick(MenuStep(), GameMenus.SideStep(), ActionPressed(), GameMenus.CancelKey()); return; }
            _state.PlayMinutes += Time.deltaTime / 60f;
            if (_menus != null)
            {
                _menus.Species = _partnerName; _menus.Day = _dayCount + 1;
                if (_menus.Active)
                {
                    _menus.Tick(MenuStep(), GameMenus.SideStep(), ActionPressed(), GameMenus.CancelKey());
                    if (_battle != null && _battle.Active && _menus.LastUsedItem >= 0) { _battle.LastItem = _menus.LastUsedItem; _menus.LastUsedItem = -1; }   // the boy throws it to the partner
                    if (!_menus.Active && _shopWait) { _shopWait = false; AdvanceScript(); }   // the shop closed: the script goes on
                    return;
                }
                if (_battle != null && _battle.Active)
                {
                    _battle.ReadBack();
                    _battle.Tick(GameMenus.SideStep(), ActionPressed(), GameMenus.TriangleKey() || GameMenus.BagKey(), GameMenus.CancelKey());   // Triangle opens the item page
                    if (!_battle.Active)
                    {
                        EndBattle();
                        if (_battleWait) { _battleWait = false; if (_runner != null) AdvanceScript(); }
                    }
                    return;
                }
                if (_runner == null)
                {
                    if (GameMenus.TriangleKey()) { _menus.OpenMain(); return; }
                    if (GameMenus.BagKey()) { _menus.OpenBag(); return; }
                    if (GameMenus.ShopKey()) { _menus.OpenShop(); return; }
                    if (GameMenus.GiveKey()) _menus.DebugGive();
                }
            }
            UpdateNpcs();
            TickActors();
            if (_runner != null && !_pBusy && _partnerAnim != null && _partner != null && !_movers.ContainsKey(_partner) && !_partnerAnim.Busy
                && (_partnerAnim.Slot == WalkSlot || _partnerAnim.Slot == RunSlot)) _partnerAnim.SetSlot(IdleSlot);    // no running on the spot during a talk
            if (_pBusy && _runner == null) return;
            if (_eggOpen)
            {
                int es = GameMenus.SideStep();
                if (es != 0) _eggSlot = (_eggSlot + es + 4) % 4;
                if (ActionPressed()) { _eggOpen = false; AdvanceScript(); }
                return;
            }
            if (_runner != null)
            {
                if (_wait > 0f || _waitAll)
                {
                    _wait -= Time.deltaTime;
                    if (_wait <= 0f && (!_waitAll || !ActorsBusy())) { _wait = 0f; _waitAll = false; AdvanceScript(); }
                    return;
                }
                if (_choiceOpts != null)
                {
                    int ms = MenuStep();
                    if (ms != 0) _choiceSel = (_choiceSel + ms + _choiceOpts.Count) % _choiceOpts.Count;
                    if (ActionPressed()) { _runner.Choose(_choiceSel); _choiceOpts = null; AdvanceScript(); }
                }
                else if (ActionPressed()) AdvanceScript();
                return;
            }
            CheckCareRequests();
            if (_pBusy || _runner != null) return;
            if (_battleCooldown > 0f) _battleCooldown -= Time.deltaTime;
            if (!_changing) { TryStartBattle(); if (_battle != null && _battle.Active) return; if (_runner != null) return; }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            SlotViewer();
            if (RecruitKey()) { _state.SetFlag(0xF9, true); BuildNpcs(); Say("Test: Coelamon recruited (flag 0xF9 on)"); }
#endif
            if (ActionPressed()) TryTalk();
            if (_runner != null) return;
            Vector2 input = ReadMove();
            // arrow keys follow the screen: "up" walks up the screen, "right" to the right, whatever way the map's camera faces
            Vector3 dir = ScreenToGround(input);
            bool moving = dir.sqrMagnitude > 0.01f;
            if (moving)
            {
                dir.Normalize();
                TickTamerWay();
                Vector3 step = dir * (_anim != null ? _anim.TakeForward() : WalkSpeed * Time.deltaTime);
                Vector3 pos = _player.position;
                if (Free(pos + new Vector3(step.x, 0, 0))) pos.x += step.x;
                if (Free(pos + new Vector3(0, 0, step.z))) pos.z += step.z;
                _player.position = pos;
                var target = Quaternion.LookRotation(-dir) * Quaternion.Euler(0, FacingOffset, 0);
                _player.rotation = Quaternion.RotateTowards(_player.rotation, target, TurnSpeed * Time.deltaTime);
            }
            if (_anim != null)
            {
                int want = !moving ? IdleSlot : GameMenus.WalkHeld() ? WalkSlot : PlayerRunSlot;    // the boy runs; holding R1 (E) makes him walk
                if (_anim.Slot != want) _anim.SetSlot(want);
            }

            UpdatePartner(dir);

            // exits: grid values 110..119, doors and triggers: 50..109
            int cell = _map.CollisionAt(_player.position.x / Unit, _player.position.z / Unit);
            if (cell == 120 && (_state.Para.Condition & PartnerCare.Poopy) != 0) { if (_armed) { _armed = false; StartDgSection(ToiletSection); } }
            else if (cell >= 110 && cell < 120)
            {
                if (_armed) TryExit(cell - 110);
            }
            else if (cell >= 50 && cell < 110)
            {
                // 50..79 run by themselves, 80..109 need the action button
                if (_armed && (cell < 80 || ActionPressed())) TryDoor(cell);
            }
            else _armed = true;
        }

        bool _showStats;
        GameMenus _menus;
        CharacterSetup _setup;
        int _speaker = 255, _dayCount;
        [Tooltip("Name shown for the boy in menus and dialogue (the real game lets the player type one).")] public string PlayerNameText = "A";
        [Tooltip("Name shown for the partner.")] public string PartnerNameText = "B";
        static bool StatsKeyPressed()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            return k != null && k.f4Key.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.F4);
#else
            return false;
#endif
        }

        void DrawStats()
        {
            var s = _state;
            string[] names = { "Offense", "Defense", "Speed", "Brains", "Max HP", "Max MP", "HP", "MP" };
            var sb = new System.Text.StringBuilder("Partner (F4)" + System.Environment.NewLine);
            for (int i = 0; i < names.Length; i++) sb.Append(names[i]).Append(": ").Append(s.Stats[i]).Append(System.Environment.NewLine);
            sb.Append("Money: ").Append(s.Money).Append("   Bag slots: ").Append(s.ItemSlots).Append("   Items: ").Append(s.BagKinds()).Append(System.Environment.NewLine);
            var p = s.Para; string nl = System.Environment.NewLine;
            sb.Append("Clock ").Append(_care.Hour.ToString("00")).Append(":").Append(_care.Minute.ToString("00")).Append("   time ").Append(GameTimeRuns ? "running" : "stopped").Append(nl);
            sb.Append("Age ").Append(s.Age).Append("   Lives ").Append(s.Life).Append("   Lifespan left ").Append(p.Lifetime).Append(" h").Append(nl);
            sb.Append("Hours since evolving ").Append(p.EvoTimer).Append("   Fights won ").Append(p.Battles).Append(nl);
            sb.Append("Weight ").Append(s.Weight).Append("   Care mistakes ").Append(p.CareMistakes).Append("   Virus ").Append(p.Virus).Append(nl);
            sb.Append("Happiness ").Append(s.Happiness).Append("   Discipline ").Append(s.Discipline).Append("   Tiredness ").Append(p.Tiredness).Append(nl);
            sb.Append("Energy ").Append(p.Energy).Append("   Food ").Append(p.FoodLevel).Append(" min (hungry at ").Append(p.NextHungerHour).Append(":00)   Poop ").Append(p.PoopLevel).Append(nl);
            sb.Append("Sleepy ").Append(p.SleepyHour).Append(":").Append(p.SleepyMinute.ToString("00")).Append("   Wakes ").Append(p.WakeupHour).Append(":").Append(p.WakeupMinute.ToString("00"));
            GUI.Box(new Rect(10, _hintBottom + 6, 430, 300), sb.ToString());
        }

        static bool NamesKeyPressed()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            return k != null && k.f3Key.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.F3);
#else
            return false;
#endif
        }

        void DrawName(Transform t, string name, Color col)
        {
            if (t == null || string.IsNullOrEmpty(name) || _cam == null) return;
            Vector3 sp = _cam.WorldToScreenPoint(t.position + Vector3.up * 1.5f);
            if (sp.z <= 0) return;
            var st = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            var rc = new Rect(sp.x - 80, Screen.height - sp.y - 12, 160, 24);
            st.normal.textColor = Color.black;
            GUI.Label(new Rect(rc.x + 1, rc.y + 1, rc.width, rc.height), name, st);
            st.normal.textColor = col;
            GUI.Label(rc, name, st);
        }

        public void GotoMap(string path)
        {
            if (_disc == null || _changing || string.IsNullOrEmpty(path)) return;
            StartCoroutine(ChangeMap(path, -1));
        }

        static bool OverlayKeyPressed()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            return k != null && k.f2Key.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.F2);
#else
            return false;
#endif
        }

        static bool GridKeyPressed()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            return k != null && k.f1Key.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.F1);
#else
            return false;
#endif
        }

        void ToggleGrid()
        {
            _showGrid = !_showGrid;
            if (_gridOverlay != null) { Destroy(_gridOverlay); _gridOverlay = null; }
            if (_showGrid) BuildGridOverlay();
        }

        // F1: paints the walking grid on the floor. red = blocked, green = exit, blue = script trigger/door, yellow = other
        void BuildGridOverlay()
        {
            var verts = new List<Vector3>(); var cols = new List<Color>(); var tris = new List<int>();
            for (int tz = 0; tz < 100; tz++)
                for (int tx = 0; tx < 100; tx++)
                {
                    byte v = _map.Collision[tx + tz * 100];
                    if (v == 0) continue;
                    Color c;
                    if (v == 0x80) c = new Color(1f, 0.1f, 0.1f, 0.35f);
                    else if (v >= 110 && v < 120) c = new Color(0.1f, 1f, 0.2f, 0.55f);
                    else if (v >= 50 && v < 110) c = new Color(0.2f, 0.4f, 1f, 0.55f);
                    else c = new Color(1f, 1f, 0.1f, 0.45f);
                    float x0 = (tx - 50) * 1f, z0 = (49 - tz) * 1f;
                    int b = verts.Count;
                    verts.Add(new Vector3(x0, 0.02f, z0)); verts.Add(new Vector3(x0 + 1, 0.02f, z0));
                    verts.Add(new Vector3(x0 + 1, 0.02f, z0 + 1)); verts.Add(new Vector3(x0, 0.02f, z0 + 1));
                    for (int k = 0; k < 4; k++) cols.Add(c);
                    tris.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
                }
            var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts); mesh.SetColors(cols); mesh.SetTriangles(tris, 0);
            _gridOverlay = new GameObject("GridOverlay");
            _gridOverlay.transform.SetParent(transform, false);
            _gridOverlay.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = _gridOverlay.AddComponent<MeshRenderer>();
            mr.sharedMaterial = new Material(Shader.Find("Sprites/Default"));
        }

        static int MenuStep()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (k == null) return 0;
            if (k.upArrowKey.wasPressedThisFrame || k.wKey.wasPressedThisFrame) return -1;
            if (k.downArrowKey.wasPressedThisFrame || k.sKey.wasPressedThisFrame) return 1;
            return 0;
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) return -1;
            if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) return 1;
            return 0;
#else
            return 0;
#endif
        }

        static bool ActionPressed()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            return k != null && (k.spaceKey.wasPressedThisFrame || k.enterKey.wasPressedThisFrame || k.zKey.wasPressedThisFrame);
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Z);
#else
            return false;
#endif
        }

        // does the map 'id' have an exit that leads back to the current map?
        int ConnectsBack(int id)
        {
            try
            {
                string p = MapCatalog.PathFor(_mapNames, id);
                if (!_disc.Exists(p)) return 0;
                var m = MapBackground.Parse(_disc.ReadFile(p), _disc.ReadFile(p.Substring(0, p.Length - 4) + ".TFS"));
                foreach (var w in m.Warps) if (w.TargetMap == _mapId) return 1;
            }
            catch (Exception) { }
            return 0;
        }

        void TryDoor(int trigger)
        {
            _armed = false;
            if (_scripts == null || !_scripts.Available || _mapId < 0) { Say("Trigger " + trigger + ": map scripts not found on this disc."); return; }
            if (StartScript(trigger, null)) return;
            var hits = _scripts.FindMapChanges(_scripts.ScriptIdForMap(_mapId), trigger, _mapNames.Count);
            // targets that have an exit leading back to this map come first (keeping the script's order otherwise)
            var ordered = new List<int[]>();
            foreach (var h in hits) if (ConnectsBack(h[0]) == 1) ordered.Add(h);
            foreach (var h in hits) if (ConnectsBack(h[0]) == 0) ordered.Add(h);
            hits = ordered;
            foreach (var h in hits)
            {
                string next = MapCatalog.PathFor(_mapNames, h[0]);
                if (!_disc.Exists(next) || !_disc.Exists(next.Substring(0, next.Length - 4) + ".TFS")) continue;
                Debug.Log("Door " + trigger + " in " + _mapNames[_mapId] + " -> " + _mapNames[h[0]] + " exit " + h[1] + (hits.Count > 1 ? "  (script has " + hits.Count + " possible targets, using the first)" : ""));
                StartCoroutine(ChangeMap(next, h[1]));
                return;
            }
            Say("Trigger " + trigger + " in " + _mapNames[_mapId] + " is a script event (no door found). Not supported yet.");
        }

        void TryExit(int exitIndex)
        {
            var w = _map.Warps[exitIndex];
            if (w.TargetMap >= _mapNames.Count || w.TargetExit > 9)
            {
                Say("This exit leads to a story scene, which isn't built yet.");
                _armed = false;
                return;
            }
            string next = MapCatalog.PathFor(_mapNames, w.TargetMap);
            if (!_disc.Exists(next) || !_disc.Exists(next.Substring(0, next.Length - 4) + ".TFS"))
            {
                Say("Map file not found: " + next);
                _armed = false;
                return;
            }
            StartCoroutine(ChangeMap(next, w.TargetExit));
        }

        IEnumerator ChangeMap(string path, int exit)
        {
            _changing = true;
            if (_anim != null) _anim.SetSlot(IdleSlot);
            for (float t = 0; t < 1f; t += Time.deltaTime / Mathf.Max(0.01f, FadeSeconds)) { _fade = t; yield return null; }
            _fade = 1f;
            try { LoadMap(path, exit); }
            catch (Exception e) { _error = e.Message; Debug.LogException(e); }
            yield return null;
            for (float t = 1f; t > 0f; t -= Time.deltaTime / Mathf.Max(0.01f, FadeSeconds)) { _fade = t; yield return null; }
            _fade = 0f;
            _changing = false;
            int arrive = _arrive; _arrive = 255;
            if (_intro && _mapId >= 0 && _mapNames[_mapId] == "TWNA01") FinishIntro();
            else if (arrive != 255) StartScript(arrive, null);
        }

        void Say(string text) { _message = text; _messageUntil = Time.time + 3f; }

        // ------------------------------------------------------------------ camera scrolling

        Vector2 MapPixel(Vector3 unityPos)
        {
            Vector3 q = ToGame(unityPos) - _vpG;
            float z = Vector3.Dot(q, _fG);
            return new Vector2(_map.PixelWidth * 0.5f + _h * Vector3.Dot(q, _rG) / z,
                               _map.PixelHeight * 0.5f + _h * Vector3.Dot(q, _dG) / z);
        }

        void ApplyScroll(Vector2 shiftSign, out Vector2 expectedViewport, Vector3 focusPoint)
        {
            float winH = WindowHeightPx, winW = winH * _cam.aspect;
            _cam.sensorSize = new Vector2(24f * _cam.aspect, 24f);
            _cam.focalLength = _h * 24f / winH;

            Vector2 c = MapPixel(focusPoint);
            float cx = _map.PixelWidth <= winW ? _map.PixelWidth * 0.5f : Mathf.Clamp(c.x, winW * 0.5f, _map.PixelWidth - winW * 0.5f);
            float cy = _map.PixelHeight <= winH ? _map.PixelHeight * 0.5f : Mathf.Clamp(c.y, winH * 0.5f, _map.PixelHeight - winH * 0.5f);
            float dx = cx - _map.PixelWidth * 0.5f, dy = cy - _map.PixelHeight * 0.5f;
            _cam.lensShift = new Vector2(shiftSign.x * dx / winW, shiftSign.y * -dy / winH);

            // where the focus point should land on screen if the sign choice is right
            expectedViewport = new Vector2((c.x - (cx - winW * 0.5f)) / winW, 1f - (c.y - (cy - winH * 0.5f)) / winH);
        }

        void LateUpdate()
        {
            if (_player == null || _map == null) return;
            Vector3 focus = (_camOn ? _camPos : _player.position) + Vector3.up * 1f;
            if (_battle != null && _battle.Active) focus = _battleFocus;       // unsetCameraFollowPlayer: the camera stays where it was for the whole fight

            if (!_calibrated)
            {
                // Try the four sign combinations of the lens shift and keep the one that puts the Digimon where it should be.
                float best = float.MaxValue;
                foreach (var sx in new[] { 1f, -1f })
                    foreach (var sy in new[] { 1f, -1f })
                    {
                        Vector2 exp;
                        var s = new Vector2(sx, sy);
                        ApplyScroll(s, out exp, focus);
                        Vector3 vp = _cam.WorldToViewportPoint(focus);
                        float err = (new Vector2(vp.x, vp.y) - exp).sqrMagnitude;
                        if (err < best) { best = err; _shiftSign = s; }
                    }
                _calibrated = true;
            }
            Vector2 e2;
            ApplyScroll(_shiftSign, out e2, focus);
            if (_shadow != null) _shadow.position = new Vector3(_player.position.x, 0.03f, _player.position.z);
            if (_shadow2 != null && _partner != null) _shadow2.position = new Vector3(_partner.position.x, 0.03f, _partner.position.z);
        }

        string SpeakerName(int id, out Color c)
        {
            c = UiKit.Yellow;
            if (id == 0xFD) { c = new Color(0.55f, 0.65f, 1f); return _menus != null ? _menus.PlayerName : PlayerNameText; }
            if (id == 0xFC) { c = new Color(0.55f, 0.65f, 1f); return _menus != null ? _menus.PartnerName : PartnerNameText; }
            foreach (var n in _npcs) if (n.Data.Script == id) return n.Name;
            return "";
        }

        float _hintBottom = 34f;    // where the top help text ends (it wraps on a narrow Game view)

        void OnGUI()
        {
            DrawScreen();
            DrawCareOverlay();
            DrawDebugMenu();        // last, so nothing covers it
        }

        void DrawScreen()
        {
            if (!string.IsNullOrEmpty(_error))
            {
                GUI.Label(new Rect(10, 10, 1400, 24), "Error: " + _error);
                return;
            }
            if (_map != null && _player != null)
            {
                string name = Path.GetFileNameWithoutExtension(MapPath);
                string pn = _curPalette == 0 ? "day" : _curPalette == 1 ? "evening" : "night";
                string hint = "Map " + name + "   " + ((int)_clock).ToString("00") + ":" + ((int)((_clock % 1f) * 60f)).ToString("00") + " " + pn + " (T = +1 hour)   |   Arrow keys/WASD walk, Space = action, F1 = walking grid, F2 = foreground on/off, F3 = names, Tab = menu, F4 = stats, I = bag"
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    + ", F5 = shop (test), F6 = +1000 Bits and items (test), [ ] = partner animation slots, F7 = recruit Coelamon (test), F10 = debug menu, F11 = save, F12 = load"
#endif
                    + "   |   grid value here: " + _map.CollisionAt(_player.position.x / Unit, _player.position.z / Unit);
                float hintW = Screen.width - 20f, hintH = GUI.skin.label.CalcHeight(new GUIContent(hint), hintW);
                GUI.Label(new Rect(10, 10, hintW, hintH), hint);
                _hintBottom = 10f + hintH;
            }
            if (_showStats) DrawStats();
            if (_setup != null && _setup.Active) { _setup.OnGUI(); return; }
            if (_menus != null) _menus.OnGUI();
            if (_battle != null) _battle.OnGUI(_cam);
            if (ShowNames && _player != null && _map != null && !_changing && (_menus == null || !_menus.Active))
            {
                DrawName(_player, _playerName, new Color(1f, 0.95f, 0.5f));
                DrawName(_partner, _partnerName, new Color(0.7f, 1f, 0.7f));
                foreach (var n in _npcs) DrawName(n.T, n.Name, n.Data.FollowMode == 1 ? Color.white : new Color(1f, 0.6f, 0.6f));
            }
            if (_runner != null && (_line != null || _choiceOpts != null))
            {
                UiKit.Begin();
                var r = new Rect(135, 730, 1152, 275);
                UiKit.Box(r, new Color(0.10f, 0.17f, 0.15f, 0.93f));
                Color spc; string spk = SpeakerName(_speaker, out spc);
                float ty = r.y + 28;
                if (spk.Length > 0) { UiKit.Text(new Rect(r.x + 32, r.y + 12, 800, 60), spk, 48, spc); ty = r.y + 72; }
                string body = _line ?? "";
                UiKit.Text(new Rect(r.x + 32, ty, r.width - 64, r.height - (ty - r.y) - 10), body, 46, Color.white, TextAnchor.UpperLeft, true);
                if (_choiceOpts != null)
                {
                    float oy = ty + (body.Length > 0 ? 62 : 0);
                    for (int i = 0; i < _choiceOpts.Count; i++)
                        UiKit.Text(new Rect(r.x + 32, oy + i * 54, r.width - 64, 54), (i == _choiceSel ? "> " : "   ") + _choiceOpts[i], 46, i == _choiceSel ? UiKit.Yellow : Color.white);
                }
                else if ((int)(Time.time * 2f) % 2 == 0) UiKit.Text(new Rect(r.xMax - 90, r.yMax - 70, 60, 50), "X", 40, new Color(0.5f, 0.6f, 1f), TextAnchor.MiddleCenter);
                UiKit.End();
            }
            if (!string.IsNullOrEmpty(_message) && Time.time < _messageUntil) GUI.Label(new Rect(10, _hintBottom, Screen.width - 20, 24), _message);
            if (_fade > 0.001f)
            {
                var old = GUI.color;
                GUI.color = new Color(0, 0, 0, Mathf.Clamp01(_fade));
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
                GUI.color = old;
            }
        }
    }
}
