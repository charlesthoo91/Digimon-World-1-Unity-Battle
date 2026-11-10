using System.Collections;
using System.Collections.Generic;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The arena's world while a tournament runs (std/std_setup.c, std/std_main.c, std/std_hud.c and the undecompiled scene code of STD_REL.BIN):
    /// the ring (ArenaStage), the picture's own pieces (ArenaScreens), the arena camera (STD_tickVSPhase: STD_CAMERA_STATE 1 orbit, 3 / 5 a
    /// preset near a Digimon, 4 / 6 a random viewpoint, 8 / 11 a fixed viewpoint, 9 the intro chase, 10 turning slowly), the entrants'
    /// introductions (STD_runIntro), the knock-out camera (STD_startCameraIntro) and the champion's podium (STD_tickChampionScene).
    /// Everything ticks at the game's 20 frames a second (TickArena); the camera is set from that state every Unity frame (ArenaCameraLate).
    /// </summary>
    public sealed partial class PlayableRoom
    {
        ArenaStage _arena;
        ArenaScreens _arenaScr;
        bool _arenaOn;
        float _arenaAcc; int _arenaFrame;
        float _savedNear, _savedFar;

        sealed class ArenaFighter { public Transform T; public DigimonAnimator A; public int Type, Height, Radius; public bool IsPartner; }
        ArenaFighter _p1, _p2;                     // ENTITY_TABLE[1] (the partner) and STD_OPPONENT_ENTITY

        // STDVS_CAMERA, GS_VIEWPOINT, VIEWPORT_DISTANCE and the camera's state
        int _camState, _camTimer, _viewDist = 500, _minDist = 1000, _fightFrames;
        readonly int[] _camRot = new int[3], _camTrans = new int[3];
        Vector3 _vp, _vr;
        ArenaFighter _focus; int _focusRange;

        // ---------------------------------------------------------------- on / off

        // STD_initializeTournament: the map's Digimon and the boy go (removeMapEntities, isOnScreen 0), the arena loads, STD_initializeCamera
        void ArenaOn(int arena, int cup)
        {
            _arena = ArenaStage.Load(_disc, StdBin, ExeBytes, arena, cup, transform);
            _arenaScr = ArenaScreens.Load(_disc, Battle.StatFont, StdBin, _arena.Vram);
            _arenaScr.Sound = (bank, n) => { if (_audio != null) _audio.Play(bank, n); };
            _arenaScr.TitleShort = _mapId == 0x6A;
            _arenaOn = true; _arenaAcc = 0f;
            HideRoom(true);
            _savedNear = _cam.nearClipPlane; _savedFar = _cam.farClipPlane;
            _cam.nearClipPlane = 0.1f; _cam.farClipPlane = 300f;
            InitializeArenaCamera();
        }

        // STD_deinitializeTournament: the arena goes, the partner stands on the ground again, the boy is back
        void ArenaOff()
        {
            _arenaOn = false;
            HideRoom(false);
            if (_arena != null) _arena.Destroy();
            if (_arenaScr != null) _arenaScr.Destroy();
            _arena = null; _arenaScr = null;
            _cam.nearClipPlane = _savedNear; _cam.farClipPlane = _savedFar;
            _calibrated = false;        // the map camera's lens is set again (ApplyScroll)
        }

        // the map's own pieces (the painted backdrop, its foreground pieces, doors and objects, piles, the debug grid) are not in the arena
        void HideRoom(bool hide)
        {
            if (_backdrop != null) _backdrop.SetActive(!hide);
            if (_overlayRoot != null) _overlayRoot.SetActive(!hide && ShowOverlays);
            if (_objRoot != null) _objRoot.SetActive(!hide);
            if (_gridOverlay != null) _gridOverlay.SetActive(!hide);
            foreach (var o in _pileObjs) if (o != null) o.gameObject.SetActive(!hide);
        }

        /// <summary>STD_initializeCamera: projection 500, the eye at (0, 500, 3000) turned 100 about X (RotMatrix, no super coordinate).</summary>
        void InitializeArenaCamera()
        {
            _viewDist = 500;
            _camRot[0] = 100; _camRot[1] = 0; _camRot[2] = 0;
            _camTrans[0] = 0; _camTrans[1] = 500; _camTrans[2] = 3000;
        }

        // one game frame of everything the arena's objects tick (STD_battleTickFrame)
        void TickArena()
        {
            if (!_arenaOn) return;
            _arenaAcc += Time.deltaTime;
            while (_arenaAcc >= 0.05f)
            {
                _arenaAcc -= 0.05f;
                _arenaFrame++;
                ArenaFrame();
            }
        }

        IEnumerator ArenaFrames(int n) { int until = _arenaFrame + n; while (_arenaFrame < until) yield return null; }

        void ArenaFrame()
        {
            var s = _arenaScr;
            foreach (var o in _pileObjs) if (o != null && o.gameObject.activeSelf) o.gameObject.SetActive(false);
            if (s.IntroOn) s.TickIntro();
            if (s.BracketOn) s.TickBracket();
            if (s.StatsOn) { s.TickStats(); if (s.Counting) Snd(0x16); }
            if (s.TextStage != 0) s.TickText();
            if (_introNameOn) TickIntroName();
            if (_chaseOn) TickCameraChase();
            if (_introCamOn) TickCameraIntro();
            if (_camState == 10) _camRot[1] = (_camRot[1] + 2) & 0xFFF;        // STD_applyRotatingCamera
            switch (_arena.Scene)
            {
                case 1: _arena.TickWin(); break;
                case 2: _arena.TickLose(); break;
                case 3: _arena.TickVersus(); if (_arena.VersusFinished) _arena.ShowScene(0); break;       // STD_removeVersusModelScene once it is over
                case 4: TickChampion(); break;
            }
            if (_battle != null && _battle.Active && _battle.Tournament) FightFrame();
            SyncArenaHud();
            s.TickHud();
        }

        // STD_tickBattle's camera part: a timed viewpoint runs out to the orbit; every 600th frame half the time the fixed viewpoint 4 for 60-100
        // frames; while nobody attacks or is thrown back (and no timer runs) the camera goes back to its orbit
        void FightFrame()
        {
            if (!_battle.InFight) return;
            _fightFrames++;
            if (_camState != 7 && _camState != 8)
            {
                if (_camTimer != 0) { _camTimer--; if (_camTimer == 0) _camState = 1; }
                if (_fightFrames % 600 == 0 && Random.Range(0, 2) == 1) { _camState = 6; SetRandomViewpoint(_p1, 4); _camTimer = Random.Range(0, 0x29) + 0x3C; }
            }
            if (!_battle.Acting && _camTimer == 0 && _camState != 7 && _camState != 8) _camState = 1;
        }

        void SyncArenaHud()
        {
            var s = _arenaScr;
            if (_battle == null || !_battle.Active || !_battle.Tournament || _battle.Partner == null) { s.HudOn = false; s.MarkersOn = false; return; }
            var b = _battle;
            s.Hp[0] = Mathf.RoundToInt(b.Partner.ShownHp); s.MaxHp[0] = b.Partner.MaxHp; s.Mp[0] = b.Partner.Mp; s.MaxMp[0] = b.Partner.MaxMp;
            s.Hp[1] = Mathf.RoundToInt(b.Enemy.ShownHp); s.MaxHp[1] = b.Enemy.MaxHp; s.Mp[1] = b.Enemy.Mp; s.MaxMp[1] = b.Enemy.MaxMp;
            s.Finish = b.Partner.Finish; s.FinishMax = b.Partner.FinishMax;
            s.Commands = b.TourCommands; s.CommandElement = b.TourCommandElement; s.Hover = b.Hover;
            s.Fighting = b.InFight; s.FinisherOrdered = b.FinisherOrdered; s.Charging = b.PartnerCharging; s.ChargeCount = b.ChargeCount;
            s.MoveName = CommandName(b);
        }

        // STD_renderMoveName: a technique's MOVE_NAMES entry, else STD_COMMAND_NAMES[cmd - 1]
        string CommandName(Battle b)
        {
            if (b.TourCommands.Length == 0 || b.Hover < 0 || b.Hover >= b.TourCommands.Length) return "";
            int cmd = b.TourCommands[b.Hover];
            if (cmd >= 8 && cmd < 0xC)
            {
                int k = cmd - 8;
                return k < b.Partner.Techs.Count ? b.Partner.Techs[k].Name : "";
            }
            uint p = System.BitConverter.ToUInt32(StdBin, StdAt(0x8007A688) + 4 * Mathf.Clamp(cmd - 1, 0, 7));
            return p >= StdBase && p < StdBase + (uint)StdBin.Length ? StdString(p) : Exe(p);
        }

        void DrawArena()
        {
            if (!_arenaOn || _arenaScr == null) return;
            _arenaScr.Wipe = _arena.Scene == 2 ? _arena.WipeHeights : null;
            _arenaScr.ModelScene = _arena.Scene != 0 ? _arena.OverlayRt : null;
            if (_arena.Overlay != null) _arena.Overlay.fieldOfView = 2f * Mathf.Atan(120f / _arena.OverlayH) * Mathf.Rad2Deg;
            _arenaScr.Draw();
        }

        // ---------------------------------------------------------------- the fighters

        ArenaFighter Fighter(Transform t, DigimonAnimator a, int type, bool partner)
        {
            var info = DigimonStatsCatalog.Get(_disc, type);
            return new ArenaFighter { T = t, A = a, Type = type, Height = info != null ? info.Height : 0, Radius = info != null ? info.Radius : 0, IsPartner = partner };
        }

        static Vector3 Loc(ArenaFighter f) { return ArenaStage.ToGame(f.T.position); }

        // posData->rotation.vy (the model's front is -Z; FacingOffset as FaceRot)
        int Vy(ArenaFighter f)
        {
            Vector3 fw = f.T.rotation * Quaternion.Inverse(Quaternion.Euler(0, FacingOffset, 0)) * Vector3.forward;
            return ((int)(Mathf.Atan2(fw.x, fw.z) * 4096f / (Mathf.PI * 2f))) & 0xFFF;
        }

        void SetPlace(ArenaFighter f, int x, int y, int z, int vy)
        {
            f.T.position = ArenaStage.ToUnity(new Vector3(x, y, z));
            f.T.rotation = Quaternion.Euler(0, vy * 360f / 4096f, 0) * Quaternion.Euler(0, FacingOffset, 0);
        }

        static void Play(ArenaFighter f, int slot, bool once = false) { if (f.A != null && f.A.HasSlot(slot)) f.A.SetSlot(slot, once); }

        // calculateBoneMatrix(entity, bone): the bone's world place and turn in game space (our bones keep the game's turns with Y flipped)
        void Bone(ArenaFighter f, int bone, out Matrix4x4 r, out Vector3 t)
        {
            var n = f.A != null ? f.A.Node(bone) : null;
            t = ArenaStage.ToGame(n != null ? n.position : f.T.position);
            Matrix4x4 pose = Matrix4x4.identity;
            if (n != null)
            {
                Matrix4x4 u = Matrix4x4.Rotate(Quaternion.Inverse(f.T.rotation) * n.rotation), fl = Matrix4x4.Scale(new Vector3(1, -1, 1));
                pose = fl * u * fl;
            }
            r = ArenaStage.RotMatrix(0, Vy(f), 0) * pose;
        }

        // _atan(a, b) = 0xC00 - ratan2(a, b) (4096 a turn)
        static int Atan(float a, float b) { return 0xC00 - Mathf.RoundToInt(Mathf.Atan2(a, b) * 4096f / (Mathf.PI * 2f)); }
        static int Lerp(int a, int b, int t0, int t1, int t) { return EffectPlayer.PsLerp(a, b, t0, t1, t); }

        // ---------------------------------------------------------------- the camera, every Unity frame (STD_tickVSPhase)

        void ArenaCameraLate()
        {
            var cam = _cam;
            switch (_camState)
            {
                case 1: CameraOrbit(cam); break;
                case 2:
                {
                    // STD_setCameraYXZ: straight down from 8000 over the middle between them
                    Vector3 a = Loc(_p1), b = Loc(_p2);
                    var c = ArenaStage.RotMatrixYXZ(-0x400, 0, 0);
                    ArenaStage.Frame(cam, c, new Vector3(a.x + (b.x - a.x) / 2f, -0x1F40, a.z + (b.z - a.z) / 2f), _viewDist);
                    break;
                }
                case 3: case 5:
                {
                    // STD_setCameraToEntity: the preset turned with the Digimon and placed relative to it
                    if (_focus == null) goto default;
                    var r = ArenaStage.RotMatrix(_camRot[0], (_camRot[1] - Vy(_focus)) & 0xFFF, _camRot[2]);
                    Vector3 p = Loc(_focus), rp = r.MultiplyVector(p);
                    ArenaStage.View(cam, r, new Vector3(_camTrans[0] - rp.x, _camTrans[1] - rp.y, _camTrans[2] - rp.z), _viewDist);
                    break;
                }
                case 4: case 6:
                {
                    // STD_setViewpointRotationFromEntity: looking at the Digimon's bone 1
                    if (_focus != null) { Matrix4x4 r; Vector3 t; Bone(_focus, 1, out r, out t); _vr = t; }
                    ArenaStage.LookAt(cam, _vp, _vr, _viewDist);
                    break;
                }
                case 7:
                {
                    // STD_setCameraLookAtEntity
                    if (_focus == null) goto default;
                    var other = _focus == _p1 ? _p2 : _p1;
                    Vector3 sp = Loc(_focus), op = Loc(other);
                    _camRot[1] = (-Atan(op.z - sp.z, op.x - sp.x) + 0x800) & 0xFFF;
                    var r = ArenaStage.RotMatrix(_camRot[0], _camRot[1], _camRot[2]);
                    Vector3 rp = r.MultiplyVector(sp);
                    ArenaStage.View(cam, r, new Vector3(_camTrans[0] - rp.x, _camTrans[1] - rp.y, _camTrans[2] - rp.z), _viewDist);
                    break;
                }
                case 9:
                {
                    // STD_applyEntityViewpoint: projection 0x15E, looking at the Digimon's place at two thirds of its height
                    _viewDist = 0x15E;
                    if (_focus != null) { Vector3 p = Loc(_focus); _vr = new Vector3(p.x, -_focus.Height * 2 / 3, p.z); }
                    ArenaStage.LookAt(cam, _vp, _vr, _viewDist);
                    break;
                }
                case 8: case 11: ArenaStage.LookAt(cam, _vp, _vr, _viewDist); break;
                default:
                    ArenaStage.View(cam, ArenaStage.RotMatrix(_camRot[0], _camRot[1], _camRot[2]), new Vector3(_camTrans[0], _camTrans[1], _camTrans[2]), _viewDist);
                    break;
            }
            if (_arenaScr != null) Markers();
        }

        // STD_setCameraOrbit: beside the middle of the two at a distance from theirs (x VIEWPORT_DISTANCE / 200, at least
        // STD_DEFAULT_CAM_MIN_DISTANCE, at most 0x1068), 1000 up, looking across (RotMatrixYXZ, super = the eye's coordinate)
        void CameraOrbit(Camera cam)
        {
            if (_p1 == null || _p2 == null) return;
            Vector3 a = Loc(_p2), b = Loc(_p1);
            float dx = a.x - b.x, dz = a.z - b.z;
            int dist = Mathf.Max(1, (int)Mathf.Sqrt(dx * dx + dz * dz));
            int d = (int)((uint)(dist * _viewDist) / 200u);
            if (d < _minDist) d = _minDist;
            if (d > 0x1068) d = 0x1068;
            int ang = 0x1000 - Atan(dx, dz);
            var t = new Vector3(b.x + (int)dx / 2 + d * dz / dist, -0x3E8, b.z + (int)dz / 2 - d * dx / dist);
            var c = ArenaStage.RotMatrixYXZ(Atan(d, -0x2BC) + 0x800, ang + 0x800, 0);
            ArenaStage.Frame(cam, c, t, _viewDist);
        }

        // STD_renderPlayerMarker: the two fighters are the same kind of Digimon (STD_initializePlayerMarker). Not in an online match (ours: the
        // friend is not "COM")
        void Markers()
        {
            var s = _arenaScr;
            s.MarkersOn = _battle != null && _battle.Active && _battle.Tournament && !_onlineMatch && _p1 != null && _p2 != null && _p1.Type == _p2.Type;
            if (!s.MarkersOn) return;
            var fs = new[] { _p1, _p2 };
            for (int id = 0; id < 2; id++)
            {
                Matrix4x4 r; Vector3 t; Bone(fs[id], 1, out r, out t);
                Vector3 w = ArenaStage.ToUnity(t), v = _cam.WorldToViewportPoint(w);
                float depth = Mathf.Max(1f, v.z * 100f);
                float off = _viewDist * (fs[id].Radius / 2) / depth;
                s.MarkerAt[id] = new Vector2((v.x - 0.5f) * 320f + off, (0.5f - v.y) * 240f);
            }
        }

        // ---------------------------------------------------------------- viewpoints

        // STD_setViewpointFromBone: the eye 'dist' away along RotMatrixZYX(rot) from the bone's place plus offset (its height from bone 1); projection 0x15E
        void ViewpointFromBone(ArenaFighter f, Vector3 offset, int rx, int ry, int rz, int dist)
        {
            Matrix4x4 r0; Vector3 t0; Bone(f, 0, out r0, out t0);
            Vector3 o = r0.MultiplyVector(offset);
            _vr = new Vector3(t0.x + Mathf.Floor(o.x), t0.y + Mathf.Floor(o.y), t0.z + Mathf.Floor(o.z));
            Matrix4x4 r1; Vector3 t1; Bone(f, 1, out r1, out t1);
            _vr.y = t1.y;
            Vector3 e = EffectPlayer.RotateZYX(rx & 0xFFF, ry & 0xFFF, rz & 0xFFF, new Vector3(0, 0, dist));
            _vp = new Vector3(Mathf.Floor(e.x), Mathf.Floor(e.y), Mathf.Floor(e.z)) + _vr;
            _viewDist = 0x15E;
        }

        int PresetS16(int i, int k) { return System.BitConverter.ToInt16(StdBin, StdAt(0x8007A390) + 12 * i + 2 * k); }
        int RandomViewS16(int i, int k) { return System.BitConverter.ToInt16(StdBin, StdAt(0x8007A3FC) + 6 * i + 2 * k); }

        // STD_selectRandomCamera(entity, mode, sub): a third of the time (always for mode 5) a preset near it (STD_CAMERA_PRESETS[mode], or one of
        // 6-8 for a wide technique); mode 5 is the knock-out: the WIN scene (the opponent fell) or the LOSE scene, and STD_startCameraIntro
        void SelectRandomCamera(ArenaFighter e, int mode, int sub)
        {
            if (mode != 5 && Random.Range(0, 3) != 0) return;
            _focus = e;
            int p = sub != 3 ? mode : Random.Range(0, 3) + 6;
            for (int k = 0; k < 3; k++) { _camRot[k] = PresetS16(p, k); _camTrans[k] = PresetS16(p, 3 + k); }
            if (mode < 5) { _camState = 3; return; }
            if (e != _p1) _arena.AddWin(); else _arena.AddLose(new System.Random());
            _focus = _p1;
            StartCameraIntro(_p1, _p2);
        }

        // STD_setRandomViewpoint(entity, idx): a third of the time (not in state 7), projection 500; idx 0-3 an eye placed by the Digimon's turn
        // (STD_RANDOM_VIEWPOINTS[idx], its height as it is), 4 the fixed eye STD_RANDOM_VIEWPOINTS[4]; it looks at the Digimon's bone 1
        void SetRandomViewpoint(ArenaFighter e, int idx)
        {
            if (Random.Range(0, 3) != 0) return;
            if (_camState == 7) return;
            _viewDist = 0x1F4;
            if (idx < 4)
            {
                _focus = e; _camState = 4;
                Vector3 o = ArenaStage.RotMatrix(0, Vy(e), 0).MultiplyVector(new Vector3(RandomViewS16(idx, 0), RandomViewS16(idx, 1), RandomViewS16(idx, 2)));
                Vector3 p = Loc(e);
                _vp = new Vector3(Mathf.Floor(o.x) + p.x, Mathf.Floor(o.y), Mathf.Floor(o.z) + p.z);
            }
            else { _camState = 6; _vp = new Vector3(RandomViewS16(idx, 0), RandomViewS16(idx, 1), RandomViewS16(idx, 2)); }
        }

        // STD_setupMoveExecution's end: not while a wide technique holds a preset (state 3) nor in state 6; short and self techniques pick
        // among presets 0-1, the others among 0-4
        void OnTechCamera(Battle.Fighter f, int tech)
        {
            var td = DigimonStatsCatalog.TechOf(tech);
            int range = td != null ? td.Range : 1;
            if (_camState == 3 && _focusRange == 3) return;
            if (_camState == 6) return;
            int n = range == 1 || range == 4 ? 2 : 5;
            var who = f == _battle.Partner ? _p1 : _p2;
            int before = _camState;
            SelectRandomCamera(who, Random.Range(0, n), range);
            if (_camState == 3 && before != 3 || _focus == who) _focusRange = range;
        }

        // ---------------------------------------------------------------- the introductions (STD_runIntro)

        // STD_placePlayer1 / 2: 0x9C4 (Beetle Land 0x708) either side of the middle, facing each other, standing (0x21)
        void PlacePlayers(int which)
        {
            int x = which == 1 ? 0x708 : 0x9C4;
            SetPlace(_p1, x, 0, 0, 0x400); Play(_p1, 0x21);
            SetPlace(_p2, -x, 0, 0, 0xC00); Play(_p2, 0x21);
        }

        // STD_tickCameraChase / STD_startCameraChase state
        bool _chaseOn; int _chaseTimer, _chasePhase, _chaseSide; Vector3 _chaseLast, _chaseTarget; bool _chaseTech;

        // STD_startCameraChase(entity, dx, side): its goal dx along -X, the eye beside it (side 0 left, 1 right) at its bone (state 9)
        void StartCameraChase(ArenaFighter e, int dx, int side)
        {
            _focus = e;
            _chaseLast = Loc(e);
            _chaseTarget = new Vector3(_chaseLast.x - dx, _chaseLast.y, _chaseLast.z);
            Play(e, 0x21);
            _camState = 9;
            _chaseTimer = 0; _chasePhase = 0; _chaseSide = side; _chaseOn = true; _chaseTech = false;
            if (e.A != null) { e.A.BodyMovesItself = true; e.A.TakeBodyMove(); }
            int ry = (side == 0 ? -0x638 : 0x638) + Vy(e);
            int offY = -e.Height * 2 / 3;
            int dist = ((e.Height + e.Radius) / 2) * 0x5AA / 450;
            ViewpointFromBone(e, new Vector3(0, offY, 0), 0, ry, 0, dist);
            _vp.y = offY * 3 / 10;
            if (_vp.y > -0xB4) _vp.y = -0xB4;
        }

        // STD_getFighterDistance: how far it has come, in hundredths of the way
        static int Progress(Vector3 last, Vector3 target, Vector3 now)
        {
            float toTarget = (last - now).magnitude, toOther = (target - last).magnitude;
            return toOther > 0f ? (int)(toTarget * 100f / toOther) : 100;
        }

        void TickCameraChase()
        {
            var e = _focus;
            if (e == null) return;
            MoveByAnimation(e);
            if (_chaseTimer < 0x14) return;
            if (_chaseTimer == 0x14) Play(e, 0x23);
            if (_chasePhase == 0)
            {
                int dist = Progress(_chaseLast, _chaseTarget, Loc(e));
                if (dist >= 0x23) { _chaseLast = Loc(e); _chasePhase = 1; _camState = 8; }
                else
                {
                    int ry = (_chaseSide == 0 ? Lerp(-0x638, -0x293, 0, 0x23, dist) : Lerp(0x638, 0x293, 0, 0x23, dist)) + Vy(e);
                    int offY = -e.Height * 7 / 10;
                    int d2 = ((e.Height + e.Radius) / 2) * 0x5AA / 450;
                    ViewpointFromBone(e, new Vector3(0, offY, 0), 0, ry, 0, d2);
                    _vp.y = offY * 7 / 10;
                    if (_vp.y > -0xB4) _vp.y = -0xB4;
                    _chaseTimer++;
                    return;
                }
            }
            int t = Progress(_chaseLast, _chaseTarget, Loc(e));
            CameraLerp(e, t, _chaseSide);
            if (NearTarget(e, _chaseTarget))
            {
                // its first technique's animation, once (animFlag | 2); the intro ends when it is over
                int slot = -1;
                if (e.IsPartner) { for (int i = 0; i < 3 && slot < 0; i++) slot = PartnerMoveAnim(i); }
                else if (_tourFoe != null) for (int i = 0; i < 3 && slot < 0; i++) if (_tourFoe.Data.MoveSlots[i] != 0xFF) slot = _tourFoe.Data.MoveSlots[i];
                if (slot >= 0 && e.A != null && e.A.HasSlot(slot)) { e.A.SetSlot(slot, true); _chaseTech = true; }
                else _chaseTech = true;
                _chaseTimer = -1;
                return;
            }
            _chaseTimer++;
        }

        // the partner's stats.base.moves[i]: its Techset slot as an animation number (0x2E + its place in the Digimon's move list), 0xFF none
        int PartnerMoveAnim(int i)
        {
            var info = DigimonStatsCatalog.Get(_disc, PartnerType);
            if (info == null) return -1;
            var set = _state.TechSet;
            if (set != null && i < set.Count && set[i] >= 0) return info.MoveSlot(set[i]);
            if (set == null || !set.Exists(x => x >= 0))
            {
                // no Techset: the fight takes the first mastered techniques (Battle.SetupTechs), so does this
                int n = 0;
                foreach (int t in info.Learnable) if (_state.Techs[t]) { if (n == i) return info.MoveSlot(t); n++; }
            }
            return -1;
        }

        static bool NearTarget(ArenaFighter e, Vector3 p)
        {
            Vector3 l = Loc(e);
            return !(p.x - 50 > l.x || l.x > p.x + 50 || p.z - 50 > l.z || l.z > p.z + 50);
        }

        // the run's forward travel moves the Digimon for real (its animation's bone 0, entityMoveForward); the model then does not show it
        void MoveByAnimation(ArenaFighter e)
        {
            if (e.A == null) return;
            float m = e.A.TakeBodyMove();
            Vector3 fwd = e.T.rotation * Quaternion.Inverse(Quaternion.Euler(0, FacingOffset, 0)) * Vector3.back;
            fwd.y = 0f;
            if (fwd.sqrMagnitude > 1e-6f) e.T.position += fwd.normalized * m;
        }

        // STD_updateCameraLerp(t, flip): from beside its front to its other side as it comes (t = hundredths of the way)
        void CameraLerp(ArenaFighter e, int t, int flip)
        {
            int b = ((e.Height + e.Radius) / 2) * 0x62C / 450, dbl = b * 2;
            int rx = Lerp(0x2AA, 0xE3, 0, 0x64, t);
            int ry = Lerp(0x5C7, 0xA38, 0, 0x64, t);
            int dist = ry < 0x801 ? Lerp(b, dbl, 0x5C7, 0x800, ry) : Lerp(dbl, b, 0x800, 0xA38, ry);
            if (flip != 0) ry = -ry;
            ry += Vy(e);
            ViewpointFromBone(e, new Vector3(0, -e.Height * 2 / 3, 0), rx, ry, 0, dist);
        }

        // STD_setPostIntroPosition: Cross: straight to its goal, standing
        void SkipIntro(ArenaFighter e)
        {
            if (_chaseTimer == -1) return;
            SetPlace(e, (int)_chaseTarget.x, (int)_chaseTarget.y, (int)_chaseTarget.z, Vy(e));
            Play(e, 0x21);
            _chaseTimer = -1;
        }

        // STD_addIntroText / STD_tickIntroName / STD_renderIntroName: the name's letters come one every 4 frames (the US build draws none of them);
        // once all are there the Digimon starts running (0x23, the chase timer to 20), the name rises 28 a frame, then the numbers come
        bool _introNameOn; int _nameFrames, _nameShown, _nameAnim, _nameY, _nameLen; ArenaFighter _nameOf;

        void AddIntroText(ArenaFighter e)
        {
            _nameFrames = 4; _nameShown = 0; _nameY = 68; _nameOf = e; _introNameOn = true;
            var info = DigimonStatsCatalog.Get(_disc, e.Type);
            _nameLen = (info != null ? info.Name.Length : 0) / 2;
            if (e.Type == 0x4E || e.Type == 0x3C) _nameLen = 10;
        }

        void TickIntroName()
        {
            _nameFrames++;
            if (_nameLen == _nameShown && _nameAnim == 3)
            {
                if (_chaseTimer == 0) { Play(_nameOf, 0x23); _chaseTimer = 20; }
                if (_nameY >= -71) _nameY -= 28;
                else AddIntroStats(_nameOf);
            }
            if (_nameFrames % 4 == 0) { if (_nameShown < _nameLen) { _nameShown++; _nameAnim = 0; } }
            else if (_nameAnim != 3) _nameAnim++;
        }

        void AddIntroStats(ArenaFighter e)
        {
            if (_arenaScr.StatsOn) return;
            int[] v;
            if (e.IsPartner) v = new int[] { _state.Stats[6], _state.Stats[7], _state.Stats[0], _state.Stats[1], _state.Stats[2], _state.Stats[3] };
            else { var d = _tourFoe.Data; v = new[] { d.Hp, d.Mp, d.Off, d.Def, d.Spd, d.Brain }; }
            _arenaScr.AddStats(v);
        }

        Npc _tourFoe;

        // an exe string with its full-width (two-byte) letters (STD_STR_HP "ＨＰ")
        string ExeSjis(uint a)
        {
            var sb = new System.Text.StringBuilder();
            byte[] b = ExeBytes;
            for (int p = ExeAt(a); p < b.Length && b[p] != 0; p++)
            {
                if (b[p] >= 0x80 && p + 1 < b.Length && b[p + 1] != 0) { sb.Append(MapScripts.Pair(b[p] << 8 | b[p + 1])); p++; }
                else sb.Append((char)b[p]);
            }
            return sb.ToString();
        }

        // STD_runIntro(which): the partner, then the opponent: each runs in under its own camera to its music (STD_MUSIC tracks 0 and 1) and shows
        // its numbers; Cross skips to its place. Ours: the opponent's voices are not loaded separately (readVBALLSection)
        IEnumerator RunIntro(int which, int music)
        {
            int dist = which == 1 ? 0x514 : 0x7D0;
            PlacePlayers(which);
            // STD_drawStatLabelText: HP, MP and four stat names on the text page
            _arenaScr.StatLabel = new[] { ExeSjis(0x80134808), ExeSjis(0x80134810), ExeSjis(0x80124C0C + 2 * 12), ExeSjis(0x80124C0C + 3 * 12), ExeSjis(0x80124C0C + 4 * 12), ExeSjis(0x80124C0C + 5 * 12) };
            foreach (var e in new[] { _p1, _p2 })
            {
                StartCameraChase(e, e == _p1 ? dist : -dist, e == _p1 ? 0 : 1);
                AddIntroText(e);
                if (_audio != null) { _audio.StopMusic(); _audio.StopSound(); _audio.PlayMusic(music, e == _p1 ? 0 : 1); }
                while (!(_chaseTech && _chaseTimer == -1 && (e.A == null || !e.A.Busy)))
                {
                    if (GameMenus.CrossPressed()) { SkipIntro(e); break; }
                    yield return null;
                }
                _introNameOn = false; _arenaScr.RemoveStats(); _chaseOn = false;
                if (e.A != null) e.A.TakeBodyMove();
                if (_audio != null) { _audio.StopMusic(); _audio.StopSound(); }
            }
        }

        // ---------------------------------------------------------------- the knock-out camera (STD_startCameraIntro / STD_tickCameraIntro)

        bool _introCamOn; int _icTimer, _icAngle, _icDist, _icRadius6;
        ArenaFighter _icTarget, _icEntity;
        readonly int[] _icXs = new int[22], _icDists = new int[22], _icHeights = new int[22];

        // around the partner: the angle from the opponent, distances and heights at set times (STD_D_8007A41C) with random ones after the fifth,
        // read off a line through those points (processSomeArenaArrays with all curvatures 0)
        void StartCameraIntro(ArenaFighter target, ArenaFighter entity)
        {
            _icTimer = 0; _icEntity = entity; _icTarget = target;
            _camState = 8; _introCamOn = true;
            Vector3 dt = Loc(entity) - Loc(target);
            _icAngle = (-Atan(dt.z, dt.x) + 0x7DE) & 0xFFF;
            int d = (int)Mathf.Sqrt(dt.x * dt.x + dt.z * dt.z) + 0x2BC;
            _icDist = Mathf.Max(d, 0x5DC);
            ViewpointFromBone(target, Vector3.zero, 0, _icAngle, 0, _icDist);
            _icRadius6 = target.Radius * 3 * 2;
            _vr.y = -target.Height * 2 / 3;
            _vp.y = -entity.Height;
            for (int i = 0; i < 22; i++) _icXs[i] = System.BitConverter.ToInt32(StdBin, StdAt(0x8007A41C) + 4 * i);
            _icHeights[0] = entity.Height; _icHeights[1] = entity.Height; _icHeights[2] = entity.Height * 200 / 100;
            _icHeights[3] = target.Height * 380 / 100; _icHeights[4] = target.Height * 250 / 100;
            for (int i = 5; i < 22; i++) _icHeights[i] = Random.Range(0x50, Mathf.Max(0x50, target.Height * 180 / 100) + 1);
            _icDists[0] = d; _icDists[1] = d;
            if (d < 0x5DC) d = 0x5DC;
            _icDists[2] = d * 120 / 100; _icDists[3] = d * 80 / 100; _icDists[4] = _icRadius6;
            int lo = _icRadius6 * 45 * 2 / 100, hi = _icRadius6 * 45 * 4 / 100;
            for (int i = 5; i < 22; i++) _icDists[i] = Random.Range(Mathf.Min(lo, hi), Mathf.Max(lo, hi) + 1);
        }

        void TickCameraIntro()
        {
            if (_icTimer >= 0x1E) _icAngle += ClampLerp(0x1E, 0x3C, _icTimer, 0, 0x5B);
            _icDist = Spline(_icXs, _icDists, _icTimer);
            ViewpointFromBone(_icTarget, Vector3.zero, 0, _icAngle, 0, _icDist);
            _vr.y = -_icTarget.Height * 2 / 3;
            _vp.y = -Spline(_icXs, _icHeights, _icTimer);
            _icTimer++;
        }

        void RemoveCameraIntro() { _introCamOn = false; _icTimer = -1; }

        // STD_interpolateClamped2
        static int ClampLerp(int lo, int hi, int t, int start, int end)
        {
            if (hi < lo) { int x = lo; lo = hi; hi = x; }
            t = t < lo ? lo : (hi < t ? hi : t);
            if (lo >= t) return start;
            return start + (end - start) * (t - lo) / (hi - lo);
        }

        // processSomeArenaArrays(22, x, xs, ys, y2) with y2 all 0 (initializeSomeArenaArrays): the point before x, plus (x - its x) x the whole-number slope
        static int Spline(int[] xs, int[] ys, int x)
        {
            int klo = 0, khi = xs.Length - 1;
            while (klo < khi) { int k = (klo + khi) / 2; if (xs[k] < x) klo = k + 1; else khi = k; }
            if (klo > 0) klo--;
            if (klo + 1 >= xs.Length) klo = xs.Length - 2;
            int h = xs[klo + 1] - xs[klo];
            if (h == 0) return ys[klo];
            return ys[klo] + (x - xs[klo]) * ((ys[klo + 1] - ys[klo]) / h);
        }

        // ---------------------------------------------------------------- the champion (STD_initializeChampionScene, STD_setChampionCamera, STD_tickChampionScene)

        void InitializeChampionScene()
        {
            _arena.ChampState = 0;
            Play(_p1, 0x21);
            SetPlace(_p1, 0, -0x1E, 0, 0x200);
            // STD_setChampionCamera: state 11, from (0, -200, -(radius x 2 + 800)) at two thirds of its height, projection 0x200
            _camState = 11;
            _vp = new Vector3(0, -0xC8, -(_p1.Radius * 2 + 0x320));
            _vr = new Vector3(0, -_p1.Height * 2 / 3, 0);
            _viewDist = 0x200;
            _arena.InitChampionPictures();
        }

        // state 1: the letters sink, the partner and the podium rise 25 a frame while the eye circles it (STD_CHAMPION_ORBIT_ANGLES) for 79
        // frames; then it cheers (0x2A once); state 2: when the cheer is over the eye rises (STD_CHAMPION_CAMERA_Y x 2) and the projection grows
        // 20 a frame for 7 frames; done after 21 frames
        void TickChampion()
        {
            var a = _arena;
            switch (a.ChampState)
            {
                case 0:
                    if (a.TickChampionLetters()) { a.ChampState = 1; a.Timer = 0; Play(_p1, 0x21); }
                    break;
                case 1:
                {
                    a.Timer++;
                    a.SinkChampionLetters();
                    _p1.T.position += new Vector3(0f, 0x19 * 0.01f, 0f);
                    if (a.Podium != null) { a.Podium.Loc[1] -= 0x19; a.Place(a.Podium); }
                    if (a.Timer < 0x50)
                    {
                        int rot = a.OrbitAngle(a.Timer - 1) & 0xFFF;
                        _vr.y = Loc(_p1).y - (_p1.Height * 2) / 3;
                        Vector3 o = ArenaStage.RotMatrix(0, rot, 0).MultiplyVector(new Vector3(0, 0, -(_p1.Radius * 2 + 0x320)));
                        _vp.x = Mathf.Floor(o.x); _vp.z = Mathf.Floor(o.z);
                    }
                    else { Play(_p1, 0x2A, true); a.Timer = 0; a.ChampState = 2; }
                    break;
                }
                case 2:
                    if (_p1.A == null || !_p1.A.Busy)
                    {
                        if (_vp.y >= -0x7CF) _vp.y = a.ChampCamY(a.Timer) * 2;
                        a.Timer++;
                        if (a.Timer < 8) _viewDist += 0x14;
                    }
                    if (a.Timer >= 0x15) a.ChampDone = true;
                    break;
            }
        }
    }
}
