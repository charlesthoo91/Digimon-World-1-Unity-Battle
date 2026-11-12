using System;
using System.Collections.Generic;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// The technique effects' screen and world primitives (btl/battle_effect.c): screen sprites, particle-flash sprites, lines, 2D / 3D textured
    /// quads, scrolling and parallax backgrounds, wireframes, radial waves, ribbons, ring tubes, screen overlays and fades, colour-table loads.
    /// The game sorts these into its ordering table by depth / 16; here a screen primitive is drawn as a quad at that view depth in front of
    /// the camera, so the depth buffer puts it in front of or behind the models as the ordering table does.
    /// </summary>
    public sealed partial class EffectPlayer
    {
        // one primitive a program put up this step; drawn every frame until the next step
        sealed class Prim
        {
            public bool Screen;               // corners in PS1 pixels from the middle of the picture (+ Ot), else game-space points
            public bool Line;                 // two points: a line one PS1 pixel wide
            public Vector3[] P = new Vector3[4];
            public int Ot;                    // ordering-table index (view depth / 16)
            public Texture2D Tex;             // null = untextured (flat colour)
            public Vector2[] Uv;              // picture pixels, from the top
            public float TexW = 1, TexH = 1;
            public Color Col = Color.white;
            public Color[] VCol;              // Gouraud colours (ribbons)
            public int Attr = 5;              // as a sprite's: bit 2 semi-transparent, bits 0-1 blend mode
        }

        /// <summary>BTL_disableMapLayer: the map picture is switched off while the effect runs (the room hides its background).</summary>
        public Action<bool> OnMapLayer;
        Camera _viewCam;
        readonly List<Mesh> _meshPool = new List<Mesh>();
        int _meshUsed;
        static readonly int[] LineDx = { 1, 0, -1, 0 }, LineDy = { 0, 1, 0, -1 };                 // BTL_LINE_OFFSET_X / Y
        static readonly int[] BoxSx = { -1, 1, 1, -1, -1, 1, 1, -1 }, BoxSy = { -1, -1, 1, 1, -1, -1, 1, 1 }, BoxSz = { -1, -1, -1, -1, 1, 1, 1, 1 };
        static readonly int[] BoxLines = { 0, 1, 2, 3, 5, 4, 7, 6, 1, 5, 6, 2, 4, 0, 3, 7 };       // BTL_WIREFRAME_BOX_LINES
        static readonly int[] WaveUvs = { 0x07, 0x10, 0x07, 0x1f, 0x00, 0x10, 0x00, 0x1f, 0x08, 0x00, 0x08, 0x0f, 0x17, 0x00, 0x17, 0x0f, 0x17, 0x00, 0x17, 0x0f, 0x08, 0x00, 0x08, 0x0f };
        static readonly int[] RibbonUvs = { 104, 0, 135, 0, 104, 31, 135, 31 };
        static readonly int[,] RibbonColours = { { 0x7c, 0x7c, 0x7c }, { 0x00, 0x7c, 0x7c }, { 0x3c, 0x7c, 0x3c } };

        // ---------------------------------------------------------------- PS1 screen <-> our camera

        // worldPosToScreenPos: PS1 pixels from the middle and the view depth in game units. BTL_setupFixedCamera: the view from (0, 0, -2000), projection 0x200
        Vector3 Ps1Project(Vector3 game)
        {
            if (_r != null && _r.FixedCam)
            {
                float z = game.z + 2000f;
                if (z < 1f) z = 1f;
                return new Vector3(game.x * 0x200 / z, game.y * 0x200 / z, z);
            }
            var cam = _viewCam;
            if (cam == null) return new Vector3(0, 0, 0);
            Vector3 sp = cam.WorldToScreenPoint(ToUnity((int)game.x, (int)game.y, (int)game.z)); Rect pic = UiKit.Picture;
            return new Vector3((sp.x - pic.x) / pic.width * 320f - 160f, (Screen.height - sp.y - pic.y) / pic.height * 240f - 120f, sp.z / Unit);
        }

        // a PS1 screen point at an ordering-table index, as a point in the world in front of our camera
        static Vector3 Ps1ToWorld(Camera cam, Vector3 p, int ot)
        {
            Rect pic = UiKit.Picture;
            float sx = pic.x + (p.x + 160f) / 320f * pic.width, sy = Screen.height - (pic.y + (p.y + 120f) / 240f * pic.height);
            float dist = Mathf.Clamp(ot * 16f * Unit, cam.nearClipPlane * 1.05f, cam.farClipPlane * 0.98f);
            return cam.ScreenToWorldPoint(new Vector3(sx, sy, dist));
        }

        // ---------------------------------------------------------------- adding primitives

        Prim ScreenQuad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, int ot)
        {
            var p = new Prim { Screen = true, Ot = ot };
            p.P[0] = a; p.P[1] = b; p.P[2] = c; p.P[3] = d;
            _r.Prims.Add(p);
            return p;
        }

        Prim ScreenRect(float x, float y, float w, float h, int ot) { return ScreenQuad(new Vector2(x, y), new Vector2(x + w, y), new Vector2(x, y + h), new Vector2(x + w, y + h), ot); }

        void ScreenLine(Vector2 a, Vector2 b, int ot, Color col, int attr)
        {
            var p = new Prim { Screen = true, Line = true, Ot = ot, Col = col, Attr = attr };
            p.P[0] = a; p.P[1] = b;
            _r.Prims.Add(p);
        }

        Prim WorldQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            var p = new Prim();
            p.P[0] = a; p.P[1] = b; p.P[2] = c; p.P[3] = d;
            _r.Prims.Add(p);
            return p;
        }

        // the technique's picture, colour-table row 'row', with a UV rectangle in picture pixels (the game adds the picture's place in its page)
        void Texture(Prim p, int row, int u, int v, int w, int h)
        {
            var a = AssetOf(_r.Tech);
            if (a.Width == 0) return;
            p.Tex = TextureOf(a, row); p.TexW = a.Width; p.TexH = a.Height;
            p.Uv = new[] { new Vector2(u, v), new Vector2(u + w, v), new Vector2(u, v + h), new Vector2(u + w, v + h) };
        }

        // a textured primitive multiplies by colour / 128; flat ones are the colour itself
        static Color Tex128(int r, int g, int b) { return new Color(r / 128f, g / 128f, b / 128f, 1f); }
        Color ColourAt(int p, bool textured) { var m = _r.M; int r = I32(m, p), g = I32(m, p + 4), b = I32(m, p + 8); return textured ? Tex128(r, g, b) : new Color32((byte)r, (byte)g, (byte)b, 255); }

        // tpage | semi: 0 = solid; else abr in bits 5-6 (0x20 add, 0x40 subtract, 0x60 quarter); a semi value with no abr bits means half
        static int AttrOfSemi(int semi) { return semi == 0 ? 0 : 4 | ((semi >> 5) & 3); }

        // ---------------------------------------------------------------- the sub-instructions

        // BTL_renderScreenSprite: x, y, u, v, w, h, mx, my, rotate, depth, scale x, scale y, colour row (below 0: x / y from the middle of the screen)
        void ScreenSprite()
        {
            int flip = Pop(), sy = Pop(), sx = Pop(), depth = Pop(), rot = Pop(), my = Pop(), mx = Pop(), h = Pop(), w = Pop(), v = Pop(), u = Pop(), y = Pop(), x = Pop();
            if (depth < 0 || depth >= 0x1000) return;
            int row = flip < 0 ? -flip : flip;
            float kx = sx / 4096f, ky = sy / 4096f, ang = rot * Turn;
            float cs = Mathf.Cos(ang), sn = Mathf.Sin(ang);
            Func<float, float, Vector2> corner = (cx, cy) => { float px = (cx - mx) * kx, py = (cy - my) * ky; return new Vector2(x + px * cs - py * sn, y + px * sn + py * cs); };
            var p = ScreenQuad(corner(0, 0), corner(w, 0), corner(0, h), corner(w, h), depth);
            p.Attr = 5;                       // attribute 0x50000000: semi-transparent, added
            Texture(p, row, u, v, w, h);
        }

        // BTL_renderParticleFlashSprite: the technique's picture drawn as renderParticleFlash draws SPOT256 (a quarter, mirrored four times)
        void ParticleFlashSprite()
        {
            int n = Pop(), depth = Pop(), scale = (short)Pop(), sizeY = Pop(), sizeX = Pop(), vBase = Pop(), uBase = Pop(), y = Pop(), x = Pop();
            if (depth < 0xA || depth >= 0x1000) return;
            int w1 = sizeX - 1, h1 = sizeY - 1;
            int hx = (scale * w1 >> 10), hy = (scale * h1 >> 10);
            if (hx <= 0 || hy <= 0) return;
            hx++; hy++;
            for (int qx = -1; qx <= 1; qx += 2)
                for (int qy = -1; qy <= 1; qy += 2)
                {
                    // the picture's outer corner at the outside of each quarter, its inner corner at the middle
                    var p = ScreenQuad(new Vector2(x + qx * hx, y + qy * hy), new Vector2(x, y + qy * hy), new Vector2(x + qx * hx, y), new Vector2(x, y), depth);
                    p.Attr = 5;
                    Texture(p, n, uBase, vBase, w1, h1);
                }
        }

        // BTL_projectPositionToScreen: place -> screen x, y and depth / 16
        void ProjectToScreen()
        {
            int depthOut = Pop(), yOut = Pop(), xOut = Pop(), src = Pop();
            var s = Ps1Project(Vec(src));
            Put(_r.M, xOut, (int)s.x); Put(_r.M, yOut, (int)s.y); Put(_r.M, depthOut, (int)s.z >> 4);
        }

        // BTL_applyHomingMovement: turn towards the target by up to 'limit' in heading and pitch, speed up when nearly facing it (x 1.3) or slow down (x 0.82)
        void Homing()
        {
            var m = _r.M;
            int limit = Pop(), speedP = Pop(); Pop(); int maxS = Pop(), minS = Pop(), tp = Pop();
            Vector3 d = Vec(tp) - Pos;
            Vector3 o = RotY(-I32(m, _cur + 0x14)).MultiplyVector(d);
            int yaw = (-(Atan(o.x, o.z) - 0x400)) & 0xFFF; if (yaw >= 0x801) yaw -= 0x1000;
            int dy = yaw > 0 ? (yaw < limit ? yaw : limit) : (-yaw < limit ? yaw : -limit);
            Put(m, _cur + 0x14, I32(m, _cur + 0x14) + dy);
            int pitch = (Atan(o.y, o.z) - 0x400 - I32(m, _cur + 0x18)) & 0xFFF; if (pitch >= 0x801) pitch -= 0x1000;
            int dp = pitch > 0 ? (pitch < limit ? pitch : limit) : (-pitch < limit ? pitch : -limit);
            int px = I32(m, _cur + 0x18) + dp; Put(m, _cur + 0x18, px);
            if ((px & 0xFFF) >= 0x400 && (px & 0xFFF) < 0xC00) Put(m, _cur + 0x18, 0x800 - px);
            int sp = I32(m, speedP);
            if (limit * 40 / 100 >= Mathf.Abs(dp) + Mathf.Abs(dy)) { sp = sp * 130 / 100; if (maxS < sp) sp = maxS; }
            else { sp = sp * 82 / 100; if (sp < minS) sp = minS; }
            Put(m, speedP, sp);
            Vector3 v = RotX(I32(m, _cur + 0x18)).MultiplyVector(new Vector3(0, 0, -(sp << 3)));
            v = RotY(I32(m, _cur + 0x14)).MultiplyVector(v);
            Vector3 pos = Pos;
            SetVec(PosAt, new Vector3(pos.x + ((int)v.x >> 3), pos.y + ((int)v.y >> 3), pos.z + ((int)v.z >> 3)));
        }

        // BTL_renderEFELine: two screen points, colour, depth, flags (0x20: four lines a pixel apart at half colour, added)
        void EfeLine()
        {
            int flags = Pop(), depth = Pop(), col = Pop(), b = Pop(), c = Pop(), d = Pop(), a = Pop();
            // the stack held: a (x of the first point), d (its y), c (x of the second), b (its y)
            if (depth <= 0x20 || depth >= 0x1000) return;
            Color k = ColourAt(col, false);
            Vector2 p0 = new Vector2(a, d), p1 = new Vector2(c, b);
            if ((flags & 0x20) != 0)
            {
                Color half = new Color(k.r * 0.5f, k.g * 0.5f, k.b * 0.5f, 1f);
                for (int i = 0; i < 4; i++) ScreenLine(p0 + new Vector2(LineDx[i], LineDy[i]), p1 + new Vector2(LineDx[i], LineDy[i]), depth, half, 5);
            }
            else ScreenLine(p0, p1, depth, k, 0);
        }

        // BTL_render2DTexturedQuad: corners (x, y, w, h, or four pointers when semi is below 0), depth, colour, u, v, w, h, colour row, semi
        void Quad2D()
        {
            var m = _r.M;
            int semi = Pop(), clutY = Pop(), dv = Pop(), du = Pop(), v0 = Pop(), u0 = Pop(), col = Pop(), depth = Pop(), d = Pop(), c = Pop(), b = Pop(), a = Pop();
            if (depth <= 0x20 || depth >= 0x1000) return;
            Prim p;
            if (semi < 0)
            {
                semi = -semi & 0xFFFF;
                Func<int, Vector2> pt = q => new Vector2(I32(m, q), I32(m, q + 4));
                p = ScreenQuad(pt(a), pt(b), pt(c), pt(d), depth);
            }
            else p = ScreenRect(a, b, c, d, depth);
            p.Attr = AttrOfSemi(semi); p.Col = ColourAt(col, true);
            Texture(p, clutY, u0, v0, du, dv);
        }

        // BTL_render3DTexturedQuad: four places in the world, colour, u, v, w, h, colour row, semi
        void Quad3D()
        {
            int semi = Pop(), clutY = Pop(), dv = Pop(), du = Pop(), v0 = Pop(), u0 = Pop(), col = Pop(), p4 = Pop(), p3 = Pop(), p2 = Pop(), p1 = Pop();
            var p = WorldQuad(Vec(p1), Vec(p2), Vec(p3), Vec(p4));
            p.Attr = AttrOfSemi(semi); p.Col = ColourAt(col, true);
            Texture(p, clutY, u0, v0, du, dv);
        }

        // the world's rotation as the game's view matrix sees it (GsWSMATRIX, game axes: x right, y down, z forward)
        Matrix4x4 ViewRotation()
        {
            var cam = _viewCam;
            if (cam == null) return Matrix4x4.identity;
            Matrix4x4 w = cam.worldToCameraMatrix; w.SetColumn(3, new Vector4(0, 0, 0, 1));
            return Matrix4x4.Scale(new Vector3(1, -1, -1)) * w * Matrix4x4.Scale(new Vector3(1, -1, 1));
        }

        // BTL_convertToViewSpace: a place and a rotation in the view's terms
        void ToViewSpace()
        {
            int rotIn = Pop(), trans = Pop(), out2 = Pop(), out1 = Pop();
            var cam = _viewCam;
            Matrix4x4 r = ViewRotation();
            Vector3 camGame = cam != null ? ToGame(cam.transform.position) : Vector3.zero;
            SetVec(out1, r.MultiplyVector(Vec(trans) - camGame));
            SetVec(out2, EulerYXZ(r * YXZ(Vec(rotIn))));
        }

        // BTL_selectRandomTargetEntity: a random living entity of ENTITY_TABLE 1-9; the source itself only when the argument is not 0 (the game names it excludeSelf,
        // but the test lets everyone through when it is set). The hit subs never hurt the source, so aiming at itself harms nobody.
        void RandomTarget()
        {
            int self = Pop();
            var living = LivingEntities();
            if (_ctx.Src != null && self != 0) living.Add(_ctx.Src);
            if (living.Count > 0) _ctx.Tgt = living[_rng.Next(living.Count)];
        }

        // BTL_getCameraRotation
        void CameraRotation() { SetVec(Pop(), EulerYXZ(ViewRotation().transpose)); }

        // BTL_renderScrollingBackground: 11 x 9 tiles of 32 x 32 from the picture, scrolled, added, at ordering place 0x1E
        void ScrollingBackground()
        {
            var m = _r.M;
            int color = Pop(), sy = Pop(), sx = Pop(), pal = Pop(), tiles = Pop();
            sx %= 0x140; int tx = (sx >> 5) % 11; if (sx < 0) { sx += 0x140; tx += 10; }
            sy %= 0x100; int ty = (sy >> 5) % 9; if (sy < 0) { sy += 0x100; ty += 8; }
            int y0 = (sy & 0x1F) - 0x98;
            for (int row = 0; row < 9; row++, y0 += 0x20)
            {
                int x0 = (sx & 0x1F) - 0xC0, uy = ((ty + row) % 9) * 11;
                for (int col = 0; col < 11; col++, x0 += 0x20)
                {
                    int ux = (tx + col) % 11, tile = m[Mathf.Clamp(tiles + uy + ux, 0, m.Length - 1)];
                    var p = ScreenRect(x0, y0, 0x20, 0x20, 0x1E);
                    p.Attr = 5; p.Col = Tex128(color, color, color);
                    Texture(p, 0, S16(m, pal + tile * 2), 0, 0x1F, 0x1F);
                }
            }
        }

        // BTL_renderParallaxSprites: {size, x, y, depth, u} until a size below 0
        void ParallaxSprites()
        {
            var m = _r.M;
            int oy = Pop(), ox = Pop(), p = Pop();
            for (int guard = 0; guard < 256 && p + 10 <= m.Length; guard++, p += 10)
            {
                int size = S16(m, p), sx = S16(m, p + 2), sy = S16(m, p + 4), depth = S16(m, p + 6), u = S16(m, p + 8);
                if (size < 0) break;
                int x = (depth * (sx + ox)) >> 7, y = (depth * (sy + oy)) >> 7;
                x %= 400; y %= 320;
                if (x < 0) x += 360;
                if (y < 0) y += 280;
                x -= 0xC8; y -= 0xA0;
                int sz = (size * depth) >> 8;
                var q = ScreenRect(x, y, sz, sz, 0x1E);
                q.Attr = 0;
                Texture(q, 1, u, 0, 0x1F, 0x1F);
            }
        }

        // BTL_renderParallelLines: n + 1 lines from a to b, each moved on by (to - from) / n, behind everything (ordering place 0xFFD)
        void ParallelLines(Vector3 a, Vector3 b, int n, Vector3 from, Vector3 to, Color col)
        {
            if (n == 0) n = 1;
            Vector3 d = new Vector3((int)(to.x - from.x) / n, (int)(to.y - from.y) / n, (int)(to.z - from.z) / n);
            for (int i = 0; i <= n; i++, a += d, b += d)
            {
                var pa = Ps1Project(a); if (pa.z <= 0x200 || pa.z >= 0x10000) continue;
                var pb = Ps1Project(b); if (pb.z <= 0x200 || pb.z >= 0x10000) continue;
                ScreenLine(pa, pb, 0xFFD, col, 0);
            }
        }

        // BTL_renderWireframeGrid
        void WireframeGrid()
        {
            var m = _r.M;
            int y = Pop(), n2 = Pop(), z2 = Pop(), z1 = Pop(), n1 = Pop(), x2 = Pop(), x1 = Pop(), col = Pop(), rotSrc = Pop(), bas = Pop();
            Vector3 r = Vec(rotSrc);
            Matrix4x4 rm = RotZ((int)r.z) * RotY((int)r.y) * RotX((int)r.x);
            Vector3 p0 = rm.MultiplyVector(new Vector3(x1, y, z1)), p1 = rm.MultiplyVector(new Vector3(x2, y, z1)), p2 = rm.MultiplyVector(new Vector3(x1, y, z2)), p3 = rm.MultiplyVector(new Vector3(x2, y, z2));
            Vector3 b = Vec(bas); Color k = new Color32((byte)I32(m, col), (byte)I32(m, col + 4), (byte)I32(m, col + 8), 255);
            ParallelLines(b + p0, b + p2, n1, p0, p1, k);
            ParallelLines(b + p0, b + p1, n2, p0, p2, k);
        }

        // BTL_renderWireframeBox: the box (extent around base) turned by the part's rotation; four 4-point lines at ordering place 0x22
        void WireframeBox()
        {
            int col = Pop(), ext = Pop(), bas = Pop();
            var m = _r.M;
            Matrix4x4 rm = RotZ(I32(m, _cur + 0x18)) * RotY(I32(m, _cur + 0x14)) * RotX(I32(m, _cur + 0x10));
            Vector3 e = Vec(ext), b = Vec(bas);
            var pts = new Vector3[8];
            for (int i = 0; i < 8; i++) pts[i] = Ps1Project(b + rm.MultiplyVector(new Vector3(e.x * BoxSx[i], e.y * BoxSy[i], e.z * BoxSz[i])));
            Color k = ColourAt(col, false);
            for (int l = 0; l < 4; l++)
                for (int s = 0; s < 3; s++) ScreenLine(pts[BoxLines[l * 4 + s]], pts[BoxLines[l * 4 + s + 1]], 0x22, k, 0);
        }

        // BTL_loadClutColors (count colours of four 16-bit parts each, into row y from entry x) and BTL_addClutLoadPrim (16 raw colours into row y)
        void LoadClut(bool raw)
        {
            var m = _r.M;
            var a = AssetOf(_r.Tech);
            if (raw)
            {
                int idx = Pop(), src = Pop(), y = Pop(); Pop();
                src += idx * 2;
                if (a.Tim == null) return;
                for (int i = 0; i < 16; i++) a.Tim.SetPaletteColor(y, i, S16(m, src + i * 2) & 0xFFFF);
                ForgetTextures(a, y);
            }
            else
            {
                int src = Pop(), count = Pop(), x = Pop(), y = Pop();
                if (count <= 0 || count > 16 || a.Tim == null) return;
                for (int i = 0; i < count; i++, src += 8)
                    a.Tim.SetPaletteColor(y, x + i, (S16(m, src) & 0xFFFF) + ((S16(m, src + 2) & 0xFFFF) << 5) + ((S16(m, src + 4) & 0xFFFF) << 10) + ((S16(m, src + 6) & 0xFFFF) << 15));
                ForgetTextures(a, y);
            }
        }

        static void ForgetTextures(Asset a, int row)
        {
            if (row < 0 || row >= a.Tex.Length || a.Tex[row] == null) return;
            UnityEngine.Object.Destroy(a.Tex[row]); a.Tex[row] = null;
        }

        // BTL_drawTMDScreenSpace: the model placed straight in the view (rotation YXZ, scale, the part's place as view coordinates)
        void DrawTmdScreenSpace()
        {
            int scale = Pop(), id = Pop();
            var a = AssetOf(_r.Tech);
            var cam = _viewCam;
            if (a.Meshes == null || id < 0 || id >= a.Meshes.Length || cam == null) return;
            var m = _r.M;
            Vector3 s = Vec(scale) / 4096f, p = Pos;
            Matrix4x4 local = Matrix4x4.Translate(p) * YXZ(new Vector3(I32(m, RotAt), I32(m, RotAt + 4), I32(m, RotAt + 8))) * Matrix4x4.Scale(s);
            Matrix4x4 w = cam.cameraToWorldMatrix * Matrix4x4.Scale(new Vector3(Unit, -Unit, -Unit)) * local;
            _r.Models.Add(new Model { Object = id, M = w });
        }

        // BTL_interpolateClamped
        static int Clamped(int lo, int hi, int t, int start, int end)
        {
            if (hi < lo) { int x = lo; lo = hi; hi = x; }
            t = t < lo ? lo : (hi < t ? hi : t);
            if (lo >= t) return start;
            return start + (end - start) * (t - lo) / (hi - lo);
        }

        Matrix4x4 PartMatrix(Vector3 scale)
        {
            var m = _r.M;
            return Matrix4x4.Translate(Pos) * YXZ(new Vector3(I32(m, RotAt), I32(m, RotAt + 4), I32(m, RotAt + 8))) * Matrix4x4.Scale(scale / 4096f);
        }

        // BTL_renderRadialWaves: six rays of quads (every 0x2AA around), radius 200 to 3000, waving up and down with the frame
        void RadialWaves()
        {
            var m = _r.M;
            Vector3 scale = Vec(Pop());
            Vector3 rot = new Vector3(I32(m, RotAt), I32(m, RotAt + 4), I32(m, RotAt + 8));
            int phase = I32(m, _cur) * -400;
            for (int ring = 0; ring < 6; ring++)
            {
                rot.y += 0x2AA;
                Matrix4x4 w = Matrix4x4.Translate(Pos) * YXZ(rot) * Matrix4x4.Scale(scale / 4096f);
                int pHw = 0, pH = 0, pR = 0;
                for (int radius = 0xC8; radius < 0xBB8; radius += 0x64)
                {
                    int h = radius < 0x2EF ? Clamped(0xC8, 0x2EE, radius, 0xC8, 0xFA) : radius < 0x8CB ? Clamped(0x2EE, 0x8CA, radius, 0xFA, 0x7D) : Clamped(0x8CA, 0xBB8, radius, 0x7D, 0xA);
                    int i = phase + radius * 4 + ring * 0x309;
                    h = ((h * Sin(i & 0xFFF)) >> 12) - h - 0xC8;
                    int hw = radius / 5;
                    if (radius != 0xC8)
                    {
                        var p = WorldQuad(w.MultiplyPoint3x4(new Vector3(-hw, h, radius)), w.MultiplyPoint3x4(new Vector3(hw, h, radius)),
                                          w.MultiplyPoint3x4(new Vector3(-pHw, pH, pR)), w.MultiplyPoint3x4(new Vector3(pHw, pH, pR)));
                        p.Attr = 5;
                        int o = radius == 0x12C ? 8 : radius >= 0xB54 ? 16 : 0;
                        var a = AssetOf(_r.Tech);
                        if (a.Width != 0)
                        {
                            p.Tex = TextureOf(a, 1); p.TexW = a.Width; p.TexH = a.Height;
                            p.Uv = new[] { new Vector2(WaveUvs[o], WaveUvs[o + 1]), new Vector2(WaveUvs[o + 2], WaveUvs[o + 3]), new Vector2(WaveUvs[o + 4], WaveUvs[o + 5]), new Vector2(WaveUvs[o + 6], WaveUvs[o + 7]) };
                        }
                    }
                    pHw = hw; pH = h; pR = radius;
                }
            }
        }

        // BTL_initializeRibbonPoints / BTL_tickRibbonPoints: ten points {x, y, z, drift} (8 bytes each)
        void RibbonInit()
        {
            var m = _r.M; int q = Pop();
            for (int i = 0; i < 10; i++)
            {
                int o = q + i * 8;
                Put16(m, o, i * 0x190 - 0x708); Put16(m, o + 2, _rng.Next(-0x96, 0x97)); Put16(m, o + 4, 0); Put16(m, o + 6, _rng.Next(-0x1E, 0x1F));
            }
        }

        void RibbonTick()
        {
            var m = _r.M; int q = Pop(), frame = I32(m, _cur);
            for (int i = 0; i < 10; i++)
            {
                int o = q + i * 8;
                if ((frame + i * 8) % 10 == 0) Put16(m, o + 6, _rng.Next(-0xF, 0x10));
                int pad = S16(m, o + 6);
                Put16(m, o + 2, Mathf.Clamp(S16(m, o + 2) + pad, -0x96, 0x96));
                Put16(m, o + 4, Mathf.Clamp(S16(m, o + 4) + pad, -0x96, 0x96));
            }
        }

        // BTL_renderRibbonStrip: nine Gouraud quads between the points (500 up, 200 down), colours cycling through BTL_RIBBON_COLORS, fading in and out
        void RibbonStrip()
        {
            var m = _r.M;
            Pop(); Vector3 scale = Vec(Pop()); int pts = Pop();
            int frame = I32(m, _cur), k = (frame / 10) % 3, n = (k + 1) % 3;
            int r = Clamped(0, 10, frame % 10, RibbonColours[k, 0], RibbonColours[n, 0]), g = Clamped(0, 10, frame % 10, RibbonColours[k, 1], RibbonColours[n, 1]), b = Clamped(0, 10, frame % 10, RibbonColours[k, 2], RibbonColours[n, 2]);
            int f = frame < 0xF ? Clamped(1, 7, frame, 0, 0x1000) : Clamped(0x17, 0x1E, frame, 0x1000, 0);
            r = r * f >> 12; g = g * f >> 12; b = b * f >> 12;
            Color full = Tex128(r, g, b), half = Tex128(r >> 1, g >> 1, b >> 1), none = new Color(0, 0, 0, 1);
            Matrix4x4 w = PartMatrix(scale);
            Func<int, int, Vector3> edge = (i, side) => { int o = pts + i * 8; return w.MultiplyPoint3x4(new Vector3(S16(m, o), S16(m, o + 2) + (side == 0 ? -0x1F4 : 0xC8), S16(m, o + 4))); };
            var a = AssetOf(_r.Tech);
            for (int i = 1; i < 10; i++)
            {
                var p = WorldQuad(edge(i - 1, 0), edge(i, 0), edge(i - 1, 1), edge(i, 1));
                p.Attr = 5;
                p.VCol = new[] { i == 1 ? none : half, i == 9 ? none : half, i == 1 ? none : full, i == 9 ? none : full };
                if (a.Width != 0)
                {
                    p.Tex = TextureOf(a, 2); p.TexW = a.Width; p.TexH = a.Height;
                    p.Uv = new[] { new Vector2(RibbonUvs[0], RibbonUvs[1]), new Vector2(RibbonUvs[2], RibbonUvs[3]), new Vector2(RibbonUvs[4], RibbonUvs[5]), new Vector2(RibbonUvs[6], RibbonUvs[7]) };
                }
            }
        }

        // BTL_renderRingTube: n rings of ten points (radius and height per ring) round a centre, joined by quads coloured per ring
        void RingTube()
        {
            var m = _r.M;
            int start = Pop(), colors = Pop(), heights = Pop(), radius = Pop(), n = Pop(), center = Pop();
            if (n < 2) return;
            Vector3 c = Vec(center);
            var pt = new Vector3[n, 10];
            for (int i = 0; i < n; i++)
            {
                int r = I32(m, radius + i * 4);
                for (int j = 0; j < 10; j++)
                {
                    int ang = j * 0x200 / 10;
                    pt[i, j] = new Vector3(c.x + ((r * Cos512(0x80 - ang)) >> 12), c.y + I32(m, heights + i * 4), c.z + ((r * Sin512(0x80 - ang)) >> 12));
                }
            }
            for (int i = 0; i < n - 1; i++)
            {
                int col = I32(m, colors + i * 4);
                for (int j = 0; j < 10; j++)
                {
                    int j1 = (j + 1) % 10;
                    var p = WorldQuad(pt[i + 1, j1], pt[i, j1], pt[i + 1, j], pt[i, j]);
                    p.Attr = 5; p.Col = Tex128(col, col, col);
                    Texture(p, 0, ((i + start) & 1) * 24, 0, 0x17, 0x1F);
                }
            }
        }

        // BTL_renderScreenOverlay: the whole screen in one colour at an ordering place; mode bit 2 = semi-transparent, bits 0-1 its blend
        void ScreenOverlay()
        {
            int mode = Pop(), col = Pop(), layer = Pop();
            var p = ScreenRect(-160, -120, 320, 240, layer);
            p.Col = ColourAt(col, false); p.Attr = (mode & 4) != 0 ? 4 | (mode & 3) : 0;
        }

        // BTL_renderScreenFade: SPOT256's corner (3 x 3 at v 0x80) over the whole screen, subtracted, behind everything (0xFFE)
        void ScreenFade()
        {
            int c = Pop();
            var p = ScreenRect(-160, -120, 320, 240, 0xFFE);
            p.Attr = 6; p.Col = Tex128(c, c, c);
            if (_spot != null) { p.Tex = _spot; p.TexW = TimImage.W(_spot); p.TexH = TimImage.H(_spot); p.Uv = new[] { new Vector2(0, 0), new Vector2(3, 0), new Vector2(0, 3), new Vector2(3, 3) }; }
        }

        // ---------------------------------------------------------------- drawing

        Mesh PoolMesh()
        {
            if (_meshUsed >= _meshPool.Count) { var nm = new Mesh(); nm.MarkDynamic(); _meshPool.Add(nm); }
            return _meshPool[_meshUsed++];
        }

        static readonly int[] QuadTris = { 0, 1, 2, 2, 1, 3 };

        readonly Material[] _backMats = new Material[8];
        Material BackMaterial(int attr, Material front)
        {
            var m = _backMats[attr & 7];
            if (m == null) { m = new Material(front); m.renderQueue = front.renderQueue - 1; _backMats[attr & 7] = m; }
            return m;
        }

        void DrawPrims(Run r, Camera cam)
        {
            foreach (var p in r.Prims)
            {
                Material mat = SpriteMaterial(p.Attr);
                if (mat == null) continue;
                // ordering place 0xFFE (BTL_renderScreenFade) is behind every other effect primitive: the PS1 draws it first. Unity sorts see-through
                // meshes by their distance, and the full-screen quad's middle is near the camera, so it came last and wiped out
                // DG Dimension's grid (0xFFD). Such primitives now go in a queue drawn before the other effects
                if (p.Screen && p.Ot >= 0xFFE) mat = BackMaterial(p.Attr, mat);
                if (p.Line)
                {
                    Vector3 a = p.Screen ? Ps1ToWorld(cam, p.P[0], p.Ot) : ToUnity((int)p.P[0].x, (int)p.P[0].y, (int)p.P[0].z);
                    Vector3 b = p.Screen ? Ps1ToWorld(cam, p.P[1], p.Ot) : ToUnity((int)p.P[1].x, (int)p.P[1].y, (int)p.P[1].z);
                    DrawLine(a, b, p.Col, cam, mat);
                    Drawn++;
                    continue;
                }
                var mesh = PoolMesh();
                var v = new Vector3[4];
                for (int i = 0; i < 4; i++) v[i] = p.Screen ? Ps1ToWorld(cam, p.P[i], p.Ot) : ToUnity((int)p.P[i].x, (int)p.P[i].y, (int)p.P[i].z);
                mesh.Clear();
                mesh.vertices = v;
                var uv = new Vector2[4];
                for (int i = 0; i < 4; i++) uv[i] = p.Uv != null ? new Vector2(p.Uv[i].x / p.TexW, p.Uv[i].y / p.TexH) : Vector2.zero;
                mesh.uv = uv;
                if (p.VCol != null) mesh.colors = p.VCol;
                mesh.triangles = QuadTris;
                mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
                _mp.SetTexture("_MainTex", p.Tex != null ? (Texture)p.Tex : Texture2D.whiteTexture);
                _mp.SetColor("_Color", p.Col);
                _mp.SetVector("_Rect", new Vector4(0, 0, 1, 1));
                _mp.SetFloat("_VertexColor", p.VCol != null ? 1f : 0f);
                Graphics.DrawMesh(mesh, Matrix4x4.identity, mat, 0, null, 0, _mp);
                Drawn++;
            }
        }
    }
}
