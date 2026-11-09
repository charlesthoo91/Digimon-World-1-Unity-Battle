using System;
using System.Collections.Generic;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// What Digimon Curling (kar/kar.c, KAR_REL.BIN) draws on top of the picture: its sprites (KAR_renderSprite: a POLY_FT4 on texture page
    /// (640, 0), 4-bit, colour set (clutX, clutY), at ordering-table slot 10), the dotted aim arrow (KAR_renderAimArrow), the text page that
    /// drawString writes into, UI box 0 with its text slices (renderString), the yes/no cursor (renderSelectionCursor) and the "next" mark
    /// (renderUIBox), and the stones still in hand (drawn with their own view, KAR_renderScene), which come in as a picture.
    /// The game's code records one game frame's commands (BeginFrame ... the KAR_render functions); OnGUI draws the last frame's.
    /// </summary>
    public sealed class CurlingView : UiBoxPage
    {
        public readonly UiBox Box = new UiBox { Id = 0 };
        /// <summary>The map's video memory page (640, 0) read 4-bit with colour set (x, y) = CLUT word; supplied by the room.</summary>
        public Func<int, Texture2D> Page4;
        /// <summary>The stones in hand, seen by their own camera (GsSetRefView2(KAR_D_8005B580), GsSetProjection(0x200)).</summary>
        public RenderTexture Hand;
        public bool HandOn;

        struct Cmd { public int Kind, Z, X, Y, U, V, W, H, Clut, Tint, Rot; }
        readonly List<Cmd> _cmds = new List<Cmd>();
        readonly Color32[] _text = new Color32[256 * 256];
        Texture2D _textTex; bool _textDirty = true;
        readonly Dictionary<long, Texture2D> _cells = new Dictionary<long, Texture2D>();

        CurlingView() { }

        public static CurlingView Load(DiscImage disc, GameFont font)
        {
            if (disc == null || !disc.Exists("/SLUS_010.32")) return null;
            var v = new CurlingView();
            v.Init(disc, font);
            return v;
        }

        // ---- UI box 0
        /// <summary>createAnimatedUIBox(0, 0, 2, rect, from)</summary>
        public void OpenBox(RectInt rect, RectInt from) { Open(Box, rect, from, 2 << 4); }
        /// <summary>removeAnimatedUIBox(0, NULL)</summary>
        public void CloseBox() { Close(Box); }
        /// <summary>renderUIBoxAnimated, once a game frame.</summary>
        public void StepBox() { Step(Box); }

        // ---- the text page
        /// <summary>clearTextArea</summary>
        public void ClearText() { Array.Clear(_text, 0, _text.Length); _textDirty = true; }
        /// <summary>drawString(s, x, y) in the colour setTextColor chose (FONT_CLUT entry).</summary>
        public void DrawString(string s, int x, int y, int colour)
        {
            if (s == null || Font == null) return;
            Font.Render(s, _text, 256, 256, x, y, Font.Colour(colour));
            _textDirty = true;
        }

        // ---- one game frame's commands
        public void BeginFrame() { _cmds.Clear(); }

        /// <summary>KAR_renderSprite: (x, y, w, h) with texels (u, v, w, h), colour (r, g, b) / 128.</summary>
        public void Sprite(int x, int y, int clutX, int clutY, int u, int v, int w, int h, int r)
        {
            _cmds.Add(new Cmd { Kind = 0, Z = 10, X = x, Y = y, U = u, V = v, W = w, H = h, Clut = (clutY << 6) | (clutX >> 4), Tint = r });
        }

        /// <summary>One dot of the aim arrow: a 16 x 16 quad round (x, y), at the depth of its point (drawn here under the sprites).</summary>
        public void ArrowDot(int x, int y, int u, int v, int w, int h, int clut, int shade)
        {
            _cmds.Add(new Cmd { Kind = 0, Z = 12, X = x - 8, Y = y - 8, U = u, V = v, W = w, H = h, Clut = clut, Tint = shade });
        }

        /// <summary>The arrow's head: the quad (-8, 8), (8, 8), (-8, -8), (8, -8) turned by rot (4096 = a turn), texels (u, v) at its first corner.</summary>
        public void ArrowHead(int x, int y, int u, int v, int uw, int clut, int shade, int rot)
        {
            _cmds.Add(new Cmd { Kind = 1, Z = 12, X = x, Y = y, U = u, V = v, W = uw, H = uw, Clut = clut, Tint = shade, Rot = rot });
        }

        /// <summary>renderString(0, x, y, w, h, u, v, 6, 1): a slice of the text page in TEXT_COLORS[0] over a black copy 1 pixel down and right.</summary>
        public void Text(int x, int y, int w, int h, int u, int v)
        {
            _cmds.Add(new Cmd { Kind = 2, Z = 6, X = x, Y = y, W = w, H = h, U = u, V = v });
        }

        /// <summary>renderSelectionCursor(x, y, w, h, 6)</summary>
        public void Cursor(int x, int y, int w, int h) { _cmds.Add(new Cmd { Kind = 3, Z = 6, X = x, Y = y, W = w, H = h }); }

        /// <summary>renderUIBox(0): the "next" mark at (box + 0xEF, box + 0x2B), SYSTEM_W u 0x4C + 16 x (timer / 4 % 3), v 0xB7, CLUT (96, 500).</summary>
        public void NextMark(int timer) { _cmds.Add(new Cmd { Kind = 4, Z = 6, X = Box.Rect.x + 0xEF, Y = Box.Rect.y + 0x2B, U = timer / 4 % 3 * 16 + 0x4C }); }

        // ---- drawing (OnGUI)
        public void Draw()
        {
            if (_textDirty)
            {
                if (_textTex == null) _textTex = new Texture2D(256, 256, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                _textTex.SetPixels32(_text); _textTex.Apply(); _textDirty = false;
            }
            Begin();
            if (HandOn && Hand != null)
            {
                var rt = Hand;
                Add(11, () => GUI.DrawTexture(new Rect(0, 0, UiKit.W, UiKit.H), rt, ScaleMode.StretchToFill, true));
            }
            foreach (var c in _cmds) if (c.Z != 6) Issue(c);
            DrawBox(Box, () => { foreach (var c in _cmds) if (c.Z == 6) Issue(c); });
            UiKit.Begin();
            Flush();
            UiKit.End();
        }

        void Issue(Cmd c)
        {
            switch (c.Kind)
            {
                case 0:
                    {
                        var tex = Cell(c.Clut, c.U, c.V, c.W, c.H, c.Tint);
                        if (tex != null) Picture(c.Z, tex, c.X, c.Y, c.W, c.H, 0, 0);
                        break;
                    }
                case 1:
                    {
                        var tex = Cell(c.Clut, c.U, c.V, c.W, c.H, c.Tint);
                        if (tex == null) break;
                        float deg = c.Rot * 360f / 4096f, cx = c.X, cy = c.Y;
                        // corner 0 (-8, +8) takes texel (u, v): the picture is upside down before it is turned
                        Add(c.Z, () =>
                        {
                            var m = GUI.matrix;
                            GUIUtility.RotateAroundPivot(deg, new Vector2(X(cx), Y(cy)));
                            GUI.DrawTextureWithTexCoords(new Rect(X(cx - 8), Y(cy - 8), 16 * 4.42f, 16 * 4.42f), tex, new Rect(0f, 1f, 1f, -1f));
                            GUI.matrix = m;
                        });
                        break;
                    }
                case 2:
                    {
                        var tex = _textTex; if (tex == null || c.W <= 0) break;
                        Color t = TextColor[0];
                        Rect uv = new Rect(c.U / 256f, 1f - (c.V + c.H) / 256f, c.W / 256f, c.H / 256f);
                        int x = c.X, y = c.Y, w = c.W, h = c.H;
                        Add(c.Z, () => { var o = GUI.color; GUI.color = t; GUI.DrawTextureWithTexCoords(new Rect(X(x), Y(y), w * 4.42f, h * 4.42f), tex, uv); GUI.color = o; });
                        Add(c.Z, () => { var o = GUI.color; GUI.color = Color.black; GUI.DrawTextureWithTexCoords(new Rect(X(x + 1), Y(y + 1), w * 4.42f, h * 4.42f), tex, uv); GUI.color = o; });
                        break;
                    }
                case 3: SelectionCursor(c.Z, c.X, c.Y, c.W, c.H); break;
                case 4: SysIcon(c.Z, c.X, c.Y, 16, 12, c.U, 0xB7, 12); break;
            }
        }

        // the texels (u, v, w, h) of the page with that colour set, times tint / 128 (a textured primitive multiplies and clamps)
        Texture2D Cell(int clut, int u, int v, int w, int h, int tint)
        {
            if (Page4 == null || w <= 0 || h <= 0) return null;
            long key = ((long)clut << 40) | ((long)u << 32) | ((long)v << 24) | ((long)w << 16) | ((long)h << 8) | (long)(tint & 0xFF);
            Texture2D t;
            if (_cells.TryGetValue(key, out t)) return t;
            var page = Page4(clut);
            if (page == null) return null;
            var src = page.GetPixels32();
            var px = new Color32[w * h];
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    int sx = (u + i) & 0xFF, sy = (v + j) & 0xFF;
                    Color32 c = src[(255 - sy) * 256 + sx];
                    if (tint != 128) c = new Color32((byte)Mathf.Min(255, c.r * tint / 128), (byte)Mathf.Min(255, c.g * tint / 128), (byte)Mathf.Min(255, c.b * tint / 128), c.a);
                    px[(h - 1 - j) * w + i] = c;
                }
            t = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            t.SetPixels32(px); t.Apply();
            _cells[key] = t;
            return t;
        }

        /// <summary>The pictures made for this map (its video memory changes with the next map).</summary>
        public void Forget()
        {
            foreach (var t in _cells.Values) if (t != null) UnityEngine.Object.Destroy(t);
            _cells.Clear();
        }
    }
}
