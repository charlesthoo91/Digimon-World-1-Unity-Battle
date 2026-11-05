using System;
using System.Collections.Generic;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>One side of a technique, for the effect programs.</summary>
    public sealed class EffectActor { public Transform T; public DigimonAnimator A; public int Type, Radius, Height, BoneCount; }

    /// <summary>
    /// The game's technique effects (ETCHI/EFEDAT.EFE, btl/battle_effect.c). Each technique has a 20480-byte block: a 52-byte header, then a small program with its data,
    /// a TMD model and a TIM picture. The program runs 20 times a second and draws sprites and models, plays sounds and decides when the target is hit.
    /// Addresses in the programs are places in the block's body, so the block is copied and used as the program's memory.
    /// Not ported: the particle emitters, screen sprites, wireframes, ribbons, untextured model faces.
    /// </summary>
    public sealed partial class EffectPlayer
    {
        const int Block = 20480, Header = 0x34;
        const float Unit = 0.01f;
        sealed class Ctx { public int Inst, Some, Instance, Parent, BoneOffset; public EffectActor Src, Tgt; }
        // Attr = GsSPRITE attribute >> 28: bit 2 semi-transparent, bits 0-1 the blend mode (0 half, 1 add, 2 subtract, 3 quarter add)
        sealed class Sprite { public Vector3 Pos; public float W, H, Roll; public Rect Uv; public int Row, Attr = 5; public Color Col; }
        sealed class Model { public int Object; public Matrix4x4 M; }

        sealed class Run
        {
            public int Tech; public byte[] M; public Ctx[] Ctxs; public int Start; public int Logged, LoggedStep, Steps;
            public Action<Vector3> OnHit; public bool Hit;
            public EffectActor Src, Tgt;
            public int Flash = -1;
            public readonly List<Prim> Prims = new List<Prim>();
            public bool FixedCam, MapOff;
            public readonly List<Sprite> Sprites = new List<Sprite>();
            public readonly List<Model> Models = new List<Model>();
            public int[] UvPtr, UvKey, UvFrame; public Vector2[] UvOff;
            public readonly List<Emitter> Emitters = new List<Emitter>();
        }
        // BTL_addParticleEmitter: a spray from the owner's place, one new spark a frame, up to 20
        sealed class Spark { public int Dist, Vel; public Vector3 Dir, Pos; }
        sealed class Emitter { public int Owner, Frames, Start, Vel, Acc, Kind; public Color Col; public readonly Spark[] Sparks = new Spark[20]; }
        sealed class Asset { public TimImage Tim; public Texture2D[] Tex = new Texture2D[16]; public Mesh[] Meshes; public int[] Rows; public int Width, Height; }
        sealed class Flash { public Vector3 Pos; public int T, Max, Mode, S0, S1; public bool Front; public Vector3 C0, C1; }

        readonly byte[] _file; readonly Action<int> _sound;
        readonly Dictionary<int, Asset> _assets = new Dictionary<int, Asset>();
        readonly List<Run> _runs = new List<Run>();
        readonly List<Flash> _flashes = new List<Flash>();
        readonly System.Random _rng = new System.Random();
        readonly Material _mat; readonly MaterialPropertyBlock _mp = new MaterialPropertyBlock();
        readonly Mesh _quad; readonly Texture2D _glow, _particle;
        float _acc;

        public EffectPlayer(DiscImage disc, Action<int> sound)
        {
            _file = disc.ReadFile("/ETCHI/EFEDAT.EFE"); _sound = sound;
            var sh = Shader.Find("DW1Remake/Effect");
            _mat = sh != null ? new Material(sh) : null;
            _quad = new Mesh();
            _quad.vertices = new[] { new Vector3(-.5f, -.5f, 0), new Vector3(.5f, -.5f, 0), new Vector3(-.5f, .5f, 0), new Vector3(.5f, .5f, 0) };
            _quad.uv = new[] { new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, 0), new Vector2(1, 0) };
            _quad.triangles = new[] { 0, 1, 2, 2, 1, 3 };
            _glow = new Texture2D(32, 32, TextureFormat.RGBA32, false);
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                {
                    float d = Mathf.Clamp01(1f - new Vector2(x - 15.5f, y - 15.5f).magnitude / 16f);
                    _glow.SetPixel(x, y, new Color(d, d, d, d));
                }
            _glow.Apply();
            // renderFXParticle draws a 15 x 15 picture from VRAM page (768,256) at v 160 with CLUT (192,489): ETCDAT/DEFEFF.TIM (VRAM 768,384), row 32, colour set 1
            TimImage spot;
            if (disc.Exists("/ETCDAT/SPOT256.TIM") && TimImage.TryParse(disc.ReadFile("/ETCDAT/SPOT256.TIM"), 0, out spot)) _spot = spot.ToTexture(0);
            TimImage def;
            if (disc.Exists("/ETCDAT/DEFEFF.TIM") && TimImage.TryParse(disc.ReadFile("/ETCDAT/DEFEFF.TIM"), 0, out def)) { _particle = def.ToTexture(1); _particleTim = def; }
        }
        readonly TimImage _particleTim; readonly Texture2D _spot;

        static int I32(byte[] m, int p) { return p >= 0 && p + 4 <= m.Length ? BitConverter.ToInt32(m, p) : 0; }
        static short S16(byte[] m, int p) { return p >= 0 && p + 2 <= m.Length ? BitConverter.ToInt16(m, p) : (short)0; }
        static void Put(byte[] m, int p, int v) { if (p >= 0 && p + 4 <= m.Length) { m[p] = (byte)v; m[p + 1] = (byte)(v >> 8); m[p + 2] = (byte)(v >> 16); m[p + 3] = (byte)(v >> 24); } }
        static void Put16(byte[] m, int p, int v) { if (p >= 0 && p + 2 <= m.Length) { m[p] = (byte)v; m[p + 1] = (byte)(v >> 8); } }
        static void Put8(byte[] m, int p, int v) { if (p >= 0 && p < m.Length) m[p] = (byte)v; }

        Asset AssetOf(int tech)
        {
            Asset a;
            if (_assets.TryGetValue(tech, out a)) return a;
            a = new Asset();
            int b = tech * Block, tmd0 = I32(_file, b + 20), tmd1 = I32(_file, b + 24), tim0 = I32(_file, b + 28), end = I32(_file, b + 36);
            if (end > tim0) TimImage.TryParse(_file, b + Header + tim0, out a.Tim);
            if (a.Tim != null) { a.Width = a.Tim.Width; a.Height = a.Tim.Height; }
            if (tmd1 > tmd0)
            {
                var raw = new byte[tmd1 - tmd0 + 4];
                raw[0] = 4;
                Array.Copy(_file, b + Header + tmd0, raw, 4, tmd1 - tmd0);
                try
                {
                    var model = MmdModel.Parse(raw);
                    a.Meshes = new Mesh[model.Parts.Count]; a.Rows = new int[a.Meshes.Length];
                    for (int i = 0; i < a.Meshes.Length; i++)
                    {
                        a.Meshes[i] = BuildMesh(model.Parts[i], a);
                        if (model.Parts[i].Faces.Count > 0) a.Rows[i] = model.Parts[i].Faces[0].Row;     // each object names its own colour set (CLUT y - 488)
                    }
                }
                catch (Exception e) { Debug.LogWarning("Effect model " + tech + ": " + e.Message); }
            }
            _assets[tech] = a;
            return a;
        }

        static Mesh BuildMesh(MmdModel.Part p, Asset a)
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
            float w = Mathf.Max(1, a.Width), h = Mathf.Max(1, a.Height);
            foreach (var f in p.Faces)
                for (int k = 0; k < 3; k++)
                {
                    int i = f.V[k] * 3;
                    t.Add(v.Count);
                    v.Add(new Vector3(p.Verts[i], p.Verts[i + 1], p.Verts[i + 2]));
                    uv.Add(new Vector2(f.U[k] / w, f.T[k] / h));
                }
            var m = new Mesh();
            m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(t, 0);
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
            return m;
        }

        /// <summary>Starts the technique's effect. False when it has none for this Digimon. onHit fires once with the place of the hit.</summary>
        public bool Start(int tech, EffectActor src, EffectActor tgt, Action<Vector3> onHit)
        {
            if (_mat == null || tech < 0 || (tech + 1) * Block > _file.Length) return false;
            int b = tech * Block;
            int start = I32(_file, b), init = I32(_file, b + 4), subs = I32(_file, b + 44);
            if (start <= 0 || start >= Block || subs <= 0 || subs > 256) return false;     // the game has no limit (largest on the disc: 149); 256 only rejects the unused block at the end
            var r = new Run { Tech = tech, M = new byte[Block - Header], Src = src, Tgt = tgt, OnHit = onHit, Ctxs = new Ctx[subs], Start = start };
            Array.Copy(_file, b + Header, r.M, 0, r.M.Length);
            for (int i = 0; i < subs; i++) r.Ctxs[i] = new Ctx { Src = src, Tgt = tgt };
            var asset = AssetOf(tech);
            // the technique's data is loaded afresh when it starts: colours a previous run loaded into its colour table are gone
            if (asset.Tim != null && asset.Tim.ClearPaletteOverrides()) for (int row = 0; row < asset.Tex.Length; row++) ForgetTextures(asset, row);
            int objs = asset.Meshes != null ? asset.Meshes.Length : 0;
            r.UvOff = new Vector2[objs]; r.UvPtr = new int[objs]; r.UvKey = new int[objs]; r.UvFrame = new int[objs];
            Exec(r, r.Ctxs[0], 0, 0, init);     // the program's setup
            Exec(r, r.Ctxs[0], 0, 0, start);    // and its first step
            if (_section == -1 && r.Ctxs[0].Inst == 0) return false;   // no bones listed for this Digimon
            _runs.Add(r);
            return true;
        }

        public void Clear()
        {
            _runs.Clear(); _flashes.Clear(); _itemFx.Clear(); _hitFx.Clear(); _entityFx.Clear(); _clouds.Clear();
            if (_mapOff) { _mapOff = false; if (OnMapLayer != null) OnMapLayer(true); }
        }
        bool _mapOff;

        // ---------------------------------------------------------------- BTL_addItemParticles: the sparkle when a thrown item reaches the partner
        // 20 particles from the top of the Digimon (location - height), 40 frames; each lives 25, falls by 0x36 a frame (in 1/16 units), slows by its drag,
        // fades from 0xFF0 to 0; drawn as renderFXParticle 20 units wide, all at the first one's brightness. Velocities and drags are made once (BTL_initializeItemParticleVelocities).
        sealed class ItemParticle { public int Life, X, Y, Z, Vx, Vy, Vz, Bright, Fade; }
        sealed class ItemFx { public int Timer; public ItemParticle[] P = new ItemParticle[20]; }
        readonly List<ItemFx> _itemFx = new List<ItemFx>();
        Vector3Int[] _itemVel; Vector2Int[] _itemDrag;

        public void AddItemParticles(Vector3 unityPos, int height)
        {
            if (_itemFx.Count >= 4) return;
            if (_itemVel == null)
            {
                _itemVel = new Vector3Int[20]; _itemDrag = new Vector2Int[20];
                for (int i = 0; i < 20; i++)
                {
                    int rx = _rng.Next(1000) - 0x1F4, ry = _rng.Next(0x1000);
                    Vector3 o = (RotZ(0) * RotY(ry) * RotX(rx)).MultiplyVector(new Vector3(0, -0x230, 0));
                    _itemVel[i] = new Vector3Int((int)o.x, (int)o.y, (int)o.z);
                    int ax = Mathf.Abs(rx), r = 0x3C + (0x1E - 0x3C) * ax / 0x226;      // lerp(0x3c, 0x1e, 0, 0x226, |rx|)
                    if (r == 0) r = 1;
                    _itemDrag[i] = new Vector2Int(_itemVel[i].x / r, _itemVel[i].z / r);
                }
            }
            Vector3 g = ToGame(unityPos);
            var fx = new ItemFx { Timer = 0x28 };
            for (int j = 0; j < 20; j++)
                fx.P[j] = new ItemParticle { Life = 0x19, X = (int)g.x << 4, Y = ((int)g.y - height) << 4, Z = (int)g.z << 4, Vx = _itemVel[j].x, Vy = _itemVel[j].y, Vz = _itemVel[j].z, Bright = 0xFF0, Fade = -0xFF0 / 0x19 };
            _itemFx.Add(fx);
        }

        void StepItemFx()
        {
            for (int k = _itemFx.Count - 1; k >= 0; k--)
            {
                var b = _itemFx[k];
                if (--b.Timer < 0) { _itemFx.RemoveAt(k); continue; }
                for (int i = 0; i < 20; i++)
                {
                    var p = b.P[i];
                    p.Life--;
                    p.Vx -= _itemDrag[i].x; p.X += p.Vx;
                    p.Vy += 0x36; p.Y += p.Vy;
                    p.Vz -= _itemDrag[i].y; p.Z += p.Vz;
                    p.Bright += p.Fade;
                }
            }
        }

        void DrawItemFx(Camera cam)
        {
            if (_particle == null) return;
            foreach (var b in _itemFx)
            {
                float c = Mathf.Max(0, b.P[0].Bright >> 4) / 128f;
                foreach (var p in b.P)
                    if (p.Life >= 0)
                        DrawQuad(_particle, ToUnity(p.X >> 4, p.Y >> 4, p.Z >> 4), 20 * Unit, 20 * Unit, 0f, new Rect(0f, 32f / _particleTim.Height, 15f / _particleTim.Width, 15f / _particleTim.Height), new Color(c, c, c, 1f), cam);
            }
        }

        /// <summary>Call every frame of the fight: steps at 20 a second and draws.</summary>
        public void Tick(float dt, Camera cam)
        {
            _acc += dt;
            _viewCam = cam; _meshUsed = 0;
            while (_acc >= 0.05f)
            {
                _acc -= 0.05f;
                for (int i = _runs.Count - 1; i >= 0; i--) if (!Step(_runs[i])) _runs.RemoveAt(i);
                // the map picture comes back once no running effect has it switched off
                if (_mapOff && !_runs.Exists(x => x.MapOff)) { _mapOff = false; if (OnMapLayer != null) OnMapLayer(true); }
                for (int i = _flashes.Count - 1; i >= 0; i--) if (++_flashes[i].T >= _flashes[i].Max) _flashes.RemoveAt(i);
                StepItemFx();
                StepHitFx();
            }
            if (cam == null || _mat == null) return;
            foreach (var r in _runs) Draw(r, cam);
            foreach (var f in _flashes) { Drawn++; DrawFlash(f, cam); }
            DrawItemFx(cam);
            DrawHitFx(cam);
        }

        // ---------------------------------------------------------------- the hit effects (main/efe.c)
        // createParticleFX / tickParticleFX / renderParticleFX: up to 4 at once. kind = the technique's MOVE_DATA.special (its colour),
        // mode 0 / 1 / 2 (1 = BTL_startHitAnimation, 2 = a blocked blow): 2 / 3 / 2 flashes on the second frame and 25 / 48 / 20 sparks flying out
        sealed class ParticleFx { public int State, Timer, EndFrame, Mode, R, G, B; public Vector3 Pos; public EffectActor Ent; public int[,] Spread = new int[4, 3]; }
        // addEntityParticleFX: a little spark on a random bone of the hit Digimon, a new bone and picture every 4 frames (up to 20)
        sealed class EntityFx { public int Timer, Bone, Mode; public EffectActor Owner; }
        // createCloudFX: a puff of dust that rises and grows for 14 frames (up to 60)
        sealed class CloudFx { public int State; public float X, Z, Ground; }
        readonly List<ParticleFx> _hitFx = new List<ParticleFx>();
        readonly List<EntityFx> _entityFx = new List<EntityFx>();
        readonly List<CloudFx> _clouds = new List<CloudFx>();
        Vector3[] _fxParticleDirs;
        Texture2D _defRow0, _defRow2;
        static readonly int[] FlashCount = { 2, 3, 2 }, SparkCount = { 0x19, 0x30, 0x14 };     // PARTICLE_FX_FLASH_COUNT, PARTICLE_FX_PARTICLE_COUNT
        // ENTITY_PARTICLE_FX_MODES (exe 0x80123370): width, height (game units), u, v, w, h on DEFEFF
        static readonly int[,] EntityModes = {
            { 0x78, 0x50, 0x80, 0x80, 0x0f, 0x0f }, { 0x78, 0x50, 0xc0, 0x80, 0x0f, 0x0f }, { 0xc8, 0x50, 0x80, 0x90, 0x1f, 0x07 },
            { 0xc8, 0x96, 0x80, 0x80, 0x0f, 0x0f }, { 0xc8, 0x96, 0xc0, 0x80, 0x0f, 0x0f }, { 0xfa, 0x64, 0x80, 0x90, 0x1f, 0x07 },
            { 0x64, 0x64, 0x80, 0x80, 0x0f, 0x0f }, { 0x64, 0x64, 0xc0, 0x80, 0x0f, 0x0f }, { 0xc8, 0x64, 0x80, 0x90, 0x1f, 0x07 } };
        // CLOUD_FX_U, CLOUD_FX_Y, CLOUD_FX_SCALE, CLOUD_FX_COLOR (exe 0x801233CC...) by frame
        static readonly int[] CloudU = { 0x00, 0x00, 0x20, 0x20, 0x20, 0x40, 0x40, 0x40, 0x40, 0x40, 0x60, 0x60, 0x60, 0x60 };
        static readonly int[] CloudY = { -50, -54, -58, -62, -66, -70, -74, -78, -82, -86, -90, -93, -96, -99 };
        static readonly int[] CloudScale = { 0x2000, 0x2ee0, 0x3840, 0x4000, 0x4000, 0x5000, 0x5000, 0x5000, 0x5000, 0x5400, 0x5800, 0x5c00, 0x6000, 0x6400 };
        static readonly int[] CloudColour = { 0x80, 0x80, 0x80, 0x80, 0x79, 0x73, 0x6c, 0x66, 0x60, 0x59, 0x53, 0x4c, 0x46, 0x40 };

        int Rand() { return _rng.Next(0x8000); }

        /// <summary>createParticleFX(kind, mode, attack position, hit Digimon, lifetime). The colours by kind are the game's.</summary>
        public void AddHitFx(int kind, int mode, Vector3 unityPos, EffectActor ent, int lifetime)
        {
            if (_hitFx.Count >= 4 || mode < 0 || mode > 2) return;
            if (_fxParticleDirs == null)
            {
                // initializeParticleFX: 50 directions, each (0, 0, (150 random + 250) x 16) turned by three random angles
                _fxParticleDirs = new Vector3[50];
                for (int i = 0; i < 50; i++)
                {
                    int ax = (((Rand() & 0x7F) - 0x40) << 12) / 64, ay = (((Rand() & 0x7F) - 0x40) << 12) / 64, az = (((Rand() & 0x7F) - 0x40) << 12) / 64;
                    _fxParticleDirs[i] = (RotZ(az) * RotY(ay) * RotX(ax)).MultiplyVector(new Vector3(0, 0, (Rand() % 150 + 0xFA) * 0x10));
                }
            }
            var fx = new ParticleFx { Mode = mode, Pos = ToGame(unityPos), Ent = ent, EndFrame = lifetime + 4 };
            for (int k = 0; k < 2; k++) { for (int a = 0; a < 3; a++) fx.Spread[a, k] = Rand() % 200 - 100; fx.Spread[3, k] = Rand() % 170 + 250; }
            if (mode == 0) fx.Timer = 0x13;
            else if (mode == 1) { fx.Timer = 0x16; for (int a = 0; a < 3; a++) fx.Spread[a, 2] = Rand() % 200 - 100; fx.Spread[3, 2] = Rand() % 200 + 300; }
            else fx.Timer = 0xE;
            switch (kind)
            {
                case 0: case 1: fx.R = 0xff; fx.G = 0x6e; fx.B = 0x46; break;
                case 2: fx.R = 0xc8; fx.G = 0xc8; fx.B = 0x64; break;
                case 3: fx.R = 0xcc; fx.G = 0; fx.B = 0x88; break;
                case 4: fx.R = 0x32; fx.G = 0x32; fx.B = 0xff; break;
                case 5: fx.R = 0x96; fx.G = 0x96; fx.B = 0x96; break;
                case 6: fx.R = 0xff; fx.G = 0x6e; fx.B = 0x6e; break;
                default: return;
            }
            _hitFx.Add(fx);
        }

        void AddEntityFx(EffectActor owner, int timer)
        {
            if (owner == null || _entityFx.Count >= 20) return;
            int bones = Mathf.Max(2, owner.BoneCount);
            _entityFx.Add(new EntityFx { Owner = owner, Timer = timer, Bone = Rand() % (bones - 1) + 1, Mode = 0 });
        }

        void AddCloud(float x, float z, float ground)
        {
            if (_clouds.Count >= 0x3C) return;
            _clouds.Add(new CloudFx { X = x, Z = z, Ground = ground });
        }

        // once a game frame: tickParticleFX, tickEntityParticleFX, tickCloudFX
        void StepHitFx()
        {
            for (int i = _hitFx.Count - 1; i >= 0; i--)
            {
                var fx = _hitFx[i]; var e = fx.Ent;
                fx.State++; fx.EndFrame--;
                if (fx.State == 10)
                {
                    if (fx.Mode == 0 && e != null) { AddEntityFx(e, 0xE); AddEntityFx(e, 0xE); AddEntityFx(e, 0xE); }
                    else if (fx.Mode == 1) { AddEntityFx(e, 0x16); AddEntityFx(e, 0x16); AddEntityFx(e, 0x16); }
                    continue;
                }
                if (fx.State == 13)
                {
                    if (fx.Mode == 0 && e != null) { AddEntityFx(e, 0xE); AddEntityFx(e, 0xE); }
                    else if (fx.Mode == 1) { AddEntityFx(e, 0x16); AddEntityFx(e, 0xE); }
                    continue;
                }
                if (fx.EndFrame < 4 && fx.EndFrame >= 0 && fx.Mode == 1 && e != null && e.T != null)
                {
                    // dust round its feet: a ring at its radius plus up to half again
                    Vector3 b = ToGame(e.T.position);
                    int radius = Mathf.Max(2, e.Radius), spread = radius >> 1;
                    for (int k = 0; k < fx.EndFrame + 2; k++)
                    {
                        int angle = Rand(), dist = radius + Rand() % spread;
                        AddCloud(b.x + ((dist * PsSin(angle)) >> 12), b.z - ((dist * PsCos(angle)) >> 12), b.y);
                    }
                    continue;
                }
                if (fx.State >= fx.Timer && fx.EndFrame < 0) _hitFx.RemoveAt(i);
            }
            for (int i = _entityFx.Count - 1; i >= 0; i--)
            {
                var fx = _entityFx[i];
                if (fx.Timer <= 0 || fx.Owner.T == null) { _entityFx.RemoveAt(i); continue; }
                if (fx.Timer % 4 == 0) { fx.Bone = Rand() % (Mathf.Max(2, fx.Owner.BoneCount) - 1) + 1; fx.Mode = Rand() % 9; }
                fx.Timer--;
            }
            for (int i = _clouds.Count - 1; i >= 0; i--) if (++_clouds[i].State >= 0xE) _clouds.RemoveAt(i);
        }

        // _sin / _cos: 4096 = 1, 512 steps to a full turn
        static int PsSin(int a) { return PsSin512(a); }
        static int PsCos(int a) { return PsSin512(a + 0x80); }

        void DrawHitFx(Camera cam)
        {
            if (_spot != null)
                foreach (var fx in _hitFx)
                {
                    // renderParticleFX at state 2: the flashes (renderParticleFlash: SPOT256 at u 0x40 v 0xC0, CLUT (0,487), added),
                    // half their width on screen = spread x 8 x viewport / depth x 63 / 1024, i.e. spread x 8 x 63 / 1024 game units
                    if (fx.State != 2) continue;
                    for (int k = 0; k < FlashCount[fx.Mode]; k++)
                    {
                        Vector3 g = fx.Pos + new Vector3(fx.Spread[0, k], fx.Spread[1, k], fx.Spread[2, k]);
                        float half = fx.Spread[3, k] * 8f * 63f / 1024f * Unit;
                        DrawSpot(ToUnity((int)g.x, (int)g.y, (int)g.z), half, new Rect(64f / _spot.width, 64f / _spot.height, 63f / _spot.width, 63f / _spot.height),
                                 new Color(fx.R / 128f, fx.G / 128f, fx.B / 128f, 1f), cam, null);
                        Drawn++;
                    }
                }
            foreach (var fx in _hitFx)
            {
                // the sparks fly out along the 50 fixed directions by sin(lerp(0, 0x6E)) and fade from the kind's colour to black, 40 units wide
                if (fx.State >= fx.Timer) continue;
                int t = fx.Timer, s = fx.State;
                var col = new Color32((byte)(fx.R + (0 - fx.R) * s / t), (byte)(fx.G + (0 - fx.G) * s / t), (byte)(fx.B + (0 - fx.B) * s / t), 255);
                int reach = PsSin512(0x6E * s / t) >> 7;
                for (int i = 0; i < SparkCount[fx.Mode]; i++)
                {
                    Vector3 d = _fxParticleDirs[i];
                    Vector3 g = fx.Pos + new Vector3(((int)(reach * d.x)) >> 9, ((int)(reach * d.y)) >> 9, ((int)(reach * d.z)) >> 9);
                    DrawFxParticle(ToUnity((int)g.x, (int)g.y, (int)g.z), 0x28, col, cam);
                }
            }
            if (_particleTim == null) return;
            if (_defRow2 == null) { _defRow2 = _particleTim.ToTexture(2); _defRow0 = _particleTim.ToTexture(0); }
            float tw = _particleTim.Width, th = _particleTim.Height;
            foreach (var fx in _entityFx)
            {
                // renderEntityParticleFX: on the bone's place, picture (u + (w + 1) x (timer % 4), v) of DEFEFF colour set 2 (CLUT (192,490)), added,
                // put 0x37 ordering steps nearer, so it shows over the Digimon
                var bone = fx.Owner.A != null ? fx.Owner.A.Node(fx.Bone) : null;
                Vector3 at = bone != null ? bone.position : fx.Owner.T.position;
                int m = fx.Mode, u = EntityModes[m, 2] + (EntityModes[m, 4] + 1) * (fx.Timer % 4), v = EntityModes[m, 3] - 0x80;
                DrawQuad(_defRow2, at, EntityModes[m, 0] * Unit, EntityModes[m, 1] * Unit, 0f, new Rect(u / tw, v / th, EntityModes[m, 4] / tw, EntityModes[m, 5] / th), Color.white, cam, FrontMaterial);
                Drawn++;
            }
            foreach (var c in _clouds)
            {
                // renderCloudFX: a 32 x 32 GsSPRITE (semi-transparent, added) from DEFEFF colour set 0 (CLUT (192,488)) at v 0x80 and u by frame,
                // CLOUD_FX_Y over the ground, CLOUD_FX_SCALE (4096 = 32 game units wide), grey CLOUD_FX_COLOR
                int s = c.State; float size = 32f * CloudScale[s] / 4096f * Unit, k = CloudColour[s] / 128f;
                DrawQuad(_defRow0, ToUnity((int)c.X, (int)(c.Ground + CloudY[s]), (int)c.Z), size, size, 0f, new Rect(CloudU[s] / tw, 0f, 32f / tw, 32f / th), new Color(k, k, k, 1f), cam);
                Drawn++;
            }
        }

        readonly Material[] _spriteMats = new Material[8];
        Material _subFront;

        // the sprite's attribute: semi-transparent ones blend by their mode (subtract = the dark clouds of Electric Cloud and others); solid ones cover
        Material SpriteMaterial(int attr)
        {
            if (_mat == null) return null;
            if (attr == 5) return _mat;
            var m = _spriteMats[attr & 7];
            if (m != null) return m;
            m = new Material(_mat);
            if ((attr & 4) != 0) MmdModel.SetPsxBlend(m, attr & 3);
            else
            {
                m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha); m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_BlendOp", (float)UnityEngine.Rendering.BlendOp.Add);
            }
            _spriteMats[attr & 7] = m;
            return m;
        }

        Material FrontMaterial
        {
            get
            {
                if (_matFront == null && _mat != null) { _matFront = new Material(_mat); _matFront.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always); }
                return _matFront;
            }
        }

        // renderParticleFlash: the 64 x 64 picture is one quarter of the spot (its outer corner at u, v); it is drawn four times, mirrored
        // round the middle, so the whole spot is 2 x half wide
        void DrawSpot(Vector3 centre, float half, Rect uv, Color col, Camera cam, Material mat)
        {
            Vector3 r = cam.transform.right * (half * 0.5f), up = cam.transform.up * (half * 0.5f);
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                    DrawQuad(_spot, centre + r * sx + up * sy, -sx * half, sy * half, 0f, uv, col, cam, mat);
        }

        public int Running { get { return _runs.Count; } }
        /// <summary>The added-to-the-picture material (blend mode 1), for other effects such as the evolution scene's streaks.</summary>
        public Material AddMaterial { get { return _mat; } }
        /// <summary>Counts every sprite, model and flash handed to the screen: Battle uses it to see that an effect really draws.</summary>
        public int Drawn;

        bool Step(Run r)
        {
            r.Sprites.Clear(); r.Models.Clear(); r.Prims.Clear(); r.Steps++;
            bool alive = false;
            foreach (var c in r.Ctxs)
            {
                if (c.Inst == 0) continue;
                Put(r.M, c.Instance, I32(r.M, c.Instance) + 1);
                Exec(r, c, c.Instance, c.Parent, c.Inst);
                if (c.Inst != 0) alive = true;
            }
            // BTL_renderEFEEngine: then every live part runs its second program (the one sub 2 names next to its tick program); the sprites and models are placed there
            foreach (var c in r.Ctxs)
                if (c.Inst != 0 && c.Some > 0) Exec(r, c, c.Instance, c.Parent, c.Some);
            for (int i = 0; i < r.UvPtr.Length; i++) StepUv(r, i);
            StepEmitters(r);
            return alive || r.Emitters.Count > 0;
        }

        // BTL_tickEFEUVAnimation: list of {frames, du, dv}; the offsets add up, as the game adds them into the model
        void StepUv(Run r, int i)
        {
            int p = r.UvPtr[i];
            if (p <= 0) return;
            int n = I32(r.M, p);
            if (n <= 0) return;
            int key = p + 4 + r.UvKey[i] * 4;
            if (++r.UvFrame[i] < S16(r.M, key)) return;
            r.UvFrame[i] = 0;
            r.UvKey[i] = (r.UvKey[i] + 1) % n;
            key = p + 4 + r.UvKey[i] * 4;
            r.UvOff[i] += new Vector2((sbyte)r.M[key + 2], (sbyte)r.M[key + 3]);
        }

        // BTL_tickParticleEmitters
        void StepEmitters(Run r)
        {
            for (int i = r.Emitters.Count - 1; i >= 0; i--)
            {
                var e = r.Emitters[i];
                if (I32(r.M, e.Owner) == -1 || --e.Frames <= 0) { r.Emitters.RemoveAt(i); continue; }
                for (int k = 0; k < 20; k++)
                    if (e.Sparks[k] == null || e.Sparks[k].Dist <= 0)
                    {
                        int rx = ((_rng.Next(128) - 64) << 12) / 64, ry = ((_rng.Next(128) - 64) << 12) / 64, rz = ((_rng.Next(128) - 64) << 12) / 64;
                        e.Sparks[k] = new Spark
                        {
                            Dist = e.Start, Vel = e.Vel, Pos = new Vector3(I32(r.M, e.Owner + 4), I32(r.M, e.Owner + 8), I32(r.M, e.Owner + 12)),
                            Dir = (RotZ(rz) * RotY(ry) * RotX(rx)).MultiplyVector(new Vector3(0, 0, e.Start))
                        };
                        break;
                    }
                foreach (var s in e.Sparks) if (s != null && s.Dist > 0) { s.Dist -= s.Vel; s.Vel -= e.Acc; }
            }
        }

        // BTL_renderParticleEmitters: each spark is a line from f0 to f1 along its direction; close to the camera (depth / 4 under 1000) kinds 0 and 2
        // draw the particle picture instead (renderFXParticle, 25 units wide). Lines are 1 PlayStation pixel wide here (the game's drawLine2P).
        void DrawEmitters(Run r, Camera cam)
        {
            foreach (var e in r.Emitters)
                foreach (var s in e.Sparks)
                {
                    if (s == null || s.Dist <= 0 || e.Start == 0) continue;
                    int f0, f1;
                    switch (e.Kind)
                    {
                        case 0: f0 = s.Dist << 4; f1 = (s.Dist + 7) << 4; break;
                        case 1: f0 = s.Dist << 4; f1 = s.Dist << 2; break;
                        case 3: f0 = (e.Start - s.Dist) << 4; f1 = (e.Start - s.Dist) << 2; break;
                        default: f0 = (e.Start - s.Dist) << 4; f1 = ((e.Start - s.Dist) - 5) << 4; break;
                    }
                    f0 /= e.Start; f1 /= e.Start;
                    Vector3 ga = s.Pos + s.Dir * (f0 / 256f), gb = s.Pos + s.Dir * (f1 / 256f);
                    Vector3 a = ToUnity((int)ga.x, (int)ga.y, (int)ga.z), b = ToUnity((int)gb.x, (int)gb.y, (int)gb.z);
                    float depth = Vector3.Dot(a - cam.transform.position, cam.transform.forward) / Unit;
                    Drawn++;
                    if (depth / 4f < 1000f && (e.Kind & 1) == 0 && _particle != null)
                        DrawQuad(_particle, a, 25 * Unit, 25 * Unit, 0f, new Rect(0f, 32f / _particleTim.Height, 15f / _particleTim.Width, 15f / _particleTim.Height), e.Col * (255f / 128f), cam);      // a textured primitive: colour 128 = the picture as it is
                    else DrawLine(a, b, e.Col, cam);
                }
        }

        void DrawLine(Vector3 a, Vector3 b, Color col, Camera cam) { DrawLine(a, b, col, cam, _mat); }

        void DrawLine(Vector3 a, Vector3 b, Color col, Camera cam, Material mat)
        {
            Vector3 d = b - a; if (d.sqrMagnitude < 1e-8f) return;
            float dist = Vector3.Dot((a + b) * 0.5f - cam.transform.position, cam.transform.forward);
            float pixel = 2f * dist * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / 240f;      // one line of the 240-line PlayStation picture
            Vector3 side = Vector3.Cross(d, cam.transform.forward).normalized * pixel;
            var m = Matrix4x4.identity;
            m.SetColumn(0, d); m.SetColumn(1, side); m.SetColumn(2, cam.transform.forward); m.SetColumn(3, new Vector4((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f, (a.z + b.z) * 0.5f, 1f));
            _mp.SetFloat("_VertexColor", 0f);
            _mp.SetTexture("_MainTex", Texture2D.whiteTexture); _mp.SetColor("_Color", col); _mp.SetVector("_Rect", new Vector4(0, 0, 1, 1));
            Graphics.DrawMesh(_quad, m, mat ?? _mat, 0, null, 0, _mp);
        }

        // ---------------------------------------------------------------- the machine
        readonly int[] _stack = new int[256], _call = new int[96];
        int _sp, _cp, _pc, _reg, _cur, _prev, _section, _hitIndex;
        Ctx _ctx; Run _r;

        void Push(int v) { if (_sp < _stack.Length) _stack[_sp++] = v; }
        int Pop() { return _sp > 0 ? _stack[--_sp] : 0; }

        int Read(byte[] m, int p, int size) { return size == 4 ? I32(m, p) : size == 2 ? S16(m, p) : (p >= 0 && p < m.Length ? (sbyte)m[p] : 0); }
        static int SizeOf(int v) { int s = v & 0xF00; return s == 0x400 ? 4 : s == 0x200 ? 2 : 1; }
        int Addr(int off, int seg) { return seg == 0 ? off + _cur : off; }

        void Exec(Run r, Ctx c, int cur, int prev, int pc)
        {
            _r = r; _ctx = c; _cur = cur; _prev = prev; _pc = pc; _sp = 0; _cp = 0; _section = -1;
            var ctxStack = new List<Ctx>(); var m = r.M;
            ctxStack.Add(null); _call[_cp++] = 0;
            for (int guard = 0; _pc > 0 && guard < 20000; guard++)
            {
                int v = S16(m, _pc) & 0xFFFF, op = v & 0xFF, a = S16(m, _pc + 2), b = S16(m, _pc + 4);
                switch (op)
                {
                    case 0: _reg = a; _pc += 6; break;
                    case 1: _reg = Read(m, Addr(a, b), SizeOf(v)); _pc += 6; break;
                    case 2: _reg = _rng.Next(0x8000); _pc += 2; break;
                    case 3:
                    {
                        int isz = (v & 0xF000) == 0x4000 ? 4 : (v & 0xF000) == 0x2000 ? 2 : 1;
                        int idx = Read(m, Addr(S16(m, _pc + 6), S16(m, _pc + 8)), isz), size = SizeOf(v);
                        _reg = Read(m, a + idx * size, size); _pc += 10; break;
                    }
                    case 4: Operate(m, Addr(a, b), v); _pc += 6; break;
                    case 7:
                    {
                        int x = Read(m, Addr(a, b), SizeOf(v));
                        bool res;
                        switch (v >> 12) { case 0: res = x != _reg; break; case 1: res = x == _reg; break; case 2: res = x >= _reg; break; case 3: res = x > _reg; break; case 4: res = x <= _reg; break; default: res = x < _reg; break; }
                        _pc = res ? _pc + 10 : S16(m, _pc + 6) & 0xFFFF; break;
                    }
                    case 9: _pc = a & 0xFFFF; break;
                    case 10: Push(a); _pc += 6; break;
                    case 11: Push(Read(m, Addr(a, b), SizeOf(v))); _pc += 6; break;
                    case 12: Push(Addr(a, b)); _pc += 6; break;
                    case 13: _call[_cp++] = _pc + 6; ctxStack.Add(_ctx); _pc = a & 0xFFFF; break;
                    case 14: { _pc += 2; Sub(v >> 8); break; }
                    case 15: Return(ctxStack); break;
                    case 16: Put(m, Addr(a, b), Pop()); _pc += 6; break;
                    case 17: Spawn(m, ctxStack); break;
                    default: _pc = 0; break;
                }
            }
            if (_ctx != null && _ctx.Instance != 0 && I32(m, _ctx.Instance) == -1) _ctx.Inst = 0;
            if (c.Instance != 0 && I32(m, c.Instance) == -1) c.Inst = 0;
        }

        void Return(List<Ctx> ctxs)
        {
            _pc = _cp > 0 ? _call[--_cp] : 0;
            var c = ctxs.Count > 0 ? ctxs[ctxs.Count - 1] : null;
            if (ctxs.Count > 0) ctxs.RemoveAt(ctxs.Count - 1);
            if (_pc != 0 && c != null) { _ctx = c; _cur = c.Instance; _prev = c.Parent; }
        }

        void Spawn(byte[] m, List<Ctx> ctxs)
        {
            int n = S16(m, _pc + 2), inst = S16(m, _pc + 4) & 0xFFFF, stride = S16(m, _pc + 8), i;
            for (i = 0; i < n; i++) { if (I32(m, inst) == -1) break; inst += stride; }
            if (i == n) { Return(ctxs); return; }
            if (_section == -1) _section = i;
            int at = Array.IndexOf(_r.Ctxs, _ctx);
            var src = _ctx.Src; var tgt = _ctx.Tgt;
            while (at < _r.Ctxs.Length && _r.Ctxs[at].Inst != 0) at++;
            if (at >= _r.Ctxs.Length) { Return(ctxs); return; }
            _hitIndex = 0;
            _prev = _cur; _cur = inst;
            _ctx = _r.Ctxs[at]; _ctx.Instance = _cur; _ctx.Parent = _prev;
            _ctx.Src = _prev == 0 ? _r.Src : src; _ctx.Tgt = _prev == 0 ? _r.Tgt : tgt;
            _pc += 12;
        }

        void Operate(byte[] m, int p, int v)
        {
            int size = SizeOf(v), x = Read(m, p, size), r = _reg;
            switch ((v >> 12) & 7)
            {
                case 0: x = r; break;
                case 1: x += r; break;
                case 2: x -= r; break;
                case 3: x *= r; break;
                case 4: if (r != 0) x /= r; break;
                case 5: if (r != 0) x %= r; break;
                case 6: x <<= r; break;
                default: x >>= r; break;
            }
            if (size == 4) Put(m, p, x); else if (size == 2) Put16(m, p, x); else Put8(m, p, x);
        }

        // ---------------------------------------------------------------- game-space helpers
        public static Vector3 ToGame(Vector3 u) { return new Vector3(u.x / Unit, -u.y / Unit, u.z / Unit); }
        public static Vector3 ToUnity(int x, int y, int z) { return new Vector3(x * Unit, -y * Unit, z * Unit); }
        const float Turn = Mathf.PI * 2f / 4096f;

        public static float FacingOffset;
        static int FacingOf(EffectActor a)
        {
            Vector3 f = a.T.rotation * Quaternion.Inverse(Quaternion.Euler(0, FacingOffset, 0)) * Vector3.forward;
            return ((int)(Mathf.Atan2(f.x, f.z) / Turn)) & 0xFFF;
        }

        static Vector3 BonePos(EffectActor a, int bone)
        {
            var n = a.A != null ? a.A.Node(bone) : null;
            return ToGame(n != null ? n.position : a.T.position);
        }

        // the bone's world rotation in game space: the Digimon's heading (RotMatrix of (0, vy, 0)) times its pose below the root.
        // Our bones hold the game's rotations with Y flipped (MmdModel.SetLocal: F x R x F), so the pose is flipped back the same way
        static Matrix4x4 BoneRot(EffectActor a, int bone)
        {
            var n = a.A != null ? a.A.Node(bone) : null;
            Matrix4x4 pose = Matrix4x4.identity;
            if (n != null)
            {
                Matrix4x4 u = Matrix4x4.Rotate(Quaternion.Inverse(a.T.rotation) * n.rotation), f = Matrix4x4.Scale(new Vector3(1, -1, 1));
                pose = f * u * f;
            }
            return RotY(FacingOf(a)) * pose;
        }

        /// <summary>ApplyMatrixSV with RotMatrixZYX(rx, ry, rz), as the particle code builds it (game units and angles).</summary>
        public static Vector3 RotateZYX(int rx, int ry, int rz, Vector3 v) { return (RotZ(rz) * RotY(ry) * RotX(rx)).MultiplyVector(v); }

        static Matrix4x4 RotX(int a) { float c = Mathf.Cos(a * Turn), s = Mathf.Sin(a * Turn); var m = Matrix4x4.identity; m[1, 1] = c; m[1, 2] = -s; m[2, 1] = s; m[2, 2] = c; return m; }
        static Matrix4x4 RotY(int a) { float c = Mathf.Cos(a * Turn), s = Mathf.Sin(a * Turn); var m = Matrix4x4.identity; m[0, 0] = c; m[0, 2] = s; m[2, 0] = -s; m[2, 2] = c; return m; }
        static Matrix4x4 RotZ(int a) { float c = Mathf.Cos(a * Turn), s = Mathf.Sin(a * Turn); var m = Matrix4x4.identity; m[0, 0] = c; m[0, 1] = -s; m[1, 0] = s; m[1, 1] = c; return m; }
        // BTL_EFE_SUB_OPCODES: how many values each sub-instruction takes off the stack (counted from the decompiled functions), for the ones not ported
        static readonly int[] SubPops = {
            1, 3, 2, 2, 2, 0, 0, 0, 2, 0, 2, 2, 6, 3, 1, 2, 4, 1, 0, 1,
            13, 4, 9, 1, 13, 3, 3, 1, 1, 0, 5, 3, 1, 1, 4, 2, 6, 1, 3, 3,
            6, 3, 1, 2, 3, 2, 0, 2, 2, 2, 2, 2, 2, 2, 2, 0, 3, 2, 2, 2,
            7, 3, 3, 0, 0, 12, 10, 1, 3, 0, 11, 2, 2, 2, 4, 0, 1, 2, 4, 2,
            4, 1, 1, 1, 1, 3, 6, 3, 0, 3, 2, 3, 1, 0, 1, 0, 1 };
        static EffectPlayer() { SubPops[75] = 1; }     // BTL_selectRandomTargetEntity pops with *--EFE_DATA_STACK, not EFE_POP1
        readonly HashSet<int> _unported = new HashSet<int>();
        /// <summary>VIEWPORT_DISTANCE: the map's projection distance (set by the room on each map).</summary>
        public static int ViewportDistance = 0x200;

        // the game's _atan(a, b) (math.c, worked out from its table): 0xC00 - atan2(a, b), in 4096 steps; so 0x400 - _atan(dx, dz) = atan2(-dx, -dz), the transforms' heading
        static int Atan(float a, float b) { return (0xC00 - (int)(Mathf.Atan2(a, b) / Turn)) & 0xFFF; }
        static int Sin512(int a) { return PsSin512(a); }
        static int Cos512(int a) { return PsSin512(a + 0x80); }

        // RotMatrixYXZ: Y x X x Z, in the same axis conventions as RotX / RotY / RotZ
        static Matrix4x4 YXZ(Vector3 r) { return RotY((int)r.y) * RotX((int)r.x) * RotZ((int)r.z); }

        // matrixToEuler2: the angles of a Y x X x Z matrix (4096 = a full turn)
        static Vector3 EulerYXZ(Matrix4x4 m)
        {
            Func<float, float, int> at = (y, x) => ((int)Mathf.Round(Mathf.Atan2(y, x) / Turn));
            if (Mathf.Abs(m[2, 2]) < 1e-6f && Mathf.Abs(m[0, 2]) < 1e-6f)
                return new Vector3(m[1, 2] > 0 ? -0x400 : 0x400, at(-m[2, 0], m[0, 0]), 0);
            int vy = at(m[0, 2], m[2, 2]), vz = at(m[1, 0], m[1, 1]);
            float sy = Mathf.Sin(vy * Turn), cy = Mathf.Cos(vy * Turn), sz = Mathf.Sin(vz * Turn), cz = Mathf.Cos(vz * Turn);
            float max = Mathf.Max(Mathf.Max(Mathf.Abs(sy), Mathf.Abs(cy)), Mathf.Max(Mathf.Abs(sz), Mathf.Abs(cz))), value;
            if (max == Mathf.Abs(sy)) value = m[0, 2] / sy;
            else if (max == Mathf.Abs(cy)) value = m[2, 2] / cy;
            else if (max == Mathf.Abs(sz)) value = m[1, 0] / sz;
            else value = m[1, 1] / cz;
            return new Vector3(at(-m[1, 2], value), vy, vz);
        }

        // matrixToEuler1: the angles of an X x Y x Z matrix, the order drawTMD's RotMatrix uses
        static Vector3 EulerXYZ(Matrix4x4 m)
        {
            Func<float, float, int> at = (y, x) => ((int)Mathf.Round(Mathf.Atan2(y, x) / Turn));
            if (Mathf.Abs(m[2, 2]) < 1e-6f && Mathf.Abs(m[0, 0]) < 1e-6f)
                return new Vector3(at(m[2, 1], m[1, 1]), m[0, 2] > 0 ? 0x400 : -0x400, 0);
            int vx = at(-m[1, 2], m[2, 2]), vz = at(-m[0, 1], m[0, 0]);
            float sx = Mathf.Sin(vx * Turn), cx = Mathf.Cos(vx * Turn), sz = Mathf.Sin(vz * Turn), cz = Mathf.Cos(vz * Turn);
            float max = Mathf.Max(Mathf.Max(Mathf.Abs(sx), Mathf.Abs(cx)), Mathf.Max(Mathf.Abs(sz), Mathf.Abs(cz))), value;
            if (max == Mathf.Abs(sx)) value = -m[1, 2] / sx;
            else if (max == Mathf.Abs(cx)) value = m[2, 2] / cx;
            else if (max == Mathf.Abs(sz)) value = -m[0, 1] / sz;
            else value = m[0, 0] / cz;
            return new Vector3(vx, at(m[0, 2], value), vz);
        }

        static int Sin(int a) { return (int)(Mathf.Sin(a * Turn) * 4096f); }
        static int Cos(int a) { return (int)(Mathf.Cos(a * Turn) * 4096f); }

        Vector3 Vec(int p) { var m = _r.M; return new Vector3(I32(m, p), I32(m, p + 4), I32(m, p + 8)); }
        void SetVec(int p, Vector3 v) { var m = _r.M; Put(m, p, (int)v.x); Put(m, p + 4, (int)v.y); Put(m, p + 8, (int)v.z); }
        int PosAt { get { return _cur + 4; } }
        int RotAt { get { return _cur + 0x10; } }
        Vector3 Pos { get { return Vec(PosAt); } }

        void SetTransform(Vector3 p, int rx, int ry, int rz) { SetVec(PosAt, p); Put(_r.M, RotAt, rx); Put(_r.M, RotAt + 4, ry); Put(_r.M, RotAt + 8, rz); }

        // BTL_setTransformToBoneOffset (29) and BTL_setTransformToBoneMatrix (69): the place the program listed for this Digimon, say its mouth.
        // calculateBoneMatrix: the offset is turned by the bone's whole world matrix; 29 keeps the heading (0, vy, 0), 69 takes the bone's angles (matrixToEuler2)
        void TransformToBone(bool boneAngles = false)
        {
            var m = _r.M; int o = _ctx.BoneOffset;
            var a = _ctx.Src; int ry = FacingOf(a);
            if (o == 0) { SetTransform(BonePos(a, 1), 0, ry, 0); return; }
            int bone = S16(m, o);
            if (a.BoneCount > 0 && bone >= a.BoneCount) bone = 0;
            Vector3 local = new Vector3(S16(m, o + 2), S16(m, o + 4), S16(m, o + 6));
            Matrix4x4 br = BoneRot(a, bone);
            Vector3 world = br.MultiplyVector(local);
            if (!boneAngles) { SetTransform(BonePos(a, bone) + world, 0, ry, 0); return; }
            Vector3 e = EulerYXZ(br);
            SetTransform(BonePos(a, bone) + world, (int)e.x, (int)e.y, (int)e.z);
        }

        void Hit(Vector3 at)
        {
            if (_r.Hit) return;
            _r.Hit = true;
            if (_r.OnHit != null) _r.OnHit(ToUnity((int)at.x, (int)at.y, (int)at.z));
        }

        bool Touching(Vector3 at, Vector3 ext)
        {
            var t = _ctx.Tgt;
            if (t == null) return false;
            Vector3 p = ToGame(t.T.position), d = at - p;
            float r = t.Radius;
            return Mathf.Abs(d.x) < ext.x + r && Mathf.Abs(d.z) < ext.z + r && d.y < ext.y && d.y > -t.Height - ext.y;
        }

        // ---------------------------------------------------------------- BTL_EFE_SUB_OPCODES
        void Sub(int id)
        {
            var m = _r.M;
            switch (id)
            {
                case 0:
                {
                    int e = Pop(), want = _ctx.Src.Type, first = e;
                    while (S16(m, e) != want && S16(m, e) >= 0) e += 10;
                    if (S16(m, e) < 0) e = first;     // a variant of a Digimon: the game maps it to the original type first
                    _ctx.BoneOffset = e + 2; break;
                }
                case 1:
                {
                    int ptr = Pop(); Pop(); int idx = Pop();
                    if (idx >= 0 && idx < _r.UvPtr.Length && ptr != -1) { _r.UvPtr[idx] = ptr; _r.UvKey[idx] = 0; _r.UvFrame[idx] = 0; _r.UvOff[idx] = Vector2.zero; }
                    break;
                }
                case 2: { int a = Pop(), b = Pop(); Put(m, _cur, 0); _ctx.Inst = b; _ctx.Some = a; break; }
                case 3: case 77: DrawTmd(id == 77); break;
                case 4:
                {
                    int y = Pop(), x = Pop();
                    Push(x + 1); Push(y + 1); Push(x / 2); Push(y / 2); Push(0); Push(5); Push(0);
                    Sprite3D(); break;
                }
                case 5:
                    if (_prev == 0) TransformToBone();
                    else for (int i = 0; i < 6; i++) Put(m, _cur + 4 + i * 4, I32(m, _prev + 4 + i * 4));
                    break;
                case 6: Push(-1); BoxHit(); break;
                case 7:
                {
                    int vp = Pop(), rp = Pop(); Vector3 v = Vec(vp), r = Vec(rp);
                    SetVec(vp, (RotZ((int)r.z) * RotY((int)r.y) * RotX((int)r.x)).MultiplyVector(v)); break;
                }
                case 8: case 32:
                {
                    int r = id == 8 ? Pop() : 25, idx = Pop();
                    var a = id == 8 ? _ctx.Tgt : _ctx.Src; int ry = FacingOf(a);
                    Vector3 p = BonePos(a, idx);
                    p.x += (r * Sin(-(ry >> 3) * 8)) >> 12; p.z += (r * Sin(-(((ry >> 3) + 0x80) & 0x1FF) * 8)) >> 12;
                    SetTransform(p, 0, ry, 0); break;
                }
                case 9: Hit(Pos); break;
                case 10:
                {
                    // BTL_getScatteredSpawnPosition: the source's listed bone (or the parent's place), spread at random by up to r
                    int r = Pop(), o = Pop();
                    Vector3 v = _prev == 0 ? BonePos(_ctx.Src, _ctx.BoneOffset != 0 ? S16(m, _ctx.BoneOffset) : 1) : Vec(_prev + 4);
                    if (r > 0) v += new Vector3(_rng.Next(r) - (r >> 1), _rng.Next(r) - (r >> 1), _rng.Next(r) - (r >> 1));
                    SetVec(o, v); break;
                }
                case 11: Pop(); Pop(); break;
                case 12:
                {
                    int o = Pop(), t = Pop(), t1 = Pop(), t0 = Pop(), b = Pop(), a = Pop();
                    float k = t1 == t0 ? 0f : (t - t0) / (float)(t1 - t0);
                    SetVec(o, Vector3.LerpUnclamped(Vec(a), Vec(b), k)); break;
                }
                case 13:
                {
                    int turn = Pop(), speed = Pop(), tp = Pop(); Vector3 p = Pos, target = Vec(tp);
                    int ry = I32(m, RotAt + 4);
                    int want = ((int)(Mathf.Atan2(-(target.x - p.x), -(target.z - p.z)) / Turn)) & 0xFFF, d = (want - ry) & 0xFFF;
                    if (d > 0x800) d -= 0x1000;
                    if (d > 0) { if (d < turn) turn = d; } else if (-d < turn) turn = d; else turn = -turn;
                    ry += turn; Put(m, RotAt + 4, ry);
                    SetVec(PosAt, p + (RotZ(I32(m, RotAt + 8)) * RotY(ry) * RotX(I32(m, RotAt))).MultiplyVector(new Vector3(0, 0, -speed))); break;
                }
                case 14: SetVec(Pop(), ToGame(_ctx.Tgt.T.position)); break;
                case 15: Pop(); Pop(); break;
                case 16:
                {
                    int frames = Pop(), vec = Pop(), n = Pop(), kind = Pop();
                    if (_r.Emitters.Count < 4)
                        _r.Emitters.Add(new Emitter { Owner = _cur, Frames = frames, Kind = kind, Start = n * 16, Vel = n / 8 * 16, Acc = n / 160 * 16, Col = new Color(I32(m, vec) / 255f, I32(m, vec + 4) / 255f, I32(m, vec + 8) / 255f, 1f) });
                    break;
                }
                case 17: { int o = Pop(); Put(m, o, _hitIndex == 0 ? 1 : -1); _hitIndex++; break; }
                case 18: CreateFlash(); break;
                case 19:
                {
                    // BTL_addCloudEffect: createCloudFX at the given place (x and z; the puff's height is CLOUD_FX_Y over the ground)
                    Vector3 p = Vec(Pop());
                    float ground = _ctx.Src != null ? ToGame(_ctx.Src.T.position).y : 0f;
                    AddCloud(p.x, p.z, ground); break;
                }
                case 23: case 37:
                {
                    int o = Pop(); var a = id == 23 ? _ctx.Tgt : _ctx.Src;
                    SetVec(o, new Vector3(a.Radius, a.Height, a.Radius)); break;
                }
                case 24: Sprite3D(); break;
                case 25:
                {
                    // BTL_calculatePolarOffset: _sin / _cos, 512 steps to a full turn
                    int r = Pop(), ang = Pop(), o = Pop(); Put(m, o + 8, (r * Sin512(0x80 - ang)) >> 12); Put(m, o, (r * Cos512(0x80 - ang)) >> 12); break;
                }
                case 26:
                {
                    int size = Pop(), off = Pop(), dst = Pop(), src = _prev + off;
                    if (size == 12) SetVec(dst, Vec(src)); else if (size == 4) Put(m, dst, I32(m, src)); else if (size == 2) Put16(m, dst, S16(m, src)); else Put8(m, dst, Read(m, src, 1));
                    break;
                }
                case 28: { int s = Pop(); if (s >= 0 && _sound != null) _sound(s); break; }
                case 29: TransformToBone(); break;
                case 33: { Vector3 p = Vec(Pop()), q = Pos; Put(m, RotAt + 4, ((int)(Mathf.Atan2(-(p.x - q.x), -(p.z - q.z)) / Turn)) & 0xFFF); break; }
                case 38: case 39: { int o = Pop(), s = Pop(), a = Pop(); Put(m, o, (s * (id == 38 ? Sin(a & 0xFFF) : Cos(a & 0xFFF))) >> 12); break; }
                case 40:
                {
                    int end = Pop(), start = Pop(), t = Pop(), hi = Pop(), lo = Pop(), o = Pop();
                    if (hi < lo) { int x = lo; lo = hi; hi = x; }
                    t = Mathf.Clamp(t, lo, hi);
                    Put(m, o, lo == hi ? start : start + (t - lo) * (end - start) / (hi - lo)); break;
                }
                case 41: { int a = Pop(), b = Pop(), o = Pop(); Put(m, o, a > b ? _rng.Next(b, a) : b); break; }
                case 43:
                {
                    // BTL_getVectorEulerAngles / toEulerAngles (ratan2 = the usual atan2): y = atan2(x, z), x = -atan2(y, length on the ground), both in -0x800..0x7FF.
                    // Unlike the transforms' own heading (sub 33, atan2(-x, -z)), this one points +Z at the target: sub 57 then turns a +Z vector with it
                    Vector3 v = Vec(Pop()); int o = Pop();
                    int ax = -(int)(Mathf.Atan2(v.y, new Vector2(v.x, v.z).magnitude) / Turn) & 0xFFF; if (ax >= 0x800) ax -= 0x1000;
                    int ay = (int)(Mathf.Atan2(v.x, v.z) / Turn) & 0xFFF; if (ay >= 0x800) ay -= 0x1000;
                    Put(m, o, ax); Put(m, o + 4, ay); Put(m, o + 8, 0); break;
                }
                case 44:
                {
                    Vector3 ext = Vec(Pop()); int mode = Pop(), o = Pop();
                    Put(m, o, Touching(Pos, ext) && (mode == 1 || !_r.Hit) ? 1 : 0); break;
                }
                case 46: break;
                case 47: { Vector3 v = Vec(Pop()); Put(m, Pop(), (int)v.magnitude); break; }
                case 48: { int s = Pop(), d = Pop(); SetVec(d, Vec(s)); break; }
                case 49: { int b = Pop(), a = Pop(); SetVec(a, Vec(a) + Vec(b)); break; }
                case 50: { int b = Pop(), a = Pop(); SetVec(a, Vec(a) - Vec(b)); break; }
                case 51: { int b = Pop(), a = Pop(); SetVec(a, Vector3.Scale(Vec(a), Vec(b))); break; }
                case 52:
                {
                    int b = Pop(), a = Pop(); Vector3 x = Vec(a), y = Vec(b);
                    SetVec(a, new Vector3(y.x != 0 ? x.x / y.x : 0, y.y != 0 ? x.y / y.y : 0, y.z != 0 ? x.z / y.z : 0)); break;
                }
                case 56: case 62:
                {
                    // BTL_getTargetBoneTransform / BTL_getSourceBoneTransform: the bone's world position and the angles of its whole world matrix (matrixToEuler2)
                    int rot = Pop(), pos = Pop(), bone = Pop(); var a = id == 56 ? _ctx.Tgt : _ctx.Src;
                    SetVec(pos, BonePos(a, bone)); SetVec(rot, EulerYXZ(BoneRot(a, bone))); break;
                }
                case 57:
                {
                    int vp = Pop(), rp = Pop(); Vector3 r = Vec(rp);
                    SetVec(vp, (RotY((int)r.y) * RotX((int)r.x) * RotZ((int)r.z)).MultiplyVector(Vec(vp))); break;
                }
                case 45: case 58:
                {
                    // BTL_normalizeRotationAngles / BTL_normalizeRotationAngles2: Y x X x Z angles (RotMatrixYXZ) to the X x Y x Z angles drawTMD wants (matrixToEuler1)
                    int v = Pop(), o = Pop();
                    SetVec(o, EulerXYZ(YXZ(Vec(v)))); break;
                }
                case 59:
                {
                    // BTL_combineRotations / multiplyRotations: RotMatrixYXZ(a) x RotMatrixYXZ(b), back to angles with matrixToEuler2 (nothing when b is 0)
                    int b = Pop(), a = Pop(); Vector3 ra = Vec(a), rb = Vec(b);
                    if (rb == Vector3.zero) break;
                    Matrix4x4 mm = YXZ(ra) * YXZ(rb);
                    SetVec(a, EulerYXZ(mm)); break;
                }
                case 61:
                {
                    int size = Pop(), off = Pop(), src = Pop(), dst = _prev + off;
                    if (size == 12) SetVec(dst, Vec(src)); else if (size == 4) Put(m, dst, I32(m, src)); break;
                }
                case 67: Pop(); break;
                case 88:
                {
                    // BTL_faceTargetEntity: the source turns to its target
                    var s = _ctx.Src; var t = _ctx.Tgt;
                    if (s != null && t != null) { Vector3 d = t.T.position - s.T.position; d.y = 0f; if (d.sqrMagnitude > 1e-6f) s.T.rotation = Quaternion.LookRotation(-d) * Quaternion.Euler(0, FacingOffset, 0); }      // the model's front is -Z, as FaceRot
                    break;
                }
                case 69: TransformToBone(true); break;
                case 71: { int s = Pop(), a = Pop(); SetVec(a, Vec(a) * s); break; }
                case 72: { int s = Pop(), a = Pop(); if (s != 0) SetVec(a, Vec(a) / s); break; }
                case 89: LineHit(); break;
                case 90:
                {
                    // BTL_applyRadiusAttackHit: hit flag, radius; the target is hit when it is within radius + its own radius (on the ground plane)
                    int o = Pop(), rad = Pop();
                    Put(m, o, 0);
                    var t = _ctx.Tgt;
                    if (t == null || _r.Hit) break;
                    Vector3 p = Pos, q = ToGame(t.T.position);
                    float dx = p.x - q.x, dz = p.z - q.z;
                    if ((float)rad * rad + (float)t.Radius * t.Radius < dx * dx + dz * dz) break;
                    // the attack object: BTL_calculateAttackHitPosition with the target's radius, at half its height
                    Vector3 at = _ctx.Src != null ? AttackHitPosition(t, _ctx.Src, t.Radius) : q; at.y = -t.Height / 2;
                    Put(m, o, 1); Hit(at); break;
                }
                case 91: BoxHit(); break;
                case 34:
                {
                    // BTL_checkTargetCollision: radius, flag, angle out, offset out: within the radius of the target -> the angle back from it and a point on that circle
                    int rad = Pop(), flag = Pop(), angOut = Pop(), vecOut = Pop();
                    var t = _ctx.Tgt; if (t == null) { Put(m, flag, 0); break; }
                    Vector3 q = ToGame(t.T.position), p = Pos;
                    float d0 = p.x - q.x, d1 = p.z - q.z;
                    if ((float)rad * rad < d0 * d0 + d1 * d1) { Put(m, flag, 0); break; }
                    Put(m, flag, 1);
                    int ang = ((0x400 - Atan(d0, d1)) & 0xFFF) + 0x800;
                    Put(m, angOut, ang);
                    ang = (short)ang >> 3;
                    Put(m, vecOut + 8, (rad * Sin512(0x80 - ang)) >> 12); Put(m, vecOut, (rad * Cos512(0x80 - ang)) >> 12); break;
                }
                case 53: { int b = Pop(), a = Pop(); Vector3 x = Vec(a), y = Vec(b); SetVec(a, new Vector3((int)x.x & (int)y.x, (int)x.y & (int)y.y, (int)x.z & (int)y.z)); break; }
                case 54: { int b = Pop(), a = Pop(); Vector3 x = Vec(a), y = Vec(b); SetVec(a, new Vector3((int)x.x >> (int)y.x, (int)x.y >> (int)y.y, (int)x.z >> (int)y.z)); break; }
                case 55:
                {
                    // BTL_centerTransformOnEntities: the middle of every other Digimon in the fight (ours: the target), rotation 0
                    var t = _ctx.Tgt; Vector3 q = t != null ? ToGame(t.T.position) : Vector3.zero;
                    SetVec(PosAt, q); SetVec(RotAt, Vector3.zero); break;
                }
                case 73: { int k = Pop(), a = Pop(); Vector3 x = Vec(a); SetVec(a, new Vector3((int)x.x & k, (int)x.y & k, (int)x.z & k)); break; }
                case 81: case 94: Put(m, Pop(), ViewportDistance); break;                                // BTL_getViewportDistance
                case 20: ScreenSprite(); break;
                case 21: ProjectToScreen(); break;
                case 22: ParticleFlashSprite(); break;
                case 27: AddEntityFx(_ctx.Src, Pop()); break;                                             // BTL_addSourceEntityParticleFX
                case 30: ScrollingBackground(); break;
                case 31: ParallaxSprites(); break;
                case 35: { int idx = Pop(), o = Pop(); Put(m, o, idx >= 0 && idx < _r.UvFrame.Length ? _r.UvFrame[idx] : 0); break; }   // BTL_getUVAnimTimer
                case 36: Homing(); break;
                case 42: Pop(); break;                                                                   // BTL_printDebugValue
                case 95: break;                                                                          // BTL_markEFEFinished: clears UNUSED_EFE_ARRAY, nothing else
                case 60: EfeLine(); break;
                case 63: _r.FixedCam = true; break;                                                       // BTL_setupFixedCamera
                case 64: _r.FixedCam = false; break;                                                      // BTL_restoreCameraView
                case 65: Quad2D(); break;
                case 66: WireframeGrid(); break;
                case 68: WireframeBox(); break;
                case 70: Quad3D(); break;
                case 74: ToViewSpace(); break;
                case 75: RandomTarget(); break;
                case 76: CameraRotation(); break;
                case 78: LoadClut(false); break;
                case 79: DrawTmdScreenSpace(); break;
                case 80: LoadClut(true); break;
                case 82: RadialWaves(); break;
                case 83: RibbonInit(); break;
                case 84: RibbonTick(); break;
                case 85: RibbonStrip(); break;
                case 86: RingTube(); break;
                case 87: ScreenOverlay(); break;
                case 92: ScreenFade(); break;
                case 93: if (!_r.MapOff) { _r.MapOff = true; _mapOff = true; if (OnMapLayer != null) OnMapLayer(false); } break;   // BTL_disableMapLayer
                case 96: Put(m, Pop(), _r.Hit ? 0 : 1); break;                                           // BTL_isTargetUnhit
                default:
                {
                    // not ported yet: take its arguments off the stack as the game does, so the rest of the program reads the right values
                    int n = id >= 0 && id < SubPops.Length ? SubPops[id] : 0;
                    for (int i = 0; i < n; i++) Pop();
                    if (_unported.Add(id)) Debug.Log("[effect] technique " + _r.Tech + ": sub-instruction " + id + " is not ported (nothing drawn for it)");
                    break;
                }
            }
        }

        // BTL_calculateAttackHitPosition: a point on the circle of 'radius' round 'self', on the side of 'other' (heading _atan(dz, dx) + 0x800,
        // RotMatrixZYX on (0, -height, radius)), x and z kept in 16 bits; the callers then set y themselves
        Vector3 AttackHitPosition(EffectActor self, EffectActor other, int radius)
        {
            Vector3 q = ToGame(self.T.position), s = ToGame(other.T.position);
            int vy = (Atan((int)s.z - (int)q.z, (int)s.x - (int)q.x) + 0x800) & 0xFFF;
            Vector3 o = RotY(vy).MultiplyVector(new Vector3(0, -self.Height, radius));
            return new Vector3((short)(int)q.x + (int)o.x, (int)o.y, (short)(int)q.z + (int)o.z);
        }

        // BTL_applyLineAttackHit: width (not read), hit flag, a vector. The line on the ground from this part to the source; the target is hit when that
        // line touches the square of its radius (doSomethingWithSomePoints) and this part is not higher than its top less the vector's y. The attack
        // object: BTL_calculateAttackHitPosition with its radius, at this part's height. (Every Digimon but the source is tested; ours: the target.)
        void LineHit()
        {
            var m = _r.M;
            Pop(); int o = Pop(), arg = Pop();
            Put(m, o, 0);
            var t = _ctx.Tgt; var s = _ctx.Src;
            if (t == null || s == null || _r.Hit) return;
            Vector3 p = Pos, src = ToGame(s.T.position), q = ToGame(t.T.position);
            int r = t.Radius;
            int[] rect = { (short)((int)q.x - r), (short)((int)q.z - r), (short)((int)q.x + r), (short)((int)q.z + r) };
            if (LineTouchesBox(rect, (int)p.x, (int)p.z, (int)src.x, (int)src.z) != -1) return;
            if ((int)p.y < -t.Height - I32(m, arg + 4)) return;
            Vector3 at = AttackHitPosition(t, s, t.Radius); at.y = (int)p.y;
            Put(m, o, 1); Hit(at);
        }

        // doSomethingWithSomePoints (effect_common.c): -1 when the line (x0, y0)-(x1, y1) touches the box {x0, y0, x1, y1}: an end inside it, the ends on
        // opposite sides through it, or the corners not all on one side of the line; 0 when it misses
        static int LineTouchesBox(int[] rect, int ax, int ay, int bx, int by)
        {
            int[] px = { ax, bx }, py = { ay, by }, code = new int[2];
            for (int i = 0; i < 2; i++)
            {
                code[i] = rect[2] < px[i] ? 5 : px[i] < rect[0] ? 3 : 4;
                if (rect[3] < py[i]) code[i] += 3; else if (py[i] < rect[1]) code[i] -= 3;
                if (code[i] == 4) return -1;
            }
            if (code[0] + code[1] == 8) return -1;
            if (code[0] / 3 == code[1] / 3) return 0;
            if (code[0] % 3 == code[1] % 3) return 0;
            int dx = bx - ax, dy = by - ay;
            int[] cx = { rect[0] - ax, rect[2] - ax, rect[0] - ax, rect[2] - ax }, cy = { rect[1] - ay, rect[1] - ay, rect[3] - ay, rect[3] - ay };
            // 32-bit products, as the game's (they may wrap round on long lines, as on the PS1)
            unchecked
            {
                int cross = dx * cy[0] - dy * cx[0];
                for (int i = 1; i < 4; i++) if (cross * (dx * cy[i] - dy * cx[i]) < 0) return -1;
            }
            return 0;
        }

        // BTL_applyBoxAttackHit
        void BoxHit()
        {
            Pop(); int o = Pop(); Vector3 ext = Vec(Pop());
            bool hit = !_r.Hit && Touching(Pos, ext);
            Put(_r.M, o, hit ? 1 : 0);
            if (hit) Hit(Pos);
        }

        // EFECreateFlash: colour max, colour min, scale max, scale min, frames, fixed depth, place, mode (0 and 1 add, 0x20 subtracts; below 0 = screen offset)
        void CreateFlash()
        {
            var m = _r.M;
            int cMax = Pop(), cMin = Pop(), sMax = Pop(), sMin = Pop(), t = Pop(), depth = Pop(), pos = Pop(), mode = Pop();
            Vector3 p = Vec(pos);
            Func<int, Vector3> col = c => new Vector3(I32(m, c), I32(m, c + 4), I32(m, c + 8));
            AddFlash(mode, ToUnity((int)p.x, (int)p.y, (int)p.z), t, sMin, sMax, col(cMin), col(cMax), depth);
        }

        /// <summary>EFECreateFlash from outside a program (the evolution scene). Colours in the game's 0-255 units.</summary>
        /// <param name="depth">the ordering-table place the game gives it: a small one (0x21) lies over every model, a large one (0xFA0) behind them</param>
        public void AddFlash(int mode, Vector3 at, int frames, int scaleMin, int scaleMax, Vector3 colourMin, Vector3 colourMax, int depth = 0xFA0)
        {
            int abs = Mathf.Abs(mode);
            if (abs != 0 && abs != 1 && abs != 0x20) return;
            if (_flashes.Count >= 12) return;
            _flashes.Add(new Flash { Pos = at, Max = Mathf.Max(1, frames), Mode = mode, S0 = scaleMin, S1 = scaleMax, C0 = colourMin, C1 = colourMax, Front = depth < 0x40 });
        }

        static int PsLerp(int a, int b, int t0, int t1, int t)
        {
            if (t1 == t0) return 0;
            int range = b - a, r = range * (t - t0) / (t1 - t0);
            if (range >= 0) { r %= range + 1; if (r < 0) r += range; } else { r %= range - 1; if (r > 0) r += range; }
            return r + a;
        }
        static int PsSin512(int a) { return Mathf.RoundToInt(Mathf.Sin((a & 0x1FF) * Mathf.PI * 2f / 512f) * 4096f); }

        // renderEFEFlash + renderParticleFlash: a 64 x 64 picture from ETCDAT/SPOT256.TIM (VRAM 832,384, 8-bit; page (832,256) v 0xC0 = row 64), u 0x40 for mode 0, 0 otherwise.
        // Half its width on screen is scale x 63 / 1024 with scale = viewport x lerp(scaleMin, scaleMax) x 10 / depth + a pulse of up to 300 + 1:
        // in the world that is lerp(scale) x 10 x 63 / 1024 game units, plus the pulse in screen pixels. Colour = lerp(min, max) x sin(lerp(0x80, 0x14)), 128 = as drawn.
        void DrawFlash(Flash f, Camera cam)
        {
            if (_spot == null) return;
            int t = f.T, abs = Mathf.Abs(f.Mode);
            Vector3 c = new Vector3(PsLerp((int)f.C0.x, (int)f.C1.x, 0, f.Max, t), PsLerp((int)f.C0.y, (int)f.C1.y, 0, f.Max, t), PsLerp((int)f.C0.z, (int)f.C1.z, 0, f.Max, t));
            float fac = PsSin512(PsLerp(0x80, 0x14, 0, f.Max, t)) / 4096f;
            var col = new Color(c.x * fac / 128f, c.y * fac / 128f, c.z * fac / 128f, 1f);
            float dist = Mathf.Max(0.01f, Vector3.Dot(f.Pos - cam.transform.position, cam.transform.forward));
            float pixel = 2f * dist * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / 240f;
            int pulse = ((PsSin512(PsLerp(0, 0x1EB, 0, 0x17, t)) * 300) >> 12) + 1;
            float half = PsLerp(f.S0, f.S1, 0, f.Max, t) * 10f * 63f / 1024f * Unit + (pulse * 63f / 1024f + 1f) * pixel;
            // renderEFEFlash: uBase = the table at exe 0x8013421C [mode & 3] = 0x40, 0, 0, 0, so mode 0x20 takes mode 0's picture
            float u = (abs & 3) == 0 ? 0x40 : 0;
            if (f.Front && _matFront == null) { _matFront = new Material(_mat); _matFront.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always); }
            Material mat = f.Front ? _matFront : null;
            if (abs == 0x20)
            {
                // mode 0x20 subtracts (blend 2)
                mat = SpriteMaterial(6);
                if (f.Front && mat != null)
                {
                    if (_subFront == null) { _subFront = new Material(mat); _subFront.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always); }
                    mat = _subFront;
                }
            }
            DrawSpot(f.Pos, half, new Rect(u / _spot.width, 64f / _spot.height, 63f / _spot.width, 63f / _spot.height), col, cam, mat);
        }

        // BTL_renderProjectedSprite: scale x VIEWPORT_DISTANCE x 32 / otz, where otz is RotTransPers's return, the depth / 4 (worldPosToScreenPos multiplies
        // the same GTE value by 4 to get the depth). So the sprite is w x scale / 32 game units wide; 4096 means w x 128.
        void Sprite3D()
        {
            int row = Pop(), attr = Pop(), rotate = Pop(); Pop(); Pop();
            int h = Pop(), w = Pop(), v = Pop(), u = Pop(), sy = Pop(), sx = Pop(), col = Pop(), pos = Pop();
            var a = AssetOf(_r.Tech);
            if (a.Width == 0) return;
            Vector3 p = Vec(pos); var m = _r.M;
            _r.Sprites.Add(new Sprite
            {
                Pos = ToUnity((int)p.x, (int)p.y, (int)p.z), W = w * sx / 32f * Unit, H = h * sy / 32f * Unit, Roll = rotate, Row = row, Attr = attr & 7,
                Uv = new Rect(u / (float)a.Width, v / (float)a.Height, w / (float)a.Width, h / (float)a.Height),
                Col = new Color(I32(m, col) / 128f, I32(m, col + 4) / 128f, I32(m, col + 8) / 128f, 1f)
            });
        }

        void DrawTmd(bool yxz)
        {
            int scale = Pop(), id = Pop();
            var a = AssetOf(_r.Tech);
            if (a.Meshes == null || id < 0 || id >= a.Meshes.Length) return;
            var m = _r.M; int rx = I32(m, RotAt), ry = I32(m, RotAt + 4), rz = I32(m, RotAt + 8);
            Matrix4x4 r = yxz ? RotY(ry) * RotX(rx) * RotZ(rz) : RotX(rx) * RotY(ry) * RotZ(rz);
            Vector3 s = Vec(scale) / 4096f, p = Pos;
            Matrix4x4 w = Matrix4x4.Translate(ToUnity((int)p.x, (int)p.y, (int)p.z)) * Matrix4x4.Scale(new Vector3(Unit, -Unit, Unit)) * r * Matrix4x4.Scale(s);
            _r.Models.Add(new Model { Object = id, M = w });
        }

        // ---------------------------------------------------------------- drawing
        Texture2D TextureOf(Asset a, int row)
        {
            if (a.Tim == null) return _glow;
            row = Mathf.Clamp(row, 0, Mathf.Max(0, a.Tim.PaletteCount - 1));
            if (row >= a.Tex.Length) row = 0;
            if (a.Tex[row] == null) a.Tex[row] = a.Tim.ToTexture(row);
            return a.Tex[row];
        }

        Material _matFront;     // the same, drawn over everything (ZTest Always)

        /// <summary>renderFXParticle: the DEFEFF particle (15 x 15 at row 32), size game units wide, colour 128 = as it is. Call once a frame.</summary>
        public void DrawFxParticle(Vector3 unityPos, int size, Color32 col, Camera cam)
        {
            if (_particle == null || cam == null) return;
            DrawQuad(_particle, unityPos, size * Unit, size * Unit, 0f, new Rect(0f, 32f / _particleTim.Height, 15f / _particleTim.Width, 15f / _particleTim.Height),
                     new Color(col.r / 128f, col.g / 128f, col.b / 128f, 1f), cam);
        }

        void DrawQuad(Texture2D tex, Vector3 pos, float w, float h, float roll, Rect uv, Color col, Camera cam, Material mat = null)
        {
            _mp.SetFloat("_VertexColor", 0f);
            _mp.SetTexture("_MainTex", tex); _mp.SetColor("_Color", col); _mp.SetVector("_Rect", new Vector4(uv.x, uv.y, uv.width, uv.height));
            var mx = Matrix4x4.TRS(pos, cam.transform.rotation * Quaternion.Euler(0, 0, roll), new Vector3(w, h, 1f));
            Graphics.DrawMesh(_quad, mx, mat ?? _mat, 0, null, 0, _mp);
        }

        void Draw(Run r, Camera cam)
        {
            var a = AssetOf(r.Tech);
            if (r.Logged < 4 && (r.Logged == 0 || r.Sprites.Count > 0 || r.Models.Count > 0) && r.Steps > r.LoggedStep)
            {
                // [effect] what the program puts on screen: the first sprite's size, colour and place against the camera
                string first = "";
                if (r.Sprites.Count > 0)
                {
                    var s0 = r.Sprites[0]; Vector3 vp = cam.WorldToViewportPoint(s0.Pos);
                    first = " | sprite0 " + s0.W.ToString("0.00") + "x" + s0.H.ToString("0.00") + " m, colour " + s0.Col + ", uv " + s0.Uv + ", row " + s0.Row + ", viewport " + vp.ToString("0.00");
                }
                Debug.Log("[effect] tech " + r.Tech + " step " + r.Steps + ": sprites " + r.Sprites.Count + ", models " + r.Models.Count + ", emitters " + r.Emitters.Count + ", picture " + a.Width + "x" + a.Height + (a.Tim != null ? " (" + a.Tim.PaletteCount + " colour sets)" : " (none)") + first);
                r.Logged++; r.LoggedStep = r.Steps + 2;
            }
            DrawPrims(r, cam);
            foreach (var s in r.Sprites)
            {
                Color c = s.Col;
                if ((s.Attr & 4) != 0 && (s.Attr & 3) == 0) c.a = 0.5f;            // half and half
                else if ((s.Attr & 4) != 0 && (s.Attr & 3) == 3) c.a = 0.25f;      // a quarter added
                DrawQuad(TextureOf(a, s.Row), s.Pos, s.W, s.H, s.Roll, s.Uv, c, cam, SpriteMaterial(s.Attr));
            }
            Drawn += r.Sprites.Count;
            DrawEmitters(r, cam);
            if (a.Meshes == null) return;
            Drawn += r.Models.Count;
            foreach (var o in r.Models)
            {
                _mp.SetFloat("_VertexColor", 0f);
                _mp.SetTexture("_MainTex", TextureOf(a, a.Rows != null && o.Object < a.Rows.Length ? a.Rows[o.Object] : 0)); _mp.SetColor("_Color", Color.white); _mp.SetVector("_Rect", new Vector4(r.UvOff[o.Object].x / Mathf.Max(1, a.Width), r.UvOff[o.Object].y / Mathf.Max(1, a.Height), 1, 1));
                Graphics.DrawMesh(a.Meshes[o.Object], o.M, _mat, 0, null, 0, _mp);
            }
        }
    }
}
