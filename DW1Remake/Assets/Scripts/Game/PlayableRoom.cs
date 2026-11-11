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
        const float FadeSeconds = 1f;          // screen fade when changing map: fadeToBlack(20) / fadeFromBlack(20), 20 game frames (tamerTickChangeMap, scriptTickChangeMap)

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
            public Transform T; public MeshRenderer R, Blend; public MapOverlay.Inst I; public int Cur, Timer; public float Dm;
        }
        float _clock = -1f;                    // hours, 0..24; a copy of the game clock in _care
        float _clockFrames;                    // time towards the next game frame (20 a second)
        PartnerCare _care;
        GameAudio _audio;
        int _curPalette;
        float _lightFactor = 1f;
        readonly List<Light> _lightList = new List<Light>();
        readonly List<float> _lightBase = new List<float>();
        MapScripts _scripts;
        int _mapId;
        // the map number to load: two map entries share a file (65 and 232 are both YAKA25; 232 is the one YAKA11B leads to, script 188, with
        // the way out on tile 81; 65 uses script 5, an empty table), so the number cannot be found again from the file name (user 2026-11-11)
        int _wantMapId = -1;
        string PathOfMap(int id) { _wantMapId = id; return MapCatalog.PathFor(_mapNames, id); }
        MapBackground _map;
        Camera _cam;
        Transform _player;
        DigimonAnimator _anim;
        DropShadow _dropShadow;      // renderDropShadow: the game's shadow under every Digimon and dropped item
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
            DigimonAnimator.Paused = false;
            if (s_toTitle) { s_toTitle = false; TitleScreen = true; QuickStart = false; NewGame = false; LoadSaveOnStart = false; }
#if !UNITY_EDITOR && !DEVELOPMENT_BUILD
            // a player build always starts at the title screen (the scene's test ticks are for the editor), with no names over the Digimon
            TitleScreen = true; QuickStart = false; NewGame = false; LoadSaveOnStart = false; ShowNames = false;
#endif
#if UNITY_WEBGL && !UNITY_EDITOR
            // the browser version: the HD-2D look is off (it is too heavy for many browsers), the PS1 look as the original
            Hd2D = false;
            // the web uses the "Mobile" quality level, whose 0.8 render scale needs an upscaling pass, and the URP post-processing shaders
            // are not in the web build: the camera's picture came out black (user 2026-11-10). Full scale and no post-processing, as with
            // HD-2D off on the PC
            var rp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (rp != null) rp.renderScale = 1f;
            foreach (var c in Camera.allCameras)
            {
                var ad = c.GetUniversalAdditionalCameraData();
                if (ad != null) ad.renderPostProcessing = false;
            }
#endif
            string why, path = StartDiscPath(out why);
            string err = path != null ? OpenDisc(path) : null;
            if (_disc == null)
            {
                // no usable disc yet: the disc screen (PlayableRoomDisc); the browser version first looks for the disc kept from a last visit
                _picker = new DiscPicker { Path = path ?? "", Error = err ?? why };
#if UNITY_WEBGL && !UNITY_EDITOR
                _webReading = true; _webNote = "Looking for the disc you chose before ...";
                WebFiles.LoadKeptDisc(gameObject.name, "OnWebDisc");
#endif
                return;
            }
            try { Load(); }
            catch (Exception e) { _error = e.Message; Debug.LogException(e); }
        }

        void OnDestroy()
        {
            if (_otHooked) UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering -= OtFlatten;
            if (_fs != null) _fs.Dispose();
        }

        void Load()
        {
            _mapNames = MapCatalog.Load(_disc);
            _mapFlags = MapCatalog.LoadFlags(_disc);
            MapCatalog.LoadImageCounts(_disc, out _num8, out _num4);
            _clock = StartHour;
            _scripts = MapScripts.Load(_disc);
            var catalog = ItemCatalog.Load(_disc);
            _itemCatalog = catalog;
            _menus = new GameMenus(_state, catalog);
            _menus.Sound = n => { if (_audio != null) _audio.Play(0, n); };
            _menus.BuildBoyModel = () =>
            {
                if (_entries == null) _entries = new List<DigimonEntry>(DigimonCatalog.Load(_disc));
                if (_allTim == null) _allTim = _disc.ReadFile("/CHDAT/ALLTIM.TIM");
                DigimonEntry e = null;
                foreach (var x in _entries) if (x.Type == 0) { e = x; break; }
                return e != null ? MmdModel.Parse(_disc.ReadFile(e.MmdPath)).Build(e, _allTim) : null;
            };
            _menus.BuildPartnerModel = () =>
            {
                if (_entries == null) _entries = new List<DigimonEntry>(DigimonCatalog.Load(_disc));
                if (_allTim == null) _allTim = _disc.ReadFile("/CHDAT/ALLTIM.TIM");
                DigimonEntry e = null;
                foreach (var x in _entries) if (x.Type == PartnerType) { e = x; break; }
                return e != null ? MmdModel.Parse(_disc.ReadFile(e.MmdPath)).Build(e, _allTim) : null;
            };
            _menus.Icons = ItemIcons.Load(_disc);
            _battle = new Battle(_state, FaceRot);
            _battle.Art = BattleArt.Load(_disc);
            _battle.Tiredness = () => _state.Para.Tiredness;
            _battle.Injured = () => (_state.Para.Condition & PartnerCare.Injured) != 0;
            _battle.SetInjured = () => _state.Para.Condition |= PartnerCare.Injured;
            _battle.Font = GameFont.Load(_disc);
            { var all = TimImage.ParseAll(_disc.ReadFile("/ETCDAT/ETCTIM.BIN")); if (all.Count > 0) Battle.CountdownTim = all[0]; }     // the faint countdown's pictures
            Battle.StatFont = _battle.Font;
            _battle.EndView = BattleEndView.Load(_disc, Battle.StatFont);
            if (_battle.EndView != null) _battle.EndView.Sound = n => { if (_audio != null) _audio.Play(0, n); };
            _battle.OnDrop = (item, at) => SpawnDropped(item, at);
            _battle.Exe = _disc.ReadFile("/SLUS_010.32");
            if (_disc.Exists("/BTL_REL.BIN") && _disc.Exists("/SLUS_010.32")) _battle.LoadTextTables(_disc.ReadFile("/BTL_REL.BIN"), _disc.ReadFile("/SLUS_010.32"));
            if (_disc.Exists("/BTL_REL.BIN")) _battle.LoadAura(_disc.ReadFile("/BTL_REL.BIN"));
            // condition effects (BattleStatus.cs): translateConditionFXToEntity, and the map's lights with GsSetAmbient(0x800) for the lit confusion model
            _battle.GameDisc = _disc;
            _battle.FxPoint = ConditionFxPoint;
            _battle.MapLights = () => _map == null ? null : new PsxTmd.Lights
            {
                Dir = new[] { _map.LightDir[0].normalized, _map.LightDir[1].normalized, _map.LightDir[2].normalized },
                Col = new[] { _map.LightColor[0], _map.LightColor[1], _map.LightColor[2] },
                Ambient = 0x800 / 4096f
            };
            _battle.ItemName = i => i >= 0 && i < catalog.Count ? catalog[i].Name : "";
            _battle.OnOpenBag = () => _menus.OpenBag();
            _battle.Icons = BattleIcons.Load(_disc);
            try
            {
                EffectPlayer.FacingOffset = FacingOffset; _battle.Effects = new EffectPlayer(_disc, id => { if (_audio != null) _audio.Play(8, id); });
                // BTL_disableMapLayer (setMapLayerEnabled 0): the map picture and its pieces are not drawn while the effect runs
                _battle.Effects.OnMapLayer = on =>
                {
                    if (_backdrop != null) _backdrop.SetActive(on);
                    if (_overlayRoot != null) _overlayRoot.SetActive(on && ShowOverlays);
                };
            }
            catch (Exception e) { Debug.LogWarning("Technique effects not available: " + e.Message); }
            _menus.BIcons = _battle.Icons;
            _menus.Faces = Portraits.Load(_disc);
            _menus.PartnerInfo = DigimonStatsCatalog.Get(_disc, PartnerType);
            if (_menus.PartnerInfo != null && _menus.PartnerInfo.Learnable.Count > 0)
            {
                int first = _menus.PartnerInfo.Learnable[0];       // Agumon starts with Spit Fire, learned and equipped
                _state.Techs[first] = true;
                if (!_state.TechSet.Contains(first)) { int hole = _state.TechSet.IndexOf(-1); if (hole >= 0) _state.TechSet[hole] = first; else _state.TechSet.Add(first); }
            }
            _state.Raised[PartnerType] = true;
            _care = new PartnerCare(_state, t => DigimonStatsCatalog.RaiseOf(_disc, t), c => DigimonStatsCatalog.SleepOf(_disc, c), t => DigimonStatsCatalog.Get(_disc, t).Level);
            _state.DailyTrigger(n => UnityEngine.Random.Range(0, n));      // a game starts with a day's cards
            _care.OnNewDay = () => { _dayCount++; _state.DailyTrigger(n => UnityEngine.Random.Range(0, n)); };      // dailyPStatTrigger
            _care.EvoItemTarget = i => DigimonStatsCatalog.EvoItemTarget(_disc, i);
            _care.ReqOf = t => DigimonStatsCatalog.ReqOf(_disc, t);
            _care.GainOf = t => DigimonStatsCatalog.GainOf(_disc, t);
            _care.InfoOf = t => DigimonStatsCatalog.Get(_disc, t);
            _care.TechOf = DigimonStatsCatalog.TechOf;
            try { _audio = new GameAudio(_disc, _state, gameObject); _audio.SetPartner(PartnerType); }
            catch (Exception e) { Debug.LogWarning("Sound not available: " + e.Message); }
            if (_audio != null)
            {
                _state.OnSound = _audio.Play;
                _battle.Sfx = n => _audio.Play(0, n);
                _battle.OnStopMusic = _audio.StopMusic;
                _state.OnBgm = id => _audio.SetBgm(id, _care.Hour);
                _menus.Sfx = n => _audio.Play(0, n);
                DigimonAnimator.SoundSink = (a, bank, val) =>
                {
                    Vector3 v = _cam != null ? _cam.WorldToViewportPoint(a.transform.position) : Vector3.zero;
                    if (v.x >= 0f && v.x <= 1f && v.y >= 0f && v.y <= 1f) _audio.PlayAnim(a.DigimonType < 0 ? 0 : a.DigimonType, bank, val);   // only what is on screen is heard
                };
            }
            _state.OnSpawnItem = SpawnItem;
            _state.OnDamage = dmg => { if (_battle != null && _partner != null) _battle.AddMapText(_cam, _partner.position + Vector3.up * 0.9f, dmg); };
            _state.OnSpawnChest = (item, x, y, z, rot, trigger) => SpawnChest(item, x, z, rot, trigger);
            _state.OnBlockTiles = (x, y, w, h) => _blocks.Add(new RectInt(x, y, w, h));
            _menus.OnCommand = CareCommand;
            _menus.OnFeed = FeedItem;
            _menus.InBattle = () => _battle != null && _battle.Active;
            _menus.ThrowBusy = () => _battle != null && _battle.ThrowBusy;
            _menus.CanSleep = () => (_state.Para.Condition & PartnerCare.Sleepy) != 0;
            _menus.FishingAvail = FishAvailability;      // addGameMenu: the eighth option on the fishing maps (PlayableRoomFishing)
            _menus.OnFish = FishStart;
            _care.SetClock((int)StartHour, 0);
            _clock = _care.ClockHours;
            _care.InitPartner(PartnerType);
            _menus.Art = MenuArt.Load(_disc);
            _menus.Tri = TriangleMenuView.Load(_disc, Battle.StatFont);
            if (_menus.Tri != null) _menus.Tri.Sound = n => { if (_audio != null) _audio.Play(0, n); };
            _menus.BoyOrigin = () => BoySquare();
            _menus.Inv = InventoryView.Load(_disc, Battle.StatFont, _state, _itemCatalog, _menus.Icons);
            if (_menus.Inv != null) { _menus.Inv.Sound = n => { if (_audio != null) _audio.Play(0, n); }; _menus.Inv.Art = _battle != null ? _battle.Art : null; }
            _menus.Care = _care;
            _menus.ItemFunction = id => _care.ItemFunction(id);       // a thrown item in a fight: ITEM_FUNCTIONS[item] (tickThrownItem)
            {
                byte[] exe = _disc.ReadFile("/SLUS_010.32"); uint load = System.BitConverter.ToUInt32(exe, 0x18);
                for (int i = 0; i < 4; i++) PartnerCare.HealingValues[i] = System.BitConverter.ToInt16(exe, (int)(0x8013435C - load + 0x800) + 2 * i);   // HEALING_VALUES
                System.Array.Copy(exe, (int)(0x8012FE78 - load + 0x800), _state.RecyclableItems, 0, 0x4E);      // RECYCLABLE_ITEMS
            }
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
            _state.TournamentNameOf = TournamentName;
            _state.BgmNameOf = BgmTrackName;                // BGM_TRACK_NAMES (text code 0x11, the jukebox)      // TOURNAMENT_NAMES (text code 0x12, PlayableRoomTournament)
            _state.TechNameOf = n => { DigimonStatsCatalog.Get(_disc, PartnerType); return DigimonStatsCatalog.NameOf(n); };     // MOVE_NAMES (exe 0x80126054)
            // what the script commands read: YEAR and DAY (30 days a year) from the days played, the map, the partner, DIGIMON_DATA's type byte
            _state.MapIdNow = () => _mapId;
            _state.PartnerTypeNow = () => PartnerType;
            _state.AttributeOf = t => { var inf = DigimonStatsCatalog.Get(_disc, t); return inf != null ? inf.Attribute : 0; };
            _state.DateNow = () => new[] { _dayCount / 30, _dayCount % 30, _care.Hour, _care.Minute };
            _state.AdvanceDate = (y, d, h, m) =>
            {
                int now = ScriptState.DateToMinutes(_dayCount / 30, _dayCount % 30, _care.Hour, _care.Minute), to = ScriptState.DateToMinutes(y, d, h, m);
                if (now < to) _care.SkipHours((to - now) / 60);
                _dayCount = y * 30 + d;
                _care.SetClock(h, m);
                _clock = _care.ClockHours;
            };

            if (WantTitle) { _title = new TitleState(); StartCoroutine(Title()); }     // the title screen; it calls StartGame
            else StartGame();
        }

        // the game itself: the boy, the partner and the first map (a new game, a save, or Quick Start)
        void StartGame()
        {
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
            // a script animation's forward travel (bone 0) moves the boy for real, as a map Digimon's does (TickActors, _playerScripted): at Coela Point
            // each jump (animation 0x15) carries him on past the 0x6C glide's point onto the Coelamon, and the next jump starts from there
            // (user's PS1 clips 2026-11-04: the 2nd glide ends 4 m short of the 2nd Coelamon, yet he lands on it and takes off from it)
            if (_anim != null) _anim.BodyMovesItself = true;
            MmdModel.UseLit = false;
            MmdModel.Brightness = 1f;

            if (HasPartner) BuildPartner();

            if (NewGame)
            {
                int ti = _mapNames.IndexOf("TOPN01");        // the opening takes place here
                if (ti >= 0) MapPath = PathOfMap(ti);
                _state.SetFlag(54, true); _state.SetFlag(55, true);      // the same flags as the game's own "Opening mode"
            }
            else if (loadSave)
            {
                ApplySave(sv);
                int mi = SavedMapId(sv);
                if (mi >= 0) MapPath = PathOfMap(mi);
            }
            else if (QuickStart)
            {
                QuickStartState();
                int ti = _mapNames.IndexOf("TWNA01");
                if (ti >= 0) MapPath = PathOfMap(ti);
            }
            else foreach (var f in InitialFlags) _state.SetFlag(f, true);
            LoadMap(MapPath, NewGame ? 9 : (QuickStart && !loadSave) ? 2 : -1);
            if (NewGame) BeginNewGame();
            else if (loadSave) PlaceFromSave(sv);
            else if (QuickStart && _partner != null) { _partner.gameObject.SetActive(true); PlacePartner(_player.position, Vector3.back); }
            if (Hd2D) SetupHd2DOnce();
            if (!NewGame) RunMapStart(255);
        }

        // ------------------------------------------------------------------ map Digimon (NPCs)

        sealed class Npc
        {
            public MapDigimon Data; public Transform T; public DigimonAnimator Anim; public string Name;
            public List<Vector3> Path = new List<Vector3>();     // its waypoints (Unity); for a wait section the game uses the raw x as the frame count
            public List<int> WaitFrames = new List<int>();
            // MapDigimonPath.activeSection, waypointWaitTimer, animation (0 when loaded), hasWaypointTarget
            public int Section, WaitTimer, AiAnim = 0; public bool HasTarget;
            public bool Scripted;       // a script started its animation (0x56); the AI leaves it until it starts one of its own
            public Vector3 Home; public bool Chasing;
            public int LookState; public Vector3 TrackTarget; public bool RanIntoBoy;     // MapDigimonEntity.lookAtTamerState, targetLocation (the follow modes)
            public int AutoTalk;        // NPC_ENTITIES.autotalk (0x47's third byte, 0x6A): 1 = its script runs the moment the boy touches it
            public int Record;          // its record in the map file: ENTITY_TABLE[2 + Record]
            public bool Stopped;        // MapDigimonEntity.stopAnim: it spoke in the running script, its AI waits for the script to end
        }
        readonly List<Npc> _npcs = new List<Npc>();
        // NPC_ACTIVE_ANIM: scriptStartAnimation and scriptSetEntityWalking (0x56, 0x4E, 0x51-0x53) call setActiveAnim for a map Digimon, and from then on
        // NPCEntityTickOverworld runs none of its AI (no waypoints, no tracking, no idle) until the map is loaded again. A script walk ends in animation 0.
        readonly HashSet<int> _aiOff = new HashSet<int>();
        void NpcAiOff(Transform t) { foreach (var n in _npcs) if (n.T == t) _aiOff.Add(n.Record); }
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
            public Transform WalkTarget;      // tickEntityWalkTo with a target entity: its place is read again every frame, and touching anyone ends the walk
            public bool HasTurn; public Quaternion TurnTo;
            public bool Looking; public Vector3 LookPos; public float LookAcc; public int LookVy; public bool LookVyKnown;    // tickLookAtEntity: ROTATION_DATA, 0x200 a game frame
            public bool Gliding; public Vector3 GlideStep; public float GlideAcc;    // tickEntityMoveTo: a fixed step each game frame, height kept
            public int GlideAxis = -1; public bool GlideCam, WalkCam;   // WalkCam: 0x52 / 0x53, tickEntityWalkTo with the camera (moveCameraByDiff)                         // tickEntityMoveToAxis: only that axis (0 x, 1 y, 2 z); 0x73 moves the camera with it
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

        int _waitFor = 0xFF;

        // WAIT_FOR_ENTITY's test: 0xFF every movement slot empty (and the sync bit); 0xC9 / 0xD5 the sync bit only; 0xD6 training loaded (ours at once);
        // 0xC8 slot 10, the camera; 0xCA-0xD4 slots 11-21, doors and moving map objects (ours: all of them together); anyone else that Digimon's
        // slot; an id that is not on the map is 0xFF (scriptIdToEntityId), so it waits for everything
        bool BusyFor(int who)
        {
            if (who == 0xFF) return ActorsBusy();
            if (who == 0xC9 || who == 0xD5) return !_syncBit;
            if (who == 0xD6) return false;
            if (who == 0xC8) return _camMove || (_camOn && !_pBusy && (_camPos - _camGoal).sqrMagnitude > 0.0004f);
            if (who >= 0xCA && who <= 0xD4) return ObjectsBusy();
            Transform t; DigimonAnimator an;
            if (!ActorOf(who, out t, out an)) return ActorsBusy();
            return _movers.ContainsKey(t);
        }

        bool ActorsBusy()
        {
            return !_syncBit || _movers.Count > 0 || ObjectsBusy() || _camMove || (_camOn && !_pBusy && (_camPos - _camGoal).sqrMagnitude > 0.0004f);
        }

        // runs one cutscene command; true = the script must wait here
        bool ExecCmd(ScriptAction a)
        {
            Transform t, t2; DigimonAnimator an, an2; bool evWait;
            switch (a.Cmd)
            {
                case 0x49: DigimonRoutine(a.Frames); break;
                case 0xFB: ScriptLoadMap(a.Actor, a.Frames); break;
                case 0x64:
                    // 0x64 n = run built-in function n (variable 254 = its parameter, usually an actor)
                    if (a.Frames == 0x37)   // ends the endless lying loop of the actor (the boy waking up)
                    {
                        if (ActorOf(a.Actor, out t, out an) && an != null) { an.ReleaseLoop(); }
                    }
                    else if (a.Frames == 7) { _syncBit = false; Run(Training(_state.Vars[0xFE]), false); _waitAll = true; return true; }
                    else if (a.Frames == 0x1D)       // DOO2_openEggBox: "Which egg do you want?" (script 212); stopSound, playSound(8, 4)
                    {
                        if (_eggBox == null) { _eggBox = EggBoxView.Load(_disc, Battle.StatFont); if (_eggBox != null) _eggBox.Sound = Snd; }
                        _eggOpen = true; _eggSlot = 0;
                        if (_eggBox != null) _eggBox.OpenBox();
                        if (_audio != null) _audio.Play(8, 4);
                        return true;
                    }
                    else if (a.Frames == 0)
                    {
                        // openDiscardItem: choose an item from the bag (Kunemon's "Give food"); the game's item box (PlayableRoomRoutines.DiscardItem)
                        _line = null; _choiceOpts = null; StartCoroutine(DiscardItem()); return true;
                    }
                    else if (a.Frames == 0x0C) { int it = _state.Vars[0xFE]; if (it != 0xFF) _state.RemoveItem(it, 1); }       // take one of the chosen item
                    else if (a.Frames == 0x0D) { int it = _state.Vars[0xFE]; if (it != 0xFF) _state.RemoveItem(it, 0x63); }    // take all of it
                    else if (a.Frames == 0x15) ResetPieceAnimation(_state.Vars[0xF7], _state.Vars[0xF8]);                      // resetMapObjectAnimation(var 247, var 248)
                    else if (a.Frames == 0x1E) _state.ShopVariable = 0;                                                                  // SHOP_VARIABLE = 0
                    else if (a.Frames == 0x1F) _state.ShopVariable += _state.Vars[0xF4] + (_state.Vars[0xF3] << 8);                     // += variables 243/244 (one SHOP_VARIABLE, shown by text code 0x10)
                    else if (a.Frames == 0x38) { _state.Vars[0xF3] = (byte)((_state.ShopVariable / 256) & 0xFF); _state.Vars[0xF4] = (byte)(_state.ShopVariable & 0xFF); }
                    else if (a.Frames == 0x21)
                    {
                        // flags 0x25-0x27 on; variable 2 = 0x16 on day 0x16 else 0x95; variable 3 = 0x16; variable 4 = the partner's type
                        _state.SetFlag(0x25, true); _state.SetFlag(0x26, true); _state.SetFlag(0x27, true);
                        _state.Vars[2] = (byte)(_dayCount % 30 != 0x16 ? 0x95 : 0x16); _state.Vars[3] = 0x16; _state.Vars[4] = (byte)PartnerType;
                    }
                    else if (a.Frames == 0x2D || a.Frames == 0x2E) { StartCoroutine(WhiteFade(a.Frames == 0x2D, _state.Vars[0xFE])); }   // fadeToWhite / fadeFromWhite, over variable 254 frames
                    else if (a.Frames == 0x30) { _line = null; _choiceOpts = null; StartCoroutine(SaveMachine()); return true; }   // openSaveMachine; tickSaveMachine until the menu closes
                    else if (a.Frames == 0x0E) { _line = null; _choiceOpts = null; StartCoroutine(CardShop(_state.Vars[0xFE])); return true; }        // rollCardPack / openBuyCardMenu / openSellCardMenu
                    else if (a.Frames == 0x16) { _line = null; _choiceOpts = null; StartCoroutine(CheckMedals()); return true; }                       // scriptCheckTournamentMedal
                    else if (a.Frames == 0x12) { _line = null; _choiceOpts = null; StartCoroutine(TournamentSchedule()); return true; }               // initTournamentSchedule (PlayableRoomTournament)
                    else if (a.Frames == 0x06) { StartCoroutine(Tournament()); return true; }                                                         // scriptStartTournament
                    else if (a.Frames == 0x0A) { _line = null; _choiceOpts = null; StartCoroutine(BirdraTransport()); return true; }   // tickBirdraTransport
                    else if (a.Frames == 0x09) { _line = null; _choiceOpts = null; StartCoroutine(Jukebox()); return true; }           // openJukebox (Giromon)
                    else if (a.Frames == 0x02) { _line = null; _choiceOpts = null; StartCoroutine(MojyamonTrade()); return true; }     // openMojyamonShop
                    else if (a.Frames == 0x0B) { _line = null; _choiceOpts = null; StartCoroutine(ItemKeeper()); return true; }        // tickItemKeeper
                    else if (a.Frames == 0x01) { _line = null; _choiceOpts = null; StartCoroutine(MeritShop()); return true; }         // openMeritShop (ShogunGekomon, GKYO02)
                    else if (a.Frames == 0x03 || a.Frames == 0x04 || a.Frames == 0x05 || a.Frames == 0x11) { }                    // the game does nothing for these
                    else if (EventBuiltin(a.Frames, out evWait)) return evWait;                                                      // the story events (PlayableRoomEvents)
                    else if (a.Frames == 8 && _state.GetFlag(3)) { _line = null; _choiceOpts = null; StartCoroutine(RecycleShop(_state.Vars[0xFE])); return true; }   // openRecycleShop
                    else if (a.Frames == 0x0F) { var ib = ItemBox(); if (ib != null) ib.OpenBits(DialogueOrigin(0xFD), ShopBitsText(ShopSection(_state.Vars[0xFE]))); }   // createShopBitsBox(1)
                    else if (a.Frames == 8)     // openShop: the script switched on flag 0x180 + item for each item it sells; variable 254 = the shopkeeper
                    {
                        _line = null; _choiceOpts = null;
                        StartCoroutine(Shop(i => _state.GetFlag(0x180 + i), _state.Vars[0xFE]));
                        return true;
                    }
                    else Debug.Log("[script] built-in routine 0x64 " + a.Frames.ToString("X2") + " not ported");
                    break;
                case 0x66:   // the fight: Frames = variable 250 (IS_PREDEFINED_BATTLE); a predefined fight's enemies are variables 251-253, any other is the Digimon talked to
                {
                    // SCRIPT_OP_START_BATTLE / handleBattleStart read variables 251-253 only when variable 250 is set: they keep whatever an earlier
                    // scripted fight left in them (the scripts set 250 back to 0 but not those), so a wild fight must not look at them
                    Npc foe = null;
                    if (a.Frames != 0 && a.Actor != 0 && a.Actor != 0xFF) foreach (var n in _npcs) if (n.Data.Script == a.Actor && n.T != null) { foe = n; break; }
                    if (foe == null) foe = _talkingTo;
                    if (foe != null && foe.T != null && _battle != null && !_battle.Active)
                    {
                        _line = null; _choiceOpts = null; _battleWait = true;
                        StartBattle(foe, a.Frames != 0);
                        foreach (int id in new[] { (int)_state.Vars[0xFC], (int)_state.Vars[0xFD] })     // the second and third enemy of a scripted fight
                        {
                            if (a.Frames == 0 || id == 0 || id == 0xFF) continue;
                            foreach (var n in _npcs)
                                if (n.Data.Script == id && n.T != null && n != foe) { _battle.AddEnemy(n.T, n.Anim, n.Name, DigimonStatsCatalog.Get(_disc, n.Data.Type), n.Data); break; }
                        }
                        if (a.Frames != 0)
                            foreach (var n in _npcs)
                            {
                                if (n == foe || n.T == null || !n.T.gameObject.activeInHierarchy) continue;
                                bool named = n.Data.Script == _state.Vars[0xFB] || n.Data.Script == _state.Vars[0xFC] || n.Data.Script == _state.Vars[0xFD];
                                if (named) continue;
                                if (!OnScreen(n)) { n.T.gameObject.SetActive(false); _offAtFight.Add(n.T); } else RunAway(n);
                            }
                        // SCRIPT_OP_START_BATTLE: with variable 250 set, each of 251-253 that is not 0xFF becomes its entity number (scriptIdToEntityId:
                        // 0xFD the boy 0, 0xFC the partner 1, a map Digimon 2 + its slot, else 0xFF) and stays so after the fight
                        if (a.Frames != 0)
                            for (int v = 0xFB; v < 0xFE; v++)
                            {
                                int id = _state.Vars[v];
                                if (id == 0xFF) continue;
                                int e = id == 0xFD ? 0 : id == 0xFC ? 1 : 0xFF;
                                if (e == 0xFF) foreach (var n in _npcs) if (n.T != null && n.Data.Script == id) { e = 2 + n.Record; break; }
                                _state.Vars[v] = (byte)e;
                            }
                        return true;
                    }
                    break;
                }
                case 0x6A: foreach (var n in _npcs) if (n.Data.Script == a.Actor) n.AutoTalk = a.Frames; break;
                case 0x57: SetPieceVisible(a.Actor, a.Frames); break;
                case 0x47: ScriptSetDigimon(a.Actor, a.Target, a.Frames); break;
                case 0x54:      // resetEntityOrigin: its home and its waypoints move by how far it is from home now
                    foreach (var n in _npcs)
                        if (n.Data.Script == a.Actor && n.T != null)
                        {
                            Vector3 delta = n.T.position - n.Home; delta.y = 0f;
                            n.Home += delta;
                            for (int i = 0; i < n.Path.Count && i < 8; i++)      // only walking waypoints move; a wait's x is its frame count
                            {
                                if (n.Data.AiSections[i] == -1) break;
                                if (n.Data.AiSections[i] != 0) n.Path[i] += delta;
                            }
                            break;
                        }
                    break;
                case 0x48:   // an actor leaves the map
                    foreach (var n in _npcs) if (n.Data.Script == a.Actor && n.T != null) { n.T.gameObject.SetActive(false); break; }
                    break;
                case 0x67: _wait = a.Frames / 20f; return _wait > 0f;
                case 0x4A:
                    // WAIT_FOR_ENTITY 0xD5 first clears SOME_SCRIPT_SYNC_BIT, then waits for a scene to set it (the death map's sections 71-73 wait for DOOA / MURD)
                    if (a.Actor == 0xD5) _syncBit = false;
                    _waitFor = a.Actor; _waitAll = BusyFor(_waitFor); return _waitAll;
                case 0x26:
                    // SET_TEXTBOX_SIZE: scriptPauseGame(0xFF), then scriptSetTextboxSize makes box 0 of its own size (Shell's newspaper, the fishing
                    // and dirt-job explanations); the texts after it go into that box until a 0x1B or 0x10
                    _customBox = new Vector3Int(a.Target, a.Frames, a.Actor);
                    SetDialogueOwner(0xFF);
                    _line = null; _choiceOpts = null;
                    if (_dlg != null) { _dlg.SetupCustom(a.Target, a.Frames, DialogueOrigin(a.Actor)); _dlgKey = null; }
                    break;
                case 0x27:
                    // FADEOUT_HUD (closeBox): box 0 is the dialogue box, which goes at once (removeStaticUIBox); the next text grows a new one
                    if (a.Actor == 0) { _line = null; _lineCustom = false; _choiceOpts = null; if (_dlg != null && _dlg.Showing) _dlg.Shut(); _dlgKey = null; }
                    else if (a.Actor == 2 && _itemBox != null) _itemBox.Close(_itemBox.Bits);
                    _wait = 1f / 20f; return true;
                case 0x4C:
                    if (ActorOf(a.Actor, out t, out an) && ActorOf(a.Target, out t2, out an2))
                    {
                        // tickLookAtEntity: the first frame keeps where the target stands (ROTATION_DATA), then every frame rotateEntity turns him 0x200
                        // towards it (TickActors); standing on the target, _atan(0, 0) is 0x800
                        var m = MoverFor(t, an); m.Looking = true; m.LookPos = t2.position; m.LookAcc = -1f / 20f; m.LookVyKnown = false; m.HasTurn = false;
                        // Ours (user's choice 2026-11-09, PROGRESS 5c): Vegiemon's recruit scene hops it with animation 0x1C, which has no loop, and
                        // never starts idle again, so the PS1 holds the hop's last pose through the whole talk. When it turns to the boy after the
                        // hops, idle starts once the hop ends. Vegiemon only (DIGIMON_DATA types 25 and 147)
                        if (an != null && an.Slot == 0x1C)
                            foreach (var n in _npcs)
                                if (n.Anim == an && n.Data != null && (n.Data.Type == 25 || n.Data.Type == 147)) { an.QueueSlot(0, false); break; }
                    }
                    break;
                case 0x4D:
                    if (ActorOf(a.Actor, out t, out an))
                    {
                        // ENTITY_SET_ROTATION: its movement slot becomes tickEntitySetRotation, which sets rotation.vy at once and is done
                        _movers.Remove(t);
                        t.rotation = Quaternion.Euler(0, a.Angle * 360f / 4096f, 0) * Quaternion.Euler(0, FacingOffset, 0);
                    }
                    break;
                case 0x4E:
                    if (ActorOf(a.Actor, out t, out an))
                    {
                        var m = MoverFor(t, an); m.Goal = new Vector3(a.X * Unit, 0, a.Z * Unit); m.Walking = true; m.Still = 0f;
                        NpcAiOff(t);
                        if (an != null) an.SetSlot(ScriptWalkSlot(a.Actor, a.Anim));
                    }
                    break;
                case 0x50:
                    if (ActorOf(a.Actor, out t, out an))
                    {
                        // MOVE_CAMERA_TO_ENTITY (tickCameraMoveToEntity -> tickCameraMovement): where it stands when the move starts, held inside the map
                        FreezeCamera();
                        StartCamMove(t.position, a.Frames);
                    }
                    break;
                case 0x51: case 0x53: case 0x52:
                    if (ActorOf(a.Actor, out t, out an))
                    {
                        Vector3 goal = new Vector3(a.X * Unit, 0, a.Z * Unit);
                        t2 = null;
                        if (a.Cmd != 0x52) { if (!ActorOf(a.Target, out t2, out an2)) break; goal = t2.position; }
                        var m = MoverFor(t, an); m.Goal = goal; m.Walking = true; m.Still = 0f; m.WalkTarget = a.Cmd != 0x52 ? t2 : null;
                        NpcAiOff(t);
                        if (an != null) an.SetSlot(ScriptWalkSlot(a.Actor, a.Anim));
                        // 0x52 / 0x53: moveCameraByDiff every frame, the camera moves as far as the walker does
                        m.WalkCam = a.Cmd != 0x51;
                        if (m.WalkCam) FreezeCamera();
                    }
                    break;
                case 0x6D: case 0x6F:
                    if (ActorOf(a.Actor, out t, out an) && ActorOf(a.Target, out t2, out an2)) { var m = MoverFor(t, an); m.Goal = t2.position; m.Walking = true; m.Still = 0f; }
                    break;
                case 0x72: case 0x73:
                    if (ActorOf(a.Actor, out t, out an))
                    {
                        // ENTITY_MOVE_TO_AXIS (_WITH_CAMERA), tickEntityMoveToAxis: (value - position on that axis) / speed in whole game units each frame,
                        // the first frame only works it out; done (and put on the value) once it is past it. Not a walk: the Digimon keeps its animation.
                        var m = MoverFor(t, an);
                        Vector3 pos = t.position;
                        int axis = a.Target, cur = axis == 0 ? Mathf.RoundToInt(pos.x / Unit) : axis == 1 ? Mathf.RoundToInt(-pos.y / Unit) : Mathf.RoundToInt(pos.z / Unit);
                        int step = (int)(a.X - cur) / Mathf.Max(1, a.Frames);
                        Vector3 goal = pos, st = Vector3.zero;
                        if (axis == 0) { goal.x = a.X * Unit; st.x = step * Unit; }
                        else if (axis == 1) { goal.y = -a.X * Unit; st.y = -step * Unit; }
                        else { goal.z = a.X * Unit; st.z = step * Unit; }
                        if (step == 0) { t.position = goal; break; }      // ours: a step of 0 would never get there
                        m.Goal = goal; m.GlideStep = st; m.Walking = false; m.Gliding = true; m.GlideAcc = -1f / 20f; m.GlideAxis = axis;
                        m.GlideCam = a.Cmd == 0x73;
                    }
                    break;
                case 0x6C:
                    if (ActorOf(a.Actor, out t, out an))
                    {
                        // ENTITY_MOVE_TO: SCRIPT_MOVEMENT type 8, tickEntityMoveTo: (target - position) / speed each frame until both x and z reach it;
                        // the boy's jumps over the Coelamon at Coela Point are this; his jump animation (0x15) gives the height and the last ~4 m (bone 0, drawn)
                        float zFix = a.Actor == 0xFC ? PartnerPlaceZOffset : 0f;   // measured against the PS1 game: Agumon starts further from the boy than the script's number
                        // STILL OURS (user's decision 2026-11-05): a glide in a map's 0xFE setup section lands at once. On the PS1 it ends under the
                        // fade-in (TWNB07: the recruited Betamon, 33 m in 10 frames, to his stall); our map loads faster and he was seen flying there.
                        if (_runnerSection == 0xFE)
                        {
                            Mover old;
                            if (_movers.TryGetValue(t, out old)) old.Gliding = false;
                            t.position = new Vector3(a.X * Unit, t.position.y, a.Z * Unit + zFix);
                            break;
                        }
                        var m = MoverFor(t, an);
                        m.Goal = new Vector3(a.X * Unit, t.position.y, a.Z * Unit + zFix);
                        m.Walking = false;
                        // tickEntityMoveTo: (target - position) / speed in whole game units, C division (towards zero)
                        int gx = Mathf.RoundToInt(t.position.x / Unit), gz = Mathf.RoundToInt(t.position.z / Unit), gs = Mathf.Max(1, a.Frames);
                        m.GlideStep = new Vector3((a.X - gx) / gs, 0f, (a.Z + Mathf.RoundToInt(zFix / Unit) - gz) / gs) * Unit;
                        m.Gliding = true; m.GlideAcc = -1f / 20f; m.GlideAxis = -1; m.GlideCam = false;       // the first frame only works out the step
                    }
                    break;
                case 0x4F:
                {
                    // MOVE_CAMERA_TO (tickCameraMoveTo -> tickCameraMovement): the point at the boy's height to the middle of the picture in 'speed'
                    // frames, a straight move; the goal is held inside the map (cameraIsAtEdge), so it ends at an edge
                    FreezeCamera();
                    StartCamMove(new Vector3(a.X * Unit, _player.position.y, a.Z * Unit), a.Frames);
                    break;
                }
                case 0x56:
                    // the number is the animation slot of that Digimon (0 = idle, 2 = walk, ~29 = talking...); ignored if the model has no such slot
                    // scriptStartAnimation: startAnimationTamer / partnerStartAnimation / scriptNPCStartAnimation all call startAnimation, which starts it at once
                    if (ActorOf(a.Actor, out t, out an) && an != null && !(_movers.ContainsKey(t) && _movers[t].Walking))
                    {
                        an.SetSlot(a.Target, a.Target != 0 && a.Target != 2); _animated.Add(an);
                        if (an == _anim) { _playerScripted = a.Target != 0 && a.Target != 2; an.TakeBodyMove(); }
                        // a map Digimon: its forward travel becomes real movement (UpdateNpcs), so the model must not also show it
                        foreach (var n in _npcs) if (n.Anim == an) { n.Scripted = true; _aiOff.Add(n.Record); an.BodyMovesItself = true; an.TakeBodyMove(); }
                    }
                    break;
                default:
                    // map objects; MOVE_BOULDER ends the script's frame (longjmp 2), so the script goes on one frame later
                    if (ExecObjectCmd(a) && a.Cmd == 0x77) { _wait = 1f / 20f; return true; }
                    break;
            }
            return false;
        }

        bool _playerScripted;      // the boy is in a script animation (0x56): its forward travel moves him

        void TickActors()
        {
            Mover pm = null;
            if (_playerScripted && _player != null && _movers.TryGetValue(_player, out pm) && pm.Walking) _playerScripted = false;     // a script walk takes over (its walk carries him)
            if (_playerScripted && _anim != null && _player != null && !DigimonAnimator.Paused)
            {
                // the jump's own travel: during a 0x6C glide the glide holds him (tickEntityMoveTo); once it has ended the rest of the travel carries him on.
                // When he last looked at someone (0x4C) it carries him onto that place and no further: user's PS1 clips 2026-11-04, each jump at Coela
                // Point ends on the middle of the Coelamon he looked at, the 2nd one 4 m past its glide point (the tamer's animation code is not decompiled)
                float travel = _anim.TakeBodyMove();
                if (pm == null) _movers.TryGetValue(_player, out pm);
                bool gliding = pm != null && pm.Gliding;
                // applyRootMomentum (anim.c): every frame of the animation the root's travel is added to his position along his facing, glide or not;
                // setEntityPosition (the glide's step) also moves the animation's running total, so the two add up (tickEntityMoveTo, tamer.c)
                if (travel != 0f)
                {
                    Vector3 front = _player.rotation * Quaternion.Inverse(Quaternion.Euler(0, FacingOffset, 0)) * Vector3.back;
                    front.y = 0f;
                    _player.position += front.normalized * travel;
                }
            }
            if (_camMove) TickCamMove();
            else if (_camOn) _camPos = Vector3.MoveTowards(_camPos, _camGoal, _camSpeed * Time.deltaTime);
            // cameraIsAtEdge: the camera never leaves the map, whatever moves it
            if (_camOn && _map != null) { _camPos = ClampCam(_camPos); _camGoal = ClampCam(_camGoal); }
            if (_movers.Count == 0) return;
            var list = new List<Mover>(_movers.Values);
            foreach (var m in list)
            {
                if (m.T == null) { _movers.Remove(m.T); continue; }
                if (m.Gliding)
                {
                    m.GlideAcc += Time.deltaTime;
                    while (m.Gliding && m.GlideAcc >= 1f / 20f)
                    {
                        m.GlideAcc -= 1f / 20f;
                        Vector3 p = m.T.position + m.GlideStep;
                        if (m.GlideAxis >= 0)
                        {
                            // tickEntityMoveToAxis
                            float v = m.GlideAxis == 0 ? p.x : m.GlideAxis == 1 ? -p.y : p.z, goal = m.GlideAxis == 0 ? m.Goal.x : m.GlideAxis == 1 ? -m.Goal.y : m.Goal.z;
                            float sv = m.GlideAxis == 0 ? m.GlideStep.x : m.GlideAxis == 1 ? -m.GlideStep.y : m.GlideStep.z;
                            bool done = sv < 0 ? v <= goal : v >= goal;
                            if (done) { if (m.GlideAxis == 0) p.x = m.Goal.x; else if (m.GlideAxis == 1) p.y = m.Goal.y; else p.z = m.Goal.z; }
                            Vector3 moved = p - m.T.position;
                            m.T.position = p;
                            // moveCameraByDiff: the camera moves as far as the Digimon did
                            if (m.GlideCam) { FreezeCamera(); _camPos += moved; _camGoal = _camPos; }
                            if (done) m.Gliding = false;
                            continue;
                        }
                        bool doneX = m.GlideStep.x < 0 ? p.x <= m.Goal.x : p.x >= m.Goal.x;
                        bool doneZ = m.GlideStep.z < 0 ? p.z <= m.Goal.z : p.z >= m.Goal.z;
                        // the snap onto the goal sets only the position; while the boy's script animation still plays, its next tick
                        // rewrites the position from its own running total, so the snap does not last (applyRootMomentum)
                        bool animOn = m.T == _player && _playerScripted && _anim != null && _anim.Busy;
                        if (doneX && !animOn) p.x = m.Goal.x;
                        if (doneZ && !animOn) p.z = m.Goal.z;
                        m.T.position = p;
                        if (doneX && doneZ) m.Gliding = false;
                    }
                }
                else if (m.Walking)
                {
                    // tickEntityWalkTo: it turns straight to the goal, its walk carries it step by step, and it stops on the goal's tile
                    if (m.WalkTarget != null) m.Goal = m.WalkTarget.position;
                    Vector3 to = m.Goal - m.T.position; to.y = 0;
                    float step = m.A != null ? m.A.TakeForward() : ActorSpeed * Time.deltaTime;
                    m.Still = step > 0f ? 0f : m.Still + Time.deltaTime;
                    if (m.Still > 0.5f) step = ActorSpeed * Time.deltaTime;     // an animation with no forward movement must not hang the script
                    Vector3 next = m.T.position + (to.sqrMagnitude > 0f ? to.normalized * Mathf.Min(step, to.magnitude) : Vector3.zero);
                    // with a target entity, entityCheckCollision tries the step against every other Digimon and the boy: a step into one is not taken
                    // and the walk is over (col 0-8). The Gekomon that run at the boy on STIC02 stop round him, not on top of him
                    bool touched = m.WalkTarget != null && step > 0f && (m.T == _player ? TouchesAnyone(m.T, next) : StepRunsInto(m.T, m.T.position, next));
                    if (TileOf(m.T.position) == TileOf(m.Goal) || to.sqrMagnitude < 0.0001f || touched) { m.Walking = false; m.WalkTarget = null; if (m.A != null) m.A.SetSlot(IdleSlot); }
                    else
                    {
                        m.T.rotation = FaceRot(to);
                        Vector3 was = m.T.position;
                        m.T.position = next;     // never past the goal, so a long stride cannot circle it and hang the script
                        if (m.WalkCam) { FreezeCamera(); _camPos += m.T.position - was; _camGoal = _camPos; }
                    }
                }
                else if (m.Looking)
                {
                    m.LookAcc += Time.deltaTime;
                    while (m.Looking && m.LookAcc >= 1f / 20f) { m.LookAcc -= 1f / 20f; if (StepLookAt(m)) m.Looking = false; }
                }
                else if (m.HasTurn)
                {
                    m.T.rotation = Quaternion.RotateTowards(m.T.rotation, m.TurnTo, 540f * Time.deltaTime);
                    if (Quaternion.Angle(m.T.rotation, m.TurnTo) < 1f) m.HasTurn = false;
                }
                if (!m.Walking && !m.HasTurn && !m.Gliding && !m.Looking) _movers.Remove(m.T);
            }
        }

        // tickLookAtEntity's frame: getRotationDifference (_atan of the whole-unit difference) then rotateEntity at speed 0x200; vy is kept unwrapped
        // between frames as the game keeps it. True when he faces it. rotateEntity does nothing when both ways round are equally far (the game
        // would stay in that state; here it ends, so a script cannot hang).
        bool StepLookAt(Mover m)
        {
            if (!m.LookVyKnown)
            {
                Vector3 f = m.T.rotation * Quaternion.Inverse(Quaternion.Euler(0, FacingOffset, 0)) * Vector3.forward;
                m.LookVy = ((int)(Mathf.Atan2(f.x, f.z) * 4096f / (2f * Mathf.PI))) & 0xFFF; m.LookVyKnown = true;
            }
            int dx = (short)(Mathf.RoundToInt(m.LookPos.x / Unit) - Mathf.RoundToInt(m.T.position.x / Unit));
            int dz = (short)(Mathf.RoundToInt(m.LookPos.z / Unit) - Mathf.RoundToInt(m.T.position.z / Unit));
            int target = (dx == 0 && dz == 0) ? 0x800 : (0xC00 - (int)(Mathf.Atan2(dz, dx) * 4096f / (2f * Mathf.PI))) & 0xFFF;     // _atan(dz, dx)
            int vy = m.LookVy, cw, cc;
            if (target > vy) { cw = target - vy; cc = vy + (4096 - target); } else { cw = target + (4096 - vy); cc = vy - target; }
            bool done = false;
            if (vy < target)
            {
                if (cc < cw) { vy -= 0x200; if (target - 4096 > vy) { vy = target; done = true; } }
                else if (cw < cc) { vy += 0x200; if (target < vy) { vy = target; done = true; } }
                else done = true;
            }
            else if (target < vy)
            {
                if (cc < cw) { vy -= 0x200; if (vy < target) { vy = target; done = true; } }
                else if (cw < cc) { vy += 0x200; if (target + 4096 < vy) { vy = target; done = true; } }
                else done = true;
            }
            else done = true;
            m.LookVy = vy;
            m.T.rotation = Quaternion.Euler(0, vy * 360f / 4096f, 0) * Quaternion.Euler(0, FacingOffset, 0);
            return done;
        }

        // scriptSetEntityWalking: the boy's walk / run is animation 2 + n, anyone else's (n + 1) x 2 (n = 0 walk, 1 run)
        static int ScriptWalkSlot(int actor, int anim) { return actor == 0xFD ? anim + 2 : (anim + 1) << 1; }

        // a script ended (or changed map): stop scripted movement and give the camera back to the player
        void EndCutscene()
        {
            _playerScripted = false;
            foreach (var m in _movers.Values) if (m.A != null) m.A.SetSlot(IdleSlot);
            // the boy and partner go back to their own states; a map Digimon keeps the animation the script gave it (Kunemon eating on its bush)
            foreach (var an in _animated)
            {
                if (an == null) continue;
                bool npc = false;
                foreach (var n in _npcs) if (n.Anim == an && n.Scripted) npc = true;
                if (!npc) an.SetSlot(IdleSlot);
            }
            _animated.Clear();
            // setCameraFollowPlayer only follows his steps from here (tickCameraFollowPlayer): the camera stays where the script left it
            if (_camOn && _map != null && _player != null) _camOffsetPx = MapPixel(ClampCam(_camPos) + Vector3.up) - MapPixel(_player.position + Vector3.up);
            _movers.Clear(); _camOn = false; _camMove = false; _wait = 0f; _waitAll = false;
        }

        // the opening: the real script runs in TOPN01, goes to Jijimon's house, then out to File City
        void BeginNewGame()
        {
            _state.Vars[103] = (byte)PartnerType;   // the opening picks the partner's starting stats by this (3 = Agumon)
            // first the character setup (Jijimon's questions and the name screens), then the opening script
            _setup = new CharacterSetup(_scripts, _state, _menus, _disc) { Art = _battle != null ? _battle.Art : null, JijimonSquare = JijimonSquare };
            _setup.DrawBackground = !BuildSetupStage();
            _setup.OnDone = () => { DestroySetupStage(); StartCoroutine(AfterNewGameScene()); };
            _setup.Start();
        }

        // main.c after newGameScene: playMovie(1, 1); variable 254 (day = 0, night = 1) gives Agumon (3) or Gabumon (0x11), setDigimonRaised
        IEnumerator AfterNewGameScene()
        {
            yield return PlayMovie(1);
            // initializeMusic and the map head script give the map its music back; fadeFromBlack(0x28) while the opening script starts
            // (the boy is lying down before the picture shows)
            if (_audio != null) _audio.RestoreBgm();
            _fade = 1f; StartCoroutine(FadeInAfterTitle());
            int pid = _state.Vars[0xFE] == 0 ? 3 : 0x11;
            if (pid != PartnerType) NewGamePartner(pid);
            _intro = true; if (!StartScript(54, null)) FinishIntro();
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
        // an auto tile's section, run as callScriptSection does from checkMapInteraction: the boy only stops if it shows something
        void AutoTile(int section)
        {
            var log = new List<string>();
            _runner = new ScriptRunner(_scripts, _state, _scripts.ScriptIdForMap(_mapId), section, log);
            _runnerSection = section; _bump = null; _sectionEvent = false;      // checkMapInteraction: callScriptSection(script, trigger, 0)
            _talkingTo = null; _scriptAny = false; _line = null; _choiceOpts = null;
            AdvanceScript();
            // he stops, unless the section's first commands already gave him a walk or an animation of his own (MIST05 Cherrymon: 0x52 runs him to
            // her at once; setting idle after it left him carried there standing, user 2026-11-07)
            Mover pm;
            bool own = _playerScripted || (_player != null && _movers.TryGetValue(_player, out pm) && pm.Walking);
            if (_runner != null && _anim != null && !own) _anim.SetSlot(IdleSlot);
            if (_runner != null) FreezeCamera();
            foreach (var l in log) Debug.Log("[script section " + section + " in " + _mapNames[_mapId] + "] " + l);
        }

        int _runnerSection = -1;      // the section the running script started from (0xFE = the map's own setup on arrival)

        /// <param name="evt">SCRIPT_SECTION_IS_EVENT: callScriptSection's third value (1 = a map Digimon talked to, the map's setup and arrival)</param>
        bool StartScript(int section, Npc who, bool evt = false)
        {
            if (_scripts == null || !_scripts.Available || _mapId < 0) return false;
            _sectionEvent = evt || who != null || section == 0xFE;
            var log = new List<string>();
            _runner = new ScriptRunner(_scripts, _state, _scripts.ScriptIdForMap(_mapId), section, log);
            _runnerSection = section; _bump = null;
            _talkingTo = who; _scriptAny = false; _line = null; _choiceOpts = null;
            if (_anim != null) _anim.SetSlot(IdleSlot);
            AdvanceScript();
            if (_runner != null) FreezeCamera();
            foreach (var l in log) Debug.Log("[script section " + section + " in " + _mapNames[_mapId] + "] " + l);
            return _scriptAny || _runner != null;
        }

        // setDialogueOwner -> scriptPauseGame(owner): a map Digimon that takes the dialogue box stops thinking for the rest of the script
        // (setMovementEnabled(id, 1): stopAnim, clearMapAITable: no target, no look-at, its route from the first section); the end of the
        // script lets them all go (setMovementEnabled(-1, 0)). Its script walks and animations still work. GIAS07: Patamon stops running
        // its route once it has cried "HELP ME!" (user 2026-11-05: ours went on hopping round during the talk)
        int _dialogueOwner = 0xFF;

        void SetDialogueOwner(int owner)
        {
            // scriptPauseGame -> setMovementEnabled(0, 1): the boy goes to state 6, whose tamerTickIdle starts animation 0. A script that begins
            // with waits (the Numemon in the sewer after Andromon) left him in the run pose he had when it started (user's clip 2026-11-09).
            // Only when no script walk or animation of his own is running
            Mover pm;
            bool own = _playerScripted || (_player != null && _movers.TryGetValue(_player, out pm) && (pm.Walking || pm.Gliding));
            if (_anim != null && !own && (_anim.Slot == WalkSlot || _anim.Slot == PlayerRunSlot || _anim.Slot == RunSlot)) _anim.SetSlot(IdleSlot);
            if (owner == _dialogueOwner) return;
            _dialogueOwner = owner;
            if (owner >= 0xC8) return;
            foreach (var n in _npcs)
                if (n.Data.Script == owner) { n.Stopped = true; n.HasTarget = false; n.Chasing = false; n.LookState = 0; n.Section = 0; }
        }

        void ReleaseSpeakers()
        {
            _dialogueOwner = 0xFF;
            _allStopped = false;
            foreach (var n in _npcs) n.Stopped = false;
        }

        // setMovementEnabled(-1, 1), from the fight command when a scripted fight is won with trigger 1 set (the scripts set it just before
        // 0x66): stopAnim on all 8 slots of MAP_DIGIMON_TABLE and clearMapAITable(-1). stopAnim belongs to the slot, not the entity, so a
        // Digimon the script switches on later (0x47, scriptSetDigimon leaves stopAnim alone) stands still too until the script ends.
        // STIC02 after Otamamon: the Gekomon stand at their places until 0x51 runs them to the boy (user's PS1 clip 2026-11-07)
        bool _allStopped;

        void StopAllMapDigimon()
        {
            _allStopped = true;
            foreach (var n in _npcs) { n.Stopped = true; n.HasTarget = false; n.Chasing = false; n.LookState = 0; n.Section = 0; }
        }

        // moves the running script to its next text box, menu or map change
        void AdvanceScript()
        {
            if (_scriptFadeOut) return;      // held on its 4B until the map changes
            while (_runner != null)
            {
                var a = _runner.Next();
                // the script ended (IS_SCRIPT_PAUSED): writePStat(0, MAIN_D_80134FE7)
                if (a == null)
                {
                    _state.Vars[0] = _state.Var0Reset; _runner = null; _runnerSection = -1; _line = null; _lineCustom = false; _choiceOpts = null; _talkingTo = null; EndCutscene();
                    ReleaseSpeakers();
                    // section 0xFE has ended: the arrival section follows
                    if (_thenSection != 255) { int s = _thenSection; _thenSection = 255; StartScript(s, null, true); return; }
                    if (_intro) FinishIntro();
                    return;
                }
                if (a.Cmd >= 0) { if (ExecCmd(a)) return; continue; }
                if (a.Text != null) { _line = a.Text; _lineCustom = a.Custom; _speaker = a.Speaker; SetDialogueOwner(a.Speaker); _choiceOpts = null; _scriptAny = true; return; }
                if (a.Options != null) { if (a.Selection) { _speaker = a.Speaker; SetDialogueOwner(a.Speaker); _line = null; } _lineCustom = false; _choiceOpts = a.Options; _choiceSel = 0; _scriptAny = true; return; }
                // scriptTickChangeMap: the script stays on this command (its text box, glides and animations go on) until the fade has gone black
                if (a.Map >= 0 && DoChangeMap(a, false, true)) { _scriptAny = true; return; }
            }
        }

        /// <param name="warp">a script's 4B / teleport: the area's name shows by consumeMapChangeShowName (a fled fight's change shows none)</param>
        bool DoChangeMap(ScriptAction a, bool instant = false, bool warp = false)
        {
            if (a.Map < 0 || a.Map >= _mapNames.Count || a.Exit > 9) return false;
            string next = PathOfMap(a.Map);
            if (!_disc.Exists(next) || !_disc.Exists(next.Substring(0, next.Length - 4) + ".TFS")) return false;
            Debug.Log("Script: " + _mapNames[_mapId] + " -> " + _mapNames[a.Map] + " exit " + a.Exit);
            _prevExit = _curExit;                   // scriptTickChangeMap: PREVIOUS_EXIT = CURRENT_EXIT
            _arrive = a.Arrive;
            bool showName = warp && ConsumeShowName();
            if (instant) { ScriptMapChangeEnd(); StartCoroutine(ChangeMap(next, a.Exit, true)); }
            else { _scriptFadeOut = true; StartCoroutine(ChangeMap(next, a.Exit, false, true, false, showName ? a.Map : -1)); }
            return true;
        }

        // scriptTickChangeMap 0 -> 1: fadeToBlack(20) while the script waits on 4B; nothing it set up ends until the picture is black (Seadramon's
        // ride at MAYO06: the three glide on under the fade; ending the scene first put Seadramon back in its upright idle pose, user's clip 2026-11-10)
        bool _scriptFadeOut;

        // changeMap: the script is gone with the map (longjmp 3)
        void ScriptMapChangeEnd()
        {
            _scriptFadeOut = false;
            EndCutscene();
            _state.Vars[0] = _state.Var0Reset;      // the map change (longjmp 3): writePStat(0, MAIN_D_80134FE7)
            _runner = null; _line = null; _choiceOpts = null; _talkingTo = null;
        }

        // handleFleeing: the arrival point of CURRENT_EXIT plus (0, 0, -3000) turned by the exit's rotation + 0x800 (RotMatrix about y), as a Unity position
        Vector3 FleeOutPoint()
        {
            if (_map == null || _map.Warps == null || _curExit < 0 || _curExit >= _map.Warps.Length) return _player != null ? _player.position : Vector3.zero;
            var w = _map.Warps[_curExit];
            float a = ((w.Rotation + 0x800) & 0xFFF) * 2f * Mathf.PI / 4096f;
            return new Vector3((w.SpawnX - 3000f * Mathf.Sin(a)) * Unit, 0f, (w.SpawnZ - 3000f * Mathf.Cos(a)) * Unit);
        }

        // PREVIOUS_SCREEN / PREVIOUS_EXIT / CURRENT_EXIT: where a successful Run sends the boy back to
        // the new game sets PREVIOUS_SCREEN = CURRENT_SCREEN = 0xCC (map.c); a save brings its own
        int _prevScreen = 0xCC, _prevExit, _curExit;

        // changeMap: the map he came from; leaving Birdramon's map (0xCF) counts as coming from the map before its landing place
        void NotePreviousMap(int from, int to, int exit)
        {
            _prevScreen = from;
            if (from == 0xCF)
            {
                if (to == 0x26) _prevScreen = 0x58;
                if (to == 0x46) _prevScreen = 0x45;
                if (to == 0x4F) _prevScreen = 0x11;
                if (to == 0x5D) _prevScreen = 0x5A;
                if (to == 0x77) _prevScreen = 0x76;
                if (to == 0x69) _prevScreen = 0x6A;
                if (to == 0x26 || to == 0x46) _prevExit = 2;
                if (to == 0x4F || to == 0x77) _prevExit = 1;
                if (to == 0x5D || to == 0x69) _prevExit = 0;
            }
            // changeMap's other fixed cases
            if (from == 0x2B && to == 0x2A) { _prevScreen = 0x29; _prevExit = 1; }
            if (from == 6 && to == 0x69) _prevScreen = 0x6A;
            if (from == 0x8E && to == 0x87) { _prevScreen = 0x84; _prevExit = 1; }
            if (from == 5 && to == 0x0C) { _prevScreen = 0x0D; _prevExit = 1; }
            _curExit = exit;
        }

        void ClearNpcs()
        {
            foreach (var n in _npcs) if (n.T != null) Destroy(n.T.gameObject);
            _npcs.Clear(); _runner = null; _line = null; _choiceOpts = null; _talkingTo = null; _dialogueOwner = 0xFF; _bump = null;
            _movers.Clear(); _camOn = false; _wait = 0f; _waitAll = false; _animated.Clear();
        }

        void BuildNpcs()
        {
            ClearNpcs();
            if (_map == null || _map.Npcs == null) return;
            if (_entries == null) _entries = new List<DigimonEntry>(DigimonCatalog.Load(_disc));
            if (_allTim == null) _allTim = _disc.ReadFile("/CHDAT/ALLTIM.TIM");
            // the map's own script says which of its Digimon are active, depending on the story flags
            var active = new HashSet<int>(); var autotalk = new Dictionary<int, int>();
            if (_scripts != null && _scripts.Available && _mapId >= 0)
            {
                var log = new List<string>();
                var r = new ScriptRunner(_scripts, _state, 0, _mapId, log);
                for (int guard = 0; guard < 500; guard++) { var a = r.Next(); if (a == null || a.Options != null) break; }
                foreach (var sp in r.Spawns) { active.Add(sp[1]); autotalk[sp[1]] = sp[2]; }
                foreach (var l in log) Debug.Log("[map setup " + _mapNames[_mapId] + "] " + l);
            }
            for (int ni = 0; ni < _map.Npcs.Count; ni++)
            {
                if (!active.Contains(ni) && !_spawnAll) continue;
                var n = MakeNpc(ni, autotalk.ContainsKey(ni) ? autotalk[ni] : 0);
                if (n != null) _npcs.Add(n);
            }
        }

        // initializeLoadedMap: the first map Digimon whose box the boy stands in when the map loads (entityCheckCollision) leaves the map (isOnMap = 0);
        // for types 0x6D, 0x75 and up, 0x5C on map 0x9B, 0x6A on map 0x55, 1, 2, 0x0F, 0x10, 0x1D, 0x1E, 0x2B, 0x2C the boy goes back onto his exit's
        // arrival point instead, the partner 200 units off it on the side the exit faces
        void ArrivalOverlap(int exit)
        {
            if (_player == null) return;
            Npc hit = null;
            foreach (var n in _npcs) if (n.T != null && n.T.gameObject.activeInHierarchy && Touching(n)) { hit = n; break; }
            if (hit == null) return;
            int t = hit.Data.Type;
            bool stays = t == 0x6D || t >= 0x75 || (t == 0x5C && _mapId == 0x9B) || (t == 0x6A && _mapId == 0x55)
                || t == 1 || t == 2 || t == 0x0F || t == 0x10 || t == 0x1D || t == 0x1E || t == 0x2B || t == 0x2C;
            if (!stays) { Destroy(hit.T.gameObject); _npcs.Remove(hit); return; }
            if (exit < 0 || exit >= 10) return;
            var w = _map.Warps[exit];
            _player.position = new Vector3(w.SpawnX * Unit, -w.SpawnY * Unit, w.SpawnZ * Unit);
            int x = w.SpawnX, z = w.SpawnZ, r = w.Rotation;
            if (r <= 0x200 || r > 0xE00) z += 200;
            else if (r <= 0x600) x += 200;
            else if (r <= 0xA00) z -= 200;
            else x -= 200;
            if (_partner != null) _partner.position = new Vector3(x * Unit, -w.SpawnY * Unit, z * Unit);
            _tamerStart = _tamerPrev = TileOf(_player.position); _tamerTile = _tamerStart;
        }

        Npc MakeNpc(int ni, int autotalk)
        {
            var d = _map.Npcs[ni];
            DigimonEntry entry = null;
            foreach (var e in _entries) if (e.Type == d.Type) { entry = e; break; }
            if (entry == null) { Debug.LogWarning("[npcs] " + _mapNames[_mapId] + " #" + ni + ": Digimon type " + d.Type + " has no model entry - not shown."); return null; }
            GameObject go;
            try
            {
                MmdModel.UseLit = Hd2D && UseMapLights;
                MmdModel.Brightness = Hd2D && UseMapLights ? CharacterBrightness : 1f;
                go = MmdModel.Parse(_disc.ReadFile(entry.MmdPath)).Build(entry, _allTim);
            }
            catch (System.Exception ex) { Debug.LogWarning("NPC " + entry.Name + ": " + ex.Message); return null; }
            finally { MmdModel.UseLit = false; MmdModel.Brightness = 1f; }
            go.transform.SetParent(transform, false);
            var n = new Npc { Data = d, T = go.transform, Anim = go.GetComponent<DigimonAnimator>(), Name = entry.Name, AutoTalk = autotalk, Record = ni };
            if (n.Anim != null) n.Anim.DigimonType = d.Type;
            // scriptSetDigimon: setEntityPosition(posX, posY, posZ), so a Digimon can stand on something (Kunemon on its bush)
            Vector3 p = ToUnity(d.Pos);
            go.transform.position = p;
            n.Home = p;
            float a = d.RotY * 2f * Mathf.PI / 4096f;
            go.transform.rotation = Quaternion.LookRotation(new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a))) * Quaternion.Euler(0, FacingOffset, 0);
            foreach (var w in d.Waypoints) { Vector3 q = ToUnity(w); q.y = 0; n.Path.Add(q); n.WaitFrames.Add((int)w.x); }
            if (n.Anim != null) n.Anim.SetSlot(IdleSlot);
            return n;
        }

        // scriptSetDigimon (command 0x47 while a script runs, e.g. Coelamon rising at Coela Point): only if the record has that type;
        // the old one in that slot is removed, the new one starts at the record's place
        void ScriptSetDigimon(int type, int slot, int autotalk)
        {
            if (_map == null || slot < 0 || slot >= _map.Npcs.Count || _map.Npcs[slot].Type != type) return;
            bool stopped = _allStopped;     // the slot's stopAnim stays as it was
            for (int i = _npcs.Count - 1; i >= 0; i--)
                if (_npcs[i].Record == slot) { stopped |= _npcs[i].Stopped; if (_npcs[i].T != null) Destroy(_npcs[i].T.gameObject); _npcs.RemoveAt(i); }
            var n = MakeNpc(slot, autotalk);
            if (n != null) { n.Stopped = stopped; _npcs.Add(n); }
        }

        float _npcAcc;

        void UpdateNpcs()
        {
            if (DigimonAnimator.Paused) return;         // NPCEntityTickOverworld does nothing while a menu is open
            _npcAcc += Time.deltaTime * 20f;
            int frames = (int)_npcAcc; _npcAcc -= frames;
            if (frames > 3) frames = 3;
            foreach (var n in _npcs)
            {
                if (n.T == null) continue;
                // a script walk (0x4E, 0x51-0x53; tickEntityWalkTo) moves it by its walk / run animation's travel (TickActors); in the game a Digimon
                // moves once a frame, by its own animation, so nothing else may take that travel or change its animation meanwhile
                Mover sw;
                if (_movers.TryGetValue(n.T, out sw) && sw.Walking) continue;
                if (n.Scripted && n.Anim != null && n != _battleNpc)
                {
                    // entityMoveForward (from entityCheckCollision in NPCEntityTickOverworld, every frame): the animation's forward travel moves the
                    // Digimon for real, so it stays where a script animation took it (Kunemon's jump off its bush)
                    Vector3 front = n.T.rotation * Quaternion.Inverse(Quaternion.Euler(0, FacingOffset, 0)) * Vector3.back;
                    front.y = 0f;
                    n.T.position += front.normalized * n.Anim.TakeBodyMove();
                }
                // NPC_ACTIVE_ANIM != 0: the AI, the tracking and the stopAnim idle all wait (only the animation's own travel moved it, above)
                if (_aiOff.Contains(n.Record)) continue;
                if (n == _talkingTo || n == _battleNpc) continue;
                // NPCEntityTickOverworld with stopAnim set (and no script animation): it only goes back to animation 0
                if (n.Stopped) { if (!n.Scripted && n.AiAnim != IdleSlot) NpcAnim(n, IdleSlot); continue; }
                if (ChaseScripted(n)) continue;
                if (n.Data.FollowMode == 16 && Hostile(n)) { if (n.Anim != null && n.Anim.Slot != IdleSlot) n.Anim.SetSlot(IdleSlot); continue; }   // it only ever runs at you, it never strolls
                // NPCEntityTickOverworld, a game frame: the waypoint AI while lookAtTamerState is 0, then its follow mode's part. RanIntoBoy lasts one
                // Update: its run ending on the boy is the step that touched him (checked by TryStartBattle / TryTalk right after)
                n.RanIntoBoy = false;
                for (int f = 0; f < frames; f++) { if (n.LookState == 0) TickWaypointAI(n); TickFollowMode(n); }
                if (!n.Scripted && n.AiAnim >= 0 && n.Anim != null && n.Anim.Slot != n.AiAnim && n.Anim.HasSlot(n.AiAnim)) n.Anim.SetSlot(n.AiAnim);
                if (n.AiAnim == WalkSlot || n.AiAnim == RunSlot)
                {
                    // the walk and run animations carry the Digimon forward along its facing
                    Vector3 fwd = n.T.rotation * Quaternion.Inverse(Quaternion.Euler(0, FacingOffset, 0)) * Vector3.back;
                    fwd.y = 0f;
                    if (n.Anim != null)
                    {
                        // entityCheckCollision: the step is undone when it runs into the boy, the partner or another Digimon (a Digimon already
                        // overlapping one may still step out of it)
                        Vector3 next = n.T.position + fwd.normalized * n.Anim.TakeForward();
                        if (!TouchesAnyone(n.T, next) || TouchesAnyone(n.T, n.T.position)) n.T.position = next;
                    }
                }
            }
        }

        // NPCEntityTickWaypointAI, once a game frame: section types 0 tickWaypointWait, 1 / 2 NPCEntityTickWaypointWalk with animation 2 / 4;
        // past section 8 or on a -1 it starts again at 0
        void TickWaypointAI(Npc n)
        {
            var s = n.Data.AiSections;
            switch (s[n.Section])
            {
                case 0:
                    if (!n.HasTarget) { NpcAnim(n, IdleSlot); n.WaitTimer = 0; n.HasTarget = true; }
                    else if (++n.WaitTimer >= (n.Section < n.WaitFrames.Count ? n.WaitFrames[n.Section] : 0)) { n.HasTarget = false; n.WaitTimer = 0; n.Section++; }
                    break;
                case 1: case 2:
                {
                    int anim = s[n.Section] == 1 ? WalkSlot : RunSlot;
                    Vector3 target = n.Section < n.Path.Count ? n.Path[n.Section] : n.T.position;
                    if (!n.HasTarget) { NpcAnim(n, anim); n.HasTarget = true; break; }
                    // getRotationDifference / rotateEntity: it turns 0x71 a frame towards the waypoint, unless something is in its way
                    Vector3 to = target - n.T.position; to.y = 0f;
                    if (!Touching(n) && to.sqrMagnitude > 1e-6f) n.T.rotation = Quaternion.RotateTowards(n.T.rotation, FaceRot(to), 0x71 * 360f / 4096f);
                    if (TileOf(n.T.position) == TileOf(target)) { n.HasTarget = false; n.Section++; }
                    break;
                }
            }
            if (n.Section >= 8 || s[n.Section] == -1) n.Section = 0;
        }

        void NpcAnim(Npc n, int slot)
        {
            if (n.AiAnim == slot) return;
            n.AiAnim = slot; n.Scripted = false;
            if (n.Anim != null) n.Anim.BodyMovesItself = false;
            if (n.Anim != null && n.Anim.HasSlot(slot)) n.Anim.SetSlot(slot);
        }

        // 'from' = where the walker stands now. Fix for the original's Mojyamon trap (GameFAQs Glitch FAQ C4): a map Digimon whose route walks
        // onto the boy no longer locks him in; while they overlap, any step that does not go closer to it is allowed.
        bool BlockedByNpc(Vector3 u, Vector3? from = null)
        {
            foreach (var n in _npcs)
                if (n.T != null && n.Data.FollowMode == 1)
                {
                    float dx = u.x - n.T.position.x, dz = u.z - n.T.position.z, d = dx * dx + dz * dz;
                    if (d >= 0.6f * 0.6f) continue;
                    if (from.HasValue)
                    {
                        float fx = from.Value.x - n.T.position.x, fz = from.Value.z - n.T.position.z, f = fx * fx + fz * fz;
                        if (f < 0.6f * 0.6f && d >= f) continue;
                    }
                    return true;
                }
            return false;
        }

        // talk to the closest friendly Digimon in front of you
        void TryTalk()
        {
            // checkMapInteraction: Cross while the boy's step runs into a map Digimon (the boxes of their radii) runs its script; standing still,
            // the Cross frame stretches his box 50 units the way he faces (CrossReach)
            Npc best = null; float bd = float.MaxValue;
            bool standing = _anim == null || _anim.Slot == IdleSlot;
            foreach (var n in _npcs)
            {
                if (n.T == null || !n.T.gameObject.activeInHierarchy) continue;
                if (n != _bump && !NpcPushing(n) && !(standing && CrossReach(n))) continue;
                float d = Vector3.Distance(n.T.position, _player.position);
                if (d < bd) { bd = d; best = n; }
            }
            if (best == null) return;
            if (_scripts == null || !_scripts.Available) return;
            // mod Online battle: Greymon in his arena asks first (PlayableRoomOnline)
            if (OnlineArenaTalk(best))
            {
                Vector3 lk = _player.position - best.T.position; lk.y = 0;
                if (lk.sqrMagnitude > 0.01f) best.T.rotation = Quaternion.LookRotation(-lk.normalized) * Quaternion.Euler(0, FacingOffset, 0);
                if (best.Anim != null && !best.Scripted) best.Anim.SetSlot(IdleSlot);
                return;
            }
            // a section that ends without showing anything (OMOC03's Tinmon before flag 0x15E: IF the flag, "Thanks.", else END) does nothing on the
            // PS1 either: no line, no turn. Only a section missing from the script gets our note.
            if (!StartScript(best.Data.Script, best))
            {
                if (!ScriptRunner.HasSection(_scripts, _scripts.ScriptIdForMap(_mapId), best.Data.Script))
                    Say("(" + best.T.name + " has nothing to say - script " + best.Data.Script + " is not in this map's script)");
                return;
            }
            Vector3 look = _player.position - best.T.position; look.y = 0;
            if (look.sqrMagnitude > 0.01f)
                best.T.rotation = Quaternion.LookRotation(-look.normalized) * Quaternion.Euler(0, FacingOffset, 0);
            if (best.Anim != null && !best.Scripted) best.Anim.SetSlot(IdleSlot);      // not over the animation its script just started
        }

        // ------------------------------------------------------------------ battles with wild Digimon

        Battle _battle; Npc _battleNpc; Vector3 _battleFocus; float _camBack = 1f;
        bool _recentrePending;      // ours: the mod Recentre camera waits for the fight's script to end

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
        bool _battleWait;

        // checkMapInteraction / NPCEntityTickOverworld: touching a map Digimon runs its script on its own only when its autotalk (0x47's third
        // byte, 0x6A) is 1; with 0 it needs Cross (TryTalk). A wild one's fight comes from its script (0x66), never from the touch itself.
        // Ours before: every wild Digimon (follow mode 12) ran its script on touch, so TUNN07_3's DemiMeramon (autotalk 0, script 5 only talks)
        // said "Hey, how are you?" again and again (user 2026-11-05)
        void TryStartBattle()
        {
            if (_battle == null || _partner == null || !_partner.gameObject.activeInHierarchy || _intro) return;
            foreach (var n in _npcs)
            {
                if (n.T == null || !n.T.gameObject.activeInHierarchy) continue;
                if (n.AutoTalk != 1) continue;
                Vector3 d = n.T.position - _player.position; d.y = 0;
                // its own script runs at once: lines, then the fight (command 0x66)
                if (n != _bump && !NpcPushing(n)) continue;      // entityCheckCollision: his step runs into it, or its step runs into him
                if (_anim != null) _anim.SetSlot(IdleSlot);
                if (n.Anim != null && !n.Scripted) n.Anim.SetSlot(IdleSlot);
                if (n.Data.Script > 0 && StartScript(n.Data.Script, n)) { Vector3 look = _player.position - n.T.position; look.y = 0; if (look.sqrMagnitude > 0.01f) n.T.rotation = FaceRot(look); return; }
                if (Hostile(n)) StartBattle(n);
                return;
            }
        }

        [Tooltip("Metres per second of the scripted opponent (the first Agumon) when it runs after you: fast enough to meet you half way on the map, as in the original. Not measured exactly.")] public float ChaseSpeed = 7f;

        const int ChaseSlot = 4;    // the run

        // the game's collision boxes (entityCheckEntityCollision): squares of each one's radius (DIGIMON_DATA +0x18) round the two of them
        bool Touching(Npc n) { return TouchingAt(n, _player.position); }

        // the game radius of whoever this is: the boy (DIGIMON_DATA 0), the partner, or a map Digimon
        int RadiusOf(Transform t)
        {
            if (t == _player) { var b = DigimonStatsCatalog.Get(_disc, 0); return b != null ? b.Radius : 0; }
            if (t == _partner) return _menus != null && _menus.PartnerInfo != null ? _menus.PartnerInfo.Radius : 120;
            foreach (var n in _npcs) if (n.T == t) { var it = DigimonStatsCatalog.Get(_disc, n.Data.Type); return it != null ? it.Radius : 0; }
            return 0;
        }

        // entityCheckCollision, for a walker: does its box at 'at' overlap the box of the boy, the partner or any map Digimon on the map
        bool TouchesAnyone(Transform walker, Vector3 at)
        {
            int r = RadiusOf(walker);
            var others = new List<Transform>();
            if (_player != null) others.Add(_player);
            if (_partner != null && _partner.gameObject.activeInHierarchy) others.Add(_partner);
            foreach (var n in _npcs) if (n.T != null && n.T.gameObject.activeInHierarchy) others.Add(n.T);
            foreach (var o in others)
            {
                if (o == walker) continue;
                float reach = (r + RadiusOf(o)) * Unit;
                if (Mathf.Abs(o.position.x - at.x) < reach && Mathf.Abs(o.position.z - at.z) < reach) return true;
            }
            return false;
        }

        // entityCheckCollision -> entityCheckEntityCollision (map.c) for a walker that is not the boy: only the edge(s) of its box on the side it
        // steps to are tested (dx < 0 its left edge, dx > 0 its right, dz < 0 its top, dz > 0 its bottom), each against the other's box (isRectInRect,
        // edges count). So a Digimon that stands in someone and walks away from them is not stopped (GIAS04: MetalMamemon starts in the boy and
        // runs to Biyomon, user's clip 2026-11-08). The boy as the walker keeps the whole-box test (TouchesAnyone).
        bool StepRunsInto(Transform walker, Vector3 from, Vector3 next)
        {
            int dx = Mathf.RoundToInt((next.x - from.x) / Unit), dz = Mathf.RoundToInt((next.z - from.z) / Unit);
            if (dx == 0 && dz == 0) return false;
            int r = RadiusOf(walker);
            int nx = Mathf.RoundToInt(next.x / Unit), nz = Mathf.RoundToInt(next.z / Unit);
            int left = nx - r, right = nx + r, bottom = nz + r, top = nz - r;
            var others = new List<Transform>();
            if (_player != null) others.Add(_player);
            if (_partner != null && _partner.gameObject.activeInHierarchy) others.Add(_partner);
            foreach (var n in _npcs) if (n.T != null && n.T.gameObject.activeInHierarchy) others.Add(n.T);
            foreach (var o in others)
            {
                if (o == walker) continue;
                int ro = RadiusOf(o), ox = Mathf.RoundToInt(o.position.x / Unit), oz = Mathf.RoundToInt(o.position.z / Unit);
                int x0 = ox - ro, x1 = ox + ro, z0 = oz - ro, z1 = oz + ro;
                if (dx < 0 && EdgeInBox(x0, x1, z0, z1, left, bottom, left, top)) return true;
                if (dx > 0 && EdgeInBox(x0, x1, z0, z1, right, bottom, right, top)) return true;
                if (dz < 0 && EdgeInBox(x0, x1, z0, z1, left, top, right, top)) return true;
                if (dz > 0 && EdgeInBox(x0, x1, z0, z1, left, bottom, right, bottom)) return true;
            }
            return false;
        }

        // isRectInRect: the box (x0..x1, z0..z1) and the edge given as (l, t, r, b) in its argument order
        static bool EdgeInBox(int x0, int x1, int z0, int z1, int l, int t, int r, int b)
        {
            return !(x1 < l || r < x0 || t < z0 || b > z1);
        }

        // entityCheckEntityCollision for the boy on the map: on the frame Cross is pressed while he stands (animation 0), his box reaches 50 units
        // further on the side(s) he faces (vy in (0, 0x800): -x; (0x800, 0x1000): +x; < 0x400 or > 0xC00: -z; (0x400, 0xC00): +z). So a Digimon
        // right beside him can be talked to from any side, not only when his step runs into it (OMOC03's Tinmon, user 2026-11-07)
        bool CrossReach(Npc n)
        {
            var b = DigimonStatsCatalog.Get(_disc, 0); var it = DigimonStatsCatalog.Get(_disc, n.Data.Type);
            float r = (b != null ? b.Radius : 0) * Unit, ro = (it != null ? it.Radius : 0) * Unit, ext = 50f * Unit;
            Vector3 f = _player.rotation * Quaternion.Inverse(Quaternion.Euler(0, FacingOffset, 0)) * Vector3.forward;
            int vy = ((int)(Mathf.Atan2(f.x, f.z) * 4096f / (2f * Mathf.PI))) & 0xFFF;
            float left = _player.position.x - r, right = _player.position.x + r, top = _player.position.z - r, bottom = _player.position.z + r;
            if (vy > 0 && vy < 0x800) left -= ext;
            if (vy > 0x800) right += ext;
            if (vy < 0x400 || vy > 0xC00) top -= ext;
            if (vy > 0x400 && vy < 0xC00) bottom += ext;
            Vector3 o = n.T.position;
            return left < o.x + ro && right > o.x - ro && top < o.z + ro && bottom > o.z - ro;
        }

        bool TouchingAt(Npc n, Vector3 boy)
        {
            var b = DigimonStatsCatalog.Get(_disc, 0); var it = DigimonStatsCatalog.Get(_disc, n.Data.Type);
            float reach = ((b != null ? b.Radius : 0) + (it != null ? it.Radius : 0)) * Unit;
            return Mathf.Abs(n.T.position.x - boy.x) < reach && Mathf.Abs(n.T.position.z - boy.z) < reach;
        }

        // the scripted opponent (follow mode 16, the first Agumon) runs after you on its map until you fight it: the story cannot go on without that fight
        // NPCEntityTickOverworld: what a map Digimon does when the boy comes near, by its follow mode.
        // 2, 11 look at him; 3-5, 8, 9, 12-14, 17, 18 walk at him inside a square round their home; 6, 15 walk and 7, 16 run inside the game's radius test
        // NPCEntityTickOverworld's follow modes (overworld.c), one game frame. 2 / 11: NPCEntityTickLookingAtTamer; 3-5 / 12-14:
        // NPCEntityTickTrackingTamer(3): note where the boy stands, walk (2) then run (4) there turning 0x71 a frame, stop on that tile or on
        // touching the boy, stand 40 frames, walk home, and start again while he is in the square; 6 / 15 (walk), 7 / 16 (run), 8 / 9 / 17 / 18
        // (walk): NPCEntityTickTrackingTamer2(4): go at him while the radius test holds, then home and 80 frames of standing. The square is
        // trackingRange round its record's place; the radius test is the game's sum dx + dz < (range x 6 / 10)^2. A part runs when he is in
        // range or it is already under way. Ours before walked straight at him and turned home the moment he left (MIST07, user 2026-11-07)
        void TickFollowMode(Npc n)
        {
            int m = n.Data.FollowMode;
            if (_player == null || _changing || _intro) return;
            Vector3 boy = _player.position;
            int range = n.Data.Tracking;
            bool square = Mathf.Abs(boy.x - n.Home.x) < range * Unit && Mathf.Abs(boy.z - n.Home.z) < range * Unit;
            bool radius = ((n.T.position.x - boy.x) + (n.T.position.z - boy.z)) / Unit < (range * 6 / 10) * (range * 6 / 10);
            bool free = !TouchesAnyone(n.T, n.T.position);       // NPC_COLLISION_STATE == -1
            switch (m)
            {
                case 2: case 11:
                    if (!square && n.LookState == 0) return;
                    if (n.LookState == 0) { NpcAnim(n, IdleSlot); n.LookState = 1; }
                    else
                    {
                        RotateToward(n.T, boy, 0x71);
                        if (!square) { n.LookState = 0; n.HasTarget = false; }
                    }
                    return;
                case 3: case 4: case 5: case 12: case 13: case 14:
                    if (!square && n.LookState == 0) return;
                    switch (n.LookState)
                    {
                        case 0: n.TrackTarget = boy; NpcAnim(n, WalkSlot); n.LookState = 1; break;
                        case 1: NpcAnim(n, RunSlot); n.LookState = 2; break;
                        case 2:
                            if (free) RotateToward(n.T, n.TrackTarget, 0x71);
                            if (TileOf(n.T.position) == TileOf(n.TrackTarget) || Touching(n)) { n.RanIntoBoy = Touching(n); NpcAnim(n, IdleSlot); n.WaitTimer = 0; n.LookState = 3; }
                            break;
                        case 3:
                            if (++n.WaitTimer >= 40) { NpcAnim(n, WalkSlot); n.TrackTarget = n.Home; n.WaitTimer = 0; n.LookState = 4; }
                            break;
                        case 4:
                            if (free) RotateToward(n.T, n.TrackTarget, 0x71);
                            if (TileOf(n.T.position) == TileOf(n.TrackTarget)) { NpcAnim(n, IdleSlot); n.WaitTimer = 0; n.LookState = 0; n.HasTarget = false; }
                            break;
                    }
                    return;
                case 6: case 7: case 8: case 9: case 15: case 16: case 17: case 18:
                {
                    bool enter = m == 8 || m == 9 || m == 17 || m == 18 ? square : radius;
                    if (!enter && n.LookState == 0) return;
                    int anim = m == 7 || m == 16 ? RunSlot : WalkSlot;
                    switch (n.LookState)
                    {
                        case 0: NpcAnim(n, anim); n.LookState = 1; break;
                        case 1:
                            if (radius) { if (free) RotateToward(n.T, boy, 0x71); }
                            else { n.LookState = 2; n.TrackTarget = n.Home; }
                            break;
                        case 2:
                            if (free) RotateToward(n.T, n.TrackTarget, 0x71);
                            if (TileOf(n.T.position) == TileOf(n.TrackTarget)) { NpcAnim(n, IdleSlot); n.WaitTimer = 0; n.LookState = 3; }
                            break;
                        case 3:
                            if (++n.WaitTimer >= 80) { n.LookState = 0; n.HasTarget = false; n.WaitTimer = 0; }
                            break;
                    }
                    return;
                }
            }
        }

        // getRotationDifference + rotateEntity: vy turns 'speed' a frame the shorter way round towards _atan(dz, dx) of the whole-unit difference
        void RotateToward(Transform t, Vector3 at, int speed)
        {
            Vector3 f = t.rotation * Quaternion.Inverse(Quaternion.Euler(0, FacingOffset, 0)) * Vector3.forward;
            int vy = ((int)(Mathf.Atan2(f.x, f.z) * 4096f / (2f * Mathf.PI))) & 0xFFF;
            int dx = Mathf.RoundToInt(at.x / Unit) - Mathf.RoundToInt(t.position.x / Unit), dz = Mathf.RoundToInt(at.z / Unit) - Mathf.RoundToInt(t.position.z / Unit);
            int target = (0xC00 - (int)(Mathf.Atan2(dz, dx) * 4096f / (2f * Mathf.PI))) & 0xFFF;
            int cw, cc;
            if (target > vy) { cw = target - vy; cc = vy + (4096 - target); } else { cw = target + (4096 - vy); cc = vy - target; }
            if (vy < target)
            {
                if (cc < cw) { vy -= speed; if (target - 4096 > vy) vy = target; }
                else if (cw < cc) { vy += speed; if (target < vy) vy = target; }
            }
            else if (target < vy)
            {
                if (cc < cw) { vy -= speed; if (vy < target) vy = target; }
                else if (cw < cc) { vy += speed; if (target + 4096 < vy) vy = target; }
            }
            t.rotation = Quaternion.Euler(0, vy * 360f / 4096f, 0) * Quaternion.Euler(0, FacingOffset, 0);
        }

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
        readonly HashSet<Transform> _onScreenAtFight = new HashSet<Transform>();    // map Digimon on screen when the fight began
        readonly HashSet<Transform> _offAtFight = new HashSet<Transform>();         // off screen then: handleBattleStart takes them off the map until it ends

        void StartBattle(Npc enemy, bool noFlee = false)
        {
            if (_state.Stats[4] == 0)   // not a New Game start: give the partner the opening's Agumon numbers so a fight can be tried
            {
                ushort[] v = { 80, 60, 70, 70, 800, 600, 800, 600 };
                for (int i = 0; i < v.Length; i++) _state.Stats[i] = v[i];
                if (_state.ItemSlots == 0) _state.ItemSlots = 10;
            }
            _battleNpc = enemy;
            _battle.MapId = _mapId;
            if (_anim != null) _anim.SetSlot(IdleSlot);
            _prePlayerPos = _player.position;
            if (_menus != null) _menus.LastUsedItem = -1;
            _battleFocus = VisibleCamPos() + Vector3.up * 1f;
            BringPartnerNear();
            Vector3 right = _cam != null ? _cam.transform.right : Vector3.right;
            var boyInfo = DigimonStatsCatalog.Get(_disc, 0);
            _tamerActive = 0;
            _battle.Walkable = GroundFree;     // walls only: in a scripted fight the opponent is a standing map Digimon and must not block itself or the boy
            if (_audio != null) { _audio.BattleMusic(noFlee, enemy.Data.Type); _audio.BattleSounds(); }
            // handleBattleStart: FLEE_DISABLED[0] = isTriggerSet(1) (Run locked, the enemies pay no MP, they stand after the fight). Not variable 250:
            // MIHA01's Mamemon is a plain 66 00 after 1C 00 01 00, and ran off before "Oh no. I lost." (user 2026-11-11)
            _battle.Start(_player, _anim, _partner, _partnerAnim, _menus != null ? _menus.PartnerName : _partnerName, DigimonStatsCatalog.Get(_disc, PartnerType),
                          enemy.T, enemy.Anim, enemy.Name, DigimonStatsCatalog.Get(_disc, enemy.Data.Type), enemy.Data, right, _state.GetFlag(1), _tamerWay, _tamerStart, boyInfo != null ? boyInfo.Radius * Unit : 0f);
            // isOnScreen (NPCEntityTick, entityIsOffScreen 0x140 x 0xF0) as it was on the last map frame: the fight does not update it (NPCEntityTickBattle)
            _onScreenAtFight.Clear(); _offAtFight.Clear();
            foreach (var n in _npcs) if (n.T != null && n.T.gameObject.activeInHierarchy && OnScreen(n)) _onScreenAtFight.Add(n.T);
            GatherOthers(enemy, noFlee);
            _battle.FleeOut = FleeOutPoint();
            Debug.Log("[battle] " + _battle.Partner.Name + " vs " + enemy.Name + " (" + (_battle.Enemy.Info != null ? _battle.Enemy.Info.LevelName : "?") + ", HP " + _battle.Enemy.MaxHp + ", Off " + _battle.Enemy.Off + ")");
        }

        // handleBattleStart (btl.c), for the other Digimon of the map. A fight you walked into: each one on screen joins (up to 3 enemies) when
        // a roll of 0-99 is under FLEE_CHANCES[partner's type - 1][its type - 1] (DIGIMON_DATA type: 1 Data, 2 Vaccine, 3 Virus; 70 out of 100 when
        // the partner has none), with item 0x23 adding 20 and item 0x24 taking 50 off, and flag 1 making everyone join; on a "concave" map a wall
        // between it and the boy or the partner makes it run. The others run; off-screen ones leave the map.
        // A scripted fight (variable 250 = 1): its enemies are named by the script and every other one runs.
        void GatherOthers(Npc enemy, bool scripted)
        {
            byte[] exe = _disc.ReadFile("/SLUS_010.32");
            uint load = System.BitConverter.ToUInt32(exe, 0x18);
            int flee = (int)(0x8012BB14 - load + 0x800), concaveAt = (int)(0x8012BB20 - load + 0x800);
            bool concave = false;
            for (int i = 0; i < 18; i++) if (exe[concaveAt + i] == _mapId) concave = true;
            var partnerInfo = DigimonStatsCatalog.Get(_disc, PartnerType);
            int count = 1;
            foreach (var n in _npcs)
            {
                if (n == enemy || n.T == null || !n.T.gameObject.activeInHierarchy) continue;
                if (scripted) continue;     // the 0x66 handler adds the script's enemies, and sends the rest away after it
                bool seen = OnScreen(n);
                Debug.Log("[battle] other " + n.Name + " at PS1 " + Ps1Point(n.T.position) + (seen ? " on screen" : " off screen: leaves the map"));
                if (!seen) { n.T.gameObject.SetActive(false); _offAtFight.Add(n.T); continue; }
                bool join = false;
                if (count < 3)
                {
                    bool blocked = false;
                    if (concave) blocked = LineBlocked(TileOf(_player.position).x, TileOf(_player.position).y, TileOf(n.T.position).x, TileOf(n.T.position).y)
                                        || LineBlocked(TileOf(_partner.position).x, TileOf(_partner.position).y, TileOf(n.T.position).x, TileOf(n.T.position).y);
                    if (!blocked)
                    {
                        int chance = UnityEngine.Random.Range(0, 100);
                        if (_state.Bag[0x24] > 0) chance -= 0x32; else if (_state.Bag[0x23] > 0) chance += 0x14;
                        if (_state.GetFlag(1)) chance = 0;
                        var info = DigimonStatsCatalog.Get(_disc, n.Data.Type);
                        int pa = partnerInfo != null ? partnerInfo.Attribute : 0xFF, ea = info != null ? info.Attribute : 1;
                        if (pa != 0xFF && pa >= 1 && pa <= 3 && ea >= 1 && ea <= 3) join = chance < exe[flee + (pa - 1) * 3 + (ea - 1)];
                        else join = UnityEngine.Random.Range(0, 100) < 0x46;
                    }
                }
                Debug.Log("[battle] other " + n.Name + (join ? " joins" : " runs"));
                if (join) { count++; _battle.AddEnemy(n.T, n.Anim, n.Name, DigimonStatsCatalog.Get(_disc, n.Data.Type), n.Data); }
                else RunAway(n);
            }
        }

        void RunAway(Npc n)
        {
            var info = DigimonStatsCatalog.Get(_disc, n.Data.Type);
            Vector3 to = ToUnity(n.Data.Flee); to.y = n.T.position.y;
            _battle.AddRunner(n.T, n.Anim, to, info != null ? info.Height : 0, info != null ? info.Radius : 0);
        }

        void EndBattle()
        {
            if (_audio != null && _mapId >= 0 && _mapId < _mapFlags.Count) _audio.SetMapSounds(_mapFlags[_mapId] & 0x1F);
            if (_audio != null && (_battle.Won || _battle.Fled)) _audio.RestoreBgm();
            _care.AfterBattle(_battle.Won ? PartnerCare.WonBattle : _battle.Fled ? PartnerCare.FledBattle : PartnerCare.Lost);
            if (_battle.Fled && _state.Stats[16] < 9999) _state.Stats[16]++;   // battles fled
            // a lost fight ends with the partner held down in 0x2B (BTL_isBattleFinished: the death countdown); nothing stands it up before MURD
            if (_partnerAnim != null && (_battle.Won || _battle.Fled)) _partnerAnim.SetSlot(IdleSlot);
            // startBattle (tamer.c): unsetCameraFollowPlayer, and nothing in the fight or after it moves the camera, so the picture stays where it
            // was before the fight. Ours used to glide it to the boy (a 20-frame createCameraMovement, listed as ours): after KODA00's Tyrannomon
            // fight the script walks everyone to new places with no camera command, and Tyrannomon was off the picture (user 2026-11-09)
            _camBack = 1f;
            _way.Clear();
            if (_anim != null) _anim.SetSlot(IdleSlot);
            if (!Free(_player.position)) _player.position = _prePlayerPos;
            if (_camOn) { _camMove = false; _camPos = _camGoal = _battleFocus - Vector3.up * 1f; }
            else _camOffsetPx = MapPixel(_battleFocus) - MapPixel(_player.position + Vector3.up * 1f);
            // ours (mod Recentre camera): after a won wild fight (no flag 1: the enemies ran off) the picture glides back to the boy once
            // the script that started the fight has ended
            _recentrePending = ModSettings.FightRecentre && _battle.Won && !_battle.NoFlee;
            // the end of the fight (battle_main.c): a map Digimon with isOnScreen = 1 leaves the map (isOnMap = 0), any other is put back (isOnMap = 1).
            // isOnScreen is not updated during the fight, so the ones that were on screen when it began (the enemies and those that ran) are gone,
            // and the ones that were off screen (taken off the map by handleBattleStart) come back where they were. After a scripted fight its
            // own fighters are left alone. Ours: a Digimon that was already off the map before the fight (a script's 0x48) stays off.
            var inFight = new HashSet<Transform>();
            if (_battleNpc != null && _battleNpc.T != null) inFight.Add(_battleNpc.T);
            // all of the fight's own enemies: the target changes during a fight with several (NextEnemy swaps it with an extra), so the last one
            // fought is Enemy and not in Extras; it was left out and deleted (OGRE11 WaruSeadramon vanished instead of running off, user 2026-11-09)
            if (_battle.Enemy != null && _battle.Enemy.T != null) inFight.Add(_battle.Enemy.T);
            foreach (var x in _battle.Extras) if (x.T != null) inFight.Add(x.T);
            for (int i = _npcs.Count - 1; i >= 0; i--)
            {
                var n = _npcs[i];
                if (n.T == null) { _npcs.RemoveAt(i); continue; }
                if (_battle.NoFlee && inFight.Contains(n.T)) continue;
                if (_onScreenAtFight.Contains(n.T)) { Destroy(n.T.gameObject); _npcs.RemoveAt(i); }
                else if (_offAtFight.Contains(n.T)) n.T.gameObject.SetActive(true);
            }
            _onScreenAtFight.Clear(); _offAtFight.Clear();
            if (!_battle.Won && !_battle.Fled) LoseFight();      // section 0x4DE, as at the end of the fight command
            else if (_battleNpc != null && _battleNpc.T != null && _battleNpc.Anim != null) _battleNpc.Anim.SetSlot(IdleSlot);
            _tamerTile = TileOf(_player.position);     // both stay where the fight left them; the partner walks back to the boy by itself
            _battleNpc = null;
            // the fight command, outcome 0 (ran away): the map he came from, at PREVIOUS_EXIT, without the map name
            if (_battle.Fled && _prevScreen >= 0)
            {
                _runner = null; _line = null; _choiceOpts = null;
                _fade = 1f;     // the picture is black from the fight (fadeToBlack): it stays black until the map is there
                DoChangeMap(new ScriptAction { Map = _prevScreen, Exit = _prevExit, Arrive = 255 });
            }
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
            if (_partnerAnim != null) _partnerAnim.DigimonType = PartnerType;
            if (_audio != null) _audio.SetPartner(PartnerType);
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
        // isOnScreen of a map Digimon (NPCEntityTick: entityIsOffScreen(entity, 0x140, 0xF0) ^ 1); type 0x98 in animation 0x1C always counts as on screen
        bool OnScreen(Npc n)
        {
            if (n.T == null) return false;
            if (n.Data.Type == 0x98 && n.Anim != null && n.Anim.Slot == 0x1C) return true;
            var info = DigimonStatsCatalog.Get(_disc, n.Data.Type);
            return !Battle.OffScreen(_cam, n.T.position, info != null ? info.Radius : 0, info != null ? info.Height : 0);
        }

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
            if (_partnerAnim != null && next != cur && (cur == WalkSlot || cur == 3 || cur == RunSlot) && next != WalkSlot && next != 3 && next != RunSlot)
                Debug.Log("[follow] partner stops " + away.ToString("0.00") + " m from the boy (stands inside " + (radius * 2.5f).ToString("0.00") + " m once its step ends)");
            if (_partnerAnim != null && next != cur)
            {
                _partnerAnim.SetSlot(next);
                _partnerStepEnd = Time.time + _partnerAnim.SecondsOf(next);
            }

            float step = _partnerAnim != null ? _partnerAnim.TakeForward() : WalkSpeed * Time.deltaTime;
            // entityCheckCollision: the two are boxes of their radii, and it stops the moment it touches the boy, even in mid step
            var boyInfo = DigimonStatsCatalog.Get(_disc, 0);
            float touch = radius + (boyInfo != null ? boyInfo.Radius * 0.01f : 1f);
            if (away < radius * 3.5f && Mathf.Abs(p.x - _partner.position.x) < touch && Mathf.Abs(p.z - _partner.position.z) < touch)
            {
                if (next == WalkSlot || next == 3 || next == RunSlot)
                {
                    next = IdleOrSlow;
                    if (_partnerAnim != null) { _partnerAnim.SetSlot(next); _partnerStepEnd = Time.time + _partnerAnim.SecondsOf(next); }
                }
                step = 0f;
            }
            if ((next == WalkSlot || next == 3 || next == RunSlot) && step > 0f)
            {
                Vector3 pos = _partner.position, by = heading * step;
                if (Free(pos, pos + by)) pos += by;
                else if (Free(pos, pos + new Vector3(by.x, 0, 0))) pos.x += by.x;    // a wall stops the blocked direction only
                else if (Free(pos, pos + new Vector3(0, 0, by.z))) pos.z += by.z;
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
            _playerShadowOff = false;      // initializeTamer: PLAYER_SHADOW_ENABLED = 1
            ClearWorldItems();
            ClearMapObjects();

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
            ClearNpcs(); _aiOff.Clear();      // clearMapDigimon: NPC_ACTIVE_ANIM is 0 again

            _map = map;
            MapPath = path;
            string fileName = Path.GetFileNameWithoutExtension(path);
            _mapId = _wantMapId >= 0 && _wantMapId < _mapNames.Count && _mapNames[_wantMapId] == fileName ? _wantMapId : _mapNames.IndexOf(fileName);
            _wantMapId = -1;
            _curPalette = DesiredPalette(out _lightFactor);

            // the game's camera, in game space (Y down)
            _vpG = _map.Viewpoint;
            _fG = (_map.ReferencePoint - _vpG).normalized;
            _rG = Vector3.Cross(new Vector3(0, 1, 0), _fG).normalized;
            _dG = Vector3.Cross(_fG, _rG);
            _h = _map.ViewDistance;
            EffectPlayer.ViewportDistance = (int)_h;

            SetupCamera();
            BuildBackdrop();
            BuildOverlays(mapBytes);
            LoadDoors(mapBytes);
            LoadTrainingPoop(mapBytes);
            LoadWarpCrystals(mapBytes);
            if (Hd2D && UseMapLights) SetupLights();

            // where the Digimon appears
            Vector3 want, face;
            bool atWarp = false; Vector3 partnerAt = Vector3.zero; int warpRot = 0;
            // loadMapEntities (tamer.c) takes the exit's point as it is, with no test for (0, 0): Birdramon's drop-offs use exit 9, which is all zeros on
            // FRZL06 (the middle of the map, facing 0); ours took zeros for "no exit" and searched a free tile near the map's reference point
            if (arriveAtExit >= 0 && arriveAtExit < 10)
            {
                var w = _map.Warps[arriveAtExit];
                // readMapWarps (tamer.c): setupTamerOnWarp puts the boy exactly on the exit's point, even on a blocked tile (OGRE10's Whamon
                // drop-off is inside the pot, which hides him), and setupPartnerOnWarp puts the partner 200 units off it by the exit's
                // rotation (z + 200 up to 0x200 or past 0xE00, x + 200 to 0x600, z - 200 to 0xA00, else x - 200), turned the other way.
                // Ours used to look for the nearest free tile for both (user 2026-11-09: the boy stood on the pot)
                atWarp = true; warpRot = w.Rotation & 0xFFF;
                int px = w.SpawnX, pz = w.SpawnZ, rr = warpRot;
                if (rr <= 0x200 || rr > 0xE00) pz += 200; else if (rr <= 0x600) px += 200; else if (rr <= 0xA00) pz -= 200; else px -= 200;
                partnerAt = new Vector3(px * Unit, -w.SpawnY * Unit, pz * Unit);
                // initializeLoadedMap: the boy and partner stand at the exit's height (spawnY); MAYO04B's lower field is 2600 below its cliff top
                want = new Vector3(w.SpawnX * Unit, -w.SpawnY * Unit, w.SpawnZ * Unit);
                float a = w.Rotation * 2f * Mathf.PI / 4096f;
                face = new Vector3(-Mathf.Sin(a), 0, -Mathf.Cos(a));
            }
            else
            {
                want = ToUnity(_map.ReferencePoint);
                face = Vector3.back;
            }
            _player.position = atWarp ? want : FindStart(want);
            _tamerWay.Clear(); _tamerActive = 0; _tamerStart = _tamerPrev = TileOf(_player.position);
            _player.rotation = Quaternion.LookRotation(-face) * Quaternion.Euler(0, FacingOffset, 0);
            if (_anim != null) _anim.SetSlot(IdleSlot);
            if (atWarp && _partner != null)
            {
                PlacePartner(_player.position, face);
                _partner.position = partnerAt;
                _partner.rotation = Quaternion.LookRotation(face) * Quaternion.Euler(0, FacingOffset, 0);      // (rotation + 0x800) % 0x1000
            }
            else PlacePartner(_player.position, face);
            BuildNpcs();
            ArrivalOverlap(arriveAtExit);
            CareOnMap(mapBytes);
            if (_audio != null && _mapId >= 0 && _mapId < _mapFlags.Count) _audio.SetMapSounds(_mapFlags[_mapId] & 0x1F);
            if (_gridOverlay != null) { Destroy(_gridOverlay); _gridOverlay = null; }
            if (_showGrid) BuildGridOverlay();
            _calibrated = false; _camOffsetPx = Vector2.zero;
            _armed = false;     // exits only work after you have stepped off the arrival spot
            _featherShown = false;
            FishCheckMap();     // checkFishingMap: fishing on maps 6 and 8 (PlayableRoomFishing)
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

        // layer 0: the whole piece (not semi-transparent); 1: its solid colours; 2: its STP colours, drawn with the piece's blend mode
        Material GetOverlayMaterial(int obj, int layer)
        {
            Material m;
            int key = obj * 4 + layer;
            if (_ovMat.TryGetValue(key, out m)) return m;
            var sh = Shader.Find("DW1Remake/Overlay");
            if (sh == null) return null;
            var o = _overlay.Objects[obj];
            // a piece 0 wide or 0 high covers no pixels on the PS1 (its sprite draws nothing); ours made a 1 x 1 texture for an empty picture and
            // SetPixels32 threw, which stopped the whole map from loading (GIAS07 north, user 2026-11-05)
            if (o.W <= 0 || o.H <= 0) return null;
            var tex = new Texture2D(o.W, o.H, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.SetPixels32(_overlay.BuildObject(obj, _map.GetPalette(_curPalette), layer));
            tex.Apply();
            m = new Material(sh);
            m.SetTexture("_BaseMap", tex);
            m.SetVector("_Texel", new Vector4(1f / tex.width, 1f / tex.height, 0, 0));
            m.SetFloat("_BlurMax", Hd2D ? TiltShiftBlur : 0f);
            m.SetFloat("_FocusCenter", FocusCenter);
            m.SetFloat("_FocusHalf", FocusHalfHeight);
            m.SetFloat("_Sharpen", Hd2D ? Sharpen : 0f);
            if (layer == 2) MmdModel.SetPsxBlend(m, o.Trans);
            _ovTex[key] = tex; _ovMat[key] = m;
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
            _mapVram = _overlay;
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

            _ovPieces = new GameObject[_overlay.Instances.Length];
            for (int ii = 0; ii < _overlay.Instances.Length; ii++)
            {
                var inst = _overlay.Instances[ii];
                if ((inst.Flag & 0x80) != 0) continue;
                // renderMapOverlays: a -2 step draws nothing (its timer still runs). A piece may start with one (the Coelamon shadow's
                // 16 pieces wait their turn), so it is built from its first real picture and starts hidden
                int f0 = -1;
                for (int k = 0; k < 8 && inst.Frames[k] != -1; k++) if (inst.Frames[k] >= 0) { f0 = inst.Frames[k]; break; }
                if (f0 < 0 || f0 >= _overlay.Objects.Length) continue;
                bool blankFirst = inst.Frames[0] == -2;
                var ob = _overlay.Objects[f0];
                
                // how far away it is decides whether it is in front of or behind the Digimon
                Vector3 world = new Vector3(ob.WX, ob.WY, ob.WZ);
                float z = Vector3.Dot(world - _vpG, _fG);
                int order = Mathf.Clamp((int)(z / 16f), 35, 4095);
                if (ob.WY == 10000) order = 4094;
                float dm = Mathf.Min(order * 16f * Unit, 295f);

                bool semi = _overlay.IsSemi(f0);
                var mat = GetOverlayMaterial(f0, semi ? 1 : 0);
                if (mat == null && Shader.Find("DW1Remake/Overlay") == null) return;
                bool empty = mat == null;       // an empty piece keeps its place (scripts and animations name pieces by number) but draws nothing
                var go = new GameObject("Piece");
                go.transform.SetParent(_overlayRoot.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = _quadMesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                // the blended colours on a second quad in the same place
                var bgo = new GameObject("Blend");
                bgo.transform.SetParent(go.transform, false);
                bgo.AddComponent<MeshFilter>().sharedMesh = _quadMesh;
                var br = bgo.AddComponent<MeshRenderer>();
                br.sharedMaterial = GetOverlayMaterial(f0, 2);
                br.shadowCastingMode = ShadowCastingMode.Off;
                br.receiveShadows = false;
                br.enabled = semi && !blankFirst && !empty;
                mr.enabled = !blankFirst && !empty;
                PlaceOverlay(go.transform, inst, ob, dm);
                _ovPieces[ii] = go;
                go.SetActive(inst.Flag != 1);       // flag 1 = hidden (renderMapOverlays); scripts switch it (0x57)

                int frames = 0;
                for (int k = 0; k < 8 && inst.Frames[k] != -1; k++) frames++;
                if (frames > 1) { var fs = new System.Text.StringBuilder(); for (int k = 0; k < 8; k++) fs.Append(inst.Frames[k]).Append('/').Append(inst.Times[k]).Append(' '); }
                if (frames > 1) _ovAnims.Add(new OvAnim { T = go.transform, R = mr, Blend = br, I = inst, Cur = 0, Timer = 0, Dm = dm });
            }
        }

        GameObject[] _ovPieces;
        float _white;      // the screen fade to white (built-ins 0x2D / 0x2E)

        System.Collections.IEnumerator WhiteFade(bool toWhite, int frames)
        {
            float secs = Mathf.Max(1, frames) / 20f;
            for (float t = 0f; t < secs; t += Time.deltaTime) { _white = toWhite ? t / secs : 1f - t / secs; yield return null; }
            _white = toWhite ? 1f : 0f;
        }

        // resetMapObjectAnimation: these pieces start their animation again from the first frame
        void ResetPieceAnimation(int start, int count)
        {
            if (_overlay == null) return;
            foreach (var a in _ovAnims)
            {
                int idx = System.Array.IndexOf(_overlay.Instances, a.I);
                if (idx >= start && idx < start + count) { a.Cur = 0; a.Timer = 0; ShowOverlayFrame(a); }
            }
        }

        // renderMapOverlays draws the piece of the current frame every frame, so a piece shows its new frame at once (a -2 frame: nothing).
        // A reset must do the same, or a shadow piece caught showing stays on through its blank frame (Coela Point's double shadow)
        void ShowOverlayFrame(OvAnim a)
        {
            int f = a.I.Frames[a.Cur];
            // -2 = nothing drawn for this long (renderMapOverlays skips the primitive); the painted background shows
            if (f < 0 || f >= _overlay.Objects.Length) { a.R.enabled = false; a.Blend.enabled = false; return; }
            bool semi = _overlay.IsSemi(f);
            var mat = GetOverlayMaterial(f, semi ? 1 : 0);
            if (mat == null) { a.R.enabled = false; a.Blend.enabled = false; return; }     // an empty frame: nothing drawn
            a.R.enabled = true;
            a.R.sharedMaterial = mat;
            a.Blend.enabled = semi;
            if (semi) a.Blend.sharedMaterial = GetOverlayMaterial(f, 2);
            PlaceOverlay(a.T, a.I, _overlay.Objects[f], a.Dm);
        }

        // SET_OBJ_VISIBILITY -> setMapObjectsFlag(object, 1, flag)
        void SetPieceVisible(int index, int flag)
        {
            if (_overlay == null || index < 0 || index >= _overlay.Instances.Length) return;
            _overlay.Instances[index].Flag = flag;
            if (_ovPieces != null && _ovPieces[index] != null) _ovPieces[index].SetActive(flag != 1);
        }

        void TickOverlayAnimations()
        {
            if (_overlay == null || _ovAnims.Count == 0 || _overlayRoot == null || !_overlayRoot.activeSelf) return;
            _ovAcc += Time.deltaTime * 20f;     // renderMapOverlays steps the timers once a game frame (20 a second)
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
                    ShowOverlayFrame(a);
                }
            }
        }

        void RefreshOverlayPalette()
        {
            if (_overlay == null) return;
            var pal = _map.GetPalette(_curPalette);
            foreach (var kv in _ovTex)
            {
                if (!_overlay.UsesMapPalette(kv.Key / 4)) continue;
                kv.Value.SetPixels32(_overlay.BuildObject(kv.Key / 4, pal, kv.Key % 4));
                kv.Value.Apply();
            }
        }

        void SetupCamera()
        {
            _cam = Camera.main;
            if (_cam == null) _cam = new GameObject("Main Camera").AddComponent<Camera>();
            DigiviceModel.ExcludeFrom(_cam);       // the Status page's partner copy is seen only by its own camera
            MedalModel.ExcludeFrom(_cam);          // the Medals page's turning medal, the same way
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
                mat = new Material(MmdModel.UnlitShader);
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
                    var p = new Vector3((tx - 50 + 0.5f), want.y, (50 - tz - 0.5f));    // tile centre in metres
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

        bool Free(Vector3 u) { return GroundFree(u) && !BlockedByNpc(u); }

        // a step from 'from' to 'u' (see BlockedByNpc)
        bool Free(Vector3 from, Vector3 u) { return GroundFree(u) && !BlockedByNpc(u, from); }

        bool GroundFree(Vector3 u)
        {
            const float r = 0.3f;
            if (TileBlocked(u)) return false;
            return Walkable(u.x - r, u.z - r) && Walkable(u.x + r, u.z - r) && Walkable(u.x - r, u.z + r) && Walkable(u.x + r, u.z + r);
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

            // the game's drop shadow (renderDropShadow); built once, it is read from the disc
            if (_dropShadow == null) _dropShadow = new DropShadow(_disc);
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
            // the pad's directions (Pad: mapped keys, controller, touch buttons)
            float x = 0, y = 0;
            if (Pad.Held(PadButton.Right)) x += 1;
            if (Pad.Held(PadButton.Left)) x -= 1;
            if (Pad.Held(PadButton.Up)) y += 1;
            if (Pad.Held(PadButton.Down)) y -= 1;
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
                if (FishIsFishing) return false;      // tickGameClock: no time passes while the boy fishes (isFishing)
                return (_menus == null || !_menus.Active) && (_battle == null || !_battle.Active) && !(_onlineOn && !_onlineMatch);
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
#if UNITY_EDITOR || DEVELOPMENT_BUILD
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (k != null && k.tKey.wasPressedThisFrame) _care.SkipHours(1);   // T = jump forward one hour (test key)
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.T)) _care.SkipHours(1);
#endif
#endif
            _clock = _care.ClockHours;
            if (_audio != null) { _audio.Tick(); if (_battle == null || !_battle.Active) _audio.UpdateBgm(_care.Hour); }
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

        // main.c: the game's loop waits VSync(3), every third screen refresh of a 60 Hz TV, so the picture changes 20 times a second; the title
        // and memory-card menus wait VSync(0) (60). The mod Smooth 60 FPS (ours) keeps 60 everywhere. Unity's vSync is off so the limit holds
        void LimitFrameRate()
        {
            int want = _title != null || _movie != null || _picker != null || ModSettings.Smooth60 ? 60 : 20;
            if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
            if (Application.targetFrameRate != want) Application.targetFrameRate = want;
        }

        void Update()
        {
            LimitFrameRate();
            GameMenus.TickRepeat();
            if (_title != null || _picker != null || _movie != null) return;      // the title screen reads its own buttons (Title); the disc screen has its own
            HandleDebugAndSaveKeys();
            UpdateClock();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // test keys F1-F4: not in a player build
            if (GridKeyPressed()) ToggleGrid();
            if (NamesKeyPressed()) ShowNames = !ShowNames;
            if (StatsKeyPressed()) _showStats = !_showStats;
            if (OverlayKeyPressed()) { ShowOverlays = !ShowOverlays; if (_overlayRoot != null) _overlayRoot.SetActive(ShowOverlays); }
#endif
            TickOverlayAnimations();
            MapViewAutoOff();
            if (_recentrePending && _runner == null && !_camOn && !_changing && !(_battle != null && _battle.Active))
            {
                // the same 1 s glide from where the fight left the picture to the boy, then the picture follows him from his middle
                _recentrePending = false; _camOffsetPx = Vector2.zero; _camBack = 0f;
            }
            if (_changing)
            {
                // the map Digimon of the new map animate while the screen is still black: what their walk carried meanwhile must not be paid out
                // at once afterwards (Betamon shot across TWNB07 to his stall on arrival)
                foreach (var changing in _npcs) if (changing.Anim != null) { changing.Anim.TakeForward(); changing.Anim.TakeBodyMove(); }
                return;
            }
            if (_player == null || _map == null) return;
            TickArena();        // the arena's world at 20 frames a second while a tournament runs (PlayableRoomArena)
            SyncPiles();
            TickMapObjects();
            if (_setup != null && _setup.Active) { _setup.Tick(MenuStep(), GameMenus.SideStep(), ActionPressed(), GameMenus.CancelKey()); return; }
            _state.PlayMinutes += Time.deltaTime / 60f;
            bool fighting = _battle != null && _battle.Active;
            // the online battle screen (ours) stops the world as the Triangle menu does; its match does not
            bool onlineScreen = _onlineOn && !_onlineMatch;
            DigimonAnimator.Paused = (_menus != null && _menus.Active || onlineScreen) && !fighting;      // the Triangle menu stops everyone at once; the item page in a fight does not
            HideActors(_menus != null && _menus.HidesActors);
            if (onlineScreen) return;
            if (_menus != null)
            {
                _menus.Species = _partnerName; _menus.Day = _dayCount + 1;
                _menus.Year = _dayCount / 30 + 1; _menus.DayOfYear = _dayCount % 30 + 1;
                if (_menus.Active)
                {
                    _menus.Tick(MenuStep(), GameMenus.SideStep(), ActionPressed(), GameMenus.CancelKey());
                    if (fighting)
                    {
                        // the fight goes on under the item page; a chosen item closes it and the boy throws it
                        if (_menus.LastUsedItem >= 0) { int id = _menus.LastUsedItem; _menus.LastUsedItem = -1; _battle.ThrowItem(id, () => _menus.ApplyItem(id)); }
                        _battle.ReadBack();
                        _battle.Tick(0, false, false, false);
                    }
                    return;
                }
                if (_battle != null && _battle.Active)
                {
                    _battle.ReadBack();
                    _battle.Tick(GameMenus.SideStep(), ActionPressed(), GameMenus.TriangleKey() || GameMenus.BagKey(), GameMenus.CancelKey());   // Triangle opens the item page
                    if (!_battle.Active && !_tourOn)        // an arena match: the tournament's own code takes over (PlayableRoomTournament)
                    {
                        EndBattle();
                        if (_battleWait)
                        {
                            _battleWait = false;
                            // SCRIPT_OP_START_BATTLE, a won fight: with trigger 1 set, setMovementEnabled(-1, 1)
                            if (_runner != null && _battle.Won && _state.GetFlag(1)) StopAllMapDigimon();
                            if (_runner != null) AdvanceScript();
                        }
                    }
                    return;
                }
                if (_runner == null && !_pBusy && !_routineOn)     // (not while our routine without a script runs: the online link screen takes typed codes) tamerTickWalkingState: the menu opens only while the boy walks freely, not during a partner scene (tamer state 6)
                {
                    if (GameMenus.EscapeKey()) { StartCoroutine(BackToTitle()); return; }      // ours: Escape while walking = back to the title screen
                    if (GameMenus.TriangleMenuKey()) { _mapViewOn = false; _menus.OpenMain(); return; }      // isKeyDown(Triangle): held and fresh
                    MapViewKey();       // ours: Select opens / closes the Map mod
                    if (GameMenus.BagKey()) { _menus.OpenBag(); return; }
                    if (GameMenus.ShopKey() && !_routineOn) { StartCoroutine(Shop(i => i < _itemCatalog.Count && _itemCatalog[i].Price > 0 && _itemCatalog[i].Category <= 2, 0xFF)); return; }   // dev: a test shop with every priced item, the 0x4CE texts
                    if (GameMenus.GiveKey()) _menus.DebugGive();
                }
            }
            UpdateNpcs();
            TickActors();
            // the fish swim on the fishing maps whatever the boy does (object 0x3001); while he fishes (tamer state 11) FISH_tickRod has him
            FishUpdate();
            if (_fishTamer) return;
            if (_runner != null && !_pBusy && !SetupWaitOnly() && _partnerAnim != null && _partner != null && !_movers.ContainsKey(_partner) && !_partnerAnim.Busy
                && (_partnerAnim.Slot == WalkSlot || _partnerAnim.Slot == RunSlot)) _partnerAnim.SetSlot(IdleSlot);    // no running on the spot during a talk
            if (_pBusy && _runner == null) return;
            if (_eggOpen)
            {
                // DOO2_tickEggInput until the box has shrunk away
                int es = GameMenus.SideStep();
                bool gone = _eggBox == null ? ActionPressed() : _eggBox.Tick(es, ShopCross());
                if (_eggBox != null) _eggSlot = _eggBox.Slot; else if (es != 0) _eggSlot = (_eggSlot + es + 4) % 4;
                if (gone) { _eggOpen = false; AdvanceScript(); }
                return;
            }
            if (_saveMachineOn || _sys != null || _routineOn) return;      // the save machine / sleep menu / a built-in routine reads the buttons itself
            if (_runner != null)
            {
                if (_scriptFadeOut) return;      // scriptTickChangeMap: the picture fades, the buttons do nothing

                if (_wait > 0f || _waitAll)
                {
                    if (SetupWaitOnly())
                    {
                        // the camera was held for the script (FreezeCamera): it goes along with his steps, as it does after a script
                        Vector3 was = _player.position;
                        WalkBoy();
                        if (_camOn) { _camPos += _player.position - was; _camGoal = _camPos; }
                    }
                    _wait -= Time.deltaTime;
                    if (_wait <= 0f && (!_waitAll || !BusyFor(_waitFor))) { _wait = 0f; _waitAll = false; _waitFor = 0xFF; AdvanceScript(); }
                    return;
                }
                if (_choiceOpts != null)
                {
                    int ms = MenuStep();
                    // tickSelectionDialogue: up/down wrap round (sound 2), Cross picks (sound 3); Circle leaves the selection (sound 4) unless flag 0x31 is on (sound 0xB)
                    if (ms != 0) { _choiceSel = (_choiceSel + ms + _choiceOpts.Count) % _choiceOpts.Count; Snd(2); }
                    if (ActionPressed()) { Snd(3); _runner.Choose(_choiceSel); _choiceOpts = null; AdvanceScript(); }
                    else if (GameMenus.CancelKey())
                    {
                        if (_state.GetFlag(0x31)) Snd(0xB);
                        else { Snd(4); _runner.CancelChoice(); _choiceOpts = null; AdvanceScript(); }
                    }
                }
                // tickConfirmDialogue: Cross (once the box is open) goes on with sound 3
                else if (DialogueReady && ActionPressed()) { Snd(3); AdvanceScript(); }
                // tickCustomSizedTextbox (the 0x26 box): Circle too ends the text, with sound 4
                else if (_lineCustom && _line != null && DialogueReady && GameMenus.CancelKey()) { Snd(4); AdvanceScript(); }
                return;
            }
            CheckCareRequests();
            TickTournamentRegistration();
            if (_care.WantAutoPilot) { _care.WantAutoPilot = false; StartDgSection(0x4DD); }
            if (_pBusy || _runner != null) return;
            if (!_changing) { TryStartBattle(); if (_battle != null && _battle.Active) return; if (_runner != null) return; }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            SlotViewer();
            if (RecruitKey()) { _state.SetFlag(0xF9, true); BuildNpcs(); Say("Test: Coelamon recruited (flag 0xF9 on)"); }
#endif
            CheckPickup();
            if (_pBusy) return;
            if (ActionPressed() && TryChest()) return;
            if (ActionPressed()) TryTalk();
            if (_runner != null) return;
            WalkBoy();

            // exits: grid values 110..119, doors and triggers: 50..109
            int cell = _map.CollisionAt(_player.position.x / Unit, _player.position.z / Unit);
            if (cell == 120 && (_state.Para.Condition & PartnerCare.Poopy) != 0) { if (_armed) { _armed = false; StartDgSection(ToiletSection); } }
            else if (cell >= 110 && cell < 120)
            {
                if (_armed) TryExit(cell - 110);
            }
            else if (cell > 50 && cell < 80)
            {
                // checkMapInteraction: 51..79 call their section every frame he stands there while no script runs (on arrival too); a door with no section
                // (ours: the map change found in the script) still waits until he has stepped off
                if (_scripts != null && _scripts.Available && ScriptRunner.HasSection(_scripts, _scripts.ScriptIdForMap(_mapId), cell))
                {
                    AutoTile(cell);
                    // Ours (user's request 2026-11-09): MIST03's section 54, "Hey, a bird's feathers are fallen here. They are still fresh. Are we too
                    // late?", is on the disc but nothing starts it. It plays on tile 52, once per visit, when tile 52's own section did nothing
                    // and Kokatorimon is neither here (flag 174, its morning hours) nor in the city (flag 250)
                    if (_runner == null && cell == 52 && !_featherShown && _mapId >= 0 && _mapNames[_mapId] == "MIST03" && !_state.GetFlag(174) && !_state.GetFlag(250))
                    { _featherShown = true; AutoTile(54); }
                }
                else if (_armed) TryDoor(cell);
            }
            else if (cell >= 80 && cell < 110)
            {
                // 80..109 run on every Cross press while he stands there, so no stepping off is needed
                // mod Online battle: Cross at Greymon's counter (tile 81 in ROOM13 / ROOM19) asks first (PlayableRoomOnline)
                if (ActionPressed() && !OnlineArenaTile(cell)) TryDoor(cell);
            }
            else _armed = true;
        }

        // the boy's free walk and his partner following (the walking part of Update)
        void WalkBoy()
        {
            Vector2 input = ReadMove();
            // arrow keys follow the screen: "up" walks up the screen, "right" to the right, whatever way the map's camera faces
            Vector3 dir = ScreenToGround(input);
            bool moving = dir.sqrMagnitude > 0.01f;
            if (moving)
            {
                dir.Normalize();
                TickTamerWay();
                Vector3 step = dir * (_anim != null ? _anim.TakeForward() : WalkSpeed * Time.deltaTime);
                Vector3 pos = _player.position, was = pos;
                // checkMapInteraction: the boy's collision test leaves his partner out, so he walks through it
                if (Free(pos, pos + new Vector3(step.x, 0, 0))) pos.x += step.x;
                if (Free(pos, pos + new Vector3(0, 0, step.z))) pos.z += step.z;
                if (step.sqrMagnitude > 0f)
                {
                    // entityCheckCollision: the step is tried (entityMoveForward) and tested against every map Digimon's box; a step into one is not
                    // taken (animFlag 2) and that Digimon is the one touched. So the boy never stands inside a box, and once he stops pushing
                    // there is no touch: a talk-on-touch Digimon does not start again by itself (GIAS09's Leomon, user 2026-11-05)
                    Npc hit = BumpInto(was, pos);
                    if (hit != null) pos = was;
                    _bump = hit;
                }
                _player.position = pos;
                var target = Quaternion.LookRotation(-dir) * Quaternion.Euler(0, FacingOffset, 0);
                _player.rotation = Quaternion.RotateTowards(_player.rotation, target, TurnSpeed * Time.deltaTime);
            }
            else _bump = null;      // standing: the idle animation moves him nowhere, so the test at his own place finds nothing
            if (_anim != null)
            {
                int want = !moving ? IdleSlot : GameMenus.WalkHeld() ? WalkSlot : PlayerRunSlot;    // the boy runs; holding R1 (E) makes him walk
                if (_anim.Slot != want) _anim.SetSlot(want);
            }

            UpdatePartner(dir);
        }

        Npc _bump;      // the map Digimon the boy's last step ran into (entityCheckCollision's answer)
        bool _featherShown;      // ours: MIST03's unused feather line has played on this visit

        // the first active map Digimon whose box the step from 'from' to 'to' runs into. Fix kept (Glitch FAQ C4, user 2026-11-04): a boy who is already
        // inside a box (a Digimon walked onto him) may step anywhere that does not take him closer to it, and that is no touch
        Npc BumpInto(Vector3 from, Vector3 to)
        {
            foreach (var n in _npcs)
            {
                if (n.T == null || !n.T.gameObject.activeInHierarchy || !TouchingAt(n, to)) continue;
                if (TouchingAt(n, from))
                {
                    Vector3 a = from - n.T.position, b = to - n.T.position; a.y = 0f; b.y = 0f;
                    if (b.sqrMagnitude >= a.sqrMagnitude) continue;
                }
                return n;
            }
            return null;
        }

        // NPCEntityTickOverworld: a map Digimon whose own step runs into the boy (entityCheckCollision answers 0, the tamer) touches him too
        bool NpcPushing(Npc n)
        {
            if (n.Stopped || n.Anim == null || !Touching(n)) return false;
            if (n.RanIntoBoy) return true;
            return n.Anim.Slot == WalkSlot || n.Anim.Slot == RunSlot || n.Anim.Slot == ChaseSlot;
        }

        // STILL OURS (user's decision 2026-11-05): while a map's 0xFE setup script only waits (0x67, no text, no choice, nothing of the boy or
        // partner moving under the script, no script camera move), the boy may walk. On the PS1 those waits sit under the 20-frame fade-in
        // (TWNB07: 15 + 5 frames after Betamon's glide); ours fades in faster and the boy stood frozen. No exits or triggers meanwhile.
        bool SetupWaitOnly()
        {
            if (_runnerSection != 0xFE || _wait <= 0f || _waitAll || _line != null || _choiceOpts != null || _camMove || _playerScripted) return false;
            Mover m;
            if (_player != null && _movers.TryGetValue(_player, out m) && (m.Walking || m.Gliding || m.Looking)) return false;
            if (_partner != null && _movers.TryGetValue(_partner, out m) && (m.Walking || m.Gliding || m.Looking)) return false;
            return true;
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
            if (Pad.Pressed(PadButton.Up)) return -1;
            if (Pad.Pressed(PadButton.Down)) return 1;
            return 0;
        }

        static bool ActionPressed()
        {
            return Pad.Pressed(PadButton.Cross) || Pad.Pressed(PadButton.Start) || Pad.Pressed(PadButton.Circle);
        }

        // does the map 'id' have an exit that leads back to the current map?
        int ConnectsBack(int id)
        {
            try
            {
                string p = MapCatalog.PathFor(_mapNames, id);     // only looks: not PathOfMap
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
                string next = PathOfMap(h[0]);
                if (!_disc.Exists(next) || !_disc.Exists(next.Substring(0, next.Length - 4) + ".TFS")) continue;
                Debug.Log("Door " + trigger + " in " + _mapNames[_mapId] + " -> " + _mapNames[h[0]] + " exit " + h[1] + (hits.Count > 1 ? "  (script has " + hits.Count + " possible targets, using the first)" : ""));
                StartCoroutine(ChangeMap(next, h[1], false, false, true, h[0]));
                return;
            }
            // checkMapInteraction -> callScriptSection(script, trigger, 0): the map's script has no section for this tile (getScriptSection
            // gives 0; YAKA25's script 5 is only a stub table, sections 5-8), so nothing happens on the PS1 either. Ours said "not supported"
            // on the screen (user 2026-11-11); now only the Console notes it
            Debug.Log("[script] trigger " + trigger + " in " + _mapNames[_mapId] + ": the map's script has no section for it (nothing happens)");
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
            string next = PathOfMap(w.TargetMap);
            if (!_disc.Exists(next) || !_disc.Exists(next.Substring(0, next.Length - 4) + ".TFS"))
            {
                Say("Map file not found: " + next);
                _armed = false;
                return;
            }
            _prevExit = exitIndex;      // checkMapInteraction: PREVIOUS_EXIT = trigger - 110
            StartCoroutine(ChangeMap(next, w.TargetExit, false, false, true, w.TargetMap));
        }

        /// <param name="script">a script's 4B (scriptTickChangeMap): the world and the script's scene go on under the fade; the scene ends once it is black</param>
        /// <param name="walk">an exit walked into (tamerTickChangeMap): startAnimationTamer(2) for the fade</param>
        /// <param name="nameOf">addMapNameObject: from fade frame 10 this map's area name is drawn on the fading picture until the new map is in (-1 none)</param>
        IEnumerator ChangeMap(string path, int exit, bool instant = false, bool script = false, bool walk = false, int nameOf = -1)
        {
            string name = nameOf >= 0 ? AreaName(nameOf) : null;
            int wantId = _wantMapId;       // the number PathOfMap gave with this path, kept through the fade
            if (script)
            {
                // the room keeps ticking (glides, animations, map Digimon) while the picture goes black
                for (float t = _fade; t < 1f; t += Time.deltaTime / Mathf.Max(0.01f, FadeSeconds)) { _fade = t; if (t >= 0.5f) _mapNameShown = name; yield return null; }
                ScriptMapChangeEnd();
            }
            _changing = true;
            if (_anim != null && !script) _anim.SetSlot(walk ? WalkSlot : IdleSlot);
            // instant: under the life-lost scene's full-screen black (MURD_renderFullscreenFade 0xFF), which changes map with no fade of its own
            // a fade already under way (Run's fadeToBlack) goes on from where it is
            if (!instant) for (float t = _fade; t < 1f; t += Time.deltaTime / Mathf.Max(0.01f, FadeSeconds)) { _fade = t; if (t >= 0.5f) _mapNameShown = name; yield return null; }
            _fade = instant ? 0f : 1f;
            if (!instant) { _mapNameShown = name; yield return null; }      // ours: the black frame with the name is seen at least once (the PS1 shows it while the disc loads)
            if (_anim != null && walk) _anim.SetSlot(IdleSlot);      // changeMap: initializeTamer puts him back in his idle
            int fromMap = _mapId;
            _wantMapId = wantId;
            try { LoadMap(path, exit); NotePreviousMap(fromMap, _mapId, exit); }
            catch (Exception e) { _error = e.Message; Debug.LogException(e); }
            _mapNameShown = null;      // removeObject(0xFA1) right after changeMap
            yield return null;
            // scriptTickChangeMap: changeMap (the map head script, then its section 0xFE and the arrival section) and fadeFromBlack(20) in the same
            // frame, so the map's own script already plays while the picture fades in. TWNB07's 0xFE glides the recruited Betamon 33 m to his stall
            // in 10 frames (6C 07): on the PS1 that ends under the fade; ours ran it after the fade, and he was seen shooting across the map.
            _changing = false;
            int arrive = _arrive; _arrive = 255;
            if (arrive == 255 && _state.SectionOnExit >= 0) { arrive = _state.SectionOnExit; _state.SectionOnExit = -1; }     // SCRIPT_OP_SECTION_ON_EXIT
            if (_intro && _mapId >= 0 && _mapNames[_mapId] == "TWNA01") FinishIntro();
            else RunMapStart(arrive);
            if (!instant)
            {
                // a fade the script starts itself takes over (one fade on the PS1): stop writing ours as soon as someone else changed it
                float wrote = _fade;
                for (float t = 1f; t > 0f; t -= Time.deltaTime / Mathf.Max(0.01f, FadeSeconds))
                {
                    if (_fade != wrote) yield break;
                    _fade = wrote = t; yield return null;
                }
                if (_fade == wrote) _fade = 0f;
            }
        }

        int _thenSection = 255;

        // returnFromScriptFile (stack entry 3): once the map head script ends, the map's own script runs its section 0xFE (map pieces shown or hidden,
        // ground items, chests...); then the arrival section of the map change (stack entry 4)
        void RunMapStart(int arrive)
        {
            _thenSection = arrive;
            if (!StartScript(0xFE, null) && _runner == null && _thenSection != 255) { int s = _thenSection; _thenSection = 255; StartScript(s, null, true); }
        }

        void Say(string text) { _message = text; _messageUntil = Time.time + 3f; }

        // ------------------------------------------------------------------ camera scrolling

        // ---- the camera as the game keeps it: a place in pixels over the painted map (MAP_TILE_DATA.cameraX / Y), held inside the map
        Vector2 _camOffsetPx;      // free walking: how far the camera's middle is from the boy (the game's camera only follows his steps)

        // cameraIsAtEdge's limits as a clamp of a picture point
        Vector2 ClampPx(Vector2 c)
        {
            float winH = WindowHeightPx, winW = winH * (_cam != null ? _cam.aspect : 4f / 3f);
            float cx = _map.PixelWidth <= winW ? _map.PixelWidth * 0.5f : Mathf.Clamp(c.x, winW * 0.5f, _map.PixelWidth - winW * 0.5f);
            float cy = _map.PixelHeight <= winH ? _map.PixelHeight * 0.5f : Mathf.Clamp(c.y, winH * 0.5f, _map.PixelHeight - winH * 0.5f);
            return new Vector2(cx, cy);
        }

        // the world point at that picture point, at a height (MapPixel backwards)
        Vector3 WorldAtPixel(Vector2 px, float unityY)
        {
            Vector3 dir = _fG + ((px.x - _map.PixelWidth * 0.5f) / _h) * _rG + ((px.y - _map.PixelHeight * 0.5f) / _h) * _dG;
            float gy = -unityY / Unit;
            float k = Mathf.Abs(dir.y) < 1e-6f ? -1f : (gy - _vpG.y) / dir.y;
            // a picture point above the horizon (GIAS06B's sky at the top of the map) never meets that height in front of the camera: the
            // height was met behind it, whose picture point is another one, so ClampCam moved the camera again every frame and a 0x4F / 4A C8
            // wait never ended (user 2026-11-05). Every point on the ray shows at the same picture point: take one in front instead
            if (k <= 0f) k = 1000f;
            return ToUnity(_vpG + dir * k);
        }

        // a camera place (the point 1 m under the middle of the picture) moved inside the map
        Vector3 ClampCam(Vector3 pos)
        {
            if (_map == null || _h == 0f) return pos;
            Vector3 f = pos + Vector3.up;
            Vector2 px = MapPixel(f), c = ClampPx(px);
            // already inside the map: the point stays as it is. Rebuilding it every frame moved a point that shows the sky (GIAS06B) to a new height
            // each time, and the camera crept sideways for ever (user 2026-11-06)
            if ((c - px).sqrMagnitude < 0.01f) return pos;
            return WorldAtPixel(c, f.y) - Vector3.up;
        }

        // the camera place shown now
        Vector3 VisibleCamPos()
        {
            if (_map == null || _h == 0f) return _camOn ? _camPos : _player.position;
            Vector3 f = (_camOn ? _camPos : _player.position) + Vector3.up;
            Vector2 want = MapPixel(f) + (_camOn ? Vector2.zero : _camOffsetPx), c = ClampPx(want);
            if (_camOn && (c - want).sqrMagnitude < 0.01f) return _camPos;
            return WorldAtPixel(c, f.y) - Vector3.up;
        }

        // tickCameraMovement: the picture moves by (target - now) / speed whole pixels a game frame, plus one more pixel for the first
        // |remainder| + 1 frames, and stops on the target (or a map edge: the target is held inside the map first)
        bool _camMove; Vector2 _camMoveCur, _camMoveTo, _camMoveStep; int _camMoveExtraX, _camMoveExtraY; float _camMoveAcc, _camMoveY;
        void StartCamMove(Vector3 target, int speed)
        {
            speed = Mathf.Max(1, speed);
            Vector3 f = target + Vector3.up;
            Vector2 from = ClampPx(MapPixel(_camPos + Vector3.up)), to = ClampPx(MapPixel(f));
            from = new Vector2(Mathf.Round(from.x), Mathf.Round(from.y)); to = new Vector2(Mathf.Round(to.x), Mathf.Round(to.y));
            int dx = (int)(to.x - from.x), dy = (int)(to.y - from.y);
            _camMoveCur = from; _camMoveTo = to; _camMoveStep = new Vector2(dx / speed, dy / speed);
            _camMoveExtraX = Mathf.Abs(dx % speed); _camMoveExtraY = Mathf.Abs(dy % speed);
            _camMoveY = f.y; _camMoveAcc = 0f; _camMove = true;
            _camGoal = WorldAtPixel(to, f.y) - Vector3.up;
        }

        void TickCamMove()
        {
            _camMoveAcc += Time.deltaTime;
            while (_camMove && _camMoveAcc >= 1f / 20f)
            {
                _camMoveAcc -= 1f / 20f;
                // which way each axis still has to go; 0 once it is there (Mathf.Sign(0) is +1: an axis that arrived first with a negative step
                // went on past its target for ever, and a 4A C8 wait never ended: GKYO02 ShogunGekomon's 0x50 back to the boy, user 2026-11-07)
                float sx = _camMoveTo.x > _camMoveCur.x ? 1f : _camMoveTo.x < _camMoveCur.x ? -1f : 0f;
                float sy = _camMoveTo.y > _camMoveCur.y ? 1f : _camMoveTo.y < _camMoveCur.y ? -1f : 0f;
                Vector2 c = _camMoveCur + _camMoveStep;
                if (_camMoveExtraX-- >= 0) c.x += sx;
                if (_camMoveExtraY-- >= 0) c.y += sy;
                if (sx > 0 ? c.x > _camMoveTo.x : c.x < _camMoveTo.x) c.x = _camMoveTo.x;
                if (sy > 0 ? c.y > _camMoveTo.y : c.y < _camMoveTo.y) c.y = _camMoveTo.y;
                if (sx == 0) c.x = _camMoveTo.x;
                if (sy == 0) c.y = _camMoveTo.y;
                _camMoveCur = c;
                _camPos = WorldAtPixel(c, _camMoveY) - Vector3.up;
                if (c == _camMoveTo) { _camMove = false; _camPos = _camGoal; }
            }
        }

        // a script runs (tamer state 10): tickCameraFollowPlayer stops, the camera stays put until a script command moves it
        void FreezeCamera()
        {
            if (_camOn || _player == null) return;
            _camPos = _camGoal = VisibleCamPos(); _camOn = true; _camOffsetPx = Vector2.zero;
        }

        Vector2 MapPixel(Vector3 unityPos)
        {
            Vector3 q = ToGame(unityPos) - _vpG;
            float z = Vector3.Dot(q, _fG);
            return new Vector2(_map.PixelWidth * 0.5f + _h * Vector3.Dot(q, _rG) / z,
                               _map.PixelHeight * 0.5f + _h * Vector3.Dot(q, _dG) / z);
        }

        void ApplyScroll(Vector2 shiftSign, out Vector2 expectedViewport, Vector3 focusPoint, Vector2 extraPx = default(Vector2))
        {
            float winH = WindowHeightPx, winW = winH * _cam.aspect;
            _cam.sensorSize = new Vector2(24f * _cam.aspect, 24f);
            _cam.focalLength = _h * 24f / winH;

            Vector2 c = MapPixel(focusPoint) + extraPx;
            float cx = _map.PixelWidth <= winW ? _map.PixelWidth * 0.5f : Mathf.Clamp(c.x, winW * 0.5f, _map.PixelWidth - winW * 0.5f);
            float cy = _map.PixelHeight <= winH ? _map.PixelHeight * 0.5f : Mathf.Clamp(c.y, winH * 0.5f, _map.PixelHeight - winH * 0.5f);
            cx = Mathf.Round(cx); cy = Mathf.Round(cy);      // MAP_TILE_DATA.cameraX / Y are whole pixels
            float dx = cx - _map.PixelWidth * 0.5f, dy = cy - _map.PixelHeight * 0.5f;
            _scrollPx = new Vector2(cx, cy);
            _cam.lensShift = new Vector2(shiftSign.x * dx / winW, shiftSign.y * -dy / winH);

            // where the focus point should land on screen if the sign choice is right
            expectedViewport = new Vector2((c.x - (cx - winW * 0.5f)) / winW, 1f - (c.y - (cy - winH * 0.5f)) / winH);
        }

        void LateUpdate()
        {
            if (_player == null || _map == null) return;
            Rect pic = UiKit.Picture;      // the PS1 picture is 4:3: the camera draws only there, black bars beside it
            _cam.rect = new Rect(pic.x / Screen.width, pic.y / Screen.height, pic.width / Screen.width, pic.height / Screen.height);
            if (_arenaOn) { ArenaCameraLate(); return; }     // the arena's own camera (STD_tickVSPhase); no map scrolling, shadows or map items there
            Vector3 focus = (_camOn ? _camPos : _player.position) + Vector3.up * 1f;
            Vector2 extra = _camOn ? Vector2.zero : _camOffsetPx;
            if (_battle != null && _battle.Active) { focus = _battleFocus; extra = Vector2.zero; }      // unsetCameraFollowPlayer: the camera stays where it was for the whole fight
            else if (_camBack < 1f)
            {
                _camBack = Mathf.Min(1f, _camBack + Time.deltaTime);
                focus = Vector3.Lerp(_battleFocus, focus, _camBack); extra = Vector2.zero;
            }
            // fishing moves the picture itself (FISH_scrollCameraTo)
            Vector2 fishPx;
            if (!(_battle != null && _battle.Active) && FishCameraPixel(out fishPx)) { focus = _player.position + Vector3.up * 1f; extra = fishPx - MapPixel(focus); }

            if (!_calibrated)
            {
                // Try the four sign combinations of the lens shift and keep the one that puts the Digimon where it should be.
                float best = float.MaxValue;
                foreach (var sx in new[] { 1f, -1f })
                    foreach (var sy in new[] { 1f, -1f })
                    {
                        Vector2 exp;
                        var s = new Vector2(sx, sy);
                        ApplyScroll(s, out exp, focus, extra);
                        Vector3 vp = _cam.WorldToViewportPoint(focus);
                        float err = (new Vector2(vp.x, vp.y) - exp).sqrMagnitude;
                        if (err < best) { best = err; _shiftSign = s; }
                    }
                _calibrated = true;
            }
            Vector2 e2;
            ApplyScroll(_shiftSign, out e2, focus, extra);
            // updateDrawingOffsets: at a map edge the picture stops while the boy walks on, so an offset the camera was left with shrinks there (never grows)
            if (!_camOn && !_fishCamOn && extra != Vector2.zero && !(_battle != null && _battle.Active))
            {
                Vector2 me = MapPixel(_player.position + Vector3.up), vis = _scrollPx - me;
                if (Mathf.Sign(vis.x) == Mathf.Sign(_camOffsetPx.x) && Mathf.Abs(vis.x) < Mathf.Abs(_camOffsetPx.x)) _camOffsetPx.x = vis.x;
                if (Mathf.Sign(vis.y) == Mathf.Sign(_camOffsetPx.y) && Mathf.Abs(vis.y) < Mathf.Abs(_camOffsetPx.y)) _camOffsetPx.y = vis.y;
                if (Mathf.Sign(vis.x) != Mathf.Sign(_camOffsetPx.x)) _camOffsetPx.x = 0f;
                if (Mathf.Sign(vis.y) != Mathf.Sign(_camOffsetPx.y)) _camOffsetPx.y = 0f;
            }
            EventsLate();
            DrawDropShadows();
            DrawGroundItems();
            if (_healFx != null) _healFx.Update(Time.deltaTime, _battle != null ? _battle.Effects : null, _cam);
            EvoCameraLate();
            CullDoors();        // renderDoors: isBoxOffScreen
            FishDrawWorld();
        }

        DialogueBox _dlg; string _dlgKey;
        bool _lineCustom;                // the shown text goes into the 0x26 box (ScriptAction.Custom)
        Vector3Int _customBox;           // the last 0x26: columns, rows, origin id

        // the game's dialogue box (DialogueBox) follows the script's text: setDialogueOwner sets up a new box when the speaker changes, the box stays
        // (empty) between texts while the script runs, and closeAllTextboxes shuts it when the script ends. False = no game font: the old box is drawn
        bool SyncDialogue()
        {
            if (_dlg == null)
            {
                if (_disc == null || Battle.StatFont == null) return false;
                _dlg = DialogueBox.Load(_disc, Battle.StatFont);
                if (_dlg == null) return false;
                _dlg.Sound = Snd;
            }
            bool has = (_runner != null || _routineOn) && (_line != null || _choiceOpts != null);
            // startBattle: the fight takes the screen over; the box comes back only when the script talks again
            // the arena (STD_initializeTournament) has no dialogue box: the script's box went with the map
            if ((_runner == null && !_routineOn) || (_battle != null && _battle.Active) || _arenaOn || _onlineOn) { if (_dlg.Showing) _dlg.Shut(); _dlgKey = null; return true; }
            if (!has) { if (_dlgKey != null) { _dlg.Clear(); _dlgKey = null; } return true; }
            if (_lineCustom && _choiceOpts == null) { if (!_dlg.Showing || !_dlg.Custom) { _dlg.SetupCustom(_customBox.x, _customBox.y, DialogueOrigin(_customBox.z)); _dlgKey = null; } }
            else if (!_dlg.Showing || _dlg.Custom || _dlg.Owner != _speaker) { _dlg.Setup(_speaker, DialogueOrigin(_speaker)); _dlgKey = null; }
            string text = _line ?? "";
            int choices = 0;
            if (_choiceOpts != null) { text = (text.Length > 0 ? text + "\n" : "") + string.Join("\n", _choiceOpts.ToArray()); choices = _choiceOpts.Count; }
            string key = _speaker + "|" + text + "|" + choices;
            if (key != _dlgKey) { Color c; _dlg.Show(_speaker, SpeakerName(_speaker, out c), text, choices); _dlgKey = key; }
            _dlg.Current = _choiceSel;
            return true;
        }

        // setupBoxOrigin: a 10 x 10 square whose top left is the speaker's bone 1 on screen (getEntityScreenPos(entity, 1), utils.c: the bone's world
        // matrix through the camera, from the middle of the picture); none for 0xFF
        RectInt? DialogueOrigin(int speaker)
        {
            Transform t = null; DigimonAnimator a = null;
            if (speaker == 0xFD) { t = _player; a = _anim; }
            else if (speaker == 0xFC) { t = _partner; a = _partnerAnim; }
            else if (speaker != 0xFF) foreach (var n in _npcs) if (n.Data.Script == speaker && n.T != null) { t = n.T; a = n.Anim; break; }
            if (t == null) return null;
            Vector2 p = BoneScreen(t, a);
            return new RectInt((int)p.x, (int)p.y, 10, 10);
        }

        // setupNewGameDialogueBox / setupNameSelectorBox: the map Digimon of type 0x75 (Jijimon), bone 1, the square's top left there
        RectInt? JijimonSquare()
        {
            foreach (var n in _npcs) if (n.T != null && n.Data.Type == 0x75) { Vector2 p = BoneScreen(n.T, n.Anim); return new RectInt((int)p.x, (int)p.y, 10, 10); }
            return null;
        }

        // getEntityScreenPos(entity, 1): where bone 1 is on the PS1 screen
        Vector2 BoneScreen(Transform t, DigimonAnimator a)
        {
            var b = a != null ? a.Node(1) : null;
            return Ps1Point(b != null ? b.position : t.position);
        }

        // tickConfirmDialogue: Cross only once UI box 0 is open (UI_BOX_DATA[0].state == 1)
        bool DialogueReady { get { return _dlg == null || _dlg.Box.State == 1; } }

        bool _playerShadowOff;      // PLAYER_SHADOW_ENABLED = 0 (the ending)

        // renderDropShadow: no shadow when getTileTrigger gives -1, a blocked tile (0x80, e.g. water) or 0xFF (the boy over the water at Coela Point)
        bool ShadowOnTile(Vector3 at)
        {
            if (_map == null || _map.Collision == null) return true;
            int c = _map.CollisionAt(at.x / Unit, at.z / Unit);
            return c != 0x80 && c != 0xFF;
        }

        // renderDropShadow for every Digimon on the map (radius from DIGIMON_DATA, at its own height), renderDroppedItemShadow for every item on the ground
        // tickPlayerMenu clears the boy's and the partner's isOnScreen: they are not drawn while the Player menu is open
        bool _actorsHidden;
        void HideActors(bool hide)
        {
            if (hide == _actorsHidden) return;
            _actorsHidden = hide;
            foreach (var t in new[] { _player, _partner })
                if (t != null) foreach (var r in t.GetComponentsInChildren<Renderer>(true)) r.enabled = !hide;
        }

        void DrawDropShadows()
        {
            if (_actorsHidden) { DrawOtherShadows(); return; }
            if (_dropShadow == null || !_dropShadow.Ready || _map == null || _disc == null) return;
            if (_player != null && _player.gameObject.activeInHierarchy && !_playerShadowOff && ShadowOnTile(_player.position))
            { var si = DigimonStatsCatalog.Get(_disc, 0); _dropShadow.Draw(_player.position, si != null ? si.Radius : 50); }
            if (_partner != null && _partner.gameObject.activeInHierarchy && ShadowOnTile(_partner.position))
            { var si = _menus != null ? _menus.PartnerInfo : null; _dropShadow.Draw(_partner.position, si != null ? si.Radius : 120); }
            DrawOtherShadows();
        }

        void DrawOtherShadows()
        {
            if (_dropShadow == null || !_dropShadow.Ready || _map == null || _disc == null) return;
            foreach (var n in _npcs)
            {
                if (n.T == null || !n.T.gameObject.activeInHierarchy || !ShadowOnTile(n.T.position)) continue;
                var si = DigimonStatsCatalog.Get(_disc, n.Data.Type);
                if (si != null) _dropShadow.Draw(n.T.position, si.Radius);
            }
            foreach (var g in _ground) _dropShadow.Draw(new Vector3(g.Pos.x, 0f, g.Pos.z), 100f);
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
            if (_picker != null) { DrawDiscPicker(); return; }
            if (_movie != null) { DrawMovie(); return; }
            if (_title != null) { DrawTitle(); return; }
            if (Event.current.type == EventType.Repaint && _cam != null) UiKit.Bars();
            DrawEvents();
            DrawScreen();
            DrawCareOverlay();
            DrawStatusBars();
            DrawClock();
            DrawWorldItems();
            DrawTraining();
            DrawCurling();
            DrawFishing();
            DrawMapView();          // ours: the Map mod
            DrawTournament();
            DrawDebugMenu();        // last, so nothing covers it
        }

        void DrawScreen()
        {
            if (!string.IsNullOrEmpty(_error))
            {
                GUI.Label(new Rect(10, 10, 1400, 24), "Error: " + _error);
                return;
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // the test help line at the top (map, clock, keys, grid value): not in a player build
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
#else
            _hintBottom = 10f;
#endif
            if (_showStats) DrawStats();
            if (_setup != null && _setup.Active) { _setup.OnGUI(); return; }
            if (_battle != null) _battle.OnGUI(_cam);
            DrawArena();
            if (_battle != null && _battle.Active && _battle.FleeBlack > 0f)
            {
                // fadeToBlack(0x14) while the boy runs off
                var ob = GUI.color; GUI.color = new Color(0f, 0f, 0f, _battle.FleeBlack);
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
                GUI.color = ob;
            }
            if (_menus != null) _menus.OnGUI();       // the item page lies over the fight
            if (ShowNames && _player != null && _map != null && !_changing && (_menus == null || !_menus.Active) && !_arenaOn)
            {
                DrawName(_player, _playerName, new Color(1f, 0.95f, 0.5f));
                DrawName(_partner, _partnerName, new Color(0.7f, 1f, 0.7f));
                foreach (var n in _npcs) DrawName(n.T, n.Name, n.Data.FollowMode == 1 ? Color.white : new Color(1f, 0.6f, 0.6f));
            }
            if (SyncDialogue()) _dlg.Draw();
            else if (_runner != null && (_line != null || _choiceOpts != null))
            {
                UiKit.Begin();
                Color spc; string spk = SpeakerName(_speaker, out spc);
                string body = ScriptRunner.Plain(_line ?? "");
                if (_choiceOpts != null) body = body.Replace('\n', ' ');     // a question over its choices on one line ("Training: Boulder Moving")
                float bodyH = body.Length > 0 ? Mathf.Max(62f, UiKit.Height(body, 46, 1152 - 64)) : 0f;
                float need = (spk.Length > 0 ? 72f : 28f) + (_choiceOpts != null ? bodyH + _choiceOpts.Count * 54f : 0f) + 25f;
                var r = new Rect(135, 1005 - Mathf.Max(275f, need), 1152, Mathf.Max(275f, need));
                UiKit.Box(r, UiKit.DialogFill);
                float ty = r.y + 28;
                if (spk.Length > 0) { UiKit.Text(new Rect(r.x + 32, r.y + 12, 800, 60), spk, 48, spc); ty = r.y + 72; }
                UiKit.Text(new Rect(r.x + 32, ty, r.width - 64, r.height - (ty - r.y) - 10), body, 46, Color.white, TextAnchor.UpperLeft, true);
                if (_choiceOpts != null)
                {
                    float oy = ty + bodyH;
                    for (int i = 0; i < _choiceOpts.Count; i++)
                        UiKit.Text(new Rect(r.x + 32, oy + i * 54, r.width - 64, 54), (i == _choiceSel ? "> " : "   ") + ScriptRunner.Plain(_choiceOpts[i]), 46, i == _choiceSel ? UiKit.Yellow : Color.white);
                }
                else if ((int)(Time.time * 2f) % 2 == 0) UiKit.Text(new Rect(r.xMax - 90, r.yMax - 70, 60, 50), "X", 40, new Color(0.5f, 0.6f, 1f), TextAnchor.MiddleCenter);
                UiKit.End();
            }
            DrawRoutineList();
            if (!string.IsNullOrEmpty(_message) && Time.time < _messageUntil) GUI.Label(new Rect(10, _hintBottom, Screen.width - 20, 24), _message);
            DrawIris();
            if (_white > 0.001f)
            {
                var ow = GUI.color; GUI.color = new Color(1, 1, 1, Mathf.Clamp01(_white));
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
                GUI.color = ow;
            }
            if (_fade > 0.001f)
            {
                var old = GUI.color;
                GUI.color = new Color(0, 0, 0, Mathf.Clamp01(_fade));
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
                GUI.color = old;
            }
            DrawMapName();          // renderMapName, on the fading picture
            DrawEvoFront();
            DrawSysScreen();        // the sleep menu shows on the black screen
        }
    }
}
