using System;
using System.Collections.Generic;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// Fishing (fish/fish.c = FISH_REL.BIN at 0x80070000, main/fish.c). checkFishingMap loads it on map 6 (MAYO06) and map 8 (MAYO10): from then
    /// on the fish swim in the water (FISH_tickFishingWorld / FISH_renderFishingWorld, object 0x3001), whether the boy fishes or not. The Triangle
    /// menu gets its eighth option (getFishingAvailability: 1 = shown greyed, no rod or no food; 2 = usable); choosing it runs initializeFishing
    /// and tamer state 11 (tamerTickFishing -> FISH_tamerTick -> FISH_tickRod every frame). A caught Seadramon ends it with DG.SCN 0 section 0x4E4
    /// (triggerSeadramonCutscene) and fishing stays off on this map.
    /// Every table, text and number is read from FISH_REL.BIN and the exe at run time (addresses from the decompilation's symbols); the pictures
    /// are \ETCDAT\FISHING.TIM / FISHING2.TIM, the rod \ETCDAT\SAO.TMD, Seadramon \ETCDAT\SEA.TMD.
    /// The game's screen work is in "projected" pixels: a point through the map's fixed camera, from the middle of the whole painted map (the
    /// picture scrolls over it by the drawing offset). Here that is MapPixel - half the map (FishProject); the view's middle is _fishView.
    /// </summary>
    public sealed partial class PlayableRoom
    {
        const uint FishBase = 0x80070000;
        const int FishConfirm = 0x40, FishCancel = 0x10, FishAlt = 0x20;      // US: CONFIRM_BUTTON Cross, CANCEL_BUTTON Triangle, ALT_BUTTON Circle
        const int FishUp = 0x1000, FishRight = 0x2000, FishDown = 0x4000, FishLeft = 0x8000;

        struct FV { public int X, Y, Z; public FV(int x, int y, int z) { X = x; Y = y; Z = z; } }      // VECTOR
        struct FA { public int X, Y, Z; public FA(int x, int y, int z) { X = x; Y = y; Z = z; } }      // SVECTOR (angles, 4096 a turn)

        sealed class FishSprite { public int Tpage, Clut, U0, V0, U1, V1, Ox, Oy; }
        sealed class FishSpot { public int Weight; public int[] Px, Py; }
        sealed class FishKind { public int Item, Lo, Hi, DelayMin, DelayMax, Bait; }

        // FishingMapData (0x38 bytes)
        sealed class FishMap
        {
            public int MapId, Scale, Unk8, ScaleSmall, ScaleLarge, SeaTime1, SeaTime2, CastRange;
            public FishSpot[] Spots;
            public string Tim;
            public int[] SeaX, SeaY;
        }

        // Fish (56 bytes)
        sealed class Fish
        {
            public bool Active; public int Kind, Size, Transparency;
            public FV Pos; public FA Rot;
            public int AnimPhase, State, FramesOnState, Lifetime, TimeNextMove, TimeThisMove;
            public void CopyFrom(Fish f)
            {
                Active = f.Active; Kind = f.Kind; Size = f.Size; Transparency = f.Transparency; Pos = f.Pos; Rot = f.Rot; AnimPhase = f.AnimPhase;
                State = f.State; FramesOnState = f.FramesOnState; Lifetime = f.Lifetime; TimeNextMove = f.TimeNextMove; TimeThisMove = f.TimeThisMove;
            }
        }

        sealed class FishRipple { public FV Pos; public int Kind, Frame, Sprite, Scale; public bool Active; }
        sealed class FishMarker { public bool Active; public int Lifetime, Sprite; public FV Pos; }

        // ------------------------------------------------------------------ the loaded data
        bool _fishOn;                 // FISHING_DATA_PTR != NULL
        byte[] _fishBin, _fishExe; uint _fishExeLoad;
        FishMap _fishMap;
        FishSprite[] _fishSprites;
        FishKind[] _fishKinds;
        int[] _fishBaitItem = new int[6], _fishBaitThreshold = new int[6];
        sbyte[] _fishBite;
        int[] _fishWater1X, _fishWater1Y, _fishWater2X, _fishWater2Y;
        string[] _fishText;
        int[] _fishSeq958, _fishSeq960, _fishSeq968, _fishSeq96C, _fishSeq4B4, _fishSeq99C, _fishSeqA4D8, _fishSeqA4E4, _fishSeqA4F8, _fishSeqA540, _fishSeqA554;
        int[] _fishLargeFrames, _fishSmallFrames, _fishHookLarge, _fishHookSmall;
        FA[] _fishSeaOffsets, _fishRodOffsets;
        readonly FA[] _fishRodSegRot = new FA[5];         // FISH_D_8007A5BC
        readonly FA[] _fishSeaHist = new FA[5];           // FISH_SEADRAMON_HISTORY
        readonly FV[] _fishRodSegPos = new FV[5];         // FISH_ROD_SEGMENT_POS
        readonly Matrix4x4[] _fishRodSegMat = new Matrix4x4[5];
        FA _fishBaitOfs, _fishBaitUp, _fishTipOfs, _fishTipUp, _fishRodBaseOfs, _fishThrowVec;
        FV _fishReelTarget;
        int _fishGaugeX, _fishGaugeY;
        string _fishHooked;           // MAIN_D_801349D8 "Hooked!"
        static readonly int[] FishRecordSize = new int[8];    // FISH_RECORD_SIZE (main BSS: kept for the whole session, not saved)

        // FishingData
        bool _fishDisabled, _fishCatchComplete;
        readonly Fish[] _fishPool = new Fish[33];
        int _fishPoolNext, _fishPoolCount, _fishPoolFrames, _fishEscapeTimer;
        readonly FishRipple[] _fishRipples = new FishRipple[16];
        int _fishRippleCount, _fishRippleNext, _fishAttractRadius; FishRipple _fishAttractor;
        readonly FishMarker[] _fishMarkers = new FishMarker[16];
        int _fishMarkerCount, _fishMarkerNext;
        int[] _fishSeaX, _fishSeaY;

        // FishingRod
        bool _rDisplayRod, _rDisplaySwimmer, _rDisplayLine;
        int _rState, _rFrames, _rItemSlot, _rBait, _rThrowPower, _rItemTaken, _rSwimmerType, _rPressed;
        FV _rLineStart, _rLineStartMirror;

        // HookedFish
        readonly Fish _hFish = new Fish();
        int _hNibble, _hCatching, _hAnimMode, _hPower, _hPowerMax, _hStrike, _hEscaped;

        // FishingView (text box) and the bait menu
        bool _fbShown, _ftShown, _ftScrolling;
        int _fbLastRow, _fbSelected = -1, _fbRepeat, _ftFramesOpen;
        RectInt _fbStart;
        FV _ftScrollTo;

        // FishingTension
        bool _tDisplay; int _tLevel, _tAtLimit, _tLimit; Color32 _tColour = new Color32(255, 255, 255, 255);

        // FishingItem (the bait / the catch on the line)
        bool _iShown; FV _iPos; int _iSprite;

        // FishingSwimmer (the float)
        FV _sPos, _sVel; int _sState, _sTimer, _sSprite, _sMode, _sDist, _sGravity;

        // MAIN_D_801351DC: the camera's place saved when fishing starts, and whether fishing moves the camera
        bool _fishCamOn; Vector2 _fishSaved, _fishView;
        bool _fishTamer;              // tamer state 11
        int _fishPad, _fishPadPrev, _fishLatch, _fishVsync; float _fishAcc;
        uint _fishRand;
        FishingView _fishView2D;

        // drawing
        PsxVram _fishVram;
        PsxTmd.Built[] _fishRod, _fishSea;
        readonly List<UnityEngine.Object> _fishAssets = new List<UnityEngine.Object>();
        Material _fishMatSolid; readonly Material[] _fishMatBlend = new Material[4];
        MaterialPropertyBlock _fishMp;
        readonly List<Mesh> _fishMeshes = new List<Mesh>(); int _fishMeshUsed;

        // ------------------------------------------------------------------ reading FISH_REL.BIN and the exe
        int FB8(uint a) { return _fishBin[(int)(a - FishBase)]; }
        int FS8(uint a) { return (sbyte)_fishBin[(int)(a - FishBase)]; }
        int FS16(uint a) { return BitConverter.ToInt16(_fishBin, (int)(a - FishBase)); }
        int FU16(uint a) { return BitConverter.ToUInt16(_fishBin, (int)(a - FishBase)); }
        int FS32(uint a) { return BitConverter.ToInt32(_fishBin, (int)(a - FishBase)); }
        uint FU32(uint a) { return BitConverter.ToUInt32(_fishBin, (int)(a - FishBase)); }
        string FStr(uint a)
        {
            int o = (int)(a - FishBase), e = o;
            while (e < _fishBin.Length && _fishBin[e] != 0) e++;
            return System.Text.Encoding.ASCII.GetString(_fishBin, o, e - o);
        }
        int XAt(uint a) { return (int)(a - _fishExeLoad + 0x800); }
        int XS8(uint a) { return (sbyte)_fishExe[XAt(a)]; }
        int XU8(uint a) { return _fishExe[XAt(a)]; }
        int XS16(uint a) { return BitConverter.ToInt16(_fishExe, XAt(a)); }
        FA XSv(uint a) { return new FA(XS16(a), XS16(a + 2), XS16(a + 4)); }
        int[] XBytes(uint a, int n, bool signed) { var r = new int[n]; for (int i = 0; i < n; i++) r[i] = signed ? XS8(a + (uint)i) : XU8(a + (uint)i); return r; }
        int[] FWords(uint a, int n) { var r = new int[n]; for (int i = 0; i < n; i++) r[i] = FS32(a + (uint)(4 * i)); return r; }

        bool FishReadData()
        {
            if (_fishBin == null) _fishBin = _disc.ReadFile("/FISH_REL.BIN");
            if (_fishExe == null) { _fishExe = _disc.ReadFile("/SLUS_010.32"); if (_fishExe != null) _fishExeLoad = BitConverter.ToUInt32(_fishExe, 0x18); }
            if (_fishBin == null || _fishExe == null || _fishBin.Length < 0xAB40) return false;
            // FISH_SPRITES 0x8007A200: 49 x {tpage, clut, u0, v0, u1, v1, ox, oy}
            _fishSprites = new FishSprite[49];
            for (int i = 0; i < 49; i++)
            {
                uint a = 0x8007A200u + (uint)(10 * i);
                _fishSprites[i] = new FishSprite { Tpage = FU16(a), Clut = FU16(a + 2), U0 = FB8(a + 4), V0 = FB8(a + 5), U1 = FB8(a + 6), V1 = FB8(a + 7), Ox = FB8(a + 8), Oy = FB8(a + 9) };
            }
            // FISH_BAIT_CHANCES 0x8007A1F4: 6 x {item, threshold}
            for (int i = 0; i < 6; i++) { _fishBaitItem[i] = FB8(0x8007A1F4u + (uint)(2 * i)); _fishBaitThreshold[i] = FB8(0x8007A1F5u + (uint)(2 * i)); }
            _fishBite = new sbyte[200];
            for (int i = 0; i < 200; i++) _fishBite[i] = (sbyte)FS8(0x8007A3ECu + (uint)i);
            // FISH_KIND_INFO 0x8007AA28: 7 x {item, lo, hi, moveDelayMin, moveDelayMax, bait (s16)}
            _fishKinds = new FishKind[7];
            for (int i = 0; i < 7; i++)
            {
                uint a = 0x8007AA28u + (uint)(0x18 * i);
                _fishKinds[i] = new FishKind { Item = FS32(a), Lo = FS32(a + 4), Hi = FS32(a + 8), DelayMin = FS32(a + 12), DelayMax = FS32(a + 16), Bait = FS16(a + 20) };
            }
            // the ripple and float picture sequences
            _fishSeq4B4 = new int[20]; for (int i = 0; i < 20; i++) _fishSeq4B4[i] = FS8(0x8007A4B4u + (uint)i);
            _fishSeqA4D8 = FWords(0x8007A4D8, 3); _fishSeqA4E4 = FWords(0x8007A4E4, 5); _fishSeqA4F8 = FWords(0x8007A4F8, 18);
            _fishSeqA540 = FWords(0x8007A540, 5); _fishSeqA554 = FWords(0x8007A554, 6);
            _fishSeaOffsets = new FA[5]; _fishRodOffsets = new FA[5];
            for (int i = 0; i < 5; i++)
            {
                _fishSeaOffsets[i] = new FA(FS16(0x8007A56Cu + (uint)(8 * i)), FS16(0x8007A56Eu + (uint)(8 * i)), FS16(0x8007A570u + (uint)(8 * i)));
                _fishRodOffsets[i] = new FA(FS16(0x8007A594u + (uint)(8 * i)), FS16(0x8007A596u + (uint)(8 * i)), FS16(0x8007A598u + (uint)(8 * i)));
                _fishRodSegRot[i] = new FA(FS16(0x8007A5BCu + (uint)(8 * i)), FS16(0x8007A5BEu + (uint)(8 * i)), FS16(0x8007A5C0u + (uint)(8 * i)));
                _fishSeaHist[i] = new FA(FS16(0x8007AB40u + (uint)(8 * i)), FS16(0x8007AB42u + (uint)(8 * i)), FS16(0x8007AB44u + (uint)(8 * i)));
            }
            _fishReelTarget = new FV(FS32(0x8007AA18), FS32(0x8007AA1C), FS32(0x8007AA20));
            // the water tables of FISH_isFishableWater1 (FISH_D_8007A890, 16 points) and 2 (FISH_D_8007A9B0, 7 points)
            _fishWater1X = new int[16]; _fishWater1Y = new int[16];
            for (int i = 0; i < 16; i++) { _fishWater1X[i] = FS16(0x8007A890u + (uint)(4 * i)); _fishWater1Y[i] = FS16(0x8007A892u + (uint)(4 * i)); }
            _fishWater2X = new int[7]; _fishWater2Y = new int[7];
            for (int i = 0; i < 7; i++) { _fishWater2X[i] = FS16(0x8007A9B0u + (uint)(4 * i)); _fishWater2Y[i] = FS16(0x8007A9B2u + (uint)(4 * i)); }
            // the texts (US): FISH_STR_THEY_TOOK_MY_BAIT ... FISH_STR_IT_GOT_STUCK
            uint[] t = { 0x8007A5F4, 0x8007A608, 0x8007A614, 0x8007A628, 0x8007A634, 0x8007A650, 0x8007A670, 0x8007A688, 0x8007A69C, 0x8007A6A8,
                         0x8007A6C4, 0x8007A6D8, 0x8007A6F8, 0x8007A70C, 0x8007A720, 0x8007A73C, 0x8007A758, 0x8007A774, 0x8007A784 };
            _fishText = new string[t.Length];
            for (int i = 0; i < t.Length; i++) _fishText[i] = FStr(t[i]);
            // the exe's fishing tables (MAIN_D_80134958-801349D8)
            _fishSeq958 = XBytes(0x80134958, 6, true); _fishSeq960 = XBytes(0x80134960, 7, true); _fishSeq968 = XBytes(0x80134968, 3, true); _fishSeq96C = XBytes(0x8013496C, 5, true);
            _fishGaugeX = XS16(0x80134974); _fishGaugeY = XS16(0x80134976);
            _fishBaitOfs = XSv(0x8013497C); _fishBaitUp = XSv(0x80134984); _fishTipOfs = XSv(0x8013498C); _fishTipUp = XSv(0x80134994);
            _fishSeq99C = XBytes(0x8013499C, 8, true);
            _fishLargeFrames = XBytes(0x801349A4, 8, false); _fishSmallFrames = XBytes(0x801349AC, 4, false);
            _fishHookLarge = XBytes(0x801349B0, 6, false); _fishHookSmall = XBytes(0x801349B8, 6, false);
            _fishRodBaseOfs = XSv(0x801349C8); _fishThrowVec = XSv(0x801349D0);
            int o = XAt(0x801349D8), e = o; while (e < _fishExe.Length && _fishExe[e] != 0) e++;
            _fishHooked = System.Text.Encoding.ASCII.GetString(_fishExe, o, e - o);
            return true;
        }

        // FISHING_MAP_DATA 0x8007AAD0 (0x38 bytes each), FISHING_MAP_COUNT 2
        FishMap FishReadMap(int mapId)
        {
            const int count = 2;
            int found = -1;
            for (int i = 0; i < count; i++) if (FS16(0x8007AAD0u + (uint)(0x38 * i)) == mapId) { found = i; break; }
            if (found < 0) return null;
            uint a = 0x8007AAD0u + (uint)(0x38 * found);
            var m = new FishMap
            {
                MapId = FS16(a), Scale = FS32(a + 4), Unk8 = FS32(a + 8), ScaleSmall = FS32(a + 0xC), ScaleLarge = FS32(a + 0x10),
                SeaTime1 = FS16(a + 0x14), SeaTime2 = FS16(a + 0x16), CastRange = FS16(a + 0x28)
            };
            uint spots = FU32(a + 0x18); int nSpots = FS32(a + 0x1C);
            m.Spots = new FishSpot[Mathf.Clamp(nSpots, 0, 6)];
            for (int i = 0; i < m.Spots.Length; i++)
            {
                uint s = spots + (uint)(16 * i);
                uint pts = FU32(s + 8); int n = FB8(s + 12);
                var sp = new FishSpot { Weight = FB8(s), Px = new int[n], Py = new int[n] };
                for (int k = 0; k < n; k++) { sp.Px[k] = FS16(pts + (uint)(4 * k)); sp.Py[k] = FS16(pts + (uint)(4 * k + 2)); }
                m.Spots[i] = sp;
            }
            m.Tim = FStr(FU32(a + 0x2C));
            uint sea = FU32(a + 0x30); int seaN = FS32(a + 0x34);
            m.SeaX = new int[Mathf.Max(0, seaN)]; m.SeaY = new int[Mathf.Max(0, seaN)];
            for (int k = 0; k < seaN; k++) { m.SeaX[k] = FS16(sea + (uint)(4 * k)); m.SeaY[k] = FS16(sea + (uint)(4 * k + 2)); }
            return m;
        }

        // ------------------------------------------------------------------ main/fish.c
        // getBestFishingRod: trigger 46 the Amazing Rod (2), trigger 45 the Old Rod (1)
        int FishBestRod() { return _state.GetFlag(46) ? 2 : _state.GetFlag(45) ? 1 : 0; }

        // hasFoodItem: a bag slot whose item has ITEM_PARA.sortingValue 2
        bool FishHasFood()
        {
            for (int i = 0; i < 30; i++)
            {
                int id = _state.Slots[i];
                if (id != 0xFF && FishIsFood(id)) return true;
            }
            return false;
        }
        bool FishIsFood(int id) { return _itemCatalog != null && id < _itemCatalog.Count && _itemCatalog[id].Category == 2; }

        /// <summary>getFishingAvailability: 0 no fishing here, 1 the option greyed (no rod or no food), 2 usable.</summary>
        int FishAvailability()
        {
            if (!_fishOn || _fishDisabled) return 0;
            if (FishBestRod() == 0 || !FishHasFood()) return 1;
            return 2;
        }

        bool FishIsFishing { get { return _fishOn && _rState != 0; } }      // isFishing: the game clock stops (tickGameClock)

        // checkFishingMap: deinitializeFishing, then FISH_init on maps 6 and 8
        void FishCheckMap()
        {
            FishDeinit();
            if (_mapId != 8 && _mapId != 6) return;
            try
            {
                if (!FishReadData()) { Debug.LogWarning("[fishing] FISH_REL.BIN or the exe missing"); return; }
                _fishMap = FishReadMap(_mapId);
                if (_fishMap == null) return;
            }
            catch (Exception e) { Debug.LogWarning("[fishing] " + e.Message); return; }
            _fishOn = true;
            if (_fishRand == 0) _fishRand = (uint)Environment.TickCount | 1u;
            FishInit();
            Debug.Log("[fishing] map " + _mapId + " (" + _fishMap.Tim + ", " + _fishMap.Spots.Length + " fish spots)");
        }

        // deinitializeFishing (map change, partnerTickDying)
        void FishDeinit()
        {
            if (!_fishOn) return;
            _fishDisabled = false;
            FishEnd();
            FishInitRipples(); FishInitMarkers(); _hFish.Active = false;
            _fishOn = false; _fishTamer = false;
            foreach (var o in _fishAssets) if (o != null) Destroy(o);
            // the meshes kept for reuse went with the assets: reusing them on the next fishing map threw every frame and nothing was drawn
            _fishAssets.Clear(); _fishMeshes.Clear(); _fishMeshUsed = 0; _fishRod = null; _fishSea = null;
            if (_fishPlate != null) _fishPlate.Remove();
            if (_fishStrikePlate != null) _fishStrikePlate.Remove();
            if (_fishVram != null) _fishVram.Destroy();
            _fishVram = null;
            _fishMatSolid = null; for (int i = 0; i < 4; i++) _fishMatBlend[i] = null;
        }

        // FISH_init
        void FishInit()
        {
            _fishDisabled = false; _fishCatchComplete = false;
            FishLoadPictures();
            _fbShown = false; _fbLastRow = -1;          // FISH_initBaitMenu
            _ftShown = false;                           // FISH_initTextBox
            FishResetTension();
            _iShown = false;
            FishResetBobber();
            FishInitPool();
            _rState = 0; _rDisplayRod = false; _rDisplayLine = false; _rDisplaySwimmer = false;     // FISH_resetRodState
            _hFish.Active = false;
            FishInitRipples(); FishInitMarkers();
            if (_fishView2D == null && Battle.StatFont != null)
            {
                _fishView2D = FishingView.Load(_disc, Battle.StatFont);
                if (_fishView2D != null) _fishView2D.Sound = Snd;
            }
            if (_fishView2D != null)
            {
                var v = _fishView2D;
                v.CloseNow(v.Bait); v.CloseNow(v.Text); v.Gauge = false;
                v.ItemPics = _menus != null ? _menus.Icons : null; v.Art = _battle != null ? _battle.Art : null; v.Items = _itemCatalog; v.State = _state;
                v.GaugeX = _fishGaugeX; v.GaugeY = _fishGaugeY;
                var f = _fishSprites[31]; var b = _fishSprites[32];
                v.FrameSpr = new[] { f.U0, f.V0, f.U1, f.V1, f.Ox, f.Oy }; v.BarSpr = new[] { b.U0, b.V0, b.U1, b.V1, b.Ox, b.Oy };
                v.Sheet = _fishVram != null ? _fishVram.Page(f.Tpage, f.Clut, 0) : null;
            }
        }

        // FISH_loadSpritesAndSeadramon: the map's TIM (FISHING.TIM / FISHING2.TIM) into video memory, \ETCDAT\SEA.TMD; FISH_loadFishing: \ETCDAT\SAO.TMD
        void FishLoadPictures()
        {
            _fishVram = new PsxVram();
            string tim = (_fishMap.Tim ?? "").Replace('\\', '/');
            if (_disc.Exists(tim)) _fishVram.LoadTims(_disc.ReadFile(tim));
            else Debug.LogWarning("[fishing] " + tim + " not found");
            _fishSea = FishBuildTmd(FStr(0x8007A4C8));
            _fishRod = FishBuildTmd(FStr(0x8007A5E4));
        }

        PsxTmd.Built[] FishBuildTmd(string path)
        {
            path = path.Replace('\\', '/');
            if (!_disc.Exists(path)) { Debug.LogWarning("[fishing] " + path + " not found"); return null; }
            var tmd = PsxTmd.Parse(_disc.ReadFile(path));
            var r = new PsxTmd.Built[tmd.Objs.Count];
            for (int i = 0; i < r.Length; i++) r[i] = PsxTmd.Built.Make(tmd.Objs[i], _fishVram, FishTmdMaterial, _fishAssets);
            return r;
        }

        // GsSortObject4J's packets with our PS1 shader (as the arena's models): one-sided packets culled, semi-transparent colours blended by the page's abr
        Material FishTmdMaterial(PsxTmd.Prim p, int layer)
        {
            var sh = Shader.Find("DW1Remake/Psx");
            if (sh == null) return null;
            var m = new Material(sh) { renderQueue = 2000 };
            m.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Back);
            if (layer < 0) { m.SetFloat("_Textured", 0f); if (p.Semi) MmdModel.SetPsxBlend(m, (p.Tpage >> 5) & 3); return m; }
            m.SetTexture("_MainTex", _fishVram.Page(p.Tpage, p.Clut, layer));
            if (layer == 2) MmdModel.SetPsxBlend(m, (p.Tpage >> 5) & 3);
            return m;
        }

        // ------------------------------------------------------------------ the frame loop
        /// <summary>Once a Unity frame, from Update: the game's frames at 20 a second (object 0x3001, tamer state 11, the UI boxes' ticks).</summary>
        void FishUpdate()
        {
            if (!_fishOn) return;
            if (Pad.Pressed(PadButton.Cross)) _fishLatch |= FishConfirm;
            if (Pad.Pressed(PadButton.Triangle)) _fishLatch |= FishCancel;
            if (Pad.Pressed(PadButton.Circle)) _fishLatch |= FishAlt;
            if (Pad.Pressed(PadButton.Up)) _fishLatch |= FishUp;
            if (Pad.Pressed(PadButton.Down)) _fishLatch |= FishDown;
            if (Pad.Pressed(PadButton.Left)) _fishLatch |= FishLeft;
            if (Pad.Pressed(PadButton.Right)) _fishLatch |= FishRight;
            _fishAcc = Mathf.Min(_fishAcc + Time.deltaTime, 0.25f);
            while (_fishAcc >= 0.05f && _fishOn)
            {
                _fishAcc -= 0.05f;
                FishFrame();
            }
        }

        void FishFrame()
        {
            // pollInputGame: POLLED_INPUT_PREVIOUS = POLLED_INPUT, then the pad (a press between two game frames is kept for the next one)
            _fishPadPrev = _fishPad;
            _fishPad = (Pad.Held(PadButton.Cross) ? FishConfirm : 0) | (Pad.Held(PadButton.Triangle) ? FishCancel : 0) | (Pad.Held(PadButton.Circle) ? FishAlt : 0)
                     | (Pad.Held(PadButton.Up) ? FishUp : 0) | (Pad.Held(PadButton.Down) ? FishDown : 0) | (Pad.Held(PadButton.Left) ? FishLeft : 0)
                     | (Pad.Held(PadButton.Right) ? FishRight : 0) | _fishLatch;
            _fishLatch = 0;
            _fishVsync++;
            FishTickWorld();
            if (_fishTamer) FishTamerTickFishing();
            // the UI boxes' tick functions
            if (_fishView2D != null)
            {
                if (_fishView2D.Bait.State == 1 || _fbShown) FishTickBaitMenu();
                if (_ftShown) FishTickTextBox();
                _fishView2D.StepBoxes();
            }
        }

        bool FishFresh(int bit) { return (_fishPad & ~_fishPadPrev & bit) != 0; }
        bool FishHeld(int bit) { return (_fishPad & bit) != 0; }

        // tamerTickFishing (main/fish.c): a finished Seadramon catch turns fishing off here and starts its cutscene
        void FishTamerTickFishing()
        {
            int done = FishTamerTick();
            if (done != 0)
            {
                _fishDisabled = true;
                StartDgSection(0x4E4);      // triggerSeadramonCutscene: callScriptSection(0, 0x4E4, 0)
            }
        }

        // FISH_tamerTick: back to tamer state 0 when the rod is put away, or once Seadramon has been pulled in
        int FishTamerTick()
        {
            FishTickRod();
            if (!FishIsFishing) { _fishTamer = false; return 0; }      // tamerSetState(0), startGameTime
            if (_fishCatchComplete)
            {
                FishTickHooked(0x11);
                FishEnd();
                _fishTamer = false;
                return 1;
            }
            return 0;
        }

        /// <summary>The Triangle menu's Fishing option: setCameraFollowPlayer, initializeFishing (FISH_loadFishing), tamerSetState(0xB).</summary>
        void FishStart()
        {
            if (!_fishOn) return;
            FishLoadFishing();
            _fishTamer = true;
        }

        // FISH_tickFishingWorld (object 0x3001). (PARTNER_STATE 0xD also stops it in the game; ours has no partner state numbers to test.)
        void FishTickWorld()
        {
            if (_fishDisabled) return;
            FishUpdateRodPose();
            if (FishTextBoxVisible()) return;
            FishTickRipples();
            FishTickMarkers();
            FishTickPopulation();
            FishTickHooked(0);
        }

        // FISH_endFishing
        void FishEnd()
        {
            if (_rDisplayRod)
            {
                FishApplySavedScroll();
                if (_anim != null) _anim.SetSlot(IdleSlot);      // setCameraFollowPlayer, startAnimation(tamer, 0)
                _rState = 0; _rDisplayLine = false; _rDisplayRod = false;
            }
            FishCloseBaitMenuIfOpen();
            FishCloseTextBox();                                  // FISH_shutdownTextBox
            FishResetTension();
            _iShown = false;
            FishResetBobber();
        }

        // FISH_loadFishing
        void FishLoadFishing()
        {
            if (_rDisplayRod) FishEnd();
            FishEnableCameraControl();                           // unsetCameraFollowPlayer, FISH_enableCameraControl
            _rState = 2; _rDisplayRod = true; _rDisplaySwimmer = false; _rDisplayLine = false;
            FishResetBobber();
            FishTickHooked(0xF);
            _rSwimmerType = FishBestRod();
            // the Amazing Rod (and, defensively, no rod) is type 2: MoveImage((0x1C0, 0x1E4, 0x1C, 8) -> (0x1C0, 0x1DC)) copies 8 colour rows
            // of the game's own video memory; that area is not one ours keeps, so it is not done (see PROGRESS "Still ours")
            if (_rSwimmerType != 1) _rSwimmerType = 2;
        }

        // ------------------------------------------------------------------ PS1 maths
        int FishRandom(int min, int max)
        {
            // customRandom(min, max): a number from min to max, both included
            if (max < min) { int t = min; min = max; max = t; }
            _fishRand = _fishRand * 1103515245u + 12345u;
            return min + (int)((_fishRand >> 8) % (uint)(max - min + 1));
        }

        static int FRsin(int a) { return (int)Math.Round(Math.Sin(a * Math.PI / 2048.0) * 4096.0); }
        static int FRcos(int a) { return (int)Math.Round(Math.Cos(a * Math.PI / 2048.0) * 4096.0); }
        static int FRatan2(int y, int x) { if (x == 0 && y == 0) return 0; return (int)Math.Round(Math.Atan2(y, x) * 2048.0 / Math.PI); }
        static int FDist(int x, int y, int z) { return (int)Math.Sqrt((double)x * x + (double)y * y + (double)z * z); }
        // the R3000's division: by zero it gives -1 (or 1 for a negative number); the game's population count divides by zero with no fish yet
        static int FDiv(int a, int b) { if (b == 0) return a >= 0 ? -1 : 1; return a / b; }
        static int FClamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

        // FISH_remapClamped (fish_model.c)
        static int FishRemap(int x, int x0, int x1, int y0, int y1)
        {
            if (x1 == x0) return y0;
            if (x1 < x0) { int t = x0; x0 = x1; x1 = t; }
            x = x < x0 ? x0 : (x1 < x ? x1 : x);
            return y0 + (((y1 - y0) * (x - x0)) + ((x1 - x0) / 2)) / (x1 - x0);
        }

        static Matrix4x4 FYXZ(FA r) { return ArenaStage.RotMatrixYXZ(r.X, r.Y, r.Z); }
        static FV FApply(Matrix4x4 m, FV v)
        {
            Vector3 r = m.MultiplyVector(new Vector3(v.X, v.Y, v.Z));
            return new FV((int)Math.Floor(r.x), (int)Math.Floor(r.y), (int)Math.Floor(r.z));
        }
        static FV FApply(Matrix4x4 m, FA v) { return FApply(m, new FV(v.X, v.Y, v.Z)); }
        static FV FAdd(FV a, FV b) { return new FV(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        static FV FRotate(FA rot, FV v) { return FApply(FYXZ(rot), v); }      // rotateVector

        // toEulerAngles: y = ratan2(dx, dz), x = -ratan2(dy, ground), both kept in -0x800..0x7FF
        static FA FToEuler(int dx, int dy, int dz)
        {
            int vy = FRatan2(dx, dz);
            int g = FDist(dx, 0, dz);
            int vx = -FRatan2(dy, g);
            vx &= 0xFFF; if (vx >= 0x800) vx -= 0x1000;
            vy &= 0xFFF; if (vy >= 0x800) vy -= 0x1000;
            return new FA(vx, vy, 0);
        }

        // the boy's facing (TAMER_ENTITY rotation.vy) and FISH_getBoneTransform: a bone's world place and turn in game space
        int FishTamerVy
        {
            get
            {
                Vector3 f = _player.rotation * Quaternion.Inverse(Quaternion.Euler(0, FacingOffset, 0)) * Vector3.forward;
                return ((int)Math.Round(Math.Atan2(f.x, f.z) * 2048.0 / Math.PI)) & 0xFFF;
            }
            set { _player.rotation = Quaternion.Euler(0, (value & 0xFFF) * 360f / 4096f, 0) * Quaternion.Euler(0, FacingOffset, 0); }
        }

        void FishBone(int bone, out FV pos, out Matrix4x4 rot)
        {
            var n = _anim != null ? _anim.Node(bone) : null;
            if (n == null && _anim != null) n = _anim.Node(0);
            Vector3 g = ToGame(n != null ? n.position : _player.position);
            pos = new FV(Mathf.RoundToInt(g.x), Mathf.RoundToInt(g.y), Mathf.RoundToInt(g.z));
            Matrix4x4 pose = Matrix4x4.identity;
            if (n != null)
            {
                Matrix4x4 u = Matrix4x4.Rotate(Quaternion.Inverse(_player.rotation) * n.rotation), f = Matrix4x4.Scale(new Vector3(1, -1, 1));
                pose = f * u * f;
            }
            rot = ArenaStage.RotMatrixYXZ(0, FishTamerVy, 0) * pose;
        }

        FV FishTamerPos { get { Vector3 g = ToGame(_player.position); return new FV(Mathf.RoundToInt(g.x), Mathf.RoundToInt(g.y), Mathf.RoundToInt(g.z)); } }

        // ------------------------------------------------------------------ the screen: projected pixels (the whole map's frame)
        int FishHalfW { get { return _map != null ? _map.PixelWidth / 2 : 160; } }
        int FishHalfH { get { return _map != null ? _map.PixelHeight / 2 : 120; } }
        // FISH_screenLeft / Right / Top / Bottom: -DRAW_OFFSET_LIMIT_X_MAX + x ... (initializeMapTiles: X_MAX = width / 2, X_MIN = 320 - width / 2)
        int FishEdgeL(int x) { return x - FishHalfW; }
        int FishEdgeR(int x) { return x - 320 + FishHalfW; }
        int FishEdgeT(int y) { return y - FishHalfH; }
        int FishEdgeB(int y) { return y - 240 + FishHalfH; }
        // FISH_viewX(0xA0) / FISH_viewY(0x78): the picture's middle now
        int FishViewX { get { return _fishCamOn ? (int)_fishView.x : (int)(_scrollPx.x - FishHalfW); } }
        int FishViewY { get { return _fishCamOn ? (int)_fishView.y : (int)(_scrollPx.y - FishHalfH); } }

        // FISH_projectPoint: through GsWSMATRIX, x and y times VIEWPORT_DISTANCE over the depth; nothing behind the camera or past 0x10000
        bool FishProject(FV p, out int x, out int y)
        {
            x = 0; y = 0;
            if (_map == null) return false;
            Vector3 q = new Vector3(p.X, p.Y, p.Z) - _vpG;
            float z = Vector3.Dot(q, _fG);
            if (z <= 0f || z >= 0x10000) return false;
            x = (int)(_h * Vector3.Dot(q, _rG) / z);
            y = (int)(_h * Vector3.Dot(q, _dG) / z);
            return true;
        }

        float FishDepth(FV p) { return Vector3.Dot(new Vector3(p.X, p.Y, p.Z) - _vpG, _fG); }

        // FISH_isScreenPointVisible: anywhere on the whole painted map
        bool FishVisible(int x, int y) { return x >= FishEdgeL(0) && x <= FishEdgeR(0x140) && y >= FishEdgeT(0) && y <= FishEdgeB(0xF0); }

        // mapToWorldPosition: the ground (y 0) under a projected point
        FV FishMapToWorld(int x, int y)
        {
            Vector3 u = WorldAtPixel(new Vector2(x + FishHalfW, y + FishHalfH), 0f);
            Vector3 g = ToGame(u);
            return new FV(Mathf.RoundToInt(g.x), Mathf.RoundToInt(g.y), Mathf.RoundToInt(g.z));
        }

        // the map's isWater
        bool FishIsWater(int x, int y) { return _fishMap.MapId == 8 ? FishWater1(x, y) : FishWater2(x, y); }

        // FISH_isFishableWater1 (map 8): above the shore line FISH_D_8007A890, not below the picture
        bool FishWater1(int x, int y)
        {
            if (FishEdgeB(0xF0) < y) return false;
            return FishLerpByX(x, y, _fishWater1X, _fishWater1Y) >= 0;
        }

        // FISH_isFishableWater2 (map 6): left of x 0x1AD; above y 6 always, else left of the line FISH_D_8007A9B0
        bool FishWater2(int x, int y)
        {
            if (x >= 0x1AD) return false;
            if (y < 6) return true;
            return FishLerpByY(x, y, _fishWater2X, _fishWater2Y) <= 0;
        }

        static int FishLerpByX(int x, int bas, int[] tx, int[] ty)
        {
            int n = tx.Length;
            if (x < tx[0] || tx[n - 1] < x) return 0;
            int i;
            for (i = n - 2; i >= 0; i--) if (x >= tx[i]) break;
            if (i < 0) i = 0;
            int y = ty[i] + ((x - tx[i]) * (ty[i + 1] - ty[i])) / (tx[i + 1] - tx[i]);
            return bas - y;
        }

        static int FishLerpByY(int bas, int y, int[] tx, int[] ty)
        {
            int n = tx.Length;
            if (y < ty[0] || ty[n - 1] < y) return 0;
            int i;
            for (i = n - 2; i >= 0; i--) if (y >= ty[i]) break;
            if (i < 0) i = 0;
            int x = tx[i] + ((y - ty[i]) * (tx[i + 1] - tx[i])) / (ty[i + 1] - ty[i]);
            return bas - x;
        }

        bool FishInCastRange(FV pos, int extra)
        {
            FV t = FishTamerPos;
            int d = FDist(pos.X - t.X, 0, pos.Z - t.Z);
            return !(_fishMap.CastRange + extra < d);
        }

        // ------------------------------------------------------------------ the camera (FISH_panCameraTo, FISH_scrollCameraTo ...)
        bool FishPanTo(int radius, FV pos, int maxDX, int maxDY)
        {
            int vx, vy;
            FishProject(pos, out vx, out vy);
            int cx = FishViewX, cy = FishViewY;
            if (Math.Abs(vx - cx) <= maxDX) vx = cx; else vx += vx > cx ? -maxDX : maxDX;
            if (Math.Abs(vy - cy) <= maxDY) vy = cy; else vy += vy > cy ? -maxDY : maxDY;
            vx = FClamp(vx, FishEdgeL(0xA0), FishEdgeR(0xA0));
            vy = FClamp(vy, FishEdgeT(0x78), FishEdgeB(0x78));
            if (vx - cx == 0 && vy - cy == 0) return false;
            if (radius > 0 && FDist(vx - cx, vy - cy, 0) >= radius)
            {
                int ang = FRatan2(vy - cy, vx - cx);
                vx = cx + ((radius * FRcos(ang)) >> 12);
                vy = cy + ((radius * FRsin(ang)) >> 12);
            }
            FishScrollTo(vx, vy);
            return true;
        }

        bool FishPanToTamer(int radius) { return FishPanTo(radius, FishTamerPos, 0, 0); }

        bool FishPanWithDPad()
        {
            int x = FishViewX, y = FishViewY, cx = x, cy = y;
            if ((_fishPad & FishUp) != 0) y -= 5;
            if ((_fishPad & FishDown) != 0) y += 5;
            if ((_fishPad & FishLeft) != 0) x -= 5;
            if ((_fishPad & FishRight) != 0) x += 5;
            x = FClamp(x, FishEdgeL(0xA0), FishEdgeR(0xA0));
            y = FClamp(y, FishEdgeT(0x78), FishEdgeB(0x78));
            if (Math.Abs(x - cx) <= 0 && Math.Abs(y - cy) <= 0) return false;
            FishScrollTo(x, y);
            return true;
        }

        void FishScrollTo(int x, int y)
        {
            if (!_fishCamOn) return;
            _fishView = new Vector2(FClamp(x, FishEdgeL(0xA0), FishEdgeR(0xA0)), FClamp(y, FishEdgeT(0x78), FishEdgeB(0x78)));
        }

        // FISH_enableCameraControl: the place now is kept; from now on fishing moves the camera
        void FishEnableCameraControl()
        {
            FishApplySavedScroll();
            Vector2 now = new Vector2(Mathf.Round(_scrollPx.x) - FishHalfW, Mathf.Round(_scrollPx.y) - FishHalfH);
            _fishCamOn = true; _fishSaved = now; _fishView = now;
            _camOn = false;
        }

        // FISH_applySavedCameraScroll, then setCameraFollowPlayer: the picture goes back to where it was and follows the boy's steps again
        void FishApplySavedScroll()
        {
            if (!_fishCamOn) return;
            _fishCamOn = false;
            // ours (user's request 2026-11-11): the picture comes back centred on the boy, not to the place FISH_enableCameraControl kept
            // (that place could leave him off the picture's middle, even off the picture, on both fishing maps)
            _camOffsetPx = Vector2.zero;
        }

        /// <summary>For LateUpdate: while fishing moves the camera, the picture's middle it wants (in map pixels).</summary>
        bool FishCameraPixel(out Vector2 px)
        {
            px = Vector2.zero;
            if (!_fishCamOn || _map == null) return false;
            px = new Vector2(_fishView.x + FishHalfW, _fishView.y + FishHalfH);
            return true;
        }

        // ------------------------------------------------------------------ the bait menu (box 3)
        bool FishBaitMenuVisible()
        {
            if (_fishView2D == null) return _fbShown;
            int s = _fishView2D.Bait.State;
            return s == 2 || s == 3 || _fbShown;
        }

        void FishCloseBaitMenu()
        {
            if (!_fbShown) return;
            _fbShown = false;
            if (_fishView2D != null) { _fishView2D.Bait.Origin = _fbStart; _fishView2D.Close(_fishView2D.Bait); }
        }

        void FishCloseBaitMenuIfOpen() { if (_fbShown) FishCloseBaitMenu(); }

        // FISH_tickBaitMenu: Up / Down / Left / Right on a fresh press or after 11 frames held (sound 2), Cross picks a cell, Triangle gives up
        void FishTickBaitMenu()
        {
            var v = _fishView2D;
            if (_fishPad == _fishPadPrev) _fbRepeat++; else _fbRepeat = 0;
            if (!_fbShown || v == null || v.Bait.State != 1) return;
            _fbLastRow = -1;
            if (FishFresh(FishUp) || (_fbRepeat >= 0xB && FishHeld(FishUp)))
            {
                Snd(2);
                if (--v.CursorRow < 0) v.CursorRow = 0;
                if (v.CursorRow < v.RowOffset) { v.RowOffset = v.CursorRow; _fbLastRow = v.RowOffset; }
            }
            if (FishFresh(FishDown) || (_fbRepeat >= 0xB && FishHeld(FishDown)))
            {
                Snd(2);
                if (++v.CursorRow >= v.TotalRows) v.CursorRow = v.TotalRows - 1;
                if (v.CursorRow >= v.RowOffset + v.VisibleRows) { v.RowOffset = v.CursorRow - v.VisibleRows + 1; _fbLastRow = v.RowOffset + v.VisibleRows - 1; }
            }
            if (FishFresh(FishLeft) || (_fbRepeat >= 0xB && FishHeld(FishLeft))) { Snd(2); if (--v.Column < 0) v.Column = 1; }
            if (FishFresh(FishRight) || (_fbRepeat >= 0xB && FishHeld(FishRight))) { Snd(2); if (++v.Column >= 2) v.Column = 0; }
            if (FishFresh(FishConfirm))
            {
                Snd(2);
                int cell = v.Column + v.CursorRow * 2;
                if (cell >= 0 && cell < 30 && v.BaitSlots[cell] != -1) { _fbSelected = v.BaitSlots[cell]; FishCloseBaitMenu(); }
            }
            if (FishFresh(FishCancel)) { Snd(2); _fbSelected = -1; FishCloseBaitMenu(); }
        }

        // FISH_openBaitMenu: the bag's food in slot order, two to a row; box 3 grows out of the boy's square
        bool FishOpenBaitMenu()
        {
            var v = _fishView2D;
            if (v == null) return false;
            _fbLastRow = -1; v.Column = 0; v.CursorRow = 0; v.RowOffset = 0; v.VisibleRows = 9;
            int n = 0;
            for (int i = 0; i < 30; i++)
            {
                int id = _state.Slots[i];
                if (id == 0xFF || !FishIsFood(id)) continue;
                v.BaitSlots[n++] = i;
                if (n >= 30) break;
            }
            v.TotalRows = (n + 1) / 2;
            for (; n < 30; n++) v.BaitSlots[n] = -1;
            if (v.TotalRows <= 0) return false;
            Vector2 p = BoneScreen(_player, _anim);
            _fbStart = new RectInt((int)p.x - 5, (int)p.y - 5, 10, 10);
            v.ScrollBar = v.TotalRows > v.VisibleRows;
            v.Open(v.Bait, new RectInt(-0x98, -0x68, 0x132, 0xB6), _fbStart, 2 << 4);
            v.Bait.Color = 0;
            _fbShown = true;
            return true;
        }

        // ------------------------------------------------------------------ the text box (box 2)
        bool FishTextBoxVisible()
        {
            if (_fishView2D == null) return _ftShown;
            int s = _fishView2D.Text.State;
            return s == 2 || s == 3 || _ftShown;
        }

        void FishCloseTextBox()
        {
            if (_ftShown && _fishView2D != null) _fishView2D.CloseNow(_fishView2D.Text);
            _ftShown = false;
        }

        // FISH_tickTextBox: Cross (after 10 frames) or Triangle closes it with sound 3; the input is used up
        void FishTickTextBox()
        {
            if (!_ftShown) return;
            _ftScrolling = true;
            if ((_ftFramesOpen >= 10 && FishFresh(FishConfirm)) || FishFresh(FishCancel))
            {
                _fishPad = 0;
                FishCloseTextBox();
                Snd(3);
            }
            if (_ftFramesOpen < 0x64) _ftFramesOpen++;
        }

        // FISH_showTextBox: static box 2 at (-0x82, 0x2A) 0x106 x 0x3B with up to four lines; the camera goes to 'target' while it is open
        void FishShowTextBox(string l0, string l1, string l2, string l3, FV target)
        {
            FishCloseTextBox();
            _fishPad = 0;
            _ftScrollTo = target; _ftFramesOpen = 0;
            var v = _fishView2D;
            if (v != null)
            {
                v.TextLines[0] = l0; v.TextLines[1] = l1; v.TextLines[2] = l2; v.TextLines[3] = l3;
                v.OpenStatic(v.Text, new RectInt(-0x82, 0x2A, 0x106, 0x3B), 2 << 4);
                v.Text.Color = 0;
            }
            _ftShown = true;
            Debug.Log("[fishing] " + l0 + (l1 != null ? " / " + l1 : "") + (l2 != null ? " / " + l2 : "") + (l3 != null ? " / " + l3 : ""));
        }

        // FISH_scrollViewToTarget
        void FishScrollViewToTarget()
        {
            if (_ftScrolling) { FishPanTo(0x10, _ftScrollTo, 0, 0); _ftScrolling = false; }
        }

        // ------------------------------------------------------------------ FISH_randomPointInSpot
        FV FishRandomPointInSpot(int index)
        {
            if (index < 0 || index >= 6 || index >= _fishMap.Spots.Length) return new FV(0, 0, 0);
            var spot = _fishMap.Spots[index];
            int[] pick = new int[3];
            // ours: a cap on the retries (the game tries for ever; a spot with no water on our picture would hang Unity)
            for (int tries = 0; tries < 2000; tries++)
            {
                for (int t = 0; t < 3; t++)
                {
                    bool dup;
                    do
                    {
                        dup = false;
                        pick[t] = FishRandom(0, spot.Px.Length - 1);
                        for (int j = 0; j < t; j++) if (pick[t] == pick[j]) dup = true;
                    } while (dup && spot.Px.Length >= 3);
                }
                // the points are held inside the picture in the table itself
                for (int t = 0; t < 3; t++)
                {
                    int k = pick[t];
                    spot.Px[k] = (short)FClamp(spot.Px[k], FishEdgeL(0), FishEdgeR(0x140));
                    spot.Py[k] = (short)FClamp(spot.Py[k], FishEdgeT(0), FishEdgeB(0xF0));
                }
                int r = FishRandom(0, 0x1000);
                int px = (short)((spot.Px[pick[0]] * r + spot.Px[pick[1]] * (0x1000 - r)) >> 12);
                int py = (short)((spot.Py[pick[0]] * r + spot.Py[pick[1]] * (0x1000 - r)) >> 12);
                r = FishRandom(0, 0x1000);
                px = (short)((spot.Px[pick[2]] * r + px * (0x1000 - r)) >> 12);
                py = (short)((spot.Py[pick[2]] * r + py * (0x1000 - r)) >> 12);
                if (!FishVisible(px, py)) continue;
                if (!FishIsWater(px, py)) continue;
                return FishMapToWorld(px, py);
            }
            Debug.LogWarning("[fishing] no water point found in spot " + index);
            return FishTamerPos;
        }

        // ------------------------------------------------------------------ markers (the bite test's dots)
        void FishInitMarkers()
        {
            for (int i = 0; i < 16; i++) { if (_fishMarkers[i] == null) _fishMarkers[i] = new FishMarker(); _fishMarkers[i].Active = false; }
            _fishMarkerCount = 0; _fishMarkerNext = 0;
        }

        void FishFreeMarker(FishMarker s) { if (s.Active) { s.Active = false; _fishMarkerCount--; } }

        int FishAllocMarker()
        {
            if (_fishMarkerCount >= 16) return -1;
            int p = _fishMarkerNext;
            do
            {
                if (!_fishMarkers[p].Active) { _fishMarkerNext = p; return p; }
                p++; if (p >= 16) p = 0;
            } while (p != _fishMarkerNext);
            _fishMarkerCount = 16;
            return -1;
        }

        void FishSpawnMarker(int sprite, FV pos)
        {
            int i = FishAllocMarker();
            if (i < 0) return;
            _fishMarkerCount++;
            var s = _fishMarkers[i];
            s.Active = true; s.Pos = pos; s.Sprite = sprite; s.Lifetime = 10;
        }

        void FishTickMarkers() { for (int i = 0; i < 16; i++) { var s = _fishMarkers[i]; if (s.Active && --s.Lifetime == 0) FishFreeMarker(s); } }
        void FishClearMarkers() { for (int i = 0; i < 16; i++) _fishMarkers[i].Active = false; _fishMarkerCount = 0; }

        // ------------------------------------------------------------------ ripples
        void FishInitRipples()
        {
            for (int i = 0; i < 16; i++) { if (_fishRipples[i] == null) _fishRipples[i] = new FishRipple(); _fishRipples[i].Active = false; }
            _fishRippleCount = 0; _fishRippleNext = 0; _fishAttractor = null;
        }

        void FishFreeRipple(FishRipple s) { if (s.Active) { s.Active = false; _fishRippleCount--; } }

        void FishTickRipple(FishRipple s)
        {
            if (!s.Active) return;
            switch (s.Kind)
            {
                case 0: s.Sprite = FSeq(_fishSeq958, s.Frame / 2); break;
                case -1: s.Sprite = FSeq(_fishSeq960, s.Frame); break;
                case -2: s.Sprite = FSeq(_fishSeq968, s.Frame / 3); break;
                case -3: s.Sprite = FSeq(_fishSeq96C, s.Frame); break;
                case -4: s.Sprite = FSeq(_fishSeq4B4, s.Frame); break;
                default: FishFreeRipple(s); return;
            }
            s.Frame++;
            if (s.Sprite == -1) { FishFreeRipple(s); return; }
            if (s.Sprite >= 13) FishFreeRipple(s);
        }

        static int FSeq(int[] seq, int i) { return i >= 0 && i < seq.Length ? seq[i] : -1; }

        int FishAllocRipple()
        {
            if (_fishRippleCount >= 16) return -1;
            int p = _fishRippleNext;
            do
            {
                if (!_fishRipples[p].Active) { _fishRippleNext = p; return p; }
                p++; if (p >= 16) p = 0;
            } while (p != _fishRippleNext);
            _fishRippleCount = 16;
            return -1;
        }

        // FISH_spawnRipple: on the water (y 0), spread at random; a ripple with a radius makes the fish near it flee
        void FishSpawnRipple(int kind, FV pos, int attractRadius, int spread, int scale)
        {
            int i = FishAllocRipple();
            if (i < 0) return;
            _fishRippleCount++;
            var s = _fishRipples[i];
            s.Active = true; s.Pos = pos; s.Kind = kind; s.Frame = 0; s.Sprite = 0;
            s.Pos.X += FishRandom(-spread, spread);
            s.Pos.Z += FishRandom(-spread, spread);
            s.Pos.Y = 0;
            s.Scale = scale;
            if (attractRadius != 0) { _fishAttractor = s; _fishAttractRadius = attractRadius; }
        }

        void FishTickRipples()
        {
            for (int i = 0; i < 16; i++) if (_fishRipples[i].Active) FishTickRipple(_fishRipples[i]);
            if (_fishAttractor != null && !_fishAttractor.Active) _fishAttractor = null;
        }

        // ------------------------------------------------------------------ the tension gauge
        void FishResetTension() { _tDisplay = false; _tLevel = 0; _tAtLimit = 0; _tColour = new Color32(255, 255, 255, 255); }

        void FishBeginTension() { FishResetTension(); _tDisplay = true; _tLimit = _rSwimmerType == 1 ? 0x1388 : 0x2710; }

        // FISH_updateTension: Cross held pulls it up, a fish swimming off (state 0x32) too; 2 = slack (the hook comes out), 1 = held at the
        // top for (4 - remap(size, 50, 300, 1, 3)) x 5 frames (the line snaps)
        int FishUpdateTension()
        {
            int n = (_rPressed > 0 ? 2 : 0) + (_hFish.State == 0x32 ? 1 : 0);
            int size = _hFish.Size;
            switch (n)
            {
                case 0: _tLevel += FishRemap(size, 0x32, 0x12C, -0xA6, -0xA6); break;
                case 1: _tLevel += FishRemap(size, 0x32, 0x12C, 0x29, 0x53); break;
                case 2: _tLevel += FishRemap(size, 0x32, 0x12C, 0x47, 0x7D); break;
                case 3: _tLevel += FishRemap(size, 0x32, 0x12C, 0xA6, 0x1F4); break;
            }
            _tLevel = FClamp(_tLevel, -_tLimit, _tLimit);
            int lim = _tLimit, half = lim / 2;
            int r, g, b;
            if (_tLevel >= half) { r = FishRemap(_tLevel, half, lim, 0xFF, 0xFF); g = FishRemap(_tLevel, half, lim, 0xFF, 0); b = FishRemap(_tLevel, half, lim, 0, 0); }
            else if (_tLevel >= 0) { r = FishRemap(_tLevel, 0, half, 0xFF, 0xFF); g = FishRemap(_tLevel, 0, half, 0xFF, 0xFF); b = FishRemap(_tLevel, 0, half, 0xFF, 0); }
            else if (_tLevel >= -lim / 2) { r = FishRemap(_tLevel, -lim / 2, 0, 0, 0xFF); g = FishRemap(_tLevel, -lim / 2, 0, 0xFF, 0xFF); b = FishRemap(_tLevel, -lim / 2, 0, 0xFF, 0xFF); }
            else { r = FishRemap(_tLevel, -lim, -lim / 2, 0, 0); g = FishRemap(_tLevel, -lim, -lim / 2, 0, 0xFF); b = FishRemap(_tLevel, -lim, -lim / 2, 0xFF, 0xFF); }
            _tColour = new Color32((byte)r, (byte)g, (byte)b, 255);
            if (_tLevel == -_tLimit) return 2;
            if (_tLevel == _tLimit)
            {
                int snapSteps = 4 - FishRemap(size, 0x32, 0x12C, 1, 3);
                if (++_tAtLimit == snapSteps * 5) return 1;
            }
            else _tAtLimit = 0;
            return 0;
        }

        // ------------------------------------------------------------------ the bait / catch picture
        void FishShowBait(FV? pos, int sprite) { _iShown = true; _iSprite = sprite; if (pos.HasValue) _iPos = pos.Value; }

        // FISH_moveBaitToRodTip: towards bone 6's end (+ (28, 0, 2) turned, + (0, 93, 0)), at most maxStep a frame (0 = at once). 1 = still on its way
        bool FishMoveBaitToRodTip(int maxStep)
        {
            FV target; Matrix4x4 m;
            FishBone(6, out target, out m);
            target = FAdd(target, FApply(m, _fishBaitOfs));
            target = FAdd(target, new FV(_fishBaitUp.X, _fishBaitUp.Y, _fishBaitUp.Z));
            int dist = maxStep == 0 ? 0 : FDist(target.X - _iPos.X, target.Y - _iPos.Y, target.Z - _iPos.Z);
            if (maxStep != 0 && maxStep < dist)
            {
                FA rot = FToEuler(target.X - _iPos.X, target.Y - _iPos.Y, target.Z - _iPos.Z);
                _iPos = FAdd(_iPos, FRotate(rot, new FV(0, 0, maxStep)));
                return true;
            }
            _iPos = target;
            return false;
        }

        // FISH_tickBaitSprite: the bait hangs 100 units past the float along the line; when it drops into the water while flying, a ripple
        void FishTickBaitSprite()
        {
            if (!_iShown) return;
            FV was = _iPos;
            FA rot = FToEuler(_sPos.X - _rLineStart.X, _sPos.Y - _rLineStart.Y, _sPos.Z - _rLineStart.Z);
            _iPos = FAdd(FRotate(rot, new FV(0, 0, 0x64)), _sPos);
            if (was.Y >= 0) return;
            if (_iPos.Y < 0) return;
            if (_rState != 8) return;
            FV probe = _iPos; probe.Y = 0;
            int x, y;
            bool blocked = !FishProject(probe, out x, out y) || !FishVisible(x, y) || !FishIsWater(x, y);
            if (blocked) return;
            FishSpawnRipple(0, _iPos, 0x64, 0, _fishMap.Unk8 * 75);
        }

        // ------------------------------------------------------------------ the float (FishingSwimmer)
        void FishResetBobber() { _sState = 3; _sSprite = 0; _sTimer = 0; _sMode = 0; }      // FISH_resetBobberAnim

        // FISH_getRodTipPos: bone 6's place + (28, 0, 2) turned by it + (0, 17, 0)
        FV FishRodTip()
        {
            FV v; Matrix4x4 m;
            FishBone(6, out v, out m);
            FV o = FAdd(FApply(m, _fishTipOfs), v);
            return FAdd(o, new FV(_fishTipUp.X, _fishTipUp.Y, _fishTipUp.Z));
        }

        // FISH_bobberCommand: 0 = a frame of its animation, 2 / 11 / 12 = a nibble or a bite (a sound and a ripple), 4-7 / 14 = set states.
        // (Command 12 goes to state 0xB as 11 does: state 0xC's long dip is never reached in the game either.)
        void FishBobber(int command)
        {
            switch (command)
            {
                case 4: _sPos = FishRodTip(); _sSprite = 1; _sState = 4; _sTimer = -1; break;
                case 5: _sSprite = 1; _sState = 5; _sTimer = -1; break;
                case 6: _sSprite = 1; _sState = 6; _sTimer = -1; break;
                case 7: FishSpawnRipple(-1, _sPos, 0x64, 0, 0x1000); _sState = 7; _sTimer = -1; break;
                case 2: Fx(3); FishSpawnRipple(-3, _sPos, 0, 0, 0x1000); _sState = 0xA; _sTimer = -1; break;
                case 11:
                case 12: Fx(4); FishSpawnRipple(-4, _sPos, 0, 0, 0x1000); _sState = 0xB; _sTimer = -1; break;
                case 14: _sState = 0xE; _sTimer = -1; break;
                case 0:
                    switch (_sState)
                    {
                        case 3: _sSprite = 0; break;
                        case 7:
                            _sSprite = _fishSeq99C[_sTimer < 0 ? 0 : _sTimer >= 8 ? 7 : _sTimer];
                            if (_sSprite <= -1) { _sSprite = 3; _sState = 8; _sTimer = -1; }
                            break;
                        case 8:
                            if (FishRandom(1, 2) == 1) { _sPos.X += FishRandom(-1, 1); _sPos.Z += FishRandom(-1, 1); }
                            if (FishRandom(1, 0x3C) != 1) break;
                            FishSpawnRipple(-2, _sPos, 0, 0, 0x1000);
                            _sState = 9; _sTimer = -1;
                            break;
                        case 9:
                            {
                                int k = _sTimer / 3;
                                _sSprite = _fishSeqA4D8[k < 0 ? 0 : k >= 3 ? 2 : k];
                                if (_sSprite == -1) { _sSprite = 3; _sState = 8; _sTimer = -1; }
                                break;
                            }
                        case 10:
                            _sSprite = _fishSeqA4E4[_sTimer < 0 ? 0 : _sTimer >= 5 ? 4 : _sTimer];
                            if (_sSprite == -1) { _sSprite = 3; _sState = 8; _sTimer = -1; }
                            break;
                        case 12:
                            _sSprite = _fishSeqA4F8[_sTimer < 0 ? 0 : _sTimer >= 0x12 ? 0x11 : _sTimer];
                            if (_sSprite == -1) { _sSprite = 3; _sState = 8; _sTimer = -1; }
                            break;
                        case 11:
                            _sSprite = _fishSeqA540[_sTimer < 0 ? 0 : _sTimer >= 5 ? 4 : _sTimer];
                            if (_sSprite == -1) { _sState = 0xD; _sTimer = -1; }
                            break;
                        case 14:
                            _sSprite = _fishSeqA554[_sTimer < 0 ? 0 : _sTimer >= 6 ? 5 : _sTimer];
                            if (_sSprite == -1) { _sSprite = 3; _sState = 8; _sTimer = -1; }
                            break;
                    }
                    break;
                default: return;
            }
            _sTimer++;
        }

        void Fx(int n) { if (_audio != null) _audio.Play(8, n); }      // playSound(8, n): the map's sound bank

        // FISH_tickBobberPhysics: mode 1 on the rod's tip; modes 2-4 flying (drag, gravity, held at the line's length), kept over the picture
        void FishTickBobberPhysics()
        {
            FishBobber(0);
            switch (_sMode)
            {
                case 1: _sPos = FishRodTip(); break;
                case 2: case 3: case 4:
                    {
                        FV old = _sPos;
                        _sVel.X -= FDrag(_sVel.X); _sVel.Y -= FDrag(_sVel.Y); _sVel.Z -= FDrag(_sVel.Z);
                        _sVel.Y += _sGravity;
                        if (_sDist != 0)
                        {
                            int d = FDist(_sPos.X - _rLineStart.X, _sPos.Y - _rLineStart.Y, _sPos.Z - _rLineStart.Z);
                            if (_sDist < d)
                            {
                                FA rot = FToEuler(_sPos.X - _rLineStart.X, _sPos.Y - _rLineStart.Y, _sPos.Z - _rLineStart.Z);
                                _sPos = FAdd(FRotate(rot, new FV(0, 0, _sDist)), _rLineStart);
                                _sVel = FAdd(_sVel, new FV(_sPos.X - old.X, _sPos.Y - old.Y, _sPos.Z - old.Z));
                            }
                        }
                        _sPos = FAdd(_sPos, _sVel);
                        if (_sDist != 0)
                        {
                            int d = FDist(_sPos.X - _rLineStart.X, _sPos.Y - _rLineStart.Y, _sPos.Z - _rLineStart.Z);
                            if (_sDist < d)
                            {
                                FA rot = FToEuler(_sPos.X - _rLineStart.X, _sPos.Y - _rLineStart.Y, _sPos.Z - _rLineStart.Z);
                                _sPos = FAdd(FRotate(rot, new FV(0, 0, _sDist)), _rLineStart);
                            }
                        }
                        int x, y;
                        if (!FishProject(new FV(_sPos.X, 0, _sPos.Z), out x, out y) || !FishVisible(x, y))
                        {
                            _sPos.X = old.X; _sPos.Z = old.Z; _sVel.X = 0; _sVel.Z = 0;
                        }
                        FishTickBaitSprite();
                        break;
                    }
            }
        }

        // v * 300 / 4096 off each frame, at least 1 towards 0
        static int FDrag(int v) { int d = (v * 300) >> 12; return d != 0 ? d : (v > 0 ? 1 : v < 0 ? -1 : 0); }

        // ------------------------------------------------------------------ Seadramon
        void FishPushSeadramon(FA src)
        {
            for (int i = 4; i > 0; i--) _fishSeaHist[i] = _fishSeaHist[i - 1];
            _fishSeaHist[0] = src;
        }

        void FishSpawnSeadramon(Fish f)
        {
            f.Kind = 6;
            f.Size = FishRandom(_fishKinds[6].Lo, _fishKinds[6].Hi);
            f.Lifetime = FishRandom(0x258, 0x4B0);
            f.State = 0; f.FramesOnState = 0; f.Transparency = 0; f.TimeThisMove = 0;
            _fishSeaX = _fishMap.SeaX; _fishSeaY = _fishMap.SeaY;
            f.State = 0;
            f.Pos = FishMapToWorld(_fishSeaX[f.State], _fishSeaY[f.State]);
            f.State++;
            FV n = FishMapToWorld(_fishSeaX[f.State], _fishSeaY[f.State]);
            f.Rot = FToEuler(n.X - f.Pos.X, 0, n.Z - f.Pos.Z);
        }

        // FISH_tickSeadramon: along its path, turning at most 0x80 a frame and slowing for the turns; past the end it fades out
        void FishTickSeadramon(Fish f)
        {
            if (f.State >= _fishSeaX.Length)
            {
                f.TimeThisMove = FClamp(f.TimeThisMove + 0xA, 0, 0x32);
                f.Pos = FAdd(f.Pos, FRotate(f.Rot, new FV(0, 0, f.TimeThisMove)));
                f.Transparency = FClamp(f.Transparency - 8, 0, 0x80);
                if (f.Transparency <= 0) { f.Active = false; _fishPoolCount--; return; }
            }
            else
            {
                FV target = FishMapToWorld(_fishSeaX[f.State], _fishSeaY[f.State]);
                f.Transparency = FClamp(f.Transparency + 8, 0, 0x80);
                FA r = FToEuler(target.X - f.Pos.X, 0, target.Z - f.Pos.Z);
                int turn = (r.Y - f.Rot.Y) & 0xFFF;
                if (turn >= 0x800) turn -= 0x1000;
                f.TimeThisMove += Math.Abs(turn) != 0 ? -0xA : 0xA;
                f.TimeThisMove = FClamp(f.TimeThisMove, 0, 0x32);
                f.Rot.Y = (short)(f.Rot.Y + FClamp(turn, -0x80, 0x80));
                f.Pos = FAdd(f.Pos, FRotate(f.Rot, new FV(0, 0, f.TimeThisMove)));
                int d = FDist(target.X - f.Pos.X, 0, target.Z - f.Pos.Z);
                if (d <= (f.TimeThisMove > 0xA ? f.TimeThisMove : 0xA)) f.State++;
                f.TimeThisMove = d / 10 > 1 ? d / 10 : 1;
            }
            f.FramesOnState++;
            f.AnimPhase++;
            FishPushSeadramon(f.Rot);
        }

        // ------------------------------------------------------------------ the fish
        void FishSpawn(Fish f, int kind)
        {
            f.Kind = kind;
            f.Size = FishRandom(_fishKinds[kind].Lo, _fishKinds[kind].Hi);
            f.Lifetime = FishRandom(0x258, 0x4B0);
            f.State = 0; f.FramesOnState = 0; f.Transparency = 0;
            f.Pos = FishRandomPointInSpot(f.Kind);
            FV v = FishRandomPointInSpot(f.Kind);
            f.Rot = FToEuler(v.X - f.Pos.X, 0, v.Z - f.Pos.Z);
        }

        void FishPickNextMove(Fish f)
        {
            f.TimeNextMove = FishRandom(_fishKinds[f.Kind].DelayMin, _fishKinds[f.Kind].DelayMax);
            f.State = 0xA; f.FramesOnState = -1;
        }

        void FishFaceAway(Fish f, FV from)
        {
            f.Rot = FToEuler(from.X - f.Pos.X, 0, from.Z - f.Pos.Z);
            f.Rot.Y += 0x800;
            f.State = 0x1E; f.FramesOnState = -1;
        }

        // checkAttractor: a splash near it scares the fish off (and no fish comes for 30 frames); a fish that likes the bait swims to the float
        void FishCheckAttractor(Fish f)
        {
            if (_fishAttractor != null && FDist(_fishAttractor.Pos.X - f.Pos.X, 0, _fishAttractor.Pos.Z - f.Pos.Z) <= _fishAttractRadius)
            {
                _fishEscapeTimer = 0x1E;
                FishFaceAway(f, _fishAttractor.Pos);
                return;
            }
            if (_rBait != _fishKinds[f.Kind].Bait) return;
            int d = FDist(_sPos.X - f.Pos.X, 0, _sPos.Z - f.Pos.Z);
            if (d < 0xC9) return;
            if (d < 0x258)
            {
                FV sw = _sPos;
                f.Rot = FToEuler(sw.X - f.Pos.X, 0, sw.Z - f.Pos.Z);
                f.TimeThisMove = FDist(sw.X - f.Pos.X, 0, sw.Z - f.Pos.Z) / 8;
                f.TimeThisMove = FClamp(f.TimeThisMove, 3, 0x32);
                f.TimeThisMove = FClamp(f.TimeThisMove, 3, f.Size);
                f.State = 0x14; f.FramesOnState = -1;
            }
        }

        // FISH_tickFish: 0 fading in, 0xA waiting, 0x14 swimming to a point, 0x1E fading out
        void FishTick(Fish f)
        {
            if (f.Kind == 6) { FishTickSeadramon(f); return; }
            switch (f.State)
            {
                case 0:
                    f.Pos = FAdd(f.Pos, FRotate(f.Rot, new FV(0, 0, 0x1E)));
                    f.Transparency = FClamp(f.Transparency + 8, 0, 0x80);
                    if (f.Transparency != 0x80) break;
                    FishPickNextMove(f);
                    break;
                case 0xA:
                    if (--f.Lifetime == 0) { FishFaceAway(f, FishTamerPos); break; }
                    if (--f.TimeNextMove == 0)
                    {
                        FV target = FishRandomPointInSpot(f.Kind);
                        f.Rot = FToEuler(target.X - f.Pos.X, 0, target.Z - f.Pos.Z);
                        f.TimeThisMove = FDist(target.X - f.Pos.X, 0, target.Z - f.Pos.Z) / 8;
                        f.TimeThisMove = FClamp(f.TimeThisMove, 0, 0x32);
                        f.TimeThisMove = FClamp(f.TimeThisMove, 0, f.Size);
                        f.State = 0x14; f.FramesOnState = -1;
                        break;
                    }
                    FishCheckAttractor(f);
                    break;
                case 0x14:
                    f.Pos = FAdd(f.Pos, FRotate(f.Rot, new FV(0, 0, f.TimeThisMove)));
                    f.TimeThisMove -= f.TimeThisMove >> 3;
                    if (f.TimeThisMove < 8) { FishPickNextMove(f); break; }
                    FishCheckAttractor(f);
                    break;
                case 0x1E:
                    f.Pos = FAdd(f.Pos, FRotate(f.Rot, new FV(0, 0, 0x28)));
                    f.Transparency = FClamp(f.Transparency - 8, 0, 0x80);
                    if (f.Transparency == 0) { f.Active = false; _fishPoolCount--; }
                    break;
            }
            f.FramesOnState++;
            f.AnimPhase++;
        }

        void FishInitPool()
        {
            for (int i = 0; i < 33; i++) { if (_fishPool[i] == null) _fishPool[i] = new Fish(); _fishPool[i].Active = false; }
            _fishPoolNext = 1; _fishPoolCount = 0; _fishPoolFrames = 0; _fishEscapeTimer = 0;
        }

        Fish FishAlloc()
        {
            if (_fishPoolCount >= 33) return null;
            int p = _fishPoolNext;
            do
            {
                if (!_fishPool[p].Active) { _fishPoolNext = p; return _fishPool[p]; }
                p++; if (p >= 33) p = 0;
            } while (p != _fishPoolNext);
            return null;
        }

        bool FishSeadramonSpawned() { foreach (var f in _fishPool) if (f.Active && f.Kind == 6) return true; return false; }

        // FISH_tickFishPopulation: up to 32 fish, each new one of the kind with the fewest per its spot's weight; Seadramon at its hours
        void FishTickPopulation()
        {
            _fishPoolFrames++;
            if (_fishEscapeTimer > 0) _fishEscapeTimer--;
            else
            {
                if (_fishPoolCount < 0x20)
                {
                    int[] counts = new int[6]; int total = 0;
                    foreach (var f in _fishPool)
                    {
                        if (!f.Active || f.Kind < 0 || f.Kind >= 6) continue;
                        counts[f.Kind]++; total++;
                    }
                    for (int i = 0; i < 6; i++) counts[i] = FDiv(counts[i] * 10000, total * (i < _fishMap.Spots.Length ? _fishMap.Spots[i].Weight : 0));
                    int bestIdx = 0, best = 0x2711;
                    for (int i = 0; i < 6; i++) if (counts[i] <= best) { best = counts[i]; bestIdx = i; }
                    var nf = FishAlloc();
                    if (nf != null) { nf.Active = true; _fishPoolCount++; FishSpawn(nf, bestIdx); }
                }
                if (_fishVsync % 20 == 0 && (_fishMap.SeaTime1 != 0x270F || _fishMap.SeaTime2 != 0x270F) && _care != null)
                {
                    if (_fishMap.SeaTime1 / 100 == _care.Hour || _fishMap.SeaTime2 / 100 == _care.Hour)
                    {
                        if (!FishSeadramonSpawned() && !_hFish.Active)
                        {
                            var sea = FishAlloc();
                            if (sea != null) { sea.Active = true; _fishPoolCount++; FishSpawnSeadramon(sea); }
                        }
                    }
                }
            }
            for (int i = 0; i < 33; i++) if (_fishPool[i].Active) FishTick(_fishPool[i]);
        }

        // FISH_findFishToBite (US): a fish within 200 of the float, tested every 20th frame (Seadramon every 8th, 20 in 100); the chance comes
        // from FISH_BITE_CHANCE by the bait's item number (rows of 6 from item 0x26) and the fish's kind; each test leaves a dot
        Fish FishFindToBite(FV loc, int bait)
        {
            if (_hFish.Active) return null;
            int rowOff = (bait - 0x26) * 6;
            for (int i = 0; i < 33; i++)
            {
                var f = _fishPool[i];
                if (!f.Active) continue;
                if (FDist(f.Pos.X - loc.X, 0, f.Pos.Z - loc.Z) >= 0xC9) continue;
                int chance;
                if (f.Kind == 6)
                {
                    if (_fishVsync % 8 != 0) continue;
                    chance = 0x14;
                }
                else
                {
                    if (_fishVsync % 20 != 0) continue;
                    if (f.State != 0xA && f.State != 0x14) continue;
                    chance = 0;
                    if (bait >= 0x26 && bait < 0x47) { int k = rowOff + f.Kind; chance = k >= 0 && k < _fishBite.Length ? _fishBite[k] : 0; }
                }
                FishSpawnMarker(chance == 0 ? 0x30 : 0x2F, f.Pos);
                if (chance < FishRandom(1, 0x64)) continue;
                return f;
            }
            return null;
        }

        // FISH_hookFish: the fish becomes the hooked one and turns to the float; small fish nibble 20-40 frames, big ones 40-60
        void FishHook(Fish f)
        {
            FishClearMarkers();
            _hFish.CopyFrom(f);
            f.Active = false;
            _fishPoolCount--;
            _hFish.Rot = FToEuler(_sPos.X - _hFish.Pos.X, 0, _sPos.Z - _hFish.Pos.Z);
            _hFish.State = 10; _hFish.FramesOnState = 0; _hFish.AnimPhase = 0;
            _hAnimMode = 0; _hStrike = 0;
            if (_hFish.Size < 0x96) { _hNibble = FishRandom(0x14, 0x28); _hCatching = 0x1E; }
            else { _hNibble = FishRandom(0x28, 0x3C); _hCatching = 0x3C; }
            _hPower = _hFish.Size + 0x96;
            _hPowerMax = _hPower + 0x64;
            _hEscaped = 0;
        }

        // the hooked fish swims to the float, at most 10 a frame
        void FishHookedToFloat()
        {
            int dx = _sPos.X - _hFish.Pos.X, dz = _sPos.Z - _hFish.Pos.Z;
            int d = FDist(dx, 0, dz);
            if (d < 4) return;
            _hFish.Rot = FToEuler(dx, 0, dz);
            d = d <= 0 ? 1 : d >= 0xB ? 0xA : d;
            _hFish.Pos = FAdd(_hFish.Pos, FRotate(_hFish.Rot, new FV(0, 0, d)));
        }

        // up to 30 tries: a heading whose point 300 ahead of the float is on the water, else one towards a random point of its spot
        void FishHookedHeading()
        {
            for (int i = 0; i < 0x1E; i++)
            {
                FV probe = FAdd(FRotate(_hFish.Rot, new FV(0, 0, 0x12C)), _sPos);
                int x, y;
                if (FishProject(probe, out x, out y) && FishVisible(x, y) && FishIsWater(x, y)) continue;
                FV p = FishRandomPointInSpot(_hFish.Kind);
                _hFish.Rot = FToEuler(p.X - _hFish.Pos.X, 0, p.Z - _hFish.Pos.Z);
            }
        }

        void FishHookedGrabbed() { _hAnimMode = 1; _hFish.State = 0x28; _hFish.FramesOnState = -1; }

        void FishHookedEscape()
        {
            _hEscaped = 1;
            FishBobber(7);
            FV t = FishTamerPos;
            _hFish.Rot = FToEuler(t.X - _hFish.Pos.X, 0, t.Z - _hFish.Pos.Z);
            _hFish.Rot.Y += 0x800;
            _hFish.State = 0x5A; _hFish.FramesOnState = -1;
        }

        // FISH_tickHookedFish: mode 0 a frame; 0x10 struck (it fights), 0xF it gets away, 0x12 Seadramon pulled in, 0x11 gone (caught)
        void FishTickHooked(int mode)
        {
            if (!_hFish.Active) return;
            if (FishTextBoxVisible()) return;
            bool count = true;
            switch (mode)
            {
                case 0x10: FishHookedGrabbed(); break;
                case 0xF: FishHookedEscape(); break;
                case 0x12: _hFish.State = 0x64; _hFish.FramesOnState = -1; break;
                case 0x11: _hFish.Active = false; count = false; break;
                case 0: FishHookedFrame(); break;
                default: count = false; break;
            }
            if (count)
            {
                _hFish.FramesOnState++;
                _hFish.AnimPhase++;
                if (_hStrike != 0) _hStrike--;
            }
            if (_hFish.Kind == 6) FishPushSeadramon(_hFish.Rot);
        }

        void FishHookedFrame()
        {
            FV t = FishTamerPos;
            switch (_hFish.State)
            {
                case 0xA:
                    if (_hFish.FramesOnState != _hNibble)
                    {
                        // nibbles: small fish now and then (not in the last 20 frames), big ones a pull
                        if (_hFish.Size < 0x96) { if (FishRandom(1, 0x14) == 1 && _hNibble - _hFish.FramesOnState >= 0x14) FishBobber(2); }
                        else if (FishRandom(1, 0x14) == 1) FishBobber(0xC);
                        FishHookedToFloat();
                    }
                    else
                    {
                        // the bite: the float goes under and the bait is eaten
                        FishBobber(0xB);
                        _rItemTaken = 1;
                        int id = _state.Slots[_rItemSlot];
                        if (id != 0xFF) { _state.RemoveItem(id, 1); _state.SyncSlots(); }
                        _hFish.State = 0x14; _hFish.FramesOnState = -1;
                    }
                    break;
                case 0x14:
                    FishHookedToFloat();
                    if (_hFish.FramesOnState >= _hCatching) FishHookedEscape();
                    break;
                case 0x28:
                    {
                        if (FishRandom(0, 0xA) == 0) Fx(5);
                        if (FishRandom(1, 0xA) == 1)
                        {
                            _hFish.Rot = FToEuler(t.X - _hFish.Pos.X, 0, t.Z - _hFish.Pos.Z);
                            _hFish.Rot.Y += FishRandom(-0x384, 0x384) + 0x800;
                        }
                        if (FishRandom(1, 4) == 1)
                        {
                            FishSpawnRipple(0, _hFish.Pos, 0x2BC, _hFish.Size / 2, (_hFish.Size << 12) / 120);
                            // (the game works out a point a fish's length ahead here and does not use it)
                            FishSpawnRipple(0, _hFish.Pos, 0x2BC, 0, _hFish.Size / 2);
                        }
                        FishHookedHeading();
                        FV probe = FAdd(FRotate(_hFish.Rot, new FV(0, 0, _hFish.TimeThisMove / (_hPower <= _hPowerMax / 10 ? 0xF : 8))), _sPos);
                        _hFish.Pos = probe; _sPos = probe;
                        bool power = false;
                        if (_rPressed == -1) power = true;
                        else if (_rPressed >= 0x3D && _rPressed % 3 == 0) power = true;
                        else if (FishRandom(1, 0x14) < 3) power = true;
                        if (power && FishRandom(1, 0x1000) <= (_hPower << 12) / _hPowerMax)
                        {
                            // it swims off again, away from the boy
                            _hFish.Rot = FToEuler(t.X - _hFish.Pos.X, 0, t.Z - _hFish.Pos.Z);
                            _hFish.Rot.Y += FishRandom(-0x384, 0x384) + 0x800;
                            _hFish.TimeThisMove = FishRandom(0x14, 0x32);
                            _hNibble = FishRandom(9, 0x13);
                            _hFish.State = 0x32; _hFish.FramesOnState = -1;
                            break;
                        }
                        if (_rState != 0xB) FishHookedEscape();
                        break;
                    }
                case 0x32:
                    {
                        if (_hFish.FramesOnState % 2 == 0) _hPower = FClamp(_hPower - 1, 0, _hPowerMax);
                        if (FishRandom(0, 0xA) == 0) Fx(6);
                        FishSpawnRipple(0, _hFish.Pos, 0x2BC, 1, (_hFish.Size << 12) / 120);
                        FishHookedHeading();
                        FV probe = FAdd(FRotate(_hFish.Rot, new FV(0, 0, _hFish.TimeThisMove)), _sPos);
                        _hFish.Pos = probe; _sPos = probe;
                        if (_rState != 0xB) { FishHookedEscape(); break; }
                        if (_hFish.FramesOnState >= _hNibble) FishHookedGrabbed();
                        break;
                    }
                case 0x5A:
                    _hFish.Pos = FAdd(_hFish.Pos, FRotate(_hFish.Rot, new FV(0, 0, 0x28)));
                    _hFish.Transparency = FClamp(_hFish.Transparency - 8, 0, 0x80);
                    if (_hFish.Transparency != 0) break;
                    _hFish.Active = false;
                    break;
                case 0x64:
                    {
                        // Seadramon is drawn in towards FISH_REEL_TARGET, 50 a frame at most
                        int d = FClamp(FDist(_fishReelTarget.X - _hFish.Pos.X, 0, _fishReelTarget.Z - _hFish.Pos.Z), 0, 0x32);
                        _hFish.Rot = FToEuler(_fishReelTarget.X - _hFish.Pos.X, 0, _fishReelTarget.Z - _hFish.Pos.Z);
                        _hFish.Pos = FAdd(_hFish.Pos, FRotate(_hFish.Rot, new FV(0, 0, d)));
                        FishSpawnRipple(0, _hFish.Pos, 0x2BC, 1, (_hFish.Size << 12) / 120);
                        if (FishRandom(1, 0xA) < 6) Fx(6);
                        break;
                    }
            }
        }

        // FISH_tryStrike: Cross in the first half of the bite hooks it
        bool FishTryStrike()
        {
            if (_hFish.State == 0x14 && _hFish.FramesOnState < _hCatching / 2)
            {
                _hStrike = 0x14;
                FishTickHooked(0x10);
                return true;
            }
            FishTickHooked(0xF);
            return false;
        }

        // ------------------------------------------------------------------ the rod
        // FISH_updateRodPose: the rod's five segments from bone 9 (a fixed turn, then (1, 19, -48)), bending towards the float by the tension
        void FishUpdateRodPose()
        {
            if (_player == null) return;
            FV v0; Matrix4x4 boneRot;
            FishBone(9, out v0, out boneRot);
            Matrix4x4 m1 = FYXZ(new FA(0x400, 0x400, 0));
            Matrix4x4 m2 = FYXZ(FToEuler(-6, 0x34, 0x127));
            Matrix4x4 m = boneRot * (m1 * m2);
            v0 = FAdd(v0, FApply(m, _fishRodBaseOfs));
            int len = _tDisplay ? ((_tLevel + _tLimit) << 9) / (_tLimit * 2) : 0x80;
            FV d = new FV(_sPos.X - v0.X, _sPos.Y - v0.Y, _sPos.Z - v0.Z);
            FV lv = FApply(m.transpose, d);
            FA e = FToEuler(lv.X, lv.Y, lv.Z);
            int ang = FRatan2(lv.Y, lv.X);
            int limX = (short)((-(len * FRsin(ang))) >> 12), limY = (short)((len * FRcos(ang)) >> 12);
            e.X = e.X > 0 ? (e.X < limX ? e.X : limX) : (e.X > limX ? e.X : limX);
            e.Y = e.Y > 0 ? (e.Y < limY ? e.Y : limY) : (e.Y > limY ? e.Y : limY);
            for (int k = 1; k < 4; k++) _fishRodSegRot[k] = new FA(e.X >> 2, e.Y >> 2, e.Z >> 2);
            for (int k = 0; k < 5; k++)
            {
                m = m * FYXZ(_fishRodSegRot[k]);
                _fishRodSegPos[k] = v0;
                _fishRodSegMat[k] = m;
                v0 = FAdd(v0, FApply(m, _fishRodOffsets[k]));
            }
            _rLineStartMirror = _rLineStart;
            _rLineStart = _fishRodSegPos[4];
        }

        // FISH_tickRod: the fishing states. 2 the camera to the boy, 3 the bait menu, 1 raising the rod (Left / Right turn him), 0xE96 the camera to
        // the float, 6 aiming, 7 winding up (Cross again throws, harder the longer it was held), 8 the throw, 9 waiting (Cross held reels in),
        // 0xA a bite (Cross strikes), 0xB the fight, 0xD the catch comes in, 0xE putting the rod away, 0x4B0 / 0x4B1 line cut / hook lost,
        // 0x44C something got stuck, 0x11AF Seadramon pulled in
        void FishTickRod()
        {
            if (FishTextBoxVisible()) { FishScrollViewToTarget(); return; }
            int n; uint r;
            switch (_rState)
            {
                case 2: goto state2;
                case 3: goto state3;
                case 1: goto state1;
                case 0xE96: goto stateE96;
                case 6: goto state6;
                case 7: goto state7;
                case 8: goto state8;
                case 9: goto state9;
                case 0xA: goto stateA;
                case 0xB: goto stateB;
                case 0xD: goto stateD;
                case 0xE: goto stateE;
                case 0x4B0: goto state4B0;
                case 0x4B1: goto state4B1;
                case 0x44C: goto state44C;
                case 0x11AF: goto state11AF;
                default: goto tick;
            }

        giveUp:
            if (!FishHasFood()) goto toStateE;
            if (_anim != null) _anim.SetSlot(IdleSlot);
            _rDisplaySwimmer = false;
            _iShown = false;
            FishResetBobber();
            _rDisplayLine = false;
            _sMode = 0;
            _rState = 2; _rFrames = -1;
            goto tick;

        state2:
            if (FishPanToTamer(0x10)) goto tick;
            _rState = 3; _rFrames = -1;
            goto tick;

        state3:
            if (_rFrames == 0) { FishOpenBaitMenu(); goto tick; }
            if (FishBaitMenuVisible()) goto tick;
            if (_fbSelected == -1) goto toStateE;
            _rItemSlot = _fbSelected;
            _rBait = _state.Slots[_rItemSlot];
            _rItemTaken = 0;
            _rDisplaySwimmer = false;
            FishAnim(0x1B);
            _rState = 1; _rFrames = -1;
            goto tick;

        state1:
            if ((_fishPad & FishLeft) != 0) FishTamerVy = FishTamerVy - 0x40;
            if ((_fishPad & FishRight) != 0) FishTamerVy = FishTamerVy + 0x40;
            if (FishAnimRunning()) goto tick;
            _rDisplaySwimmer = true;

        cast:
            _sPos = FishRodTip();
            if (_rItemTaken != 0) goto giveUp;
            FishAnim(0x1C);
            FishBobber(4);
            _sMode = 1;
            _rDisplayLine = true;
            FishShowBait(null, _rBait);
            FishMoveBaitToRodTip(0);
            _rState = 0xE96; _rFrames = -1;
            goto tick;

        stateE96:
            if (FishPanTo(0x10, _sPos, 0x28, 0x1E)) goto tick;
            _rState = 6; _rFrames = -1;
            goto tick;

        state6:
            FishMoveBaitToRodTip(0);
            if ((_fishPad & FishAlt) != 0) { FishPanWithDPad(); goto tick; }
            if (FishFresh(FishConfirm)) goto throwRod;
            if (FishFresh(FishCancel)) { _iShown = false; goto toStateE; }
            if ((_fishPad & FishLeft) != 0) FishTamerVy = FishTamerVy - 0x40;
            if ((_fishPad & FishRight) != 0) FishTamerVy = FishTamerVy + 0x40;
            FishPanTo(0x10, _sPos, 0x28, 0x1E);
            goto tick;

        throwRod:
            FishAnim(0x1D);
            _sMode = 2;
            _sVel = new FV(0, 0, 0);
            _sDist = FDist(_sPos.X - _rLineStart.X, _sPos.Y - _rLineStart.Y, _sPos.Z - _rLineStart.Z);
            _sGravity = 0x1E;
            _rState = 7; _rFrames = -1;
            goto tick;

        state7:
            if (FishFresh(FishConfirm)) goto release;
            if (FishFresh(FishAlt)) { _rFrames = 0x14; goto release; }
            FishPanTo(0x10, _sPos, 0x28, 0x1E);
            if (!FishAnimRunning()) goto cast;
            goto tick;

        release:
            {
                FishAnim(0x1E);
                FishUpdateRodPose();
                _rThrowPower = _rFrames + 5;
                _sMode = 3;
                _sPos = _rLineStart;
                _sDist = 0;
                _sGravity = 3;
                FA v = _fishThrowVec;
                v.Z = FishRemap(_rThrowPower, 0, 0x14, 0x64, _rSwimmerType == 1 ? 0xC8 : 0x190);
                FA rot = new FA(0, FishTamerVy + 0x800, -0xC8);
                _sVel = FApply(FYXZ(rot), v);
                Fx(0);
                _rState = 8; _rFrames = -1;
                goto tick;
            }

        state8:
            FishPanTo(0, _sPos, 0x28, 0x1E);
            if ((_fishPad & FishAlt) != 0) FishPanWithDPad();
            else FishPanTo(0x10, _sPos, 0x28, 0x1E);
            if (FishFresh(FishConfirm)) { _sVel.X = 0; _sVel.Z = 0; }
            if (_sPos.Y < 0)
            {
                if (FishFresh(FishCancel)) goto cast;
                goto tick;
            }

        splash:
            {
                _iShown = false;
                _sPos.Y = 0;
                int x, y;
                if (!FishProject(_sPos, out x, out y)) goto cast;
                if (!FishVisible(x, y)) goto cast;
                if (!FishIsWater(x, y)) goto cast;
                Fx(2);
                if (_anim != null) _anim.LoopOnce();
                FishBobber(7);
                _sMode = 0;
                _rState = 9; _rFrames = -1;
                goto tick;
            }

        state9:
            if ((_fishPad & FishConfirm) != 0)
            {
                // reeling in: the float comes towards the rod by what the rod's tip moved along the line
                if (FishFresh(FishConfirm) || !FishAnimRunning()) FishAnim(0x20);
                Matrix4x4 m2 = FYXZ(FToEuler(_rLineStart.X - _sPos.X, 0, _rLineStart.Z - _sPos.Z));
                FV dd = new FV(_rLineStart.X - _rLineStartMirror.X, _rLineStart.Y - _rLineStartMirror.Y, _rLineStart.Z - _rLineStartMirror.Z);
                FV lv = FApply(m2.transpose, dd);
                lv.X = 0; lv.Y = 0;
                if (lv.Z > 0) _sPos = FAdd(_sPos, FApply(m2, lv));
                // something takes the bait off a bare hook now and then: 1 in 10 every 20th frame after 30
                if (_rItemTaken != 0 && _rFrames >= 0x1F && _rFrames % 20 == 0 && FishRandom(1, 0x2710) < 0x3E9)
                {
                    r = (uint)FishRandom(0, 0x63);
                    for (n = 0; n < 6; n++) if (r <= _fishBaitThreshold[n]) break;
                    if (n >= 6) n--;
                    _rBait = _fishBaitItem[n];
                    goto stolen;
                }
                goto visibleCheck;
            }
            if (((_fishPadPrev & ~_fishPad) & FishConfirm) != 0 || !FishAnimRunning()) FishAnim(0x1F);
            if (_rItemTaken == 0)
            {
                Fish f = FishFindToBite(_sPos, _rBait);
                if (f != null) { FishHook(f); goto hooked; }
            }

        visibleCheck:
            {
                int x, y;
                if (!FishProject(_sPos, out x, out y)) goto cast;
                if (!FishVisible(x, y)) goto cast;
                if (!FishIsWater(x, y)) goto cast;
                if (FishInCastRange(_sPos, 0)) goto cast;
                if ((_fishPad & FishAlt) != 0) { FishPanWithDPad(); goto tick; }
                FishPanTo(0x10, _sPos, 0x28, 0x1E);
                if (FishFresh(FishCancel)) goto cast;
                goto tick;
            }

        hooked:
            _rState = 0xA; _rFrames = -1;
            goto tick;

        stateA:
            FishPanTo(0x10, _sPos, 0x28, 0x1E);
            if (_hEscaped != 0 || !_hFish.Active)
            {
                FishShowTextBox(_fishText[0], null, null, null, _sPos);
                goto splash;
            }
            if (FishFresh(FishConfirm))
            {
                if (!FishTryStrike())
                {
                    if (_rItemTaken != 0) { FishShowTextBox(_fishText[1], _fishText[2], null, null, _sPos); goto splash; }
                    FishShowTextBox(_fishText[3], _fishText[4], null, null, _sPos);
                    goto splash;
                }
                FishShowTextBox(_fishHooked, null, null, null, _sPos);
                goto beginTension;
            }
            if (!FishFresh(FishCancel)) goto tick;
            FishTickHooked(0xF);
            goto splash;

        beginTension:
            FishBeginTension();
            _rPressed = 0;
            _rState = 0xB; _rFrames = -1;
            goto tick;

        stateB:
            {
                if ((_fishPad & FishConfirm) != 0)
                {
                    if (_rPressed < 0) _rPressed = 0;
                    _rPressed++;
                    if (FishFresh(FishConfirm) || !FishAnimRunning()) FishAnim(0x22);
                    Matrix4x4 m3 = FYXZ(FToEuler(_rLineStart.X - _sPos.X, 0, _rLineStart.Z - _sPos.Z));
                    FV d2 = new FV(_rLineStart.X - _rLineStartMirror.X, _rLineStart.Y - _rLineStartMirror.Y, _rLineStart.Z - _rLineStartMirror.Z);
                    FV lv2 = FApply(m3.transpose, d2);
                    lv2.X = 0; lv2.Y = 0;
                    lv2.Z = (lv2.Z * 200) / 100;
                    lv2.Z -= FishRemap(_hFish.Size, 0x32, 0x12C, 1, 0xA);
                    if (lv2.Z > 0)
                    {
                        d2 = FApply(m3, lv2);
                        d2.X /= 2; d2.Y /= 2; d2.Z /= 2;
                        _sPos = FAdd(_sPos, d2);
                    }
                }
                else
                {
                    if (_rPressed > 0) _rPressed = 0;
                    _rPressed--;
                    if (((_fishPadPrev & ~_fishPad) & FishConfirm) != 0) FishAnim(0x21);
                }
                int x, y;
                if (FishProject(_sPos, out x, out y) && FishVisible(x, y) && !FishIsWater(x, y)) goto landed;
                if (FishInCastRange(_sPos, _hFish.Size)) goto landed;
                switch (FishUpdateTension())
                {
                    case 1: FishTickHooked(0xF); FishResetTension(); goto lineSnap;
                    case 2: FishTickHooked(0xF); FishResetTension(); goto fishEscaped;
                }
                if (FishFresh(FishCancel)) { FishTickHooked(0xF); FishResetTension(); goto splash; }
                if ((_fishPad & FishAlt) != 0) { FishPanWithDPad(); goto tick; }
                FishPanTo(0x10, _sPos, 0x28, 0x1E);
                goto tick;
            }

        landed:
            FishResetTension();
            if (_hFish.Kind == 6) { FishTickHooked(0x12); goto toState11AF; }
            FishTickHooked(0x11);
            _rBait = _hFish.Kind + 0x3E;
            Fx(7);
            FishShowBait(_sPos, _rBait);
            FishSpawnRipple(0, _sPos, 1, 0, _fishMap.Unk8 * 10);
            _sMode = 0;
            _rState = 0xD; _rFrames = -1;
            goto tick;

        stateD:
            {
                bool moving = FishMoveBaitToRodTip(0x28);
                _sPos = _iPos;
                bool panning = FishPanTo(0x20, _sPos, 0x28, 0x1E);
                if (moving) goto tick;
                if (panning) goto tick;
                FishShowBait(null, _rBait);
                FishMoveBaitToRodTip(0);
                // the US texts: the metres of a "Great!" fish (200 or more) are size / 200 and the rest size % 200 (a 250 cm fish reads
                // "1 m 50 cm"), the original's own sum, kept (PROGRESS 5c)
                int size = _hFish.Size;
                string msg;
                if (size >= 0xC8) msg = FishFormat(_fishText[5], size / 200, size % 200);
                else if (size >= 0x64) msg = FishFormat(_fishText[6], size / 100, size % 100);
                else msg = FishFormat(_fishText[7], size % 100, 0);
                int kind = _hFish.Kind;
                if (kind >= 0 && kind < FishRecordSize.Length && size > FishRecordSize[kind]) FishRecordSize[kind] = size;
                _state.Para.FishCaught++;
                Fx(8);
                string name = _itemCatalog != null && _rBait >= 0 && _rBait < _itemCatalog.Count ? _itemCatalog[_rBait].Name : "";
                bool given = _state.AddItem(_rBait, 1);
                _state.SyncSlots();
                if (!given) FishShowTextBox(name, msg, _fishText[8], _fishText[9], _iPos);
                else FishShowTextBox(name, msg, _fishText[8], null, _iPos);
                goto finish;
            }

        finish:
            FishAnim(0x1C);
            FishBobber(4);
            _sMode = 1;
            _rDisplayLine = true;
            FishTamerVy = 0;        // (mapId == 8 ? 0 : mapId == 0xA ? 0 : 0)
            FishShowBait(null, _rBait);
            FishMoveBaitToRodTip(0);

        toStateE:
            _rState = 0xE; _rFrames = -1;
            goto tick;

        stateE:
            if (FishPanToTamer(0x10)) goto tick;
            FishEnd();
            goto tick;

        lineSnap:
            Fx(9);
            _rDisplayLine = false;
            FishBobber(0xE);
            _rState = 0x4B0; _rFrames = -1;
            goto tick;

        state4B0:
            if (_rFrames != 0xA) goto tick;
            Fx(0xA);
            FishShowTextBox(_fishText[10], _fishText[11], _fishText[12], null, _sPos);
            goto toStateE;

        fishEscaped:
            Fx(9);
            _rDisplayLine = true;
            FishBobber(0xE);
            _rState = 0x4B1; _rFrames = -1;
            goto tick;

        state4B1:
            if (_rFrames != 0xA) goto tick;
            Fx(0xA);
            FishShowTextBox(_fishText[13], _fishText[11], _fishText[12], null, _sPos);
            goto splash;

        stolen:
            Fx(9);
            FishShowTextBox(_fishText[14], _fishText[15], null, null, _sPos);
            _rState = 0x44C; _rFrames = -1;
            goto tick;

        state44C:
            {
                FishShowBait(null, _rBait);
                FishMoveBaitToRodTip(0);
                Fx(8);
                string name = _itemCatalog != null && _rBait >= 0 && _rBait < _itemCatalog.Count ? _itemCatalog[_rBait].Name : "";
                bool given = _state.AddItem(_rBait, 1);
                _state.SyncSlots();
                if (!given) FishShowTextBox(name, _fishText[16], _fishText[17], null, _iPos);
                else FishShowTextBox(name, _fishText[18], null, null, _iPos);
                goto finish;
            }

        toState11AF:
            _rState = 0x11AF; _rFrames = -1;
            goto tick;

        state11AF:
            if (FishPanToTamer(0x10)) goto tick;
            if (FDist(_fishReelTarget.X - _hFish.Pos.X, 0, _fishReelTarget.Z - _hFish.Pos.Z) >= 0x32) goto tick;
            _fishCatchComplete = true;

        tick:
            _rFrames++;
            FishTickBobberPhysics();
        }

        // startAnimation(TAMER, n) and animFlag bit 0
        void FishAnim(int slot) { if (_anim != null) _anim.SetSlot(slot, true); }
        bool FishAnimRunning() { return _anim != null && _anim.Running; }

        // sprintf with the texts' %d
        static string FishFormat(string fmt, int a, int b)
        {
            var sb = new System.Text.StringBuilder(); int arg = 0;
            for (int i = 0; i < fmt.Length; i++)
            {
                if (fmt[i] == '%' && i + 1 < fmt.Length && fmt[i + 1] == 'd') { sb.Append(arg++ == 0 ? a : b); i++; }
                else sb.Append(fmt[i]);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ drawing (FISH_renderFishingWorld), once a Unity frame from LateUpdate
        // world pieces at ordering place 0x20 and up scroll with the picture: they are drawn as quads at their view depth in front of the camera
        // (as the technique effects' screen primitives), so the map's models sort with them as the ordering table does
        void FishDrawWorld()
        {
            if (!_fishOn || _fishDisabled || _cam == null || _map == null) return;
            _fishMeshUsed = 0;
            if (_fishMp == null) _fishMp = new MaterialPropertyBlock();
            for (int i = 0; i < 33; i++) if (_fishPool[i].Active) FishDrawFish(_fishPool[i].Kind, _fishPool[i].Pos, _fishPool[i].Rot, _fishPool[i].Size, _fishPool[i].AnimPhase, _fishPool[i].Transparency, false);
            if (_hFish.Active) FishDrawFish(_hFish.Kind, _hFish.Pos, _hFish.Rot, _hFish.Size, _hFish.AnimPhase, _hFish.Transparency, _hFish.State >= 0x1F);
            for (int i = 0; i < 16; i++) if (_fishRipples[i].Active) FishDrawRipple(_fishRipples[i]);
            for (int i = 0; i < 16; i++) if (_fishMarkers[i].Active) FishDrawMarker(_fishMarkers[i]);
            if (!_rDisplayRod) return;
            if (_rDisplaySwimmer) { FishDrawRod(); FishDrawBobber(); }
            if (_rDisplayLine) FishDrawLine();
            FishDrawBait();
        }

        /// <summary>From OnGUI: the boxes and the gauge (ordering places under 0x20 stay on the screen).</summary>
        void DrawFishing()
        {
            if (!_fishOn || _fishView2D == null) return;
            DrawStrikeIndicator();
            var v = _fishView2D;
            // FISH_drawTensionGauge (drawn with the world, only while the rod is out)
            v.Gauge = !_fishDisabled && _rDisplayRod && _tDisplay;
            if (v.Gauge) { v.GaugeBars = FishRemap(_tLevel, -_tLimit, _tLimit, 0, 9); v.GaugeColour = _tColour; }
            v.Draw();
            // ours (mod Tension value, title > OPTIONS): the gauge's value under it, 0% = the hook comes out, 100% = the line snaps
            if (!v.Gauge || !ModSettings.TensionValue) { if (_fishPlate != null) _fishPlate.Remove(); return; }
            if (_fishPlate == null) _fishPlate = FrameBox.Load(_disc, Battle.StatFont, 4);
            if (_fishPlate == null) return;
            int pct = _tLimit > 0 ? (int)((_tLevel + _tLimit) * 100L / (2L * _tLimit)) : 0;
            int left = _fishGaugeX - _fishSprites[31].Ox, top = _fishGaugeY - _fishSprites[31].Oy + Math.Abs(_fishSprites[31].V1 - _fishSprites[31].V0);
            int w = Math.Abs(_fishSprites[31].U1 - _fishSprites[31].U0);
            _fishPlate.ShowText(new RectInt(left + w / 2 - 18, top + 2, 36, 10), pct + "%");
        }

        FrameBox _fishPlate, _fishStrikePlate;

        // ours (mod Fishing indicator, title > OPTIONS): "PRESS X" over the float while Cross would hook the fish: the bite (rod state 0xA,
        // hooked fish state 0x14) in the first half of its catching time, as FISH_tryStrike tests it
        void DrawStrikeIndicator()
        {
            bool on = ModSettings.FishingIndicator && !_fishDisabled && _rState == 0xA && _hFish.Active && _hEscaped == 0
                      && _hFish.State == 0x14 && _hFish.FramesOnState < _hCatching / 2 && !FishTextBoxVisible() && FishDepth(_sPos) > 0f;
            if (!on) { if (_fishStrikePlate != null) _fishStrikePlate.Remove(); return; }
            if (_fishStrikePlate == null) _fishStrikePlate = FrameBox.Load(_disc, Battle.StatFont, 4);
            if (_fishStrikePlate == null) return;
            Vector2 p = Ps1Point(ToUnity(new Vector3(_sPos.X, _sPos.Y, _sPos.Z)));
            int x = Mathf.Clamp((int)p.x - 22, -156, 112), y = Mathf.Clamp((int)p.y - 30, -116, 106);
            _fishStrikePlate.ShowText(new RectInt(x, y, 44, 10), "PRESS X");
        }

        Mesh FishMesh()
        {
            if (_fishMeshUsed >= _fishMeshes.Count) { var m = new Mesh(); m.MarkDynamic(); _fishMeshes.Add(m); _fishAssets.Add(m); }
            return _fishMeshes[_fishMeshUsed++];
        }

        Material FishMaterial(int blend)      // -1 solid, 0-3 a PS1 blend
        {
            if (_fishMatSolid == null)
            {
                var sh = Shader.Find("DW1Remake/Effect");
                if (sh == null) return null;
                _fishMatSolid = new Material(sh);
                _fishMatSolid.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha); _fishMatSolid.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _fishMatSolid.SetFloat("_BlendOp", (float)UnityEngine.Rendering.BlendOp.Add);
                _fishAssets.Add(_fishMatSolid);
            }
            if (blend < 0) return _fishMatSolid;
            if (_fishMatBlend[blend & 3] == null)
            {
                var m = new Material(_fishMatSolid);
                MmdModel.SetPsxBlend(m, blend & 3);
                _fishMatBlend[blend & 3] = m; _fishAssets.Add(m);
            }
            return _fishMatBlend[blend & 3];
        }

        // a PS1 screen point (from the picture's middle) at a view depth in game units, as a point in the world in front of our camera
        Vector3 FishToWorld(Vector2 p, float depth)
        {
            Rect pic = UiKit.Picture;
            float sx = pic.x + (p.x + 160f) / 320f * pic.width, sy = Screen.height - (pic.y + (p.y + 120f) / 240f * pic.height);
            float dist = Mathf.Clamp(depth * Unit, _cam.nearClipPlane * 1.05f, _cam.farClipPlane * 0.98f);
            return _cam.ScreenToWorldPoint(new Vector3(sx, sy, dist));
        }

        void FishQuad(Vector2[] s, float depth, Texture tex, Vector2[] uv, Color col, Material mat)
        {
            if (mat == null || tex == null) return;
            var mesh = FishMesh();
            var v = new Vector3[4];
            for (int i = 0; i < 4; i++) v[i] = FishToWorld(s[i], depth);
            mesh.Clear();
            mesh.vertices = v; mesh.uv = uv; mesh.triangles = new[] { 0, 1, 2, 2, 1, 3 };
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
            _fishMp.SetTexture("_MainTex", tex);
            _fishMp.SetColor("_Color", col);
            _fishMp.SetVector("_Rect", new Vector4(0, 0, 1, 1));
            _fishMp.SetFloat("_VertexColor", 0f);
            Graphics.DrawMesh(mesh, Matrix4x4.identity, mat, 0, null, 0, _fishMp);
        }

        // a FishSprite's quad: solid colours drawn as they are; with flags bit 2 its STP colours blended by abr = flags & 3 (the PS1 blends
        // only those texels). Colour = the primitive's RGB (0x80 = as the picture is)
        void FishSpriteQuad(Vector2[] s, float depth, FishSprite d, Color col, int flags)
        {
            int tpage = d.Tpage | ((flags & 3) << 5);
            var uv = new[] { FUv(d.U0, d.V0), FUv(d.U1, d.V0), FUv(d.U0, d.V1), FUv(d.U1, d.V1) };
            if ((flags >> 2) == 0) { FishQuad(s, depth, _fishVram.Page(tpage, d.Clut, 0), uv, col, FishMaterial(-1)); return; }
            FishQuad(s, depth, _fishVram.Page(tpage, d.Clut, 1), uv, col, FishMaterial(-1));
            FishQuad(s, depth, _fishVram.Page(tpage, d.Clut, 2), uv, col, FishMaterial(flags & 3));
        }

        // picture pixels / 256 from the top: DW1Effect turns v over itself (Unity textures count from the bottom)
        static Vector2 FUv(int u, int v) { return new Vector2(u / 256f, v / 256f); }
        static Color FCol(int rgb) { return new Color((rgb & 0xFF) / 128f, ((rgb >> 8) & 0xFF) / 128f, ((rgb >> 16) & 0xFF) / 128f, 1f); }

        // FISH_drawSprite3D: the sprite's rectangle in its own x / y plane, turned (RotMatrixYXZ) and scaled, at ordering place avg depth / 16 + depth
        void FishSprite3D(FV pos, FA rot, int sx, int sy, FishSprite d, int rgb, int flags, int depthOfs)
        {
            Matrix4x4 m = FYXZ(rot);
            int w = Math.Abs(d.U1 - d.U0), h = Math.Abs(d.V1 - d.V0);
            int x0 = (-d.Ox * sx) >> 12, x1 = (sx * (-d.Ox + w)) >> 12, y0 = (-d.Oy * sy) >> 12, y1 = (sy * (-d.Oy + h)) >> 12;
            Vector3[] c = { new Vector3(x0, y0, 0), new Vector3(x1, y0, 0), new Vector3(x0, y1, 0), new Vector3(x1, y1, 0) };
            var s = new Vector2[4]; float zs = 0f;
            for (int i = 0; i < 4; i++)
            {
                Vector3 g = m.MultiplyVector(c[i]) + new Vector3(pos.X, pos.Y, pos.Z);
                float z = Vector3.Dot(g - _vpG, _fG);
                if (z <= 0f) return;
                zs += z;
                s[i] = Ps1Point(ToUnity(g));
            }
            int ot = ((int)(zs / 4f) >> 4) + depthOfs;
            FishSpriteQuad(s, ot * 16f, d, FCol(rgb), flags);
        }

        // FISH_drawFishSprite: a big fish's 8 frames or a small one's 4 at the map's scale, lying on the water (x -0x400), dark by its fade and
        // subtracted (flags 6); hooked and fighting, the 6 frames of the leaping fish. Seadramon (kind 6) is its model
        void FishDrawFish(int kind, FV pos, FA rot, int size, int phase, int transparency, bool fighting)
        {
            if (kind == 6) { FishDrawSeadramon(pos); return; }
            int sprite, scale;
            if (!fighting)
            {
                if (size >= 0x64) { sprite = _fishLargeFrames[(phase / 3) & 7]; scale = size * _fishMap.ScaleLarge; }
                else { sprite = _fishSmallFrames[(phase / 4) & 3]; scale = size * _fishMap.ScaleSmall; }
            }
            else
            {
                if (size >= 0x64) { sprite = _fishHookLarge[(int)((uint)phase % 6)]; scale = size * 170; }
                else { sprite = _fishHookSmall[(int)((uint)phase % 6)]; scale = size * 0x155; }
            }
            int shade = transparency * 30 / 128;
            int colour = (shade & 0xFF) | ((shade & 0xFF) << 8) | ((shade & 0xFF) << 16);
            if (sprite < 0 || sprite >= _fishSprites.Length) return;
            FishSprite3D(pos, new FA(-0x400, rot.Y, 0), scale, scale, _fishSprites[sprite], colour, 6, 0x64);
        }

        // FISH_drawRipple: kind 0 = FISH_SPRITES[n - 1], the others [n + 4]; added (flags 5), 10 places nearer
        void FishDrawRipple(FishRipple s)
        {
            if (s.Sprite <= 0) return;
            int idx = s.Kind == 0 ? s.Sprite - 1 : s.Sprite + 4;
            if (idx < 0 || idx >= _fishSprites.Length) return;
            int sc = (_fishMap.Scale * s.Scale) >> 12;
            FishSprite3D(s.Pos, new FA(0, 0, 0), sc, sc, _fishSprites[idx], 0x808080, 5, -10);
        }

        // FISH_drawMarker: FISH_drawSprite2D at the projected point, ordering place 0x42
        void FishDrawMarker(FishMarker s)
        {
            if (FishDepth(s.Pos) <= 0f || s.Sprite < 0 || s.Sprite >= _fishSprites.Length) return;
            var d = _fishSprites[s.Sprite];
            Vector2 p = Ps1Point(ToUnity(new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z)));
            int w = Math.Abs(d.U1 - d.U0), h = Math.Abs(d.V1 - d.V0);
            float x0 = p.x - d.Ox, y0 = p.y - d.Oy;
            var q = new[] { new Vector2(x0, y0), new Vector2(x0 + w, y0), new Vector2(x0, y0 + h), new Vector2(x0 + w, y0 + h) };
            FishSpriteQuad(q, 0x42 * 16f, d, FCol(0x808080), 0);
        }

        // FISH_bobberCommand 1: the float's picture (the Old Rod's from sprite 0x11, the Amazing Rod's from 0x18), solid, 10 places nearer
        void FishDrawBobber()
        {
            if (_sSprite <= 0) return;
            int idx = _sSprite - 1 + (_rSwimmerType == 1 ? 0x11 : 0x18);
            if (idx < 0 || idx >= _fishSprites.Length) return;
            FishSprite3D(_sPos, new FA(0, 0, 0), _fishMap.Scale, _fishMap.Scale, _fishSprites[idx], 0x808080, 0, -10);
        }

        // FISH_drawBaitSprite: the item's picture (renderItemSprite's) as a square of radius unk8 at its depth, only above the water
        void FishDrawBait()
        {
            if (!_iShown || _iPos.Y >= 0) return;
            float z = FishDepth(_iPos);
            if (z <= 0f || z >= 0x10000) return;
            var icons = _menus != null ? _menus.Icons : null;
            if (icons == null || icons.Sheet == null) return;
            float r = _fishMap.Unk8 * _h / z;
            Vector2 p = Ps1Point(ToUnity(new Vector3(_iPos.X, _iPos.Y, _iPos.Z)));
            var q = new[] { new Vector2(p.x - r, p.y - r), new Vector2(p.x + r, p.y - r), new Vector2(p.x - r, p.y + r), new Vector2(p.x + r, p.y + r) };
            Rect uv = icons.Uv(_iSprite);
            var uvs = new[] { new Vector2(uv.xMin, 1f - uv.yMax), new Vector2(uv.xMax, 1f - uv.yMax), new Vector2(uv.xMin, 1f - uv.yMin), new Vector2(uv.xMax, 1f - uv.yMin) };      // from the top (DW1Effect turns v over)
            FishQuad(q, z, icons.Sheet, uvs, Color.white, FishMaterial(-1));
        }

        // FISH_drawFishingLine: from the rod's tip to the float in the gauge's colour, cut to the picture, in pieces of about 64 pixels
        void FishDrawLine()
        {
            FV[] ends = { _rLineStart, _sPos };
            var p = new Vector2[2]; var z = new float[2];
            for (int i = 0; i < 2; i++)
            {
                z[i] = FishDepth(ends[i]);
                if (z[i] <= 0f || z[i] >= 0x10000) return;
                p[i] = Ps1Point(ToUnity(new Vector3(ends[i].X, ends[i].Y, ends[i].Z)));
            }
            if (!FishClip(p, -160f, 160f, -120f, 120f)) return;
            int n = (FDist((int)(p[1].x - p[0].x), (int)(p[1].y - p[0].y), 0) + 0x40) / 0x40;
            if (n <= 0) n = 1;
            Color col = new Color(_tColour.r / 255f, _tColour.g / 255f, _tColour.b / 255f, 1f);
            Vector2 prev = p[0]; float prevZ = z[0];
            for (int j = 1; j <= n; j++)
            {
                Vector2 cur = Vector2.Lerp(p[0], p[1], j / (float)n);
                float cz = Mathf.Lerp(z[0], z[1], j / (float)n);
                FishLine(prev, cur, Mathf.Max(cz, prevZ), col);
                prev = cur; prevZ = cz;
            }
        }

        // FISH_clipLineToRect
        static bool FishClip(Vector2[] pts, float xMin, float xMax, float yMin, float yMax)
        {
            int[] code = new int[2];
            for (int i = 0; i < 2; i++) code[i] = (pts[i].x < xMin ? 1 : 0) | (pts[i].x > xMax ? 2 : 0);
            if ((code[0] & code[1]) != 0) return false;
            for (int i = 0; i < 2; i++)
            {
                Vector2 o = pts[1 - i];
                if ((code[i] & 1) != 0) { pts[i].y = o.y + (xMin - o.x) * (pts[i].y - o.y) / (pts[i].x - o.x); pts[i].x = xMin; }
                if ((code[i] & 2) != 0) { pts[i].y = o.y + (xMax - o.x) * (pts[i].y - o.y) / (pts[i].x - o.x); pts[i].x = xMax; }
            }
            for (int i = 0; i < 2; i++) code[i] = (pts[i].y < yMin ? 1 : 0) | (pts[i].y > yMax ? 2 : 0);
            if ((code[0] & code[1]) != 0) return false;
            for (int i = 0; i < 2; i++)
            {
                Vector2 o = pts[1 - i];
                if ((code[i] & 1) != 0) { pts[i].x = o.x + (yMin - o.y) * (pts[i].x - o.x) / (pts[i].y - o.y); pts[i].y = yMin; }
                if ((code[i] & 2) != 0) { pts[i].x = o.x + (yMax - o.y) * (pts[i].x - o.x) / (pts[i].y - o.y); pts[i].y = yMax; }
            }
            return true;
        }

        // drawLine2P: one PS1 pixel wide
        void FishLine(Vector2 a, Vector2 b, float depth, Color col)
        {
            Vector2 d = b - a;
            if (d.sqrMagnitude < 1e-6f) return;
            Vector2 side = new Vector2(-d.y, d.x).normalized * 0.5f;
            var q = new[] { a - side, b - side, a + side, b + side };
            var uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
            FishQuad(q, depth, Texture2D.whiteTexture, uv, col, FishMaterial(-1));
        }

        // a model piece at a game-space place and turn (GsSortObject4J), lit by the map's three lights with ambient 0x800 (map.c GsSetAmbient)
        void FishDrawTmd(PsxTmd.Built b, Matrix4x4 rot, FV pos)
        {
            if (b == null || b.Mesh == null) return;
            var lights = new PsxTmd.Lights
            {
                Dir = new[] { _map.LightDir[0].normalized, _map.LightDir[1].normalized, _map.LightDir[2].normalized },
                Col = new[] { _map.LightColor[0], _map.LightColor[1], _map.LightColor[2] },
                Ambient = 0x800 / 4096f
            };
            b.Relight(lights, rot);
            Matrix4x4 f = Matrix4x4.Scale(new Vector3(1, -1, 1)), m = f * rot * f;
            m.SetColumn(3, new Vector4(pos.X * Unit, -pos.Y * Unit, pos.Z * Unit, 1f));
            for (int k = 0; k < b.Mats.Length; k++) if (b.Mats[k] != null) Graphics.DrawMesh(b.Mesh, m, b.Mats[k], 0, null, k);
        }

        // FISH_drawRodSegments: SAO.TMD's four objects at the first four segments
        void FishDrawRod()
        {
            if (_fishRod == null) return;
            for (int i = 0; i < 4 && i < _fishRod.Length; i++)
            {
                if (FishDepth(_fishRodSegPos[i]) <= 0f) continue;
                FishDrawTmd(_fishRod[i], _fishRodSegMat[i], _fishRodSegPos[i]);
            }
        }

        // FISH_drawSeadramon: SEA.TMD's five pieces along the last five headings (each turned (x + 0x800, y, 0x800 - z) to draw), joined by FISH_D_8007A56C
        void FishDrawSeadramon(FV start)
        {
            if (_fishSea == null) return;
            FV pos = start;
            for (int i = 0; i < 5 && i < _fishSea.Length; i++)
            {
                FA h = _fishSeaHist[i];
                Matrix4x4 drawRot = FYXZ(new FA(h.X + 0x800, h.Y, 0x800 - h.Z));
                FishDrawTmd(_fishSea[i], drawRot, pos);
                pos = FAdd(pos, FApply(FYXZ(h), _fishSeaOffsets[i]));
            }
        }
    }
}
