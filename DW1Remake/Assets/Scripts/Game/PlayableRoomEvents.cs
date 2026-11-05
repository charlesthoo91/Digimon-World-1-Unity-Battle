using System.Collections;
using System.Collections.Generic;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The story-event built-ins of script command 0x64 (scriptInstruction64to7E, main/script_interp.c): 0x13/0x14/0x1A/0x1B the dirt cart and
    /// dirt pile (map_object.c), 0x1C the Monochromon mood bubble (script_common.c), 0x22 the Meramon shake (map.c), 0x25 removing the mist
    /// and the mist itself (renderMist / tickRemoveMist, overworld.c), 0x26 the Ninjamon effect (overworld.c), 0x2F losing techniques when
    /// every life is lost (lostAllLives, script_common.c), 0x31/0x32 the Angemon pedestal (map_object.c). All run on the 20-a-second game frame.
    /// </summary>
    public sealed partial class PlayableRoom
    {
        Vector2 _scrollPx;          // the camera's centre on the painted map (MAP_TILE_DATA.cameraX / Y)
        int _evFrame;               // PLAYTIME_FRAMES for these effects

        // the 0x64 built-ins this file runs; wait = the script stops here (ACTIVE_INSTRUCTION / longjmp 2)
        bool EventBuiltin(int n, out bool wait)
        {
            wait = false;
            switch (n)
            {
                case 0x20:                                                                       // initializeNamingBuffer(variable 254), then newGameStateMachine
                    _line = null; _choiceOpts = null; _routineOn = true;
                    _setup = new CharacterSetup(_scripts, _state, _menus, _disc) { Art = _battle != null ? _battle.Art : null, DrawBackground = false, JijimonSquare = JijimonSquare };
                    _setup.OnDone = () => { _routineOn = false; AdvanceScript(); };
                    _setup.StartNaming(_state.Vars[0xFE]);
                    wait = true; return true;
                case 0x28: StartBuildup(); return true;                                          // EAB_startBuildup(the Digimon of script 5)
                case 0x33: if (_audio != null) _audio.SetMapSounds(0x15); return true;          // BOSS_EFE.TMD, EAB_REL, voice bank 0x73 (read when used here), loadMapSounds2(0x15)
                case 0x34: if (_audio != null) _audio.SetMapSounds(0x14); return true;          // OP.TIM, ENDI_REL, loadMapSounds2(0x14)
                case 0x35: _line = null; _choiceOpts = null; Run(EndingScene(true), true); wait = true; return true;    // tamerSetState(0x10), then MAIN_STATE 3
                case 0x36: _line = null; _choiceOpts = null; StartCoroutine(GameClearSave()); wait = true; return true;  // gameClearSave, then scriptTickMainMenu
                case 0x19: _state.Var0Reset = _state.Vars[0xFE]; return true;                  // MAIN_D_80134FE7 = variable 254
                case 0x10: if (_itemBox != null) _itemBox.Close(_itemBox.Bits); _wait = 1f / 20f; wait = true; return true;   // triggerBoxCloseFlag(2): the shop's Bits box shuts; longjmp 2
                case 0x13: _cartActive = (sbyte)_state.Vars[0xFE]; _wait = 1f / 20f; wait = true; return true;    // setDirtCartModel(variable 254), then longjmp 2
                case 0x14: _pileSize--; PlacePile(); _wait = 1f / 20f; wait = true; return true;                   // decreaseDirtPileSize, then longjmp 2
                case 0x1A: LoadDirtCart(); return true;
                case 0x1B: LoadDirtPile(); return true;
                case 0x1C: MonochromonBubble(); return true;
                case 0x22: _shakeFrame = 0; _shakeX = 0; _shakeBox = 0; return true;           // createMeramonShake
                case 0x23: case 0x24: case 0x27: return true;                                  // loadShopLibrary / readMapTFS / loadTrainingLibrary: we load these when they are needed
                case 0x25: _line = null; _choiceOpts = null; StartCoroutine(EventRoutine(TickRemoveMist)); wait = true; return true;
                case 0x26: StartNinjamon(); return true;
                case 0x2F: _line = null; _choiceOpts = null; StartCoroutine(LostAllLives()); wait = true; return true;
                case 0x29: _toyBox = 0; SpawnStatic("/ETCNA/GAND.TMD", "Gearbox", new[] { -100, 0, 1700 }); return true;     // spawnGearbox
                case 0x2A:                                                                                                  // somethingToyTown(variable 254)
                    _toyBox = (sbyte)_state.Vars[0xFE];
                    if (_toyBox == 0 && _state.GetFlag(350)) Debug.Log("[script] readMapTFS(0x92): the tiles loaded later would come from map 0x92's picture (not followed)");
                    return true;
                case 0x2B: SpawnToyTownBoxes(); return true;
                case 0x2C: _toyBox = (sbyte)_state.Vars[0xFE]; return true;                                                // openToyTownBox(variable 254)
                case 0x31: SpawnPedestal(); return true;
                case 0x32: StartCoroutine(EventRoutine(MovePedestal)); wait = true; return true;
            }
            return false;
        }

        // a built-in that ticks once a game frame until it says it is done; then the script goes on
        IEnumerator EventRoutine(System.Func<bool> tick)
        {
            _routineOn = true;
            float acc = 0f;
            bool done = false;
            while (!done)
            {
                yield return null;
                acc += Time.deltaTime * 20f;
                while (acc >= 1f && !done) { acc -= 1f; done = tick(); }
            }
            _routineOn = false;
            AdvanceScript();
        }

        // scriptInstructionFBtoFF 0xFB: sets the running script and loads the map. Map setup sections start with it for their own map,
        // which is already loaded here with that script.
        void ScriptLoadMap(int scriptId, int mapId)
        {
            if (mapId == _mapId) return;
            Debug.Log("[script] 0xFB: script " + scriptId + ", map " + mapId + " (another map) not followed");
        }

        void ClearEvents()
        {
            _shakeFrame = -1; _shakeX = 0;
            _ninjaFrame = -1; _monoTimer = -1;
            _cartActive = 2; _cartT[0] = _cartT[1] = null; _pileT = null; _pedestalT = null;
            _toyBox = 0; _boxLid[0] = _boxLid[1] = null;
            _buildupOn = false; foreach (var r in _eabRings) { r.Timer = -1; r.T = null; } ClearEabParticles(); _eabFlash = -1;
            foreach (var t in _evTex.Values) if (t != null) Destroy(t);
            _evTex.Clear();
            foreach (var m in _evMat.Values) if (m != null) Destroy(m);
            _evMat.Clear();
        }

        // once a game frame (TickMapObjects)
        void TickEvents()
        {
            _evFrame++;
            TickMeramonShake();
            TickNinjamon();
            if (_monoTimer > 0) _monoTimer--;
            TickMist();
            TickToyTownBoxes();
            TickEab();
        }

        // after the camera is placed: the shake moves the whole picture, the cart follows the partner
        void EventsLate()
        {
            if (_shakeX != 0)
            {
                float winW = WindowHeightPx * _cam.aspect;
                _cam.lensShift += new Vector2(_shiftSign.x * -_shakeX / winW, 0f);      // DRAWING_OFFSET_X += offset
            }
            PlaceDirtCart();
        }

        void DrawEvents()
        {
            if (Event.current.type != EventType.Repaint || _cam == null || _map == null) return;
            DrawMist();
            DrawMonochromonBubble();
            DrawMeramonFlash();
            DrawEabFlash();
        }

        // ------------------------------------------------------------------ drawing PS1 quads over the picture

        readonly Dictionary<long, Texture2D> _evTex = new Dictionary<long, Texture2D>();     // pictures from this map's video memory
        readonly Dictionary<Texture2D, Material> _evMat = new Dictionary<Texture2D, Material>();

        // a page of the map's video memory, its colours times tint / 128 (the primitive's RGB)
        Texture2D EvPage(int tpage, int clut, int layer, int tint)
        {
            if (_mapVram == null) return null;
            long key = ((long)tpage << 40) | ((long)clut << 16) | ((long)layer << 12) | (long)tint;
            Texture2D t;
            if (_evTex.TryGetValue(key, out t)) return t;
            var px = _mapVram.PagePixels(tpage, clut, _map.GetPalette(_curPalette), layer);
            if (tint != 128) for (int i = 0; i < px.Length; i++) px[i] = Tint(px[i], tint, tint, tint);
            t = new Texture2D(256, 256, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            t.SetPixels32(px); t.Apply();
            _evTex[key] = t;
            return t;
        }

        static Color32 Tint(Color32 c, int r, int g, int b)
        {
            return new Color32((byte)Mathf.Min(255, c.r * r / 128), (byte)Mathf.Min(255, c.g * g / 128), (byte)Mathf.Min(255, c.b * b / 128), c.a);
        }

        Material EvBlend(Texture2D t, int abr)
        {
            Material m;
            if (_evMat.TryGetValue(t, out m)) return m;
            var sh = Shader.Find("DW1Remake/GuiBlend");      // drawn from OnGUI: depth test off, reads the texture Graphics.DrawTexture passes
            if (sh != null)
            {
                m = new Material(sh) { mainTexture = t };
                MmdModel.SetPsxBlend(m, abr);
            }
            else { m = MmdModel.MakeBlendMaterial(t, abr); Debug.LogWarning("[objects] DW1Remake/GuiBlend shader not found"); }
            _evMat[t] = m;
            return m;
        }

        // one textured quad in PS1 screen pixels from the middle of the picture (x, y, w, h; a negative w mirrors it), its texels
        // u, v (from the top), uw, vh; cut to the 320 x 240 picture as the GPU's drawing area does. mat == null: drawn solid.
        static void DrawPs1Quad(Texture2D tex, float u, float v, float uw, float vh, float x, float y, float w, float h, Material mat)
        {
            if (tex == null || w == 0f || h == 0f) return;
            float x0 = Mathf.Max(Mathf.Min(x, x + w), -160f), x1 = Mathf.Min(Mathf.Max(x, x + w), 160f);
            float y0 = Mathf.Max(Mathf.Min(y, y + h), -120f), y1 = Mathf.Min(Mathf.Max(y, y + h), 120f);
            if (x1 <= x0 || y1 <= y0) return;
            System.Func<float, float> uAt = px => u + (px - x) / w * uw;
            System.Func<float, float> vAt = py => v + (py - y) / h * vh;
            float ua = uAt(x0), ub = uAt(x1), va = vAt(y0), vb = vAt(y1);
            var src = new Rect(ua / tex.width, 1f - vb / tex.height, (ub - ua) / tex.width, (vb - va) / tex.height);
            Rect p = UiKit.Picture;
            var dst = new Rect(p.x + (x0 / 320f + 0.5f) * p.width, p.y + (y0 / 240f + 0.5f) * p.height, (x1 - x0) / 320f * p.width, (y1 - y0) / 240f * p.height);
            if (mat == null) GUI.DrawTextureWithTexCoords(dst, tex, src);
            else Graphics.DrawTexture(dst, tex, src, 0, 0, 0, 0, mat);
        }

        // ------------------------------------------------------------------ 0x22 Meramon shake

        int _shakeFrame = -1, _shakeX, _shakeBox;     // MERAMON_SHAKE_FRAME_COUNT, what it added to DRAWING_OFFSET_X, MERAMON_SHAKE_POS_X (= POS_Y)
        Texture2D[] _spot;                             // SPOT256.TIM, solid / STP colours, times (MERAMON_SHAKE_RED 0, GREEN 0xFF, BLUE 0xFF)

        // tickMeramonShake: 210 frames; the picture moves 1, 2, 3, 2, 1 pixels from side to side, and a box of SPOT256 grows by 32 each other frame
        // (up to 400 x 400) and is taken away from the picture (blend 2)
        void TickMeramonShake()
        {
            if (_shakeFrame < 0) return;
            int f = _shakeFrame;
            int offset = f < 0x1F ? 1 : f < 0x3D ? 2 : f < 0x79 ? 3 : f < 0xB5 ? 2 : 1;
            if (f % 2 == 0)
            {
                offset = -offset;
                _shakeBox = Mathf.Max(-200, _shakeBox - 0x20);       // MERAMON_SHAKE_OFFSET
            }
            _shakeX += offset;
            _shakeFrame++;
            if (_shakeFrame >= 0xD2) { _shakeFrame = -1; _shakeX = 0; }
        }

        void DrawMeramonFlash()
        {
            if (_shakeFrame < 0 || _shakeBox == 0) return;
            if (_spot == null)
            {
                _spot = new Texture2D[3];
                TimImage tim;
                if (!_disc.Exists("/ETCDAT/SPOT256.TIM") || !TimImage.TryParse(_disc.ReadFile("/ETCDAT/SPOT256.TIM"), 0, out tim)) return;
                for (int layer = 1; layer <= 2; layer++)
                {
                    var src = tim.ToTextureLayer(0, layer);
                    var px = src.GetPixels32();
                    for (int i = 0; i < px.Length; i++) px[i] = Tint(px[i], 0, 0xFF, 0xFF);
                    src.SetPixels32(px); src.Apply();
                    src.filterMode = FilterMode.Point;
                    _spot[layer] = src;
                }
            }
            if (_spot[1] == null) return;
            // getTPage(1, 2, 832, 256), u 64 v 128 size 63: SPOT256 sits at VRAM 832,384, so its pixels 64-127, rows 0-63
            float x = _shakeBox + _shakeX, size = -2f * _shakeBox;
            DrawPs1Quad(_spot[1], 64, 0, 63, 63, x, _shakeBox, size, size, null);
            DrawPs1Quad(_spot[2], 64, 0, 63, 63, x, _shakeBox, size, size, EvBlend(_spot[2], 2));
        }

        // ------------------------------------------------------------------ 0x26 Ninjamon

        const int NinjaPieces = 0x29;
        int _ninjaFrame = -1;                        // NINJAMON_FX_COUNTER
        readonly int[] _ninjaX = new int[NinjaPieces], _ninjaY = new int[NinjaPieces], _ninjaDx = new int[NinjaPieces], _ninjaDy = new int[NinjaPieces];

        // createNinjamonEffect: keeps where the first 41 map pieces are (storeMapObjectPosition), each gets a speed of 12-21 across and 3-12 down
        void StartNinjamon()
        {
            if (_overlay == null) return;
            for (int i = 0; i < NinjaPieces && i < _overlay.Instances.Length; i++)
            {
                _ninjaX[i] = _overlay.Instances[i].X; _ninjaY[i] = _overlay.Instances[i].Y;
                _ninjaDx[i] = Random.Range(0, 10) + 12; _ninjaDy[i] = Random.Range(0, 10) + 3;
            }
            _ninjaFrame = 0;
        }

        // renderNinjamonEffect: they fly; past x 160 one starts again from its place with a new speed; they are drawn in front (order 10);
        // after 60 frames all 41 are hidden (flag 1)
        void TickNinjamon()
        {
            if (_ninjaFrame < 0 || _overlay == null) return;
            float dm = 10 * 16f * Unit;
            for (int i = 0; i < NinjaPieces && i < _overlay.Instances.Length; i++)
            {
                var inst = _overlay.Instances[i];
                inst.X += _ninjaDx[i]; inst.Y += _ninjaDy[i];
                if (inst.X > 0xA0)
                {
                    inst.X = _ninjaX[i]; inst.Y = _ninjaY[i];
                    _ninjaDx[i] = Random.Range(0, 10) + 12; _ninjaDy[i] = Random.Range(0, 10) + 3;
                }
                foreach (var a in _ovAnims) if (a.I == inst) a.Dm = dm;
                ReplacePiece(i, dm);
            }
            if (++_ninjaFrame >= 0x3C)
            {
                for (int i = 0; i < NinjaPieces && i < _overlay.Instances.Length; i++)
                {
                    _overlay.Instances[i].Flag = 1;
                    if (_ovPieces != null && i < _ovPieces.Length && _ovPieces[i] != null) _ovPieces[i].SetActive(false);
                }
                _ninjaFrame = -1;
            }
        }

        // ------------------------------------------------------------------ 0x1C Monochromon mood bubble

        int _monoTimer = -1, _monoU;                 // MONOCHROMON_BUBBLE_TIMER (-1 = no bubble), MONOCHROMON_BUBBLE_SPRITE.u

        // createMonochromonMoodBubble: variable 247 = the Digimon, 248 = the mood (0-4 shows one, 5 or more takes it away)
        void MonochromonBubble()
        {
            int who = _state.Vars[0xF7], mood = _state.Vars[0xF8];
            if (mood < 5)
            {
                Transform t; DigimonAnimator an;
                if (ActorOf(who, out t, out an)) { _monoTimer = 0x1E; _monoU = mood * 32; }
            }
            else _monoTimer = -1;
        }

        // renderMonochromonMoodBubble: 32 x 32 at u = mood x 32 of the map's page (640,0) with colour row 486, added to the picture (attribute
        // 0x50000000); over 30 frames it grows from 1/16 to full size and rises 16 pixels
        void DrawMonochromonBubble()
        {
            if (_monoTimer < 0) return;
            Transform t; DigimonAnimator an;
            if (!ActorOf(_state.Vars[0xF7], out t, out an)) return;
            int type = t == _partner ? PartnerType : -1;
            foreach (var n in _npcs) if (n.T == t) { type = n.Data.Type; break; }
            Vector3 s = _cam.WorldToScreenPoint(ConditionFxPoint(t, an, type));
            if (s.z <= 0f) return;
            float x = ToPs1X(s.x), y = ToPs1Y(s.y);
            int offset = 0x10 - (_monoTimer >> 1);
            float scale = offset / 16f;
            int tpage = 640 / 64, clut = 486 << 6;
            DrawPs1Quad(EvPage(tpage, clut, 1, 128), _monoU, 0, 32, 32, x - 16f * scale, y - offset - 16f * scale, 32f * scale, 32f * scale, null);
            var stp = EvPage(tpage, clut, 2, 128);
            if (stp != null) DrawPs1Quad(stp, _monoU, 0, 32, 32, x - 16f * scale, y - offset - 16f * scale, 32f * scale, 32f * scale, EvBlend(stp, 1));
        }

        const uint ConditionFxOffsets = 0x8012A2C4, OriginalTypes = 0x8012A84C;   // CONDITION_FX_OFFSETS (177 SVECTORs), ORIGINAL_DIGIMON_TYPES (180 int16)
        short[] _fxOffsets, _originalTypes;

        // translateConditionFXToEntity (effect_common.c): CONDITION_FX_OFFSETS[getOriginalType(type)] holds x, y, z and a bone number (pad);
        // the point is that offset moved by the bone's world matrix. getOriginalType: ORIGINAL_DIGIMON_TYPES[type], or the type itself when that is < 0.
        Vector3 ConditionFxPoint(Transform t, DigimonAnimator an, int type)
        {
            if (_fxOffsets == null)
            {
                byte[] exe = _disc.ReadFile("/SLUS_010.32");
                uint load = System.BitConverter.ToUInt32(exe, 0x18);
                _fxOffsets = new short[177 * 4]; _originalTypes = new short[180];
                for (int i = 0; i < _fxOffsets.Length; i++) _fxOffsets[i] = System.BitConverter.ToInt16(exe, (int)(ConditionFxOffsets - load + 0x800) + i * 2);
                for (int i = 0; i < _originalTypes.Length; i++) _originalTypes[i] = System.BitConverter.ToInt16(exe, (int)(OriginalTypes - load + 0x800) + i * 2);
            }
            int orig = type < 0 || type > 0xB0 ? -1 : _originalTypes[type] < 0 ? type : _originalTypes[type];
            if (orig < 0 || orig >= 177) return t.position;
            int bone = _fxOffsets[orig * 4 + 3];
            var node = an != null ? an.Node(bone) : null;
            if (node == null) node = t;
            // our bones use the game's layout: game units x 0.01 with y flipped (as the model's vertices)
            return node.TransformPoint(new Vector3(_fxOffsets[orig * 4], -_fxOffsets[orig * 4 + 1], _fxOffsets[orig * 4 + 2]) * MmdModel.Scale);
        }

        // ------------------------------------------------------------------ mist (renderMist, tickRemoveMist)

        static readonly int[] MistMaps = { 115, 116, 117, 118, 119, 121, 163, 220 };
        readonly int[] _mistX = { -0xA0, 0xA0 }, _mistX2 = { 0xA0, -0xA0 }, _mistY = { -0x78, 0x78 }, _mistClut = { 0x50, 0x10 };    // MIST_X_OFFSETS, MAIN_D_80134228, MIST_Y_OFFSETS, MIST_CLUT_Y
        Vector2Int _mistScroll; bool _mistScrollSet;

        bool MistShown()
        {
            if (System.Array.IndexOf(MistMaps, _mapId) < 0) return false;
            return !((_mapId == 0xA3 || _mapId == 0xDC) && _state.GetFlag(0x155));
        }

        // the mist moves with the camera and drifts left one pixel every other frame; the second layer goes 1.2 times as fast the other way
        void TickMist()
        {
            var cam = new Vector2Int(Mathf.RoundToInt(_scrollPx.x), Mathf.RoundToInt(_scrollPx.y));
            if (!_mistScrollSet) { _mistScroll = cam; _mistScrollSet = true; }
            int dx = _mistScroll.x - cam.x, dy = _mistScroll.y - cam.y;
            _mistScroll = cam;
            if (!MistShown()) return;
            if (_mapId != 0xDC && _state.GetFlag(0x94)) { _mistClut[0] = 0xC0; _mistClut[1] = 0x80; }
            _mistX[0] += dx;
            if (_evFrame % 2 == 0) _mistX[0]--;
            _mistX[0] = MistWrap(_mistX[0], 0xA0, -0x1DF, 0x280);
            _mistX[1] = MistWrap(_mistX[0] + 0x140, 0xA0, -0x1DF, 0x280);
            _mistX2[0] += (short)(dx * 12 / 10);
            if (_evFrame % 2 == 0) _mistX2[0]++;
            _mistX2[0] = MistWrap(_mistX2[0], 0x1E0, -0x9F, 0x280);
            _mistX2[1] = MistWrap(MistWrap(_mistX2[0] + 0x140, 0x1E0, -0x9F, 0x280), 0x1E0, -0x9F, 0x280);
            _mistY[0] += dy;
            _mistY[0] = MistWrap(_mistY[0], 0x78, -0x167, 0x1E0);
            _mistY[1] = MistWrap(_mistY[0] + 0xF0, 0x78, -0x167, 0x1E0);
        }

        static int MistWrap(int v, int hi, int lo, int span)
        {
            if (v >= hi) v -= span;
            if (v < lo) v += span;
            return v;
        }

        // tickRemoveMist: the second colour sets step on by one a frame until the first reaches 0xC0 (then 0xC0 / 0x80)
        bool TickRemoveMist()
        {
            _mistClut[0] += 0x10; _mistClut[1] += 0x10;
            if (_mistClut[0] >= 0xC0) { _mistClut[0] = 0xC0; _mistClut[1] = 0x80; return true; }
            return false;
        }

        // renderMist: 8 quads of the map's page (704,0) with colour row 486, texels 0,0 to 255,200 stretched over 320 x 240. Four with
        // colour set 0, colour 0x96, blend 3 (a quarter added); four mirrored with colour 0x50, blend 1 (added) and the colour set of MIST_CLUT_Y.
        void DrawMist()
        {
            if (!MistShown() || _mapVram == null) return;
            int tpage = 704 / 64;
            for (int i = 0; i < 8; i++)
            {
                if (i < 4)
                {
                    int clut = 486 << 6;
                    var solid = EvPage(tpage, clut, 1, 0x96); var stp = EvPage(tpage, clut, 2, 0x96);
                    float x = _mistX[i % 2], y = _mistY[i / 2];
                    DrawPs1Quad(solid, 0, 0, 255, 200, x, y, 320, 240, null);
                    DrawPs1Quad(stp, 0, 0, 255, 200, x, y, 320, 240, EvBlend(stp, 3));
                }
                else
                {
                    int set = (_mapId == 0xA3 || _mapId == 0xDC) ? 0 : _mapId == 0x77 ? _mistClut[0] : _mistClut[1];
                    int clut = (486 << 6) | ((set / 16) & 63);
                    var solid = EvPage(tpage, clut, 1, 0x50); var stp = EvPage(tpage, clut, 2, 0x50);
                    float x = _mistX2[i % 2], y = _mistY[(i - 4) / 2];
                    // x0 = x, x1 = x - 320: the picture is mirrored, from x - 320 to x
                    DrawPs1Quad(solid, 0, 0, 255, 200, x, y, -320, 240, null);
                    DrawPs1Quad(stp, 0, 0, 255, 200, x, y, -320, 240, EvBlend(stp, 1));
                }
            }
        }

        // ------------------------------------------------------------------ 0x1A / 0x1B / 0x13 / 0x14 dirt cart and dirt pile

        readonly Transform[] _cartT = new Transform[2];       // GENERAL_OBJECTS[0] = \ETCNA\DOSYA.TMD (cart), [1] = \ETCNA\HAKO.TMD (box)
        int _cartActive = 2;                                  // ACTIVE_DIRT_CART_MODEL (2 = none)
        Transform _pileT;
        int _pileSize = 15;                                   // DIRT_PILE_SIZE_COUNTER (the game keeps it over map changes)

        // loadDirtCartModel: both models; their picture is in the page at 960,256 v 152+ = the condition sheet (ETCTIM.BIN at 23808,
        // VRAM 960,384) with colour row 486 (CLUT 272,486)
        void LoadDirtCart()
        {
            _cartActive = 2;
            try
            {
                TimImage tim;
                if (!TimImage.TryParse(_disc.ReadFile("/ETCDAT/ETCTIM.BIN"), 23808, out tim)) return;
                Texture2D all = tim.ToTexture(6), solid = tim.ToTextureLayer(6, 1), stp = tim.ToTextureLayer(6, 2);
                _objAssets.Add(all); _objAssets.Add(solid); _objAssets.Add(stp);
                System.Func<MmdModel.Face, int, Texture2D> tex = (f, layer) => layer == 0 ? all : layer == 1 ? solid : stp;
                System.Func<MmdModel.Face, int, Vector2> uv = (f, i) => new Vector2((f.U[i] + 0.5f) / tim.Width, 1f - (f.T[i] - 128 + 0.5f) / tim.Height);
                string[] files = { "/ETCNA/DOSYA.TMD", "/ETCNA/HAKO.TMD" };
                for (int k = 0; k < 2; k++)
                {
                    var model = ReadTmd(_disc.ReadFile(files[k]));
                    if (model.Parts.Count == 0) continue;
                    _cartT[k] = MakeStaticObject(BuildStatic(model.Parts[0], tex, uv, _objAssets), k == 0 ? "Dirt cart" : "Dirt box", ObjRoot());
                    _cartT[k].gameObject.SetActive(false);
                }
            }
            catch (System.Exception ex) { Debug.LogWarning("[objects] dirt cart: " + ex.Message); }
        }

        // renderDirtCartModel: the chosen model stands radius + 100 in front of the partner, turned as the partner is (its height not added)
        void PlaceDirtCart()
        {
            for (int k = 0; k < 2; k++)
            {
                var t = _cartT[k];
                if (t == null) continue;
                bool on = k == _cartActive && _partner != null;
                t.gameObject.SetActive(on);
                if (!on) continue;
                var info = DigimonStatsCatalog.Get(_disc, PartnerType);
                int r = (info != null ? info.Radius : 0) + 100;
                float yaw = _partner.eulerAngles.y - FacingOffset;
                int vy = Mathf.RoundToInt(yaw / 360f * 4096f);
                Vector3 g = ToGame(_partner.position);
                float a = vy * 2f * Mathf.PI / 4096f;
                int[] pos = { Mathf.RoundToInt(g.x + Mathf.Sin(a) * r), 0, Mathf.RoundToInt(g.z + Mathf.Cos(a) * r) }, rot = { 0, vy, 0 };
                PlaceStatic(t, pos, rot);
            }
        }

        // loadDirtPileModel: \ETCNA\T_YAMA.TMD (the map's 8-bit page at 640,0, colour row 484) at (2037, 0, 849), size counter / 15
        void LoadDirtPile()
        {
            if (_mapVram == null) return;
            try
            {
                var model = ReadTmd(_disc.ReadFile("/ETCNA/T_YAMA.TMD"));
                if (model.Parts.Count == 0) return;
                var m = BuildStatic(model.Parts[0], PageTexture, (f, i) => new Vector2((f.U[i] + 0.5f) / 256f, 1f - (f.T[i] + 0.5f) / 256f), _objAssets);
                _pileT = MakeStaticObject(m, "Dirt pile", ObjRoot());
                PlacePile();
            }
            catch (System.Exception ex) { Debug.LogWarning("[objects] dirt pile: " + ex.Message); }
        }

        // decreaseDirtPileSize / renderDirtPile: scale (counter x 4096) / 15; drawn only while the counter is 6 or more
        void PlacePile()
        {
            if (_pileT == null) return;
            PlaceStatic(_pileT, new[] { 2037, 0, 849 }, new[] { 0, 0, 0 });
            float s = ((_pileSize * 0x1000) / 15) / 4096f;
            _pileT.localScale = new Vector3(s, s, s);
            _pileT.gameObject.SetActive(_pileSize >= 6);
        }

        // ------------------------------------------------------------------ 0x31 / 0x32 Angemon pedestal

        Transform _pedestalT;
        int _pedX, _pedZ = 1500;                               // ANGEMON_PEDESTAL_PROGRESS_X / _Z

        // spawnAngemonPedestal: \ETCNA\ABOX.TMD (the map's 8-bit page at 704,0, colour row 484) at (0, 0, 1500)
        void SpawnPedestal()
        {
            if (_mapVram == null) return;
            try
            {
                var model = ReadTmd(_disc.ReadFile("/ETCNA/ABOX.TMD"));
                if (model.Parts.Count == 0) return;
                var m = BuildStatic(model.Parts[0], PageTexture, (f, i) => new Vector2((f.U[i] + 0.5f) / 256f, 1f - (f.T[i] + 0.5f) / 256f), _objAssets);
                if (_pedestalT != null) Destroy(_pedestalT.gameObject);
                _pedestalT = MakeStaticObject(m, "Angemon pedestal", ObjRoot());
                _pedX = 0; _pedZ = 1500;
                PlaceStatic(_pedestalT, new[] { _pedX, 0, _pedZ }, new[] { 0, 0, 0 });
            }
            catch (System.Exception ex) { Debug.LogWarning("[objects] Angemon pedestal: " + ex.Message); }
        }

        // moveAngemonPedestal: 10 further along Z a frame, shaking 5 to each side, until Z 2000
        bool MovePedestal()
        {
            _pedZ += 10;
            _pedX = _pedZ % 20 == 0 ? 5 : -5;
            bool done = false;
            if (_pedZ > 1999) { _pedZ = 2000; done = true; }
            if (_pedestalT != null) PlaceStatic(_pedestalT, new[] { _pedX, 0, _pedZ }, new[] { 0, 0, 0 });
            return done;
        }

        // ------------------------------------------------------------------ 0x29 - 0x2C Toy Town

        int _toyBox;                                           // TOY_TOWN_SELECTED_BOX: 1 = the big box opens, 2 = the small one
        readonly Transform[] _boxLid = new Transform[2];
        readonly int[] _lidProgress = new int[2];              // BOX_LID_PROGRESS.big / .small

        // one model of the map's 4-bit page 640,0 (loadStaticTMD), placed with no rotation
        Transform SpawnStatic(string file, string name, int[] pos)
        {
            if (_mapVram == null) return null;
            try
            {
                var model = ReadTmd(_disc.ReadFile(file));
                if (model.Parts.Count == 0) return null;
                var t = MakeStaticObject(BuildStatic(model.Parts[0], PageTexture, (f, i) => new Vector2((f.U[i] + 0.5f) / 256f, 1f - (f.T[i] + 0.5f) / 256f), _objAssets), name, ObjRoot());
                PlaceStatic(t, pos, new[] { 0, 0, 0 });
                return t;
            }
            catch (System.Exception ex) { Debug.LogWarning("[objects] " + name + ": " + ex.Message); return null; }
        }

        // spawnToyTownBoxes: \ETCNA\BIGBOX.TMD at (500, -600, 600) and \ETCNA\SMABOX.TMD at (-400, -400, 600); object 1 of each is the lid,
        // whose coordinates hang on the box's (GsInitCoordinate2(&COORDS[0], &COORDS[1]))
        void SpawnToyTownBoxes()
        {
            if (_mapVram == null) return;
            string[] files = { "/ETCNA/BIGBOX.TMD", "/ETCNA/SMABOX.TMD" };
            int[][] at = { new[] { 500, -600, 600 }, new[] { -400, -400, 600 } };
            System.Func<MmdModel.Face, int, Vector2> uv = (f, i) => new Vector2((f.U[i] + 0.5f) / 256f, 1f - (f.T[i] + 0.5f) / 256f);
            for (int k = 0; k < 2; k++)
            {
                try
                {
                    var model = ReadTmd(_disc.ReadFile(files[k]));
                    if (model.Parts.Count < 2) continue;
                    var box = MakeStaticObject(BuildStatic(model.Parts[0], PageTexture, uv, _objAssets), k == 0 ? "Big box" : "Small box", ObjRoot());
                    PlaceStatic(box, at[k], new[] { 0, 0, 0 });
                    _boxLid[k] = MakeStaticObject(BuildStatic(model.Parts[1], PageTexture, uv, _objAssets), "Lid", box);
                    PlaceStatic(_boxLid[k], new[] { 0, 0, 0 }, new[] { 0, 0, 0 });
                }
                catch (System.Exception ex) { Debug.LogWarning("[objects] Toy Town box: " + ex.Message); }
            }
            _lidProgress[0] = _lidProgress[1] = 0;
            _toyBox = 0;
        }

        // renderToyTownBoxes: the chosen box's lid turns back 100 a frame about X, to -0x400 (a quarter turn)
        void TickToyTownBoxes()
        {
            if (_toyBox != 1 && _toyBox != 2) return;
            int k = _toyBox - 1;
            _lidProgress[k] = Mathf.Max(-0x400, _lidProgress[k] - 100);
            if (_boxLid[k] != null) PlaceStatic(_boxLid[k], new[] { 0, 0, 0 }, new[] { _lidProgress[k], 0, 0 });
        }

        // ------------------------------------------------------------------ 0x2F all lives lost

        // lostAllLives: forgets round((10 - tamer level) x 2 x techniques known / 100) random techniques (of the first 0x3A), each with
        // script 0's text 7 of section 0x4D8 (variable 243 = the technique, 244 = how many are left)
        IEnumerator LostAllLives()
        {
            _routineOn = true;
            int n = 0;
            for (int i = 0; i < 0x3A; i++) if (_state.Techs[i]) n++;
            n = (10 - _state.TamerLevel) * 2 * (n * 100) / 100;
            if (n % 100 >= 0x32) n += 100;
            n /= 100;
            if (n != 0)
            {
                _state.Vars[0xF4] = (byte)n;
                while (true)
                {
                    var pool = new List<int>();
                    for (int i = 0; i < 0x3A; i++) if (_state.Techs[i]) pool.Add(i);
                    if (pool.Count == 0) break;
                    int move = pool[(pool.Count * Random.Range(0, 0x8000)) >> 15];      // randomLimit(j)
                    UnlearnMove(move);
                    _state.Vars[0xF3] = (byte)move;
                    yield return HeadSay(0x4D8, 7, 0xFF);
                    int left = _state.Vars[0xF4] - 1;
                    if (left == 0) break;
                    _state.Vars[0xF4] = (byte)left;
                }
            }
            _routineOn = false;
            AdvanceScript();
        }

        // unlearnMove (utils2.c): the technique leaves its Techset slot; then the game ANDs the 32-bit word of learned techniques with a 16-bit
        // mask: 0x2C and 0x30 clear bit 12, 0x37 and 0x39 clear nothing, the others clear their bit (none when it is 16 or more). Because the
        // mask is 16 bits, techniques 16-31 of the same word (move / 32) are cleared too (original behaviour, kept).
        void UnlearnMove(int move)
        {
            int slot = _state.TechSet.IndexOf(move);
            if (slot >= 0) _state.TechSet[slot] = -1;
            int mask = move == 0x2C || move == 0x30 ? 0x1000 : move == 0x37 || move == 0x39 ? 0 : (1 << (move % 32)) & 0xFFFF;
            uint keep = (uint)((mask ^ 0xFFFF) & 0xFFFF);
            int word = move / 32;
            for (int b = 0; b < 32; b++)
                if (((keep >> b) & 1) == 0 && word * 32 + b < _state.Techs.Length) _state.Techs[word * 32 + b] = false;
        }
    }
}
