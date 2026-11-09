using System;
using System.Collections.Generic;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The arena's own world (std/std_setup.c, std/std_hud.c and the US machine code of the scenes that were never decompiled; STD_REL.BIN
    /// loads at 0x80052AE0). STD_initializeTournament: the video memory it fills (ETCTIM.BIN is already there from the start), the ring model
    /// STDDAT/T_TOGI (File City) or B_TOGI (Beetle Land) with its walking grid B_TOGI.ATR, the lights (STD_initializeLighting), and the
    /// "model scene": the WIN / LOSE letters (WIN_LOSE.TMD), the draw letters (DRAW.TMD), CHAMPION (CHAMP.TMD) and the podium (T_DAI / B_DAI),
    /// drawn with their own matrix and projection on top of the picture. It sits at the world's middle as on the PS1 (the effects work in
    /// 16-bit game units); the room hides its own pieces while it shows (PlayableRoomArena.HideRoom).
    /// </summary>
    public sealed class ArenaStage
    {
        public static readonly Vector3 Origin = Vector3.zero;
        public const int OverlayLayer = 28;
        const uint StdBase = 0x80052AE0;

        public readonly PsxVram Vram = new PsxVram();
        public int Arena;                      // 0 File City (T_TOGI), 1 Beetle Land (B_TOGI, map 0x6A)
        public GameObject Root;
        public byte[] Std, Exe;
        uint _load;
        readonly List<UnityEngine.Object> _owned = new List<UnityEngine.Object>();
        readonly List<KeyValuePair<Material, int[]>> _texMats = new List<KeyValuePair<Material, int[]>>();     // material -> mode, page x, y, clut x, y, layer
        byte[] _atr;
        PsxTmd.Lights _lights;
        GameObject[] _ring;

        // ---------------------------------------------------------------- the model scene (STD_MODEL_SCENE_MATRIX, its own ordering table at 4)
        public sealed class ModelObj
        {
            public GameObject Go; public PsxTmd.Built B;
            public int[] Loc = new int[3], Rot = new int[3], Scale = { 4096, 4096, 4096 };
            public bool Off;                   // GsDOBJ2 attribute 0x80000000 (GsDOFF)
            public bool World;                 // drawn in the arena (the podium), not on the model scene
            public string Lit;                 // the turn and scale its lit colours were worked out for
        }
        public readonly ModelObj[] Result = new ModelObj[8];     // STD_RESULT_MODEL_OBJECTS: WIN_LOSE.TMD 0-3 WIN, 4-7 LOSE; [0] the podium after STD_loadChampionModels
        public readonly ModelObj[] Versus = new ModelObj[9];     // STD_VERSUS_MODEL_OBJECTS: DRAW.TMD 0-3, then CHAMP.TMD 0-8
        public ModelObj Podium;
        public Camera Overlay; public RenderTexture OverlayRt; Transform _space;
        public int OverlayH = 0x15E;           // GsSetProjection of the scene being drawn (0x15E win / lose, 0x200 draw / champion)
        public int Scene;                      // object 0x19D: 0 none, 1 win, 2 lose, 3 versus (draw), 4 champion
        public int Timer;                      // STD_WIN_LOSS_DRAW_TIMER
        public readonly short[] Steps = new short[10];     // STD_MODEL_SCENE_STEPS
        public readonly short[] WipeHeights = new short[32];   // STD_LOSE_WIPE_HEIGHTS
        readonly byte[] _wipeOrder = new byte[32];         // STD_LOSE_WIPE_ORDER
        short[] _winRotX = new short[21], _winRotY = new short[21], _winFirstX = new short[21], _winLastX = new short[21];
        short[] _loseDropY, _loseTilt, _champCamY, _orbit = new short[80];
        short[] _versusTargetX = new short[4];
        int[] _sceneMatrix = new int[12];     // STD_MODEL_SCENE_MATRIX: 3 x 3 (4096 = 1), translation

        int U32At(byte[] b, uint a, uint load) { return BitConverter.ToInt32(b, (int)(a - load)); }
        int StdS16(uint a) { return BitConverter.ToInt16(Std, (int)(a - StdBase)); }
        int StdS32(uint a) { return BitConverter.ToInt32(Std, (int)(a - StdBase)); }
        string StdPath(uint ptrAt) { return StdStr((uint)StdS32(ptrAt)); }
        string StdStr(uint a)
        {
            int p = (int)(a - StdBase), j = p;
            while (j < Std.Length && Std[j] != 0) j++;
            return System.Text.Encoding.ASCII.GetString(Std, p, j - p).Replace('\\', '/');
        }
        int ExeAt(uint a) { return (int)(a - _load + 0x800); }

        public static Vector3 ToUnity(Vector3 g) { return Origin + new Vector3(g.x, -g.y, g.z) * 0.01f; }
        public static Vector3 ToGame(Vector3 u) { u -= Origin; return new Vector3(u.x * 100f, -u.y * 100f, u.z * 100f); }
        static Vector3 Flip(Vector3 v) { return new Vector3(v.x, -v.y, v.z); }

        // STD_initializeTournament (the parts that load): STD_initializeLighting, STD_loadArenaAssets, STD_loadWinLoseModel, the cup's GRADE picture,
        // STDTIM.BIN, TIME.TIM. ETCTIM.BIN is the main game's (loaded at start, so it is under everything the arena loads)
        public static ArenaStage Load(DiscImage disc, byte[] std, byte[] exe, int arena, int cup, Transform parent)
        {
            var s = new ArenaStage { Std = std, Exe = exe, Arena = arena };
            s._load = BitConverter.ToUInt32(exe, 0x18);
            if (disc.Exists("/ETCDAT/ETCTIM.BIN")) s.Vram.LoadTims(disc.ReadFile("/ETCDAT/ETCTIM.BIN"));
            s.Root = new GameObject("Arena");
            s.Root.transform.SetParent(parent, false);
            s.Root.transform.position = Origin;
            s.InitLights();
            // STD_loadArenaAssets: STD_ARENA_TIMS[id] (STD_ARENA_COUNTS.timCount[id] pictures), STD_ARENA_MODELS[id] (modelCount[id] objects), STD_ARENA_COLLISIONS[id]
            int counts = s.ExeAt(0x801347FC);
            int modelCount = exe[counts + arena], timCount = exe[counts + 2 + arena];
            string tim = s.StdPath(0x8007A338 + 4 * (uint)arena), tmd = s.StdPath(0x8007A304 + 4 * (uint)arena), atr = s.StdPath(0x8007A358 + 4 * (uint)arena);
            if (disc.Exists(tim)) s.Vram.LoadTims(disc.ReadFile(tim), 0, timCount);
            s._atr = disc.Exists(atr) ? disc.ReadFile(atr) : null;
            // the cup's picture (STD_GRADES[cup], 0x80079C24), STD_loadTIMToVRAM(STDTIM.BIN): six pictures, loadTIMFile(TIME.TIM)
            string grade = cup >= 0 && cup < 23 ? s.StdPath(0x80079C24 + 4 * (uint)cup) : null;
            if (grade != null && disc.Exists(grade)) s.Vram.LoadTims(disc.ReadFile(grade), 0, 1);
            string stdtim = s.StdStr(0x80079C80), time = s.StdStr(0x80079C94);
            if (disc.Exists(stdtim)) s.Vram.LoadTims(disc.ReadFile(stdtim), 0, 6);
            if (disc.Exists(time)) s.Vram.LoadTims(disc.ReadFile(time), 0, 1);
            if (disc.Exists(tmd)) s.BuildRing(PsxTmd.Parse(disc.ReadFile(tmd)), modelCount);
            s.ReadTables();
            s.MakeOverlay();
            s.LoadWinLose(disc);
            Debug.Log("[arena] " + tmd + " (" + modelCount + " objects), " + tim + " (" + timCount + "), " + atr + ", " + grade);
            return s;
        }

        // STD_initializeLighting: three flat lights and the ambient 0x400 (these numbers are the code's own immediates)
        void InitLights()
        {
            _lights = new PsxTmd.Lights
            {
                Dir = new[] { new Vector3(1000, 1000, 1000).normalized, new Vector3(-1000, 1000, -1000).normalized, new Vector3(-20, 20, 200).normalized },
                Col = new[] { new Color(1f, 1f, 1f), new Color(160 / 255f, 160 / 255f, 160 / 255f), new Color(96 / 255f, 96 / 255f, 96 / 255f) },
                Ambient = 0x400 / 4096f
            };
        }

        void ReadTables()
        {
            for (int i = 0; i < 12; i++) _sceneMatrix[i] = i < 9 ? StdS16(0x8007AA10 + 2 * (uint)i) : StdS32(0x8007AA10 + 0x14 + 4 * (uint)(i - 9));
            _champCamY = new short[8]; for (int i = 0; i < 8; i++) _champCamY[i] = (short)StdS16(0x8007AA30 + 2 * (uint)i);
            _loseDropY = new short[24]; for (int i = 0; i < 24; i++) _loseDropY[i] = (short)StdS16(0x8007AA40 + 2 * (uint)i);
            _loseTilt = new short[14]; for (int i = 0; i < 14; i++) _loseTilt[i] = (short)StdS16(0x8007AA70 + 2 * (uint)i);
            var curve = new short[18]; for (int i = 0; i < 18; i++) curve[i] = (short)StdS16(0x8007AA8C + 2 * (uint)i);
            for (int i = 0; i < 4; i++) _versusTargetX[i] = BitConverter.ToInt16(Exe, ExeAt(0x801348DC) + 2 * i);
            // STD_loadWinLoseModel: the WIN letters' turn and the first / last letters' x for steps 0x14 - 0x28
            for (int i = 0x14; i < 0x29; i++)
            {
                _winRotX[i - 0x14] = (short)((i * 0x2000 / 40) & 0xFFF);
                _winRotY[i - 0x14] = (short)(((i * 0x800 / 40) + 0x800) & 0xFFF);
                if (i < 0x25) { _winFirstX[i - 0x14] = (short)(curve[0x24 - i] - 0xE6); _winLastX[i - 0x14] = (short)(0x118 - curve[0x24 - i]); }
                else { _winFirstX[i - 0x14] = (short)(curve[i - 0x24] - 0xE6); _winLastX[i - 0x14] = (short)(0x118 - curve[i - 0x24]); }
            }
            // STD_loadChampionModels: STD_CHAMPION_ORBIT_ANGLES, 40 steps easing in, then 39 easing out
            int a = 0xA00, n = 0x28;
            for (int i = 1; i <= n; i++) _orbit[i - 1] = (short)(a * (i * i) / (n * n));
            for (int i = 0; i < n - 1; i++) _orbit[i + 0x28] = (short)(0x1400 - _orbit[0x26 - i]);
        }

        // ---------------------------------------------------------------- materials

        Material PsxMaterial(PsxTmd.Prim p, int layer, int queue)
        {
            var sh = Shader.Find("DW1Remake/Psx");
            if (sh == null) { Debug.LogWarning("[arena] shader DW1Remake/Psx not found"); return null; }
            var m = new Material(sh);
            m.renderQueue = queue;
            m.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Back);       // GsSortObject4J drops a one-sided packet seen from behind
            if (layer < 0) { m.SetFloat("_Textured", 0f); if (p.Semi) Blend(m, (p.Tpage >> 5) & 3); return m; }
            int mode = (p.Tpage >> 7) & 3, px = (p.Tpage & 15) * 64, py = ((p.Tpage >> 4) & 1) * 256, cx = (p.Clut & 63) * 16, cy = (p.Clut >> 6) & 511;
            m.SetTexture("_MainTex", Vram.Page(mode, px, py, cx, cy, layer));
            _texMats.Add(new KeyValuePair<Material, int[]>(m, new[] { mode, px, py, cx, cy, layer }));
            if (layer == 2) Blend(m, (p.Tpage >> 5) & 3);
            return m;
        }

        // the PS1 blend of a semi-transparent packet (abr): 0 half and half, 1 added, 2 subtracted, 3 a quarter added
        static void Blend(Material m, int abr)
        {
            float one = (float)UnityEngine.Rendering.BlendMode.One, add = (float)UnityEngine.Rendering.BlendOp.Add;
            switch (abr & 3)
            {
                case 0: m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha); m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha); m.SetFloat("_Opacity", 0.5f); m.SetFloat("_BlendOp", add); break;
                case 1: m.SetFloat("_SrcBlend", one); m.SetFloat("_DstBlend", one); m.SetFloat("_Opacity", 1f); m.SetFloat("_BlendOp", add); break;
                case 2: m.SetFloat("_SrcBlend", one); m.SetFloat("_DstBlend", one); m.SetFloat("_Opacity", 1f); m.SetFloat("_BlendOp", (float)UnityEngine.Rendering.BlendOp.ReverseSubtract); break;
                default: m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha); m.SetFloat("_DstBlend", one); m.SetFloat("_Opacity", 0.25f); m.SetFloat("_BlendOp", add); break;
            }
            m.SetFloat("_ZWrite", 0f);
            m.renderQueue += 1000;
        }

        /// <summary>After MoveImage (the champion letters' shine): the materials on the page at (px, py) read their picture again.</summary>
        public void RefreshTextures(int px, int py)
        {
            foreach (var kv in _texMats) { var k = kv.Value; if (k[1] == px && k[2] == py) kv.Key.SetTexture("_MainTex", Vram.Page(k[0], k[1], k[2], k[3], k[4], k[5])); }
        }

        GameObject MakeObject(PsxTmd.Built b, string name, Transform parent, int layer)
        {
            var go = new GameObject(name);
            go.layer = layer;
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = b.Mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = b.Mats;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            _owned.Add(go);
            return go;
        }

        // STD_loadArenaAssets + STD_renderArena0 / 1: the ring's objects at the arena's middle (GsInitCoordinate2(NULL)). The PS1 draws
        // arena 0's objects 2 and 0 (arena 1's 1, 2 and 0) into two small ordering tables put at the very back of the picture, behind
        // everything (GsClearOt points 0xFFF and 0xFFE); ours draws those first.
        void BuildRing(PsxTmd tmd, int count)
        {
            _ring = new GameObject[count];
            for (int i = 0; i < count && i < tmd.Objs.Count; i++)
            {
                int queue = 2000;
                if (Arena == 0) { if (i == 2) queue = 1900; else if (i == 0) queue = 1910; }
                else { if (i == 1 || i == 2) queue = 1900; else if (i == 0) queue = 1910; }
                int q = queue;
                var b = PsxTmd.Built.Make(tmd.Objs[i], Vram, (p, layer) => PsxMaterial(p, layer, q), _owned);
                b.Relight(_lights, Matrix4x4.identity);
                _ring[i] = MakeObject(b, "Ring " + i, Root.transform, 0);
            }
        }

        /// <summary>STD_ARENA_COLLISIONS: the ring's walking grid (100 x 100 like a map's; 0x80 blocked).</summary>
        public bool Walkable(Vector3 u)
        {
            if (_atr == null) return true;
            Vector3 g = ToGame(u);
            int tx = (int)(g.x / 100f) + 50; if (g.x < 0) tx--;
            int tz = 50 - (int)(g.z / 100f); if (g.z > 0) tz--;
            if (tx < 0 || tx >= 100 || tz < 0 || tz >= 100) return false;
            return _atr[tx + tz * 100] != 0x80;
        }

        // ---------------------------------------------------------------- the model scene

        void MakeOverlay()
        {
            var go = new GameObject("Arena model scene camera");
            go.transform.SetParent(Root.transform, false);
            go.transform.localPosition = new Vector3(0f, 1000f, 0f);     // far above the ring; it sees only its own layer
            _owned.Add(go);
            Overlay = go.AddComponent<Camera>();
            Overlay.cullingMask = 1 << OverlayLayer;
            Overlay.clearFlags = CameraClearFlags.SolidColor;
            Overlay.backgroundColor = new Color(0f, 0f, 0f, 0f);
            Overlay.aspect = 4f / 3f;
            Overlay.nearClipPlane = 0.1f; Overlay.farClipPlane = 200f;
            OverlayRt = new RenderTexture(640, 480, 24, RenderTextureFormat.ARGB32) { filterMode = FilterMode.Point };
            _owned.Add(OverlayRt);
            Overlay.targetTexture = OverlayRt;
            var ad = UnityEngine.Rendering.Universal.CameraExtensions.GetUniversalAdditionalCameraData(Overlay);
            if (ad != null) ad.renderPostProcessing = false;
            // GsWSMATRIX = STD_MODEL_SCENE_MATRIX: the objects' places go through it (its translation puts them 1000 in front of the eye)
            _space = new GameObject("Model scene").transform;
            _space.SetParent(go.transform, false);
            _space.localPosition = new Vector3(_sceneMatrix[9], -_sceneMatrix[10], _sceneMatrix[11]) * 0.01f;
            _space.localScale = new Vector3(_sceneMatrix[0] / 4096f, _sceneMatrix[4] / 4096f, _sceneMatrix[8] / 4096f);
            go.SetActive(false);
        }

        ModelObj MakeModel(PsxTmd tmd, int i, string name, bool world)
        {
            var o = new ModelObj { World = world };
            o.B = PsxTmd.Built.Make(tmd.Objs[i], Vram, (p, layer) => PsxMaterial(p, layer, 2000), _owned);
            o.Go = MakeObject(o.B, name, world ? Root.transform : _space, world ? 0 : OverlayLayer);
            o.Go.SetActive(false);
            return o;
        }

        // STD_loadWinLoseModel: eight objects; 4-7 (LOSE) start at (0, -0x2BC, 0xDC)
        void LoadWinLose(DiscImage disc)
        {
            string path = StdStr(0x8007AAC4);
            if (!disc.Exists(path)) return;
            var tmd = PsxTmd.Parse(disc.ReadFile(path));
            for (int i = 0; i < 8 && i < tmd.Objs.Count; i++) Result[i] = MakeModel(tmd, i, "Result " + i, false);
            for (int i = 4; i < 8; i++) if (Result[i] != null) { Result[i].Loc = new[] { 0, -0x2BC, 0xDC }; Result[i].Rot = new int[3]; }
        }

        // STD_loadVersusSceneModel: DRAW.TMD's four objects at (0x3E8, 0x78, 0x280)
        public void LoadVersus(DiscImage disc)
        {
            Timer = 0;
            for (int i = 0; i < 9; i++) if (Versus[i] != null) { Versus[i].Go.SetActive(false); Versus[i] = null; }
            string path = StdStr((uint)BitConverter.ToInt32(Exe, ExeAt(0x801348D8)));      // STD_VERSUS_MODEL_PATH -> DRAW.TMD
            if (!disc.Exists(path)) return;
            var tmd = PsxTmd.Parse(disc.ReadFile(path));
            for (int i = 0; i < 4 && i < tmd.Objs.Count; i++)
            {
                Versus[i] = MakeModel(tmd, i, "Draw " + i, false);
                Versus[i].Loc = new[] { 0x3E8, 0x78, 0x280 };
            }
        }

        // STD_loadChampionModels: CHAMP.TMD's nine letters (their pictures moved by STD_offsetTMDObjectUVs(x = (8 - i) x -24, v + 0x30), scale y 2,
        // hidden), and the podium (B_DAI on Beetle Land's map 0x6A, else T_DAI) at (0, -0x1E, 0) in the arena
        public void LoadChampion(DiscImage disc)
        {
            string path = StdStr(0x8007AADC);
            if (disc.Exists(path))
            {
                byte[] d = disc.ReadFile(path);
                for (int i = 0; i < 9; i++) PsxTmd.OffsetObjectUvs(d, (8 - i) * -24, 0x30, i);
                var tmd = PsxTmd.Parse(d);
                for (int i = 0; i < 9; i++) if (Versus[i] != null) { Versus[i].Go.SetActive(false); Versus[i] = null; }
                for (int i = 0; i < 9 && i < tmd.Objs.Count; i++)
                {
                    var o = MakeModel(tmd, i, "Champion " + i, false);
                    o.Off = true; o.Scale = new[] { 0x1000, 0x2000, 0x1000 };
                    int x;
                    switch (i) { case 5: x = 0xBE; break; case 6: x = 0x168; break; case 7: x = 0x230; break; case 8: x = 0x2E4; break; default: x = i * 0xC8 - 0x2F8; break; }
                    o.Loc = new[] { x, 0xC8, 0x7BC };
                    Versus[i] = o;
                }
            }
            string dai = StdStr(Arena == 1 ? 0x8007AAF0u : 0x8007AB04u);
            if (disc.Exists(dai))
            {
                var tmd = PsxTmd.Parse(disc.ReadFile(dai));
                if (tmd.Objs.Count > 0) { Podium = MakeModel(tmd, 0, "Podium", true); Podium.Loc = new[] { 0, -0x1E, 0 }; }
            }
        }

        static Matrix4x4 RotX(int a) { float c = Mathf.Cos(a * Turn), s = Mathf.Sin(a * Turn); var m = Matrix4x4.identity; m[1, 1] = c; m[1, 2] = -s; m[2, 1] = s; m[2, 2] = c; return m; }
        static Matrix4x4 RotY(int a) { float c = Mathf.Cos(a * Turn), s = Mathf.Sin(a * Turn); var m = Matrix4x4.identity; m[0, 0] = c; m[0, 2] = s; m[2, 0] = -s; m[2, 2] = c; return m; }
        static Matrix4x4 RotZ(int a) { float c = Mathf.Cos(a * Turn), s = Mathf.Sin(a * Turn); var m = Matrix4x4.identity; m[0, 0] = c; m[0, 1] = -s; m[1, 0] = s; m[1, 1] = c; return m; }
        const float Turn = Mathf.PI * 2f / 4096f;
        public static Matrix4x4 RotMatrix(int x, int y, int z) { return RotX(x) * RotY(y) * RotZ(z); }
        public static Matrix4x4 RotMatrixYXZ(int x, int y, int z) { return RotY(y) * RotX(x) * RotZ(z); }
        public static Matrix4x4 RotMatrixZYX(int x, int y, int z) { return RotZ(z) * RotY(y) * RotX(x); }

        // setupModelMatrix: TransMatrix, RotMatrix, ScaleMatrix; lit packets follow the turn (GsSetLightMatrix(lw))
        public void Place(ModelObj o)
        {
            if (o == null || o.Go == null) return;
            var t = o.Go.transform;
            MmdModel.SetLocal(t, o.Rot, o.Loc);
            t.localScale = new Vector3(o.Scale[0], o.Scale[1], o.Scale[2]) / 4096f;
            string key = o.Rot[0] + "," + o.Rot[1] + "," + o.Rot[2] + "," + o.Scale[0] + "," + o.Scale[1] + "," + o.Scale[2];
            if (key != o.Lit)
            {
                var m = RotMatrix(o.Rot[0], o.Rot[1], o.Rot[2]) * Matrix4x4.Scale(new Vector3(o.Scale[0], o.Scale[1], o.Scale[2]) / 4096f);
                o.B.Relight(_lights, m);
                o.Lit = key;
            }
        }

        /// <summary>Which model scene objects show (the scene's render function sorts exactly these) and the projection it sets.</summary>
        public void ShowScene(int scene)
        {
            Scene = scene;
            for (int i = 0; i < 8; i++) if (Result[i] != null && !Result[i].World) Result[i].Go.SetActive(scene == 1 && i < 4 || scene == 2 && i >= 4);
            for (int i = 0; i < 9; i++) if (Versus[i] != null) Versus[i].Go.SetActive(((scene == 3 && i < 4) || scene == 4) && !Versus[i].Off);
            OverlayH = scene == 1 || scene == 2 ? 0x15E : 0x200;
            if (Overlay != null) Overlay.gameObject.SetActive(scene != 0);
            if (Overlay != null) Overlay.fieldOfView = 2f * Mathf.Atan(120f / OverlayH) * Mathf.Rad2Deg;
        }

        public void ShowPodium(bool on) { if (Podium != null) { Podium.Go.SetActive(on); if (on) Place(Podium); } }

        // ---------------------------------------------------------------- the WIN scene (STD_addWinScene / STD_tickWinScene, machine code 0x8006CCE0)

        public void AddWin()
        {
            int[] xs = { -0xFA, -0x32, 0x64, 0x104 };
            for (int i = 0; i < 4; i++) if (Result[i] != null) { Result[i].Loc = new[] { xs[i], 0, -0x30C }; Result[i].Rot = new int[3]; Result[i].Scale = new[] { 4096, 4096, 4096 }; }
            Timer = 0;
            for (int i = 0; i < 4; i++) { Steps[i] = 0; Place(Result[i]); }
            ShowScene(1);
        }

        // each letter from frame 0x3C + 6i: forward 50 a frame until 0xDC, 20 steps of its turn; the first and last letters swing out and back in x
        public void TickWin()
        {
            if (Timer < 0xA0) Timer++;
            for (int i = 0; i < 4; i++)
            {
                var o = Result[i];
                if (o == null) continue;
                if (Timer > 0x3C + 6 * i)
                {
                    if (o.Loc[2] < 0xDC) o.Loc[2] += 0x32;
                    if (Steps[i] < 0x14) Steps[i]++;
                    o.Rot[0] = _winRotX[Steps[i]]; o.Rot[1] = _winRotY[Steps[i]];
                    if (i == 0) o.Loc[0] = _winFirstX[Steps[i]];
                    if (i == 3) o.Loc[0] = _winLastX[Steps[3]];
                }
                Place(o);
            }
        }

        // ---------------------------------------------------------------- the LOSE scene (STD_addLoseScene / STD_tickLoseScene, machine code 0x8006C6DC)

        public void AddLose(System.Random rng)
        {
            for (int i = 0; i < 32; i++) { WipeHeights[i] = 0; _wipeOrder[i] = (byte)i; }
            for (int i = 0; i < 32; i++) { int j = rng.Next(32); byte t = _wipeOrder[i]; _wipeOrder[i] = _wipeOrder[j]; _wipeOrder[j] = t; }
            Timer = 0;
            for (int i = 0; i < 4; i++) Steps[i] = 0;
            for (int i = 4; i < 8; i++) Place(Result[i]);
            ShowScene(2);
        }

        // the letters drop in one after another (frames 61, 64, 67, 70) along STD_LOSE_DROP_Y; once a letter is 11 steps in, eight of the 32 black
        // strips (in a shuffled order) grow 60 a frame to 240; from frame 116 they tilt back (STD_LOSE_TILT), and from 128 they fall off together
        public void TickLose()
        {
            if (Timer < 0x8C) Timer++;
            int t = Timer;
            if (t >= 0x3D && Steps[0] < 0x16) Steps[0]++;
            if (t >= 0x40 && Steps[1] < 0x16) Steps[1]++;
            if (t >= 0x43 && Steps[2] < 0x16) Steps[2]++;
            if (t >= 0x46 && Steps[3] < 0x16) Steps[3]++;
            for (int s = 0; s < 4; s++)
            {
                if (Steps[s] < 0xB) continue;
                for (int k = 0; k < 8; k++) { int c = _wipeOrder[s * 8 + k]; if (WipeHeights[c] < 0xF0) WipeHeights[c] += 0x3C; }
            }
            if (t >= 0x74)
            {
                if (t < 0x82) { int tilt = _loseTilt[t - 0x74]; for (int i = 4; i < 8; i++) if (Result[i] != null) Result[i].Rot[0] = tilt; }
                if (t == 0x7F) Steps[0] = 0;
                if (t >= 0x80) { int y = _loseDropY[Steps[0]] + 0x320; for (int i = 4; i < 8; i++) if (Result[i] != null) Result[i].Loc[1] = y; }
            }
            else for (int i = 4; i < 8; i++) if (Result[i] != null) Result[i].Loc[1] = _loseDropY[Steps[i - 4]];
            for (int i = 4; i < 8; i++) Place(Result[i]);
        }

        // ---------------------------------------------------------------- the DRAW scene (STD_addVersusModelScene / STD_tickVersusModelScene)

        public void AddVersus() { for (int i = 0; i < 4; i++) Place(Versus[i]); ShowScene(3); }

        public void TickVersus()
        {
            Timer++;
            for (int i = 0; i < 4; i++)
            {
                var o = Versus[i];
                if (o == null) continue;
                if (Timer > i * 5 && o.Loc[0] != _versusTargetX[i])
                {
                    o.Loc[0] -= 200;
                    if (o.Loc[0] < _versusTargetX[i]) o.Loc[0] = _versusTargetX[i];
                }
                Place(o);
            }
        }

        public bool VersusFinished { get { return Timer >= 0x51; } }

        // ---------------------------------------------------------------- CHAMPION (STD_initializeChampionScene, STD_tickChampionScene machine code 0x8006BFD4)

        public int ChampState;                 // STD_CHAMPION_SCENE_STATE
        public bool ChampDone;                 // STD_CHAMPION_SCENE_DONE
        public short OrbitAngle(int i) { return _orbit[Mathf.Clamp(i, 0, 79)]; }
        public short ChampCamY(int i) { return _champCamY[Mathf.Clamp(i, 0, 7)]; }

        // STD_initializeChampionScene's MoveImage: the 6 x 0x30 strip at (0x378, 0) into each letter's place (0x348 + 6i, 0x30)
        public void InitChampionPictures()
        {
            Timer = 0;
            for (int i = 0; i < 9; i++) { Steps[i] = 0; Vram.Move(0x378, 0, 6, 0x30, i * 6 + 0x348, 0x30); }
            RefreshTextures(0x340, 0);
        }

        /// <summary>STD_addChampionScene: done = 0, the scene starts in state 0.</summary>
        public void AddChampion() { ChampDone = false; ChampState = 0; ShowScene(4); for (int i = 0; i < 9; i++) Place(Versus[i]); }

        /// <summary>State 0 of STD_tickChampionScene: the letters come one by one (from frame 5i, spinning 0x200 a frame until frame 20 + 5i) while
        /// each one's picture slides (MoveImage of the strip at 0x376, 0x374 ... then 0x378) from frame 61 + i; true when state 0 is over (frame 97).</summary>
        public bool TickChampionLetters()
        {
            Timer++;
            bool moved = false;
            for (int i = 0; i < 9; i++)
            {
                var o = Versus[i];
                if (Timer >= 0x3D + i && Steps[i] < 0xB)
                {
                    int x = Steps[i] < 0xA ? 0x378 - (Steps[i] + 1) * 2 : 0x378;
                    Steps[i]++;
                    Vram.Move(x, 0, 6, 0x30, 0x348 + 6 * i, 0x30);
                    moved = true;
                }
                if (o != null && Timer >= 5 * i)
                {
                    if (Timer == 5 + 5 * i) { o.Off = false; o.Go.SetActive(true); }
                    if (Timer >= 20 + 5 * i) o.Rot[1] = 0;
                    else o.Rot[1] = (o.Rot[1] + 0x200) & 0xFFF;
                }
                Place(o);
            }
            if (moved) RefreshTextures(0x340, 0);
            return Timer >= 0x61;
        }

        /// <summary>State 1: the letters sink 50 a frame to 600.</summary>
        public void SinkChampionLetters()
        {
            for (int i = 0; i < 9; i++) { var o = Versus[i]; if (o == null) continue; if (o.Loc[1] < 0x258) o.Loc[1] += 0x32; Place(o); }
        }

        // ---------------------------------------------------------------- cameras (GsSetRefView2, GsSetView2) and the projection

        /// <summary>The PS1 camera's projection: VIEWPORT_DISTANCE h on the 240-line picture, nothing shifted.</summary>
        public static void Lens(Camera cam, float h)
        {
            cam.usePhysicalProperties = true;
            cam.gateFit = Camera.GateFitMode.Vertical;
            cam.sensorSize = new Vector2(24f * cam.aspect, 24f);
            cam.focalLength = h * 24f / 240f;
            cam.lensShift = Vector2.zero;
        }

        /// <summary>GsSetRefView2: from vp looking at vr (game units, arena space), no roll.</summary>
        public static void LookAt(Camera cam, Vector3 vp, Vector3 vr, float h)
        {
            Vector3 p = ToUnity(vp), t = ToUnity(vr);
            if ((t - p).sqrMagnitude > 1e-8f) cam.transform.SetPositionAndRotation(p, Quaternion.LookRotation(t - p, Vector3.up));
            Lens(cam, h);
        }

        /// <summary>GsSetView2 with view.view = r (world to eye: v = r w + t) and no super coordinate.</summary>
        public static void View(Camera cam, Matrix4x4 r, Vector3 t, float h)
        {
            Vector3 r0 = r.GetRow(0), r1 = r.GetRow(1), r2 = r.GetRow(2);
            Vector3 pos = -(r0 * t.x + r1 * t.y + r2 * t.z);
            cam.transform.SetPositionAndRotation(ToUnity(pos), Quaternion.LookRotation(Flip(r2), -Flip(r1)));
            Lens(cam, h);
        }

        /// <summary>GsSetView2 with view.super = a coordinate (its matrix c at t): the eye's own frame in the world.</summary>
        public static void Frame(Camera cam, Matrix4x4 c, Vector3 t, float h)
        {
            Vector3 down = c.GetColumn(1), fwd = c.GetColumn(2);
            cam.transform.SetPositionAndRotation(ToUnity(t), Quaternion.LookRotation(Flip(fwd), -Flip(down)));
            Lens(cam, h);
        }

        public void Destroy()
        {
            foreach (var o in _owned) if (o != null) UnityEngine.Object.Destroy(o);
            _owned.Clear();
            if (Root != null) UnityEngine.Object.Destroy(Root);
            Vram.Destroy();
        }
    }
}
