using System.Collections;
using System.Collections.Generic;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The end of the story. EAB_REL (eab.c): built-in 0x28 EAB_startBuildup (rings round the hidden boss), script routine 0x49 8
    /// (tamerTickMachinedramonSpawn -> EAB_tick / EAB_tickSpawn: flashes, black, rings, particle squares, the camera turning round it,
    /// the model from wireframe to solid). ENDI_REL (endi.c): routine 0x49 6 and built-in 0x35 (tamerTickEnding -> ENDI_tickEnding: the boy
    /// fades into sparks). Built-ins 0x33 / 0x34 load their sounds, 0x36 gameClearSave (awardMachinedramonData, "Do you want to save?").
    /// </summary>
    public sealed partial class PlayableRoom
    {
        const uint EabBase = 0x80060000, EabFlashes = 0x8006170C, EabBeats = 0x8006179C;      // EAB_REL.BIN loads at 0x80060000

        // ------------------------------------------------------------------ rings (EAB_D_800617D0: 3 slots)

        sealed class EabRing { public int Timer = -1; public bool Spawn; public Transform T; }
        readonly EabRing[] _eabRings = { new EabRing(), new EabRing(), new EabRing() };
        Transform _eabWho;                    // the entity the rings stand on
        bool _buildupOn; int _buildupFrame;   // object 0x60B (EAB_tickBuildup)
        Material _eabFlashMat; Texture2D _eabFlashTex; int _eabFlash = -1;     // EAB_renderFlash this frame (colour, -1 = none)
        Color _spotTexel; bool _spotRead;

        // the ring model: \ETCHI\BOSS_EFE.TMD object 0, the map's page 640,0 (colour row 486), its colour set to 0x3C (EAB_setModelColor), added
        Transform MakeRing()
        {
            if (_mapVram == null) return null;
            try
            {
                var model = ReadTmd(_disc.ReadFile("/ETCHI/BOSS_EFE.TMD"));
                if (model.Parts.Count == 0) return null;
                var m = BuildStatic(model.Parts[0], (f, layer) => EvPage(f.Tpage, f.Clut, layer, 0x3C),
                                    (f, i) => new Vector2((f.U[i] + 0.5f) / 256f, 1f - (f.T[i] + 0.5f) / 256f), _objAssets);
                var t = MakeStaticObject(m, "Boss ring", ObjRoot());
                t.gameObject.SetActive(false);
                return t;
            }
            catch (System.Exception ex) { Debug.LogWarning("[objects] boss ring: " + ex.Message); return null; }
        }

        // EAB_addBuildupRing / EAB_addSpawnRing: the first free of 3
        void AddRing(bool spawn)
        {
            foreach (var r in _eabRings)
                if (r.Timer < 0)
                {
                    if (r.T == null) r.T = MakeRing();
                    r.Timer = 0; r.Spawn = spawn;
                    return;
                }
        }

        void RemoveRings() { foreach (var r in _eabRings) { r.Timer = -1; if (r.T != null) r.T.gameObject.SetActive(false); } }

        static int PsSin512(int a) { return Mathf.RoundToInt(Mathf.Sin((a & 0x1FF) * Mathf.PI * 2f / 512f) * 4096f); }    // _sin: 512 = a turn

        // EAB_tickBuildupRing (18 frames) / EAB_tickSpawnRing (9), then their render: on the entity's feet, turned 0x800, scale x/z and y
        void TickRings()
        {
            foreach (var r in _eabRings)
            {
                if (r.Timer < 0) continue;
                if (++r.Timer >= (r.Spawn ? 9 : 0x12)) { r.Timer = -1; if (r.T != null) r.T.gameObject.SetActive(false); continue; }
                if (r.T == null || _eabWho == null) continue;
                int sxz = r.Spawn ? PsLerpWrap(0x16CC, 0x10B8, 1, 0x12, r.Timer) : PsLerpWrap(0x10B8, 0x614, 1, 0x12, r.Timer);
                int angle = PsLerpWrap(0, 0x80, 1, 0x12, r.Timer);
                int sy = r.Spawn ? PsSin512(angle) * 0x2B8 / 4096 + 0xAE : PsSin512(angle) * 0xC3C / 4096 + 0x15C;
                Vector3 g = ToGame(_eabWho.position);
                PlaceStatic(r.T, new[] { (int)g.x, (int)g.y, (int)g.z }, new[] { 0, 0x800, 0 });
                r.T.localScale = new Vector3(sxz / 4096f, sy / 4096f, sxz / 4096f);
                r.T.gameObject.SetActive(true);
            }
        }

        // once a game frame (TickEvents)
        void TickEab()
        {
            if (_buildupOn)
            {
                _buildupFrame = (_buildupFrame + 1) % 18;                 // EAB_tickBuildup
                if (_buildupFrame % 6 == 0) AddRing(false);
            }
            TickRings();
        }

        // 0x28: EAB_startBuildup(the Digimon of script 5): it is hidden and the rings grow round where it stands
        void StartBuildup()
        {
            Transform t; DigimonAnimator an;
            if (!ActorOf(5, out t, out an)) return;
            _eabWho = t;
            foreach (var r in _eabRings) r.Timer = -1;                       // EAB_initializeRings
            _buildupOn = true; _buildupFrame = 0;
            t.gameObject.SetActive(false);                                // isOnMap = 0
            if (_audio != null) _audio.Play(8, 0);                        // playSound2(8, 0)
        }

        // EAB_renderFlash: a full-screen POLY_FT4 of SPOT256's corner texels (u 0, v 0x80 of page 832,256), colour c, subtracted (blend 2)
        void DrawEabFlash() { DrawSubtractFade(_eabFlash); }

        // the full-screen POLY_FT4 of SPOT256's corner texels (u 0, v 0x80 of page 832,256), colour c (0x80 = the texels as they are), subtracted
        // (blend 2): EAB_renderFlash, MURD_renderFullscreenFade
        void DrawSubtractFade(int c)
        {
            if (c <= 0 || Event.current.type != EventType.Repaint) return;
            if (!_spotRead)
            {
                _spotRead = true;
                TimImage tim;
                if (_disc.Exists("/ETCDAT/SPOT256.TIM") && TimImage.TryParse(_disc.ReadFile("/ETCDAT/SPOT256.TIM"), 0, out tim))
                {
                    var t = tim.ToTexture(0); var px = t.GetPixels32(); Destroy(t);
                    Color sum = Color.black;
                    for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++) sum += (Color)px[(tim.Height - 1 - y) * tim.Width + x];
                    _spotTexel = sum / 9f;
                }
            }
            if (_eabFlashTex == null) { _eabFlashTex = new Texture2D(1, 1, TextureFormat.RGBA32, false); }
            float k = c / 128f;
            _eabFlashTex.SetPixel(0, 0, new Color(Mathf.Min(1f, _spotTexel.r * k), Mathf.Min(1f, _spotTexel.g * k), Mathf.Min(1f, _spotTexel.b * k), 1f));
            _eabFlashTex.Apply();
            if (_eabFlashMat == null) { var sh = Shader.Find("DW1Remake/GuiBlend"); if (sh == null) return; _eabFlashMat = new Material(sh); MmdModel.SetPsxBlend(_eabFlashMat, 2); }
            _eabFlashMat.mainTexture = _eabFlashTex;
            Graphics.DrawTexture(UiKit.Picture, _eabFlashTex, new Rect(0, 0, 1, 1), 0, 0, 0, 0, _eabFlashMat);
        }

        // ------------------------------------------------------------------ particles (EAB_addParticle / EAB_renderParticle: 100 slots)

        sealed class EabParticle { public int Timer; public Vector3 Pos; public Color32 Col; public Mesh Mesh; }
        readonly List<EabParticle> _eabParticles = new List<EabParticle>();

        // a square on the ground round the point, growing from 8 to 0x9C4 and fading to black over 0x56 frames (four LINE_F2s)
        void DrawEabParticles()
        {
            foreach (var p in _eabParticles)
            {
                int size = PsLerpWrap(8, 0x9C4, 0, 0x56, p.Timer);
                Vector3 g = p.Pos;
                var v = new List<Vector3>
                {
                    ToUnity(new Vector3(g.x + size, g.y, g.z + size)), ToUnity(new Vector3(g.x + size, g.y, g.z - size)),
                    ToUnity(new Vector3(g.x - size, g.y, g.z - size)), ToUnity(new Vector3(g.x - size, g.y, g.z + size))
                };
                if (p.Mesh == null) p.Mesh = new Mesh();
                p.Mesh.Clear(); p.Mesh.SetVertices(v); p.Mesh.SetIndices(new[] { 0, 1, 1, 2, 2, 3, 3, 0 }, MeshTopology.Lines, 0); p.Mesh.RecalculateBounds();
                var c = new Color(PsLerpWrap(p.Col.r, 0, 0, 0x56, p.Timer) / 255f, PsLerpWrap(p.Col.g, 0, 0, 0x56, p.Timer) / 255f, PsLerpWrap(p.Col.b, 0, 0, 0x56, p.Timer) / 255f, 1f);
                DrawLines(p.Mesh, c);
            }
        }

        void TickEabParticles()
        {
            for (int i = _eabParticles.Count - 1; i >= 0; i--)
                if (_eabParticles[i].Timer >= 0x56) { if (_eabParticles[i].Mesh != null) Destroy(_eabParticles[i].Mesh); _eabParticles.RemoveAt(i); }
                else _eabParticles[i].Timer++;
        }

        void ClearEabParticles() { foreach (var p in _eabParticles) if (p.Mesh != null) Destroy(p.Mesh); _eabParticles.Clear(); }

        // ------------------------------------------------------------------ 0x49 routine 8: the Machinedramon spawn

        // EAB_tick + EAB_tickSpawn on ENTITY_TABLE[2] (the map's first Digimon record); 0x154 frames
        IEnumerator MachinedramonSpawn()
        {
            Npc boss = null;
            foreach (var n in _npcs) if (n.Record == 0 && n.T != null) { boss = n; break; }
            byte[] eab = _disc.Exists("/EAB_REL.BIN") ? _disc.ReadFile("/EAB_REL.BIN") : null;
            if (boss == null || eab == null) { Debug.LogWarning("[objects] Machinedramon spawn: no Digimon in slot 2 or no EAB_REL.BIN"); yield break; }
            _eabWho = boss.T;
            System.Func<uint, int, int> s32 = (a, i) => System.BitConverter.ToInt32(eab, (int)(a - EabBase) + 4 * i);
            var fx = _battle != null ? _battle.Effects : null;
            ClearEabParticles();
            // EAB_tick: nine flashes at the table's points (EAB_D_8006170C: x, y, z, size), mode 0x20, depth 0x22, 0x32 frames, colour 0x14 to 0xFF
            for (int i = 0; i < 9 && fx != null; i++)
                fx.AddFlash(0x20, ToUnity(new Vector3(s32(EabFlashes, i * 4), s32(EabFlashes, i * 4 + 1), s32(EabFlashes, i * 4 + 2))), 0x32, 0,
                            s32(EabFlashes, i * 4 + 3), new Vector3(0x14, 0x14, 0x14), new Vector3(0xFF, 0xFF, 0xFF), 0x22);
            if (_audio != null) _audio.Play(8, 1);
            int frame = 0, phase = 0;
            DView saved = CurrentView();
            float acc = 0f;
            int heading = GameHeading(boss.T);
            while (true)
            {
                yield return null;
                if (phase == 1 && _wireTotal < 16) DrawWireParts();
                DrawEabParticles();
                acc += Time.deltaTime * 20f;
                bool done = false;
                while (acc >= 1f && !done)
                {
                    acc -= 1f;
                    frame++;
                    _eabFlash = -1;
                    TickEabParticles();
                    if (phase == 0)
                    {
                        if (frame >= 0x2D) _eabFlash = 0xFF;
                        if (frame < 0x32) continue;
                        // phase 1: it appears (animation 0x21), everyone else and the map go, the build-up stops
                        phase = 1;
                        boss.T.gameObject.SetActive(true);
                        if (boss.Anim != null) boss.Anim.SetSlot(0x21);
                        HideWorld(true); boss.T.gameObject.SetActive(true);
                        if (_partner != null) _partner.gameObject.SetActive(false);
                        saved = CurrentView();
                        _buildupOn = false;
                        if (_audio != null) _audio.Play(8, 2);
                        CollectEvoParts(boss.Anim, boss.Data.Type); _wireTotal = 16;
                    }
                    if (phase == 1)
                    {
                        AddRing(true);
                        if (frame == 0xD2 && boss.Anim != null) boss.Anim.SetSlot(0x33);
                        if (frame >= 0x32 && frame < 0x53) _eabFlash = PsLerpWrap(0xFF, 0, 0x32, 0x52, frame);
                        int beat = (sbyte)eab[(int)(EabBeats - EabBase) + frame % 18];
                        if (beat != 0)
                        {
                            int g = PsLerpWrap(0x32, 0xE6, 1, 5, beat);
                            g = g * PsSin512(PsLerpWrap(0, 0x80, 0x32, 0x122, frame)) / 4096;
                            if (_eabParticles.Count < 100) _eabParticles.Add(new EabParticle { Timer = 0, Pos = ToGame(boss.T.position), Col = new Color32(0x0A, (byte)Mathf.Clamp(g, 0, 255), 0x0A, 255) });
                        }
                        if (frame >= 0x32 && frame < 0x123) _wireTotal = 0x10 - PsSin512(PsLerpWrap(0x80, 0, 0x32, 0x122, frame)) * 16 / 4096;
                        // EAB_calculateCameraOrbit: round bone 0 (100 up), distance and height closing in
                        int vy = (frame >= 0x32 && frame < 0x123 ? PsLerpWrap(0x71C, 0x171C, 0x32, 0x122, frame) : 0x171C) + heading;
                        int dist = PsLerpWrap(0xDAC, 0xBB8, 0x32, 0x140, frame), height = PsLerpWrap(0x258, 0x32, 0x32, 0x140, frame);
                        var root = boss.Anim != null ? boss.Anim.Node(0) : null;
                        Vector3 r = ToGame(root != null ? root.position : boss.T.position); r.y -= 0x64;
                        float a = (vy & 0xFFF) * Mathf.PI * 2f / 4096f;
                        Vector3 eye = new Vector3(r.x + Mathf.Sin(a) * dist, r.y - height, r.z + Mathf.Cos(a) * dist);
                        _evoCam = true; _evoEye = ToUnity(eye); _evoLook = ToUnity(r); _evoFov = saved.Fov; _evoShift = saved.Shift;
                        if (frame == 0x13F) { _evoEye = saved.Eye; _evoLook = saved.Look; }
                        if (frame < 0x140) continue;
                        // phase 2: back to the map
                        phase = 2;
                        ClearEabParticles();
                        _wireTotal = 16; DrawWireParts();
                        HideWorld(false); if (_partner != null) _partner.gameObject.SetActive(true);
                        _evoCam = false;
                        if (_audio != null) _audio.Play(8, 3);
                        continue;
                    }
                    if (phase == 2)
                    {
                        if (frame < 0x14A) AddRing(true);
                        if (frame >= 0x154) { RemoveRings(); done = true; }
                    }
                }
                if (done) break;
            }
            _eabFlash = -1; _eabWho = null;
        }

        // ------------------------------------------------------------------ the ending (ENDI_tickEnding on ENTITY_TABLE[0], the boy)

        sealed class EndiParticle { public int Bone; public Vector3 Off, World; }
        readonly List<EndiParticle> _endi = new List<EndiParticle>();
        bool _endiAttached = true; Vector3 _endiBase;

        // the boy's model made semi-transparent, added (ENDI_setModelSemiTrans: tpage blend 1), its colours faded to black over 60 frames
        // (ENDI_fadeClut); sparks on random bones (ENDI_spawnParticle: offset -70..69, -40..39, -40..39); 60-78 he shrinks to 0x999,
        // to 80 grows to 0x1800; at 80 sound (8, 1), the sparks leave the bones and rise (speed -450, +20 a frame) until it stops
        IEnumerator EndingScene(bool endGame)
        {
            if (_audio != null) _audio.Play(8, 0);
            var mats = new List<Material>();
            foreach (var r in _player.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.materials)
                {
                    m.SetFloat("_Surface", 1); m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One); m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
                    m.SetFloat("_ZWrite", 0); m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); m.renderQueue = 3000;
                    mats.Add(m);
                }
            _playerShadowOff = true;           // PLAYER_SHADOW_ENABLED = 0 (initializeTamer sets it back)
            _endi.Clear(); _endiAttached = true;
            var fx = _battle != null ? _battle.Effects : null;
            var entry = (DigimonEntry)null;
            int bones = 16;
            if (_entries == null) _entries = new List<DigimonEntry>(DigimonCatalog.Load(_disc));
            foreach (var e in _entries) if (e.Type == 0) { entry = e; break; }
            if (entry != null) bones = entry.BoneCount;
            Vector3 scale0 = _player.localScale;
            int frame = 0, phase = 0, velocity = 0;
            float acc = 0f;
            while (phase < 3)
            {
                yield return null;
                acc += Time.deltaTime * 20f;
                while (acc >= 1f && phase < 3)
                {
                    acc -= 1f;
                    frame++;
                    if (phase == 0)
                    {
                        float k = Mathf.Clamp01((60 - frame) / 60f);
                        foreach (var m in mats) m.SetColor("_BaseColor", new Color(k * MmdModel.Brightness, k * MmdModel.Brightness, k * MmdModel.Brightness, 1f));
                        SpawnEndi(frame % Mathf.Max(1, bones - 1) + 1);
                        if (frame >= 60) phase = 1;
                    }
                    else if (phase == 1)
                    {
                        SpawnEndi(frame % Mathf.Max(1, bones - 1) + 1);
                        int s = frame <= 78 ? PsLerpWrap(0x1000, 0x999, 60, 78, frame) : PsLerpWrap(0x999, 0x1800, 78, 80, frame);
                        _player.localScale = scale0 * (s / 4096f);
                        if (frame >= 80)
                        {
                            if (_audio != null) _audio.Play(8, 1);
                            phase = 2;
                            // ENDI_releaseParticles: each spark keeps where it is now, against bone 0
                            var root = _anim != null ? _anim.Node(0) : null;
                            _endiBase = root != null ? root.position : _player.position;
                            foreach (var p in _endi) { p.World = EndiWorld(p); p.Off = p.World - _endiBase; }
                            _endiAttached = false;
                            velocity = -450;
                        }
                    }
                    else if (phase == 2)
                    {
                        _endiBase += Vector3.down * (velocity * Unit);
                        velocity += 20;
                        if (velocity >= 0) phase = 3;
                    }
                }
                // ENDI_renderParticles: each frame a random grey 60-129 and size 15-29
                if (fx != null)
                    foreach (var p in _endi)
                    {
                        Vector3 w = _endiAttached ? EndiWorld(p) : _endiBase + p.Off;
                        fx.DrawFxParticle(w, Random.Range(0, 15) + 15, new Color32((byte)(Random.Range(0, 70) + 60), (byte)(Random.Range(0, 70) + 60), (byte)(Random.Range(0, 70) + 60), 255), _cam);
                    }
            }
            _endi.Clear();
            if (endGame)
            {
                // built-in 0x35: SOME_SCRIPT_SYNC_BIT -> MAIN_STATE 3: the game ends (cleanupGame, playMovie(3, 1)) and starts again at the title
                yield return PlayMovie(3);
                UnityEngine.SceneManagement.SceneManager.LoadScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
            }
        }

        void SpawnEndi(int bone)
        {
            _endi.Add(new EndiParticle { Bone = bone, Off = new Vector3(Random.Range(0, 140) - 70, Random.Range(0, 80) - 40, Random.Range(0, 80) - 40) });
        }

        // the spark's offset times the boy's scale, then moved by its bone's matrix, which holds that scale too (so it counts twice, as in the game)
        Vector3 EndiWorld(EndiParticle p)
        {
            var node = _anim != null ? _anim.Node(p.Bone) : null;
            if (node == null) node = _player;
            float s = _player.localScale.x;
            return node.TransformPoint(new Vector3(p.Off.x, -p.Off.y, p.Off.z) * MmdModel.Scale * s);
        }

        // ------------------------------------------------------------------ the new game's partner

        // initializePartner(Agumon or Gabumon) after Jijimon's questions: the one set up at start is swapped for the chosen one
        void NewGamePartner(int pid)
        {
            var old = DigimonStatsCatalog.Get(_disc, PartnerType);
            if (old != null && old.Learnable.Count > 0)
            {
                int f = old.Learnable[0];
                _state.Techs[f] = false;
                int i = _state.TechSet.IndexOf(f); if (i >= 0) _state.TechSet[i] = -1;
            }
            _state.Raised[PartnerType] = false;
            SwapPartner(pid);
            _care.InitPartner(pid);
            _state.Raised[pid] = true;                                   // setDigimonRaised
            _state.Vars[103] = (byte)pid;
            var info = DigimonStatsCatalog.Get(_disc, pid);
            if (info != null && info.Learnable.Count > 0)
            {
                int first = info.Learnable[0];
                _state.Techs[first] = true;
                if (!_state.TechSet.Contains(first)) { int hole = _state.TechSet.IndexOf(-1); if (hole >= 0) _state.TechSet[hole] = first; else _state.TechSet.Add(first); }
            }
            if (_audio != null) _audio.SetPartner(pid);
        }

        // ------------------------------------------------------------------ 0x36 game clear save

        // gameClearSave: the partner goes to battle registration slot 39 as Machinedramon (awardMachinedramonData: type 0x73, the
        // partner's numbers and name, techniques of animations 0x2E, 0x30, 0x32), the saved map is File City's top map for the story so far
        // (getFileCityTopMap), then the main menu asks "Do you want to save?" (menu 0x45)
        IEnumerator GameClearSave()
        {
            _saveMachineOn = true;
            var r = _state.Registry[39];
            r.Hp = _state.Stats[4]; r.Mp = _state.Stats[5]; r.Off = _state.Stats[0]; r.Def = _state.Stats[1]; r.Speed = _state.Stats[2]; r.Brain = _state.Stats[3];
            r.Discipline = _state.Discipline; r.Type = 0x73; r.Name = _state.PartnerName;
            var info = DigimonStatsCatalog.Get(_disc, 0x73);
            int[] anims = { 0x2E, 0x30, 0x32 };
            for (int i = 0; i < 3; i++) { int t = info != null ? info.RawMoves[anims[i] - 0x2E] : 0xFF; r.Moves[i] = t == 0xFF ? -1 : t; }
            int top = FileCityTopMap();
            var s = YesNoScreen(Exe(0x80131568), 0x46, 0x3C, 0x57, 0x63, 67, 87);
            s.Box(0x3C, 0x32, 0xBC, 0x20); s.Box(0x3C, 0x52, 0x38, 0x22);
            int answer = 0;
            while (answer != 1) yield return Ask(s, a => answer = a);
            if (s.Pos == 0) SaveClearFile(top);
            _saveMachineOn = false;
            AdvanceScript();
        }

        // getFileCityTopMap (map.c): which of the File City maps 0xA8-0xB3 the town has grown to
        int FileCityTopMap()
        {
            bool dc = _state.GetFlag(0xDC) && _state.GetFlag(0xD6), dd = _state.GetFlag(0xDD), e1 = _state.GetFlag(0xE1), f6 = _state.GetFlag(0xF6);
            int b = dc ? (dd ? 0xB1 : 0xAE) : (dd ? 0xAB : 0xA8);
            return e1 ? b + 2 : f6 ? b + 1 : b;
        }

        void SaveClearFile(int map)
        {
            if (_player == null) return;
            var d = SaveGameData.From(_state);
            d.Map = map >= 0 && map < _mapNames.Count ? _mapNames[map] : _mapNames[_mapId];
            d.X = _player.position.x; d.Y = _player.position.y; d.Z = _player.position.z;      // only CURRENT_SCREEN changes; the boy's place is saved as it is
            d.Clock = _clock; d.Day = _dayCount; d.PartnerType = PartnerType;
            d.PrevScreen = _prevScreen; d.PrevExit = _prevExit; d.CurExit = _curExit;
            _mainState = 2; d.MainState = 2;      // gameClearSave: MAIN_STATE = 2, saved with the game
            System.IO.File.WriteAllText(SavePath, JsonUtility.ToJson(d));
        }
    }
}
