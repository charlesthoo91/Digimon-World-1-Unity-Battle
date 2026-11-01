using System;
using System.Collections.Generic;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>One side of a technique, for the effect programs.</summary>
    public sealed class EffectActor { public Transform T; public DigimonAnimator A; public int Type, Radius, Height; }

    /// <summary>
    /// The game's technique effects (ETCHI/EFEDAT.EFE, btl/battle_effect.c). Each technique has a 20480-byte block: a 52-byte header, then a small program with its data,
    /// a TMD model and a TIM picture. The program runs 20 times a second and draws sprites and models, plays sounds and decides when the target is hit.
    /// Addresses in the programs are places in the block's body, so the block is copied and used as the program's memory.
    /// Not ported: the particle emitters, screen sprites, wireframes, ribbons, untextured model faces.
    /// </summary>
    public sealed class EffectPlayer
    {
        const int Block = 20480, Header = 0x34;
        const float Unit = 0.01f;
        sealed class Ctx { public int Inst, Some, Instance, Parent, BoneOffset; public EffectActor Src, Tgt; }
        sealed class Sprite { public Vector3 Pos; public float W, H, Roll; public Rect Uv; public int Row; public Color Col; }
        sealed class Model { public int Object; public Matrix4x4 M; }

        sealed class Run
        {
            public int Tech; public byte[] M; public Ctx[] Ctxs; public int Start;
            public Action<Vector3> OnHit; public bool Hit;
            public EffectActor Src, Tgt;
            public int Flash = -1;
            public readonly List<Sprite> Sprites = new List<Sprite>();
            public readonly List<Model> Models = new List<Model>();
            public int[] UvPtr, UvKey, UvFrame; public Vector2[] UvOff;
            public readonly List<Emitter> Emitters = new List<Emitter>();
        }
        // BTL_addParticleEmitter: a spray from the owner's place, one new spark a frame, up to 20
        sealed class Spark { public int Dist, Vel; public Vector3 Dir, Pos; }
        sealed class Emitter { public int Owner, Frames, Start, Vel, Acc, Kind; public Color Col; public readonly Spark[] Sparks = new Spark[20]; }
        sealed class Asset { public TimImage Tim; public Texture2D[] Tex = new Texture2D[16]; public Mesh[] Meshes; public int Width, Height; }
        sealed class Flash { public Vector3 Pos; public int T, Max; public float S0, S1; public Color C0, C1; }

        readonly byte[] _file; readonly Action<int> _sound;
        readonly Dictionary<int, Asset> _assets = new Dictionary<int, Asset>();
        readonly List<Run> _runs = new List<Run>();
        readonly List<Flash> _flashes = new List<Flash>();
        readonly System.Random _rng = new System.Random();
        readonly Material _mat; readonly MaterialPropertyBlock _mp = new MaterialPropertyBlock();
        readonly Mesh _quad; readonly Texture2D _glow;
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
        }

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
                    a.Meshes = new Mesh[model.Parts.Count];
                    for (int i = 0; i < a.Meshes.Length; i++) a.Meshes[i] = BuildMesh(model.Parts[i], a);
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
            if (start <= 0 || start >= Block || subs <= 0 || subs > 64) return false;
            var r = new Run { Tech = tech, M = new byte[Block - Header], Src = src, Tgt = tgt, OnHit = onHit, Ctxs = new Ctx[subs], Start = start };
            Array.Copy(_file, b + Header, r.M, 0, r.M.Length);
            for (int i = 0; i < subs; i++) r.Ctxs[i] = new Ctx { Src = src, Tgt = tgt };
            var asset = AssetOf(tech);
            int objs = asset.Meshes != null ? asset.Meshes.Length : 0;
            r.UvOff = new Vector2[objs]; r.UvPtr = new int[objs]; r.UvKey = new int[objs]; r.UvFrame = new int[objs];
            Exec(r, r.Ctxs[0], 0, 0, init);     // the program's setup
            Exec(r, r.Ctxs[0], 0, 0, start);    // and its first step
            if (_section == -1 && r.Ctxs[0].Inst == 0) return false;   // no bones listed for this Digimon
            _runs.Add(r);
            return true;
        }

        public void Clear() { _runs.Clear(); _flashes.Clear(); }

        /// <summary>Call every frame of the fight: steps at 20 a second and draws.</summary>
        public void Tick(float dt, Camera cam)
        {
            _acc += dt;
            while (_acc >= 0.05f)
            {
                _acc -= 0.05f;
                for (int i = _runs.Count - 1; i >= 0; i--) if (!Step(_runs[i])) _runs.RemoveAt(i);
                for (int i = _flashes.Count - 1; i >= 0; i--) if (++_flashes[i].T >= _flashes[i].Max) _flashes.RemoveAt(i);
            }
            if (cam == null || _mat == null) return;
            foreach (var r in _runs) Draw(r, cam);
            foreach (var f in _flashes)
            {
                float k = f.T / (float)Mathf.Max(1, f.Max), s = Mathf.Lerp(f.S0, f.S1, k);
                DrawQuad(_glow, f.Pos, s, s, 0f, new Rect(0, 0, 1, 1), Color.Lerp(f.C0, f.C1, k), cam);
            }
        }

        public int Running { get { return _runs.Count; } }

        bool Step(Run r)
        {
            r.Sprites.Clear(); r.Models.Clear();
            bool alive = false;
            foreach (var c in r.Ctxs)
            {
                if (c.Inst == 0) continue;
                Put(r.M, c.Instance, I32(r.M, c.Instance) + 1);
                Exec(r, c, c.Instance, c.Parent, c.Inst);
                if (c.Inst != 0) alive = true;
            }
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

        void DrawEmitters(Run r, Camera cam)
        {
            foreach (var e in r.Emitters)
                foreach (var s in e.Sparks)
                {
                    if (s == null || s.Dist <= 0 || e.Start == 0) continue;
                    int f = e.Kind == 0 || e.Kind == 1 ? s.Dist << 4 : (e.Start - s.Dist) << 4;
                    Vector3 g = s.Pos + s.Dir * (f / (float)e.Start / 256f);
                    DrawQuad(_glow, ToUnity((int)g.x, (int)g.y, (int)g.z), 0.2f, 0.2f, 0f, new Rect(0, 0, 1, 1), e.Col, cam);
                }
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
        static Vector3 ToGame(Vector3 u) { return new Vector3(u.x / Unit, -u.y / Unit, u.z / Unit); }
        static Vector3 ToUnity(int x, int y, int z) { return new Vector3(x * Unit, -y * Unit, z * Unit); }
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

        static Matrix4x4 RotX(int a) { float c = Mathf.Cos(a * Turn), s = Mathf.Sin(a * Turn); var m = Matrix4x4.identity; m[1, 1] = c; m[1, 2] = -s; m[2, 1] = s; m[2, 2] = c; return m; }
        static Matrix4x4 RotY(int a) { float c = Mathf.Cos(a * Turn), s = Mathf.Sin(a * Turn); var m = Matrix4x4.identity; m[0, 0] = c; m[0, 2] = s; m[2, 0] = -s; m[2, 2] = c; return m; }
        static Matrix4x4 RotZ(int a) { float c = Mathf.Cos(a * Turn), s = Mathf.Sin(a * Turn); var m = Matrix4x4.identity; m[0, 0] = c; m[0, 1] = -s; m[1, 0] = s; m[1, 1] = c; return m; }
        static int Sin(int a) { return (int)(Mathf.Sin(a * Turn) * 4096f); }
        static int Cos(int a) { return (int)(Mathf.Cos(a * Turn) * 4096f); }

        Vector3 Vec(int p) { var m = _r.M; return new Vector3(I32(m, p), I32(m, p + 4), I32(m, p + 8)); }
        void SetVec(int p, Vector3 v) { var m = _r.M; Put(m, p, (int)v.x); Put(m, p + 4, (int)v.y); Put(m, p + 8, (int)v.z); }
        int PosAt { get { return _cur + 4; } }
        int RotAt { get { return _cur + 0x10; } }
        Vector3 Pos { get { return Vec(PosAt); } }

        void SetTransform(Vector3 p, int rx, int ry, int rz) { SetVec(PosAt, p); Put(_r.M, RotAt, rx); Put(_r.M, RotAt + 4, ry); Put(_r.M, RotAt + 8, rz); }

        // BTL_setTransformToBoneOffset and BTL_setTransformToBoneMatrix: the place the program listed for this Digimon, say its mouth
        void TransformToBone()
        {
            var m = _r.M; int o = _ctx.BoneOffset;
            var a = _ctx.Src; int ry = FacingOf(a);
            if (o == 0) { SetTransform(BonePos(a, 1), 0, ry, 0); return; }
            int bone = S16(m, o);
            Vector3 local = new Vector3(S16(m, o + 2), S16(m, o + 4), S16(m, o + 6));
            Vector3 world = RotY(ry).MultiplyVector(local);
            SetTransform(BonePos(a, bone) + world, 0, ry, 0);
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
                case 10: Pop(); Pop(); break;
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
                case 19: Pop(); break;
                case 23: case 37:
                {
                    int o = Pop(); var a = id == 23 ? _ctx.Tgt : _ctx.Src;
                    SetVec(o, new Vector3(a.Radius, a.Height, a.Radius)); break;
                }
                case 24: Sprite3D(); break;
                case 25:
                {
                    int r = Pop(), ang = Pop(), o = Pop(); Put(m, o + 8, (r * Sin(0x80 - ang)) >> 12); Put(m, o, (r * Cos(0x80 - ang)) >> 12); break;
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
                    Vector3 v = Vec(Pop()); int o = Pop();
                    Put(m, o, ((int)(Mathf.Atan2(v.y, new Vector2(v.x, v.z).magnitude) / Turn)) & 0xFFF);
                    Put(m, o + 4, ((int)(Mathf.Atan2(-v.x, -v.z) / Turn)) & 0xFFF); Put(m, o + 8, 0); break;
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
                    int rot = Pop(), pos = Pop(), bone = Pop(); var a = id == 56 ? _ctx.Tgt : _ctx.Src;
                    SetVec(pos, BonePos(a, bone)); SetVec(rot, new Vector3(0, FacingOf(a), 0)); break;
                }
                case 57:
                {
                    int vp = Pop(), rp = Pop(); Vector3 r = Vec(rp);
                    SetVec(vp, (RotY((int)r.y) * RotX((int)r.x) * RotZ((int)r.z)).MultiplyVector(Vec(vp))); break;
                }
                case 58: { int v = Pop(), o = Pop(); SetVec(o, Vec(v)); break; }
                case 59: { int b = Pop(), a = Pop(); SetVec(a, Vec(a) + Vec(b)); break; }
                case 61:
                {
                    int size = Pop(), off = Pop(), src = Pop(), dst = _prev + off;
                    if (size == 12) SetVec(dst, Vec(src)); else if (size == 4) Put(m, dst, I32(m, src)); break;
                }
                case 67: Pop(); break;
                case 69: TransformToBone(); break;
                case 71: { int s = Pop(), a = Pop(); SetVec(a, Vec(a) * s); break; }
                case 72: { int s = Pop(), a = Pop(); if (s != 0) SetVec(a, Vec(a) / s); break; }
                case 89: { Pop(); int o = Pop(); Pop(); bool h = Touching(Pos, Vector3.one * 50f); if (h) Hit(Pos); Put(m, o, h ? 1 : 0); break; }
                case 90: case 91: BoxHit(); break;
                default: break;
            }
        }

        // BTL_applyBoxAttackHit
        void BoxHit()
        {
            Pop(); int o = Pop(); Vector3 ext = Vec(Pop());
            bool hit = !_r.Hit && Touching(Pos, ext);
            Put(_r.M, o, hit ? 1 : 0);
            if (hit) Hit(Pos);
        }

        void CreateFlash()
        {
            var m = _r.M;
            int cMax = Pop(), cMin = Pop(), sMax = Pop(), sMin = Pop(), t = Pop(); Pop(); int pos = Pop(); Pop();
            Vector3 p = Vec(pos);
            Func<int, Color> col = c => new Color(I32(m, c) / 128f, I32(m, c + 4) / 128f, I32(m, c + 8) / 128f, 1f);
            _flashes.Add(new Flash { Pos = ToUnity((int)p.x, (int)p.y, (int)p.z), Max = Mathf.Max(1, t), S0 = sMin / 2f * Unit, S1 = sMax / 2f * Unit, C0 = col(cMin), C1 = col(cMax) });
        }

        // BTL_renderProjectedSprite. On screen a scale of 4096 means w x 32 game units on the map.
        void Sprite3D()
        {
            int row = Pop(); Pop(); int rotate = Pop(); Pop(); Pop();
            int h = Pop(), w = Pop(), v = Pop(), u = Pop(), sy = Pop(), sx = Pop(), col = Pop(), pos = Pop();
            var a = AssetOf(_r.Tech);
            if (a.Width == 0) return;
            Vector3 p = Vec(pos); var m = _r.M;
            _r.Sprites.Add(new Sprite
            {
                Pos = ToUnity((int)p.x, (int)p.y, (int)p.z), W = w * sx / 128f * Unit, H = h * sy / 128f * Unit, Roll = rotate, Row = row,
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

        void DrawQuad(Texture2D tex, Vector3 pos, float w, float h, float roll, Rect uv, Color col, Camera cam)
        {
            _mp.SetTexture("_MainTex", tex); _mp.SetColor("_Color", col); _mp.SetVector("_Rect", new Vector4(uv.x, uv.y, uv.width, uv.height));
            var mx = Matrix4x4.TRS(pos, cam.transform.rotation * Quaternion.Euler(0, 0, roll), new Vector3(w, h, 1f));
            Graphics.DrawMesh(_quad, mx, _mat, 0, null, 0, _mp);
        }

        void Draw(Run r, Camera cam)
        {
            var a = AssetOf(r.Tech);
            foreach (var s in r.Sprites) DrawQuad(TextureOf(a, s.Row), s.Pos, s.W, s.H, s.Roll, s.Uv, s.Col, cam);
            DrawEmitters(r, cam);
            if (a.Meshes == null) return;
            foreach (var o in r.Models)
            {
                _mp.SetTexture("_MainTex", TextureOf(a, 0)); _mp.SetColor("_Color", Color.white); _mp.SetVector("_Rect", new Vector4(r.UvOff[o.Object].x / Mathf.Max(1, a.Width), r.UvOff[o.Object].y / Mathf.Max(1, a.Height), 1, 1));
                Graphics.DrawMesh(a.Meshes[o.Object], o.M, _mat, 0, null, 0, _mp);
            }
        }
    }
}
