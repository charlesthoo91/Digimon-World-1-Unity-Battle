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
    public sealed class PlayableRoom : MonoBehaviour
    {
        [Header("What to load")]
        public string DiscPath = "";
        [MapPath] public string MapPath = "/MAP/MAP1/MAYO01.MAP";   // the map you start in
        public int DigimonType = 3;            // 3 = Agumon
        public int Palette = 0;                // used only when Auto Clock is off: 0 day, 1 evening, 2 night

        [Header("Partner")]
        public bool HasPartner = true;         // a second Digimon that follows you
        public int PartnerType = 3;            // 3 = Agumon
        public float PartnerDistance = 3f;     // how far behind you it walks (metres)

        [Header("Foreground layers")]
        public bool ShowOverlays = true;       // tree tops, rock edges... that hide the Digimon when it walks behind them (F2 to switch)

        [Header("Time of day")]
        public bool AutoClock = true;          // the game clock runs and the map changes day / evening / night
        public float StartHour = 8f;           // the original game starts at 08:00
        public float GameMinutesPerSecond = 1.5f;   // the original: 1 game minute = 20 frames = 0.67 s

        [Header("Feel")]
        public float WalkSpeed = 3f;           // metres per second (a tile is 1 metre)
        public float TurnSpeed = 720f;         // degrees per second
        public float FacingOffset = 0f;        // set to 180 if the Digimon walks backwards
        public int IdleSlot = 0;
        public int WalkSlot = 2;
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
        float _clock = -1f;                    // hours, 0..24
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
        readonly List<Vector3> _trail = new List<Vector3>();
        Vector3 _lastTrail;
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

            if (NewGame) DigimonType = 0;       // the human boy
            DigimonEntry entry = null;
            foreach (var e in DigimonCatalog.Load(_disc)) if (e.Type == DigimonType) { entry = e; break; }
            if (entry == null) throw new InvalidDataException("Digimon type " + DigimonType + " not found.");
            byte[] mmd = _disc.ReadFile(entry.MmdPath);
            byte[] allTim = _disc.ReadFile("/CHDAT/ALLTIM.TIM");

            // --- the Digimon (built once, kept when the map changes)
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
            else foreach (var f in InitialFlags) _state.SetFlag(f, true);
            LoadMap(MapPath, NewGame ? 9 : -1);
            if (NewGame) BeginNewGame();
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
            public Vector3 Goal; public bool Walking;
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
            return _movers.Count > 0 || (_camOn && (_camPos - _camGoal).sqrMagnitude > 0.0004f);
        }

        // runs one cutscene command; true = the script must wait here
        bool ExecCmd(ScriptAction a)
        {
            Transform t, t2; DigimonAnimator an, an2;
            switch (a.Cmd)
            {
                case 0x67: _wait = a.Frames / 30f; return _wait > 0f;
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
                        var m = MoverFor(t, an); m.Goal = new Vector3(a.X * Unit, 0, a.Z * Unit); m.Walking = true;
                        if (an != null) an.SetSlot(WalkSlot);
                    }
                    break;
                case 0x6C:
                    if (ActorOf(a.Actor, out t, out an))
                    {
                        t.position = new Vector3(a.X * Unit, 0, a.Z * Unit);
                        _movers.Remove(t);
                    }
                    break;
                case 0x4F:
                {
                    if (!_camOn) { _camOn = true; _camPos = _player.position; }
                    _camGoal = new Vector3(a.X * Unit, 0, a.Z * Unit);
                    float secs = Mathf.Max(0.1f, a.Frames / 30f);
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
                    Vector3 to = m.Goal - m.T.position; to.y = 0;
                    float step = ActorSpeed * Time.deltaTime;
                    if (to.magnitude <= step) { m.T.position = m.Goal; m.Walking = false; if (m.A != null) m.A.SetSlot(IdleSlot); }
                    else
                    {
                        m.T.position += to.normalized * step;
                        m.T.rotation = Quaternion.RotateTowards(m.T.rotation, FaceRot(to), 720f * Time.deltaTime);
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
            _intro = true;
            if (!StartScript(54, null)) FinishIntro();
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
                if (a.Text != null) { _line = a.Text; _choiceOpts = null; _scriptAny = true; return; }
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
            Debug.Log("[npcs] " + _mapNames[_mapId] + ": " + _npcs.Count + " of " + _map.Npcs.Count + " shown: " + sb);
        }

        void UpdateNpcs()
        {
            foreach (var n in _npcs)
            {
                if (n.T == null || n.Path.Count == 0 || n == _talkingTo) continue;
                if (n.Wait > 0) { n.Wait -= Time.deltaTime; if (n.Anim != null && n.Anim.Slot != IdleSlot) n.Anim.SetSlot(IdleSlot); continue; }
                Vector3 pos = n.T.position, to = n.Path[n.Next] - pos; to.y = 0;
                if (to.magnitude < 0.1f) { n.Next = (n.Next + 1) % n.Path.Count; n.Wait = 1f + (n.Next % 3); continue; }
                Vector3 dir = to.normalized;
                n.T.position = pos + dir * 1.5f * Time.deltaTime;
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

        // ------------------------------------------------------------------ partner

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
        void UpdatePartner(Vector3 facing)
        {
            if (_partner == null) return;
            Vector3 p = _player.position;
            if ((p - _lastTrail).magnitude > 0.15f) { _trail.Add(p); _lastTrail = p; if (_trail.Count > 400) _trail.RemoveAt(0); }

            // length of the path from the partner to you
            float path = 0f;
            Vector3 prev = _partner.position;
            foreach (var t in _trail) { path += (t - prev).magnitude; prev = t; }
            path += (p - prev).magnitude;

            // it always catches up: if it is hopelessly far behind it jumps back to your trail
            if (path > PartnerDistance * 6f && _trail.Count > 0)
            {
                _partner.position = _trail[Mathf.Max(0, _trail.Count - 1 - (int)(PartnerDistance / 0.15f))];
                _trail.Clear();
                path = (p - _partner.position).magnitude;
            }

            bool moving = false;
            if (path > PartnerDistance)
            {
                float speed = WalkSpeed * Mathf.Clamp(path / PartnerDistance, 1f, 2.5f);
                float left = speed * Time.deltaTime;
                Vector3 pos = _partner.position, dir = Vector3.zero;
                while (left > 0f && _trail.Count > 0)
                {
                    Vector3 to = _trail[0] - pos;
                    float len = to.magnitude;
                    if (len <= left) { pos = _trail[0]; _trail.RemoveAt(0); left -= len; dir = len > 0.0001f ? to / len : dir; }
                    else { pos += to / len * left; dir = to / len; left = 0f; }
                }
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.0001f)
                {
                    moving = true;
                    var target = Quaternion.LookRotation(-dir.normalized) * Quaternion.Euler(0, FacingOffset, 0);
                    _partner.rotation = Quaternion.RotateTowards(_partner.rotation, target, TurnSpeed * Time.deltaTime);
                }
                _partner.position = pos;
            }
            if (_partnerAnim != null)
            {
                int want = moving ? WalkSlot : IdleSlot;
                if (_partnerAnim.Slot != want) _partnerAnim.SetSlot(want);
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
            _trail.Clear();
            _lastTrail = playerPos;
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
            _player.rotation = Quaternion.LookRotation(-face) * Quaternion.Euler(0, FacingOffset, 0);
            if (_anim != null) _anim.SetSlot(IdleSlot);
            PlacePartner(_player.position, face);
            BuildNpcs();
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

        void UpdateClock()
        {
            if (_map == null) return;
            if (AutoClock) _clock = (_clock + GameMinutesPerSecond / 60f * Time.deltaTime) % 24f;
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (k != null && k.tKey.wasPressedThisFrame) _clock = (Mathf.Floor(_clock) + 1f) % 24f;   // T = jump forward one hour
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.T)) _clock = (Mathf.Floor(_clock) + 1f) % 24f;
#endif
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
            UpdateClock();
            if (GridKeyPressed()) ToggleGrid();
            if (NamesKeyPressed()) ShowNames = !ShowNames;
            if (OverlayKeyPressed()) { ShowOverlays = !ShowOverlays; if (_overlayRoot != null) _overlayRoot.SetActive(ShowOverlays); }
            TickOverlayAnimations();
            if (_player == null || _map == null || _changing) return;
            UpdateNpcs();
            TickActors();
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
            if (ActionPressed()) TryTalk();
            if (_runner != null) return;
            Vector2 input = ReadMove();
            // arrow keys follow the screen: "up" walks up the screen, "right" to the right, whatever way the map's camera faces
            Vector3 dir = ScreenToGround(input);
            bool moving = dir.sqrMagnitude > 0.01f;
            if (moving)
            {
                dir.Normalize();
                Vector3 step = dir * WalkSpeed * Time.deltaTime;
                Vector3 pos = _player.position;
                if (Free(pos + new Vector3(step.x, 0, 0))) pos.x += step.x;
                if (Free(pos + new Vector3(0, 0, step.z))) pos.z += step.z;
                _player.position = pos;
                var target = Quaternion.LookRotation(-dir) * Quaternion.Euler(0, FacingOffset, 0);
                _player.rotation = Quaternion.RotateTowards(_player.rotation, target, TurnSpeed * Time.deltaTime);
            }
            if (_anim != null)
            {
                int want = moving ? WalkSlot : IdleSlot;
                if (_anim.Slot != want) _anim.SetSlot(want);
            }

            UpdatePartner(dir);

            // exits: grid values 110..119, doors and triggers: 50..109
            int cell = _map.CollisionAt(_player.position.x / Unit, _player.position.z / Unit);
            if (cell >= 110 && cell < 120)
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

        void OnGUI()
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
                GUI.Label(new Rect(10, 10, 1400, 24), "Map " + name + "   " + ((int)_clock).ToString("00") + ":" + ((int)((_clock % 1f) * 60f)).ToString("00") + " " + pn + " (T = +1 hour)   |   Arrow keys/WASD walk, Space = action, F1 = walking grid, F2 = foreground on/off, F3 = names   |   grid value here: " + _map.CollisionAt(_player.position.x / Unit, _player.position.z / Unit));
            }
            if (ShowNames && _player != null && _map != null && !_changing)
            {
                DrawName(_player, _playerName, new Color(1f, 0.95f, 0.5f));
                DrawName(_partner, _partnerName, new Color(0.7f, 1f, 0.7f));
                foreach (var n in _npcs) DrawName(n.T, n.Name, n.Data.FollowMode == 1 ? Color.white : new Color(1f, 0.6f, 0.6f));
            }
            if (_runner != null && (_line != null || _choiceOpts != null))
            {
                var st = new GUIStyle(GUI.skin.label) { fontSize = 20, wordWrap = true };
                st.normal.textColor = Color.white;
                float w = Mathf.Min(Screen.width - 40, 900);
                string txt = _line ?? "";
                if (_choiceOpts != null)
                {
                    txt += "\n";
                    for (int i = 0; i < _choiceOpts.Count; i++) txt += (i == _choiceSel ? "> " : "   ") + _choiceOpts[i] + "\n";
                    txt += "(Up/Down + Space)";
                }
                else txt += "   [Space]";
                float h = Mathf.Max(110, st.CalcHeight(new GUIContent(txt), w - 28) + 20);
                var box = new Rect((Screen.width - w) / 2, Screen.height - h - 20, w, h);
                GUI.Box(box, "");
                GUI.Label(new Rect(box.x + 14, box.y + 8, w - 28, h - 16), txt, st);
            }
            if (!string.IsNullOrEmpty(_message) && Time.time < _messageUntil) GUI.Label(new Rect(10, 34, 1400, 24), _message);
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
