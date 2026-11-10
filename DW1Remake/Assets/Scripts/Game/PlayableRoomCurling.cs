using System.Collections;
using System.Collections.Generic;
using DW1Remake.Formats;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DW1Remake.Game
{
    /// <summary>
    /// Digimon Curling with Penguinmon or MetalMamemon (kar/kar.c, KAR_REL.BIN loaded at 0x80053800 when map 136 FRZL17 loads:
    /// checkCurlingMap). FRZL17's script (131, section 0x33 on its arrival tile) runs Digimon routine 49 01 (callDigimonRoutine 1:
    /// KAR_start, tamer state 12 = KAR_tick every frame, partner state 11), waits (4A FF) for SOME_SCRIPT_SYNC_BIT, which
    /// KAR_finishMatch sets, and changes map; the arrival sections of script 206 read variable 0x79 (0 lost, 1 won, 2 won with 10 or more)
    /// and give the prizes. Variable 0x7A picks the opponent (0 Penguinmon = ENTITY_TABLE[2], 1 MetalMamemon = ENTITY_TABLE[3]).
    /// Every table and text is read from KAR_REL.BIN and the exe at run time; the stones are \ETCDAT\KARRING.TMD, their pictures and the
    /// sprites are in the map's video memory (page 640, 0, colour sets at y 486).
    /// Names: the decompilation's MAIN_D_ / KAR_D_ globals are given the names below, each with its address.
    /// </summary>
    public sealed partial class PlayableRoom
    {
        const uint KarBase = 0x80053800;

        struct KarVec { public int X, Y, Z; }

        // KarStone (0xAC bytes): state -0x65 out / unused, -1 in hand, 0 held, 1 on the ice, 2 pegged, 3 pegged and pushed; +100 = blinking off
        sealed class KarStone
        {
            public int State, Type, Weight, Speed, Angle, Ring, PrevRing, Model = -1;
            public KarVec Target, Pos;
            public int RotX, RotY, RotZ;
            public Transform T; public int Shown = -1; public bool InHand;
            public void CopyFrom(KarStone s)
            {
                State = s.State; Type = s.Type; Weight = s.Weight; Speed = s.Speed; Angle = s.Angle; Ring = s.Ring; PrevRing = s.PrevRing;
                Target = s.Target; Pos = s.Pos; RotX = s.RotX; RotY = s.RotY; RotZ = s.RotZ; Model = s.Model;
            }
        }

        // KarStoneRow: score, thrown, unk2 (the throw power the computer wants), unk4 (the stone it will throw), 5 stones
        sealed class KarRow { public int Score, Thrown, Unk2, Unk4; public readonly KarStone[] S = new KarStone[5]; }

        sealed class KarSpr { public int X, Y, ClutX, ClutY, U, V, W, H, R; }

        byte[] _karBin;
        readonly KarRow[] _karRows = { new KarRow(), new KarRow(), new KarRow() };   // KAR_D_8005B5A0[3]: the boy, the opponent, the obstacles
        readonly KarStone _karLast = new KarStone();                                  // KAR_D_80063914: a copy of the boy's last thrown stone
        readonly KarStone[] _karOrder = new KarStone[11];                             // KAR_D_800639C0: the stones in the order they were thrown
        int _karState;                 // MAIN_D_80135244
        int _karSel;                   // MAIN_D_8013523A (s16) the stone chosen in the row
        int _karTurn;                  // MAIN_D_8013523C (s8) whose turn: 0 the boy, 1 the opponent
        int _karPower;                 // MAIN_D_8013523E (s16)
        ushort _karTimer;              // MAIN_D_80135252 (u16)
        int _karHint;                  // MAIN_D_80135250 (s8) the explanation / hint page; 0 none
        int _karAim;                   // MAIN_D_8013524C
        int _karTurnSpeed;             // MAIN_D_80135254 (s16)
        int _karOpp;                   // MAIN_D_80135248 (u8) ENTITY_TABLE index of the opponent (variable 0x7A + 2)
        int _karTally;                 // MAIN_D_80135256 (s8)
        ushort _karArrowBlink;         // MAIN_D_80135238 (u16)
        int _karReady;                 // MAIN_D_80135220 (u8)
        int _karCamDone;               // MAIN_D_80135224
        int _karSlideVoice = -1;       // MAIN_D_80135228 (playSound2's voice)
        int _karHintLine;              // MAIN_D_8013522C (u8)
        int _karSkip, _karTallyStep;   // MAIN_D_8013522D, MAIN_D_8013522E (s8)
        int _karBlinkCount, _karBlinkTimer;   // MAIN_D_80135230, MAIN_D_80135234
        int _karRingsLit;              // MAIN_D_80135257 (u8)
        int _karLastTallied = -1;      // MAIN_D_80134A4F (s8)
        KarVec _karCamFrom, _karCamTo; // KAR_D_80063904, KAR_D_800638F4
        bool _karObject;               // addObject(0x1388, 0xF, KAR_tickStones, KAR_renderScene) is on
        bool _karOn, _karDone;
        int _karInk = 1;               // setTextColor
        int _karBoxTimer;              // TEXTBOX_OPEN_TIMER for the "next" mark
        int _karPad, _karPadPrev; bool _karCrossLatch;
        int _karCamPhase = -1;         // CAMERA_REACHED_TARGET
        uint _karRand = 1;
        readonly int[] _karVy = new int[16];         // posData->rotation.vy of ENTITY_TABLE[0..15]
        readonly bool[] _karVySet = new bool[16];
        CurlingView _karView;
        Transform _karRoot, _karHandRoot; Camera _karHandCam; RenderTexture _karHandRt;
        StaticModel[] _karModels;
        Npc _karOppNpc;
        bool _karBodyBoy, _karBodyOpp;

        static readonly Vector3 KarHandOrigin = new Vector3(0f, 9000f, 0f);     // the in-hand view, far from every map; only its own camera sees layer 31 here
        const int KarHandLayer = 31;

        // ------------------------------------------------------------------ disc data

        int KarS16(uint a) { return System.BitConverter.ToInt16(_karBin, (int)(a - KarBase)); }
        int KarS32(uint a) { return System.BitConverter.ToInt32(_karBin, (int)(a - KarBase)); }
        int KarU8(uint a) { return _karBin[(int)(a - KarBase)]; }
        int KarS8(uint a) { return (sbyte)_karBin[(int)(a - KarBase)]; }
        int ExeS16(uint a) { return System.BitConverter.ToInt16(ExeBytes, ExeAt(a)); }
        int ExeS8(uint a) { return (sbyte)ExeBytes[ExeAt(a)]; }

        // a string the overlay points to: its own data, or the exe's small data (0x80134A38 " ")
        string KarStr(uint ptr)
        {
            if (ptr == 0) return null;
            byte[] b; int p;
            if (ptr >= 0x80090000) { b = ExeBytes; p = ExeAt(ptr); } else { b = _karBin; p = (int)(ptr - KarBase); }
            var sb = new System.Text.StringBuilder();
            for (; p < b.Length && b[p] != 0; p++)
            {
                if (b[p] >= 0x80 && p + 1 < b.Length && b[p + 1] != 0) { sb.Append((char)(b[p] << 8 | b[p + 1])); p++; }
                else sb.Append((char)b[p]);
            }
            return sb.ToString();
        }

        string KarStrAt(uint table, int i) { return KarStr((uint)KarS32(table + (uint)(4 * i))); }

        KarSpr KarSprite(uint a)
        {
            return new KarSpr { X = KarS16(a), Y = KarS16(a + 2), ClutX = KarS16(a + 4), ClutY = KarS16(a + 6), U = KarS16(a + 8), V = KarS16(a + 10), W = KarS16(a + 12), H = KarS16(a + 14), R = KarU8(a + 16) };
        }

        RectInt ExeRect(uint a) { return new RectInt(ExeS16(a), ExeS16(a + 2), ExeS16(a + 4), ExeS16(a + 6)); }

        // ------------------------------------------------------------------ the PS1 maths the code uses (libgte / libc)

        // rsin / rcos: 4096 = one turn, 4096 = 1
        static int KSin(int a) { return (int)System.Math.Round(System.Math.Sin((a & 0xFFF) * System.Math.PI * 2.0 / 4096.0) * 4096.0); }
        static int KCos(int a) { return KSin(a + 0x400); }
        // ratan2(y, x): -2048..2048
        static int KAtan2(int y, int x) { if (x == 0 && y == 0) return 0; return (int)System.Math.Round(System.Math.Atan2(y, x) * 2048.0 / System.Math.PI); }
        // KAR_distance: SquareRoot0(x * x + y * y) as unsigned
        static int KarDistance(int x, int y)
        {
            uint s = (uint)(x * x) + (uint)(y * y);
            return (int)System.Math.Floor(System.Math.Sqrt(s));
        }
        // rand (libc): next = next * 1103515245 + 12345, (next >> 16) & 0x7FFF
        int KRand() { _karRand = _karRand * 1103515245u + 12345u; return (int)((_karRand >> 16) & 0x7FFF); }

        // ------------------------------------------------------------------ the entities (ENTITY_TABLE[0] boy, [1] partner, [2 + record] map Digimon)

        Transform KarT(int e) { return e == 0 ? _player : e == 1 ? _partner : (_karOppNpc != null && e == _karOpp ? _karOppNpc.T : NpcByRecord(e - 2)); }
        DigimonAnimator KarA(int e)
        {
            if (e == 0) return _anim;
            if (e == 1) return _partnerAnim;
            foreach (var n in _npcs) if (n.Record == e - 2) return n.Anim;
            return null;
        }
        Transform NpcByRecord(int r) { foreach (var n in _npcs) if (n.Record == r) return n.T; return null; }

        // startAnimation
        void KarStartAnim(int e, int slot)
        {
            var a = KarA(e);
            if (a != null && a.HasSlot(slot)) a.SetSlot(slot, slot != 0 && slot != 2);
        }

        KarVec KarLoc(int e)
        {
            var t = KarT(e); if (t == null) return new KarVec();
            Vector3 g = ToGame(t.position);
            return new KarVec { X = Mathf.RoundToInt(g.x), Y = Mathf.RoundToInt(g.y), Z = Mathf.RoundToInt(g.z) };
        }
        void KarSetLoc(int e, int x, int z)
        {
            var t = KarT(e); if (t == null) return;
            t.position = new Vector3(x * Unit, t.position.y, z * Unit);
        }
        void KarSetVy(int e, int vy) { if (e < 0 || e >= _karVy.Length) return; _karVy[e] = vy; _karVySet[e] = true; }
        int KarVyOf(int e) { return e >= 0 && e < _karVy.Length && _karVySet[e] ? _karVy[e] : 0; }

        // posData[bone].posMatrix.workm.t: the bone's world position
        KarVec KarBone(int e, int bone)
        {
            var a = KarA(e); var n = a != null ? a.Node(bone) : null;
            if (n == null) return KarLoc(e);
            Vector3 g = ToGame(n.position);
            return new KarVec { X = Mathf.RoundToInt(g.x), Y = Mathf.RoundToInt(g.y), Z = Mathf.RoundToInt(g.z) };
        }

        // ------------------------------------------------------------------ the pad (POLLED_INPUT / POLLED_INPUT_PREVIOUS, once a game frame)

        static bool KarCrossHeld()
        {
            return Pad.Held(PadButton.Cross);
        }
        bool KarHeld(int bit) { return (_karPad & bit) != 0; }
        bool KarPressed(int bit) { return (_karPad & bit) != 0 && (_karPadPrev & bit) == 0; }
        const int PadUp = 0x1000, PadRight = 0x2000, PadDown = 0x4000, PadLeft = 0x8000, PadCross = 0x40;   // CONFIRM_BUTTON: Cross

        // ------------------------------------------------------------------ start and the frame loop

        // callDigimonRoutine 1: KAR_start, tamerSetState(12) (KAR_tick each frame), partnerSetState(11)
        IEnumerator Curling()
        {
            if (!KarStart()) { Debug.LogWarning("[curling] could not start (KAR_REL.BIN, KARRING.TMD or the map's pictures missing)"); yield break; }
            float acc = 0f;
            while (!_karDone)
            {
                if (GameMenus.CrossPressed()) _karCrossLatch = true;
                acc += Time.deltaTime;
                while (acc >= 0.05f && !_karDone)
                {
                    acc -= 0.05f;
                    _karPadPrev = _karPad;
                    Vector2 m = ReadMove();
                    _karPad = (m.y > 0f ? PadUp : 0) | (m.x > 0f ? PadRight : 0) | (m.y < 0f ? PadDown : 0) | (m.x < 0f ? PadLeft : 0)
                            | (KarCrossHeld() || _karCrossLatch ? PadCross : 0);
                    _karCrossLatch = false;
                    KarFrame();
                }
                yield return null;
            }
            KarEnd();
        }

        bool KarStart()
        {
            try
            {
                if (_karBin == null) _karBin = _disc.ReadFile("/KAR_REL.BIN");
                if (_karBin == null || _mapVram == null) return false;
                // KAR_start: \ETCDAT\KARRING.TMD (KAR_D_8005ABA4), GsMapModelingData
                var tmd = ReadTmd(_disc.ReadFile(KarStr(0x8005ABA4).Replace('\\', '/')));
                _karModels = new StaticModel[tmd.Parts.Count];
                for (int i = 0; i < tmd.Parts.Count; i++)
                    _karModels[i] = BuildStatic(tmd.Parts[i], PageTexture, (f, k) => new Vector2((f.U[k] + 0.5f) / 256f, 1f - (f.T[k] + 0.5f) / 256f), _objAssets);
            }
            catch (System.Exception e) { Debug.LogWarning("[curling] " + e.Message); return false; }
            _karRand = (uint)System.Environment.TickCount;
            if (_karView == null) { _karView = CurlingView.Load(_disc, Battle.StatFont); if (_karView != null) _karView.Sound = Snd; }
            if (_karView == null) return false;
            _karView.Forget();
            _karView.Page4 = clut => EvPage(10, clut, 0, 128);       // getTPage(0, 0, 640, 0)
            _karView.ClearText();
            _karRoot = new GameObject("Curling stones").transform; _karRoot.SetParent(ObjRoot(), false);
            _karHandRoot = new GameObject("Curling stones in hand").transform; _karHandRoot.position = KarHandOrigin;
            KarHandCamera();
            for (int p = 0; p < 3; p++)
                for (int i = 0; i < 5; i++)
                {
                    var s = new KarStone();
                    s.T = new GameObject("Stone " + p + "." + i).transform; s.T.SetParent(_karRoot, false);
                    _karRows[p].S[i] = s;
                }
            FreezeCamera();
            DigiviceModel.ExcludeFrom(_cam);
            _karOn = true; _karDone = false; _karCamPhase = -1; _karInk = 1; _karLastTallied = -1;
            for (int e = 0; e < _karVySet.Length; e++) _karVySet[e] = false;

            // KAR_start
            _karState = 0;
            KarSetupMatch(0);
            _karObject = true;
            _karOpp = _state.Vars[0x7A] + 2;
            _karOppNpc = null;
            foreach (var n in _npcs) if (n.Record == _karOpp - 2) _karOppNpc = n;
            if (_karOppNpc == null) Debug.LogWarning("[curling] no map Digimon for ENTITY_TABLE[" + _karOpp + "]");
            else _aiOff.Add(_karOppNpc.Record);       // ours: the map AI must not walk it while KAR_tick places it
            // applyRootMomentum: an animation's travel moves the body (the throw), so the models must not also show it
            if (_anim != null) { _karBodyBoy = _anim.BodyMovesItself; _anim.BodyMovesItself = true; _anim.TakeBodyMove(); }
            if (_karOppNpc != null && _karOppNpc.Anim != null) { _karBodyOpp = _karOppNpc.Anim.BodyMovesItself; _karOppNpc.Anim.BodyMovesItself = true; _karOppNpc.Anim.TakeBodyMove(); }
            Debug.Log("[curling] match against ENTITY_TABLE[" + _karOpp + "] " + (_karOppNpc != null ? _karOppNpc.Name : "?"));
            return true;
        }

        void KarHandCamera()
        {
            if (_karHandCam == null)
            {
                _karHandCam = new GameObject("Curling hand camera").AddComponent<Camera>();
                _karHandCam.cullingMask = 1 << KarHandLayer;
                _karHandCam.clearFlags = CameraClearFlags.SolidColor;
                _karHandCam.backgroundColor = new Color(0, 0, 0, 0);
                _karHandCam.fieldOfView = 2f * Mathf.Atan(120f / 512f) * Mathf.Rad2Deg;    // GsSetProjection(0x200) on a 240-line picture
                _karHandCam.aspect = 4f / 3f;
                _karHandCam.nearClipPlane = 0.1f; _karHandCam.farClipPlane = 200f;
                _karHandRt = new RenderTexture(640, 480, 24, RenderTextureFormat.ARGB32);
                _karHandCam.targetTexture = _karHandRt;
                var ad = UnityEngine.Rendering.Universal.CameraExtensions.GetUniversalAdditionalCameraData(_karHandCam);
                if (ad != null) ad.renderPostProcessing = false;
            }
            _karHandCam.gameObject.SetActive(true);
            // KAR_D_8005B580: GsRVIEW2 viewpoint (0, 0, -3320) looking at (0, 0, 0)
            KarVec vp = new KarVec { X = KarS32(0x8005B580), Y = KarS32(0x8005B584), Z = KarS32(0x8005B588) };
            KarVec vr = new KarVec { X = KarS32(0x8005B58C), Y = KarS32(0x8005B590), Z = KarS32(0x8005B594) };
            Vector3 eye = KarHandOrigin + ToUnity(new Vector3(vp.X, vp.Y, vp.Z)), at = KarHandOrigin + ToUnity(new Vector3(vr.X, vr.Y, vr.Z));
            _karHandCam.transform.position = eye;
            _karHandCam.transform.rotation = Quaternion.LookRotation((at - eye).normalized, Vector3.up);
        }

        void KarEnd()
        {
            _karOn = false;
            if (_karSlideVoice >= 0 && _audio != null) _audio.StopVoice(_karSlideVoice);
            _karSlideVoice = -1;
            KarRingFlagsAllOff();
            if (_karRoot != null) Destroy(_karRoot.gameObject);
            if (_karHandRoot != null) Destroy(_karHandRoot.gameObject);
            _karRoot = _karHandRoot = null;
            if (_karHandCam != null) _karHandCam.gameObject.SetActive(false);
            if (_karView != null) { _karView.BeginFrame(); _karView.CloseNow(_karView.Box); _karView.HandOn = false; }
            if (_anim != null) { _anim.TakeBodyMove(); _anim.BodyMovesItself = _karBodyBoy; }
            if (_karOppNpc != null && _karOppNpc.Anim != null) { _karOppNpc.Anim.TakeBodyMove(); _karOppNpc.Anim.BodyMovesItself = _karBodyOpp; }
            // the camera stays where the match left it (tamer state 12 never follows the boy) until the script changes map
            if (_camOn && _map != null && _player != null) _camOffsetPx = MapPixel(ClampCam(_camPos) + Vector3.up) - MapPixel(_player.position + Vector3.up);
        }

        // one game frame: the tamer's tick (KAR_tick), the object's tick (KAR_tickStones), UI box 0, the object's render (KAR_renderScene)
        void KarFrame()
        {
            _karView.BeginFrame();
            _karBoxTimer++;
            KarTick();
            if (_karObject) KarTickStones();
            _karView.StepBox();
            if (_karObject) KarRenderScene();
            KarApplyActors();
            if (_karState == 0x14) _karDone = true;
        }

        // KAR_tick
        void KarTick()
        {
            KarTickMatchState();
            KarRenderAimArrow();
            KarRenderStoneCursor();
            KarRenderScores();
            KarRenderPowerMeter();
            KarRenderReadyPrompt();
            KarRenderNamePlates();
            KarHandleAimScroll();
            KarUpdateRingMarkers();
            KarCheckStonesStopped();
        }

        // the facings the code set, and applyRootMomentum: each frame of an animation its root's travel moves the body along its facing
        void KarApplyActors()
        {
            for (int e = 0; e < _karVySet.Length; e++)
            {
                if (!_karVySet[e]) continue;
                var t = KarT(e); if (t == null) continue;
                t.rotation = Quaternion.Euler(0, _karVy[e] * 360f / 4096f, 0) * Quaternion.Euler(0, FacingOffset, 0);
            }
            KarMomentum(0); KarMomentum(_karOpp);
        }

        void KarMomentum(int e)
        {
            var a = KarA(e); var t = KarT(e);
            if (a == null || t == null) return;
            float travel = a.TakeBodyMove();
            if (travel == 0f) return;
            Vector3 front = t.rotation * Quaternion.Inverse(Quaternion.Euler(0, FacingOffset, 0)) * Vector3.back;
            front.y = 0f;
            t.position += front.normalized * travel;
        }

        // ------------------------------------------------------------------ the camera (map.c)

        // tickCameraMoveTo -> checkCameraMovement: the first call starts tickCameraMovement (0), it returns 1 once the frame after it arrived
        int KarCamMoveTo(int x, int z, int speed)
        {
            if (_karCamPhase == -1)
            {
                _karCamPhase = 0;
                StartCamMove(new Vector3(x * Unit, _player.position.y, z * Unit), speed);
                return 0;
            }
            if (_karCamPhase == 1) { _karCamPhase = -1; return 1; }
            if (!_camMove) _karCamPhase = 1;
            return 0;
        }

        // moveCameraByOffset: MAP_TILE_DATA.cameraX / Y += the pixels, held inside the map (cameraIsAtEdge)
        void KarCamShift(Vector2 px)
        {
            if (_map == null) return;
            FreezeCamera();
            Vector3 f = _camPos + Vector3.up;
            Vector2 c = ClampPx(MapPixel(f) + px);
            _camPos = _camGoal = WorldAtPixel(c, f.y) - Vector3.up;
        }

        // moveCameraByDiff: the camera moves as far on the picture as the point moved from 'from' to 'to'
        void KarCamByDiff(KarVec from, KarVec to)
        {
            if (_map == null) return;
            Vector2 a = MapPixel(ToUnity(new Vector3(from.X, from.Y, from.Z))), b = MapPixel(ToUnity(new Vector3(to.X, to.Y, to.Z)));
            KarCamShift(new Vector2(Mathf.Round(b.x) - Mathf.Round(a.x), Mathf.Round(b.y) - Mathf.Round(a.y)));
        }

        void KarSound(int n) { if (_audio != null) _audio.Play(8, n); }      // playSound2(8, n): the map's sound bank

        // ------------------------------------------------------------------ KAR_setupMatch

        // mode 0 a match (stone types at random), 1 the explanation (types 0, 1, 2, 3, 3)
        void KarSetupMatch(int mode)
        {
            int obstacles = KRand() % 3;
            _karSel = 0; _karTurn = 0; _karPower = 0;
            for (int p = 0; p < 3; p++)
            {
                var row = _karRows[p];
                KRand();                                    // seed = rand(), not used
                row.Score = 0; row.Thrown = 0; row.Unk2 = 0; row.Unk4 = 0;
                for (int i = 0; i < 5; i++)
                {
                    var s = row.S[i];
                    int n = KRand();
                    s.Speed = 0; s.Angle = 0; s.Ring = 0; s.PrevRing = 0;
                    n = n % 10;
                    if (mode == 0) s.Type = KarS8(0x8005AB8C + (uint)n);     // KAR_D_8005AB8C: 3 2 2 1 1 1 0 0 0 0
                    else { s.Type = i; if (s.Type >= 4) s.Type = 3; }
                    KarLink(s, s.Type * 3 + KarS32(0x8005AB80 + (uint)(4 * p)));    // KAR_D_8005AB80: model ids 2, 1, 0
                    if (p < 2)
                    {
                        // (the first pair of values is overwritten at once, as the original does)
                        s.Pos.Y = i * 0xAA - 0x50;
                        s.Pos.X = p != 0 ? 0x2BC : -0x2BC;
                        s.Pos.Z = 0;
                        s.RotX = 0x96; s.RotY = 0; s.RotZ = 0;
                        s.State = -1;
                    }
                    else if (i < obstacles)
                    {
                        s.Pos.Y = 0;
                        s.Pos.X = KarS32(0x8005AB98 + (uint)(4 * i)) - KRand() % 0x13F;     // KAR_D_8005AB98: 705, 235, -235
                        s.Pos.Z = -(KRand() % 0x3E8 + 0x3E8);
                        s.RotX = s.RotY = s.RotZ = 0;
                        s.State = 1; s.Speed = 0;
                    }
                    else
                    {
                        s.Pos = new KarVec();
                        s.RotX = s.RotY = s.RotZ = 0;
                        s.State = -0x65; s.Speed = 0;
                    }
                    s.Weight = ExeS16(0x80134A08 + (uint)(2 * s.Type));       // MAIN_D_80134A08: 30, 25, 35, 30
                }
            }
        }

        // GsLinkObject4: the stone shows model 'id' of KARRING.TMD
        void KarLink(KarStone s, int id)
        {
            s.Model = id;
            if (s.Shown == id || s.T == null || _karModels == null || id < 0 || id >= _karModels.Length) return;
            for (int i = s.T.childCount - 1; i >= 0; i--) Destroy(s.T.GetChild(i).gameObject);
            var m = MakeStaticObject(_karModels[id], "KARRING " + id, s.T);
            if (s.InHand) foreach (var t in m.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = KarHandLayer;
            s.Shown = id;
        }

        // ------------------------------------------------------------------ KAR_tickStones (the object's tick)

        void KarTickStones()
        {
            for (int p = 0; p < 3; p++)
                for (int i = 0; i < 5; i++)
                {
                    var s = _karRows[p].S[i];
                    if (s.State != 3 && s.State != 2) s.Target = s.Pos;
                    if (_karState == 5)
                    {
                        if (i == _karSel && p == _karTurn) s.RotY = (short)(s.RotY + 0x14);
                        else s.RotY = 0;
                    }
                    if (s.State > 0 && s.Speed >= 0)
                    {
                        int move = (short)(s.Speed / s.Weight);
                        uint speed = (uint)(s.Speed * 98);
                        s.Speed = (short)(speed / 100);
                        if (move < 2) s.Speed = 0;
                        if (s.State < 3)
                        {
                            s.Pos.X += move * KCos(s.Angle) / 4096;
                            s.Pos.Z += move * KSin(s.Angle) / 4096;
                        }
                        if (s.State == 3)
                        {
                            if (s.Speed > 0)
                            {
                                int step = (short)(move / 10);
                                if ((s.Angle -= 0x800) < 0) s.Angle += 0x1000;
                                s.Pos.X = s.Target.X + step * KCos(s.Angle) / 4096;
                                s.Pos.Z = s.Target.Z + step * KSin(s.Angle) / 4096;
                                if (step == 0) s.Speed = 0;
                            }
                            else { s.Speed = 0; s.State = 2; }
                        }
                        if (s.State == 1 && move <= 0 && s.Type == 3)
                        {
                            // MAIN_D_80134A4C: pegged models 11, 10, 9
                            s.State = 2;
                            KarLink(s, ExeS8(0x80134A4C + (uint)p));
                            KarSound(4);
                        }
                    }
                }
            KarUpdateCollisions();
        }

        // ------------------------------------------------------------------ KAR_renderScene (the object's render)

        void KarRenderScene()
        {
            bool any = false;
            for (int p = 0; p < 3; p++)
                for (int i = 0; i < 5; i++)
                {
                    var s = _karRows[p].S[i];
                    bool show = true;
                    if (p == 1 && _karState != 3 && _karState < 0x10 && (_karHint != 0 || _karState < 5)) show = false;
                    else if (_karState < 4 && _karTimer < i * 0x3C + 0x3C) show = false;
                    if (show && s.State == 0)
                    {
                        int idx = _karTurn != 0 ? _karOpp : 0;
                        if (idx == 2)
                        {
                            s.Pos = KarLoc(idx);
                            s.Pos.X -= 0x50;
                        }
                        else
                        {
                            var b = KarBone(idx, idx == 0 ? 9 : 4);
                            s.Pos.X = b.X; s.Pos.Y = b.Y + 0x78; s.Pos.Z = b.Z;
                            s.RotX = s.RotY = s.RotZ = 0;
                        }
                    }
                    // state < -0x64 and >= 0x65 are not drawn; below 0 the stone is drawn with the in-hand view
                    if (show && (s.State < -0x64 || s.State >= 0x65)) show = false;
                    KarPlace(s, show);
                    if (show && s.State < 0) any = true;
                }
            _karView.HandOn = any;
        }

        void KarPlace(KarStone s, bool show)
        {
            if (s.T == null) return;
            bool hand = s.State < 0;
            if (hand != s.InHand || s.T.parent == null)
            {
                s.InHand = hand;
                s.T.SetParent(hand ? _karHandRoot : _karRoot, false);
                foreach (var t in s.T.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = hand ? KarHandLayer : 0;
            }
            if (s.T.gameObject.activeSelf != show) s.T.gameObject.SetActive(show);
            if (show) PlaceStatic(s.T, new[] { s.Pos.X, s.Pos.Y, s.Pos.Z }, new[] { (int)(short)s.RotX, (int)(short)s.RotY, (int)(short)s.RotZ });
        }

        // ------------------------------------------------------------------ KAR_finishMatch

        void KarFinishMatch()
        {
            if (_karState == 0x14) return;
            _karState = 0x14;
            KarSetVy(_karOpp, 0x800); KarSetVy(1, 0x800); KarSetVy(0, 0x800);
            KarStartAnim(0, 2); KarStartAnim(1, 2); KarStartAnim(_karOpp, 2);
            _syncBit = true;                                            // SOME_SCRIPT_SYNC_BIT = 1
            _karObject = false;                                         // removeObject(0x1388, 0xF)
            for (int p = 0; p < 3; p++) for (int i = 0; i < 5; i++) KarPlace(_karRows[p].S[i], false);
            _karView.HandOn = false;
            _karInk = 1;                                                // setTextColor(1)
            // variable 0x79: 2 won with 10 points or more, 1 won, 0 lost (a tie is lost)
            if (_karRows[0].Score > _karRows[1].Score) _state.Vars[0x79] = (byte)(_karRows[0].Score >= 10 ? 2 : 1);
            else _state.Vars[0x79] = 0;
            Debug.Log("[curling] finished " + _karRows[0].Score + " : " + _karRows[1].Score + ", variable 0x79 = " + _state.Vars[0x79]);
        }

        // ------------------------------------------------------------------ the sprites

        // KAR_renderSprite
        void KarDrawSprite(KarSpr s) { _karView.Sprite(s.X, s.Y, s.ClutX, s.ClutY, s.U, s.V, s.W, s.H, s.R); }

        // KAR_renderAimArrow: 15 dots along the facing of the one who throws, from the stone, mirrored at the walls (x +-0x2EE); the last is the head
        void KarRenderAimArrow()
        {
            int player = _karTurn != 0 ? _karOpp : 0;
            if (_karState < 6 || _karState >= 8) return;
            if (_karState >= 2)
            {
                if (_karTimer-- != 0) _karTimer = (ushort)(_karTimer + 0xBF);
                if (_karArrowBlink++ >= 0x28) _karArrowBlink = 0;
            }
            else _karArrowBlink = 0;
            var from = _karRows[_karTurn].S[_karSel].Pos;
            int vy = KarVyOf(player);
            int prevX = 0, prevZ = 0;
            for (int i = 0; i < 15; i++)
            {
                int vz = (i + 1) * -200;
                // RotMatrix(rotation) then ApplyMatrixSV: only the turn round y
                int ox = (short)((KSin(vy) * vz) >> 12), oz = (short)((KCos(vy) * vz) >> 12);
                ox = (short)(ox + from.X); oz = (short)(oz + from.Z);
                int shade = (byte)(_karTimer + (i * 0xBF) / 15);
                if (shade >= 0xBF) shade -= 0xBF;
                while (ox < -0x2EE || ox >= 0x2EF)
                {
                    if (ox < -0x2EE) { int w = (short)(ox + 0x2EE); ox = (short)(ox - w * 2); }
                    if (ox >= 0x2EF) { int w = (short)(ox - 0x2EE); ox = (short)(ox - w * 2); }
                }
                Vector2 sxy = Ps1Point(ToUnity(new Vector3(ox, 0, oz)));
                int sx = Mathf.RoundToInt(sxy.x), sy = Mathf.RoundToInt(sxy.y);
                int clut = (0x1E6 << 6) | (0x80 >> 4);
                if (i != _karArrowBlink / 3)
                {
                    if (i < 0xE) _karView.ArrowDot(sx, sy, 0x18, 0x28, 16, 16, clut, shade + 0x40);
                    else
                    {
                        int rot = KAtan2(prevZ - oz, prevX - ox);
                        rot = rot - 0xC00;
                        rot = rot % 0x1000;
                        rot = -rot;
                        _karView.ArrowHead(sx, sy, 0x28, 0x28, rot == 0x800 ? 16 : 15, clut, shade + 0x40, rot);
                    }
                }
                prevX = ox; prevZ = oz;
            }
        }

        // KAR_renderStoneCursor: KAR_D_8005B478 beside the chosen stone in hand
        void KarRenderStoneCursor()
        {
            var s = KarSprite(0x8005B478);
            if (_karState == 5)
            {
                s.X = _karTurn != 0 ? 0x64 : -0x78;
                s.Y = _karSel * 30 - 0x2D;
                KarDrawSprite(s);
            }
        }

        // KAR_renderScores: two digits each (KAR_D_8005B48C with the glyphs of KAR_D_8005B4A0; glyph 10 = minus)
        void KarRenderScores()
        {
            var s = KarSprite(0x8005B48C);
            for (int i = 0; i < 2; i++)
            {
                int score = _karRows[i].Score;
                int lo = (short)(score % 10), hi = (short)(score / 10);
                int x = i != 0 ? 0x68 : -0x69;
                if (score < 0) hi = 10;
                if (lo < 0) lo *= -1;
                s.U = KarS16(0x8005B4A0 + (uint)(4 * lo)); s.V = KarS16(0x8005B4A2 + (uint)(4 * lo));
                s.X = x; s.Y = -0x3E;
                KarDrawSprite(s);
                s.U = KarS16(0x8005B4A0 + (uint)(4 * hi)); s.V = KarS16(0x8005B4A2 + (uint)(4 * hi));
                s.X = x - 0x10; s.Y = -0x3E;
                KarDrawSprite(s);
            }
        }

        // KAR_renderPowerMeter: KAR_D_8005B4CC, one bar piece per 250 of power, the 10th in another colour set
        void KarRenderPowerMeter()
        {
            var bar = KarSprite(0x8005B4CC); var frame = KarSprite(0x8005B4CC + 20);
            int step = 250;
            if (_karState >= 8 && _karState < 0xA)
            {
                frame.X = _karTurn != 0 ? 0x32 : -0x32;
                frame.ClutX = _karTurn != 0 ? 0x80 : 0x70;
                for (int i = 10; i > 0; i--)
                {
                    if (step * i <= _karPower)
                    {
                        bar.X = (frame.X + i * 3) - 3;
                        bar.ClutX = i == 10 ? 0x30 : 0x70;
                        KarDrawSprite(bar);
                    }
                }
                KarDrawSprite(frame);
            }
        }

        // KAR_renderReadyPrompt: KAR_D_8005B56C blinks (every 8 frames) in state 3
        void KarRenderReadyPrompt()
        {
            var s = KarSprite(0x8005B56C);
            if (_karState == 3)
            {
                if (_karReady++ >= 0x31) _karReady = 0x30;
                _karReady &= 0xFF;
                if ((_karReady & 8) != 0) KarDrawSprite(s);
            }
            else _karReady = 0;
        }

        // KAR_renderNamePlates: KAR_D_8005B4F4 (4 sprites); MetalMamemon's face KAR_D_8005B558 instead of the third
        void KarRenderNamePlates()
        {
            for (int i = 0; i < 4; i++)
            {
                var s = KarSprite(0x8005B4F4 + (uint)(20 * i));
                if (_karOpp == 3 && i == 2) s = KarSprite(0x8005B558);
                KarDrawSprite(s);
            }
        }

        // KAR_handleAimScroll: while the boy aims, up / down scroll the picture 10 pixels; left / right 8 when he turns fast
        void KarHandleAimScroll()
        {
            if (_karState != 6) return;
            int dx = 0, dy = 0;
            if (_karTurn != 0) return;
            if (_karTurnSpeed >= 10 || _karTurnSpeed < -9 || _karHint != 0)
            {
                if (KarHeld(PadLeft)) dx -= 8;
                if (KarHeld(PadRight)) dx += 8;
            }
            if (KarHeld(PadUp)) dy -= 10;
            if (KarHeld(PadDown)) dy += 10;
            if (dx != 0 || dy != 0) KarCamShift(new Vector2(dx, dy));
        }

        // ------------------------------------------------------------------ the rings' map pieces

        // KAR_updateRingMarkers: map pieces KAR_D_8005B450 (start, count) light up while a moving stone is inside a zone (KAR_D_8005B460),
        // and the ring of the stone being counted; otherwise they are off
        void KarUpdateRingMarkers()
        {
            var flags = new int[4];       // MAIN_D_80134A48 copied: all 0
            for (int k = 0; k < 4; k++) flags[k] = ExeS8(0x80134A48 + (uint)k);
            if (_karState == 0xB)
            {
                _karRingsLit = 0;
                for (int p = 0; p < 2; p++)
                    for (int si = 0; si < 5; si++)
                    {
                        var s = _karRows[p].S[si];
                        if (s.State > 0 && s.Speed > 0)
                        {
                            for (int z = 0; z < 4; z++)
                            {
                                if (((_karRingsLit >> z) & 1) != 0) continue;
                                uint zo = 0x8005B460 + (uint)(6 * z);
                                int dist = KarDistance(s.Pos.X + KarS16(zo), s.Pos.Z + KarS16(zo + 2));
                                if (dist < KarS16(zo + 4)) { _karRingsLit |= 1 << z; z = 4; }
                            }
                        }
                    }
                for (int p = 0; p < 4; p++)
                {
                    if (((_karRingsLit >> p) & 1) != 0) flags[p] = 1;
                    KarRingPieces(p, flags[p] ^ 1);
                }
            }
            if (_karState == 0xD)
            {
                var t = _karTally >= 0 && _karTally < 11 ? _karOrder[_karTally] : null;
                if (t != null && t.Ring != t.PrevRing && t.State < 0x65)
                {
                    int ring = t.Ring & 0xF;
                    if (ring != 0) flags[ring - 1] = 1;
                }
                for (int k = 0; k < 4; k++) KarRingPieces(k, flags[k] ^ 1);
            }
            if (_karState != 0xD && _karState != 0xB) for (int k = 0; k < 4; k++) KarRingPieces(k, flags[k] ^ 1);
        }

        void KarRingPieces(int ring, int flag) { PieceFlags(KarS16(0x8005B450 + (uint)(4 * ring)), KarS16(0x8005B452 + (uint)(4 * ring)), flag); }
        void KarRingFlagsAllOff() { if (_karBin != null) for (int k = 0; k < 4; k++) KarRingPieces(k, 1); }

        // ------------------------------------------------------------------ KAR_checkStonesStopped

        void KarCheckStonesStopped()
        {
            bool moving = false;
            if (_karState != 0xB) return;
            for (int p = 0; p < 3; p++)
                for (int n = 0; n < 5; n++)
                {
                    var s = _karRows[p].S[n];
                    if (s.State > 0)
                    {
                        if (s.Speed > 0) { moving = true; break; }
                        s.Speed = 0;
                    }
                }
            if (!moving) _karState = 0xC;
            // a stone that stopped past the throwing line (z >= 0xBE) is out
            for (int p = 0; p < 3; p++)
                for (int n = 0; n < 5; n++)
                {
                    var s = _karRows[p].S[n];
                    if (s.State > 0 && s.Pos.Z >= 0xBE && s.Speed <= 0) s.State = -0x65;
                }
        }

        // ------------------------------------------------------------------ collisions

        // KAR_updateCollisions: the walls, then (state 11) 11 steps between where each stone was and is; a pair closer than 0x96 collides.
        // The US build repeats the last step while it still finds a collision there: that is glitch O (NTSC only: a 2-3 s freeze after a
        // throw, then a stone somewhere else on the ice). FIXED by the user's decision 2026-11-07: no repeat, as the JP build does.
        void KarUpdateCollisions()
        {
            var cur = new KarVec[15]; var prev = new KarVec[15];
            KarBounceOffWall();
            if (_karState != 0xB) return;
            for (int step = 0; step < 0xB; step++)
            {
                for (int p = 0; p < 3; p++)
                    for (int i = 0; i < 5; i++)
                    {
                        var s = _karRows[p].S[i];
                        if (s.State > 0)
                        {
                            cur[p * 5 + i].X = (s.Target.X * (10 - step) + s.Pos.X * step) / 10;
                            cur[p * 5 + i].Z = (s.Target.Z * (10 - step) + s.Pos.Z * step) / 10;
                            cur[p * 5 + i].Y = s.Target.Y;
                        }
                    }
                for (int a = 0; a < 15; a++)
                {
                    if (_karRows[a / 5].S[a % 5].State <= 0) continue;
                    for (int b = a + 1; b < 15; b++)
                    {
                        if (_karRows[b / 5].S[b % 5].State <= 0) continue;
                        int dist = KarDistance(cur[a].X - cur[b].X, cur[a].Z - cur[b].Z);
                        if (dist < 0x96)
                        {
                            if (step == 0) KarResolveCollision(_karRows[b / 5].S[b % 5], cur[b], _karRows[a / 5].S[a % 5], cur[a]);
                            else KarResolveCollision(_karRows[b / 5].S[b % 5], prev[b], _karRows[a / 5].S[a % 5], prev[a]);
                        }
                    }
                }
                for (int i = 0; i < 15; i++) prev[i] = cur[i];
            }
        }

        // KAR_bounceOffWall: zones of KAR_getWallZone; 1 / 2 side walls, 3 / 4 the slanted corners, 5 the far wall, 6 the near line
        void KarBounceOffWall()
        {
            for (int p = 0; p < 3; p++)
                for (int n = 0; n < 5; n++)
                {
                    var s = _karRows[p].S[n];
                    if (s.State <= 0 || s.Speed == 0) continue;
                    int zone = KarWallZone((short)s.Pos.X, (short)s.Pos.Z);
                    if (zone > 0 && zone < 6) KarSound(3);
                    int d; KarVec contact;
                    switch (zone)
                    {
                        case 1:
                            d = (short)(s.Pos.X + 0x2D5); s.Pos.X -= d * 2;
                            s.Angle = 0x800 - s.Angle;
                            break;
                        case 2:
                            s.Angle = 0x1800 - s.Angle;
                            d = (short)(s.Pos.X - 0x2D5); s.Pos.X -= d * 2;
                            break;
                        case 3:
                            contact = KarFindWallContact(s, 0);
                            KarReflectOffDiagonal(s, 0);
                            KarPlaceAtContact(s, contact);
                            break;
                        case 4:
                            contact = KarFindWallContact(s, 1);
                            KarReflectOffDiagonal(s, 1);
                            KarPlaceAtContact(s, contact);
                            break;
                        case 5:
                            s.Angle = -s.Angle;
                            d = (short)(-0x9DD - s.Pos.Z); s.Pos.Z += d * 2;
                            break;
                        case 6:
                            if (s.Target.Z < 0x465)
                            {
                                KarSound(3);
                                s.Angle = -s.Angle;
                                d = (short)(s.Pos.Z - 0x465); s.Pos.Z -= d * 2;
                            }
                            break;
                    }
                }
        }

        static int KarWallZone(int x, int z)
        {
            if (z >= 0x465) return 6;
            if (z >= -0x7D0)
            {
                if (x < -0x2D4) return 1;
                if (x >= 0x2D5) return 2;
            }
            else
            {
                if (z + x * 2 < -0xDC4 && x < 0x1AA) return 3;
                if (z - x * 2 < -0xDC4 && x >= 0x1A9) return 4;
                if (z < -0x9DC) return 5;
            }
            return 0;
        }

        // KAR_findWallContact: back along the stone's way until it is on the near side of the slanted wall
        KarVec KarFindWallContact(KarStone s, int flag)
        {
            int i = 0, t; KarVec v = new KarVec();
            int ang = (s.Angle + 0x800) & 0xFFF;
            do
            {
                v.X = s.Pos.X + i * KCos(ang) / 4096;
                v.Z = s.Pos.Z + i * KSin(ang) / 4096;
                i++;
                t = flag == 0 ? (v.Z + 0xDC5) + v.X * 2 : (v.Z + 0xDC5) - v.X * 2;
            } while (t <= 0 && i < 100000);
            return v;
        }

        // KAR_reflectOffDiagonal: the angle (reversed) mirrored round the wall's direction ratan2(1, +-2)
        static void KarReflectOffDiagonal(KarStone s, int dir)
        {
            int cur = (s.Angle + 0x800) & 0xFFF;
            int ang = dir == 0 ? KAtan2(1, 2) : KAtan2(1, -2);
            s.Angle = ang * 2 - cur;
        }

        // KAR_placeAtContact: the rest of this frame's way goes on from the wall in the new direction
        static void KarPlaceAtContact(KarStone s, KarVec p)
        {
            int d = KarDistance(p.X - s.Target.X, p.Z - s.Target.Z);
            d = KarDistance(s.Target.Z - s.Pos.Z, s.Target.X - s.Pos.X) - d;
            s.Pos.X = p.X + d * KCos(s.Angle) / 4096;
            s.Pos.Z = p.Z + d * KSin(s.Angle) / 4096;
        }

        // KAR_resolveStoneCollision: sound 1 (hard) or 2; a pegged stone, a resting one, or two moving ones
        void KarResolveCollision(KarStone sa, KarVec a, KarStone sb, KarVec b)
        {
            int speed = (short)(sa.Speed + sb.Speed);
            KarSound(speed >= 0xBB8 ? 1 : 2);
            if (sa.State == 2 || sa.State == 3 || sb.State == 2 || sb.State == 3)
            {
                if (sa.State == 2 || sa.State == 3) KarCollidePegged(sa, a, sb, b);
                else KarCollidePegged(sb, b, sa, a);
            }
            else if (sa.Speed <= 0 || sb.Speed <= 0)
            {
                if (sa.Speed <= 0) KarCollideResting(sa, a, sb, b);
                else KarCollideResting(sb, b, sa, a);
            }
            else KarCollideMoving(sb, b, sa, a);
        }

        // KAR_collidePeggedStone: the mover glances off; the pegged stone turns round its pegs (state 3) with part of the speed
        void KarCollidePegged(KarStone pegged, KarVec a, KarStone mover, KarVec b)
        {
            // KAR_debugCollisionStone returns at once
            int ang1 = KAtan2(mover.Target.Z - mover.Pos.Z, mover.Target.X - mover.Pos.X) & 0xFFF;
            int r = KAtan2(b.Z - a.Z, b.X - a.X) & 0xFFF;
            int da = r * 2 - ang1;
            if (da >= 0x1000) da = da % 0x1000;
            int dd = System.Math.Abs(r - ang1);
            dd = dd % 0x400;
            dd = 0x400 - dd;
            r = KarDistance(mover.Pos.X - b.X, mover.Pos.Z - b.Z);
            r = dd * r / 0x400;
            int dist, guard = 0;
            do
            {
                mover.Pos.X = b.X + r * KCos(da) / 4096;
                mover.Pos.Z = b.Z + r * KSin(da) / 4096;
                r++;
                dist = KarDistance(mover.Pos.X - pegged.Pos.X, mover.Pos.Z - pegged.Pos.Z);
            } while (dist < 0x97 && ++guard < 100000);
            int t = (mover.Speed >> 1) * dd / 0x400;
            pegged.Speed = (short)t;
            mover.Speed = (short)(mover.Speed - t);
            pegged.Angle = KAtan2(mover.Target.Z - b.Z, mover.Target.X - b.X);
            mover.Angle = da;
            pegged.State = 3;
            mover.Target = b;
        }

        // KAR_collideRestingStone: the resting stone takes the share of the speed the hit gives it; the two are moved apart
        void KarCollideResting(KarStone resting, KarVec a, KarStone mover, KarVec b)
        {
            if (resting.Speed == 0 && mover.Speed == 0) { resting.Pos = a; mover.Pos = b; return; }
            int share = KarImpactShare(mover, b, a);
            KarVec p1 = KarSeparation(mover, b, a);
            resting.Pos.X = resting.Pos.X + p1.X; resting.Pos.Z = resting.Pos.Z + p1.Z;
            mover.Pos.X = mover.Pos.X - p1.X; mover.Pos.Z -= p1.Z;
            int d = KarDistance(resting.Pos.X - mover.Pos.X, resting.Pos.Z - mover.Pos.Z);
            int ang = KAtan2(p1.Z, p1.X);
            int n = KarDistance(p1.X, p1.Z);
            int r = 0, guard = 0;
            p1 = resting.Pos; KarVec p2 = mover.Pos;
            if (n != 0)
            {
                while (d < 0x97 && ++guard < 100000)
                {
                    resting.Pos.X = p1.X + r * KCos(ang) / 4096; resting.Pos.Z = p1.Z + r * KSin(ang) / 4096;
                    mover.Pos.X = p2.X - r * KCos(ang) / 4096; mover.Pos.Z = p2.Z - r * KSin(ang) / 4096;
                    d = KarDistance(resting.Pos.X - mover.Pos.X, resting.Pos.Z - mover.Pos.Z);
                    r++;
                }
            }
            else
            {
                int push = KAtan2(mover.Pos.Z - resting.Pos.Z, mover.Pos.X - resting.Pos.X);
                while (d < 0x97 && ++guard < 100000)
                {
                    mover.Pos.X = p2.X + r * KCos(push) / 4096; mover.Pos.Z = p2.Z + r * KSin(push) / 4096;
                    d = KarDistance(resting.Pos.X - mover.Pos.X, resting.Pos.Z - mover.Pos.Z);
                    r++;
                }
            }
            resting.Target = a; mover.Target = b;
            mover.Speed = (short)(mover.Speed - share);
            resting.Speed = (short)share;
            resting.Angle = KAtan2(resting.Pos.Z - a.Z, resting.Pos.X - a.X) & 0xFFF;
            mover.Angle = KAtan2(mover.Pos.Z - b.Z, mover.Pos.X - b.X) & 0xFFF;
        }

        // KAR_collideMovingStones: each gives the other its share; the faster one is moved off
        void KarCollideMoving(KarStone sa, KarVec a, KarStone sb, KarVec b)
        {
            // KAR_debugCollisionPair returns at once
            int share1 = KarImpactShare(sa, a, b), share2 = KarImpactShare(sb, b, a);
            KarVec p1 = KarSeparation(sb, b, a), p2 = KarSeparation(sa, a, b);
            KarVec delta = new KarVec { X = p2.X - p1.X, Z = p2.Z - p1.Z };
            int ang = KarDistance(delta.X, delta.Z), d, r, guard = 0;
            if (ang != 0)
            {
                ang = KAtan2(delta.Z, delta.X);
                sb.Pos.X = sb.Pos.X + delta.X; sb.Pos.Z = sb.Pos.Z + delta.Z;
                sa.Pos.X = sa.Pos.X - delta.X; sa.Pos.Z -= delta.Z;
                d = KarDistance(sa.Pos.X - sb.Pos.X, sa.Pos.Z - sb.Pos.Z);
                r = 0;
                p1 = sa.Pos; p2 = sb.Pos;
                sa.Speed = (short)(sa.Speed + (short)(share2 - share1));
                sb.Speed = (short)(sb.Speed + (short)(share1 - share2));
                if (sa.Speed > sb.Speed)
                {
                    while (d < 0x97 && ++guard < 100000)
                    {
                        sa.Pos.X = p1.X + r * KCos(ang) / 4096; sa.Pos.Z = p1.Z + r * KSin(ang) / 4096;
                        d = KarDistance(sb.Pos.X - sa.Pos.X, sb.Pos.Z - sa.Pos.Z);
                        r++;
                    }
                }
                else
                {
                    while (d < 0x97 && ++guard < 100000)
                    {
                        sb.Pos.X = p2.X + r * KCos(ang) / 4096; sb.Pos.Z = p2.Z + r * KSin(ang) / 4096;
                        d = KarDistance(sb.Pos.X - sa.Pos.X, sb.Pos.Z - sa.Pos.Z);
                        r++;
                    }
                }
                sa.Target = a; sb.Target = b;
                sa.Angle = KAtan2(sa.Pos.Z - a.Z, sa.Pos.X - a.X);
                sb.Angle = KAtan2(sb.Pos.Z - b.Z, sb.Pos.X - b.X);
            }
            else
            {
                if (sb.Speed / sa.Weight < 5 || sa.Speed / sb.Weight < 5)
                {
                    sb.Speed = 0;
                    sb.Pos = sb.Target;
                    sa.Pos = sa.Target;
                }
                else { sb.Pos = b; sa.Pos = a; }
            }
        }

        // KAR_computeImpactShare: the part of the speed that goes along the line to the other stone
        static int KarImpactShare(KarStone s, KarVec a, KarVec b)
        {
            int ang = s.Angle & 0xFFF;
            int impact = KAtan2(b.Z - a.Z, b.X - a.X) & 0xFFF;
            int d = (ang - impact) & 0xFFF;
            int ret = 0;
            if (d < 0x400 || d > 0xC00)
            {
                if (d > 0xC00) d = (0x1000 - d) & 0xFFF;
                d = 0x400 - (d % 0x400);
                ret = d * s.Speed / 0x400;
            }
            return ret;
        }

        // KAR_computeSeparation
        static KarVec KarSeparation(KarStone s, KarVec a, KarVec b)
        {
            int cur = s.Angle & 0xFFF;
            int ang = KAtan2(b.Z - a.Z, b.X - a.X) & 0xFFF;
            int d = System.Math.Abs(cur - ang);
            KarVec v = new KarVec();
            if (d < 0x400 || d > 0xC00)
            {
                d = d % 0x400;
                int dist = KarDistance(s.Pos.Z - a.Z, s.Pos.X - a.X);
                int r = (0x400 - d) * dist / 0x400, n, guard = 0;
                do
                {
                    v.X = r * KCos(ang) / 4096;
                    v.Z = r * KSin(ang) / 4096;
                    n = KarDistance(v.X, v.Z);
                    r++;
                } while (n == 0 && ++guard < 100000);
            }
            return v;
        }

        // ------------------------------------------------------------------ aiming and throwing

        // KAR_beginAiming
        void KarBeginAiming()
        {
            int idx = _karTurn != 0 ? _karOpp : 0;
            _karState = 6;
            if (idx == 0) KarStartAnim(0, 0x23);
            else if (_karOpp == 2) KarStartAnim(_karOpp, 0x1C);
            var s = _karRows[_karTurn].S[_karSel];
            s.State = 0;
            if (s.Type == 3) KarLink(s, _karTurn != 0 ? 1 : 2);       // the pegs are not shown while it is thrown
            _karAim = -0x400;
            KarSetVy(idx, 0);
        }

        // KAR_selectPreviousStone / KAR_selectNextStone: the next one still in hand (state -1)
        void KarSelectPrevious()
        {
            if (_karSel > 0)
                for (int r = _karSel - 1; r >= 0; r--)
                    if (_karRows[_karTurn].S[r].State == -1) { _karSel = r; return; }
        }
        void KarSelectNext()
        {
            if (_karSel < 4)
                for (int r = _karSel + 1; r < 5; r++)
                    if (_karRows[_karTurn].S[r].State == -1) { _karSel = r; return; }
        }

        // KAR_turnAimLeft / KAR_turnAimRight (the same code)
        void KarTurnAim(int delta)
        {
            delta = (short)delta;
            int idx = _karTurn != 0 ? _karOpp : 0;
            KarSetVy(idx, (short)(KarVyOf(idx) - delta));
            _karAim += delta;
        }

        // KAR_beginThrow
        void KarBeginThrow()
        {
            _karState = 7;
            if (_karTurn == 0) KarStartAnim(0, 0x24);
            else if (_karOpp == 2) KarStartAnim(_karOpp, 0x1D);
            else KarStartAnim(_karOpp, 0x1F);
        }

        // ------------------------------------------------------------------ the yes / no prompt and the hint box (UI box 0)

        // KAR_tickYesNoPrompt: "Start Game" / "Explain Game" (US: one string, KAR_D_8005ABB8, shown as two slices); returns 1 or 2 when chosen
        int KarYesNo()
        {
            RectInt final = ExeRect(0x80134A10), start = ExeRect(0x80134A18);
            while (_karHint < 2)
            {
                // MAIN_D_80134A20: { KAR_D_8005ABB8, NULL } on the US disc
                if (_karHint == 0) _karView.DrawString(KarStr(0x8005ABB8), 0, _karHint * 13 + 0xDD, _karInk);
                _karHint++;
                break;
            }
            if (_karView.Box.State == 0)
            {
                if (_karHint == 2) { _karView.OpenBox(final, start); _karHint = 3; }
                else if (_karHint == 3 || _karHint == 4) return _karHint - 2;
            }
            if (_karView.Box.State == 1)
            {
                if (KarPressed(PadCross)) _karView.CloseBox();
                else
                {
                    if (KarPressed(PadUp)) { _karHint = _karHint == 3 ? 4 : 3; Snd(2); }
                    else if (KarPressed(PadDown)) { _karHint = _karHint == 3 ? 4 : 3; Snd(2); }
                    _karView.Text(-0x7C, 0x30, 0x52, 0xD, 0, 0xDC);
                    _karView.Text(-0x7C, 0x3D, 0x5C, 0xD, 0x53, 0xDC);
                    _karView.Cursor(final.x + 6, final.y + 7 + (_karHint - 3) * 13, 0x5E, 0xD);
                }
            }
            return 0;
        }

        // KAR_tickHintBox: page n of the opponent's hints, a line drawn each frame; Cross turns the page; returns 0 once it is closed (or none)
        int KarHintBox(int n)
        {
            RectInt final = ExeRect(0x80134A28), start = ExeRect(0x80134A30);
            if (_karHint == 0) return 0;
            if (n < 4) { start.x = 0x4B; start.y = -5; }
            if (_karView.Box.State == 0)
            {
                if (_karHint == 1)
                {
                    _karView.OpenBox(final, start);
                    _karView.ClearText();
                    _karHintLine = 0;
                    return 1;
                }
                return 0;
            }
            n = (sbyte)(_karOpp == 2 ? KarDrawHintPage(0x8005AF58, 0x8005B04C, n, _karHintLine) : KarDrawHintPage(0x8005B318, 0x8005B40C, n, _karHintLine));
            if (_karHintLine < n * 4) _karHintLine++;
            if (_karView.Box.State == 1)
            {
                if (KarPressed(PadCross))
                {
                    _karHint++;
                    Snd(3);
                    if (n < _karHint) { _karView.CloseBox(); _karHint = -1; }
                }
                if (_karHint != -1)
                {
                    var r = _karView.Box.Rect;
                    _karView.Text(r.x + 4, r.y + 2, 0xFC, 0x34, 0, (_karHint - 1) * 52);
                    _karView.NextMark(_karBoxTimer);
                }
            }
            return 1;
        }

        // KAR_drawHintPagePenguinmon / KAR_drawHintPageMetalMamemon: line n of hint idx (the first of every 4 is the name, colour 7); returns its pages
        int KarDrawHintPage(uint strs, uint offs, int idx, int n)
        {
            _karInk = n % 4 == 0 ? 7 : 1;
            int first = KarU8(offs + (uint)idx), next = KarU8(offs + (uint)(idx + 1));
            int k = first + n;
            if (k >= 0 && k < 61) _karView.DrawString(KarStrAt(strs, k), 0, n * 13 + 1, _karInk);
            return (sbyte)((next - first) / 4);
        }

        // ------------------------------------------------------------------ rings and scores

        // KAR_classifyStoneRings: ring 1-4 of each stone past z -0x564 (zones KAR_D_8005B438), 0x10 for the opponent's
        void KarClassifyStoneRings()
        {
            for (int p = 0; p < 2; p++)
            {
                _karRows[p].Score = 0;
                for (int i = 0; i < 5; i++)
                {
                    var s = _karRows[p].S[i];
                    s.PrevRing = s.Ring;
                    s.Ring = 0;
                    if (s.State <= 0) continue;
                    if (s.Pos.Z >= -0x564) continue;
                    for (int zi = 0; zi < 4; zi++)
                    {
                        uint zo = 0x8005B438 + (uint)(6 * zi);
                        int dist = KarDistance(s.Pos.X + KarS16(zo), s.Pos.Z + KarS16(zo + 2));
                        if (dist > KarS16(zo + 4)) continue;
                        s.Ring = zi + 1;
                        if (p != 0) s.Ring |= 0x10;
                        break;
                    }
                }
            }
            KarRegisterThrownStone();
        }

        // KAR_registerThrownStone
        void KarRegisterThrownStone()
        {
            _karTally = (sbyte)(_karRows[0].Thrown + _karRows[1].Thrown);
            if (_karTally >= 0 && _karTally < 11) _karOrder[_karTally] = _karRows[_karTurn].S[_karSel];
            _karLastTallied = -1;
        }

        KarStone KarOrderAt(int i) { return i >= 0 && i < 11 ? _karOrder[i] : null; }

        // KAR_tickScoreTally: from the last thrown stone back, the camera goes to each stone whose ring changed, it blinks 8 times and its points
        // (MAIN_D_80134A44: 2, 1, -2, 2) count; Cross counts the rest at once. Returns 1 when done.
        int KarTickScoreTally()
        {
            if (KarPressed(PadCross)) _karSkip = 1;
            if (_karSkip == 1 && (_karTallyStep == 2 || _karTallyStep == 1))
            {
                _karTally = (sbyte)(_karRows[0].Thrown + _karRows[1].Thrown);
                _karTally = (sbyte)(_karTally - 1);
                _karRows[1].Score = _karRows[0].Score = 0;
                while (_karTally >= 0)
                {
                    var s = KarOrderAt(_karTally);
                    if (s != null)
                    {
                        int player = (s.Ring & 0x10) != 0 ? 1 : 0, ring = s.Ring & 0xF;
                        if (s.State > 0 && ring != 0) _karRows[player].Score = (sbyte)(_karRows[player].Score + ExeS8(0x80134A44 + (uint)(ring - 1)));
                    }
                    _karTally = (sbyte)(_karTally - 1);
                }
                for (int i = 0; i < 3; i++) for (int j = 0; j < 5; j++) { var s = _karRows[i].S[j]; if (s.State >= 100) s.State -= 100; }
                _karBlinkCount = 0; _karBlinkTimer = 0;
                _karSkip = _karTallyStep = 0;
                return 1;
            }
            if (_karLastTallied != _karTally)
            {
                while (_karTally >= 0)
                {
                    var s = KarOrderAt(_karTally);
                    int ring = s != null ? s.Ring & 0xF : 0;
                    if (ring != 0) { _karLastTallied = _karTally; _karTallyStep = 0; break; }
                    _karTally = (sbyte)(_karTally - 1);
                }
                if (_karTally < 0) { _karSkip = 0; return 1; }
            }
            var t = KarOrderAt(_karTally);
            if (t == null) { _karSkip = 0; return 1; }
            if (_karTallyStep == 0)
            {
                if (t.Ring != t.PrevRing)
                {
                    int done = KarCamMoveTo(t.Pos.X, t.Pos.Z, 5);
                    if (done == 1) _karTallyStep = 1;
                    else return 0;
                }
                else _karTallyStep = 1;
            }
            if (_karTallyStep == 1)
            {
                int player = (t.Ring & 0x10) != 0 ? 1 : 0, ring = t.Ring & 0xF;
                _karRows[player].Score = (sbyte)(_karRows[player].Score + ExeS8(0x80134A44 + (uint)(ring - 1)));
                _karTallyStep = 2;
                if (t.Ring != t.PrevRing && (t.Ring & 0xF) != 0) KarSound(ring == 3 ? 6 : 5);
            }
            if (_karTallyStep == 2 && _karTally >= 0)
            {
                if (t.Ring != t.PrevRing && (t.Ring & 0xF) != 0)
                {
                    if (_karBlinkTimer++ >= 0xB)
                    {
                        if (t.State < 100) t.State += 100; else t.State -= 100;
                        _karBlinkTimer -= 10;
                        if (_karBlinkCount++ >= 7) { _karBlinkCount = 0; _karTallyStep = 4; _karTally = (sbyte)(_karTally - 1); }
                    }
                }
                else _karTally = (sbyte)(_karTally - 1);
                return _karTally < 0 ? 1 : 0;
            }
            return 0;
        }

        // ------------------------------------------------------------------ the opponent's shot (KAR_chooseOpponentShot)

        int KarChooseOpponentShot()
        {
            int angle, clear; int tx, tz;
            if (_karOpp == 2)
            {
                // Penguinmon: straight at the middle if the way is clear; else knock out the boy's stone in the GOOD ring, or one of its own
                angle = KarFindClearShotAngle(0, -0x6A4);
                if (angle == -1)
                {
                    if (_karRows[0].Score > _karRows[1].Score)
                    {
                        angle = KarAimAtStoneInRing(1, 0x14, out tx, out tz);
                        if (angle != -1) KarSetOpponentShot(0, angle, tx, tz);
                        else KarPenguinmonFallback();
                    }
                    else KarPenguinmonFallback();
                }
                else KarSetOpponentShot(1, angle, 0, -0x6A4);
            }
            else
            {
                var b = KarBone(_karOpp, 4);
                int px = (short)b.X, pz = (short)b.Z;
                var opp = _karRows[1];
                if (opp.Thrown == 0)
                {
                    angle = KAtan2(-0x8FC - pz, -0x190 - px);
                    clear = KarFindClearShotAngle(-0x190, -0x8FC);
                    if (angle == clear) KarSetOpponentShot(1, angle, -0x190, -0x8FC);
                    else
                    {
                        angle = KAtan2(-0x6A4 - pz, -px);
                        clear = KarFindClearShotAngle(0, -0x6A4);
                        if (angle == clear) KarSetOpponentShot(1, angle, 0, -0x6A4);
                        else
                        {
                            angle = KAtan2(-0x6A4 - pz, -px);
                            if (opp.Thrown % 2 != 0) angle += KRand() % 100; else angle -= KRand() % 100;
                            KarSetOpponentShot(0, angle, 0, -0x6A4);
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < 5; i++)
                    {
                        if (opp.S[i].State != -1) continue;
                        if (opp.S[i].Type == 3)
                        {
                            opp.Unk4 = i;
                            angle = KAtan2(-0x6A4 - pz, -px);
                            clear = KarFindClearShotAngle(0, -0x6A4);
                            if (angle == clear) KarSetOpponentShot(1, angle, 0, -0x6A4);
                            else opp.Unk4 = 6;
                            break;
                        }
                        opp.Unk4 = 6;
                    }
                    if (opp.Unk4 == 6)
                    {
                        var last = _karLast;
                        switch (last.Type)
                        {
                            case 0: case 1: case 2:
                                if (last.Ring == 3 || last.Ring == 0)
                                {
                                    angle = KAtan2(-0x8FC - pz, -0x190 - px);
                                    KarFindClearShotAngle(-0x190, -0x8FC);
                                    KarSetOpponentShot(1, angle, -0x190, -0x8FC);
                                }
                                else if (last.State < 0)
                                {
                                    angle = KAtan2(-0x6A4 - pz, -px);
                                    if (opp.Thrown % 2 != 0) angle += KRand() % 100; else angle -= KRand() % 100;
                                    KarSetOpponentShot(0, angle, 0, -0x6A4);
                                }
                                else if (KRand() % 2 != 0)
                                {
                                    angle = KAtan2(-0x8FC - pz, -0x190 - px);
                                    clear = KarFindClearShotAngle(-0x190, -0x8FC);
                                    if (angle == clear) KarSetOpponentShot(1, angle, -0x190, -0x8FC);
                                    else
                                    {
                                        angle = KAtan2(last.Pos.Z - pz, last.Pos.X - px);
                                        KarSetOpponentShot(0, angle, (short)last.Pos.X, (short)last.Pos.Z);
                                    }
                                }
                                else
                                {
                                    angle = KarAimBankShot(last, 0x190, -0x8FC);
                                    KarSetOpponentShot(0, angle, 0x190, -0x8FC);
                                    opp.Unk2 = 0x960;
                                }
                                break;
                            case 3:
                                angle = KAtan2(-0x6A4 - pz, -px);
                                if (opp.Thrown % 2 != 0) angle += KRand() % 100; else angle -= KRand() % 100;
                                KarSetOpponentShot(0, angle, 0, -0x6A4);
                                break;
                        }
                    }
                }
            }
            return 0;
        }

        // the part of Penguinmon's choice both branches end in
        void KarPenguinmonFallback()
        {
            int tx, tz;
            int angle = KarAimAtStoneInRing(0, 2, out tx, out tz);
            if (angle != -1) KarSetOpponentShot(0, angle, tx, tz);
            else
            {
                angle = KarFindClearShotAngle(-0x190, -0x960);
                if (angle != -1) KarSetOpponentShot(1, angle, 0, -0x960);
                else
                {
                    angle = KarAimAtRandomStone();
                    KarSetOpponentShot(0, angle, 0, -0x960);
                    _karRows[1].Unk2 = 0x960;
                }
            }
        }

        // KAR_findClearShotAngle: walks from the opponent to (x, z); Penguinmon gives -1 when a stone is in the way, MetalMamemon the angle off that stone
        int KarFindClearShotAngle(int x, int z)
        {
            x = (short)x; z = (short)z;
            var loc = KarLoc(_karOpp);
            int px = (short)loc.X, pz = (short)loc.Z;
            if (_karOpp != 2) { var b = KarBone(_karOpp, 4); px = (short)b.X; pz = (short)b.Z; }
            int ang = KAtan2(z - pz, x - px);
            int step = _karOpp == 2 ? 30 : 10;
            int guard = 0;
            while (z < pz && ++guard < 100000)
            {
                px = (short)(px + (short)(step * KCos(ang) / 4096));
                pz = (short)(pz + (short)(step * KSin(ang) / 4096));
                for (int p = 0; p < 3; p++)
                    for (int n = 0; n < 5; n++)
                    {
                        var s = _karRows[p].S[n];
                        if (s.State <= 0) continue;
                        if (KarDistance(px - s.Pos.X, pz - s.Pos.Z) < 0x97)
                        {
                            if (_karOpp == 2) return -1;
                            return KAtan2(s.Pos.Z - z, s.Pos.X - x);
                        }
                    }
            }
            return ang;
        }

        // KAR_aimAtStoneInRing: the first stone of the row (not pegged) with that ring value
        int KarAimAtStoneInRing(int player, int key, out int outX, out int outZ)
        {
            outX = outZ = 0;
            for (int i = 0; i < 5; i++)
            {
                var s = _karRows[player].S[i];
                if (s.State < 2 && s.Ring == key)
                {
                    int r = KarFindClearShotAngle((short)s.Pos.X, (short)s.Pos.Z);
                    outX = (short)s.Pos.X; outZ = (short)s.Pos.Z;
                    return r;
                }
            }
            return -1;
        }

        // KAR_setOpponentShot: the first unused stone in the order KAR_D_8005B418[row] (types), its angle and the power to reach (b, c)
        void KarSetOpponentShot(int row, int val, int b, int c)
        {
            KarStone stone = null;
            for (int i = 0; i < 4; i++)
            {
                int n = KarFindUnusedStoneOfType((sbyte)KarS32(0x8005B418 + (uint)(16 * row + 4 * i)));
                if (n >= 0) { _karRows[1].Unk4 = n; stone = _karRows[1].S[n]; break; }
            }
            if (stone == null) return;       // (the original would write through an unset pointer; it always has a stone left on its turn)
            stone.Angle = val;
            _karRows[1].Unk2 = (short)KarComputeThrowPower((uint)stone.Weight, (short)b, (short)c);
        }

        // KAR_aimAtRandomStone: at one of the boy's stones on the ice (from 0x190, 0x578), or the middle
        int KarAimAtRandomStone()
        {
            if (_karLast.State < 0) return KAtan2(-0xC1C, -0x190);
            var cands = new List<KarStone>();
            foreach (var s in _karRows[0].S) if (s.State == 1) cands.Add(s);
            int px, pz;
            if (cands.Count == 0) { px = 0; pz = -0x6A4; }
            else { int idx = (byte)(KRand() % cands.Count); px = (short)cands[idx].Pos.X; pz = (short)cands[idx].Pos.Z; }
            return KAtan2(pz - 0x578, px - 0x190);
        }

        // KAR_aimBankShot: off the left wall (-0x2D5) to hit 'stone' towards (x, z)
        int KarAimBankShot(KarStone stone, int x, int z)
        {
            int ang = KAtan2(stone.Pos.Z - z, stone.Pos.X - x);
            int cx = stone.Pos.X + (KCos(ang) * 150) / 4096;
            int cz = stone.Pos.Z + (KSin(ang) * 150) / 4096;
            int t = cx + 0x2D5;
            cx = -0x2D5 - t;
            var b = KarBone(_karOpp, 4);
            return KAtan2(cz - b.Z, cx - b.X);
        }

        int KarFindUnusedStoneOfType(int key)
        {
            for (int i = 0; i < 5; i++) { var s = _karRows[1].S[i]; if (s.State == -1 && s.Type == key) return i; }
            return -1;
        }

        // KAR_computeThrowPower: the speed that slides from (0x190, 0x578) to (b, c) under the 98 % a frame, less 600 (Penguinmon) or 500
        int KarComputeThrowPower(uint a, int b, int c)
        {
            int dist = KarDistance(0x190 - b, 0x578 - c);
            uint acc = 0, cur = a, value = cur * 100;
            int guard = 0;
            while (acc < (uint)dist && ++guard < 100000)
            {
                acc += cur / a;
                value = value * 100 / 98;
                cur = value / 100;
            }
            if (_karOpp == 2) cur -= 0x258; else cur -= 0x1F4;
            return (int)cur;
        }

        // ------------------------------------------------------------------ KAR_tickMatchState

        void KarTickMatchState()
        {
            int player = _karTurn != 0 ? _karOpp : 0;
            int prompt;
            switch (_karState)
            {
                case 0:
                    _karTimer = 0;
                    KarSetLoc(0, -0x190, 0x578); KarSetVy(0, 0);
                    KarSetLoc(_karOpp, 0x190, 0x578); KarSetVy(_karOpp, 0);
                    KarStartAnim(0, 0); KarStartAnim(_karOpp, 0);
                    _karState = 1;
                    _karView.ClearText();
                    _karHint = 0;
                    break;
                case 1:
                    prompt = KarYesNo();
                    if (prompt == 1) { _karState = 3; _karHint = 0; }
                    else if (prompt == 2) { _karView.ClearText(); _karState = 2; _karHint = 0; KarSetupMatch(1); }
                    break;
                case 3:
                    // the "ready" prompt blinks; the stones come out one by one (KAR_renderScene)
                    if (_karReady >= 0x30)
                    {
                        _karTimer = (ushort)(_karTimer + 6);
                        if (_karTimer >= 0x169) { _karTimer = 0; _karState = 5; }
                    }
                    break;
                case 2:
                    _karTimer = (ushort)(_karTimer + 6);
                    if (_karTimer >= 0x169) { _karTimer = 0; _karState = 4; }
                    break;
                case 4:
                    // the explanation: the boy's throw is made for him (a random stone, power 0x7D0, at ratan2(-0xC1C, 0x190))
                    _karHint = 1;
                    _karState = 5;
                    _karRows[0].Unk4 = KRand() % 5;
                    _karRows[0].Unk2 = 0x7D0;
                    _karRows[0].S[_karRows[0].Unk4].Angle = KAtan2(-0xC1C, 0x190);
                    break;
                case 5:
                    if (_karTurn != 0 || _karHint != 0)
                    {
                        if (KarHintBox(0) != 0) break;
                        if (_karTimer++ >= 0x10)
                        {
                            _karTimer = (ushort)(_karTimer - 0xF);
                            if (_karSel == _karRows[_karTurn].Unk4)
                            {
                                _karRows[2].S[4].CopyFrom(_karRows[_karTurn].S[_karSel]);
                                _karRows[2].S[4].State = -0x65;
                                KarLink(_karRows[2].S[4], _karRows[2].S[4].Model);
                                KarBeginAiming();
                                _karTimer = 0;
                                if (_karHint != 0) _karHint = 1;
                            }
                            if (_karRows[_karTurn].Unk4 > _karSel) KarSelectNext();
                        }
                    }
                    else
                    {
                        if (KarPressed(PadCross)) KarBeginAiming();
                        else if (KarPressed(PadUp)) KarSelectPrevious();
                        else if (KarPressed(PadDown)) KarSelectNext();
                    }
                    _karTurnSpeed = 0;
                    break;
                case 6:
                    if (_karTurn != 0 || _karHint != 0)
                    {
                        int target = _karRows[_karTurn].S[_karSel].Angle;
                        if (KarHintBox(1) != 0)
                        {
                            // while the hint shows, the pad turns the aim
                            if (KarHeld(PadRight) && _karAim < -0x18F)
                            {
                                _karTurnSpeed = _karTurnSpeed > 0 ? (short)(_karTurnSpeed + 1) : 1;
                                KarTurnAim(_karTurnSpeed);
                            }
                            else if (KarHeld(PadLeft) && _karAim >= -0x5B6)
                            {
                                _karTurnSpeed = _karTurnSpeed < 0 ? (short)(_karTurnSpeed - 1) : -1;
                                KarTurnAim(_karTurnSpeed);
                            }
                            else if (!KarHeld(PadLeft) && !KarHeld(PadRight)) { _karTurnSpeed = 0; break; }
                            else break;
                        }
                        if (target <= _karAim + 0xA && target >= _karAim - 0xA)
                        {
                            KarBeginThrow();
                            if (_karHint != 0) _karHint = 1;
                        }
                        else if (target > _karAim) KarTurnAim(0xA);
                        else KarTurnAim(-0xA);
                    }
                    else
                    {
                        if (KarPressed(PadCross)) KarBeginThrow();
                        if (KarHeld(PadRight) && _karAim < -0x18F)
                        {
                            _karTurnSpeed = _karTurnSpeed > 0 ? (short)(_karTurnSpeed + 1) : 1;
                            KarTurnAim(_karTurnSpeed);
                        }
                        if (KarHeld(PadLeft) && _karAim >= -0x5B6)
                        {
                            _karTurnSpeed = _karTurnSpeed < 0 ? (short)(_karTurnSpeed - 1) : -1;
                            KarTurnAim(_karTurnSpeed);
                        }
                        if (!KarHeld(PadLeft) && !KarHeld(PadRight)) _karTurnSpeed = 0;
                    }
                    break;
                case 7:
                    {
                        var s = _karRows[_karTurn].S[_karSel];
                        _karCamDone = KarCamMoveTo(s.Pos.X, s.Pos.Z, 0x14);
                        if (_karCamDone == 1) _karState = 8;
                        _karTurnSpeed = 0;
                        break;
                    }
                case 8:
                    _karPower = (short)(_karPower + 0x64);
                    if (_karPower >= 0xBB8)
                    {
                        // the gauge ran full before Cross: the throw is taken back
                        _karPower = 0;
                        _karState = 6;
                        KarSetLoc(0, -0x190, 0x578);
                        KarSetLoc(_karOpp, 0x190, 0x578);
                        if (_karTurn == 0) KarStartAnim(0, 0); else KarStartAnim(_karOpp, 0);
                    }
                    else if (_karTurn != 0 || _karHint != 0)
                    {
                        if (KarHintBox(2) != 0)
                        {
                            if (_karRows[_karTurn].Unk2 <= _karPower) _karPower = (short)_karRows[_karTurn].Unk2;
                        }
                        else if (_karRows[1].Unk2 <= _karPower)
                        {
                            _karState = 9;
                            _karRows[_karTurn].S[_karSel].RotY = (short)KarVyOf(_karOpp);
                            if (_karHint != 0) _karHint = 1;
                        }
                    }
                    else if (KarPressed(PadCross))
                    {
                        _karState = 9;
                        _karRows[_karTurn].S[_karSel].Angle = _karAim;
                        _karRows[_karTurn].S[_karSel].RotY = (short)KarVyOf(0);
                    }
                    break;
                case 9:
                    {
                        if (_karTurn == 0) KarStartAnim(0, 0x25);
                        else if (_karOpp == 2) KarStartAnim(_karOpp, 0x1E);
                        else KarStartAnim(_karOpp, 0x20);
                        var s = _karRows[_karTurn].S[_karSel];
                        s.Speed = (short)(_karPower + 0x258);
                        _karState = 0xA;
                        _karTimer = 0;
                        if (_karSlideVoice >= 0 && _audio != null) _audio.StopVoice(_karSlideVoice);
                        _karSlideVoice = _audio != null ? _audio.PlayVoice(8, 0) : -1;
                        _karCamTo = s.Pos;
                        break;
                    }
                case 0xA:
                    if (_karTimer++ >= 3)
                    {
                        _karTimer = 0;
                        var s = _karRows[_karTurn].S[_karSel];
                        s.State = 1;
                        _karCamTo = s.Pos;
                        _karState = 0xB;
                    }
                    break;
                case 0xB:
                    // the camera follows the stone
                    _karCamFrom = _karCamTo;
                    _karCamTo = _karRows[_karTurn].S[_karSel].Pos;
                    KarCamByDiff(_karCamFrom, _karCamTo);
                    break;
                case 0xC:
                    {
                        if (_karSlideVoice >= 0 && _audio != null) _audio.StopVoice(_karSlideVoice);     // thunkStopSoundMask
                        _karSlideVoice = -1;
                        KarClassifyStoneRings();
                        _karRows[_karTurn].Thrown++;
                        _karState = 0xD;
                        for (int i = 0; i < 2; i++)
                        {
                            _karRows[i].Thrown = 0;
                            for (int j = 0; j < 5; j++) if (_karRows[i].S[j].State != -1) _karRows[i].Thrown++;
                        }
                        break;
                    }
                case 0xD:
                    prompt = KarTickScoreTally();
                    if (prompt != 0) _karState = _karHint != 0 ? 0xE : 0xF;
                    break;
                case 0xE:
                    _karCamDone = KarCamMoveTo(0, -0x6A4, 0xA);
                    if (_karCamDone == 1) _karState = 0xF;
                    break;
                case 0xF:
                    if (KarHintBox(3) == 0)
                    {
                        KarSetLoc(0, -0x190, 0x578);
                        KarSetLoc(_karOpp, 0x190, 0x578);
                        KarStartAnim(0, 0); KarStartAnim(_karOpp, 0);
                        if (_karTurn == 0) _karLast.CopyFrom(_karRows[0].S[_karSel]);
                        if (_karHint != 0)
                        {
                            // the explanation is over: back to the boy, and a real match
                            var loc = KarLoc(player);
                            _karCamDone = KarCamMoveTo(loc.X, loc.Z, 0x14);
                            if (_karCamDone == 1)
                            {
                                KarSetVy(player, 0);
                                _karHint = 0;
                                _karState = 3;
                                KarSetupMatch(0);
                                _karTimer = 0;
                            }
                        }
                        else
                        {
                            // whose turn: the one who threw fewer; when level, the one behind (after 5 each, the opponent)
                            var r0 = _karRows[0]; var r1 = _karRows[1];
                            if (r0.Thrown == r1.Thrown)
                            {
                                if (r0.Thrown == 5) _karTurn = 1;
                                else if (r0.Score < r1.Score) _karTurn = 0;
                                else _karTurn = 1;
                            }
                            else if (r0.Thrown < r1.Thrown) _karTurn = 0;
                            else _karTurn = 1;
                            for (int j = 0; j < 5; j++) if (_karRows[_karTurn].S[j].State == -1) { _karSel = j; break; }
                            _karTimer = 0;
                            _karPower = 0;
                            _karState = 0x10;
                        }
                    }
                    break;
                case 0x10:
                    {
                        var loc = KarLoc(player);
                        _karCamDone = KarCamMoveTo(loc.X, loc.Z, 0x14);
                        if (_karCamDone == 1)
                        {
                            if (_karRows[0].Thrown >= 5 && _karRows[1].Thrown >= 5)
                            {
                                // the end: "Awh, I lost." (8, sound 7) or "Yeah! I won!" (7, sound 8)
                                _karHint = 1;
                                _karView.ClearText();
                                _karState = 0x13;
                                if (_karRows[0].Score > _karRows[1].Score) { KarSound(7); _karTimer = 8; }
                                else { KarSound(8); _karTimer = 7; }
                            }
                            else if (_karTurn != 0) _karState = 0x11;
                            else _karState = 5;
                        }
                        break;
                    }
                case 0x11:
                    // the opponent chooses, then says something (4 ahead of you / 5 behind / 6 level)
                    KarChooseOpponentShot();
                    _karState = 0x13;
                    _karHint = 1;
                    _karTimer = 0;
                    _karPower = 0;
                    if (_karRows[0].Score > _karRows[1].Score) _karTimer = 5;
                    else if (_karRows[0].Score == _karRows[1].Score) _karTimer = 6;
                    else _karTimer = 4;
                    break;
                case 0x12:
                    if (_karView.Box.State == 1) _karState = 0x13;
                    break;
                case 0x13:
                    if (KarHintBox((sbyte)_karTimer) == 0)
                    {
                        _karHint = 0;
                        if (_karTimer >= 7) KarFinishMatch();
                        else { _karState = 5; _karTimer = 0; _karPower = 0; }
                    }
                    break;
                case 0x14:
                    _karTimer = 0;
                    _karPower = 0;
                    KarFinishMatch();
                    break;
            }
        }

        // OnGUI
        void DrawCurling()
        {
            if (!_karOn || _karView == null) return;
            _karView.Hand = _karHandRt;
            _karView.Draw();
        }

        // ------------------------------------------------------------------ debug (F10): straight into FRZL17, whose arrival tile starts the match

        void TestCurling(int opponent)
        {
            if (_changing || _runner != null || _karOn || (_battle != null && _battle.Active)) { Say("Not now."); return; }
            int mi = _mapNames.IndexOf("FRZL17");
            if (mi < 0) { Say("FRZL17 not found."); return; }
            _state.Vars[0x7A] = (byte)opponent;
            _debugOpen = false;
            StartCoroutine(ChangeMap(MapCatalog.PathFor(_mapNames, mi), 1));      // as script 206 does: 4B 88 01 FF
        }
    }
}
